using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The navigation panel surface's own contract: what a publication puts on the wire.</summary>
/// <remarks>
///     Every structural fact the gate resolves on was measured against the live client on 2026-09-10,
///     where <c>#MainMenu_Title</c> and <c>MainNavMenuContainer</c> each occur in exactly one of the 2581
///     loaded modules, that module has exactly one export whose memo renders the container, and that
///     export's <c>type</c> is a writable and configurable own property. What the probe demands is the
///     gate's own find, exercised against the emitted asset by <c>eng/check-navigation-panel.mjs</c>.
/// </remarks>
public sealed class SteamNavigationPanelTests
{
    [Fact]
    public void EntriesAndHiddenNamesReachTheWireAsPublished()
    {
        SteamNavigationPanelState state = new(
            [new SteamNavigationItem("wsgm-overlay", "WSGM", "cores", After: "/library")],
            ["power"]);

        var wire = SteamNavigationPanelSurface.Serialize(state);
        var item = wire.GetProperty("items")[0];

        Assert.Equal("wsgm-overlay", item.GetProperty("id").GetString());
        Assert.Equal("WSGM", item.GetProperty("label").GetString());
        Assert.Equal("cores", item.GetProperty("icon").GetString());
        Assert.Equal("/library", item.GetProperty("after").GetString());
        Assert.Equal("power", wire.GetProperty("hidden")[0].GetString());
    }

    [Fact]
    public void ARouteAndAGlyphReachTheWireUnderTheNamesTheGateReads()
    {
        SteamNavigationPanelState state = new(
            [new SteamNavigationItem("settings", "Settings", Before: "power", Route: "/host/settings", Glyph: "M1 1h2v2H1Z")],
            []);

        var item = SteamNavigationPanelSurface.Serialize(state).GetProperty("items")[0];

        Assert.Equal("/host/settings", item.GetProperty("route").GetString());
        Assert.Equal("M1 1h2v2H1Z", item.GetProperty("glyph").GetString());
    }

    [Fact]
    public async Task ActivationReachesTheBackendAndAMalformedIdIsRefusedByName()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamNavigationPanelSurface.Module(
                Always, () => new ValueTask<SteamNavigationPanelState?>(null as SteamNavigationPanelState), backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamNavigationPanelSurface.PatchId, "activate", """{"id":"wsgm-overlay"}""");
        var refused = await DispatchAsync(
            set, SteamNavigationPanelSurface.PatchId, "activate", """{"id":""}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The navigation activation payload is invalid.", refused.Error);
        Assert.Equal(["navigation wsgm-overlay"], backend.Calls);
    }
}
