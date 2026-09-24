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
///         The export in the module carrying <c>router-backstack</c> remains as the fallback, for a client
///         whose route list holds something other than plain Route elements. Neither it nor its module is
///         a condition of installing: a gate that refuses a client because one lookup drifted takes every
///         page down with it, which is exactly what happened.
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
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "steam-ui.router-root",
        "pages",
        "steam-pages-v2:unique-router-module+claimable-memo+route-switch",
        $$"""
          {{SteamUiProbeJs.Preamble("steam_ui_pages_probe_")}}
            const router=req.findUnique(['Settings.Root()','TopLevelTransition']);
            if(!router)return JSON.stringify({routerModule:0,backstackModule:0,steamRoute:0});
            // Steam's own back-stack Route, the one that gives a page native back navigation.
            // Reported, not required: the gate builds with the Route it borrows from the route list
            // Steam is rendering, and only falls back to this export when that list cannot be read
            // from. Counted by value rather than by export name, the way the gate's resolver counts
            // it, so a module re-exporting the Route under a second name is one match, not ambiguity.
            const backstack=req.findUnique(['router-backstack']);
            const markers={{SteamUiProbeJs.BackstackRouteMarkers}};
            const routes=new Set();
            if(backstack){
              const backstackExports=req(backstack[0]);
              for(const name of Object.keys(backstackExports)){
                try{
                  const value=backstackExports[name];
                  if(typeof value==='function'&&markers.every(marker=>String(value).includes(marker)))
                    routes.add(value);
                }catch{
                  // An export whose getter throws is not the Route.
                }
              }
            }
            // The router memo is NOT an export: it is built locally inside that module, so the
            // handle comes from SharedJSContext's own React root, which is the tree every Steam
            // window renders from. Read-only walk, bounded, matching on component source. It is
            // breadth-first over an explicit queue, like the Home carousel probe, because recursing
            // down a long sibling chain can exhaust the stack before the bound is reached.
            // A claimed member reads as the wrapper, not as Steam's function, so the source test has
            // to see through our own claim exactly as the gate's walk does. Without this the probe
            // stops finding the router the moment the gate holds it, reports routerFound:0, and the
            // manager retracts a patch that had just applied and verified. Both marker spellings,
            // for the same reason ownership.ts reads both.
            const unwrap=(value)=>{
              if(!value)return value;
              if(value.__steamUiPageHostClaimed!==true&&value.__wsgmPageHostClaimed!==true)return value;
              const stored=value.__steamUiPageHostOriginal??value.__wsgmPageHostOriginal;
              return stored&&stored.kind==='steam-ui-property-snapshot-v1'?stored.value:stored;
            };
            const host=document.getElementById('root');
            const key=host?Object.keys(host).find(n=>n.startsWith('__reactContainer$')):null;
            let memo=null,visited=0;
            const queue=key?[host[key]]:[];
            for(let head=0;head<queue.length&&!memo&&visited<=60000;head++){
              const node=queue[head];
              if(!node)continue;
              visited++;
              const resolved=unwrap(node.type);
              if(typeof resolved==='function'&&String(resolved).includes('Settings.Root()')
                &&node.elementType&&typeof node.elementType==='object'
                &&node.elementType.type===node.type){memo=node.elementType;break;}
              queue.push(node.child,node.sibling);
            }
            const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
            return JSON.stringify({
              routerModule:1,
              backstackModule:backstack?1:0,
              steamRoute:routes.size,
              routerFound:memo?1:0,
              claimable:{{SteamUiProbeJs.Replaceable("descriptor")}},
              claimed:!!memo&&memo.type.__steamUiPageHostClaimed===true,
              // The switch itself, matched the way the gate matches the list it renders.
              routeSwitch:count(['computedMatch','TopLevelTransition']),
              react:count({{SteamUiProbeJs.ReactTokens}})
            });
          {{SteamUiProbeJs.Close}}
          """,
        // Only what the gate cannot work without: the router it claims, the switch it wraps, React,
        // and a memo whose type it can put back. The back-stack module and its Route export are
        // reported for diagnostics and deliberately absent here. Requiring them is what declared an
        // otherwise healthy client incompatible on 2026-09-24 and took every custom page with it.
        root =>
            SteamUiPatchEvaluation.IsOne(root, "routerModule")
            && SteamUiPatchEvaluation.IsOne(root, "routerFound")
            && SteamUiPatchEvaluation.IsOne(root, "routeSwitch")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            // Claimable, or already ours. A patch that holds the member has answered the question
            // the flag exists to ask, and demanding both at once would retract every applied gate.
            && (SteamUiPatchEvaluation.Flag(root, "claimable")
                || SteamUiPatchEvaluation.Flag(root, "claimed")),
        // The Route is borrowed from Steam's first render through the claimed switch, which has not
        // necessarily happened by the time verification runs, so holding the router is what verify
        // proves. Whether a page was built, and with which Route, is reported in the gate's status.
        "status.installed&&status.resolved&&status.claimed",
        "!status.claimed",
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
        return SteamSurfaceModule.Declare(
            id,
            PatchId,
            enabled,
            read,
            SteamSurfaceJsonContext.Default.SteamPageState,
            [Patch],
            []);
    }
}
