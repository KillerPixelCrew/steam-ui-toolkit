using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     Native Steam notices: a title, a body and a route drawn with Valve's notification template in the
///     toast and the notification tray, and opened by mouse, touch or controller activation.
/// </summary>
/// <remarks>
///     A notice is a host-initiated call of the verified gate's <c>show</c>, the same lifecycle path the
///     patch manager uses for a gate's probe and status. The bridge's command allowlist governs requests
///     the page sends to the host; this call carries no page request. It is bound to the generation the
///     caller observed twice: the page refuses it unless its bridge was installed for exactly that
///     execution context and document, and the host discards an answer from any later generation.
/// </remarks>
public static class SteamNotices
{
    /// <summary>The lifecycle-owned notice renderer extension.</summary>
    public const string PatchId = "steam-ui.notices";

    /// <summary>The registered injected gate.</summary>
    internal const string GateName = "notices";

    /// <summary>The longest title Steam is handed.</summary>
    public const int MaximumTitleLength = 160;

    /// <summary>The longest body Steam is handed.</summary>
    public const int MaximumBodyLength = 800;

    /// <summary>Declares the notice capability; the caller enables its patch with its other host surfaces.</summary>
    /// <returns>A module with the one gate patch and no commands or publications.</returns>
    public static ISteamUiModule Module()
    {
        return new SteamUiModule("notices",
            [new SteamGatePatch(PatchId, GateName, "native-notices-v1", "Notices")], [], []);
    }

    /// <summary>Shows one notice through the verified capability; false asks the host to use its own notice.</summary>
    /// <remarks>Never retries an uncertain delivery. Steam's toast preferences remain in force.</remarks>
    /// <param name="transport">The existing session transport, borrowed.</param>
    /// <param name="id">Stable identity: a notice already in Steam's tray is not shown twice.</param>
    /// <param name="title">The notice title, at most <see cref="MaximumTitleLength" /> characters.</param>
    /// <param name="body">The notice body, at most <see cref="MaximumBodyLength" /> characters.</param>
    /// <param name="route">Steam route opened on activation.</param>
    /// <param name="token">Cancels before delivery.</param>
    /// <returns>Whether Steam accepted the notice into its notification tray in the same generation.</returns>
    public static async Task<bool> TryShowAsync(ISteamUiTransport transport, string id, string title, string body,
        string route, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var before = transport.GetSnapshots().FirstOrDefault(snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext && snapshot.Health == SteamUiTransportHealth.Ready);
        if (before is null
            || string.IsNullOrEmpty(id)
            || title is null || title.Length > MaximumTitleLength
            || body is null || body.Length > MaximumBodyLength
            || !SteamRouteNavigation.IsNavigable(route))
        {
            return false;
        }

        try
        {
            var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext,
                ShowExpression(before.Generations, id, title, body, route), TimeSpan.FromSeconds(2), token)
                .ConfigureAwait(false);
            return result.Answered && result.Value == "true" && result.Generations == before.Generations
                   && SteamSharedContext.IsReadyAt(transport, before.Generations);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The evaluation that hands one notice to the gate installed for these generations.</summary>
    /// <param name="generations">The generations the caller observed.</param>
    /// <param name="id">The notice id.</param>
    /// <param name="title">The title.</param>
    /// <param name="body">The body.</param>
    /// <param name="route">The route.</param>
    /// <returns>An expression answering <c>true</c> only when Steam took the notice into its tray.</returns>
    internal static string ShowExpression(SteamUiGenerations generations, string id, string title, string body,
        string route)
    {
        var context = generations.ExecutionContext.ToString(CultureInfo.InvariantCulture);
        var document = generations.Document.ToString(CultureInfo.InvariantCulture);
        return SteamGatePatch.GateExpression(
            SteamCef.JsString(GateName),
            "try{if(b.contextGeneration!==" + context + "||b.documentGeneration!==" + document
            + "||!bridge.status?.().held)return false;return bridge.show({id:" + SteamCef.JsString(id)
            + ",title:" + SteamCef.JsString(title) + ",body:" + SteamCef.JsString(body)
            + ",route:" + SteamCef.JsString(route) + "});}catch{return false;}",
            "return false;");
    }
}
