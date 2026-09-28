using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The theme-styles surface: what its probe asks of a client and what it publishes.</summary>
public sealed class SteamThemeStyleTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamThemeStyleSurface.Patch;

    [Fact]
    public void StylesAreDeclaredRatherThanCommanded()
    {
        // A stylesheet is state: it exists because the host published it, and nothing in a window
        // can ask the host for a different one.
        Assert.Empty(SteamThemeStyleSurface.Commands);
    }

    [Fact]
    public void TheProbeReadsSteamsPopupManagerAndNothingElse()
    {
        // The gate touches documents, not modules: no webpack capture, no module id, no export.
        var probe = Gate.ProbeExpression;

        Assert.Contains("g_PopupManager", probe, StringComparison.Ordinal);
        Assert.Contains("GetPopups", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("webpackChunk", probe, StringComparison.Ordinal);

        using var compatible = JsonDocument.Parse("""{"popupManager":1,"popups":3}""");
        using var absent = JsonDocument.Parse("""{"popupManager":0,"popups":0}""");

        Assert.True(Gate.Compatible(compatible.RootElement));
        Assert.False(Gate.Compatible(absent.RootElement));
    }

    [Fact]
    public void TheGateVerifiesByHoldingAndRemovesByLettingGo()
    {
        Assert.Equal("status.installed&&status.resolved", Gate.VerifyOk);
        Assert.Equal("!status.installed", Gate.RemoveOk);
        Assert.Equal(SteamUiTargetRole.SharedJsContext, Gate.TargetRole);
    }

    [Fact]
    public void BlocksReachTheWireWithTheirTargetsAndHash()
    {
        var wire = SteamThemeStyleSurface.Serialize(new SteamThemeState(
            [new SteamThemeStyle("dark.theme.css", "body{}", ["~Gamepad~", "QuickAccess.*"], "abc")], 4));

        var style = wire.GetProperty("styles")[0];
        Assert.Equal("dark.theme.css", style.GetProperty("id").GetString());
        Assert.Equal("body{}", style.GetProperty("css").GetString());
        Assert.Equal("abc", style.GetProperty("hash").GetString());
        Assert.Equal(2, style.GetProperty("targets").GetArrayLength());
        Assert.Equal(4, wire.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task TheModulePublishesTheBlocksUnderItsRevision()
    {
        var revision = 7L;
        var module = SteamThemeStyleSurface.Module(
            Always,
            () => new ValueTask<SteamThemeState?>(new SteamThemeState(
                [new SteamThemeStyle("one", "a{}", ["SP"], "1")], 7)),
            () => revision);

        var publication = Assert.Single(module.Publications);
        Assert.Equal(SteamThemeStyleSurface.PatchId, publication.PatchId);
        Assert.NotNull(publication.Revision);
        Assert.Equal(7, publication.Revision!());
        var payload = await publication.Read();
        Assert.Equal("one", payload!.Value.GetProperty("styles")[0].GetProperty("id").GetString());
        Assert.Empty(module.Commands);
    }
}
