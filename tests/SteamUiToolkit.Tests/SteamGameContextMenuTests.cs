using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The typed per-game context menu command route and its library-menu fingerprint.</summary>
public sealed class SteamGameContextMenuTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamGameContextMenuSurface.Patch;

    [Fact]
    public void ProbeRequiresReactAndJsxForObservationWithoutRequiringPrivateFactoryPlacement()
    {
        var probe = Gate.ProbeExpression;

        Assert.Contains("GetTargetApps", probe, StringComparison.Ordinal);
        Assert.Contains("BuildManageSubmenu", probe, StringComparison.Ordinal);
        Assert.Contains("GetPrimaryActionMenuItem", probe, StringComparison.Ordinal);
        Assert.Contains(".jsx", probe, StringComparison.Ordinal);
        Assert.Contains(".jsxs", probe, StringComparison.Ordinal);

        using var compatible = JsonDocument.Parse(
            """{"menuModule":1,"react":1,"jsx":1}""");
        using var relocated = JsonDocument.Parse(
            """{"menuModule":0,"react":1,"jsx":1}""");
        using var split = JsonDocument.Parse(
            """{"menuModule":2,"react":1,"jsx":1}""");

        Assert.True(Gate.Compatible(compatible.RootElement));
        Assert.True(Gate.Compatible(relocated.RootElement));
        Assert.True(Gate.Compatible(split.RootElement));
        Assert.Contains("menuModule:count", probe, StringComparison.Ordinal);
        Assert.Contains("!status.ambiguous", Gate.VerifyOk, StringComparison.Ordinal);
        Assert.Contains("!status.nativeRefused", Gate.VerifyOk, StringComparison.Ordinal);
        Assert.Contains("status.observing", Gate.VerifyOk, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"menuModule":1,"react":1}""")]
    [InlineData("""{"menuModule":1,"react":1,"jsx":0}""")]
    [InlineData("""{"menuModule":1,"react":1,"jsx":2}""")]
    public void ProbeRefusesMissingOrAmbiguousJsxRuntime(string json)
    {
        using var probe = JsonDocument.Parse(json);

        Assert.False(Gate.Compatible(probe.RootElement));
    }

    [Theory]
    [InlineData("""{"react":0,"jsx":1}""")]
    [InlineData("""{"react":2,"jsx":1}""")]
    public void ProbeStillRefusesMissingOrAmbiguousReact(string json)
    {
        using var probe = JsonDocument.Parse(json);

        Assert.False(Gate.Compatible(probe.RootElement));
    }

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
