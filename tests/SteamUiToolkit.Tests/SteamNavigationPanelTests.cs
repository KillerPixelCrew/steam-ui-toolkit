using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The navigation panel surface's own contract: what the probe demands of a client before the
///     panel is claimed, and what a publication puts on the wire.
/// </summary>
/// <remarks>
///     Every structural fact asserted here was measured against the live client on 2026-09-10, where
///     <c>#MainMenu_Title</c> and <c>MainNavMenuContainer</c> each occur in exactly one of the 2581
///     loaded modules, that module has exactly one export whose memo renders the container, and that
///     export's <c>type</c> is a writable and configurable own property.
/// </remarks>
public sealed class SteamNavigationPanelTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamNavigationPanelSurface.Patch;

    [Fact]
    public void TheProbeNamesEveryStructuralFactTheGateResolvesOn()
    {
        // Each fact is separate so an incompatible client says which one moved. A probe that
        // reported one boolean would only ever say "something changed".
        var probe = Gate.ProbeExpression;

        Assert.Contains("#MainMenu_Title", probe, StringComparison.Ordinal);
        Assert.Contains("MainNavMenuContainer", probe, StringComparison.Ordinal);
        Assert.Contains("RunnningAppSeparator", probe, StringComparison.Ordinal);
        Assert.Contains("memoExports", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeSelectsTheExportByWhatItDrawsRatherThanByName()
    {
        // Minified export names are right for exactly one client build. The live panel's export is
        // called v_ today and that name is deliberately nowhere in this file or the probe.
        var probe = Gate.ProbeExpression;

        Assert.DoesNotContain("v_", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("exports.Ie", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeSeesThroughTheGatesOwnClaim()
    {
        // A successful patch must stay compatible with its own next probe. The gate replaces the
        // memo's type with a wrapper, so a probe testing the live value's source stops finding the
        // export the moment the gate holds it. memoExports read 0 while the patch was Applied, and
        // the manager retracts on that verdict: the panel was torn down and rebuilt every poll, so
        // WSGM's entries were never in it long enough to see.
        var probe = Gate.ProbeExpression;

        Assert.Contains("__steamUiNavigationPanelClaimed", probe, StringComparison.Ordinal);
        Assert.Contains("__steamUiNavigationPanelOriginal", probe, StringComparison.Ordinal);
        Assert.Contains("steam-ui-property-snapshot-v1", probe, StringComparison.Ordinal);
        // The pre-rename spelling a previous build could have left on a running client.
        Assert.Contains("__wsgmNavigationPanelClaimed", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlreadyClaimedPanelIsCompatibleWithoutBeingClaimableAgain()
    {
        using var document = JsonDocument.Parse(
            """{"menuModule":1,"panelRoot":1,"memoExports":1,"react":1,"claimable":false,"claimed":true}""");

        Assert.True(Gate.Compatible(document.RootElement));
    }

    [Theory]
    [InlineData("""{"menuModule":1,"panelRoot":1,"memoExports":1,"react":1,"claimable":true}""", true)]
    // Two candidate exports is ambiguous, which is a refusal rather than a reason to pick one.
    [InlineData("""{"menuModule":1,"panelRoot":1,"memoExports":2,"react":1,"claimable":true}""", false)]
    // A non-writable type could be replaced by nothing and restored to nothing.
    [InlineData("""{"menuModule":1,"panelRoot":1,"memoExports":1,"react":1,"claimable":false}""", false)]
    [InlineData("""{"menuModule":0,"panelRoot":1,"memoExports":1,"react":1,"claimable":true}""", false)]
    [InlineData("""{"menuModule":1,"panelRoot":0,"memoExports":1,"react":1,"claimable":true}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, Gate.Compatible(document.RootElement));
    }

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
        Assert.Equal(["activate wsgm-overlay"], backend.Calls);
    }
}
