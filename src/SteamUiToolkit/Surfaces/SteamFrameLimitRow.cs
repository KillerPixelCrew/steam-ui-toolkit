using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The frame-limit row: a cap slider and a switch for off.</summary>
/// <remarks>
///     One continuous slider bookended by the panel's limits, plus a separate switch for off, as SteamOS
///     draws it. There are no notches: the limiter holds any integer, and a cap changes no display state.
///     <para>
///         <paramref name="Progress" /> must be one of <c>idle</c>, <c>queued</c>, <c>applying</c>,
///         <c>deferred</c>, <c>applied</c>, <c>rejected</c>, <c>failed</c> or <c>external-change</c>; the
///         row rejects anything else and reports why in its render outcome. The list is exhaustive on
///         purpose: a host outcome missing from it is read as a malformed state and takes the whole row
///         down.
///     </para>
/// </remarks>
/// <param name="Available">Whether the row can be operated at all.</param>
/// <param name="MinimumFps">
///     Lowest cap the slider offers, or null when unknown. A pair with <paramref name="MaximumFps" />
///     .
/// </param>
/// <param name="MaximumFps">Highest cap the slider offers, or null when unknown.</param>
/// <param name="DesiredFps">
///     The cap asked for, 0 for off, or null when none. It need not lie between the bookends: a cap
///     the limiter really holds stretches them rather than invalidating the row, because the row is
///     where the user would correct it.
/// </param>
/// <param name="ObservedFps">
///     The cap the limiter reports, 0 for off, or null when unread. Stretches the bookends like
///     <paramref name="DesiredFps" />.
/// </param>
/// <param name="Progress">Command progress in the closed vocabulary above.</param>
/// <param name="Fault">The last failure's text, or empty.</param>
/// <param name="StatusText">One line describing the state, or why the row cannot be operated.</param>
/// <param name="LimitEnabled">
///     Whether a cap applies. Off is a switch of its own, so the slider keeps the cap the user last
///     chose.
/// </param>
/// <param name="Accent">
///     Whether the row is marked: its description is drawn in Steam's accent colour after the host's
///     <see cref="SteamQuickAccessLayout.AccentLabel" />.
/// </param>
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
    bool Accent = false);

/// <summary>What answers the frame-limit row.</summary>
public interface ISteamFrameLimitBackend
{
    /// <summary>Applies a frame cap, or zero to switch the limit off.</summary>
    /// <param name="fps">The cap, or 0 for off.</param>
    /// <param name="correlationId">Correlates the command across the backend's log.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>Where the value is kept is the backend's decision; the row only says what was chosen.</remarks>
    Task<SteamUiCommandResult> SetFrameLimitAsync(
        int fps,
        string correlationId,
        CancellationToken cancellationToken);

}

/// <summary>SteamOS's frame-limit row on the Performance tab, hand-built on Valve's primitives.</summary>
/// <remarks>
///     Deliberately not Valve's own component: that one is a notch slider fed by
///     <c>fps_limit_options</c>, and a free 30-120 range made it unusable. This row is built from
///     Valve's slider and toggle fields and labelled by Valve's own tokens, so it reads as native. It is
///     the layout kind <c>frameLimit</c>, mounted by <see cref="SteamQuickAccessRowsSurface.HostPatch" />
///     and drawn while this state is published.
/// </remarks>
public static class SteamFrameLimitRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.frame-limit";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setFrameLimit"];

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamFrameLimitState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamFrameLimitState);
    }

    /// <summary>Declares the row's state and answers. The component host draws it.</summary>
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
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamFrameLimitState,
            [],
            [
                new SteamUiCommandHandler(PatchId, "setFrameLimit", (request, cancellationToken) =>
                    TryReadValue(request.Payload, out var fps)
                        ? backend.SetFrameLimitAsync(fps, request.ToCorrelationId(), cancellationToken)
                        : Task.FromResult(SteamUiCommandResult.Invalid("The frame-limit payload is invalid.")))
            ]);
    }

    /// <summary>Reads the shape the row's write sends: <c>{value}</c> and nothing else.</summary>
    private static bool TryReadValue(JsonElement payload, out int value)
    {
        return SteamUiPayload.TryReadInt(payload, "value", int.MinValue, int.MaxValue, out value)
               && SteamUiPayload.HasExactly(payload, 1);
    }
}
