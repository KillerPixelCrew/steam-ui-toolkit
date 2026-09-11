using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One choice a timeout row offers.</summary>
/// <param name="Seconds">The timeout in seconds; zero means never.</param>
/// <param name="Label">The choice as the row shows it. Bounded by the gate.</param>
public sealed record SteamTimeoutOption(int Seconds, string Label);

/// <summary>One host-owned row in Steam's Screensaver settings.</summary>
/// <param name="Id">
/// Stable row identity: a lowercase letter, then up to 31 lowercase letters, digits or hyphens. The
/// row's choices come back under it.
/// </param>
/// <param name="Label">The row's label.</param>
/// <param name="Description">A line under the label, or empty.</param>
/// <param name="Seconds">The observed value in seconds; zero means never.</param>
/// <param name="Options">Every choice the row offers. Only these can be selected.</param>
/// <param name="Available">Whether the row accepts a choice right now.</param>
public sealed record SteamTimeoutRow(
    string Id,
    string Label,
    string Description,
    int Seconds,
    IReadOnlyList<SteamTimeoutOption> Options,
    bool Available);

/// <summary>What the host adds to Steam's Screensaver settings.</summary>
/// <param name="Rows">The rows, in order, appended after Steam's own; at most four.</param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamScreensaverState(IReadOnlyList<SteamTimeoutRow> Rows, long Revision = 0);

/// <summary>Steam's own screensaver timeouts, as its client settings hold them.</summary>
/// <param name="PluggedInSeconds">
/// <c>system_idle_screensaver_ac_sec</c>: the timeout the Screensaver section edits on a machine
/// Steam believes has no battery, and the plugged-in one otherwise. Zero means disabled.
/// </param>
/// <param name="BatterySeconds"><c>system_idle_screensaver_battery_sec</c>, or null when Steam holds no value.</param>
/// <param name="Battery">
/// Whether Steam believes the machine has a battery, which is when it keeps the two timeouts apart
/// on its Power page.
/// </param>
public sealed record SteamScreensaverReport(int PluggedInSeconds, int? BatterySeconds, bool Battery);

/// <summary>What answers Steam's Screensaver settings.</summary>
public interface ISteamScreensaverBackend
{
    /// <summary>Receives Steam's screensaver timeouts.</summary>
    /// <remarks>
    /// Sent when the gate first reads them, whenever they change while the settings page is open,
    /// and each time the page opens, so a host can refresh what it publishes.
    /// </remarks>
    /// <param name="report">The timeouts.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>The outcome. A refusal is reported rather than swallowed.</returns>
    Task<SteamUiCommandResult> ReportAsync(SteamScreensaverReport report, CancellationToken cancellationToken);

    /// <summary>Applies a choice made in one of the host's rows.</summary>
    /// <param name="row">The row's <see cref="SteamTimeoutRow.Id"/>.</param>
    /// <param name="seconds">The chosen timeout in seconds; zero means never.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome, with its reason when refused.</returns>
    Task<SteamUiCommandResult> SetTimeoutAsync(string row, int seconds, CancellationToken cancellationToken);
}

/// <summary>
/// Big Picture's Screensaver settings, with host-owned timeout rows beside Steam's own screensaver
/// timeout.
/// </summary>
/// <remarks>
/// The Settings root builds its page list through <c>React.useMemo</c>. The gate takes the shared
/// useMemo claim, replaces the customization page's content with a wrapper that renders it, and in
/// what the page renders replaces the Screensaver section, found by its label token and its
/// <c>ForceScreensaver</c> call, with a wrapper that appends the host's rows. The rows use Valve's
/// own dropdown field. Steam's screensaver timeouts are read from its client settings store inside
/// Steam's mobx observer and reported to the host.
/// <para>
/// Mapped from the September 2026 client beta's bundle on 2026-09-11: the section's label token
/// with <c>ForceScreensaver</c> occurs in exactly one module, the page list carries
/// <c>/settings/customization</c> from Steam's route table, and the section's idle row writes
/// <c>system_idle_screensaver_ac_sec</c>.
/// </para>
/// </remarks>
public static class SteamScreensaverSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.screensaver";

    /// <summary>The longest timeout a row or a report may carry: one week, in seconds.</summary>
    public const int MaximumSeconds = 604800;

    /// <summary>The most rows one publication may carry.</summary>
    public const int MaximumRows = 4;

    /// <summary>The most choices one row may offer.</summary>
    public const int MaximumOptions = 16;

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["report", "setTimeout"];

    /// <summary>The gate that adds the host's rows to Steam's Screensaver section.</summary>
    /// <remarks>
    /// Each structural fact is reported separately, so an incompatible client says which one moved.
    /// The observer hook is reported but not required. The useMemo claim is shared and never
    /// examined here, so a claim this or another gate already holds stays compatible.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.settings-pages",
        gateName: "screensaver",
        fingerprint: "steam-screensaver-v1:unique-section-module+customization-route+settings-store",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_screensaver_probe_")}}
              let route='';
              try{route=req.exported(['GameAPIOSK:','/gameapiosk'],
                v=>typeof v?.Settings?.Customization==='function').Settings.Customization();}catch{}
              let settings=false;
              try{settings=!!req.exported(['get clientSettings()','m_setDeferredSettings'],
                v=>!!v&&typeof v==='object'&&typeof v.clientSettings==='object');}catch{}
              return JSON.stringify({
                react:count(['react.transitional.element','useState','cloneElement','createElement']),
                fields:count(['DialogSlider_Container','DropDownField','SliderField']),
                section:count(['"#Settings_Customization_Screensaver"','ForceScreensaver']),
                route:typeof route==='string'&&route.startsWith('/')?route:'',
                settings:settings,
                observer:count(['mobx-react-lite requires React with Hooks support'])
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "fields")
            && SteamUiPatchEvaluation.IsOne(root, "section")
            && root.TryGetProperty("route", out JsonElement route)
            && route.ValueKind == JsonValueKind.String
            && route.GetString() is { Length: > 0 }
            && SteamGatePatch.Flag(root, "settings"),
        verifyOk: "status.installed&&status.resolved&&status.claimed",
        removeOk: "!status.claimed",
        subject: "Screensaver settings gate");

    private static readonly Regex RowId = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamScreensaverState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamScreensaverState);

    /// <summary>
    /// Reads the exact <c>report</c> payload: <c>acSeconds</c>, <c>batterySeconds</c> (a number or
    /// null) and <c>battery</c>, nothing else.
    /// </summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="report">The report, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadReport(JsonElement payload, out SteamScreensaverReport report)
    {
        report = new(0, null, false);
        if (payload.ValueKind != JsonValueKind.Object
            || !SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadInt(payload, "acSeconds", 0, MaximumSeconds, out int pluggedIn)
            || !payload.TryGetProperty("batterySeconds", out JsonElement battery)
            || !payload.TryGetProperty("battery", out JsonElement hasBattery)
            || hasBattery.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        int? batterySeconds = null;
        if (battery.ValueKind != JsonValueKind.Null)
        {
            if (!SteamUiPayload.TryReadInt(payload, "batterySeconds", 0, MaximumSeconds, out int value))
            {
                return false;
            }
            batterySeconds = value;
        }

        report = new(pluggedIn, batterySeconds, hasBattery.ValueKind is JsonValueKind.True);
        return true;
    }

    /// <summary>Reads the exact <c>setTimeout</c> payload: a row id and a number of seconds.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="row">The row id, when this returns true.</param>
    /// <param name="seconds">The chosen timeout, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadTimeout(JsonElement payload, out string row, out int seconds)
    {
        seconds = 0;
        if (!SteamUiPayload.TryReadBoundedString(payload, "row", 32, out row)
            || !RowId.IsMatch(row)
            || !SteamUiPayload.TryReadInt(payload, "seconds", 0, MaximumSeconds, out seconds)
            || !SteamUiPayload.HasExactly(payload, 2))
        {
            row = string.Empty;
            seconds = 0;
            return false;
        }

        return true;
    }

    /// <summary>Declares the surface as one module: the gate, the rows, the report and the choices.</summary>
    /// <param name="enabled">Whether the rows may be published right now.</param>
    /// <param name="read">The rows, or null when there is nothing to say yet.</param>
    /// <param name="backend">What hears the report and applies the choices.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamScreensaverState?>> read,
        ISteamScreensaverBackend backend,
        string id = "screensaver")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamScreensaverState),
            ],
            commands:
            [
                new(PatchId, "report", (request, cancellationToken) =>
                    TryReadReport(request.Payload, out SteamScreensaverReport report)
                        ? backend.ReportAsync(report, cancellationToken)
                        : SteamSurfaceModule.Invalid("The screensaver report is invalid.")),
                new(PatchId, "setTimeout", (request, cancellationToken) =>
                    TryReadTimeout(request.Payload, out string row, out int seconds)
                        ? backend.SetTimeoutAsync(row, seconds, cancellationToken)
                        : SteamSurfaceModule.Invalid("The timeout payload is invalid.")),
            ]);
    }
}
