using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit.Surfaces;

/// <summary>Requests Steam game activation for an existing overlay process.</summary>
public static class SteamGameWindowActivation
{
    /// <summary>Raises the application associated with exactly one overlay for the selected process.</summary>
    /// <param name="transport">The host-owned subscribed transport.</param>
    /// <param name="processId">The selected game process, never the main Steam window.</param>
    /// <param name="cancellationToken">Cancels the bounded request.</param>
    /// <returns>Whether Steam's call completed in the observed generation. This does not prove
    /// window focus or overlay recovery; the host must restore its exact selected HWND.</returns>
    /// <remarks>Never launches an application, retries a request, or falls back to Big Picture.</remarks>
    public static async Task<bool> RaiseAsync(ISteamUiTransport transport, uint processId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        cancellationToken.ThrowIfCancellationRequested();
        var before = transport.GetSnapshots().FirstOrDefault(snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext && snapshot.Health == SteamUiTransportHealth.Ready);
        if (processId == 0 || before is null) { return false; }
        var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext,
            CreateExpression(processId, DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds()),
            TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        return !cancellationToken.IsCancellationRequested && result.Reachable && result.Value == "true"
            && result.Generations == before.Generations
            && SteamSharedContext.IsReadyAt(transport, before.Generations);
    }

    internal static string CreateExpression(uint processId, long expiresAt) => $$"""
        (async()=>{
          try {
            if(Date.now()>{{expiresAt.ToString(CultureInfo.InvariantCulture)}})return false;
            const pid={{processId.ToString(CultureInfo.InvariantCulture)}};
            const windows=window.SteamUIStore?.WindowStore?.OverlayWindows;
            const apps=window.SteamClient?.Apps;
            if(!pid||!Array.isArray(windows)||windows.length>32||typeof apps?.RaiseWindowForGame!=='function')return false;
            const matches=windows.filter(w=>w.params?.browserInfo?.m_unPID===pid);
            if(matches.length!==1||matches[0].IsGamepadUIOverlayWindow?.()!==true)return false;
            const info=matches[0].params.browserInfo,appid=info.m_unAppID,gameid=info.m_gameID;
            if(!Number.isInteger(appid)||appid<=0||appid>4294967295)return false;
            // Non-Steam shortcuts carry a 64-bit GameID, which must never pass through Number.
            if(typeof gameid!=='string'||!/^[1-9][0-9]{0,19}$/.test(gameid)||BigInt(gameid)>18446744073709551615n)return false;
            await apps.RaiseWindowForGame(gameid);
            return true;
          }catch{return false;}
        })()
        """;
}
