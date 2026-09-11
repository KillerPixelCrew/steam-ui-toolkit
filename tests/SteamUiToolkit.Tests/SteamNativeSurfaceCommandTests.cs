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
              const routes={P:{GamepadUI:{Keyboard:()=>'/keyboard'}},r:()=>'/routes'};
              const require=id=>{assert.equal(id,'80344');return routes;};
              require.m={'80344':function(){/* GameAPIOSK:()=>"/gameapiosk" */},'61236':function(){/* unrelated */}};
              const context=vm.createContext({window:{SteamUIStore:ui,webpackChunksteamui:{push(a){a[2](require);}}}});
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
              ui.BHomeAndQuickAccessButtonsEnabled=()=>true;
              main.MenuStore={CloseSideMenus(){}};
              main.VirtualKeyboardManager={IsShowingVirtualKeyboard:{Value:false},SetDismissOnEnterKey(){},
                SetVirtualKeyboardVisible(){this.IsShowingVirtualKeyboard.Value=true;calls.push('keyboard');}};
              assert.equal(run('keyboard'),true);assert.equal(run('keyboard'),true);
              assert.equal(calls.filter(c=>c==='keyboard').length,1);
              overlay.IsGamepadUIOverlayWindow=()=>true;
              overlay.MenuStore=main.MenuStore;
              overlay.VirtualKeyboardManager={...main.VirtualKeyboardManager,IsShowingVirtualKeyboard:{Value:false}};
              overlay.NavigateWithoutChangingFocus=(route,a,b)=>{assert.equal(route,'/keyboard');assert.equal(a,true);assert.equal(b,true);calls.push('game-keyboard');};
              assert.equal(run('gameKeyboard'),true);
              assert.equal(calls.at(-1),'game-keyboard');
              delete overlay.NavigateWithoutChangingFocus;assert.equal(run('gameKeyboard'),false);
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
            keyboard = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.Keyboard, 0, 0),
            gameKeyboard = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.Keyboard, 42, 123),
        }));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }
}
