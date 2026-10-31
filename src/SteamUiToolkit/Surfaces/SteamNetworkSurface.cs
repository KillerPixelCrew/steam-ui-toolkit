using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One access point fed through Steam's own network-store ingestion path.</summary>
/// <param name="Ssid">The network name.</param>
/// <param name="Strength">Signal in Steam's four bands, 1-4; see <see cref="SteamNetworkSurface.StrengthFromPercent" />.</param>
/// <param name="Secured">Whether the network requires a key.</param>
/// <param name="Connected">
///     Whether this is the connected network. A connected entry also drives
///     the header's Wi-Fi indicator, which is empty on Windows without it.
/// </param>
public sealed record SteamNetworkAccessPoint(
    string Ssid,
    int Strength,
    bool Secured,
    bool Connected);

/// <summary>The latest join the host accepted from Steam's page, and how it ended.</summary>
/// <param name="Attempt">The attempt number the <c>connect</c> answer carried.</param>
/// <param name="Ssid">The network being joined.</param>
/// <param name="Pending">Whether the join is still running.</param>
/// <param name="Joined">Whether it joined; meaningful once <paramref name="Pending" /> is false.</param>
/// <param name="Error">Why it did not join, when it did not.</param>
public sealed record SteamNetworkJoin(
    long Attempt,
    string Ssid,
    bool Pending,
    bool Joined,
    string? Error);

/// <summary>The networks Steam's Internet page lists and the header indicator reads.</summary>
/// <param name="Networks">The networks to list, in order.</param>
/// <param name="Join">The latest join, or null before the page has asked for one.</param>
public sealed record SteamNetworkState(
    IReadOnlyList<SteamNetworkAccessPoint> Networks,
    SteamNetworkJoin? Join = null);

/// <summary>What answers Steam's Internet page: the scan lifetime, joining, leaving and forgetting.</summary>
/// <remarks>
///     Steam's page starts and stops scanning as it opens and closes, and the gate forwards exactly
///     that. The networks it lists are the host's, under access point ids Steam's Windows backend has
///     never seen, so the gate answers the page's join, disconnect and forget for them through this
///     backend as well. The radio toggle stays with Steam.
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

    /// <summary>Starts joining a listed network.</summary>
    /// <param name="ssid">The network the page picked.</param>
    /// <param name="password">The password the page collected, or null when it collected none.</param>
    /// <param name="cancellationToken">Cancels the request, not the join it started.</param>
    /// <returns>
    ///     <see cref="SteamNetworkSurface.JoinAccepted" /> once the join is running. A join outlasts one
    ///     request, so its outcome is published as <see cref="SteamNetworkState.Join" />.
    /// </returns>
    Task<SteamUiCommandResult> ConnectAsync(string ssid, string? password, CancellationToken cancellationToken);

    /// <summary>Leaves the connected network.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Forgets a listed network's saved credentials.</summary>
    /// <param name="ssid">The network to forget.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> ForgetAsync(string ssid, CancellationToken cancellationToken);
}

/// <summary>Steam's own Wi-Fi surface — the Internet page and the header indicator — revealed and fed.</summary>
/// <remarks>
///     The Windows client tracks its wireless device and only
///     <c>get networkManagementAvailable(){return TS.IS_STEAMOS}</c> keeps the UI away; the gate
///     overrides that one getter. Every backend report then carries an empty access-point list, so
///     the gate writes the published networks through the store's own <c>SetDeviceInfo</c> with a
///     no-op <c>MarkAsNotPresent</c> that pins them across the backend's periodic reports.
/// </remarks>
public static class SteamNetworkSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.network";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["startScan", "stopScan", "connect", "disconnect", "forget"];

    /// <summary>The gate that reveals Steam's Wi-Fi surface by overriding one Deck-only store getter.</summary>
    /// <remarks>
    ///     Reveals but does not populate on its own: the Windows backend reports an empty access-point
    ///     list, so verification reports the surface rather than treating a revealed row over no
    ///     networks as success. The probe is the gate's own find and loads no module: not ready until
    ///     Steam publishes its network store; incompatible when the getter is not configurable, or
    ///     already reads true without being this gate's own, which is a client this gate leaves alone.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "network",
        "steam-network-gate-v1:configurable-getter+currently-hidden",
        "Network gate");

    /// <summary>The answer to <c>connect</c>: the join is running under this attempt number.</summary>
    /// <param name="attempt">The number the published <see cref="SteamNetworkJoin" /> will carry.</param>
    /// <returns>An applied result carrying <c>{ attempt }</c>.</returns>
    public static SteamUiCommandResult JoinAccepted(long attempt)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("attempt", attempt);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new SteamUiCommandResult(true, null, document.RootElement.Clone());
    }

    /// <summary>Maps a signal percentage onto the four strength bands Steam draws as arcs.</summary>
    /// <param name="signalPercent">Signal quality, 0-100.</param>
    /// <returns>1 to 4 filled arcs.</returns>
    public static int StrengthFromPercent(int signalPercent)
    {
        return signalPercent switch
        {
            >= 75 => 4,
            >= 50 => 3,
            >= 25 => 2,
            _ => 1
        };
    }

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamNetworkState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamNetworkState);
    }

    /// <summary>Declares the surface as one module: the gate, the state, and the answers.</summary>
    /// <param name="enabled">
    ///     Whether the state may be published right now. A consumer that keeps
    ///     the header indicator on outside the rest of its menu gates this more loosely than the
    ///     others.
    /// </param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What answers the page's scan, join, disconnect and forget requests.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamNetworkState?>> read,
        ISteamNetworkBackend backend,
        string id = "network")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamNetworkState,
            [Patch],
            [
                SteamUiModuleBuilder.Command(PatchId, "startScan", backend.StartScanAsync),
                SteamUiModuleBuilder.Command(PatchId, "stopScan", backend.StopScanAsync),
                SteamUiModuleBuilder.Command<(string Ssid, string? Password)>(
                    PatchId,
                    "connect",
                    TryReadJoin,
                    (join, cancellationToken) => backend.ConnectAsync(join.Ssid, join.Password, cancellationToken),
                    "The network join payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "disconnect", backend.DisconnectAsync),
                SteamUiModuleBuilder.Command<string>(
                    PatchId,
                    "forget",
                    static (payload, out ssid) =>
                        SteamUiPayload.TryReadNonBlankString(payload, "ssid", out ssid)
                        && SteamUiPayload.HasExactly(payload, 1),
                    backend.ForgetAsync,
                    "The network forget payload is invalid.")
            ]);
    }

    /// <summary>Reads a join: the network, and the password or null when the page collected none.</summary>
    private static bool TryReadJoin(JsonElement payload, out (string Ssid, string? Password) join)
    {
        string? password = null;
        var read = SteamUiPayload.TryReadNonBlankString(payload, "ssid", out var ssid)
                   && SteamUiPayload.TryReadNullableString(payload, "password", out password)
                   && SteamUiPayload.HasExactly(payload, 2);
        join = (ssid, password);
        return read;
    }
}
