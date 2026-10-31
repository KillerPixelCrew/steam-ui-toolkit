using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One custom page registered with Steam's router.</summary>
/// <param name="Id">Stable identity, unique within one publication.</param>
/// <param name="Path">
///     The route, which must be absolute and must not be <c>/</c>. Steam's own matcher sees it, so
///     parameters work the way they do for Valve's routes: <c>/wsgm/artwork/:appid</c> matches
///     <c>/wsgm/artwork/440</c>.
/// </param>
/// <param name="Title">The page's heading and its accessible name.</param>
/// <param name="Override">
///     Whether this page replaces a Steam route of the same path rather than adding a new one. Steam's
///     switch takes the first matching child, so an override is inserted ahead of Valve's routes and an
///     addition behind them. Defaults to adding, because silently shadowing a client route is not
///     something a caller should get by accident.
/// </param>
/// <param name="Template">A toolkit-owned renderer name. The default renders a heading-only page.</param>
public sealed record SteamPage(
    string Id,
    string Path,
    string Title,
    bool Override = false,
    string Template = "default");

/// <summary>The pages that should currently be registered.</summary>
/// <param name="Pages">The pages, in the order they are inserted.</param>
/// <param name="Revision">Monotonic host observation revision, shared by publications and readback.</param>
public sealed record SteamPageState(IReadOnlyList<SteamPage> Pages, long Revision = 0);

/// <summary>
///     Custom pages inside Steam's Game Mode UI.
/// </summary>
/// <remarks>
///     Steam's router renders its routes as the children of its own switch component, so registering a
///     page is a list operation on props rather than DOM work — unlike the navigation panel, whose
///     entries only exist once its root renders. The gate claims the router memo's <c>type</c>, the same
///     handle <see cref="SteamNavigationPanelSurface" /> claims, and finds the route list by content: the
///     array holding a route for a path every client has.
///     <para>
///         The <c>Route</c> the gate builds pages with is Steam's own, never react-router's. That is what
///         gives a custom page native back-navigation, because Steam's Route registers the match with the
///         back stack; react-router's renders the same content and silently loses it, and nobody would
///         notice until they pressed B. It is taken from the route list itself: every element in that list
///         is the component Steam is rendering that route with, so the gate borrows the Route rather than
///         describing it. A description can stop matching, and on 2026-09-24 one did.
///     </para>
///     <para>
///         The probe still reports the module carrying <c>router-backstack</c>, for diagnostics only: the
///         gate does not use it, and it is not a condition of installing. A gate that refuses a client
///         because one lookup drifted takes every page down with it, which is exactly what happened.
///         Removal hands back every router and switch drawing the claims, including ones mounted after
///         install.
///     </para>
///     <para>
///         Mapped against the live client on 2026-09-10: the switch carries 31 route children, its selection
///         rule is first-match, the router module is unique on <c>Settings.Root()</c> plus
///         <c>TopLevelTransition</c>, and the back-stack module is unique on <c>router-backstack</c>.
///     </para>
/// </remarks>
public static class SteamPageSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.pages";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    /// <remarks>Empty: pages are declared by publication and carry no user-initiated write.</remarks>
    public static IReadOnlyList<string> Commands { get; } = [];

    /// <summary>The gate that claims Steam's router and inserts the registered pages.</summary>
    /// <remarks>
    ///     The probe is the gate's own find. It requires only what the gate cannot work without: the
    ///     unique router module, the route switch, React, and a router memo whose <c>type</c> it can put
    ///     back or already holds. A router module with no router or switch mounted is a window still
    ///     loading or Big Picture closed, which is not ready. The back-stack module and its Route are
    ///     reported among the facts and deliberately not required: requiring them is what declared an
    ///     otherwise healthy client incompatible on 2026-09-24 and took every custom page with it. The
    ///     Route is borrowed from Steam's first render through the claimed switch, which need not have
    ///     happened when verification runs, so holding the claims is what verify proves; whether a page
    ///     was built, and with which Route, is reported in the gate's status.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "pages",
        "steam-pages-v2:unique-router-module+claimable-memo+route-switch",
        "Custom page gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPageState state)
    {
        return JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPageState);
    }

    /// <summary>Declares the surface as one module: the gate and the registered pages.</summary>
    /// <param name="enabled">Whether the pages may be published right now.</param>
    /// <param name="read">The pages that should be registered, or null when there is nothing to say.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamPageState?>> read,
        string id = "pages")
    {
        return SteamUiModuleBuilder.Module(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamPageState,
            [Patch],
            []);
    }
}
