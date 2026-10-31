using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The typed per-game context menu command route.</summary>
/// <remarks>
///     What the probe requires, React and the JSX runtime without any private factory placement, is the
///     gate's own find, exercised against the emitted asset by <c>eng/check-extension-surfaces.mjs</c>.
/// </remarks>
public sealed class SteamGameContextMenuTests
{
    [Fact]
    public void ItemsReachTheWire()
    {
        var wire = SteamGameContextMenuSurface.Serialize(new SteamGameContextMenuState(
            [new SteamGameContextMenuItem("org.example.artwork", "Change Artwork…", "native-actions")], 3));

        Assert.Equal("org.example.artwork", wire.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("Change Artwork…", wire.GetProperty("items")[0].GetProperty("label").GetString());
        Assert.Equal(3, wire.GetProperty("revision").GetInt64());
        Assert.Equal("native-actions", wire.GetProperty("items")[0].GetProperty("template").GetString());
    }

    [Fact]
    public async Task ActivationCarriesTheExactSteamAppAndRejectsOtherShapes()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamGameContextMenuSurface.Module(
                Always,
                () => new ValueTask<SteamGameContextMenuState?>(null as SteamGameContextMenuState),
                backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamGameContextMenuSurface.PatchId, "activate", """{"appId":480,"id":"org.example.artwork"}""");
        var refused = await DispatchAsync(
            set, SteamGameContextMenuSurface.PatchId, "activate", """{"appId":0,"id":"org.example.artwork"}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The game context menu activation payload is invalid.", refused.Error);
        Assert.Equal(["game-menu 480 org.example.artwork"], backend.Calls);
    }
}
