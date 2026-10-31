using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One group of host rows in Quick Access, placed by the host's layout.</summary>
/// <param name="Id">
///     The group's stable id. A <see cref="SteamQuickAccessSection" /> that names it among its
///     <see cref="SteamQuickAccessSection.Kinds" /> draws the rows there, in the order the section lists
///     its kinds. A group no layout section names is drawn as a section of its own on the Performance
///     tab, after the layout's <see cref="SteamQuickAccessLayout.Performance" /> sections, when it has a
///     <paramref name="Title" />; without one it is not drawn while a layout is published. With no
///     layout at all every group draws on the Performance tab. The id of a titled group is also the
///     id its fold is kept under in <see cref="SteamPanelFoldsState" />.
/// </param>
/// <param name="Rows">The rows, in order, drawn with Steam's own fields.</param>
/// <param name="Title">
///     The heading of a group no layout section names, or null. Ignored for a group a layout section
///     places, which takes that section's heading.
/// </param>
/// <param name="Glyph">
///     SVG path data on a 24x24 grid drawn beside <paramref name="Title" />, or null.
/// </param>
/// <param name="Groups">
///     Titled or untitled sub-groups drawn after <paramref name="Rows" /> inside the same section, or
///     null.
/// </param>
public sealed record SteamQuickAccessRowSection(
    string Id,
    IReadOnlyList<SteamSettingsRow> Rows,
    string? Title = null,
    string? Glyph = null,
    IReadOnlyList<SteamSettingsSection>? Groups = null);

/// <summary>Every host row group drawn in Steam's Quick Access tabs.</summary>
/// <param name="Sections">The groups, in order.</param>
/// <param name="Revision">
///     Increases whenever the published rows change. A change releases settled drafts to the new
///     values; an unchanged revision keeps what is being edited.
/// </param>
public sealed record SteamQuickAccessRowsState(IReadOnlyList<SteamQuickAccessRowSection> Sections, long Revision);

/// <summary>Applies a change to one currently published Quick Access row.</summary>
public interface ISteamQuickAccessRowsBackend
{
    /// <summary>Validates and applies the change against the host's current rows.</summary>
    /// <param name="key">The published <see cref="SteamSettingsRow.Key" />.</param>
    /// <param name="value">
    ///     The edited value: a boolean, number, string or, for a curve row, an array of points. An action
    ///     row sends <c>true</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the command.</param>
    /// <returns>The truthful command outcome; a refusal's reason is shown on the row.</returns>
    Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken);
}

/// <summary>
///     Host rows in Quick Access, described by <see cref="SteamSettingsRow" /> and drawn by the injected
///     component host with Steam's own fields, and the one patch that mounts every Quick Access row.
/// </summary>
/// <remarks>
///     The component host is one gate. It wraps Steam's Performance and Quick Settings tabs and draws
///     every host row in them: the groups this surface publishes, the frame-limit row
///     (<see cref="SteamFrameLimitRow" />), the lighting editor (<see cref="SteamLightingZonesRow" />),
///     Valve's own performance components while <see cref="SteamPerformanceSurface" /> publishes, and the
///     plugin frontends' rows. Each draws only while its state is published; the host itself is
///     <see cref="HostPatch" />, registered by this module.
/// </remarks>
public static class SteamQuickAccessRowsSurface
{
    /// <summary>The state and command namespace of the rows.</summary>
    public const string PatchId = "steam-ui.quick-access-rows";

    /// <summary>The id of the component host's patch.</summary>
    public const string HostPatchId = "steam-ui.quick-access";

    /// <summary>The longest row key the command accepts.</summary>
    public const int MaximumKeyLength = 256;

    /// <summary>The longest text value the command accepts.</summary>
    public const int MaximumTextLength = 4096;

    /// <summary>The most curve points the command accepts.</summary>
    public const int MaximumArrayLength = 64;

    /// <summary>The exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["set"];

    /// <summary>The component host's patch: one probe, one install, for every Quick Access row.</summary>
    public static SteamGatePatch HostPatch { get; } = new(
        HostPatchId,
        "nativeComponents",
        "steam-ui-quick-access-v2:performance-root+valve-fields+valve-layout+localizer",
        "Quick Access component host",
        LogHostStatus);

    /// <summary>Reports what the host's last row insertion actually achieved.</summary>
    /// <param name="root">The verification result, carrying the host's <c>status</c>.</param>
    /// <param name="error">The page's own error, when verification could not reach the host.</param>
    /// <remarks>
    ///     Verification proves the host is installed and its tab claim held. Both can be true while a tab
    ///     shows nothing of the host's, so the append outcome of each tab, why each row did or did not
    ///     draw and whether Steam's toggle resolved are logged too, keyed once through
    ///     <see cref="SteamUiLog.Change" /> so a steady outcome is stated once and a change is stated again.
    /// </remarks>
    internal static void LogHostStatus(JsonElement root, string? error)
    {
        if (HostStatusReport(root, error) is { } report)
        {
            SteamUiLog.Change("steam.ui.append.quick-access", $"Steam UI Quick Access rows: {report}");
        }
    }

    /// <summary>The line <see cref="LogHostStatus" /> writes for one verification.</summary>
    /// <param name="root">The verification result, carrying the host's <c>status</c>.</param>
    /// <param name="error">The page's own error, when verification could not reach the host.</param>
    /// <returns>The report, or null when the result carries nothing to report.</returns>
    internal static string? HostStatusReport(JsonElement root, string? error)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object)
        {
            return "{\"append\":" + Raw(status, "lastAppend", "{\"never\":true}")
                   + ",\"appendQuickSettings\":" + Raw(status, "lastAppendQuickSettings", "{\"never\":true}")
                   + ",\"rows\":" + Raw(status, "renderOutcomes", "{}")
                   + ",\"toggle\":" + Raw(status, "toggleResolved", "null") + "}";
        }

        return error == "bridge unavailable" ? "{\"error\":\"bridge unavailable\"}" : null;
    }

    private static string Raw(JsonElement status, string name, string absent)
    {
        return status.TryGetProperty(name, out var value)
               && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.GetRawText()
            : absent;
    }

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The rows.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamQuickAccessRowsState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamQuickAccessRowsState);
    }

    /// <summary>Declares the component host, the rows publication and the validated <c>set</c> command.</summary>
    /// <param name="enabled">Whether the rows may be published now.</param>
    /// <param name="read">
    ///     Reads the current rows, or null to publish nothing. Publish an empty <c>Sections</c> list to
    ///     clear prior rows.
    /// </param>
    /// <param name="backend">What applies a row's change.</param>
    /// <param name="revision">
    ///     The current state revision, or null when the host has none. When given it must change whenever
    ///     the published rows change; an unchanged revision skips reading and serializing rows already
    ///     delivered to the current document.
    /// </param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamQuickAccessRowsState?>> read,
        ISteamQuickAccessRowsBackend backend,
        Func<long>? revision = null,
        string id = "quick-access-rows")
    {
        ArgumentNullException.ThrowIfNull(backend);
        var publication = revision is null
            ? SteamUiModuleBuilder.Publication(PatchId, enabled, read,
                SteamSurfaceJsonContext.Default.SteamQuickAccessRowsState)
            : SteamUiModuleBuilder.Publication(PatchId, enabled, read,
                SteamSurfaceJsonContext.Default.SteamQuickAccessRowsState, revision);
        return new SteamUiModule(id, [HostPatch], [publication],
        [
            SteamUiModuleBuilder.Command<(string Key, JsonElement Value)>(PatchId, "set", TryReadSet,
                (request, token) => backend.SetAsync(request.Key, request.Value, token),
                "The Quick Access row payload is invalid.")
        ]);
    }

    /// <summary>Reads one <c>set</c> payload: exactly a key and a primitive or curve value.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="value">The key and a detached copy of the value.</param>
    /// <returns>Whether the payload has that shape within the bounds.</returns>
    internal static bool TryReadSet(JsonElement payload, out (string Key, JsonElement Value) value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || key.Length > MaximumKeyLength
            || !payload.TryGetProperty("value", out var setting))
        {
            return false;
        }

        var bounded = setting.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => true,
            JsonValueKind.String => setting.GetString()!.Length <= MaximumTextLength,
            JsonValueKind.Array => setting.GetArrayLength() <= MaximumArrayLength,
            _ => false
        };
        if (!bounded)
        {
            return false;
        }

        value = (key, setting.Clone());
        return true;
    }
}
