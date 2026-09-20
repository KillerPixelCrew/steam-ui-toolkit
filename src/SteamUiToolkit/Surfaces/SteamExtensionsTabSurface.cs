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
/// <param name="Kind">Boolean, number, text, or secret.</param>
/// <param name="BooleanValue">Current boolean value.</param>
/// <param name="NumberValue">Current numeric value.</param>
/// <param name="TextValue">Current text value; secret values are never published back.</param>
/// <param name="Minimum">Inclusive numeric minimum.</param>
/// <param name="Maximum">Inclusive numeric maximum.</param>
/// <param name="Choices">Optional finite text choices.</param>
public sealed record SteamExtensionsTabSetting(
    string Key,
    string Label,
    string Kind,
    bool? BooleanValue = null,
    double? NumberValue = null,
    string? TextValue = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null);

/// <summary>One extension shown in the Quick Access Extensions tab.</summary>
/// <param name="Id">Opaque extension instance identity returned when a setting changes.</param>
/// <param name="Name">Plain display name.</param>
/// <param name="Version">The extension version, or an empty string when it was refused before parsing.</param>
/// <param name="Status">Short truthful lifecycle or rejection state.</param>
/// <param name="Detail">Optional bounded detail for a refusal or unavailable action.</param>
/// <param name="Actions">Host-rendered actions declared by the plugin for this tab.</param>
/// <param name="Settings">Host-rendered plugin settings.</param>
/// <param name="ConfigurationRevision">Expected revision for the next setting change.</param>
public sealed record SteamExtensionsTabItem(
    string Id,
    string Name,
    string Version,
    string Status,
    string? Detail = null,
    IReadOnlyList<SteamExtensionsTabAction>? Actions = null,
    IReadOnlyList<SteamExtensionsTabSetting>? Settings = null,
    long ConfigurationRevision = 0);

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
    public static IReadOnlyList<string> Commands { get; } = ["activate", "configure"];

    /// <summary>The Quick Access tab patch.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.extensions-tab",
        "extensionsTab",
        "steam-extensions-tab-v1:unique-qam-browser-view+claimable-memo",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_extensions_tab_probe_")}}
            const qam=req.findUnique(['QuickAccessMenuBrowserView']);
            if(!qam)return JSON.stringify({qamModule:0});
            const exports=req(qam[0]);
            const candidates=Object.keys(exports).filter(name=>{
              const value=exports[name];
              const stored=value?.type?.__steamUiExtensionsTabClaimed===true
                ?value.type.__steamUiExtensionsTabOriginal:value?.type;
              const original=stored?.kind==='steam-ui-property-snapshot-v1'?stored.value:stored;
              return value&&typeof value==='object'&&typeof original==='function'
                &&String(original).includes('QuickAccessMenuBrowserView');
            });
            const memo=candidates.length===1?exports[candidates[0]]:null;
            const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
            return JSON.stringify({
              qamModule:1,
              memoExports:candidates.length,
              claimable:{{SteamUiProbeJs.Replaceable("descriptor")}},
              claimed:!!memo&&memo.type.__steamUiExtensionsTabClaimed===true,
              react:count({{SteamUiProbeJs.ReactTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "qamModule")
            && SteamUiPatchEvaluation.IsOne(root, "memoExports")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.Flag(root, "claimable"),
        "status.installed&&status.resolved&&status.claimed&&status.nativeFocusableResolved",
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
                    "The extension setting payload is invalid.")
            ]);
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
