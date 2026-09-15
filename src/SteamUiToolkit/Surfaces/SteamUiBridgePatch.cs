using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
/// Probes the live native performance/QAM structure and installs only the narrow bridge.
/// It deliberately does not alter a Windows, SteamOS, device, capability, or component gate.
/// </summary>
/// <remarks>
/// Register this in the same manager as every dependent gate and row patch. The manager orders
/// synchronization by stable patch id and retries unmet conditions, so consumers must not rely on
/// registration call order. A consumer's kill-switch policy usually keeps the bridge enabled for
/// as long as any surface is wanted. Its fingerprint requires the Quick Access performance
/// structure to be present and unique, which is the structure the surfaces in this library were
/// verified against.
/// </remarks>
public sealed class SteamUiBridgePatch : ISteamUiPatch
{
    /// <summary>The bootstrap's stable patch id.</summary>
    public const string PatchId = "steam-ui.bridge";

    private const string StructuralFingerprint =
        "qam-v1:tdp-availability+tdp-component+perf-actions+profile-readonly";
    private const string VerifyExpression =
        "(()=>{const b=window." + SteamUiBridgeIdentity.Namespace + ";"
        + "return JSON.stringify({ok:!!b&&b.version===1,version:b&&b.version});})()";
    private const string RemoveExpression =
        "JSON.stringify({ok:!window." + SteamUiBridgeIdentity.Namespace + "})";

    // Live-probed 2026-08-28 against the current Windows Steam SharedJSContext:
    // each conjunction identifies exactly one module. Module ids are intentionally
    // not retained because they are build output, not compatibility evidence.
    private static readonly string ProbeExpression = $$"""
        {{SteamUiProbeJs.Preamble("steam_ui_bridge_probe_")}}
          return JSON.stringify({
            tdpAvailability:count(['is_tdp_limit_available','steamos_tdp_limit_enabled','tdp_limit_min','tdp_limit_max']),
            tdpComponent:count({{SteamUiProbeJs.Tokens(SteamUiProbeJs.TdpPresentationTokens)}}),
            performanceActions:count({{SteamUiProbeJs.Tokens(SteamUiProbeJs.PerformanceActionTokens)}}),
            profileProjection:count(['#PlatformPerformanceProfile_Label','steamos_platform_performance_profile','rgOptions'])
          });
        {{SteamUiProbeJs.Close}}
        """;

    private readonly SteamUiBridgeHost _bridge;

    /// <summary>Creates the bootstrap patch around its owned bridge.</summary>
    /// <param name="bridge">The versioned narrow Runtime-binding bridge.</param>
    public SteamUiBridgePatch(SteamUiBridgeHost bridge) =>
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    /// <inheritdoc />
    public string Id => PatchId;

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "steam-ui.bridge-binding";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <inheritdoc />
    public Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken) =>
        SteamGatePatch.ProbeAsync(
            context,
            ProbeExpression,
            static root => SteamUiPatchEvaluation.IsOne(root, "tdpAvailability")
                && SteamUiPatchEvaluation.IsOne(root, "tdpComponent")
                && SteamUiPatchEvaluation.IsOne(root, "performanceActions")
                && SteamUiPatchEvaluation.IsOne(root, "profileProjection"),
            StructuralFingerprint,
            cancellationToken);

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken) =>
        await _bridge.BootstrapAsync(cancellationToken).ConfigureAwait(false)
            ? new SteamUiPatchOperationResult(true, null)
            : new SteamUiPatchOperationResult(false, "Native-QAM bridge handshake failed.");

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken) =>
        _bridge.IsReady
            ? SteamUiPatchEvaluation.EvaluateOutcomeAsync(
                context,
                TargetRole,
                VerifyExpression,
                "Bridge verification failed.",
                cancellationToken)
            : Task.FromResult(
                new SteamUiPatchOperationResult(false, "Native-QAM bridge is not ready."));

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        await _bridge.RemoveAsync(cancellationToken).ConfigureAwait(false);
        return await SteamUiPatchEvaluation.EvaluateOutcomeAsync(
                context,
                TargetRole,
                RemoveExpression,
                "Bridge resource remains present.",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
