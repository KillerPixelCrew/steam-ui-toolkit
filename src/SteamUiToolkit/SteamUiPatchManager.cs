using System;
using System.Collections.Concurrent;
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

/// <summary>Hard bounds declared by one Steam UI patch.</summary>
public sealed record SteamUiPatchBounds
{
    /// <summary>Creates validated bounds for every patch phase and retained payload.</summary>
    /// <param name="OperationTimeout">Maximum duration of one phase.</param>
    /// <param name="MaximumExpressionCharacters">Maximum repository-owned expression size.</param>
    /// <param name="MaximumDiagnosticCharacters">Maximum retained diagnostic size.</param>
    /// <remarks>
    ///     Parameter casing preserves the names exposed by the original positional record constructor,
    ///     because callers may use named arguments.
    /// </remarks>
    public SteamUiPatchBounds(
        TimeSpan OperationTimeout,
        int MaximumExpressionCharacters,
        int MaximumDiagnosticCharacters)
    {
        SteamUiShared.ThrowIfInvalidTimeout(
            OperationTimeout,
            "Patch timeouts must be positive and no greater than 30 seconds.");
        if (MaximumExpressionCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumExpressionCharacters));
        }

        if (MaximumDiagnosticCharacters <= 0 || MaximumDiagnosticCharacters > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDiagnosticCharacters),
                "Diagnostic bounds must be between 1 and 65536 characters.");
        }

        this.OperationTimeout = OperationTimeout;
        this.MaximumExpressionCharacters = MaximumExpressionCharacters;
        this.MaximumDiagnosticCharacters = MaximumDiagnosticCharacters;
    }

    /// <summary>Maximum duration of one patch phase.</summary>
    public TimeSpan OperationTimeout { get; }

    /// <summary>Maximum repository-owned expression size.</summary>
    public int MaximumExpressionCharacters { get; }

    /// <summary>Maximum retained diagnostic size.</summary>
    public int MaximumDiagnosticCharacters { get; }

    /// <summary>Conservative defaults for small bootstrap and store patches.</summary>
    public static SteamUiPatchBounds Default { get; } =
        new(TimeSpan.FromSeconds(8), 96 * 1024, 2048);

    /// <summary>Deconstructs these bounds for callers using positional syntax.</summary>
    /// <param name="operationTimeout">Maximum duration of one phase.</param>
    /// <param name="maximumExpressionCharacters">Maximum expression size.</param>
    /// <param name="maximumDiagnosticCharacters">Maximum diagnostic size.</param>
    public void Deconstruct(
        out TimeSpan operationTimeout,
        out int maximumExpressionCharacters,
        out int maximumDiagnosticCharacters)
    {
        operationTimeout = OperationTimeout;
        maximumExpressionCharacters = MaximumExpressionCharacters;
        maximumDiagnosticCharacters = MaximumDiagnosticCharacters;
    }
}

/// <summary>Positive structural probe result required before patch application.</summary>
/// <param name="TargetPresent">Whether the target could be evaluated.</param>
/// <param name="Compatible">Whether the expected structure was found.</param>
/// <param name="Unique">Whether the structural match was uniquely identified.</param>
/// <param name="Fingerprint">Stable semantic fingerprint, never a module id alone.</param>
/// <param name="Diagnostic">Bounded probe details.</param>
public sealed record SteamUiPatchProbeResult(
    bool TargetPresent,
    bool Compatible,
    bool Unique,
    string? Fingerprint,
    string? Diagnostic);

/// <summary>Result of applying, verifying, or removing a Steam UI patch.</summary>
/// <param name="Succeeded">Whether the phase completed positively.</param>
/// <param name="Diagnostic">Bounded phase details.</param>
public readonly record struct SteamUiPatchOperationResult(bool Succeeded, string? Diagnostic);

/// <summary>Context that applies one patch's declared evaluation bounds.</summary>
public sealed class SteamUiPatchContext
{
    private readonly SteamUiPatchBounds _bounds;
    private readonly ISteamUiTransport _transport;

    internal SteamUiPatchContext(ISteamUiTransport transport, SteamUiPatchBounds bounds)
    {
        _transport = transport;
        _bounds = bounds;
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

        if (expression.Length > _bounds.MaximumExpressionCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expression), "Patch expression exceeded its declared bound.");
        }

        return _transport.EvaluateAsync(
            role, expression, _bounds.OperationTimeout, cancellationToken);
    }
}

/// <summary>One independently versioned, probed, verified, removable Steam UI patch.</summary>
public interface ISteamUiPatch
{
    /// <summary>Stable collision-resistant patch id.</summary>
    string Id { get; }

    /// <summary>Patch implementation version.</summary>
    int Version { get; }

    /// <summary>Allowlisted target required by this patch.</summary>
    SteamUiTargetRole TargetRole { get; }

    /// <summary>Serialization key for DOM/store resources that may conflict.</summary>
    string ResourceKey { get; }

    /// <summary>Hard phase and payload bounds.</summary>
    SteamUiPatchBounds Bounds { get; }

    /// <summary>Probes for a positive unique live fingerprint without mutation.</summary>
    Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Applies only resources owned by this patch.</summary>
    Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Functionally verifies the resulting patch state.</summary>
    Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Removes and verifies removal of only this patch's owned resources.</summary>
    Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);
}

/// <summary>Sanitized health and compatibility evidence for one registered patch.</summary>
/// <param name="Id">Stable patch id.</param>
/// <param name="Version">Patch version.</param>
/// <param name="Enabled">Whether its individual kill switch permits application.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Fingerprint">Latest positive live fingerprint.</param>
/// <param name="Generations">Generations under which health was assessed.</param>
/// <param name="LastFailure">Latest bounded failure.</param>
/// <param name="LastChangedUtc">Time of the latest state change.</param>
public sealed record SteamUiPatchSnapshot(
    string Id,
    int Version,
    bool Enabled,
    SteamUiPatchState State,
    string? Fingerprint,
    SteamUiGenerations Generations,
    string? LastFailure,
    DateTimeOffset LastChangedUtc);

/// <summary>Serializes patch work, isolates failures, and owns independent kill switches.</summary>
public sealed class SteamUiPatchManager : IAsyncDisposable
{
    // The manager subscribes to transport events in its constructor, so a generation change can be
    // enumerating patches on the pump thread while the host is still registering them. Registration
    // therefore publishes a new immutable map instead of mutating the one a reader may be walking.
    private readonly object _registrationSync = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resourceGates =
        new(StringComparer.Ordinal);

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transport.GenerationChanged -= OnGenerationChanged;
        Volatile.Write(ref _globalEnabled, false);
        CancelActivePatchOperations();
        // The pass this can wait behind is the fire-and-forget one queued by
        // StartQueuedSynchronizationIfNeeded, which runs SynchronizeAsync with
        // CancellationToken.None. CancelActivePatchOperations only cancels each entry's own
        // in-flight operation, not that outer loop's token, so an untimed wait here could block
        // teardown indefinitely behind an unresponsive steamwebhelper. Bound it to the same
        // ceiling every per-patch operation already respects; on timeout, give up on removing
        // the patches rather than race their state against whatever pass is still holding the
        // gate, and let the caller's own deadline handle the rest.
        using var gateTimeout = new CancellationTokenSource(SteamUiShared.MaximumOperationTimeout);
        try
        {
            await _schedulerGate.WaitAsync(gateTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SteamUiLog.Warn(
                "Steam UI patch manager disposal gave up waiting for an in-flight "
                + "synchronization pass; patches were not removed.");
            return;
        }

        try
        {
            foreach (var entry in _patches.Values)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(entry.Patch.Bounds.OperationTimeout);
                    await RemovePatchAsync(entry, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SetState(entry, SteamUiPatchState.RemoveFailed,
                        Snapshot(entry).Fingerprint, ex.Message);
                }
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

    /// <summary>Registers a patch before the first synchronization.</summary>
    /// <param name="patch">The independently versioned patch.</param>
    public void Register(ISteamUiPatch patch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Version <= 0
            || string.IsNullOrWhiteSpace(patch.Id)
            || string.IsNullOrWhiteSpace(patch.ResourceKey)
            || !Enum.IsDefined(patch.TargetRole)
            || patch.Bounds is null)
        {
            throw new ArgumentException(
                "Steam UI patch identity, version, target, resource, and bounds are required.");
        }

        lock (_registrationSync)
        {
            if (_patches.ContainsKey(patch.Id))
            {
                throw new InvalidOperationException(
                    $"Steam UI patch '{patch.Id}' is already registered.");
            }

            // The gate exists before the patch is published, so no reader can find the patch
            // without its gate.
            _resourceGates.TryAdd(patch.ResourceKey, new SemaphoreSlim(1, 1));
            _patches = _patches.Add(
                patch.Id,
                new PatchEntry(patch, new SteamUiPatchContext(_transport, patch.Bounds)));
        }
    }

    /// <summary>Enables or disables the global emergency kill switch.</summary>
    /// <param name="enabled">Whether any patch may remain applied.</param>
    public void SetGlobalEnabled(bool enabled)
    {
        SetGlobalSwitch(enabled);
        QueueSynchronization();
    }

    /// <summary>Changes the global kill switch and waits until every patch has reacted.</summary>
    /// <param name="enabled">Whether any patch may remain applied.</param>
    /// <param name="cancellationToken">Cancels synchronization.</param>
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

    private void SetGlobalSwitch(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _globalEnabled, enabled);
        if (!enabled)
        {
            CancelActivePatchOperations();
        }
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

    private void QueueSynchronization()
    {
        Interlocked.Exchange(ref _queuedSynchronizationPending, 1);
        StartQueuedSynchronizationIfNeeded();
    }

    private void StartQueuedSynchronizationIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _queuedSynchronizationRunning, 1, 0) == 0)
        {
            // Always leave the caller before synchronizing. A transport backed by already-complete
            // tasks can otherwise run an entire patch pass inline for every switch changed in one
            // settings update, repeatedly reapplying the same resources before the next switch is
            // even set.
            _ = Task.Run(SynchronizeAfterSwitchAsync);
        }
    }

    private async Task SynchronizeAfterSwitchAsync()
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
                    SteamUiLog.Warn($"Steam UI kill-switch synchronization failed: {ex.Message}");
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

    /// <summary>Probes, applies, verifies, or retracts every patch independently.</summary>
    /// <param name="cancellationToken">Cancels the synchronization.</param>
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _schedulerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            foreach (var entry in _patches.Values)
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

    /// <summary>Returns immutable health snapshots for diagnostics and UI.</summary>
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
        var resourceGate = _resourceGates[patch.ResourceKey];
        await resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? activeGenerationEpoch = null;
        CancellationTokenSource? activeOperation = null;
        try
        {
            bool patchEnabled;
            lock (entry.Sync)
            {
                patchEnabled = entry.Enabled;
            }

            if (!Volatile.Read(ref _globalEnabled) || !patchEnabled)
            {
                await RemovePatchAsync(entry, cancellationToken).ConfigureAwait(false);
                return;
            }

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
                        SetStateLocked(
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
                return;
            }

            if (!probe.Compatible || !probe.Unique || string.IsNullOrWhiteSpace(probe.Fingerprint))
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

            var applied = await RunPhaseAsync(
                    entry, patch.ApplyAsync, cancellationToken, operation)
                .ConfigureAwait(false);
            if (!applied.Succeeded)
            {
                TrySetStateForGeneration(
                    entry,
                    generationEpoch,
                    SteamUiPatchState.Degraded,
                    probe.Fingerprint,
                    applied.Diagnostic);
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
            var removed = await RunPhaseAsync(
                    entry, patch.RemoveAsync, cancellationToken, operation)
                .ConfigureAwait(false);
            TrySetStateForGeneration(
                entry,
                generationEpoch,
                removed.Succeeded ? SteamUiPatchState.Degraded : SteamUiPatchState.RemoveFailed,
                probe.Fingerprint,
                removed.Succeeded
                    ? verified.Diagnostic
                    : $"{verified.Diagnostic} Removal also failed: {removed.Diagnostic}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetOutcome(
                entry,
                activeGenerationEpoch,
                SteamUiPatchState.Retrying,
                "Patch operation timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetOutcome(entry, activeGenerationEpoch, SteamUiPatchState.Degraded, ex.Message);
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

            resourceGate.Release();
        }
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
        source.CancelAfter(entry.Patch.Bounds.OperationTimeout);
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
                        removed.Succeeded ? null : removed.Diagnostic);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    SetState(
                        entry,
                        SteamUiPatchState.RemoveFailed,
                        snapshot.Fingerprint,
                        "Patch removal timed out.");
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
                SetState(entry, SteamUiPatchState.Disabled, snapshot.Fingerprint, null);
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

    private async Task RetractIncompatiblePatchAsync(
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

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        foreach (var entry in _patches.Values)
        {
            if (entry.Patch.TargetRole != snapshot.Role)
            {
                continue;
            }

            CancellationTokenSource? activeOperation = null;
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
                    SetStateLocked(
                        entry,
                        SteamUiPatchState.Retrying,
                        entry.Snapshot.Fingerprint,
                        "Steam UI generation changed; reapply required.");
                }
            }

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
        lock (entry.Sync)
        {
            if (entry.GenerationEpoch != generationEpoch)
            {
                return false;
            }

            SetStateLocked(entry, state, fingerprint, failure);
            return true;
        }
    }

    private static void SetState(
        PatchEntry entry,
        SteamUiPatchState state,
        string? fingerprint,
        string? failure)
    {
        lock (entry.Sync)
        {
            SetStateLocked(entry, state, fingerprint, failure);
        }
    }

    /// <summary>Records a patch's new state and reports the transition.</summary>
    /// <remarks>
    ///     The single funnel every outcome of <see cref="SynchronizePatchAsync" /> passes through, which
    ///     is why the log line belongs here rather than at the seven call sites.
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
    ///         steady Verified state would otherwise be the next thing to flood the log.
    ///     </para>
    /// </remarks>
    private static void SetStateLocked(
        PatchEntry entry,
        SteamUiPatchState state,
        string? fingerprint,
        string? failure)
    {
        var generations = entry.TransportSnapshot?.Generations ?? default;
        entry.Snapshot = new SteamUiPatchSnapshot(
            entry.Patch.Id,
            entry.Patch.Version,
            entry.Enabled,
            state,
            SteamUiShared.Bound(fingerprint, 512),
            generations,
            SteamUiShared.Bound(failure, entry.Patch.Bounds.MaximumDiagnosticCharacters),
            DateTimeOffset.UtcNow);

        var detail = string.IsNullOrWhiteSpace(entry.Snapshot.LastFailure)
            ? string.Empty
            : $" — {entry.Snapshot.LastFailure}";
        SteamUiLog.Change(
            "steam.ui.patch." + entry.Patch.Id,
            $"Steam UI patch {entry.Patch.Id} v{entry.Patch.Version}: {state}{detail}",
            // A transport the host closed on purpose is expected, not a fault worth a warning.
            state is not (SteamUiPatchState.Applied or SteamUiPatchState.Verified
                    or SteamUiPatchState.Applying or SteamUiPatchState.Disabled)
                && !SteamUiTransportSession.IsClosedReason(entry.Snapshot.LastFailure));
    }

    private sealed class PatchEntry(ISteamUiPatch patch, SteamUiPatchContext context)
    {
        internal object Sync { get; } = new();

        internal ISteamUiPatch Patch { get; } = patch;

        // Built once at registration: the context only pairs the transport with the patch's
        // declared bounds, and every phase of every synchronization used an identical one.
        internal SteamUiPatchContext Context { get; } = context;

        internal bool Enabled { get; set; } = true;

        internal IAsyncDisposable? Subscription { get; set; }

        internal SteamUiTransportSnapshot? TransportSnapshot { get; set; }

        internal long GenerationEpoch { get; set; }

        internal CancellationTokenSource? ActiveOperationCancellation { get; set; }

        internal SteamUiPatchSnapshot Snapshot { get; set; } = new(
            patch.Id,
            patch.Version,
            true,
            SteamUiPatchState.Unknown,
            null,
            default,
            null,
            DateTimeOffset.UtcNow);
    }
}
