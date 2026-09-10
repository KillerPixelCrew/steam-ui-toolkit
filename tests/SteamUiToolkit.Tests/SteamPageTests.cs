using System.Reflection;
using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>
/// The custom-page surface's contract: what the probe demands of a client, and what a publication
/// puts on the wire.
/// </summary>
/// <remarks>
/// Measured against the live client on 2026-09-10. The router module is unique on
/// <c>Settings.Root()</c> plus <c>TopLevelTransition</c>, the back-stack module is unique on
/// <c>router-backstack</c> and has exactly one export matching the Route fingerprint, and the
/// router memo is reachable through SharedJSContext's React root in 659 visited nodes with a
/// writable, configurable <c>type</c>.
/// </remarks>
public sealed class SteamPageTests
{
    private static readonly Func<bool> Always = () => true;

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
        string probe = ProbeOf();

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
        string probe = ProbeOf();

        Assert.Contains("__reactContainer$", probe, StringComparison.Ordinal);
        Assert.Contains("elementType", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeRequiresSteamsOwnRouteRatherThanAnyRoute()
    {
        // Steam's back-stack Route is what gives a page native back navigation. React-router's
        // renders the same content and silently loses it, so the probe pins the fingerprint that
        // tells them apart rather than accepting whatever the module exports.
        Assert.Contains(@"routePath:.\.match\?\.path.", ProbeOf(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""", true)]
    // The router has not rendered yet: not a broken client, but not claimable either.
    [InlineData("""{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":0,"claimable":true,"routeSwitch":1,"react":1}""", false)]
    // Two Route candidates is ambiguous, which is a refusal rather than a reason to pick one.
    [InlineData("""{"routerModule":1,"backstackModule":1,"steamRoute":2,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""", false)]
    [InlineData("""{"routerModule":1,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":false,"routeSwitch":1,"react":1}""", false)]
    [InlineData("""{"routerModule":0,"backstackModule":1,"steamRoute":1,"routerFound":1,"claimable":true,"routeSwitch":1,"react":1}""", false)]
    [InlineData("""{"error":"Steam modules unavailable"}""", false)]
    public void CompatibilityRequiresEveryFactAndAUniqueMatchForEachOne(string json, bool expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, CompatibilityOf(document.RootElement));
    }

    [Fact]
    public void VerificationRequiresTheRouteComponentAsWellAsTheClaim()
    {
        // A claimed router with no Route resolved would register pages that cannot be built.
        Assert.Equal(
            "status.installed&&status.resolved&&status.routeResolved&&status.claimed", VerifyOf());
        Assert.Equal("!status.claimed", RemoveOf());
    }

    [Fact]
    public void APageReachesTheWireWithItsPathTitleAndOverrideFlag()
    {
        SteamPageState state = new(
        [
            new SteamPage("artwork", "/wsgm/artwork/:appid", "Artwork"),
            new SteamPage("settings", "/settings", "WSGM Settings", Override: true),
        ]);

        JsonElement wire = SteamPageSurface.Serialize(state);

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

    [Fact]
    public async Task ANullReadingPublishesNothingRatherThanAnEmptyPageSet()
    {
        SteamPageState? state = null;
        SteamUiModuleSet set = new([SteamPageSurface.Module(Always, () => new(state))]);
        SteamUiStatePublication publication = Assert.Single(set.Publications);

        Assert.Null(await publication.Read());
        state = new SteamPageState([new SteamPage("artwork", "/wsgm/artwork", "Artwork")]);

        Assert.Equal(
            "artwork",
            (await publication.Read())!.Value.GetProperty("pages")[0].GetProperty("id").GetString());
    }

    private static string ProbeOf() => Field<string>("_probeExpression");

    private static string VerifyOf() => Field<string>("_verifyOk");

    private static string RemoveOf() => Field<string>("_removeOk");

    private static bool CompatibilityOf(JsonElement root) =>
        Field<Func<JsonElement, bool>>("_compatible")(root);

    private static T Field<T>(string name) =>
        (T)SteamPageSurface.Patch.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(SteamPageSurface.Patch)!;
}
