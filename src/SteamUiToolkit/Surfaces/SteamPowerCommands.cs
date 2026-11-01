using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>How Steam's own suspend flow ended when it was asked to skip the real PC suspend.</summary>
public enum SteamSuspendFlowResult
{
    /// <summary>
    ///     Nothing reached Steam, or its answer cannot be trusted: SharedJSContext was not ready, the
    ///     generation changed, the store or a method was missing, or no answer arrived in time.
    /// </summary>
    /// <remarks>An unanswered request may still have started the flow inside Steam.</remarks>
    Unavailable,

    /// <summary>
    ///     Steam did not start the flow: a suspend blocker was held, or a suspend was already in
    ///     progress, so <c>suspending</c> never became true for this request.
    /// </summary>
    Refused,

    /// <summary>
    ///     The flow started and ended without Steam reaching its complete preparation state, which is
    ///     how its <c>PrepareForSystemSuspend</c> failure path ends.
    /// </summary>
    Failed,

    /// <summary>Steam was still suspending when the budget ran out.</summary>
    TimedOut,

    /// <summary>Steam prepared for suspend, finished its own flow and left the PC running.</summary>
    Completed
}

/// <summary>Drives Steam's own power menu and suspend flow in Big Picture.</summary>
/// <remarks>
///     One-shot evaluations in SharedJSContext through the host's subscribed transport. Nothing is
///     installed in the page and nothing is retried. The members were mapped offline from the
///     installed 2026-11-01 client bundle and still need a live check.
/// </remarks>
public static class SteamPowerCommands
{
    /// <summary>The default time Steam's suspend flow is given to end.</summary>
    public static readonly TimeSpan DefaultSuspendBudget = TimeSpan.FromSeconds(10);

    /// <summary>The longest suspend budget accepted, kept inside the transport's operation bound.</summary>
    public static readonly TimeSpan MaximumSuspendBudget = TimeSpan.FromSeconds(25);

    private const int SuspendPollMs = 150;

    // Valve's ESuspendResumeProgressState wire value for k_ESuspendResumeProgressState_Complete.
    private const int SuspendProgressComplete = 1;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    // Covers the page's own budget plus dispatch and reply.
    private static readonly TimeSpan SuspendReplyMargin = TimeSpan.FromSeconds(2);

    /// <summary>Opens Big Picture's power menu, as Steam does for its native power-menu notification.</summary>
    /// <param name="transport">The host-owned subscribed transport.</param>
    /// <param name="cancellationToken">Cancels the bounded request.</param>
    /// <returns>
    ///     Whether <c>SteamUIStore.OpenPowerMenu(null)</c> was called in a ready SharedJSContext whose
    ///     generations did not change. This does not prove the menu is on screen.
    /// </returns>
    /// <remarks>No retry and no fallback.</remarks>
    public static async Task<bool> OpenPowerMenuAsync(ISteamUiTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await EvaluateAsync(transport, CreateOpenPowerMenuExpression(
            DateTimeOffset.UtcNow.Add(CommandTimeout).ToUnixTimeMilliseconds()), CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        return value == "true";
    }

    /// <summary>
    ///     Runs Steam's own suspend flow without the real PC suspend and waits for it to end, so the host
    ///     can suspend the PC itself.
    /// </summary>
    /// <param name="transport">The host-owned subscribed transport.</param>
    /// <param name="budget">
    ///     How long the flow may take, positive and no greater than <see cref="MaximumSuspendBudget" />;
    ///     <see cref="DefaultSuspendBudget" /> suits Steam's usual few seconds.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting; a flow Steam already started keeps running.</param>
    /// <returns>How the flow ended. Only <see cref="SteamSuspendFlowResult.Completed" /> means Steam finished.</returns>
    /// <remarks>
    ///     Calls <c>SuspendResumeStore.InitiateSleep(true)</c> once and polls <c>suspending</c> inside the
    ///     page. After the PC resumes, the host calls <see cref="NotifyResumedAsync" />.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive or exceeds the maximum.</exception>
    public static async Task<SteamSuspendFlowResult> RunSuspendFlowAsync(ISteamUiTransport transport,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (budget <= TimeSpan.Zero || budget > MaximumSuspendBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(budget),
                "The suspend budget must be positive and no greater than 25 seconds.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var value = await EvaluateAsync(transport, CreateSuspendFlowExpression(
            DateTimeOffset.UtcNow.Add(CommandTimeout).ToUnixTimeMilliseconds(), budget),
            budget + SuspendReplyMargin, cancellationToken).ConfigureAwait(false);
        return value switch
        {
            "completed" => SteamSuspendFlowResult.Completed,
            "refused" => SteamSuspendFlowResult.Refused,
            "failed" => SteamSuspendFlowResult.Failed,
            "timeout" => SteamSuspendFlowResult.TimedOut,
            _ => SteamSuspendFlowResult.Unavailable
        };
    }

    /// <summary>Tells Steam the system has resumed, so it runs its own resume flow.</summary>
    /// <param name="transport">The host-owned subscribed transport.</param>
    /// <param name="cancellationToken">Cancels the bounded request.</param>
    /// <returns>
    ///     Whether <c>SuspendResumeStore.InitiateResume()</c> was called in a ready SharedJSContext whose
    ///     generations did not change. Steam's resume work continues after the call returns.
    /// </returns>
    /// <remarks>No retry and no fallback.</remarks>
    public static async Task<bool> NotifyResumedAsync(ISteamUiTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await EvaluateAsync(transport, CreateResumeExpression(
            DateTimeOffset.UtcNow.Add(CommandTimeout).ToUnixTimeMilliseconds()), CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        return value == "true";
    }

    /// <summary>Builds the power-menu request.</summary>
    /// <param name="expiresAt">UTC Unix time in milliseconds after which the page must refuse to call Steam.</param>
    /// <returns>JavaScript returning true only after <c>OpenPowerMenu(null)</c> was called.</returns>
    internal static string CreateOpenPowerMenuExpression(long expiresAt)
    {
        return $$"""
                 (()=>{
                   try {
                     if(Date.now()>{{expiresAt.ToString(CultureInfo.InvariantCulture)}})return false;
                     // Steam publishes its UI store as window.SteamUIStore; its sleep manager opens the
                     // menu the same way for the native power-menu notification.
                     const ui=window.SteamUIStore;
                     if(typeof ui?.OpenPowerMenu!=='function')return false;
                     ui.OpenPowerMenu(null);
                     return true;
                   }catch{return false;}
                 })()
                 """;
    }

    /// <summary>Builds the suspend-flow request.</summary>
    /// <param name="expiresAt">UTC Unix time in milliseconds after which the page must refuse to start the flow.</param>
    /// <param name="budget">How long the page waits for <c>suspending</c> to return to false.</param>
    /// <returns>
    ///     An async expression answering <c>completed</c>, <c>refused</c>, <c>failed</c>, <c>timeout</c> or
    ///     <c>unavailable</c>.
    /// </returns>
    internal static string CreateSuspendFlowExpression(long expiresAt, TimeSpan budget)
    {
        var budgetMs = (long)Math.Ceiling(budget.TotalMilliseconds);
        return $$"""
                 (async()=>{
                   try {
                     if(Date.now()>{{expiresAt.ToString(CultureInfo.InvariantCulture)}})return 'unavailable';
                     // Steam publishes its sleep manager as window.SuspendResumeStore.
                     const store=window.SuspendResumeStore;
                     if(typeof store?.InitiateSleep!=='function'||typeof store.GetSuspendResumeState!=='function'
                       ||typeof store.suspending!=='boolean')return 'unavailable';
                     // Steam ignores a request while blockers are held or a suspend is in progress.
                     if(store.suspending)return 'refused';
                     const deadline=Date.now()+{{budgetMs.ToString(CultureInfo.InvariantCulture)}};
                     // true is suppress_actual_suspend: Steam runs its flow and leaves the PC running.
                     store.InitiateSleep(true);
                     if(store.suspending!==true)return 'refused';
                     while(store.suspending===true){
                       if(Date.now()>=deadline)return 'timeout';
                       await new Promise(r=>setTimeout(r,Math.max(0,Math.min({{SuspendPollMs.ToString(CultureInfo.InvariantCulture)}},deadline-Date.now()))));
                     }
                     return store.GetSuspendResumeState()==={{SuspendProgressComplete.ToString(CultureInfo.InvariantCulture)}}?'completed':'failed';
                   }catch{return 'unavailable';}
                 })()
                 """;
    }

    /// <summary>Builds the resume notification.</summary>
    /// <param name="expiresAt">UTC Unix time in milliseconds after which the page must refuse to call Steam.</param>
    /// <returns>JavaScript returning true only after <c>InitiateResume()</c> was called.</returns>
    internal static string CreateResumeExpression(long expiresAt)
    {
        return $$"""
                 (()=>{
                   try {
                     if(Date.now()>{{expiresAt.ToString(CultureInfo.InvariantCulture)}})return false;
                     const store=window.SuspendResumeStore;
                     if(typeof store?.InitiateResume!=='function')return false;
                     store.InitiateResume();
                     return true;
                   }catch{return false;}
                 })()
                 """;
    }

    /// <summary>Evaluates once in a ready SharedJSContext and keeps the answer only at the same generations.</summary>
    /// <param name="transport">The host-owned subscribed transport.</param>
    /// <param name="expression">The request.</param>
    /// <param name="timeout">The dispatch budget.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answer, or null when nothing trustworthy came back.</returns>
    private static async Task<string?> EvaluateAsync(ISteamUiTransport transport, string expression,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = transport.GetSnapshots().FirstOrDefault(snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext && snapshot.Health == SteamUiTransportHealth.Ready);
        if (before is null)
        {
            return null;
        }

        var result = await transport.EvaluateAsync(SteamUiTargetRole.SharedJsContext, expression, timeout,
            cancellationToken).ConfigureAwait(false);
        return result.Answered && result.Generations == before.Generations
                               && SteamSharedContext.IsReadyAt(transport, before.Generations)
            ? result.Value
            : null;
    }
}
