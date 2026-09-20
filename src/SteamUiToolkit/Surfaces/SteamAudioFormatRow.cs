using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One bounded option in the advanced audio Quick Access controls.</summary>
/// <param name="Id">Stable value sent back to the backend.</param>
/// <param name="Label">Human-readable text shown in Quick Access.</param>
public sealed record SteamAudioFormatOption(string Id, string Label);

/// <summary>Live channel/default-format and spatial-audio choices for the active playback endpoint.</summary>
/// <param name="Available">Whether the active output exposes advanced audio configuration.</param>
/// <param name="FormatOptions">Supported playback formats, at most 64.</param>
/// <param name="CurrentFormat">The active format id, or empty when it cannot be determined.</param>
/// <param name="SpatialOptions">Supported spatial formats, at most 16.</param>
/// <param name="CurrentSpatial">The active spatial format id, or empty when it cannot be determined.</param>
/// <param name="StatusText">Why the controls are unavailable, when known.</param>
public sealed record SteamAudioFormatState(
    bool Available,
    IReadOnlyList<SteamAudioFormatOption> FormatOptions,
    string CurrentFormat,
    IReadOnlyList<SteamAudioFormatOption> SpatialOptions,
    string CurrentSpatial,
    string StatusText);

/// <summary>Applies a selected advanced audio setting for the active playback endpoint.</summary>
public interface ISteamAudioFormatBackend
{
    /// <summary>Applies one format identifier published by the row.</summary>
    /// <param name="formatId">The offered format identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The truthful write outcome.</returns>
    Task<SteamUiCommandResult> SetFormatAsync(string formatId, CancellationToken cancellationToken);

    /// <summary>Applies one spatial-audio identifier published by the row.</summary>
    /// <param name="spatialId">The offered spatial-audio identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The truthful write outcome.</returns>
    Task<SteamUiCommandResult> SetSpatialAsync(string spatialId, CancellationToken cancellationToken);
}

/// <summary>Advanced audio dropdowns hosted in Steam Quick Settings.</summary>
public static class SteamAudioFormatRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.audio-format";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setFormat", "setSpatial"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "audioFormat",
        "native-qam-audio-format-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_audio_format_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamAudioFormatState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamAudioFormatState);
    }

    /// <summary>Declares the row as one module: the patch, state, and answer handlers.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What applies a selected choice.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamAudioFormatState?>> read,
        ISteamAudioFormatBackend backend,
        string id = "audioFormat")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamAudioFormatState,
            [Patch],
            [
                SteamSurfaceModule.Command<string>(
                    PatchId,
                    "setFormat",
                    SteamUiPayload.TryReadTarget,
                    backend.SetFormatAsync,
                    "The audio format payload is invalid."),
                SteamSurfaceModule.Command<string>(
                    PatchId,
                    "setSpatial",
                    SteamUiPayload.TryReadTarget,
                    backend.SetSpatialAsync,
                    "The spatial audio payload is invalid.")
            ]);
    }
}
