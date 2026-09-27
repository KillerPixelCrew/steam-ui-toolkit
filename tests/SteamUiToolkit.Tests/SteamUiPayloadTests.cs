using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>The shared payload readers every surface reads its commands with.</summary>
public sealed class SteamUiPayloadTests
{
    [Theory]
    [InlineData("""{"query":""}""", true, "")]
    [InlineData("""{"query":"halo"}""", true, "halo")]
    [InlineData("""{"query":"toolong"}""", false, "")]
    [InlineData("""{"query":3}""", false, "")]
    [InlineData("""{}""", false, "")]
    public void AStringMayBeEmptyButNotMissingOrTooLong(string json, bool accepted, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamUiPayload.TryReadString(document.RootElement, "query", 6, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ABoundedStringStillRefusesEmpty()
    {
        using var document = JsonDocument.Parse("""{"query":""}""");

        Assert.False(SteamUiPayload.TryReadBoundedString(document.RootElement, "query", 6, out _));
    }
}
