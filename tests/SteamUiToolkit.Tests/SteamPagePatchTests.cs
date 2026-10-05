using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>The gate patch every host page is declared with.</summary>
public sealed class SteamPagePatchTests
{
    private static SteamGatePatch Page()
    {
        return (SteamGatePatch)SteamPagePatch.Create(
            "example.page",
            "examplePage",
            "example-page-v1",
            "Example page",
            [SteamPageProbe.React, SteamPageProbe.Fields, SteamPageProbe.LibraryClasses]);
    }

    [Fact]
    public void TheProbeCountsEveryModuleThePageDrawsFrom()
    {
        var probe = Page().ProbeExpression;

        Assert.Contains("react:count(" + SteamUiProbeJs.ReactTokens + ")", probe, StringComparison.Ordinal);
        Assert.Contains("controls:count(" + SteamUiProbeJs.NativeFieldTokens + ")", probe, StringComparison.Ordinal);
        Assert.Contains("classes:count(" + SteamUiProbeJs.LibraryClassTokens + ")", probe, StringComparison.Ordinal);
        Assert.EndsWith(SteamUiProbeJs.Close, probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"react":1,"controls":1,"classes":1}""", true)]
    [InlineData("""{"react":1,"controls":2,"classes":1}""", false)]
    [InlineData("""{"react":1,"controls":1}""", false)]
    public void ThePageIsCompatibleOnlyWhenEveryModuleIsUnique(string counts, bool compatible)
    {
        using var document = JsonDocument.Parse(counts);

        Assert.Equal(compatible, Page().Compatible(document.RootElement));
    }

    [Fact]
    public void DuplicateAndInvalidProbeNamesAreRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => SteamPagePatch.Create("example.page", "example", "v1", "Example",
            [SteamPageProbe.React, SteamPageProbe.React]));
        Assert.Throws<ArgumentException>(() => SteamPagePatch.Create("example.page", "example", "v1", "Example",
            [new SteamPageProbe("invalid-name", SteamUiProbeJs.ReactTokens)]));
    }

    [Fact]
    public void APageDrawnFromNothingIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            SteamPagePatch.Create("example.page", "examplePage", "v1", "Example", []));
    }
}
