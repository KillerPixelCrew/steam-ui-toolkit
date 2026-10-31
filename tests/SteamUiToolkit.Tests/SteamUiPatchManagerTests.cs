namespace SteamUiToolkit.Tests;

// Two tests read the shared SteamUiLog sink, so the class runs apart from the others that do.
[Collection("Steam UI diagnostics")]
public sealed class SteamUiPatchManagerTests
{
    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task RegistrationRejectsAnInvalidPhaseTimeout()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            manager.Register(new FakePatch("zero") { OperationTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            manager.Register(new FakePatch("long") { OperationTimeout = TimeSpan.FromSeconds(31) }));
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
    public async Task TheBridgeIsAppliedBeforeEveryGateThatLivesInIt()
    {
        // In id order steam-ui.animations and steam-ui.artwork-browser came before steam-ui.bridge:
        // after a Big Picture restart they installed into the old bridge, the new one replaced them
        // with gates nobody installed, and both pages stayed on "Loading".
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        List<string> order = [];
        foreach (var id in (string[])["steam-ui.animations", "steam-ui.bridge", "steam-ui.themes"])
        {
            manager.Register(new FakePatch(id) { OperationTimeout = FixtureTimeout, Applied = order.Add });
        }

        await manager.SynchronizeAsync();

        Assert.Equal(["steam-ui.bridge", "steam-ui.animations", "steam-ui.themes"], order);
    }

    [Fact]
    public async Task SynchronousKillSwitchPromptlyRetractsAndReleasesSubscription()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { OperationTimeout = FixtureTimeout };
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
        var patch = new FakePatch { OperationTimeout = FixtureTimeout, BlockVerification = true };
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
        var patch = new FakePatch { OperationTimeout = FixtureTimeout };
        manager.Register(patch);
        await manager.SynchronizeAsync();
        patch.Compatible = false;

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    [Fact]
    public async Task ATargetNotReadyWhileSteamLoadsIsProbedAgainWithoutAnotherReload()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(10));
        var patch = new FakePatch { OperationTimeout = FixtureTimeout, TargetPresent = false };
        manager.Register(patch);

        await manager.SynchronizeAsync();
        Assert.Equal(SteamUiPatchState.AbsentTarget, Assert.Single(manager.GetSnapshots()).State);
        patch.TargetPresent = true;

        await TestJson.WaitUntilAsync(() => Assert.Single(manager.GetSnapshots()).State == SteamUiPatchState.Verified);

        Assert.Equal(1, patch.ApplyCalls);
    }

    [Fact]
    public async Task ADegradedPatchIsAppliedAgainWithoutAnotherReload()
    {
        // A failed apply asked nothing again until the next reload, so a Quick Access panel whose last
        // pass failed stayed stock for the rest of the session (issue #226).
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(10));
        var patch = new FakePatch { OperationTimeout = FixtureTimeout, ThrowOnApply = true };
        manager.Register(patch);

        await manager.SynchronizeAsync();
        Assert.Equal(SteamUiPatchState.Degraded, Assert.Single(manager.GetSnapshots()).State);
        patch.ThrowOnApply = false;

        await TestJson.WaitUntilAsync(() => Assert.Single(manager.GetSnapshots()).State == SteamUiPatchState.Verified);
    }

    [Fact]
    public async Task GatesWaitForTheBridgeInsteadOfFailingAgainstIt()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(10));
        var bridge = new FakePatch(SteamUiBridgePatch.PatchId) { OperationTimeout = FixtureTimeout, ThrowOnApply = true };
        var gate = new FakePatch("steam-ui.gate") { OperationTimeout = FixtureTimeout };
        manager.Register(bridge);
        manager.Register(gate);

        await manager.SynchronizeAsync();
        var waiting = manager.GetSnapshots().Single(snapshot => snapshot.Id == gate.Id);

        Assert.Equal(SteamUiPatchState.Retrying, waiting.State);
        Assert.Equal("Waiting for the Steam UI bridge.", waiting.LastFailure);
        Assert.Equal(0, gate.ApplyCalls);
        bridge.ThrowOnApply = false;

        await TestJson.WaitUntilAsync(() =>
            manager.GetSnapshots().All(snapshot => snapshot.State == SteamUiPatchState.Verified));
        Assert.Equal(1, gate.ApplyCalls);
    }

    [Fact]
    public async Task AHealthCheckReappliesALostBridgeAndEveryGateInIt()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(10));
        var bridge = new FakePatch(SteamUiBridgePatch.PatchId) { OperationTimeout = FixtureTimeout };
        var gate = new FakePatch("steam-ui.gate") { OperationTimeout = FixtureTimeout };
        manager.Register(bridge);
        manager.Register(gate);
        await manager.SynchronizeAsync();
        bridge.VerifySucceeds = false;

        manager.QueueHealthCheck();
        await TestJson.WaitUntilAsync(() => bridge.ApplyCalls >= 2);
        Assert.Equal(1, gate.ApplyCalls);
        bridge.VerifySucceeds = true;

        await TestJson.WaitUntilAsync(() =>
            gate.ApplyCalls == 2
            && manager.GetSnapshots().All(snapshot => snapshot.State == SteamUiPatchState.Verified));
    }

    [Fact]
    public async Task AHealthCheckOfAHoldingBridgeTouchesNothingElse()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(10));
        var bridge = new FakePatch(SteamUiBridgePatch.PatchId) { OperationTimeout = FixtureTimeout };
        var gate = new FakePatch("steam-ui.gate") { OperationTimeout = FixtureTimeout };
        manager.Register(bridge);
        manager.Register(gate);
        await manager.SynchronizeAsync();
        var gateVerifications = gate.VerifyCalls;
        var bridgeVerifications = bridge.VerifyCalls;

        manager.QueueHealthCheck();
        await TestJson.WaitUntilAsync(() => bridge.VerifyCalls == bridgeVerifications + 1);

        Assert.Equal(gateVerifications, gate.VerifyCalls);
        Assert.Equal(1, bridge.ApplyCalls);
        Assert.Equal(1, gate.ApplyCalls);
    }

    [Fact]
    public async Task RepeatedSynchronizationVerifiesHealthyPatchWithoutReapplyingIt()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FakePatch { OperationTimeout = FixtureTimeout };
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
        var patch = new FakePatch { OperationTimeout = FixtureTimeout, BlockVerification = true };
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
        var patch = new FakePatch { OperationTimeout = FixtureTimeout };
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
        var patch = new FakePatch { OperationTimeout = FixtureTimeout };
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
        var patch = new FakePatch { OperationTimeout = FixtureTimeout };
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
        var broken = new FakePatch("broken") { ThrowOnApply = true };
        var healthy = new FakePatch("healthy");
        manager.Register(broken);
        manager.Register(healthy);

        await manager.SynchronizeAsync();
        var snapshots = manager.GetSnapshots().ToDictionary(snapshot => snapshot.Id);

        Assert.Equal(SteamUiPatchState.Degraded, snapshots["broken"].State);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["healthy"].State);
        Assert.Equal(1, broken.RemoveCalls);
        Assert.False(broken.Mutated);
    }

    [Fact]
    public async Task IndividualKillSwitchRemovesOnlyOwnedPatch()
    {
        await using var transport = new FakeSteamUiTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var first = new FakePatch("first");
        var second = new FakePatch("second");
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
            OperationTimeout = TimeSpan.FromSeconds(1),
            PhaseDelay = TimeSpan.FromMilliseconds(500)
        };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Verified, snapshot.State);
    }

    [Fact]
    public async Task IncompatibleStandsForItsGenerationUntilTheGenerationOrASwitchChanges()
    {
        // A build that lacks what a patch needs does not grow it in the next minute. Retrying it like a
        // transient failure spent the retry sequence on it and logged the same verdict all session.
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            await using var transport = new FakeSteamUiTransport();
            await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(1));
            var patch = new FakePatch("fixture.incompatible") { OperationTimeout = FixtureTimeout, Compatible = false };
            manager.Register(patch);

            await manager.SynchronizeAsync();
            Assert.Equal(SteamUiPatchState.Incompatible, Assert.Single(manager.GetSnapshots()).State);
            await Task.Delay(100);
            await manager.SynchronizeAsync();

            Assert.Equal(1, patch.ProbeCalls);
            Assert.Single(log.Changes("steam.ui.patch.fixture.incompatible", "Incompatible"));

            transport.AdvanceDocumentGeneration();
            await manager.SynchronizeAsync();
            Assert.Equal(2, patch.ProbeCalls);

            await manager.SetPatchEnabledAsync(patch.Id, true);
            Assert.Equal(3, patch.ProbeCalls);

            patch.Compatible = true;
            await manager.SetGlobalEnabledAsync(false);
            await manager.SetGlobalEnabledAsync(true);
            Assert.Equal(SteamUiPatchState.Verified, Assert.Single(manager.GetSnapshots()).State);
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    [Fact]
    public async Task AnAbsentGateIsLeftToTheHealthCheckWhichProbesEveryAbsentGateInOneEvaluation()
    {
        // Big Picture can stay closed for hours. The retry sequence ended long before it opened, and
        // nothing asked an absent gate again, so Home kept Steam's own carousel until the next reload.
        await using var transport = new FakeSteamUiTransport();
        var ready = false;
        var probes = 0;
        var batches = 0;
        transport.OnEvaluate = call =>
        {
            var answer = ready
                ? """{"ok":true,"outcome":"ready","facts":{}}"""
                : """{"ok":false,"outcome":"notReady","reason":"the router is not mounted"}""";
            string value;
            if (call.Expression.Contains(".map(n=>", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref batches);
                value = "[" + answer + "]";
            }
            else if (call.Expression.Contains("bridge.probe()", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref probes);
                value = answer;
            }
            else
            {
                value = """{"ok":true}""";
            }

            return Task.FromResult(transport.Reply(value));
        };
        await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(1));
        var bridge = new FakePatch(SteamUiBridgePatch.PatchId) { OperationTimeout = FixtureTimeout };
        var gate = new SteamGatePatch("steam-ui.example", "exampleGate", "example-v1", "Example gate");
        manager.Register(bridge);
        manager.Register(gate);

        await manager.SynchronizeAsync();
        Assert.Equal(SteamUiPatchState.AbsentTarget, State(manager, gate.Id));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref probes));

        manager.QueueHealthCheck();
        await TestJson.WaitUntilAsync(() => Volatile.Read(ref batches) == 1);
        Assert.Equal(SteamUiPatchState.AbsentTarget, State(manager, gate.Id));
        Assert.Equal(1, Volatile.Read(ref probes));

        ready = true;
        manager.QueueHealthCheck();
        await TestJson.WaitUntilAsync(() => State(manager, gate.Id) == SteamUiPatchState.Verified);
        Assert.Equal(2, Volatile.Read(ref batches));
        Assert.Equal(2, Volatile.Read(ref probes));
    }

    [Fact]
    public async Task EveryPatchCountsItsOwnRetriesAndTheWarningNamesTheOnesThatRanOut()
    {
        // One shared sequence let a patch that never recovered spend every other patch's retries: a
        // patch that started failing late got whatever was left, then nothing until the next reload.
        var log = new RecordingLog();
        SteamUiLog.Use(log);
        try
        {
            await using var transport = new FakeSteamUiTransport();
            await using var manager = new SteamUiPatchManager(transport, TimeSpan.FromMilliseconds(1));
            var always = new FakePatch("fixture.always") { OperationTimeout = FixtureTimeout, ApplyFailures = -1 };
            var late = new FakePatch("fixture.late") { OperationTimeout = FixtureTimeout };
            manager.Register(always);
            manager.Register(late);

            await manager.SynchronizeAsync();
            Assert.Equal(SteamUiPatchState.Verified, State(manager, late.Id));
            await TestJson.WaitUntilAsync(() => always.ProbeCalls >= 6, TimeSpan.FromSeconds(5));
            // The late patch loses its generation and needs more passes than the other has left.
            late.ApplyFailures = 6;
            transport.AdvanceDocumentGeneration();

            await TestJson.WaitUntilAsync(() => State(manager, late.Id) == SteamUiPatchState.Verified,
                TimeSpan.FromSeconds(10));
            await TestJson.WaitUntilAsync(() => log.Warnings.Count == 1, TimeSpan.FromSeconds(5));
            await Task.Delay(300);

            Assert.Equal(9, always.ProbeCalls);
            var warning = Assert.Single(log.Warnings);
            Assert.Contains("fixture.always (Degraded: fixture apply failure)", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture.late", warning, StringComparison.Ordinal);
        }
        finally
        {
            SteamUiLog.Use(null);
        }
    }

    private static SteamUiPatchState State(SteamUiPatchManager manager, string id)
    {
        return manager.GetSnapshots().Single(snapshot => snapshot.Id == id).State;
    }

    /// <summary>Records the manager's lines, so a test can count what was written.</summary>
    private sealed class RecordingLog : ISteamUiLog
    {
        private readonly List<(string Key, string Message)> _changes = [];
        private readonly List<string> _warnings = [];

        internal IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return _warnings.Where(line => line.Contains("fixture.", StringComparison.Ordinal)).ToArray();
                }
            }
        }

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }

        public void Change(string key, string message, bool warning = false)
        {
            lock (_changes)
            {
                _changes.Add((key, message));
            }
        }

        internal IReadOnlyList<string> Changes(string key, string text)
        {
            lock (_changes)
            {
                return _changes.Where(change => change.Key == key && change.Message.Contains(text, StringComparison.Ordinal))
                    .Select(change => change.Message).ToArray();
            }
        }
    }
}
