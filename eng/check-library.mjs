// Exercise the emitted library surfaces against inert React fixtures: the library badge on Home's
// tiles, the library details stat on a game's play bar, and the shared JSX-runtime claim the stat
// holds through the "elements" gate.
//
// Resolution is checked live and pinned by the patches' probes. What this proves is what the shipped
// bytes do once they hold their targets.
//
// The badge: it lands immediately left of Valve's Steam Input badge in one right-aligned row, names
// the published library or the internal label, is green for an installed game and grey for one that
// is not, stays away from a game with no library to name, leaves a tile without the anchor
// untouched, reports Big Art Mode once per change, and hands the tile back on removal.
//
// The stat: only the play bar's stats row gets it, built from Valve's classes and Steam's localized
// label; it names the published library, the internal label or nothing by the badge's rules and dims
// a library that is not installed; it is added once; another element transform registered through
// the "elements" gate coexists on the one claim; and the runtime is handed back only when the last
// transform leaves.
//
// Badges off: no library is named, while a managed shortcut still carries its availability.
//
// Availability: an unavailable managed shortcut's play bar keeps Valve's Focusable and sends
// `recheck` instead of launching; the Installed tab gains the available managed shortcut and loses
// the unavailable one; the library filter's installed-only options leave it out while a dynamic
// collection's base filter, ordinary shortcuts and Steam games are untouched; reinsertion and
// removal restore Valve's elements, collections and filter methods.
import assert from "node:assert/strict";
import {
    assertRemoveRetries,
    createReact,
    element,
    failingHost,
    find as findIn,
    Fragment,
    gateSource,
    instantiate,
    loadAsset,
    sharedFragments,
} from "./check-harness.mjs";

const asset = loadAsset();
// The ownership primitives and gate helpers, real, so the claims below are the shipped ones.
const shared = sharedFragments(asset);

// A publication changes only the subscribed app's visible badge, including a first publication.
{
    const local = instantiate(
        {},
        `${shared}\n${gateSource(asset, "gates/library-badge.ts")}`,
        "createLibraryBadgeReading()",
    );
    let first = 0;
    let second = 0;
    local.storeFor(70).subscribe(() => first++);
    local.storeFor(71).subscribe(() => second++);
    const state = { libraries: [{ name: "Blue card", connected: false, appIds: [70] }] };
    local.publish(state);
    assert.deepEqual([first, second], [1, 0], "the first answer refreshes only its app");
    local.publish(state);
    assert.deepEqual([first, second], [1, 0], "identical visible state refreshes nothing");
    local.publish({ libraries: [{ name: "Blue card", connected: true, appIds: [70] }] });
    assert.deepEqual([first, second], [2, 0], "a storage change leaves unrelated apps alone");
    local.publish({
        shortcuts: [{ appId: 71, available: false, location: "", reason: "Missing game" }],
    });
    assert.deepEqual([first, second], [3, 1], "managed availability updates the right app");
    assert.equal(local.read().shortcuts.get(71).reason, "Missing game");
}

// --- library badge ---------------------------------------------------------------------------------
{
    // A React stand-in with exactly the APIs the gate uses. A clone given no children gets an empty child
    // list, which is what this fixture was written against.
    const react = createReact({
        cloneReplacesChildren: true,
        useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
    });

    // Valve's module, reduced to what the gate resolves on: one memo export whose type draws the tile,
    // one function export drawing the controller-support icon, and the tree between them shaped like
    // the live client's — a Focusable root, a fragment, a hover wrapper, host divs, then the icon row.
    function SteamInputBadge() {
        return element("div", { className: "controller-support" });
    }
    Object.defineProperty(SteamInputBadge, "toString", {
        value: () => 'function(){ "ControllerSupportIcon"; }',
    });
    const Focusable = () => null;
    const Hover = () => null;
    function Tile(props) {
        const app = props.app;
        const icons = [element("div", { className: "copies" })];
        if (!app.musicAlbum) icons.push(element(SteamInputBadge, { overview: app }));
        return element(Focusable, {
            navKey: `appportrait_${app.appid}`,
            children: [
                element(Fragment, {
                    children: [
                        element(Hover, {
                            children: element(Fragment, {
                                children: [
                                    element("img", { src: `/assets/${app.appid}/hero.jpg` }),
                                    element("div", {
                                        className: "outer",
                                        children: element("div", {
                                            className: "inner",
                                            children: element("div", {
                                                className: "icons",
                                                children: icons,
                                            }),
                                        }),
                                    }),
                                ],
                            }),
                        }),
                        element("div", { style: { display: "none" }, children: app.display_name }),
                    ],
                }),
                element("div", { className: "extra" }),
            ],
        });
    }
    const { host: memo, failNext } = failingHost({
        $$typeof: Symbol.for("react.memo"),
        type: Tile,
        compare: null,
    });
    const exports = { TK: memo, Kt: SteamInputBadge, aT: 1 };
    const settings = { clientSettings: { library_home_big_art: false } };
    // The tile stylesheet's class map, as css-loader emits it: Valve's names to this build's hashes.
    const classMap = {
        LibraryItemBox: "box-hash",
        LibraryItemIcons: "row-hash",
        ControllerSupportIcon: "icon-hash",
    };

    const requests = [];
    const globals = {
        getWebpackRuntime: () => {
            const require = (id) =>
                id === "tile"
                    ? exports
                    : id === "settings"
                      ? { rV: settings }
                      : id === "classes"
                        ? classMap
                        : react;
            require.findUnique = (tokens) =>
                tokens.includes("appportrait_")
                    ? ["tile"]
                    : tokens.includes("m_setDeferredSettings")
                      ? ["settings"]
                      : tokens.includes('LibraryItemIcons:"')
                        ? ["classes"]
                        : ["react"];
            return require;
        },
        request: (patchId, command, payload) => {
            requests.push(`${command} ${payload.bigArt}`);
            return Promise.resolve();
        },
        subscribe: (patchId, listener) => {
            globals.publish = listener;
            return () => {
                globals.publish = null;
            };
        },
        publish: null,
    };

    const gate = instantiate(
        globals,
        `${shared}\n${gateSource(asset, "gates/library-badge.ts")}`,
        "createLibraryBadge()",
    );

    // Renders one tile through the claimed memo and reports what the icon row holds: the row's
    // children in order, with our badge described by its text and colour and Valve's by identity.
    const render = (app) => {
        const tree = memo.type({ app });
        return tree.type?.name === "LiveLibraryTile" ? tree.type(tree.props) : tree;
    };
    const find = (node, predicate) => findIn(react, node, predicate);
    const iconRow = (tree) => find(tree, (node) => node.props?.className === "icons")[0];
    const describe = (tree) => {
        const row = iconRow(tree);
        if (!row) return null;
        return react.Children.toArray(row.props.children).map((child) => {
            if (child.type === SteamInputBadge) return "valve";
            if (child.type !== "div" || !Array.isArray(child.props.children))
                return child.props.className;
            return child.props.children.map((inner) =>
                inner.type === SteamInputBadge
                    ? "valve"
                    : `${inner.props.children}:${inner.props.style.background.includes("76, 160, 54") ? "green" : "grey"}`,
            );
        });
    };

    const installedGame = { appid: 70, installed: true, display_name: "Seventy" };
    assert.deepEqual(
        describe(render(installedGame)),
        ["copies", "valve"],
        "the fixture tile must render",
    );

    const installed = gate.install();
    assert.ok(installed.ok, `install failed: ${installed.error}`);
    assert.ok(gate.status().claimed, "the memo type must be claimed");
    assert.ok(gate.status().settingsResolved, "the settings store must resolve");
    assert.ok(gate.status().classesResolved, "the tile class map must resolve");
    assert.deepEqual(requests, ["homeLayout false"], "the layout must be reported once on install");

    // Nothing published yet: the host names every library, so there is nothing to name.
    assert.deepEqual(
        describe(render(installedGame)),
        ["copies", "valve"],
        "a game no published library holds gets no badge",
    );

    // The published libraries decide the name; Steam's installed flag decides the colour.
    globals.publish({
        libraries: [
            { name: "Blue card", connected: false, appIds: [70] },
            { name: "Games", connected: true, appIds: [71] },
            { name: "D:", connected: true, appIds: [72] },
        ],
    });
    assert.deepEqual(
        describe(render({ appid: 70, installed: false })),
        ["copies", ["Blue card:grey", "valve"]],
        "a game on an absent card keeps naming the card, in grey",
    );
    assert.deepEqual(
        describe(render({ appid: 71, installed: true })),
        ["copies", ["Games:green", "valve"]],
        "an installed game on a present card names the card, in green",
    );
    assert.deepEqual(
        describe(render({ appid: 72, installed: true })),
        ["copies", ["D::green", "valve"]],
        "two internal libraries draw their own names",
    );
    assert.deepEqual(
        describe(render({ appid: 73, installed: false })),
        ["copies", "valve"],
        "a game installed nowhere has no library to name",
    );

    // Valve's own element survives by identity inside the row, and the tile's other children are the
    // originals: only the path to the anchor is cloned.
    const decorated = render({ appid: 71, installed: true });
    const valve = find(decorated, (node) => node.type === SteamInputBadge);
    assert.equal(valve.length, 1, "Valve's badge must be rendered exactly once");
    assert.equal(valve[0].props.overview.appid, 71);
    assert.equal(find(decorated, (node) => node.props?.className === "extra").length, 1);
    assert.equal(find(decorated, (node) => node.props?.className === "outer").length, 1);

    // The box wears Valve's row and badge classes, so it fades with the focused tile the way Valve's
    // icon does and keeps that icon a direct child of a row; the glyph-sized geometry is overridden.
    const box = find(decorated, (node) => node.props?.className === "row-hash icon-hash");
    assert.equal(box.length, 1, "the box must wear Valve's row and badge classes");
    assert.equal(box[0].props.style.width, "auto");
    assert.equal(box[0].props.style.maxWidth, "none");
    assert.equal(box[0].props.style.backgroundColor, "transparent");
    assert.equal(box[0].props.style.marginInlineStart, "auto");
    assert.equal(box[0].props.children[1], valve[0], "Valve's badge must be the box's last child");

    // A tile without the anchor is handed back untouched and counted, not decorated somewhere else.
    const album = { appid: 74, installed: true, musicAlbum: true };
    const untouched = render(album);
    assert.equal(
        find(untouched, (node) => node.props?.className === "steam-ui-library-badge").length,
        0,
    );
    assert.match(
        gate.status().lastOutcome,
        /unanchored=1/,
        "a tile without the anchor must be reported",
    );
    assert.match(gate.status().lastOutcome, /libraries=3 apps=3/);

    // Big Art Mode is reported once per change, on the next render, never once per render.
    render(installedGame);
    assert.deepEqual(requests, ["homeLayout false"], "an unchanged layout must not be re-reported");
    settings.clientSettings.library_home_big_art = true;
    render(installedGame);
    render(installedGame);
    assert.deepEqual(requests, ["homeLayout false", "homeLayout true"]);
    assert.equal(gate.status().bigArt, true);

    const removed = gate.remove();
    assert.ok(removed.ok, `remove failed: ${removed.error}`);
    assert.equal(memo.type, Tile, "removal must hand back exactly what was displaced");
    assert.ok(!gate.status().claimed);
    assert.deepEqual(
        describe(render(installedGame)),
        ["copies", "valve"],
        "removal must restore Valve's tile",
    );

    assert.ok(gate.install().ok, "the gate must be reinstallable");
    assert.deepEqual(
        describe(render(installedGame)),
        ["copies", "valve"],
        "a reinstall starts from no libraries",
    );
    assertRemoveRetries(gate, failNext, "library badge");
    assert.equal(memo.type, Tile);

    console.log("library badge gate: ok");
}

// --- library details stat and the shared JSX claim -------------------------------------------------
{
    const libraries = gateSource(asset, "gates/library-badge.ts");
    const elements = gateSource(asset, "gates/elements.ts");

    const react = createReact({
        singleChild: true,
        useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
    });
    function jsxProduction(type, props, key) {
        return element(type, props, key ?? null);
    }
    const runtime = { jsx: jsxProduction, jsxs: jsxProduction };
    const classMap = {
        GameStatsSection: "stats-hash",
        GameStat: "stat-hash",
        GameStatRight: "right-hash",
        PlayBarLabel: "label-hash",
        PlayBarDetailLabel: "detail-hash",
        LastPlayed: "last-hash",
        LastPlayedInfo: "info-hash",
    };
    const localize = (token) => (token === "#Settings_Page_Library" ? "Bibliothek" : token);
    Object.defineProperty(localize, "toString", {
        value: () => "function F(k,...E){let R=C.LocalizeString(k);return R===void 0?k:R}",
    });
    const quiet = () => "";
    Object.defineProperty(quiet, "toString", {
        value: () => "function v(k,...E){let R=C.LocalizeString(k,!0);return R===void 0?k:R}",
    });
    const modules = {
        react,
        runtime,
        classes: classMap,
        localization: { we: localize, wW: quiet },
    };
    const moduleFor = (tokens) =>
        tokens.includes("useState")
            ? "react"
            : tokens.includes(".jsxs")
              ? "runtime"
              : tokens.some((token) => token.startsWith("GameStatsSection"))
                ? "classes"
                : tokens.includes("LocalizeString")
                  ? "localization"
                  : null;

    let publish = null;
    const globals = {
        window: {},
        getWebpackRuntime: () => {
            const require = (id) => modules[id];
            require.exported = () => null;
            require.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
            require.resolve = (tokens) => modules[moduleFor(tokens)];
            return require;
        },
        subscribe: (patchId, listener) => {
            assert.equal(
                patchId,
                "steam-ui.library-badge",
                "the stat reads the badge's publication",
            );
            publish = listener;
            return () => {
                publish = null;
            };
        },
        request: () => Promise.resolve(),
    };
    const built = instantiate(
        globals,
        `${shared}\n${libraries}\n${elements}`,
        "{ details: createLibraryDetails(), gate: createElementsGate() }",
    );
    const { details, gate } = built;

    // Steam's stats row, created through the runtime exactly as the section's render does.
    const LastPlayed = () => null;
    const Playtime = () => null;
    const row = (overview) =>
        runtime.jsxs("div", {
            className: "stats-hash",
            children: [
                false,
                runtime.jsx(LastPlayed, { overview, details: {} }),
                runtime.jsx(Playtime, { overview }),
            ],
        });
    const statOf = (tree) => {
        const stat = tree.props.children.find((child) => child?.key === "steam-ui-library-details");
        return stat?.type?.name === "LiveLibraryStat" ? (stat.type(stat.props) ?? undefined) : stat;
    };
    const describe = (stat) => {
        const right = stat.props.children;
        const [label, value] = right.props.children;
        return {
            stat: stat.props.className,
            right: right.props.className,
            label: `${label.props.className}:${label.props.children}`,
            value: `${value.props.className}:${react.Children.toArray(value.props.children)
                .filter((child) => typeof child === "string")
                .join("")}`,
            dimmed: value.props.style?.opacity === 0.55,
        };
    };

    assert.equal(statOf(row({ appid: 70, installed: true })), undefined, "nothing before install");

    let result = details.install();
    assert.ok(result.ok, `install failed: ${result.error}`);
    assert.ok(details.status().claimed && details.status().resolved && details.status().localized);
    assert.equal(runtime.jsxs.name, "SteamUiElement", "the runtime must be claimed");

    // Nothing published: no library to name, so no stat.
    assert.equal(
        statOf(row({ appid: 70, installed: true })),
        undefined,
        "no stat before a publication",
    );

    publish({
        libraries: [
            { name: "Blue card", connected: false, appIds: [70] },
            { name: "Games", connected: true, appIds: [71] },
            { name: "D:", connected: true, appIds: [72] },
        ],
    });
    assert.deepEqual(describe(statOf(row({ appid: 70, installed: false }))), {
        stat: "stat-hash last-hash",
        right: "right-hash",
        label: "label-hash:Bibliothek",
        value: "detail-hash info-hash:Blue card",
        dimmed: true,
    });
    assert.equal(
        describe(statOf(row({ appid: 70, installed: false }))).dimmed,
        true,
        "not installed is dimmed",
    );
    assert.equal(
        describe(statOf(row({ appid: 71, installed: true }))).value,
        "detail-hash info-hash:Games",
    );
    assert.equal(
        describe(statOf(row({ appid: 72, installed: true }))).value,
        "detail-hash info-hash:D:",
    );
    const nowhere = row({ appid: 73, installed: false });
    assert.equal(statOf(nowhere), undefined, "a game installed nowhere gets no visible stat");
    assert.match(
        details.status().lastOutcome,
        /without=2/,
        "the unpublished and the uninstalled game draw no stat",
    );

    // Only the stats row, and only once.
    const other = runtime.jsxs("div", {
        className: "other",
        children: [runtime.jsx(LastPlayed, { overview: { appid: 71, installed: true } })],
    });
    assert.equal(other.props.children.length, 1, "other elements must be left alone");
    const already = runtime.jsxs("div", {
        className: "stats-hash",
        children: [
            runtime.jsx(LastPlayed, { overview: { appid: 71, installed: true } }),
            element("div", {}, "steam-ui-library-details"),
        ],
    });
    assert.equal(already.props.children.length, 2, "the stat must not be added twice");

    // A second transform through the "elements" gate shares the one claim; a throwing one is skipped.
    const headers = [];
    assert.ok(
        gate.register("wsgm.download-sort", (create, type, props, key) => {
            if (props?.sectionTitle !== "#Downloads_Section_Current") return undefined;
            headers.push(type);
            return create("header-row", { children: [create(type, props, key)] }, null);
        }).ok,
    );
    assert.ok(
        gate.register("throws", () => {
            throw new Error("transform failed");
        }).ok,
    );
    assert.equal(gate.register("", () => undefined).ok, false, "an unnamed transform is refused");
    const header = runtime.jsx("section", { sectionTitle: "#Downloads_Section_Current" });
    assert.equal(header.type, "header-row");
    assert.deepEqual(headers, ["section"]);
    assert.ok(
        statOf(row({ appid: 71, installed: true })),
        "the stat must keep drawing beside another transform",
    );
    assert.ok(gate.registered("wsgm.download-sort"));

    // Removal hands the runtime back only with the last transform.
    result = details.remove();
    assert.ok(result.ok, `remove failed: ${result.error}`);
    assert.equal(details.status().claimed, false);
    assert.notEqual(runtime.jsx, jsxProduction, "another transform keeps the claim");
    assert.equal(
        statOf(row({ appid: 71, installed: true })),
        undefined,
        "a removed stat must not draw",
    );
    assert.ok(gate.unregister("throws").ok);
    assert.ok(gate.unregister("wsgm.download-sort").ok);
    assert.equal(runtime.jsx, jsxProduction, "the last transform must hand jsx back");
    assert.equal(runtime.jsxs, jsxProduction, "the last transform must hand jsxs back");
    assert.equal(gate.registered("wsgm.download-sort"), false);

    assert.ok(details.install().ok, "the stat must be reinstallable");
    assert.equal(
        statOf(row({ appid: 74, installed: true })),
        undefined,
        "a reinstall starts from no libraries",
    );
    assert.ok(details.remove().ok);
    assert.equal(runtime.jsx, jsxProduction);

    console.log(
        "Library details: stat on the stats row only, badge rules, one shared JSX claim, restored with the last transform.",
    );
}

// --- badge preference ------------------------------------------------------------------------------
// With badges off no library is named, while managed availability still is.
{
    const built = instantiate(
        {},
        `${shared}\n${gateSource(asset, "gates/library-badge.ts")}`,
        "{ local: createLibraryBadgeReading(), libraryForOverview, unavailableShortcut }",
    );
    const { local, libraryForOverview, unavailableShortcut } = built;
    let changes = 0;
    local.storeFor(70).subscribe(() => changes++);
    const libraries = [{ name: "Blue card", connected: true, appIds: [70, 71] }];
    const shortcuts = [
        { appId: 71, available: false, location: "Blue card", reason: "Insert it." },
    ];
    local.publish({ libraries, shortcuts });
    assert.equal(local.read().showBadges, true, "badges are shown unless the host says otherwise");
    assert.equal(
        libraryForOverview({ appid: 70, installed: true }, local.read()).name,
        "Blue card",
    );
    local.publish({ libraries, shortcuts, showBadges: false });
    assert.equal(changes, 2, "turning badges off redraws a named game");
    assert.equal(
        libraryForOverview({ appid: 70, installed: true }, local.read()),
        null,
        "badges off: an unmanaged game names no library",
    );
    const managed = libraryForOverview({ appid: 71, installed: true }, local.read());
    assert.deepEqual(
        [managed.managed, managed.installed, managed.reason],
        [true, false, "Insert it."],
        "badges off: a managed shortcut still carries its availability",
    );
    assert.equal(unavailableShortcut(71, local.read()).location, "Blue card");
    assert.equal(
        unavailableShortcut(70, local.read()),
        null,
        "an unmanaged app is never unavailable",
    );
}

// --- managed availability: play bar, Installed tab and library filter -----------------------------
{
    const libraries = gateSource(asset, "gates/library-badge.ts");
    const react = createReact({
        singleChild: true,
        useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
        useState: (initial) => [initial, () => {}],
        useMemo: (factory) => factory(),
    });
    function jsxProduction(type, props, key) {
        return element(type, props, key ?? null);
    }
    const runtime = { jsx: jsxProduction, jsxs: jsxProduction };
    const classMap = {
        PlayButtonContainer: "container-hash",
        PlayButton: "button-hash",
        ButtonText: "text-hash",
        Green: "green-hash",
        Disabled: "disabled-hash",
    };

    // Steam's app filter: the base class users' dynamic collections hold, and the library's subclass.
    class AppFilter {
        MatchesImpl(overview) {
            return overview.local_per_client_data?.installed === true;
        }
        MatchesScoredImpl(overview) {
            return overview.installed ? 1 : 0;
        }
    }
    class LibraryFilter extends AppFilter {
        MatchesImpl(overview) {
            return overview.appid > 0 && super.MatchesImpl(overview);
        }
        MatchesScoredImpl(overview) {
            return overview.appid > 0 ? super.MatchesScoredImpl(overview) : 0;
        }
    }
    const baseMatches = AppFilter.prototype.MatchesImpl;
    const libraryMatches = LibraryFilter.prototype.MatchesImpl;
    const libraryScored = LibraryFilter.prototype.MatchesScoredImpl;

    const overview = (appid, shortcut, installed = true) => ({
        appid,
        installed,
        local_per_client_data: { installed },
        per_client_data: [{ installed }],
        BIsShortcut: () => shortcut,
    });
    const steamGame = overview(10, false);
    const missing = overview(71, true);
    const ready = overview(72, true);
    const ordinary = overview(73, true);
    const games = { id: "type-games", allApps: [steamGame, missing, ready, ordinary] };
    // The desktop window's Installed collection keeps installed shortcuts; the gamepad window's
    // drops every one. This one keeps the missing card to show that the view drops it.
    const installedCollection = {
        id: "local-install",
        apps: new Set([10, 71]),
        allApps: [steamGame, missing],
        visibleApps: [steamGame, missing],
        GetAppCountWithToolsFilter: () => 2,
    };
    const allGames = {
        id: "all-games",
        apps: new Set([10]),
        allApps: [steamGame],
        visibleApps: [steamGame],
    };
    const store = {
        GetCollection: (id) => (id === "type-games" ? games : null),
        BIsVisible: () => true,
    };
    const modules = {
        react,
        runtime,
        classes: classMap,
        filters: { E6: AppFilter, zG: LibraryFilter, n: 1 },
        store: { md: store, A8: { LocalGames: "local-install" } },
    };
    const moduleFor = (tokens) =>
        tokens.includes("useState")
            ? "react"
            : tokens.includes(".jsxs")
              ? "runtime"
              : tokens.some((token) => token.startsWith("PlayButtonContainer"))
                ? "classes"
                : tokens.includes("MatchesImpl")
                  ? "filters"
                  : tokens.includes("Computing localGamesCollection")
                    ? "store"
                    : null;

    let publish = null;
    const requests = [];
    const globals = {
        window: {},
        getWebpackRuntime: () => {
            const require = (id) => modules[id];
            require.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
            require.resolve = (tokens) => {
                const id = moduleFor(tokens);
                if (!id) throw new Error(`absent: ${tokens}`);
                return modules[id];
            };
            require.exported = (tokens, predicate) => {
                const fits = new Set(Object.values(require.resolve(tokens)).filter(predicate));
                if (fits.size !== 1)
                    throw new Error(`export ${fits.size ? "ambiguous" : "absent"}`);
                return [...fits][0];
            };
            return require;
        },
        subscribe: (patchId, listener) => {
            assert.equal(
                patchId,
                "steam-ui.library-badge",
                "availability reads the badge's publication",
            );
            publish = listener;
            return () => {
                publish = null;
            };
        },
        request: (patchId, command, payload) => {
            requests.push(`${patchId} ${command} ${JSON.stringify(payload)}`);
            return Promise.resolve();
        },
    };
    const gate = instantiate(globals, `${shared}\n${libraries}`, "createLibraryAvailability()");

    // Valve's play bar, created through the runtime as the play button's render does.
    const Explainer = () => null;
    const Focusable = () => null;
    const launches = [];
    const playBar = (app, green = true) =>
        runtime.jsxs(Focusable, {
            className: `outer container-hash${green ? " green-hash" : ""}`,
            children: [
                runtime.jsxs(Focusable, {
                    className: "button-hash child-hash",
                    autoFocus: true,
                    onClick: () => launches.push(app.appid),
                    children: [
                        element("svg", {}),
                        element("div", { className: "text-hash", children: "Play" }),
                    ],
                }),
                false,
                runtime.jsx(Explainer, { overview: app, active: false }),
            ],
        });
    // A live wrapper draws what it renders; anything else is Valve's element.
    const render = (node) =>
        typeof node.type === "function" && node.type.name.startsWith("LiveAvailability")
            ? node.type(node.props)
            : node;
    const isLive = (node) =>
        typeof node.type === "function" && node.type.name.startsWith("LiveAvailability");
    const Grid = () => null;
    const grid = (collection) => runtime.jsx(Grid, { collection, eSortBy: 1 });
    const ids = (list) => list.map((app) => app.appid);

    let result = gate.install();
    assert.ok(result.ok, `install failed: ${result.error}`);
    assert.ok(gate.status().resolved && gate.status().claimed, "the gate must hold its claims");
    assert.equal(LibraryFilter.prototype.MatchesImpl.name, "SteamUiLibraryAvailabilityFilter");
    assert.equal(
        AppFilter.prototype.MatchesImpl,
        baseMatches,
        "the base filter class is never claimed",
    );

    // Nothing managed: a shortcut's play bar renders Valve's element and no grid is wrapped.
    assert.equal(isLive(playBar(steamGame)), false, "a Steam game's play bar is never wrapped");
    const before = playBar(missing);
    assert.ok(isLive(before), "a shortcut's play bar follows the publication");
    assert.equal(render(before).props.className, "outer container-hash green-hash");
    assert.equal(
        isLive(grid(installedCollection)),
        false,
        "no grid changes while nothing is managed",
    );

    publish({
        libraries: [],
        shortcuts: [
            { appId: 71, available: false, location: "Blue card", reason: "Insert the card." },
            { appId: 72, available: true, location: "Blue card", reason: "" },
        ],
    });

    // The unavailable shortcut's primary action is Recheck, in Valve's own Focusable.
    const unavailable = render(playBar(missing));
    assert.equal(unavailable.props.className, "outer container-hash disabled-hash");
    const [button, gap, explainer] = unavailable.props.children;
    assert.equal(button.props.autoFocus, true, "focus behavior stays Valve's");
    assert.equal(button.props.className, "button-hash child-hash");
    assert.equal(button.props.children[0].props.children, "Recheck");
    assert.match(
        button.props["aria-label"],
        /Recheck\. Blue card is unavailable\. Insert the card\./,
    );
    assert.equal(gap, false);
    assert.equal(explainer.type, Explainer, "every other child stays");
    button.props.onClick({ stopPropagation() {}, preventDefault() {} });
    assert.deepEqual(requests, ['steam-ui.library-badge recheck {"appId":71}']);
    assert.deepEqual(launches, [], "Recheck never launches");
    assert.equal(gate.status().replaced, 1);

    // An available managed shortcut and an ordinary shortcut keep Valve's play bar.
    assert.equal(render(playBar(ready)).props.className, "outer container-hash green-hash");
    assert.equal(render(playBar(ordinary)).props.className, "outer container-hash green-hash");

    // The library filter leaves the unavailable shortcut out of installed-only options; a user's
    // dynamic collection, an ordinary shortcut and the available one are judged as before.
    const filter = new LibraryFilter();
    assert.equal(filter.MatchesImpl(missing), false, "Installed leaves the missing card out");
    assert.equal(filter.MatchesScoredImpl(missing), 0);
    assert.equal(filter.MatchesImpl(ready), true);
    assert.equal(filter.MatchesImpl(ordinary), true);
    assert.equal(filter.MatchesImpl(steamGame), true);
    assert.equal(
        new AppFilter().MatchesImpl(missing),
        true,
        "a dynamic collection keeps its member",
    );
    assert.equal(
        missing.local_per_client_data.installed,
        true,
        "nothing in Valve's store is written",
    );

    // The Installed tab adds the available managed shortcut and drops the missing one.
    const installedGrid = grid(installedCollection);
    assert.ok(isLive(installedGrid));
    const view = render(installedGrid).props.collection;
    assert.notEqual(view, installedCollection);
    assert.equal(view.id, "local-install");
    assert.deepEqual(ids(view.allApps), [10, 72]);
    assert.deepEqual(ids(view.visibleApps), [10, 72]);
    assert.deepEqual([...view.apps], [10, 72]);
    assert.equal(
        view.GetAppCountWithToolsFilter({}),
        2,
        "Steam's count with one added and one dropped",
    );
    assert.equal(render(installedGrid).props.eSortBy, 1, "every other prop stays");
    // Other grids keep their collection; they are redrawn so their own filter runs again.
    assert.equal(render(grid(allGames)).props.collection, allGames);

    // Reinsertion restores the launch, the filters and the Installed tab without a Steam render.
    publish({
        libraries: [],
        shortcuts: [
            { appId: 71, available: true, location: "Blue card", reason: "" },
            { appId: 72, available: true, location: "Blue card", reason: "" },
        ],
    });
    const restored = render(before);
    assert.equal(restored.props.className, "outer container-hash green-hash");
    restored.props.children[0].props.onClick();
    assert.deepEqual(launches, [71], "an available shortcut launches through Valve's handler");
    assert.equal(filter.MatchesImpl(missing), true);
    assert.deepEqual(ids(render(installedGrid).props.collection.allApps), [10, 71, 72]);

    // Removal hands every claim and element back.
    result = gate.remove();
    assert.ok(result.ok, `remove failed: ${result.error}`);
    assert.equal(gate.status().claimed, false);
    assert.equal(LibraryFilter.prototype.MatchesImpl, libraryMatches);
    assert.equal(LibraryFilter.prototype.MatchesScoredImpl, libraryScored);
    assert.equal(runtime.jsx, jsxProduction, "the runtime is handed back");
    assert.equal(render(installedGrid).props.collection, installedCollection);
    assert.ok(gate.install().ok, "the gate must be reinstallable");
    assert.ok(gate.remove().ok);

    console.log(
        "Library availability: Recheck on the play bar, managed Installed tab, installed-only filters, restored on return and removal.",
    );
}
