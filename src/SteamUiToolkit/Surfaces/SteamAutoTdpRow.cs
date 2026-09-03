using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>An automatic power-limit controller as the row renders it.</summary>
/// <remarks>
/// Deliberately more than a boolean. A switch that only says "on" leaves a user watching the power
/// limit move with no way to tell control from a fault, so the state carries what the controller
/// is actually doing: the watts it settled on, whether it is controlling, and why not when it is
/// not.
/// </remarks>
/// <param name="Available">Whether the switch may be operated at all. False hides the row.</param>
/// <param name="Enabled">The stored setting, which is what the switch shows.</param>
/// <param name="Controlling">Whether the controller is currently moving the power limit.</param>
/// <param name="Watts">The limit it settled on, 1-200, when it has one. Shown beside the switch while controlling.</param>
/// <param name="Progress">Command progress; the row disables itself while <c>queued</c>, <c>applying</c> or <c>replacing</c>.</param>
/// <param name="StatusText">One line describing what it is doing, or why it cannot.</param>
public sealed record SteamAutoTdpState(
    bool Available,
    bool Enabled,
    bool Controlling,
    int? Watts,
    string Progress,
    string StatusText);

/// <summary>What answers the automatic power-limit switch.</summary>
public interface ISteamAutoTdpBackend
{
    /// <summary>Stores the setting.</summary>
    /// <param name="enabled">The wanted state. Re-sending the current value is harmless: the page
    /// and the store can disagree for one frame after a change made somewhere else.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>An "Automatic TDP" toggle on the Performance tab, placed with the power limit it moves.</summary>
/// <remarks>
/// Not a SteamOS feature; a toggle built on Valve's field so an automatic controller reads as
/// native. Its probe requires the same TDP presentation Valve's power-limit rows do: with no
/// native power limit there is nothing for it to sit beside, and nothing for it to drive. Valve
/// has no string for it, so the label is English.
/// </remarks>
public static class SteamAutoTdpRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.auto-tdp";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setAutoTdp"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "autoTdp",
        "native-qam-auto-tdp-v1:presentation+performance-root+valve-toggle",
        "steam_ui_auto_tdp_probe_",
        "tdpPresentation",
        [
            "#QuickAccess_Tab_Perf_TDPLimitEnabled",
            "steamos_tdp_limit",
            "showBookendLabels",
        ]);

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamAutoTdpState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamAutoTdpState);

    /// <summary>Declares the row as one module: the patch, the state, and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What stores the setting.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamAutoTdpState?>> read,
        ISteamAutoTdpBackend backend,
        string id = "auto-tdp")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamAutoTdpState),
            ],
            commands:
            [
                new(PatchId, "setAutoTdp", (request, cancellationToken) =>
                    SteamUiPayload.TryReadEnabled(request.Payload, out bool on)
                        ? backend.SetAutoTdpAsync(on, cancellationToken)
                        : SteamSurfaceModule.Invalid("The AutoTDP payload is invalid.")),
            ]);
    }
}
