namespace SteamUiToolkit.Tests;

public sealed class SteamSoundOverrideTests
{
    [Fact]
    public async Task PublicationCarriesEveryVariantAndTheHostRevisionAndExposesNoCommands()
    {
        var sounds = new Dictionary<string, IReadOnlyList<string>>
        {
            ["focus.wav"] = Enumerable.Range(0, 20).Select(index => $"data:audio/wav;base64,{index}").ToArray()
        };
        var state = new SteamSoundOverrideState(sounds, Revision: 17);
        var module = SteamSoundOverrideSurface.Module(() => true,
            () => ValueTask.FromResult<SteamSoundOverrideState?>(state), () => state.Revision, id: "example.sounds");
        var set = new SteamUiModuleSet([module]);
        var publication = Assert.Single(module.Publications);

        Assert.Equal(17L, publication.Revision!());
        var payload = await publication.Read();
        Assert.True(payload.HasValue);
        Assert.Equal(17, payload.Value.GetProperty("revision").GetInt64());
        Assert.Equal(20, payload.Value.GetProperty("sounds").GetProperty("focus.wav").GetArrayLength());
        Assert.Empty(module.Commands);
        Assert.Empty(set.AllowedCommands[SteamSoundOverrideSurface.PatchId]);
        Assert.False(set.TryGetCommand(SteamSoundOverrideSurface.PatchId, "play", out _));
    }

    [Fact]
    public async Task MissingStatePublishesNothing()
    {
        var module = SteamSoundOverrideSurface.Module(() => true,
            () => new ValueTask<SteamSoundOverrideState?>(), () => 0);
        Assert.Null(await Assert.Single(module.Publications).Read());
    }
}
