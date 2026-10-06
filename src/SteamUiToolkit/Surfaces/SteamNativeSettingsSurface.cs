using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The native Big Picture Settings pages a host may augment.</summary>
public static class SteamNativeSettingsPageId
{
    /// <summary>Steam's Display page.</summary>
    public const string Display = "display";

    /// <summary>Steam's Power page, revealed only while the host publishes sections.</summary>
    public const string Power = "power";

    /// <summary>Steam's Audio page.</summary>
    public const string Audio = "audio";

    /// <summary>Steam's Controller page.</summary>
    public const string Controller = "controller";
}

/// <summary>Host-owned sections appended to one existing native Settings page.</summary>
/// <param name="Id">One of <see cref="SteamNativeSettingsPageId" />.</param>
/// <param name="Sections">The sections in display order; an empty list retracts the addition.</param>
public sealed record SteamNativeSettingsPage(string Id, IReadOnlyList<SteamSettingsSection> Sections);

/// <summary>The currently available additions to Steam's native Settings pages.</summary>
/// <param name="Pages">Unique native page identities and their host-owned sections.</param>
/// <param name="Revision">Increases when the host's values or descriptors change.</param>
public sealed record SteamNativeSettingsState(IReadOnlyList<SteamNativeSettingsPage> Pages, long Revision);

/// <summary>The host that owns native Settings changes.</summary>
public interface ISteamNativeSettingsBackend
{
    /// <summary>Validates a primitive change against current availability and applies it once.</summary>
    /// <param name="key">The published row key.</param>
    /// <param name="value">The selected primitive value; an action sends true.</param>
    /// <param name="cancellationToken">Cancels waiting for the command.</param>
    /// <returns>The truthful command outcome, including a refusal reason.</returns>
    Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken);
}

/// <summary>Adds native fields to Steam's existing Display, Power, Audio and Controller pages.</summary>
/// <remarks>
///     Steam owns each page's route, title, navigation and original content. The gate uses the shared
///     memo and JSX claims to append host sections and adopt an already mounted Settings root.
///     It reveals only the Power descriptor while the host has sections for it; no platform identity
///     is changed. Null state retracts additions, and every field uses the shared settings renderer.
/// </remarks>
public static class SteamNativeSettingsSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.native-settings";

    /// <summary>The exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["set"];

    /// <summary>The patch that augments native Settings without replacing native fields.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "nativeSettings",
        "steam-native-settings-v1:unique-native-descriptors+settings-root+valve-fields",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_native_settings_probe_")}}
            return JSON.stringify({
              descriptors:count(['#Settings_Page_Display','#Settings_Page_Power',
                '#Settings_Page_Audio','#Settings_Page_Controller']),
              root:count(['#Settings_Title','SettingsModal','SettingsTitleBar']),
              react:count({{SteamUiProbeJs.ReactTokens}}),
              jsx:count({{SteamUiProbeJs.JsxRuntimeTokens}}),
              fields:count({{SteamUiProbeJs.NativeFieldTokens}}),
              focusable:count({{SteamUiProbeJs.NativeFocusableTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "descriptors")
            && SteamUiPatchEvaluation.IsOne(root, "root")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "jsx")
            && SteamUiPatchEvaluation.IsOne(root, "fields")
            && SteamUiPatchEvaluation.IsOne(root, "focusable"),
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimsRemaining&&status.ownedRoots===0",
        "Native Settings gate");

    /// <summary>Serializes the typed publication.</summary>
    /// <param name="state">The current page additions.</param>
    /// <returns>The camelCase wire payload.</returns>
    public static JsonElement Serialize(SteamNativeSettingsState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamNativeSettingsState);
    }

    /// <summary>Declares the native page patch, state and bounded setting command.</summary>
    /// <param name="enabled">Whether native page additions are enabled.</param>
    /// <param name="read">Reads current sections, or null to retract them.</param>
    /// <param name="backend">The owner that revalidates availability and values.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, Func<ValueTask<SteamNativeSettingsState?>> read,
        ISteamNativeSettingsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module("nativeSettings", PatchId, enabled, read,
            SteamSurfaceJsonContext.Default.SteamNativeSettingsState, [Patch],
            [
                SteamUiModuleBuilder.Command<SetRequest>(PatchId, "set", TryReadSet,
                    (request, token) => backend.SetAsync(request.Key, request.Value, token),
                    "The native Settings payload is invalid.")
            ]);
    }

    private static bool TryReadSet(JsonElement payload, out SetRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || key.Length > 1024
            || !payload.TryGetProperty("value", out var setting)
            || setting.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                or JsonValueKind.String)
            || (setting.ValueKind == JsonValueKind.String && setting.GetString()!.Length > 4096))
        {
            return false;
        }

        value = new SetRequest(key, setting.Clone());
        return true;
    }

    private readonly record struct SetRequest(string Key, JsonElement Value);
}
