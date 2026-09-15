using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
/// One registered Steam service/store gate, driven entirely by data: a probe expression with a
/// compatibility predicate over its JSON, and the injected gate's install/status/remove surface.
/// </summary>
/// <remarks>
/// The behavior every gate shares lives here once: the probe skeleton, the
/// <c>bridge.install()</c> apply, and the status-checked verify/remove wrappers. What a gate
/// supplies (a namespace, an RPC answer, a revealed flag) lives in its injected fragment under
/// <c>SteamUiAssets\Source\gates\</c>; what makes the client compatible lives in the probe
/// expression and predicate each surface declares. Every probe accepts "already ours" as
/// compatible — requiring the pre-patch shape alone made a successful apply invalidate its own next
/// probe and tear the gate down (see the inline probe comments on each surface).
/// </remarks>
public sealed class SteamGatePatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;
    private const string BridgeUnavailable =
        "return JSON.stringify({ok:false,error:'bridge unavailable'});";
    private readonly string _fingerprint;
    private readonly string _probeExpression;
    private readonly Func<JsonElement, bool> _compatible;
    private readonly string _verifyOk;
    private readonly string _removeOk;
    private readonly string _applyExpression;
    private readonly string _verifyExpression;
    private readonly string _removeExpression;
    private readonly string _subject;

    /// <summary>Declares one gate.</summary>
    /// <param name="id">Stable patch id.</param>
    /// <param name="resourceKey">The owned client resource, serialized against conflicts.</param>
    /// <param name="gateName">The name the injected bridge registers this gate under.</param>
    /// <param name="fingerprint">Stable structural fingerprint reported on a positive probe.</param>
    /// <param name="probeExpression">Read-only probe naming literal modules only.</param>
    /// <param name="compatible">Reads the probe's JSON into a compatibility verdict.</param>
    /// <param name="verifyOk">JS predicate over the gate's <c>status</c> proving it holds.</param>
    /// <param name="removeOk">JS predicate over <c>status</c> proving removal left nothing.</param>
    /// <param name="subject">Diagnostic subject, e.g. "Audio namespace".</param>
    public SteamGatePatch(
        string id,
        string resourceKey,
        string gateName,
        string fingerprint,
        string probeExpression,
        Func<JsonElement, bool> compatible,
        string verifyOk,
        string removeOk,
        string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeExpression);
        ArgumentNullException.ThrowIfNull(compatible);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifyOk);
        ArgumentException.ThrowIfNullOrWhiteSpace(removeOk);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Id = id;
        ResourceKey = resourceKey;
        _fingerprint = fingerprint;
        _probeExpression = probeExpression;
        _compatible = compatible;
        _verifyOk = verifyOk;
        _removeOk = removeOk;
        _subject = subject;
        string gate = SteamCef.JsString(gateName);
        _applyExpression = GateExpression(gate, "return JSON.stringify(bridge.install());");
        _verifyExpression = GateExpression(
            gate,
            "const status=bridge.status();return JSON.stringify({ok:" + verifyOk + ",status});");
        _removeExpression = GateExpression(
            gate,
            "const removed=bridge.remove();const status=bridge.status();"
                + "return JSON.stringify({ok:removed.ok&&" + removeOk + "});");
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey { get; }

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>The read-only probe this gate evaluates.</summary>
    internal string ProbeExpression => _probeExpression;

    /// <summary>The compatibility verdict over the probe's JSON.</summary>
    internal Func<JsonElement, bool> Compatible => _compatible;

    /// <summary>The JS predicate over <c>status</c> that verification requires.</summary>
    internal string VerifyOk => _verifyOk;

    /// <summary>The JS predicate over <c>status</c> that removal requires.</summary>
    internal string RemoveOk => _removeOk;

    /// <inheritdoc />
    public Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        ProbeAsync(context, _probeExpression, _compatible, _fingerprint, cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _applyExpression,
            _subject + " installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _verifyExpression,
            _subject + " verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            _removeExpression,
            _subject + " removal failed.",
            cancellationToken);

    /// <summary>Evaluates a SharedJSContext probe and reads it into a probe result.</summary>
    /// <param name="context">The patch context to evaluate through.</param>
    /// <param name="expression">The read-only probe.</param>
    /// <param name="compatible">Reads the probe's JSON into a compatibility verdict.</param>
    /// <param name="fingerprint">The fingerprint reported on a positive verdict.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>A compatible and unique result, or the page's own answer as the diagnostic.</returns>
    internal static async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        string expression,
        Func<JsonElement, bool> compatible,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            bool matched = compatible(document.RootElement);
            return new SteamUiPatchProbeResult(
                true,
                matched,
                matched,
                matched ? fingerprint : null,
                matched ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <summary>Builds an expression bound to one registered injected gate.</summary>
    /// <param name="gate">The gate name as a JavaScript string literal.</param>
    /// <param name="body">Statements run with <c>bridge</c> bound to the gate.</param>
    /// <param name="unavailable">The statement run instead when no such gate is installed.</param>
    /// <returns>The complete self-invoking expression.</returns>
    /// <remarks>
    /// A missing gate reads the same as a missing bridge, because from here they are the same
    /// failure: nothing of ours is installed to talk to.
    /// </remarks>
    internal static string GateExpression(
        string gate,
        string body,
        string unavailable = BridgeUnavailable) =>
        "(()=>{const b=window["
        + SteamCef.JsString(BridgeNamespace)
        + "];const bridge=b&&b.gate?b.gate(" + gate + "):null;"
        + "if(!bridge)" + unavailable
        + body
        + "})()";
}
