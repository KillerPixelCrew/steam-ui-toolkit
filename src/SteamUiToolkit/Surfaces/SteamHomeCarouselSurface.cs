using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUiToolkit;

/// <summary>What Big Picture Home's carousel should draw from.</summary>
/// <param name="IncludeUninstalled">Whether owned games that are not installed are listed, greyed, after the installed ones.</param>
/// <param name="DisconnectedAppIds">
/// Games that live on a library that is not attached right now. They leave the carousel even while
/// Steam still reports them installed, which it does for a moment after a card is pulled.
/// </param>
/// <param name="Revision">Monotonic host observation revision.</param>
public sealed record SteamHomeCarouselState(
    bool IncludeUninstalled,
    IReadOnlyList<long> DisconnectedAppIds,
    long Revision = 0);

/// <summary>What the carousel reported it holds, once per change.</summary>
/// <param name="Items">Entries in the list, the running-game separator included.</param>
/// <param name="Purchases">Recent purchases not yet played.</param>
/// <param name="Installed">Installed games from the attached libraries.</param>
/// <param name="Uninstalled">Owned games listed although not installed.</param>
/// <param name="Excluded">Games the host marked as on a disconnected library.</param>
/// <param name="Tracking">Whether Steam's own observer hook re-renders the carousel when a collection changes.</param>
/// <param name="Fallback">Whether nothing qualified and Steam's own list was shown instead.</param>
public sealed record SteamHomeCarouselReport(
    int Items,
    int Purchases,
    int Installed,
    int Uninstalled,
    int Excluded,
    bool Tracking,
    bool Fallback);

/// <summary>Hears what the Home carousel reports.</summary>
public interface ISteamHomeCarouselBackend
{
    /// <summary>Receives what the carousel holds after it rebuilt its list.</summary>
    /// <param name="report">The counts.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>The outcome. A refusal is reported rather than swallowed.</returns>
    Task<SteamUiCommandResult> ReportAsync(SteamHomeCarouselReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Big Picture Home's carousel, listing the games on the libraries attached right now instead of
/// Steam's own mix of recent and new games.
/// </summary>
/// <remarks>
/// The list Home draws is one array of app ids passed to the carousel and its background. The gate
/// replaces that array in what the carousel renders, so Steam's own components draw it, and bounds
/// the virtualized carousel's overscan to the component's default, which Home otherwise sets to the
/// whole list and so mounts every tile. Home, the carousel and the hook that builds Steam's list are
/// all module-local; Home is taken from the router's route list and its <c>type</c> is claimed.
/// <para>
/// Ordering happens in the gate because its inputs, every installed and owned game with its
/// timestamps, do not fit the bridge's payload bound. The host decides which libraries count and
/// whether uninstalled games appear.
/// </para>
/// <para>
/// Mapped from the September 2026 client beta's bundle on 2026-09-11: <c>HomeTabsActive</c> with
/// <c>#Showcase_RecentGames</c> occurs in exactly one module, the route for <c>/library/home</c>
/// renders a <c>React.memo</c> whose <c>type</c> is a writable and configurable own property, and
/// mobx-react-lite's <c>useObserver</c> is the hook Steam builds its own list on.
/// </para>
/// </remarks>
public static class SteamHomeCarouselSurface
{
    /// <summary>The patch id this surface publishes under and answers commands for.</summary>
    public const string PatchId = "steam-ui.home-carousel";

    /// <summary>The exact command vocabulary the injected gate sends.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["report"];

    /// <summary>The gate that claims Home and feeds its carousel.</summary>
    /// <remarks>
    /// Each structural fact is reported separately, so an incompatible client says which one moved.
    /// The observer hook is reported but not required: without it the carousel still follows the
    /// host and Steam's own list, and a change elsewhere shows on its next render. It accepts a Home
    /// this gate already claimed.
    /// </remarks>
    public static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        id: PatchId,
        resourceKey: "steam-ui.home-root",
        gateName: "homeCarousel",
        fingerprint: "steam-home-carousel-v1:unique-home-module+route-home-memo",
        probeExpression: $$"""
            {{SteamUiProbeJs.CountingPreamble("steam_ui_home_carousel_probe_")}}
              // Home is module-local, so the handle is the page element under the /library/home route.
              // Big Picture's router renders in the Big Picture popup's own root, not SharedJSContext's
              // #root, so that window is searched first. Read-only walk, bounded, matching on content.
              const fiberOf=el=>{if(!el)return null;const k=Object.keys(el).find(n=>n.startsWith('__reactContainer$'));return k?el[k]:null;};
              const roots=[];
              const add=doc=>{try{const f=fiberOf(doc&&doc.getElementById('popup_target'))||fiberOf(doc&&doc.getElementById('root'));if(f&&!roots.includes(f))roots.push(f);}catch(e){ } };
              add(window.SteamUIStore?.WindowStore?.GamepadUIMainWindowInstance?.BrowserWindow?.document);
              try{for(const p of window.g_PopupManager?.m_mapPopups?.values?.()??[])add(p&&p.window&&p.window.document);}catch(e){}
              add(document);
              // Breadth-first: a router sits near the top, and depth-first can spend the bound inside a
              // mounted library grid first. What the walk saw is reported, so a miss says why.
              let home=null,visited=0,homeRoutes=0,page='';
              const queue=roots.slice();
              for(let head=0;head<queue.length&&!home&&visited<250000;head++){
                const node=queue[head];
                if(!node)continue;
                visited++;
                // A Fragment's fiber holds its children array as the props themselves.
                const props=node.memoizedProps;
                const kids=Array.isArray(props)?props:props&&props.children;
                if(Array.isArray(kids)&&kids.length>2&&kids.length<512){
                  const route=kids.find(k=>k&&k.props&&k.props.path==='/library/home');
                  if(route){
                    homeRoutes++;
                    const type=route.props.children&&route.props.children.type;
                    page=!type?'none':typeof type==='function'?'function':String(type.$$typeof);
                    // Home by its source, or one this gate already claimed, whose type is ours.
                    if(type&&typeof type==='object'&&typeof type.type==='function'
                      &&(type.type.__steamUiHomeCarouselClaimed===true
                        ||(String(type.type).includes('HomeTabsActive')
                          &&String(type.type).includes('HomeActiveTab'))))home=type;
                  }
                }
                queue.push(node.child,node.sibling);
              }
              const descriptor=home?Object.getOwnPropertyDescriptor(home,'type'):null;
              return JSON.stringify({
                homeModule:count(['HomeTabsActive','#Showcase_RecentGames']),
                homeFound:home?1:0,
                roots:roots.length,
                visited:visited,
                homeRoutes:homeRoutes,
                page:page,
                claimable:!!descriptor&&descriptor.writable===true&&descriptor.configurable===true,
                // Already ours is compatible; see the remarks on this patch.
                claimed:!!home&&home.type.__steamUiHomeCarouselClaimed===true,
                stores:typeof window.collectionStore?.GetCollection==='function'
                  &&typeof window.appStore?.GetAppOverviewByAppID==='function',
                observer:count(['mobx-react-lite requires React with Hooks support']),
                react:count(['react.transitional.element','useState','cloneElement','createElement'])
              });
            }catch(error){return JSON.stringify({error:String(error)}); } })()
            """,
        compatible: root =>
            SteamUiPatchEvaluation.IsOne(root, "homeModule")
            && SteamUiPatchEvaluation.IsOne(root, "homeFound")
            && SteamUiPatchEvaluation.IsOne(root, "react")
            && SteamGatePatch.Flag(root, "stores")
            && SteamGatePatch.Flag(root, "claimable"),
        verifyOk: "status.installed&&status.resolved&&status.claimed",
        removeOk: "!status.claimed",
        subject: "Home carousel gate");

    /// <summary>Serializes a state exactly as the module publishes it.</summary>
    /// <param name="state">The state to serialize.</param>
    /// <returns>The wire payload.</returns>
    public static JsonElement Serialize(SteamHomeCarouselState state) =>
        JsonSerializer.SerializeToElement(
            state, SteamSurfaceJsonContext.Default.SteamHomeCarouselState);

    /// <summary>Reads the exact <c>report</c> payload: five counts and two flags, nothing else.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="report">The report, when this returns true.</param>
    /// <returns>Whether the payload had that shape.</returns>
    public static bool TryReadReport(JsonElement payload, out SteamHomeCarouselReport report)
    {
        const int Maximum = 100_000;
        report = new(0, 0, 0, 0, 0, false, false);
        if (payload.ValueKind != JsonValueKind.Object
            || !SteamUiPayload.HasExactly(payload, 7)
            || !SteamUiPayload.TryReadInt(payload, "items", 0, Maximum, out int items)
            || !SteamUiPayload.TryReadInt(payload, "purchases", 0, Maximum, out int purchases)
            || !SteamUiPayload.TryReadInt(payload, "installed", 0, Maximum, out int installed)
            || !SteamUiPayload.TryReadInt(payload, "uninstalled", 0, Maximum, out int uninstalled)
            || !SteamUiPayload.TryReadInt(payload, "excluded", 0, Maximum, out int excluded)
            || !TryReadFlag(payload, "tracking", out bool tracking)
            || !TryReadFlag(payload, "fallback", out bool fallback))
        {
            return false;
        }

        report = new(items, purchases, installed, uninstalled, excluded, tracking, fallback);
        return true;
    }

    /// <summary>Declares the surface as one module: the gate, the instruction, and the report.</summary>
    /// <param name="enabled">Whether the instruction may be published right now.</param>
    /// <param name="read">What the carousel should draw from, or null when there is nothing to say yet.</param>
    /// <param name="backend">What hears the carousel's report.</param>
    /// <param name="id">The module id, for diagnostics and duplicate detection.</param>
    /// <returns>The module to register.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamHomeCarouselState?>> read,
        ISteamHomeCarouselBackend backend,
        string id = "home-carousel")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            patches: [Patch],
            publications:
            [
                SteamSurfaceModule.Publication(
                    PatchId, enabled, read, SteamSurfaceJsonContext.Default.SteamHomeCarouselState),
            ],
            commands:
            [
                new(PatchId, "report", (request, cancellationToken) =>
                    TryReadReport(request.Payload, out SteamHomeCarouselReport report)
                        ? backend.ReportAsync(report, cancellationToken)
                        : SteamSurfaceModule.Invalid("The home carousel report is invalid.")),
            ]);
    }

    private static bool TryReadFlag(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.ValueKind is JsonValueKind.True;
        return true;
    }
}
