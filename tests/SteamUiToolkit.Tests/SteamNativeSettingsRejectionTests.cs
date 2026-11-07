using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class SteamNativeSettingsRejectionTests
{
    [Fact]
    public void TheRejectionReportIsReadAsKeysAndReasons()
    {
        using var document = JsonDocument.Parse(
            """{"rows":[{"key":"gpu/x","reason":"invalid range"},{"key":"y","reason":"duplicate key"}]}""");

        Assert.True(SteamNativeSettingsSurface.TryReadRejected(document.RootElement, out var text));
        Assert.Equal("gpu/x (invalid range), y (duplicate key)", text);
    }

    [Theory]
    [InlineData("""{"rows":[]}""")]
    [InlineData("""{"rows":[{"key":1,"reason":"x"}]}""")]
    [InlineData("""{"rows":[],"extra":1}""")]
    public void AMalformedReportIsRefused(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(SteamNativeSettingsSurface.TryReadRejected(document.RootElement, out _));
    }

    [Fact]
    public void TheVocabularyCarriesTheRejectionReport()
    {
        Assert.Equal(["set", "rejected"], SteamNativeSettingsSurface.Commands);
    }
}
