using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
/// The one place a patch turns an injected expression into a patch result.
/// </summary>
/// <remarks>
/// Every host patch injects a self-contained expression that returns
/// <c>JSON.stringify({ok:…,error:…})</c>, so the parse is the same everywhere. It lived in three
/// copies that had already drifted: two returned only the caller's fallback text when the page
/// answered with a shape that had no <c>error</c> string, which is exactly the case a remote log
/// needs to see. Reading the page's own answer is the useful behaviour, so it is what all patches
/// now do.
/// </remarks>
public static class SteamUiPatchEvaluation
{
    private const int MaxDiagnosticLength = 2048;

    /// <summary>Evaluates an expression and reads its <c>ok</c>/<c>error</c> outcome.</summary>
    /// <param name="context">The patch context to evaluate through.</param>
    /// <param name="role">Which Steam target to evaluate in.</param>
    /// <param name="expression">The self-contained expression to evaluate.</param>
    /// <param name="fallback">Diagnostic used when the page reported nothing usable.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The patch operation result.</returns>
    public static Task<SteamUiPatchOperationResult> EvaluateOutcomeAsync(
        SteamUiPatchContext context,
        SteamUiTargetRole role,
        string expression,
        string fallback,
        CancellationToken cancellationToken) =>
        EvaluateOutcomeAsync(context, role, expression, fallback, inspect: null, cancellationToken);

    /// <summary>Evaluates an expression, lets the caller read its answer, then reads the outcome.</summary>
    /// <param name="context">The patch context to evaluate through.</param>
    /// <param name="role">Which Steam target to evaluate in.</param>
    /// <param name="expression">The self-contained expression to evaluate.</param>
    /// <param name="fallback">Diagnostic used when the page reported nothing usable.</param>
    /// <param name="inspect">Reads the parsed answer and the page's own error, if any, before the
    /// outcome is decided.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The patch operation result.</returns>
    internal static async Task<SteamUiPatchOperationResult> EvaluateOutcomeAsync(
        SteamUiPatchContext context,
        SteamUiTargetRole role,
        string expression,
        string fallback,
        Action<JsonElement, string?>? inspect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            role,
            expression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchOperationResult(false, result.Error ?? fallback);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool succeeded = root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
            if (succeeded && inspect is null)
            {
                return new SteamUiPatchOperationResult(true, null);
            }

            string? reported = root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
            inspect?.Invoke(root, reported);
            if (succeeded)
            {
                return new SteamUiPatchOperationResult(true, null);
            }
            return new SteamUiPatchOperationResult(
                false,
                reported ?? Bounded(result.Value) ?? fallback);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchOperationResult(false, ex.Message);
        }
    }

    /// <summary>Whether a probe counted exactly one match for a required structural token set.</summary>
    /// <param name="root">The parsed probe result.</param>
    /// <param name="property">The count property to check.</param>
    /// <returns><see langword="true"/> when the count is exactly one.</returns>
    /// <remarks>
    /// Exactly one, never "at least one": a second match means the Steam build has two candidate
    /// components and the patch cannot tell which one it would be modifying.
    /// </remarks>
    public static bool IsOne(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int count)
        && count == 1;

    /// <summary>Reads one boolean flag out of a probe result.</summary>
    /// <param name="root">The probe's JSON.</param>
    /// <param name="name">The flag's property name.</param>
    /// <returns>True only when the property exists and is literally <c>true</c>.</returns>
    public static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True;

    /// <summary>Whether a probe reported success and every named boolean was true.</summary>
    /// <param name="value">The raw probe result.</param>
    /// <param name="requiredFlags">Boolean properties that must all be present and true. With none,
    /// this is simply whether the page reported <c>ok</c>.</param>
    /// <returns><see langword="true"/> when the target is genuinely compatible.</returns>
    /// <remarks>
    /// Unparseable output is a failure, never an optimistic success.
    /// <para>
    /// A probe that reports its own structural findings alongside <c>ok</c> has to have them read.
    /// The glyph-style probe returned whether each build-coupled selector class still exists while
    /// only <c>ok</c> — which is <c>!!document.head</c> — decided compatibility, so a Steam build
    /// that renamed one of them was still called compatible and the patch installed rules that
    /// could no longer match anything, instead of falling back to Valve's native rendering.
    /// </para>
    /// </remarks>
    public static bool IsSuccessful(string value, params string[] requiredFlags)
    {
        ArgumentNullException.ThrowIfNull(requiredFlags);
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            foreach (string flag in requiredFlags)
            {
                if (!root.TryGetProperty(flag, out JsonElement reported)
                    || reported.ValueKind != JsonValueKind.True)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Truncates a page-supplied diagnostic to a bounded length.</summary>
    /// <param name="value">The raw diagnostic.</param>
    /// <returns>The bounded diagnostic, or null when there was nothing to report.</returns>
    public static string? Bounded(string? value) => value switch
    {
        null or "" => null,
        { Length: <= MaxDiagnosticLength } => value,
        _ => value[..MaxDiagnosticLength] + "...",
    };
}
