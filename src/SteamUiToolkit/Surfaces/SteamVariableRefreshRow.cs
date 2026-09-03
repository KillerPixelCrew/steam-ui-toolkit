using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The variable-refresh switch as the row renders it.</summary>
/// <param name="Available">Whether a backend capability backs the switch at all. False hides the row.</param>
/// <param name="Enabled">What the device reports now, not what was last asked for.</param>
/// <param name="Progress">Command progress; the row disables itself while <c>queued</c>, <c>applying</c> or <c>replacing</c>.</param>
/// <param name="StatusText">One line describing the state, or why the row cannot be operated.</param>
public sealed record SteamVariableRefreshState(
    bool Available,
    bool Enabled,
    string Progress,
    string StatusText);

/// <summary>What answers the variable-refresh switch.</summary>
public interface ISteamVariableRefreshBackend
{
    /// <summary>Turns variable refresh rate on or off.</summary>
    /// <param name="enabled">The wanted state.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome. Awaited before the switch settles, so a refused write leaves it
    /// where the hardware actually is.</returns>
    Task<SteamUiCommandResult> SetVariableRefreshRateAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>A variable-refresh toggle on the Performance tab, labelled by Valve's own token.</summary>
/// <remarks>
/// Valve ships one, and it cannot be used: its component is gated on a react-query over
/// <c>SteamClient.System.DisplayManager</c>, whose <c>GetState</c> the Windows client does not
/// define — the query never succeeds and the component returns null before it reads a single
/// published field (live-probed 2026-08-30). This row is built from Valve's toggle field instead.
/// </remarks>
public static class SteamVariableRefreshRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.variable-refresh";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setVariableRefreshRate"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "vrr",
        "native-qam-vrr-v1:performance-actions+performance-root+valve-toggle",
        "steam_ui_variable_refresh_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamVariableRefreshState state) =>
        JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamVariableRefreshState);

    /// <summary>Declares the row as one module: the patch, the state, and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the switch.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamVariableRefreshState?>> read,
        ISteamVariableRefreshBackend backend,
        string id = "vrr")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamVariableRefreshState),
            ],
            commands:
            [
                new(PatchId, "setVariableRefreshRate", (request, cancellationToken) =>
                    SteamUiPayload.TryReadEnabled(request.Payload, out bool on)
                        ? backend.SetVariableRefreshRateAsync(on, cancellationToken)
                        : SteamSurfaceModule.Invalid("The variable-refresh payload is invalid.")),
            ]);
    }
}
