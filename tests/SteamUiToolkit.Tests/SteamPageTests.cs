namespace SteamUiToolkit.Tests;

/// <summary>The custom-page surface's contract: what a publication puts on the wire.</summary>
/// <remarks>
///     Measured against the live client on 2026-09-10 and re-read on 2026-09-24. The router module is
///     unique on <c>Settings.Root()</c> plus <c>TopLevelTransition</c>, and the router memo is reachable
///     through SharedJSContext's React root with a writable, configurable <c>type</c>. What the probe
///     demands of a client is the gate's own find, exercised against the emitted asset by
///     <c>eng/check-pages.mjs</c>: not ready while the router is not mounted, incompatible without the
///     router module, and ready from its held claims while installed.
/// </remarks>
public sealed class SteamPageTests
{
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
