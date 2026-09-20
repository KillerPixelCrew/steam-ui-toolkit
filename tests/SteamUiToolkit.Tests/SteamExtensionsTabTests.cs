using System.Text.Json;
using static SteamUiToolkit.Tests.Fakes.SurfaceDispatch;

namespace SteamUiToolkit.Tests;

/// <summary>The Extensions tab's typed state, strict activation route and Quick Access fingerprint.</summary>
public sealed class SteamExtensionsTabTests
{
    private static SteamGatePatch Gate => (SteamGatePatch)SteamExtensionsTabSurface.Patch;

    [Fact]
    public void ProbeRequiresOneQuickAccessMemoAndAClaimableType()
    {
        var probe = Gate.ProbeExpression;

        Assert.Contains("QuickAccessMenuBrowserView", probe, StringComparison.Ordinal);
        Assert.Contains("memoExports", probe, StringComparison.Ordinal);
        Assert.Contains("claimable", probe, StringComparison.Ordinal);

        using var compatible = JsonDocument.Parse(
            """{"qamModule":1,"memoExports":1,"claimable":true,"react":1}""");
        using var ambiguous = JsonDocument.Parse(
            """{"qamModule":1,"memoExports":2,"claimable":true,"react":1}""");

        Assert.True(Gate.Compatible(compatible.RootElement));
        Assert.False(Gate.Compatible(ambiguous.RootElement));
    }

    [Fact]
    public void ItemsReachTheWireWithTheirVisibleFailureDetail()
    {
        var wire = SteamExtensionsTabSurface.Serialize(new SteamExtensionsTabState(
            [new SteamExtensionsTabItem("org.example.art", "Artwork", "1.0.0", "Refused", "Wrong API")]));

        var item = wire.GetProperty("items")[0];
        Assert.Equal("org.example.art", item.GetProperty("id").GetString());
        Assert.Equal("Artwork", item.GetProperty("name").GetString());
        Assert.Equal("Wrong API", item.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ActivationReachesTheBackendAndRejectsAnEmptyIdentity()
    {
        RecordingBackend backend = new();
        SteamUiModuleSet set = new(
        [
            SteamExtensionsTabSurface.Module(
                Always,
                () => new ValueTask<SteamExtensionsTabState?>(null as SteamExtensionsTabState),
                backend)
        ]);

        var applied = await DispatchAsync(
            set, SteamExtensionsTabSurface.PatchId, "activate", """{"id":"org.example.art"}""");
        var refused = await DispatchAsync(set, SteamExtensionsTabSurface.PatchId, "activate", """{"id":""}""");

        Assert.True(applied.Succeeded);
        Assert.Equal("The extension activation payload is invalid.", refused.Error);
        Assert.Equal(["activate org.example.art"], backend.Calls);
    }
}
