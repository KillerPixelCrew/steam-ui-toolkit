// Exercise the emitted library-badge gate against an inert React fixture.
//
// Resolution is checked live and pinned by the patch's probe; what this proves is what the
// shipped bytes do to a tile once they have claimed it: the badge lands immediately left of Valve's
// Steam Input badge in one right-aligned row, names the published library or the internal label,
// is green for an installed game and grey for one that is not, stays away from a game with no
// library to name, leaves a tile without the anchor untouched, reports Big Art Mode once per
// change, and hands the tile back on removal.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createLibraryBadge()");
assert.ok(start >= 0, "the emitted asset must contain the library badge gate");
const end = asset.indexOf('registerGate("libraryBadge"', start);
assert.ok(end > start, "the library badge gate must register itself");

// A React stand-in with exactly the APIs the gate uses. Elements are plain objects and nothing
// here reconciles: the gate walks props, so a "render" is one call of the tile's type.
const ElementMarker = Symbol("element");
const Fragment = Symbol.for("react.fragment");
const element = (type, props, key = null) => ({ [ElementMarker]: true, type, props: props ?? {}, key });
const react = {
  Fragment,
  createElement(type, props, ...children) {
    const { key = null, ...rest } = props ?? {};
    return element(type, children.length ? { ...rest, children } : rest, key);
  },
  cloneElement: (source, props, ...children) =>
    element(source.type, { ...source.props, ...props, children }, source.key),
  isValidElement: (value) => !!value && typeof value === "object" && value[ElementMarker] === true,
  Children: {
    toArray: (children) =>
      (Array.isArray(children) ? children : children === undefined ? [] : [children]).filter(
        (child) => child !== null && child !== undefined && child !== false,
      ),
  },
};

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

const requests = [];
const globals = {
  getWebpackRuntime: () => {
    const require = (id) => (id === "tile" ? exports : id === "settings" ? { rV: settings } : react);
    require.findUnique = (tokens) =>
      tokens.includes("appportrait_")
        ? ["tile"]
        : tokens.includes("m_setDeferredSettings")
          ? ["settings"]
          : ["react"];
    return require;
  },
  request: (patchId, command, payload) => {
    requests.push(`${command} ${payload.bigArt}`);
    return Promise.resolve();
  },
  claimMember: (host, member, keys, replacement) => {
    const original = host[member];
    const next = replacement(original);
    Object.defineProperty(next, keys.marker, { value: true });
    Object.defineProperty(next, keys.original, { value: original });
    host[member] = next;
    return { ok: true, reclaimed: false };
  },
  releaseMember: (host, member, keys) => {
    if (host[member]?.[keys.marker]) host[member] = host[member][keys.original];
    return { ok: true };
  },
  memberClaimed: (host, member, keys) => host?.[member]?.[keys.marker] === true,
  subscribe: (patchId, listener) => {
    globals.publish = listener;
    return () => {
      globals.publish = null;
    };
  },
  publish: null,
};

const gate = new Function(
  ...Object.keys(globals),
  asset.slice(start, end) + "\nreturn createLibraryBadge();",
)(...Object.values(globals));

// Renders one tile through the claimed memo and reports what the icon row holds: the row's
// children in order, with our badge described by its text and colour and Valve's by identity.
const render = (app) => memo.type({ app });
const find = (node, predicate, found = []) => {
  if (!react.isValidElement(node)) return found;
  if (predicate(node)) found.push(node);
  react.Children.toArray(node.props?.children).forEach((child) => find(child, predicate, found));
  return found;
};
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
