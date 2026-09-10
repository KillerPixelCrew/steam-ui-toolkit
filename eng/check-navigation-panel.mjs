// Exercise the emitted navigation-panel gate against an inert React fixture.
//
// The gate's risk is not its resolution — that is checked live and pinned by the patch's probe —
// but what it does to the tree once it has claimed the panel. This runs the emitted JavaScript,
// not the TypeScript source, so it proves the shipped bytes descend to the panel root, place
// entries against Valve's anchors, hide by route and by key, and hand the panel back on removal.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createNavigationPanel()");
assert.ok(start >= 0, "the emitted asset must contain the navigation gate");
const end = asset.indexOf('registerGate("navigationPanel"', start);
assert.ok(end > start, "the navigation gate must register itself");

// A React stand-in with exactly the four APIs the gate uses. Elements are plain objects, so a
// "render" here is a call and nothing more; nothing in the gate needs a reconciler.
const ElementMarker = Symbol("element");
const element = (type, props, key = null) => ({
  [ElementMarker]: true,
  type,
  props: props ?? {},
  key,
});
const react = {
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

// Valve's panel, reduced to the two facts the gate matches on: the root's source carries both
// tokens, and its children are entry elements keyed by descriptor key and carrying a route.
const entry = (key, route, label) => element(() => null, { route, label }, key);
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
const globals = {
  getWebpackRuntime: () => {
    const require = (id) => (id === "menu" ? exports : { createElement: react.createElement });
    require.findUnique = (tokens) =>
      tokens.includes("MainNavMenuContainer") ? ["menu"] : ["react"];
    return require;
  },
  createIconRenderer: () => (name) => element("svg", { name }),
  request: (patchId, command, payload) => {
    requests.push(`${command} ${payload.id}`);
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
// The react module the gate resolves is the fixture above, not the stub the resolver returns for
// anything else, so createElement/cloneElement/Children come from one place.
globals.getWebpackRuntime = () => {
  const require = (id) => (id === "menu" ? exports : react);
  require.findUnique = (tokens) => (tokens.includes("MainNavMenuContainer") ? ["menu"] : ["react"]);
  return require;
};

const gate = new Function(
  ...Object.keys(globals),
  asset.slice(start, end) + "\nreturn createNavigationPanel();",
)(...Object.values(globals));

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

// Activation reaches the bridge under the entry's own id.
globals.publish({ items: [{ id: "activate-me", label: "Activate Me" }], hidden: [] });
elementsWithLabels()
  .find((node) => node.props["aria-label"] === "Activate Me")
  .props.onClick();
assert.deepEqual(requests, ["activate activate-me"], "activation must carry the entry's own id");

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
