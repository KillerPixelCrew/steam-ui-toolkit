using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Outcome of one change the running Steam client was asked to make.</summary>
/// <param name="Reachable">
///     Whether a validated Steam target ran the request. An unreachable client changed nothing,
///     so a caller may keep the request and offer it again; it must not read this as a refusal.
/// </param>
/// <param name="Accepted">Whether Steam completed the change without throwing.</param>
/// <param name="Error">
///     Why the target was unreachable, or Steam's own error when it refused. Null on success.
/// </param>
public readonly record struct SteamClientWriteResult(bool Reachable, bool Accepted, string? Error)
{
    /// <summary>Whether Steam was reached and completed the change.</summary>
    public bool Succeeded => Reachable && Accepted;
}

/// <summary>Shared construction and parsing for one-shot calls into Steam's client API.</summary>
internal static class SteamClientScript
{
    private const string ErrorReply =
        "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}";

    /// <summary>Formats an app id as the unsigned literal Steam's client API expects.</summary>
    /// <param name="appId">The app id.</param>
    internal static string AppId(uint appId)
    {
        return appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Wraps a statement list in an async IIFE that reports <c>{ok:true}</c> after it completes
    ///     and <c>{ok:false,err}</c> when any statement throws.
    /// </summary>
    /// <param name="statements">The statements, each terminated with a semicolon.</param>
    internal static string Write(string statements)
    {
        return "(async()=>{try{" + statements + "return JSON.stringify({ok:true});}" + ErrorReply + "})()";
    }

    /// <summary>Wraps a statement list that returns its own JSON string on success.</summary>
    /// <param name="statements">The statements, ending in a <c>return JSON.stringify(...)</c>.</param>
    internal static string Read(string statements)
    {
        return "(async()=>{try{" + statements + "}" + ErrorReply + "})()";
    }

    /// <summary>A statement that waits for Steam to apply a setter on its own thread.</summary>
    /// <param name="milliseconds">How long to wait.</param>
    internal static string Settle(int milliseconds)
    {
        return "await new Promise(r=>setTimeout(r," + milliseconds.ToString(CultureInfo.InvariantCulture) +
               "));";
    }

    /// <summary>
    ///     An expression that resolves to <c>RegisterForAppDetails</c>' first answer, or null when Steam
    ///     does not answer within <paramref name="timeoutMilliseconds" />.
    /// </summary>
    /// <param name="appId">The app id.</param>
    /// <param name="timeoutMilliseconds">How long an unknown id may keep the promise open.</param>
    /// <remarks>
    ///     The call is a subscription, not a getter: Steam calls back with the current details and again
    ///     on every change, so the registration is released on both the answer and the timeout.
    /// </remarks>
    internal static string AppDetailsPromise(uint appId, int timeoutMilliseconds)
    {
        return "new Promise(res=>{let t;try{const h=SteamClient.Apps.RegisterForAppDetails(" +
               AppId(appId) + ",d=>{clearTimeout(t);try{h.unregister();}catch(_){}res(d);});" +
               "t=setTimeout(()=>{try{h.unregister();}catch(_){}res(null);}," +
               timeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + ");}" +
               "catch(_){res(null);}})";
    }

    /// <summary>Runs an expression through the given transport, or the session's when none is given.</summary>
    /// <param name="transport">A specific transport, or null for <see cref="SteamUiTransportSession" />.</param>
    /// <param name="role">The target the expression needs.</param>
    /// <param name="expression">The expression.</param>
    /// <param name="timeout">The evaluation deadline.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The outcome. Never throws for an unreachable target, a timeout or a cancellation.</returns>
    internal static async Task<CefEvalResult> EvaluateAsync(
        ISteamUiTransport? transport,
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (transport is null)
        {
            return role == SteamUiTargetRole.MainWindow
                ? await SteamUiTransportSession.EvaluateOnVisibleWindowAsync(expression, timeout, cancellationToken)
                    .ConfigureAwait(false)
                : await SteamUiTransportSession.EvaluateAsync(expression, timeout, cancellationToken)
                    .ConfigureAwait(false);
        }

        try
        {
            var result = await transport.EvaluateAsync(role, expression, timeout, cancellationToken)
                .ConfigureAwait(false);
            return result.Reachable
                ? CefEvalResult.Ok(result.Value)
                : CefEvalResult.Unreachable(result.Error ?? $"Steam UI {role} target is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CefEvalResult.Unreachable("Timed out talking to Steam's debug port.");
        }
        catch (Exception ex)
        {
            return CefEvalResult.Unreachable(ex.Message);
        }
    }

    /// <summary>Maps the reply of a <see cref="Write" /> expression to a result.</summary>
    /// <param name="result">The evaluation outcome.</param>
    internal static SteamClientWriteResult ParseWrite(CefEvalResult result)
    {
        if (!result.Reachable)
        {
            return new SteamClientWriteResult(false, false, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamClientWriteResult(true, false, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (IsOk(root))
            {
                return new SteamClientWriteResult(true, true, null);
            }

            return new SteamClientWriteResult(true, false, ErrorOf(root) ?? "Steam rejected the change.");
        }
        catch (JsonException ex)
        {
            return new SteamClientWriteResult(true, false, ex.Message);
        }
    }

    /// <summary>Whether a reply object carries <c>ok: true</c>.</summary>
    /// <param name="root">The reply.</param>
    internal static bool IsOk(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("ok", out var ok)
               && ok.ValueKind == JsonValueKind.True;
    }

    /// <summary>The <c>err</c> string of a reply object, when it has one.</summary>
    /// <param name="root">The reply.</param>
    internal static string? ErrorOf(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("err", out var err)
               && err.ValueKind == JsonValueKind.String
            ? err.GetString()
            : null;
    }

    /// <summary>A string property of a reply object, or an empty string.</summary>
    /// <param name="root">The reply.</param>
    /// <param name="name">The property.</param>
    internal static string StringOf(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
