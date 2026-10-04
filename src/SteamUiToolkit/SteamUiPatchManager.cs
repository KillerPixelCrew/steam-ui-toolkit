using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Observable lifecycle state of one independently recoverable Steam UI patch.</summary>
public enum SteamUiPatchState
{
    /// <summary>The patch has not yet been probed.</summary>
    Unknown,

    /// <summary>The required Steam target is absent.</summary>
    AbsentTarget,

    /// <summary>The live target does not match the patch's unique structural fingerprint.</summary>
    Incompatible,

    /// <summary>The patch passed its probe and is being applied.</summary>
    Applying,

    /// <summary>The patch reported application but has not passed functional verification.</summary>
    Applied,

    /// <summary>The patch is applied and positively verified.</summary>
    Verified,

    /// <summary>The patch is independently impaired while other patches remain available.</summary>
    Degraded,

    /// <summary>The patch was disabled and its owned resources were removed.</summary>
    Disabled,

    /// <summary>Owned-resource removal failed and remains observable.</summary>
    RemoveFailed,

    /// <summary>The patch is waiting for its target or generation to recover.</summary>
    Retrying
}

/// <summary>Positive structural probe result required before patch application.</summary>
/// <param name="TargetPresent">Whether the target could be evaluated.</param>
/// <param name="Compatible">Whether the expected structure was found exactly once.</param>
/// <param name="Fingerprint">Stable semantic fingerprint, never a module id alone.</param>
/// <param name="Diagnostic">Bounded probe details.</param>
public sealed record SteamUiPatchProbeResult(
    bool TargetPresent,
    bool Compatible,
    string? Fingerprint,
    string? Diagnostic);

/// <summary>Result of applying, verifying, or removing a Steam UI patch.</summary>
/// <param name="Succeeded">Whether the phase completed positively.</param>
/// <param name="Diagnostic">Bounded phase details.</param>
public readonly record struct SteamUiPatchOperationResult(bool Succeeded, string? Diagnostic);

/// <summary>Context that evaluates one patch's expressions under its phase timeout.</summary>
public sealed class SteamUiPatchContext
{
    private readonly TimeSpan _operationTimeout;
    private readonly ISteamUiTransport _transport;

    internal SteamUiPatchContext(ISteamUiTransport transport, TimeSpan operationTimeout)
    {
        _transport = transport;
        _operationTimeout = operationTimeout;
    }

    /// <summary>Evaluates repository-owned code on the patch's allowlisted target.</summary>
    /// <param name="role">The patch target.</param>
    /// <param name="expression">Repository-owned JavaScript.</param>
    /// <param name="cancellationToken">Cancels the phase.</param>
    /// <returns>The bounded evaluation result.</returns>
    public Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return _transport.EvaluateAsync(role, expression, _operationTimeout, cancellationToken);
    }
}

/// <summary>One probed, verified, removable Steam UI patch.</summary>
public interface ISteamUiPatch
{
    /// <summary>Stable collision-resistant patch id.</summary>
    string Id { get; }

    /// <summary>Allowlisted target required by this patch.</summary>
    SteamUiTargetRole TargetRole { get; }

    /// <summary>
    ///     Maximum duration of one phase: probe, apply, verify or remove. Positive and at most 30
    ///     seconds; <see cref="SteamUiPatchManager.DefaultOperationTimeout" /> unless the patch says
    ///     otherwise.
    /// </summary>
    TimeSpan OperationTimeout => SteamUiPatchManager.DefaultOperationTimeout;

    /// <summary>Probes for a positive unique live fingerprint without mutation.</summary>
    /// <param name="context">Evaluates on the patch's target.</param>
    /// <param name="cancellationToken">Cancels the phase.</param>
    /// <returns>Whether the target is present and compatible, with its fingerprint.</returns>
    Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Applies only resources owned by this patch.</summary>
    /// <param name="context">Evaluates on the patch's target.</param>
    /// <param name="cancellationToken">Cancels the phase.</param>
    /// <returns>Whether the patch reported application.</returns>
    Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Functionally verifies the resulting patch state.</summary>
    /// <param name="context">Evaluates on the patch's target.</param>
    /// <param name="cancellationToken">Cancels the phase.</param>
    /// <returns>Whether the patch does what it claims.</returns>
    Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Removes and verifies removal of only this patch's owned resources.</summary>
    /// <param name="context">Evaluates on the patch's target.</param>
    /// <param name="cancellationToken">Cancels the phase.</param>
    /// <returns>Whether nothing of the patch remains.</returns>
    Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);
}

/// <summary>Sanitized health and compatibility evidence for one registered patch.</summary>
/// <param name="Id">Stable patch id.</param>
/// <param name="Enabled">Whether its individual kill switch permits application.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Fingerprint">Latest positive live fingerprint.</param>
/// <param name="Generations">Generations under which health was assessed.</param>
/// <param name="LastFailure">
///     Latest bounded failure. For a patch whose module failed, the reason it was taken off.
/// </param>
/// <param name="LastChangedUtc">Time of the latest state change.</param>
public sealed record SteamUiPatchSnapshot(
    string Id,
    bool Enabled,
    SteamUiPatchState State,
    string? Fingerprint,
    SteamUiGenerations Generations,
    string? LastFailure,
    DateTimeOffset LastChangedUtc);

/// <summary>Serializes patch work, isolates failures, and owns independent kill switches.</summary>
/// <remarks>
///     One synchronization pass, under one scheduler gate, first removes every patch that should be
///     off, gates before the bridge they live in, then applies every patch that should be on, the
///     bridge first. Shutdown removes in the same order.
/// </remarks>
public sealed class SteamUiPatchManager : IAsyncDisposable
{
    /// <summary>How many times an absent target is probed again within one generation: 1, 2, 4, 8 and 16 s after.</summary>
    private const int SettleRetryLimit = 5;

    // The manager subscribes to transport events in its constructor, so a generation change can be
    // enumerating patches on the pump thread while the host is still registering them. Registration
    // therefore publishes a new immutable map instead of mutating the one a reader may be walking.
    private readonly object _registrationSync = new();

    private readonly SemaphoreSlim _schedulerGate = new(1, 1);
    private readonly ISteamUiTransport _transport;
    private int _disposed;
    private bool _globalEnabled = true;

    private volatile ImmutableSortedDictionary<string, PatchEntry> _patches =
        ImmutableSortedDictionary.Create<string, PatchEntry>(StringComparer.Ordinal);

    private int _queuedSynchronizationPending;
    private int _queuedSynchronizationRunning;

    /// <summary>Creates a registry over the single process-owned Steam UI transport.</summary>
    /// <param name="transport">Persistent Steam UI transport.</param>
    public SteamUiPatchManager(ISteamUiTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.GenerationChanged += OnGenerationChanged;
    }

    /// <summary>The phase timeout a patch has unless it declares its own: eight seconds.</summary>
    public static TimeSpan DefaultOperationTimeout { get; } = TimeSpan.FromSeconds(8);

    /// <summary>
    ///     Raised after each queued synchronization pass, once the pass has released the scheduler, on
    ///     a thread of its own.
    /// </summary>
    /// <remarks>
    ///     A handler may await <see cref="SetGlobalEnabledAsync" /> or another awaited switch; that runs
    ///     its own pass. A handler that throws is logged and does not stop later passes. The awaited
    ///     forms and <see cref="SynchronizeAsync" /> do not raise it.
    /// </remarks>
    public event EventHandler? Synchronized;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Retracts every patch, bridge last, no longer than the caller allows.</summary>
    /// <param name="cancellationToken">
    ///     The caller's deadline. When it fires, the patches not yet removed are recorded as
    ///     <see cref="SteamUiPatchState.RemoveFailed" />, named in one log line, and this returns.
    /// </param>
    /// <returns>A task that completes once every patch was removed or the deadline passed.</returns>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transport.GenerationChanged -= OnGenerationChanged;
        Volatile.Write(ref _globalEnabled, false);
        CancelActivePatchOperations();
        // The pass this can wait behind may be the fire-and-forget queued one, which runs with no
        // token of its own. Its operations were just cancelled, so it ends promptly; the ceiling and
        // the caller's deadline still bound the wait behind an unresponsive steamwebhelper.
        using var gateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateTimeout.CancelAfter(SteamUiShared.MaximumOperationTimeout);
        try
        {
            await _schedulerGate.WaitAsync(gateTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SteamUiLog.Warn(
                "Steam UI patch manager shutdown gave up waiting for an in-flight "
                + "synchronization pass; patches were not removed.");
            return;
        }

        try
        {
            var pending = RemovalOrder(_patches.Values).ToList();
            while (pending.Count > 0)
            {
                var entry = pending[0];
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await RemovePatchAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetState(entry, SteamUiPatchState.RemoveFailed, Snapshot(entry).Fingerprint, ex.Message);
                }

                pending.RemoveAt(0);
            }

            if (pending.Count > 0)
            {
                foreach (var entry in pending)
                {
                    SetState(
                        entry,
                        SteamUiPatchState.RemoveFailed,
                        Snapshot(entry).Fingerprint,
                        "Shutdown deadline reached before removal.");
                }

                SteamUiLog.Warn(
                    "Steam UI patch manager shutdown reached its deadline before removing "
                    + string.Join(", ", pending.Select(entry => entry.Patch.Id)) + ".");
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
        // SemaphoreSlim has no unmanaged state. Leaving the scheduler objects for GC avoids a
        // dispose-versus-WaitAsync race with a caller that passed its disposed check immediately
        // before shutdown took ownership of the scheduler.
    }

    /// <summary>Registers a patch; one registered after the first synchronization joins the next pass.</summary>
    /// <param name="patch">The patch.</param>
    public void Register(ISteamUiPatch patch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(patch);
        if (string.IsNullOrWhiteSpace(patch.Id) || !Enum.IsDefined(patch.TargetRole))
        {
            throw new ArgumentException("Steam UI patch identity and target are required.", nameof(patch));
        }

        SteamUiShared.ThrowIfInvalidTimeout(
            patch.OperationTimeout,
            "Patch timeouts must be positive and no greater than 30 seconds.",
            nameof(patch));
        lock (_registrationSync)
        {
            if (_patches.ContainsKey(patch.Id))
            {
                throw new InvalidOperationException(
                    $"Steam UI patch '{patch.Id}' is already registered.");
            }

            _patches = _patches.Add(
                patch.Id,
                new PatchEntry(patch, new SteamUiPatchContext(_transport, patch.OperationTimeout)));
        }
    }

    /// <summary>Removes a patch from the page, then forgets it.</summary>
    /// <param name="patchId">The registered patch id; an unknown one is ignored.</param>
    /// <param name="cancellationToken">Cancels the wait for the scheduler and the removal.</param>
    /// <returns>A task that completes once the patch is no longer registered.</returns>
    /// <remarks>
    ///     The entry goes even when its removal failed, which is logged: the module that owned the
    ///     patch is gone, so nothing would ever try again. Its kill switch and any fault go with it, so
    ///     a patch registered again under the same id starts clean.
    /// </remarks>
    public async Task UnregisterAsync(string patchId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_patches.TryGetValue(patchId, out var entry))
        {
            return;
        }

        CancellationTokenSource? active;
        lock (entry.Sync)
        {
            entry.Removing = true;
            active = entry.ActiveOperationCancellation;
        }

        SteamUiShared.CancelSafely(active);
        await _schedulerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemovePatchAsync(entry, cancellationToken).ConfigureAwait(false);
            if (Snapshot(entry).State != SteamUiPatchState.Disabled)
            {
                SteamUiLog.Warn(
                    $"Steam UI patch {patchId} was unregistered without a confirmed removal: "
                    + (Snapshot(entry).LastFailure ?? "no detail"));
            }
        }
        finally
        {
            lock (_registrationSync)
            {
                _patches = _patches.Remove(patchId);
            }

            _schedulerGate.Release();
        }
    }

    /// <summary>Takes a patch off for good because the module behind it failed.</summary>
    /// <param name="patchId">The registered patch id; an unknown one is ignored.</param>
    /// <param name="reason">Why, kept as the patch's failure.</param>
    /// <remarks>
    ///     The patch is removed on the next pass and stays off whatever its switch says, for as long as
    ///     it is registered. There is no reset: unregistering it is the only way back.
    /// </remarks>
    public void Fault(string patchId, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Volatile.Read(ref _disposed) != 0 || !_patches.TryGetValue(patchId, out var entry))
        {
            return;
        }

        CancellationTokenSource? active;
        lock (entry.Sync)
        {
            if (entry.FaultReason is not null)
            {
                return;
            }

            entry.FaultReason = reason;
            active = entry.ActiveOperationCancellation;
        }

        SteamUiShared.CancelSafely(active);
        QueueSynchronization();
    }

    /// <summary>Enables or disables the global emergency kill switch.</summary>
    /// <param name="enabled">Whether any patch may remain applied.</param>
    /// <remarks>Queues a synchronization when the switch moves.</remarks>
    public void SetGlobalEnabled(bool enabled)
    {
        if (SetGlobalSwitch(enabled))
        {
            QueueSynchronization();
        }
    }

    /// <summary>Changes the global kill switch and waits until every patch has reacted.</summary>
    /// <param name="enabled">Whether any patch may remain applied.</param>
    /// <param name="cancellationToken">Cancels synchronization.</param>
    /// <returns>A task that completes once the pass ran.</returns>
    public Task SetGlobalEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        SetGlobalSwitch(enabled);
        return SynchronizeAsync(cancellationToken);
    }

    /// <summary>Sets one patch's independent kill switch.</summary>
    /// <param name="patchId">Stable patch id.</param>
    /// <param name="enabled">Whether that patch may be applied.</param>
    public void SetPatchEnabled(string patchId, bool enabled)
    {
        if (SetPatchSwitch(patchId, enabled, true))
        {
            QueueSynchronization();
        }
    }

    /// <summary>Changes one patch kill switch and waits until that patch has reacted.</summary>
    /// <param name="patchId">Stable patch id.</param>
    /// <param name="enabled">Whether the patch may be applied.</param>
    /// <param name="cancellationToken">Cancels synchronization.</param>
    /// <returns>A task that completes once the pass ran.</returns>
    /// <remarks>Runs a full pass; nothing in a pass is redone for a patch that already holds.</remarks>
    public Task SetPatchEnabledAsync(
        string patchId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        // Always synchronizes, even when the switch already had this value: a caller awaiting
        // cleanup must see a removal that previously failed attempted again.
        SetPatchSwitch(patchId, enabled, false);
        return SynchronizeAsync(cancellationToken);
    }

    /// <summary>Asks for one synchronization pass, coalescing repeats into the pending one.</summary>
    /// <remarks>
    ///     The pass runs on a thread of its own and raises <see cref="Synchronized" /> when it is done.
    ///     Hosts call this when something a probe depends on may have changed, such as a new target
    ///     generation.
    /// </remarks>
    public void QueueSynchronization()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _queuedSynchronizationPending, 1);
        StartQueuedSynchronizationIfNeeded();
    }

    private bool SetGlobalSwitch(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var changed = Volatile.Read(ref _globalEnabled) != enabled;
        Volatile.Write(ref _globalEnabled, enabled);
        if (!enabled)
        {
            CancelActivePatchOperations();
        }

        return changed;
    }

    private bool SetPatchSwitch(string patchId, bool enabled, bool onlyWhenChanged)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_patches.TryGetValue(patchId, out var entry))
        {
            throw new KeyNotFoundException($"Steam UI patch '{patchId}' is not registered.");
        }

        CancellationTokenSource? activeOperation = null;
        lock (entry.Sync)
        {
            if (onlyWhenChanged && entry.Enabled == enabled)
            {
                return false;
            }

            entry.Enabled = enabled;
            entry.Snapshot = entry.Snapshot with
            {
                Enabled = enabled,
                LastChangedUtc = DateTimeOffset.UtcNow
            };
            if (!enabled)
            {
                activeOperation = entry.ActiveOperationCancellation;
            }
        }

        SteamUiShared.CancelSafely(activeOperation);
        return true;
    }

    private void CancelActivePatchOperations()
    {
        foreach (var entry in _patches.Values)
        {
            CancellationTokenSource? cancellation;
            lock (entry.Sync)
            {
                cancellation = entry.ActiveOperationCancellation;
            }

            SteamUiShared.CancelSafely(cancellation);
        }
    }

    private void StartQueuedSynchronizationIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _queuedSynchronizationRunning, 1, 0) == 0)
        {
            // Always leave the caller before synchronizing. A transport backed by already-complete
            // tasks can otherwise run an entire patch pass inline for every switch changed in one
            // settings update, repeatedly reapplying the same resources before the next switch is
            // even set.
            _ = Task.Run(SynchronizeQueuedAsync);
        }
    }

    private async Task SynchronizeQueuedAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _queuedSynchronizationPending, 0) != 0)
            {
                try
                {
                    await SynchronizeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SteamUiLog.Warn($"Steam UI patch synchronization failed: {ex.Message}");
                }

                // After the pass released the scheduler, and never inline: a handler that awaits a
                // switch takes the scheduler like any other caller.
                if (Synchronized is not null)
                {
                    _ = Task.Run(RaiseSynchronized);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _queuedSynchronizationRunning, 0);
            if (Volatile.Read(ref _queuedSynchronizationPending) != 0)
            {
                StartQueuedSynchronizationIfNeeded();
            }
        }
    }

    private void RaiseSynchronized()
    {
        try
        {
            Synchronized?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Steam UI patch synchronization handler failed: {ex.Message}");
        }
    }

    /// <summary>Removes every patch that should be off, then probes and applies every one that should be on.</summary>
    /// <param name="cancellationToken">Cancels the synchronization.</param>
    /// <returns>A task that completes once the pass ran.</returns>
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _schedulerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var entries = _patches.Values.ToArray();
            // Removal first and the bridge last: a gate's removal goes through the bridge.
            foreach (var entry in RemovalOrder(entries).Where(entry => !Wanted(entry)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RemovePatchAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            foreach (var entry in ApplyOrder(entries).Where(Wanted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SynchronizePatchAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
    }

    /// <summary>Every patch in id order, except that the bridge comes first.</summary>
    /// <param name="entries">The patches.</param>
    /// <returns>The order patches are applied in.</returns>
    /// <remarks>
    ///     Every gate lives inside the bridge. In plain id order, steam-ui.animations and
    ///     steam-ui.artwork-browser were applied after a Big Picture restart before steam-ui.bridge:
    ///     they installed into the old bridge and verified, then the new bridge replaced them with
    ///     gates nobody installed, and both pages stayed on "Loading" until WSGM restarted
    ///     (2026-09-29).
    /// </remarks>
    private static IEnumerable<PatchEntry> ApplyOrder(IEnumerable<PatchEntry> entries)
    {
        return entries.OrderBy(entry => entry.Patch.Id != SteamUiBridgePatch.PatchId)
            .ThenBy(entry => entry.Patch.Id, StringComparer.Ordinal);
    }

    /// <summary>Every patch in id order, except that the bridge comes last.</summary>
    /// <param name="entries">The patches.</param>
    /// <returns>The order patches are removed in.</returns>
    /// <remarks>Removing a gate goes through the bridge, so the bridge cannot go first.</remarks>
    private static IEnumerable<PatchEntry> RemovalOrder(IEnumerable<PatchEntry> entries)
    {
        return entries.OrderBy(entry => entry.Patch.Id == SteamUiBridgePatch.PatchId)
            .ThenBy(entry => entry.Patch.Id, StringComparer.Ordinal);
    }

    private bool Wanted(PatchEntry entry)
    {
        if (!Volatile.Read(ref _globalEnabled))
        {
            return false;
        }

        lock (entry.Sync)
        {
            return entry.Enabled && entry.FaultReason is null && !entry.Removing;
        }
    }

    /// <summary>Returns immutable health snapshots for diagnostics and UI.</summary>
    /// <returns>One snapshot per registered patch, in id order.</returns>
    public IReadOnlyList<SteamUiPatchSnapshot> GetSnapshots()
    {
        return _patches.Values.Select(static entry =>
        {
            lock (entry.Sync)
            {
                return entry.Snapshot;
            }
        }).ToArray();
    }

    private async Task SynchronizePatchAsync(
        PatchEntry entry, CancellationToken cancellationToken)
    {
        var patch = entry.Patch;
        long? activeGenerationEpoch = null;
        CancellationTokenSource? activeOperation = null;
        string? appliedFingerprint = null;
        var applyStarted = false;
        try
        {
            activeOperation = new CancellationTokenSource();
            lock (entry.Sync)
            {
                entry.ActiveOperationCancellation = activeOperation;
            }

            entry.Subscription ??= await _transport.SubscribeAsync(
                patch.TargetRole, cancellationToken).ConfigureAwait(false);
            long generationEpoch;
            SteamUiPatchState stateBeforeProbe;
            string? fingerprintBeforeProbe;
            StateLine? line = null;
            lock (entry.Sync)
            {
                var observedSnapshot =
                    FindTransportSnapshot(patch.TargetRole);
                if (entry.TransportSnapshot is { } retainedSnapshot
                    && observedSnapshot is { } currentSnapshot
                    && retainedSnapshot.Generations != currentSnapshot.Generations)
                {
                    // The transport updates its current snapshot before raising GenerationChanged.
                    // A synchronization racing that event can therefore observe the replacement
                    // first. Treat that observation exactly like the event or the later event sees
                    // matching generations and cannot invalidate a stale Verified state anymore.
                    entry.TransportSnapshot = currentSnapshot;
                    entry.GenerationEpoch++;
                    if (entry.Snapshot.State is SteamUiPatchState.Applying
                        or SteamUiPatchState.Applied
                        or SteamUiPatchState.Verified)
                    {
                        line = SetStateLocked(
                            entry,
                            SteamUiPatchState.Retrying,
                            entry.Snapshot.Fingerprint,
                            "Steam UI generation changed; reapply required.");
                    }
                }
                else
                {
                    entry.TransportSnapshot = observedSnapshot;
                }

                generationEpoch = entry.GenerationEpoch;
                activeGenerationEpoch = generationEpoch;
                stateBeforeProbe = entry.Snapshot.State;
                fingerprintBeforeProbe = entry.Snapshot.Fingerprint;
            }

            Write(line);
            var operation = activeOperation.Token;
            SteamUiPatchProbeResult probe;
            try
            {
                probe = await RunPhaseAsync(entry, patch.ProbeAsync, cancellationToken, operation)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TrySetStateForGeneration(
                    entry,
                    generationEpoch,
                    SteamUiPatchState.Degraded,
                    null,
                    ex.Message);
                return;
            }

            if (!probe.TargetPresent)
            {
                TrySetStateForGeneration(
                    entry,
                    generationEpoch,
                    SteamUiPatchState.AbsentTarget,
                    null,
                    probe.Diagnostic);
                ScheduleSettleRetry(entry, generationEpoch);
                return;
            }

            if (!probe.Compatible || string.IsNullOrWhiteSpace(probe.Fingerprint))
            {
                var diagnostic = probe.Diagnostic
                                 ?? "Patch fingerprint was not a unique positive match.";
                if (stateBeforeProbe is SteamUiPatchState.Applying
                    or SteamUiPatchState.Applied
                    or SteamUiPatchState.Verified)
                {
                    await RetractIncompatiblePatchAsync(
                            entry,
                            generationEpoch,
                            diagnostic,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    TrySetStateForGeneration(
                        entry,
                        generationEpoch,
                        SteamUiPatchState.Incompatible,
                        null,
                        diagnostic);
                }

                return;
            }

            // A settings update can legitimately request another synchronization while this patch
            // is already healthy in the same generation. Verify the retained resource first and
            // only mutate it again if verification says it was lost. Besides avoiding needless UI
            // churn, this keeps a request bridge continuously available while unrelated switches
            // are changing.
            if (stateBeforeProbe == SteamUiPatchState.Verified
                && string.Equals(
                    fingerprintBeforeProbe,
                    probe.Fingerprint,
                    StringComparison.Ordinal))
            {
                var retainedVerification = await RunPhaseAsync(
                        entry, patch.VerifyAsync, cancellationToken, operation)
                    .ConfigureAwait(false);
                if (retainedVerification.Succeeded)
                {
                    TrySetStateForGeneration(
                        entry,
                        generationEpoch,
                        SteamUiPatchState.Verified,
                        probe.Fingerprint,
                        null);
                    return;
                }
            }

            if (!TrySetStateForGeneration(
                    entry,
                    generationEpoch,
                    SteamUiPatchState.Applying,
                    probe.Fingerprint,
                    null))
            {
                return;
            }

            appliedFingerprint = probe.Fingerprint;
            applyStarted = true;
            var applied = await RunPhaseAsync(
                    entry, patch.ApplyAsync, cancellationToken, operation)
                .ConfigureAwait(false);
            if (!applied.Succeeded)
            {
                // A failed apply may have injected part of itself already; it is not left there.
                await RemoveAfterFailedApplyAsync(
                        entry,
                        generationEpoch,
                        probe.Fingerprint,
                        applied.Diagnostic ?? "Patch application failed.",
                        SteamUiPatchState.Degraded,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!TrySetStateForGeneration(
                    entry,
                    generationEpoch,
                    SteamUiPatchState.Applied,
                    probe.Fingerprint,
                    null))
            {
                return;
            }

            var verified = await RunPhaseAsync(
                    entry, patch.VerifyAsync, cancellationToken, operation)
                .ConfigureAwait(false);
            if (verified.Succeeded)
            {
                TrySetStateForGeneration(
                    entry,
                    generationEpoch,
                    SteamUiPatchState.Verified,
                    probe.Fingerprint,
                    null);
                return;
            }

            // An applied-but-unverified mutation is not left in the client. It cannot be shown to
            // do what it claims, and leaving it there keeps Valve's own UI replaced by something
            // unproven while later synchronization probes and reapplies over it. Removal restores
            // the native surface; if that cannot be verified either, the patch says so.
            SteamUiLog.Warn(
                $"Steam UI patch {patch.Id} applied but did not verify; removing it: "
                + $"{verified.Diagnostic ?? "no detail"}");
            await RemoveAfterFailedApplyAsync(
                    entry,
                    generationEpoch,
                    probe.Fingerprint,
                    verified.Diagnostic ?? "Patch verification failed.",
                    SteamUiPatchState.Degraded,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Three sources share this token, and a log has to tell a switch from a hung renderer.
            var switchedOff = !Wanted(entry);
            bool generationChanged;
            lock (entry.Sync)
            {
                generationChanged = activeGenerationEpoch is { } epoch && entry.GenerationEpoch != epoch;
            }

            if (switchedOff)
            {
                // The removal pass that the switch queued takes whatever was applied.
                SetOutcome(entry, activeGenerationEpoch, SteamUiPatchState.Retrying,
                    "Patch operation cancelled by its switch.");
            }
            else if (generationChanged)
            {
                SetOutcome(entry, activeGenerationEpoch, SteamUiPatchState.Retrying,
                    "Steam UI generation changed during the operation.");
            }
            else
            {
                var timedOut = $"Patch operation timed out after {patch.OperationTimeout.TotalSeconds:0.#} s.";
                if (applyStarted && activeGenerationEpoch is { } epoch)
                {
                    // Removed, then probed again on the settle schedule rather than reapplied blindly.
                    if (await RemoveAfterFailedApplyAsync(
                                entry,
                                epoch,
                                appliedFingerprint,
                                timedOut,
                                SteamUiPatchState.Retrying,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        ScheduleSettleRetry(entry, epoch);
                    }
                }
                else
                {
                    SetOutcome(entry, activeGenerationEpoch, SteamUiPatchState.Retrying, timedOut);
                    if (activeGenerationEpoch is { } probed)
                    {
                        ScheduleSettleRetry(entry, probed);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (applyStarted && activeGenerationEpoch is { } epoch)
            {
                await RemoveAfterFailedApplyAsync(
                        entry,
                        epoch,
                        appliedFingerprint,
                        ex.Message,
                        SteamUiPatchState.Degraded,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                SetOutcome(entry, activeGenerationEpoch, SteamUiPatchState.Degraded, ex.Message);
            }
        }
        finally
        {
            if (activeOperation is not null)
            {
                lock (entry.Sync)
                {
                    if (ReferenceEquals(entry.ActiveOperationCancellation, activeOperation))
                    {
                        entry.ActiveOperationCancellation = null;
                    }
                }

                activeOperation.Dispose();
            }
        }
    }

    /// <summary>Removes what a failed, thrown or timed-out apply may already have injected.</summary>
    /// <param name="entry">The patch.</param>
    /// <param name="generationEpoch">The epoch the apply ran in.</param>
    /// <param name="fingerprint">The fingerprint the apply was made against.</param>
    /// <param name="diagnostic">Why the apply did not hold.</param>
    /// <param name="stateWhenRemoved">The state recorded when the removal succeeded.</param>
    /// <param name="cancellationToken">The pass's own cancellation, not the cancelled operation's.</param>
    /// <returns>Whether the removal succeeded.</returns>
    /// <remarks>Runs once under a fresh phase timeout. It is never retried here.</remarks>
    private static async Task<bool> RemoveAfterFailedApplyAsync(
        PatchEntry entry,
        long generationEpoch,
        string? fingerprint,
        string diagnostic,
        SteamUiPatchState stateWhenRemoved,
        CancellationToken cancellationToken)
    {
        SteamUiPatchOperationResult removed;
        try
        {
            removed = await RunPhaseAsync(entry, entry.Patch.RemoveAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            removed = new SteamUiPatchOperationResult(false, "Patch removal timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            removed = new SteamUiPatchOperationResult(false, ex.Message);
        }

        TrySetStateForGeneration(
            entry,
            generationEpoch,
            removed.Succeeded ? stateWhenRemoved : SteamUiPatchState.RemoveFailed,
            fingerprint,
            removed.Succeeded
                ? diagnostic
                : $"{diagnostic} Removal also failed: {removed.Diagnostic}");
        return removed.Succeeded;
    }

    /// <summary>Runs one patch phase under its own declared budget.</summary>
    /// <param name="entry">The patch whose phase runs, and whose context it runs in.</param>
    /// <param name="phase">The phase to run.</param>
    /// <param name="cancellationToken">The synchronization's own cancellation.</param>
    /// <param name="operationCancellation">
    ///     Cancels the active phase for a kill switch or
    ///     generation replacement.
    /// </param>
    /// <returns>The phase's result.</returns>
    /// <remarks>
    ///     One timeout per phase, as the bound is documented. A single source spanning probe, apply and
    ///     verify meant a reachable but slow client that spent most of the budget probing had its
    ///     otherwise in-budget apply or verification cancelled underneath it, and the patch dropped to
    ///     Retrying with nothing actually wrong.
    /// </remarks>
    private static async Task<T> RunPhaseAsync<T>(
        PatchEntry entry,
        Func<SteamUiPatchContext, CancellationToken, Task<T>> phase,
        CancellationToken cancellationToken,
        CancellationToken operationCancellation = default)
    {
        using var source =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operationCancellation);
        source.CancelAfter(entry.Patch.OperationTimeout);
        return await phase(entry.Context, source.Token).ConfigureAwait(false);
    }

    /// <summary>Records how a synchronization failed outside its phase results.</summary>
    /// <param name="entry">The patch.</param>
    /// <param name="generationEpoch">
    ///     The epoch the synchronization took, or null before it took
    ///     one; a later epoch's state is then left alone.
    /// </param>
    /// <param name="state">The resulting state.</param>
    /// <param name="failure">The bounded reason.</param>
    private static void SetOutcome(
        PatchEntry entry,
        long? generationEpoch,
        SteamUiPatchState state,
        string failure)
    {
        var fingerprint = Snapshot(entry).Fingerprint;
        if (generationEpoch is { } epoch)
        {
            TrySetStateForGeneration(entry, epoch, state, fingerprint, failure);
        }
        else
        {
            SetState(entry, state, fingerprint, failure);
        }
    }

    private async Task RemovePatchAsync(
        PatchEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = Snapshot(entry);
            string? reason;
            lock (entry.Sync)
            {
                reason = entry.FaultReason;
            }

            if (snapshot.State != SteamUiPatchState.Disabled)
            {
                try
                {
                    var removed = await RunPhaseAsync(
                            entry, entry.Patch.RemoveAsync, cancellationToken)
                        .ConfigureAwait(false);
                    SetState(entry,
                        removed.Succeeded
                            ? SteamUiPatchState.Disabled
                            : SteamUiPatchState.RemoveFailed,
                        snapshot.Fingerprint,
                        removed.Succeeded ? reason : removed.Diagnostic);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    SetState(
                        entry,
                        SteamUiPatchState.RemoveFailed,
                        snapshot.Fingerprint,
                        $"Patch removal timed out after {entry.Patch.OperationTimeout.TotalSeconds:0.#} s.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetState(
                        entry,
                        SteamUiPatchState.RemoveFailed,
                        snapshot.Fingerprint,
                        ex.Message);
                }
            }
            else
            {
                SetState(entry, SteamUiPatchState.Disabled, snapshot.Fingerprint, reason);
            }
        }
        finally
        {
            if (entry.Subscription is not null)
            {
                await entry.Subscription.DisposeAsync().ConfigureAwait(false);
                entry.Subscription = null;
            }
        }
    }

    private static async Task RetractIncompatiblePatchAsync(
        PatchEntry entry,
        long generationEpoch,
        string incompatibility,
        CancellationToken cancellationToken)
    {
        try
        {
            var removed = await RunPhaseAsync(
                    entry, entry.Patch.RemoveAsync, cancellationToken)
                .ConfigureAwait(false);
            TrySetStateForGeneration(
                entry,
                generationEpoch,
                removed.Succeeded
                    ? SteamUiPatchState.Incompatible
                    : SteamUiPatchState.RemoveFailed,
                null,
                removed.Succeeded
                    ? incompatibility
                    : $"{incompatibility} Removal also failed: {removed.Diagnostic}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TrySetStateForGeneration(
                entry,
                generationEpoch,
                SteamUiPatchState.RemoveFailed,
                null,
                $"{incompatibility} Removal timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TrySetStateForGeneration(
                entry,
                generationEpoch,
                SteamUiPatchState.RemoveFailed,
                null,
                $"{incompatibility} Removal failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Probes a patch whose target was absent again a few times, backing off, within the same
    ///     generation. A Steam window that has just loaded is probed before it has mounted everything:
    ///     the custom pages' router was absent three seconds into a reload on 2026-09-28, the patch
    ///     was refused as incompatible, and nothing asked again until the next reload, so every custom
    ///     page stayed blank. A probe now says so with <c>notReady</c> and is recorded as an absent
    ///     target; an incompatible verdict stands for the generation. A timed-out apply that was
    ///     removed again is probed on the same schedule.
    /// </summary>
    /// <param name="entry">The patch.</param>
    /// <param name="generationEpoch">The generation the retries belong to.</param>
    private void ScheduleSettleRetry(PatchEntry entry, long generationEpoch)
    {
        int attempt;
        lock (entry.Sync)
        {
            if (entry.SettleEpoch != generationEpoch)
            {
                entry.SettleEpoch = generationEpoch;
                entry.SettleAttempts = 0;
            }

            if (entry.SettleAttempts >= SettleRetryLimit)
            {
                return;
            }

            attempt = ++entry.SettleAttempts;
        }

        var delay = TimeSpan.FromSeconds(1 << (attempt - 1));
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            QueueSynchronization();
        });
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        foreach (var entry in _patches.Values)
        {
            if (entry.Patch.TargetRole != snapshot.Role)
            {
                continue;
            }

            CancellationTokenSource? activeOperation = null;
            StateLine? line = null;
            lock (entry.Sync)
            {
                // Compare the generation of the last published lifecycle result, not the mutable
                // transport observation. A concurrent synchronization can observe the transport's
                // new snapshot in the tiny interval before this event is raised; treating that
                // observation as completed work would leave an old Verified result current and
                // suppress the reapply this event exists to request.
                if (entry.Snapshot.Generations == snapshot.Generations)
                {
                    entry.TransportSnapshot = snapshot;
                    continue;
                }

                entry.TransportSnapshot = snapshot;
                entry.GenerationEpoch++;
                activeOperation = entry.ActiveOperationCancellation;
                if (activeOperation is not null
                    || entry.Snapshot.State is SteamUiPatchState.Applying
                        or SteamUiPatchState.Applied
                        or SteamUiPatchState.Verified)
                {
                    line = SetStateLocked(
                        entry,
                        SteamUiPatchState.Retrying,
                        entry.Snapshot.Fingerprint,
                        "Steam UI generation changed; reapply required.");
                }
            }

            Write(line);
            SteamUiShared.CancelSafely(activeOperation);
        }
    }

    private SteamUiTransportSnapshot? FindTransportSnapshot(SteamUiTargetRole role)
    {
        return _transport.GetSnapshots().FirstOrDefault(snapshot => snapshot.Role == role);
    }

    private static SteamUiPatchSnapshot Snapshot(PatchEntry entry)
    {
        lock (entry.Sync)
        {
            return entry.Snapshot;
        }
    }

    private static bool TrySetStateForGeneration(
        PatchEntry entry,
        long generationEpoch,
        SteamUiPatchState state,
        string? fingerprint,
        string? failure)
    {
        StateLine line;
        lock (entry.Sync)
        {
            if (entry.GenerationEpoch != generationEpoch)
            {
                return false;
            }

            line = SetStateLocked(entry, state, fingerprint, failure);
        }

        Write(line);
        return true;
    }

    private static void SetState(
        PatchEntry entry,
        SteamUiPatchState state,
        string? fingerprint,
        string? failure)
    {
        StateLine line;
        lock (entry.Sync)
        {
            line = SetStateLocked(entry, state, fingerprint, failure);
        }

        Write(line);
    }

    /// <summary>Records a patch's new state and returns the line that reports the transition.</summary>
    /// <param name="entry">The patch, whose lock the caller holds.</param>
    /// <param name="state">The new state.</param>
    /// <param name="fingerprint">The fingerprint to keep.</param>
    /// <param name="failure">The failure to keep, or null.</param>
    /// <returns>The log line, which the caller writes after leaving the lock.</returns>
    /// <remarks>
    ///     The single funnel every outcome of <see cref="SynchronizePatchAsync" /> passes through, which
    ///     is why the log line is built here rather than at each call site.
    ///     <para>
    ///         This state machine already computed exactly what a remote diagnosis needs — which patch,
    ///         whether the target was absent, whether the fingerprint matched, and a bounded diagnostic
    ///         saying why — and then put all of it in a snapshot that nothing logged. A native QAM that
    ///         never appeared produced no line at all, so "nothing is in Steam's QAM" and "the host never tried"
    ///         looked identical from a pasted log.
    ///     </para>
    ///     <para>
    ///         Keyed per patch so each one's transitions are tracked independently, and via
    ///         <see cref="SteamUiLog.Change" /> because synchronization re-runs on every Steam UI generation and a
    ///         steady Verified state would otherwise be the next thing to flood the log. Written outside
    ///         the entry's lock, so a sink that reads snapshots cannot wait on it.
    ///     </para>
    /// </remarks>
    private static StateLine SetStateLocked(
        PatchEntry entry,
        SteamUiPatchState state,
        string? fingerprint,
        string? failure)
    {
        var generations = entry.TransportSnapshot?.Generations ?? default;
        entry.Snapshot = new SteamUiPatchSnapshot(
            entry.Patch.Id,
            entry.Enabled,
            state,
            fingerprint,
            generations,
            failure,
            DateTimeOffset.UtcNow);

        // The snapshot keeps the whole fingerprint and diagnostic; only the log line is bounded.
        var detail = string.IsNullOrWhiteSpace(failure)
            ? string.Empty
            : $" — {SteamUiShared.Bound(failure, SteamUiShared.MaximumDiagnosticLength)}";
        return new StateLine(
            "steam.ui.patch." + entry.Patch.Id,
            $"Steam UI patch {entry.Patch.Id}: {state}{detail}",
            // A transport the host closed on purpose is expected, not a fault worth a warning. The
            // manager always holds a subscription, so its role reads Idle only while closed.
            state is not (SteamUiPatchState.Applied or SteamUiPatchState.Verified
                    or SteamUiPatchState.Applying or SteamUiPatchState.Disabled)
                && entry.TransportSnapshot?.Health != SteamUiTransportHealth.Idle);
    }

    private static void Write(StateLine? line)
    {
        if (line is { } value)
        {
            SteamUiLog.Change(value.Key, value.Text, value.Warning);
        }
    }

    private readonly record struct StateLine(string Key, string Text, bool Warning);

    private sealed class PatchEntry(ISteamUiPatch patch, SteamUiPatchContext context)
    {
        internal object Sync { get; } = new();

        internal ISteamUiPatch Patch { get; } = patch;

        // Built once at registration: the context only pairs the transport with the patch's
        // phase timeout, and every phase of every synchronization used an identical one.
        internal SteamUiPatchContext Context { get; } = context;

        internal bool Enabled { get; set; } = true;

        // Why the module behind the patch failed. Set once; the patch stays off until unregistered.
        internal string? FaultReason { get; set; }

        // Set while the patch is being unregistered, so no pass applies it again meanwhile.
        internal bool Removing { get; set; }

        internal IAsyncDisposable? Subscription { get; set; }

        internal SteamUiTransportSnapshot? TransportSnapshot { get; set; }

        internal long GenerationEpoch { get; set; }

        // The generation the refusal retries below belong to, and how many were scheduled for it.
        internal long SettleEpoch { get; set; } = -1;

        internal int SettleAttempts { get; set; }

        internal CancellationTokenSource? ActiveOperationCancellation { get; set; }

        internal SteamUiPatchSnapshot Snapshot { get; set; } = new(
            patch.Id,
            true,
            SteamUiPatchState.Unknown,
            null,
            default,
            null,
            DateTimeOffset.UtcNow);
    }
}
