// Exercise the emitted library details stat and the shared JSX-runtime claim against inert fixtures.
//
// Resolution is checked live and pinned by the patch's probe. What this proves is what the shipped
// bytes do once they hold the runtime: that only the play bar's stats row gets the stat, built from
// Valve's classes and Steam's localized label; that it names the published library, the internal
// label or nothing by the badge's rules and dims a library that is not installed; that it is added
// once; that another element transform registered through the "elements" gate coexists on the one
// claim; and that the runtime is handed back only when the last transform leaves.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const slice = (from, to) => {
  const start = asset.indexOf(from);
  const end = asset.indexOf(to, start);
  assert.ok(start >= 0 && end > start, `${from} must be emitted`);
  return asset.slice(start, end);
};
const ownership = slice("const defineHidden", "const transportReply");
const libraries = slice("const readLibraryBadgeState", 'registerGate("libraryDetails"');
const elements = slice("function createElementsGate()", 'registerGate("elements"');

const ElementMarker = Symbol("element");
const element = (type, props, key = null) => ({ [ElementMarker]: true, type, props: props ?? {}, key });
const react = {
  createElement(type, props, ...children) {
    const { key = null, ...rest } = props ?? {};
    if (children.length) rest.children = children.length === 1 ? children[0] : children;
    return element(type, rest, key);
  },
  isValidElement: (value) => !!value && typeof value === "object" && value[ElementMarker] === true,
};
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
const built = new Function(
  ...Object.keys(globals),
  `${ownership}\n${libraries}\n${elements}\nreturn { details: createLibraryDetails(), gate: createElementsGate() };`,
)(...Object.values(globals));
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
