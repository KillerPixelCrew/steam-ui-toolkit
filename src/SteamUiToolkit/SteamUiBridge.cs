using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A typed request from repository-owned Steam UI code to the host.</summary>
/// <param name="Version">Bridge schema version.</param>
/// <param name="Type">Either <c>request</c> or <c>cancel</c>.</param>
/// <param name="PatchId">The patch owning the command.</param>
/// <param name="Command">The allowlisted semantic command.</param>
/// <param name="Sequence">Monotonic request sequence in the current bridge generation.</param>
/// <param name="ActionGeneration">Origin generation used for duplicate suppression.</param>
/// <param name="ContextGeneration">Expected JavaScript context generation.</param>
/// <param name="DocumentGeneration">Expected document generation.</param>
/// <param name="Payload">Bounded semantic payload.</param>
public sealed record SteamUiBridgeRequest(
    int Version,
    string Type,
    string PatchId,
    string Command,
    long Sequence,
    long ActionGeneration,
    long ContextGeneration,
    long DocumentGeneration,
    JsonElement Payload)
{
    /// <summary>One identifier that follows this request through a backend's own log.</summary>
    /// <returns>The generations and sequence that authorized the request, joined.</returns>
    /// <remarks>
    ///     A method rather than a property so the source-generated serializer never treats it as a
    ///     wire field. The prefix is the established one from the logs this format was diagnosed in.
    /// </remarks>
    public string ToCorrelationId()
    {
        return $"native-qam:{ContextGeneration}:{DocumentGeneration}:{Sequence}:{ActionGeneration}";
    }
}

/// <summary>Result of authorizing one narrow Steam UI bridge request.</summary>
/// <param name="Accepted">Whether the host may dispatch the request.</param>
/// <param name="Reason">A bounded rejection reason.</param>
public readonly record struct SteamUiBridgeAuthorizationResult(bool Accepted, string? Reason);

/// <summary>Authorizes consumer-declared commands without exposing generic evaluation or host APIs.</summary>
public sealed class SteamUiBridgeAuthorizer
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _commands;

    private readonly Dictionary<string, (long Sequence, long ActionGeneration)> _last =
        new(StringComparer.Ordinal);

    private readonly object _sync = new();
    private SteamUiGenerations _generations;
    private long _lastRequestSequence;

    /// <summary>Creates an authorizer bound to one context and consumer vocabulary.</summary>
    /// <param name="generations">The generations that installed the bridge.</param>
    /// <param name="allowedCommands">The state identities and commands the consumer declared.</param>
    public SteamUiBridgeAuthorizer(
        SteamUiGenerations generations,
        IReadOnlyDictionary<string, IReadOnlyList<string>> allowedCommands)
    {
        _generations = generations;
        _commands = CopyVocabulary(allowedCommands);
    }

    /// <summary>Replaces the bridge generation and clears replay state.</summary>
    /// <param name="generations">The newly installed bridge generations.</param>
    public void Reset(SteamUiGenerations generations)
    {
        lock (_sync)
        {
            _generations = generations;
            _last.Clear();
            _lastRequestSequence = 0;
        }
    }

    /// <summary>Validates schema, vocabulary, generation, ordering, replay, and payload bounds.</summary>
    /// <param name="request">The decoded bridge request.</param>
    /// <returns>An explicit accept or rejection.</returns>
    public SteamUiBridgeAuthorizationResult Authorize(SteamUiBridgeRequest request)
    {
        if (request.Version != SteamUiBridgeHost.SchemaVersion)
        {
            return Reject("schema version mismatch");
        }

        if (request.Type is not ("request" or "cancel"))
        {
            return Reject("message type is not allowlisted");
        }

        if (string.IsNullOrEmpty(request.PatchId)
            || string.IsNullOrEmpty(request.Command)
            || !_commands.TryGetValue(request.PatchId, out var commands)
            || !Contains(commands, request.Command))
        {
            return Reject("patch command is not allowlisted");
        }

        if (request.Sequence <= 0 || request.ActionGeneration <= 0)
        {
            return Reject("sequence or action generation is invalid");
        }

        if (request.Payload.ValueKind == JsonValueKind.Undefined
            || SteamUiBridgeHost.ExceedsPayloadLimit(request.Payload))
        {
            return Reject("payload exceeded its limit");
        }

        lock (_sync)
        {
            if (request.ContextGeneration != _generations.ExecutionContext
                || request.DocumentGeneration != _generations.Document)
            {
                return Reject("stale bridge generation");
            }

            var key = request.PatchId + "\n" + request.Command;
            _last.TryGetValue(key, out var previous);
            if (request.Type == "cancel")
            {
                return request.Sequence <= previous.Sequence
                    ? new SteamUiBridgeAuthorizationResult(true, null)
                    : Reject("cancel references an unknown request");
            }

            if (request.Sequence <= _lastRequestSequence || request.Sequence <= previous.Sequence)
            {
                return Reject("request sequence was replayed");
            }

            if (request.ActionGeneration <= previous.ActionGeneration)
            {
                return Reject("action generation was replayed");
            }

            _lastRequestSequence = request.Sequence;
            _last[key] = (request.Sequence, request.ActionGeneration);
            return new SteamUiBridgeAuthorizationResult(true, null);
        }
    }

    internal static bool Contains(IReadOnlyList<string> commands, string command)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (string.Equals(commands[index], command, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CopyVocabulary(
        IReadOnlyDictionary<string, IReadOnlyList<string>> allowedCommands)
    {
        ArgumentNullException.ThrowIfNull(allowedCommands);
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (patchId, commands) in allowedCommands)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
            ArgumentNullException.ThrowIfNull(commands);
            var names = new string[commands.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(command);
                if (!seen.Add(command))
                {
                    throw new ArgumentException(
                        $"Patch '{patchId}' declares command '{command}' more than once.",
                        nameof(allowedCommands));
                }

                names[index] = command;
            }

            copy.Add(patchId, Array.AsReadOnly(names));
        }

        return copy;
    }

    private static SteamUiBridgeAuthorizationResult Reject(string reason)
    {
        return new SteamUiBridgeAuthorizationResult(false, reason);
    }
}

/// <summary>Installs and owns the versioned Runtime-binding bridge for native-QAM patches.</summary>
public sealed class SteamUiBridgeHost : IAsyncDisposable
{
    /// <summary>Current bridge schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Maximum decoded payload size accepted from injected code.</summary>
    public const int MaximumPayloadCharacters = 16 * 1024;

    private const string Namespace = SteamUiBridgeIdentity.Namespace;
    private const string BindingName = SteamUiBridgeIdentity.BindingName;

    // Handed to the injected side, which refuses a request beyond this many pending ones and
    // answers a request the host has not settled within the timeout itself.
    private const int MaximumPendingRequests = 32;
    private const int RequestTimeoutMilliseconds = 5000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _allowedCommands;
    private readonly SteamUiInjectedAsset _asset;
    private readonly SteamUiBridgeAuthorizer _authorizer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Task _requestPump;

    // The injected side permits MaximumPendingRequests pending requests; reserve matching room for
    // each one's cancellation so a saturated request burst cannot strand its own cleanup message.
    private readonly Channel<SteamUiBridgeRequest> _requests =
        Channel.CreateBounded<SteamUiBridgeRequest>(
            new BoundedChannelOptions(2 * MaximumPendingRequests)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    // The last envelope each patch id actually landed in the current document. The envelope carries
    // both generations, so a new document or execution context is a different string and republishes
    // everything without this needing to be consulted about it.
    private readonly ConcurrentDictionary<string, string> _published = new(StringComparer.Ordinal);

    private readonly object _stateSync = new();
    private readonly ISteamUiTransport _transport;
    private int _disposed;
    private long _generationEpoch;
    private SteamUiGenerations _generations;
    private volatile bool _ready;

    /// <summary>Creates a bridge over the process-owned persistent transport.</summary>
    /// <param name="transport">The single Steam UI transport owner.</param>
    /// <param name="asset">
    ///     The script this host injects, and its hash. Supplied by the host
    ///     because the bridge has no business knowing what its consumer injects.
    /// </param>
    /// <param name="allowedCommands">
    ///     The exact state identities and semantic commands declared by
    ///     the consumer's modules. The bridge copies this vocabulary at construction.
    /// </param>
    public SteamUiBridgeHost(
        ISteamUiTransport transport,
        SteamUiInjectedAsset asset,
        IReadOnlyDictionary<string, IReadOnlyList<string>> allowedCommands)
    {
        _asset = asset;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.Sha256);
        _allowedCommands = SteamUiBridgeAuthorizer.CopyVocabulary(allowedCommands);
        _authorizer = new SteamUiBridgeAuthorizer(default, _allowedCommands);
        _transport.NotificationReceived += OnNotificationReceived;
        _transport.GenerationChanged += OnGenerationChanged;
        _requestPump = DispatchRequestsAsync();
    }

    /// <summary>Whether the bootstrap handshake is healthy for the current generation.</summary>
    public bool IsReady => _ready;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transport.NotificationReceived -= OnNotificationReceived;
        _transport.GenerationChanged -= OnGenerationChanged;
        MarkNotReady();
        _requests.Writer.TryComplete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var gateHeld = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            gateHeld = true;
            await RemoveCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Steam may still be reachable while its document is already tearing down. Bridge
            // cleanup is best effort; it must not abort the enclosing desktop-restore sequence.
            SteamUiLog.Warn("Steam UI bridge removal exceeded the shutdown budget.");
        }
        finally
        {
            if (gateHeld)
            {
                _gate.Release();
            }
        }

        try
        {
            await _requestPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SteamUiLog.Warn("Steam UI bridge request handlers exceeded their shutdown budget.");
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI bridge request cleanup failed: {ex.Message}");
        }
    }

    /// <summary>Raised only after a request passes the compiled semantic allowlist.</summary>
    public event EventHandler<SteamUiBridgeRequest>? RequestReceived;

    /// <summary>Installs the Runtime binding and idempotent bootstrap for the current context.</summary>
    /// <param name="cancellationToken">Cancels installation.</param>
    /// <returns>True after a positive compatibility handshake.</returns>
    public async Task<bool> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            MarkNotReady();
            await _transport.SetRuntimeBindingAsync(
                SteamUiTargetRole.SharedJsContext,
                BindingName,
                true,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);

            var snapshot = FindSharedSnapshot();
            long bootstrapEpoch;
            lock (_stateSync)
            {
                bootstrapEpoch = _generationEpoch;
            }

            var configuration = BuildConfiguration(snapshot.Generations);
            var expression = _asset.Source.Replace(
                "__STEAM_UI_CONFIGURATION_JSON__", configuration, StringComparison.Ordinal);
            var result = await _transport.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                expression,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Reachable || result.Value is null)
            {
                return false;
            }

            var ready = IsPositiveAcknowledgement(
                result, snapshot.Generations, out var malformed);
            if (malformed is not null)
            {
                MarkNotReady();
                SteamUiLog.Warn($"Steam UI bridge bootstrap failed: {malformed}");
                return false;
            }

            lock (_stateSync)
            {
                if (ready && bootstrapEpoch == _generationEpoch)
                {
                    _generations = result.Generations;
                    _authorizer.Reset(_generations);
                    _ready = true;
                }
                else
                {
                    _ready = false;
                }

                return _ready;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkNotReady();
            SteamUiLog.Warn($"Steam UI bridge bootstrap failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns one command result to the exact current bridge generation.</summary>
    /// <param name="request">The accepted request being completed.</param>
    /// <param name="ok">Whether the command succeeded.</param>
    /// <param name="payload">A bounded JSON payload, or null.</param>
    /// <param name="error">A bounded semantic failure.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>True when the current document accepted the response.</returns>
    public async Task<bool> RespondAsync(
        SteamUiBridgeRequest request,
        bool ok,
        JsonElement? payload,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetReadyGenerations(out var generations)
            || request.Version != SchemaVersion
            || request.ContextGeneration != generations.ExecutionContext
            || request.DocumentGeneration != generations.Document
            || !_allowedCommands.TryGetValue(request.PatchId, out var commands)
            || !SteamUiBridgeAuthorizer.Contains(commands, request.Command)
            || (payload.HasValue && ExceedsPayloadLimit(payload.Value)))
        {
            return false;
        }

        return await DeliverAsync(
                BuildResponse(request, ok, payload, error),
                generations,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Publishes immutable semantic state to subscribers of one allowlisted patch.</summary>
    /// <param name="patchId">The exact allowlisted patch identity.</param>
    /// <param name="payload">Bounded semantic state with no raw device or host data.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>True when the current document accepted the state envelope.</returns>
    public async Task<bool> PublishStateAsync(
        string patchId,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetReadyGenerations(out var generations)
            || !_allowedCommands.ContainsKey(patchId)
            || ExceedsPayloadLimit(payload))
        {
            return false;
        }

        // Unchanged state is the common case: the publication signal is raised by fixed polls, not by
        // anything actually changing, so most rounds would re-escape and re-send envelopes the
        // document already holds. Re-sending one is not free, it is a Runtime.evaluate round trip
        // under the operation timeout.
        var envelope = BuildState(patchId, payload, generations);
        if (_published.TryGetValue(patchId, out var delivered)
            && string.Equals(delivered, envelope, StringComparison.Ordinal))
        {
            return true;
        }

        var accepted = await DeliverAsync(envelope, generations, cancellationToken)
            .ConfigureAwait(false);
        if (accepted)
        {
            _published[patchId] = envelope;
        }
        else
        {
            // A refused envelope was never taken, so the next round has to offer it again.
            _published.TryRemove(patchId, out _);
        }

        return accepted;
    }

    /// <summary>Hands one envelope to the injected bridge of the expected generation.</summary>
    /// <param name="envelope">The serialized response or state envelope.</param>
    /// <param name="generations">The generations the envelope was built for.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>True when that document accepted the envelope.</returns>
    private async Task<bool> DeliverAsync(
        string envelope,
        SteamUiGenerations generations,
        CancellationToken cancellationToken)
    {
        var expression = "(()=>{const b=window[" + SteamCef.JsString(Namespace)
                                                 + "];return JSON.stringify({ok:!!(b&&b.deliver(JSON.parse("
                                                 + SteamCef.JsString(envelope) + ")))});})()";
        var result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return IsPositiveAcknowledgement(result, generations, out _);
    }

    /// <summary>Removes only the host-owned bridge namespace and Runtime binding.</summary>
    /// <param name="cancellationToken">Cancels cleanup.</param>
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await RemoveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveCoreAsync(CancellationToken cancellationToken)
    {
        MarkNotReady();
        try
        {
            await _transport.SetRuntimeBindingAsync(
                SteamUiTargetRole.SharedJsContext,
                BindingName,
                false,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SteamUiLog.Warn($"Steam UI Runtime binding removal was incomplete: {ex.Message}");
        }

        try
        {
            await _transport.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                "(()=>{const k=" + SteamCef.JsString(Namespace)
                                 + ";const b=window[k];if(b&&b.dispose)b.dispose('Steam UI removed');"
                                 + "try{delete window[k];}catch(e){}return JSON.stringify({ok:true});})()",
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SteamUiLog.Warn($"Steam UI bridge namespace removal was incomplete: {ex.Message}");
        }
    }

    private void OnNotificationReceived(object? sender, SteamUiNotification notification)
    {
        if (notification.Role != SteamUiTargetRole.SharedJsContext
            || notification.Method != "Runtime.bindingCalled"
            || !TryGetReadyGenerations(out var generations)
            || notification.Generations.ExecutionContext != generations.ExecutionContext
            || notification.Generations.Document != generations.Document)
        {
            return;
        }

        try
        {
            using var parameters = JsonDocument.Parse(notification.ParametersJson);
            var root = parameters.RootElement;
            if (!root.TryGetProperty("name", out var name)
                || name.GetString() != BindingName
                || !root.TryGetProperty("payload", out var payloadElement)
                || payloadElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var payload = payloadElement.GetString();
            if (payload is null || payload.Length > MaximumPayloadCharacters)
            {
                return;
            }

            var request = JsonSerializer.Deserialize(
                payload, SteamUiBridgeJsonContext.Default.SteamUiBridgeRequest);
            if (request is null)
            {
                return;
            }

            var authorization = _authorizer.Authorize(request);
            if (!authorization.Accepted)
            {
                // The payload prefix is included because the identifying fields are exactly what a
                // decoding fault empties: "rejected /: schema version mismatch" describes a request
                // that never decoded just as well as one that was genuinely refused, and telling
                // them apart took a live tap on the Runtime binding.
                SteamUiLog.Change(
                    "steam.ui.bridge.rejected",
                    $"Steam UI bridge rejected {request.PatchId}/{request.Command}: "
                    + $"{authorization.Reason} (payload: "
                    + payload[..Math.Min(payload.Length, 200)] + ")",
                    true);
                return;
            }

            if (!_requests.Writer.TryWrite(request))
            {
                SteamUiLog.Warn(
                    $"Steam UI bridge request queue was full; refused {request.PatchId}/"
                    + request.Command + ".");
            }
        }
        catch (JsonException ex)
        {
            SteamUiLog.Warn($"Steam UI bridge rejected malformed payload: {ex.Message}");
        }
    }

    private async Task DispatchRequestsAsync()
    {
        // The pump starts in the constructor, so without this every RequestReceived handler runs on
        // whatever thread built the host. A handler that blocks before its first real await, such as
        // a hardware write, would then freeze that thread.
        await foreach (var request
                       in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                continue;
            }

            lock (_stateSync)
            {
                if (!_ready
                    || request.ContextGeneration != _generations.ExecutionContext
                    || request.DocumentGeneration != _generations.Document)
                {
                    continue;
                }
            }

            var handlers = RequestReceived;
            if (handlers is null)
            {
                continue;
            }

            foreach (EventHandler<SteamUiBridgeRequest> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, request);
                }
                catch (Exception ex)
                {
                    SteamUiLog.Warn(
                        $"Steam UI bridge request handler failed for {request.PatchId}/"
                        + $"{request.Command}: {ex.Message}");
                }
            }
        }
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role != SteamUiTargetRole.SharedJsContext)
        {
            return;
        }

        lock (_stateSync)
        {
            // A pure transport reconnect moves only Browser, Target or Session: the enable chatter
            // on the new socket is ignored before it is published, so context and document stay put.
            // Runtime.addBinding was registered on the old session, though, so no binding call can
            // arrive until bootstrap runs again, and staying ready would make IsReady lie.
            var current = snapshot.Generations;
            if (current.Browser != _generations.Browser
                || current.Target != _generations.Target
                || current.Session != _generations.Session
                || current.ExecutionContext != _generations.ExecutionContext
                || current.Document != _generations.Document)
            {
                _generationEpoch++;
                _ready = false;
                _authorizer.Reset(snapshot.Generations);
            }
        }
    }

    private SteamUiTransportSnapshot FindSharedSnapshot()
    {
        foreach (var snapshot in _transport.GetSnapshots())
        {
            if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("SharedJSContext channel is not registered.");
    }

    private void MarkNotReady()
    {
        lock (_stateSync)
        {
            _ready = false;
        }

        // Nothing survives in a document that is gone. The generations in each envelope would force a
        // republish anyway; dropping them here keeps a stale document's state out of the comparison
        // rather than relying on that.
        _published.Clear();
    }

    private bool TryGetReadyGenerations(out SteamUiGenerations generations)
    {
        lock (_stateSync)
        {
            generations = _generations;
            return _ready;
        }
    }

    /// <summary>Whether a payload's raw JSON exceeds <see cref="MaximumPayloadCharacters" />.</summary>
    /// <param name="payload">The payload to measure.</param>
    /// <returns>True when its raw text is longer than the limit in UTF-16 characters.</returns>
    /// <remarks>
    ///     UTF-8 never takes fewer bytes than UTF-16 takes characters, so a raw value within the limit in
    ///     bytes is within it in characters, and only a longer one is decoded to count. Neither
    ///     materializes the text the way measuring <see cref="JsonElement.GetRawText" /> did.
    /// </remarks>
    internal static bool ExceedsPayloadLimit(JsonElement payload)
    {
        var raw = JsonMarshal.GetRawUtf8Value(payload);
        return raw.Length > MaximumPayloadCharacters
               && Encoding.UTF8.GetCharCount(raw) > MaximumPayloadCharacters;
    }

    /// <summary>Reads an injected expression's <c>{ok:true}</c> answer for the expected generation.</summary>
    /// <param name="result">The evaluation result.</param>
    /// <param name="expectedGenerations">The generations the expression was sent to.</param>
    /// <param name="malformed">Why the answer could not be read, when it was not an object.</param>
    /// <returns>True for a structured positive answer from the expected generation.</returns>
    private static bool IsPositiveAcknowledgement(
        SteamUiEvaluationResult result,
        SteamUiGenerations expectedGenerations,
        out string? malformed)
    {
        malformed = null;
        if (!result.Reachable || result.Value is null)
        {
            return false;
        }

        bool ok;
        try
        {
            using var acknowledgement = JsonDocument.Parse(result.Value);
            ok = acknowledgement.RootElement.TryGetProperty("ok", out var value)
                 && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Unparseable text, or JSON whose root is not an object.
            malformed = ex.Message;
            return false;
        }

        return ok
               && result.Generations.ExecutionContext == expectedGenerations.ExecutionContext
               && result.Generations.Document == expectedGenerations.Document;
    }

    private static string WriteJson<TState>(TState state, Action<Utf8JsonWriter, TState> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            write(writer, state);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private string BuildConfiguration(SteamUiGenerations generations)
    {
        return WriteJson((Host: this, Generations: generations), static (writer, state) =>
        {
            writer.WriteString("namespace", Namespace);
            writer.WriteString("binding", BindingName);

            // The bootstrap reuses an already-installed bridge when the version and both Steam
            // generations match, and neither of those changes when the host is updated. So a new host
            // build kept running the PREVIOUS build's injected script until Steam itself restarted:
            // a fix to the bootstrap appeared to have no effect, and the only clue was a diagnostic
            // field that was missing from output the new code would have produced. Pinning the
            // asset's own hash makes a changed script replace the bridge on the next
            // synchronization, which is what "the bootstrap was updated" has to mean.
            writer.WriteString("assetHash", state.Host._asset.Sha256);
            writer.WriteNumber("contextGeneration", state.Generations.ExecutionContext);
            writer.WriteNumber("documentGeneration", state.Generations.Document);
            writer.WriteNumber("maximumPending", MaximumPendingRequests);
            writer.WriteNumber("timeoutMilliseconds", RequestTimeoutMilliseconds);
            writer.WriteStartObject("allowed");
            foreach (var pair in state.Host._allowedCommands)
            {
                writer.WriteStartArray(pair.Key);
                foreach (var command in pair.Value)
                {
                    writer.WriteStringValue(command);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    private static string BuildResponse(
        SteamUiBridgeRequest request, bool ok, JsonElement? payload, string? error)
    {
        return WriteJson((Request: request, Ok: ok, Payload: payload, Error: error), static (writer, state) =>
        {
            writer.WriteString("type", "response");
            writer.WriteString("patchId", state.Request.PatchId);
            writer.WriteString("command", state.Request.Command);
            writer.WriteNumber("sequence", state.Request.Sequence);
            writer.WriteNumber("contextGeneration", state.Request.ContextGeneration);
            writer.WriteNumber("documentGeneration", state.Request.DocumentGeneration);
            writer.WriteBoolean("ok", state.Ok);
            writer.WritePropertyName("payload");
            if (state.Payload.HasValue)
            {
                state.Payload.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            if (!string.IsNullOrEmpty(state.Error))
            {
                writer.WriteString(
                    "error", state.Error.Length <= 1024 ? state.Error : state.Error[..1024]);
            }
        });
    }

    private static string BuildState(
        string patchId,
        JsonElement payload,
        SteamUiGenerations generations)
    {
        return WriteJson((PatchId: patchId, Payload: payload, Generations: generations), static (writer, state) =>
        {
            writer.WriteString("type", "state");
            writer.WriteString("patchId", state.PatchId);
            writer.WriteNumber("contextGeneration", state.Generations.ExecutionContext);
            writer.WriteNumber("documentGeneration", state.Generations.Document);
            writer.WritePropertyName("payload");
            state.Payload.WriteTo(writer);
        });
    }
}

// CamelCase because that is what the bootstrap sends and what this file's own response writers
// emit. Without it the source generator matched PascalCase, and with case-insensitivity explicitly
// off NOTHING bound: every property took its default, so Version arrived as 0 and each request was
// refused as a "schema version mismatch" with an empty patch id. Every native-QAM command had been
// rejected since the bridge was written — invisible only because no row rendered to send one.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(SteamUiBridgeRequest))]
internal sealed partial class SteamUiBridgeJsonContext : JsonSerializerContext;
