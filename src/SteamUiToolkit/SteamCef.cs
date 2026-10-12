using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SteamUiToolkit;

/// <summary>The observed state of Steam's loopback CEF remote-debugging listener.</summary>
public enum SteamCefDebuggingState
{
    /// <summary>The listener table was readable and no loopback or wildcard debug-port listener exists.</summary>
    Closed,

    /// <summary>A verified Steam process owns the debug-port listener reached over loopback.</summary>
    Open,

    /// <summary>The listener table or its owner could not be verified, or a foreign process owns the port.</summary>
    Unavailable
}

/// <summary>
///     Owns Steam's remote-debugging opt-in, passive listener inspection and JavaScript/endpoint validation helpers.
/// </summary>
public static class SteamCef
{
    /// <summary>The loopback port Steam opens its remote-debugging endpoint on.</summary>
    internal const int DebugPort = 8080;

    private const string FlagFileName = ".cef-enable-remote-debugging";

    /// <summary>
    ///     Writes the CEF remote-debugging flag into the Steam directory when
    ///     it is missing so Steam opens its localhost devtools port on next start.
    ///     Idempotent and best-effort; returns whether the flag is present afterwards.
    /// </summary>
    /// <param name="steamDirectory">
    ///     Steam's install directory, or <see langword="null" /> when the
    ///     host could not find it. The caller must validate this directory before allowing a write.
    /// </param>
    /// <param name="enabled">
    ///     The host's configured CEF master switch, independent of whether
    ///     startup readiness currently allows a transport connection.
    /// </param>
    /// <returns>
    ///     True when enabled and the flag exists or was created. False when disabled, no directory
    ///     was supplied, or file access failed. An existing flag is never removed.
    /// </returns>
    public static bool EnsureRemoteDebuggingEnabled(string? steamDirectory, bool enabled)
    {
        // Master switch off: never write the debug flag. An existing flag — from the user, another
        // tool, or a previous run — is deliberately left in place. Deleting a file shared with
        // things this library knows nothing about is not its call; it simply stops opting Steam in.
        if (!enabled || steamDirectory is null)
        {
            return false;
        }

        try
        {
            var flag = Path.Combine(steamDirectory, FlagFileName);
            if (!File.Exists(flag))
            {
                File.WriteAllBytes(flag, Array.Empty<byte>());
                SteamUiLog.Info($"Steam CEF remote-debugging enabled ({flag}).");
            }

            return true;
        }
        catch (Exception ex)
        {
            SteamUiLog.Warn($"Could not enable Steam CEF remote-debugging: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Inspects the native TCP listener table and owning processes without opening an HTTP or
    ///     debugger connection, changing Steam's opt-in flag or controlling its process lifetime.
    /// </summary>
    /// <param name="reason">The observed listener or refusal reason, ready for the caller to log.</param>
    /// <returns>
    ///     <see cref="SteamCefDebuggingState.Closed" /> only when the listener table was readable and
    ///     no loopback or wildcard listener exists on the debug port;
    ///     <see cref="SteamCefDebuggingState.Open" /> only for a verified Steam owner; otherwise
    ///     <see cref="SteamCefDebuggingState.Unavailable" />.
    /// </returns>
    /// <remarks>
    ///     An open listener does not prove Steam startup or target readiness. The host owns those
    ///     checks and any recovery policy. This method performs no logging.
    /// </remarks>
    public static SteamCefDebuggingState GetRemoteDebuggingState(out string reason)
    {
        return GetRemoteDebuggingState(
            NativeTcp.ListListeners(),
            static processId =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.ProcessName;
                }
                catch
                {
                    return null;
                }
            },
            out reason);
    }

    /// <summary>
    ///     Builds a JS string literal (with surrounding quotes) for embedding
    ///     a value into an expression. JSON-encoding is mandatory — a raw path would
    ///     lose its backslashes.
    /// </summary>
    /// <param name="value">The non-null string to embed.</param>
    /// <returns>A JSON-escaped JavaScript string literal, including its surrounding quotes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public static string JsString(string value)
    {
        return "\"" + JsonEncodedText.Encode(value) + "\"";
    }

    /// <summary>
    ///     Decides whether a <c>webSocketDebuggerUrl</c> from <c>/json/list</c> may
    ///     be connected to. A squatter answering the HTTP probe could otherwise redirect the
    ///     CDP client anywhere, so the URL must stay loopback WebSocket on the same port.
    ///     <para>A pure seam over the live check so the hardening is regression-testable.</para>
    /// </summary>
    /// <param name="candidate">The URL exactly as Steam's target list reported it.</param>
    /// <returns><see langword="true" /> when the URL is safe to connect to.</returns>
    internal static bool IsAllowedDebuggerUrl(string? candidate)
    {
        return Uri.TryCreate(candidate, UriKind.Absolute, out var socketUri)
               && socketUri.Scheme is "ws" or "wss"
               && (socketUri.Host == "127.0.0.1" || socketUri.Host == "localhost")
               && socketUri.Port == DebugPort;
    }

    /// <summary>
    ///     Decides whether the debug port is owned by Steam, given a listener table
    ///     and a pid→process-name lookup. A loopback listener is preferred over a wildcard one
    ///     so a 127.0.0.1 squatter cannot hide behind Steam's own <c>[::]</c>/<c>0.0.0.0</c> row.
    ///     <para>A pure seam over the live check so the hardening is regression-testable.</para>
    /// </summary>
    /// <param name="listeners">The TCP listener table, or null when it could not be read.</param>
    /// <param name="processName">Resolves a pid to its process name, or null if it is gone.</param>
    /// <param name="reason">
    ///     What was observed, whatever the verdict, ready to be logged verbatim by the caller.
    /// </param>
    /// <returns><see langword="true" /> only when a Steam process owns the port.</returns>
    /// <remarks>
    ///     Returns a distinct reason for an unreadable listener table, absent listener, foreign
    ///     process or accepted owner; this method performs no logging.
    /// </remarks>
    internal static bool IsSteamPortOwner(IReadOnlyList<NativeTcp.Listener>? listeners,
        Func<int, string?> processName,
        out string reason)
    {
        return GetRemoteDebuggingState(listeners, processName, out reason)
               == SteamCefDebuggingState.Open;
    }

    /// <summary>Classifies the listener state using the same ordered owner gate as discovery.</summary>
    /// <param name="listeners">The TCP listener table, or null when it could not be read.</param>
    /// <param name="processName">Resolves a pid to its process name, or null if it is gone.</param>
    /// <param name="reason">The observed listener or refusal reason.</param>
    /// <returns>Closed for an absent listener, Open for a verified Steam owner, otherwise Unavailable.</returns>
    internal static SteamCefDebuggingState GetRemoteDebuggingState(
        IReadOnlyList<NativeTcp.Listener>? listeners,
        Func<int, string?> processName,
        out string reason)
    {
        if (listeners is null)
        {
            // A failed table read is NOT "nothing is listening" — reporting it as such
            // once sent the maintainer hunting a closed Steam port that was open.
            reason = $"the TCP listener table was unavailable, so the owner of port {DebugPort} "
                     + "could not be verified";
            return SteamCefDebuggingState.Unavailable;
        }

        // Only a loopback or wildcard (0.0.0.0) row can answer a connect to 127.0.0.1. A process
        // bound to one specific LAN address on the port is never reached, so it must not decide the
        // verdict by sitting in the table ahead of Steam's own wildcard row.
        var candidates = listeners
            .Where(l => l.Port == DebugPort
                        && l.LocalAddress is NativeTcp.Loopback or 0)
            .OrderBy(l => l.LocalAddress == NativeTcp.Loopback ? 0 : 1)
            .ToList();
        if (candidates.Count == 0)
        {
            reason = $"nothing is listening on port {DebugPort}";
            return SteamCefDebuggingState.Closed;
        }

        var unidentified = 0;
        foreach (var listener in candidates)
        {
            var name = processName(listener.ProcessId);
            if (name is null)
            {
                // The owner exited between the table read and the lookup; that row is
                // stale, so keep scanning rather than treating it as a verdict.
                unidentified++;
                continue;
            }

            if (name is "steamwebhelper" or "steam")
            {
                reason = $"port {DebugPort} is owned by {name} (pid {listener.ProcessId})";
                return SteamCefDebuggingState.Open;
            }

            // Decisive, not just logged: we connect to 127.0.0.1, and Windows routes
            // that to the MOST SPECIFIC binding — the loopback listener sorted first
            // above. If that one is not Steam, then Steam's own wildcard row further
            // down the list belongs to a socket our connect never reaches, and
            // continuing the scan would clear a squatter sitting in front of Steam.
            reason = $"port {DebugPort} is owned by {name} (pid {listener.ProcessId}), not Steam";
            return SteamCefDebuggingState.Unavailable;
        }

        // Every candidate's owning process exited during lookup. That is distinct from a confirmed
        // non-Steam owner and must remain diagnosable.
        reason = $"{unidentified} listener(s) on port {DebugPort} could not be attributed to a "
                 + "running process";
        return SteamCefDebuggingState.Unavailable;
    }
}
