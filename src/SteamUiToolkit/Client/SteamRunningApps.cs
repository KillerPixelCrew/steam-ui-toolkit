using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A reading of the apps Steam reports as running.</summary>
/// <param name="Reachable">
///     Whether Steam answered. A deliberately disabled transport counts as reachable with no apps, so a
///     consumer's non-Steam fallback keeps working; only a failure is unreachable.
/// </param>
/// <param name="AppIds">The running app ids.</param>
/// <param name="SourceGeneration">
///     A counter the in-page observer advances on every change to the running set. It restarts when
///     Steam's SharedJSContext is replaced.
/// </param>
/// <param name="Diagnostic">Why the reading is unusable, for the log.</param>
public sealed record SteamRunningAppsObservation(
    bool Reachable,
    IReadOnlyList<uint> AppIds,
    long SourceGeneration,
    string? Diagnostic);

/// <summary>
///     Tracks which apps Steam is running from Steam's own lifetime notifications, and reads the details a
///     consumer needs to match a running app to a process.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SubscribeAsync" /> keeps the SharedJSContext attached and, on the first
///         <see cref="ObserveAsync" />, installs one resident observer: it seeds the running set from the
///         app store (<c>display_status</c> 4) and then follows
///         <c>SteamClient.GameSessions.RegisterForAppLifetimeNotifications</c>, advancing
///         <see cref="SteamRunningAppsObservation.SourceGeneration" /> on every change to the set. Each
///         later read returns that state without re-registering. Focus is never used to infer what is
///         running.
///     </para>
///     <para>
///         Disposing the subscription unregisters the observer. A replaced SharedJSContext loses it, and
///         the next read installs a fresh one seeded from the app store again. Several readers may share
///         one observer, since reading does not change it; disposing any reader's lease removes it, and
///         the others' next read installs a fresh one.
///     </para>
/// </remarks>
public sealed class SteamRunningAppsProbe
{
    private const string ObserverProperty = "__steamUiRunningApps";

    private const string RemoveExpression =
        "(()=>{try{const R=window." + ObserverProperty + ";if(R){" +
        "R.dispose();delete window." + ObserverProperty + ";}" +
        "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()";

    private static readonly TimeSpan EvaluationBudget = TimeSpan.FromSeconds(4);

    private const string InstallObserver =
        "if(!window." + ObserverProperty + "){" +
        "const ids=new Set((window.appStore&&appStore.allApps||[])" +
        ".filter(a=>Number(a.display_status)===4).map(a=>Number(a.appid))" +
        ".filter(a=>Number.isInteger(a)&&a>0&&a<=4294967295));" +
        "const R={ids:ids,gen:1,dispose:()=>{}};window." + ObserverProperty + "=R;" +
        "const h=SteamClient.GameSessions.RegisterForAppLifetimeNotifications(e=>{" +
        "const id=Number(e&&e.unAppID);if(!Number.isInteger(id)||id<=0||id>4294967295)return;" +
        "const before=ids.size;if(e.bRunning)ids.add(id);else ids.delete(id);" +
        "if(ids.size!==before)R.gen++;});" +
        "R.dispose=()=>{try{h.unregister();}catch(_){}};}";

    /// <summary>Reads the running set, installing the observer first when it is missing.</summary>
    internal const string ObserveExpression =
        "(()=>{try{" + InstallObserver +
        "const R=window." + ObserverProperty + ";" +
        "return JSON.stringify({ok:true,ids:[...R.ids],generation:R.gen});" +
        "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

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
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reading.</returns>
    public async Task<SteamRunningAppsObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var result = await SteamClientScript.EvaluateAsync(
            _transport,
            SteamUiTargetRole.SharedJsContext,
            ObserveExpression,
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

    /// <summary>Maps an observer reply to a reading. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamRunningAppsObservation ParseObservation(CefEvalResult result)
    {
        if (!result.Reachable || result.Value is null)
        {
            // A deliberate disable or hold means Steam names no app. Reporting it as a failure would
            // suppress a consumer's fallback for games started outside Steam.
            if (SteamUiTransportSession.IsClosedReason(result.Error))
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
                foreach (var id in ids.EnumerateArray())
                {
                    if (TryAppId(id, out var appId))
                    {
                        appIds.Add(appId);
                    }
                }
            }

            return new SteamRunningAppsObservation(
                true,
                appIds,
                TryInt64(root, "generation", out var generation) ? generation : 0,
                null);
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
