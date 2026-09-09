using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit.Surfaces;

/// <summary>The native controller command to replay into a Steam window.</summary>
public enum SteamNativeSurfaceAction
{
    /// <summary>Steam's Quick Access button.</summary>
    QuickAccess,
    /// <summary>Steam's Home/Overlay button.</summary>
    Home,
}

/// <summary>Replays semantic controller commands through Steam's existing window handlers.</summary>
public static class SteamNativeSurfaceCommands
{
    /// <summary>Invokes one native handler on an exact main or game-overlay window.</summary>
    /// <param name="transport">The subscribed host transport.</param>
    /// <param name="action">The semantic button to replay.</param>
    /// <param name="processId">Zero for the main window, otherwise the exact overlay process.</param>
    /// <param name="appId">Zero for the main window, otherwise the exact overlay app.</param>
    /// <param name="generations">The generation in which the target was observed.</param>
    /// <param name="cancellationToken">Cancels the bounded dispatch.</param>
    /// <returns>Whether the handler was invoked. Surface opening requires a separate observation.</returns>
    /// <remarks>No retry or main-window fallback is performed. Steam retains its native debounce.</remarks>
    public static async Task<bool> ReplayAsync(ISteamUiTransport transport, SteamNativeSurfaceAction action,
        uint processId, uint appId, SteamUiGenerations generations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        string expression = CreateExpression(action, processId, appId);
        if (!Current(transport, generations))
        {
            return false;
        }
        var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext, expression,
            TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return result.Reachable && result.Generations == generations && Current(transport, generations)
            && result.Value == "true";
    }

    private static bool Current(ISteamUiTransport transport, SteamUiGenerations generations) =>
        transport.GetSnapshots().Any(snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext
            && snapshot.Health == SteamUiTransportHealth.Ready && snapshot.Generations == generations);

    internal static string CreateExpression(SteamNativeSurfaceAction action, uint processId, uint appId)
    {
        if (!Enum.IsDefined(action) || (processId == 0 && appId != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }
        string method = action == SteamNativeSurfaceAction.QuickAccess
            ? "OnQuickAccessButtonPressed" : "OnHomeButtonPressed";
        return $$"""
            (()=>{
              try {
                const require={{SteamUiModuleResolver.CreateExpression("native_surface")}};
                const ui=require("61236").oy,store=ui?.WindowStore;
                const pid={{processId.ToString(CultureInfo.InvariantCulture)}},appid={{appId.ToString(CultureInfo.InvariantCulture)}};
                if(typeof ui?.BHomeAndQuickAccessButtonsEnabled!=='function'||!ui.BHomeAndQuickAccessButtonsEnabled())return false;
                let target;
                if(pid===0)target=store?.MainWindowInstance;
                else {
                  const windows=store?.OverlayWindows;
                  if(!Array.isArray(windows)||windows.length>32)return false;
                  const matches=windows.filter(w=>w.params?.browserInfo?.m_unPID===pid&&w.params?.browserInfo?.m_unAppID===appid);
                  if(matches.length!==1||matches[0].IsGamepadUIOverlayWindow?.()!==true)return false;
                  target=matches[0];
                }
                if(typeof target?.{{method}}!=='function')return false;
                target.{{method}}();return true;
              }catch{return false;}
            })()
            """;
    }
}
