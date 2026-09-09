using SteamUiToolkit.Surfaces;

namespace SteamUiToolkit.Tests;

public sealed class SteamSideMenuTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"pid\":0,\"appid\":0,\"menu\":9}]")]
    [InlineData("[{\"pid\":0,\"appid\":0}]")]
    [InlineData("[{\"pid\":0,\"appid\":0,\"menu\":false}]")]
    [InlineData("[{\"pid\":0,\"appid\":0,\"menu\":0},{\"pid\":0,\"appid\":0,\"menu\":0}]")]
    public void UnknownOrMalformedStateNeverProvesClosure(string? json)
    {
        var state = new SteamSideMenuSnapshot(default, SteamSideMenuObserver.Parse(json));
        Assert.Null(state.Windows);
        Assert.False(state.AllSideMenusClosed);
    }

    [Fact]
    public void OverlayQamPreventsClosureEvenWhenMainWindowIsClosed()
    {
        var windows = SteamSideMenuObserver.Parse("""
            [{"pid":0,"appid":0,"menu":0},{"pid":42,"appid":123,"menu":2}]
            """);
        var snapshot = new SteamSideMenuSnapshot(default, windows);
        Assert.False(snapshot.AllSideMenusClosed);
        Assert.Equal(42u, windows![1].ProcessId);
        Assert.Equal(SteamSideMenu.QuickAccess, windows[1].Menu);
    }

    [Fact]
    public void ConfirmedClosedMainWindowHasKnownState()
    {
        var snapshot = new SteamSideMenuSnapshot(default,
            SteamSideMenuObserver.Parse("[{\"pid\":0,\"appid\":0,\"menu\":0}]"));
        Assert.True(snapshot.AllSideMenusClosed);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public void ClosedMenusDoNotProveAnOverlayIsInactive(string active, bool closed)
    {
        var windows = SteamSideMenuObserver.Parse(
            "[{\"pid\":0,\"appid\":0,\"menu\":0,\"active\":false},"
            + "{\"pid\":42,\"appid\":123,\"menu\":0,\"active\":" + active + "}]");
        var snapshot = new SteamSideMenuSnapshot(default, windows);
        Assert.True(snapshot.AllSideMenusClosed);
        Assert.Equal(closed, snapshot.AllSteamSurfacesClosed);
    }
}
