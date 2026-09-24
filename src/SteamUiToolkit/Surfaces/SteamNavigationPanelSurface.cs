using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>Where an added entry sits relative to Steam's own.</summary>
/// <param name="Id">Stable identity, returned on activation and unique within one publication.</param>
/// <param name="Label">The entry's accessible name and visible text.</param>
/// <param name="Icon">A toolkit glyph name, or null for a text-only entry.</param>
/// <param name="Before">
///     The Steam entry this one is inserted before, named by its route (<c>/library</c>) or by Valve's
///     own descriptor key (<c>power</c>). Null leaves placement to <see cref="After" /> or
///     <see cref="Position" />.
/// </param>
/// <param name="After">The Steam entry this one is inserted after, named the same way.</param>
/// <param name="Position">
///     <c>start</c> or <c>end</c> when the entry is not anchored to one of Steam's. An entry with no
///     placement at all, and one whose anchor is not in the panel, goes to the end rather than being
///     dropped.
/// </param>
/// <param name="Route">
///     The page the entry opens, such as a route registered through <see cref="SteamPageSurface" />.
///     An entry with a route is drawn by Valve's own route entry: it is active on that page and below
///     it, and selecting it navigates with Valve's own action, so the host is not asked. Without one,
///     selecting the entry sends <c>activate</c>, and an answer carrying a <c>route</c> is followed
///     after the menu closes. A route that is not absolute, is <c>/</c>, or is longer than 256
///     characters is not drawn.
/// </param>
/// <param name="Glyph">
///     The entry's icon as SVG path data on a 24x24 grid, filled with the row's own colour and with
///     holes cut even-odd, which is how Valve draws the menu's icons. Takes precedence over
///     <see cref="Icon" />. Only path commands and numbers are drawn.
/// </param>
/// <remarks>
///     An added entry is always drawn by one of Valve's own entry components, taken from the entries
///     the panel already renders. Where the one it needs is not there, the entry is not drawn and the
///     gate reports it as <c>unrendered</c> rather than showing an imitation.
/// </remarks>
public sealed record SteamNavigationItem(
    string Id,
    string Label,
    string? Icon = null,
    string? Before = null,
    string? After = null,
    string? Position = null,
    string? Route = null,
    string? Glyph = null);

/// <summary>The additions and hidden entries the panel should show.</summary>
/// <param name="Items">Entries to add, in the order they should be placed.</param>
/// <param name="Hidden">
///     Steam entries to hide, named by route or descriptor key. Hiding is applied before insertion, so
///     an anchor and the entry it anchors to cannot disagree about what the user can see.
/// </param>
/// <param name="Revision">Monotonic host observation revision, shared by publications and readback.</param>
public sealed record SteamNavigationPanelState(
    IReadOnlyList<SteamNavigationItem> Items,
    IReadOnlyList<string> Hidden,
    long Revision = 0);

/// <summary>What answers an added navigation entry.</summary>
public interface ISteamNavigationPanelBackend
{
    /// <summary>Reports that the user activated an added entry.</summary>
    /// <param name="id">The <see cref="SteamNavigationItem.Id" /> that was activated.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>The outcome. A refusal is reported rather than swallowed.</returns>
    Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
///     Steam's left slideout navigation panel, as an extension surface.
/// </summary>
/// <remarks>
///     The panel is module-private: its root builds its own entry list and neither the root nor the
///     builder is exported, and the builder calls React hooks, so the list cannot even be read from
///     outside a render. The gate therefore claims the one public handle — the exported memo's
///     <c>type</c> — and reaches the root by rendering, which is the mechanism the native-row filter in
///     <c>components.ts</c> already uses.
///     <para>
///         Entries are addressed by route or by Valve's own descriptor key, never by index or by a
///         generated class name: routes and keys are stable across client builds and languages, whereas the
///         rendered labels are localized and the class names are content hashes.
///     </para>
///     <para>
///         Mapped against the live client on 2026-09-10. <c>#MainMenu_Title</c> occurs in exactly one of
///         the 2581 modules the client loads and <c>MainNavMenuContainer</c> in exactly one; the module
///         they both name has exactly one export whose memo renders the outer container, and that export's
///         <c>type</c> is a writable, configurable own property, which is what makes the claim restorable.
///     </para>
/// </remarks>
public static class SteamNavigationPanelSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.navigation-panel";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["activate"];

    /// <summary>The gate that claims the panel and applies the published entries.</summary>
    /// <remarks>
    ///     The probe requires each structural fact the gate resolves on, separately, so an incompatible
    ///     client says which one moved rather than only that something did. It accepts a panel this
    ///     gate has already claimed: requiring the pre-patch shape alone would make a successful apply
    ///     fail its own next probe and tear the claim down on every poll.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.navigation-panel-root",
        "navigationPanel",
        "steam-navigation-panel-v1:unique-menu-module+single-memo-export",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_navigation_probe_")}}
            const menu=req.findUnique(['#MainMenu_Title','MainNavMenuContainer']);
            if(!menu)return JSON.stringify({menuModule:0});
            const exports=req(menu[0]);
            // Through the gate's own claim, or the export disappears the moment the gate holds it.
            {{SteamUiProbeJs.Unwrap("NavigationPanel")}}
            // The export is chosen by what its component draws, never by its minified name.
            const memos=Object.keys(exports).filter(name=>{
              const value=exports[name];
              if(!value||typeof value!=='object')return false;
              const drawn=unwrap(value.type);
              return typeof drawn==='function'&&String(drawn).includes('MainNavMenuContainer');
            });
            const memo=memos.length===1?exports[memos[0]]:null;
            const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
            return JSON.stringify({
              menuModule:1,
              // The panel root itself, matched the way the gate matches it while descending.
              panelRoot:count(['#MainMenu_Title','RunnningAppSeparator']),
              memoExports:memos.length,
              // Writable and configurable, or the claim could neither replace nor restore it.
              claimable:{{SteamUiProbeJs.Replaceable("descriptor")}},
              // Already ours is compatible; see the remarks on this patch.
              claimed:{{SteamUiProbeJs.Claimed("memo?.type", "NavigationPanel")}},
              react:count({{SteamUiProbeJs.ReactTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        root =>
            SteamUiPatchEvaluation.IsOne(root, "menuModule")
            && SteamUiPatchEvaluation.IsOne(root, "panelRoot")
            && SteamUiPatchEvaluation.IsOne(root, "memoExports")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamUiPatchEvaluation.ClaimableOrOurs(root),
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimed",
        "Navigation panel gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamNavigationPanelState state)
    {
        return JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamNavigationPanelState);
    }

    /// <summary>Declares the surface as one module: the gate, the entries, and the answer.</summary>
    /// <param name="enabled">Whether the entries may be published right now.</param>
    /// <param name="read">The entries the panel should show, or null when there is nothing to say.</param>
    /// <param name="backend">What answers an added entry's activation.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamNavigationPanelState?>> read,
        ISteamNavigationPanelBackend backend,
        string id = "navigation-panel")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamNavigationPanelState,
            [Patch],
            [
                SteamSurfaceModule.Command(
                    PatchId,
                    "activate",
                    static (JsonElement payload, out string entryId) =>
                        SteamUiPayload.TryReadBoundedString(payload, "id", 64, out entryId),
                    backend.ActivateAsync,
                    "The navigation activation payload is invalid.")
            ]);
    }
}
