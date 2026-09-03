using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The contract a consumer relies on: every surface's declared command list is exactly the
/// vocabulary its module puts on the bridge, malformed payloads are refused with the documented
/// reason before any backend runs, and well-formed ones reach the backend as typed values.
/// </summary>
public sealed class SteamSurfaceModuleTests
{
    private static readonly Func<bool> Always = () => true;

    [Fact]
    public void EverySurfaceDeclaresExactlyTheCommandsItsModuleAnswers()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamAudioSurface.Module(Always, () => new(null as SteamAudioState), backend),
            SteamNetworkSurface.Module(Always, () => new(null as SteamNetworkState), backend),
            SteamBluetoothSurface.Module(Always, () => new(null as SteamBluetoothState), backend),
            SteamBrightnessSurface.Module(Always, () => new(null as SteamBrightnessState), backend),
            SteamPowerLimitSurface.Module(Always, () => new(null as SteamPowerLimitState), backend),
            SteamPerformanceSurface.Module(Always, () => new(null as SteamPerformanceState), backend),
            SteamFrameLimitRow.Module(Always, () => new(null as SteamFrameLimitState), backend),
            SteamVariableRefreshRow.Module(Always, () => new(null as SteamVariableRefreshState), backend),
            SteamResolutionRow.Module(Always, () => new(null as SteamResolutionState), backend),
            SteamAutoTdpRow.Module(Always, () => new(null as SteamAutoTdpState), backend),
            SteamControllerTargetRow.Module(Always, () => new(null as SteamControllerTargetState), backend),
            SteamDeviceControlsRow.Module(Always, () => new(null as SteamDeviceControlsState), backend),
        ]);

        Assert.Equal(SteamAudioSurface.Commands, set.AllowedCommands[SteamAudioSurface.PatchId]);
        Assert.Equal(SteamNetworkSurface.Commands, set.AllowedCommands[SteamNetworkSurface.PatchId]);
        Assert.Equal(SteamBluetoothSurface.Commands, set.AllowedCommands[SteamBluetoothSurface.PatchId]);
        Assert.Equal(SteamBrightnessSurface.Commands, set.AllowedCommands[SteamBrightnessSurface.PatchId]);
        Assert.Equal(SteamPowerLimitSurface.Commands, set.AllowedCommands[SteamPowerLimitSurface.PatchId]);
        Assert.Equal(SteamPerformanceSurface.Commands, set.AllowedCommands[SteamPerformanceSurface.PatchId]);
        Assert.Equal(SteamFrameLimitRow.Commands, set.AllowedCommands[SteamFrameLimitRow.PatchId]);
        Assert.Equal(SteamVariableRefreshRow.Commands, set.AllowedCommands[SteamVariableRefreshRow.PatchId]);
        Assert.Equal(SteamResolutionRow.Commands, set.AllowedCommands[SteamResolutionRow.PatchId]);
        Assert.Equal(SteamAutoTdpRow.Commands, set.AllowedCommands[SteamAutoTdpRow.PatchId]);
        Assert.Equal(SteamControllerTargetRow.Commands, set.AllowedCommands[SteamControllerTargetRow.PatchId]);
        Assert.Equal(SteamDeviceControlsRow.Commands, set.AllowedCommands[SteamDeviceControlsRow.PatchId]);

        // The full set registers together without an identity collision, which is what a consumer
        // declaring every surface at once relies on.
        Assert.Equal(12, set.Modules.Count);
    }

    [Fact]
    public async Task AudioVolumeReachesTheBackendTypedAndAMalformedOneIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamAudioSurface.Module(Always, () => new(null as SteamAudioState), backend)]);

        SteamUiCommandResult applied = await Dispatch(
            set, SteamAudioSurface.PatchId, "setVolume", """{"percent":40,"input":true}""");
        SteamUiCommandResult refused = await Dispatch(
            set, SteamAudioSurface.PatchId, "setVolume", """{"percent":140}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("volume 40 input", Assert.Single(backend.Calls));
        Assert.False(refused.Succeeded);
        Assert.Equal("The audio volume payload is invalid.", refused.Error);
    }

    [Fact]
    public async Task GetDevicesAnswersWithTheCurrentStateOrRefusesWhenThereIsNone()
    {
        RecordingBackend backend = new();
        SteamAudioState? current = null;
        SteamUiModuleSet set = new([SteamAudioSurface.Module(Always, () => new(current), backend)]);

        SteamUiCommandResult absent = await Dispatch(set, SteamAudioSurface.PatchId, "getDevices", "null");
        current = new SteamAudioState(true, [new("spk", "Speakers", true, false)], "spk", "", 55, false, null, false, "");
        SteamUiCommandResult present = await Dispatch(set, SteamAudioSurface.PatchId, "getDevices", "null");

        Assert.False(absent.Succeeded);
        Assert.True(present.Succeeded);
        Assert.Equal(55, present.Payload!.Value.GetProperty("volumePercent").GetInt32());
        Assert.Equal("spk", present.Payload.Value.GetProperty("devices")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task BluetoothDeviceOperationsCarryTheDeviceIdAndTheBlueZFlagsAreAcceptedByDefault()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamBluetoothSurface.Module(Always, () => new(null as SteamBluetoothState), backend)]);

        SteamUiCommandResult connect = await Dispatch(
            set, SteamBluetoothSurface.PatchId, "connect", """{"device":"aa:bb"}""");
        SteamUiCommandResult trusted = await Dispatch(
            set, SteamBluetoothSurface.PatchId, "setTrusted", """{"anything":1}""");
        SteamUiCommandResult refused = await Dispatch(
            set, SteamBluetoothSurface.PatchId, "forget", """{"id":"aa:bb"}""");

        Assert.True(connect.Succeeded);
        Assert.Equal("connect aa:bb", Assert.Single(backend.Calls));
        Assert.True(trusted.Succeeded);
        Assert.False(refused.Succeeded);
        Assert.Equal("The Bluetooth device payload is invalid.", refused.Error);
    }

    [Fact]
    public async Task PowerLimitCarriesTheSwitchBesideTheWatts()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamPowerLimitSurface.Module(Always, () => new(null as SteamPowerLimitState), backend)]);

        SteamUiCommandResult released = await Dispatch(
            set, SteamPowerLimitSurface.PatchId, "setPrimaryLimit", """{"watts":15,"enabled":false}""");
        SteamUiCommandResult refused = await Dispatch(
            set, SteamPowerLimitSurface.PatchId, "setPrimaryLimit", """{"watts":"15","enabled":true}""");

        Assert.True(released.Succeeded);
        Assert.Equal("limit 15 off", Assert.Single(backend.Calls));
        Assert.Equal("The primary power-limit payload is invalid.", refused.Error);
    }

    [Fact]
    public async Task FrameLimitCarriesPersistenceAndACorrelationId()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamFrameLimitRow.Module(Always, () => new(null as SteamFrameLimitState), backend)]);

        SteamUiCommandResult applied = await Dispatch(
            set, SteamFrameLimitRow.PatchId, "setFrameLimit", """{"value":60,"persistence":"application"}""");
        SteamUiCommandResult unknownPersistence = await Dispatch(
            set, SteamFrameLimitRow.PatchId, "setFrameLimit", """{"value":60,"persistence":"forever"}""");
        SteamUiCommandResult refresh = await Dispatch(
            set, SteamFrameLimitRow.PatchId, "setRefreshRate", """{"value":75,"persistence":"automatic"}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The frame-limit payload is invalid.", unknownPersistence.Error);
        Assert.True(refresh.Succeeded);
        Assert.Equal(
            ["frame 60 Application native-qam:3:4:1:2", "refresh 75"],
            backend.Calls);
    }

    [Fact]
    public async Task PerformanceDeltasAreDecodedBeforeTheBackendSeesThem()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamPerformanceSurface.Module(Always, () => new(null as SteamPerformanceState), backend)]);

        SteamUiCommandResult applied = await Dispatch(
            set,
            SteamPerformanceSurface.PatchId,
            "updateSettings",
            """{"delta":{"gameid":570,"settings_delta":{"per_app":{"fps_limit":45}}}}""");
        SteamUiCommandResult undecoded = await Dispatch(
            set, SteamPerformanceSurface.PatchId, "updateSettings", """{"delta":"CgQI"}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("perf 570 FrameLimit=45", Assert.Single(backend.Calls));
        Assert.False(undecoded.Succeeded);
        Assert.Contains("undecoded", undecoded.Error);
    }

    [Fact]
    public async Task DeviceColourRequiresExactlyZoneAndColour()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamDeviceControlsRow.Module(Always, () => new(null as SteamDeviceControlsState), backend)]);

        SteamUiCommandResult applied = await Dispatch(
            set, SteamDeviceControlsRow.PatchId, "setLightingColor", """{"zone":"ring","color":16711680}""");
        SteamUiCommandResult extra = await Dispatch(
            set, SteamDeviceControlsRow.PatchId, "setLightingColor", """{"zone":"ring","color":1,"alpha":1}""");
        SteamUiCommandResult charge = await Dispatch(
            set, SteamDeviceControlsRow.PatchId, "setChargeLimit", """{"percent":80}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The lighting-color payload is invalid.", extra.Error);
        Assert.True(charge.Succeeded);
        Assert.Equal(["color ring FF0000", "charge 80"], backend.Calls);
    }

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanAZero()
    {
        RecordingBackend backend = new();
        SteamBrightnessState? level = null;
        SteamUiModuleSet set = new([SteamBrightnessSurface.Module(Always, () => new(level), backend)]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        level = new SteamBrightnessState(42);
        JsonElement? published = await publication.Read();

        Assert.Equal(42, published!.Value.GetProperty("percent").GetInt32());
    }

    private static async Task<SteamUiCommandResult> Dispatch(
        SteamUiModuleSet set,
        string patchId,
        string command,
        string payloadJson)
    {
        Assert.True(set.TryGetCommand(patchId, command, out SteamUiCommandDelegate? handler));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            patchId,
            command,
            1,
            2,
            3,
            4,
            payload.RootElement.Clone());
        return await handler!(request, CancellationToken.None);
    }

    /// <summary>One backend for every surface, recording what reached it in a readable form.</summary>
    private sealed class RecordingBackend :
        ISteamAudioBackend,
        ISteamNetworkBackend,
        ISteamBluetoothBackend,
        ISteamBrightnessBackend,
        ISteamPowerLimitBackend,
        ISteamPerformanceBackend,
        ISteamFrameLimitBackend,
        ISteamVariableRefreshBackend,
        ISteamResolutionBackend,
        ISteamAutoTdpBackend,
        ISteamControllerTargetBackend,
        ISteamDeviceControlsBackend
    {
        internal List<string> Calls { get; } = [];

        private Task<SteamUiCommandResult> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(SteamUiCommandResult.Applied);
        }

        public Task<SteamUiCommandResult> SetDefaultDeviceAsync(string deviceId, bool input, CancellationToken cancellationToken) =>
            Record($"default {deviceId} {(input ? "input" : "output")}");

        public Task<SteamUiCommandResult> SetVolumeAsync(int percent, bool input, CancellationToken cancellationToken) =>
            Record($"volume {percent} {(input ? "input" : "output")}");

        public Task<SteamUiCommandResult> StartScanAsync(CancellationToken cancellationToken) => Record("scan on");

        public Task<SteamUiCommandResult> StopScanAsync(CancellationToken cancellationToken) => Record("scan off");

        public Task<SteamUiCommandResult> SetDiscoveringAsync(bool discovering, CancellationToken cancellationToken) =>
            Record($"discover {discovering}");

        public Task<SteamUiCommandResult> PairAsync(string deviceId, CancellationToken cancellationToken) => Record($"pair {deviceId}");

        public Task<SteamUiCommandResult> CancelPairAsync(string deviceId, CancellationToken cancellationToken) => Record($"cancel {deviceId}");

        public Task<SteamUiCommandResult> ConnectAsync(string deviceId, CancellationToken cancellationToken) => Record($"connect {deviceId}");

        public Task<SteamUiCommandResult> DisconnectAsync(string deviceId, CancellationToken cancellationToken) => Record($"disconnect {deviceId}");

        public Task<SteamUiCommandResult> ForgetAsync(string deviceId, CancellationToken cancellationToken) => Record($"forget {deviceId}");

        public Task<SteamUiCommandResult> SetBrightnessAsync(int percent, CancellationToken cancellationToken) => Record($"brightness {percent}");

        public Task<SteamUiCommandResult> SetPrimaryLimitAsync(int watts, bool enabled, CancellationToken cancellationToken) =>
            Record($"limit {watts} {(enabled ? "on" : "off")}");

        public Task<SteamUiCommandResult> ApplyAsync(SteamPerformanceDelta delta, string correlationId, CancellationToken cancellationToken) =>
            Record($"perf {delta.SteamAppId} " + string.Join(",", delta.Recognized.Select(c => $"{c.Kind}={c.Value}")));

        public Task<SteamUiCommandResult> SetFrameLimitAsync(int fps, SteamSettingPersistence persistence, string correlationId, CancellationToken cancellationToken) =>
            Record($"frame {fps} {persistence} {correlationId}");

        public Task<SteamUiCommandResult> SetRefreshRateAsync(int hz, CancellationToken cancellationToken) => Record($"refresh {hz}");

        public Task<SteamUiCommandResult> SetVariableRefreshRateAsync(bool enabled, CancellationToken cancellationToken) => Record($"vrr {enabled}");

        public Task<SteamUiCommandResult> SetResolutionAsync(string option, CancellationToken cancellationToken) => Record($"resolution {option}");

        public Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken) => Record($"auto {enabled}");

        public Task<SteamUiCommandResult> SetControllerTargetAsync(string target, CancellationToken cancellationToken) => Record($"target {target}");

        public Task<SteamUiCommandResult> SetChargeLimitAsync(int percent, CancellationToken cancellationToken) => Record($"charge {percent}");

        public Task<SteamUiCommandResult> SetLightingBrightnessAsync(int percent, CancellationToken cancellationToken) => Record($"lighting {percent}");

        public Task<SteamUiCommandResult> SetLightingColorAsync(string zone, int color, CancellationToken cancellationToken) =>
            Record($"color {zone} {color:X6}");
    }
}
