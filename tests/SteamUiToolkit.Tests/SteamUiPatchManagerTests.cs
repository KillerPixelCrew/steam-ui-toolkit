namespace SteamUiToolkit.Tests;

public sealed class SteamUiPatchManagerTests
{
    private static readonly SteamUiPatchBounds FixtureBounds = new(TimeSpan.FromSeconds(1), 1024, 1024);

    [Fact]
    public void PatchBoundsPreservePublishedNamedArguments()
    {
        SteamUiPatchBounds bounds = new(
            TimeSpan.FromSeconds(1),
            4096,
            512);

        Assert.Equal(TimeSpan.FromSeconds(1), bounds.OperationTimeout);
        Assert.Equal(4096, bounds.MaximumExpressionCharacters);
        Assert.Equal(512, bounds.MaximumDiagnosticCharacters);
    }

    [Fact]
    public void PatchBoundsRejectInvalidTimeoutsAndSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamUiPatchBounds(
            TimeSpan.Zero,
            1,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamUiPatchBounds(
            TimeSpan.FromSeconds(31),
            1,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamUiPatchBounds(
            TimeSpan.FromSeconds(1),
            0,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteamUiPatchBounds(
            TimeSpan.FromSeconds(1),
            1,
            64 * 1024 + 1));
    }

    [Theory]
    [InlineData("""{"ok":true,"rowClass":true,"logoClass":true}""", true)]
    [InlineData("""{"ok":true,"rowClass":false,"logoClass":true}""", false)]
    [InlineData("""{"ok":true,"rowClass":true,"logoClass":false}""", false)]
    [InlineData("""{"ok":true}""", false)]
    [InlineData("""{"ok":false,"rowClass":true,"logoClass":true}""", false)]
    public void ARequiredStructuralFlagIsPartOfCompatibilityRatherThanDecoration(
        string probe,
        bool expected)
    {
        // A probe that reports its own structural findings has to have them read. The glyph-style
        // probe returned whether each build-coupled selector class still exists while only "ok" —
        // which is !!document.head — decided compatibility, so a Steam build that renamed one was
        // still called compatible and the patch installed rules matching nothing.
        Assert.Equal(
            expected,
            SteamUiPatchEvaluation.IsSuccessful(probe, "rowClass", "logoClass"));
    }

    [Fact]
    public async Task SynchronousKillSwitchPromptlyRetractsAndReleasesSubscription()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds };
        manager.Register(patch);
        await manager.SynchronizeAsync();
        Assert.Equal(SteamUiPatchState.Verified, Assert.Single(manager.GetSnapshots()).State);

        manager.SetPatchEnabled(patch.Id, false);

        await patch.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await TestJson.WaitUntilAsync(() => Assert.Single(manager.GetSnapshots()).State == SteamUiPatchState.Disabled);
        Assert.Equal(1, transport.ReleasedSubscriptions);
    }

    [Fact]
    public async Task KillSwitchCancelsAnInProgressPatchPhaseBeforeRetracting()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds, BlockVerification = true };
        manager.Register(patch);
        var applying = manager.SynchronizeAsync();
        await patch.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        manager.SetPatchEnabled(patch.Id, false);

        await patch.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await applying;
        await TestJson.WaitUntilAsync(() => Assert.Single(manager.GetSnapshots()).State == SteamUiPatchState.Disabled);
    }

    [Fact]
    public async Task IncompatibleProbeRetractsPreviouslyAppliedPatch()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds };
        manager.Register(patch);
        await manager.SynchronizeAsync();
        patch.Compatible = false;

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    [Fact]
    public async Task RepeatedSynchronizationVerifiesHealthyPatchWithoutReapplyingIt()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds };
        manager.Register(patch);

        await manager.SynchronizeAsync();
        await manager.SynchronizeAsync();

        Assert.Equal(SteamUiPatchState.Verified, Assert.Single(manager.GetSnapshots()).State);
        Assert.Equal(1, patch.ApplyCalls);
        Assert.Equal(2, patch.VerifyCalls);
    }

    [Fact]
    public async Task GenerationChangeDuringVerificationCannotPublishStaleVerifiedState()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds, BlockVerification = true };
        manager.Register(patch);

        var synchronization = manager.SynchronizeAsync();
        await patch.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.AdvanceDocumentGeneration();
        patch.ReleaseVerification.TrySetResult();
        await synchronization;

        Assert.Equal(SteamUiPatchState.Retrying, Assert.Single(manager.GetSnapshots()).State);
    }

    [Fact]
    public async Task DelayedEventForTheVerifiedGenerationDoesNotInvalidateIt()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds };
        manager.Register(patch);
        await manager.SynchronizeAsync();

        transport.EmitCurrentGeneration();

        Assert.Equal(SteamUiPatchState.Verified, Assert.Single(manager.GetSnapshots()).State);
    }

    [Fact]
    public async Task SynchronizationDetectsGenerationBeforeItsDelayedEvent()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds };
        manager.Register(patch);
        await manager.SynchronizeAsync();

        transport.AdvanceGenerationWithoutEvent();
        await manager.SynchronizeAsync();
        transport.EmitCurrentGeneration();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Verified, snapshot.State);
        Assert.Equal(2, patch.ApplyCalls);
        Assert.Equal(transport.Generations, snapshot.Generations);
    }

    [Fact]
    public async Task AwaitedGlobalKillSwitchCompletesOnlyAfterRemoval()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { Bounds = FixtureBounds };
        manager.Register(patch);
        await manager.SynchronizeAsync();

        await manager.SetGlobalEnabledAsync(false);

        Assert.Equal(SteamUiPatchState.Disabled, Assert.Single(manager.GetSnapshots()).State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    [Fact]
    public async Task PatchFailureDoesNotBlockIndependentPatch()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var broken = new FakePatch("broken", "dom-a") { ThrowOnApply = true };
        var healthy = new FakePatch("healthy", "dom-b");
        manager.Register(broken);
        manager.Register(healthy);

        await manager.SynchronizeAsync();
        var snapshots = manager.GetSnapshots().ToDictionary(snapshot => snapshot.Id);

        Assert.Equal(SteamUiPatchState.Degraded, snapshots["broken"].State);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["healthy"].State);
    }

    [Fact]
    public async Task IndividualKillSwitchRemovesOnlyOwnedPatch()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var first = new FakePatch("first", "dom-a");
        var second = new FakePatch("second", "dom-b");
        manager.Register(first);
        manager.Register(second);
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("first", false);
        await manager.SynchronizeAsync();

        Assert.Equal(1, first.RemoveCalls);
        Assert.Equal(0, second.RemoveCalls);
        Assert.Equal(SteamUiPatchState.Disabled,
            manager.GetSnapshots().Single(snapshot => snapshot.Id == "first").State);
        Assert.Equal(SteamUiPatchState.Verified,
            manager.GetSnapshots().Single(snapshot => snapshot.Id == "second").State);
    }

    [Fact]
    public async Task AnAppliedPatchThatDoesNotVerifyIsRemovedRatherThanLeftInTheClient()
    {
        await using var transport = new FakeSteamUiTransport();
        await using SteamUiPatchManager manager = new(transport);
        FakePatch patch = new() { VerifySucceeds = false };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Degraded, snapshot.State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    [Fact]
    public async Task APatchWhoseRemovalAlsoFailsReportsRemoveFailed()
    {
        await using var transport = new FakeSteamUiTransport();
        await using SteamUiPatchManager manager = new(transport);
        FakePatch patch = new() { VerifySucceeds = false, RemoveSucceeds = false };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.RemoveFailed, snapshot.State);
    }

    [Fact]
    public async Task EveryPhaseGetsItsOwnDeclaredBudget()
    {
        // The bound is documented as the maximum duration of one phase. Sharing one source across
        // probe, apply and verify let a slow client spend most of it probing and have its otherwise
        // in-budget apply cancelled underneath it. Three phases of 500 ms against a 1 s bound:
        // a shared budget would need 1.5 s and fail, while each phase keeps 500 ms of slack for a
        // busy runner.
        await using var transport = new FakeSteamUiTransport();
        await using SteamUiPatchManager manager = new(transport);
        FakePatch patch = new()
        {
            Bounds = new SteamUiPatchBounds(TimeSpan.FromSeconds(1), 4096, 512),
            PhaseDelay = TimeSpan.FromMilliseconds(500)
        };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Verified, snapshot.State);
    }
}
