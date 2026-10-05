using System.Text.Json;
using System.Runtime.CompilerServices;

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

    /// <summary>Polls until <paramref name="predicate" /> holds, failing after one second unless told otherwise.</summary>
    internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? limit = null,
        [CallerArgumentExpression(nameof(predicate))] string? condition = null)
    {
        var budget = limit ?? TimeSpan.FromSeconds(1);
        using var timeout = new CancellationTokenSource(budget);
        try
        {
            while (!predicate())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {budget} waiting for {condition}.", error);
        }
    }
}
