using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One audio endpoint as Steam's own device picker renders it.</summary>
/// <param name="Id">
///     Stable endpoint identifier. Any string; the injected side mints the numeric
///     identity Steam's store keys by and translates back on every command.
/// </param>
/// <param name="Name">Endpoint name as the platform reports it.</param>
/// <param name="HasOutput">Whether the endpoint can render.</param>
/// <param name="HasInput">Whether the endpoint can capture.</param>
public sealed record SteamAudioDevice(
    string Id,
    string Name,
    bool HasOutput,
    bool HasInput);

/// <summary>Audio as Steam's own menu renders it.</summary>
/// <remarks>
///     Volume and mute follow the default endpoint independently for render and capture. Steam's model
///     allows values per device, but the injected side gives every endpoint of a direction that
///     direction's current default, because a per-device number for an inactive endpoint would be
///     invented. An endpoint present in both directions is one entry carrying both flags: Steam's
///     device model is one entry with a direction test, and listing it twice puts the same hardware in
///     the picker under two identities.
/// </remarks>
/// <param name="Available">Whether audio can be observed and changed at all.</param>
/// <param name="Devices">Every endpoint, output and input.</param>
/// <param name="ActiveOutputDeviceId">The default render endpoint, or empty.</param>
/// <param name="ActiveInputDeviceId">The default capture endpoint, or empty.</param>
/// <param name="VolumePercent">System volume, 0-100.</param>
/// <param name="Muted">Whether the default render endpoint is muted.</param>
/// <param name="InputVolumePercent">Default capture volume, 0-100, or null when unavailable.</param>
/// <param name="InputMuted">Whether the default capture endpoint is muted.</param>
/// <param name="StatusText">A human-readable fault, or empty.</param>
public sealed record SteamAudioState(
    bool Available,
    IReadOnlyList<SteamAudioDevice> Devices,
    string ActiveOutputDeviceId,
    string ActiveInputDeviceId,
    int VolumePercent,
    bool Muted,
    int? InputVolumePercent,
    bool InputMuted,
    string StatusText);

/// <summary>What answers Steam's audio page: the default-device and volume writes.</summary>
/// <remarks>
///     Reads come from the state the consumer publishes, so the backend only has to act. Every method
///     returns the truthful outcome; a refusal must carry its reason because the page has nowhere to
///     put one and the user otherwise sees a control that did nothing.
/// </remarks>
public interface ISteamAudioBackend
{
    /// <summary>Makes one endpoint the default for its direction.</summary>
    /// <param name="deviceId">The endpoint, as the published state named it.</param>
    /// <param name="input">Whether the capture default is being set rather than the render one.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetDefaultDeviceAsync(
        string deviceId,
        bool input,
        CancellationToken cancellationToken);

    /// <summary>Sets the master volume for one direction.</summary>
    /// <param name="percent">Target volume, 0-100.</param>
    /// <param name="input">Whether to set the default capture endpoint rather than render.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetVolumeAsync(
        int percent,
        bool input,
        CancellationToken cancellationToken);
}

/// <summary>
///     Steam's own audio page and Quick Settings audio section, backed by the consumer's endpoints.
/// </summary>
/// <remarks>
///     The Windows client ships the whole surface and gates it on <c>SteamClient.System.Audio</c>
///     existing. The injected gate supplies that namespace, feeds the running store through its own
///     <c>RegisterOrUpdateDevice</c> path, and turns its device-volume writes into the commands the
///     backend answers. The consumer supplies <see cref="SteamAudioState" /> and an
///     <see cref="ISteamAudioBackend" />; everything Steam-shaped stays here.
/// </remarks>
public static class SteamAudioSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.audio";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["getDevices", "setDefaultDevice", "setVolume"];

    /// <summary>The gate that supplies the audio backend behind <c>SteamClient.System.Audio</c>.</summary>
    /// <remarks>
    ///     The store caches <c>m_bAvailable = null != SteamClient.System.Audio</c> at construction,
    ///     which already ran; the singleton has to be reachable so it can be written to directly.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.audio-namespace",
        "audio",
        "native-qam-audio-v1:store+absent-namespace+reachable-singleton",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_audio_probe_")}}
            let singleton=false;
            // The store by what it is, never by module id or export name: the September 2026 beta
            // renumbered module 1409 and this probe refused audio until it stopped naming it.
            try{singleton=!!req.exported(['SteamClient.System.Audio','RegisterForDeviceAdded','m_bAvailable'],
              v=>!!v&&typeof v==='object'&&'m_bAvailable' in v&&typeof v.RegisterOrUpdateDevice==='function');}catch{}
            return JSON.stringify({
              audioStore:count(['SteamClient.System.Audio','RegisterForDeviceAdded','m_bAvailable']),
              audioNamespaceAbsent:{{SteamUiProbeJs.OwnedOrAbsentNamespace("Audio")}},
              storeSingletonReachable:singleton
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "audioStore")
            && SteamUiPatchEvaluation.Flag(root, "audioNamespaceAbsent")
            && SteamUiPatchEvaluation.Flag(root, "storeSingletonReachable"),
        "status.installed&&status.namespacePresent",
        "!status.namespacePresent",
        "Audio namespace");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamAudioState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamAudioState);
    }

    /// <summary>Declares the surface as one module: the gate, the state, and the answers.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the page's writes.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    /// <remarks>
    ///     Publish once after the gate installs: the running store's availability was cached when
    ///     Steam started, before the namespace existed, and the first publication is what flips it.
    /// </remarks>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamAudioState?>> read,
        ISteamAudioBackend backend,
        string id = "audio")
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamAudioState,
            [Patch],
            [
                new SteamUiCommandHandler(PatchId, "getDevices", async (_, _) =>
                {
                    var state = await read().ConfigureAwait(false);
                    return state is null
                        ? new SteamUiCommandResult(false, "Audio is not currently observable.")
                        : new SteamUiCommandResult(true, null, Serialize(state));
                }),
                SteamSurfaceModule.Command<(string Id, bool Input)>(
                    PatchId,
                    "setDefaultDevice",
                    TryReadDevicePayload,
                    (device, cancellationToken) =>
                        backend.SetDefaultDeviceAsync(device.Id, device.Input, cancellationToken),
                    "The audio device payload is invalid."),
                SteamSurfaceModule.Command<(int Percent, bool Input)>(
                    PatchId,
                    "setVolume",
                    TryReadVolumePayload,
                    (volume, cancellationToken) =>
                        backend.SetVolumeAsync(volume.Percent, volume.Input, cancellationToken),
                    "The audio volume payload is invalid.")
            ]);
    }

    /// <summary>Reads the endpoint and direction of a default-device change.</summary>
    private static bool TryReadDevicePayload(JsonElement payload, out (string Id, bool Input) device)
    {
        var input = false;
        var read = SteamUiPayload.TryReadBoundedString(payload, "id", 512, out var id)
                   && SteamUiPayload.TryReadBoolean(payload, "input", out input)
                   && SteamUiPayload.HasExactly(payload, 2);
        device = (id, input);
        return read;
    }

    /// <summary>Reads a volume change; <c>input</c> is optional and defaults to render.</summary>
    private static bool TryReadVolumePayload(JsonElement payload, out (int Percent, bool Input) volume)
    {
        volume = default;
        if (!SteamUiPayload.TryReadInt(payload, "percent", 0, 100, out var percent))
        {
            return false;
        }

        if (!payload.TryGetProperty("input", out _))
        {
            volume = (percent, false);
            return SteamUiPayload.HasExactly(payload, 1);
        }

        if (!SteamUiPayload.TryReadBoolean(payload, "input", out var input))
        {
            return false;
        }

        volume = (percent, input);
        return SteamUiPayload.HasExactly(payload, 2);
    }
}
