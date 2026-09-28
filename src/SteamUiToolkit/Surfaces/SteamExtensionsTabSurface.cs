using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One host-rendered action inside an extension.</summary>
/// <param name="Id">Opaque action identity returned when activated.</param>
/// <param name="Label">Plain visible action label.</param>
public sealed record SteamExtensionsTabAction(string Id, string Label);

/// <summary>One host-rendered extension setting.</summary>
/// <param name="Key">Stable setting identity within the plugin.</param>
/// <param name="Label">Plain visible setting label.</param>
/// <param name="Kind">
///     <c>boolean</c>, <c>number</c>, <c>text</c>, <c>secret</c>, <c>order</c> or <c>color</c>. A
///     number with <paramref name="Choices" /> is drawn as a slider whose notches carry the choices as
///     labels and whose value is the chosen index; a colour is a CSS colour string edited in a modal.
/// </param>
/// <param name="BooleanValue">Current boolean value.</param>
/// <param name="NumberValue">Current numeric value.</param>
/// <param name="TextValue">Current text value; secret values are never published back.</param>
/// <param name="Minimum">Inclusive numeric minimum.</param>
/// <param name="Maximum">Inclusive numeric maximum.</param>
/// <param name="Choices">Optional finite text choices.</param>
/// <param name="Description">Optional second line under the label.</param>
/// <param name="Parent">
///     The key of a boolean setting on the same item this one belongs to. The row is drawn indented
///     under its parent and only while the parent is on, the way CSSLoader shows a theme's patches
///     only for an enabled theme.
/// </param>
/// <param name="Highlight">Whether the description is drawn in the accent colour, for "update available".</param>
public sealed record SteamExtensionsTabSetting(
    string Key,
    string Label,
    string Kind,
    bool? BooleanValue = null,
    double? NumberValue = null,
    string? TextValue = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    string? Description = null,
    string? Parent = null,
    bool Highlight = false);

/// <summary>One extension shown in the Quick Access Extensions tab.</summary>
/// <param name="Id">Opaque extension instance identity returned when a setting changes.</param>
/// <param name="Name">Plain display name.</param>
/// <param name="Version">The extension version, or an empty string when it was refused before parsing.</param>
/// <param name="Status">Short truthful lifecycle or rejection state.</param>
/// <param name="Detail">Optional bounded detail for a refusal or unavailable action.</param>
/// <param name="Actions">Host-rendered actions declared by the plugin for this tab.</param>
/// <param name="Settings">Host-rendered plugin settings.</param>
/// <param name="ConfigurationRevision">Expected revision for the next setting change.</param>
/// <param name="Collapsible">
///     Whether the section folds. A collapsible section is headed by a button carrying the name, the
///     detail line and a caret, and its rows are drawn only while it is open; the header sends
///     <c>collapse</c> and the host publishes the new state.
/// </param>
/// <param name="Collapsed">Whether a collapsible section is currently folded.</param>
public sealed record SteamExtensionsTabItem(
    string Id,
    string Name,
    string Version,
    string Status,
    string? Detail = null,
    IReadOnlyList<SteamExtensionsTabAction>? Actions = null,
    IReadOnlyList<SteamExtensionsTabSetting>? Settings = null,
    long ConfigurationRevision = 0,
    bool Collapsible = false,
    bool Collapsed = false);

/// <summary>The current contents of the Quick Access Extensions tab.</summary>
/// <param name="Items">Installed extensions, including refused packages so their failure is visible.</param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamExtensionsTabState(IReadOnlyList<SteamExtensionsTabItem> Items, long Revision = 0);

/// <summary>Answers an explicit activation of an Extensions-tab entry.</summary>
public interface ISteamExtensionsTabBackend
{
    /// <summary>Handles the selected extension identity.</summary>
    /// <param name="id">One published extension identity.</param>
    /// <param name="cancellationToken">Cancels the user-initiated activation.</param>
    /// <returns>A truthful result, including a reason when the extension has no openable surface.</returns>
    Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken);

    /// <summary>Changes one declared setting for an exact expected configuration revision.</summary>
    /// <param name="id">One published extension instance identity.</param>
    /// <param name="key">One published setting key.</param>
    /// <param name="value">The primitive setting value.</param>
    /// <param name="expectedRevision">The published configuration revision.</param>
    /// <param name="cancellationToken">Cancels waiting without implying the change was undone.</param>
    /// <returns>A truthful result, including a reason when validation or delivery fails.</returns>
    Task<SteamUiCommandResult> ConfigureAsync(
        string id,
        string key,
        JsonElement value,
        long expectedRevision,
        CancellationToken cancellationToken);

    /// <summary>Folds or unfolds one collapsible section.</summary>
    /// <param name="id">One published extension instance identity.</param>
    /// <param name="collapsed">Whether the section should be folded.</param>
    /// <param name="cancellationToken">Cancels waiting without implying the change was undone.</param>
    /// <returns>A truthful result; the host publishes the section's new state.</returns>
    Task<SteamUiCommandResult> CollapseAsync(string id, bool collapsed, CancellationToken cancellationToken);
}

/// <summary>
/// A generic Quick Access tab for Steam UI extensions.
/// </summary>
/// <remarks>
/// The tab is deliberately host-rendered: an extension supplies only identity and state through a
/// bounded publication, never an arbitrary React tree or a raw Steam object. That makes one broken
/// extension a visible refusal instead of code that can compromise the tab that lists every other
/// extension.
/// </remarks>
public static class SteamExtensionsTabSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.extensions-tab";

    /// <summary>The commands emitted by the tab.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["activate", "configure", "collapse"];

    /// <summary>The Quick Access tab patch.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.extensions-tab",
        "extensionsTab",
        "steam-extensions-tab-v2:unique-qam-browser-view+claimable-memo+native-panel",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_extensions_tab_probe_")}}
            const qam=req.findUnique(['QuickAccessMenuBrowserView']);
            if(!qam)return JSON.stringify({qamModule:0});
            const exports=req(qam[0]);
            // Through the gate's own claim, or the export disappears the moment the gate holds it.
            {{SteamUiProbeJs.Unwrap("ExtensionsTab")}}
            const candidates=Object.keys(exports).filter(name=>{
              const value=exports[name];
              const original=unwrap(value?.type);
              return value&&typeof value==='object'&&typeof original==='function'
                &&String(original).includes('QuickAccessMenuBrowserView');
            });
            const memo=candidates.length===1?exports[candidates[0]]:null;
            const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
            return JSON.stringify({
              qamModule:1,
              memoExports:candidates.length,
              claimable:{{SteamUiProbeJs.Replaceable("descriptor")}},
              claimed:{{SteamUiProbeJs.Claimed("memo?.type", "ExtensionsTab")}},
              react:count({{SteamUiProbeJs.ReactTokens}}),
              focusable:count({{SteamUiProbeJs.NativeFocusableTokens}}),
              controls:count({{SteamUiProbeJs.NativeFieldTokens}}),
              panel:count({{SteamUiProbeJs.PanelLayoutTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "qamModule")
            && SteamUiPatchEvaluation.IsOne(root, "memoExports")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "focusable")
            && SteamUiPatchEvaluation.IsOne(root, "controls")
            && SteamUiPatchEvaluation.IsOne(root, "panel")
            && SteamUiPatchEvaluation.ClaimableOrOurs(root),
        "status.installed&&status.resolved&&status.claimed&&status.nativeComponentsResolved",
        "!status.claimed",
        "Extensions tab");

    /// <summary>Serializes the state exactly as the injected tab reads it.</summary>
    /// <param name="state">The current tab state.</param>
    /// <returns>The camel-case bridge payload.</returns>
    public static JsonElement Serialize(SteamExtensionsTabState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamExtensionsTabState);
    }

    /// <summary>Declares the tab, its publication and its activation command as one module.</summary>
    /// <param name="enabled">Whether the tab may be installed and published.</param>
    /// <param name="read">The current extension list, or null when no observation is available.</param>
    /// <param name="backend">The host action backend.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The complete module declaration.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamExtensionsTabState?>> read,
        ISteamExtensionsTabBackend backend,
        string id = "extensions-tab")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamExtensionsTabState,
            [Patch],
            [
                SteamSurfaceModule.Command(
                    PatchId,
                    "activate",
                    static (JsonElement payload, out string extensionId) =>
                        SteamUiPayload.TryReadBoundedString(payload, "id", 96, out extensionId),
                    backend.ActivateAsync,
                    "The extension activation payload is invalid."),
                SteamSurfaceModule.Command<(string Id, string Key, JsonElement Value, long Revision)>(
                    PatchId,
                    "configure",
                    TryReadConfiguration,
                    (value, token) => backend.ConfigureAsync(
                        value.Id, value.Key, value.Value, value.Revision, token),
                    "The extension setting payload is invalid."),
                SteamSurfaceModule.Command<(string Id, bool Collapsed)>(
                    PatchId,
                    "collapse",
                    TryReadCollapse,
                    (value, token) => backend.CollapseAsync(value.Id, value.Collapsed, token),
                    "The extension collapse payload is invalid.")
            ]);
    }

    private static bool TryReadCollapse(JsonElement payload, out (string Id, bool Collapsed) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadBoundedString(payload, "id", 96, out var id)
            || !SteamUiPayload.TryReadBoolean(payload, "collapsed", out var collapsed)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        value = (id, collapsed);
        return true;
    }

    private static bool TryReadConfiguration(
        JsonElement payload,
        out (string Id, string Key, JsonElement Value, long Revision) value)
    {
        value = default;
        if (!SteamUiPayload.TryReadBoundedString(payload, "id", 96, out var id)
            || !SteamUiPayload.TryReadBoundedString(payload, "key", 128, out var key)
            || !payload.TryGetProperty("value", out var settingValue)
            || !payload.TryGetProperty("revision", out var revisionProperty)
            || !revisionProperty.TryGetInt64(out var revision)
            || revision < 0
            || settingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                or JsonValueKind.String)
            || !SteamUiPayload.HasExactly(payload, 4))
        {
            return false;
        }

        value = (id, key, settingValue.Clone(), revision);
        return true;
    }
}
