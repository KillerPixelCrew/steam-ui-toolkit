using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Owns one persistent bounded CDP connection for each allowlisted Steam UI target.</summary>
public sealed class PersistentSteamUiTransport : ISteamUiTransport
{
    // How many evaluations may run out their own deadline back to back before the connection is
    // treated as dead. One is an overloaded renderer or a heavy expression; a run of them is a
    // target that has stopped servicing CDP behind a websocket that is still open.
    private const int UnansweredEvaluationsBeforeReconnect = 2;

    /// <summary>The reason a closed transport reports when the host gave none.</summary>
    public const string DefaultClosedReason = "Steam CEF integration disabled in settings.";

    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30)
    ];

    // As long as the longest retry delay, so a connection that outlives one full backoff step is
    // treated as healthy.
    private static readonly TimeSpan StableConnectionUptime = TimeSpan.FromSeconds(30);

    private readonly Task _bindingEventPump;

    private readonly Channel<SteamUiNotification> _bindingEvents =
        Channel.CreateBounded<SteamUiNotification>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    private readonly Dictionary<SteamUiTargetRole, TargetChannel> _channels;

    private readonly ISteamUiEndpointDiscovery _discovery;
    private readonly Task _generationEventPump;

    // A generation snapshot is a hint a later one of the same role supersedes, so each role keeps
    // only its latest in TargetChannel.PendingGeneration and this lane carries which roles have one.
    // A role is queued at most once, so the lane is never full and a burst on one role can never
    // evict another role's snapshot. A Runtime.bindingCalled frame is a user action, so it has its
    // own lane that refuses (and logs) a write when full instead of evicting an earlier action.
    private readonly Channel<SteamUiTargetRole> _generationEvents;

    private readonly bool _ownsDiscovery;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ISteamUiCdpWireFactory _wireFactory;
    private volatile string _closedReason = DefaultClosedReason;
    private int _disposed;
    private volatile bool _enabled = true;

    /// <summary>Creates a production transport using Steam's validated loopback endpoint.</summary>
    public PersistentSteamUiTransport()
        : this(false)
    {
    }

    /// <summary>Creates a transport that can defer attachment until Steam has a main window.</summary>
    /// <param name="requireMainWindow">
    ///     Whether discovery must find a validated main-window
    ///     target before attaching to any role, including the headless shared context.
    /// </param>
    public PersistentSteamUiTransport(bool requireMainWindow)
        : this(
            new SteamUiEndpointDiscovery(requireMainWindow),
            new SteamUiWebSocketWireFactory(),
            true,
            null)
    {
    }

    /// <summary>Creates a transport over a supplied discovery and wire factory.</summary>
    /// <param name="discovery">Finds the target for a role.</param>
    /// <param name="wireFactory">Opens the channel to a discovered target.</param>
    /// <param name="retryDelays">The reconnect backoff steps; null for the production 1, 4, 16 and 30 s.</param>
    internal PersistentSteamUiTransport(
        ISteamUiEndpointDiscovery discovery,
        ISteamUiCdpWireFactory wireFactory,
        IReadOnlyList<TimeSpan>? retryDelays = null)
        : this(discovery, wireFactory, false, retryDelays)
    {
    }

    private PersistentSteamUiTransport(
        ISteamUiEndpointDiscovery discovery,
        ISteamUiCdpWireFactory wireFactory,
        bool ownsDiscovery,
        IReadOnlyList<TimeSpan>? retryDelays)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _wireFactory = wireFactory ?? throw new ArgumentNullException(nameof(wireFactory));
        _ownsDiscovery = ownsDiscovery;
        _retryDelays = retryDelays is { Count: > 0 } ? retryDelays : DefaultRetryDelays;
        _channels = Enum.GetValues<SteamUiTargetRole>().ToDictionary(
            role => role,
            role => new TargetChannel(role));
        _generationEvents = Channel.CreateBounded<SteamUiTargetRole>(
            new BoundedChannelOptions(_channels.Count)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        _bindingEventPump = PumpBindingsAsync();
        _generationEventPump = PumpGenerationsAsync();
    }

    /// <summary>Raised for each <c>Runtime.bindingCalled</c> notification.</summary>
    /// <remarks>
    ///     Generation-changing notifications only advance generations and raise
    ///     <see cref="GenerationChanged" />. Other CDP notifications from the enabled domains have no
    ///     consumer and are dropped as they arrive.
    /// </remarks>
    public event EventHandler<SteamUiNotification>? NotificationReceived;

    /// <inheritdoc />
    public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> SubscribeAsync(
        SteamUiTargetRole role, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Subscribe(role, true, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(expression);
        SteamUiShared.ThrowIfInvalidTimeout(timeout);
        if (!_enabled)
        {
            return new SteamUiEvaluationResult(
                SteamUiDispatch.Closed,
                null,
                _closedReason,
                GenerationsOf(GetChannel(role)));
        }

        await using var lease = await LeaseAsync(role, timeout, cancellationToken)
            .ConfigureAwait(false);
        var channel = lease.Channel;
        SteamUiCdpConnection? connection = null;
        try
        {
            connection = await EnsureConnectedAsync(
                    channel,
                    lease.OwnershipGeneration,
                    lease.Deadline)
                .ConfigureAwait(false);
            if (connection is null)
            {
                lock (channel.Sync)
                {
                    return new SteamUiEvaluationResult(
                        SteamUiDispatch.NotSent,
                        null,
                        channel.LastFailure ?? "Steam UI target is unavailable.",
                        channel.Generations);
                }
            }

            // Any answer, a JavaScript exception included, proves the renderer is alive.
            var (value, error) = await connection.EvaluateAsync(expression, timeout, lease.Deadline)
                .ConfigureAwait(false);
            SetHealth(
                channel,
                lease.OwnershipGeneration,
                SteamUiTransportHealth.Ready,
                null);
            ResetTimeouts(channel, lease.OwnershipGeneration);
            return new SteamUiEvaluationResult(SteamUiDispatch.Answered, value, error, GenerationsOf(channel));
        }
        catch (SteamUiUnansweredException ex) when (ex.InnerException is OperationCanceledException
                                                    && cancellationToken.IsCancellationRequested)
        {
            // The caller stopped waiting after the send began. That says nothing about Steam's health,
            // but the expression may still run.
            return new SteamUiEvaluationResult(
                SteamUiDispatch.Unanswered,
                null,
                "Steam UI evaluation was cancelled after it was sent.",
                GenerationsOf(channel));
        }
        catch (SteamUiUnansweredException ex) when (ex.InnerException is OperationCanceledException)
        {
            DropUnansweredConnection(channel, lease.OwnershipGeneration, connection!);
            return new SteamUiEvaluationResult(
                SteamUiDispatch.Unanswered, null, "Steam UI evaluation timed out.", GenerationsOf(channel));
        }
        catch (SteamUiUnansweredException ex)
        {
            var cause = ex.InnerException ?? ex;
            SetHealth(
                channel,
                lease.OwnershipGeneration,
                cause is InvalidDataException ? SteamUiTransportHealth.Incompatible : SteamUiTransportHealth.Retrying,
                cause.Message);
            return new SteamUiEvaluationResult(
                SteamUiDispatch.Unanswered, null, cause.Message, GenerationsOf(channel));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up before anything was sent, which says nothing about Steam. Never a
            // health signal.
            return new SteamUiEvaluationResult(
                SteamUiDispatch.NotSent, null, "Steam UI evaluation was cancelled.", GenerationsOf(channel));
        }
        catch (OperationCanceledException)
        {
            if (connection is not null)
            {
                DropUnansweredConnection(channel, lease.OwnershipGeneration, connection);
            }

            return new SteamUiEvaluationResult(
                SteamUiDispatch.NotSent, null, "Steam UI evaluation timed out before it was sent.",
                GenerationsOf(channel));
        }
        catch (InvalidDataException ex)
        {
            SetHealth(
                channel,
                lease.OwnershipGeneration,
                SteamUiTransportHealth.Incompatible,
                ex.Message);
            return new SteamUiEvaluationResult(SteamUiDispatch.NotSent, null, ex.Message, GenerationsOf(channel));
        }
        catch (Exception ex)
        {
            SetHealth(
                channel,
                lease.OwnershipGeneration,
                SteamUiTransportHealth.Retrying,
                ex.Message);
            return new SteamUiEvaluationResult(SteamUiDispatch.NotSent, null, ex.Message, GenerationsOf(channel));
        }
    }

    /// <inheritdoc />
    public async Task SetRuntimeBindingAsync(
        SteamUiTargetRole role,
        string bindingName,
        bool installed,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        SteamUiShared.ThrowIfInvalidTimeout(timeout);
        if (!_enabled)
        {
            throw new InvalidOperationException(_closedReason);
        }

        await using var lease = await LeaseAsync(role, timeout, cancellationToken)
            .ConfigureAwait(false);
        var connection = await EnsureConnectedAsync(
                                 lease.Channel,
                                 lease.OwnershipGeneration,
                                 lease.Deadline)
                             .ConfigureAwait(false)
                         ?? throw new IOException("Steam UI target is unavailable.");
        _ = await connection.InvokeAsync(
                installed ? "Runtime.addBinding" : "Runtime.removeBinding",
                writer => writer.WriteString("name", bindingName),
                timeout,
                lease.Deadline)
            .ConfigureAwait(false);
        SetHealth(
            lease.Channel,
            lease.OwnershipGeneration,
            SteamUiTransportHealth.Ready,
            null);
    }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots()
    {
        return _channels.Values.Select(Snapshot).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NotificationReceived = null;
        GenerationChanged = null;
        _shutdown.Cancel();
        foreach (var channel in _channels.Values)
        {
            SteamUiCdpConnection? connection;
            CancellationTokenSource? reconnectCancellation;
            lock (channel.Sync)
            {
                (reconnectCancellation, connection) =
                    DetachLocked(channel, SteamUiTransportHealth.Disposed);
            }

            reconnectCancellation?.Cancel();
            reconnectCancellation?.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        _bindingEvents.Writer.TryComplete();
        _generationEvents.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_bindingEventPump, _generationEventPump)
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SteamUiLog.Warn("Steam UI event handlers exceeded their shutdown budget.");
        }

        if (_ownsDiscovery)
        {
            (_discovery as IDisposable)?.Dispose();
        }
        // Reconnect attempts can finish just after cancellation even when their wire is already
        // detached. Keep the managed token source alive for those late continuations; it is
        // collected with the transport.
    }

    /// <summary>Takes the temporary subscription and deadline one request runs under.</summary>
    /// <param name="role">The target the request needs.</param>
    /// <param name="timeout">The complete request deadline.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <returns>A lease that disposes the deadline, then releases the subscription.</returns>
    private async ValueTask<RequestLease> LeaseAsync(
        SteamUiTargetRole role,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // A request-only subscription. The request connects through EnsureConnectedAsync itself, so
        // a reconnect loop started here only raced it for the connect gate and was cancelled again by
        // the release. Releasing still closes the connection when nobody else holds the channel.
        var subscription = Subscribe(role, false, cancellationToken);
        var channel = GetChannel(role);
        var ownershipGeneration = GetOwnershipGeneration(channel);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        return new RequestLease(subscription, channel, ownershipGeneration, deadline);
    }

    /// <summary>Counts one holder of the channel, starting its reconnect loop for a persistent one.</summary>
    /// <param name="role">The target the holder needs.</param>
    /// <param name="reconnect">Whether the holder keeps the channel connected between requests.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <returns>The subscription whose disposal releases the holder.</returns>
    private IAsyncDisposable Subscribe(
        SteamUiTargetRole role, bool reconnect, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var channel = GetChannel(role);
        lock (channel.Sync)
        {
            channel.Subscribers++;
            // A zero-subscriber release cancels its loop immediately but that task may not have
            // observed cancellation yet. Keying restart only on IsCompleted left a new subscriber
            // holding a channel whose sole reconnect loop was already doomed to exit.
            if (reconnect
                && _enabled
                && (channel.ReconnectCancellation is null
                    || channel.ReconnectCancellation.IsCancellationRequested))
            {
                StartReconnectLocked(channel);
            }
        }

        return new Subscription(this, channel);
    }

    /// <summary>Stops or resumes all CEF traffic while retaining subscriber intent.</summary>
    /// <param name="enabled">Whether repository-owned evaluations may reach Steam.</param>
    /// <param name="closedReason">
    ///     What evaluations report while closed, for a host that holds the transport closed for a reason
    ///     other than its settings; null for <see cref="DefaultClosedReason" />.
    /// </param>
    /// <remarks>
    ///     The host's master switch. While closed, every evaluation answers
    ///     <see cref="SteamUiDispatch.Closed" /> with the closed reason and nothing reaches Steam.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The transport was disposed.</exception>
    public void SetEnabled(bool enabled, string? closedReason = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _closedReason = string.IsNullOrWhiteSpace(closedReason)
            ? DefaultClosedReason
            : closedReason;
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        foreach (var channel in _channels.Values)
        {
            CancellationTokenSource? cancellation = null;
            SteamUiCdpConnection? connection = null;
            lock (channel.Sync)
            {
                if (enabled)
                {
                    if (channel.Subscribers > 0
                        && (channel.ReconnectCancellation is null
                            || channel.ReconnectCancellation.IsCancellationRequested))
                    {
                        StartReconnectLocked(channel);
                    }
                }
                else
                {
                    (cancellation, connection) = DetachLocked(channel, SteamUiTransportHealth.Idle);
                }
            }

            cancellation?.Cancel();
            cancellation?.Dispose();
            if (connection is not null)
            {
                _ = DisposeDetachedConnectionAsync(channel.Role, connection);
            }
        }
    }

    private void StartReconnectLocked(TargetChannel channel)
    {
        channel.ReconnectCancellation?.Dispose();
        channel.ReconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token);
        // Every detach has already moved the generation past the previous owner's loop, so the new
        // loop takes the current one and a request leased under it keeps its connection attempt.
        var ownershipGeneration = channel.OwnershipGeneration;
        _ = ReconnectLoopAsync(
            channel,
            ownershipGeneration,
            channel.ReconnectCancellation.Token);
    }

    private async Task ReconnectLoopAsync(
        TargetChannel channel,
        long ownershipGeneration,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (_enabled && !cancellationToken.IsCancellationRequested)
        {
            SteamUiCdpConnection? connection;
            var absent = false;
            try
            {
                connection = await EnsureConnectedAsync(
                        channel,
                        ownershipGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
                absent = connection is null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetHealth(
                    channel,
                    ownershipGeneration,
                    SteamUiTransportHealth.Retrying,
                    ex.Message);
                connection = null;
            }

            if (connection is not null)
            {
                var connectedAt = Stopwatch.GetTimestamp();
                try
                {
                    await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                }

                // Only a session that stayed up resets the backoff. A CEF that accepts the socket
                // and drops it at once (normal during a Steam update or crash loop) otherwise pinned
                // this loop at the 1 s delay, rediscovering and re-running every patch each second.
                if (Stopwatch.GetElapsedTime(connectedAt) >= StableConnectionUptime)
                {
                    attempt = 0;
                }
            }

            // A target that is simply not there yet (Steam starting, Big Picture not open) is polled
            // at the first step, so attachment follows its window within one step. Only failed and
            // dropped connections escalate the backoff.
            var delay = absent ? _retryDelays[0] : RetryDelay(_retryDelays, attempt++);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Returns the bounded reconnect delay for a zero-based failed attempt.</summary>
    internal static TimeSpan RetryDelay(IReadOnlyList<TimeSpan> delays, int attempt)
    {
        return delays[Math.Clamp(attempt, 0, delays.Count - 1)];
    }

    private async Task<SteamUiCdpConnection?> EnsureConnectedAsync(
        TargetChannel channel,
        long ownershipGeneration,
        CancellationToken cancellationToken)
    {
        void ThrowIfOwnerLeftLocked()
        {
            if (Volatile.Read(ref _disposed) != 0
                || channel.Subscribers == 0
                || channel.OwnershipGeneration != ownershipGeneration)
            {
                SteamUiLog.Info(
                    $"Steam UI {channel.Role} connection completed after its owner left; "
                    + "discarding it.");
                throw new OperationCanceledException(
                    "The Steam UI channel owner changed while connecting.");
            }
        }

        lock (channel.Sync)
        {
            if (channel.Connection is not null)
            {
                return channel.Connection;
            }

            channel.Health = SteamUiTransportHealth.Connecting;
        }

        await channel.ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (channel.Sync)
            {
                if (channel.Connection is not null)
                {
                    return channel.Connection;
                }
            }

            var endpoint = await _discovery.DiscoverAsync(channel.Role, cancellationToken)
                .ConfigureAwait(false);
            if (endpoint is null)
            {
                SetHealth(
                    channel,
                    ownershipGeneration,
                    SteamUiTransportHealth.Unavailable,
                    $"Steam UI {channel.Role} target is absent.");
                return null;
            }

            var wire = await _wireFactory.ConnectAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            SteamUiCdpConnection? connection = null;
            var wireOwnedByConnection = false;
            try
            {
                lock (channel.Sync)
                {
                    ThrowIfOwnerLeftLocked();
                }

                connection = new SteamUiCdpConnection(
                    wire,
                    (method, parameters) =>
                        OnNotification(channel, connection, method, parameters),
                    (closedConnection, failure) =>
                        OnConnectionClosed(channel, closedConnection, failure));
                connection.Start();
                wireOwnedByConnection = true;

                // Generation tracking depends on notifications. Runtime, Page and DOM events are
                // silent until their domains are enabled. Keep the candidate private until all
                // three calls succeed, so neither health nor GenerationChanged can claim a channel
                // ready while an in-place document replacement would still be invisible.
                await EnableGenerationDomainsAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
                lock (channel.Sync)
                {
                    // Recheck after enabling domains: the owner may have gone away while the CDP
                    // setup calls were in flight. Publishing here regardless would leave a live
                    // socket after the last subscription was released or CEF was disabled.
                    ThrowIfOwnerLeftLocked();

                    var generations = channel.Generations;
                    if (!string.Equals(channel.BrowserId, endpoint.BrowserId, StringComparison.Ordinal))
                    {
                        channel.BrowserId = endpoint.BrowserId;
                        generations = generations with
                        {
                            Browser = generations.Browser + 1,
                            Target = generations.Target + 1,
                            Frame = generations.Frame + 1,
                            ExecutionContext = generations.ExecutionContext + 1,
                            Document = generations.Document + 1
                        };
                    }
                    else if (!string.Equals(channel.TargetId, endpoint.TargetId, StringComparison.Ordinal))
                    {
                        generations = generations with
                        {
                            Target = generations.Target + 1,
                            Frame = generations.Frame + 1,
                            ExecutionContext = generations.ExecutionContext + 1,
                            Document = generations.Document + 1
                        };
                    }

                    channel.TargetId = endpoint.TargetId;
                    channel.Generations = generations with
                    {
                        Session = generations.Session + 1
                    };
                    channel.Connection = connection;
                    channel.Health = SteamUiTransportHealth.Ready;
                    channel.LastFailure = null;
                }

                RaiseGenerationChanged(channel);
                return connection;
            }
            catch
            {
                if (wireOwnedByConnection)
                {
                    lock (channel.Sync)
                    {
                        if (ReferenceEquals(channel.Connection, connection))
                        {
                            channel.Connection = null;
                        }
                    }

                    if (connection is not null)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    await wire.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            channel.ConnectGate.Release();
        }
    }

    private static async Task EnableGenerationDomainsAsync(
        SteamUiCdpConnection connection,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(5);
        await connection.InvokeAsync("Debugger.setPauseOnExceptions", writer => writer.WriteString("state", "none"),
                timeout, cancellationToken)
            .ConfigureAwait(false);
        await connection.InvokeAsync("Debugger.disable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        await connection.InvokeAsync("Runtime.enable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        await connection.InvokeAsync("Page.enable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
        await connection.InvokeAsync("DOM.enable", null, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private void OnNotification(
        TargetChannel channel,
        SteamUiCdpConnection? connection,
        string method,
        string parameters)
    {
        // Only generation changes and the Runtime binding have a consumer. Console and DOM chatter
        // from the enabled domains is dropped here, before the lock, the snapshot and the channel.
        var generationMethod = method is "Page.frameNavigated"
            or "Runtime.executionContextCreated"
            or "Runtime.executionContextDestroyed"
            or "Runtime.executionContextsCleared"
            or "DOM.documentUpdated";
        if (!generationMethod && method != "Runtime.bindingCalled")
        {
            return;
        }

        SteamUiGenerations generations;
        lock (channel.Sync)
        {
            // Ignore domain-enable chatter from a candidate that has not been published, plus any
            // final notification an already-detached socket races with its disposal. Only the
            // connection named by the current ownership generation may advance its generations.
            if (!ReferenceEquals(channel.Connection, connection))
            {
                return;
            }

            generations = channel.Generations;
            channel.Generations = method switch
            {
                "Page.frameNavigated" => generations with
                {
                    Frame = generations.Frame + 1,
                    Document = generations.Document + 1
                },
                "Runtime.executionContextCreated" => generations with
                {
                    ExecutionContext = generations.ExecutionContext + 1
                },
                "Runtime.executionContextDestroyed" or "Runtime.executionContextsCleared" =>
                    generations with
                    {
                        ExecutionContext = generations.ExecutionContext + 1,
                        Document = generations.Document + 1
                    },
                "DOM.documentUpdated" => generations with
                {
                    Document = generations.Document + 1
                },
                _ => generations
            };
        }

        if (!generationMethod)
        {
            if (!_bindingEvents.Writer.TryWrite(
                    new SteamUiNotification(channel.Role, method, parameters, generations)))
            {
                SteamUiLog.Warn(
                    $"Steam UI {channel.Role} binding queue was full; a Runtime binding call was refused.");
            }

            return;
        }

        RaiseGenerationChanged(channel);
    }

    private void OnConnectionClosed(
        TargetChannel channel, SteamUiCdpConnection connection, Exception? failure)
    {
        lock (channel.Sync)
        {
            if (!ReferenceEquals(channel.Connection, connection))
            {
                return;
            }

            channel.Connection = null;
            channel.Health = _enabled && channel.Subscribers > 0
                ? SteamUiTransportHealth.Retrying
                : SteamUiTransportHealth.Idle;
            channel.LastFailure = failure?.Message ?? "Steam UI target closed the channel.";
        }
    }

    private async ValueTask ReleaseAsync(TargetChannel channel)
    {
        SteamUiCdpConnection? connection = null;
        CancellationTokenSource? reconnectCancellation = null;
        lock (channel.Sync)
        {
            if (channel.Subscribers > 0)
            {
                channel.Subscribers--;
            }

            if (channel.Subscribers == 0)
            {
                (reconnectCancellation, connection) =
                    DetachLocked(channel, SteamUiTransportHealth.Idle);
            }
        }

        reconnectCancellation?.Cancel();
        reconnectCancellation?.Dispose();
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Clears the unanswered-evaluation run after Steam answers on this connection.</summary>
    private static void ResetTimeouts(TargetChannel channel, long ownershipGeneration)
    {
        lock (channel.Sync)
        {
            if (channel.OwnershipGeneration == ownershipGeneration)
            {
                channel.ConsecutiveTimeouts = 0;
            }
        }
    }

    /// <summary>
    ///     Retires a connection whose target has stopped answering, so the channel's reconnect
    ///     loop builds a fresh one.
    /// </summary>
    /// <remarks>
    ///     A renderer can stop servicing CDP while its websocket stays open, and nothing else in
    ///     this transport notices: a timed-out evaluation used to leave the channel marked Ready
    ///     with the same dead socket in place, so every later request timed out too. On 2026-09-26
    ///     that ran for three minutes — the patch pipeline stalled, WSGM's Big Picture close request
    ///     was never consumed, and the desktop return rebuilt the desktop underneath a Big Picture
    ///     window Steam was no longer servicing — and it ended only because Steam's own helper
    ///     restarted. One slow evaluation is not that state, so the socket goes after a run of them.
    /// </remarks>
    /// <param name="channel">The channel whose connection went unanswered.</param>
    /// <param name="ownershipGeneration">The generation the caller leased; a stale one is ignored.</param>
    /// <param name="connection">The connection the caller used.</param>
    private static void DropUnansweredConnection(
        TargetChannel channel,
        long ownershipGeneration,
        SteamUiCdpConnection connection)
    {
        SteamUiCdpConnection? unanswered = null;
        lock (channel.Sync)
        {
            if (channel.OwnershipGeneration != ownershipGeneration
                || !ReferenceEquals(channel.Connection, connection))
            {
                return;
            }

            channel.LastFailure = "Steam UI evaluation timed out.";
            if (++channel.ConsecutiveTimeouts < UnansweredEvaluationsBeforeReconnect)
            {
                return;
            }

            channel.ConsecutiveTimeouts = 0;
            unanswered = connection;
        }

        SteamUiLog.Warn(
            $"Steam UI {channel.Role} stopped answering {UnansweredEvaluationsBeforeReconnect} "
            + "evaluations in a row; retiring the connection so the channel reconnects.");
        // Disposal runs the connection's close path, which unpublishes it and lets the channel's
        // reconnect loop rebuild. Detaching here instead would retire that loop with it.
        _ = DisposeDetachedConnectionAsync(channel.Role, unanswered);
    }

    private static void SetHealth(
        TargetChannel channel,
        long ownershipGeneration,
        SteamUiTransportHealth health,
        string? failure)
    {
        lock (channel.Sync)
        {
            if (channel.OwnershipGeneration != ownershipGeneration)
            {
                return;
            }

            channel.Health = health;
            channel.LastFailure = SteamUiShared.Bound(failure, SteamUiShared.MaximumDiagnosticLength);
        }
    }

    /// <summary>Retires the channel's owner and hands back what the caller must close.</summary>
    /// <param name="channel">The channel, whose lock the caller holds.</param>
    /// <param name="health">The health the detached channel reports.</param>
    /// <returns>The reconnect loop's cancellation and the connection, both now unpublished.</returns>
    private static (CancellationTokenSource? Reconnect, SteamUiCdpConnection? Connection) DetachLocked(
        TargetChannel channel,
        SteamUiTransportHealth health)
    {
        channel.OwnershipGeneration++;
        var reconnect = channel.ReconnectCancellation;
        channel.ReconnectCancellation = null;
        var connection = channel.Connection;
        channel.Connection = null;
        channel.Health = health;
        return (reconnect, connection);
    }

    private static async Task DisposeDetachedConnectionAsync(
        SteamUiTargetRole role,
        SteamUiCdpConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Closing disabled Steam UI {role} channel failed: {ex.Message}");
        }
    }

    /// <summary>Keeps the role's latest snapshot and queues the role when none was pending.</summary>
    private void RaiseGenerationChanged(TargetChannel channel)
    {
        bool queued;
        lock (channel.Sync)
        {
            queued = channel.PendingGeneration is not null;
            channel.PendingGeneration = Snapshot(channel);
        }

        if (!queued)
        {
            _generationEvents.Writer.TryWrite(channel.Role);
        }
    }

    // Both pumps start in the constructor, which a consumer may well run on its UI thread. Without
    // ConfigureAwait(false) the loop captures that SynchronizationContext and posts every handler
    // back to it, so a busy UI thread stalls generation and binding delivery instead of the other
    // way round.
    private async Task PumpBindingsAsync()
    {
        await foreach (var notification in _bindingEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Raise(NotificationReceived, notification, "Steam UI binding handler failed");
        }
    }

    private async Task PumpGenerationsAsync()
    {
        await foreach (var role in _generationEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var channel = _channels[role];
            SteamUiTransportSnapshot? snapshot;
            lock (channel.Sync)
            {
                snapshot = channel.PendingGeneration;
                channel.PendingGeneration = null;
            }

            if (snapshot is not null)
            {
                Raise(GenerationChanged, snapshot, "Steam UI generation handler failed");
            }
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T item, string failure)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, item);
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"{failure}: {ex.Message}");
            }
        }
    }

    private static SteamUiTransportSnapshot Snapshot(TargetChannel channel)
    {
        lock (channel.Sync)
        {
            return new SteamUiTransportSnapshot(
                channel.Role,
                channel.Health,
                channel.Generations,
                channel.TargetId,
                channel.LastFailure,
                channel.Connection?.OutstandingRequests ?? 0,
                channel.Subscribers);
        }
    }

    private TargetChannel GetChannel(SteamUiTargetRole role)
    {
        return _channels.TryGetValue(role, out var channel)
            ? channel
            : throw new ArgumentOutOfRangeException(nameof(role));
    }

    private static long GetOwnershipGeneration(TargetChannel channel)
    {
        lock (channel.Sync)
        {
            return channel.OwnershipGeneration;
        }
    }

    private static SteamUiGenerations GenerationsOf(TargetChannel channel)
    {
        lock (channel.Sync)
        {
            return channel.Generations;
        }
    }

    private sealed class TargetChannel(SteamUiTargetRole role)
    {
        internal object Sync { get; } = new();

        internal SemaphoreSlim ConnectGate { get; } = new(1, 1);

        internal SteamUiTargetRole Role { get; } = role;

        internal SteamUiTransportHealth Health { get; set; } = SteamUiTransportHealth.Idle;

        internal SteamUiGenerations Generations { get; set; }

        internal string? BrowserId { get; set; }

        internal string? TargetId { get; set; }

        internal string? LastFailure { get; set; }

        internal SteamUiCdpConnection? Connection { get; set; }

        internal int Subscribers { get; set; }

        /// <summary>
        ///     Evaluations that have run out their own deadline back to back on this connection.
        ///     A renderer that has stopped answering keeps its websocket open, so nothing else ever
        ///     retires the socket; see <see cref="DropUnansweredConnection" />.
        /// </summary>
        internal int ConsecutiveTimeouts { get; set; }

        internal long OwnershipGeneration { get; set; }

        internal CancellationTokenSource? ReconnectCancellation { get; set; }

        /// <summary>The latest undelivered generation snapshot; the role is queued once while it is set.</summary>
        internal SteamUiTransportSnapshot? PendingGeneration { get; set; }
    }

    private readonly struct RequestLease(
        IAsyncDisposable subscription,
        TargetChannel channel,
        long ownershipGeneration,
        CancellationTokenSource deadline) : IAsyncDisposable
    {
        private readonly IAsyncDisposable _subscription = subscription;
        private readonly CancellationTokenSource _deadline = deadline;

        internal TargetChannel Channel { get; } = channel;

        internal long OwnershipGeneration { get; } = ownershipGeneration;

        internal CancellationToken Deadline => _deadline.Token;

        public ValueTask DisposeAsync()
        {
            _deadline.Dispose();
            return _subscription.DisposeAsync();
        }
    }

    private sealed class Subscription(
        PersistentSteamUiTransport owner,
        TargetChannel channel) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.ReleaseAsync(channel)
                : ValueTask.CompletedTask;
        }
    }
}
