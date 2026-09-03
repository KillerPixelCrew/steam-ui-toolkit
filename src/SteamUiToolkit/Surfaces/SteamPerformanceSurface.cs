using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>
/// The performance state supplied in place of Steam's absent <c>SteamClient.System.Perf</c>.
/// </summary>
/// <remarks>
/// The field names are Valve's, taken from the generated protobuf metadata in the client's own
/// bundle (<c>CMsgSystemPerfLimits</c>, <c>CMsgSystemPerfSettingsGlobal</c>,
/// <c>CMsgSystemPerfSettingsPerApp</c>), because the store's controls read them by name. They are
/// spelled out here rather than derived from a naming policy: the outer object is camelCase for the
/// injected gate and the inner objects are the protobuf's snake_case, so one policy cannot serve
/// both and a wrong name is silently a missing control.
/// <para>
/// <b>Every field is nullable and omitted when null, and that is the safety property.</b> Control
/// availability is read straight out of this state — <c>msgLimits?.is_vrr_supported ?? false</c> —
/// so a field the backend cannot honour is left out and Valve's own wrapper renders nothing. Hiding
/// costs no CSS and no patching; adding a field is what makes a control appear.
/// </para>
/// <para>
/// <b>Limits and settings are a pair.</b> Hiding a control by omitting its <c>limits</c> field is
/// safe; advertising it in <c>limits</c> and then omitting its <c>settings</c> value is not —
/// Valve's component renders, finds no value, and throws inside Steam's error boundary, taking
/// the whole Performance tab with it (device, 2026-08-30).
/// </para>
/// </remarks>
public sealed record SteamPerformanceState
{
    /// <summary>Valve's "no game": the Steam client's own pseudo-app id, never <c>"0"</c>.</summary>
    /// <remarks>
    /// The profile header, the per-game toggle's availability and the name lookup all compare
    /// game ids against 769 (live-read 2026-09-02). Publishing "0" made the header take the
    /// game-specific branch, look up game id 0, and render "Use profile from" with an empty name.
    /// </remarks>
    public const string NoGame = "769";

    /// <summary>Bounds and support flags. Omitted fields hide their controls.</summary>
    [JsonPropertyName("limits")]
    public SteamPerformanceLimits? Limits { get; init; }

    /// <summary>Settings that apply to every application.</summary>
    [JsonPropertyName("global")]
    public SteamPerformanceGlobalSettings? Global { get; init; }

    /// <summary>Settings for the application the profile is currently being edited for.</summary>
    [JsonPropertyName("perApp")]
    public SteamPerformanceApplicationSettings? PerApp { get; init; }

    /// <summary>The running application's Steam AppID as a string, or <see cref="NoGame"/>.</summary>
    /// <remarks>
    /// Steam decides the per-game profile is in use by comparing this with
    /// <see cref="ActiveProfileGameId"/>: equal, and not the pseudo-app, means the running game's
    /// own profile is the one on screen.
    /// </remarks>
    [JsonPropertyName("currentGameId")]
    public string CurrentGameId { get; init; } = NoGame;

    /// <summary>The AppID whose profile is being edited, or <see cref="NoGame"/> for the global profile.</summary>
    [JsonPropertyName("activeProfileGameId")]
    public string ActiveProfileGameId { get; init; } = NoGame;
}

/// <summary>Bounds and support flags for the performance controls a backend can honour.</summary>
/// <remarks>
/// Deliberately partial. The message also carries CPU governor bounds, FSR sharpness bounds, split
/// scaling filters and scalers, external-display refresh bounds, and
/// <c>is_dynamic_refresh_rate_in_steam_supported</c>; none is modelled, so none of those controls
/// renders. <c>tdp_limit_min</c>/<c>tdp_limit_max</c> exist in this message and are also not
/// modelled: no component in the performance bundle renders a TDP control, so the fields would be
/// read by nothing. Valve's TDP row comes from <see cref="SteamPowerLimitSurface"/> instead.
/// </remarks>
public sealed record SteamPerformanceLimits
{
    /// <summary>The frame caps the slider offers, as its notches, in ascending order.</summary>
    /// <remarks>
    /// The slider's labels are <c>value.toString()</c> over this array, so the notches and the
    /// options are the same list; there is no separate label channel to fill.
    /// </remarks>
    [JsonPropertyName("fps_limit_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? FpsLimitOptions { get; init; }

    /// <summary>The same notches, for a display Steam considers external.</summary>
    /// <remarks>
    /// THE external-twin rule, referenced by every other twin here and by the delta reader: EVERY
    /// display field in this message has an <c>_external</c> twin, and Valve's controls read and
    /// write whichever side their own display test selects — a handheld's built-in panel can report
    /// <c>bDisplayIsExternal: true</c>, so on such hardware the external twin is the one that
    /// renders. Supplying only the internal fields left the frame-limit slider a grey bar with a
    /// label: the component rendered with an empty notch list. A backend managing one display
    /// should carry the same values in both twins rather than guess which Steam will read.
    /// </remarks>
    [JsonPropertyName("fps_limit_options_external")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? FpsLimitOptionsExternal { get; init; }

    /// <summary>Whether the panel supports variable refresh rate.</summary>
    [JsonPropertyName("is_vrr_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVrrSupported { get; init; }

    /// <summary>Whether a refresh rate can be chosen by hand.</summary>
    [JsonPropertyName("is_manual_display_refresh_rate_available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsManualDisplayRefreshRateAvailable { get; init; }

    /// <summary>Lowest selectable refresh rate in Hz.</summary>
    [JsonPropertyName("display_refresh_manual_hz_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayRefreshManualHzMin { get; init; }

    /// <summary>Lowest selectable refresh rate, for an externally-reported display.</summary>
    [JsonPropertyName("display_external_refresh_manual_hz_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayExternalRefreshManualHzMin { get; init; }

    /// <summary>Highest selectable refresh rate, for an externally-reported display.</summary>
    [JsonPropertyName("display_external_refresh_manual_hz_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayExternalRefreshManualHzMax { get; init; }

    /// <summary>Highest selectable refresh rate in Hz.</summary>
    [JsonPropertyName("display_refresh_manual_hz_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayRefreshManualHzMax { get; init; }
}

/// <summary>Performance settings that are not per-application.</summary>
public sealed record SteamPerformanceGlobalSettings
{
    /// <summary>The performance overlay level, as Valve's wire enum. See <see cref="SteamOverlayLevelWire"/>.</summary>
    /// <remarks>
    /// Always supply a number, and always one of the five the selector knows. It resolves the
    /// notch with <c>levels.find(l =&gt; l.value === current).notchIndex</c> and does not guard
    /// the miss, so a level outside the enum throws inside the render and Steam's error boundary
    /// blanks the whole Performance tab.
    /// </remarks>
    [JsonPropertyName("perf_overlay_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerfOverlayLevel { get; init; }

    /// <summary>Whether the panel shows its advanced rows.</summary>
    [JsonPropertyName("is_advanced_settings_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsAdvancedSettingsEnabled { get; init; }

    /// <summary>The second gate on the refresh-rate row for an externally-classified display.</summary>
    /// <remarks>
    /// Live-read from the refresh-rate hook 2026-08-30: availability is
    /// <c>external ? (is_manual_display_refresh_rate_available &amp;&amp;
    /// allow_external_display_refresh_control) : (is_manual_display_refresh_rate_available &amp;&amp;
    /// !disable_refresh_rate_management)</c>. A built-in panel that reports as external leaves the
    /// row hidden on the availability flag alone — this is the half that was missing.
    /// </remarks>
    [JsonPropertyName("allow_external_display_refresh_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowExternalDisplayRefreshControl { get; init; }
}

/// <summary>Performance settings for one application, or for the global profile.</summary>
public sealed record SteamPerformanceApplicationSettings
{
    /// <summary>The frame cap in FPS. Never zero: "off" is <see cref="IsFpsLimitEnabled"/>.</summary>
    [JsonPropertyName("fps_limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FpsLimit { get; init; }

    /// <summary>The same cap, for a display Steam considers external. See the limits twin.</summary>
    [JsonPropertyName("fps_limit_external")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FpsLimitExternal { get; init; }

    /// <summary>Whether the frame cap is applied at all.</summary>
    /// <remarks>Steam draws the cap and its on/off state from two fields. Without the flag the
    /// slider renders at the cap but reads as disabled.</remarks>
    [JsonPropertyName("is_fps_limit_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFpsLimitEnabled { get; init; }

    /// <summary>Whether variable refresh rate is on.</summary>
    [JsonPropertyName("is_vrr_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVrrEnabled { get; init; }

    /// <summary>The chosen refresh rate in Hz.</summary>
    [JsonPropertyName("display_refresh_manual_hz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayRefreshManualHz { get; init; }

    /// <summary>The same rate, for a display Steam considers external.</summary>
    [JsonPropertyName("display_external_refresh_manual_hz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayExternalRefreshManualHz { get; init; }

    /// <summary>Whether this application keeps its own profile rather than using the global one.</summary>
    [JsonPropertyName("is_game_perf_profile_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsGamePerfProfileEnabled { get; init; }
}

/// <summary>Translates Steam's <c>perf_overlay_level</c> wire values to and from the selector's notch order.</summary>
/// <remarks>
/// Valve added the Minimal preset last, so <c>EGraphicsPerfOverlayLevel</c> is Hidden=0, Basic=1,
/// Medium=2, Full=3, Minimal=4 — while the selector presents OFF, Minimal, Basic, Medium, Full.
/// Treating the wire value as the notch put the top level on the first notch and shifted the rest
/// (live-verified 2026-09-01: parking the selector on notch 1 stores <c>perf_overlay_level=4</c>).
/// A backend that thinks in notches translates at this boundary in both directions.
/// </remarks>
public static class SteamOverlayLevelWire
{
    /// <summary>Highest notch Steam's overlay-level selector has.</summary>
    /// <remarks>Read off the selector itself: it builds five entries, OFF plus 1 to 4, and
    /// resolves the current value against them without a fallback.</remarks>
    public const int MaximumNotch = 4;

    /// <summary>Maps a Steam wire value to the selector notch.</summary>
    /// <param name="steamValue">The <c>perf_overlay_level</c> value Steam sent.</param>
    /// <returns>The notch, with unknown values reading as off.</returns>
    public static int ToNotch(int steamValue) => steamValue switch
    {
        4 => 1,
        1 => 2,
        2 => 3,
        3 => 4,
        _ => 0,
    };

    /// <summary>Maps a selector notch to the Steam wire value the selector resolves.</summary>
    /// <param name="notch">The notch, 0 to <see cref="MaximumNotch"/>.</param>
    /// <returns>The wire value, with unknown notches reading as hidden.</returns>
    public static int ToSteam(int notch) => notch switch
    {
        1 => 4,
        2 => 1,
        3 => 2,
        4 => 3,
        _ => 0,
    };
}

/// <summary>The performance settings Steam's panel can ask a backend to write.</summary>
/// <remarks>
/// Only the settings behind a control this surface can render. A delta naming anything else is
/// reported as unsupported rather than silently dropped, because a control that appears to work
/// and does nothing is worse than one that is not there.
/// </remarks>
public enum SteamPerformanceSetting
{
    /// <summary>The frame cap in FPS.</summary>
    FrameLimit,

    /// <summary>Whether the frame cap applies.</summary>
    FrameLimitEnabled,

    /// <summary>The performance overlay level, as Valve's wire enum. See <see cref="SteamOverlayLevelWire"/>.</summary>
    OverlayLevel,

    /// <summary>Whether variable refresh rate is on.</summary>
    VariableRefreshRate,

    /// <summary>The manually chosen refresh rate in Hz.</summary>
    RefreshRateHz,

    /// <summary>Whether the running application keeps its own profile.</summary>
    PerApplicationProfileEnabled,

    /// <summary>Whether the advanced rows are shown.</summary>
    AdvancedSettingsEnabled,
}

/// <summary>One setting change Steam's performance panel asked for.</summary>
/// <param name="Kind">Which setting changed.</param>
/// <param name="Value">The requested value; meaning depends on <paramref name="Kind"/>. Flags
/// arrive as 0 or 1.</param>
public readonly record struct SteamPerformanceChange(SteamPerformanceSetting Kind, int Value)
{
    /// <summary>Reads the change as a flag.</summary>
    public bool AsFlag => Value != 0;
}

/// <summary>What one <c>UpdateSettings</c> call asked for.</summary>
/// <param name="Recognized">Changes the surface understands, in the order they appeared.</param>
/// <param name="ResetToDefault">Whether the panel asked to reset the current profile. Arrives on
/// its own: Valve's button sends only this flag.</param>
/// <param name="SteamAppId">The AppID the delta targets, or null for the global profile.</param>
/// <param name="Unsupported">Field names that were present and are not modelled, for the log.
/// Never empty silently.</param>
public sealed record SteamPerformanceDelta(
    IReadOnlyList<SteamPerformanceChange> Recognized,
    bool ResetToDefault,
    uint? SteamAppId,
    IReadOnlyList<string> Unsupported);

/// <summary>
/// Decodes a <c>CMsgSystemPerfUpdateSettings</c> that the injected gate forwarded as an object.
/// </summary>
/// <remarks>
/// Every setter in Valve's store builds a delta and hands it to the one <c>UpdateSettings</c>
/// method, so this is where all of them arrive. The message shapes belong to the client, so the
/// injected half forwards <c>toObject()</c> verbatim and this half does the interpreting; nothing
/// about the wire format is reimplemented on either side.
/// <para>
/// A delta carries only what changed, and a settings message nests
/// <c>settings_delta.global</c>/<c>settings_delta.per_app</c>. Both are optional and either may be
/// absent on any given call.
/// </para>
/// </remarks>
public static class SteamPerformanceDeltaReader
{
    /// <summary>The Steam client's own pseudo-game id, Valve's vocabulary for the global profile.</summary>
    /// <remarks>
    /// Every store setter stamps <c>gameid</c> from the current or active profile game id, and a
    /// backend publishes 769 for both whenever no per-game profile is in force, so a global-profile
    /// write arrives carrying 769. Reading it as a real AppID would refuse every one of those
    /// writes as stale against a session that has no running application.
    /// </remarks>
    private const ulong SteamClientPseudoGameId = 769;

    /// <summary>Reads a forwarded update-settings payload.</summary>
    /// <param name="payload">The request payload, expected to carry a <c>delta</c> object.</param>
    /// <param name="delta">The decoded delta when this returns true.</param>
    /// <param name="error">Why the payload could not be read, when this returns false.</param>
    /// <returns>Whether the payload was a readable delta.</returns>
    public static bool TryRead(
        JsonElement payload,
        out SteamPerformanceDelta delta,
        out string? error)
    {
        delta = new SteamPerformanceDelta([], false, null, []);
        error = null;

        if (payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty("delta", out JsonElement message))
        {
            error = "The performance delta payload carried no delta object.";
            return false;
        }

        if (message.ValueKind is JsonValueKind.String)
        {
            // Named separately because it is one specific regression, not a malformed payload:
            // every SystemPerfStore setter calls UpdateSettings with serializeBase64String(), so a
            // string here means the injected gate stopped decoding it and EVERY performance control
            // has silently stopped working. Saying so beats "no delta object".
            error = "The performance delta arrived undecoded; the injected gate did not deserialize "
                + "the update-settings message.";
            return false;
        }

        if (message.ValueKind is not JsonValueKind.Object)
        {
            error = "The performance delta payload carried no delta object.";
            return false;
        }

        List<SteamPerformanceChange> recognized = [];
        List<string> unsupported = [];

        bool resetToDefault = ReadFlag(message, "reset_to_default") ?? false;
        uint? steamAppId = ReadAppId(message);

        if (message.TryGetProperty("settings_delta", out JsonElement settings)
            && settings.ValueKind is JsonValueKind.Object)
        {
            if (settings.TryGetProperty("global", out JsonElement global)
                && global.ValueKind is JsonValueKind.Object)
            {
                ReadFields(global, recognized, unsupported);
            }

            if (settings.TryGetProperty("per_app", out JsonElement perApp)
                && perApp.ValueKind is JsonValueKind.Object)
            {
                ReadFields(perApp, recognized, unsupported);
            }
        }

        delta = new SteamPerformanceDelta(recognized, resetToDefault, steamAppId, unsupported);
        return true;
    }

    private static void ReadFields(
        JsonElement settings,
        List<SteamPerformanceChange> recognized,
        List<string> unsupported)
    {
        foreach (JsonProperty property in settings.EnumerateObject())
        {
            // toObject() emits every field of the message, not only the ones the setter touched, so
            // a null or absent value is "not part of this delta" and must not be applied. Treating
            // them as changes would make one slider write every other control's current value back
            // on every drag.
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            SteamPerformanceSetting? kind = property.Name switch
            {
                // The `_external` names are the same settings, written by the same controls when
                // Steam classifies the panel's display as external. A delta carries one twin or the
                // other, never both, because the control reads and writes whichever side its own
                // display test selected.
                "fps_limit" or "fps_limit_external" => SteamPerformanceSetting.FrameLimit,
                "is_fps_limit_enabled" => SteamPerformanceSetting.FrameLimitEnabled,
                "perf_overlay_level" => SteamPerformanceSetting.OverlayLevel,
                "is_vrr_enabled" => SteamPerformanceSetting.VariableRefreshRate,
                "display_refresh_manual_hz" or "display_external_refresh_manual_hz" =>
                    SteamPerformanceSetting.RefreshRateHz,
                "is_game_perf_profile_enabled" => SteamPerformanceSetting.PerApplicationProfileEnabled,
                "is_advanced_settings_enabled" => SteamPerformanceSetting.AdvancedSettingsEnabled,
                _ => null,
            };

            if (kind is not { } setting)
            {
                unsupported.Add(property.Name);
                continue;
            }

            if (TryReadInteger(property.Value, out int value))
            {
                recognized.Add(new SteamPerformanceChange(setting, value));
            }
            else
            {
                unsupported.Add(property.Name);
            }
        }
    }

    private static bool TryReadInteger(JsonElement value, out int result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = 1;
                return true;
            case JsonValueKind.False:
                result = 0;
                return true;
            case JsonValueKind.Number when value.TryGetInt32(out result):
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool? ReadFlag(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <remarks>
    /// <c>gameid</c> is a 64-bit id, and the client emits it as either a number or a string
    /// depending on magnitude. Anything that is not a Steam AppID — zero, the Steam client's own
    /// pseudo-app, or a value beyond 32 bits such as a full game id — targets the global profile
    /// rather than being guessed at.
    /// </remarks>
    private static uint? ReadAppId(JsonElement message)
    {
        if (!message.TryGetProperty("gameid", out JsonElement value))
        {
            return null;
        }

        ulong raw = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), out ulong parsed) => parsed,
            _ => 0,
        };

        return raw is > 0 and <= uint.MaxValue && raw != SteamClientPseudoGameId
            ? (uint)raw
            : null;
    }
}

/// <summary>What applies the writes Steam's own Performance tab makes.</summary>
public interface ISteamPerformanceBackend
{
    /// <summary>Applies one <c>UpdateSettings</c> call from Steam's own performance panel.</summary>
    /// <param name="delta">The decoded delta. A single call can carry several changes; apply them
    /// in the order they arrived, because a delta that turns the cap on and sets it in one message
    /// must not apply the two out of order.</param>
    /// <param name="correlationId">Correlates the command across the backend's log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>Whether every recognized change applied, and the first failure if not.</returns>
    Task<SteamUiCommandResult> ApplyAsync(
        SteamPerformanceDelta delta,
        string correlationId,
        CancellationToken cancellationToken);
}

/// <summary>Valve's own Performance tab, backed by the consumer's performance state.</summary>
/// <remarks>
/// <c>SystemPerfStore</c>'s constructor optional-chains through a <c>SteamClient.System.Perf</c>
/// that does not exist on Windows, so its state stays empty and every control renders null. The
/// gate supplies that namespace, writes the published state into the store, and decodes each
/// setter's protobuf delta through the message's own <c>deserializeBinary</c> before forwarding.
/// The module also mounts Valve's own rows that read that store: the profile header and its
/// per-game toggle, the reset button, the overlay-level selector, and the manual refresh-rate row
/// in Quick Settings. Which of them show anything is decided entirely by which fields the
/// published state carries.
/// </remarks>
public static class SteamPerformanceSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.performance";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["updateSettings"];

    /// <summary>The gate that supplies the performance backend behind <c>SteamClient.System.Perf</c>.</summary>
    /// <remarks>
    /// Its own resource key, separate from the row patches that mount into the panel: this
    /// supplies data, they render, and a failure in one must not disable the other.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.performance-namespace",
        gateName: "perf",
        fingerprint: "native-qam-perf-v1:store+absent-namespace+reachable-singleton",
        // The store is counted by the source tokens that make it the perf store, never by module
        // id; the singleton is reached through the one export exposing a Get() returning a
        // state-carrying store, because the state is written into a client that is already running.
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_performance_probe_")}}
              let singleton=false;
              try{
                const mod=req('74514');
                const holder=mod&&Object.values(mod).find(v=>v&&typeof v.Get==='function');
                const store=holder?holder.Get():null;
                singleton=!!(store&&'m_msgState' in store);
              }catch{}
              return JSON.stringify({
                perfStore:count(['SteamClient.System.Perf','RegisterForStateChanges','m_msgState']),
                perfNamespaceAbsent:(()=>{const p=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System.Perf;
                  // Absent, or present and ours — see the audio probe. An orphaned Perf namespace
                  // is the worse case: it leaves SystemPerfStore holding half-written state, which
                  // is what crashed the whole Performance tab.
                  return !p||p.__steamUiOwnedNamespace===true||p.__wsgmOwnedNamespace===true;})(),
                  // The __wsgm* spellings are the markers a build before the rename wrote; read as ours so
                  // that upgrade needs no Steam restart. Never written.
                storeSingletonReachable:singleton
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "perfStore")
            && SteamGatePatch.Flag(root, "perfNamespaceAbsent")
            && SteamGatePatch.Flag(root, "storeSingletonReachable"),
        verifyOk: "status.installed&&status.namespacePresent",
        removeOk: "!status.namespacePresent",
        subject: "Performance namespace");

    /// <summary>Valve's profile header and the per-game profile toggle, as one row kind.</summary>
    /// <remarks>
    /// Two separate exports of the perf-components module on the current client — re-probed
    /// 2026-09-02 after the header rendered with no way to enable a profile — mounted as two rows
    /// under this one kind because they are halves of one feature: the header names whose profile
    /// is on screen, the toggle is the only control that can change that.
    /// </remarks>
    public static SteamQuickAccessRowPatch ProfileHeaderRow { get; } = new(
        "steam-ui.valve-profile-header",
        "valveProfileHeader",
        "native-qam-valve-profile-header-v1:performance-actions+performance-root+valve-header",
        "steam_ui_valve_header_probe_");

    /// <summary>Valve's reset-to-default button, rendered last because it undoes everything above it.</summary>
    public static SteamQuickAccessRowPatch ResetRow { get; } = new(
        "steam-ui.valve-reset",
        "valveReset",
        "native-qam-valve-reset-v1:performance-actions+performance-root+valve-reset",
        "steam_ui_valve_reset_probe_");

    /// <summary>Valve's own performance-overlay selector.</summary>
    public static SteamQuickAccessRowPatch OverlayLevelRow { get; } = new(
        "steam-ui.valve-overlay-level",
        "valveOverlayLevel",
        "native-qam-valve-overlay-level-v1:performance-actions+performance-root+valve-selector",
        "steam_ui_valve_overlay_probe_");

    /// <summary>Valve's manual refresh-rate row, mounted into Quick Settings.</summary>
    /// <remarks>It reads <c>limits.display_refresh_manual_hz_*</c> from the published state, so
    /// it appears exactly when the backend supplies those fields.</remarks>
    public static SteamQuickAccessRowPatch RefreshRateRow { get; } = new(
        "steam-ui.valve-refresh-rate",
        "valveRefreshRate",
        "native-qam-valve-refresh-rate-v1:performance-actions+performance-root+valve-refresh",
        "steam_ui_valve_refresh_probe_");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPerformanceState state) =>
        JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamPerformanceState);

    /// <summary>Declares the surface as one module: the gate, Valve's rows, the state and the answer.</summary>
    /// <param name="enabled">Whether the state may be published right now.</param>
    /// <param name="read">The current state, or null to publish nothing this round.</param>
    /// <param name="backend">What applies the panel's writes.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamPerformanceState?>> read,
        ISteamPerformanceBackend backend,
        string id = "performance")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch, ProfileHeaderRow, ResetRow, OverlayLevelRow, RefreshRateRow],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPerformanceState),
            ],
            commands:
            [
                new(PatchId, "updateSettings", (request, cancellationToken) =>
                {
                    if (!SteamPerformanceDeltaReader.TryRead(
                        request.Payload,
                        out SteamPerformanceDelta delta,
                        out string? readError))
                    {
                        SteamUiLog.Warn($"Native QAM performance delta refused: {readError}");
                        return SteamSurfaceModule.Invalid(
                            readError ?? "The performance delta payload is invalid.");
                    }

                    return backend.ApplyAsync(delta, request.ToCorrelationId(), cancellationToken);
                }),
            ]);
    }
}
