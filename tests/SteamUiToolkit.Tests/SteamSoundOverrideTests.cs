using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

public sealed class SteamSoundOverrideTests
{
    [Fact]
    public async Task PublicationCarriesEveryVariantAndTheHostRevisionAndOnlyDecoderReports()
    {
        var sounds = new Dictionary<string, IReadOnlyList<string>>
        {
            ["focus.wav"] = Enumerable.Range(0, 20).Select(index => $"data:audio/wav;base64,{index}").ToArray()
        };
        var state = new SteamSoundOverrideState(sounds, 17);
        var module = SteamSoundOverrideSurface.Module(() => true,
            () => ValueTask.FromResult<SteamSoundOverrideState?>(state), () => state.Revision, "example.sounds");
        var set = new SteamUiModuleSet([module]);
        var publication = Assert.Single(module.Publications);

        Assert.Equal(17L, publication.Revision!());
        var payload = await publication.Read();
        Assert.True(payload.HasValue);
        Assert.Equal(17, payload.Value.GetProperty("revision").GetInt64());
        Assert.Equal(20, payload.Value.GetProperty("sounds").GetProperty("focus.wav").GetArrayLength());
        Assert.Single(module.Commands);
        Assert.Equal("status", Assert.Single(set.AllowedCommands[SteamSoundOverrideSurface.PatchId]));
        Assert.False(set.TryGetCommand(SteamSoundOverrideSurface.PatchId, "play", out _));
    }

    [Fact]
    public async Task MissingStatePublishesNothing()
    {
        var module = SteamSoundOverrideSurface.Module(() => true,
            () => new ValueTask<SteamSoundOverrideState?>(), () => 0);
        Assert.Null(await Assert.Single(module.Publications).Read());
    }

    [Fact]
    public async Task DecoderReportsCarryPartialSuccessWithoutFailingThePatch()
    {
        SteamSoundOverrideStatus? reported = null;
        var module = SteamSoundOverrideSurface.Module(Always,
            () => new ValueTask<SteamSoundOverrideState?>(), () => 7, reportStatus: status => reported = status);
        var result = await DispatchAsync(new SteamUiModuleSet([module]), SteamSoundOverrideSurface.PatchId,
            "status", """{"revision":7,"loading":false,"resources":3,"error":"Unreadable sound: focus.wav"}""");

        Assert.True(result.Succeeded);
        Assert.Equal(new SteamSoundOverrideStatus(7, false, 3, "Unreadable sound: focus.wav"), reported);
    }

    [Theory]
    [InlineData("""{"revision":"7","loading":false,"resources":3,"error":null}""")]
    [InlineData("""{"revision":7,"loading":false,"resources":-1,"error":null}""")]
    [InlineData("""{"revision":7,"loading":0,"resources":3,"error":null}""")]
    [InlineData("""{"revision":7,"loading":false,"resources":3,"error":null,"extra":1}""")]
    public async Task InvalidReportsNeverReachTheHost(string payload)
    {
        var called = false;
        var module = SteamSoundOverrideSurface.Module(Always,
            () => new ValueTask<SteamSoundOverrideState?>(), () => 7, reportStatus: _ => called = true);
        var result = await DispatchAsync(new SteamUiModuleSet([module]), SteamSoundOverrideSurface.PatchId,
            "status", payload);

        Assert.False(result.Succeeded);
        Assert.False(called);
    }
}
