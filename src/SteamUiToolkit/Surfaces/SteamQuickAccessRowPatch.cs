using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
/// Shared bounded lifecycle for one independently versioned row mounted by the injected component
/// host into Valve's Performance or Quick Settings panel.
/// </summary>
/// <remarks>
/// Every row differs only in its declaration: id, compiled component kind, fingerprint, probe chunk
/// label, and — for the rows that do not ride the performance-actions module — the factory tokens
/// that uniquely identify their own module. Which kinds exist, whether each is Valve's own
/// reactivated export or is hand-built on Valve's field primitives, and why, is decided in
/// <c>SteamUiAssets\Source\components.ts</c>, where each kind's factory carries that rationale.
/// The declarations live on the surface that owns each row.
/// </remarks>
public sealed class SteamQuickAccessRowPatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;
    private static readonly string[] CommonRequiredCounts =
    [
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];
    private static readonly string[] PerformanceActionTokens =
    [
        "SetFPSLimitEnabled",
        "SetFPSLimit",
        "SetPerfOverlayLevel",
        "SteamClient.System.Perf",
    ];

    private readonly string _componentKind;
    private readonly string _fingerprint;
    private readonly string _chunkLabel;
    private readonly string _primaryCountName;
    private readonly IReadOnlyList<string> _primaryTokens;

    /// <summary>Declares one row.</summary>
    /// <param name="id">Stable patch id.</param>
    /// <param name="componentKind">Compiled component kind accepted by the injected host.</param>
    /// <param name="fingerprint">Stable structural fingerprint describing the exact positive match.</param>
    /// <param name="chunkLabel">Stable webpack chunk label, kept for live diagnostics and probe tooling.</param>
    /// <param name="primaryCountName">The row-specific probe result property.</param>
    /// <param name="primaryTokens">Tokens that uniquely identify the row-specific factory.</param>
    public SteamQuickAccessRowPatch(
        string id,
        string componentKind,
        string fingerprint,
        string chunkLabel,
        string primaryCountName = "performanceActions",
        IReadOnlyList<string>? primaryTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryCountName);
        Id = id;
        _componentKind = componentKind;
        _fingerprint = fingerprint;
        _chunkLabel = chunkLabel;
        _primaryCountName = primaryCountName;
        _primaryTokens = primaryTokens ?? PerformanceActionTokens;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <summary>One key for every row, so the mounted set serializes on one gate.</summary>
    public string ResourceKey => "steam-ui.performanceormance-root";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>The compiled kind the injected host installs for this row.</summary>
    public string ComponentKind => _componentKind;

    /// <summary>Read-only structural probe shared by every row.</summary>
    private string ProbeExpression => $$"""
        {{SteamUiProbeJs.CountingPreamble(_chunkLabel)}}
          return JSON.stringify({
            {{_primaryCountName}}:count({{JsonSerializer.Serialize(_primaryTokens, SteamSurfaceJsonContext.Default.IReadOnlyListString)}}),
            performanceRoot:count(['#QuickAccess_Tab_Perf_Common_Settings','#QuickAccess_Tab_Perf_BatteryTimeRemaining','TS.ON_FRAME']),
            nativeFields:count(['DialogSlider_Container','DropDownField','SliderField']),
            nativeLayout:count(['PanelSectionTitle','PanelSectionRow','spinner']),
            localization:count(['Attempting to localize token','Unable to find localization token','LocalizeString']),
            react:count(['react.transitional.element','useState','cloneElement','createElement'])
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            ProbeExpression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool unique = SteamUiPatchEvaluation.IsOne(root, _primaryCountName);
            foreach (string property in CommonRequiredCounts)
            {
                unique &= SteamUiPatchEvaluation.IsOne(root, property);
            }

            return new SteamUiPatchProbeResult(
                true,
                unique,
                unique,
                unique ? _fingerprint : null,
                unique ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "return JSON.stringify(bridge.install("
            + SteamCef.JsString(_componentKind)
            + "));})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            "Native-QAM component installation failed.",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "const status=bridge.status("
            + SteamCef.JsString(_componentKind)
            + ");return JSON.stringify({ok:status.ok&&status.registered"
            + "&&status.hostVersion===1&&status.performanceRootWrapped,status});})()";
        SteamUiPatchOperationResult result = await SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            "Native-QAM component verification failed.",
            cancellationToken).ConfigureAwait(false);

        // Verification asks whether the component registered and the performance root is wrapped.
        // Both can be true while the Quick Access panel shows nothing, because the rows are only
        // inserted if the tree Steam renders contains the section they attach to — and on Windows
        // Steam does not render the SteamOS-gated performance blocks at all. Reporting the append
        // outcome is what separates "the host did not run" from "the host ran and found nowhere to
        // put it".
        await LogAppendOutcomeAsync(context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:true,absent:true});"
            + "const removed=bridge.remove("
            + SteamCef.JsString(_componentKind)
            + ");const status=bridge.status("
            + SteamCef.JsString(_componentKind)
            + ");return JSON.stringify({ok:removed.ok&&!status.registered});})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            "Native-QAM component removal failed.",
            cancellationToken);
    }

    /// <summary>Reports what the last row-insertion attempt actually achieved.</summary>
    /// <param name="context">The live patch context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// Read-only, and deliberately best-effort: a diagnostic that could fail verification would
    /// make the log a liability. Keyed per row through <see cref="SteamUiLog.Change"/>, so a steady
    /// outcome is stated once and a change in it is stated again.
    /// </remarks>
    private async Task LogAppendOutcomeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            string expression = "(()=>{const b=window[" + SteamCef.JsString(BridgeNamespace)
                + "];const g=b&&b.gate?b.gate('nativeComponents'):null;"
                + "if(!g)return JSON.stringify({error:'bridge unavailable'});"
                + "const s=g.status(" + SteamCef.JsString(_componentKind) + ");"
                + "return JSON.stringify({append:s.lastAppend||{never:true},"
                + "rows:s.renderOutcomes,toggle:s.toggleResolved});})()";
            SteamUiEvaluationResult evaluation = await context.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                expression,
                cancellationToken).ConfigureAwait(false);
            if (!evaluation.Reachable || evaluation.Value is null)
            {
                return;
            }

            SteamUiLog.Change(
                "steam.ui.append." + Id,
                $"Native-QAM rows for {Id}: {evaluation.Value}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SteamUiLog.Change(
                "steam.ui.append.error." + Id,
                $"Native-QAM row report failed: {ex.Message}");
        }
    }
}
