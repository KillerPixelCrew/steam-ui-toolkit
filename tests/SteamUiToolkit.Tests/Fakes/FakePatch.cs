namespace SteamUiToolkit.Tests.Fakes;

/// <summary>A patch whose phases are scripted, with no Steam and no evaluation.</summary>
internal sealed class FakePatch(string id = "fixture.patch") : ISteamUiPatch
{
    private int _applyCalls;
    private int _removeCalls;
    private int _verifyCalls;

    internal bool Compatible { get; set; } = true;

    internal bool TargetPresent { get; set; } = true;

    internal bool ThrowOnApply { get; init; }

    internal bool Mutated { get; private set; }

    internal bool BlockVerification { get; init; }

    internal bool VerifySucceeds { get; init; } = true;

    internal bool RemoveSucceeds { get; init; } = true;

    internal TimeSpan PhaseDelay { get; init; }

    /// <summary>Told the patch id whenever this patch is applied, so a test can see the order.</summary>
    internal Action<string>? Applied { get; init; }

    internal int ApplyCalls => Volatile.Read(ref _applyCalls);

    internal int VerifyCalls => Volatile.Read(ref _verifyCalls);

    internal int RemoveCalls => Volatile.Read(ref _removeCalls);

    internal TaskCompletionSource VerifyStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseVerification { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource RemoveStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id => id;

    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    public TimeSpan OperationTimeout { get; init; } = SteamUiPatchManager.DefaultOperationTimeout;

    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayAsync(cancellationToken);
        return new SteamUiPatchProbeResult(
            TargetPresent,
            Compatible,
            Compatible ? "fixture-fingerprint" : null,
            Compatible ? null : "fixture incompatible");
    }

    public async Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutated = true;
        if (ThrowOnApply)
        {
            throw new InvalidOperationException("fixture apply failure");
        }

        Interlocked.Increment(ref _applyCalls);
        Applied?.Invoke(id);
        await DelayAsync(cancellationToken);
        return new SteamUiPatchOperationResult(true, null);
    }

    public async Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _verifyCalls);
        VerifyStarted.TrySetResult();
        await DelayAsync(cancellationToken);
        if (BlockVerification)
        {
            await ReleaseVerification.Task.WaitAsync(cancellationToken);
        }

        return new SteamUiPatchOperationResult(VerifySucceeds, VerifySucceeds ? null : "no proof");
    }

    public async Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _removeCalls);
        RemoveStarted.TrySetResult();
        await DelayAsync(cancellationToken);
        if (RemoveSucceeds)
        {
            Mutated = false;
        }

        return new SteamUiPatchOperationResult(
            RemoveSucceeds,
            RemoveSucceeds ? null : "removal unverified");
    }

    private Task DelayAsync(CancellationToken cancellationToken)
    {
        return PhaseDelay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(PhaseDelay, cancellationToken);
    }
}
