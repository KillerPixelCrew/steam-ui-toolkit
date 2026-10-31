namespace SteamUiToolkit.Tests;

/// <summary>The gate patch every host page is declared with.</summary>
/// <remarks>
///     What the probe requires is the page's own <c>components</c> and <c>required</c> list, exercised
///     against the emitted asset by <c>eng/check-pages.mjs</c>; the patch only asks the page's gate.
/// </remarks>
public sealed class SteamPagePatchTests
{
    private static SteamGatePatch Page()
    {
        return (SteamGatePatch)SteamPagePatch.Create(
            "example.page",
            "examplePage",
            "example-page-v1",
            "Example page");
    }

    [Fact]
    public void TheProbeIsThePageGatesOwn()
    {
        var page = Page();

        Assert.Equal("example.page", page.Id);
        Assert.Equal("examplePage", page.GateName);
        Assert.Contains("b.gate(\"examplePage\")", page.ProbeExpression, StringComparison.Ordinal);
        Assert.Contains("bridge.probe()", page.ProbeExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPageGateIsNotReadyRatherThanIncompatible()
    {
        Assert.Contains("outcome:'notReady',reason:'bridge unavailable'", Page().ProbeExpression,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void APageWithoutAGateNameIsRefused(string gateName)
    {
        Assert.Throws<ArgumentException>(() =>
            SteamPagePatch.Create("example.page", gateName, "v1", "Example"));
    }
}
