using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One audio endpoint as Steam's own device picker renders it.</summary>
/// <param name="Id">Stable endpoint identifier. Any string; the injected side mints the numeric
/// identity Steam's store keys by and translates back on every command.</param>
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
/// Volume and mute follow the default endpoint independently for render and capture. Steam's model
/// allows values per device, but the injected side gives every endpoint of a direction that
/// direction's current default, because a per-device number for an inactive endpoint would be
/// invented. An endpoint present in both directions is one entry carrying both flags: Steam's
/// device model is one entry with a direction test, and listing it twice puts the same hardware in
/// the picker under two identities.
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
/// Reads come from the state the consumer publishes, so the backend only has to act. Every method
/// returns the truthful outcome; a refusal must carry its reason because the page has nowhere to
/// put one and the user otherwise sees a control that did nothing.
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
/// Steam's own audio page and Quick Settings audio section, backed by the consumer's endpoints.
/// </summary>
/// <remarks>
/// The Windows client ships the whole surface and gates it on <c>SteamClient.System.Audio</c>
/// existing. The injected gate supplies that namespace, feeds the running store through its own
/// <c>RegisterOrUpdateDevice</c> path, and turns its device-volume writes into the commands the
/// backend answers. The consumer supplies <see cref="SteamAudioState"/> and an
/// <see cref="ISteamAudioBackend"/>; everything Steam-shaped stays here.
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
    /// The store caches <c>m_bAvailable = null != SteamClient.System.Audio</c> at construction,
    /// which already ran; the singleton has to be reachable so it can be written to directly.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.audio-namespace",
        gateName: "audio",
        fingerprint: "native-qam-audio-v1:store+absent-namespace+reachable-singleton",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_audio_probe_")}}
              let singleton=false;
              try{const mod=req('1409');singleton=!!(mod&&mod.F5&&('m_bAvailable' in mod.F5));}catch{}
              return JSON.stringify({
                audioStore:count(['SteamClient.System.Audio','RegisterForDeviceAdded','m_bAvailable']),
                audioNamespaceAbsent:(()=>{const a=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System.Audio;
                  // Absent, or present and OURS. A namespace this gate installed is not evidence of a
                  // native backend, and treating it as one made this patch declare itself incompatible
                  // five seconds after a successful install, tear down, and orphan the namespace it had
                  // just defined — leaving Steam's audio page empty until Steam itself restarted.
                  return !a||a.__steamUiOwnedNamespace===true;})(),
                storeSingletonReachable:singleton
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "audioStore")
            && SteamGatePatch.Flag(root, "audioNamespaceAbsent")
            && SteamGatePatch.Flag(root, "storeSingletonReachable"),
        verifyOk: "status.installed&&status.namespacePresent",
        removeOk: "!status.namespacePresent",
        subject: "Audio namespace");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamAudioState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamAudioState);

    /// <summary>Declares the surface as one module: the gate, the state, and the answers.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the page's writes.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    /// <remarks>
    /// Publish once after the gate installs: the running store's availability was cached when
    /// Steam started, before the namespace existed, and the first publication is what flips it.
    /// </remarks>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamAudioState?>> read,
        ISteamAudioBackend backend,
        string id = "audio")
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamAudioState),
            ],
            commands:
            [
                new(PatchId, "getDevices", async (_, _) =>
                {
                    SteamAudioState? state = await read().ConfigureAwait(false);
                    return state is null
                        ? new SteamUiCommandResult(false, "Audio is not currently observable.")
                        : new SteamUiCommandResult(true, null, Serialize(state));
                }),
                new(PatchId, "setDefaultDevice", (request, cancellationToken) =>
                    TryReadDevicePayload(request.Payload, out string deviceId, out bool input)
                        ? backend.SetDefaultDeviceAsync(deviceId, input, cancellationToken)
                        : SteamSurfaceModule.Invalid("The audio device payload is invalid.")),
                new(PatchId, "setVolume", (request, cancellationToken) =>
                    TryReadVolumePayload(request.Payload, out int percent, out bool input)
                        ? backend.SetVolumeAsync(percent, input, cancellationToken)
                        : SteamSurfaceModule.Invalid("The audio volume payload is invalid.")),
            ]);
    }

    /// <summary>Reads the endpoint and direction of a default-device change.</summary>
    private static bool TryReadDevicePayload(JsonElement payload, out string id, out bool input)
    {
        input = false;
        if (!SteamUiPayload.TryReadBoundedString(payload, "id", 512, out id)
            || !payload.TryGetProperty("input", out JsonElement inputProperty)
            || inputProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            return false;
        }

        input = inputProperty.ValueKind is JsonValueKind.True;
        return true;
    }

    private static bool TryReadVolumePayload(JsonElement payload, out int percent, out bool input)
    {
        input = false;
        if (!SteamUiPayload.TryReadInt(payload, "percent", 0, 100, out percent))
        {
            return false;
        }

        if (!payload.TryGetProperty("input", out JsonElement inputProperty))
        {
            return SteamUiPayload.HasExactly(payload, 1);
        }
        if (inputProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        input = inputProperty.ValueKind is JsonValueKind.True;
        return SteamUiPayload.HasExactly(payload, 2);
    }
}
