using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamPerformanceDeltaTests
{
    private static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void APerAppFrameLimitChangeIsRecognized()
    {
        bool read = SteamPerformanceDeltaReader.TryRead(
            Payload("""{"delta":{"gameid":42,"settings_delta":{"per_app":{"fps_limit":60}}}}"""),
            out SteamPerformanceDelta delta,
            out string? error);

        Assert.True(read);
        Assert.Null(error);
        Assert.Equal((uint)42, delta.SteamAppId);
        Assert.Equal(
            new SteamPerformanceChange(SteamPerformanceSetting.FrameLimit, 60),
            Assert.Single(delta.Recognized));
    }

    [Fact]
    public void NullFieldsAreNotChanges()
    {
        // toObject() emits the whole message, not only what the setter touched. Treating an unset
        // field as a change would make one slider write every other control's value back on every
        // drag.
        SteamPerformanceDeltaReader.TryRead(
            Payload(
                """
                {"delta":{"settings_delta":{"per_app":{"fps_limit":60,"is_vrr_enabled":null,
                "display_refresh_manual_hz":null}}}}
                """),
            out SteamPerformanceDelta delta,
            out _);

        Assert.Equal(
            new SteamPerformanceChange(SteamPerformanceSetting.FrameLimit, 60),
            Assert.Single(delta.Recognized));
    }

    [Fact]
    public void BooleansBecomeFlags()
    {
        SteamPerformanceDeltaReader.TryRead(
            Payload(
                """
                {"delta":{"settings_delta":{"per_app":{"is_vrr_enabled":true,
                "is_fps_limit_enabled":false}}}}
                """),
            out SteamPerformanceDelta delta,
            out _);

        Assert.Contains(
            delta.Recognized,
            change => change.Kind is SteamPerformanceSetting.VariableRefreshRate && change.AsFlag);
        Assert.Contains(
            delta.Recognized,
            change => change.Kind is SteamPerformanceSetting.FrameLimitEnabled && !change.AsFlag);
    }

    [Fact]
    public void GlobalAndPerAppSettingsBothArrive()
    {
        SteamPerformanceDeltaReader.TryRead(
            Payload(
                """
                {"delta":{"settings_delta":{"global":{"perf_overlay_level":3},
                "per_app":{"fps_limit":30}}}}
                """),
            out SteamPerformanceDelta delta,
            out _);

        Assert.Equal(2, delta.Recognized.Count);
    }

    [Fact]
    public void AnUnbackedFieldIsReportedRatherThanSilentlyDropped()
    {
        // A control that appears to work and does nothing is worse than one that is not there, so
        // an unsupported field has to reach the log.
        SteamPerformanceDeltaReader.TryRead(
            Payload("""{"delta":{"settings_delta":{"per_app":{"cpu_governor":2}}}}"""),
            out SteamPerformanceDelta delta,
            out _);

        Assert.Empty(delta.Recognized);
        Assert.Equal("cpu_governor", Assert.Single(delta.Unsupported));
    }

    [Fact]
    public void ResetToDefaultIsCarried()
    {
        SteamPerformanceDeltaReader.TryRead(
            Payload("""{"delta":{"reset_to_default":true}}"""),
            out SteamPerformanceDelta delta,
            out _);

        Assert.True(delta.ResetToDefault);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("769")]
    [InlineData("\"769\"")]
    [InlineData("\"18374686479671623680\"")]
    public void AGameIdThatIsNotAnAppIdTargetsTheGlobalProfile(string gameId)
    {
        // gameid is 64-bit and the client emits it as a number or a string by magnitude. A full
        // game id is not an AppID and must not be truncated into one, and 769 — the Steam client's
        // own pseudo-app — is how every store setter addresses the global profile.
        SteamPerformanceDeltaReader.TryRead(
            Payload($$$"""{"delta":{"gameid":{{{gameId}}}}}"""),
            out SteamPerformanceDelta delta,
            out _);

        Assert.Null(delta.SteamAppId);
    }

    [Fact]
    public void AGameIdSentAsAStringStillResolves()
    {
        SteamPerformanceDeltaReader.TryRead(
            Payload("""{"delta":{"gameid":"570"}}"""),
            out SteamPerformanceDelta delta,
            out _);

        Assert.Equal((uint)570, delta.SteamAppId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"delta":7}""")]
    [InlineData("[]")]
    public void APayloadWithoutADeltaObjectIsRefusedWithAReason(string json)
    {
        bool read = SteamPerformanceDeltaReader.TryRead(
            Payload(json),
            out _,
            out string? error);

        Assert.False(read);
        Assert.NotNull(error);
    }

    [Fact]
    public void AnUndecodedDeltaIsNamedAsTheGateRegressionItIs()
    {
        // Every store setter calls UpdateSettings with serializeBase64String(); a string here means
        // the gate stopped decoding and every performance control silently stopped working.
        bool read = SteamPerformanceDeltaReader.TryRead(
            Payload("""{"delta":"CgQIAxAB"}"""),
            out _,
            out string? error);

        Assert.False(read);
        Assert.Contains("undecoded", error);
    }
}

public sealed class SteamOverlayLevelWireTests
{
    // Valve's EGraphicsPerfOverlayLevel: Hidden=0, Basic=1, Medium=2, Full=3, Minimal=4 — while
    // the selector presents OFF, Minimal, Basic, Medium, Full.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void WireAndNotchTranslateBothWays(int steamValue, int notch)
    {
        Assert.Equal(notch, SteamOverlayLevelWire.ToNotch(steamValue));
        Assert.Equal(steamValue, SteamOverlayLevelWire.ToSteam(notch));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void UnknownValuesReadAsOff(int value)
    {
        Assert.Equal(0, SteamOverlayLevelWire.ToNotch(value));
        Assert.Equal(0, SteamOverlayLevelWire.ToSteam(value));
    }
}

public sealed class SteamPerformanceStateTests
{
    [Fact]
    public void UnsuppliedFieldsAreOmittedSoValveHidesTheirControls()
    {
        // Availability is read straight out of this state, so an omitted field is a hidden control
        // and a null written explicitly would be read as a value.
        JsonElement json = SteamPerformanceSurface.Serialize(new SteamPerformanceState
        {
            Limits = new SteamPerformanceLimits { FpsLimitOptions = [30, 60] },
            Global = new SteamPerformanceGlobalSettings { PerfOverlayLevel = 0 },
            PerApp = new SteamPerformanceApplicationSettings { FpsLimit = 60, IsFpsLimitEnabled = true },
        });

        string text = json.GetRawText();
        Assert.Contains("\"fps_limit_options\":[30,60]", text);
        Assert.DoesNotContain("is_vrr_supported", text);
        Assert.DoesNotContain("display_refresh_manual_hz", text);
        Assert.Contains("\"currentGameId\":\"769\"", text);
        Assert.Contains("\"activeProfileGameId\":\"769\"", text);
    }
}
