using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>One custom page registered with Steam's router.</summary>
/// <param name="Id">Stable identity, unique within one publication.</param>
/// <param name="Path">
/// The route, which must be absolute and must not be <c>/</c>. Steam's own matcher sees it, so
/// parameters work the way they do for Valve's routes: <c>/wsgm/artwork/:appid</c> matches
/// <c>/wsgm/artwork/440</c>.
/// </param>
/// <param name="Title">The page's heading and its accessible name.</param>
/// <param name="Override">
/// Whether this page replaces a Steam route of the same path rather than adding a new one. Steam's
/// switch takes the first matching child, so an override is inserted ahead of Valve's routes and an
/// addition behind them. Defaults to adding, because silently shadowing a client route is not
/// something a caller should get by accident.
/// </param>
public sealed record SteamPage(string Id, string Path, string Title, bool Override = false);

/// <summary>The pages that should currently be registered.</summary>
/// <param name="Pages">The pages, in the order they are inserted.</param>
/// <param name="Revision">Monotonic host observation revision, shared by publications and readback.</param>
public sealed record SteamPageState(IReadOnlyList<SteamPage> Pages, long Revision = 0);

/// <summary>
/// Custom pages inside Steam's Game Mode UI.
/// </summary>
/// <remarks>
/// Steam's router renders its routes as the children of its own switch component, so registering a
/// page is a list operation on props rather than DOM work — unlike the navigation panel, whose
/// entries only exist once its root renders. The gate claims the router memo's <c>type</c>, the same
/// handle <see cref="SteamNavigationPanelSurface"/> claims, and finds the route list by content: the
/// array holding a route for a path every client has.
/// <para>
/// The <c>Route</c> the gate builds pages with is Steam's own, resolved from the module carrying
/// <c>router-backstack</c>, never react-router's. That is what gives a custom page native
/// back-navigation, because Steam's Route registers the match with the back stack. React-router's
/// renders the same content and silently loses it, which is the failure this would otherwise ship
/// with and nobody would notice until they pressed B.
/// </para>
/// <para>
/// Mapped against the live client on 2026-09-10: the switch carries 31 route children, its selection
/// rule is first-match, the router module is unique on <c>Settings.Root()</c> plus
/// <c>TopLevelTransition</c>, and the back-stack module is unique on <c>router-backstack</c>.
/// </para>
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
        id: PatchId,
        resourceKey: "steam-ui.router-root",
        gateName: "pages",
        fingerprint: "steam-pages-v1:unique-router-module+backstack-route",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_pages_probe_")}}
              const router=req.findUnique(['Settings.Root()','TopLevelTransition']);
              const backstack=req.findUnique(['router-backstack']);
              if(!router||!backstack)return JSON.stringify({
                routerModule:router?1:0,backstackModule:backstack?1:0});
              // Steam's own back-stack Route, the one that gives a page native back navigation.
              const backstackExports=req(backstack[0]);
              const routes=Object.keys(backstackExports).filter(name=>
                typeof backstackExports[name]==='function'
                &&/routePath:.\.match\?\.path./.test(String(backstackExports[name])));
              // The router memo is NOT an export: it is built locally inside that module, so the
              // handle comes from SharedJSContext's own React root, which is the tree every Steam
              // window renders from. Read-only walk, bounded, matching on component source.
              const host=document.getElementById('root');
              const key=host?Object.keys(host).find(n=>n.startsWith('__reactContainer$')):null;
              let memo=null,visited=0;
              const walk=node=>{
                if(!node||memo||visited>60000)return;
                visited++;
                if(typeof node.type==='function'&&String(node.type).includes('Settings.Root()')
                  &&node.elementType&&typeof node.elementType==='object'
                  &&node.elementType.type===node.type){memo=node.elementType;return;}
                walk(node.child);walk(node.sibling);
              };
              if(key)walk(host[key]);
              const descriptor=memo?Object.getOwnPropertyDescriptor(memo,'type'):null;
              return JSON.stringify({
                routerModule:1,
                backstackModule:1,
                steamRoute:routes.length,
                routerFound:memo?1:0,
                claimable:!!descriptor&&descriptor.writable===true&&descriptor.configurable===true,
                claimed:!!memo&&memo.type.__steamUiPageHostClaimed===true,
                // The switch itself, matched the way the gate matches the list it renders.
                routeSwitch:count(['computedMatch','TopLevelTransition']),
                react:count(['react.transitional.element','useState','cloneElement','createElement'])
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "routerModule")
            && SteamUiPatchEvaluation.IsOne(root, "backstackModule")
            && SteamUiPatchEvaluation.IsOne(root, "steamRoute")
            && SteamUiPatchEvaluation.IsOne(root, "routerFound")
            && SteamUiPatchEvaluation.IsOne(root, "routeSwitch")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamGatePatch.Flag(root, "claimable"),
        verifyOk: "status.installed&&status.resolved&&status.routeResolved&&status.claimed",
        removeOk: "!status.claimed",
        subject: "Custom page gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamPageState state) =>
        JsonSerializer.SerializeToElement(state, SteamSurfaceJsonContext.Default.SteamPageState);

    /// <summary>Declares the surface as one module: the gate and the registered pages.</summary>
    /// <param name="enabled">Whether the pages may be published right now.</param>
    /// <param name="read">The pages that should be registered, or null when there is nothing to say.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamPageState?>> read,
        string id = "pages")
        => new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamPageState),
            ],
            commands: []);
}
