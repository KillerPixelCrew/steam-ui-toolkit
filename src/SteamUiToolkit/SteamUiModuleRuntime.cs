using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     Runs the two traffic directions between registered modules and the injected side: state pushed
///     out, and commands answered back.
/// </summary>
/// <remarks>
///     This is the half of hosting that is the same for any consumer. What it deliberately does NOT
///     own is which patches should be applied when — that is the host's policy, it differs per
///     application, and pulling it in here would mean a constructor full of predicates that only
///     describe one host's rules.
///     <para>
///         One rule here is load-bearing rather than incidental:
///         <b>
///             every refusal is logged with its
///             reason
///         </b>
///         . The reason is built by the module and handed straight back to the injected side,
///         which has nowhere to put it, so a control the user operated that quietly did nothing would
///         otherwise leave no trace at all on this side of the bridge. That defect cost a session — Steam
///         had a 28 W limit stored, the gate had forwarded it, and the hardware was still at 30 W with not
///         one line saying why.
///     </para>
/// </remarks>
public sealed class SteamUiModuleRuntime : IAsyncDisposable
{
    private readonly SteamUiBridgeHost _bridge;

    private readonly Func<bool> _commandsEnabled;

    // Sequences restart at 1 for every bridge generation, so a handler from the previous document
    // that has not yet observed cancellation would otherwise collide with the new document's first
    // request, and the new request would be dropped unanswered.
    private readonly Dictionary<InflightKey, CancellationTokenSource> _inflight = [];
    private readonly SteamUiModuleSet _modules;
    private readonly HashSet<string> _failedModules = new(StringComparer.Ordinal);
    private readonly object _moduleGate = new();
    private readonly Task _publication;
    private readonly SemaphoreSlim _publicationSignal = new(0, 1);
    private readonly Func<bool> _publishEnabled;
    private readonly object _requestGate = new();
    private readonly HashSet<Task> _requestTasks = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;
    private int _publicationPending;

    /// <summary>Raised once when a module's callback or publication throws.</summary>
    public event EventHandler<SteamUiModuleFailure>? ModuleFailed;

    /// <summary>Starts the publication pump and begins answering bridge requests.</summary>
    /// <param name="bridge">The bridge this runtime publishes through and answers on.</param>
    /// <param name="modules">The registered modules supplying publications and command handlers.</param>
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
        Func<bool> commandsEnabled,
        Func<bool> publishEnabled)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _commandsEnabled = commandsEnabled ?? throw new ArgumentNullException(nameof(commandsEnabled));
        _publishEnabled = publishEnabled ?? throw new ArgumentNullException(nameof(publishEnabled));
        _bridge.RequestReceived += OnRequestReceived;
        _publication = Task.Run(PublishLoopAsync);
    }

    /// <summary>Stops answering, drains in-flight work, and releases the pump.</summary>
    public async ValueTask DisposeAsync()
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
            await _publication.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] requestTasks;
        lock (_requestGate)
        {
            requestTasks = [.. _requestTasks];
        }

        try
        {
            await Task.WhenAll(requestTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI semantic request cleanup failed: {ex.Message}");
        }

        // These managed synchronization objects are collected with the runtime. A bridge callback
        // already dispatched just before unsubscription may still observe the cancelled token;
        // disposing the source here would turn that harmless late callback into a teardown race.
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
            inflight = [.. _inflight.Values];
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
        lock (_requestGate)
        {
            if (!_inflight.TryAdd(key, requestCancellation))
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

            outcome = !_commandsEnabled()
                      || IsModuleFailed(request.PatchId)
                      || !_modules.TryGetCommand(
                          request.PatchId,
                          request.Command,
                          out var handler)
                      || handler is null
                ? SteamUiCommandResult.Refused
                : await handler(request, requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            RemoveInflight(key, requestCancellation);
            return;
        }
        catch (Exception ex)
        {
            FailModule(request.PatchId, "command " + request.Command, ex);
            outcome = new SteamUiCommandResult(false, ex.Message);
        }

        // Keyed per patch and command, because a gate can repeat a refused write on its own
        // schedule: the first prints, the repeats are counted.
        if (!outcome.Succeeded)
        {
            // The payload goes with the reason. A refusal that names a missing field is unreadable
            // without the payload it was reading: "named neither a volume nor a drive" is the same
            // line whether the identifier was absent, spelled differently, or of a type the reader
            // rejected, and those have completely different fixes.
            SteamUiLog.Change(
                $"steam.ui.request.{request.PatchId}.{request.Command}",
                $"Steam UI request {request.PatchId}/{request.Command} did nothing: "
                + (outcome.Error ?? "no reason reported")
                + $" Payload: {request.Payload}",
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
                foreach (var publication in _modules.Publications)
                {
                    try
                    {
                        if (IsModuleFailed(publication.PatchId) || !publication.Enabled())
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

                        deliveries.Add(PublishOneAsync(publication.PatchId, state));
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        FailModule(publication.PatchId, "state publication", ex);
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
    /// <remarks>
    ///     Deliberately faultless. These run together under one <see cref="Task.WhenAll(Task[])" />,
    ///     which surfaces a single exception and would otherwise let the first failing surface stand in
    ///     for the rest, hiding which one actually broke.
    /// </remarks>
    private async Task PublishOneAsync(string patchId, JsonElement state)
    {
        try
        {
            var accepted = await _bridge.PublishStateAsync(patchId, state, _shutdown.Token)
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
        CancellationTokenSource? cancellation;
        lock (_requestGate)
        {
            _inflight.TryGetValue(key, out cancellation);
        }

        SteamUiShared.CancelSafely(cancellation);
    }

    private void RemoveInflight(InflightKey key, CancellationTokenSource owner)
    {
        lock (_requestGate)
        {
            if (_inflight.TryGetValue(key, out var current)
                && ReferenceEquals(current, owner))
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

    private void FailModule(string patchId, string operation, Exception error)
    {
        if (!_modules.TryGetModule(patchId, out var module) || module is null)
        {
            SteamUiLog.Warn($"Steam UI {operation} for unknown module {patchId} failed: {error.Message}");
            return;
        }

        lock (_moduleGate)
        {
            if (!_failedModules.Add(module.Id))
            {
                return;
            }
        }

        SteamUiLog.Warn($"Steam UI module {module.Id} failed during {operation}: {error}");
        ModuleFailed?.Invoke(this, new SteamUiModuleFailure(module, operation, error.Message, error.StackTrace));
    }

    private bool IsModuleFailed(string patchId)
    {
        if (!_modules.TryGetModule(patchId, out var module) || module is null)
        {
            return false;
        }

        lock (_moduleGate)
        {
            return _failedModules.Contains(module.Id);
        }
    }
}

/// <summary>One module failure isolated by the semantic runtime.</summary>
/// <param name="Module">The module that failed.</param>
/// <param name="Operation">The callback or publication that threw.</param>
/// <param name="Error">The failure message.</param>
/// <param name="Stack">The managed stack when available.</param>
public sealed record SteamUiModuleFailure(ISteamUiModule Module, string Operation, string Error, string? Stack);
