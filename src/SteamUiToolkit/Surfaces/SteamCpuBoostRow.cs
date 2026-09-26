using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The Performance menu's processor boost-mode dropdown.</summary>
/// <remarks>
///     The power-profile shape plus the per-game marker: host-named choices, the one in effect, a
///     line of status, and the host's setting id while the running game's own profile supplies the
///     value. The host owns what the choices mean and the OS write.
/// </remarks>
/// <param name="Available">Whether selection is enabled. False keeps the status visible.</param>
/// <param name="Options">At most 64 modes with unique identifiers.</param>
/// <param name="Current">The mode in effect, or empty when the machine is set to something the host does not offer.</param>
/// <param name="StatusText">Current state or the last failure.</param>
/// <param name="OverrideId">The host's setting id while the running game overrides the value, else null.</param>
public sealed record SteamCpuBoostState(
    bool Available,
    IReadOnlyList<SteamPowerProfileOption> Options,
    string Current,
    string StatusText,
    string? OverrideId);

/// <summary>Applies a host's processor boost modes.</summary>
public interface ISteamCpuBoostBackend
{
    /// <summary>Selects and verifies one published mode.</summary>
    /// <param name="option">Stable mode id.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <returns>The verified outcome or a refusal.</returns>
    Task<SteamUiCommandResult> SetCpuBoostAsync(string option, CancellationToken cancellationToken);
}

/// <summary>A processor boost-mode dropdown on Steam's Performance tab, using Valve's dropdown field.</summary>
public static class SteamCpuBoostRow
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.cpu-boost";

    /// <summary>The row's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setCpuBoost"];

    /// <summary>Reversible registration with the shared Performance row host.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "cpuBoost",
        "native-qam-cpu-boost-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_cpu_boost_probe_");

    /// <summary>Serializes state for the injected component.</summary>
    /// <param name="state">State to publish.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamCpuBoostState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamCpuBoostState);
    }

    /// <summary>Declares the patch, state publication and command handler.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads fresh host state.</param>
    /// <param name="backend">Applies mode selections.</param>
    /// <param name="id">Module identity.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled, Func<ValueTask<SteamCpuBoostState?>> read,
        ISteamCpuBoostBackend backend, string id = "cpu-boost")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id, PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamCpuBoostState, [Patch],
            [
                SteamSurfaceModule.Command<string>(PatchId, "setCpuBoost", SteamUiPayload.TryReadTarget,
                    backend.SetCpuBoostAsync, "The processor boost payload is invalid.")
            ]);
    }
}
