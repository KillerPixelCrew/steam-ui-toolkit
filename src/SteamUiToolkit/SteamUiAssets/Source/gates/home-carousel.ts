/**
 * Replaces only the matched home-carousel collection by claiming Home's memo.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createHomeCarousel() {
    const patchId = "steam-ui.home-carousel";
    const claimKeys = {
        marker: "__steamUiHomeCarouselClaimed",
        original: "__steamUiHomeCarouselOriginal",
    } as const;

    const HomeTokens = ["HomeTabsActive", "HomeActiveTab"] as const;
    const CarouselTokens = ["#Showcase_RecentGames", "RecentGamesContainer"] as const;
    const KnownRoute = "/library/home";
    // Valve's collection ids, the string values of its own enum.
    const InstalledCollection = "local-install";
    const PurchasedCollection = "recent-purchased";
    const OwnedCollection = "my-games";

    const MaximumDescent = 12;
    // Fiber reads are cheap and the walk runs once per install; the bound stops a cyclic or runaway
    // tree, not a legitimate search through a window with a whole library mounted.
    const MaximumNodesVisited = 250000;
    const ContainerClass = "steam-ui-home-carousel";

    let react;
    let home: any = null;
    let useObserver: any = null;

    // The host's instruction, replaced whole on each publication.
    let policy = {includeUninstalled: false, disconnected: new Set<number>()};
    const local = createLocalStore();

    let lastOutcome = "never rendered";
    let lastReport = "";
    let lastSearch = { roots: 0, visited: 0, homeRoutes: 0, page: "", mounted: 0 };
    let cached: {
        key: unknown[];
        list: number[];
        // How many games Steam's own carousel holds, which is how far ahead of focus it keeps tiles.
        window: number;
        css: string;
        counts: Record<string, number | boolean>;
    } | null = null;

    const carouselChecks = new WeakMap<object, boolean>();
    const carouselCache = new Map();
    const recentGamesCache = new Map();

    const collectionStore = () => (window as any).collectionStore;
    const appStore = () => (window as any).appStore;

    const collection = (id) => {
        try {
            return collectionStore()?.GetCollection(id) ?? null;
        } catch {
            return null;
        }
    };
    // One empty list for every absent input, so an absent input keys the list the same each render.
    const NoApps: readonly any[] = Object.freeze([]);
    const appsOf = (source) => {
        try {
            const apps = source?.visibleApps;
            return Array.isArray(apps) ? apps : NoApps;
        } catch {
            return NoApps;
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

    // The carousel's inputs: each collection's apps, read inside Steam's observer so a recomputed
    // collection re-renders the carousel. The arrays, not the collections, are what the list is keyed
    // on: a collection object never changes identity, so an install or uninstall left the list as it
    // was until Steam's own recent list happened to change.
    const readInputs = () => ({
        installed: appsOf(collection(InstalledCollection)),
        purchased: appsOf(collection(PurchasedCollection)),
        owned: policy.includeUninstalled ? appsOf(collection(OwnedCollection)) : NoApps,
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
        const key = [local.revision(), steamGames, inputs.installed, inputs.purchased, inputs.owned];
        if (cached && cached.key.length === key.length && cached.key.every((value, index) => value === key[index])) {
            return cached;
        }

        const prefix = steamGames.length > 1 && steamGames[1] === 0 ? [steamGames[0], 0] : [];
        const placed = new Set<number>(prefix);

        const pool = new Map<number, any>();
        for (const overview of inputs.installed) {
            if (eligible(overview) && isInstalled(overview)) pool.set(overview.appid, overview);
        }
        // Steam's own list carries played shortcuts, which its installed collection leaves out.
        for (const appid of steamGames) {
            if (!appid || placed.has(appid) || pool.has(appid)) continue;
            const overview = overviewOf(appid);
            if (eligible(overview) && isInstalled(overview)) pool.set(appid, overview);
        }

        const purchases = new Map<number, any>();
        for (const overview of inputs.purchased) {
            if (eligible(overview) && lastPlayed(overview) === 0) purchases.set(overview.appid, overview);
        }

        const timed: { appid: number; time: number; purchase: boolean }[] = [];
        const unplayed: { appid: number; time: number }[] = [];
        for (const [appid, overview] of pool) {
            if (placed.has(appid) || purchases.has(appid)) continue;
            const time = lastPlayed(overview);
            if (time > 0) timed.push({appid, time, purchase: false});
            else unplayed.push({appid, time: overview.rt_last_time_played_or_installed || 0});
        }
        for (const [appid, overview] of purchases) {
            if (placed.has(appid)) continue;
            timed.push({appid, time: overview.rt_purchased_time || 0, purchase: true});
        }
        timed.sort((left, right) => right.time - left.time || left.appid - right.appid);
        const pinned = timed.findIndex((entry) => !entry.purchase);
        if (pinned > 0) timed.unshift(timed.splice(pinned, 1)[0]);
        unplayed.sort((left, right) => right.time - left.time || left.appid - right.appid);

        const uninstalled: { appid: number; time: number; bought: number }[] = [];
        if (policy.includeUninstalled) {
            for (const overview of inputs.owned) {
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
        ];
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
        cached = {key, list, window: steamGames.length, css, counts};
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
        request(patchId, "report", counts).catch(() => {
        });
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
                    return react.cloneElement(element, {games: result.list});
                }
                if (typeof props.onItemFocus === "function" || "showFeaturedItem" in props) {
                    carousels++;
                    return react.createElement(
                        recentGamesFor(element.type),
                        keyed(element, {...props, games: result.list}),
                    );
                }
            }
            return mapChildren(react, element, (kid) => replace(kid, depth + 1));
        };
        const output = replace(tree, 0);
        lastOutcome = result
            ? `background=${background} carousel=${carousels} items=${result.list.length}`
            : "no games list in the carousel's output";
        return output;
    };

    // The box carousel's own function component, rendered here so its overscan stays what Steam's own
    // list gives it, inside a container that carries the grey rules. The container is always present,
    // so toggling the rules never changes the tree's shape and remounts the carousel.
    //
    // Steam sets the overscan to its list's length, at most 20, so every tile of its own carousel is
    // mounted and loading from the start. Given the whole library, that length would mount every
    // game at once; the component's default of 3 mounted only the tiles in view, so each image
    // started loading when its tile scrolled in, and again when it came back, which is slow wherever
    // the art is not in Steam's local cache (2026-09-29, 246 games on a handheld). Steam's own length
    // keeps the first tiles loading at once and the rest loading ahead of focus.
    const recentGamesFor = (type) => {
        if (typeof type !== "function" || type.prototype?.isReactComponent) return type;
        let wrapped = recentGamesCache.get(type);
        if (wrapped) return wrapped;
        wrapped = function SteamUiRecentGames(props) {
            const rendered = type(props);
            const bounded =
                react.isValidElement(rendered) && typeof rendered.props?.overscan === "number"
                    ? react.cloneElement(rendered, {overscan: cached?.window || undefined})
                    : rendered;
            return react.createElement(
                "div",
                {className: ContainerClass, style: {display: "contents"}},
                react.createElement("style", {key: "steam-ui-home-carousel-grey"}, cached?.css ?? ""),
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
            const inner = type.$$typeof === ReactMemoType ? type.type : null;
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
            react.useSyncExternalStore(local.subscribe, local.revision);
            const inputs = tracked ? tracked(readInputs, "SteamUiHomeCarousel") : readInputs();
            const tree = inner(props);
            const output = gate.installed() ? retarget(tree, inputs) : tree;
            // Reported after the render rather than from it; `report` drops a repeat of the same counts.
            const counts = gate.installed() ? cached?.counts : null;
            react.useEffect(() => {
                if (counts) report(counts);
            }, [counts]);
            return output;
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
        return mapChildren(react, element, (kid) => decorate(kid, depth + 1));
    };

    // Home by its own source, or a Home an earlier injection already claimed: the claim replaces
    // `type`, so requiring the source alone would make a successful apply fail its next resolution.
    const isHome = (type) =>
        !!type &&
        typeof type === "object" &&
        type.$$typeof === ReactMemoType &&
        sourceMatches(unclaimedValue(type.type, claimKeys), HomeTokens);

    // Home from the router's route list. The list is found by content — the array holding a route for
    // /library/home — and the page element under that route names the Home memo. Bounded and
    // read-only; the memo is one object whichever window renders it, so claiming it reaches them all.
    // What the walk saw is answered with it, so a miss on a new client says which assumption failed
    // without anyone attaching to Steam.
    const findHome = (react) => {
        let found = null;
        const roots = reactRootFibers();
        const search = { roots: roots.length, visited: 0, homeRoutes: 0, page: "", mounted: 0 };
        const walk = walkFibers(roots, MaximumNodesVisited, (node) => {
            // A Fragment's fiber holds its children array as the props themselves.
            const props = node.memoizedProps;
            const children = Array.isArray(props) ? props : props?.children;
            if (!isSteamRouteList(react, children, KnownRoute)) return;
            const route = children.find(
                (child) => react.isValidElement(child) && child.props?.path === KnownRoute,
            );
            if (!route) return;
            search.homeRoutes++;
            const type = route.props?.children?.type;
            search.page = !type ? "none" : typeof type === "function" ? "function" : String(type.$$typeof);
            if (isHome(type)) found = type;
            return !!found;
        });
        search.visited = walk.visited;
        // Homes already on screen: Big Picture starts on Home, and install adopts each of them.
        if (found) {
            walkFibers(roots, MaximumNodesVisited, (node) => {
                if (node.elementType === found) search.mounted++;
            });
        }
        return { home: found, search };
    };

    // Pure: everything the claim needs, or which fact is missing. A Home that is not mounted is Big
    // Picture closed or still building its tree, which is not ready; a client without the module, the
    // stores or a claimable Home is incompatible.
    const find = (runtime) => {
        const react = resolveReact(runtime);
        if (!react) return gateIncompatible("React runtime was not a unique match");
        if (
            typeof react.useSyncExternalStore !== "function" ||
            typeof react.useEffect !== "function" ||
            typeof react.memo !== "function"
        ) {
            return gateIncompatible("React runtime lacks useSyncExternalStore, useEffect or memo");
        }
        if (!runtime.findUnique(["HomeTabsActive", CarouselTokens[0]])) {
            return gateIncompatible("Home module was not a unique match", { homeModule: 0 });
        }
        // Wanted, not required: without it the carousel still follows the host and Steam's own list,
        // and an install elsewhere shows on its next render. `status.tracking` says which.
        const tracking = findUseObserver(runtime);
        const stores =
            typeof collectionStore()?.GetCollection === "function" &&
            typeof appStore()?.GetAppOverviewByAppID === "function";
        const { home, search } = findHome(react);
        const facts = { homeModule: 1, homeFound: home ? 1 : 0, stores, observer: tracking ? 1 : 0, ...search };
        if (!stores) return gateNotReady("collection or app store is unavailable", facts);
        if (!home) {
            const where =
                `roots=${search.roots} visited=${search.visited} homeRoutes=${search.homeRoutes} ` +
                `page=${search.page || "-"}`;
            return search.homeRoutes === 0
                ? gateNotReady(`the router is not mounted (${where})`, facts)
                : gateIncompatible(`the /library/home route does not draw Home (${where})`, facts);
        }
        const descriptor = Object.getOwnPropertyDescriptor(home, "type");
        if (
            !memberClaimed(home, "type", claimKeys) &&
            !(descriptor?.writable === true && descriptor.configurable === true)
        ) {
            return gateIncompatible("Home's type is not writable and configurable", facts);
        }
        return gateReady({ react, home, useObserver: tracking, search }, facts);
    };

    let component: ClaimedComponent | null = null;
    const gate = defineGate({
        name: "home-carousel",
        find,
        bind: (handles, ledger) => {
            react = handles.react;
            home = handles.home;
            useObserver = handles.useObserver;
            lastSearch = handles.search;
            // Home renders through this memo wherever the router draws it. The wrapper adds no hooks,
            // so a Home already on screen is adopted into it without remounting.
            const claimed = ledger.component({
                host: home,
                keys: claimKeys,
                bound: MaximumNodesVisited,
                wrap: (original) =>
                    function SteamUiHome(props) {
                        const tree = original(props);
                        return gate.installed() ? decorate(tree, 0) : tree;
                    },
            });
            if (!claimed.ok) return claimed.error;
            component = claimed;
            ledger.subscribe(patchId, (published) => {
                const ids = Array.isArray(published?.disconnectedAppIds) ? published.disconnectedAppIds : [];
                const disconnected = new Set<number>();
                for (const appid of ids) {
                    if (Number.isInteger(appid) && appid > 0) disconnected.add(appid);
                }
                policy = {
                    includeUninstalled: published?.includeUninstalled === true,
                    disconnected,
                };
                local.changed();
            });
            return { adopted: claimed.status().adopted };
        },
        // A carousel on screen re-renders and hands back Steam's own list and overscan.
        unbind: () => {
            component = null;
            policy = { includeUninstalled: false, disconnected: new Set<number>() };
            local.changed();
            cached = null;
            lastReport = "";
            carouselCache.clear();
            recentGamesCache.clear();
            lastOutcome = "removed";
        },
        status: () => ({
            claimed: memberClaimed(home, "type", claimKeys),
            tracking: !!useObserver,
            includeUninstalled: policy.includeUninstalled,
            disconnected: policy.disconnected.size,
            counts: cached?.counts ?? null,
            search: lastSearch,
            // Homes on screen at install, and how many of them still draw something other than the
            // claim: an adoption whose render is pending, or a type React reset underneath us.
            mounted: component?.status() ?? { adopted: 0, scheduled: false, stale: 0 },
            lastOutcome,
        }),
    });
    return gate;
}

registerGate("homeCarousel", createHomeCarousel());
