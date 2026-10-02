namespace SteamUiToolkit.Tests;

public sealed class SteamLibraryReadTests
{
    [Fact]
    public void AValidEmptyLibraryRemainsSuccessful()
    {
        var result = SteamLibraryData.ParseReadGames(CefEvalResult.Ok("{\"ok\":true,\"apps\":[]}"));
        Assert.True(result.Succeeded);
        Assert.Empty(result.Games);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"err\":\"Library loading\"}")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"apps\":[{\"id\":\"bad\",\"name\":\"Game\"}]}")]
    [InlineData("invalid")]
    public void FailedOrInvalidAnswersNeverPretendToBeAnEmptyLibrary(string payload)
    {
        Assert.False(SteamLibraryData.ParseReadGames(CefEvalResult.Ok(payload)).Succeeded);
    }

    [Fact]
    public void AConfirmedLibraryIncludesTheShortcutIdentityAndSortsNames()
    {
        var result = SteamLibraryData.ParseReadGames(CefEvalResult.Ok(
            "{\"ok\":true,\"apps\":[{\"id\":2147483690,\"name\":\"Zed\",\"sc\":true},{\"id\":42,\"name\":\"Alpha\",\"sc\":false}]}"));
        Assert.True(result.Succeeded);
        Assert.Equal(["Alpha", "Zed"], result.Games.Select(game => game.Name));
        Assert.True(result.Games[1].Shortcut);
    }
}
