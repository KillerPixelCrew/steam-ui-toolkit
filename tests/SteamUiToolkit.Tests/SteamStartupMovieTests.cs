namespace SteamUiToolkit.Tests;

/// <summary>Setting Steam's own startup movie choice aside and putting it back: replies and script shape.</summary>
public sealed class SteamStartupMovieTests
{
    [Fact]
    public void ASetAsideChoiceComesBackWhole()
    {
        var result = SteamStartupMovie.Parse(CefEvalResult.Ok(
            """{"ok":true,"choice":{"movieId":"38357673024","localPath":"/communityitemscache/a.webm","shuffle":true}}"""));

        Assert.True(result.Succeeded);
        Assert.Equal(new SteamStartupMovieChoice("38357673024", "/communityitemscache/a.webm", true), result.Choice);
        Assert.False(result.Choice!.IsDefault);
    }

    [Fact]
    public void NothingToChangeIsASuccessWithoutAChoice()
    {
        var result = SteamStartupMovie.Parse(CefEvalResult.Ok("""{"ok":true,"choice":null}"""));

        Assert.True(result.Succeeded);
        Assert.Null(result.Choice);
    }

    [Fact]
    public void UnreachableIsToldApartFromRefused()
    {
        var unreachable = SteamStartupMovie.Parse(CefEvalResult.Unreachable("port closed"));
        var refused = SteamStartupMovie.Parse(CefEvalResult.Ok("""{"ok":false,"err":"not loaded"}"""));

        Assert.False(unreachable.Reachable);
        Assert.Equal("port closed", unreachable.Error);
        Assert.True(refused.Reachable);
        Assert.False(refused.Accepted);
        Assert.Equal("not loaded", refused.Error);
    }

    [Fact]
    public void TheDefaultIsNoItemNoPathAndNoShuffle()
    {
        Assert.True(new SteamStartupMovieChoice("", "", false).IsDefault);
        Assert.False(new SteamStartupMovieChoice("", "", true).IsDefault);
    }

    [Fact]
    public void BothScriptsWriteThroughSteamsOwnSetterAndPutBackOnlyOverTheDefault()
    {
        var setAside = SteamStartupMovie.SetAsideScript();
        var restore = SteamStartupMovie.RestoreScript(new SteamStartupMovieChoice("1", "/x'.webm", false));

        Assert.Contains("GetClientSetting('startup_movie_local_path')[1]", setAside, StringComparison.Ordinal);
        Assert.Contains("if(plain(now))", setAside, StringComparison.Ordinal);
        Assert.Contains("if(!plain(now)||plain(back))", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("/x'.webm'", restore, StringComparison.Ordinal);
    }
}
