namespace SteamUiToolkit.Tests;

/// <summary>What every claiming gate declares about its own verification, removal and probe.</summary>
public sealed class SteamGatePatchContractTests
{
    [Theory]
    [InlineData("home-carousel", "status.installed&&status.resolved&&status.claimed", "!status.claimed")]
    [InlineData("library-badge", "status.installed&&status.resolved&&status.claimed", "!status.claimed")]
    [InlineData("navigation-panel", "status.installed&&status.resolved&&status.claimed", "!status.claimed")]
    // The page gate verifies on the claim alone. Its Route is borrowed from Steam's first render
    // through the claimed switch, which need not have happened yet when verification runs, so
    // demanding it here would tear down a gate that is about to work. The status reports it instead.
    [InlineData("page", "status.installed&&status.resolved&&status.claimed", "!status.claimed")]
    [InlineData("screensaver", "status.installed&&status.resolved&&status.claimed", "!status.claimed")]
    public void VerificationRequiresTheClaimAndRemovalRequiresItsAbsence(
        string surface, string verify, string remove)
    {
        var gate = Gate(surface);

        Assert.Equal(verify, gate.VerifyOk);
        Assert.Equal(remove, gate.RemoveOk);
    }

    [Theory]
    [InlineData("home-carousel", "__steamUiHomeCarouselClaimed")]
    [InlineData("library-badge", "__steamUiLibraryBadgeClaimed")]
    [InlineData("navigation-panel", "__steamUiNavigationPanelClaimed")]
    public void AnAlreadyClaimedResourceStaysCompatible(string surface, string marker)
    {
        // Requiring the pre-patch shape alone would make a successful apply fail its own next probe,
        // and the manager would tear down the claim it had just verified on every poll.
        Assert.Contains(marker, Gate(surface).ProbeExpression, StringComparison.Ordinal);
    }

    internal static SteamGatePatch Gate(string surface)
    {
        return (SteamGatePatch)(surface switch
        {
            "home-carousel" => SteamHomeCarouselSurface.Patch,
            "library-badge" => SteamLibraryBadgeSurface.Patch,
            "library-details" => SteamLibraryBadgeSurface.DetailsPatch,
            "navigation-panel" => SteamNavigationPanelSurface.Patch,
            "page" => SteamPageSurface.Patch,
            "screensaver" => SteamScreensaverSurface.Patch,
            "storage" => SteamStorageSurface.Patch,
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        });
    }
}
