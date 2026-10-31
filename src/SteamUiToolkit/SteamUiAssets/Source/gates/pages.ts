// Custom pages inside Steam's Game Mode UI.
//
// Mapped against the live client on 2026-09-10, and cross-read against decky-loader's RouterHook
// (b4b8be3) as evidence for the approach:
//
//   <memo>            source carries "Settings.Root()"; the router
//     switch          Steam's own route switch: computedMatch + TopLevelTransition, 31 route children
//       <Route ...>   one per page, children of the switch rather than rendered output
//
// The switch is Steam's own, not react-router's Switch. Its source shows the selection rule: it
// walks `children`, takes the FIRST valid element whose `path` matches, and clones it with
// `location` and `computedMatch`. Two things follow, and both are in the API rather than hidden:
//
//   - appending is safe for a path Steam does not have, and overriding one of Steam's requires
//     going in front of it, so a page declares which it wants;
//   - routes are plain elements passed as `children`, so registering a page is a list operation on
//     props. No descent into rendered output is needed, unlike the navigation panel, where entries
//     do not exist until the root renders.
//
// The Route component is Steam's own, taken off the route list Steam already rendered, never
// react-router's. That is what gives a custom page native back-navigation: Steam's Route registers
// the match with the back stack, so B and the back gesture pop the page the way they pop /settings.
// Using react-router's Route renders the same content and silently loses that.
const steamPageRenderers = new Map<string, (react: any, page: any) => any>();
/**
 * Registers a renderer for one page template in this bridge generation.
 * @param template Stable template key used by host page publications.
 * @param render Renderer receiving Steam's React runtime and the published page descriptor with matched route params.
 */
const registerSteamPageRenderer = (template: string, render: (react: any, page: any) => any) => {
    if (!template || template === "default" || steamPageRenderers.has(template)) {
        throw new Error(`Steam page renderer '${template}' is invalid or already registered.`);
    }
    steamPageRenderers.set(template, render);
};
/**
 * Installs host-owned routes and renders registered page templates inside Steam's router.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createPageHost() {
    const patchId = "steam-ui.pages";
    const claimKeys = {
        marker: "__steamUiPageHostClaimed",
        original: "__steamUiPageHostOriginal",
    } as const;
    // The route switch's own claim. It has no public handle, so its mounted fibers are adopted by
    // source and each one carries this marker on its wrapper.
    const switchKeys = {
        marker: "__steamUiPageSwitchClaimed",
        original: "__steamUiPageSwitchOriginal",
    } as const;
    // Steam's own route switch, matched the way the gate matches the list it renders.
    const SwitchTokens = ["computedMatch", "TopLevelTransition"] as const;

    // The router, unique on this pair. "Settings.Root()" alone matches six modules and
    // "TopLevelTransition" is the switch's own; together they name exactly one.
    const RouterTokens = ["Settings.Root()", "TopLevelTransition"] as const;
    // What Steam's back-stack Route reads, in its author's words: the JSX prop it fills in and the
    // optional member access it fills it from. Used to verify the Route borrowed from the route list,
    // never to find one; a fingerprint names what an author typed, not how a minifier spelled it.
    const BackstackRouteMarkers = ["routePath:", ".match?.path"] as const;

    // A path every build of the client has and no consumer would register, used to recognise the
    // route list among the router's children.
    const KnownRoute = "/library/home";
    const MaximumDescent = 8;
    const PageKeyPrefix = "steam-ui-page-";

    let react;
    // Steam's own Route, taken off the `/library/home` element in the route list Steam is rendering:
    // the component itself rather than a description of it, so no client build can rename it away.
    let borrowedRoute = null;
    let routeVerified = false;
    let memo: any = null;
    let router: ClaimedComponent | null = null;
    let hostPages: any[] = [];

    let pages: {
        id: string;
        path: string;
        title: string;
        override?: boolean;
        template?: string;
    }[] = [];
    // The host's pages, then the ones plugin frontends contribute.
    const composePages = () => [
        ...hostPages,
        ...pluginFrontendItems("page").map((slot) => ({
            ...slot.value,
            template: slot.id,
        })),
    ];
    let lastOutcome = "never rendered";
    // The last page renderer that threw, which cost its own page and fell open to the heading.
    let rendererError = "";
    let observedRoutes: string[] = [];

    const descendCache = new Map();

    // One registered page's body. The content is described by the host rather than supplied as a
    // component: a consumer's React lives in its own process, so what crosses the bridge is data,
    // and a renderer registered under the page's template draws it.
    //
    // The renderer runs here, when Steam draws the page, not when the route is built. Routes are
    // built the moment pages are published, which on a cold start is before the gate a renderer
    // needs has resolved; calling it then either baked a null child into the route or threw inside
    // Steam's router render, whose error boundary replaces the whole client. A renderer that throws
    // now costs its own page, falls open to the heading, and names its template.
    function SteamUiPageBody({ page }) {
        const renderer = steamPageRenderers.get(page.template);
        if (renderer) {
            try {
                return renderer(react, page);
            } catch (error) {
                rendererError = `page renderer '${page.template}' threw: ${String(error)}`;
            }
        }
        return react.createElement(
            "div",
            { className: "steam-ui-page", role: "region", "aria-label": page.title },
            react.createElement("h1", null, page.title),
            react.createElement("div", { id: `steam-ui-page-body-${page.id}` }),
        );
    }

    const buildRoute = (page) =>
        react.createElement(
            borrowedRoute,
            { path: page.path, key: `${PageKeyPrefix}${page.id}` },
            // Steam's back-stack Route calls function children with its native route props. Keep the
            // published descriptor intact and detach params from that match on every render, including
            // reloads; consumers must never resolve a game identity from focus or the visible artwork.
            (route) =>
                route?.match
                    ? react.createElement(SteamUiPageBody, {
                          page: { ...page, params: { ...route.match.params } },
                      })
                    : null,
        );

    // Whether an array of elements is the router's route list.
    const isRouteList = (value) => isSteamRouteList(react, value, KnownRoute);

    // Inserts the registered pages into the route list.
    //
    // Overrides go in front of Steam's own routes and additions behind them, because the switch takes
    // the first match. Both keep their relative order, so two overrides of the same path resolve in
    // registration order rather than arbitrarily.
    const applyPages = (routes) => {
        // Idempotent: the route list is reached twice per render, once in the router's output and once
        // by the mounted switch, and a page inserted by the first pass must not be inserted again.
        const own = routes.filter(
            (route) => typeof route?.key === "string" && route.key.startsWith(PageKeyPrefix),
        );
        const steam = routes.filter((route) => !own.includes(route));
        observedRoutes = steam
            .filter((route) => react.isValidElement(route) && typeof route.props?.path === "string")
            .map((route) => route.props.path);

        // A host element would be a string type and cannot be built with; anything else is what Steam
        // renders that route with, verified against the Route's own markers for the status only.
        const known = steam.find(
            (route) => react.isValidElement(route) && route.props?.path === KnownRoute,
        );
        if (known && typeof known.type !== "string") {
            borrowedRoute = known.type;
            routeVerified = sourceMatches(known.type, BackstackRouteMarkers);
        }

        const wanted = pages;
        if (!wanted.length) {
            lastOutcome = `routes=${steam.length} pages=0`;
            return routes;
        }
        // Loud rather than empty: a surface that silently draws nothing is a defect.
        if (!borrowedRoute) {
            lastOutcome = `routes=${steam.length} pages=${wanted.length} route=unavailable`;
            return routes;
        }

        const overrides = wanted.filter((page) => page.override === true).map(buildRoute);
        const additions = wanted.filter((page) => page.override !== true).map(buildRoute);
        lastOutcome = `routes=${steam.length} overrides=${overrides.length} additions=${additions.length}`;
        return [...overrides, ...steam, ...additions];
    };

    // Finds the route list in the router's returned element tree and replaces it.
    //
    // The list is found by content — the array holding a route for a path the client always has —
    // rather than by an index chain into props. decky-loader's gamepad path indexes
    // children.props.children[0].props.children, which is exactly the kind of selector that breaks on
    // a client update with no diagnostic; its own desktop path searches by /library/home instead, and
    // that is the half worth following.
    const replaceRouteList = (element, depth) => {
        if (depth > MaximumDescent || !react.isValidElement(element)) return element;

        const children = element.props?.children;
        if (isRouteList(children)) {
            return react.cloneElement(element, { children: applyPages(children) });
        }

        return mapChildren(react, element, (kid) => replaceRouteList(kid, depth + 1));
    };

    const pageDescender = (type) =>
        function SteamUiPageDescend(props) {
            return descend(type(props));
        };
    const descend = (element) => {
        if (!react.isValidElement(element)) return element;
        const replaced = replaceRouteList(element, 0);
        if (replaced !== element) return replaced;
        return descendInto(react, element, descendCache, pageDescender) ?? element;
    };

    // Finds the router's memo and Steam's mounted route switch through SharedJSContext's own React
    // roots, in one bounded walk.
    //
    // SharedJSContext holds the tree that every Steam window renders from, which is why a claim made
    // here reaches the Big Picture window and the menu window alike. The router module is unique, but
    // it exports nothing that reaches the router: the memo is built locally inside the module
    // (verified against the live client on 2026-09-10), so the handle comes from the rendered tree,
    // which is also where decky-loader gets it. The search matches on component source, through the
    // gate's own claims, or a find while the claims are held finds no router.
    const findMounted = () => {
        let found: any = null;
        let routeSwitch = false;
        const roots = reactRootFibers();
        walkFibers(roots, MaximumMountedNodes, (node) => {
            const elementType = node.elementType;
            if (
                !found &&
                elementType &&
                typeof elementType === "object" &&
                sourceMatches(unclaimedValue(elementType.type, claimKeys), [RouterTokens[0]])
            ) {
                found = elementType;
            }
            routeSwitch ||= sourceMatches(unclaimedValue(node.type, switchKeys), SwitchTokens);
            return !!found && routeSwitch;
        });
        return { memo: found, routeSwitch, roots: roots.length };
    };

    // Steam's own back-stack Route, the one that gives a page native back navigation. Reported, not
    // required: the gate builds with the Route it borrows from the route list Steam is rendering.
    // Counted by value rather than by export name, so a module re-exporting the Route under a second
    // name is one match, not ambiguity. Requiring it is what declared an otherwise healthy client
    // incompatible on 2026-09-24 and took every custom page with it.
    const countSteamRoutes = (runtime) => {
        const backstack = runtime.findUnique(["router-backstack"]);
        if (!backstack) return { backstackModule: 0, steamRoute: 0 };
        const routes = new Set<any>();
        try {
            const exports = runtime(backstack[0]);
            for (const name of Object.keys(exports ?? {})) {
                try {
                    const value = exports[name];
                    if (typeof value === "function" && sourceMatches(value, BackstackRouteMarkers))
                        routes.add(value);
                } catch {
                    // An export whose getter throws is not the Route.
                }
            }
        } catch {
            // A back-stack module that will not load is reported as having no Route.
        }
        return { backstackModule: 1, steamRoute: routes.size };
    };

    // Pure: what the gate cannot work without, which is the router it claims, the switch it wraps,
    // React, and a memo whose type it can put back. A router module that is there with no router
    // mounted is a window still loading, or Big Picture closed: not ready.
    const find = (runtime) => {
        const resolvedReact = resolveReact(runtime);
        if (!resolvedReact) return gateIncompatible("React runtime was not a unique match", { react: 0 });
        // Checked although nothing is loaded from it, so a miss is specific: "Steam moved the router"
        // and "the tree has not been built yet" are different problems.
        if (!runtime.findUnique([RouterTokens[0], RouterTokens[1]])) {
            return gateIncompatible("router module was not a unique match", { routerModule: 0, react: 1 });
        }
        const routeSwitch = runtime.count([...SwitchTokens]);
        const mountedNow = findMounted();
        const descriptor = mountedNow.memo ? Object.getOwnPropertyDescriptor(mountedNow.memo, "type") : null;
        const claimed = !!mountedNow.memo && memberClaimed(mountedNow.memo, "type", claimKeys);
        const facts = {
            routerModule: 1,
            ...countSteamRoutes(runtime),
            routerFound: mountedNow.memo ? 1 : 0,
            switchMounted: mountedNow.routeSwitch,
            roots: mountedNow.roots,
            claimable: descriptor?.writable === true && descriptor.configurable === true,
            claimed,
            routeSwitch,
            react: 1,
        };
        if (routeSwitch !== 1) return gateIncompatible("route switch module was not a unique match", facts);
        if (!mountedNow.memo) return gateNotReady("the router is not mounted", facts);
        if (!mountedNow.routeSwitch) return gateNotReady("Steam's route switch is not mounted", facts);
        if (!claimed && !facts.claimable) {
            return gateIncompatible("the router's type is not writable and configurable", facts);
        }
        return gateReady({ react: resolvedReact, memo: mountedNow.memo }, facts);
    };

    const gate = defineGate({
        name: "pages",
        find,
        bind: (handles, ledger) => {
            react = handles.react;
            memo = handles.memo;
            // The claim reaches the next mount only; the router already on screen is adopted, or the
            // claim is correct and inert until the user happens to remount it.
            const claimedRouter = ledger.component({
                host: memo,
                keys: claimKeys,
                wrap: (original) =>
                    function SteamUiPageRouter(props) {
                        return descend(original(props));
                    },
            });
            if (!claimedRouter.ok) return claimedRouter.error;
            const routeSwitch = ledger.component({
                keys: switchKeys,
                match: (_fiber, type) => sourceMatches(type, SwitchTokens),
                wrap: (originalSwitch) =>
                    function SteamUiPageSwitch(props) {
                        const children = react.Children.toArray(props?.children);
                        return originalSwitch({
                            ...props,
                            children: isRouteList(children) ? applyPages(children) : children,
                        });
                    },
            });
            if (!routeSwitch.ok) return routeSwitch.error;
            if (routeSwitch.status().adopted === 0) return "Steam's mounted route switch was not found";
            router = claimedRouter;

            // Plugin pages already loaded are drawn from the start; neither subscription replays them.
            pages = composePages();
            ledger.subscribe(patchId, (state) => {
                const declared = Array.isArray(state?.pages) ? state.pages : [];
                const next = declared.filter(
                    (page) =>
                        page &&
                        typeof page.id === "string" &&
                        typeof page.title === "string" &&
                        typeof page.path === "string" &&
                        // A path has to be absolute or Steam's matcher never sees it, and a page that
                        // claims every route would black out the client.
                        page.path.startsWith("/") &&
                        page.path !== "/",
                );
                // The wrappers read `pages` from their closure, so a publication changes nothing React
                // can see on its own. Only a changed list earns a render: the class above the router is
                // the one asked, and its render re-runs every route, the configurator's edit session
                // included.
                if (!publicationChanged(hostPages, next)) return;
                hostPages = next;
                pages = composePages();
                claimedRouter.rerender();
            });
            const unsubscribePlugins = subscribePluginFrontends(() => {
                pages = composePages();
                claimedRouter.rerender();
            });
            ledger.cleanup("plugin frontends", unsubscribePlugins);
        },
        unbind: () => {
            router = null;
            hostPages = [];
            pages = [];
            // Borrowed from a render that is about to be undone, so it is not carried into the next
            // install.
            borrowedRoute = null;
            routeVerified = false;
            descendCache.clear();
            lastOutcome = "removed";
        },
        status: () => ({
            routeResolved: !!borrowedRoute,
            // "borrowed (unverified)" is a Route whose source lacks the back-stack markers: it draws,
            // and back navigation may be the thing it lost.
            routeSource: borrowedRoute ? (routeVerified ? "borrowed" : "borrowed (unverified)") : "none",
            claimed: memberClaimed(memo, "type", claimKeys),
            pages: pages.length,
            // Whether the claim reached the routers already on screen, and whether one is still
            // drawing something else: the difference between "claimed" and "actually running".
            mounted: router?.status() ?? { adopted: 0, scheduled: false, stale: 0 },
            // What the last render actually saw. Everything above can be true while no page is
            // reachable, because insertion depends on finding the route list in the tree Steam
            // rendered.
            routeCount: observedRoutes.length,
            lastOutcome,
            rendererError,
        }),
    });
    return gate;
}

registerGate("pages", createPageHost());
