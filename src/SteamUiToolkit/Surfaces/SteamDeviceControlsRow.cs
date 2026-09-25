using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One bounded integer device control rendered as a percent slider.</summary>
/// <param name="Available">Whether the slider may be operated. False hides it.</param>
/// <param name="Minimum">Lowest value, at least 0.</param>
/// <param name="Maximum">Highest value, at most 100 and above <paramref name="Minimum" />.</param>
/// <param name="Step">Slider step, at least 1 and within the range.</param>
/// <param name="Desired">The value asked for, on a step, or null.</param>
/// <param name="Observed">The value the device reports, on a step, or null.</param>
/// <param name="Progress">
///     Command progress; the slider disables itself while <c>queued</c>, <c>applying</c> or
///     <c>replacing</c>.
/// </param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="OverrideId">
///     The host's setting id while the running game's own profile supplies this value, so the row marks
///     it in Steam's accent colour; null otherwise.
/// </param>
public sealed record SteamDeviceRangeState(
    bool Available,
    int Minimum,
    int Maximum,
    int Step,
    int? Desired,
    int? Observed,
    string Progress,
    string StatusText,
    string? OverrideId = null);

/// <summary>One independently writable lighting zone.</summary>
/// <param name="Id">Zone identifier, 1-64 characters, unique within the state.</param>
/// <param name="Label">What the zone dropdown shows.</param>
/// <param name="Available">Whether the zone may be written. Unavailable zones are not offered.</param>
/// <param name="DesiredColor">The colour asked for as 0xRRGGBB, or null.</param>
/// <param name="ObservedColor">The colour the device reports as 0xRRGGBB, or null.</param>
/// <param name="Progress">Command progress; the colour sliders disable themselves while busy.</param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="OverrideId">
///     The host's setting id while the running game's own profile supplies this value, so the row marks
///     it in Steam's accent colour; null otherwise.
/// </param>
public sealed record SteamLightingZoneState(
    string Id,
    string Label,
    bool Available,
    int? DesiredColor,
    int? ObservedColor,
    string Progress,
    string StatusText,
    string? OverrideId = null);

/// <summary>Device charging and lighting controls shown in Steam Quick Settings.</summary>
/// <param name="ChargeLimit">The battery charge-limit slider, or null to omit it.</param>
/// <param name="LightingBrightness">The lighting-brightness slider, or null to omit it.</param>
/// <param name="LightingZones">
///     At most 16 zones. With at least one available zone the row draws a
///     zone dropdown, a colour preview and hue, saturation and value sliders.
/// </param>
public sealed record SteamDeviceControlsState(
    SteamDeviceRangeState? ChargeLimit,
    SteamDeviceRangeState? LightingBrightness,
    IReadOnlyList<SteamLightingZoneState> LightingZones);

/// <summary>What answers the device-control rows.</summary>
public interface ISteamDeviceControlsBackend
{
    /// <summary>Sets the battery charge limit.</summary>
    /// <param name="percent">The limit, on a published step.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetChargeLimitAsync(int percent, CancellationToken cancellationToken);

    /// <summary>Sets the lighting brightness.</summary>
    /// <param name="percent">The brightness, on a published step.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetLightingBrightnessAsync(int percent, CancellationToken cancellationToken);

    /// <summary>Sets one zone's colour.</summary>
    /// <param name="zone">The zone id.</param>
    /// <param name="color">
    ///     The colour as 0xRRGGBB. The row coalesces slider edits 350 ms after
    ///     the last change, so a firmware write-rate limit does not queue stale intermediate colours.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetLightingColorAsync(string zone, int color, CancellationToken cancellationToken);
}

/// <summary>Charge-limit and persistent device-lighting controls in Quick Settings.</summary>
/// <remarks>
///     Built from Valve's slider, dropdown and row primitives. The HSV interaction is this library's:
///     Steam's generic HSV implementation is closed over by its module and not exported, and its
///     exported controller-LED wrapper calls <c>SteamClient.Input.PreviewControllerLEDColor</c>, a
///     Steam Input side effect unrelated to device lighting (live-probed 2026-08-31). Hue, saturation
///     and value stay local while dragging and a write is requested only on release.
/// </remarks>
public static class SteamDeviceControlsRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.device-controls";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["setChargeLimit", "setLightingBrightness", "setLightingColor"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "deviceControls",
        "native-qam-device-controls-v1:performance-root+valve-slider+valve-dropdown",
        "steam_ui_device_controls_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamDeviceControlsState state)
    {
        return JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamDeviceControlsState);
    }

    /// <summary>Declares the row as one module: the patch, the state, and the answers.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the sliders.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamDeviceControlsState?>> read,
        ISteamDeviceControlsBackend backend,
        string id = "device-controls")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamDeviceControlsState,
            [Patch],
            [
                SteamSurfaceModule.Command<int>(
                    PatchId,
                    "setChargeLimit",
                    TryReadPercent,
                    backend.SetChargeLimitAsync,
                    "The charge-limit payload is invalid."),
                SteamSurfaceModule.Command<int>(
                    PatchId,
                    "setLightingBrightness",
                    TryReadPercent,
                    backend.SetLightingBrightnessAsync,
                    "The lighting-brightness payload is invalid."),
                SteamSurfaceModule.Command<(string Zone, int Color)>(
                    PatchId,
                    "setLightingColor",
                    TryReadColor,
                    (value, cancellationToken) =>
                        backend.SetLightingColorAsync(value.Zone, value.Color, cancellationToken),
                    "The lighting-color payload is invalid.")
            ]);
    }

    private static bool TryReadPercent(JsonElement payload, out int percent)
    {
        return SteamUiPayload.TryReadInt(payload, "percent", 0, 100, out percent)
               && SteamUiPayload.HasExactly(payload, 1);
    }

    private static bool TryReadColor(JsonElement payload, out (string Zone, int Color) value)
    {
        var color = 0;
        var read = SteamUiPayload.TryReadBoundedString(payload, "zone", 64, out var zone)
                   && SteamUiPayload.TryReadInt(payload, "color", 0, 0xFFFFFF, out color)
                   && SteamUiPayload.HasExactly(payload, 2);
        value = (zone, color);
        return read;
    }
}
