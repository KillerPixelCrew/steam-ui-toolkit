using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Validated replacement audio URLs, keyed by the current client's exact sound resource names.</summary>
/// <param name="Sounds">Resource filenames mapped to one or more audio data URLs.</param>
/// <param name="Revision">The host's monotonic revision.</param>
public sealed record SteamSoundOverrideState(IReadOnlyDictionary<string, string[]> Sounds, long Revision = 0);

/// <summary>Reversible overrides of Big Picture's own audio playback manager, without filesystem changes.</summary>
public static class SteamSoundOverrideSurface
{
    /// <summary>The override publication and patch identity.</summary>
    public const string PatchId = "steam-ui.sound-overrides";

    /// <summary>The bounded audio-manager gate. An incompatible client keeps stock audio.</summary>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        PatchId,
        "soundOverrides",
        "steam-sound-overrides-v1:gamepad-audio-manager",
        $$"""
        {{SteamUiProbeJs.Preamble("sound-overrides-probe")}}
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
    /// <returns>The module for the session's shared runtime.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, Func<ValueTask<SteamSoundOverrideState?>> read,
        Func<long> revision)
    {
        return new SteamUiModule("sound-overrides", [Patch],
            [SteamUiModuleBuilder.Publication(PatchId, enabled, read,
                SteamSurfaceJsonContext.Default.SteamSoundOverrideState, revision)], []);
    }
}
