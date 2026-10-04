using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>A reading of the apps Steam reports as running.</summary>
/// <param name="Reachable">
///     Whether Steam answered. A deliberately closed transport counts as reachable with no apps, so a
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
///         The observer is published on the page only after Steam accepted the registration, so a
///         registration that throws leaves nothing behind and the next read installs again. One left by
///         an earlier host that never cleaned up is a working observer of the same shape and is reused.
///     </para>
///     <para>
///         Disposing the subscription unregisters the observer. A replaced SharedJSContext loses it, and
///         the next read installs a fresh one seeded from the app store again. The probe has one reader
///         (see <see cref="SteamClient.RunningApps" />).
///     </para>
/// </remarks>
public sealed class SteamRunningAppsProbe
{
    private const string ObserverProperty = "__steamUiRunningApps";

    private const string RemoveExpression =
        "(()=>{try{const R=window." + ObserverProperty + ";if(R){" +
        "R.dispose();delete window." + ObserverProperty + ";}" +
        "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    private static readonly TimeSpan EvaluationBudget = TimeSpan.FromSeconds(4);

    // Registers first and publishes the observer only once Steam returned a handle that can be
    // released, so a throwing registration never leaves a frozen set that later reads trust.
    private const string InstallObserver =
        "if(!window." + ObserverProperty + "){" +
        "const ids=new Set((window.appStore&&appStore.allApps||[])" +
        ".filter(a=>Number(a.display_status)===4).map(a=>Number(a.appid)>>>0)" +
        ".filter(a=>a>0));" +
        "const R={ids:ids,gen:1,dispose:()=>{}};" +
        "const h=SteamClient.GameSessions.RegisterForAppLifetimeNotifications(e=>{" +
        "const id=Number(e&&e.unAppID);if(!Number.isInteger(id)||id<=0||id>4294967295)return;" +
        "const before=ids.size;if(e.bRunning)ids.add(id);else ids.delete(id);" +
        "if(ids.size!==before)R.gen++;});" +
        "if(!h||typeof h.unregister!=='function')" +
        "throw new Error('Steam returned no lifetime registration handle.');" +
        "R.dispose=()=>{try{h.unregister();}catch(_){}};" +
        "window." + ObserverProperty + "=R;}";

    /// <summary>Reads the running set, installing the observer first when it is missing.</summary>
    internal const string ObserveExpression =
        "(()=>{try{" + InstallObserver +
        "const R=window." + ObserverProperty + ";" +
        "return JSON.stringify({ok:true,ids:[...R.ids],generation:R.gen});" +
        "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    private readonly SteamClient _client;

    internal SteamRunningAppsProbe(SteamClient client)
    {
        _client = client;
    }

    /// <summary>Keeps SharedJSContext attached until the returned lease is disposed.</summary>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    /// <returns>A lease that also removes the in-page observer.</returns>
    public async ValueTask<IAsyncDisposable> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var transportLease = await _client.Transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext,
            cancellationToken).ConfigureAwait(false);
        return new ObserverLease(_client, transportLease);
    }

    /// <summary>Reads the running set, installing the observer when it is missing.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reading.</returns>
    public async Task<SteamRunningAppsObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.ReadAsync(ObserveExpression, EvaluationBudget, cancellationToken)
            .ConfigureAwait(false);
        return ParseObservation(result);
    }

    /// <summary>Reads one app's details with this probe's short deadline.</summary>
    /// <param name="appId">The app id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The details. A shortcut's details name its Target; a store title's name only its install
    ///     folder.
    /// </returns>
    public Task<SteamReadResult<SteamAppDetails>> ReadDetailsAsync(
        uint appId,
        CancellationToken cancellationToken = default)
    {
        return _client.Apps.ReadDetailsAsync(appId, EvaluationBudget, cancellationToken);
    }

    /// <summary>Maps an observer reply to a reading. Pure, for tests.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamRunningAppsObservation ParseObservation(SteamUiEvaluationResult result)
    {
        // A deliberately closed transport means Steam names no app. Reporting it as a failure would
        // suppress a consumer's fallback for games started outside Steam.
        if (result.Dispatch == SteamUiDispatch.Closed)
        {
            return new SteamRunningAppsObservation(true, [], 0, null);
        }

        if (result.Dispatch != SteamUiDispatch.Answered || result.Error is not null || result.Value is null)
        {
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
                    SteamClientScript.RefusalOf(root) ?? "Steam rejected the running-app observer.");
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
        SteamClient client,
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
                var result = await client.ReadAsync(RemoveExpression, EvaluationBudget, CancellationToken.None)
                    .ConfigureAwait(false);
                if (result.Dispatch == SteamUiDispatch.Answered
                    && (result.Error is not null || result.Value is not { } value || !IsOk(value)))
                {
                    SteamUiLog.Warn(
                        $"Steam running-app observer cleanup was refused: {result.Error ?? result.Value ?? "no reply"}");
                }
            }
            finally
            {
                await transportLease.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static bool IsOk(string value)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                return SteamClientScript.IsOk(document.RootElement);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
