using System.Text.Json;

namespace SteamUiToolkit.Tests;

public sealed class ScreensaverContentCommandTests
{
    [Theory]
    [InlineData("""{"action":"configure","id":"photos","option":"shuffle","value":true}""", true)]
    [InlineData("""{"action":"configure","id":"photos","option":"fill","value":false}""", true)]
    [InlineData("""{"action":"configure","id":"photos","option":"intervalSeconds","value":2}""", true)]
    [InlineData("""{"action":"configure","id":"scene","option":"framesPerSecond","value":60}""", true)]
    [InlineData("""{"action":"configure","id":"photos","option":"intervalSeconds","value":1}""", false)]
    [InlineData("""{"action":"configure","id":"scene","option":"framesPerSecond","value":61}""", false)]
    [InlineData("""{"action":"configure","id":"photos","option":"folder","value":"C:/"}""", false)]
    [InlineData("""{"action":"configure","id":"photos","option":"shuffle","value":"true"}""", false)]
    [InlineData("""{"action":"refresh","path":"C:/"}""", false)]
    [InlineData("""{"action":"selectionResult","revision":"1","error":null}""", false)]
    [InlineData("""{"action":"selectionResult","revision":1,"error":null}""", true)]
    [InlineData("""{"action":"playback","active":false,"id":"","selectorId":"steam-gameslideshow"}""", true)]
    [InlineData("""{"action":"thumbnail","id":"photos"}""", true)]
    [InlineData("""{"action":"thumbnail","id":"photos","folder":"C:/"}""", false)]
    [InlineData("""{"action":"rename","id":"photos","name":"Travel"}""", true)]
    [InlineData("""{"action":"rename","id":"photos","name":"Travel","shuffle":true}""", false)]
    [InlineData("""{"action":"relocate","id":"photos","folder":"C:/Photos"}""", true)]
    [InlineData("""{"action":"relocate","id":"photos","folder":"C:/Photos","pack":true}""", false)]
    public void OnlyExactClosedOperationsCrossTheTrustedConfigurationBoundary(string json, bool accepted)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(accepted, SteamScreensaverContent.TryRead(document.RootElement, out _));
    }
}
