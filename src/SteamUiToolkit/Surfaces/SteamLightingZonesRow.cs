using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One independently writable lighting zone.</summary>
/// <param name="Id">Zone identifier, non-empty and unique within the state.</param>
/// <param name="Label">What the zone dropdown shows.</param>
/// <param name="Available">Whether the zone may be written. Unavailable zones are not offered.</param>
/// <param name="DesiredColor">The colour asked for as 0xRRGGBB, or null.</param>
/// <param name="ObservedColor">The colour the device reports as 0xRRGGBB, or null.</param>
/// <param name="Progress">
///     Command progress; the colour sliders disable themselves while <c>queued</c>, <c>applying</c> or
///     <c>replacing</c>.
/// </param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="Accent">
///     Whether the zone is marked: the editor's switch names it after the host's
///     <see cref="SteamQuickAccessLayout.AccentLabel" />, and the zone dropdown's description is drawn in
///     Steam's accent colour.
/// </param>
public sealed record SteamLightingZoneState(
    string Id,
    string Label,
    bool Available,
    int? DesiredColor,
    int? ObservedColor,
    string Progress,
    string StatusText,
    bool Accent = false);

/// <summary>The lighting zones the inline colour editor edits.</summary>
/// <param name="Zones">
///     The zones. With at least one available zone the editor draws an Edit color switch and, while it
///     is on, a zone dropdown, a colour preview and hue, saturation and value sliders.
/// </param>
public sealed record SteamLightingZonesState(IReadOnlyList<SteamLightingZoneState> Zones);

/// <summary>What answers the lighting editor.</summary>
public interface ISteamLightingZonesBackend
{
    /// <summary>Sets one zone's colour.</summary>
    /// <param name="zone">The zone id.</param>
    /// <param name="color">
    ///     The colour as 0xRRGGBB. The editor coalesces slider edits 350 ms after the last change, so a
    ///     firmware write-rate limit does not queue stale intermediate colours.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetLightingColorAsync(string zone, int color, CancellationToken cancellationToken);
}

/// <summary>The inline lighting-colour editor in Quick Settings, drawn by the component host.</summary>
/// <remarks>
///     Built from Valve's toggle, slider, dropdown and row primitives. The HSV interaction is this
///     library's: Steam's generic HSV implementation is closed over by its module and not exported, and
///     its exported controller-LED wrapper calls <c>SteamClient.Input.PreviewControllerLEDColor</c>, a
///     Steam Input side effect unrelated to device lighting (live-probed 2026-08-31). Hue, saturation and
///     value stay local while dragging and a write is requested only on release. The editor is the
///     layout kind <c>lightingZones</c>; it is mounted by <see cref="SteamQuickAccessRowsSurface.HostPatch" />
///     and draws while this state is published.
/// </remarks>
public static class SteamLightingZonesRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.lighting-zones";

    /// <summary>The exact command vocabulary the injected editor sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setLightingColor"];

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamLightingZonesState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamLightingZonesState);
    }

    /// <summary>Declares the editor's state and command. The component host draws it.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current zones, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the editor.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamLightingZonesState?>> read,
        ISteamLightingZonesBackend backend,
        string id = "lighting-zones")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamLightingZonesState,
            [],
            [
                SteamUiModuleBuilder.Command<(string Zone, int Color)>(
                    PatchId,
                    "setLightingColor",
                    TryReadColor,
                    (value, cancellationToken) =>
                        backend.SetLightingColorAsync(value.Zone, value.Color, cancellationToken),
                    "The lighting-color payload is invalid.")
            ]);
    }

    private static bool TryReadColor(JsonElement payload, out (string Zone, int Color) value)
    {
        var color = 0;
        var read = SteamUiPayload.TryReadNonBlankString(payload, "zone", out var zone)
                   && SteamUiPayload.TryReadInt(payload, "color", 0, 0xFFFFFF, out color)
                   && SteamUiPayload.HasExactly(payload, 2);
        value = (zone, color);
        return read;
    }
}
