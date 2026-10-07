// A library badge on every library tile: the name of the Steam library that holds the game, drawn
// beside Valve's own Steam Input badge in the tile's icon row.
//
// Mapped against the September 2026 client beta on 2026-09-11. One module carries the library tile
// and everything it draws:
//
//   tile  an exported React.memo (mobx observer): the app tile, props { app, bFeatured, context, … }
//         rendered by Home's carousel, the library grid and three other callers, all through the
//         export, and keyed in Steam's focus tree as `appportrait_<appid>`
//     Focusable, navKey "appportrait_<appid>"
//       hover wrapper
//         div.LibraryItemOverlayOuterArea > div.LibraryItemOverlayInnerArea > div.LibraryBottomItems
//           div.LibraryItemIcons     the icon row: justify-content space-between
//             badge                  exported: the Steam Input badge, props { overview }   <- anchor
//
// The badge is exported but the tile calls it by its module-local name, so claiming the badge export
// changes nothing the tile draws. The claim is on the tile memo's `type` instead, and the badge is
// found in what the tile RENDERS by element type — identity with the export, never a class name or
// a minified name — and replaced by a row of two: this badge, then Valve's. The tile is drawn
// through the export by every caller, so one claim reaches the carousel and the grid alike, and
// the badge inherits the row's own visibility: Valve shows the icon row on the focused tile only,
// and this badge appears and disappears with it.
//
// The badge names the library and says whether the game is installed, by colour: green when the
// game is installed, grey when it is not — which is what a card being disconnected amounts to. The
// text is the library's name alone, and the host names every library. A game that no published
// library holds gets no badge, because there is no library to name.
//
// Big Art Mode is Steam's own `library_home_big_art` client setting, read from the settings store
// the Home component itself reads it from. The badge is tile-relative and does not care, but a
// consumer may: the gate reports the mode to the host through `homeLayout` when it first resolves
// and whenever a tile render sees it change, and carries it in `status` for verification.
// The host's published libraries, read once for every gate that names them, indexed by app id. A
// malformed entry is skipped rather than failing the whole reading.
const readLibraryBadgeState = (state) => {
    const libraries = new Map<number, { name: string; connected: boolean }>();
    const shortcuts = new Map<
        number,
        { appId: number; available: boolean; location: string; reason: string }
    >();
    const published = Array.isArray(state?.libraries) ? state.libraries : [];
    let count = 0;
    for (const entry of published) {
        if (!entry || typeof entry.name !== "string" || !Array.isArray(entry.appIds)) continue;
        count++;
        const library = {
            name: entry.name,
            connected: entry.connected === true,
        };
        for (const appid of entry.appIds) {
            if (typeof appid !== "number" || !Number.isInteger(appid) || appid <= 0) continue;
            libraries.set(appid, library);
        }
    }
    for (const entry of Array.isArray(state?.shortcuts) ? state.shortcuts : []) {
        if (
            !Number.isInteger(entry?.appId) ||
            entry.appId <= 0 ||
            entry.appId > 0xffffffff ||
            typeof entry.available !== "boolean" ||
            typeof entry.location !== "string" ||
            typeof entry.reason !== "string"
        )
            continue;
        shortcuts.set(entry.appId, entry);
    }
    return { libraries, shortcuts, count };
};

// The library to name for one app overview, or null when there is none. Steam's own installed flag is
// the authority on installed; a disconnected card's games are not installed by Steam's reckoning. The
// published connection stands in only where the overview cannot say. A game that no published library
// holds has no library to name.
const libraryForOverview = (overview, reading: ReturnType<typeof readLibraryBadgeState>) => {
    const appid = typeof overview?.appid === "number" ? overview.appid : null;
    if (appid === null) return null;
    const library = reading.libraries.get(appid);
    const shortcut = reading.shortcuts.get(appid);
    const installed =
        typeof overview.installed === "boolean"
            ? overview.installed
            : (library?.connected ?? false);
    if (!library && !shortcut) return null;
    return {
        appId: appid,
        name: shortcut?.location || library?.name || "Game storage",
        installed: shortcut?.available ?? installed,
        reason: shortcut?.available === false ? shortcut.reason : "",
        managed: !!shortcut,
    };
};

// A mounted tile or stat subscribes to its app alone. Publications update only the apps whose
// visible badge changed, without walking Steam's fibers or repainting the rest of the library.
const createLibraryBadgeReading = () => {
    let current = readLibraryBadgeState(null);
    const stores = new Map<number, ReturnType<typeof createLocalStore>>();
    const storeFor = (appId: number) => {
        let store = stores.get(appId);
        if (!store) {
            store = createLocalStore();
            stores.set(appId, store);
        }
        return store;
    };
    return {
        read: () => current,
        storeFor,
        publish: (state) => {
            const previous = current;
            current = readLibraryBadgeState(state);
            for (const [appId, store] of stores) {
                const before = libraryForOverview({ appid: appId }, previous);
                const after = libraryForOverview({ appid: appId }, current);
                if (
                    before?.name !== after?.name ||
                    before?.installed !== after?.installed ||
                    before?.reason !== after?.reason ||
                    before?.managed !== after?.managed
                ) {
                    store.changed();
                }
            }
            return current;
        },
        changed: () => {
            for (const store of stores.values()) store.changed();
        },
    };
};

function createLibraryBadge() {
    const patchId = "steam-ui.library-badge";
    const claimKeys = {
        marker: "__steamUiLibraryBadgeClaimed",
        original: "__steamUiLibraryBadgeOriginal",
    } as const;

    // The module that owns the tile. `appportrait_` is the tile's own focus key and occurs in exactly
    // one module; `ControllerSupportIcon` also names the stylesheet module, so the pair is what is
    // unique. Neither is a localized string or a generated class.
    const TileTokens = ["ControllerSupportIcon", "appportrait_"] as const;
    // The tile stylesheet's class map: Valve's own names for the icon row and the Steam Input badge,
    // mapped to whatever hashes this build emitted. Read by name, so the hashes are never written
    // down here. The badge's visibility comes from Valve's rules on the badge class — hidden until
    // the tile is focused or hovered — and the row wearing that class inherits them.
    const ClassMapTokens = SteamLibraryClassTokens;
    const BigArtSetting = "library_home_big_art";

    const MaximumDescent = 12;

    let runtime;
    let react;
    let tile: any = null;
    let badge: any = null;
    let settings: any = null;
    let classes: { row: string; icon: string } | null = null;
    let installed = false;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;
    let reportedBigArt: boolean | null = null;

    // The host's published libraries, replaced whole on each publication and indexed by app id.
    const local = createLibraryBadgeReading();
    let reading = local.read();
    const mounted = createMountedAdoption();

    // What the last tile render actually did, because a claimed tile can render exactly what Valve
    // shipped when the badge anchor is not in its tree. Kept as counts and the reading that render
    // saw, so a render does no string work; status builds the text.
    let outcome: "never rendered" | "rendered" | "removed" = "never rendered";
    let renderedReading = reading;
    let placed = 0;
    let unanchored = 0;

    const tileCache = new Map();

    const readBigArt = () => {
        try {
            const value = settings?.clientSettings?.[BigArtSetting];
            return typeof value === "boolean" ? value : null;
        } catch {
            return null;
        }
    };

    // Tells the host once per change, never once per render: the setting is read on every tile
    // render, which is how a toggle is noticed without a subscription into Valve's store.
    const reportBigArt = () => {
        const current = readBigArt();
        if (current === null || current === reportedBigArt) return;
        reportedBigArt = current;
        request(patchId, "homeLayout", { bigArt: current }).catch(() => {});
    };

    const badgeStyle = (installedNow) => ({
        display: "inline-block",
        maxWidth: "180px",
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
        padding: "2px 8px",
        borderRadius: "4px",
        fontSize: "13px",
        lineHeight: "17px",
        fontWeight: 600,
        letterSpacing: "0.2px",
        color: "#f2f4f5",
        background: installedNow ? "rgba(76, 160, 54, 0.92)" : "rgba(110, 115, 120, 0.85)",
    });

    // The badge for one tile, or null when there is no library to name.
    const renderBadge = (overview) => {
        // Grey when not installed, which is exactly what a disconnected card amounts to.
        const library = libraryForOverview(overview, reading);
        if (!library) return null;
        return react.createElement(
            "span",
            {
                key: "steam-ui-library-badge",
                className: "steam-ui-library-badge",
                style: badgeStyle(library.installed),
                title: library.reason || library.name,
                "aria-label": library.name,
            },
            library.name,
        );
    };

    // Replaces Valve's badge element with a right-aligned row of ours and Valve's. The icon row is
    // `space-between` with Valve's badge pushed to its end by an auto margin, so a bare sibling would
    // land at the row's far left; one flex box holding both keeps this badge immediately left of the
    // icon wherever the row puts it.
    //
    // The box wears two of Valve's own classes. The badge class carries the visibility rule — opacity
    // zero until the tile is focused or hovered — and the end-of-row margin, so the pair appears and
    // disappears with Valve's icon instead of sitting on every tile; its size, padding and pill
    // background are overridden inline because they are drawn for a 34-pixel glyph. The row class
    // keeps Valve's icon a direct child of a row, which is what its pill background is written
    // against. Without the class map the box is plain and always visible, and status says so.
    const withBadge = (element) => {
        const ours = renderBadge(element.props?.overview);
        const additions = pluginFrontendElements("library", react, {
            overview: element.props?.overview,
        });
        if (!ours && !additions.length) return element;
        const style: Record<string, unknown> = {
            display: "flex",
            alignItems: "center",
            gap: "8px",
            marginInlineStart: "auto",
        };
        let className: string | undefined;
        if (classes) {
            className = `${classes.row} ${classes.icon}`;
            Object.assign(style, {
                justifyContent: "flex-end",
                width: "auto",
                maxWidth: "none",
                maxHeight: "none",
                padding: 0,
                borderRadius: 0,
                backgroundColor: "transparent",
            });
        }
        return react.createElement(
            "div",
            { key: "steam-ui-library-badge-row", className, style },
            ours,
            ...additions,
            element,
        );
    };

    // Descends the rendered tree by props alone. The tile's whole icon row is host elements and
    // fragments below the Focusable, so nothing has to be rendered to reach the anchor; function
    // components on the way are left untouched, which keeps every identity Valve's reconciler holds.
    const decorate = (element, depth) => {
        if (depth > MaximumDescent || !react.isValidElement(element)) return element;
        if (element.type === badge) {
            placed++;
            return withBadge(element);
        }
        return mapChildren(react, element, (kid) => decorate(kid, depth + 1));
    };

    // Wraps the tile's observer so its OUTPUT can be changed. Cached against the original: a fresh
    // identity on every claim would remount every tile React reconciles.
    const wrapTile = (original) => {
        let wrapped = tileCache.get(original);
        if (wrapped) return wrapped;
        wrapped = function SteamUiLibraryTile(this: unknown, props, secondArgument) {
            const tree = original.call(this, props, secondArgument);
            reportBigArt();
            return installed
                ? react.createElement(LiveLibraryTile, {
                      tree,
                      overview: props?.app ?? props?.overview,
                  })
                : tree;
        };
        tileCache.set(original, wrapped);
        return wrapped;
    };

    function LiveLibraryTile(props) {
        const appId = props.overview?.appid ?? 0;
        const store = local.storeFor(appId);
        react.useSyncExternalStore(store.subscribe, store.revision, store.revision);
        if (!installed) return props.tree;
        reading = local.read();
        const before = placed;
        let result = decorate(props.tree, 0);
        const library = libraryForOverview(props.overview, reading);
        if (library?.managed && !library.installed && react.isValidElement(result)) {
            result = react.cloneElement(result, {
                style: { ...result.props.style, filter: "grayscale(1)", opacity: 0.58 },
                title: library.reason || library.name,
            });
        }
        if (placed === before) unanchored++;
        outcome = "rendered";
        renderedReading = reading;
        return result;
    }

    const resolve = () => {
        runtime = getWebpackRuntime("library-badge");
        const resolvedReact = resolveReact(runtime);
        if (!resolvedReact) {
            lastError = "React runtime was not a unique match";
            return false;
        }
        react = resolvedReact;
        if (typeof react.useSyncExternalStore !== "function") {
            lastError = "React runtime lacks useSyncExternalStore";
            return false;
        }

        const tileFactory = runtime.findUnique([...TileTokens]);
        if (!tileFactory) {
            lastError = "library tile module was not a unique match";
            return false;
        }
        const exports = runtime(tileFactory[0]);

        // The tile is the module's one memo export; the badge is the one function export that draws
        // the controller-support icon. Both are chosen by what they are, never by their minified names.
        const tiles = Object.keys(exports).filter((name) => {
            const value = exports[name];
            return value && typeof value === "object" && value.$$typeof === ReactMemoType;
        });
        if (tiles.length !== 1) {
            lastError = `library tile export was ${tiles.length ? "ambiguous" : "absent"}`;
            return false;
        }
        const badges = Object.keys(exports).filter((name) => {
            const value = exports[name];
            return typeof value === "function" && String(value).includes(TileTokens[0]);
        });
        if (badges.length !== 1) {
            lastError = `Steam Input badge export was ${badges.length ? "ambiguous" : "absent"}`;
            return false;
        }
        tile = exports[tiles[0]];
        badge = exports[badges[0]];

        // The class map is wanted, not required: without it the badge still draws, on every tile
        // rather than the focused one, and `status.classesResolved` says so. Read as the tile reads
        // it — the module's export, unwrapped if it is an ES default.
        classes = null;
        const classMapFactory = runtime.findUnique([...ClassMapTokens]);
        if (classMapFactory) {
            const exported = runtime(classMapFactory[0]);
            const map = classMapOf(exported);
            const row = map?.LibraryItemIcons;
            const icon = map?.ControllerSupportIcon;
            if (typeof row === "string" && row && typeof icon === "string" && icon) {
                classes = { row, icon };
            }
        }

        // The settings store is wanted, not required: without it the badge still draws and
        // `status.bigArt` says null rather than guessing.
        settings = null;
        const settingsFactory = runtime.findUnique([...SettingsTokens]);
        if (settingsFactory) {
            const stores = runtime(settingsFactory[0]);
            const candidates = Object.keys(stores).filter((name) => {
                const value = stores[name];
                return (
                    value && typeof value === "object" && typeof value.clientSettings === "object"
                );
            });
            if (candidates.length === 1) settings = stores[candidates[0]];
        }
        return true;
    };

    const install = () => {
        if (installed) return { ok: true, alreadyInstalled: true };
        const resolved = attemptResolution(resolve, (error) => {
            lastError = "library badge resolution failed: " + String(error);
        });
        if (!resolved) return { ok: false, error: lastError };

        // Every caller draws the tile through the same memo, so claiming its `type` reaches the
        // carousel and the grid without patching a single caller.
        const claim = claimMember(tile, "type", claimKeys, (original: any) => {
            if (typeof original !== "function") return original;
            return wrapTile(original);
        });
        if (!claim.ok) {
            lastError = claim.error;
            return { ok: false, error: lastError };
        }

        installed = true;
        mounted.adopt(tile, tile.type);
        lastError = "";
        reportedBigArt = null;
        reportBigArt();
        unsubscribe = subscribe(patchId, (state) => {
            reading = local.publish(state);
        });
        return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };

    const remove = () => {
        if (!installed) return { ok: true, absent: true };
        // Released before anything is forgotten, so a failed release stays installed and the next
        // remove retries it.
        const replacement = tile.type;
        const original = unclaimedValue(replacement, claimKeys);
        const released = releaseMember(tile, "type", claimKeys);
        if (!released.ok) {
            lastError = released.error ?? "library badge release failed";
            return { ok: false, error: lastError };
        }
        installed = false;
        mounted.release(original);
        // New mounts inherit the claimed memo without adoption. Restore those owned fibers once,
        // on removal; ordinary publications never traverse Steam's component tree.
        for (const fiber of mountedFibersOf(reactRootFibers(), tile, MaximumMountedNodes)) {
            if (fiber.type !== replacement) continue;
            retargetFiber(fiber, original);
            invalidateFiberProps(fiber);
            requestRender(fiber);
        }
        unsubscribe = endSubscription(unsubscribe);
        reading = local.publish(null);
        local.changed();
        tileCache.clear();
        outcome = "removed";
        return { ok: true, removed: true };
    };

    const status = () => ({
        ok: true,
        installed,
        resolved: !!tile && !!badge,
        claimed: memberClaimed(tile, "type", claimKeys),
        settingsResolved: !!settings,
        classesResolved: !!classes,
        bigArt: readBigArt(),
        libraries: reading.count,
        apps: reading.libraries.size,
        lastOutcome:
            outcome === "rendered"
                ? `placed=${placed} unanchored=${unanchored} libraries=${renderedReading.count} apps=${renderedReading.libraries.size}`
                : outcome,
        lastError,
    });

    return { install, remove, status };
}

registerGate("libraryBadge", createLibraryBadge());

// The library as a stat on a game's own page, after Last Played and Play Time.
//
// Mapped from the Stable client (UI build of 2026-09-06) and the September 2026 beta on 2026-09-11,
// whose app-details module is the same in both:
//
//   PlayBar                  exported mobx observer class
//     StatusAndStats         exported mobx observer class
//       stats section        module-local mobx observer class, rendering
//         div.GameStatsSection   claim content, cloud status, install size, Last Played,
//                                Play Time or time left, achievements, controller support
//
// Every one of those pins a non-writable render on each instance, so no claim on a type or a
// prototype holds. The row passes through the JSX runtime when Steam creates it, and that is where
// this adds to it (interceptElements in ownership.ts): the div whose class is the play bar class
// map's `GameStatsSection` gets one more child. The stat is Valve's markup for Last Played, built
// from the same class map, so it takes the row's type, spacing and narrow-window rules, and its
// label is Steam's own `#Settings_Page_Library`, localized. The app is the overview the row's own
// children are given.
//
// The data is the library badge's publication, read by the same rules: a game on a library that is
// not attached shows its library dimmed, and one installed nowhere has no stat. The row draws with the
// page, so a new publication shows the next time the page renders.
function createLibraryDetails() {
    const publicationId = "steam-ui.library-badge";
    const TransformName = "libraryDetails";
    const ClassMapTokens = [
        'GameStatsSection:"',
        'PlayBarDetailLabel:"',
        'LastPlayedInfo:"',
    ] as const;
    const RequiredClasses = [
        "GameStatsSection",
        "GameStat",
        "GameStatRight",
        "PlayBarLabel",
        "PlayBarDetailLabel",
    ];
    const LabelToken = "#Settings_Page_Library";
    const StatKey = "steam-ui-library-details";

    let runtime;
    let react: any = null;
    let jsxRuntime: any = null;
    let ui: any = null;
    let localize: ((token: string) => unknown) | null = null;
    let classes: {
        section: string;
        stat: string;
        right: string;
        label: string;
        value: string;
    } | null = null;
    let installed = false;
    let lastError = "";
    let lastOutcome = "never rendered";
    let placed = 0;
    let without = 0;
    let unsubscribe: (() => void) | null = null;
    const local = createLibraryBadgeReading();
    let reading = local.read();

    function LiveLibraryStat(props) {
        const store = local.storeFor(props.overview?.appid ?? 0);
        react.useSyncExternalStore(store.subscribe, store.revision, store.revision);
        if (!installed) return null;
        reading = local.read();
        const library = libraryForOverview(props.overview, reading);
        return library ? renderStat(library) : null;
    }

    const label = () => localizedOr(localize, LabelToken, "Library");

    const overviewIn = (children: unknown[]) => {
        for (const child of children) {
            const overview = (child as any)?.props?.overview;
            if (overview && typeof overview.appid === "number") return overview;
        }
        return null;
    };

    const renderStat = (library: NonNullable<ReturnType<typeof libraryForOverview>>) =>
        react.createElement(
            "div",
            { key: StatKey, className: classes!.stat },
            react.createElement(
                "div",
                { className: classes!.right },
                react.createElement("div", { className: classes!.label }, label()),
                react.createElement(
                    "div",
                    {
                        className: classes!.value,
                        title: library.reason || library.name,
                        style: library.installed
                            ? undefined
                            : { opacity: 0.55, whiteSpace: "normal" },
                    },
                    library.installed || !library.managed
                        ? library.name
                        : `${library.name}: Unavailable`,
                    library.reason
                        ? react.createElement(
                              "div",
                              { style: { fontSize: "12px" } },
                              library.reason,
                          )
                        : null,
                    !library.installed && library.managed && ui?.dialogButton
                        ? react.createElement(
                              ui.dialogButton,
                              {
                                  onClick: () =>
                                      request(publicationId, "recheck", {
                                          appId: library.appId,
                                      }).catch(() => {}),
                              },
                              "Recheck",
                          )
                        : null,
                ),
            ),
        );

    const transform = (create, type, props, key) => {
        if (type !== "div" || !installed || !classes || props?.className !== classes.section)
            return undefined;
        const children = Array.isArray(props.children) ? props.children : [props.children];
        if (children.some((child) => child?.key === StatKey)) return undefined;
        const overview = overviewIn(children);
        const library = libraryForOverview(overview, reading);
        const additions = pluginFrontendElements("gamePage", react, { overview });
        if (!library) {
            without++;
        } else {
            placed++;
        }
        lastOutcome = `placed=${placed} without=${without} libraries=${reading.count} apps=${reading.libraries.size}`;
        if (!overview && !additions.length) return undefined;
        return create(
            type,
            {
                ...props,
                children: [
                    ...children,
                    ...(overview
                        ? [react.createElement(LiveLibraryStat, { key: StatKey, overview })]
                        : []),
                    ...additions,
                ],
            },
            key,
        );
    };

    const resolve = () => {
        runtime = getWebpackRuntime("library-details");
        react = resolveReact(runtime);
        if (!react || typeof react.useSyncExternalStore !== "function") {
            lastError = "React unavailable or lacks useSyncExternalStore";
            return false;
        }
        jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
        if (typeof jsxRuntime?.jsx !== "function" || typeof jsxRuntime?.jsxs !== "function") {
            lastError = "JSX runtime lacks jsx or jsxs";
            return false;
        }
        // Valve's names for the play bar's classes, mapped to whatever this build emitted. Read by
        // name, never written down.
        const exported = runtime.resolve([...ClassMapTokens]);
        const map = classMapOf(exported);
        if (!map || RequiredClasses.some((name) => typeof map[name] !== "string" || !map[name])) {
            lastError = "the play bar class map lacks a stat class";
            return false;
        }
        const join = (...names: string[]) =>
            names
                .map((name) => map[name])
                .filter((value) => typeof value === "string" && value)
                .join(" ");
        classes = {
            section: map.GameStatsSection,
            stat: join("GameStat", "LastPlayed"),
            right: join("GameStatRight"),
            label: join("PlayBarLabel"),
            value: join("PlayBarDetailLabel", "LastPlayedInfo"),
        };

        // Wanted, not required: without it the label is the English word.
        localize = resolveSteamLocalizer(runtime);
        const components = resolveSteamUiComponents(runtime);
        ui = components ? { ...components, react } : null;
        return true;
    };

    const install = () => {
        if (installed) return { ok: true, alreadyInstalled: true };
        const resolved = attemptResolution(resolve, (error) => {
            lastError = "library details resolution failed: " + String(error);
        });
        if (!resolved) return { ok: false, error: lastError };

        installed = true;
        const claim = interceptElements(jsxRuntime, TransformName, transform);
        if (!claim.ok) {
            installed = false;
            lastError = claim.error ?? "the JSX runtime could not be intercepted";
            return { ok: false, error: lastError };
        }
        lastError = "";
        unsubscribe = subscribe(publicationId, (state) => {
            reading = local.publish(state);
        });
        return { ok: true, installed: true };
    };

    const remove = () => {
        if (!installed) return { ok: true, absent: true };
        const released = releaseElements(jsxRuntime, TransformName);
        if (!released.ok) {
            lastError = released.error ?? "library details release failed";
            return { ok: false, error: lastError };
        }
        installed = false;
        unsubscribe = endSubscription(unsubscribe);
        reading = local.publish(null);
        local.changed();
        lastOutcome = "removed";
        return { ok: true, removed: true };
    };

    const status = () => ({
        ok: true,
        installed,
        resolved: !!react && !!jsxRuntime && !!classes,
        claimed: elementsIntercepted(jsxRuntime, TransformName),
        localized: !!localize,
        libraries: reading.count,
        apps: reading.libraries.size,
        lastOutcome,
        lastError,
    });

    return { install, remove, status };
}

registerGate("libraryDetails", createLibraryDetails());
