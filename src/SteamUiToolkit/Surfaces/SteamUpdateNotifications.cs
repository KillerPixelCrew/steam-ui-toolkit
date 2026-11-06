using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Reusable native update notices, including Steam's toast, notification tray and controller activation.</summary>
public static class SteamUpdateNotifications
{
    /// <summary>The lifecycle-owned notification renderer extension.</summary>
    public const string PatchId = "steam-ui.update-notifications";

    /// <summary>Declares the notification capability; the caller enables its patch with other host surfaces.</summary>
    public static ISteamUiModule Module() => new SteamUiModule("update-notifications",
        [new SteamGatePatch(PatchId, "updateNotifications", "native-update-notifications-v1", "Update notifications")], [], []);

    /// <summary>Shows one notice through an already verified capability; false asks the host to use its own notice.</summary>
    /// <remarks>Never retries an uncertain delivery. Steam's toast preferences remain in force.</remarks>
    /// <param name="transport">The existing session transport, borrowed.</param>
    /// <param name="id">Stable identity used to avoid duplicate notices within a document.</param>
    /// <param name="title">The notice title, at most 160 characters.</param>
    /// <param name="body">The notice body, at most 800 characters.</param>
    /// <param name="route">Steam route opened on activation.</param>
    /// <param name="token">Cancels before delivery.</param>
    /// <returns>Whether Steam accepted the notice into its notification tray in the same generation.</returns>
    public static async Task<bool> TryShowAsync(ISteamUiTransport transport, string id, string title, string body,
        string route, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var before = transport.GetSnapshots().FirstOrDefault(snapshot => snapshot.Role == SteamUiTargetRole.SharedJsContext
            && snapshot.Health == SteamUiTransportHealth.Ready);
        if (before is null || !SteamRouteNavigation.IsNavigable(route))
        {
            return false;
        }

        var expression = "(()=>{try{const gate=window[" + SteamCef.JsString(SteamUiBridgeIdentity.Namespace)
            + "]?.gate('updateNotifications');if(!gate?.status?.().held)return false;return gate.show({id:"
            + SteamCef.JsString(id) + ",title:" + SteamCef.JsString(title) + ",body:" + SteamCef.JsString(body)
            + ",route:" + SteamCef.JsString(route) + "});}catch{return false;}})()";
        var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext, expression,
            TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        return result.Answered && result.Value == "true" && result.Generations == before.Generations
            && SteamSharedContext.IsReadyAt(transport, before.Generations);
    }
}
