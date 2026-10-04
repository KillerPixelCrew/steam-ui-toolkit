using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One section of a Quick Access tab, as the host lays it out.</summary>
/// <param name="Id">
///     The section's stable id, which is also the id its fold is kept under in
///     <see cref="SteamPanelFoldsState" />.
/// </param>
/// <param name="Title">The heading, or empty for a section drawn without one.</param>
/// <param name="Icon">The name of the toolkit glyph beside the heading, or empty for none.</param>
/// <param name="Folds">Whether the section folds under its heading; false keeps it open.</param>
/// <param name="Kinds">
///     The row kinds drawn in it, such as <c>frameLimit</c> or <c>powerLimit</c>. Rows keep the
///     toolkit's own order within a section; a kind no section names is not drawn while a layout is
///     published. The device controls' two groups are the kinds <c>charging</c> and <c>lighting</c>;
///     they draw after Steam's own Quick Settings sections, in whichever Quick Settings section names
///     each.
/// </param>
public sealed record SteamQuickAccessSection(
    string Id,
    string Title,
    string Icon,
    bool Folds,
    IReadOnlyList<string> Kinds);

/// <summary>How the host lays out the rows it adds to Steam's Quick Access tabs.</summary>
/// <remarks>
///     The toolkit holds no section, heading or label of its own: without a layout every row of a tab
///     is drawn in one untitled group. Generic row labels, such as a slider's name, stay the toolkit's.
/// </remarks>
/// <param name="Performance">
///     The Performance tab's sections, drawn in order after Steam's battery line and before any host
///     settings sections.
/// </param>
/// <param name="PerformanceEnd">The Performance tab's sections drawn after the host settings sections.</param>
/// <param name="QuickSettings">The Quick Settings tab's sections, drawn in order ahead of Steam's own.</param>
/// <param name="QuickSettingsEnd">The Quick Settings tab's sections drawn after Steam's own.</param>
/// <param name="HideValveFpsRows">
///     Whether Steam's own frame-rate counter rows are hidden on the Performance tab, for a host whose
///     own overlay replaces them. False leaves them untouched.
/// </param>
/// <param name="AccentLabel">
///     What a marked row's description starts with, such as the host's word for a value the running
///     game's own profile supplies; empty to only colour the description.
/// </param>
public sealed record SteamQuickAccessLayout(
    IReadOnlyList<SteamQuickAccessSection> Performance,
    IReadOnlyList<SteamQuickAccessSection> PerformanceEnd,
    IReadOnlyList<SteamQuickAccessSection> QuickSettings,
    IReadOnlyList<SteamQuickAccessSection> QuickSettingsEnd,
    bool HideValveFpsRows = false,
    string AccentLabel = "");

/// <summary>Publishes the host's Quick Access layout. It mounts nothing of its own.</summary>
public static class SteamQuickAccessLayoutSurface
{
    /// <summary>Identity of the layout publication.</summary>
    public const string PatchId = "steam-ui.quick-access-layout";

    /// <summary>Serializes a layout exactly as the module publishes it.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamQuickAccessLayout layout)
    {
        return JsonSerializer.SerializeToElement(layout, SteamSurfaceJsonContext.Default.SteamQuickAccessLayout);
    }

    /// <summary>Declares the layout publication. There is no patch and no command: the tabs' rows read it.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads the layout, or null to publish nothing this round.</param>
    /// <param name="id">Module identity.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamQuickAccessLayout?>> read,
        string id = "quick-access-layout")
    {
        return SteamUiModuleBuilder.Module(
            id, PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamQuickAccessLayout, [], []);
    }
}
