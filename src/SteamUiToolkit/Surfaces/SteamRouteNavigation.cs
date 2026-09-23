using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit.Surfaces;

/// <summary>Asks Steam's router to open one of its routes, once, from the host.</summary>
/// <remarks>
///     <para>
///         Every other route change starts in Steam: a gate follows the route a host command answered
///         with. This is for the one case that starts on the host side - a host surface outside Steam,
///         such as an overlay, handing the user over to a page inside it.
///     </para>
///     <para>
///         It is a single bounded evaluation rather than a field on a publication. Published state is
///         replayed to every new subscriber and forgotten when the bridge restarts, so a request left
///         in state would navigate again after a gate reinstall or a document reload. The bounds are
///         the ones the gates' own <c>navigateSteamRoute</c> applies, so the two ways in agree on what
///         a route may be.
///     </para>
/// </remarks>
public static class SteamRouteNavigation
{
    /// <summary>The longest route either way in accepts.</summary>
    public const int MaximumRouteLength = 256;

    /// <summary>Whether a route is one this may ask Steam to open.</summary>
    /// <param name="route">The route.</param>
    /// <returns>True for an absolute route other than the root, within the length bound.</returns>
    public static bool IsNavigable(string? route)
    {
        return route is { Length: > 1 and <= MaximumRouteLength }
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
        return !cancellationToken.IsCancellationRequested && result.Reachable && result.Value == "true"
               && result.Generations == before.Generations
               && SteamSharedContext.IsReadyAt(transport, before.Generations);
    }

    internal static string CreateExpression(string route, long expiresAt)
    {
        return $$"""
                 (()=>{
                   try {
                     if(Date.now()>{{expiresAt.ToString(CultureInfo.InvariantCulture)}})return false;
                     const route={{SteamCef.JsString(route)}};
                     if(typeof route!=='string'||!route.startsWith('/')||route==='/'||route.length>{{MaximumRouteLength.ToString(CultureInfo.InvariantCulture)}})return false;
                     const history=window.tempNavStore?.m_history;
                     if(!history||typeof history.push!=='function')return false;
                     history.push(route);
                     return true;
                   }catch{return false;}
                 })()
                 """;
    }
}
