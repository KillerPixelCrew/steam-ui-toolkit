using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A device power-preset dropdown alongside the Windows power-profile row.</summary>
/// <remarks>Uses the same typed state and backend as <see cref="SteamPowerProfileRow"/>.
/// An empty option list hides this optional row. Hosts may publish a current-only "custom" option;
/// it is a reading, never a selectable command.</remarks>
public static class SteamPowerPresetRow
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.power-preset";

    /// <summary>The row's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setPowerPreset"];

    /// <summary>Reversible registration with the shared Performance row host.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "powerPreset", "native-qam-power-preset-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_power_preset_probe_");

    /// <summary>Declares the optional device-preset row.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads current state.</param>
    /// <param name="backend">Applies an offered preset.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, Func<ValueTask<SteamPowerProfileState?>> read,
        ISteamPowerProfileBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule("power-preset", patches: [Patch],
            publications: [SteamSurfaceModule.Publication(
                PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPowerProfileState)],
            commands: [new(PatchId, "setPowerPreset", (request, token) =>
                SteamUiPayload.TryReadTarget(request.Payload, out string option) && option != "custom"
                    ? backend.SetPowerProfileAsync(option, token)
                    : SteamSurfaceModule.Invalid("The power-preset payload is invalid."))]);
    }
}
