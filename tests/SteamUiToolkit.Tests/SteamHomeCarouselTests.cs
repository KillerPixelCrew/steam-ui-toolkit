using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The Home carousel surface's own contract: what a publication puts on the wire and what the
///     carousel's report carries back. What the probe demands before Home is claimed is the gate's own
///     find, exercised against the emitted asset by <c>eng/check-home-carousel.mjs</c>.
/// </summary>
public sealed class SteamHomeCarouselTests
{
    [Fact]
    public void TheInstructionReachesTheWireAsPublished()
    {
        var wire = SteamHomeCarouselSurface.Serialize(
            new SteamHomeCarouselState(true, [70, 400], 3));

        Assert.True(wire.GetProperty("includeUninstalled").GetBoolean());
        Assert.Equal(400, wire.GetProperty("disconnectedAppIds")[1].GetInt64());
        Assert.Equal(3, wire.GetProperty("revision").GetInt64());
    }

    [Theory]
    [InlineData(
        """{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true,"fallback":false}""",
        true)]
    [InlineData("""{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true}""", false)]
    [InlineData(
        """{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true,"fallback":false,"extra":1}""",
        false)]
    [InlineData(
        """{"items":-1,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":true,"fallback":false}""",
        false)]
    [InlineData(
        """{"items":12,"purchases":1,"installed":9,"uninstalled":0,"excluded":4,"tracking":"yes","fallback":false}""",
        false)]
    public void TheReportIsExactlyFiveCountsAndTwoFlags(string json, bool valid)
    {
        using var payload = JsonDocument.Parse(json);

        var read = SteamHomeCarouselSurface.TryReadReport(payload.RootElement, out var report);

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
            SteamHomeCarouselSurface.Module(Always,
                () => new ValueTask<SteamHomeCarouselState?>(null as SteamHomeCarouselState), backend)
        ]);

        var applied = await DispatchAsync(
            set,
            SteamHomeCarouselSurface.PatchId,
            "report",
            """{"items":3,"purchases":0,"installed":3,"uninstalled":0,"excluded":0,"tracking":true,"fallback":false}""");
        var refused = await DispatchAsync(
            set, SteamHomeCarouselSurface.PatchId, "report", """{"items":3}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The home carousel report is invalid.", refused.Error);
        Assert.Equal(["home carousel 3"], backend.Calls);
    }
}
