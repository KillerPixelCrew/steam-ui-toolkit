using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The Performance menu's processor core-preference dropdown.</summary>
/// <remarks>
/// The same shape as the power-profile row because it is the same control: a list of host-named
/// choices, the one currently observed, and a line of status. The host owns what the choices mean.
/// </remarks>
/// <param name="Available">Whether selection is enabled. False keeps the status visible.</param>
/// <param name="Options">At most 64 preferences with unique identifiers.</param>
/// <param name="Current">Observed preference id, or empty when the machine is set to something the host does not offer.</param>
/// <param name="StatusText">Current state or the last failure.</param>
public sealed record SteamHybridCoreState(
    bool Available, IReadOnlyList<SteamPowerProfileOption> Options, string Current, string StatusText);

/// <summary>Applies a host's processor core preferences.</summary>
public interface ISteamHybridCoreBackend
{
    /// <summary>Selects and verifies one published preference.</summary>
    /// <param name="option">Stable preference id.</param>
    /// <param name="cancellationToken">Cancels before the write starts.</param>
    /// <returns>The verified outcome or a refusal.</returns>
    Task<SteamUiCommandResult> SetHybridCoresAsync(string option, CancellationToken cancellationToken);
}

/// <summary>A processor core-preference dropdown on Steam's Performance tab, using Valve's dropdown field.</summary>
public static class SteamHybridCoreRow
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.hybrid-cores";

    /// <summary>The row's exact command vocabulary.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setHybridCores"];

    /// <summary>Reversible registration with the shared Performance row host.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId, "hybridCores",
        "native-qam-hybrid-cores-v1:performance-actions+performance-root+valve-dropdown",
        "steam_ui_hybrid_cores_probe_");

    /// <summary>Serializes state for the injected component.</summary>
    /// <param name="state">State to publish.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamHybridCoreState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamHybridCoreState);

    /// <summary>Declares the patch, state publication and command handler.</summary>
    /// <param name="enabled">Whether publication is enabled.</param>
    /// <param name="read">Reads fresh host state.</param>
    /// <param name="backend">Applies preference selections.</param>
    /// <param name="id">Module identity.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled, Func<ValueTask<SteamHybridCoreState?>> read,
        ISteamHybridCoreBackend backend, string id = "hybrid-cores")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(id, patches: [Patch],
            publications: [SteamSurfaceModule.Publication(
                PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamHybridCoreState)],
            commands: [new(PatchId, "setHybridCores", (request, cancellationToken) =>
                SteamUiPayload.TryReadTarget(request.Payload, out string option)
                    ? backend.SetHybridCoresAsync(option, cancellationToken)
                    : SteamSurfaceModule.Invalid("The processor core preference payload is invalid."))]);
    }
}
