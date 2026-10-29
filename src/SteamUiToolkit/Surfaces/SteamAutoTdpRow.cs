using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>An automatic power-limit controller as the row renders it.</summary>
/// <remarks>
///     Deliberately more than a boolean. A switch that only says "on" leaves a user watching the power
///     limit move with no way to tell control from a fault, so the state carries what the controller
///     is actually doing: the watts it settled on, whether it is controlling, and why not when it is
///     not.
/// </remarks>
/// <param name="Available">Whether the switch may be operated at all. False hides the row.</param>
/// <param name="Enabled">The stored setting, which is what the switch shows.</param>
/// <param name="Controlling">Whether the controller is currently moving the power limit.</param>
/// <param name="Watts">The limit it settled on, 1-200, when it has one. Shown beside the switch while controlling.</param>
/// <param name="Progress">
///     Command progress; the row disables itself while <c>queued</c>, <c>applying</c> or
///     <c>replacing</c>.
/// </param>
/// <param name="StatusText">One line describing what it is doing, or why it cannot.</param>
/// <param name="Target">
///     The frame rate the controller holds the game to, drawn as a slider under the switch while it is
///     on; null when the backend has no target of its own to offer.
/// </param>
public sealed record SteamAutoTdpState(
    bool Available,
    bool Enabled,
    bool Controlling,
    int? Watts,
    string Progress,
    string StatusText,
    SteamAutoTdpTarget? Target = null);

/// <summary>The frame-rate target an automatic power-limit controller aims for.</summary>
/// <param name="Fps">The target in force.</param>
/// <param name="MinimumFps">Lowest target the slider offers.</param>
/// <param name="MaximumFps">Highest target the slider offers.</param>
/// <param name="StatusText">One line under the slider, such as where the value comes from.</param>
/// <param name="Accent">
///     Whether the row is marked: its description is drawn in Steam's accent colour after the host's
///     <see cref="SteamQuickAccessLayout.AccentLabel" />.
/// </param>
public sealed record SteamAutoTdpTarget(
    int Fps,
    int MinimumFps,
    int MaximumFps,
    string StatusText,
    bool Accent = false);

/// <summary>What answers the automatic power-limit switch.</summary>
public interface ISteamAutoTdpBackend
{
    /// <summary>Stores the setting.</summary>
    /// <param name="enabled">
    ///     The wanted state. Re-sending the current value is harmless: the page
    ///     and the store can disagree for one frame after a change made somewhere else.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Stores the frame-rate target.</summary>
    /// <param name="fps">The wanted target, within the published bounds.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken);
}

/// <summary>
///     An "Automatic TDP" toggle on the Performance tab, placed with the power limit it moves, and the
///     frame-rate target slider it shows while it is on.
/// </summary>
/// <remarks>
///     Not a SteamOS feature; a toggle and slider built on Valve's fields so an automatic controller
///     reads as native. Its probe requires the same TDP presentation Valve's power-limit rows do: with no
///     native power limit there is nothing for it to sit beside, and nothing for it to drive. Valve
///     has no string for it, so the label is English.
/// </remarks>
public static class SteamAutoTdpRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.auto-tdp";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setAutoTdp", "setAutoTdpTarget"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "autoTdp",
        "steam-ui-auto-tdp-v2:presentation+performance-root+valve-toggle+valve-slider",
        "steam_ui_auto_tdp_probe_",
        "tdpPresentation",
        SteamUiProbeJs.TdpPresentationTokens);

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamAutoTdpState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamAutoTdpState);
    }

    /// <summary>Declares the row as one module: the patch, the state, and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What stores the setting and the target.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamAutoTdpState?>> read,
        ISteamAutoTdpBackend backend,
        string id = "auto-tdp")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamAutoTdpState,
            [Patch],
            [
                SteamUiModuleBuilder.Command<bool>(
                    PatchId,
                    "setAutoTdp",
                    SteamUiPayload.TryReadEnabled,
                    backend.SetAutoTdpAsync,
                    "The AutoTDP payload is invalid."),
                SteamUiModuleBuilder.Command<int>(
                    PatchId,
                    "setAutoTdpTarget",
                    TryReadTarget,
                    backend.SetAutoTdpTargetAsync,
                    "The AutoTDP target payload is invalid.")
            ]);
    }

    /// <summary>Reads the target write's shape: <c>{value}</c>, a positive integer, and nothing else.</summary>
    private static bool TryReadTarget(JsonElement payload, out int value)
    {
        return SteamUiPayload.TryReadInt(payload, "value", 1, int.MaxValue, out value)
               && SteamUiPayload.HasExactly(payload, 1);
    }
}
