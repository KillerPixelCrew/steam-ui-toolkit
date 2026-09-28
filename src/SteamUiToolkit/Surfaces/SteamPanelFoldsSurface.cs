using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Which sections of the Performance and Quick Settings tabs are folded.</summary>
/// <remarks>
///     A section is named by its title, the one the panel draws: "Power profiles", "Display and
///     frame rate", "Power limits", "Controller", "Display", "Charging" and "RGB lighting". The
///     injected host folds a section the moment it is asked and keeps that for the session; this
///     state is what makes the fold outlive Steam rebuilding the tab.
/// </remarks>
/// <param name="Folded">The titles of the folded sections, at most 256 of up to 96 characters.</param>
public sealed record SteamPanelFoldsState(IReadOnlyList<string> Folded);

/// <summary>Keeps the folds of the panel's sections.</summary>
public interface ISteamPanelFoldsBackend
{
    /// <summary>Folds or unfolds one section.</summary>
    /// <param name="id">The section's title.</param>
    /// <param name="folded">Whether it should be folded.</param>
    /// <param name="cancellationToken">Cancels waiting without implying the change was undone.</param>
    /// <returns>A truthful result; the host publishes the new fold list.</returns>
    Task<SteamUiCommandResult> SetFoldedAsync(string id, bool folded, CancellationToken cancellationToken);
}

/// <summary>The fold list read by the Performance and Quick Settings panel roots.</summary>
public static class SteamPanelFoldsSurface
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.panel-folds";

    /// <summary>The surface's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setFolded"];

    /// <summary>Reversible registration with the shared panel host. It mounts no row of its own.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "panelFolds",
        "native-qam-panel-folds-v1:performance-actions+performance-root",
        "steam_ui_panel_folds_probe_");

    /// <summary>Serializes state for the injected host.</summary>
    /// <param name="state">State to publish.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPanelFoldsState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPanelFoldsState);
    }

    /// <summary>Declares the patch, state publication and command handler.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads the current fold list.</param>
    /// <param name="backend">Keeps the folds.</param>
    /// <param name="id">Module identity.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled, Func<ValueTask<SteamPanelFoldsState?>> read,
        ISteamPanelFoldsBackend backend, string id = "panel-folds")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id, PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPanelFoldsState, [Patch],
            [
                SteamSurfaceModule.Command<(string Id, bool Folded)>(
                    PatchId,
                    "setFolded",
                    TryReadFold,
                    (value, token) => backend.SetFoldedAsync(value.Id, value.Folded, token),
                    "The panel fold payload is invalid.")
            ]);
    }

    private static bool TryReadFold(JsonElement payload, out (string Id, bool Folded) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadBoundedString(payload, "id", 96, out var id)
            || !SteamUiPayload.TryReadBoolean(payload, "folded", out var folded)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (id, folded);
        return true;
    }
}
