using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

public sealed class SteamSettingsQuickAccessRowTests
{
    [Theory]
    [InlineData("{\"key\":\"\",\"value\":true}")]
    [InlineData("{\"key\":\"gpu/setting\",\"value\":{}}")]
    [InlineData("{\"key\":\"gpu/setting\",\"value\":true,\"extra\":1}")]
    public async Task InvalidChangesNeverReachTheBackend(string payload)
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([
            SteamSettingsQuickAccessRow.Module(Always,
                () => new ValueTask<SteamSettingsQuickAccessState?>(null as SteamSettingsQuickAccessState), backend)
        ]);

        var result = await DispatchAsync(set, SteamSettingsQuickAccessRow.PatchId, "set", payload);

        Assert.False(result.Succeeded);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task ASettingChangeKeepsItsPublisherQualifiedKeyAndValue()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new([
            SteamSettingsQuickAccessRow.Module(Always,
                () => new ValueTask<SteamSettingsQuickAccessState?>(null as SteamSettingsQuickAccessState), backend)
        ]);

        var result = await DispatchAsync(set, SteamSettingsQuickAccessRow.PatchId, "set",
            "{\"key\":\"gpu/setting\",\"value\":true}");

        Assert.True(result.Succeeded);
        Assert.Equal("setting gpu/setting true", Assert.Single(backend.Calls));
    }

    [Fact]
    public void CategoryIdentitySurvivesTitleChangesOnTheWire()
    {
        var wire = SteamSettingsQuickAccessRow.Serialize(new SteamSettingsQuickAccessState(
            [new SteamSettingsPage("gpu", "Adapter", [new SteamSettingsSection("Renamed", [], "frames")])], 3));

        Assert.Equal("frames", wire.GetProperty("pages")[0].GetProperty("sections")[0].GetProperty("id").GetString());
        Assert.Equal(3, wire.GetProperty("revision").GetInt64());
    }
}
