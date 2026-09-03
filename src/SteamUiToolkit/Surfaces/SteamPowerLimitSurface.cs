using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What Valve's TDP rows read out of the SteamOS Manager: whether a limit exists, and its range.</summary>
/// <remarks>
/// The rows themselves bind two client settings Steam persists (<c>steamos_tdp_limit_enabled</c>
/// and <c>steamos_tdp_limit</c>); this state only decides whether they appear and what the slider
/// spans. The current watts are not published here because the row shows what Steam stored, and
/// the gate brings the hardware to that value rather than the other way round.
/// </remarks>
/// <param name="Available">Whether a sustained power limit can be written at all. False hides the rows.</param>
/// <param name="MinimumWatts">Lowest limit the slider offers. Must be positive for the rows to appear.</param>
/// <param name="MaximumWatts">Highest limit the slider offers.</param>
public sealed record SteamPowerLimitState(
    bool Available,
    int? MinimumWatts,
    int? MaximumWatts);

/// <summary>What routes Valve's TDP rows to hardware.</summary>
public interface ISteamPowerLimitBackend
{
    /// <summary>Applies the limit Steam's rows hold, or releases it.</summary>
    /// <param name="watts">The watts the slider holds — still carried when the switch is off.</param>
    /// <param name="enabled">Whether the limit applies. Off means release the cap, not apply zero:
    /// a limit switched off is not a limit of zero watts.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome; the gate retries an unsuccessful forward on its own schedule.</returns>
    Task<SteamUiCommandResult> SetPrimaryLimitAsync(
        int watts,
        bool enabled,
        CancellationToken cancellationToken);
}

/// <summary>Valve's own TDP toggle and slider on the Performance tab, backed by the consumer's power limit.</summary>
/// <remarks>
/// Two halves of one mechanism. The gate overlays the SteamOS Manager's <c>GetState</c> answer with
/// the published availability and range — merging into the real reply, never replacing it, because
/// it carries fields a fabricated one would zero — invalidates the query that caches it, and watches
/// the two client settings Valve's rows write, forwarding a change as <c>setPrimaryLimit</c>. The
/// row patch mounts Valve's toggle and slider exports. Both share this one patch id and state.
/// </remarks>
public static class SteamPowerLimitSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "wsgm.native-qam.tdp";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setPrimaryLimit"];

    /// <summary>The SteamOS Manager RPC answer Valve's TDP rows read availability and range from.</summary>
    /// <remarks>
    /// Verification requires the overlay to be the method actually on the service; the settings
    /// watch is reported but not required, since losing it costs the write path, not the row.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "wsgm.native-qam.steamos-manager-state",
        gateName: "steamOsManager",
        fingerprint: "native-qam-steamos-manager-v1:service+tdp-row+query-layer+own-getstate",
        // The service is matched by surface, not by export name: module 90389 exports both the
        // Manager and a Telemetry service and both have GetState, so the screen-reader method is
        // what separates them. The query layer must be reachable because the row's answer is cached
        // and a state change that cannot invalidate it never reaches the screen.
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("wsgm_steamos_manager_probe_")}}
              let manager=null;
              try{
                for(const value of Object.values(req('90389')||{})){
                  if(value&&typeof value==='object'
                    &&typeof value.GetState==='function'
                    &&typeof value.RefreshScreenReaderAutoLocale==='function'){manager=value;break;}
                }
              }catch{}
              let queryLayer=false;
              try{const q=req('21371');queryLayer=typeof q?.L?.invalidateQueries==='function';}catch{}
              return JSON.stringify({
                managerFound:!!manager,
                // Valve's own method, or one of our overlays that still carries it. Requiring the
                // PRE-patch shape here is the self-incompatibility trap this project has already paid
                // for twice: a successful apply would invalidate its own probe, and the next
                // compatibility pass would tear down what it had just installed.
                // The carried original is the claim primitive's property snapshot ({value}), or a
                // bare function from a bridge older than the snapshot. Accepting only the function
                // form re-created the loop: every successful apply read as irreplaceable two seconds
                // later and the row was torn down and rebuilt on a ~2-second cycle (device, 2026-09-01).
                getStateReplaceable:!!manager&&(typeof manager.GetState==='function')
                  &&(manager.GetState.__wsgmOwnedGetState!==true
                    ||typeof manager.GetState.__wsgmOriginalGetState==='function'
                    ||typeof (manager.GetState.__wsgmOriginalGetState||{}).value==='function'),
                queryLayer,
                tdpRow:count(['is_tdp_limit_available','tdp_limit_min','tdp_limit_max'])
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamGatePatch.Flag(root, "managerFound")
            && root.TryGetProperty("tdpRow", out JsonElement row)
            && row.TryGetInt32(out int rows)
            && rows > 0
            && SteamGatePatch.Flag(root, "queryLayer")
            && SteamGatePatch.Flag(root, "getStateReplaceable"),
        verifyOk: "status.installed&&status.getStateOverlaid",
        removeOk: "!status.getStateOverlaid",
        subject: "SteamOS Manager state");

    /// <summary>Valve's power-limit toggle and slider pair.</summary>
    /// <remarks>
    /// Not gated by <c>SystemPerfStore</c> at all: both halves read availability and the watt range
    /// out of the SteamOS Manager RPC and write the <c>steamos_tdp_limit</c> client settings, so
    /// this and <see cref="Patch"/> are one mechanism in two halves.
    /// </remarks>
    public static SteamQuickAccessRowPatch ValveRows { get; } = new(
        "wsgm.native-qam.valve-tdp",
        "valveTdp",
        "native-qam-valve-tdp-v1:performance-actions+performance-root+valve-tdp-pair",
        "wsgm_native_valve_tdp_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPowerLimitState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPowerLimitState);

    /// <summary>Declares the surface as one module: the gate, Valve's rows, the state and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What routes the rows' writes to hardware.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamPowerLimitState?>> read,
        ISteamPowerLimitBackend backend,
        string id = "power-limit")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch, ValveRows],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPowerLimitState),
            ],
            commands:
            [
                new(PatchId, "setPrimaryLimit", (request, cancellationToken) =>
                    TryReadPowerLimitPayload(request.Payload, out int watts, out bool limitEnabled)
                        ? backend.SetPrimaryLimitAsync(watts, limitEnabled, cancellationToken)
                        : SteamSurfaceModule.Invalid("The primary power-limit payload is invalid.")),
            ]);
    }

    /// <summary>Reads the power-limit payload: the watts and the switch beside them.</summary>
    /// <remarks>
    /// The switch is not optional: a limit switched off still carries the watts the slider holds,
    /// and reading only the number would apply a cap the user had just turned off.
    /// </remarks>
    private static bool TryReadPowerLimitPayload(
        JsonElement payload,
        out int watts,
        out bool enabled)
    {
        watts = default;
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("watts", out JsonElement wattsProperty)
            || wattsProperty.ValueKind != JsonValueKind.Number
            || !wattsProperty.TryGetInt32(out watts)
            || !payload.TryGetProperty("enabled", out JsonElement enabledProperty)
            || enabledProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        enabled = enabledProperty.ValueKind is JsonValueKind.True;
        return true;
    }
}
