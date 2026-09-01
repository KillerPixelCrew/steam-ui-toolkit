using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A typed request from repository-owned Steam UI code to WSGM.</summary>
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
    JsonElement Payload);

/// <summary>Result of authorizing one narrow Steam UI bridge request.</summary>
/// <param name="Accepted">Whether the host may dispatch the request.</param>
/// <param name="Reason">A bounded rejection reason.</param>
public readonly record struct SteamUiBridgeAuthorizationResult(bool Accepted, string? Reason);

/// <summary>Authorizes consumer-declared commands without exposing generic evaluation or host APIs.</summary>
public sealed class SteamUiBridgeAuthorizer
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _commands;
    private readonly object _sync = new();
    private readonly Dictionary<string, (long Sequence, long ActionGeneration)> _last =
        new(StringComparer.Ordinal);
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
            || !_commands.TryGetValue(request.PatchId, out IReadOnlyList<string>? commands)
            || !Contains(commands, request.Command))
        {
            return Reject("patch command is not allowlisted");
        }
        if (request.Sequence <= 0 || request.ActionGeneration <= 0)
        {
            return Reject("sequence or action generation is invalid");
        }
        if (request.Payload.ValueKind == JsonValueKind.Undefined
            || request.Payload.GetRawText().Length > SteamUiBridgeHost.MaximumPayloadCharacters)
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

    private static bool Contains(IReadOnlyList<string> commands, string command)
    {
        for (int index = 0; index < commands.Count; index++)
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
        foreach ((string patchId, IReadOnlyList<string> commands) in allowedCommands)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
            ArgumentNullException.ThrowIfNull(commands);
            var names = new string[commands.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < commands.Count; index++)
            {
                string command = commands[index];
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

    private static SteamUiBridgeAuthorizationResult Reject(string reason) => new(false, reason);
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
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly ISteamUiTransport _transport;
    private readonly SteamUiInjectedAsset _asset;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _allowedCommands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly SteamUiBridgeAuthorizer _authorizer;
    // The injected side permits 32 pending requests; reserve matching room for each one's
    // cancellation so a saturated request burst cannot strand its own cleanup message.
    private readonly Channel<SteamUiBridgeRequest> _requests =
        Channel.CreateBounded<SteamUiBridgeRequest>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
    private readonly Task _requestPump;
    private SteamUiGenerations _generations;
    private long _generationEpoch;
    private volatile bool _ready;
    private int _disposed;

    /// <summary>Creates a bridge over the process-owned persistent transport.</summary>
    /// <param name="transport">The single Steam UI transport owner.</param>
    /// <param name="asset">The script this host injects, and its hash. Supplied by the host
    /// because the bridge has no business knowing what its consumer injects.</param>
    /// <param name="allowedCommands">The exact state identities and semantic commands declared by
    /// the consumer's modules. The bridge copies this vocabulary at construction.</param>
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
        _authorizer = new(default, _allowedCommands);
        _transport.NotificationReceived += OnNotificationReceived;
        _transport.GenerationChanged += OnGenerationChanged;
        // Cancellation messages share this queue with requests. A thread-pool continuation can be
        // delayed indefinitely when a host is saturated by synchronous providers, leaving an
        // already-running command unable to observe its cancel. One dedicated serial reader keeps
        // request ordering and cancellation independent of pool availability.
        _requestPump = Task.Factory.StartNew(
            DispatchRequests,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>Raised only after a request passes the compiled semantic allowlist.</summary>
    public event EventHandler<SteamUiBridgeRequest>? RequestReceived;

    /// <summary>Whether the bootstrap handshake is healthy for the current generation.</summary>
    public bool IsReady => _ready;

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
            lock (_stateSync)
            {
                _ready = false;
            }
            var snapshot = FindSharedSnapshot();
            await _transport.SetRuntimeBindingAsync(
                SteamUiTargetRole.SharedJsContext,
                BindingName,
                true,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);

            snapshot = FindSharedSnapshot();
            long bootstrapEpoch;
            lock (_stateSync)
            {
                bootstrapEpoch = _generationEpoch;
            }
            var configuration = BuildConfiguration(snapshot.Generations);
            var expression = _asset.Source.Replace(
                "__WSGM_CONFIGURATION_JSON__", configuration, StringComparison.Ordinal);
            var result = await _transport.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                expression,
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Reachable || result.Value is null)
            {
                return false;
            }
            using var handshake = JsonDocument.Parse(result.Value);
            bool ready = handshake.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True
                && result.Generations.ExecutionContext == snapshot.Generations.ExecutionContext
                && result.Generations.Document == snapshot.Generations.Document;
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
            lock (_stateSync)
            {
                _ready = false;
            }
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
        SteamUiGenerations generations;
        lock (_stateSync)
        {
            if (!_ready)
            {
                return false;
            }
            generations = _generations;
        }
        if (request.Version != SchemaVersion
            || request.ContextGeneration != generations.ExecutionContext
            || request.DocumentGeneration != generations.Document
            || !_allowedCommands.TryGetValue(request.PatchId, out var commands)
            || !ContainsCommand(commands, request.Command)
            || (payload.HasValue
                && payload.Value.GetRawText().Length > MaximumPayloadCharacters))
        {
            return false;
        }
        var json = BuildResponse(request, ok, payload, error);
        var expression = "(()=>{const b=window[" + SteamCef.JsString(Namespace)
            + "];return JSON.stringify({ok:!!(b&&b.deliver(JSON.parse("
            + SteamCef.JsString(json) + ")))});})()";
        var result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return IsPositiveAcknowledgement(result, generations);
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
        SteamUiGenerations generations;
        lock (_stateSync)
        {
            if (!_ready)
            {
                return false;
            }
            generations = _generations;
        }
        if (!_allowedCommands.ContainsKey(patchId)
            || payload.GetRawText().Length > MaximumPayloadCharacters)
        {
            return false;
        }

        var json = BuildState(patchId, payload, generations);
        var expression = "(()=>{const b=window[" + SteamCef.JsString(Namespace)
            + "];return JSON.stringify({ok:!!(b&&b.deliver(JSON.parse("
            + SteamCef.JsString(json) + ")))});})()";
        var result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return IsPositiveAcknowledgement(result, generations);
    }

    /// <summary>Removes only the WSGM-owned bridge namespace and Runtime binding.</summary>
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
        lock (_stateSync)
        {
            _ready = false;
        }
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
                    + ";const b=window[k];if(b&&b.dispose)b.dispose('WSGM removed');"
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
        SteamUiGenerations generations;
        lock (_stateSync)
        {
            if (!_ready)
            {
                return;
            }
            generations = _generations;
        }
        if (notification.Role != SteamUiTargetRole.SharedJsContext
            || notification.Method != "Runtime.bindingCalled"
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
                    warning: true);
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

    private void DispatchRequests()
    {
        while (_requests.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_requests.Reader.TryRead(out SteamUiBridgeRequest? request))
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

                EventHandler<SteamUiBridgeRequest>? handlers = RequestReceived;
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
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role != SteamUiTargetRole.SharedJsContext)
        {
            return;
        }
        lock (_stateSync)
        {
            if (snapshot.Generations.ExecutionContext != _generations.ExecutionContext
                || snapshot.Generations.Document != _generations.Document)
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

    private static bool ContainsCommand(IReadOnlyList<string> commands, string command)
    {
        for (int index = 0; index < commands.Count; index++)
        {
            if (string.Equals(commands[index], command, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPositiveAcknowledgement(
        SteamUiEvaluationResult result,
        SteamUiGenerations expectedGenerations)
    {
        if (!result.Reachable
            || result.Generations.ExecutionContext != expectedGenerations.ExecutionContext
            || result.Generations.Document != expectedGenerations.Document
            || string.IsNullOrWhiteSpace(result.Value))
        {
            return false;
        }
        try
        {
            using JsonDocument acknowledgement = JsonDocument.Parse(result.Value);
            JsonElement root = acknowledgement.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildConfiguration(SteamUiGenerations generations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("namespace", Namespace);
            writer.WriteString("binding", BindingName);

            // The bootstrap reuses an already-installed bridge when the version and both Steam
            // generations match, and neither of those changes when WSGM is updated. So a new WSGM
            // build kept running the PREVIOUS build's injected script until Steam itself restarted:
            // a fix to the bootstrap appeared to have no effect, and the only clue was a diagnostic
            // field that was missing from output the new code would have produced. Pinning the
            // asset's own hash makes a changed script replace the bridge on the next
            // synchronization, which is what "the bootstrap was updated" has to mean.
            writer.WriteString("assetHash", _asset.Sha256);
            writer.WriteNumber("contextGeneration", generations.ExecutionContext);
            writer.WriteNumber("documentGeneration", generations.Document);
            writer.WriteNumber("maximumPending", 32);
            writer.WriteNumber("timeoutMilliseconds", 5000);
            writer.WriteStartObject("allowed");
            foreach (var pair in _allowedCommands)
            {
                writer.WriteStartArray(pair.Key);
                foreach (var command in pair.Value)
                {
                    writer.WriteStringValue(command);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildResponse(
        SteamUiBridgeRequest request, bool ok, JsonElement? payload, string? error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("type", "response");
            writer.WriteString("patchId", request.PatchId);
            writer.WriteString("command", request.Command);
            writer.WriteNumber("sequence", request.Sequence);
            writer.WriteNumber("contextGeneration", request.ContextGeneration);
            writer.WriteNumber("documentGeneration", request.DocumentGeneration);
            writer.WriteBoolean("ok", ok);
            writer.WritePropertyName("payload");
            if (payload.HasValue)
            {
                payload.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }
            if (!string.IsNullOrEmpty(error))
            {
                writer.WriteString("error", error.Length <= 1024 ? error : error[..1024]);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildState(
        string patchId,
        JsonElement payload,
        SteamUiGenerations generations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("type", "state");
            writer.WriteString("patchId", patchId);
            writer.WriteNumber("contextGeneration", generations.ExecutionContext);
            writer.WriteNumber("documentGeneration", generations.Document);
            writer.WritePropertyName("payload");
            payload.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _transport.NotificationReceived -= OnNotificationReceived;
        _transport.GenerationChanged -= OnGenerationChanged;
        lock (_stateSync)
        {
            _ready = false;
        }
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
