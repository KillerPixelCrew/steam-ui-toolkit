using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One host-owned command added to a game's Steam context menu.</summary>
/// <param name="Id">Stable command identity, unique in one publication.</param>
/// <param name="Label">Plain visible menu text.</param>
public sealed record SteamGameContextMenuItem(string Id, string Label);

/// <summary>The host-owned commands exposed for every applicable game context menu.</summary>
/// <param name="Items">Commands in their rendered order.</param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamGameContextMenuState(IReadOnlyList<SteamGameContextMenuItem> Items, long Revision = 0);

/// <summary>Answers an explicit game-context-menu command.</summary>
public interface ISteamGameContextMenuBackend
{
    /// <summary>Handles one command for the exact game whose menu supplied it.</summary>
    /// <param name="appId">Steam's positive numeric application id.</param>
    /// <param name="id">The published command identity.</param>
    /// <param name="cancellationToken">Cancels the user-initiated operation.</param>
    /// <returns>A truthful result, including a refusal reason when the command is unavailable.</returns>
    Task<SteamUiCommandResult> ActivateAsync(uint appId, string id, CancellationToken cancellationToken);
}

/// <summary>
/// Adds host-owned commands to Steam's per-game gear and library context menu.
/// </summary>
/// <remarks>
/// Steam's stable module export supplies the menu shell, while the class that renders it is private
/// to that module. The shared JSX interceptor recognizes that exact class before its first render
/// and claims its render method. It never constructs an unknown export or accepts a
/// package-supplied React element.
/// </remarks>
public static class SteamGameContextMenuSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.game-context-menu";

    /// <summary>The exact command vocabulary the injected menu sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["activate"];

    /// <summary>The game context menu patch.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.jsx-runtime",
        "gameContextMenu",
        "steam-game-context-menu-v5:unique-library-menu-module+jsx-class-claim",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_game_context_menu_probe_")}}
            const menu=req.findUnique(['GetTargetApps','BuildManageSubmenu','GetPrimaryActionMenuItem']);
            if(!menu)return JSON.stringify({menuModule:0});
            return JSON.stringify({
              menuModule:1,
              react:count({{SteamUiProbeJs.ReactTokens}}),
              jsx:count(['react.transitional.element','.jsx','.jsxs'])
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "menuModule")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "jsx"),
        "status.installed&&status.resolved&&status.observing",
        "!status.installed",
        "Game context menu");

    /// <summary>Serializes the state exactly as the injected menu reads it.</summary>
    /// <param name="state">The current command list.</param>
    /// <returns>The camel-case bridge payload.</returns>
    public static JsonElement Serialize(SteamGameContextMenuState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamGameContextMenuState);
    }

    /// <summary>Declares the menu, its publication and its typed activation command.</summary>
    /// <param name="enabled">Whether the commands may be installed and published right now.</param>
    /// <param name="read">The current commands, or null when no observation is available.</param>
    /// <param name="backend">The host action backend.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The complete module declaration.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamGameContextMenuState?>> read,
        ISteamGameContextMenuBackend backend,
        string id = "game-context-menu")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamGameContextMenuState,
            [Patch],
            [
                SteamSurfaceModule.Command<(uint AppId, string Id)>(
                    PatchId,
                    "activate",
                    TryReadActivation,
                    (activation, cancellationToken) => backend.ActivateAsync(
                        activation.AppId, activation.Id, cancellationToken),
                    "The game context menu activation payload is invalid.")
            ]);
    }

    private static bool TryReadActivation(JsonElement payload, out (uint AppId, string Id) activation)
    {
        activation = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("appId", out var appIdProperty)
            || appIdProperty.ValueKind != JsonValueKind.Number
            || !appIdProperty.TryGetUInt32(out var appId)
            || appId == 0
            || !SteamUiPayload.TryReadBoundedString(payload, "id", 96, out var id)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        activation = (appId, id);
        return true;
    }
}
