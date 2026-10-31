using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>
///     The theme-styles surface: what it publishes. Its probe is the gate's own find, exercised against
///     the emitted asset by <c>eng/check-theme-styles.mjs</c>.
/// </summary>
public sealed class SteamThemeStyleTests
{
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
