using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Which sections of Steam's Quick Access tabs are open.</summary>
/// <remarks>
///     Every section starts folded, so the state lists the ones the user opened. The injected side
///     names a section: a Performance or Quick Settings group by its title ("Power profiles",
///     "Display and frame rate", "Power limits", "Controller", "Display", "Charging", "RGB
///     lighting"), an Extensions tab item as <c>extensions:&lt;item&gt;</c> and a switch's settings
///     under it as <c>extensions:&lt;item&gt;:&lt;key&gt;</c>. The host keeps the ids as given. The
///     injected side folds or opens a section the moment it is asked and keeps that until this state
///     agrees, which is what makes a fold outlive Steam rebuilding a tab.
/// </remarks>
/// <param name="Open">The open sections' ids.</param>
public sealed record SteamPanelFoldsState(IReadOnlyList<string> Open);

/// <summary>Keeps the folds of the Quick Access tabs' sections.</summary>
public interface ISteamPanelFoldsBackend
{
    /// <summary>Folds or opens one section.</summary>
    /// <param name="id">The section's id, as the injected side names it.</param>
    /// <param name="folded">Whether it should be folded.</param>
    /// <param name="cancellationToken">Cancels waiting without implying the change was undone.</param>
    /// <returns>A truthful result; the host publishes the new list.</returns>
    Task<SteamUiCommandResult> SetFoldedAsync(string id, bool folded, CancellationToken cancellationToken);
}

/// <summary>The open-section list every Quick Access tab reads. It mounts nothing of its own.</summary>
public static class SteamPanelFoldsSurface
{
    /// <summary>Identity for state and commands.</summary>
    public const string PatchId = "steam-ui.panel-folds";

    /// <summary>The surface's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setFolded"];

    /// <summary>Serializes state for the injected side.</summary>
    /// <param name="state">State to publish.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPanelFoldsState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPanelFoldsState);
    }

    /// <summary>Declares the state publication and command handler. There is no patch: the tabs' own gates draw the folds.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads the currently open sections.</param>
    /// <param name="backend">Keeps the folds.</param>
    /// <param name="id">Module identity.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled, Func<ValueTask<SteamPanelFoldsState?>> read,
        ISteamPanelFoldsBackend backend, string id = "panel-folds")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id, PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPanelFoldsState, [],
            [
                SteamUiModuleBuilder.Command<(string Id, bool Folded)>(
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
        if (!SteamUiPayload.TryReadNonBlankString(payload, "id", out var id)
            || !SteamUiPayload.TryReadBoolean(payload, "folded", out var folded)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (id, folded);
        return true;
    }
}
