using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What became of one change the running Steam client was asked to make.</summary>
/// <remarks>
///     Four outcomes, because a caller has to tell "nothing happened" from "something may have
///     happened". None of them is retried by this library, and a caller must not retry
///     <see cref="Unknown" /> automatically either: report it once and move on.
/// </remarks>
public enum SteamClientWriteOutcome
{
    /// <summary>The request never reached Steam (closed or absent transport, or cancelled first). Nothing changed.</summary>
    NotSent,

    /// <summary>
    ///     The request was sent but Steam's answer was lost or unreadable, so the change may have run.
    /// </summary>
    Unknown,

    /// <summary>Steam answered with a refusal or threw. Steps the script ran before that may have stuck.</summary>
    Rejected,

    /// <summary>Steam answered that the change completed.</summary>
    Applied
}

/// <summary>Outcome of one read from the running Steam client.</summary>
/// <typeparam name="T">What was read.</typeparam>
/// <param name="Dispatch">How far the request got.</param>
/// <param name="Value">
///     What Steam answered. Meaningful only when <see cref="Succeeded" />; a read that failed never
///     stands in for an empty or absent value.
/// </param>
/// <param name="Error">Why the read produced no value, or null on success.</param>
public readonly record struct SteamReadResult<T>(SteamUiDispatch Dispatch, T? Value, string? Error)
{
    /// <summary>Whether Steam answered and the answer was read.</summary>
    public bool Succeeded => Dispatch == SteamUiDispatch.Answered && Error is null;
}

/// <summary>One-shot reads and writes against the running Steam client over one transport.</summary>
/// <remarks>
///     <para>
///         A host composes one client over its transport and hands it to everything that talks to Steam's
///         client API. Nothing here is process-global: two clients over two transports are independent.
///     </para>
///     <para>
///         App, shortcut, collection and install-folder writes share one serialized lane. This
///         preserves store mutation order and the before/after identity checks used by shortcut adds.
///         The client borrows its transport; the caller owns transport disposal.
///     </para>
/// </remarks>
public sealed class SteamClient
{
    private readonly ISteamUiTransport _transport;
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>Creates a client over the host's transport.</summary>
    /// <param name="transport">The transport every call runs through.</param>
    public SteamClient(ISteamUiTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Apps = new SteamApps(this);
        Collections = new SteamCollections(this);
        InstallFolders = new SteamInstallFolders(this);
        Library = new SteamLibraryData(this);
        Downloads = new SteamDownloadActivity(this);
        CurrentPage = new SteamCurrentPage(this);
        StartupMovie = new SteamStartupMovie(this);
        RunningApps = new SteamRunningAppsProbe(this);
    }

    /// <summary>Per-app configuration, shortcuts and artwork.</summary>
    public SteamApps Apps { get; }

    /// <summary>Host-owned user collections.</summary>
    public SteamCollections Collections { get; }

    /// <summary>Steam library folders.</summary>
    public SteamInstallFolders InstallFolders { get; }

    /// <summary>The user's games, collections and store tags.</summary>
    public SteamLibraryData Library { get; }

    /// <summary>The download overview.</summary>
    public SteamDownloadActivity Downloads { get; }

    /// <summary>The game page Big Picture is showing.</summary>
    public SteamCurrentPage CurrentPage { get; }

    /// <summary>Steam's own startup movie choice.</summary>
    public SteamStartupMovie StartupMovie { get; }

    /// <summary>The apps Steam reports as running.</summary>
    /// <remarks>
    ///     Single reader: one consumer subscribes and reads. Several readers would share one in-page
    ///     observer, and any one of them disposing its lease removes it for the others.
    /// </remarks>
    public SteamRunningAppsProbe RunningApps { get; }

    /// <summary>Borrows the transport used by this client; helpers must not dispose it.</summary>
    internal ISteamUiTransport Transport => _transport;

    /// <summary>Runs one repository-owned expression and reports how far it got.</summary>
    /// <param name="role">The target the expression needs.</param>
    /// <param name="expression">The JavaScript to evaluate.</param>
    /// <param name="timeout">The complete request deadline.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>
    ///     The outcome. Never throws for a closed or absent transport, a timeout or a cancellation: each
    ///     comes back as a result whose <see cref="SteamUiEvaluationResult.Dispatch" /> says whether the
    ///     expression may have run.
    /// </returns>
    public async Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        try
        {
            return await _transport.EvaluateAsync(role, expression, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SteamUiEvaluationResult(
                SteamUiDispatch.NotSent, null, "Steam UI evaluation was cancelled.", default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SteamUiEvaluationResult(SteamUiDispatch.NotSent, null, ex.Message, default);
        }
    }

    /// <summary>Runs one library change in SharedJSContext through the write lane.</summary>
    /// <param name="expression">The change.</param>
    /// <param name="timeout">The evaluation deadline.</param>
    /// <param name="cancellationToken">
    ///     Cancels waiting for the lane and the evaluation. Pass <see cref="CancellationToken.None" />
    ///     once the caller has decided to send, so it never loses track of a change that was made.
    /// </param>
    /// <returns>The outcome. Never throws for a cancellation.</returns>
    internal async Task<SteamUiEvaluationResult> WriteAsync(
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SteamUiEvaluationResult(
                SteamUiDispatch.NotSent, null, "Steam UI evaluation was cancelled.", default);
        }

        try
        {
            return await EvaluateAsync(SteamUiTargetRole.SharedJsContext, expression, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Runs one read in SharedJSContext.</summary>
    /// <param name="expression">The read.</param>
    /// <param name="timeout">The evaluation deadline.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The outcome. Never throws for a cancellation.</returns>
    internal Task<SteamUiEvaluationResult> ReadAsync(
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return EvaluateAsync(SteamUiTargetRole.SharedJsContext, expression, timeout, cancellationToken);
    }
}
