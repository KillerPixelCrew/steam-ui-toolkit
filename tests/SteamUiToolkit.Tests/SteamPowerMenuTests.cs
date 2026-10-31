using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The power menu's Switch to Desktop wire and command route. Its probe is the gate's own find,
///     exercised against the emitted asset by <c>eng/check-power-menu.mjs</c>.
/// </summary>
public sealed class SteamPowerMenuTests
{
    [Fact]
    public void VisibilityReachesTheWire()
    {
        var wire = SteamPowerMenuSurface.Serialize(new SteamPowerMenuState(true));

        Assert.True(wire.GetProperty("visible").GetBoolean());
    }

    [Fact]
    public async Task SwitchReachesTheBackend()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamPowerMenuSurface.Module(
                Always,
                () => new ValueTask<SteamPowerMenuState?>(null as SteamPowerMenuState),
                backend)
        ]);

        var applied = await DispatchAsync(set, SteamPowerMenuSurface.PatchId, "switchToDesktop", "{}");

        Assert.True(applied.Succeeded);
        Assert.Equal(["switch-to-desktop"], backend.Calls);
    }
}
