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

    [Theory]
    [InlineData("""{"id":null}""", true, null)]
    [InlineData("""{"id":"abc"}""", true, "abc")]
    [InlineData("""{"id":""}""", false, null)]
    [InlineData("""{"id":"toolong"}""", false, null)]
    [InlineData("""{}""", false, null)]
    public void ANullableStringIsNullOrANonBlankString(string json, bool accepted, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamUiPayload.TryReadNullableString(document.RootElement, "id", 6, out var value));
        Assert.Equal(expected, accepted ? value : null);
    }

    [Theory]
    [InlineData("""{"ids":[]}""", true, 0)]
    [InlineData("""{"ids":["a","b"]}""", true, 2)]
    [InlineData("""{"ids":["a","b","c"]}""", false, 0)]
    [InlineData("""{"ids":["a",""]}""", false, 0)]
    [InlineData("""{"ids":["a",1]}""", false, 0)]
    [InlineData("""{"ids":["toolong"]}""", false, 0)]
    [InlineData("""{"ids":"a"}""", false, 0)]
    public void AStringArrayIsBoundedInCountAndLength(string json, bool accepted, int count)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamUiPayload.TryReadStrings(document.RootElement, "ids", 2, 6, out var values));
        Assert.Equal(count, values.Count);
    }
}
