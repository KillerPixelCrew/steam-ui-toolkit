using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Asks Steam's router to open one of its routes, once, from the host.</summary>
/// <remarks>
///     Navigation is a one-shot evaluation, never replayed publication state. It uses the same
///     absolute, non-root route contract as the injected navigator. A missing router, expired request
///     or changed generation reports failure; a potentially completed push is not retried.
/// </remarks>
public static class SteamRouteNavigation
{
    /// <summary>Whether a route is one this may ask Steam to open.</summary>
    /// <param name="route">The route.</param>
    /// <returns>True for an absolute route other than the root.</returns>
    public static bool IsNavigable(string? route)
    {
        return route is { Length: > 1 }
               && route[0] == '/'
               && !route.Any(char.IsControl);
    }

    /// <summary>Pushes one route on Steam's router.</summary>
    /// <param name="transport">The host-owned subscribed transport.</param>
    /// <param name="route">The route to open.</param>
    /// <param name="cancellationToken">Cancels the bounded request.</param>
    /// <returns>
    ///     Whether the push happened in the observed generation. It does not prove the page rendered
    ///     or that Steam's window is in front; focusing Steam is the caller's job.
    /// </returns>
    /// <remarks>Never retried: a push that may have happened is not pushed again.</remarks>
    public static async Task<bool> NavigateAsync(ISteamUiTransport transport, string route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        cancellationToken.ThrowIfCancellationRequested();
        var before = transport.GetSnapshots().FirstOrDefault(snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext && snapshot.Health == SteamUiTransportHealth.Ready);
        if (!IsNavigable(route) || before is null)
        {
            return false;
        }

        var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext,
            CreateExpression(route, DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds()),
            TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        return !cancellationToken.IsCancellationRequested && result.Answered && result.Value == "true"
               && result.Generations == before.Generations
               && SteamSharedContext.IsReadyAt(transport, before.Generations);
    }

    /// <summary>Builds a navigation request that refuses to run after its deadline.</summary>
    /// <param name="route">Non-root absolute Steam route, encoded as a JavaScript string literal.</param>
    /// <param name="expiresAt">UTC Unix time in milliseconds at which the request becomes stale.</param>
    /// <returns>JavaScript returning whether the existing Steam history accepted the push; page exceptions yield false.</returns>
    internal static string CreateExpression(string route, long expiresAt)
    {
        return $$"""
                 (()=>{
                   try {
                     if(Date.now()>{{expiresAt.ToString(CultureInfo.InvariantCulture)}})return false;
                     const route={{SteamCef.JsString(route)}};
                     if(typeof route!=='string'||!route.startsWith('/')||route==='/')return false;
                     const history=window.tempNavStore?.m_history;
                     if(!history||typeof history.push!=='function')return false;
                     history.push(route);
                     return true;
                   }catch{return false;}
                 })()
                 """;
    }
}
