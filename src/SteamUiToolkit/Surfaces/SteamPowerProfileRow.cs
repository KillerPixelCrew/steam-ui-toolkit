using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One host-provided power profile.</summary>
/// <param name="Id">Stable identifier, 1-64 ASCII letters, digits, dots, underscores or hyphens.</param>
/// <param name="Label">Display name, independent of identity; the injected row bounds it to 240 characters.</param>
public sealed record SteamPowerProfileOption(string Id, string Label);

/// <summary>The Performance menu's power-profile dropdown.</summary>
/// <param name="Available">Whether selection is enabled. False keeps the status visible.</param>
/// <param name="Options">At most 64 profiles with unique identifiers.</param>
/// <param name="Current">Observed profile id, or empty when unknown.</param>
/// <param name="StatusText">Current state or the last failure.</param>
public sealed record SteamPowerProfileState(
    bool Available, IReadOnlyList<SteamPowerProfileOption> Options, string Current, string StatusText);

/// <summary>Applies a host's power profiles.</summary>
public interface ISteamPowerProfileBackend
{
    /// <summary>Selects and verifies one published profile.</summary>
    /// <param name="option">Stable profile id.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <returns>The verified outcome or a refusal.</returns>
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
        "native-qam-power-profile-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_power_profile_probe_");

    /// <summary>Serializes state for the injected component.</summary>
    /// <param name="state">State to publish.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPowerProfileState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPowerProfileState);

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
        return new SteamUiModule(id, patches: [Patch],
            publications: [SteamSurfaceModule.Publication(
                PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPowerProfileState)],
            commands: [new(PatchId, "setPowerProfile", (request, cancellationToken) =>
                SteamUiPayload.TryReadTarget(request.Payload, out string option)
                    ? backend.SetPowerProfileAsync(option, cancellationToken)
                    : SteamSurfaceModule.Invalid("The power-profile payload is invalid."))]);
    }
}
