using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A point-in-time view of Steam's download activity.</summary>
/// <param name="Active">
///     Whether a transfer is running: the state is not <c>None</c> and the queue is not paused.
/// </param>
/// <param name="State">Steam's raw <c>update_state</c> string (<c>None</c>, <c>Downloading</c>, …).</param>
/// <param name="Paused">Whether the download queue is paused.</param>
/// <param name="AppId">The app currently transferring, 0 when idle.</param>
/// <param name="NetworkBytesPerSecond">The current network rate Steam reports.</param>
public readonly record struct SteamDownloadOverview(
    bool Active,
    string State,
    bool Paused,
    int AppId,
    long NetworkBytesPerSecond);

/// <summary>Reads the running client's download overview.</summary>
/// <remarks>
///     <c>SteamClient.Downloads.RegisterForDownloadOverview</c> calls back immediately with a full
///     snapshot (live-verified 2026-08-12 on the Windows client: active is <c>Downloading</c>, idle is
///     <c>None</c>), so a one-shot subscribe and release is a clean read that leaves no resident script to
///     heal across Steam restarts.
/// </remarks>
public static class SteamDownloadActivity
{
    private const string OverviewExpression =
        """
        (() => new Promise((resolve) => {
          let reg = null, done = false;
          const finish = (v) => {
            if (done) return;
            done = true;
            try { reg && reg.unregister(); } catch (e) {}
            resolve(v);
          };
          setTimeout(() => finish(JSON.stringify({ err: 'timeout' })), 4000);
          try {
            reg = SteamClient.Downloads.RegisterForDownloadOverview((o) => finish(JSON.stringify({
              state: String(o.update_state ?? ''),
              paused: !!o.paused,
              appid: o.update_appid | 0,
              bps: Math.max(0, Math.round(o.update_network_bytes_per_second || 0)),
            })));
          } catch (e) { finish(JSON.stringify({ err: String(e) })); }
        }))()
        """;

    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Reads the current download overview.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>
    ///     The overview, or null when there is no usable answer: Steam unreachable, CEF disabled, or an
    ///     unexpected payload. A caller must never read null as an active download, and should debounce
    ///     it before treating it as idle, because a transient failure is not a finished transfer.
    /// </returns>
    public static async Task<SteamDownloadOverview?> QueryAsync(CancellationToken cancellationToken = default)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(OverviewExpression, EvalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result.Reachable ? Parse(result.Value) : null;
    }

    /// <summary>
    ///     The active-transfer rule (live-verified): any state other than <c>None</c> counts while the
    ///     queue is not paused, so transitional states do not flap.
    /// </summary>
    /// <param name="state">Steam's <c>update_state</c>.</param>
    /// <param name="paused">Whether the queue is paused.</param>
    /// <returns><see langword="true" /> while a transfer is running.</returns>
    public static bool IsActive(string state, bool paused)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Length > 0 && state != "None" && !paused;
    }

    /// <summary>Parses the script's payload; null for error payloads and malformed JSON.</summary>
    /// <param name="json">The payload.</param>
    internal static SteamDownloadOverview? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("err", out _))
            {
                return null;
            }

            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";
            var paused = root.TryGetProperty("paused", out var p) && p.GetBoolean();
            var appId = root.TryGetProperty("appid", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetInt32()
                : 0;
            var bps = root.TryGetProperty("bps", out var b) && b.ValueKind == JsonValueKind.Number
                ? b.GetInt64()
                : 0;
            return new SteamDownloadOverview(IsActive(state, paused), state, paused, appId, bps);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
