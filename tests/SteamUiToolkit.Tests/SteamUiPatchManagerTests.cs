namespace SteamUiToolkit.Tests;

public sealed class SteamUiPatchManagerTests
{
    [Fact]
    public void PatchBoundsPreservePublishedNamedArguments()
    {
        SteamUiPatchBounds bounds = new(
            OperationTimeout: TimeSpan.FromSeconds(1),
            MaximumExpressionCharacters: 4096,
            MaximumDiagnosticCharacters: 512);

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

    [Fact]
    public async Task SynchronousKillSwitchPromptlyRetractsAndReleasesSubscription()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch();
        manager.Register(patch);
        await manager.SynchronizeAsync();
        Assert.Equal(SteamUiPatchState.Verified, Assert.Single(manager.GetSnapshots()).State);

        manager.SetPatchEnabled(patch.Id, false);

        await patch.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(
            () => Assert.Single(manager.GetSnapshots()).State == SteamUiPatchState.Disabled);
        Assert.Equal(1, transport.ReleasedSubscriptions);
    }

    [Fact]
    public async Task KillSwitchCancelsAnInProgressPatchPhaseBeforeRetracting()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch { BlockVerification = true };
        manager.Register(patch);
        Task applying = manager.SynchronizeAsync();
        await patch.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        manager.SetPatchEnabled(patch.Id, false);

        await patch.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await applying;
        await WaitUntilAsync(
            () => Assert.Single(manager.GetSnapshots()).State == SteamUiPatchState.Disabled);
    }

    [Fact]
    public async Task IncompatibleProbeRetractsPreviouslyAppliedPatch()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch();
        manager.Register(patch);
        await manager.SynchronizeAsync();
        patch.Compatible = false;

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    [Fact]
    public async Task RepeatedSynchronizationVerifiesHealthyPatchWithoutReapplyingIt()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch();
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
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch { BlockVerification = true };
        manager.Register(patch);

        Task synchronization = manager.SynchronizeAsync();
        await patch.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.AdvanceGeneration();
        patch.ReleaseVerification.TrySetResult();
        await synchronization;

        Assert.Equal(SteamUiPatchState.Retrying, Assert.Single(manager.GetSnapshots()).State);
    }

    [Fact]
    public async Task DelayedEventForTheVerifiedGenerationDoesNotInvalidateIt()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch();
        manager.Register(patch);
        await manager.SynchronizeAsync();

        transport.EmitCurrentGeneration();

        Assert.Equal(SteamUiPatchState.Verified, Assert.Single(manager.GetSnapshots()).State);
    }

    [Fact]
    public async Task SynchronizationDetectsGenerationBeforeItsDelayedEvent()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch();
        manager.Register(patch);
        await manager.SynchronizeAsync();

        transport.AdvanceGenerationWithoutEvent();
        await manager.SynchronizeAsync();
        transport.EmitCurrentGeneration();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Verified, snapshot.State);
        Assert.Equal(2, patch.ApplyCalls);
        Assert.Equal(transport.CurrentGenerations, snapshot.Generations);
    }

    [Fact]
    public async Task AwaitedGlobalKillSwitchCompletesOnlyAfterRemoval()
    {
        await using var transport = new PatchTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var patch = new FixturePatch();
        manager.Register(patch);
        await manager.SynchronizeAsync();

        await manager.SetGlobalEnabledAsync(false);

        Assert.Equal(SteamUiPatchState.Disabled, Assert.Single(manager.GetSnapshots()).State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FixturePatch : ISteamUiPatch
    {
        internal bool Compatible { get; set; } = true;

        internal bool BlockVerification { get; init; }

        internal int ApplyCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal TaskCompletionSource VerifyStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseVerification { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource RemoveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "fixture.patch";

        public int Version => 1;

        public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

        public string ResourceKey => "fixture.resource";

        public SteamUiPatchBounds Bounds { get; } = new(
            TimeSpan.FromSeconds(1),
            1024,
            1024);

        public Task<SteamUiPatchProbeResult> ProbeAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SteamUiPatchProbeResult(
                true,
                Compatible,
                Compatible,
                Compatible ? "fixture-fingerprint" : null,
                Compatible ? null : "fixture incompatible"));
        }

        public Task<SteamUiPatchOperationResult> ApplyAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            return Task.FromResult(new SteamUiPatchOperationResult(true, null));
        }

        public async Task<SteamUiPatchOperationResult> VerifyAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;
            VerifyStarted.TrySetResult();
            if (BlockVerification)
            {
                await ReleaseVerification.Task.WaitAsync(cancellationToken);
            }
            return new SteamUiPatchOperationResult(true, null);
        }

        public Task<SteamUiPatchOperationResult> RemoveAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCalls++;
            RemoveStarted.TrySetResult();
            return Task.FromResult(new SteamUiPatchOperationResult(true, null));
        }
    }

    private sealed class PatchTransport : ISteamUiTransport
    {
        private SteamUiGenerations _generations = new(1, 1, 1, 1, 1, 1);

        internal int ReleasedSubscriptions { get; private set; }

        internal SteamUiGenerations CurrentGenerations => _generations;

        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
            [Snapshot()];

        internal void AdvanceGeneration()
        {
            AdvanceGenerationWithoutEvent();
            GenerationChanged?.Invoke(this, Snapshot());
        }

        internal void AdvanceGenerationWithoutEvent()
        {
            _generations = _generations with
            {
                ExecutionContext = _generations.ExecutionContext + 1,
                Document = _generations.Document + 1,
            };
        }

        internal void EmitCurrentGeneration() => GenerationChanged?.Invoke(this, Snapshot());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private SteamUiTransportSnapshot Snapshot() => new(
            SteamUiTargetRole.SharedJsContext,
            SteamUiTransportHealth.Ready,
            _generations,
            "fixture",
            null,
            0,
            1);

        private sealed class Lease(PatchTransport owner) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.ReleasedSubscriptions++;
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}
