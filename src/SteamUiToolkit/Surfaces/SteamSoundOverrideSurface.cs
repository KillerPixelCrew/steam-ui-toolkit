using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Validated replacement audio URLs, keyed by the current client's exact sound resource names.</summary>
/// <param name="Sounds">Resource filenames mapped to one or more audio data URLs.</param>
/// <param name="Revision">The host's monotonic revision.</param>
public sealed record SteamSoundOverrideState(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Sounds,
    long Revision = 0);

/// <summary>The current generation's result of decoding a host's replacement sounds.</summary>
/// <param name="Revision">The publication revision being decoded.</param>
/// <param name="Loading">Whether audio validation is still in progress.</param>
/// <param name="Resources">The number of resources with at least one playable replacement.</param>
/// <param name="Error">A bounded decode failure, or null when no asset was rejected.</param>
public sealed record SteamSoundOverrideStatus(long Revision, bool Loading, int Resources, string? Error);

/// <summary>Reversible overrides of Big Picture's own audio playback manager, without filesystem changes.</summary>
public static class SteamSoundOverrideSurface
{
    /// <summary>The override publication and patch identity.</summary>
    public const string PatchId = "steam-ui.sound-overrides";

    /// <summary>The playback-validation report sent through the generation-bound bridge.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["status"];

    /// <summary>The bounded audio-manager gate. An incompatible client keeps stock audio.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "soundOverrides",
        "steam-sound-overrides-v1:gamepad-audio-manager",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_sound_overrides_probe_")}}
            let manager=false;
            try{const store=req.exported(['m_GamepadUIAudioStore','m_bHomeAndQuickAccessButtonsEnabled'],
              v=>!!v?.GamepadUIAudio?.AudioPlaybackManager);
              manager=typeof store.GamepadUIAudio.AudioPlaybackManager.PlayAudioURLWithRepeats==='function';}catch{}
            return JSON.stringify({manager});
          {{SteamUiProbeJs.Close}}
          """,
        root => SteamUiPatchEvaluation.Flag(root, "manager"),
        "status.installed&&status.claimed",
        "!status.claimed",
        "Steam UI sound overrides");

    /// <summary>Creates the publication module. Only already discovered semantic resources should be supplied.</summary>
    /// <param name="enabled">The host's CEF availability policy.</param>
    /// <param name="read">Validated replacement assets, or null to withhold a publication.</param>
    /// <param name="revision">The current asset revision, avoiding repeated large serializations.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <param name="reportStatus">Receives decoder outcomes for the host's current publication.</param>
    /// <returns>The module for the session's shared runtime.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, Func<ValueTask<SteamSoundOverrideState?>> read,
        Func<long> revision, string id = "sound-overrides", Action<SteamSoundOverrideStatus>? reportStatus = null)
    {
        return new SteamUiModule(id, [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled, read,
                    SteamSurfaceJsonContext.Default.SteamSoundOverrideState, revision)
            ],
            [
                SteamUiModuleBuilder.Command<SteamSoundOverrideStatus>(PatchId, "status", TryReadStatus,
                    (status, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        reportStatus?.Invoke(status);
                        return Task.FromResult(SteamUiCommandResult.Applied);
                    }, "The sound validation report is invalid.")
            ]);
    }

    private static bool TryReadStatus(JsonElement payload, out SteamSoundOverrideStatus status)
    {
        status = null!;
        if (!SteamUiPayload.HasExactly(payload, 4)
            || !payload.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number
            || !revision.TryGetInt64(out var version)
            || version < 0
            || !payload.TryGetProperty("loading", out var loading)
            || loading.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !payload.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Number
            || !resources.TryGetInt32(out var count)
            || count < 0
            || !payload.TryGetProperty("error", out var error)
            || error.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
            || (error.ValueKind == JsonValueKind.String && error.GetString()!.Length > 256))
        {
            return false;
        }

        status = new SteamSoundOverrideStatus(version, loading.GetBoolean(), count, error.GetString());
        return true;
    }
}
