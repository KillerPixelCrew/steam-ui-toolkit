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
import assert from "node:assert/strict";
import {
  createReact,
  element,
  find as findIn,
  Fragment,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
  slice,
} from "./check-harness.mjs";

const asset = loadAsset();
// The ownership primitives and gate helpers, real, so the claims below are the shipped ones.
const shared = sharedFragments(asset);

// --- library badge ---------------------------------------------------------------------------------
{
  // A React stand-in with exactly the APIs the gate uses. A clone given no children gets an empty child
  // list, which is what this fixture was written against.
  const react = createReact({ cloneReplacesChildren: true });

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
                      children: element("div", { className: "icons", children: icons }),
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
  const memo = { $$typeof: Symbol.for("react.memo"), type: Tile, compare: null };
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
    `${shared}\n${slice(asset, "const readLibraryBadgeState", 'registerGate("libraryBadge"')}`,
    "createLibraryBadge()",
  );

  // Renders one tile through the claimed memo and reports what the icon row holds: the row's
  // children in order, with our badge described by its text and colour and Valve's by identity.
  const render = (app) => memo.type({ app });
  const find = (node, predicate) => findIn(react, node, predicate);
  const iconRow = (tree) => find(tree, (node) => node.props?.className === "icons")[0];
  const describe = (tree) => {
    const row = iconRow(tree);
    if (!row) return null;
    return react.Children.toArray(row.props.children).map((child) => {
      if (child.type === SteamInputBadge) return "valve";
      if (child.type !== "div" || !Array.isArray(child.props.children)) return child.props.className;
      return child.props.children.map((inner) =>
        inner.type === SteamInputBadge
          ? "valve"
          : `${inner.props.children}:${inner.props.style.background.includes("76, 160, 54") ? "green" : "grey"}`,
      );
    });
  };

  const installedGame = { appid: 70, installed: true, display_name: "Seventy" };
  assert.deepEqual(describe(render(installedGame)), ["copies", "valve"], "the fixture tile must render");

  const installed = gate.install();
  assert.ok(installed.ok, `install failed: ${installed.error}`);
  assert.ok(gate.status().claimed, "the memo type must be claimed");
  assert.ok(gate.status().settingsResolved, "the settings store must resolve");
  assert.ok(gate.status().classesResolved, "the tile class map must resolve");
  assert.deepEqual(requests, ["homeLayout false"], "the layout must be reported once on install");

  // Nothing published yet: an installed game is on the internal library and says so.
  assert.deepEqual(
    describe(render(installedGame)),
    ["copies", ["Internal:green", "valve"]],
    "an installed game with no published library is internal",
  );

  // The published libraries decide the name; Steam's installed flag decides the colour.
  globals.publish({
    libraries: [
      { name: "Blue card", connected: false, appIds: [70] },
      { name: "Games", connected: true, appIds: [71] },
    ],
    internalLabel: "Claw",
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
    ["copies", ["Claw:green", "valve"]],
    "the internal label is the host's",
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
  assert.equal(find(untouched, (node) => node.props?.className === "steam-ui-library-badge").length, 0);
  assert.match(gate.status().lastOutcome, /unanchored=1/, "a tile without the anchor must be reported");
  assert.match(gate.status().lastOutcome, /libraries=2 apps=2/);

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
  assert.deepEqual(describe(render(installedGame)), ["copies", "valve"], "removal must restore Valve's tile");

  assert.ok(gate.install().ok, "the gate must be reinstallable");
  assert.deepEqual(
    describe(render(installedGame)),
    ["copies", ["Internal:green", "valve"]],
    "a reinstall starts from no libraries and the default label",
  );
  assert.ok(gate.remove().ok);
  assert.equal(memo.type, Tile);

  console.log("library badge gate: ok");
}

// --- library details stat and the shared JSX claim -------------------------------------------------
{
  const libraries = slice(asset, "const readLibraryBadgeState", 'registerGate("libraryDetails"');
  const elements = gateSource(asset, "createElementsGate", "elements");

  const react = createReact({ singleChild: true });
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
  const modules = { react, runtime, classes: classMap, localization: { we: localize, wW: quiet } };
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
      require.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
      require.resolve = (tokens) => modules[moduleFor(tokens)];
      return require;
    },
    subscribe: (patchId, listener) => {
      assert.equal(patchId, "steam-ui.library-badge", "the stat reads the badge's publication");
      publish = listener;
      return () => {
        publish = null;
      };
    },
    request: () => Promise.resolve(),
    registerGate: () => {},
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
      children: [false, runtime.jsx(LastPlayed, { overview, details: {} }), runtime.jsx(Playtime, { overview })],
    });
  const statOf = (tree) => tree.props.children.find((child) => child?.key === "steam-ui-library-details");
  const describe = (stat) => {
    const right = stat.props.children;
    const [label, value] = right.props.children;
    return {
      stat: stat.props.className,
      right: right.props.className,
      label: `${label.props.className}:${label.props.children}`,
      value: `${value.props.className}:${value.props.children}`,
      dimmed: value.props.style?.opacity === 0.55,
    };
  };

  assert.equal(statOf(row({ appid: 70, installed: true })), undefined, "nothing before install");

  let result = details.install();
  assert.ok(result.ok, `install failed: ${result.error}`);
  assert.ok(details.status().claimed && details.status().resolved && details.status().localized);
  assert.equal(runtime.jsxs.name, "SteamUiElement", "the runtime must be claimed");

  // Nothing published: an installed game is on the internal library.
  assert.deepEqual(describe(statOf(row({ appid: 70, installed: true }))), {
    stat: "stat-hash last-hash",
    right: "right-hash",
    label: "label-hash:Bibliothek",
    value: "detail-hash info-hash:Internal",
    dimmed: false,
  });

  publish({
    libraries: [
      { name: "Blue card", connected: false, appIds: [70] },
      { name: "Games", connected: true, appIds: [71] },
    ],
    internalLabel: "Claw",
  });
  assert.equal(describe(statOf(row({ appid: 70, installed: false }))).value, "detail-hash info-hash:Blue card");
  assert.equal(describe(statOf(row({ appid: 70, installed: false }))).dimmed, true, "not installed is dimmed");
  assert.equal(describe(statOf(row({ appid: 71, installed: true }))).value, "detail-hash info-hash:Games");
  assert.equal(describe(statOf(row({ appid: 72, installed: true }))).value, "detail-hash info-hash:Claw");
  const nowhere = row({ appid: 73, installed: false });
  assert.equal(nowhere.props.children.length, 3, "a game installed nowhere gets no stat");
  assert.match(details.status().lastOutcome, /without=1/);

  // Only the stats row, and only once.
  const other = runtime.jsxs("div", { className: "other", children: [runtime.jsx(LastPlayed, { overview: { appid: 71, installed: true } })] });
  assert.equal(other.props.children.length, 1, "other elements must be left alone");
  const already = runtime.jsxs("div", {
    className: "stats-hash",
    children: [runtime.jsx(LastPlayed, { overview: { appid: 71, installed: true } }), element("div", {}, "steam-ui-library-details")],
  });
  assert.equal(already.props.children.length, 2, "the stat must not be added twice");

  // A second transform through the "elements" gate shares the one claim; a throwing one is skipped.
  const headers = [];
  assert.ok(gate.register("wsgm.download-sort", (create, type, props, key) => {
    if (props?.sectionTitle !== "#Downloads_Section_Current") return undefined;
    headers.push(type);
    return create("header-row", { children: [create(type, props, key)] }, null);
  }).ok);
  assert.ok(gate.register("throws", () => {
    throw new Error("transform failed");
  }).ok);
  assert.equal(gate.register("", () => undefined).ok, false, "an unnamed transform is refused");
  const header = runtime.jsx("section", { sectionTitle: "#Downloads_Section_Current" });
  assert.equal(header.type, "header-row");
  assert.deepEqual(headers, ["section"]);
  assert.ok(statOf(row({ appid: 71, installed: true })), "the stat must keep drawing beside another transform");
  assert.ok(gate.registered("wsgm.download-sort"));

  // Removal hands the runtime back only with the last transform.
  result = details.remove();
  assert.ok(result.ok, `remove failed: ${result.error}`);
  assert.equal(details.status().claimed, false);
  assert.notEqual(runtime.jsx, jsxProduction, "another transform keeps the claim");
  assert.equal(statOf(row({ appid: 71, installed: true })), undefined, "a removed stat must not draw");
  assert.ok(gate.unregister("throws").ok);
  assert.ok(gate.unregister("wsgm.download-sort").ok);
  assert.equal(runtime.jsx, jsxProduction, "the last transform must hand jsx back");
  assert.equal(runtime.jsxs, jsxProduction, "the last transform must hand jsxs back");
  assert.equal(gate.registered("wsgm.download-sort"), false);

  assert.ok(details.install().ok, "the stat must be reinstallable");
  assert.equal(describe(statOf(row({ appid: 74, installed: true }))).value, "detail-hash info-hash:Internal");
  assert.ok(details.remove().ok);
  assert.equal(runtime.jsx, jsxProduction);

  console.log("Library details: stat on the stats row only, badge rules, one shared JSX claim, restored with the last transform.");
}
