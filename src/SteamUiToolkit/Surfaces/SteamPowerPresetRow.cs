using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Observed device preset and independent AC/battery assignments.</summary>
/// <param name="Available">Whether assignments may be changed.</param>
/// <param name="Options">Selectable device presets.</param>
/// <param name="Current">Observed preset label, including Custom when appropriate.</param>
/// <param name="StatusText">Current status or failure.</param>
/// <param name="Ac">AC assignment ID, or empty for no local assignment.</param>
/// <param name="Battery">Battery assignment ID, or empty for no local assignment.</param>
/// <param name="Scope">Human-readable assignment scope.</param>
/// <param name="UnsetLabel">Label for clearing a local assignment or inheriting the global value.</param>
public sealed record SteamPowerPresetState(bool Available, IReadOnlyList<SteamPowerProfileOption> Options,
    string Current, string StatusText, string Ac, string Battery, string Scope, string UnsetLabel);

/// <summary>Saves power-source assignments through the host's existing policy.</summary>
public interface ISteamPowerPresetBackend
{
    /// <summary>Changes one assignment.</summary>
    /// <param name="ac">True selects the AC assignment; false selects battery.</param>
    /// <param name="option">Preset ID, or null to clear the local assignment.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome or refusal.</returns>
    Task<SteamUiCommandResult> SetAssignmentAsync(bool ac, string? option, CancellationToken cancellationToken);
}

/// <summary>Power-source assignments and observed preset status on the Performance tab.</summary>
public static class SteamPowerPresetRow
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.power-preset";
    /// <summary>The row's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setAcPowerPreset", "setBatteryPowerPreset"];
    /// <summary>Reversible registration with the shared Performance row host.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "powerPreset", "native-qam-power-preset-v2:performance-actions+performance-root+valve-dropdown",
        "steam_ui_power_preset_probe_");

    /// <summary>Declares the assignment controls.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads current state.</param>
    /// <param name="backend">Saves assignments.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, Func<ValueTask<SteamPowerPresetState?>> read,
        ISteamPowerPresetBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule("power-preset", patches: [Patch],
            publications: [SteamSurfaceModule.Publication(
                PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPowerPresetState)],
            commands: [
                new(PatchId, "setAcPowerPreset", (request, token) => Apply(request, true, token)),
                new(PatchId, "setBatteryPowerPreset", (request, token) => Apply(request, false, token))]);

        Task<SteamUiCommandResult> Apply(SteamUiBridgeRequest request, bool ac, CancellationToken token)
        {
            JsonElement payload = request.Payload;
            if (SteamUiPayload.HasExactly(payload, 1)
                && payload.TryGetProperty("target", out JsonElement target)
                && target.ValueKind == JsonValueKind.Null)
            {
                return backend.SetAssignmentAsync(ac, null, token);
            }

            return SteamUiPayload.TryReadTarget(payload, out string option) && option != "custom"
                ? backend.SetAssignmentAsync(ac, option, token)
                : SteamSurfaceModule.Invalid("The power-preset payload is invalid.");
        }
    }
}
