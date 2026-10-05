using System.Diagnostics;
using System.Text.Json;

namespace SteamUiToolkit.Tests.Fakes;

/// <summary>Runs an injected expression against a scripted page model in Node.</summary>
internal static class NodeScript
{
    /// <summary>Runs <paramref name="script" />, which reads <paramref name="input" /> as JSON on stdin.</summary>
    /// <param name="script">A Node script that asserts and exits non-zero on failure.</param>
    /// <param name="input">Serialized to the script's standard input, usually the expressions.</param>
    internal static async Task<string> RunAsync(string script, object input)
    {
        var configuredNode = Environment.GetEnvironmentVariable("NODE");
        var executable = string.IsNullOrWhiteSpace(configuredNode) ? "node" : configuredNode;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException(
                $"Could not start Node '{executable}'. Set NODE to the executable path or put node on PATH.", error);
        }
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException error)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"Node script exceeded ten seconds.\nstdout:\n{await stdout}\nstderr:\n{await stderr}", error);
        }

        var output = await stdout;
        Assert.True(process.ExitCode == 0, $"stderr:\n{await stderr}\nstdout:\n{output}");
        return output;
    }
}
