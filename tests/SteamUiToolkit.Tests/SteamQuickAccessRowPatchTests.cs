namespace SteamUiToolkit.Tests;

public sealed class SteamQuickAccessRowPatchTests
{
    [Fact]
    public async Task PowerLimitRowRequiresEveryUniqueStructuralMatchBeforeInstall()
    {
        RowClient client = new(2);
        await using var manager = new SteamUiPatchManager(client.Transport);
        manager.Register(SteamPowerLimitSurface.Patch);

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(0, client.InstallCount);
    }

    [Fact]
    public async Task OverlayLevelRowRequiresUniqueNativeActionModuleBeforeInstall()
    {
        RowClient client = new(2);
        await using var manager = new SteamUiPatchManager(client.Transport);
        manager.Register(SteamPerformanceSurface.OverlayLevelRow);

        await manager.SynchronizeAsync();

        var snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(0, client.InstallCount);
    }

    [Fact]
    public async Task RowsHaveIndependentVerifiedIdentities()
    {
        RowClient client = new();
        await using var manager = new SteamUiPatchManager(client.Transport);
        manager.Register(SteamPowerLimitSurface.Patch);
        manager.Register(SteamFrameLimitRow.Patch);
        manager.Register(SteamPerformanceSurface.OverlayLevelRow);
        manager.Register(SteamControllerTargetRow.Patch);
        manager.Register(SteamDeviceControlsRow.Patch);

        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["steam-ui.power-limit"].State);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["steam-ui.frame-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["steam-ui.valve-overlay-level"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["steam-ui.controller-target"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["steam-ui.device-controls"].State);
        Assert.Equal(5, client.InstallCount);
        Assert.Equal(5, snapshots.Values.Select(snapshot => snapshot.Fingerprint).Distinct().Count());
    }

    [Fact]
    public async Task DisablingTdpLeavesControllerTargetRegistered()
    {
        RowClient client = new();
        await using var manager = new SteamUiPatchManager(client.Transport);
        manager.Register(SteamPowerLimitSurface.Patch);
        manager.Register(SteamControllerTargetRow.Patch);
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("steam-ui.power-limit", false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Disabled, snapshots["steam-ui.power-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["steam-ui.controller-target"].State);
        Assert.Contains("powerLimit", client.RemovedKinds);
        Assert.DoesNotContain("controllerTarget", client.RemovedKinds);
    }

    [Fact]
    public async Task DisablingFrameLimitLeavesValveOverlayLevelRegistered()
    {
        RowClient client = new();
        await using var manager = new SteamUiPatchManager(client.Transport);
        manager.Register(SteamFrameLimitRow.Patch);
        manager.Register(SteamPerformanceSurface.OverlayLevelRow);
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("steam-ui.frame-limit", false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Disabled, snapshots["steam-ui.frame-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["steam-ui.valve-overlay-level"].State);
        Assert.Contains("frameLimit", client.RemovedKinds);
        Assert.DoesNotContain("valveOverlayLevel", client.RemovedKinds);
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
            SteamPowerLimitSurface.Patch,
            SteamPerformanceSurface.ProfileHeaderRow,
            SteamPerformanceSurface.ResetRow,
            SteamPerformanceSurface.OverlayLevelRow,
            SteamPerformanceSurface.RefreshRateRow
        ];

        // One kind per row because the injected host installs by kind, and one resource key for all
        // of them because they mount into the same wrapped panel and must serialize on it.
        Assert.Equal(rows.Length, rows.Select(row => row.ComponentKind).Distinct().Count());
        Assert.Equal(rows.Length, rows.Select(row => row.Id).Distinct().Count());
        Assert.Single(rows.Select(row => row.ResourceKey).Distinct());
    }

    /// <summary>
    ///     A client whose performance panel answers every row probe, counts installs through the
    ///     component host, and records which row kind each removal named.
    /// </summary>
    private sealed class RowClient
    {
        private readonly int _performanceActions;

        internal RowClient(int performanceActions = 1)
        {
            _performanceActions = performanceActions;
            Transport = new FakeSteamUiTransport();
            Transport.OnEvaluate = call => Task.FromResult(Transport.Reply(Answer(call.Expression)));
        }

        internal FakeSteamUiTransport Transport { get; }

        internal int InstallCount { get; private set; }

        internal List<string> RemovedKinds { get; } = [];

        private string Answer(string expression)
        {
            if (expression.Contains("steam_ui_controller_target_probe_", StringComparison.Ordinal))
            {
                return """
                       {"controllerPresentation":1,"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                       """;
            }

            if (expression.Contains("_probe_", StringComparison.Ordinal))
            {
                return $$"""
                         {"performanceActions":{{_performanceActions}},"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                         """;
            }

            if (expression.Contains("gate('nativeComponents')", StringComparison.Ordinal)
                && expression.Contains("bridge.install(", StringComparison.Ordinal))
            {
                InstallCount++;
            }
            else if (expression.Contains("gate('nativeComponents')", StringComparison.Ordinal)
                     && expression.Contains("bridge.remove(", StringComparison.Ordinal))
            {
                RemovedKinds.Add(
                    expression.Contains("controllerTarget", StringComparison.Ordinal) ? "controllerTarget"
                    : expression.Contains("deviceControls", StringComparison.Ordinal) ? "deviceControls"
                    : expression.Contains("frameLimit", StringComparison.Ordinal) ? "frameLimit"
                    : expression.Contains("valveOverlayLevel", StringComparison.Ordinal) ? "valveOverlayLevel"
                    : "powerLimit");
            }

            return "{\"ok\":true}";
        }
    }
}
