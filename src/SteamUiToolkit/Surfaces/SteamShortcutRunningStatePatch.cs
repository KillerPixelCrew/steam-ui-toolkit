using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Preserves a running shortcut's overview through Steam's post-resume shortcut reload.</summary>
/// <remarks>
///     Steam's native shortcut reload clears its cached running bit without ending the process lifetime.
///     This patch reconciles ready local shortcut overviews with the shared positive lifetime observer
///     before Steam scopes its running apps. A real exit restores the latest native overview. It never
///     infers a running game from installation, focus, or a process name, and performs no native writes.
/// </remarks>
public sealed class SteamShortcutRunningStatePatch : ISteamUiPatch
{
    private readonly string _owner = Guid.NewGuid().ToString("N");

    /// <summary>The page-owned repair, used by the patch phases and execution checks.</summary>
    internal const string StateKey = "__steamUiShortcutRunningState";

    /// <summary>Whether the published store supports the narrow reconciliation boundary.</summary>
    internal const string ProbeExpression = """
        (()=>{
          const ui=window.SteamUIStore,apps=window.appStore;
          if(!ui||!apps||apps.m_bIsInitialized!==true)return JSON.stringify({ok:false,ready:false});
          const own=ui&&Object.getOwnPropertyDescriptor(ui,'ScopeRunningApps');
          return JSON.stringify({ready:true,ok:Object.isExtensible(ui)&&
            typeof apps.GetAppOverviewByAppID==='function'&&typeof ui.ScopeRunningApps==='function'&&
            (!own||own.configurable===true)&&
            typeof window.SteamClient?.GameSessions?.RegisterForAppLifetimeNotifications==='function'});
        })()
        """;

    /// <summary>Builds one ownership-scoped repair using the existing lifetime observer.</summary>
    internal string ApplyExpression => $$"""
        (()=>{try{
          {{SteamRunningAppsProbe.InstallObserver}}
          const key='{{StateKey}}',owner='{{_owner}}',previous=window[key];
          if(previous){
            if(previous.version!==1||typeof previous.stop!=='function')throw Error('Unknown shortcut repair owner');
            if(previous.owner===owner)return JSON.stringify({ok:previous.live&&previous.owns()&&!previous.error});
            if(!previous.stop(false))throw Error('Previous shortcut repair could not be removed');
          }
          const ui=window.SteamUIStore,apps=window.appStore,R=window.__steamUiRunningApps;
          if(R?.version!==2||!R.live||!(R.listeners instanceof Set))throw Error('Lifetime observer requires a Steam restart');
          if(!ui||!Object.isExtensible(ui)||apps?.m_bIsInitialized!==true||typeof apps.GetAppOverviewByAppID!=='function')throw Error('Steam app store is not ready');
          const saved=Object.getOwnPropertyDescriptor(ui,'ScopeRunningApps'),original=ui.ScopeRunningApps;
          if(typeof original!=='function'||(saved&&!saved.configurable))throw Error('Running-app scope is not claimable');
          const records=new Map();
          const state={version:1,owner,source:R,live:true,error:'',busy:false,owns:()=>ui.ScopeRunningApps===scope,stop:null};
          function restore(id,record){
            const app=apps.GetAppOverviewByAppID(id),rows=app?.per_client_data;
            if(app===record.app&&Array.isArray(rows)&&rows.includes(record.row)&&record.row.display_status===4){
              app.per_client_data=rows.map(row=>row===record.row?{...row,display_status:record.status}:row);
            }
          }
          function reconcile(){
            for(const [id,record] of records){
              const app=apps.GetAppOverviewByAppID(id);
              if(!R.live||!R.ids.has(id)||app!==record.app||!app?.per_client_data?.includes(record.row)){
                restore(id,record);records.delete(id);
              }
            }
            if(!R.live)return;
            for(const id of R.ids){
              // Only shortcuts with a positive native lifetime and the observed ready-state reset.
              if(!Number.isInteger(id)||id<2147483648||id>4294967295)continue;
              const app=apps.GetAppOverviewByAppID(id),rows=app?.per_client_data;
              if(!app||!Array.isArray(rows)||app.BIsShortcut?.()!==true)continue;
              const local=rows.find(row=>row.clientid==='0');
              if(!local||local.installed!==true||local.display_status!==11)continue;
              app.per_client_data=rows.map(row=>row===local?{...row,display_status:4}:row);
              records.set(id,{app,row:app.local_per_client_data,status:local.display_status});
            }
          }
          function scope(...args){
            if(state.live&&!state.busy){
              state.busy=true;
              try{reconcile();}catch(e){state.error=String(e?.message||e);}
              finally{state.busy=false;}
            }
            return original.apply(this,args);
          }
          const changed=()=>{try{scope.call(ui);}catch(e){state.error=String(e?.message||e);} };
          state.stop=(scopeNative=true)=>{
            state.live=false;R.listeners.delete(changed);
            for(const [id,record] of records)restore(id,record);
            records.clear();
            if(!state.owns())return false;
            if(saved)Object.defineProperty(ui,'ScopeRunningApps',saved);else delete ui.ScopeRunningApps;
            if(scopeNative)original.call(ui);
            if(window[key]===state)delete window[key];
            return true;
          };
          Object.defineProperty(ui,'ScopeRunningApps',{value:scope,writable:true,configurable:true});
          window[key]=state;R.listeners.add(changed);changed();
          return JSON.stringify({ok:state.owns()&&!state.error,error:state.error});
        }catch(e){return JSON.stringify({ok:false,error:String(e?.message||e)});} })()
        """;

    /// <inheritdoc />
    public string Id => "steam-ui.shortcut-running-state";

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        var result = await context.EvaluateAsync(TargetRole, ProbeExpression, cancellationToken).ConfigureAwait(false);
        var supported = result.Answered && result.Value is not null && SteamUiPatchEvaluation.IsSuccessful(result.Value);
        var ready = false;
        if (result.Answered && result.Value is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(result.Value);
                ready = document.RootElement.TryGetProperty("ready", out var value) && value.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                return new SteamUiPatchProbeResult(true, false, null, "Steam returned an invalid shortcut repair probe.");
            }
        }

        return new SteamUiPatchProbeResult(ready, supported,
            supported ? "shortcut-running-state-v1" : null, result.Error ?? (!ready ? "Waiting for Steam's initialized app store." : null));
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole, ApplyExpression,
            "Shortcut running-state repair failed.", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (()=>{const s=window.{{StateKey}};return JSON.stringify({ok:s?.owner==='{{_owner}}'&&s.live&&s.owns()&&
              window.__steamUiRunningApps===s.source&&s.source.live===true&&!s.error,error:s?.error});})()
            """;
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole, expression,
            "Shortcut running-state repair lost ownership or failed reconciliation.", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (()=>{const s=window.{{StateKey}};return JSON.stringify({ok:!s||s.owner!=='{{_owner}}'||s.stop()});})()
            """;
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole, expression,
            "Shortcut running-state repair cleanup failed.", cancellationToken);
    }
}
