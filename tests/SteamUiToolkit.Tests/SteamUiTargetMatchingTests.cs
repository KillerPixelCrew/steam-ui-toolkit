namespace SteamUiToolkit.Tests;

/// <summary>Which Steam CDP target is which. Every URL here is verbatim from /json/list on a
/// real client in game mode.</summary>
public sealed class SteamUiTargetMatchingTests
{
    [Fact]
    public void SharedContextRequiresSteamLoopbackHttpsPage()
    {
        Assert.True(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.SharedJsContext,
            "page",
            "SharedJSContext",
            "https://steamloopback.host/index.html?PLATFORM=windows"));
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.SharedJsContext,
            "page",
            "SharedJSContext",
            "https://example.test/index.html"));
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.SharedJsContext,
            "worker",
            "SharedJSContext",
            "https://steamloopback.host/index.html"));
    }

    [Fact]
    public void MainWindowIsTheTopLevelWindowRatherThanAnyPopup()
    {
        // Every URL here is verbatim from /json/list on the reference Claw in game mode. Two things
        // this pins: the title is localized, so it cannot be matched on; and the URL reported by CDP
        // is the one the target was CREATED with, not the document address it later navigated to —
        // matching "https://steamloopback.host/index.html" found nothing, and the glyph stylesheet
        // reported "MainWindow target is absent" while the window was plainly open.
        Assert.True(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "Big-Picture-Modus",
            "about:blank?createflags=6292754&minwidth=853&minheight=534&pid=0&browser=-1&browserType="));

        // A browser-view popup: Quick Access, the main menu, notification toasts.
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "QuickAccess_uid21",
            "about:blank?browserviewpopup=1&requestid=6&parentpopup=21"));

        // A context menu: created like a window but owned by one, and with no minimum size.
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "Menu",
            "about:blank?createflags=5529866&pid=0&browser=-1&openerid=21"));

        // SharedJSContext is a different role and holds almost no DOM.
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "SharedJSContext",
            "https://steamloopback.host/routes/app/489830/controllerconfigurator/summary"));
    }

}
