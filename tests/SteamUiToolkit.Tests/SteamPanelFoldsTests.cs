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
        var nested = await DispatchAsync(
            set, SteamPanelFoldsSurface.PatchId, "setFolded",
            """{"id":"extensions:wsgm.themes:theme:Dark Deck","folded":false}""");

        Assert.True(folded.Succeeded);
        Assert.True(opened.Succeeded);
        Assert.Equal("The panel fold payload is invalid.", refused.Error);
        Assert.Equal("The panel fold payload is invalid.", surplus.Error);
        Assert.Equal("The panel fold payload is invalid.", unnamed.Error);
        Assert.True(nested.Succeeded);
        Assert.Equal(
            ["Power profiles True", "Power profiles False", "extensions:wsgm.themes:theme:Dark Deck False"],
            backend.Calls);
    }

    [Fact]
    public async Task ABackendRefusalReachesTheCallerWithItsDetail()
    {
        var backend = new RecordingBackend();
        var refusal = SteamUiCommandResult.Invalid("The fold could not be saved.");
        backend.Results[nameof(backend.SetFoldedAsync)] = refusal;
        var set = new SteamUiModuleSet([
            SteamPanelFoldsSurface.Module(Always, () => new ValueTask<SteamPanelFoldsState?>(), backend)
        ]);
        var result = await DispatchAsync(set, SteamPanelFoldsSurface.PatchId, "setFolded",
            """{"id":"Power profiles","folded":true}""");

        Assert.Equal(refusal, result);
        Assert.Equal("Power profiles True", Assert.Single(backend.Calls));
    }
}
