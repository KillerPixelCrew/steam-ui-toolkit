using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>The shared payload readers every surface reads its commands with.</summary>
public sealed class SteamUiPayloadTests
{
    [Fact]
    public void AOneFieldPayloadIsReadOnlyWhenItIsThatFieldAlone()
    {
        Assert.True(SteamUiPayload.TryReadOnlyString(TestJson.Parse("""{"id":"abc"}"""), "id", out var id));
        Assert.Equal("abc", id);
        Assert.False(SteamUiPayload.TryReadOnlyString(TestJson.Parse("""{"id":""}"""), "id", out _));
        Assert.False(SteamUiPayload.TryReadOnlyString(TestJson.Parse("""{"id":"abc","x":1}"""), "id", out _));
        Assert.True(SteamUiPayload.TryReadOnlyOptionalString(TestJson.Parse("""{"id":""}"""), "id", out var empty));
        Assert.Equal("", empty);
        Assert.True(SteamUiPayload.TryReadOnlyChoice(TestJson.Parse("""{"tab":"library"}"""), "tab",
            ["browse", "library"], out var tab));
        Assert.Equal("library", tab);
        Assert.False(SteamUiPayload.TryReadOnlyChoice(TestJson.Parse("""{"tab":"other"}"""), "tab",
            ["browse", "library"], out _));
        Assert.True(SteamUiPayload.TryReadOnlyBoolean(TestJson.Parse("""{"value":true}"""), "value", out var on));
        Assert.True(on);
        Assert.False(SteamUiPayload.TryReadOnlyBoolean(TestJson.Parse("""{"value":1}"""), "value", out _));
    }

    [Fact]
    public void ARouteAnswerCarriesTheRouteTheInjectedSideFollows()
    {
        var answer = SteamUiCommandResult.Route("/wsgm/themes");

        Assert.True(answer.Succeeded);
        Assert.Equal("/wsgm/themes", answer.Payload!.Value.GetProperty("route").GetString());
        Assert.Throws<ArgumentException>(() => SteamUiCommandResult.Route(" "));
    }

    [Theory]
    [InlineData("""{"query":""}""", true, "")]
    [InlineData("""{"query":"halo"}""", true, "halo")]
    [InlineData("""{"query":"a long query is taken whole"}""", true, "a long query is taken whole")]
    [InlineData("""{"query":3}""", false, "")]
    [InlineData("""{}""", false, "")]
    public void AStringMayBeEmptyButNotMissing(string json, bool accepted, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamUiPayload.TryReadString(document.RootElement, "query", out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ANonBlankStringRefusesEmpty()
    {
        using var document = JsonDocument.Parse("""{"query":""}""");

        Assert.False(SteamUiPayload.TryReadNonBlankString(document.RootElement, "query", out _));
    }

    [Theory]
    [InlineData("""{"id":null}""", true, null)]
    [InlineData("""{"id":"abc"}""", true, "abc")]
    [InlineData("""{"id":""}""", false, null)]
    [InlineData("""{"id":"a long id is taken whole"}""", true, "a long id is taken whole")]
    [InlineData("""{}""", false, null)]
    public void ANullableStringIsNullOrANonBlankString(string json, bool accepted, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamUiPayload.TryReadNullableString(document.RootElement, "id", out var value));
        Assert.Equal(expected, accepted ? value : null);
    }

    [Theory]
    [InlineData("""{"ids":[]}""", true, 0)]
    [InlineData("""{"ids":["a","b"]}""", true, 2)]
    [InlineData("""{"ids":["a","b","c"]}""", true, 3)]
    [InlineData("""{"ids":["a",""]}""", false, 0)]
    [InlineData("""{"ids":["a",1]}""", false, 0)]
    [InlineData("""{"ids":["a long entry is taken whole"]}""", true, 1)]
    [InlineData("""{"ids":"a"}""", false, 0)]
    public void AStringArrayIsNonBlankStrings(string json, bool accepted, int count)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamUiPayload.TryReadStrings(document.RootElement, "ids", out var values));
        Assert.Equal(count, values.Count);
    }
}
