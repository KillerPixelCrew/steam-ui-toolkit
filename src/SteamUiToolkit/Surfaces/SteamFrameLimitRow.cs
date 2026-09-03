using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The unified frame-limit row, shaped like SteamOS's own.</summary>
/// <remarks>
/// One continuous slider bookended by the panel's limits, plus a separate switch for off — verified
/// against a Steam Deck showing "60 FPS (60 Hz)" between bookends 10 and 60. There are no notches:
/// the cap is a free number and the pairing is what snaps to a mode the panel can hold, which is
/// exactly the merge Valve made when it unified the two rows. With the cap off the slider becomes
/// the refresh rate, notched to exactly the modes the display accepted.
/// <para>
/// <paramref name="Progress"/> must be one of <c>idle</c>, <c>queued</c>, <c>applying</c>,
/// <c>succeeded-verified</c>, <c>applied-unverified</c>, <c>rejected</c>, <c>timed-out</c>,
/// <c>indeterminate</c>, <c>failed</c> or <c>external-change</c>; the row rejects anything else
/// and reports why in its render outcome.
/// </para>
/// </remarks>
/// <param name="Available">Whether the row can be operated at all.</param>
/// <param name="MinimumFps">Lowest cap the slider offers, or null when unknown. A pair with <paramref name="MaximumFps"/>.</param>
/// <param name="MaximumFps">Highest cap the slider offers, at most 1000.</param>
/// <param name="DesiredFps">The cap asked for, 0 for off, or null when none.</param>
/// <param name="ObservedFps">The cap the limiter reports, 0 for off, or null when unread.</param>
/// <param name="Progress">Command progress in the closed vocabulary above.</param>
/// <param name="Fault">The last failure's text, or empty.</param>
/// <param name="StatusText">One line describing the state, or why the row cannot be operated.</param>
/// <param name="LimitEnabled">Whether a cap applies. Off is a switch of its own, so the slider keeps the cap the user last chose.</param>
/// <param name="RefreshForCap">The refresh rate each cap will be presented at, keyed by cap, for the "(60 Hz)" half of the label. Empty when a cap moves no display mode.</param>
/// <param name="RefreshMinHz">Lowest refresh rate of the row's other mode, or null when the display offers none.</param>
/// <param name="RefreshMaxHz">Highest refresh rate of the row's other mode.</param>
/// <param name="CurrentRefreshHz">The rate in force, which the refresh mode needs a concrete value for.</param>
/// <param name="RefreshRates">Every rate the display accepted, ascending. Windows takes a mode or refuses, so the refresh mode is notched to exactly these.</param>
public sealed record SteamFrameLimitState(
    bool Available,
    int? MinimumFps,
    int? MaximumFps,
    int? DesiredFps,
    int? ObservedFps,
    string Progress,
    string Fault,
    string StatusText,
    bool LimitEnabled = false,
    IReadOnlyDictionary<int, int>? RefreshForCap = null,
    int? RefreshMinHz = null,
    int? RefreshMaxHz = null,
    int? CurrentRefreshHz = null,
    IReadOnlyList<int>? RefreshRates = null);

/// <summary>What answers the unified row's two modes.</summary>
public interface ISteamFrameLimitBackend
{
    /// <summary>Applies a frame cap, or zero to switch the limit off.</summary>
    /// <param name="fps">The cap, or 0 for off.</param>
    /// <param name="persistence">Which profile to keep it in.</param>
    /// <param name="correlationId">Correlates the command across the backend's log.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetFrameLimitAsync(
        int fps,
        SteamSettingPersistence persistence,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Applies a refresh rate chosen directly, in the row's refresh mode.</summary>
    /// <param name="hz">The rate, one of the published <see cref="SteamFrameLimitState.RefreshRates"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetRefreshRateAsync(int hz, CancellationToken cancellationToken);
}

/// <summary>SteamOS's unified frame-limit row on the Performance tab, hand-built on Valve's primitives.</summary>
/// <remarks>
/// Deliberately not Valve's own component: that one is a notch slider fed by
/// <c>fps_limit_options</c>, and a free 30-120 range made it unusable. This row is built from
/// Valve's slider and toggle fields and labelled by Valve's own tokens, so it reads as native.
/// </remarks>
public static class SteamFrameLimitRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "wsgm.native-qam.frame-limit";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setFrameLimit", "setRefreshRate"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "frameLimit",
        "native-qam-frame-limit-v1:performance-actions+performance-root+valve-slider",
        "wsgm_native_frame_limit_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamFrameLimitState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamFrameLimitState);

    /// <summary>Declares the row as one module: the patch, the state, and the answers.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the row's writes.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamFrameLimitState?>> read,
        ISteamFrameLimitBackend backend,
        string id = "frame-limit")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamFrameLimitState),
            ],
            commands:
            [
                new(PatchId, "setFrameLimit", (request, cancellationToken) =>
                    SteamSurfaceModule.TryReadValueWrite(
                        request.Payload, out int fps, out SteamSettingPersistence persistence)
                        ? backend.SetFrameLimitAsync(
                            fps, persistence, request.ToCorrelationId(), cancellationToken)
                        : SteamSurfaceModule.Invalid("The frame-limit payload is invalid.")),
                new(PatchId, "setRefreshRate", (request, cancellationToken) =>
                    SteamSurfaceModule.TryReadValueWrite(request.Payload, out int hz, out _)
                        ? backend.SetRefreshRateAsync(hz, cancellationToken)
                        : SteamSurfaceModule.Invalid("The refresh-rate payload is invalid.")),
            ]);
    }
}
