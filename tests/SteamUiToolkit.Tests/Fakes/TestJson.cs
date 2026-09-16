using System.Text.Json;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>JSON and timing helpers the tests share.</summary>
internal static class TestJson
{
    /// <summary>Parses JSON into an element that outlives its document.</summary>
    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Polls until <paramref name="predicate" /> holds, failing after one second.</summary>
    internal static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
