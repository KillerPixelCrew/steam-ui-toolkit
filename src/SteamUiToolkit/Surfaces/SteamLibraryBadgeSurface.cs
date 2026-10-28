using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One Steam library the badge can name, and the games it holds.</summary>
/// <param name="Name">The badge text for a game in this library, as the host names it.</param>
/// <param name="Connected">
///     Whether the library is attached right now. Steam's own installed flag decides the badge's
///     colour where it can; this stands in only for a game whose overview cannot say.
/// </param>
/// <param name="AppIds">The Steam app ids installed in this library.</param>
public sealed record SteamLibraryBadgeLibrary(string Name, bool Connected, IReadOnlyList<long> AppIds);

/// <summary>Authoritative availability of one host-managed non-Steam shortcut.</summary>
/// <param name="AppId">The confirmed unsigned Steam shortcut id.</param>
/// <param name="Available">Whether its backing content and launcher are currently usable.</param>
/// <param name="Location">The logical library or storage name.</param>
/// <param name="Reason">A readable explanation when the content cannot be launched.</param>
public sealed record SteamShortcutAvailability(uint AppId, bool Available, string Location, string Reason);

/// <summary>Everything the badge needs to name a game's library.</summary>
/// <param name="Libraries">
///     Every library the badge names, each by the name the host gives it: a removable one attached
///     or not, so a game on an absent card keeps naming the card. A game in none of them gets no
///     badge; the toolkit has no name of its own for a library.
/// </param>
/// <param name="Revision">Monotonic host observation revision.</param>
/// <param name="Shortcuts">
///     Host-managed shortcut availability, independent of Steam's installed flag. An unavailable entry
///     greys its tile, names its storage on its page, turns its primary action into Recheck and leaves
///     installed-only filters; an available one, and every app it does not list, keeps Steam's behavior.
/// </param>
/// <param name="ShowBadges">
///     Whether the decorative library badges and library stats are drawn. Managed availability is drawn
///     either way, so a host keeps publishing while it manages shortcuts with badges turned off.
/// </param>
public sealed record SteamLibraryBadgeState(
    IReadOnlyList<SteamLibraryBadgeLibrary> Libraries,
    long Revision = 0,
    IReadOnlyList<SteamShortcutAvailability>? Shortcuts = null,
    bool ShowBadges = true);

/// <summary>What the library badge tells its host.</summary>
public interface ISteamLibraryBadgeBackend
{
    /// <summary>Reports Steam's Big Picture Home layout: Big Art Mode on or off.</summary>
    /// <remarks>
    ///     Sent when the gate first resolves the setting and again whenever a tile render sees it
    ///     change, so a host learns of a toggle without polling.
    /// </remarks>
    /// <param name="bigArt">Whether <c>library_home_big_art</c> is on.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>The outcome. A refusal is reported rather than swallowed.</returns>
    Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken);

    /// <summary>Requests a fresh check of one managed shortcut's content.</summary>
    /// <param name="appId">The confirmed unsigned Steam shortcut id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the host accepted the check.</returns>
    Task<SteamUiCommandResult> RecheckAsync(uint appId, CancellationToken cancellationToken);
}

/// <summary>
///     A library badge beside Valve's Steam Input badge on every library tile: the name of the library
///     that holds the game, green when the game is installed and grey when it is not.
/// </summary>
/// <remarks>
///     The tile and the Steam Input badge are exported from one module, but the tile draws the badge
///     through its module-local name, so the claim is on the tile memo's <c>type</c> and the anchor is
///     found in what the tile renders by element type — identity with the export, never a generated
///     class name. Every caller draws the tile through that export, so one claim reaches Home's
///     carousel and the library grid alike, and the badge inherits the icon row's own visibility,
///     which Valve shows on the focused tile only.
///     <para>
///         Big Art Mode is Steam's own <c>library_home_big_art</c> client setting, read from the settings
///         store the Home component reads it from and reported to the host through <c>homeLayout</c>.
///         The badge itself is tile-relative and moves with the carousel in either layout.
///     </para>
///     <para>
///         Mapped against the September 2026 client beta on 2026-09-11: <c>appportrait_</c> occurs in
///         exactly one of the 2622 loaded modules, that module has exactly one <c>React.memo</c> export
///         and exactly one function export drawing the controller-support icon, and the memo's
///         <c>type</c> is a writable, configurable own property, which is what makes the claim restorable.
///     </para>
/// </remarks>
public static class SteamLibraryBadgeSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.library-badge";

    /// <summary>The patch id of the library stat on a game's own page.</summary>
    /// <remarks>It sends no commands and reads the badge's publication under <see cref="PatchId" />.</remarks>
    public const string DetailsPatchId = "steam-ui.library-details";

    /// <summary>The patch id of managed-shortcut availability on the play bar and in Steam's app filter.</summary>
    /// <remarks>It sends <c>recheck</c> and reads the badge's publication under <see cref="PatchId" />.</remarks>
    public const string AvailabilityPatchId = "steam-ui.library-availability";

    /// <summary>Every patch the module declares, for a host that switches them together.</summary>
    public static IReadOnlyList<string> PatchIds { get; } = [PatchId, DetailsPatchId, AvailabilityPatchId];

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["homeLayout", "recheck"];

    /// <summary>The gate that adds the library as a stat beside Last Played and Play Time on a game's page.</summary>
    /// <remarks>
    ///     The stats row is built inside mobx observer classes, which pin a non-writable render on each
    ///     instance, so the gate adds its stat where Steam creates the row: through the toolkit's shared
    ///     JSX-runtime claim, on the element whose class is the play bar class map's
    ///     <c>GameStatsSection</c>. It shares that claim's resource with every other element transform.
    ///     The localizer is reported but not required; without it the label is the English word.
    ///     <para>
    ///         Read from the Stable client (UI build of 2026-09-06) and the September 2026 beta on
    ///         2026-09-11: the app-details module is the same in both, the class map carrying
    ///         <c>GameStatsSection</c>, <c>PlayBarDetailLabel</c> and <c>LastPlayedInfo</c> occurs once, and
    ///         the row is created as <c>jsxs("div", { className: GameStatsSection, children })</c>.
    ///     </para>
    /// </remarks>
    public static ISteamUiPatch DetailsPatch { get; } = new SteamGatePatch(
        DetailsPatchId,
        "libraryDetails",
        "steam-library-details-v1:unique-jsx-runtime+play-bar-class-map",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_library_details_probe_")}}
            return JSON.stringify({
              react:count({{SteamUiProbeJs.ReactTokens}}),
              runtime:count({{SteamUiProbeJs.JsxRuntimeTokens}}),
              classMap:count(['GameStatsSection:"','PlayBarDetailLabel:"','LastPlayedInfo:"']),
              localization:count({{SteamUiProbeJs.LocalizationTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "runtime")
            && SteamUiPatchEvaluation.IsOne(root, "classMap"),
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimed",
        "Library details gate");

    /// <summary>
    ///     The gate that applies managed-shortcut availability where Steam acts on "installed": the play
    ///     bar's primary action, the library's Installed tab and the play-state options of the library
    ///     filter.
    /// </summary>
    /// <remarks>
    ///     The client has no shortcut-specific installed setter, so nothing in Valve's app store is
    ///     written, and every change below applies to managed shortcuts alone; ordinary shortcuts and
    ///     Steam games keep Steam's behavior, and nothing changes while the host manages none.
    ///     <list type="bullet">
    ///         <item>
    ///             On the play bar of an unavailable managed shortcut, the container whose class is the
    ///             play button class map's <c>PlayButtonContainer</c> keeps Valve's Focusable, autoFocus
    ///             and ref, takes Valve's disabled look, and its primary button reads Recheck and sends
    ///             <c>recheck</c> through the click or mouse-up slot Valve launched from.
    ///         </item>
    ///         <item>
    ///             An element given Steam's <c>local-install</c> collection, the Installed tab, gets a
    ///             read-only view of it that adds available managed shortcuts Steam reports installed,
    ///             which the gamepad window otherwise leaves out with every shortcut, and drops
    ///             unavailable ones, in the games collection's order.
    ///         </item>
    ///         <item>
    ///             The library's filter class, the subclass of Steam's app filter that the desktop,
    ///             gamepad and VR library filters are made of, has <c>MatchesImpl</c> and
    ///             <c>MatchesScoredImpl</c> claimed on its prototype. They evaluate an unavailable
    ///             managed shortcut through a read-only view whose installed fields read false, so
    ///             Installed and Ready to Play leave it out while All Games, Non-Steam and the other
    ///             options keep it. Users' dynamic collections hold the base class and keep their
    ///             membership.
    ///         </item>
    ///     </list>
    ///     Each swap is a live component following the publication, so the play bar and the grids,
    ///     whose render reruns the library filter through mobx-react-lite's <c>useObserver</c>, redraw
    ///     when availability moves, and hand Valve's elements back on removal.
    ///     <para>
    ///         Read offline from the installed client's <c>sp.js</c> (SHA-256
    ///         <c>692083495994D2B2CF686EFFD89C11FA987AEFA45787CBFA212627C37EDA9C7A</c>, 2026-10-20) without
    ///         executing it or attaching to Steam: the class map carrying <c>PlayButtonContainer</c>,
    ///         <c>ShutdownAppButton</c> and <c>ShowingStreaming</c> occurs once; the module carrying
    ///         <c>MatchesImpl</c> and <c>MatchesScoredImpl</c> occurs once and exports the base filter
    ///         class and one subclass, which the library store builds its filters from; and the
    ///         collection store module, carrying <c>Computing localGamesCollection</c>, exports one store.
    ///     </para>
    /// </remarks>
    public static ISteamUiPatch AvailabilityPatch { get; } = new SteamGatePatch(
        AvailabilityPatchId,
        "libraryAvailability",
        "steam-library-availability-v1:play-button-class-map+library-filter-prototype+collection-store+jsx-runtime",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_library_availability_probe_")}}
            const members=['MatchesImpl','MatchesScoredImpl'];
            const owns=(host,member)=>!!host&&Object.prototype.hasOwnProperty.call(host,member);
            const filterModule=count(['MatchesScoredImpl','MatchesImpl']);
            let filter=null;
            if(filterModule===1){
              try{
                // The library's filter class owns both methods and extends a class owning both too,
                // chosen by what it is, never by a minified name.
                filter=req.exported(['MatchesScoredImpl','MatchesImpl'],value=>{
                  const prototype=value&&value.prototype;
                  if(typeof value!=='function'||!prototype)return false;
                  const parent=Object.getPrototypeOf(prototype);
                  return members.every(member=>owns(prototype,member)&&owns(parent,member));
                });
              }catch{filter=null;}
            }
            const storeModule=count(['Computing localGamesCollection','local-install']);
            let store=null;
            if(storeModule===1){
              try{
                store=req.exported(['Computing localGamesCollection','local-install'],value=>
                  !!value&&typeof value==='object'&&typeof value.GetCollection==='function'
                    &&typeof value.BIsVisible==='function');
              }catch{store=null;}
            }
            const prototype=filter?filter.prototype:null;
            const descriptors=members.map(member=>prototype?Object.getOwnPropertyDescriptor(prototype,member):null);
            return JSON.stringify({
              react:count({{SteamUiProbeJs.ReactTokens}}),
              runtime:count({{SteamUiProbeJs.JsxRuntimeTokens}}),
              classMap:count(['PlayButtonContainer:"','ShutdownAppButton:"','ShowingStreaming:"']),
              filterModule,
              filterClass:filter?1:0,
              storeModule,
              store:store?1:0,
              // Writable and configurable, or the claim could neither replace nor restore them.
              claimable:descriptors.every(descriptor=>{{SteamUiProbeJs.Replaceable("descriptor")}}),
              // Already ours is compatible, for the reason every gate's own claim is.
              claimed:!!prototype&&members.every(member=>{{SteamUiProbeJs.Claimed("prototype[member]", "LibraryAvailability")}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.IsOne(root, "runtime")
            && SteamUiPatchEvaluation.IsOne(root, "classMap")
            && SteamUiPatchEvaluation.IsOne(root, "filterModule")
            && SteamUiPatchEvaluation.IsOne(root, "filterClass")
            && SteamUiPatchEvaluation.IsOne(root, "storeModule")
            && SteamUiPatchEvaluation.IsOne(root, "store")
            && SteamUiPatchEvaluation.Flag(root, "claimable"),
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimed",
        "Library availability gate");

    /// <summary>The gate that claims the tile and draws the badge from the published libraries.</summary>
    /// <remarks>
    ///     The probe requires each structural fact the gate resolves on, separately, so an incompatible
    ///     client says which one moved. The settings store and the tile's class map are reported but
    ///     not required: without the first the badge still draws and Big Art Mode reads as unknown,
    ///     without the second it draws on every tile rather than fading with the focused one. It
    ///     accepts a tile this gate has already claimed, for the reason every gate does.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "libraryBadge",
        "steam-library-badge-v1:unique-tile-module+single-memo-export+single-badge-export",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_library_badge_probe_")}}
            const tile=req.findUnique(['ControllerSupportIcon','appportrait_']);
            if(!tile)return JSON.stringify({tileModule:0});
            const exports=req(tile[0]);
            const memoType=Symbol.for('react.memo');
            // The tile is the module's one memo; the badge is the one function drawing the
            // controller-support icon. Chosen by what they are, never by a minified name.
            const tiles=Object.keys(exports).filter(name=>{
              const value=exports[name];
              return value&&typeof value==='object'&&value.$$typeof===memoType;
            });
            const badges=Object.keys(exports).filter(name=>{
              const value=exports[name];
              return typeof value==='function'&&String(value).includes('ControllerSupportIcon');
            });
            const memo=tiles.length===1?exports[tiles[0]]:null;
            const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
            return JSON.stringify({
              tileModule:1,
              tileExports:tiles.length,
              badgeExports:badges.length,
              // Writable and configurable, or the claim could neither replace nor restore it.
              claimable:{{SteamUiProbeJs.Replaceable("descriptor")}},
              // Already ours is compatible; see the remarks on this patch.
              claimed:!!memo&&memo.type.__steamUiLibraryBadgeClaimed===true,
              settingsModule:count({{SteamUiProbeJs.SettingsStoreTokens}}),
              // The tile stylesheet's class map, read by Valve's names; wanted for focus-only
              // visibility, not required for the badge to draw.
              classMap:count({{SteamUiProbeJs.LibraryClassTokens}}),
              react:count({{SteamUiProbeJs.ReactTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "tileModule")
            && SteamUiPatchEvaluation.IsOne(root, "tileExports")
            && SteamUiPatchEvaluation.IsOne(root, "badgeExports")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.Flag(root, "claimable"),
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimed",
        "Library badge gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamLibraryBadgeState state)
    {
        return JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamLibraryBadgeState);
    }

    /// <summary>Reads the exact <c>homeLayout</c> payload: <c>{ bigArt: bool }</c> and nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="bigArt">The reported mode.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadHomeLayout(JsonElement payload, out bool bigArt)
    {
        bigArt = false;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadBoolean(payload, "bigArt", out bigArt);
    }

    private static bool TryReadRecheck(JsonElement payload, out uint appId)
    {
        appId = 0;
        return SteamUiPayload.HasExactly(payload, 1)
               && payload.TryGetProperty("appId", out var value) && value.ValueKind == JsonValueKind.Number
               && value.TryGetUInt32(out appId)
               && appId > 0;
    }

    /// <summary>
    ///     Declares the surface as one module: the badge, details and availability gates, the libraries,
    ///     the layout report and the managed-shortcut recheck.
    /// </summary>
    /// <param name="enabled">
    ///     Whether the libraries may be published right now. A host that manages shortcuts keeps this
    ///     true while any exist, even with badges off, and says so through
    ///     <see cref="SteamLibraryBadgeState.ShowBadges" />.
    /// </param>
    /// <param name="read">The libraries to name, or null when there is nothing to say yet.</param>
    /// <param name="backend">What hears the Home layout report.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamLibraryBadgeState?>> read,
        ISteamLibraryBadgeBackend backend,
        string id = "library-badge")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamLibraryBadgeState,
            [Patch, DetailsPatch, AvailabilityPatch],
            [
                SteamUiModuleBuilder.Command<bool>(
                    PatchId,
                    "homeLayout",
                    TryReadHomeLayout,
                    backend.HomeLayoutAsync,
                    "The home layout payload is invalid."),
                SteamUiModuleBuilder.Command<uint>(PatchId, "recheck", TryReadRecheck,
                    backend.RecheckAsync, "The managed shortcut id is invalid.")
            ]);
    }
}
