using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The contract a consumer relies on: every surface's declared command list is exactly the
/// vocabulary its module puts on the bridge, malformed payloads are refused with the documented
/// reason before any backend runs, and well-formed ones reach the backend as typed values.
/// </summary>
public sealed class SteamSurfaceModuleTests
{
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
            SteamNavigationPanelSurface.Module(Always, () => new(null as SteamNavigationPanelState), backend),
            SteamLibraryBadgeSurface.Module(Always, () => new(null as SteamLibraryBadgeState), backend),
            SteamHomeCarouselSurface.Module(Always, () => new(null as SteamHomeCarouselState), backend),
            SteamPowerProfileRow.Module(Always, () => new(null as SteamPowerProfileState), backend),
            SteamPowerPresetRow.Module(Always, () => new(null as SteamPowerPresetState), backend),
            SteamHybridCoreRow.Module(Always, () => new(null as SteamHybridCoreState), backend),
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
        Assert.Equal(
            SteamNavigationPanelSurface.Commands,
            set.AllowedCommands[SteamNavigationPanelSurface.PatchId]);
        Assert.Equal(
            SteamLibraryBadgeSurface.Commands,
            set.AllowedCommands[SteamLibraryBadgeSurface.PatchId]);
        Assert.Equal(
            SteamHomeCarouselSurface.Commands,
            set.AllowedCommands[SteamHomeCarouselSurface.PatchId]);
        Assert.Equal(SteamPowerProfileRow.Commands, set.AllowedCommands[SteamPowerProfileRow.PatchId]);
        Assert.Equal(SteamPowerPresetRow.Commands, set.AllowedCommands[SteamPowerPresetRow.PatchId]);
        Assert.Equal(SteamHybridCoreRow.Commands, set.AllowedCommands[SteamHybridCoreRow.PatchId]);

        // The core-preference row owns one command, and it is not the power-profile row's.
        Assert.NotEqual(SteamHybridCoreRow.PatchId, SteamPowerProfileRow.PatchId);
        Assert.False(set.TryGetCommand(SteamHybridCoreRow.PatchId, "setPowerProfile", out _));

        // The full set registers together without an identity collision, which is what a consumer
        // declaring every surface at once relies on.
        Assert.Equal(18, set.Modules.Count);
    }

    [Fact]
    public async Task AudioVolumeReachesTheBackendTypedAndAMalformedOneIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamAudioSurface.Module(Always, () => new(null as SteamAudioState), backend)]);

        SteamUiCommandResult applied = await DispatchAsync(
            set, SteamAudioSurface.PatchId, "setVolume", """{"percent":40,"input":true}""");
        SteamUiCommandResult refused = await DispatchAsync(
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

        SteamUiCommandResult absent = await DispatchAsync(set, SteamAudioSurface.PatchId, "getDevices", "null");
        current = new SteamAudioState(true, [new("spk", "Speakers", true, false)], "spk", "", 55, false, null, false, "");
        SteamUiCommandResult present = await DispatchAsync(set, SteamAudioSurface.PatchId, "getDevices", "null");

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

        SteamUiCommandResult connect = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "connect", """{"device":"aa:bb"}""");
        // The shapes Steam's panel sends, read from the client bundle: {device}, and for the two
        // BlueZ flags {device, trusted} / {device, allowed}.
        SteamUiCommandResult trusted = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "setTrusted", """{"device":"aa:bb","trusted":true}""");
        SteamUiCommandResult wake = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "setWakeAllowed", """{"device":"aa:bb","allowed":false}""");
        SteamUiCommandResult refused = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "forget", """{"id":"aa:bb"}""");
        SteamUiCommandResult flagless = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "setTrusted", """{"device":"aa:bb"}""");

        Assert.True(connect.Succeeded);
        Assert.Equal("connect aa:bb", Assert.Single(backend.Calls));
        Assert.True(trusted.Succeeded);
        Assert.True(wake.Succeeded);
        Assert.False(refused.Succeeded);
        Assert.Equal("The Bluetooth device payload is invalid.", refused.Error);
        Assert.Equal("The Bluetooth device payload is invalid.", flagless.Error);
    }

    [Fact]
    public async Task PowerLimitsRouteIndependentExplicitWrites()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamPowerLimitSurface.Module(Always, () => new(null as SteamPowerLimitState), backend)]);

        SteamUiCommandResult released = await DispatchAsync(
            set, SteamPowerLimitSurface.PatchId, "setPrimaryLimit", """{"watts":15}""");
        SteamUiCommandResult refused = await DispatchAsync(
            set, SteamPowerLimitSurface.PatchId, "setPrimaryLimit", """{"watts":"15"}""");

        Assert.True(released.Succeeded);
        Assert.Equal("limit 15", Assert.Single(backend.Calls));
        Assert.Equal("The sustained power-limit payload is invalid.", refused.Error);
        SteamUiCommandResult boosted = await DispatchAsync(
            set, SteamPowerLimitSurface.PatchId, "setBoostLimit", """{"watts":30}""");
        Assert.True(boosted.Succeeded);
        Assert.Equal(["limit 15", "boost 30"], backend.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"watts\":0}")]
    [InlineData("{\"watts\":201}")]
    [InlineData("{\"watts\":20.5}")]
    [InlineData("{\"watts\":\"20\"}")]
    [InlineData("{\"watts\":20,\"enabled\":true}")]
    [InlineData("{\"watts\":20,\"watts\":21}")]
    public async Task PowerSlidersRejectInvalidOrLegacyPayloads(string payload)
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamPowerLimitSurface.Module(Always, () => new(null as SteamPowerLimitState), backend)]);
        foreach (string command in SteamPowerLimitSurface.Commands)
        {
            Assert.False((await DispatchAsync(set, SteamPowerLimitSurface.PatchId, command, payload)).Succeeded);
        }
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void PowerSlidersSerializeSeparateHardwareReadbacks()
    {
        SteamPowerLimitRangeState sustained = new(true, 8, 37, 1, 23, "", "");
        JsonElement wire = SteamPowerLimitSurface.Serialize(new(sustained, sustained with { ObservedWatts = 37 }));
        Assert.Equal(23, wire.GetProperty("sustained").GetProperty("observedWatts").GetInt32());
        Assert.Equal(37, wire.GetProperty("boost").GetProperty("observedWatts").GetInt32());
    }

    [Fact]
    public async Task FrameLimitCarriesPersistenceAndACorrelationId()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([SteamFrameLimitRow.Module(Always, () => new(null as SteamFrameLimitState), backend)]);

        SteamUiCommandResult applied = await DispatchAsync(
            set, SteamFrameLimitRow.PatchId, "setFrameLimit", """{"value":60,"persistence":"application"}""");
        SteamUiCommandResult unknownPersistence = await DispatchAsync(
            set, SteamFrameLimitRow.PatchId, "setFrameLimit", """{"value":60,"persistence":"forever"}""");
        SteamUiCommandResult refresh = await DispatchAsync(
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

        SteamUiCommandResult applied = await DispatchAsync(
            set,
            SteamPerformanceSurface.PatchId,
            "updateSettings",
            """{"delta":{"gameid":570,"settings_delta":{"per_app":{"fps_limit":45}}}}""");
        SteamUiCommandResult undecoded = await DispatchAsync(
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

        SteamUiCommandResult applied = await DispatchAsync(
            set, SteamDeviceControlsRow.PatchId, "setLightingColor", """{"zone":"ring","color":16711680}""");
        SteamUiCommandResult extra = await DispatchAsync(
            set, SteamDeviceControlsRow.PatchId, "setLightingColor", """{"zone":"ring","color":1,"alpha":1}""");
        SteamUiCommandResult charge = await DispatchAsync(
            set, SteamDeviceControlsRow.PatchId, "setChargeLimit", """{"percent":80}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The lighting-color payload is invalid.", extra.Error);
        Assert.True(charge.Succeeded);
        Assert.Equal(["color ring FF0000", "charge 80"], backend.Calls);
    }

    [Theory]
    [InlineData("brightness")]
    [InlineData("home-carousel")]
    [InlineData("library-badge")]
    [InlineData("navigation-panel")]
    [InlineData("page")]
    [InlineData("screensaver")]
    [InlineData("storage")]
    public async Task ANullReadingPublishesNothingRatherThanAnEmptyState(string surface)
    {
        // An empty state is a real instruction: no removable drives, nothing disconnected, every
        // installed game internal, no pages, no rows, a zero brightness. Having nothing to say yet is
        // different, and not publishing is how a surface says it.
        NullReading reading = NullReadingOf(surface);
        SteamUiStatePublication publication = Assert.Single(new SteamUiModuleSet([reading.Module]).Publications);

        Assert.Null(await publication.Read());
        reading.Supply();

        reading.Check((await publication.Read())!.Value);
    }

    private sealed record NullReading(ISteamUiModule Module, Action Supply, Action<JsonElement> Check);

    private static NullReading NullReadingOf(string surface)
    {
        RecordingBackend backend = new();
        switch (surface)
        {
            case "brightness":
                {
                    SteamBrightnessState? state = null;
                    return new(
                        SteamBrightnessSurface.Module(Always, () => new(state), backend),
                        () => state = new SteamBrightnessState(42),
                        wire => Assert.Equal(42, wire.GetProperty("percent").GetInt32()));
                }
            case "home-carousel":
                {
                    SteamHomeCarouselState? state = null;
                    return new(
                        SteamHomeCarouselSurface.Module(Always, () => new(state), backend),
                        () => state = new SteamHomeCarouselState(false, []),
                        wire => Assert.Equal(0, wire.GetProperty("disconnectedAppIds").GetArrayLength()));
                }
            case "library-badge":
                {
                    SteamLibraryBadgeState? state = null;
                    return new(
                        SteamLibraryBadgeSurface.Module(Always, () => new(state), backend),
                        () => state = new SteamLibraryBadgeState([]),
                        wire => Assert.Equal(0, wire.GetProperty("libraries").GetArrayLength()));
                }
            case "navigation-panel":
                {
                    SteamNavigationPanelState? state = null;
                    return new(
                        SteamNavigationPanelSurface.Module(Always, () => new(state), backend),
                        () => state = new SteamNavigationPanelState([], ["power"]),
                        wire => Assert.Equal("power", wire.GetProperty("hidden")[0].GetString()));
                }
            case "page":
                {
                    SteamPageState? state = null;
                    return new(
                        SteamPageSurface.Module(Always, () => new(state)),
                        () => state = new SteamPageState([new SteamPage("artwork", "/wsgm/artwork", "Artwork")]),
                        wire => Assert.Equal("artwork", wire.GetProperty("pages")[0].GetProperty("id").GetString()));
                }
            case "screensaver":
                {
                    SteamScreensaverState? state = null;
                    return new(
                        SteamScreensaverSurface.Module(Always, () => new(state), backend),
                        () => state = new SteamScreensaverState([]),
                        wire => Assert.Equal(0, wire.GetProperty("rows").GetArrayLength()));
                }
            case "storage":
                {
                    SteamStorageState? state = null;
                    return new(
                        SteamStorageSurface.Module(Always, () => new(state), backend),
                        () => state = new SteamStorageState([], []),
                        wire => Assert.Equal(0, wire.GetProperty("drives").GetArrayLength()));
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }
    }
}
