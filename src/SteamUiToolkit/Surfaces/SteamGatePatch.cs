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
    private readonly string _gateName;
    private readonly string _fingerprint;
    private readonly string _probeExpression;
    private readonly Func<JsonElement, bool> _compatible;
    private readonly string _verifyOk;
    private readonly string _removeOk;
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
        _gateName = gateName;
        _fingerprint = fingerprint;
        _probeExpression = probeExpression;
        _compatible = compatible;
        _verifyOk = verifyOk;
        _removeOk = removeOk;
        _subject = subject;
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

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            _probeExpression,
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
            bool compatible = _compatible(document.RootElement);
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? _fingerprint : null,
                compatible ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "return JSON.stringify(bridge.install());",
            _subject + " installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.status();"
            + "return JSON.stringify({ok:" + _verifyOk + ",status});",
            _subject + " verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.remove();const status=bridge.status();"
            + "return JSON.stringify({ok:removed.ok&&" + _removeOk + "});",
            _subject + " removal failed.",
            cancellationToken);

    /// <summary>Reads one boolean flag out of a probe result.</summary>
    /// <param name="root">The probe's JSON.</param>
    /// <param name="name">The flag's property name.</param>
    /// <returns>True only when the property exists and is literally <c>true</c>.</returns>
    public static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True;

    private Task<SteamUiPatchOperationResult> EvaluateAsync(
        SteamUiPatchContext context,
        string body,
        string fallback,
        CancellationToken cancellationToken)
    {
        // `bridge` is bound to this patch's own gate, looked up in the registry the fragments
        // register into. A missing gate reads the same as a missing bridge, because from here they
        // are the same failure: nothing of ours is installed to talk to.
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate(" + SteamCef.JsString(_gateName) + "):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + body
            + "})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            fallback,
            cancellationToken);
    }
}
