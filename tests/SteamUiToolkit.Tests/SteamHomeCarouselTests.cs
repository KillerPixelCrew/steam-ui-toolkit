using System.Reflection;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The Home carousel surface's own contract: what the probe demands before Home is claimed, what a
/// publication puts on the wire, and what the carousel's report carries back.
/// </summary>
/// <remarks>
/// The structural facts were read from the September 2026 client beta's shipped bundle on
/// 2026-09-11: <c>HomeTabsActive</c> with <c>#Showcase_RecentGames</c> occurs in one module, the
/// <c>/library/home</c> route renders a <c>React.memo</c>, and mobx-react-lite's startup check
/// occurs once.
/// </remarks>
public sealed class SteamHomeCarouselTests
{
    private static readonly Func<bool> Always = () => true;

    [Fact]
    public void TheProbeNamesEveryStructuralFactTheGateResolvesOn()
    {
        string probe = FieldOf<string>("_probeExpression");

        Assert.Contains("HomeTabsActive", probe, StringComparison.Ordinal);
        Assert.Contains("#Showcase_RecentGames", probe, StringComparison.Ordinal);
        Assert.Contains("/library/home", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
        Assert.Contains("GetAppOverviewByAppID", probe, StringComparison.Ordinal);
        Assert.Contains("mobx-react-lite requires React with Hooks support", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeFindsHomeByContentRatherThanByName()
    {
        // Home is `r5` and the carousel `hi` in today's build. The probe matches the route by its
        // path and the page by its source, and names neither.
        string probe = FieldOf<string>("_probeExpression");

        Assert.DoesNotContain("r5", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("46307", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"homeModule":1,"homeFound":1,"claimable":true,"claimed":false,"stores":true,"observer":1,"react":1}""", true)]
    // Steam's observer hook is wanted, not required.
    [InlineData("""{"homeModule":1,"homeFound":1,"claimable":true,"claimed":false,"stores":true,"observer":0,"react":1}""", true)]
    [InlineData("""{"homeModule":1,"homeFound":0,"claimable":false,"claimed":false,"stores":true,"observer":1,"react":1}""", false)]
    [InlineData("""{"homeModule":2,"homeFound":1,"claimable":true,"claimed":false,"stores":true,"observer":1,"react":1}""", false)]
    [InlineData("""{"homeModule":1,"homeFound":1,"claimable":true,"claimed":false,"stores":false,"observer":1,"react":1}""", false)]
    // A non-writable type could be replaced by nothing and restored to nothing.
    [InlineData("""{"homeModule":1,"homeFound":1,"claimable":false,"claimed":false,"stores":true,"observer":1,"react":1}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, FieldOf<Func<JsonElement, bool>>("_compatible")(document.RootElement));
    }

    [Fact]
    public void AnAlreadyClaimedHomeStaysCompatible()
        => Assert.Contains(
            "__steamUiHomeCarouselClaimed",
            FieldOf<string>("_probeExpression"),
            StringComparison.Ordinal);

    [Fact]
    public void VerificationRequiresTheClaimAndRemovalRequiresItsAbsence()
    {
        Assert.Equal("status.installed&&status.resolved&&status.claimed", FieldOf<string>("_verifyOk"));
        Assert.Equal("!status.claimed", FieldOf<string>("_removeOk"));
    }

    [Fact]
    public void TheInstructionReachesTheWireAsPublished()
    {
        JsonElement wire = SteamHomeCarouselSurface.Serialize(
            new SteamHomeCarouselState(IncludeUninstalled: true, [70, 400], Revision: 3));

        Assert.True(wire.GetProperty("includeUninstalled").GetBoolean());
        Assert.Equal(400, wire.GetProperty("disconnectedAppIds")[1].GetInt64());
        Assert.Equal(3, wire.GetProperty("revision").GetInt64());
    }

    [Theory]
    [InlineData("""{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true,"fallback":false}""", true)]
    [InlineData("""{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true}""", false)]
    [InlineData("""{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true,"fallback":false,"extra":1}""", false)]
    [InlineData("""{"items":-1,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true,"fallback":false}""", false)]
    [InlineData("""{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":"yes","fallback":false}""", false)]
    public void TheReportIsExactlyFiveCountsAndTwoFlags(string json, bool valid)
    {
        using JsonDocument payload = JsonDocument.Parse(json);

        bool read = SteamHomeCarouselSurface.TryReadReport(payload.RootElement, out SteamHomeCarouselReport report);

        Assert.Equal(valid, read);
        if (valid)
        {
            Assert.Equal(new SteamHomeCarouselReport(12, 1, 9, 0, 4, true, false), report);
        }
    }

    [Fact]
    public async Task TheReportReachesTheBackendAndAMalformedOneIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamHomeCarouselSurface.Module(Always, () => new(null as SteamHomeCarouselState), backend),
        ]);

        SteamUiCommandResult applied = await Dispatch(
            set,
            """{"items":3,"purchases":0,"installed":3,"uninstalled":0,"excluded":0,"tracking":true,"fallback":false}""");
        SteamUiCommandResult refused = await Dispatch(set, """{"items":3}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The home carousel report is invalid.", refused.Error);
        Assert.Equal(3, Assert.Single(backend.Reports).Items);
    }

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanAnEmptyInstruction()
    {
        // An empty instruction is a real one — nothing is disconnected — and not the same as having
        // nothing to say yet.
        SteamHomeCarouselState? state = null;
        SteamUiModuleSet set = new(
        [
            SteamHomeCarouselSurface.Module(Always, () => new(state), new RecordingBackend()),
        ]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        state = new SteamHomeCarouselState(false, []);

        Assert.Equal(0, (await publication.Read())!.Value.GetProperty("disconnectedAppIds").GetArrayLength());
    }

    private static T FieldOf<T>(string name) =>
        (T)SteamHomeCarouselSurface.Patch.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamHomeCarouselSurface.Patch)!;

    private static async Task<SteamUiCommandResult> Dispatch(SteamUiModuleSet set, string payloadJson)
    {
        Assert.True(set.TryGetCommand(
            SteamHomeCarouselSurface.PatchId, "report", out SteamUiCommandDelegate? handler));
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            SteamHomeCarouselSurface.PatchId,
            "report",
            1,
            1,
            0,
            0,
            payload.RootElement.Clone());
        return await handler!(request, CancellationToken.None);
    }

    private sealed class RecordingBackend : ISteamHomeCarouselBackend
    {
        internal List<SteamHomeCarouselReport> Reports { get; } = [];

        public Task<SteamUiCommandResult> ReportAsync(
            SteamHomeCarouselReport report,
            CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
