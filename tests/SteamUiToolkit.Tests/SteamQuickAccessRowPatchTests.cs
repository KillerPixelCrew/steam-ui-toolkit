namespace SteamUiToolkit.Tests;

public sealed class SteamQuickAccessRowPatchTests
{
    [Fact]
    public async Task ValveTdpRowRequiresEveryUniqueStructuralMatchBeforeInstall()
    {
        await using var transport = new RowTransport { PerformanceActionsCount = 2 };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(SteamPowerLimitSurface.ValveRows);

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(0, transport.InstallCount);
    }

    [Fact]
    public async Task OverlayLevelRowRequiresUniqueNativeActionModuleBeforeInstall()
    {
        await using var transport = new RowTransport { PerformanceActionsCount = 2 };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(SteamPerformanceSurface.OverlayLevelRow);

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(0, transport.InstallCount);
    }

    [Fact]
    public async Task RowsHaveIndependentVerifiedIdentities()
    {
        await using var transport = new RowTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(SteamPowerLimitSurface.ValveRows);
        manager.Register(SteamFrameLimitRow.Patch);
        manager.Register(SteamPerformanceSurface.OverlayLevelRow);
        manager.Register(SteamControllerTargetRow.Patch);
        manager.Register(SteamDeviceControlsRow.Patch);

        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["wsgm.native-qam.valve-tdp"].State);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["wsgm.native-qam.frame-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.valve-overlay-level"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.controller-target"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.device-controls"].State);
        Assert.Equal(5, transport.InstallCount);
        Assert.Equal(5, snapshots.Values.Select(snapshot => snapshot.Fingerprint).Distinct().Count());
    }

    [Fact]
    public async Task DisablingTdpLeavesControllerTargetRegistered()
    {
        await using var transport = new RowTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(SteamPowerLimitSurface.ValveRows);
        manager.Register(SteamControllerTargetRow.Patch);
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("wsgm.native-qam.valve-tdp", false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Disabled, snapshots["wsgm.native-qam.valve-tdp"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.controller-target"].State);
        Assert.Contains("valveTdp", transport.RemovedKinds);
        Assert.DoesNotContain("controllerTarget", transport.RemovedKinds);
    }

    [Fact]
    public async Task DisablingFrameLimitLeavesValveOverlayLevelRegistered()
    {
        await using var transport = new RowTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(SteamFrameLimitRow.Patch);
        manager.Register(SteamPerformanceSurface.OverlayLevelRow);
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("wsgm.native-qam.frame-limit", false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Disabled, snapshots["wsgm.native-qam.frame-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.valve-overlay-level"].State);
        Assert.Contains("frameLimit", transport.RemovedKinds);
        Assert.DoesNotContain("valveOverlayLevel", transport.RemovedKinds);
    }

    [Fact]
    public void EveryRowKindIsDistinctAndSharesTheMountResource()
    {
        SteamQuickAccessRowPatch[] rows =
        [
            SteamFrameLimitRow.Patch,
            SteamVariableRefreshRow.Patch,
            SteamResolutionRow.Patch,
            SteamAutoTdpRow.Patch,
            SteamControllerTargetRow.Patch,
            SteamDeviceControlsRow.Patch,
            SteamPowerLimitSurface.ValveRows,
            SteamPerformanceSurface.ProfileHeaderRow,
            SteamPerformanceSurface.ResetRow,
            SteamPerformanceSurface.OverlayLevelRow,
            SteamPerformanceSurface.RefreshRateRow,
        ];

        // One kind per row because the injected host installs by kind, and one resource key for all
        // of them because they mount into the same wrapped panel and must serialize on it.
        Assert.Equal(rows.Length, rows.Select(row => row.ComponentKind).Distinct().Count());
        Assert.Equal(rows.Length, rows.Select(row => row.Id).Distinct().Count());
        Assert.Single(rows.Select(row => row.ResourceKey).Distinct());
    }

    private sealed class RowTransport : ISteamUiTransport
    {
        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add { }
            remove { }
        }

        internal int PerformanceActionsCount { get; init; } = 1;

        internal int InstallCount { get; private set; }

        internal List<string> RemovedKinds { get; } = [];

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            string value;
            if (expression.Contains("wsgm_native_controller_target_probe_", StringComparison.Ordinal))
            {
                value = """
                    {"controllerPresentation":1,"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                    """;
            }
            else if (expression.Contains("_probe_", StringComparison.Ordinal))
            {
                value = $$"""
                    {"performanceActions":{{PerformanceActionsCount}},"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                    """;
            }
            else if (expression.Contains("gate('nativeComponents')", StringComparison.Ordinal)
                && expression.Contains("bridge.install(", StringComparison.Ordinal))
            {
                InstallCount++;
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("gate('nativeComponents')", StringComparison.Ordinal)
                && expression.Contains("bridge.remove(", StringComparison.Ordinal))
            {
                string kind = expression.Contains("controllerTarget", StringComparison.Ordinal)
                    ? "controllerTarget"
                    : expression.Contains("deviceControls", StringComparison.Ordinal)
                        ? "deviceControls"
                        : expression.Contains("frameLimit", StringComparison.Ordinal)
                            ? "frameLimit"
                            : expression.Contains("valveOverlayLevel", StringComparison.Ordinal)
                                ? "valveOverlayLevel"
                                : "valveTdp";
                RemovedKinds.Add(kind);
                value = "{\"ok\":true}";
            }
            else
            {
                value = "{\"ok\":true}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                value,
                null,
                new(1, 1, 1, 1, 1, 1)));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        [
            new(
                SteamUiTargetRole.SharedJsContext,
                SteamUiTransportHealth.Ready,
                new(1, 1, 1, 1, 1, 1),
                "fixture-target",
                null,
                0,
                1),
        ];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
