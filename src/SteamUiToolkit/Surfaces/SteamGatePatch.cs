using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     One registered SharedJSContext gate. Every phase asks the injected gate itself: the probe is
///     the gate's own <c>probe()</c>, apply its <c>install()</c>, verify and remove its
///     <c>status()</c> and <c>remove()</c>, read through one shared predicate over the standard status.
/// </summary>
/// <remarks>
///     A gate is declared in TypeScript with <c>defineGate</c>, whose <c>find</c> resolves what the gate
///     needs without writing anything, so its probe asks exactly the question its install asks and
///     cannot drift from it. While installed the gate answers its probe from the handles it holds, so a
///     holding gate stays compatible with its own next probe. The manager supplies phase budgets,
///     serialization and rollback; the gate owns exact restoration of its page mutations.
/// </remarks>
public sealed class SteamGatePatch : ISteamUiPatch
{
    /// <summary>The verification predicate over the standard gate status.</summary>
    /// <remarks>The gate is installed and every claim it took is still in place.</remarks>
    internal const string VerifyPredicate = "status.installed===true&&status.held===true";

    /// <summary>The removal predicate over the standard gate status and the removal result.</summary>
    /// <remarks>The removal succeeded, the gate is not installed and its ledger holds nothing.</remarks>
    internal const string RemovePredicate = "removed.ok===true&&status.installed===false&&status.claims===0";

    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;

    private const string BridgeUnavailable =
        "return JSON.stringify({ok:false,error:'bridge unavailable'});";

    // A missing bridge or gate is a client that has not got there yet, never a build that moved.
    private const string ProbeUnavailable =
        "return JSON.stringify({ok:false,outcome:'notReady',reason:'bridge unavailable'});";

    private const string ProbeBody =
        "if(typeof bridge.probe!=='function')"
        + "return JSON.stringify({ok:false,outcome:'incompatible',reason:'the gate has no probe'});"
        + "return JSON.stringify(bridge.probe());";

    private readonly string _applyExpression;
    private readonly string _fingerprint;
    private readonly string _removeExpression;
    private readonly string _subject;
    private readonly string _verifyExpression;
    private readonly Action<JsonElement, string?>? _verifyObserver;

    /// <summary>Declares one gate.</summary>
    /// <param name="id">Stable patch id.</param>
    /// <param name="gateName">The name the injected bridge registers this gate under.</param>
    /// <param name="fingerprint">Stable structural fingerprint reported on a positive probe.</param>
    /// <param name="subject">Diagnostic subject, e.g. "Audio namespace".</param>
    /// <param name="verifyObserver">
    ///     Hears every verification's result with the gate's <c>status</c>, or the page's error when the
    ///     gate could not be reached, for diagnostics a gate reports beyond its standard status. It
    ///     decides nothing; null for none.
    /// </param>
    public SteamGatePatch(
        string id,
        string gateName,
        string fingerprint,
        string subject,
        Action<JsonElement, string?>? verifyObserver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Id = id;
        GateName = gateName;
        _fingerprint = fingerprint;
        _subject = subject;
        _verifyObserver = verifyObserver;
        var gate = SteamCef.JsString(gateName);
        ProbeExpression = GateExpression(gate, ProbeBody, ProbeUnavailable);
        _applyExpression = GateExpression(gate, "return JSON.stringify(bridge.install());");
        _verifyExpression = GateExpression(
            gate,
            "const status=bridge.status();return JSON.stringify({ok:" + VerifyPredicate + ",status});");
        _removeExpression = GateExpression(
            gate,
            "const removed=bridge.remove();const status=bridge.status();"
            + "return JSON.stringify({ok:" + RemovePredicate + ",error:removed.error,status});");
    }

    /// <summary>The name the injected bridge registers this gate under.</summary>
    internal string GateName { get; }

    /// <summary>The probe this gate evaluates: the injected gate's own <c>probe()</c>.</summary>
    internal string ProbeExpression { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateGateProbeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            ProbeExpression,
            _fingerprint,
            "SharedJSContext is unavailable.",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _applyExpression,
            _subject + " installation failed.",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _verifyExpression,
            _subject + " verification failed.",
            _verifyObserver,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _removeExpression,
            _subject + " removal failed.",
            cancellationToken);
    }

    /// <summary>Builds one evaluation that asks several gates for their probe answers.</summary>
    /// <param name="gateNames">The registered gate names, in the order their answers are wanted.</param>
    /// <returns>An expression answering a JSON array with one probe answer per gate.</returns>
    /// <remarks>
    ///     The health check re-probes every absent gate in one evaluation. A missing bridge or gate
    ///     answers not ready, and a probe that throws answers incompatible with the error.
    /// </remarks>
    internal static string ProbeAllExpression(IEnumerable<string> gateNames)
    {
        var names = "[" + string.Join(",", gateNames.Select(SteamCef.JsString)) + "]";
        return "(()=>{const b=window["
               + SteamCef.JsString(BridgeNamespace)
               + "];return JSON.stringify(" + names + ".map(n=>{"
               + "const g=b&&b.gate?b.gate(n):null;"
               + "if(!g)return{ok:false,outcome:'notReady',reason:'bridge unavailable'};"
               + "if(typeof g.probe!=='function')return{ok:false,outcome:'incompatible',reason:'the gate has no probe'};"
               + "try{return g.probe();}catch(error){return{ok:false,outcome:'incompatible',reason:String(error)};}"
               + "}));})()";
    }

    /// <summary>Builds an expression bound to one registered injected gate.</summary>
    /// <param name="gate">The gate name as a JavaScript string literal.</param>
    /// <param name="body">Statements run with <c>bridge</c> bound to the gate.</param>
    /// <param name="unavailable">The statement run instead when no such gate is installed.</param>
    /// <param name="asynchronous">Whether the body awaits, so the expression evaluates to a promise.</param>
    /// <returns>The complete self-invoking expression.</returns>
    /// <remarks>
    ///     A missing gate reads the same as a missing bridge, because from here they are the same
    ///     failure: nothing of ours is installed to talk to.
    /// </remarks>
    internal static string GateExpression(
        string gate,
        string body,
        string unavailable = BridgeUnavailable,
        bool asynchronous = false)
    {
        return (asynchronous ? "(async()=>{" : "(()=>{")
               + "const b=window["
               + SteamCef.JsString(BridgeNamespace)
               + "];const bridge=b&&b.gate?b.gate(" + gate + "):null;"
               + "if(!bridge)" + unavailable
               + body
               + "})()";
    }
}
