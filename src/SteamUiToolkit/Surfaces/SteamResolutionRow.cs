using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The display-resolution row's state.</summary>
/// <param name="Available">Whether the row can be drawn at all.</param>
/// <param name="Options">Resolutions to offer, as <c>WIDTHxHEIGHT</c>, at most 64. Fewer than two
/// hides the row: a picker with nothing to pick is worse than no picker.</param>
/// <param name="Current">The resolution in force, or empty when it cannot be read. A value outside
/// <paramref name="Options"/> selects nothing rather than the first entry.</param>
/// <param name="StatusText">Why the row is unavailable, when it is.</param>
public sealed record SteamResolutionState(
    bool Available,
    IReadOnlyList<string> Options,
    string Current,
    string StatusText);

/// <summary>What answers the resolution dropdown.</summary>
public interface ISteamResolutionBackend
{
    /// <summary>Applies a resolution named as <c>WIDTHxHEIGHT</c>, exactly as the row offered it.</summary>
    /// <param name="option">The chosen option.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetResolutionAsync(string option, CancellationToken cancellationToken);
}

/// <summary>A display-resolution dropdown in Quick Settings, hand-built on Valve's dropdown field.</summary>
/// <remarks>
/// SteamOS drives resolution through gamescope and the client ships no component for it, so there
/// is nothing to mount and the row is this library's own. Its label is deliberately not localized:
/// the client has no token meaning "display resolution", and passing a token that does not exist
/// makes Steam log an unresolved token on every render.
/// </remarks>
public static class SteamResolutionRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.resolution";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setResolution"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "resolution",
        "native-qam-resolution-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_resolution_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamResolutionState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamResolutionState);

    /// <summary>Declares the row as one module: the patch, the state, and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What applies a chosen resolution.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamResolutionState?>> read,
        ISteamResolutionBackend backend,
        string id = "resolution")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamResolutionState),
            ],
            commands:
            [
                new(PatchId, "setResolution", (request, cancellationToken) =>
                    SteamUiPayload.TryReadTarget(request.Payload, out string option)
                        ? backend.SetResolutionAsync(option, cancellationToken)
                        : SteamSurfaceModule.Invalid("The resolution payload is invalid.")),
            ]);
    }
}
