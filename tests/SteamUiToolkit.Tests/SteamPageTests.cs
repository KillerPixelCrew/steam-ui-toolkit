using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The custom-page surface's contract: what the probe demands of a client, and what a publication
///     puts on the wire.
/// </summary>
/// <remarks>
///     Measured against the live client on 2026-09-10. The router module is unique on
///     <c>Settings.Root()</c> plus <c>TopLevelTransition</c>, the back-stack module is unique on
///     <c>router-backstack</c> and has exactly one export matching the Route fingerprint, and the
///     router memo is reachable through SharedJSContext's React root in 659 visited nodes with a
///     writable, configurable <c>type</c>. Re-read on 2026-09-24: all of that still holds, but the
///     Route's minified locals gained a character, which is what the marker rule now tolerates.
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
    public void TheProbeSeesThroughTheGatesOwnClaim()
    {
        // A successful patch must stay compatible with its own next probe. The gate replaces the
        // memo's type with a wrapper, so a probe testing the live value's source stops recognising
        // the router the moment the gate holds it. On 2026-09-24 that retracted the page host two
        // seconds after it applied and verified, which looks exactly like the gate never installing.
        var probe = Gate.ProbeExpression;

        Assert.Contains("__steamUiPageHostClaimed", probe, StringComparison.Ordinal);
        Assert.Contains("__steamUiPageHostOriginal", probe, StringComparison.Ordinal);
        Assert.Contains("steam-ui-property-snapshot-v1", probe, StringComparison.Ordinal);
        // The pre-rename spelling a previous build could have left on a running client.
        Assert.Contains("__wsgmPageHostClaimed", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlreadyClaimedRouterIsCompatibleWithoutBeingClaimableAgain()
    {
        using var document = JsonDocument.Parse(
            """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":false,"claimed":true,"routeSwitch":1,"react":1}""");

        Assert.True(Gate.Compatible(document.RootElement));
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

    // Steam's back-stack Route as the client actually minifies it. The first is the live body read
    // from module 72500 on 2026-09-24; the second is that same body with the single-character locals
    // the client emitted before then. A fingerprint has to match both, because the only difference
    // between them is how the minifier happened to name a local.
    private const string RouteWithTwoCharacterLocals =
        """function Y(he){const{children:Z,...q}=he,pe=be=>typeof Z==="function"?Z(be):Z;return(0,h.jsx)(D.qh,{...q,children:be=>(0,h.jsx)(Q,{routePath:be.match?.path,disabled:!be.match,children:pe(be)})})}""";

    private const string RouteWithSingleCharacterLocals =
        """function Y(h){const{children:Z,...q}=h,p=b=>typeof Z==="function"?Z(b):Z;return(0,x.jsx)(D.qh,{...q,children:b=>(0,x.jsx)(Q,{routePath:b.match?.path,disabled:!b.match,children:p(b)})})}""";

    // The route-tracking component in the same module. It names the same prop but never reads a
    // match, so the second marker is what tells the two apart.
    private const string RouteTrackerDecoy =
        """function Q(he){const{children:Z,routePath:q,disabled:pe}=he,be=(0,r.useContext)(k);return r.useEffect(()=>{if(!pe){E.y.ReportRouteMatch(q)}},[q,be,pe]),(0,h.jsx)(k.Provider,{value:!0,children:Z})}""";

    [Fact]
    public void TheProbeReportsSteamsOwnRouteRatherThanAnyRoute()
    {
        // Steam's back-stack Route is what gives a page native back navigation. React-router's
        // renders the same content and silently loses it, so the probe still reports what tells them
        // apart, for the fallback lookup and for diagnostics, even though it no longer gates on it.
        var probe = Gate.ProbeExpression;

        Assert.Contains("routePath:", probe, StringComparison.Ordinal);
        Assert.Contains(".match?.path", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouteMarkersSurviveTheClientRenamingItsMinifiedLocals()
    {
        // The regression this replaced: the old fingerprint was a regex describing the minified code
        // between the two markers, and it spelled the local out as one character. The 2026-09-24
        // client emitted two, the probe answered steamRoute:0, the gate never installed, and every
        // custom page rendered as an empty client on a client that was otherwise compatible.
        Assert.DoesNotMatch(@"routePath:.\.match\?\.path.", RouteWithTwoCharacterLocals);

        Assert.True(MatchesRouteMarkers(RouteWithTwoCharacterLocals));
        Assert.True(MatchesRouteMarkers(RouteWithSingleCharacterLocals));
        Assert.False(MatchesRouteMarkers(RouteTrackerDecoy));
    }

    // The rule the probe and the gate both apply: every marker is a plain substring of the export's
    // source, with nothing said about what lies between them.
    private static bool MatchesRouteMarkers(string source)
    {
        return source.Contains("routePath:", StringComparison.Ordinal)
               && source.Contains(".match?.path", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""",
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

    [Theory]
    // The Route export gone, and the whole module with it. Both are fallback-only: the gate builds
    // with the Route it borrows from Steam's own route list, so neither may refuse a client. This is
    // the 2026-09-24 verdict inverted, and it is the entire point of borrowing rather than matching.
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":0,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""")]
    [InlineData(
        """{"routerModule":1,"backstackModule":0,"steamRoute":0,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""")]
    // Two candidates is no longer ambiguity worth refusing over either, for the same reason.
    [InlineData(
        """{"routerModule":1,"backstackModule":1,"steamRoute":2,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""")]
    public void TheBackStackRouteLookupCannotRefuseAnOtherwiseHealthyClient(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.True(Gate.Compatible(document.RootElement));
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
