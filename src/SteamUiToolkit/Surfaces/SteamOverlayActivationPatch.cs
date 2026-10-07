using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Owns an overlay activation subscription in the current Steam document.</summary>
/// <remarks>
///     One entry per game process seen in this document's life, never pruned: the document is replaced
///     on a Steam restart. A callback whose identity is invalid is ignored, because it cannot be
///     attributed; one whose state is not a boolean forgets that identity alone.
/// </remarks>
public sealed class SteamOverlayActivationPatch : ISteamUiPatch
{
    /// <summary>Private page namespace shared by the activation subscription and side-menu observer.</summary>
    internal const string StateKey = "__steamUiOverlayActivation";
    private readonly string _removeExpression;
    private readonly string _verifyExpression;

    /// <summary>Creates a subscription owned by this instance.</summary>
    public SteamOverlayActivationPatch()
    {
        var key = SteamCef.JsString(StateKey);
        var owner = SteamCef.JsString(Guid.NewGuid().ToString("N"));
        ApplyExpression = $$"""
                            (()=>{
                              const key={{key}},owner={{owner}};
                              const previous=window[key];
                              if(previous){
                                if(previous.version!==1||typeof previous.stop!=='function')return JSON.stringify({ok:false});
                                if(previous.owner===owner)return JSON.stringify({ok:previous.live===true});
                                previous.stop();
                              }
                              const state={version:1,owner,live:false,events:new Map(),stop:null};
                              let handle;
                              state.stop=()=>{state.live=false;handle?.unregister();state.events.clear();};
                              handle=SteamClient.Overlay.RegisterForOverlayActivated((pid,appid,active)=>{
                                if(!state.live)return;
                                if(!Number.isInteger(pid)||pid<=0||!Number.isInteger(appid)||appid<0)return;
                                const identity=`${pid}:${appid}`;
                                if(typeof active!=='boolean'){state.events.delete(identity);return;}
                                state.events.set(identity,active);
                              });
                              if(typeof handle?.unregister!=='function')return JSON.stringify({ok:false});
                              state.live=true;window[key]=state;
                              return JSON.stringify({ok:true});
                            })()
                            """;
        _verifyExpression = $$"""
                              (()=>{const s=window[{{key}}];return JSON.stringify({ok:s?.owner==={{owner}}&&s.live===true});})()
                              """;
        _removeExpression = $$"""
                              (()=>{const key={{key}},s=window[key];if(s?.owner==={{owner}}){s.stop();delete window[key];}return JSON.stringify({ok:true});})()
                              """;
    }

    /// <summary>The subscription expression, fixed for this owner.</summary>
    internal string ApplyExpression { get; }

    /// <inheritdoc />
    public string Id => "steam-ui.overlay-activation";

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        var result = await context.EvaluateAsync(TargetRole,
            "JSON.stringify({ok:typeof SteamClient?.Overlay?.RegisterForOverlayActivated==='function'})",
            cancellationToken).ConfigureAwait(false);
        var supported = result.Answered && result.Value is not null &&
                        SteamUiPatchEvaluation.IsSuccessful(result.Value);
        return new SteamUiPatchProbeResult(result.Answered, supported,
            supported ? "overlay-activation-v1" : null, result.Error);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole, ApplyExpression,
            "Overlay activation subscription failed.", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole, _verifyExpression,
            "Overlay activation subscription is unavailable.", cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole, _removeExpression,
            "Overlay activation subscription cleanup failed.", cancellationToken);
    }
}
