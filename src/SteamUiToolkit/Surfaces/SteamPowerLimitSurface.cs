using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One independently writable power limit, in watts.</summary>
/// <param name="Available">Whether the control may be operated.</param>
/// <param name="MinimumWatts">Lowest supported wattage.</param>
/// <param name="MaximumWatts">Highest supported wattage, at most 200.</param>
/// <param name="StepWatts">Increment between supported values.</param>
/// <param name="ObservedWatts">Hardware readback, or null when unknown.</param>
/// <param name="Progress">Command progress, including applying or uncertain.</param>
/// <param name="StatusText">A bounded explanation of availability or the last outcome.</param>
public sealed record SteamPowerLimitRangeState(
    bool Available,
    int? MinimumWatts,
    int? MaximumWatts,
    int? StepWatts,
    int? ObservedWatts,
    string Progress,
    string StatusText);

/// <summary>Observed sustained and boost power limits shown in Quick Access.</summary>
/// <param name="Sustained">The sustained power limit, PL1.</param>
/// <param name="Boost">The boost power limit, PL2.</param>
/// <param name="Unified">Whether the primary slider controls the coordinated power pair.</param>
/// <param name="CanSelectMode">Whether the backend supports manual mode selection.</param>
public sealed record SteamPowerLimitState(
    SteamPowerLimitRangeState Sustained,
    SteamPowerLimitRangeState Boost,
    bool Unified = false,
    bool CanSelectMode = false);

/// <summary>Routes explicit power slider edits through the consumer's hardware coordinator.</summary>
public interface ISteamPowerLimitBackend
{
    /// <summary>Saves manual power mode without applying a wattage.</summary>
    /// <param name="unified">Whether to use coordinated power targets.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The persistence outcome.</returns>
    Task<SteamUiCommandResult> SetUnifiedModeAsync(bool unified, CancellationToken cancellationToken)
    {
        return Task.FromResult(SteamUiCommandResult.Refused);
    }

    /// <summary>Sets sustained power, PL1.</summary>
    /// <param name="watts">The requested wattage on a published step.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome. Uncertain writes must not be retried automatically.</returns>
    Task<SteamUiCommandResult> SetPrimaryLimitAsync(int watts, CancellationToken cancellationToken);

    /// <summary>Sets boost power, PL2.</summary>
    /// <param name="watts">The requested wattage on a published step.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome. Uncertain writes must not be retried automatically.</returns>
    Task<SteamUiCommandResult> SetBoostLimitAsync(int watts, CancellationToken cancellationToken);
}

/// <summary>Two hardware-backed power sliders built from Valve's field primitives.</summary>
/// <remarks>
///     Observations drive both sliders, including after profile changes. Only a completed user edit
///     sends a command; publication and mounting never apply Steam's persisted TDP setting.
/// </remarks>
public static class SteamPowerLimitSurface
{
    /// <summary>The patch id used for publication and commands.</summary>
    public const string PatchId = "steam-ui.power-limit";

    /// <summary>The exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setUnifiedMode", "setPrimaryLimit", "setBoostLimit"];

    /// <summary>The sustained and boost sliders on the Performance page.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "powerLimit",
        "native-qam-power-limits-v3:performance-actions+performance-root+unified-mode",
        "steam_ui_power_limits_probe_");

    /// <summary>Serializes both independent limits for the injected controls.</summary>
    /// <param name="state">The observed state.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPowerLimitState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPowerLimitState);
    }

    /// <summary>Declares the rows, state publication and explicit write commands.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads current state, or null to skip publication.</param>
    /// <param name="backend">The hardware command backend.</param>
    /// <param name="id">The module id.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamPowerLimitState?>> read,
        ISteamPowerLimitBackend backend,
        string id = "power-limit")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamPowerLimitState,
            [Patch],
            [
                SteamSurfaceModule.Command(
                    PatchId,
                    "setUnifiedMode",
                    static (JsonElement payload, out bool unified) =>
                    {
                        unified = false;
                        return SteamUiPayload.HasExactly(payload, 1)
                               && SteamUiPayload.TryReadBoolean(payload, "unified", out unified);
                    },
                    backend.SetUnifiedModeAsync,
                    "The manual power mode payload is invalid."),
                SteamSurfaceModule.Command<int>(
                    PatchId,
                    "setPrimaryLimit",
                    TryReadWatts,
                    backend.SetPrimaryLimitAsync,
                    "The sustained power-limit payload is invalid."),
                SteamSurfaceModule.Command<int>(
                    PatchId,
                    "setBoostLimit",
                    TryReadWatts,
                    backend.SetBoostLimitAsync,
                    "The boost power-limit payload is invalid.")
            ]);
    }

    private static bool TryReadWatts(JsonElement payload, out int watts)
    {
        watts = default;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadInt(payload, "watts", 1, 200, out watts);
    }
}
