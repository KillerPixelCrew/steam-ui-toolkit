// Big Picture Home's carousel, fed from the libraries attached right now.
//
// Mapped from the September 2026 client beta's shipped bundle on 2026-09-11:
//
//   <route "/library/home">    one of the router switch's children
//     Home                     a module-local React.memo; source carries "HomeTabsActive"
//       ...RecentSection
//         Carousel             module-local React.memo; source carries "#Showcase_RecentGames"
//           games = on()       module-local hook: Steam's own mix, capped at 20 app ids
//           Background { games, refOnItemFocus }        hero art for the focused game
//           RecentGames { games, onItemFocus, ... }     plain function
//             BoxCarousel { games, overscan: games.length }
//               VirtualizedBox   react-virtualized Grid, overscanColumnCount = overscan ?? 3
//
// Two facts decide the whole design.
//
// The list is one array of app ids passed as `games` to both the background and the carousel. That
// array is the data boundary: replacing it there feeds Steam's own components rather than building
// cards, and the background, focus restore and featured tile all follow it. Nothing upstream of it
// is reachable — the hook, the carousel and Home are all module-local — so the Home memo is taken
// from the router's route list, and its `type` is claimed. That router renders in the Big Picture
// popup window's own React root, not in SharedJSContext's #root. The carousel element is found in
// what Home renders.
//
// The carousel is already virtualized, and Home defeats that: it passes `overscan: games.length`,
// so every tile in the list is mounted. At Steam's cap of 20 that is harmless; at a whole library it
// is the memory flood. The Play Next carousel uses the same component with no overscan and gets the
// component's own default of 3, which is what this gate restores.
//
// Ordering is done here rather than by the host, deliberately: the candidate set is every installed
// and every owned game with its play and purchase timestamps, which does not fit the bridge's
// 16 KiB payload bound for any real library. The host owns which libraries count and whether
// uninstalled games appear; this owns reading Steam's data and projecting it into the carousel.
//
// Reactivity comes from Steam's own mobx-react-lite `useObserver`, the hook Steam's `on()` is
// built on: the wrapper reads the three collections it draws from inside it, so Steam re-renders
// the carousel when one of them recomputes. The host's publication re-renders it through
// `useSyncExternalStore`. The list itself is rebuilt only when one of those inputs actually changed,
// never on the carousel's own focus re-renders.
function createHomeCarousel() {
  const patchId = "steam-ui.home-carousel";
  const claimKeys = {
    marker: "__steamUiHomeCarouselClaimed",
    original: "__steamUiHomeCarouselOriginal",
  } as const;

  const HomeTokens = ["HomeTabsActive", "HomeActiveTab"] as const;
  const CarouselTokens = ["#Showcase_RecentGames", "RecentGamesContainer"] as const;
  // mobx-react-lite's own startup check, present once in the client.
  const ObserverTokens = ["mobx-react-lite requires React with Hooks support"] as const;
  const KnownRoute = "/library/home";
  // Valve's collection ids, the string values of its own enum.
  const InstalledCollection = "local-install";
  const PurchasedCollection = "recent-purchased";
  const OwnedCollection = "my-games";

  // A pathological bound, not a cap on the feature: the grid virtualizes, so the list may be a
  // whole library.
  const MaximumItems = 5000;
  const MaximumDisconnected = 8192;
  const MaximumDescent = 12;
  const MaximumNodesVisited = 60000;
  const ContainerClass = "steam-ui-home-carousel";

  let runtime;
  let react;
  let home: any = null;
  let useObserver: any = null;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;

  // The host's instruction, replaced whole on each publication.
  let policy = { includeUninstalled: false, disconnected: new Set<number>(), revision: 0 };
  const listeners = new Set<() => void>();

  let lastOutcome = "never rendered";
  let lastReport = "";
  let cached: {
    key: unknown[];
    list: number[];
    css: string;
    counts: Record<string, number | boolean>;
  } | null = null;

  const carouselChecks = new WeakMap<object, boolean>();
  const carouselCache = new Map();
  const recentGamesCache = new Map();

  const notify = () => {
    for (const listener of listeners) {
      try {
        listener();
      } catch {}
    }
  };
  const subscribeLocal = (listener) => {
    listeners.add(listener);
    return () => listeners.delete(listener);
  };
  const readRevision = () => policy.revision;

  const keyed = (element, props) =>
    element.key === null ? props : { ...props, key: element.key };

  const collectionStore = () => (window as any).collectionStore;
  const appStore = () => (window as any).appStore;

  const collection = (id) => {
    try {
      return collectionStore()?.GetCollection(id) ?? null;
    } catch {
      return null;
    }
  };
  const appsOf = (source) => {
    try {
      const apps = source?.visibleApps;
      return Array.isArray(apps) ? apps : [];
    } catch {
      return [];
    }
  };
  const overviewOf = (appid) => {
    try {
      return appStore()?.GetAppOverviewByAppID(appid) ?? null;
    } catch {
      return null;
    }
  };

  const call = (overview, name) =>
    typeof overview?.[name] === "function" ? overview[name]() === true : false;
  const lastPlayed = (overview) =>
    Math.max(overview.rt_last_time_locally_played || 0, overview.rt_last_time_played || 0);
  // Steam's own installed test for its local-games collection. A game on a card that is not in the
  // reader reads false here, which is the same fact the library badge greys on.
  const isInstalled = (overview) =>
    overview.local_per_client_data?.installed === true || call(overview, "BIsShortcut");
  // The same exclusions Steam's own list applies, plus the host's disconnected libraries.
  const eligible = (overview) =>
    !!overview &&
    Number.isInteger(overview.appid) &&
    overview.appid > 0 &&
    !policy.disconnected.has(overview.appid) &&
    !call(overview, "BIsMusicAlbum") &&
    !(call(overview, "BIsApplicationOrTool") && !(overview.minutes_playtime_forever > 0));

  // The carousel's inputs, read inside Steam's observer so a recomputed collection re-renders it.
  const readInputs = () => ({
    installed: collection(InstalledCollection),
    purchased: collection(PurchasedCollection),
    owned: policy.includeUninstalled ? collection(OwnedCollection) : null,
  });

  // Builds the list, or returns the last one when none of its inputs changed.
  //
  // Order:
  //   1. Steam's own running-game prefix, when it has one: the running game and its separator.
  //   2. The most recently played installed game, pinned first, as Steam pins it.
  //   3. Installed games by last played, merged with unplayed recent purchases by purchase time,
  //      so a new purchase sits among the games played around when it was bought.
  //   4. Installed games never played, newest install first.
  //   5. With the host's permission, owned games that are not installed.
  // Ties fall back to app id, so the order is deterministic.
  const listFor = (steamGames: number[], inputs) => {
    const key = [policy.revision, steamGames, inputs.installed, inputs.purchased, inputs.owned];
    if (cached && cached.key.length === key.length && cached.key.every((value, index) => value === key[index])) {
      return cached;
    }

    const prefix = steamGames.length > 1 && steamGames[1] === 0 ? [steamGames[0], 0] : [];
    const placed = new Set<number>(prefix);

    const pool = new Map<number, any>();
    for (const overview of appsOf(inputs.installed)) {
      if (eligible(overview) && isInstalled(overview)) pool.set(overview.appid, overview);
    }
    // Steam's own list carries played shortcuts, which its installed collection leaves out.
    for (const appid of steamGames) {
      if (!appid || placed.has(appid) || pool.has(appid)) continue;
      const overview = overviewOf(appid);
      if (eligible(overview) && isInstalled(overview)) pool.set(appid, overview);
    }

    const purchases = new Map<number, any>();
    for (const overview of appsOf(inputs.purchased)) {
      if (eligible(overview) && lastPlayed(overview) === 0) purchases.set(overview.appid, overview);
    }

    const timed: { appid: number; time: number; purchase: boolean }[] = [];
    const unplayed: { appid: number; time: number }[] = [];
    for (const [appid, overview] of pool) {
      if (placed.has(appid) || purchases.has(appid)) continue;
      const time = lastPlayed(overview);
      if (time > 0) timed.push({ appid, time, purchase: false });
      else unplayed.push({ appid, time: overview.rt_last_time_played_or_installed || 0 });
    }
    for (const [appid, overview] of purchases) {
      if (placed.has(appid)) continue;
      timed.push({ appid, time: overview.rt_purchased_time || 0, purchase: true });
    }
    timed.sort((left, right) => right.time - left.time || left.appid - right.appid);
    const pinned = timed.findIndex((entry) => !entry.purchase);
    if (pinned > 0) timed.unshift(timed.splice(pinned, 1)[0]);
    unplayed.sort((left, right) => right.time - left.time || left.appid - right.appid);

    const uninstalled: { appid: number; time: number; bought: number }[] = [];
    if (policy.includeUninstalled) {
      for (const overview of appsOf(inputs.owned)) {
        if (!eligible(overview) || isInstalled(overview)) continue;
        if (placed.has(overview.appid) || pool.has(overview.appid) || purchases.has(overview.appid)) {
          continue;
        }
        uninstalled.push({
          appid: overview.appid,
          time: lastPlayed(overview),
          bought: overview.rt_purchased_time || 0,
        });
      }
      uninstalled.sort(
        (left, right) => right.time - left.time || right.bought - left.bought || left.appid - right.appid,
      );
    }

    let list = [
      ...prefix,
      ...timed.map((entry) => entry.appid),
      ...unplayed.map((entry) => entry.appid),
      ...uninstalled.map((entry) => entry.appid),
    ].slice(0, MaximumItems);
    // Nothing on the attached libraries is still an answer, but an empty carousel is not one the
    // page can draw: Steam's box carousel renders nothing for an empty list. Steam's own list stands
    // in rather than leaving Home blank.
    const fellBack = list.length === prefix.length && steamGames.length > prefix.length;
    if (fellBack) list = steamGames.slice();

    // Grey means not installed, whichever section put the game there: an unplayed purchase that is
    // still downloading reads the same as an owned game that was never installed.
    const grey = fellBack
      ? []
      : list.filter((appid) => appid > 0 && !pool.has(appid) && !isInstalledId(appid));
    const css = grey.length
      ? `${grey.map((appid) => `.${ContainerClass} [data-id="${appid}"] img`).join(",")}` +
        "{filter:grayscale(1);opacity:.55}"
      : "";

    const counts = {
      items: list.length,
      purchases: timed.filter((entry) => entry.purchase).length,
      installed: timed.filter((entry) => !entry.purchase).length + unplayed.length,
      uninstalled: uninstalled.length,
      excluded: policy.disconnected.size,
      tracking: !!useObserver,
      fallback: fellBack,
    };
    cached = { key, list, css, counts };
    report(counts);
    return cached;
  };

  const isInstalledId = (appid) => {
    const overview = overviewOf(appid);
    return !!overview && isInstalled(overview);
  };

  // Tells the host what the carousel holds, once per change. This is what the host's log reads,
  // so the carousel can be checked without attaching a debugger to Steam.
  const report = (counts) => {
    const text = JSON.stringify(counts);
    if (text === lastReport) return;
    lastReport = text;
    request(patchId, "report", counts).catch(() => {});
  };

  // Replaces the carousel's list in what the carousel component rendered: the background and the
  // box carousel both receive `games`, told apart by the one prop each takes that the other does
  // not. The box carousel's own component is wrapped so its overscan can be bounded.
  const retarget = (tree, inputs) => {
    // Asserted rather than annotated: assigned inside the walk below, which control-flow narrowing
    // cannot see, so a plain `= null` would leave it typed as never after the walk.
    let result = null as ReturnType<typeof listFor> | null;
    let background = 0;
    let carousels = 0;
    const replace = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      const props = element.props;
      if (props && Array.isArray(props.games)) {
        result ??= listFor(props.games, inputs);
        if (props.refOnItemFocus !== undefined) {
          background++;
          return react.cloneElement(element, { games: result.list });
        }
        if (typeof props.onItemFocus === "function" || "showFeaturedItem" in props) {
          carousels++;
          return react.createElement(
            recentGamesFor(element.type),
            keyed(element, { ...props, games: result.list }),
          );
        }
      }
      const kids = react.Children.toArray(props?.children);
      if (!kids.length) return element;
      let changed = false;
      const next: unknown[] = [];
      for (const kid of kids) {
        const replacement = replace(kid, depth + 1);
        changed ||= replacement !== kid;
        next.push(replacement);
      }
      return changed ? react.cloneElement(element, {}, ...next) : element;
    };
    const output = replace(tree, 0);
    lastOutcome = result
      ? `background=${background} carousel=${carousels} items=${result.list.length}`
      : "no games list in the carousel's output";
    return output;
  };

  // The box carousel's own function component, rendered here so its overscan can be put back to
  // the component's default, inside a container that carries the grey rules. The container is
  // always present, so toggling the rules never changes the tree's shape and remounts the carousel.
  const recentGamesFor = (type) => {
    if (typeof type !== "function" || type.prototype?.isReactComponent) return type;
    let wrapped = recentGamesCache.get(type);
    if (wrapped) return wrapped;
    wrapped = function SteamUiRecentGames(props) {
      const rendered = type(props);
      const bounded =
        react.isValidElement(rendered) && typeof rendered.props?.overscan === "number"
          ? react.cloneElement(rendered, { overscan: undefined })
          : rendered;
      return react.createElement(
        "div",
        { className: ContainerClass, style: { display: "contents" } },
        react.createElement("style", { key: "steam-ui-home-carousel-grey" }, cached?.css ?? ""),
        bounded,
      );
    };
    recentGamesCache.set(type, wrapped);
    return wrapped;
  };

  const isCarousel = (type) => {
    if (!type || typeof type !== "object") return false;
    let known = carouselChecks.get(type);
    if (known === undefined) {
      const inner = type.$$typeof === Symbol.for("react.memo") ? type.type : null;
      const source = typeof inner === "function" ? String(inner) : "";
      known = CarouselTokens.every((token) => source.includes(token));
      carouselChecks.set(type, known);
    }
    return known;
  };

  // The carousel memo, replaced by a memo of our own with the same comparison, so the component
  // keeps the render behavior Steam gave it.
  const carouselFor = (type) => {
    let wrapped = carouselCache.get(type);
    if (wrapped) return wrapped;
    const inner = type.type;
    const tracked = useObserver;
    const Carousel = function SteamUiHomeCarousel(props) {
      react.useSyncExternalStore(subscribeLocal, readRevision);
      const inputs = tracked ? tracked(readInputs, "SteamUiHomeCarousel") : readInputs();
      const tree = inner(props);
      return installed ? retarget(tree, inputs) : tree;
    };
    wrapped = react.memo(Carousel, type.compare ?? undefined);
    carouselCache.set(type, wrapped);
    return wrapped;
  };

  // Walks what Home rendered, by props alone, to the carousel element.
  const decorate = (element, depth) => {
    if (depth > MaximumDescent || !react.isValidElement(element)) return element;
    if (isCarousel(element.type)) {
      return react.createElement(carouselFor(element.type), keyed(element, element.props));
    }
    const kids = react.Children.toArray(element.props?.children);
    if (!kids.length) return element;
    let changed = false;
    const next: unknown[] = [];
    for (const kid of kids) {
      const replacement = decorate(kid, depth + 1);
      changed ||= replacement !== kid;
      next.push(replacement);
    }
    return changed ? react.cloneElement(element, {}, ...next) : element;
  };

  // Home by its own source, or a Home an earlier injection already claimed: the claim replaces
  // `type`, so requiring the source alone would make a successful apply fail its next resolution.
  const isHome = (type) =>
    !!type &&
    typeof type === "object" &&
    type.$$typeof === Symbol.for("react.memo") &&
    typeof type.type === "function" &&
    (type.type[claimKeys.marker] === true ||
      HomeTokens.every((token) => String(type.type).includes(token)));

  const containerFiber = (element) => {
    if (!element) return null;
    const key = Object.keys(element).find((name) => name.startsWith("__reactContainer$"));
    return key ? element[key] : null;
  };

  // The React roots Home can be rendered under. Big Picture's router is not in SharedJSContext's own
  // #root: the Big Picture window is a popup whose document holds a `popup_target` root of its own,
  // so that window is asked first — through Steam's window store, then every popup Steam's popup
  // manager holds — and SharedJSContext's root last. On the reference client the first probe, which
  // searched #root alone, found the Home module, the stores and the observer and no Home.
  const reactRoots = () => {
    const roots: any[] = [];
    const add = (doc) => {
      try {
        const fiber =
          containerFiber(doc?.getElementById("popup_target")) ??
          containerFiber(doc?.getElementById("root"));
        if (fiber && !roots.includes(fiber)) roots.push(fiber);
      } catch {}
    };
    const win = window as any;
    add(win.SteamUIStore?.WindowStore?.GamepadUIMainWindowInstance?.BrowserWindow?.document);
    try {
      for (const popup of win.g_PopupManager?.m_mapPopups?.values?.() ?? []) {
        add(popup?.window?.document);
      }
    } catch {}
    add(document);
    return roots;
  };

  // Home from the router's route list. The list is found by content — the array holding a route for
  // /library/home — and the page element under that route names the Home memo. Bounded and
  // read-only; the memo is one object whichever window renders it, so claiming it reaches them all.
  const findHome = () => {
    let visited = 0;
    let found = null;
    const stack: any[] = reactRoots();
    while (stack.length && !found && visited < MaximumNodesVisited) {
      const node = stack.pop();
      if (!node) continue;
      visited++;
      const children = node.memoizedProps?.children;
      if (Array.isArray(children) && children.length > 2 && children.length < 512) {
        const route = children.find(
          (child) => react.isValidElement(child) && child.props?.path === KnownRoute,
        );
        const page = route?.props?.children;
        if (react.isValidElement(page) && isHome(page.type)) found = page.type;
      }
      stack.push(node.sibling, node.child);
    }
    return found;
  };

  const resolve = () => {
    runtime = getWebpackRuntime("home-carousel");
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
    if (typeof react.useSyncExternalStore !== "function" || typeof react.memo !== "function") {
      lastError = "React runtime lacks useSyncExternalStore or memo";
      return false;
    }

    if (!runtime.findUnique(["HomeTabsActive", CarouselTokens[0]])) {
      lastError = "Home module was not a unique match";
      return false;
    }
    if (
      typeof collectionStore()?.GetCollection !== "function" ||
      typeof appStore()?.GetAppOverviewByAppID !== "function"
    ) {
      lastError = "collection or app store is unavailable";
      return false;
    }

    // Wanted, not required: without it the carousel still follows the host and Steam's own list,
    // and an install elsewhere shows on its next render. `status.tracking` says which.
    useObserver = null;
    const observer = runtime.findUnique([...ObserverTokens]);
    if (observer) {
      const exports = runtime(observer[0]);
      const hooks = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return typeof value === "function" && value.length === 2 && String(value).includes('"observed"');
      });
      if (hooks.length === 1) useObserver = exports[hooks[0]];
    }

    home = findHome();
    if (!home) {
      lastError = "Home was not found in the router's route list";
      return false;
    }
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    try {
      if (!resolve()) return { ok: false, error: lastError };
    } catch (error) {
      lastError = "home carousel resolution failed: " + String(error);
      return { ok: false, error: lastError };
    }

    // Home renders through this memo wherever the router draws it, and a Home already on screen
    // picks the claim up when it next mounts.
    const claim = claimMember(home, "type", claimKeys, (original: any) => {
      if (typeof original !== "function") return original;
      return function SteamUiHome(props) {
        const tree = original(props);
        return installed ? decorate(tree, 0) : tree;
      };
    });
    if (!claim.ok) {
      lastError = claim.error;
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (published) => {
      const ids = Array.isArray(published?.disconnectedAppIds) ? published.disconnectedAppIds : [];
      const disconnected = new Set<number>();
      for (const appid of ids.slice(0, MaximumDisconnected)) {
        if (Number.isInteger(appid) && appid > 0) disconnected.add(appid);
      }
      policy = {
        includeUninstalled: published?.includeUninstalled === true,
        disconnected,
        revision: policy.revision + 1,
      };
      notify();
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
    // A carousel on screen re-renders and hands back Steam's own list and overscan.
    policy = { includeUninstalled: false, disconnected: new Set<number>(), revision: policy.revision + 1 };
    notify();
    cached = null;
    lastReport = "";
    carouselCache.clear();
    recentGamesCache.clear();
    const released = releaseMember(home, "type", claimKeys);
    if (!released.ok) {
      lastError = released.error ?? "home carousel release failed";
      return { ok: false, error: lastError };
    }
    lastOutcome = "removed";
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    resolved: !!home,
    claimed: memberClaimed(home, "type", claimKeys),
    tracking: !!useObserver,
    includeUninstalled: policy.includeUninstalled,
    disconnected: policy.disconnected.size,
    counts: cached?.counts ?? null,
    lastOutcome,
    lastError,
  });

  return { install, remove, status };
}

registerGate("homeCarousel", createHomeCarousel());
