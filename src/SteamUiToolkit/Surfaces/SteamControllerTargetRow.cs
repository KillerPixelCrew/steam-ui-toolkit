using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One choice in the controller-target dropdown.</summary>
/// <param name="Id">The identifier sent back on selection: 1-64 characters of ASCII letters,
/// digits, <c>.</c>, <c>_</c> and <c>-</c>, unique within the state.</param>
/// <param name="Label">What the dropdown shows.</param>
/// <param name="Available">Whether it may be chosen. Unavailable options are not offered.</param>
public sealed record SteamControllerTargetOption(
    string Id,
    string Label,
    bool Available);

/// <summary>The controller-target dropdown's state.</summary>
/// <param name="Available">Whether the row can be operated at all. False hides it.</param>
/// <param name="Targets">At most 8 options.</param>
/// <param name="SelectedTarget">The stored choice, an option id or empty.</param>
/// <param name="ObservedTarget">The target that actually exists right now, an option id or empty.
/// Shown in preference to the selection, so a target that was chosen but never came up is not
/// hidden.</param>
/// <param name="Progress">Command progress; the row disables itself while <c>queued</c>, <c>applying</c> or <c>replacing</c>.</param>
/// <param name="StatusText">One line describing the state, or why the row cannot be operated.</param>
/// <param name="ApplicationRestartRequired">Whether a running application holds the previous target,
/// so the change reaches it only on the next launch. The row says so.</param>
public sealed record SteamControllerTargetState(
    bool Available,
    IReadOnlyList<SteamControllerTargetOption> Targets,
    string SelectedTarget,
    string ObservedTarget,
    string Progress,
    string StatusText,
    bool ApplicationRestartRequired);

/// <summary>What answers the controller-target dropdown.</summary>
public interface ISteamControllerTargetBackend
{
    /// <summary>Stores and applies the chosen target.</summary>
    /// <param name="target">One of the published option ids.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetControllerTargetAsync(string target, CancellationToken cancellationToken);
}

/// <summary>A controller-target dropdown on the Performance tab, labelled by Valve's controller section title.</summary>
/// <remarks>
/// For a host that presents the physical controller to games as some emulated device, this is
/// where the user picks which. Built on Valve's dropdown field; its probe requires the controller
/// presentation module rather than the performance actions.
/// </remarks>
public static class SteamControllerTargetRow
{
    /// <summary>The patch id this row publishes under and answers commands for.</summary>
    public const string PatchId = "wsgm.native-qam.controller-target";

    /// <summary>The exact command vocabulary the injected row sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setControllerTarget"];

    /// <summary>The row patch.</summary>
    public static SteamQuickAccessRowPatch Patch { get; } = new(
        PatchId,
        "controllerTarget",
        "native-qam-controller-target-v1:controller-presentation+performance-root+valve-dropdown",
        "wsgm_native_controller_target_probe_",
        "controllerPresentation",
        [
            "#QuickAccess_Tab_Settings_Section_Controller_Title",
            "#QuickAccess_ReorderControllers_Button",
            "#QuickAccess_Tab_Perf_Title",
        ]);

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamControllerTargetState state) =>
        JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamControllerTargetState);

    /// <summary>Declares the row as one module: the patch, the state, and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What applies a chosen target.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamControllerTargetState?>> read,
        ISteamControllerTargetBackend backend,
        string id = "controller-target")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamControllerTargetState),
            ],
            commands:
            [
                new(PatchId, "setControllerTarget", (request, cancellationToken) =>
                    SteamUiPayload.TryReadTarget(request.Payload, out string target)
                        ? backend.SetControllerTargetAsync(target, cancellationToken)
                        : SteamSurfaceModule.Invalid("The controller-target payload is invalid.")),
            ]);
    }
}
