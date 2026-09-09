using System.Diagnostics;
using System.Text.Json;
using SteamUiToolkit.Surfaces;

namespace SteamUiToolkit.Tests;

public sealed class SteamNativeSurfaceCommandTests
{
    [Fact]
    public async Task ReplayUsesExactWindowAndNativeHandlerWithoutFallback()
    {
        const string script = """
            const vm=require('node:vm'),assert=require('node:assert/strict');
            let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',()=>{
              const expressions=JSON.parse(input),calls=[];
              const main={OnQuickAccessButtonPressed(){calls.push('main-qam');}};
              const overlay={params:{browserInfo:{m_unPID:42,m_unAppID:123}},
                IsGamepadUIOverlayWindow(){return true;},
                OnHomeButtonPressed(){calls.push('overlay-home');},
                OnQuickAccessButtonPressed(){calls.push('overlay-qam');}};
              const ui={BHomeAndQuickAccessButtonsEnabled:()=>true,WindowStore:{MainWindowInstance:main,OverlayWindows:[overlay]}};
              const require=id=>{assert.equal(id,'61236');return {oy:ui};};require.m={'61236':()=>{}};
              const context=vm.createContext({window:{webpackChunksteamui:{push(a){a[2](require);}}}});
              const run=key=>vm.runInContext(expressions[key],context);
              assert.equal(run('main'),true);assert.equal(run('home'),true);assert.equal(run('qam'),true);
              assert.deepEqual(calls,['main-qam','overlay-home','overlay-qam']);
              overlay.params.browserInfo.m_unAppID=124;assert.equal(run('qam'),false);
              overlay.params.browserInfo.m_unAppID=123;
              ui.WindowStore.OverlayWindows=[overlay,overlay];assert.equal(run('home'),false);
              ui.WindowStore.OverlayWindows=[];assert.equal(run('qam'),false);
              ui.WindowStore.OverlayWindows=[overlay];overlay.IsGamepadUIOverlayWindow=()=>false;
              assert.equal(run('home'),false);
              ui.BHomeAndQuickAccessButtonsEnabled=()=>false;assert.equal(run('main'),false);
              assert.equal(calls.length,3);
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
            main = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.QuickAccess, 0, 0),
            home = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.Home, 42, 123),
            qam = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.QuickAccess, 42, 123),
        }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }
}
