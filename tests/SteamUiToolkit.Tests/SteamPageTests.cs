using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The custom-page surface's contract: what the probe demands of a client, and what a publication
///     puts on the wire.
/// </summary>
/// <remarks>
///     Measured against the live client on 2026-09-10 and re-read on 2026-09-24. The router module is
///     unique on <c>Settings.Root()</c> plus <c>TopLevelTransition</c>, and the router memo is reachable
///     through SharedJSContext's React root with a writable, configurable <c>type</c>. The Route is
///     borrowed from the route list rather than looked up, so the back-stack module and its export are
///     reported by the probe and required by nothing.
/// </remarks>
public sealed class SteamPageTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamPageSurface.Patch;

    [Fact]
    public void PagesAreDeclaredRatherThanCommanded()
    {
        // A page is state, not an action: it exists because the host published it. There is no
        // command vocabulary to get wrong, and the empty list is the contract that says so.
        Assert.Empty(SteamPageSurface.Commands);
    }

    [Fact]
    public void TheProbeChecksTheRouterAndTheBackStackRouteSeparately()
    {
        var probe = Gate.ProbeExpression;

        Assert.Contains("Settings.Root()", probe, StringComparison.Ordinal);
        Assert.Contains("TopLevelTransition", probe, StringComparison.Ordinal);
        Assert.Contains("router-backstack", probe, StringComparison.Ordinal);
        Assert.Contains("routerFound", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeLooksForTheRouterInTheRenderedTree()
    {
        // The router memo is not an export — verified against the live client, where every export
        // of the router module was inspected and none carried it. The probe has to find it the same
        // way the gate does or it would pass on a client the gate cannot actually claim.
        var probe = Gate.ProbeExpression;

        Assert.Contains("__reactContainer$", probe, StringComparison.Ordinal);
        Assert.Contains("elementType", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeReportsSteamsOwnRouteByWhatItsAuthorTyped()
    {
        // Steam's back-stack Route is what gives a page native back navigation, and the probe reports
        // whether the client still exports one by two markers Valve wrote. The fingerprint these
        // replaced described the minified code between them and assumed a one-character local; the
        // 2026-09-24 client emits two, as the live body below shows, and it stopped matching.
        const string liveRoute =
            """function Y(he){const{children:Z,...q}=he,pe=be=>typeof Z==="function"?Z(be):Z;return(0,h.jsx)(D.qh,{...q,children:be=>(0,h.jsx)(Q,{routePath:be.match?.path,disabled:!be.match,children:pe(be)})})}""";
        var probe = Gate.ProbeExpression;

        Assert.Contains("routePath:", probe, StringComparison.Ordinal);
        Assert.Contains(".match?.path", probe, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"routePath:.\.match\?\.path.", liveRoute);
    }

    [Theory]
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""",
        true)]
    // Already ours is compatible: a probe demanding the pre-claim shape would retract every applied
    // gate on its next poll.
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":false,"claimed":true,"routeSwitch":1,"react":1}""",
        true)]
    // The Route is borrowed from the route list, so its export and its module are reported and
    // required by nothing; refusing over them is what took every custom page down on 2026-09-24.
    [InlineData(
        """{"routerModule":1,"backstackModule":0,"steamRoute":0,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""",
        true)]
    // The router has not rendered yet: not a broken client, but not claimable either.
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":0,"claimable":true,"routeSwitch":1,"react":1}""",
        false)]
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":false,"routeSwitch":1,"react":1}""",
        false)]
    [InlineData(
        """{"routerModule":0,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""",
        false)]
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":true,"routeSwitch":0,"react":1}""",
        false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, Gate.Compatible(document.RootElement));
    }

    [Fact]
    public void APageReachesTheWireWithItsPathTitleAndOverrideFlag()
    {
        SteamPageState state = new(
        [
            new SteamPage("artwork", "/wsgm/artwork/:appid", "Artwork"),
            new SteamPage("settings", "/settings", "WSGM Settings", true)
        ]);

        var wire = SteamPageSurface.Serialize(state);

        Assert.Equal("/wsgm/artwork/:appid", wire.GetProperty("pages")[0].GetProperty("path").GetString());
        Assert.False(wire.GetProperty("pages")[0].GetProperty("override").GetBoolean());
        Assert.True(wire.GetProperty("pages")[1].GetProperty("override").GetBoolean());
    }

    [Fact]
    public void AddingRatherThanOverridingIsTheDefault()
    {
        // Shadowing a client route is not something a caller should get by accident: an override
        // goes in front of Steam's own routes and wins the first match, which is a different
        // operation from adding a page at a path Steam does not have.
        Assert.False(new SteamPage("x", "/x", "X").Override);
    }
}
