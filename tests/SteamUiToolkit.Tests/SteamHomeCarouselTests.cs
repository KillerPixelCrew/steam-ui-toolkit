using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

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
    private static SteamGatePatch Gate => (SteamGatePatch)SteamHomeCarouselSurface.Patch;

    [Fact]
    public void TheProbeNamesEveryStructuralFactTheGateResolvesOn()
    {
        string probe = Gate.ProbeExpression;

        Assert.Contains("HomeTabsActive", probe, StringComparison.Ordinal);
        Assert.Contains("#Showcase_RecentGames", probe, StringComparison.Ordinal);
        Assert.Contains("/library/home", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
        Assert.Contains("GetAppOverviewByAppID", probe, StringComparison.Ordinal);
        // A miss reports what the walk saw, so a new client says which assumption failed.
        Assert.Contains("homeRoutes", probe, StringComparison.Ordinal);
        Assert.Contains("visited", probe, StringComparison.Ordinal);
        Assert.Contains("mobx-react-lite requires React with Hooks support", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeFindsHomeByContentRatherThanByName()
    {
        // Home is `r5` and the carousel `hi` in today's build. The probe matches the route by its
        // path and the page by its source, and names neither.
        string probe = Gate.ProbeExpression;

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

        Assert.Equal(expected, Gate.Compatible(document.RootElement));
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

        SteamUiCommandResult applied = await DispatchAsync(
            set,
            SteamHomeCarouselSurface.PatchId,
            "report",
            """{"items":3,"purchases":0,"installed":3,"uninstalled":0,"excluded":0,"tracking":true,"fallback":false}""");
        SteamUiCommandResult refused = await DispatchAsync(
            set, SteamHomeCarouselSurface.PatchId, "report", """{"items":3}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The home carousel report is invalid.", refused.Error);
        Assert.Equal(["home carousel 3"], backend.Calls);
    }
}
