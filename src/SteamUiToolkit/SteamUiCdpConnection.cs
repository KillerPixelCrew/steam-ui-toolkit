using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The framed message channel a CDP connection runs over.</summary>
/// <remarks>
/// Public because it is the seam a consumer substitutes to exercise its patches without a running
/// Steam client. Everything above it — generations, request correlation, the patch lifecycle — is
/// worth testing that way, and a framework that can only be tested against live Steam is one whose
/// consumers will not test at all.
/// </remarks>
public interface ISteamUiCdpWire : IAsyncDisposable
{
    /// <summary>Sends one complete message.</summary>
    /// <param name="message">The UTF-8 payload.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task completing when the message has been handed to the channel.</returns>
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>Waits for the next complete message.</summary>
    /// <param name="cancellationToken">Cancels the receive.</param>
    /// <returns>The payload, or <see langword="null"/> when the channel closed cleanly.</returns>
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Opens a channel to a discovered target.</summary>
public interface ISteamUiCdpWireFactory
{
    /// <summary>Connects to one target.</summary>
    /// <param name="endpoint">The discovered target, whose socket URL has already been checked to
    /// be loopback on the debug port.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The open channel.</returns>
    Task<ISteamUiCdpWire> ConnectAsync(
        SteamUiEndpoint endpoint, CancellationToken cancellationToken);
}

internal sealed class SteamUiWebSocketWireFactory : ISteamUiCdpWireFactory
{
    public async Task<ISteamUiCdpWire> ConnectAsync(
        SteamUiEndpoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(endpoint.SocketUri, cancellationToken).ConfigureAwait(false);
            return new SteamUiWebSocketWire(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class SteamUiWebSocketWire : ISteamUiCdpWire
{
    // Bounds what is READ. Steam's CEF is the peer here and its reply is accumulated into memory, so
    // without a cap a malformed or enormous response takes the shell down. Nothing bounds what WSGM
    // sends any more: WSGM decides that, and asserting our own payload is under a number we picked
    // only ever managed to refuse a legitimate one — the handheld glyph stylesheet carries every
    // control glyph and all three controller illustrations as data URIs, about 500 KB for the Claw,
    // and the old 96 KB expression cap rejected it. The patch reported "expression exceeded its byte
    // limit" and the Steam Input page silently kept Valve's artwork.
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly ClientWebSocket _socket;

    internal SteamUiWebSocketWire(ClientWebSocket socket) => _socket = socket;

    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _socket.SendAsync(
            message, WebSocketMessageType.Text, true, cancellationToken).AsTask();

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>(16 * 1024);
        while (true)
        {
            var memory = writer.GetMemory(16 * 1024);
            var result = await _socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Steam UI CDP emitted a non-text frame.");
            }
            writer.Advance(result.Count);
            if (writer.WrittenCount > MaximumResponseBytes)
            {
                throw new InvalidDataException("Steam UI CDP response exceeded its byte limit.");
            }
            if (result.EndOfMessage)
            {
                return writer.WrittenMemory.ToArray();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, "WSGM channel closed", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Disposal remains bounded; disposing the socket is the final cleanup.
            }
        }
        _socket.Dispose();
    }
}

internal sealed class SteamUiCdpConnection : IAsyncDisposable
{
    private const int MaximumOutstandingRequests = 32;
    private const int MaximumQueuedNotifications = 256;

    // Inbound only. A notification's parameters come from Steam and are held as a string, so this is
    // the same framing bound as the response cap. There is deliberately no cap on the expressions
    // WSGM sends.
    private const int MaximumNotificationBytes = 1024 * 1024;
    private readonly SteamUiEndpoint _endpoint;
    private readonly ISteamUiCdpWire _wire;
    private readonly Action<string, string> _notification;
    private readonly Action<SteamUiCdpConnection, Exception?> _closed;
    private readonly Channel<(string Method, string Parameters)> _notifications =
        Channel.CreateBounded<(string Method, string Parameters)>(
            new BoundedChannelOptions(MaximumQueuedNotifications)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _outstanding = new(MaximumOutstandingRequests);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task _reader = Task.CompletedTask;
    private Task _notificationPump = Task.CompletedTask;
    private int _nextRequestId;
    private int _disposed;
    private int _wireDisposed;
    private int _orphanResponses;
    private int _started;

    internal SteamUiCdpConnection(
        SteamUiEndpoint endpoint,
        ISteamUiCdpWire wire,
        Action<string, string> notification,
        Action<SteamUiCdpConnection, Exception?> closed)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _wire = wire ?? throw new ArgumentNullException(nameof(wire));
        _notification = notification ?? throw new ArgumentNullException(nameof(notification));
        _closed = closed ?? throw new ArgumentNullException(nameof(closed));
    }

    internal string TargetId => _endpoint.TargetId;

    internal int OutstandingRequests => _pending.Count;

    internal Task Completion => _reader;

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Steam UI CDP connection was already started.");
        }
        _notificationPump = DispatchNotificationsAsync();
        _reader = ReadLoopAsync();
    }

    internal Task<JsonElement> InvokeAsync(
        string method,
        Action<Utf8JsonWriter>? writeParameters,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        InvokeCoreAsync(method, writeParameters, timeout, cancellationToken);

    internal async Task<string?> EvaluateAsync(
        string expression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var response = await InvokeCoreAsync(
            "Runtime.evaluate",
            writer =>
            {
                writer.WriteString("expression", expression);
                writer.WriteBoolean("awaitPromise", true);
                writer.WriteBoolean("returnByValue", true);
                writer.WriteBoolean("userGesture", true);
            },
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (response.TryGetProperty("exceptionDetails", out var exception))
        {
            throw new InvalidDataException(
                $"Steam UI JavaScript exception: {Bound(exception.GetRawText(), 2048)}");
        }
        if (!response.TryGetProperty("result", out var result))
        {
            throw new InvalidDataException("Steam UI evaluation response lacked a result.");
        }
        if (result.TryGetProperty("value", out var value))
        {
            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
        }
        return null;
    }

    private async Task<JsonElement> InvokeCoreAsync(
        string method,
        Action<Utf8JsonWriter>? writeParameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        await _outstanding.WaitAsync(deadline.Token).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextRequestId);
        if (id <= 0)
        {
            _outstanding.Release();
            throw new InvalidOperationException("Steam UI CDP request identifiers were exhausted.");
        }

        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            _outstanding.Release();
            throw new InvalidOperationException("Steam UI CDP request identifier collision.");
        }

        try
        {
            var request = BuildRequest(id, method, writeParameters);
            await _sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                try
                {
                    await _wire.SendAsync(request, deadline.Token).ConfigureAwait(false);
                }
                catch
                {
                    _shutdown.Cancel();
                    throw;
                }
            }
            finally
            {
                _sendGate.Release();
            }
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            if (_pending.TryRemove(id, out _))
            {
                _outstanding.Release();
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var message = await _wire.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }
                ProcessMessage(message);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _notifications.Writer.TryComplete();
            try
            {
                await _notificationPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                SteamUiLog.Warn("Steam UI notification handlers exceeded their drain budget.");
            }
            var terminal = failure ?? new IOException("Steam UI CDP channel closed.");
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var completion))
                {
                    _outstanding.Release();
                    completion.TrySetException(terminal);
                }
            }
            try
            {
                await DisposeWireAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
            try
            {
                _closed(this, failure);
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"Steam UI close handler failed: {ex.Message}");
            }
        }
    }

    private async Task DispatchNotificationsAsync()
    {
        await foreach ((string method, string parameters) in _notifications.Reader.ReadAllAsync())
        {
            try
            {
                _notification(method, parameters);
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"Steam UI CDP notification handler failed: {ex.Message}");
            }
        }
    }

    private void ProcessMessage(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Steam UI CDP message was not an object.");
        }

        if (root.TryGetProperty("id", out var idElement))
        {
            if (idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt32(out var id)
                || id <= 0)
            {
                throw new InvalidDataException("Steam UI CDP response carried an invalid id.");
            }
            if (!_pending.TryRemove(id, out var completion))
            {
                if (Interlocked.Increment(ref _orphanResponses) <= 3)
                {
                    SteamUiLog.Warn($"Steam UI CDP ignored orphan response id {id}.");
                }
                return;
            }
            _outstanding.Release();
            if (root.TryGetProperty("error", out var error))
            {
                completion.TrySetException(new InvalidDataException(
                    $"Steam UI CDP error: {Bound(error.GetRawText(), 2048)}"));
                return;
            }
            if (!root.TryGetProperty("result", out var result))
            {
                completion.TrySetException(new InvalidDataException(
                    "Steam UI CDP response lacked result and error."));
                return;
            }
            completion.TrySetResult(result.Clone());
            return;
        }

        if (!root.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Steam UI CDP notification lacked a method.");
        }
        var method = methodElement.GetString()!;
        var parameters = root.TryGetProperty("params", out var value)
            ? value.GetRawText()
            : "{}";
        if (Encoding.UTF8.GetByteCount(parameters) > MaximumNotificationBytes)
        {
            throw new InvalidDataException("Steam UI CDP notification exceeded its byte limit.");
        }
        if (!_notifications.Writer.TryWrite((method, parameters)))
        {
            throw new InvalidDataException("Steam UI CDP notification queue exceeded its limit.");
        }
    }

    private static byte[] BuildRequest(
        int id, string method, Action<Utf8JsonWriter>? writeParameters)
    {
        var writerBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (writeParameters is not null)
            {
                writer.WriteStartObject("params");
                writeParameters(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return writerBuffer.WrittenMemory.ToArray();
    }

    private static string Bound(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "...";

    private async ValueTask DisposeWireAsync()
    {
        if (Interlocked.Exchange(ref _wireDisposed, 1) == 0)
        {
            await _wire.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        _notifications.Writer.TryComplete();
        await DisposeWireAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_reader, _notificationPump)
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch
        {
        }
        // Pending request continuations can still be leaving their semaphore finally blocks after
        // the reader drains them. These managed synchronization objects are collected with the
        // connection; explicitly disposing them here would race those continuations.
    }
}
