using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The panel's open sections: their wire shape, and the one command that changes them.</summary>
public sealed class SteamPanelFoldsTests
{
    [Fact]
    public void TheOpenSectionsReachTheWireUnderTheNameThePanelReads()
    {
        var wire = SteamPanelFoldsSurface.Serialize(new SteamPanelFoldsState(["Power profiles", "Charging"]));

        var open = wire.GetProperty("open");
        Assert.Equal(2, open.GetArrayLength());
        Assert.Equal("Power profiles", open[0].GetString());
        Assert.Equal("Charging", open[1].GetString());
    }

    [Fact]
    public void ThePatchMountsNoRowAndKeepsThePanelHostFingerprint()
    {
        Assert.Equal("panelFolds", SteamPanelFoldsSurface.Patch.ComponentKind);
        Assert.Equal(SteamPanelFoldsSurface.PatchId, SteamPanelFoldsSurface.Patch.Id);
        Assert.Equal(["setFolded"], SteamPanelFoldsSurface.Commands);
    }

    [Fact]
    public async Task AFoldCarriesTheSectionAndTheStateAndRejectsAnythingElse()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamPanelFoldsSurface.Module(
                Always, () => new ValueTask<SteamPanelFoldsState?>(null as SteamPanelFoldsState), backend)
        ]);

        var folded = await DispatchAsync(
            set, SteamPanelFoldsSurface.PatchId, "setFolded", """{"id":"Power profiles","folded":true}""");
        var opened = await DispatchAsync(
            set, SteamPanelFoldsSurface.PatchId, "setFolded", """{"id":"Power profiles","folded":false}""");
        var refused = await DispatchAsync(
            set, SteamPanelFoldsSurface.PatchId, "setFolded", """{"id":"Power profiles","folded":"yes"}""");
        var surplus = await DispatchAsync(
            set, SteamPanelFoldsSurface.PatchId, "setFolded",
            """{"id":"Power profiles","folded":true,"extra":1}""");
        var unnamed = await DispatchAsync(
            set, SteamPanelFoldsSurface.PatchId, "setFolded", """{"id":"","folded":true}""");

        Assert.True(folded.Succeeded);
        Assert.True(opened.Succeeded);
        Assert.Equal("The panel fold payload is invalid.", refused.Error);
        Assert.Equal("The panel fold payload is invalid.", surplus.Error);
        Assert.Equal("The panel fold payload is invalid.", unnamed.Error);
        Assert.Equal(["Power profiles True", "Power profiles False"], backend.Calls);
    }

    private sealed class RecordingBackend : ISteamPanelFoldsBackend
    {
        public List<string> Calls { get; } = [];

        public Task<SteamUiCommandResult> SetFoldedAsync(string id, bool folded, CancellationToken cancellationToken)
        {
            Calls.Add($"{id} {folded}");
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
