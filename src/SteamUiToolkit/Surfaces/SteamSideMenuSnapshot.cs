using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit.Surfaces;

/// <summary>The verified Steam side-menu state for one window.</summary>
public enum SteamSideMenu
{
    /// <summary>No side menu is open.</summary>
    None,
    /// <summary>The main navigation menu is open.</summary>
    Main,
    /// <summary>The Quick Access Menu is open.</summary>
    QuickAccess,
}

/// <summary>A main or game-overlay window's menu state, without inferring overlay activation.</summary>
/// <param name="ProcessId">Zero for the main window; the game process for overlay windows.</param>
/// <param name="AppId">Steam application identity, or zero for the main window.</param>
/// <param name="Menu">The window's verified side-menu state.</param>
/// <param name="OverlayActive">Overlay activation, or null when no current event has established it.</param>
/// <param name="KeyboardOpen">Native keyboard visibility, or null when unavailable.</param>
public sealed record SteamWindowSideMenu(uint ProcessId, uint AppId, SteamSideMenu Menu, bool? OverlayActive = null,
    bool? KeyboardOpen = false);

/// <summary>A bounded observation tied to the CEF generation that produced it.</summary>
/// <param name="Generations">The observed transport generations.</param>
/// <param name="Windows">Window states, or null when unavailable or malformed.</param>
public sealed record SteamSideMenuSnapshot(
    SteamUiGenerations Generations, IReadOnlyList<SteamWindowSideMenu>? Windows)
{
    /// <summary>Gets whether every reported window has a confirmed closed side menu.</summary>
    /// <remarks>This does not establish that an in-game overlay itself is closed.</remarks>
    public bool AllSideMenusClosed => Windows is { Count: > 0 }
        && System.Linq.Enumerable.All(Windows, window => window.Menu == SteamSideMenu.None);

    /// <summary>Gets whether menus and overlay activation are both confirmed closed.</summary>
    public bool AllSteamSurfacesClosed => AllSideMenusClosed
        && System.Linq.Enumerable.All(Windows!, window => window.OverlayActive == false && window.KeyboardOpen == false);
}

/// <summary>Reads Steam's known menu stores through an existing transport.</summary>
public static class SteamSideMenuObserver
{
    internal static string ReadExpression => $$"""
        (()=>{
          try {
            const require={{SteamUiModuleResolver.CreateExpression("side_menu")}};
            // Steam publishes its UI store as window.SteamUIStore; the side-menu enum is found by
            // its values in the menu store's module. Module ids and export names are per build.
            const ui=window.SteamUIStore, side=require.exported(["m_eLastRequestedSideMenu","GetOpenSideMenu"],
              v=>!!v&&typeof v==='object'&&v.None===0&&v.Main===1&&v.QuickAccess===2);
            if(side?.None!==0||side?.Main!==1||side?.QuickAccess!==2)return null;
            const store=ui?.WindowStore, main=store?.MainWindowInstance;
            const overlays=store?.OverlayWindows;
            if(!main||!Array.isArray(overlays)||overlays.length>32)return null;
            const read=(w,pid,appid)=>{
              if(typeof w?.MenuStore?.GetOpenSideMenu!=="function")throw Error("menu unavailable");
              const activation=window[{{SteamCef.JsString(SteamOverlayActivationPatch.StateKey)}}];
              const active=pid===0?false:activation?.live===true&&!activation.overflow
                ?activation.events.get(`${pid}:${appid}`):undefined;
              const keyboard=w.VirtualKeyboardManager?.IsShowingVirtualKeyboard?.Value;
              return {pid,appid,menu:w.MenuStore.GetOpenSideMenu(),active:typeof active==='boolean'?active:null,
                keyboard:typeof keyboard==='boolean'?keyboard:null};
            };
            return JSON.stringify([read(main,0,0),...overlays.map(w=>read(
              w,w.params?.browserInfo?.m_unPID,w.params?.browserInfo?.m_unAppID))]);
          }catch{return null;}
        })()
        """;

    /// <summary>Reads a snapshot without creating another transport or holding a subscription.</summary>
    /// <param name="transport">The host-owned, already subscribed transport.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>An observation; unavailable data stays unknown rather than closed.</returns>
    public static async Task<SteamSideMenuSnapshot> ReadAsync(
        ISteamUiTransport transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext,
            ReadExpression, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        bool current = System.Linq.Enumerable.Any(transport.GetSnapshots(), snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext
            && snapshot.Health == SteamUiTransportHealth.Ready
            && snapshot.Generations == result.Generations);
        return new(result.Generations, result.Reachable && current ? Parse(result.Value) : null);
    }

    internal static IReadOnlyList<SteamWindowSideMenu>? Parse(string? value)
    {
        if (value is null || value.Length > 8192)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 33)
            {
                return null;
            }
            var windows = new List<SteamWindowSideMenu>();
            var identities = new HashSet<(uint, uint)>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("pid", out var pid) || !pid.TryGetUInt32(out uint process)
                    || !item.TryGetProperty("appid", out var app) || !app.TryGetUInt32(out uint appId)
                    || !item.TryGetProperty("menu", out var menu) || !menu.TryGetInt32(out int state)
                    || state is < 0 or > 2 || !identities.Add((process, appId))
                    || (windows.Count == 0 ? process != 0 || appId != 0 : process == 0))
                {
                    return null;
                }
                bool? active = item.TryGetProperty("active", out var activation)
                    && activation.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? activation.GetBoolean() : null;
                bool? keyboard = item.TryGetProperty("keyboard", out var keyboardState)
                    && keyboardState.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? keyboardState.GetBoolean() : null;
                windows.Add(new(process, appId, (SteamSideMenu)state, active, keyboard));
            }
            return windows.AsReadOnly();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
