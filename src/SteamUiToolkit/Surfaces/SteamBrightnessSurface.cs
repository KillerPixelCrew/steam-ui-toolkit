using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>The panel backlight level Steam's brightness slider shows.</summary>
/// <param name="Percent">The level, 0 to 100, read from the panel itself.</param>
public sealed record SteamBrightnessState(int Percent);

/// <summary>What answers Steam's brightness slider.</summary>
public interface ISteamBrightnessBackend
{
    /// <summary>Sets the panel backlight.</summary>
    /// <param name="percent">The level, 0 to 100.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome.</returns>
    Task<SteamUiCommandResult> SetBrightnessAsync(int percent, CancellationToken cancellationToken);
}

/// <summary>Steam's own brightness slider in Quick Settings, revealed and backed.</summary>
/// <remarks>
/// The slider ships in the Windows client behind one settings boolean, and its native
/// <c>SetBrightness</c> is a stub whose change notifications never fire. The gate reveals the flag,
/// claims the setter so the slider's writes reach the backend, and feeds the store's observable
/// from the published level so a change made elsewhere moves the slider. Publish only on an actual
/// level change: a publication that merely restates the level fights a drag the store is ahead on.
/// </remarks>
public static class SteamBrightnessSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.brightness";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["setBrightness"];

    /// <summary>The gate that reveals Steam's brightness row and claims its setter.</summary>
    /// <remarks>
    /// The probe requires the native backend to be present: revealing the row without it would
    /// produce a slider that moves and changes nothing. Verification includes <c>setterOwned</c>
    /// because a revealed slider whose writes still reach the native stub is the exact broken
    /// state this gate first shipped with.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.brightness-availability",
        gateName: "brightness",
        fingerprint: "steam-brightness-v1:hidden-flag+present-backend",
        probeExpression: $$"""
            {{SteamUiProbeJs.Preamble("steam_ui_brightness_probe_")}}
              const store=req('59547')&&req('59547').mG&&req('59547').mG.Get();
              const settings=store&&store.m_msgSettings;
              if(!settings)return JSON.stringify({error:'display settings unavailable'});
              const display=window.SteamClient&&SteamClient.System&&SteamClient.System.Display;
              return JSON.stringify({
                fieldPresent:'is_display_brightness_available' in settings,
                // Hidden, or visible because this gate revealed it. Requiring hidden alone was the
                // self-incompatibility teardown loop: a successful apply made this false, the next
                // poll declared the patch incompatible, and the manager removed the reveal it had
                // just verified — the row flickered on a ~25-second cycle on the device (2026-08-30).
                revealable:settings.is_display_brightness_available!==true
                  ||settings.__steamUiBrightnessRevealed===true,
                backendPresent:!!display&&typeof display.SetBrightness==='function'
                  &&typeof display.RegisterForBrightnessChanges==='function'
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamGatePatch.Flag(root, "fieldPresent")
            && SteamGatePatch.Flag(root, "revealable")
            && SteamGatePatch.Flag(root, "backendPresent"),
        verifyOk: "status.installed&&status.available&&status.setterOwned",
        removeOk: "!status.available",
        subject: "Brightness gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamBrightnessState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamBrightnessState);

    /// <summary>Declares the surface as one module: the gate, the state, and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current level, or null when the panel refuses to report one.</param>
    /// <param name="backend">What answers the slider's writes.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamBrightnessState?>> read,
        ISteamBrightnessBackend backend,
        string id = "brightness")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamBrightnessState),
            ],
            commands:
            [
                new(PatchId, "setBrightness", (request, cancellationToken) =>
                    SteamUiPayload.TryReadInt(request.Payload, "percent", 0, 100, out int percent)
                        ? backend.SetBrightnessAsync(percent, cancellationToken)
                        : SteamSurfaceModule.Invalid("The brightness payload is invalid.")),
            ]);
    }
}
