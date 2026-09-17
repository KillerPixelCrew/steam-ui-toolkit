using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One start or stop Steam reported for an app.</summary>
/// <param name="Sequence">The observer's position for this event; strictly increasing per observer.</param>
/// <param name="AppId">The app id.</param>
/// <param name="Running">True for a start, false for a stop.</param>
/// <param name="Timestamp">When the observer received the notification.</param>
public readonly record struct SteamAppLifetimeEvent(
    long Sequence,
    uint AppId,
    bool Running,
    DateTimeOffset Timestamp);

/// <summary>A bounded reading of the apps Steam reports as running.</summary>
/// <param name="Reachable">
///     Whether Steam answered. A deliberately disabled transport counts as reachable with no apps, so a
///     consumer's non-Steam fallback keeps working; only a failure is unreachable.
/// </param>
/// <param name="AppIds">Up to <see cref="SteamRunningAppsProbe.MaxReportedApps" /> running app ids.</param>
/// <param name="SourceGeneration">
///     A counter the in-page observer advances on every change to the running set. It restarts when
///     Steam's SharedJSContext is replaced.
/// </param>
/// <param name="Diagnostic">Why the reading is unusable, for the log.</param>
/// <param name="ObserverId">
///     Identifies the in-page observer that answered. A different id means Steam's context was replaced
///     and every sequence number before it is meaningless.
/// </param>
/// <param name="Sequence">The observer's latest event sequence.</param>
/// <param name="Events">
///     The lifetime events after the requested sequence, oldest first; empty when none were requested.
/// </param>
/// <param name="EventsComplete">
///     False when the observer's bounded log no longer holds every event after the requested sequence.
///     The running set is still exact; only the transitions in between are lost.
/// </param>
public sealed record SteamRunningAppsObservation(
    bool Reachable,
    IReadOnlyList<uint> AppIds,
    long SourceGeneration,
    string? Diagnostic,
    string? ObserverId = null,
    long Sequence = 0,
    IReadOnlyList<SteamAppLifetimeEvent>? Events = null,
    bool EventsComplete = true);

/// <summary>
///     Tracks which apps Steam is running from Steam's own lifetime notifications, and reads the details a
///     consumer needs to match a running app to a process.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SubscribeAsync" /> keeps the SharedJSContext attached and, on the first
///         <see cref="ObserveAsync" />, installs one resident observer: it seeds the running set from the
///         app store (<c>display_status</c> 4) and then follows
///         <c>SteamClient.GameSessions.RegisterForAppLifetimeNotifications</c>, keeping the last
///         <see cref="EventLogCapacity" /> notifications in a numbered log. Each later read returns that
///         bounded state without re-registering. Focus is never used to infer what is running.
///     </para>
///     <para>
///         Disposing the subscription unregisters the observer. A replaced SharedJSContext loses it, and
///         the next read installs a fresh one under a new <see cref="SteamRunningAppsObservation.ObserverId" />.
///         Several readers may share one observer: reading the log does not consume it. Disposing any
///         reader's lease removes the shared observer, and the others resynchronize from the fresh one
///         their next read installs.
///     </para>
///     <para>
///         <see cref="SteamAppLifetimeMonitor" /> turns these readings into started and stopped events.
///     </para>
/// </remarks>
public sealed class SteamRunningAppsProbe
{
    /// <summary>The most app ids one reading carries. The observer script slices to the same count.</summary>
    public const int MaxReportedApps = 32;

    /// <summary>How many lifetime notifications the in-page observer retains.</summary>
    public const int EventLogCapacity = 64;

    private const string ObserverProperty = "__steamUiRunningApps_v2";

    private const string DisabledError = "Steam CEF integration disabled in settings.";

    // Earlier observers: WSGM's original namespace and this probe's first version, which kept no event
    // log. A client still running one keeps its callback registered, so it is released first.
    private const string LegacyCleanup =
        "try{const L=window.__wsgm&&window.__wsgm.runningAppsV1;" +
        "if(L){try{L.dispose();}catch(_){}delete window.__wsgm.runningAppsV1;}}catch(_){}" +
        "try{const L=window.__steamUiRunningApps_v1;" +
        "if(L){try{L.dispose();}catch(_){}delete window.__steamUiRunningApps_v1;}}catch(_){}";

    private const string InstallObserver =
        "if(!window." + ObserverProperty + "){" + LegacyCleanup +
        "const ids=new Set((window.appStore&&appStore.allApps||[])" +
        ".filter(a=>Number(a.display_status)===4).map(a=>Number(a.appid))" +
        ".filter(a=>Number.isInteger(a)&&a>0&&a<=4294967295));" +
        "const R={id:Date.now().toString(36)+'-'+Math.random().toString(36).slice(2,10)," +
        "ids:ids,gen:1,seq:0,log:[],dispose:()=>{}};window." + ObserverProperty + "=R;" +
        "const h=SteamClient.GameSessions.RegisterForAppLifetimeNotifications(e=>{" +
        "const id=Number(e&&e.unAppID);if(!Number.isInteger(id)||id<=0||id>4294967295)return;" +
        "const run=!!e.bRunning;R.seq++;R.log.push({s:R.seq,id:id,r:run,t:Date.now()});" +
        "if(R.log.length>64)R.log.shift();" +
        "const before=ids.size;if(run)ids.add(id);else ids.delete(id);" +
        "if(ids.size!==before)R.gen++;});" +
        "R.dispose=()=>{try{h.unregister();}catch(_){}};}";

    private const string RemoveExpression =
        "(()=>{try{const R=window." + ObserverProperty + ";if(R){" +
        "R.dispose();delete window." + ObserverProperty + ";}" +
        "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()";

    private static readonly TimeSpan EvaluationBudget = TimeSpan.FromSeconds(4);

    private readonly ISteamUiTransport _transport;

    /// <summary>Creates a probe over the host's transport.</summary>
    /// <param name="transport">The session's transport.</param>
    public SteamRunningAppsProbe(ISteamUiTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Keeps SharedJSContext attached until the returned lease is disposed.</summary>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    /// <returns>A lease that also removes the in-page observer.</returns>
    public async ValueTask<IAsyncDisposable> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var transportLease = await _transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext,
            cancellationToken).ConfigureAwait(false);
        return new ObserverLease(_transport, transportLease);
    }

    /// <summary>Reads the running set, installing the observer when it is missing.</summary>
    /// <param name="eventsAfter">
    ///     Return the logged lifetime events after this sequence, or null for none. Pass 0 with a new
    ///     <see cref="SteamRunningAppsObservation.ObserverId" /> to read the whole retained log.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reading.</returns>
    public async Task<SteamRunningAppsObservation> ObserveAsync(
        long? eventsAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (eventsAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventsAfter), eventsAfter, "A sequence is never negative.");
        }

        var result = await SteamClientScript.EvaluateAsync(
            _transport,
            SteamUiTargetRole.SharedJsContext,
            BuildObserveExpression(eventsAfter),
            EvaluationBudget,
            cancellationToken).ConfigureAwait(false);
        return ParseObservation(result);
    }

    /// <summary>Reads one app's details through this probe's transport.</summary>
    /// <param name="appId">The app id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The outcome. A shortcut's details name its Target; a store title's name only its install
    ///     folder.
    /// </returns>
    public Task<SteamAppDetailsResult> ReadDetailsAsync(uint appId, CancellationToken cancellationToken = default)
    {
        return SteamApps.ReadDetailsAsync(_transport, appId, EvaluationBudget, cancellationToken);
    }

    /// <summary>Builds the read script, with the event log slice when one is requested.</summary>
    /// <param name="eventsAfter">The sequence to read after, or null for no events.</param>
    internal static string BuildObserveExpression(long? eventsAfter)
    {
        var events = eventsAfter is { } after
            ? "const a=" + after.ToString(CultureInfo.InvariantCulture) + ";" +
              "const ev=R.log.filter(x=>x.s>a);" +
              "const complete=a>=R.seq||(R.log.length>0&&R.log[0].s<=a+1);"
            : "const ev=[];const complete=true;";
        return "(()=>{try{" + InstallObserver +
               "const R=window." + ObserverProperty + ";" + events +
               "return JSON.stringify({ok:true,observer:R.id,ids:[...R.ids].slice(0,32)," +
               "generation:R.gen,sequence:R.seq,events:ev,complete:complete});" +
               "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
    }

    /// <summary>Maps an observer reply to a reading. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamRunningAppsObservation ParseObservation(CefEvalResult result)
    {
        if (!result.Reachable || result.Value is null)
        {
            // A deliberate disable means Steam names no app. Reporting it as a failure would suppress a
            // consumer's fallback for games started outside Steam.
            if (string.Equals(result.Error, DisabledError, StringComparison.Ordinal))
            {
                return new SteamRunningAppsObservation(true, [], 0, null);
            }

            return new SteamRunningAppsObservation(
                false,
                [],
                0,
                result.Error ?? "Steam SharedJSContext is unavailable.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!SteamClientScript.IsOk(root))
            {
                return new SteamRunningAppsObservation(
                    false,
                    [],
                    0,
                    SteamClientScript.ErrorOf(root) ?? "Steam rejected the running-app observer.");
            }

            List<uint> appIds = [];
            if (root.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray().Take(MaxReportedApps))
                {
                    if (TryAppId(id, out var appId))
                    {
                        appIds.Add(appId);
                    }
                }
            }

            List<SteamAppLifetimeEvent> events = [];
            if (root.TryGetProperty("events", out var log) && log.ValueKind == JsonValueKind.Array)
            {
                var last = 0L;
                foreach (var entry in log.EnumerateArray().Take(EventLogCapacity))
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !TryInt64(entry, "s", out var sequence)
                        || sequence <= last
                        || !entry.TryGetProperty("id", out var id)
                        || !TryAppId(id, out var appId)
                        || !entry.TryGetProperty("r", out var running)
                        || running.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        continue;
                    }

                    var timestamp = TryInt64(entry, "t", out var milliseconds)
                                    && milliseconds is > 0 and < 253402300800000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                        : DateTimeOffset.UtcNow;
                    events.Add(new SteamAppLifetimeEvent(
                        sequence,
                        appId,
                        running.ValueKind == JsonValueKind.True,
                        timestamp));
                    last = sequence;
                }
            }

            var observerId = SteamClientScript.StringOf(root, "observer");
            return new SteamRunningAppsObservation(
                true,
                appIds,
                TryInt64(root, "generation", out var generation) ? generation : 0,
                null,
                observerId.Length > 0 ? observerId : null,
                TryInt64(root, "sequence", out var latest) && latest > 0 ? latest : 0,
                events,
                !root.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.False);
        }
        catch (JsonException ex)
        {
            return new SteamRunningAppsObservation(
                false,
                [],
                0,
                $"Steam running-app payload was invalid: {ex.Message}");
        }
    }

    private static bool TryAppId(JsonElement element, out uint appId)
    {
        appId = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out appId) && appId > 0;
    }

    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out value);
    }

    private sealed class ObserverLease(
        ISteamUiTransport transport,
        IAsyncDisposable transportLease) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await transport.EvaluateAsync(
                    SteamUiTargetRole.SharedJsContext,
                    RemoveExpression,
                    EvaluationBudget,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SteamUiLog.Warn($"Steam running-app observer cleanup failed: {ex.Message}");
            }
            finally
            {
                await transportLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
