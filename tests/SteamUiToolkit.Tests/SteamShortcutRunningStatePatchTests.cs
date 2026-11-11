namespace SteamUiToolkit.Tests;

public sealed class SteamShortcutRunningStatePatchTests
{
    [Fact]
    public async Task ResumeReloadKeepsTheLiveShortcutAndExitReleasesItsOverviewAndWindows()
    {
        var patch = new SteamShortcutRunningStatePatch();
        var replacement = new SteamShortcutRunningStatePatch();
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const test=JSON.parse(input),live=2147483649,unknown=2147483650,ordinary=42;
              function app(id,status){return {appid:id,per_client_data:[{clientid:'0',installed:true,display_status:status}],
                get display_status(){return this.local_per_client_data.display_status},
                get local_per_client_data(){return this.per_client_data.find(r=>r.clientid==='0')},
                BIsShortcut(){return this.appid>=2147483648}};}
              const active=app(live,4),inactive=app(unknown,11),storeApp=app(ordinary,11),all=[active,inactive,storeApp];
              let callback,removed=0;
              const appStore={m_bIsInitialized:true,allApps:all,GetAppOverviewByAppID:id=>all.find(a=>a.appid===id)};
              const prototype={ScopeRunningApps(){
                if(active.local_per_client_data.display_status!==4)removed++;
                this.running=all.filter(a=>a.local_per_client_data.display_status===4).map(a=>a.appid);
              }};
              const ui=Object.create(prototype),original=ui.ScopeRunningApps;
              const SteamClient={GameSessions:{RegisterForAppLifetimeNotifications:fn=>{callback=fn;return {unregister(){}}}}};
              const window={appStore,SteamUIStore:ui,SteamClient};
              const context=vm.createContext({window,SteamClient,appStore});
              const run=source=>JSON.parse(vm.runInContext(source,context));
              appStore.m_bIsInitialized=false;
              assert.deepEqual(run(test.probe),{ok:false,ready:false});
              appStore.m_bIsInitialized=true;
              assert.equal(run(test.probe).ok,true);
              assert.equal(run(test.apply).ok,true);
              const wrapper=ui.ScopeRunningApps;
              assert.equal(run(test.apply).ok,true);assert.equal(ui.ScopeRunningApps,wrapper);
              // Steam's bulk shortcut reload replaces its local record and drops the running flag.
              active.per_client_data=[{clientid:'0',installed:true,display_status:11}];
              ui.ScopeRunningApps();
              assert.equal(active.display_status,4);assert.equal(removed,0);
              assert.equal(inactive.display_status,11);
              callback({unAppID:ordinary,bRunning:true});assert.equal(storeApp.display_status,11);
              callback({unAppID:live,bRunning:undefined});assert.equal(active.display_status,4);
              assert.equal(run(test.replace).ok,true);assert.equal(removed,0);
              // A native exit must restore the ready record and let Valve retire its window ownership.
              callback({unAppID:live,bRunning:false});
              assert.equal(active.display_status,11);assert.equal(ui.running.includes(live),false);assert.ok(removed>0);
              // A newer native record is never overwritten by removal of an old projection.
              callback({unAppID:live,bRunning:true});assert.equal(active.display_status,4);
              active.per_client_data=[{clientid:'0',installed:true,display_status:8}];
              window.__steamUiShortcutRunningState.stop();
              assert.equal(active.display_status,8);assert.equal(ui.ScopeRunningApps,original);
              assert.equal(Object.hasOwn(ui,'ScopeRunningApps'),false);
              assert.equal(window.__steamUiRunningApps.listeners.size,0);
              window.__steamUiRunningApps.dispose();
              assert.equal(window.__steamUiRunningApps.live,false);
            });
            """;
        await NodeScript.RunAsync(script, new
        {
            apply = patch.ApplyExpression,
            replace = replacement.ApplyExpression,
            probe = SteamShortcutRunningStatePatch.ProbeExpression
        });
    }
}
