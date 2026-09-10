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
  // decky-loader's fingerprint for Steam's back-stack Route, confirmed against this client: the
  // export whose body threads the match's path into routePath.
  const RoutePattern = /routePath:.\.match\?\.path./u;

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
  let RouteComponent = null;
  let memo = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;

  let pages: { id: string; path: string; title: string; override?: boolean }[] = [];
  let lastOutcome = "never rendered";
  let observedRoutes: string[] = [];

  const descendCache = new Map();

  // One registered page. The content is described by the host rather than supplied as a component:
  // a consumer's React lives in its own process, not in this asset, so what crosses the bridge is
  // data. A page renders its title and asks the host for its body, which is the same shape the
  // Quick Access rows already use.
  const renderPage = (page) =>
    react.createElement(
      "div",
      {
        className: "steam-ui-page",
        role: "region",
        "aria-label": page.title,
      },
      react.createElement("h1", null, page.title),
      react.createElement("div", { id: `steam-ui-page-body-${page.id}` }),
    );

  const buildRoute = (page) =>
    react.createElement(
      RouteComponent,
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

    const wanted = pages.slice(0, MaximumPages);
    if (!wanted.length) {
      lastOutcome = `routes=${routes.length} pages=0`;
      return routes;
    }

    const overrides = wanted.filter((page) => page.override === true).map(buildRoute);
    const additions = wanted.filter((page) => page.override !== true).map(buildRoute);
    lastOutcome = `routes=${routes.length} overrides=${overrides.length} additions=${additions.length}`;
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

    const kids = react.Children.toArray(children);
    if (!kids.length) return element;
    let changed = false;
    const next: unknown[] = [];
    for (const kid of kids) {
      const replacement = replaceRouteList(kid, depth + 1);
      changed ||= replacement !== kid;
      next.push(replacement);
    }
    return changed ? react.cloneElement(element, {}, ...next) : element;
  };

  const descend = (element, depth) => {
    if (depth > MaximumDescent || !react.isValidElement(element)) return element;
    const replaced = replaceRouteList(element, 0);
    if (replaced !== element) return replaced;

    const type: any = element.type;
    if (typeof type === "function" && !type.prototype?.isReactComponent) {
      let wrapper = descendCache.get(type);
      if (!wrapper) {
        wrapper = function SteamUiPageDescend(props) {
          return descend(type(props), 0);
        };
        descendCache.set(type, wrapper);
      }
      return react.createElement(
        wrapper,
        element.key === null ? element.props : { ...element.props, key: element.key },
      );
    }

    return element;
  };

  const resolve = () => {
    runtime = getWebpackRuntime("pages");
    const reactFactory = runtime.findUnique([
      "react.transitional.element",
      "useState",
      "cloneElement",
      "createElement",
    ]);
    if (!reactFactory) {
      lastError = "React runtime was not a unique match";
      return false;
    }
    react = runtime(reactFactory[0]);

    const backstack = runtime.findUnique([BackstackToken]);
    if (!backstack) {
      lastError = "router-backstack module was not a unique match";
      return false;
    }
    const backstackExports = runtime(backstack[0]);
    const routes = Object.keys(backstackExports).filter(
      (name) =>
        typeof backstackExports[name] === "function" &&
        RoutePattern.test(String(backstackExports[name])),
    );
    if (routes.length !== 1) {
      lastError = `Steam's Route export was ${routes.length ? "ambiguous" : "absent"}`;
      return false;
    }
    RouteComponent = backstackExports[routes[0]];

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
  const findRouterMemo = () => {
    const host = document.getElementById("root");
    if (!host) return null;
    const key = Object.keys(host).find((name) => name.startsWith("__reactContainer$"));
    if (!key) return null;

    const seen = new Set();
    let visited = 0;
    const walk = (node) => {
      if (!node || seen.has(node) || visited > MaximumNodesVisited) return null;
      seen.add(node);
      visited++;
      if (
        typeof node.type === "function" &&
        String(node.type).includes(RouterTokens[0]) &&
        node.elementType &&
        typeof node.elementType === "object" &&
        node.elementType.type === node.type
      ) {
        return node.elementType;
      }
      return walk(node.child) || walk(node.sibling);
    };
    return walk(host[key]);
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "page host resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

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

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }

    pages = [];
    descendCache.clear();
    const released = releaseMember(memo, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "page host release failed";
      return { ok: false, error: lastError };
    }

    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!memo,
    routeResolved: !!RouteComponent,
    claimed: memberClaimed(memo, "type", claimKeys),
    pages: pages.length,
    // What the last render actually saw. Everything above can be true while no page is reachable,
    // because insertion depends on finding the route list in the tree Steam rendered.
    routeCount: observedRoutes.length,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("pages", createPageHost());
