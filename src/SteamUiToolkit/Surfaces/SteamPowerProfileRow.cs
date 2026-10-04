using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One host-provided power profile.</summary>
/// <param name="Id">Stable identifier, one or more ASCII letters, digits, dots, underscores or hyphens.</param>
/// <param name="Label">Display name, independent of identity.</param>
/// <param name="Selectable">
///     Whether the user may choose it. An option that is not selectable describes a state the host
///     reports but cannot be asked for: a dropdown lists it only while it is that dropdown's current
///     value, and never sends it.
/// </param>
public sealed record SteamPowerProfileOption(string Id, string Label, bool Selectable = true);

/// <summary>The Performance menu's power-profile dropdown.</summary>
/// <param name="Available">Whether selection is enabled. False keeps the status visible.</param>
/// <param name="Options">The profiles, with unique identifiers.</param>
/// <param name="Current">Observed profile id, or empty when unknown.</param>
/// <param name="StatusText">Current state or the last failure.</param>
public sealed record SteamPowerProfileState(
    bool Available,
    IReadOnlyList<SteamPowerProfileOption> Options,
    string Current,
    string StatusText);

/// <summary>Applies a host's power profiles.</summary>
public interface ISteamPowerProfileBackend
{
    /// <summary>Dispatches the selection of one published profile.</summary>
    /// <param name="option">Stable profile id.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <returns>
    ///     Success once the selection was dispatched, with the written value published as observed, or
    ///     why it could not be dispatched. No outcome waits on a readback.
    /// </returns>
    Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken);
}

/// <summary>A power-profile dropdown on Steam's Performance tab, using Valve's dropdown field.</summary>
public static class SteamPowerProfileRow
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.power-profile";

    /// <summary>The row's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setPowerProfile"];

    /// <summary>Reversible registration with the shared Performance row host.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "powerProfile",
        "steam-ui-power-profile-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_power_profile_probe_");

    /// <summary>Serializes state for the injected component.</summary>
    /// <param name="state">State to publish.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPowerProfileState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPowerProfileState);
    }

    /// <summary>Declares the patch, state publication and command handler.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads fresh host state.</param>
    /// <param name="backend">Applies profile selections.</param>
    /// <param name="id">Module identity.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled, Func<ValueTask<SteamPowerProfileState?>> read,
        ISteamPowerProfileBackend backend, string id = "power-profile")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id, PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPowerProfileState, [Patch],
            [
                SteamUiModuleBuilder.Command<string>(PatchId, "setPowerProfile", SteamUiPayload.TryReadTarget,
                    backend.SetPowerProfileAsync, "The power-profile payload is invalid.")
            ]);
    }
}
