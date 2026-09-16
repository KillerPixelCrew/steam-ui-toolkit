using SteamUiToolkit.Surfaces;

namespace SteamUiToolkit.Tests;

/// <summary>
///     Steam's windows as surfaces: side-menu observation, native button replay, game-window
///     activation and overlay-activation tracking.
/// </summary>
public sealed class SteamWindowSurfaceTests
{
    [Theory]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public void KeyboardMustBeConfirmedClosedBeforeOwnershipRestoration(string keyboard, bool closed)
    {
        var windows = SteamSideMenuObserver.Parse(
            "[{\"pid\":0,\"appid\":0,\"menu\":0,\"active\":false,\"keyboard\":" + keyboard + "}]");
        Assert.Equal(closed, new SteamSideMenuSnapshot(default, windows).AllSteamSurfacesClosed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"pid\":0,\"appid\":0,\"menu\":9}]")]
    [InlineData("[{\"pid\":0,\"appid\":0}]")]
    [InlineData("[{\"pid\":0,\"appid\":0,\"menu\":false}]")]
    [InlineData("[{\"pid\":0,\"appid\":0,\"menu\":0},{\"pid\":0,\"appid\":0,\"menu\":0}]")]
    public void UnknownOrMalformedStateNeverProvesClosure(string? json)
    {
        var state = new SteamSideMenuSnapshot(default, SteamSideMenuObserver.Parse(json));
        Assert.Null(state.Windows);
        Assert.False(state.AllSideMenusClosed);
    }

    [Fact]
    public void OverlayQamPreventsClosureEvenWhenMainWindowIsClosed()
    {
        var windows = SteamSideMenuObserver.Parse("""
                                                  [{"pid":0,"appid":0,"menu":0},{"pid":42,"appid":123,"menu":2}]
                                                  """);
        var snapshot = new SteamSideMenuSnapshot(default, windows);
        Assert.False(snapshot.AllSideMenusClosed);
        Assert.Equal(42u, windows![1].ProcessId);
        Assert.Equal(SteamSideMenu.QuickAccess, windows[1].Menu);
    }

    [Fact]
    public void ConfirmedClosedMainWindowHasKnownState()
    {
        var snapshot = new SteamSideMenuSnapshot(default,
            SteamSideMenuObserver.Parse("[{\"pid\":0,\"appid\":0,\"menu\":0}]"));
        Assert.True(snapshot.AllSideMenusClosed);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public void ClosedMenusDoNotProveAnOverlayIsInactive(string active, bool closed)
    {
        var windows = SteamSideMenuObserver.Parse(
            "[{\"pid\":0,\"appid\":0,\"menu\":0,\"active\":false,\"keyboard\":false},"
            + "{\"pid\":42,\"appid\":123,\"menu\":0,\"keyboard\":false,\"active\":" + active + "}]");
        var snapshot = new SteamSideMenuSnapshot(default, windows);
        Assert.True(snapshot.AllSideMenusClosed);
        Assert.Equal(closed, snapshot.AllSteamSurfacesClosed);
    }

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
        await NodeScript.RunAsync(script, new
        {
            main = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.QuickAccess, 0, 0),
            home = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.Home, 42, 123),
            qam = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.QuickAccess, 42, 123),
            keyboard = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.Keyboard, 0, 0),
            gameKeyboard = SteamNativeSurfaceCommands.CreateExpression(SteamNativeSurfaceAction.Keyboard, 42, 123)
        });
    }

    [Fact]
    public async Task ResolvesOnlyExactOverlayProcessAndRefusesMissingAmbiguousOrExpiredRequests()
    {
        const string script = """
                              const vm=require('node:vm'),assert=require('node:assert/strict');
                              let input='';process.stdin.on('data',v=>input+=v);process.stdin.on('end',async()=>{
                                const expressions=JSON.parse(input),calls=[];
                                const overlay={params:{browserInfo:{m_unPID:42,m_unAppID:123,m_gameID:'123'}},IsGamepadUIOverlayWindow:()=>true};
                                const store={OverlayWindows:[overlay]};
                                const apps={async RaiseWindowForGame(id){calls.push(id);}};
                                const context=vm.createContext({window:{SteamUIStore:{WindowStore:store},SteamClient:{Apps:apps}}});
                                const run=key=>vm.runInContext(expressions[key],context);
                                assert.equal(await run('valid'),true);assert.deepEqual(calls,['123']);
                                overlay.params.browserInfo.m_gameID='18446744069448138752';
                                assert.equal(await run('valid'),true);assert.equal(calls.at(-1),'18446744069448138752');
                                for(const invalid of [undefined,123,'0','-1','1e3','18446744073709551616']){
                                  overlay.params.browserInfo.m_gameID=invalid;assert.equal(await run('valid'),false);
                                }
                                overlay.params.browserInfo.m_gameID='123';
                                assert.equal(await run('other'),false);
                                assert.equal(await run('main'),false);
                                assert.equal(await run('expired'),false);
                                store.OverlayWindows=[overlay,overlay];assert.equal(await run('valid'),false);
                                store.OverlayWindows=[];assert.equal(await run('valid'),false);
                                store.OverlayWindows=[overlay];overlay.params.browserInfo.m_unAppID=0;
                                assert.equal(await run('valid'),false);
                                overlay.params.browserInfo.m_unAppID=123;overlay.IsGamepadUIOverlayWindow=()=>false;
                                assert.equal(await run('valid'),false);overlay.IsGamepadUIOverlayWindow=()=>true;
                                apps.RaiseWindowForGame=undefined;assert.equal(await run('valid'),false);
                                assert.deepEqual(calls,['123','18446744069448138752']);
                                apps.RaiseWindowForGame=()=>Promise.reject(Error('rejected'));
                                assert.equal(await run('valid'),false);
                                let complete;apps.RaiseWindowForGame=()=>new Promise(resolve=>complete=resolve);
                                let finished=false;const pending=run('valid').then(value=>{finished=true;return value;});
                                await Promise.resolve();assert.equal(finished,false);
                                complete();assert.equal(await pending,true);
                              });
                              """;
        var future = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await NodeScript.RunAsync(script, new
        {
            valid = SteamGameWindowActivation.CreateExpression(42, future),
            other = SteamGameWindowActivation.CreateExpression(99, future),
            main = SteamGameWindowActivation.CreateExpression(0, future),
            expired = SteamGameWindowActivation.CreateExpression(42, 0)
        });
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CompletionRequiresCurrentReadyGeneration(bool replaced, bool unavailable, bool expected)
    {
        await using var transport = GameWindowTransport();
        transport.Health = unavailable ? SteamUiTransportHealth.Unavailable : SteamUiTransportHealth.Ready;
        transport.OnEvaluate = call =>
        {
            Assert.Equal(SteamUiTargetRole.SharedJsContext, call.Role);
            Assert.Equal(TimeSpan.FromSeconds(1), call.Timeout);
            var result = transport.Reply("true");
            if (replaced)
            {
                transport.Generations = transport.Generations with { Document = 2 };
            }

            return Task.FromResult(result);
        };

        Assert.Equal(expected, await SteamGameWindowActivation.RaiseAsync(transport, 42));
        Assert.Equal(unavailable ? 0 : 1, transport.Expressions.Count);
    }

    [Fact]
    public async Task CancelledRequestNeverDispatches()
    {
        await using var transport = GameWindowTransport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SteamGameWindowActivation.RaiseAsync(transport, 42, cancellation.Token));
        Assert.Empty(transport.Expressions);
    }

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
        await NodeScript.RunAsync(script, new
        {
            first = new SteamOverlayActivationPatch().ApplyExpression,
            second = new SteamOverlayActivationPatch().ApplyExpression
        });
    }

    /// <summary>A transport activation must borrow: it may neither subscribe nor bind.</summary>
    private static FakeSteamUiTransport GameWindowTransport()
    {
        return new FakeSteamUiTransport
        {
            RejectSubscriptions = true,
            RejectBindings = true,
            EvaluationValue = "true"
        };
    }
}
