using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The Screensaver settings surface's own contract: what a publication puts on the wire, and the
///     exact shapes of the report and a choice. What the probe demands before the page list is
///     intercepted is the gate's own find, exercised against the emitted asset by
///     <c>eng/check-screensaver.mjs</c>.
/// </summary>
public sealed class SteamScreensaverTests
{
    [Fact]
    public void RowsReachTheWireWithTheirChoices()
    {
        SteamScreensaverState state = new(
            [
                new SteamTimeoutRow(
                    "plugged-in",
                    "Turn display off after",
                    "Not before the screensaver starts",
                    600,
                    [new SteamTimeoutOption(300, "5 min"), new SteamTimeoutOption(0, "Never")],
                    true)
            ],
            7);

        var wire = SteamScreensaverSurface.Serialize(state);
        var row = wire.GetProperty("rows")[0];

        Assert.Equal("plugged-in", row.GetProperty("id").GetString());
        Assert.Equal(600, row.GetProperty("seconds").GetInt32());
        Assert.True(row.GetProperty("available").GetBoolean());
        Assert.Equal("Not before the screensaver starts", row.GetProperty("description").GetString());
        Assert.Equal(0, row.GetProperty("options")[1].GetProperty("seconds").GetInt32());
        Assert.Equal("Never", row.GetProperty("options")[1].GetProperty("label").GetString());
        Assert.Equal(7, wire.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void SessionActivityDoesNotReplaceTimeoutsOrInventActivityWhenUnknown()
    {
        var state = new SteamScreensaverState([], Activity: new SteamScreensaverActivity(12, 250, true));
        var wire = SteamScreensaverSurface.Serialize(state);
        var activity = wire.GetProperty("activity");
        Assert.Equal(12, activity.GetProperty("revision").GetInt64());
        Assert.Equal(250u, activity.GetProperty("ageMilliseconds").GetUInt32());
        Assert.True(activity.GetProperty("onBattery").GetBoolean());
        Assert.Empty(wire.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null,
            SteamScreensaverSurface.Serialize(new SteamScreensaverState([])).GetProperty("activity").ValueKind);
    }

    [Theory]
    [InlineData("""{"acSeconds":300,"batterySeconds":600,"battery":true}""", true, 300, 600, true)]
    [InlineData("""{"acSeconds":0,"batterySeconds":null,"battery":false}""", true, 0, null, false)]
    [InlineData("""{"acSeconds":604800,"batterySeconds":0,"battery":false}""", true, 604800, 0, false)]
    [InlineData("""{"acSeconds":604801,"batterySeconds":0,"battery":false}""", true, 604801, 0, false)]
    [InlineData("""{"acSeconds":-1,"batterySeconds":0,"battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"batterySeconds":"600","battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"batterySeconds":600,"battery":1}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"batterySeconds":600,"battery":true,"extra":1}""", false, 0, null, false)]
    public void TheReportIsExactlyTwoTimeoutsAndABatteryFlag(
        string json, bool valid, int pluggedIn, int? battery, bool hasBattery)
    {
        using var payload = JsonDocument.Parse(json);

        Assert.Equal(valid, SteamScreensaverSurface.TryReadReport(payload.RootElement, out var report));
        if (valid)
        {
            Assert.Equal(new SteamScreensaverReport(pluggedIn, battery, hasBattery), report);
        }
    }

    [Theory]
    [InlineData("""{"row":"battery","seconds":900}""", true, "battery", 900)]
    [InlineData("""{"row":"plugged-in","seconds":0}""", true, "plugged-in", 0)]
    [InlineData("""{"row":"Battery","seconds":900}""", false, "", 0)]
    [InlineData("""{"row":"1st","seconds":900}""", false, "", 0)]
    [InlineData("""{"row":"battery","seconds":-5}""", false, "", 0)]
    [InlineData("""{"row":"battery","seconds":900,"extra":true}""", false, "", 0)]
    [InlineData("""{"row":"battery"}""", false, "", 0)]
    public void AChoiceIsExactlyARowAndSeconds(string json, bool valid, string row, int seconds)
    {
        using var payload = JsonDocument.Parse(json);

        Assert.Equal(valid,
            SteamScreensaverSurface.TryReadTimeout(payload.RootElement, out var readRow, out var readSeconds));
        Assert.Equal(row, readRow);
        Assert.Equal(seconds, readSeconds);
    }

    [Fact]
    public async Task ReportsAndChoicesReachTheBackendAndMalformedOnesAreRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamScreensaverSurface.Module(Always,
                () => new ValueTask<SteamScreensaverState?>(null as SteamScreensaverState), backend)
        ]);
        var patchId = SteamScreensaverSurface.PatchId;

        Assert.True((await DispatchAsync(set, patchId, "report",
            """{"acSeconds":300,"batterySeconds":null,"battery":false}""")).Succeeded);
        Assert.True((await DispatchAsync(set, patchId, "setTimeout", """{"row":"battery","seconds":900}""")).Succeeded);
        Assert.Equal(
            "The screensaver report is invalid.",
            (await DispatchAsync(set, patchId, "report", """{"acSeconds":"300"}""")).Error);
        Assert.Equal(
            "The timeout payload is invalid.",
            (await DispatchAsync(set, patchId, "setTimeout", """{"row":"battery"}""")).Error);
        Assert.Equal(["screensaver report 300 - False", "timeout battery 900"], backend.Calls);
        Assert.Equal(SteamScreensaverSurface.Commands, ["report", "setTimeout"]);
    }
}
