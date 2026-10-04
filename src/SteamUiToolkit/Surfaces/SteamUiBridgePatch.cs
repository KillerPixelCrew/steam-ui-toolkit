using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     Installs only the narrow bridge every gate and row lives in. It deliberately does not alter a
///     Windows, SteamOS, device, capability, or component gate.
/// </summary>
/// <remarks>
///     Register this in the same manager as every dependent gate and row patch. The manager applies
///     the bridge before every other patch and removes it after them, so consumers must not rely on
///     registration call order. A consumer's kill-switch policy usually keeps the bridge enabled for
///     as long as any surface is wanted. Its probe asks only what the bridge itself needs, webpack and
///     React; each surface probes its own modules, so a Steam build that moves one Quick Access module
///     takes only the rows that use it.
/// </remarks>
public sealed class SteamUiBridgePatch : ISteamUiPatch
{
    /// <summary>The bootstrap's stable patch id.</summary>
    public const string PatchId = "steam-ui.bridge";

    private const string StructuralFingerprint = "steam-ui-bridge-v1:webpack+react";

    private static readonly string VerifyExpression =
        "(()=>{const b=window." + SteamUiBridgeIdentity.Namespace + ";"
        + "return JSON.stringify({ok:!!b&&b.version===" + SteamUiBridgeHost.SchemaVersion
        + ",version:b&&b.version});})()";

    private const string RemoveExpression =
        "JSON.stringify({ok:!window." + SteamUiBridgeIdentity.Namespace + "})";

    // The preamble resolves webpack; a throw there answers {error} through Close.
    private static readonly string ProbeExpression = $$"""
                                                       {{SteamUiProbeJs.Preamble("steam_ui_bridge_probe_")}}
                                                         return JSON.stringify({
                                                           react:count({{SteamUiProbeJs.ReactTokens}})
                                                         });
                                                       {{SteamUiProbeJs.Close}}
                                                       """;

    private readonly SteamUiBridgeHost _bridge;

    /// <summary>Creates the bootstrap patch around its owned bridge.</summary>
    /// <param name="bridge">The versioned narrow Runtime-binding bridge.</param>
    public SteamUiBridgePatch(SteamUiBridgeHost bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <inheritdoc />
    public string Id => PatchId;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateProbeAsync(
            context,
            TargetRole,
            ProbeExpression,
            static root => SteamUiPatchEvaluation.IsOne(root, "react"),
            StructuralFingerprint,
            "SharedJSContext is unavailable.",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        var (ready, error) = await _bridge.BootstrapWithReasonAsync(cancellationToken).ConfigureAwait(false);
        return ready
            ? new SteamUiPatchOperationResult(true, null)
            : new SteamUiPatchOperationResult(false, "Steam UI bridge handshake failed: " + error);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        return _bridge.IsReady
            ? SteamUiPatchEvaluation.EvaluateOutcomeAsync(
                context,
                TargetRole,
                VerifyExpression,
                "Bridge verification failed.",
                cancellationToken)
            : Task.FromResult(
                new SteamUiPatchOperationResult(false, "Steam UI bridge is not ready."));
    }

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
