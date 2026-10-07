using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Which game page Big Picture is showing, and how that was determined.</summary>
/// <param name="AppId">The app id, or 0 when no game page is showing.</param>
/// <param name="Signal">
///     The signal that produced the id: <c>focus</c>, <c>hero image</c>, <c>library route</c>, or
///     <c>none</c>.
/// </param>
public readonly record struct SteamCurrentApp(uint AppId, string Signal);

/// <summary>Reads which game page the user is viewing from Steam's own library UI.</summary>
/// <remarks>
///     Three signals in priority order, each live-verified: the focused element's React fiber, then the
///     largest wide visible library-asset image in the visible window, then the library route in
///     SharedJSContext for a page with no artwork at all (a shortcut without images). Nothing here writes
///     to the page.
/// </remarks>
public sealed class SteamCurrentPage
{
    private const string VisibleWindowExpression =
        "JSON.stringify((()=>{try{" +
        "try{const el=document.activeElement;" +
        "if(el){const fk=Object.keys(el).find(k=>k.startsWith('__reactFiber$'));" +
        "let f=fk?el[fk]:null,hops=0;" +
        "while(f&&hops<40){const p=f.memoizedProps;" +
        "if(p&&typeof p==='object'){" +
        "if(typeof p.appid==='number')return {id:p.appid>>>0,src:'focus'};" +
        "const a=p.app||p.overview||p.appOverview;" +
        "if(a&&typeof a.appid==='number')return {id:a.appid>>>0,src:'focus'};}" +
        "f=f.return;hops++;}}}catch(e){}" +
        "const cx=window.innerWidth/2,ch=window.innerHeight;" +
        "const imgs=document.querySelectorAll('img');let best=0,bestW=0;" +
        "for(const i of imgs){const r=i.getBoundingClientRect();" +
        "if(r.width<600||r.width<=r.height)continue;" +
        "if(r.bottom<=0||r.top>=ch||cx<r.left||cx>r.right)continue;" +
        "if(i.checkVisibility&&!i.checkVisibility({checkOpacity:true,checkVisibilityCSS:true}))continue;" +
        @"const m=(i.src||'').match(/assets\/(\d+)\//);" +
        "if(m&&r.width>bestW){bestW=r.width;best=Number(m[1])>>>0;}}" +
        "return {id:best,src:best?'hero image':'none'};}catch(e){return {id:0,src:'error'};}})())";

    // Steam's router keeps SharedJSContext's location on the current route, and a game page is
    // /routes/library/app/<appid>.
    private const string RouteExpression =
        @"JSON.stringify({id:(()=>{try{const m=window.location.pathname.match(/\/library\/app\/(\d+)/);" +
        "return m?Number(m[1])>>>0:0;}catch(e){return 0;}})(),src:'library route'})";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private readonly SteamClient _client;

    /// <summary>Creates the current-page observations façade without opening a connection.</summary>
    /// <param name="client">Owning client; borrowed for dispatch and lifetime, never disposed by this façade.</param>
    internal SteamCurrentPage(SteamClient client)
    {
        _client = client;
    }

    /// <summary>Reads the game page currently in view.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>
    ///     The app and the signal that named it, with an id of 0 when Steam answered but no game page is
    ///     showing. A failed read means neither target answered, which says nothing about the page.
    /// </returns>
    public async Task<SteamReadResult<SteamCurrentApp>> GetAsync(CancellationToken cancellationToken = default)
    {
        var fromPage = Parse(await _client
            .EvaluateAsync(SteamUiTargetRole.MainWindow, VisibleWindowExpression, Budget, cancellationToken)
            .ConfigureAwait(false));
        if (fromPage is { Succeeded: true, Value.AppId: > 0 })
        {
            SteamUiLog.Info($"Steam current app {fromPage.Value.AppId} ({fromPage.Value.Signal}).");
            return fromPage;
        }

        var fromRoute = Parse(await _client
            .EvaluateAsync(SteamUiTargetRole.SharedJsContext, RouteExpression, Budget, cancellationToken)
            .ConfigureAwait(false));
        if (fromRoute is { Succeeded: true, Value.AppId: > 0 })
        {
            SteamUiLog.Info($"Steam current app {fromRoute.Value.AppId} ({fromRoute.Value.Signal}).");
            return fromRoute;
        }

        return fromPage.Succeeded || fromRoute.Succeeded
            ? new SteamReadResult<SteamCurrentApp>(SteamUiDispatch.Answered, new SteamCurrentApp(0, "none"), null)
            : fromRoute;
    }

    /// <summary>Parses one signal's reply.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>The current-app signal, including app id zero for no app, or a failed read.</returns>
    internal static SteamReadResult<SteamCurrentApp> Parse(SteamUiEvaluationResult result)
    {
        if (result.Dispatch != SteamUiDispatch.Answered || result.Error is not null || result.Value is null)
        {
            return new SteamReadResult<SteamCurrentApp>(
                result.Dispatch, default, result.Error ?? "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetUInt32(out var appId))
            {
                return new SteamReadResult<SteamCurrentApp>(
                    result.Dispatch, default, "Steam's current-page reply held no app id.");
            }

            // A decode surprise in the label degrades the diagnostic, never the detection.
            var signal = SteamClientScript.StringOf(root, "src");
            return new SteamReadResult<SteamCurrentApp>(
                result.Dispatch, new SteamCurrentApp(appId, signal.Length > 0 ? signal : "in-page"), null);
        }
        catch (JsonException ex)
        {
            SteamUiLog.Warn($"Current-app parse failed: {ex.Message}");
            return new SteamReadResult<SteamCurrentApp>(
                result.Dispatch, default, $"Steam's current-page reply was unreadable: {ex.Message}");
        }
    }
}
