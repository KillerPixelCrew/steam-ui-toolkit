using System.Reflection;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The Screensaver settings surface's own contract: what the probe demands before the page list is
/// intercepted, what a publication puts on the wire, and the exact shapes of the report and a choice.
/// </summary>
/// <remarks>
/// The structural facts were read from the September 2026 client beta's bundle on 2026-09-11: the
/// Screensaver section's label token with <c>ForceScreensaver</c> occurs in one module, and the route
/// table's <c>Settings.Customization()</c> is <c>/settings/customization</c>.
/// </remarks>
public sealed class SteamScreensaverTests
{
    private static readonly Func<bool> Always = () => true;

    [Fact]
    public void TheProbeNamesEveryStructuralFactTheGateResolvesOn()
    {
        string probe = ProbeOf(SteamScreensaverSurface.Patch);

        Assert.Contains("\"#Settings_Customization_Screensaver\"", probe, StringComparison.Ordinal);
        Assert.Contains("ForceScreensaver", probe, StringComparison.Ordinal);
        Assert.Contains("Settings.Customization()", probe, StringComparison.Ordinal);
        Assert.Contains("m_setDeferredSettings", probe, StringComparison.Ordinal);
        Assert.Contains("DropDownField", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeNamesNoModuleIdOrExportName()
    {
        // The route table was module 80344, export B, and the settings store module 39828, export
        // rV, on the beta this was mapped against. Both change with a client build.
        string probe = ProbeOf(SteamScreensaverSurface.Patch);

        Assert.DoesNotContain("80344", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("39828", probe, StringComparison.Ordinal);
        Assert.DoesNotContain(".rV", probe, StringComparison.Ordinal);
        Assert.Contains("req.exported(", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"react":1,"fields":1,"section":1,"route":"/settings/customization","settings":true,"observer":1}""", true)]
    // The observer hook is wanted, not required.
    [InlineData("""{"react":1,"fields":1,"section":1,"route":"/settings/customization","settings":true,"observer":0}""", true)]
    [InlineData("""{"react":1,"fields":1,"section":2,"route":"/settings/customization","settings":true,"observer":1}""", false)]
    [InlineData("""{"react":1,"fields":1,"section":0,"route":"/settings/customization","settings":true,"observer":1}""", false)]
    [InlineData("""{"react":1,"fields":1,"section":1,"route":"","settings":true,"observer":1}""", false)]
    [InlineData("""{"react":1,"fields":1,"section":1,"route":"/settings/customization","settings":false,"observer":1}""", false)]
    [InlineData("""{"react":2,"fields":1,"section":1,"route":"/settings/customization","settings":true,"observer":1}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, CompatibilityOf(SteamScreensaverSurface.Patch, document.RootElement));
    }

    [Fact]
    public void VerificationRequiresTheClaimAndRemovalRequiresItsAbsence()
    {
        Assert.Equal("status.installed&&status.resolved&&status.claimed", FieldOf("_verifyOk"));
        Assert.Equal("!status.claimed", FieldOf("_removeOk"));
    }

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
                Available: true),
        ],
            Revision: 7);

        JsonElement wire = SteamScreensaverSurface.Serialize(state);
        JsonElement row = wire.GetProperty("rows")[0];

        Assert.Equal("plugged-in", row.GetProperty("id").GetString());
        Assert.Equal(600, row.GetProperty("seconds").GetInt32());
        Assert.True(row.GetProperty("available").GetBoolean());
        Assert.Equal("Not before the screensaver starts", row.GetProperty("description").GetString());
        Assert.Equal(0, row.GetProperty("options")[1].GetProperty("seconds").GetInt32());
        Assert.Equal("Never", row.GetProperty("options")[1].GetProperty("label").GetString());
        Assert.Equal(7, wire.GetProperty("revision").GetInt64());
    }

    [Theory]
    [InlineData("""{"acSeconds":300,"batterySeconds":600,"battery":true}""", true, 300, 600, true)]
    [InlineData("""{"acSeconds":0,"batterySeconds":null,"battery":false}""", true, 0, null, false)]
    [InlineData("""{"acSeconds":604800,"batterySeconds":0,"battery":false}""", true, 604800, 0, false)]
    [InlineData("""{"acSeconds":604801,"batterySeconds":0,"battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":-1,"batterySeconds":0,"battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"batterySeconds":"600","battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"battery":false}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"batterySeconds":600,"battery":1}""", false, 0, null, false)]
    [InlineData("""{"acSeconds":300,"batterySeconds":600,"battery":true,"extra":1}""", false, 0, null, false)]
    public void TheReportIsExactlyTwoTimeoutsAndABatteryFlag(
        string json, bool valid, int pluggedIn, int? battery, bool hasBattery)
    {
        using JsonDocument payload = JsonDocument.Parse(json);

        Assert.Equal(valid, SteamScreensaverSurface.TryReadReport(payload.RootElement, out SteamScreensaverReport report));
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
        using JsonDocument payload = JsonDocument.Parse(json);

        Assert.Equal(valid, SteamScreensaverSurface.TryReadTimeout(payload.RootElement, out string readRow, out int readSeconds));
        Assert.Equal(row, readRow);
        Assert.Equal(seconds, readSeconds);
    }

    [Fact]
    public async Task ReportsAndChoicesReachTheBackendAndMalformedOnesAreRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamScreensaverSurface.Module(Always, () => new(null as SteamScreensaverState), backend),
        ]);

        Assert.True((await Dispatch(set, "report", """{"acSeconds":300,"batterySeconds":null,"battery":false}""")).Succeeded);
        Assert.True((await Dispatch(set, "setTimeout", """{"row":"battery","seconds":900}""")).Succeeded);
        Assert.Equal(
            "The screensaver report is invalid.",
            (await Dispatch(set, "report", """{"acSeconds":"300"}""")).Error);
        Assert.Equal(
            "The timeout payload is invalid.",
            (await Dispatch(set, "setTimeout", """{"row":"battery"}""")).Error);
        Assert.Equal(["report 300 - False", "battery 900"], backend.Calls);
        Assert.Equal(SteamScreensaverSurface.Commands, ["report", "setTimeout"]);
    }

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanNoRows()
    {
        SteamScreensaverState? state = null;
        SteamUiModuleSet set = new(
        [
            SteamScreensaverSurface.Module(Always, () => new(state), new RecordingBackend()),
        ]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        state = new SteamScreensaverState([]);

        Assert.Equal(0, (await publication.Read())!.Value.GetProperty("rows").GetArrayLength());
    }

    private static string ProbeOf(ISteamUiPatch patch) =>
        (string)patch.GetType().GetField("_probeExpression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(patch)!;

    private static string FieldOf(string name) =>
        (string)SteamScreensaverSurface.Patch.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamScreensaverSurface.Patch)!;

    private static bool CompatibilityOf(ISteamUiPatch patch, JsonElement root) =>
        ((Func<JsonElement, bool>)patch.GetType()
            .GetField("_compatible", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(patch)!)(root);

    private static async Task<SteamUiCommandResult> Dispatch(
        SteamUiModuleSet set,
        string command,
        string payloadJson)
    {
        Assert.True(set.TryGetCommand(
            SteamScreensaverSurface.PatchId, command, out SteamUiCommandDelegate? handler));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            SteamScreensaverSurface.PatchId,
            command,
            1,
            1,
            0,
            0,
            payload.RootElement.Clone());
        return await handler!(request, CancellationToken.None);
    }

    private sealed class RecordingBackend : ISteamScreensaverBackend
    {
        internal List<string> Calls { get; } = [];

        public Task<SteamUiCommandResult> ReportAsync(
            SteamScreensaverReport report,
            CancellationToken cancellationToken)
        {
            Calls.Add($"report {report.PluggedInSeconds} {report.BatterySeconds?.ToString() ?? "-"} {report.Battery}");
            return Task.FromResult(SteamUiCommandResult.Applied);
        }

        public Task<SteamUiCommandResult> SetTimeoutAsync(
            string row,
            int seconds,
            CancellationToken cancellationToken)
        {
            Calls.Add($"{row} {seconds}");
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
