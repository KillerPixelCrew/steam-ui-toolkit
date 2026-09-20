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

    [Theory]
    [InlineData("""{"id":"org.example.art","key":"slot","value":true,"revision":7}""", "True")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":3.5,"revision":7}""", "3.5")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":"wide","revision":7}""", "wide")]
    public async Task AConfigureCarriesThePrimitiveValueAndTheExactRevision(string payload, string recorded)
    {
        RecordingBackend backend = new();
        using CancellationTokenSource cancellation = new();
        var set = Modules(backend);

        var applied = await DispatchAsync(
            set, SteamExtensionsTabSurface.PatchId, "configure", payload, cancellation.Token);

        Assert.True(applied.Succeeded);
        Assert.Equal($"configure org.example.art slot {recorded} 7", Assert.Single(backend.Calls));
        Assert.Equal(cancellation.Token, Assert.Single(backend.Tokens));
    }

    [Theory]
    // A negative revision, an object and an array are not settings the host can apply, and a
    // surplus property means the request did not come from the tab this surface published.
    [InlineData("""{"id":"org.example.art","key":"slot","value":true,"revision":-1}""")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":{"a":1},"revision":7}""")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":[1],"revision":7}""")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":null,"revision":7}""")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":true}""")]
    [InlineData("""{"id":"org.example.art","key":"","value":true,"revision":7}""")]
    [InlineData("""{"id":"org.example.art","key":"slot","value":true,"revision":7,"extra":1}""")]
    public async Task AMalformedConfigureIsRefusedByNameBeforeTheBackendRuns(string payload)
    {
        RecordingBackend backend = new();
        var set = Modules(backend);

        var refused = await DispatchAsync(set, SteamExtensionsTabSurface.PatchId, "configure", payload);

        Assert.False(refused.Succeeded);
        Assert.Equal("The extension setting payload is invalid.", refused.Error);
        Assert.Empty(backend.Calls);
    }

    private static SteamUiModuleSet Modules(RecordingBackend backend)
    {
        return new SteamUiModuleSet(
        [
            SteamExtensionsTabSurface.Module(
                Always,
                () => new ValueTask<SteamExtensionsTabState?>(null as SteamExtensionsTabState),
                backend)
        ]);
    }
}
