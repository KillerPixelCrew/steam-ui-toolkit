using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Loads an unrestricted frontend bundle through the shared bridge and patch lifecycle.</summary>
public static class SteamPluginFrontendSurface
{
    /// <summary>Declares a package-owned frontend and its optional JSON backend.</summary>
    /// <param name="id">Unique generation-bound module and patch identity.</param>
    /// <param name="owner">Identity shared by every module in one package instance.</param>
    /// <param name="module">Package-local module identity used in diagnostics.</param>
    /// <param name="script">Unrestricted UTF-8 JavaScript, run with the toolkit API as <c>api</c>.</param>
    /// <param name="style">Optional unrestricted stylesheet.</param>
    /// <param name="enabled">Whether this instance may still run.</param>
    /// <param name="invoke">Optional backend, receiving the package's method and payload.</param>
    /// <param name="failed">Disables the entire owning package when any module fails.</param>
    /// <param name="read">Optional detached JSON state published to this frontend.</param>
    /// <returns>The independently removable toolkit module.</returns>
    /// <remarks>
    ///     The script has the privileges of Steam's page; failure isolation does not sandbox it.
    ///     Register the returned module with the shared runtime and use a fresh identity when the
    ///     host explicitly reloads a failed package. The failure callback must close admission for
    ///     every sibling sharing the owner, and the host must drain backend calls before unloading
    ///     package code. A null state reading withholds publication rather than clearing prior state.
    /// </remarks>
    public static ISteamUiModule Module(string id, string owner, string module, string script, string? style,
        Func<bool> enabled, Func<string, JsonElement, CancellationToken, Task<JsonElement?>>? invoke,
        Action<string, string> failed, Func<JsonElement?>? read = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(failed);
        var descriptor = JsonSerializer.Serialize(new { id, owner, module, script, style });
        return new SteamUiModule(id, [new FrontendPatch(id, descriptor, module, enabled, failed)],
            [new SteamUiStatePublication(id, enabled, () => ValueTask.FromResult(read?.Invoke()))], [
                new SteamUiCommandHandler(id, "invoke", async (request, token) =>
                {
                    if (!enabled())
                    {
                        return SteamUiCommandResult.Quarantined;
                    }

                    if (invoke is null)
                    {
                        return SteamUiCommandResult.Invalid("This frontend has no backend.");
                    }

                    if (!request.Payload.TryGetProperty("method", out var method)
                        || method.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(method.GetString())
                        || !request.Payload.TryGetProperty("payload", out var payload))
                    {
                        return SteamUiCommandResult.Invalid("Invalid frontend request.");
                    }

                    try
                    {
                        return new SteamUiCommandResult(true, null,
                            await invoke(method.GetString()!, payload, token).ConfigureAwait(false));
                    }
                    catch (Exception error) when (error is not OutOfMemoryException &&
                                                  error is not OperationCanceledException)
                    {
                        failed(module, error.Message);
                        return SteamUiCommandResult.Invalid(error.Message);
                    }
                }),
                new SteamUiCommandHandler(id, "failure", (request, _) =>
                {
                    if (request.Payload.TryGetProperty("reason", out var reason) &&
                        reason.ValueKind == JsonValueKind.String)
                    {
                        var name = request.Payload.TryGetProperty("module", out var child) &&
                                   child.ValueKind == JsonValueKind.String
                            ? child.GetString()!
                            : module;
                        failed(name, reason.GetString()!);
                    }

                    return Task.FromResult(SteamUiCommandResult.Applied);
                })
            ]);
    }

    private sealed class FrontendPatch(
        string id,
        string descriptor,
        string module,
        Func<bool> enabled,
        Action<string, string> failed)
        : ISteamUiPatch
    {
        public string Id => id;
        public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;
        public TimeSpan OperationTimeout => TimeSpan.FromSeconds(15);

        public Task<SteamUiPatchProbeResult> ProbeAsync(SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            return SteamUiPatchEvaluation.EvaluateProbeAsync(context, TargetRole,
                Expression("return JSON.stringify(p.probe());"),
                value => value.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                "plugin-frontends/react-v1", "Steam frontend runtime unavailable.", cancellationToken);
        }

        public async Task<SteamUiPatchOperationResult> ApplyAsync(SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            if (!enabled())
            {
                return new SteamUiPatchOperationResult(false, "Frontend admission closed.");
            }

            var result = await SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole,
                Expression("return JSON.stringify(await p.load(" + descriptor + "));"),
                "Frontend load failed.", cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                failed(module, result.Diagnostic ?? "Frontend load failed.");
            }

            return result;
        }

        public async Task<SteamUiPatchOperationResult> VerifyAsync(SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            var result = await SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole,
                Expression("return JSON.stringify(await p.status(" + SteamCef.JsString(id) + "));"),
                "Frontend verification failed.", cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                failed(module, result.Diagnostic ?? "Frontend verification failed.");
            }

            return result;
        }

        public Task<SteamUiPatchOperationResult> RemoveAsync(SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            return SteamUiPatchEvaluation.EvaluateOutcomeAsync(context, TargetRole,
                Expression("return JSON.stringify(await p.unload(" + SteamCef.JsString(id) + "));"),
                "Frontend removal failed.", cancellationToken);
        }

        private static string Expression(string body)
        {
            return "(async()=>{const b=window[" + SteamCef.JsString(SteamUiBridgeIdentity.Namespace)
                                                + "];const p=b?.gate('pluginFrontends');if(!p)return JSON.stringify({ok:false,error:'frontend runtime unavailable'});"
                                                + body + "})()";
        }
    }
}
