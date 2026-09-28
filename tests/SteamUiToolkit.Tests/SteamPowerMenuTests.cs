using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The power menu's Switch to Desktop command route and its menu fingerprint.</summary>
public sealed class SteamPowerMenuTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamPowerMenuSurface.Patch;

    [Fact]
    public void ProbeRequiresOnePowerMenuAndJsxRuntime()
    {
        var probe = Gate.ProbeExpression;

        Assert.Contains("#Quit_Shutdown", probe, StringComparison.Ordinal);
        Assert.Contains("#SwitchToDesktop", probe, StringComparison.Ordinal);
        Assert.Contains(".jsxs", probe, StringComparison.Ordinal);

        using var compatible = JsonDocument.Parse("""{"menuModule":1,"react":1,"jsx":1}""");
        using var missing = JsonDocument.Parse("""{"menuModule":0,"react":1,"jsx":1}""");
        using var ambiguous = JsonDocument.Parse("""{"menuModule":1,"react":1,"jsx":2}""");

        Assert.True(Gate.Compatible(compatible.RootElement));
        Assert.False(Gate.Compatible(missing.RootElement));
        Assert.False(Gate.Compatible(ambiguous.RootElement));
    }

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
