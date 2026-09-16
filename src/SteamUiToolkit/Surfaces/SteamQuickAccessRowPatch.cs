using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     Shared bounded lifecycle for one independently versioned row mounted by the injected component
///     host into Valve's Performance or Quick Settings panel.
/// </summary>
/// <remarks>
///     Every row differs only in its declaration: id, compiled component kind, fingerprint, probe chunk
///     label, and — for the rows that do not ride the performance-actions module — the factory tokens
///     that uniquely identify their own module. Which kinds exist, whether each is Valve's own
///     reactivated export or is hand-built on Valve's field primitives, and why, is decided in
///     <c>SteamUiAssets\Source\components.ts</c>, where each kind's factory carries that rationale.
///     The declarations live on the surface that owns each row.
/// </remarks>
public sealed class SteamQuickAccessRowPatch : ISteamUiPatch
{
    private const string NativeComponents = "'nativeComponents'";
    private const string VerifyFallback = "Native-QAM component verification failed.";

    private static readonly string[] CommonRequiredCounts =
    [
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react"
    ];

    private readonly string _applyExpression;

    private readonly string _fingerprint;
    private readonly string _primaryCountName;
    private readonly string _probeExpression;
    private readonly string _removeExpression;
    private readonly string _verifyExpression;

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
        ComponentKind = componentKind;
        _fingerprint = fingerprint;
        _primaryCountName = primaryCountName;

        // Read-only structural probe shared by every row. Built once: the declaration never changes
        // and the manager probes every row on each synchronization.
        _probeExpression = $$"""
                             {{SteamUiProbeJs.Preamble(chunkLabel)}}
                               return JSON.stringify({
                                 {{primaryCountName}}:count({{SteamUiProbeJs.Tokens(primaryTokens ?? SteamUiProbeJs.PerformanceActionTokens)}}),
                                 performanceRoot:count(['#QuickAccess_Tab_Perf_Common_Settings','#QuickAccess_Tab_Perf_BatteryTimeRemaining','TS.ON_FRAME']),
                                 nativeFields:count({{SteamUiProbeJs.NativeFieldTokens}}),
                                 nativeLayout:count(['PanelSectionTitle','PanelSectionRow','spinner']),
                                 localization:count({{SteamUiProbeJs.LocalizationTokens}}),
                                 react:count({{SteamUiProbeJs.ReactTokens}})
                               });
                             {{SteamUiProbeJs.Close}}
                             """;
        var kind = SteamCef.JsString(componentKind);
        _applyExpression = SteamGatePatch.GateExpression(
            NativeComponents,
            "return JSON.stringify(bridge.install(" + kind + "));");
        _verifyExpression = SteamGatePatch.GateExpression(
            NativeComponents,
            "const status=bridge.status(" + kind + ");return JSON.stringify({ok:status.ok"
            + "&&status.registered&&status.hostVersion===1&&status.performanceRootWrapped,status});");
        _removeExpression = SteamGatePatch.GateExpression(
            NativeComponents,
            "const removed=bridge.remove(" + kind + ");const status=bridge.status(" + kind + ");"
            + "return JSON.stringify({ok:removed.ok&&!status.registered});",
            "return JSON.stringify({ok:true,absent:true});");
    }

    /// <summary>The compiled kind the injected host installs for this row.</summary>
    public string ComponentKind { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <summary>One key for every row, so the mounted set serializes on one gate.</summary>
    public string ResourceKey => "steam-ui.performance-root";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <inheritdoc />
    public Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamGatePatch.ProbeAsync(
            context, _probeExpression, IsCompatible, _fingerprint, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _applyExpression,
            "Native-QAM component installation failed.",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _verifyExpression,
            VerifyFallback,
            LogAppendOutcome,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _removeExpression,
            "Native-QAM component removal failed.",
            cancellationToken);
    }

    private bool IsCompatible(JsonElement root)
    {
        var unique = SteamUiPatchEvaluation.IsOne(root, _primaryCountName);
        foreach (var property in CommonRequiredCounts)
        {
            unique &= SteamUiPatchEvaluation.IsOne(root, property);
        }

        return unique;
    }

    /// <summary>Reports what the last row-insertion attempt actually achieved.</summary>
    /// <param name="root">The verification result, carrying the host's <c>status</c>.</param>
    /// <param name="error">The page's own error, when verification could not reach the host.</param>
    /// <remarks>
    ///     Verification asks whether the component registered and the performance root is wrapped. Both
    ///     can be true while the Quick Access panel shows nothing, because the rows are only inserted if
    ///     the tree Steam renders contains the section they attach to, and on Windows Steam does not
    ///     render the SteamOS-gated performance blocks at all. Reporting the append outcome is what
    ///     separates "the host did not run" from "the host ran and found nowhere to put it".
    ///     <para>
    ///         Read from the status verification already returned rather than asked for again, in the shape
    ///         the report has always had: <c>append</c> (or <c>{"never":true}</c>), <c>rows</c> and
    ///         <c>toggle</c>. Keyed per row through <see cref="SteamUiLog.Change" />, so a steady outcome is
    ///         stated once and a change in it is stated again.
    ///     </para>
    /// </remarks>
    private void LogAppendOutcome(JsonElement root, string? error)
    {
        string report;
        if (root.TryGetProperty("status", out var status))
        {
            report = "{\"append\":"
                     + (status.ValueKind == JsonValueKind.Object
                        && status.TryGetProperty("lastAppend", out var append)
                        && IsTruthy(append)
                         ? append.GetRawText()
                         : "{\"never\":true}");
            if (status.ValueKind == JsonValueKind.Object)
            {
                if (status.TryGetProperty("renderOutcomes", out var rows))
                {
                    report += ",\"rows\":" + rows.GetRawText();
                }

                if (status.TryGetProperty("toggleResolved", out var toggle))
                {
                    report += ",\"toggle\":" + toggle.GetRawText();
                }
            }

            report += "}";
        }
        else if (error == "bridge unavailable")
        {
            report = "{\"error\":\"bridge unavailable\"}";
        }
        else
        {
            return;
        }

        SteamUiLog.Change("steam.ui.append." + Id, $"Native-QAM rows for {Id}: {report}");
    }

    private static bool IsTruthy(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
            JsonValueKind.Number => value.GetDouble() != 0,
            JsonValueKind.String => value.GetString() is { Length: > 0 },
            _ => true
        };
    }
}
