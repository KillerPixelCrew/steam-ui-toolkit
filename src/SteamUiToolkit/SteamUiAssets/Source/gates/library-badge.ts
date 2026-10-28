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
    // The host's badge preference. Off, no library is named; managed availability is still drawn,
    // because a shortcut on absent storage must not look launchable.
    const showBadges = state?.showBadges !== false;
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
    return { libraries, shortcuts, count, showBadges };
};

// The host's verdict on a managed shortcut that cannot launch now, or null for every other app:
// an available managed shortcut, an ordinary shortcut and a Steam game alike.
const unavailableShortcut = (appid: unknown, reading: ReturnType<typeof readLibraryBadgeState>) => {
    if (typeof appid !== "number") return null;
    const shortcut = reading.shortcuts.get(appid);
    return shortcut && !shortcut.available ? shortcut : null;
};

// The library to name for one app overview, or null when there is none. Steam's own installed flag is
// the authority on installed; a disconnected card's games are not installed by Steam's reckoning. The
// published connection stands in only where the overview cannot say. A game that no published library
// holds has no library to name.
const libraryForOverview = (overview, reading: ReturnType<typeof readLibraryBadgeState>) => {
    const appid = typeof overview?.appid === "number" ? overview.appid : null;
    if (appid === null) return null;
    const library = reading.showBadges ? reading.libraries.get(appid) : undefined;
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
                    before?.managed !== after?.managed ||
                    previous.showBadges !== current.showBadges
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
/**
 * Adds host library badges using shared element transforms and published library identity.
 * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
 */

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
        if (!library || !reading.showBadges) return null;
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

/**
 * Adds host metadata and frontend contributions to the matched game details page.
 * @returns Install/remove controls and diagnostics for the reversible details-page claim.
 */
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

// Availability of host-managed shortcuts where Steam acts on "installed": the primary action on a
// game's play bar, the library's Installed tab, and the play-state options of the library filter.
//
// Read offline from the installed client (sp.js of 2026-10-20) without executing it:
//
//   play bar   a mobx observer class renders one Focusable container whose className carries the
//              play button class map's `PlayButtonContainer`. Its first child is the primary
//              Focusable button (`PlayButton`, autoFocus in the gamepad UI, launching from onClick,
//              or from onMouseUp when it fakes actions); its last is a remote-play explainer
//              carrying the overview.
//   Installed  the tab is drawn as an element given `collection`, Steam's `local-install`
//              collection. That collection is every installed app of the games type and, while the
//              gamepad window is active, no shortcut at all; All Games leaves shortcuts out the same
//              way, and the Non-Steam tab is a collection of its own.
//   filter     the library's own filters (desktop, gamepad and VR) are instances of one subclass of
//              Steam's app filter class, read inside each grid's render through mobx-react-lite's
//              useObserver. Its `MatchesImpl` and `MatchesScoredImpl` read the play states from the
//              overview: `local_per_client_data.installed`, `installed` and
//              `per_client_data[].installed`. Users' dynamic collections hold a base-class filter
//              of their own and are not touched.
//
// The client has no shortcut-specific installed setter, so nothing in Valve's app store is written.
//
// The action: for a managed shortcut the host reports unavailable, the container keeps Valve's
// Focusable, autoFocus and ref, trades its green for Valve's disabled look, and its primary button
// reads Recheck and sends `recheck` instead of launching. Any other app keeps Valve's element.
//
// The Installed tab: the collection an element is given is replaced by a read-only view of it that
// adds the managed shortcuts the host reports available and Steam reports installed, and drops the
// unavailable ones, in the games collection's own order. Ordinary shortcuts and Steam games keep
// Steam's verdict.
//
// The filter: an unavailable managed shortcut is evaluated through a read-only view of its overview
// whose installed fields read false, so Installed and Ready to Play leave it out while every other
// option, All Games and Non-Steam judge it as before.
//
// Every swap is a live component following the publication, so a change in availability redraws
// the play bar and the grids, whose render reruns the filter, without waiting for a Steam render.
// Nothing is drawn differently while the host manages no shortcut.
function createLibraryAvailability() {
    const publicationId = "steam-ui.library-badge";
    const TransformName = "libraryAvailability";
    const ClassMapTokens = [
        'PlayButtonContainer:"',
        'ShutdownAppButton:"',
        'ShowingStreaming:"',
    ] as const;
    const AppFilterTokens = ["MatchesScoredImpl", "MatchesImpl"] as const;
    const CollectionStoreTokens = ["Computing localGamesCollection", "local-install"] as const;
    const InstalledCollection = "local-install";
    const GamesCollection = "type-games";
    const FilterMembers = ["MatchesImpl", "MatchesScoredImpl"] as const;
    const RequiredClasses = [
        "PlayButtonContainer",
        "PlayButton",
        "ButtonText",
        "Green",
        "Disabled",
    ];
    const ClientFields = new Set([
        "local_per_client_data",
        "most_available_per_client_data",
        "selected_per_client_data",
    ]);
    const claimKeys = {
        marker: "__steamUiLibraryAvailabilityClaimed",
        original: "__steamUiLibraryAvailabilityOriginal",
    } as const;

    let runtime;
    let react: any = null;
    let jsxRuntime: any = null;
    let filterPrototype: any = null;
    let collections: any = null;
    let classes: {
        container: string;
        button: string;
        text: string;
        green: string;
        disabled: string;
    } | null = null;
    let installed = false;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;
    let availabilityKey = "";
    let replaced = 0;
    let viewed = 0;
    let projected = 0;
    const local = createLibraryBadgeReading();
    // One store for the grids: they redraw when the set of managed shortcuts or their state moves.
    const grids = createLocalStore();
    let reading = local.read();

    const hasClass = (className: unknown, name: string) =>
        typeof className === "string" && ` ${className} `.includes(` ${name} `);

    const isShortcut = (overview) => {
        try {
            return typeof overview?.BIsShortcut === "function" && overview.BIsShortcut() === true;
        } catch {
            return false;
        }
    };

    const childrenOf = (props) =>
        Array.isArray(props?.children) ? props.children : [props?.children];

    const overviewIn = (children: unknown[]) => {
        for (let index = children.length - 1; index >= 0; index--) {
            const overview = (children[index] as any)?.props?.overview;
            if (overview && typeof overview.appid === "number") return overview;
        }
        return null;
    };

    // Valve's container with its primary button turned into Recheck. Every other child stays.
    const unavailableAction = (container, shortcut, pending: boolean, recheck: () => void) => {
        const label = pending ? "Checking…" : "Recheck";
        const description = shortcut.location
            ? `${shortcut.location} is unavailable. ${shortcut.reason}`.trim()
            : shortcut.reason || "This game is unavailable.";
        let primary = false;
        const children = childrenOf(container.props).map((child) => {
            if (
                primary ||
                !react.isValidElement(child) ||
                !hasClass(child.props?.className, classes!.button)
            )
                return child;
            primary = true;
            // The handler takes whichever slot Valve launched from, so a mouse, touch or gamepad
            // activation reaches it exactly as it reached the launch.
            const slot = typeof child.props.onMouseUp === "function" ? "onMouseUp" : "onClick";
            return react.cloneElement(
                child,
                {
                    onClick: undefined,
                    onMouseUp: undefined,
                    [slot]: (event) => {
                        event?.stopPropagation?.();
                        event?.preventDefault?.();
                        recheck();
                    },
                    "aria-label": `${label}. ${description}`,
                    title: description,
                },
                react.createElement("div", { key: "text", className: classes!.text }, label),
            );
        });
        if (!primary) return container;
        replaced++;
        const className = String(container.props.className ?? "")
            .split(" ")
            .filter((name) => name && name !== classes!.green && name !== classes!.disabled)
            .concat(classes!.disabled)
            .join(" ");
        return react.cloneElement(container, { className, children });
    };

    function LiveAvailabilityAction(props) {
        const appId = props.overview?.appid ?? 0;
        const store = local.storeFor(appId);
        react.useSyncExternalStore(store.subscribe, store.revision, store.revision);
        const [pending, setPending] = react.useState(false);
        if (!installed) return props.element;
        reading = local.read();
        const shortcut = unavailableShortcut(appId, reading);
        if (!shortcut) return props.element;
        const recheck = () => {
            if (pending) return;
            setPending(true);
            request(publicationId, "recheck", { appId })
                .catch(() => {})
                .finally(() => setPending(false));
        };
        return unavailableAction(props.element, shortcut, pending, recheck);
    }

    // Whether the host reports this managed shortcut available and Steam reports it installed:
    // the shortcuts the gamepad window's Installed tab would otherwise leave out.
    const availableManaged = (overview) =>
        reading.shortcuts.get(overview?.appid)?.available === true &&
        overview.local_per_client_data?.installed === true &&
        isShortcut(overview);

    const visible = (overview) => {
        try {
            return collections.BIsVisible(overview) === true;
        } catch {
            return false;
        }
    };

    // A read-only view of Steam's Installed collection. Its lists are derived on each read from the
    // collections Steam keeps, so the grid's observer still tracks Steam's own state.
    const installedView = (collection) => {
        const all = () => {
            const kept: Set<number> = collection.apps;
            const games = collections.GetCollection(GamesCollection)?.allApps;
            if (!Array.isArray(games)) return collection.allApps;
            return games.filter((overview) =>
                unavailableShortcut(overview?.appid, reading)
                    ? false
                    : kept.has(overview?.appid) || availableManaged(overview),
            );
        };
        const visibleOf = () => all().filter(visible);
        // Steam's count with the same apps added and dropped: the difference is only shortcuts,
        // which a tools filter never removes.
        const count = (filter) => {
            const native = collection.GetAppCountWithToolsFilter(filter);
            const kept: Set<number> = collection.apps;
            let difference = 0;
            for (const overview of visibleOf()) if (!kept.has(overview.appid)) difference++;
            for (const overview of collection.allApps) {
                if (unavailableShortcut(overview?.appid, reading) && visible(overview))
                    difference--;
            }
            return native + difference;
        };
        viewed++;
        return new Proxy(collection, {
            get: (target, property) => {
                switch (property) {
                    case "allApps":
                        return all();
                    case "visibleApps":
                        return visibleOf();
                    case "apps":
                        return new Set(all().map((overview) => overview.appid));
                    case "GetAppCountWithToolsFilter":
                        return count;
                    default:
                        return Reflect.get(target, property, target);
                }
            },
        });
    };

    // A grid, keyed by its collection, redrawn when availability moves. The Installed tab is given
    // the view; every other grid its own collection, so its render reruns the library filter.
    function LiveAvailabilityCollection(props) {
        const revision = react.useSyncExternalStore(
            grids.subscribe,
            grids.revision,
            grids.revision,
        );
        const original = props.props.collection;
        const collection = react.useMemo(
            () =>
                installed && original.id === InstalledCollection
                    ? installedView(original)
                    : original,
            [original, revision],
        );
        return props.create(props.type, { ...props.props, collection }, props.elementKey);
    }

    const isCollection = (value) =>
        !!value &&
        typeof value === "object" &&
        typeof value.id === "string" &&
        "visibleApps" in value;

    // The play bar of a shortcut, and every grid given a collection while the host manages
    // shortcuts. A Steam game's play bar keeps Valve's element.
    const transform = (create, type, props, key) => {
        if (!installed || !classes || !props) return undefined;
        if (hasClass(props.className, classes.container)) {
            const overview = overviewIn(childrenOf(props));
            if (!overview || !isShortcut(overview)) return undefined;
            return react.createElement(LiveAvailabilityAction, {
                key,
                overview,
                element: create(type, props, key),
            });
        }
        if (!reading.shortcuts.size || !isCollection(props.collection)) return undefined;
        return react.createElement(LiveAvailabilityCollection, {
            key,
            create,
            type,
            props,
            elementKey: key,
        });
    };

    const notInstalled = new WeakMap<object, object>();
    const withoutInstall = (data) => {
        if (!data || typeof data !== "object") return data;
        const existing = notInstalled.get(data);
        if (existing) return existing;
        const view = new Proxy(data, {
            get: (target, property) =>
                property === "installed" ? false : Reflect.get(target, property, target),
        });
        notInstalled.set(data, view);
        return view;
    };

    // A read-only view of one overview as the filter would see it without its storage. Built only
    // for an unavailable managed shortcut; every other app reaches the filter as it is.
    const uninstalledView = (overview) =>
        new Proxy(overview, {
            get: (target, property) => {
                if (property === "installed") return false;
                const value = Reflect.get(target, property, target);
                if (typeof property !== "string") return value;
                if (ClientFields.has(property)) return withoutInstall(value);
                if (property === "per_client_data" && Array.isArray(value))
                    return value.map(withoutInstall);
                return value;
            },
        });

    const claimFilter = (member: string) =>
        claimMember(filterPrototype, member, claimKeys, (original: any) => {
            if (typeof original !== "function") return original;
            return function SteamUiLibraryAvailabilityFilter(this: unknown, overview) {
                if (
                    installed &&
                    unavailableShortcut(overview?.appid, reading) &&
                    isShortcut(overview)
                ) {
                    projected++;
                    try {
                        return original.call(this, uninstalledView(overview));
                    } catch {
                        // A view Valve's code cannot read falls back to Valve's verdict.
                    }
                }
                return original.call(this, overview);
            };
        });

    const availabilityKeyOf = (current: ReturnType<typeof readLibraryBadgeState>) => {
        const entries: string[] = [];
        for (const [appId, shortcut] of current.shortcuts)
            entries.push(`${appId}:${shortcut.available ? 1 : 0}`);
        return entries.sort().join(",");
    };

    // Redraws the grids only when a managed shortcut is added, removed or changes availability.
    const refreshGrids = () => {
        const next = installed ? availabilityKeyOf(reading) : "";
        if (next === availabilityKey) return;
        availabilityKey = next;
        grids.changed();
    };

    const resolve = () => {
        runtime = getWebpackRuntime("library-availability");
        react = resolveReact(runtime);
        if (
            !react ||
            typeof react.useSyncExternalStore !== "function" ||
            typeof react.useState !== "function" ||
            typeof react.useMemo !== "function"
        ) {
            lastError = "React unavailable or lacks useSyncExternalStore";
            return false;
        }
        jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
        if (typeof jsxRuntime?.jsx !== "function" || typeof jsxRuntime?.jsxs !== "function") {
            lastError = "JSX runtime lacks jsx or jsxs";
            return false;
        }
        // Valve's names for the play button's classes, mapped to whatever this build emitted.
        const map = classMapOf(runtime.resolve([...ClassMapTokens]));
        if (!map || RequiredClasses.some((name) => typeof map[name] !== "string" || !map[name])) {
            lastError = "the play button class map lacks a required class";
            return false;
        }
        classes = {
            container: map.PlayButtonContainer,
            button: map.PlayButton,
            text: map.ButtonText,
            green: map.Green,
            disabled: map.Disabled,
        };
        // The library's filter class: it owns both evaluation methods and extends the base filter
        // class, which owns them too. Users' dynamic collections hold the base class.
        const owns = (host, member) => !!host && Object.prototype.hasOwnProperty.call(host, member);
        const filter = runtime.exported([...AppFilterTokens], (value) => {
            const prototype = value?.prototype;
            if (typeof value !== "function" || !prototype) return false;
            const parent = Object.getPrototypeOf(prototype);
            return FilterMembers.every((member) => owns(prototype, member) && owns(parent, member));
        });
        filterPrototype = filter.prototype;
        // The collection store, by the members the library reads from it.
        collections = runtime.exported(
            [...CollectionStoreTokens],
            (value) =>
                !!value &&
                typeof value === "object" &&
                typeof value.GetCollection === "function" &&
                typeof value.BIsVisible === "function",
        );
        return true;
    };

    const releaseFilter = () => {
        for (const member of FilterMembers) {
            const released = releaseMember(filterPrototype, member, claimKeys);
            if (!released.ok) return released;
        }
        return { ok: true };
    };

    const install = () => {
        if (installed) return { ok: true, alreadyInstalled: true };
        const resolved = attemptResolution(resolve, (error) => {
            lastError = "library availability resolution failed: " + String(error);
        });
        if (!resolved) return { ok: false, error: lastError };

        for (const member of FilterMembers) {
            const claim = claimFilter(member);
            if (!claim.ok) {
                releaseFilter();
                lastError = claim.error ?? `the library filter's ${member} could not be claimed`;
                return { ok: false, error: lastError };
            }
        }
        installed = true;
        const elements = interceptElements(jsxRuntime, TransformName, transform);
        if (!elements.ok) {
            installed = false;
            releaseFilter();
            lastError = elements.error ?? "the JSX runtime could not be intercepted";
            return { ok: false, error: lastError };
        }
        lastError = "";
        unsubscribe = subscribe(publicationId, (state) => {
            reading = local.publish(state);
            refreshGrids();
        });
        return { ok: true, installed: true };
    };

    const remove = () => {
        if (!installed) return { ok: true, absent: true };
        // Released before anything is forgotten, so a failed release stays installed and the next
        // remove retries it.
        const elements = releaseElements(jsxRuntime, TransformName);
        if (!elements.ok) {
            lastError = elements.error ?? "library availability release failed";
            return { ok: false, error: lastError };
        }
        const filter = releaseFilter() as { ok: boolean; error?: string };
        if (!filter.ok) {
            lastError = filter.error ?? "the library filter release failed";
            return { ok: false, error: lastError };
        }
        installed = false;
        unsubscribe = endSubscription(unsubscribe);
        reading = local.publish(null);
        // Live actions hand Valve's element back, and the grids their own collections.
        local.changed();
        refreshGrids();
        return { ok: true, removed: true };
    };

    const status = () => ({
        ok: true,
        installed,
        resolved: !!react && !!jsxRuntime && !!classes && !!filterPrototype && !!collections,
        claimed:
            elementsIntercepted(jsxRuntime, TransformName) &&
            !!filterPrototype &&
            FilterMembers.every((member) => memberClaimed(filterPrototype, member, claimKeys)),
        managed: reading.shortcuts.size,
        replaced,
        viewed,
        projected,
        lastError,
    });

    return { install, remove, status };
}

registerGate("libraryAvailability", createLibraryAvailability());
