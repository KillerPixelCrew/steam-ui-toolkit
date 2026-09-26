// Custom pages inside Steam's Game Mode UI.
//
// Mapped against the live client on 2026-09-10, and cross-read against decky-loader's RouterHook
// (b4b8be3) as evidence for the approach:
//
//   <memo>            source carries "Settings.Root()"; the router
//     fd              Steam's own switch: computedMatch + TopLevelTransition, 31 route children
//       <Route ...>   one per page, children of fd rather than rendered output
//
// `fd` is not react-router's Switch. Its source shows the selection rule: it walks `children`, takes
// the FIRST valid element whose `path` matches, and clones it with `location` and `computedMatch`.
// Two things follow, and both are in the API rather than hidden:
//
//   - appending is safe for a path Steam does not have, and overriding one of Steam's requires
//     going in front of it, so a page declares which it wants;
//   - routes are plain elements passed as `children`, so registering a page is a list operation on
//     props. No descent into rendered output is needed, unlike the navigation panel, where entries
//     do not exist until the root renders.
//
// The Route component is Steam's own, resolved from the module that carries "router-backstack",
// never react-router's. That is what gives a custom page native back-navigation: Steam's Route
// registers the match with the back stack, so B and the back gesture pop the page the way they pop
// /settings. Using react-router's Route renders the same content and silently loses that.
const steamPageRenderers = new Map<string, (react: any, page: any) => any>();
const registerSteamPageRenderer = (
  template: string,
  render: (react: any, page: any) => any,
) => {
  if (!template || template === "default" || steamPageRenderers.has(template)) {
    throw new Error(`Steam page renderer '${template}' is invalid or already registered.`);
  }
  steamPageRenderers.set(template, render);
};

function createPageHost() {
  const patchId = "steam-ui.pages";
  const claimKeys = {
    marker: "__steamUiPageHostClaimed",
    original: "__steamUiPageHostOriginal",
  } as const;

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
  const MaximumPages = 32;
  const MaximumDescent = 8;
  const PageKeyPrefix = "steam-ui-page-";

  let runtime;
  let react;
  // Steam's own Route, taken off the `/library/home` element in the route list Steam is rendering:
  // the component itself rather than a description of it, so no client build can rename it away.
  let borrowedRoute = null;
  let routeVerified = false;
  let memo: any = null;
  let routeSwitchFiber: any = null;
  let routeSwitchWrapper: any = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  const mounted = createMountedAdoption();

  let pages: { id: string; path: string; title: string; override?: boolean; template?: string }[] =
    [];
  let lastOutcome = "never rendered";
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
        lastError = `page renderer '${page.template}' threw: ${String(error)}`;
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
      react.createElement(SteamUiPageBody, { page }),
    );

  // Whether an array of elements is the router's route list.
  const isRouteList = (value) =>
    Array.isArray(value) &&
    value.length > 2 &&
    value.length < 512 &&
    value.some((item) => react.isValidElement(item) && item.props?.path === KnownRoute);

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
    if (own.length) return routes;

    // A host element would be a string type and cannot be built with; anything else is what Steam
    // renders that route with, verified against the Route's own markers for the status only.
    const known = steam.find(
      (route) => react.isValidElement(route) && route.props?.path === KnownRoute,
    );
    if (known && typeof known.type !== "string") {
      borrowedRoute = known.type;
      routeVerified = sourceMatches(known.type, BackstackRouteMarkers);
    }

    const wanted = pages.slice(0, MaximumPages);
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
      return descend(type(props), 0);
    };
  const descend = (element, depth) => {
    if (depth > MaximumDescent || !react.isValidElement(element)) return element;
    const replaced = replaceRouteList(element, 0);
    if (replaced !== element) return replaced;
    return descendInto(react, element, descendCache, pageDescender) ?? element;
  };

  const resolve = () => {
    runtime = getWebpackRuntime("pages");
    const resolvedReact = resolveReact(runtime);
    if (!resolvedReact) {
      lastError = "React runtime was not a unique match";
      return false;
    }
    react = resolvedReact;

    // The router module is confirmed to exist and to be unique, but it exports nothing that
    // reaches the router: the memo is built locally inside the module. Verified against the live
    // client on 2026-09-10 — every export of that module was inspected and none is a memo whose
    // type carries the marker. So the handle comes from the rendered tree instead, which is also
    // where decky-loader gets it. Checking the module anyway keeps the failure specific: "Steam
    // moved the router" and "the tree has not been built yet" are different problems.
    if (!runtime.findUnique([RouterTokens[0], RouterTokens[1]])) {
      lastError = "router module was not a unique match";
      return false;
    }

    memo = findRouterMemo();
    if (!memo) {
      lastError = "router was not found in the rendered tree";
      return false;
    }
    return true;
  };

  // Finds the router's memo through SharedJSContext's own React root.
  //
  // SharedJSContext holds the tree that every Steam window renders from, which is why a claim made
  // here reaches the Big Picture window and the menu window alike. The search is bounded in both
  // nodes visited and depth so a pathological tree cannot hang the injection, and it matches on the
  // component's source rather than on a path through the tree.
  //
  // Breadth-first over the child and sibling links, as the Home carousel walks the same tree. A
  // recursive walk nests a frame for every sibling, so a long sibling chain could exhaust the stack
  // before the node bound was ever reached.
  const findRouterMemo = () => {
    let found = null;
    walkFibers(reactRootFibers(), MaximumMountedNodes, (node) => {
      const elementType = node.elementType;
      if (!elementType || typeof elementType !== "object") return false;
      // Through the gate's own claim, or a re-resolve while the claim is held finds no router.
      if (!sourceMatches(unclaimedValue(elementType.type, claimKeys), [RouterTokens[0]])) {
        return false;
      }
      found = elementType;
      return true;
    });
    return found;
  };

  const findRouteSwitchFiber = () => {
    let found = null;
    walkFibers(reactRootFibers(), MaximumMountedNodes, (node) => {
      const original = node.type?.__steamUiPageSwitchOriginal ?? node.type;
      if (!sourceMatches(original, ["computedMatch", "TopLevelTransition"])) return false;
      found = node;
      return true;
    });
    return found;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    const resolved = attemptResolution(resolve, (error) => {
      lastError = "page host resolution failed: " + String(error);
    });
    if (!resolved) return { ok: false, error: lastError };

    const claim = claimMember(memo, "type", claimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return function SteamUiPageRouter(props) {
        return descend(original(props), 0);
      };
    });
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }

    routeSwitchFiber = findRouteSwitchFiber();
    if (!routeSwitchFiber) {
      const rolledBack = releaseMember(memo, "type", claimKeys);
      lastError = rolledBack.ok
        ? "Steam's mounted route switch was not found"
        : "Steam's mounted route switch was not found, and the router claim could not be released: " +
          (rolledBack.error ?? "unknown");
      return { ok: false, error: lastError };
    }
    const currentSwitch = routeSwitchFiber.type;
    const originalSwitch = currentSwitch?.__steamUiPageSwitchOriginal ?? currentSwitch;
    routeSwitchWrapper = function SteamUiPageSwitch(props) {
      const children = react.Children.toArray(props?.children);
      return originalSwitch({
        ...props,
        children: isRouteList(children) ? applyPages(children) : children,
      });
    };
    Object.defineProperty(routeSwitchWrapper, "__steamUiPageSwitchOriginal", {
      value: originalSwitch,
    });
    routeSwitchFiber.type = routeSwitchWrapper;
    if (routeSwitchFiber.alternate) routeSwitchFiber.alternate.type = routeSwitchWrapper;

    // The claim reaches the next mount only; the router already on screen is adopted, or the claim
    // is correct and inert until the user happens to remount it. See createMountedAdoption.
    mounted.adopt(memo, memo.type);

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      const declared = Array.isArray(state?.pages) ? state.pages : [];
      const next = declared
        .filter(
          (page) =>
            page &&
            typeof page.id === "string" &&
            typeof page.title === "string" &&
            typeof page.path === "string" &&
            // A path has to be absolute or Steam's matcher never sees it, and a page that claims
            // every route would black out the client.
            page.path.startsWith("/") &&
            page.path !== "/",
        )
        .slice(0, MaximumPages);
      // The wrappers read `pages` from their closure, so a publication changes nothing React can
      // see on its own. Only a changed list earns a render: the class above the router is the one
      // asked, and its render re-runs every route, the configurator's edit session included.
      if (!publicationChanged(pages, next)) return;
      pages = next;
      mounted.rerender();
    });
    return { ok: true, installed: true, reclaimed: claim.reclaimed };
  };

  // Every owned mutation goes back before the gate forgets it owns anything. Clearing `installed`
  // ahead of the fallible release left both wrappers running while each later remove() answered
  // `absent`, so a failed cleanup could never be retried.
  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const released = releaseMember(memo, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "page host release failed";
      return { ok: false, error: lastError };
    }

    mounted.release(memo.type);
    if (routeSwitchFiber && routeSwitchWrapper) {
      const originalSwitch = routeSwitchWrapper.__steamUiPageSwitchOriginal;
      if (routeSwitchFiber.type === routeSwitchWrapper) routeSwitchFiber.type = originalSwitch;
      if (routeSwitchFiber.alternate?.type === routeSwitchWrapper) {
        routeSwitchFiber.alternate.type = originalSwitch;
      }
    }
    routeSwitchFiber = null;
    routeSwitchWrapper = null;

    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    pages = [];
    // Borrowed from a render that is about to be undone, so it is not carried into the next install.
    borrowedRoute = null;
    routeVerified = false;
    descendCache.clear();
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!memo,
    routeResolved: !!borrowedRoute,
    // "borrowed (unverified)" is a Route whose source lacks the back-stack markers: it draws, and
    // back navigation may be the thing it lost.
    routeSource: borrowedRoute ? (routeVerified ? "borrowed" : "borrowed (unverified)") : "none",
    claimed: memberClaimed(memo, "type", claimKeys),
    pages: pages.length,
    // Whether the claim reached the routers already on screen, and whether one is still drawing
    // something else: the difference between "claimed" and "actually running".
    mounted: mounted.status(),
    // What the last render actually saw. Everything above can be true while no page is reachable,
    // because insertion depends on finding the route list in the tree Steam rendered.
    routeCount: observedRoutes.length,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("pages", createPageHost());
