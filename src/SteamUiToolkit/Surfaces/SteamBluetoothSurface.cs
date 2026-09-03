using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One Bluetooth device as Steam's own pairing panel renders it.</summary>
/// <remarks>
/// The panel reads paired and connected to decide which list a device belongs in, and the type to
/// choose its icon. Signal strength and battery are not on this record: the injected side reports
/// none, because a fabricated strength would order the list by a number that means nothing.
/// </remarks>
/// <param name="Id">Stable device identifier.</param>
/// <param name="Name">Device name, or its address when it reports none.</param>
/// <param name="Mac">Hardware address.</param>
/// <param name="EType">Steam's device-type enumeration value; 0 is the generic device.</param>
/// <param name="IsPaired">Whether the device is paired.</param>
/// <param name="IsConnected">Whether it has a live connection.</param>
public readonly record struct SteamBluetoothDevice(
    string Id,
    string Name,
    string Mac,
    int EType,
    bool IsPaired,
    bool IsConnected);

/// <summary>Bluetooth as Steam's own pairing panel expects to receive it.</summary>
/// <remarks>
/// <paramref name="Available"/> means "this machine has a radio the backend can drive", never
/// "the radio is on". Wiring it to the on/off state removes the entire settings page and the
/// toggle with it — the exact control needed to turn the radio back on.
/// </remarks>
/// <param name="Available">Whether Bluetooth can be observed and changed at all.</param>
/// <param name="Enabled">Whether the radio is on.</param>
/// <param name="Discovering">Whether a scan is running.</param>
/// <param name="Devices">Known devices, paired and discovered alike.</param>
public sealed record SteamBluetoothState(
    bool Available,
    bool Enabled,
    bool Discovering,
    IReadOnlyList<SteamBluetoothDevice> Devices);

/// <summary>What answers Steam's Bluetooth panel.</summary>
/// <remarks>
/// Every device operation receives the id the published state named; the payload shapes were read
/// from the client's bundle (2026-09-03): every device operation sends <c>{device}</c>,
/// <c>SetTrusted</c> sends <c>{device, trusted}</c>, <c>SetWakeAllowed</c> sends
/// <c>{device, allowed}</c> and <c>SetDiscovering</c> sends <c>{enabled}</c>. Trusted and
/// wake-allowed are BlueZ concepts; their default implementations accept and do nothing, because
/// refusing them makes Steam's UI report a failure for a control that was never going to change
/// anything on a platform without the concept.
/// </remarks>
public interface ISteamBluetoothBackend
{
    /// <summary>Starts or stops discovery.</summary>
    /// <param name="discovering">Whether Steam wants a scan running.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetDiscoveringAsync(bool discovering, CancellationToken cancellationToken);

    /// <summary>Steam's Pair button.</summary>
    /// <param name="deviceId">The device to pair.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> PairAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Steam cancelled a pairing in progress.</summary>
    /// <param name="deviceId">The device whose pairing to cancel.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> CancelPairAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Connects a paired device.</summary>
    /// <param name="deviceId">The device to connect.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> ConnectAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Disconnects a connected device.</summary>
    /// <param name="deviceId">The device to disconnect.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> DisconnectAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Unpairs a device.</summary>
    /// <param name="deviceId">The device to forget.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> ForgetAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Steam's trusted flag. Accepted and ignored unless overridden.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="trusted">The wanted flag.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome; applied by default.</returns>
    Task<SteamUiCommandResult> SetTrustedAsync(
        string deviceId,
        bool trusted,
        CancellationToken cancellationToken) =>
        Task.FromResult(SteamUiCommandResult.Applied);

    /// <summary>Steam's wake-allowed flag. Accepted and ignored unless overridden.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="allowed">The wanted flag.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome; applied by default.</returns>
    Task<SteamUiCommandResult> SetWakeAllowedAsync(
        string deviceId,
        bool allowed,
        CancellationToken cancellationToken) =>
        Task.FromResult(SteamUiCommandResult.Applied);
}

/// <summary>Steam's own Bluetooth page and Quick Settings panel, backed by the consumer's radio.</summary>
/// <remarks>
/// The service, its message shapes and every operation ship in the Windows client; only the
/// backend is missing, so <c>GetState</c> answers unavailable with empty adapters and devices.
/// Its handler exports are message descriptors rather than registration hooks, so the service
/// cannot be implemented — the gate replaces the stub's methods instead, publishes one synthetic
/// adapter to hang the radio toggle on, and invalidates the react-query the panel caches
/// availability in, because that cache has an infinite stale time.
/// </remarks>
public static class SteamBluetoothSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.bluetooth";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "setDiscovering",
        "pair",
        "cancelPair",
        "connect",
        "disconnect",
        "forget",
        "setTrusted",
        "setWakeAllowed",
    ];

    /// <summary>The gate that replaces the stub methods behind Steam's own Bluetooth pairing UI.</summary>
    /// <remarks>
    /// The query cache must be reachable: availability rides a react-query with infinite stale
    /// time, so without an invalidation the row keeps reading the unavailable answer no matter what
    /// the methods return.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.bluetooth-manager-service",
        gateName: "bluetooth",
        fingerprint: "steam-bluetooth-v1:operations+writable-stub+reachable-cache",
        probeExpression: $$"""
            {{SteamUiProbeJs.Preamble("steam_ui_bluetooth_probe_")}}
              const RF=req('60517')&&req('60517').RF;
              if(!RF)return JSON.stringify({error:'bluetooth service stub unavailable'});
              const ops=['GetState','SetDiscovering','Pair','CancelPair','Connect','Disconnect',
                'Forget','SetTrusted','SetWakeAllowed','GetDeviceDetails'];
              const missing=ops.filter(n=>typeof RF[n]!=='function');
              const d=Object.getOwnPropertyDescriptor(RF,'GetState');
              let cache=false;
              try{cache=typeof req('21371').L.invalidateQueries==='function';}catch{}
              return JSON.stringify({
                operationsPresent:missing.length===0,
                missing:missing,
                methodsWritable:!!d&&d.writable===true&&d.configurable===true,
                queryCacheReachable:cache
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamGatePatch.Flag(root, "operationsPresent")
            && SteamGatePatch.Flag(root, "methodsWritable")
            && SteamGatePatch.Flag(root, "queryCacheReachable"),
        verifyOk: "status.installed&&status.replaced>0",
        removeOk: "!status.installed",
        subject: "Bluetooth service");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamBluetoothState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamBluetoothState);

    /// <summary>Declares the surface as one module: the gate, the state, and the answers.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the panel's operations.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamBluetoothState?>> read,
        ISteamBluetoothBackend backend,
        string id = "bluetooth")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamBluetoothState),
            ],
            commands:
            [
                new(PatchId, "setDiscovering", (request, cancellationToken) =>
                    SteamUiPayload.TryReadEnabled(request.Payload, out bool discovering)
                        ? backend.SetDiscoveringAsync(discovering, cancellationToken)
                        : SteamSurfaceModule.Invalid("The discovery payload is invalid.")),
                Device("pair", backend.PairAsync),
                Device("cancelPair", backend.CancelPairAsync),
                Device("connect", backend.ConnectAsync),
                Device("disconnect", backend.DisconnectAsync),
                Device("forget", backend.ForgetAsync),
                DeviceFlag("setTrusted", "trusted", backend.SetTrustedAsync),
                DeviceFlag("setWakeAllowed", "allowed", backend.SetWakeAllowedAsync),
            ]);
    }

    private static SteamUiCommandHandler Device(
        string command,
        Func<string, CancellationToken, Task<SteamUiCommandResult>> operation) =>
        new(PatchId, command, (request, cancellationToken) =>
            SteamUiPayload.TryReadBoundedString(request.Payload, "device", 256, out string deviceId)
                && SteamUiPayload.HasExactly(request.Payload, 1)
                ? operation(deviceId, cancellationToken)
                : SteamSurfaceModule.Invalid("The Bluetooth device payload is invalid."));

    private static SteamUiCommandHandler DeviceFlag(
        string command,
        string flagName,
        Func<string, bool, CancellationToken, Task<SteamUiCommandResult>> operation) =>
        new(PatchId, command, (request, cancellationToken) =>
            SteamUiPayload.TryReadBoundedString(request.Payload, "device", 256, out string deviceId)
                && request.Payload.TryGetProperty(flagName, out JsonElement flag)
                && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                && SteamUiPayload.HasExactly(request.Payload, 2)
                ? operation(deviceId, flag.ValueKind is JsonValueKind.True, cancellationToken)
                : SteamSurfaceModule.Invalid("The Bluetooth device payload is invalid."));
}
