using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What one screensaver dismissal did.</summary>
/// <param name="Delivered">Whether Steam took the dismissal in the generation the call was made in.</param>
/// <param name="WasActive">Whether Steam reported its screensaver active beforehand; null when it did not say.</param>
public readonly record struct SteamScreensaverDismissalResult(bool Delivered, bool? WasActive);

/// <summary>
///     Dismisses Big Picture's screensaver through Steam's own input path,
///     <c>Screensaver.ForceScreensaver({enabled: false})</c>.
/// </summary>
/// <remarks>
///     Steam ends its saver when the saver view sees input. After a wake nothing has reached Steam yet,
///     so a saver that was showing when the machine slept can stay up over a dark screen until the
///     user presses something. A host calls <see cref="TryDismissAsync" /> once for such a wake. The
///     call is a host-initiated gate call on the lifecycle path, like a probe, bound to the bridge
///     generation the caller observed; it changes nothing that needs removing.
/// </remarks>
public static class SteamScreensaverDismissal
{
    /// <summary>The lifecycle-owned dismissal capability.</summary>
    public const string PatchId = "steam-ui.screensaver-dismissal";

    /// <summary>The registered injected gate.</summary>
    internal const string GateName = "screensaverDismissal";

    /// <summary>Declares the capability; the caller enables its patch with its other host surfaces.</summary>
    /// <returns>A module with the one gate patch and no commands or publications.</returns>
    public static ISteamUiModule Module()
    {
        return new SteamUiModule("screensaver-dismissal",
            [new SteamGatePatch(PatchId, GateName, "native-screensaver-dismissal-v1", "Screensaver dismissal")], [], []);
    }

    /// <summary>Dismisses Steam's screensaver once; never retried.</summary>
    /// <param name="transport">The existing session transport, borrowed.</param>
    /// <param name="token">Cancels before delivery.</param>
    /// <returns>Whether Steam took the dismissal, and whether its saver was showing.</returns>
    public static async Task<SteamScreensaverDismissalResult> TryDismissAsync(ISteamUiTransport transport,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var before = transport.GetSnapshots().FirstOrDefault(snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext && snapshot.Health == SteamUiTransportHealth.Ready);
        if (before is null)
        {
            return default;
        }

        try
        {
            var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext,
                DismissExpression(before.Generations), TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            if (!result.Answered || result.Value is null || result.Generations != before.Generations
                || !SteamSharedContext.IsReadyAt(transport, before.Generations))
            {
                return default;
            }

            return Read(result.Value);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return default;
        }
    }

    /// <summary>Reads the gate's answer.</summary>
    /// <param name="value">The JSON the expression answered.</param>
    /// <returns>The result; an unreadable answer is undelivered.</returns>
    internal static SteamScreensaverDismissalResult Read(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("dismissed", out var dismissed)
                || dismissed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return default;
            }

            bool? active = root.TryGetProperty("active", out var state)
                           && state.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? state.GetBoolean()
                : null;
            return new SteamScreensaverDismissalResult(dismissed.GetBoolean(), active);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>The evaluation that asks the gate installed for these generations to dismiss.</summary>
    /// <param name="generations">The generations the caller observed.</param>
    /// <returns>An expression answering the gate's JSON result, or a refusal.</returns>
    internal static string DismissExpression(SteamUiGenerations generations)
    {
        var context = generations.ExecutionContext.ToString(CultureInfo.InvariantCulture);
        var document = generations.Document.ToString(CultureInfo.InvariantCulture);
        return SteamGatePatch.GateExpression(
            SteamCef.JsString(GateName),
            "if(b.contextGeneration!==" + context + "||b.documentGeneration!==" + document
            + "||!bridge.status?.().held)return JSON.stringify({dismissed:false,active:null});"
            + "try{return JSON.stringify(await bridge.dismiss());}"
            + "catch{return JSON.stringify({dismissed:false,active:null});}",
            "return JSON.stringify({dismissed:false,active:null});",
            asynchronous: true);
    }
}
