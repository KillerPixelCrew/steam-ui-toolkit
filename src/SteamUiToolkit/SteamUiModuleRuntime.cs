using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     Runs the two traffic directions between registered modules and the injected side: state pushed
///     out, and commands answered back.
/// </summary>
/// <remarks>
///     The host owns patch enablement policy. This runtime coalesces publications, routes semantic
///     commands and logs refusal reasons without payload string values. A throwing module callback
///     quarantines that module until replacement and faults its patches for removal. Handlers must
///     return tasks promptly; backend owners serialize writes and honor cancellation.
/// </remarks>
public sealed class SteamUiModuleRuntime : IAsyncDisposable
{
    private readonly SteamUiBridgeHost _bridge;

    private readonly Func<bool> _commandsEnabled;

    // Modules that failed, by instance: a module that is removed and declared again by a restarted
    // plugin is a new instance and starts unfaulted.
    private readonly HashSet<ISteamUiModule> _failedModules = new(ReferenceEqualityComparer.Instance);

    // Sequences restart at 1 for every bridge generation, so a handler from the previous document
    // that has not yet observed cancellation would otherwise collide with the new document's first
    // request, and the new request would be dropped unanswered.
    private readonly Dictionary<InflightKey, (CancellationTokenSource Cancellation, ISteamUiModule? Module)>
        _inflight = [];

    private readonly object _moduleGate = new();
    private readonly SemaphoreSlim _modulesChange = new(1, 1);
    private readonly SteamUiPatchManager _patches;
    private readonly Task _publication;
    private readonly SemaphoreSlim _publicationSignal = new(0, 1);
    private readonly Func<bool> _publishEnabled;
    private readonly object _requestGate = new();
    private readonly HashSet<Task> _requestTasks = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;
    private volatile SteamUiModuleSet _modules;
    private int _publicationPending;

    /// <summary>Raised once when a module's callback or publication throws.</summary>
    public event EventHandler<SteamUiModuleFailure>? ModuleFailed;

    /// <summary>Starts the publication pump and begins answering bridge requests.</summary>
    /// <param name="bridge">The bridge this runtime publishes through and answers on.</param>
    /// <param name="modules">The registered modules supplying publications and command handlers.</param>
    /// <param name="patches">
    ///     The manager the modules' patches are registered with. A failing module's patches are faulted
    ///     there, so they are removed and stay off for as long as the module is registered.
    /// </param>
    /// <param name="commandsEnabled">
    ///     Whether commands may be answered at all right now. A command
    ///     arriving while this is false is refused with a reason rather than dropped.
    /// </param>
    /// <param name="publishEnabled">
    ///     Whether any state may be published this round. Evaluated once
    ///     per round; each publication's own gate is evaluated separately.
    /// </param>
    public SteamUiModuleRuntime(
        SteamUiBridgeHost bridge,
        SteamUiModuleSet modules,
        SteamUiPatchManager patches,
        Func<bool> commandsEnabled,
        Func<bool> publishEnabled)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _patches = patches ?? throw new ArgumentNullException(nameof(patches));
        _commandsEnabled = commandsEnabled ?? throw new ArgumentNullException(nameof(commandsEnabled));
        _publishEnabled = publishEnabled ?? throw new ArgumentNullException(nameof(publishEnabled));
        _bridge.RequestReceived += OnRequestReceived;
        _publication = Task.Run(PublishLoopAsync);
    }

    /// <summary>Stops answering, drains in-flight work, and releases the pump.</summary>
    /// <returns>An asynchronous operation that waits without a deadline for publication and command work to drain.</returns>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Stops answering and waits for in-flight work, no longer than the caller allows.</summary>
    /// <param name="cancellationToken">
    ///     The caller's deadline. When it fires, the publication round and the requests still running
    ///     are left to finish on their own, named in one log line, and this returns.
    /// </param>
    /// <returns>A task that completes once the work drained or the deadline passed.</returns>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _bridge.RequestReceived -= OnRequestReceived;
        CancelAllInflight();
        _shutdown.Cancel();
        try
        {
            await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            LogAbandoned();
            return;
        }

        Task[] requestTasks;
        lock (_requestGate)
        {
            requestTasks = [.. _requestTasks];
        }

        try
        {
            await Task.WhenAll(requestTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogAbandoned();
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI semantic request cleanup failed: {ex.Message}");
        }

        // These managed synchronization objects are collected with the runtime. A bridge callback
        // already dispatched just before unsubscription may still observe the cancelled token;
        // disposing the source here would turn that harmless late callback into a teardown race.
    }

    private void LogAbandoned()
    {
        int requests;
        lock (_requestGate)
        {
            requests = _requestTasks.Count;
        }

        SteamUiLog.Warn(
            $"Steam UI semantic runtime shutdown reached its deadline with {requests} request(s) still "
            + $"running and the publication round {(_publication.IsCompleted ? "finished" : "still running")}.");
    }

    /// <summary>Asks for one publication round, coalescing repeats into the pending one.</summary>
    public void QueuePublication()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _publicationPending, 1) == 0)
        {
            _publicationSignal.Release();
        }
    }

    /// <summary>Routes commands and publications through another module set.</summary>
    /// <param name="next">
    ///     The complete set from now on: the modules that stay, the ones added and none of the ones
    ///     removed.
    /// </param>
    /// <param name="cancellationToken">Cancels the removal of patches that left.</param>
    /// <returns>A task that completes once added patches were registered and removed ones retracted.</returns>
    /// <remarks>
    ///     For modules that come and go while the host runs, such as a plugin's when it becomes ready or
    ///     stops. A patch is kept only when the same instance is in both sets; a module declared again by
    ///     a restarted plugin is new, so its patches are removed and registered again and it starts
    ///     unfaulted. Added patches are registered first, then the bridge's vocabulary and the set are
    ///     swapped (<see cref="SteamUiBridgeHost.SetAllowedCommands" />), then removed patches are
    ///     retracted and unregistered. A removed module's in-flight requests are cancelled and its
    ///     commands are refused from the moment the set is swapped.
    ///     <para>
    ///         Registered patches start with their switch on, and when to apply them is the consumer's
    ///         policy: set the added patches' switches, then queue a synchronization. That pass applies
    ///         them and, when the vocabulary changed, installs the bridge again with the new one.
    ///     </para>
    /// </remarks>
    public async Task ReplaceModulesAsync(SteamUiModuleSet next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _modulesChange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _modules;
            var nextPatches = new HashSet<ISteamUiPatch>(next.Patches, ReferenceEqualityComparer.Instance);
            var currentPatches = new HashSet<ISteamUiPatch>(current.Patches, ReferenceEqualityComparer.Instance);
            var nextModules = new HashSet<ISteamUiModule>(next.Modules, ReferenceEqualityComparer.Instance);
            var removed = current.Patches.Where(patch => !nextPatches.Contains(patch)).ToArray();
            var added = next.Patches.Where(patch => !currentPatches.Contains(patch)).ToArray();

            // A patch removed and declared again by a new instance keeps its id, so the old entry has
            // to leave the manager before the new one can join it.
            var readded = new HashSet<string>(
                removed.Select(patch => patch.Id).Intersect(added.Select(patch => patch.Id), StringComparer.Ordinal),
                StringComparer.Ordinal);
            foreach (var patch in added.Where(patch => !readded.Contains(patch.Id)))
            {
                _patches.Register(patch);
            }

            _bridge.SetAllowedCommands(next.AllowedCommands);
            _modules = next;
            lock (_moduleGate)
            {
                _failedModules.RemoveWhere(module => !nextModules.Contains(module));
            }

            List<CancellationTokenSource> leaving = [];
            lock (_requestGate)
            {
                foreach (var (cancellation, module) in _inflight.Values)
                {
                    if (module is not null && !nextModules.Contains(module))
                    {
                        leaving.Add(cancellation);
                    }
                }
            }

            foreach (var cancellation in leaving)
            {
                SteamUiShared.CancelSafely(cancellation);
            }

            foreach (var patch in removed)
            {
                await _patches.UnregisterAsync(patch.Id, cancellationToken).ConfigureAwait(false);
            }

            foreach (var patch in added.Where(patch => readded.Contains(patch.Id)))
            {
                _patches.Register(patch);
            }

            QueuePublication();
        }
        finally
        {
            _modulesChange.Release();
        }
    }

    /// <summary>Cancels every request still in flight.</summary>
    /// <remarks>
    ///     Called when a generation is replaced. A semantic operation is authorized against one
    ///     execution-context and document pair, so letting it continue after either moved could apply
    ///     a result for a page that can no longer receive its response — replacement is cancellation,
    ///     exactly like an explicit cancel from the injected side.
    /// </remarks>
    public void CancelAllInflight()
    {
        CancellationTokenSource[] inflight;
        lock (_requestGate)
        {
            inflight = [.. _inflight.Values.Select(entry => entry.Cancellation)];
        }

        foreach (var cancellation in inflight)
        {
            SteamUiShared.CancelSafely(cancellation);
        }
    }

    private void OnRequestReceived(object? sender, SteamUiBridgeRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (request.Type == "cancel")
        {
            CancelInflight(InflightKey.Of(request));
            return;
        }

        var task = RespondAsync(request);
        lock (_requestGate)
        {
            _requestTasks.Add(task);
        }

        _ = ObserveCompletionAsync(task);
    }

    private async Task RespondAsync(SteamUiBridgeRequest request)
    {
        using var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var key = InflightKey.Of(request);
        var found = _modules.TryGetCommand(request.PatchId, request.Command, out var handler, out var module);
        lock (_requestGate)
        {
            if (!_inflight.TryAdd(key, (requestCancellation, module)))
            {
                SteamUiLog.Warn(
                    $"Steam UI request {request.PatchId}/{request.Command} reused sequence "
                    + $"{request.Sequence} while an earlier one was still running; not answered.");
                return;
            }
        }

        SteamUiCommandResult outcome;
        try
        {
            if (requestCancellation.IsCancellationRequested)
            {
                RemoveInflight(key, requestCancellation);
                return;
            }

            // One reason per cause: a log or a row has to be able to say which it was.
            if (!_commandsEnabled())
            {
                outcome = SteamUiCommandResult.Refused;
            }
            else if (!found || handler is null || module is null)
            {
                outcome = SteamUiCommandResult.Unhandled;
            }
            else if (IsModuleFailed(module))
            {
                outcome = SteamUiCommandResult.Quarantined;
            }
            else
            {
                outcome = await handler(request, requestCancellation.Token).ConfigureAwait(false);
                if (!outcome.Succeeded && outcome.Error is null)
                {
                    outcome = outcome with { Error = SteamUiCommandResult.NoReasonReported };
                }
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            RemoveInflight(key, requestCancellation);
            return;
        }
        catch (Exception ex)
        {
            if (module is not null)
            {
                FailModule(module, "command " + request.Command, ex);
            }

            outcome = new SteamUiCommandResult(false, ex.Message);
        }

        // Keyed per patch and command, because a gate can repeat a refused write on its own
        // schedule: the first prints, the repeats are counted.
        if (!outcome.Succeeded)
        {
            // The payload's shape goes with the reason. A refusal that names a missing field is
            // unreadable without the payload it was reading: "named neither a volume nor a drive" is
            // the same line whether the identifier was absent, spelled differently, or of a type the
            // reader rejected, and those have completely different fixes. Only its shape, never its
            // values: a plugin's secret setting travels as a payload, and testers paste this log.
            SteamUiLog.Change(
                $"steam.ui.request.{request.PatchId}.{request.Command}",
                $"Steam UI request {request.PatchId}/{request.Command} did nothing: "
                + outcome.Error
                + " Payload: "
                + SteamUiShared.DescribePayload(
                    request.Payload.ValueKind == JsonValueKind.Undefined ? null : request.Payload.GetRawText()),
                true);
        }

        try
        {
            if (requestCancellation.IsCancellationRequested)
            {
                return;
            }

            var delivered = await _bridge.RespondAsync(
                    request,
                    outcome.Succeeded,
                    outcome.Payload,
                    outcome.Error,
                    requestCancellation.Token)
                .ConfigureAwait(false);
            if (!delivered)
            {
                SteamUiLog.Change(
                    $"steam.ui.response.{request.PatchId}.{request.Command}",
                    $"Steam UI response {request.PatchId}/{request.Command} was not accepted by "
                    + "the current document.",
                    true);
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI bridge response failed: {ex.Message}");
        }
        finally
        {
            RemoveInflight(key, requestCancellation);
        }
    }

    private async Task PublishLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _publicationSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _publicationPending, 0);
                if (!_publishEnabled() || !_bridge.IsReady)
                {
                    continue;
                }

                // Reads stay sequential: several of them reach Win32 or the registry and nothing here
                // promises them a thread. It is the deliveries that were the serial cost, one
                // Runtime.evaluate at a time under the operation timeout, so those go out together.
                List<Task> deliveries = [];
                foreach (var (publication, module) in _modules.OwnedPublications)
                {
                    try
                    {
                        if (IsModuleFailed(module) || !publication.Enabled())
                        {
                            continue;
                        }

                        // A revision the document already holds needs no read at all: a round is
                        // raised by any surface's change, and rebuilding every large state on each
                        // one was the bulk of the idle cost.
                        var revision = publication.Revision?.Invoke();
                        if (revision is { } current && _bridge.IsPublished(publication.PatchId, current))
                        {
                            continue;
                        }

                        var payload = await publication.Read().ConfigureAwait(false);
                        // Null publishes nothing this round, which keeps a reading that is
                        // momentarily unavailable distinct from a zero.
                        if (payload is not { } state)
                        {
                            continue;
                        }

                        deliveries.Add(PublishOneAsync(publication.PatchId, state, revision));
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        FailModule(module, "state publication", ex);
                        SteamUiLog.Change(
                            "steam.ui.publication." + publication.PatchId,
                            $"Steam UI state publication {publication.PatchId} failed: {ex.Message}",
                            true);
                    }
                }

                // PublishOneAsync reports every publication's own outcome and never faults, so one
                // failing surface cannot cancel the others or escape into the round's own handler.
                await Task.WhenAll(deliveries).ConfigureAwait(false);
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"Steam UI semantic state publication failed: {ex.Message}");
            }
        }
    }

    /// <summary>Delivers one publication's state and reports only its own outcome.</summary>
    /// <param name="patchId">The publishing patch.</param>
    /// <param name="state">The semantic state to deliver.</param>
    /// <param name="revision">The state's revision, when its publication declares one.</param>
    /// <remarks>
    ///     Deliberately faultless. These run together under one <see cref="Task.WhenAll(Task[])" />,
    ///     which surfaces a single exception and would otherwise let the first failing surface stand in
    ///     for the rest, hiding which one actually broke.
    /// </remarks>
    private async Task PublishOneAsync(string patchId, JsonElement state, long? revision)
    {
        try
        {
            var accepted = await (revision is { } value
                    ? _bridge.PublishStateAsync(patchId, state, value, _shutdown.Token)
                    : _bridge.PublishStateAsync(patchId, state, _shutdown.Token))
                .ConfigureAwait(false);
            if (!accepted)
            {
                SteamUiLog.Change(
                    "steam.ui.publication." + patchId,
                    $"Steam UI state publication {patchId} was not accepted by the current document.",
                    true);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SteamUiLog.Change(
                "steam.ui.publication." + patchId,
                $"Steam UI state publication {patchId} failed: {ex.Message}",
                true);
        }
    }

    private void CancelInflight(InflightKey key)
    {
        CancellationTokenSource? cancellation = null;
        lock (_requestGate)
        {
            if (_inflight.TryGetValue(key, out var entry))
            {
                cancellation = entry.Cancellation;
            }
        }

        SteamUiShared.CancelSafely(cancellation);
    }

    private void RemoveInflight(InflightKey key, CancellationTokenSource owner)
    {
        lock (_requestGate)
        {
            if (_inflight.TryGetValue(key, out var current)
                && ReferenceEquals(current.Cancellation, owner))
            {
                _inflight.Remove(key);
            }
        }
    }

    private async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI semantic request failed unexpectedly: {ex.Message}");
        }
        finally
        {
            lock (_requestGate)
            {
                _requestTasks.Remove(task);
            }
        }
    }

    private readonly record struct InflightKey(long Context, long Document, long Sequence)
    {
        internal static InflightKey Of(SteamUiBridgeRequest request)
        {
            return new InflightKey(request.ContextGeneration, request.DocumentGeneration, request.Sequence);
        }
    }

    /// <summary>Quarantines the module whose own callback failed and faults the patches it installs.</summary>
    /// <param name="module">The module that declared the failing callback.</param>
    /// <param name="operation">What failed, for the log.</param>
    /// <param name="error">The failure.</param>
    private void FailModule(ISteamUiModule module, string operation, Exception error)
    {
        lock (_moduleGate)
        {
            if (!_failedModules.Add(module))
            {
                return;
            }
        }

        SteamUiLog.Warn($"Steam UI module {module.Id} failed during {operation}: {error}");
        foreach (var patch in module.Patches)
        {
            _patches.Fault(patch.Id, $"Module {module.Id} failed during {operation}: {error.Message}");
        }

        ModuleFailed?.Invoke(this, new SteamUiModuleFailure(module, operation, error.Message, error.StackTrace));
    }

    private bool IsModuleFailed(ISteamUiModule module)
    {
        lock (_moduleGate)
        {
            return _failedModules.Contains(module);
        }
    }
}

/// <summary>One module failure isolated by the semantic runtime.</summary>
/// <param name="Module">The module that failed.</param>
/// <param name="Operation">The callback or publication that threw.</param>
/// <param name="Error">The failure message.</param>
/// <param name="Stack">The managed stack when available.</param>
public sealed record SteamUiModuleFailure(ISteamUiModule Module, string Operation, string Error, string? Stack);
