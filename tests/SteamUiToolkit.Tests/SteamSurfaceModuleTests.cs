using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The contract a consumer relies on: every surface's declared command list is exactly the
///     vocabulary its module puts on the bridge, malformed payloads are refused with the documented
///     reason before any backend runs, and well-formed ones reach the backend as typed values.
/// </summary>
public sealed class SteamSurfaceModuleTests
{
    [Fact]
    public void EverySurfaceDeclaresExactlyTheCommandsItsModuleAnswers()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamAudioSurface.Module(Always, () => new ValueTask<SteamAudioState?>(null as SteamAudioState), backend),
            SteamAudioFormatRow.Module(Always,
                () => new ValueTask<SteamAudioFormatState?>(null as SteamAudioFormatState), backend),
            SteamNetworkSurface.Module(Always, () => new ValueTask<SteamNetworkState?>(null as SteamNetworkState),
                backend),
            SteamBluetoothSurface.Module(Always, () => new ValueTask<SteamBluetoothState?>(null as SteamBluetoothState),
                backend),
            SteamBrightnessSurface.Module(Always,
                () => new ValueTask<SteamBrightnessState?>(null as SteamBrightnessState), backend),
            SteamPowerLimitSurface.Module(Always,
                () => new ValueTask<SteamPowerLimitState?>(null as SteamPowerLimitState), backend),
            SteamPerformanceSurface.Module(Always,
                () => new ValueTask<SteamPerformanceState?>(null as SteamPerformanceState), backend),
            SteamFrameLimitRow.Module(Always, () => new ValueTask<SteamFrameLimitState?>(null as SteamFrameLimitState),
                backend),
            SteamVariableRefreshRow.Module(Always,
                () => new ValueTask<SteamVariableRefreshState?>(null as SteamVariableRefreshState), backend),
            SteamResolutionRow.Module(Always, () => new ValueTask<SteamResolutionState?>(null as SteamResolutionState),
                backend),
            SteamAutoTdpRow.Module(Always, () => new ValueTask<SteamAutoTdpState?>(null as SteamAutoTdpState), backend),
            SteamControllerTargetRow.Module(Always,
                () => new ValueTask<SteamControllerTargetState?>(null as SteamControllerTargetState), backend),
            SteamDeviceControlsRow.Module(Always,
                () => new ValueTask<SteamDeviceControlsState?>(null as SteamDeviceControlsState), backend),
            SteamNavigationPanelSurface.Module(Always,
                () => new ValueTask<SteamNavigationPanelState?>(null as SteamNavigationPanelState), backend),
            SteamLibraryBadgeSurface.Module(Always,
                () => new ValueTask<SteamLibraryBadgeState?>(null as SteamLibraryBadgeState), backend),
            SteamHomeCarouselSurface.Module(Always,
                () => new ValueTask<SteamHomeCarouselState?>(null as SteamHomeCarouselState), backend),
            SteamPowerProfileRow.Module(Always,
                () => new ValueTask<SteamPowerProfileState?>(null as SteamPowerProfileState), backend),
            SteamPowerPresetRow.Module(Always,
                () => new ValueTask<SteamPowerPresetState?>(null as SteamPowerPresetState), backend),
            SteamHybridCoreRow.Module(Always, () => new ValueTask<SteamHybridCoreState?>(null as SteamHybridCoreState),
                backend),
            SteamCpuBoostRow.Module(Always, () => new ValueTask<SteamCpuBoostState?>(null as SteamCpuBoostState),
                backend)
        ]);

        Assert.Equal(SteamAudioSurface.Commands, set.AllowedCommands[SteamAudioSurface.PatchId]);
        Assert.Equal(SteamAudioFormatRow.Commands, set.AllowedCommands[SteamAudioFormatRow.PatchId]);
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
        Assert.Equal(SteamCpuBoostRow.Commands, set.AllowedCommands[SteamCpuBoostRow.PatchId]);

        // The core-preference row owns one command, and it is not the power-profile row's.
        Assert.NotEqual(SteamHybridCoreRow.PatchId, SteamPowerProfileRow.PatchId);
        Assert.False(set.TryGetCommand(SteamHybridCoreRow.PatchId, "setPowerProfile", out _));

        // The full set registers together without an identity collision, which is what a consumer
        // declaring every surface at once relies on.
        Assert.Equal(20, set.Modules.Count);
    }

    [Fact]
    public async Task AudioVolumeReachesTheBackendTypedAndAMalformedOneIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([
            SteamAudioSurface.Module(Always, () => new ValueTask<SteamAudioState?>(null as SteamAudioState), backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamAudioSurface.PatchId, "setVolume", """{"percent":40,"input":true}""");
        var refused = await DispatchAsync(
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
        SteamUiModuleSet set = new([
            SteamAudioSurface.Module(Always, () => new ValueTask<SteamAudioState?>(current), backend)
        ]);

        var absent = await DispatchAsync(set, SteamAudioSurface.PatchId, "getDevices", "null");
        current = new SteamAudioState(true, [new SteamAudioDevice("spk", "Speakers", true, false)], "spk", "", 55,
            false, null, false, "");
        var present = await DispatchAsync(set, SteamAudioSurface.PatchId, "getDevices", "null");

        Assert.False(absent.Succeeded);
        Assert.True(present.Succeeded);
        Assert.Equal(55, present.Payload!.Value.GetProperty("volumePercent").GetInt32());
        Assert.Equal("spk", present.Payload.Value.GetProperty("devices")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void AudioFormatStateKeepsOptionIdentitySeparateFromTheLabelTheUserReads()
    {
        var wire = SteamAudioFormatRow.Serialize(new SteamAudioFormatState(true,
            [new SteamAudioFormatOption("2ch-16-48000", "Stereo"), new SteamAudioFormatOption("8ch-24-48000", "7.1")],
            "8ch-24-48000",
            [new SteamAudioFormatOption("off", "Off"), new SteamAudioFormatOption("dolby", "Dolby Atmos")],
            "off",
            "Exclusive mode is in use."));

        Assert.True(wire.GetProperty("available").GetBoolean());
        Assert.Equal("8ch-24-48000", wire.GetProperty("currentFormat").GetString());
        Assert.Equal("2ch-16-48000", wire.GetProperty("formatOptions")[0].GetProperty("id").GetString());
        Assert.Equal("7.1", wire.GetProperty("formatOptions")[1].GetProperty("label").GetString());
        Assert.Equal("off", wire.GetProperty("currentSpatial").GetString());
        Assert.Equal("dolby", wire.GetProperty("spatialOptions")[1].GetProperty("id").GetString());
        Assert.Equal("Exclusive mode is in use.", wire.GetProperty("statusText").GetString());
    }

    [Fact]
    public async Task BothAudioFormatChoicesReachTheBackendAndAMalformedOneIsRefusedByItsOwnName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([
            SteamAudioFormatRow.Module(Always,
                () => new ValueTask<SteamAudioFormatState?>(null as SteamAudioFormatState), backend)
        ]);

        var format = await DispatchAsync(
            set, SteamAudioFormatRow.PatchId, "setFormat", """{"target":"8ch-24-48000"}""");
        var spatial = await DispatchAsync(
            set, SteamAudioFormatRow.PatchId, "setSpatial", """{"target":"dolby"}""");
        var refusedFormat = await DispatchAsync(
            set, SteamAudioFormatRow.PatchId, "setFormat", """{"target":42}""");
        var refusedSpatial = await DispatchAsync(
            set, SteamAudioFormatRow.PatchId, "setSpatial", "null");

        Assert.True(format.Succeeded);
        Assert.True(spatial.Succeeded);
        Assert.Equal(["audio format 8ch-24-48000", "spatial audio dolby"], backend.Calls);
        Assert.False(refusedFormat.Succeeded);
        Assert.Equal("The audio format payload is invalid.", refusedFormat.Error);
        Assert.False(refusedSpatial.Succeeded);
        Assert.Equal("The spatial audio payload is invalid.", refusedSpatial.Error);
    }

    [Fact]
    public async Task BluetoothDeviceOperationsCarryTheDeviceIdAndTheBlueZFlagsAreAcceptedByDefault()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([
            SteamBluetoothSurface.Module(Always, () => new ValueTask<SteamBluetoothState?>(null as SteamBluetoothState),
                backend)
        ]);

        var connect = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "connect", """{"device":"aa:bb"}""");
        // The shapes Steam's panel sends, read from the client bundle: {device}, and for the two
        // BlueZ flags {device, trusted} / {device, allowed}.
        var trusted = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "setTrusted", """{"device":"aa:bb","trusted":true}""");
        var wake = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "setWakeAllowed", """{"device":"aa:bb","allowed":false}""");
        var refused = await DispatchAsync(
            set, SteamBluetoothSurface.PatchId, "forget", """{"id":"aa:bb"}""");
        var flagless = await DispatchAsync(
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
        SteamUiModuleSet set = new([
            SteamPowerLimitSurface.Module(Always,
                () => new ValueTask<SteamPowerLimitState?>(null as SteamPowerLimitState), backend)
        ]);

        var released = await DispatchAsync(
            set, SteamPowerLimitSurface.PatchId, "setPrimaryLimit", """{"watts":15}""");
        var refused = await DispatchAsync(
            set, SteamPowerLimitSurface.PatchId, "setPrimaryLimit", """{"watts":"15"}""");

        Assert.True(released.Succeeded);
        Assert.Equal("limit 15", Assert.Single(backend.Calls));
        Assert.Equal("The sustained power-limit payload is invalid.", refused.Error);
        var boosted = await DispatchAsync(
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
        SteamUiModuleSet set = new([
            SteamPowerLimitSurface.Module(Always,
                () => new ValueTask<SteamPowerLimitState?>(null as SteamPowerLimitState), backend)
        ]);
        foreach (var command in SteamPowerLimitSurface.Commands)
        {
            Assert.False((await DispatchAsync(set, SteamPowerLimitSurface.PatchId, command, payload)).Succeeded);
        }

        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void PowerSlidersSerializeSeparateHardwareReadbacks()
    {
        SteamPowerLimitRangeState sustained = new(true, 8, 37, 1, 23, "", "");
        var wire = SteamPowerLimitSurface.Serialize(new SteamPowerLimitState(sustained,
            sustained with { ObservedWatts = 37 }));
        Assert.Equal(23, wire.GetProperty("sustained").GetProperty("observedWatts").GetInt32());
        Assert.Equal(37, wire.GetProperty("boost").GetProperty("observedWatts").GetInt32());
    }

    [Fact]
    public async Task FrameLimitCarriesPersistenceAndACorrelationId()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([
            SteamFrameLimitRow.Module(Always, () => new ValueTask<SteamFrameLimitState?>(null as SteamFrameLimitState),
                backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamFrameLimitRow.PatchId, "setFrameLimit", """{"value":60,"persistence":"application"}""");
        var unknownPersistence = await DispatchAsync(
            set, SteamFrameLimitRow.PatchId, "setFrameLimit", """{"value":60,"persistence":"forever"}""");
        var refresh = await DispatchAsync(
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
        SteamUiModuleSet set = new([
            SteamPerformanceSurface.Module(Always,
                () => new ValueTask<SteamPerformanceState?>(null as SteamPerformanceState), backend)
        ]);

        var applied = await DispatchAsync(
            set,
            SteamPerformanceSurface.PatchId,
            "updateSettings",
            """{"delta":{"gameid":570,"settings_delta":{"per_app":{"fps_limit":45}}}}""");
        var undecoded = await DispatchAsync(
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
        SteamUiModuleSet set = new([
            SteamDeviceControlsRow.Module(Always,
                () => new ValueTask<SteamDeviceControlsState?>(null as SteamDeviceControlsState), backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamDeviceControlsRow.PatchId, "setLightingColor", """{"zone":"ring","color":16711680}""");
        var extra = await DispatchAsync(
            set, SteamDeviceControlsRow.PatchId, "setLightingColor", """{"zone":"ring","color":1,"alpha":1}""");
        var charge = await DispatchAsync(
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
        var reading = NullReadingOf(surface);
        var publication = Assert.Single(new SteamUiModuleSet([reading.Module]).Publications);

        Assert.Null(await publication.Read());
        reading.Supply();

        reading.Check((await publication.Read())!.Value);
    }

    private static NullReading NullReadingOf(string surface)
    {
        RecordingBackend backend = new();
        switch (surface)
        {
            case "brightness":
            {
                SteamBrightnessState? state = null;
                return new NullReading(
                    SteamBrightnessSurface.Module(Always, () => new ValueTask<SteamBrightnessState?>(state), backend),
                    () => state = new SteamBrightnessState(42),
                    wire => Assert.Equal(42, wire.GetProperty("percent").GetInt32()));
            }
            case "home-carousel":
            {
                SteamHomeCarouselState? state = null;
                return new NullReading(
                    SteamHomeCarouselSurface.Module(Always, () => new ValueTask<SteamHomeCarouselState?>(state),
                        backend),
                    () => state = new SteamHomeCarouselState(false, []),
                    wire => Assert.Equal(0, wire.GetProperty("disconnectedAppIds").GetArrayLength()));
            }
            case "library-badge":
            {
                SteamLibraryBadgeState? state = null;
                return new NullReading(
                    SteamLibraryBadgeSurface.Module(Always, () => new ValueTask<SteamLibraryBadgeState?>(state),
                        backend),
                    () => state = new SteamLibraryBadgeState([]),
                    wire => Assert.Equal(0, wire.GetProperty("libraries").GetArrayLength()));
            }
            case "navigation-panel":
            {
                SteamNavigationPanelState? state = null;
                return new NullReading(
                    SteamNavigationPanelSurface.Module(Always, () => new ValueTask<SteamNavigationPanelState?>(state),
                        backend),
                    () => state = new SteamNavigationPanelState([], ["power"]),
                    wire => Assert.Equal("power", wire.GetProperty("hidden")[0].GetString()));
            }
            case "page":
            {
                SteamPageState? state = null;
                return new NullReading(
                    SteamPageSurface.Module(Always, () => new ValueTask<SteamPageState?>(state)),
                    () => state = new SteamPageState([new SteamPage("artwork", "/wsgm/artwork", "Artwork")]),
                    wire => Assert.Equal("artwork", wire.GetProperty("pages")[0].GetProperty("id").GetString()));
            }
            case "screensaver":
            {
                SteamScreensaverState? state = null;
                return new NullReading(
                    SteamScreensaverSurface.Module(Always, () => new ValueTask<SteamScreensaverState?>(state), backend),
                    () => state = new SteamScreensaverState([]),
                    wire => Assert.Equal(0, wire.GetProperty("rows").GetArrayLength()));
            }
            case "storage":
            {
                SteamStorageState? state = null;
                return new NullReading(
                    SteamStorageSurface.Module(Always, () => new ValueTask<SteamStorageState?>(state), backend),
                    () => state = new SteamStorageState([], []),
                    wire => Assert.Equal(0, wire.GetProperty("drives").GetArrayLength()));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }
    }

    private sealed record NullReading(ISteamUiModule Module, Action Supply, Action<JsonElement> Check);
}
