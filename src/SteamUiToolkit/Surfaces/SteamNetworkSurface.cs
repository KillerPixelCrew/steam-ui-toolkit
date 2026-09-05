using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One access point fed through Steam's own network-store ingestion path.</summary>
/// <param name="Ssid">The network name.</param>
/// <param name="Strength">Signal in Steam's four bands, 1-4; see <see cref="SteamNetworkSurface.StrengthFromPercent"/>.</param>
/// <param name="Secured">Whether the network requires a key.</param>
/// <param name="Connected">Whether this is the connected network. A connected entry also drives
/// the header's Wi-Fi indicator, which is empty on Windows without it.</param>
public sealed record SteamNetworkAccessPoint(
    string Ssid,
    int Strength,
    bool Secured,
    bool Connected);

/// <summary>The networks Steam's Internet page lists and the header indicator reads.</summary>
/// <param name="Networks">At most 24 entries; the injected side keeps the first 24.</param>
public sealed record SteamNetworkState(IReadOnlyList<SteamNetworkAccessPoint> Networks);

/// <summary>What answers Steam's Internet page: the scan lifetime.</summary>
/// <remarks>
/// Steam's page starts and stops scanning as it opens and closes, and the gate forwards exactly
/// that. Joining, forgetting and the radio toggle are not on this surface: Steam's Windows client
/// keeps its own wireless device and drives those itself.
/// </remarks>
public interface ISteamNetworkBackend
{
    /// <summary>Steam's network page opened and wants fresh results.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> StartScanAsync(CancellationToken cancellationToken);

    /// <summary>Steam's network page closed.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> StopScanAsync(CancellationToken cancellationToken);
}

/// <summary>Steam's own Wi-Fi surface — the Internet page and the header indicator — revealed and fed.</summary>
/// <remarks>
/// The Windows client tracks its wireless device and only
/// <c>get networkManagementAvailable(){return TS.IS_STEAMOS}</c> keeps the UI away; the gate
/// overrides that one getter. Every backend report then carries an empty access-point list, so
/// the gate writes the published networks through the store's own <c>SetDeviceInfo</c> with a
/// no-op <c>MarkAsNotPresent</c> that pins them across the backend's periodic reports.
/// </remarks>
public static class SteamNetworkSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.network";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["startScan", "stopScan"];

    /// <summary>The gate that reveals Steam's Wi-Fi surface by overriding one Deck-only store getter.</summary>
    /// <remarks>
    /// Reveals but does not populate on its own: the Windows backend reports an empty access-point
    /// list, so verification reports the surface rather than treating a revealed row over no
    /// networks as success. The getter must currently read false — a client that already reports
    /// network management available is one this gate leaves alone.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.network-availability",
        gateName: "network",
        fingerprint: "steam-network-gate-v1:configurable-getter+currently-hidden",
        probeExpression: $$"""
            (()=>{try{
              const store=window.SystemNetworkStore;
              if(!store)return JSON.stringify({error:'network store unavailable'});
              const d=Object.getOwnPropertyDescriptor(
                Object.getPrototypeOf(store),'networkManagementAvailable');
              return JSON.stringify({
                getterConfigurable:!!d&&d.configurable===true&&typeof d.get==='function',
                // False, or already overridden by US. A getter this gate installed is not evidence
                // that the client reports network management natively, and reading it that way made
                // this patch refuse itself after a successful apply and tear the network list down.
                currentlyHidden:store.networkManagementAvailable===false
                  ||(!!d&&!!d.get&&(d.get.__steamUiOwnedGetter===true||d.get.__wsgmOwnedGetter===true)),
                  // The __wsgm* spellings are the markers a build before the rename wrote; read as ours so
                  // that upgrade needs no Steam restart. Never written.
                hasWirelessDevice:store.hasWirelessDevice===true
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamGatePatch.Flag(root, "getterConfigurable")
            && SteamGatePatch.Flag(root, "currentlyHidden"),
        verifyOk: "status.installed&&status.available",
        removeOk: "!status.available",
        subject: "Network gate");

    /// <summary>Maps a signal percentage onto the four strength bands Steam draws as arcs.</summary>
    /// <param name="signalPercent">Signal quality, 0-100.</param>
    /// <returns>1 to 4 filled arcs.</returns>
    public static int StrengthFromPercent(int signalPercent) => signalPercent switch
    {
        >= 75 => 4,
        >= 50 => 3,
        >= 25 => 2,
        _ => 1,
    };

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamNetworkState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamNetworkState);

    /// <summary>Declares the surface as one module: the gate, the state, and the answers.</summary>
    /// <param name="enabled">Whether the state may be published right now. A consumer that keeps
    /// the header indicator on outside the rest of its menu gates this more loosely than the
    /// others.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the page's scan requests.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamNetworkState?>> read,
        ISteamNetworkBackend backend,
        string id = "network")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamNetworkState),
            ],
            commands:
            [
                new(PatchId, "startScan", (_, cancellationToken) =>
                    backend.StartScanAsync(cancellationToken)),
                new(PatchId, "stopScan", (_, cancellationToken) =>
                    backend.StopScanAsync(cancellationToken)),
            ]);
    }
}
