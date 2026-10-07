using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>Outcome of one change the running Steam client was asked to make.</summary>
/// <param name="Outcome">Whether the change was never sent, may have run, was refused or completed.</param>
/// <param name="Error">
///     Why it was not sent, why its answer is unknown, or Steam's own error when it refused. Null when
///     applied.
/// </param>
public readonly record struct SteamClientWriteResult(SteamClientWriteOutcome Outcome, string? Error)
{
    /// <summary>Whether Steam answered that the change completed.</summary>
    public bool Succeeded => Outcome == SteamClientWriteOutcome.Applied;
}

/// <summary>Shared construction and parsing for one-shot calls into Steam's client API.</summary>
internal static class SteamClientScript
{
    // One error shape for every script: Steam's message when it gave one, and its EResult when it
    // threw a result object instead (SteamClient.InstallFolder does).
    private const string ErrorReply =
        "catch(e){const m=e&&e.message;" +
        "return JSON.stringify({ok:false,err:m?String(m):(e&&typeof e==='object')?undefined:String(e)," +
        "result:e&&e.result});}";

    /// <summary>Formats an app id as the unsigned literal Steam's client API expects.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>A culture-independent unsigned decimal JavaScript literal.</returns>
    internal static string AppId(uint appId)
    {
        return appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Wraps a statement list in an async IIFE that reports <c>{ok:true}</c> after it completes
    ///     and <c>{ok:false,err,result}</c> when any statement throws.
    /// </summary>
    /// <param name="statements">The statements, each terminated with a semicolon.</param>
    /// <returns>An async JavaScript expression returning a JSON success or refusal envelope.</returns>
    internal static string Write(string statements)
    {
        return "(async()=>{try{" + statements + "return JSON.stringify({ok:true});}" + ErrorReply + "})()";
    }

    /// <summary>Wraps a statement list that returns its own JSON string on success.</summary>
    /// <param name="statements">The statements, ending in a <c>return JSON.stringify(...)</c>.</param>
    /// <returns>An async JavaScript expression preserving the supplied success reply and wrapping exceptions.</returns>
    internal static string Read(string statements)
    {
        return "(async()=>{try{" + statements + "}" + ErrorReply + "})()";
    }

    /// <summary>A statement that waits for Steam to apply a setter on its own thread.</summary>
    /// <param name="milliseconds">How long to wait.</param>
    /// <returns>An awaitable JavaScript delay statement; it does not confirm that a setter succeeded.</returns>
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
    /// <returns>A JavaScript promise expression yielding app details or null after failure or timeout.</returns>
    internal static string AppDetailsPromise(uint appId, int timeoutMilliseconds)
    {
        return "(" + AppDetailsFunction(timeoutMilliseconds) + ")(" + AppId(appId) + ")";
    }

    /// <summary>
    ///     A function expression taking an app id and resolving like <see cref="AppDetailsPromise" />,
    ///     for a script that reads the details of several apps.
    /// </summary>
    /// <param name="timeoutMilliseconds">How long an unknown id may keep its promise open.</param>
    /// <returns>A JavaScript function expression whose per-app promise releases its details subscription on answer or timeout.</returns>
    internal static string AppDetailsFunction(int timeoutMilliseconds)
    {
        return "(id=>new Promise(res=>{let t;try{const h=SteamClient.Apps.RegisterForAppDetails(" +
               "id,d=>{clearTimeout(t);try{h.unregister();}catch(_){}res(d);});" +
               "t=setTimeout(()=>{try{h.unregister();}catch(_){}res(null);}," +
               timeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + ");}" +
               "catch(_){res(null);}}))";
    }

    /// <summary>
    ///     A statement defining <c>shortcutIds()</c>: the ids of every non-Steam shortcut in the running
    ///     client's library, or null when the library is not loaded.
    /// </summary>
    /// <remarks>
    ///     Read from the all-apps collection, because the games collection leaves shortcuts out. An app
    ///     that cannot say whether it is a shortcut is judged by its id, which Steam generates in the top
    ///     half of the unsigned range for every shortcut.
    /// </remarks>
    internal const string ShortcutIdsFunction =
        "const shortcutApps=()=>{const ac=window.collectionStore?.allAppsCollection;" +
        "const all=ac&&(ac.allApps||ac.visibleApps);if(!all)return null;" +
        "const out=[];const seen=new Set();" +
        "for(const a of all){const id=a.appid>>>0;" +
        "const sc=typeof a.BIsShortcut==='function'?!!a.BIsShortcut():id>=2147483648;" +
        "if(!sc||seen.has(id))continue;seen.add(id);out.push({id,name:a.display_name||''});}" +
        "return out;};" +
        "const shortcutIds=()=>{const s=shortcutApps();return s?new Set(s.map(a=>a.id)):null;};";

    /// <summary>
    ///     The outcome of a write whose script left no readable answer: never sent, or sent and
    ///     unanswered.
    /// </summary>
    /// <param name="dispatch">How far the request got.</param>
    /// <returns>NotSent for closed or unsent requests; Unknown for requests that may have executed.</returns>
    internal static SteamClientWriteOutcome Unread(SteamUiDispatch dispatch)
    {
        return dispatch is SteamUiDispatch.NotSent or SteamUiDispatch.Closed
            ? SteamClientWriteOutcome.NotSent
            : SteamClientWriteOutcome.Unknown;
    }

    /// <summary>Maps the reply of a <see cref="Write" /> expression to a result.</summary>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>A write outcome preserving dispatch uncertainty; an unreadable answer is Unknown, never retried here.</returns>
    internal static SteamClientWriteResult ParseWrite(SteamUiEvaluationResult result)
    {
        if (result.Dispatch != SteamUiDispatch.Answered)
        {
            return new SteamClientWriteResult(Unread(result.Dispatch), result.Error ?? "Steam did not answer.");
        }

        if (result.Error is not null)
        {
            return new SteamClientWriteResult(SteamClientWriteOutcome.Rejected, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamClientWriteResult(SteamClientWriteOutcome.Unknown, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            return IsOk(root)
                ? new SteamClientWriteResult(SteamClientWriteOutcome.Applied, null)
                : new SteamClientWriteResult(
                    SteamClientWriteOutcome.Rejected, RefusalOf(root) ?? "Steam rejected the change.");
        }
        catch (JsonException ex)
        {
            return new SteamClientWriteResult(
                SteamClientWriteOutcome.Unknown, $"Steam's reply was unreadable: {ex.Message}");
        }
    }

    /// <summary>
    ///     Maps the reply of a <see cref="Read" /> expression to a typed read. An answered refusal, an
    ///     empty answer and an unreadable one all fail the read rather than producing an empty value.
    /// </summary>
    /// <typeparam name="T">What is read.</typeparam>
    /// <param name="result">The evaluation outcome.</param>
    /// <param name="what">What is read, for the error text.</param>
    /// <param name="read">
    ///     Reads the value from an <c>ok:true</c> reply. It throws <see cref="FormatException" /> for a
    ///     reply that does not hold a valid value.
    /// </param>
    /// <returns>The decoded value and dispatch, or a default value with a refusal/decoding error.</returns>
    internal static SteamReadResult<T> ParseRead<T>(
        SteamUiEvaluationResult result,
        string what,
        Func<JsonElement, T?> read)
    {
        if (result.Dispatch != SteamUiDispatch.Answered)
        {
            return new SteamReadResult<T>(result.Dispatch, default, result.Error ?? "Steam did not answer.");
        }

        if (result.Error is not null)
        {
            return new SteamReadResult<T>(result.Dispatch, default, result.Error);
        }

        if (result.Value is null)
        {
            return new SteamReadResult<T>(result.Dispatch, default, "No response from Steam.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            return IsOk(root)
                ? new SteamReadResult<T>(result.Dispatch, read(root), null)
                : new SteamReadResult<T>(result.Dispatch, default, RefusalOf(root) ?? $"Steam returned no {what}.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException
                                       or KeyNotFoundException)
        {
            return new SteamReadResult<T>(
                result.Dispatch, default, $"Steam's {what} reply was unreadable: {ex.Message}");
        }
    }

    /// <summary>Whether a reply object carries <c>ok: true</c>.</summary>
    /// <param name="root">The reply.</param>
    /// <returns>True only for an object with a boolean true ok property.</returns>
    internal static bool IsOk(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("ok", out var ok)
               && ok.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    ///     Why a reply refused: its <c>err</c> text, or Steam's <c>EResult</c> code when it threw a result
    ///     instead of a message. Null when the reply names neither.
    /// </summary>
    /// <param name="root">The reply.</param>
    /// <returns>The textual error, a formatted EResult code, or null when neither is available.</returns>
    internal static string? RefusalOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String)
        {
            return err.GetString();
        }

        return root.TryGetProperty("result", out var code)
               && code.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? $"EResult {code.GetRawText()}"
            : null;
    }

    /// <summary>A string property of a reply object, or an empty string.</summary>
    /// <param name="root">The reply.</param>
    /// <param name="name">The property.</param>
    /// <returns>The string property value, or an empty string when the object/property has another shape.</returns>
    internal static string StringOf(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
