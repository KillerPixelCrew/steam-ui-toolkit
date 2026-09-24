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
  const BackstackToken = "router-backstack";
  // Steam's back-stack Route, named by two tokens Valve wrote — the JSX prop the export fills in and
  // the optional member access it fills it from — rather than by the shape of the minified code
  // between them. The fingerprint this replaced was decky-loader's, and it spelled that minified
  // local out as a single-character wildcard: `routePath:.\.match\?\.path.`. It stopped matching on
  // 2026-09-24, when the client began emitting two-character names (`routePath:be.match?.path`), and
  // the gate registered no route at all. A fingerprint may not describe a minified identifier; only
  // what its author typed is stable across a client build.
  //
  // This lookup is now the fallback rather than the answer. The Route the gate builds with is taken
  // from the route list Steam is currently rendering (see `applyPages`), which is the same component
  // by construction and cannot be renamed out from under us. What remains here covers the one case
  // that borrowing does not: a client whose route list holds something other than plain Route
  // elements. Because it is a fallback, failing to find it is no longer a reason to refuse a client.
  const BackstackRouteMarkers = ["routePath:", ".match?.path"] as const;
  const isBackstackRoute = (value) =>
    typeof value === "function" &&
    BackstackRouteMarkers.every((marker) => String(value).includes(marker));

  // Whether a route element's type can be used as a component. React elements hold a string type for
  // host elements like "div", which a page must not be built with.
  const isUsableRoute = (type) =>
    typeof type === "function" || (typeof type === "object" && type !== null);

  // A path every build of the client has and no consumer would register, used to recognise the
  // route list among the router's children.
  const KnownRoute = "/library/home";
  const MaximumPages = 32;
  const MaximumDescent = 8;
  // The router sits about a hundred levels down the live tree, so the bound is generous; it exists
  // to stop a cyclic or pathological tree, not to limit a legitimate search.
  const MaximumNodesVisited = 60000;

  let runtime;
  let react;
  // The fallback, resolved from the registry. `borrowedRoute` is the one Steam handed us.
  let RouteComponent = null;
  let borrowedRoute = null;
  let routeSource = "none";
  let routeLookupError = "";
  let memo: any = null;
  let routeSwitchFiber: any = null;
  let routeSwitchWrapper: any = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  // What the last install's adoption of already-mounted routers reached; see install().
  let lastAdoption: { adopted: number; scheduled: boolean } = { adopted: 0, scheduled: false };

  let pages: { id: string; path: string; title: string; override?: boolean; template?: string }[] =
    [];
  let lastOutcome = "never rendered";
  let observedRoutes: string[] = [];

  const descendCache = new Map();

  // One registered page. The content is described by the host rather than supplied as a component:
  // a consumer's React lives in its own process, not in this asset, so what crosses the bridge is
  // data. A page renders its title and asks the host for its body, which is the same shape the
  // Quick Access rows already use.
  const renderPage = (page) => {
    const renderer = steamPageRenderers.get(page.template);
    if (renderer) return renderer(react, page);
    return react.createElement(
      "div",
      {
        className: "steam-ui-page",
        role: "region",
        "aria-label": page.title,
      },
      react.createElement("h1", null, page.title),
      react.createElement("div", { id: `steam-ui-page-body-${page.id}` }),
    );
  };

  // Steam's own Route, preferring the one it is rendering with over the one the registry named.
  const activeRoute = () => borrowedRoute ?? RouteComponent;

  const buildRoute = (page) =>
    react.createElement(
      activeRoute(),
      { path: page.path, key: `steam-ui-page-${page.id}` },
      renderPage(page),
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
    observedRoutes = routes
      .filter((route) => react.isValidElement(route) && typeof route.props?.path === "string")
      .map((route) => route.props.path);

    // Steam's Route, borrowed from the element Steam is rendering `/library/home` with. This is the
    // component itself rather than something that matched a description of it, so no client build
    // can rename it away; the registry lookup above exists only for a list this cannot be read from.
    const known = routes.find(
      (route) => react.isValidElement(route) && route.props?.path === KnownRoute,
    );
    if (known && isUsableRoute(known.type)) {
      const disagrees = RouteComponent && RouteComponent !== known.type;
      borrowedRoute = known.type;
      routeSource = disagrees ? "borrowed (lookup differs)" : "borrowed";
    } else if (RouteComponent) {
      routeSource = "lookup";
    }

    const wanted = pages.slice(0, MaximumPages);
    if (!wanted.length) {
      lastOutcome = `routes=${routes.length} pages=0 route=${routeSource}`;
      return routes;
    }

    // Loud rather than empty: a page that cannot be built is the one failure this gate can reach
    // while otherwise holding the router, and a surface that silently draws nothing is a defect.
    if (!activeRoute()) {
      lastOutcome = `routes=${routes.length} pages=${wanted.length} route=unavailable`;
      return routes;
    }

    const overrides = wanted.filter((page) => page.override === true).map(buildRoute);
    const additions = wanted.filter((page) => page.override !== true).map(buildRoute);
    lastOutcome =
      `routes=${routes.length} overrides=${overrides.length} additions=${additions.length}` +
      ` route=${routeSource}`;
    return [...overrides, ...routes, ...additions];
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

    // Through the shared resolver rather than a raw require and a local scan of the export names:
    // it counts aliases of one value once, so a re-export cannot read as ambiguity, and it says
    // which of "module absent", "module ambiguous" and "export absent" actually happened.
    //
    // Recorded rather than fatal. The gate builds with the Route it borrows from Steam's own route
    // list, so a client this lookup cannot resolve is not a client the gate has to refuse. Refusing
    // one is what took every custom page down on 2026-09-24 while the client was otherwise fine.
    try {
      RouteComponent = runtime.exported([BackstackToken], isBackstackRoute);
      routeLookupError = "";
    } catch (error) {
      RouteComponent = null;
      routeLookupError = String(error);
    }

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
    const host = document.getElementById("root");
    if (!host) return null;
    const key = Object.keys(host).find((name) => name.startsWith("__reactContainer$"));
    if (!key) return null;

    const seen = new Set();
    const queue: any[] = [host[key]];
    let visited = 0;
    for (let head = 0; head < queue.length && visited <= MaximumNodesVisited; head++) {
      const node = queue[head];
      if (!node || seen.has(node)) continue;
      seen.add(node);
      visited++;
      const current = node.elementType?.type;
      const stored = current?.[claimKeys.marker] === true ? current[claimKeys.original] : current;
      const original = stored?.kind === "steam-ui-property-snapshot-v1" ? stored.value : stored;
      if (
        typeof original === "function" &&
        String(original).includes(RouterTokens[0]) &&
        node.elementType &&
        typeof node.elementType === "object"
      ) {
        return node.elementType;
      }
      queue.push(node.child, node.sibling);
    }
    return null;
  };

  const findRouteSwitchFiber = () => {
    const host = document.getElementById("root");
    const key = host
      ? Object.keys(host).find((name) => name.startsWith("__reactContainer$"))
      : null;
    const queue: any[] = key ? [(host as any)[key]] : [];
    for (
      let head = 0, visited = 0;
      head < queue.length && visited <= MaximumNodesVisited;
      head++, visited++
    ) {
      const node = queue[head];
      if (!node) continue;
      const current = node.type;
      const original = current?.__steamUiPageSwitchOriginal ?? current;
      const source = typeof original === "function" ? String(original) : "";
      if (source.includes("computedMatch") && source.includes("TopLevelTransition")) return node;
      queue.push(node.child, node.sibling);
    }
    return null;
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

    // The memo object is now patched globally, but an already mounted fiber keeps its resolved
    // function in `type`, so the claim reaches the next mount only. This gate used to swap that one
    // fiber and call forceUpdate on the nearest class ancestor, which cannot work: Steam's router is
    // a React.memo with the default comparison, so re-rendering the parent produces the same element
    // with the same props and React bails out at the memo without ever calling what we installed.
    // The result was a claim that was correct and inert — status said claimed, lastOutcome said
    // never rendered, and no page existed until the user navigated and changed the props by hand.
    //
    // adoptMountedType is the shared answer the Home carousel already used for the same bail-out:
    // it swaps `type` on every mounted instance across every React root, replaces `memoizedProps`
    // with an object that cannot shallow-compare equal, and only then asks for a render. It also
    // covers the menu and Quick Access popups, which have React roots of their own that this gate's
    // single walk of `#root` never saw.
    lastAdoption = adoptMountedType(reactRootFibers(), memo, memo.type, MaximumNodesVisited);

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      const declared = Array.isArray(state?.pages) ? state.pages : [];
      pages = declared
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
    });
    return { ok: true, installed: true, reclaimed: claim.reclaimed };
  };

  // Every owned mutation goes back before the gate forgets it owns anything. Clearing `installed`
  // ahead of the fallible release left both wrappers running while each later remove() answered
  // `absent`, so a failed cleanup could never be retried.
  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    // Read before the release hands `type` back, so the adopted instances can be matched by it.
    const wrapper = memo?.type;
    const released = releaseMember(memo, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "page host release failed";
      return { ok: false, error: lastError };
    }

    // Every router this install adopted, handed back to the function the claim displaced. No render
    // is requested: Steam's own draws again the next time the page renders.
    releaseMountedType(reactRootFibers(), memo, wrapper, memo.type, MaximumNodesVisited);
    lastAdoption = { adopted: 0, scheduled: false };
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
    routeSource = "none";
    descendCache.clear();
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!memo,
    routeResolved: !!activeRoute(),
    // Which of the two the pages are built with, so a client where the registry lookup has drifted
    // is visible as "borrowed" long before anyone has to debug an empty page.
    routeSource,
    routeLookupError,
    claimed: memberClaimed(memo, "type", claimKeys),
    pages: pages.length,
    // Whether the claim reached the routers already on screen, and whether one is still drawing
    // something else. A claim that adopted nothing is inert until Steam mounts a new router, which
    // is the difference between "claimed" and "actually running".
    mounted: {
      ...lastAdoption,
      stale: staleFibers(reactRootFibers(), memo, MaximumNodesVisited)
    },
    // What the last render actually saw. Everything above can be true while no page is reachable,
    // because insertion depends on finding the route list in the tree Steam rendered.
    routeCount: observedRoutes.length,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("pages", createPageHost());
