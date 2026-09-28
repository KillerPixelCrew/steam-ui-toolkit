using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Whether Steam's power menu offers its Switch to Desktop entry.</summary>
/// <param name="Visible">True while the host has a desktop to switch to.</param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamPowerMenuState(bool Visible, long Revision = 0);

/// <summary>Answers Steam's Switch to Desktop entry.</summary>
public interface ISteamPowerMenuBackend
{
    /// <summary>Starts the switch to the desktop the user selected in Steam's power menu.</summary>
    /// <param name="cancellationToken">Cancels the user-initiated operation.</param>
    /// <returns>A truthful result, including a refusal reason when no switch can start.</returns>
    Task<SteamUiCommandResult> SwitchToDesktopAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Revives Switch to Desktop in Steam's Big Picture power menu and hands its selection to the host.
/// </summary>
/// <remarks>
///     Valve draws the entry only under gamescope and answers it with SteamOS's session service,
///     which does nothing on Windows. The menu is a module-private observer, so the gate appends the
///     entry where the menu's root passes through the shared JSX interceptor, built from the item and
///     separator types that menu rendered and labelled with Steam's own localized string. The module
///     is proved by <c>#Quit_Shutdown</c>, which occurs once in the client, beside the entry's own
///     token. Mapped against the installed client on 2026-09-28.
/// </remarks>
public static class SteamPowerMenuSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.power-menu";

    /// <summary>The exact command vocabulary the injected entry sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["switchToDesktop"];

    /// <summary>The power menu patch.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.jsx-runtime",
        "powerMenu",
        "steam-power-menu-v1:unique-power-menu-module+jsx-element-transform",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_power_menu_probe_")}}
            return JSON.stringify({
              menuModule:count(['#Quit_Shutdown','#SwitchToDesktop']),
              react:count({{SteamUiProbeJs.ReactTokens}}),
              jsx:count(['react.transitional.element','.jsx','.jsxs'])
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "menuModule")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "jsx"),
        "status.installed&&status.resolved&&status.claimed",
        "!status.installed",
        "Power menu");

    /// <summary>Serializes the state exactly as the injected entry reads it.</summary>
    /// <param name="state">The current visibility.</param>
    /// <returns>The camel-case bridge payload.</returns>
    public static JsonElement Serialize(SteamPowerMenuState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPowerMenuState);
    }

    /// <summary>Declares the entry, its publication and its command.</summary>
    /// <param name="enabled">Whether the gate may be installed and published right now.</param>
    /// <param name="read">The current visibility, or null when no observation is available.</param>
    /// <param name="backend">The host that performs the switch.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The complete module declaration.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamPowerMenuState?>> read,
        ISteamPowerMenuBackend backend,
        string id = "power-menu")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamPowerMenuState,
            [Patch],
            [
                SteamSurfaceModule.Command<bool>(
                    PatchId,
                    "switchToDesktop",
                    TryReadEmpty,
                    (_, cancellationToken) => backend.SwitchToDesktopAsync(cancellationToken),
                    "The power menu switch payload is invalid.")
            ]);
    }

    private static bool TryReadEmpty(JsonElement payload, out bool empty)
    {
        empty = SteamUiPayload.HasExactly(payload, 0);
        return empty;
    }
}
