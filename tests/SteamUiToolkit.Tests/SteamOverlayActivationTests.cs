using System.Diagnostics;
using System.Text.Json;
using SteamUiToolkit.Surfaces;

namespace SteamUiToolkit.Tests;

public sealed class SteamOverlayActivationTests
{
    [Fact]
    public async Task SubscriptionReplacementPreservesUnknownStateAndRetiresOldCallbacks()
    {
        const string script = """
            const vm=require('node:vm'),assert=require('node:assert/strict');
            let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',()=>{
              const expressions=JSON.parse(input),callbacks=[];let registered=0,removed=0;
              const context=vm.createContext({window:{},SteamClient:{Overlay:{
                RegisterForOverlayActivated(callback){registered++;callbacks.push(callback);return {unregister(){removed++;}};}
              }}});
              const run=expression=>JSON.parse(vm.runInContext(expression,context));
              assert.equal(run(expressions.first).ok,true);
              const first=context.window.__steamUiOverlayActivation;
              assert.equal(first.events.size,0);
              assert.equal(run(expressions.first).ok,true);assert.equal(registered,1);
              callbacks[0](42,123,true);assert.equal(first.events.get('42:123'),true);
              callbacks[0](43,123,false);assert.equal(first.events.get('43:123'),false);
              assert.equal(run(expressions.second).ok,true);
              assert.equal(removed,1);assert.equal(first.live,false);
              const second=context.window.__steamUiOverlayActivation;
              assert.equal(second.events.size,0);
              callbacks[0](42,123,false);assert.equal(second.events.size,0);
              callbacks[1](42,123,true);assert.equal(second.events.get('42:123'),true);
              callbacks[1](42,123,false);assert.equal(second.events.get('42:123'),false);
              callbacks[1](42,123,'false');assert.equal(second.overflow,true);
              assert.equal(second.events.size,0);
              second.stop();assert.equal(removed,2);assert.equal(second.live,false);
              callbacks[1](42,123,true);assert.equal(second.events.size,0);
            });
            """;
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
        {
            first = new SteamOverlayActivationPatch().ApplyExpression,
            second = new SteamOverlayActivationPatch().ApplyExpression,
        }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }
}
