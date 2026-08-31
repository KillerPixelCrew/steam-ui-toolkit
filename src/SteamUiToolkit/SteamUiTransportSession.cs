using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Outcome of one evaluation against a Steam UI target.</summary>
/// <param name="Reachable">Whether a validated Steam target accepted the request at all. This is
/// the distinction that matters: an expression that ran and returned nothing is not the same as one
/// that never reached a target, and collapsing them makes "Steam is not running" and "the client
/// changed shape" indistinguishable in a log.</param>
/// <param name="Value">The by-value JavaScript result.</param>
/// <param name="Error">Why the target or expression was unavailable.</param>
public readonly record struct CefEvalResult(bool Reachable, string? Value, string? Error)
{
    /// <summary>A target accepted the expression and returned this.</summary>
    /// <param name="value">The by-value result, which may legitimately be null.</param>
    /// <returns>A reachable outcome.</returns>
    public static CefEvalResult Ok(string? value) => new(true, value, null);

    /// <summary>No validated target could run the expression.</summary>
    /// <param name="error">Why, ready to be logged verbatim.</param>
    /// <returns>An unreachable outcome carrying the reason.</returns>
    public static CefEvalResult Unreachable(string error) => new(false, null, error);
}

/// <summary>
/// Exposes the shell session's sole Steam UI transport to repository-owned one-shot operations.
/// </summary>
/// <remarks>
/// This is session ownership, not a transport: it cannot discover a target or open a socket. The
/// attached <see cref="PersistentSteamUiTransport"/> remains the only CDP implementation, so
/// artwork, collections, launch configuration, downloads and resident patches share generations,
/// reference counting and the CEF master switch.
/// </remarks>
public static class SteamUiTransportSession
{
    private static readonly object Gate = new();
    private static PersistentSteamUiTransport? _transport;
    private static volatile bool _enabled = true;

    /// <summary>Whether Steam UI integration is permitted at all right now.</summary>
    /// <remarks>The host's master switch. Everything else checks this rather than assuming, so a
    /// consumer can turn the whole surface off without tearing down the session.</remarks>
    public static bool Enabled => _enabled;

    /// <summary>Turns the whole Steam UI surface on or off.</summary>
    /// <param name="enabled">Whether integration is permitted.</param>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        lock (Gate)
        {
            _transport?.SetEnabled(enabled);
        }
    }

    /// <summary>Makes one transport the session's, for one-shot operations to borrow.</summary>
    /// <param name="transport">The session's sole transport.</param>
    /// <exception cref="InvalidOperationException">A different transport is already attached.
    /// Two would mean two CDP connections with independent generations, which is the state this
    /// type exists to prevent.</exception>
    public static void Attach(PersistentSteamUiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (Gate)
        {
            if (_transport is not null && !ReferenceEquals(_transport, transport))
            {
                throw new InvalidOperationException("A Steam UI transport is already attached.");
            }
            _transport = transport;
            transport.SetEnabled(_enabled);
        }
    }

    /// <summary>Releases the session's transport, if it is this one.</summary>
    /// <param name="transport">The transport being torn down. Detaching one that is not the
    /// attached transport does nothing, so a late teardown cannot orphan a newer session.</param>
    public static void Detach(PersistentSteamUiTransport transport)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_transport, transport))
            {
                _transport = null;
            }
        }
    }

    /// <summary>Runs one expression in SharedJSContext, where Steam's stores and services live.</summary>
    /// <param name="expression">The JavaScript to evaluate.</param>
    /// <param name="timeout">How long to wait before reporting the target unreachable.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The outcome. Never throws: an unreachable target, a timeout and a cancellation all
    /// come back as results carrying their reason, because a caller doing one-shot work against a
    /// client that may not be running should not have to distinguish those with a catch block.</returns>
    public static Task<CefEvalResult> EvaluateAsync(
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            timeout,
            cancellationToken);

    /// <summary>Runs one expression against the visible window, for anything touching the DOM.</summary>
    /// <param name="expression">The JavaScript to evaluate.</param>
    /// <param name="timeout">How long to wait before reporting the target unreachable.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The outcome, on the same never-throws contract as
    /// <see cref="EvaluateAsync(string, TimeSpan, CancellationToken)"/>.</returns>
    public static Task<CefEvalResult> EvaluateOnVisibleWindowAsync(
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(SteamUiTargetRole.MainWindow, expression, timeout, cancellationToken);

    private static async Task<CefEvalResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return CefEvalResult.Unreachable("Steam CEF integration disabled in settings.");
        }
        try
        {
            PersistentSteamUiTransport? transport;
            lock (Gate)
            {
                transport = _transport;
            }
            if (transport is null)
            {
                return CefEvalResult.Unreachable("Steam UI transport is not active.");
            }

            SteamUiEvaluationResult result = await transport.EvaluateAsync(
                    role,
                    expression,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Reachable
                ? CefEvalResult.Ok(result.Value)
                : CefEvalResult.Unreachable(
                    result.Error ?? $"Steam UI {role} target is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CefEvalResult.Unreachable("Steam CEF evaluation cancelled.");
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
}
