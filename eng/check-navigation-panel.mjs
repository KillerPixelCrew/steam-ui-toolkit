// Exercise the emitted navigation-panel gate against an inert React fixture.
//
// The gate's risk is not its resolution — that is checked live and pinned by the patch's probe —
// but what it does to the tree once it has claimed the panel. This runs the emitted JavaScript,
// not the TypeScript source, so it proves the shipped bytes descend to the panel root, place
// entries against Valve's anchors, hide by route and by key, and hand the panel back on removal.
import assert from "node:assert/strict";
import {
  createReact,
  element,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
} from "./check-harness.mjs";

const asset = loadAsset();

// A React stand-in with the APIs the gate uses. A clone given no children gets an empty child list,
// which is what this fixture was written against; nothing in the gate needs a reconciler.
const react = createReact({ cloneReplacesChildren: true });

// Valve's panel, reduced to the facts the gate relies on: the root's source carries both tokens,
// and its children are entry elements keyed by descriptor key. Route entries share one component
// and carry a route; Power is an action entry carrying an action, as on the live client. Every
// entry gets the panel's own focus handler, which an added entry must receive as well.
const RouteEntry = () => null;
const ActionEntry = () => null;
const onGamepadFocus = () => {};
const entry = (key, route, label) =>
  route
    ? element(RouteEntry, { route, label, active: "if-within-route", onGamepadFocus }, key)
    : element(ActionEntry, { action: () => {}, label, onGamepadFocus }, key);
function PanelRoot() {
  return element("div", {
    role: "menu",
    children: [
      entry("home", "/library/home", "Home"),
      entry("library", "/library", "Library"),
      entry("store", "/steamweb", "Store"),
      entry("power", undefined, "Power"),
    ],
  });
}
// The gate matches the root by source, exactly as it does against the live client.
Object.defineProperty(PanelRoot, "toString", {
  value: () => 'function(){ "#MainMenu_Title"; "RunnningAppSeparator"; }',
});
// One intermediate component, so the descent has to render something to reach the root at all.
const Middle = () => element("div", { children: [element(PanelRoot, {})] });
const Outer = () => element(Middle, {});

const memo = { $$typeof: Symbol.for("react.memo"), type: Outer, compare: null };
const exports = { v_: memo };
Object.defineProperty(Outer, "toString", { value: () => 'function(){ "MainNavMenuContainer"; }' });

const requests = [];
const navigated = [];
let closed = 0;
let answer;
const globals = {
  // The react module the gate resolves is the fixture above, so createElement, cloneElement and
  // Children come from one place.
  getWebpackRuntime: () => {
    const require = (id) => (id === "menu" ? exports : react);
    require.findUnique = (tokens) =>
      tokens.includes("MainNavMenuContainer") ? ["menu"] : ["react"];
    return require;
  },
  createIconRenderer: () => (name) => element("svg", { name }),
  renderSteamGlyph: (_react, d) => (d.startsWith("M") ? element("svg", { d }) : null),
  nextActionGeneration: () => 1,
  request: (patchId, command, payload) => {
    requests.push(`${command} ${payload.id}`);
    return Promise.resolve(answer);
  },
  window: {
    SteamUIStore: {
      WindowStore: { MainWindowInstance: { MenuStore: { CloseSideMenus: () => closed++ } } },
    },
    tempNavStore: { m_history: { push: (route) => navigated.push(route) } },
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
  `${sharedFragments(asset)}\n${gateSource(asset, "createNavigationPanel", "navigationPanel")}`,
  "createNavigationPanel()",
);

// Renders the claimed memo the way React would — call the type, then keep going into what it
// returns — collecting each element's label on the way down. The label has to be read before the
// element is rendered, because an entry component renders to nothing; its identity lives on the
// element, which is exactly where the gate reads it too.
const visit = (node, found, depth = 0) => {
  if (!react.isValidElement(node) || depth > 24) return;
  const label = node.props?.label ?? node.props?.["aria-label"];
  if (typeof label === "string" && node.props?.role !== "menu") found.push(node);
  if (typeof node.type === "function") {
    visit(node.type(node.props), found, depth + 1);
    return;
  }
  react.Children.toArray(node.props?.children).forEach((child) => visit(child, found, depth + 1));
};
const elementsWithLabels = () => {
  const found = [];
  visit(element(memo.type, {}), found);
  return found;
};
const labels = () =>
  elementsWithLabels().map((node) => node.props.label ?? node.props["aria-label"]);

assert.deepEqual(labels(), ["Home", "Library", "Store", "Power"], "the fixture panel must render");

const installed = gate.install();
assert.ok(installed.ok, `install failed: ${installed.error}`);
assert.ok(gate.status().claimed, "the memo type must be claimed");

// Nothing published yet: a claimed panel with no instructions must render exactly Valve's own.
assert.deepEqual(labels(), ["Home", "Library", "Store", "Power"], "an empty claim must change nothing");
assert.deepEqual(
  gate.status().entries.map((item) => item.key),
  ["home", "library", "store", "power"],
  "the claim must observe Valve's own entries by descriptor key",
);

// Anchored by route, anchored by descriptor key, and unanchored.
globals.publish({
  items: [
    { id: "after-library", label: "After Library", after: "/library" },
    { id: "before-power", label: "Before Power", before: "power" },
    { id: "at-start", label: "At Start", position: "start" },
    { id: "no-anchor", label: "No Anchor" },
  ],
  hidden: [],
});
assert.deepEqual(
  labels(),
  ["At Start", "Home", "Library", "After Library", "Store", "Before Power", "Power", "No Anchor"],
  "entries must be placed against Valve's own by route and by key",
);

// An anchor Steam does not have is reported, not silently dropped.
globals.publish({ items: [{ id: "orphan", label: "Orphan", after: "/nonexistent" }], hidden: [] });
assert.ok(labels().includes("Orphan"), "an unplaceable entry must still appear");
assert.match(gate.status().lastOutcome, /orphaned=1/, "an unplaceable entry must be reported");

// Hiding, by route and by descriptor key.
globals.publish({ items: [], hidden: ["/steamweb", "power"] });
assert.deepEqual(labels(), ["Home", "Library"], "hiding must accept a route or a key");
assert.match(gate.status().lastOutcome, /hidden=2/);

// Hiding is applied before insertion, so an anchor cannot point at something already hidden.
globals.publish({
  items: [{ id: "after-store", label: "After Store", after: "/steamweb" }],
  hidden: ["/steamweb"],
});
assert.deepEqual(
  labels(),
  ["Home", "Library", "Power", "After Store"],
  "an entry anchored to a hidden one must fall to the end rather than reappear beside it",
);

// An added entry is drawn by Valve's own components, never an imitation: an entry with no route
// by the action entry, which activation reaches through the bridge under the entry's own id.
globals.publish({ items: [{ id: "activate-me", label: "Activate Me" }], hidden: ["power"] });
const actionRow = elementsWithLabels().find((node) => node.props.label === "Activate Me");
assert.equal(actionRow.type, ActionEntry, "an action entry must use Valve's own action entry");
assert.equal(actionRow.props.onGamepadFocus, onGamepadFocus, "it must take the panel's focus handler");
answer = { route: "/toolkit/page" };
actionRow.props.action();
await new Promise((resolve) => setImmediate(resolve));
assert.deepEqual(requests, ["activate activate-me"], "activation must carry the entry's own id");
assert.equal(closed, 1, "a route in the answer must close the menu first");
assert.deepEqual(navigated, ["/toolkit/page"], "and then be followed");

// An entry with a route is Valve's route entry, which matches the route for its active state and
// navigates with Valve's own action, so nothing goes through the bridge and the host never has to
// answer.
globals.publish({
  items: [{ id: "page", label: "Page", route: "/toolkit/settings", glyph: "M1 1h2v2H1Z", before: "power" }],
  hidden: [],
});
const routeRow = elementsWithLabels().find((node) => node.props.label === "Page");
assert.equal(routeRow.type, RouteEntry, "a route entry must use Valve's own route entry");
assert.equal(routeRow.props.route, "/toolkit/settings");
assert.equal(routeRow.props.active, "if-within-route", "it must be active on its page and below it");
assert.equal(routeRow.props.icon.props.d, "M1 1h2v2H1Z", "a host glyph must be drawn as its icon");
assert.deepEqual(
  labels(),
  ["Home", "Library", "Store", "Page", "Power"],
  "a route entry must be placed like any other",
);

// A route that is not a route is refused where it is published, so it can never reach Valve's entry.
globals.publish({
  items: [
    { id: "root", label: "Root", route: "/" },
    { id: "relative", label: "Relative", route: "wsgm" },
  ],
  hidden: [],
});
assert.deepEqual(labels(), ["Home", "Library", "Store", "Power"], "an invalid route must not be drawn");

const removed = gate.remove();
assert.ok(removed.ok, `remove failed: ${removed.error}`);
assert.equal(memo.type, Outer, "removal must hand back exactly what was displaced");
assert.ok(!gate.status().claimed);
assert.deepEqual(labels(), ["Home", "Library", "Store", "Power"], "removal must restore Valve's panel");

// Reinstalling after a removal must work, because a settings toggle does exactly that.
assert.ok(gate.install().ok, "the gate must be reinstallable");
assert.ok(gate.remove().ok);
assert.equal(memo.type, Outer);

console.log("Navigation panel: descent, anchoring, hiding, activation and restoration passed.");
