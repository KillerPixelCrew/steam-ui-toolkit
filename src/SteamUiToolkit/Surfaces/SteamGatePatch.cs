using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
///     One registered Steam service/store gate, driven entirely by data: a probe expression with a
///     compatibility predicate over its JSON, and the injected gate's install/status/remove surface.
/// </summary>
/// <remarks>
///     The surface supplies read-only probe evidence and install/status/remove predicates. A probe
///     must recognize its own post-install claims as compatible. The manager supplies phase budgets,
///     serialization and rollback; the gate owns exact restoration of its page mutations.
/// </remarks>
public sealed class SteamGatePatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;

    private const string BridgeUnavailable =
        "return JSON.stringify({ok:false,error:'bridge unavailable'});";

    private readonly string _applyExpression;
    private readonly string _fingerprint;
    private readonly string _removeExpression;
    private readonly string _subject;
    private readonly string _verifyExpression;

    /// <summary>Declares one gate.</summary>
    /// <param name="id">Stable patch id.</param>
    /// <param name="gateName">The name the injected bridge registers this gate under.</param>
    /// <param name="fingerprint">Stable structural fingerprint reported on a positive probe.</param>
    /// <param name="probeExpression">Read-only probe that resolves the modules it needs by their source tokens.</param>
    /// <param name="compatible">Reads the probe's JSON into a compatibility verdict.</param>
    /// <param name="verifyOk">JS predicate over the gate's <c>status</c> proving it holds.</param>
    /// <param name="removeOk">JS predicate over <c>status</c> proving removal left nothing.</param>
    /// <param name="subject">Diagnostic subject, e.g. "Audio namespace".</param>
    public SteamGatePatch(
        string id,
        string gateName,
        string fingerprint,
        string probeExpression,
        Func<JsonElement, bool> compatible,
        string verifyOk,
        string removeOk,
        string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeExpression);
        ArgumentNullException.ThrowIfNull(compatible);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifyOk);
        ArgumentException.ThrowIfNullOrWhiteSpace(removeOk);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Id = id;
        _fingerprint = fingerprint;
        ProbeExpression = probeExpression;
        Compatible = compatible;
        VerifyOk = verifyOk;
        RemoveOk = removeOk;
        _subject = subject;
        var gate = SteamCef.JsString(gateName);
        _applyExpression = GateExpression(gate, "return JSON.stringify(bridge.install());");
        _verifyExpression = GateExpression(
            gate,
            "const status=bridge.status();return JSON.stringify({ok:" + verifyOk + ",status});");
        _removeExpression = GateExpression(
            gate,
            "const removed=bridge.remove();const status=bridge.status();"
            + "return JSON.stringify({ok:removed.ok&&" + removeOk + "});");
    }

    /// <summary>The read-only probe this gate evaluates.</summary>
    internal string ProbeExpression { get; }

    /// <summary>The compatibility verdict over the probe's JSON.</summary>
    internal Func<JsonElement, bool> Compatible { get; }

    /// <summary>The JS predicate over <c>status</c> that verification requires.</summary>
    internal string VerifyOk { get; }

    /// <summary>The JS predicate over <c>status</c> that removal requires.</summary>
    internal string RemoveOk { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        return SteamUiPatchEvaluation.EvaluateProbeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            ProbeExpression,
            Compatible,
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

    /// <summary>Builds an expression bound to one registered injected gate.</summary>
    /// <param name="gate">The gate name as a JavaScript string literal.</param>
    /// <param name="body">Statements run with <c>bridge</c> bound to the gate.</param>
    /// <param name="unavailable">The statement run instead when no such gate is installed.</param>
    /// <returns>The complete self-invoking expression.</returns>
    /// <remarks>
    ///     A missing gate reads the same as a missing bridge, because from here they are the same
    ///     failure: nothing of ours is installed to talk to.
    /// </remarks>
    internal static string GateExpression(
        string gate,
        string body,
        string unavailable = BridgeUnavailable)
    {
        return "(()=>{const b=window["
               + SteamCef.JsString(BridgeNamespace)
               + "];const bridge=b&&b.gate?b.gate(" + gate + "):null;"
               + "if(!bridge)" + unavailable
               + body
               + "})()";
    }
}
