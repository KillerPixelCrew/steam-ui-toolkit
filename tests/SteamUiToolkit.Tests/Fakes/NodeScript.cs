using System.Diagnostics;
using System.Text.Json;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>Runs an injected expression against a scripted page model in Node.</summary>
internal static class NodeScript
{
    /// <summary>Runs <paramref name="script" />, which reads <paramref name="input" /> as JSON on stdin.</summary>
    /// <param name="script">A Node script that asserts and exits non-zero on failure.</param>
    /// <param name="input">Serialized to the script's standard input, usually the expressions.</param>
    internal static async Task RunAsync(string script, object input)
    {
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }
}
