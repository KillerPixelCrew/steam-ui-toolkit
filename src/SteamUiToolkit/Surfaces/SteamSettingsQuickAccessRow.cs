using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Host settings sections drawn with native fields in Quick Access Performance.</summary>
/// <param name="Pages">Named sets of sections, in display order.</param>
/// <param name="Revision">Increases when the host's values or descriptors change.</param>
public sealed record SteamSettingsQuickAccessState(IReadOnlyList<SteamSettingsPage> Pages, long Revision);

/// <summary>Applies a change to one currently published Quick Access setting.</summary>
public interface ISteamSettingsQuickAccessBackend
{
    /// <summary>Validates and applies the setting against the current host state.</summary>
    /// <param name="key">The published row key.</param>
    /// <param name="value">The selected primitive value.</param>
    /// <param name="cancellationToken">Cancels waiting for the command.</param>
    /// <returns>The truthful command outcome.</returns>
    Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken);
}

/// <summary>Mounts host-owned settings sections in Quick Access using the shared settings renderer.</summary>
public static class SteamSettingsQuickAccessRow
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.settings-sections";

    /// <summary>The exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["set"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "settingsSections", "steam-ui-settings-sections-v1:performance-root+valve-fields",
        "steam_ui_settings_sections_probe_");

    /// <summary>Serializes the typed publication.</summary>
    /// <param name="state">The current sections.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamSettingsQuickAccessState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamSettingsQuickAccessState);
    }

    /// <summary>Declares the state, row patch and validated setting command.</summary>
    /// <param name="enabled">Whether native Quick Access is enabled.</param>
    /// <param name="read">Reads the current sections, or null to retract them.</param>
    /// <param name="backend">The setting owner.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, Func<ValueTask<SteamSettingsQuickAccessState?>> read,
        ISteamSettingsQuickAccessBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module("settingsSections", PatchId, enabled, read,
            SteamSurfaceJsonContext.Default.SteamSettingsQuickAccessState, [Patch],
            [
                SteamUiModuleBuilder.Command<SetRequest>(PatchId, "set", TryReadSet,
                    (request, token) => backend.SetAsync(request.Key, request.Value, token),
                    "The Quick Access setting payload is invalid.")
            ]);
    }

    private static bool TryReadSet(JsonElement payload, out SetRequest value)
    {
        value = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || !payload.TryGetProperty("value", out var setting)
            || setting.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                or JsonValueKind.String))
        {
            return false;
        }

        value = new SetRequest(key, setting.Clone());
        return true;
    }

    private readonly record struct SetRequest(string Key, JsonElement Value);
}
