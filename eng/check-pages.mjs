// Exercise the emitted custom-page gate against an inert React fixture.
//
// Resolution is checked live and pinned by the patch probe. What this proves is what the gate does
// once it holds the router: that it finds the route list by content rather than by an index chain,
// that overrides go in front of Steam's routes and additions behind them (which is what makes them
// behave differently under a first-match switch), that pages are built with Steam's own Route, and
// that removal hands the router back untouched.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createPageHost()");
assert.ok(start >= 0, "the emitted asset must contain the page gate");
const end = asset.indexOf('registerGate("pages"', start);
assert.ok(end > start, "the page gate must register itself");

const ElementMarker = Symbol("element");
const element = (type, props, key = null) => ({ [ElementMarker]: true, type, props: props ?? {}, key });
const react = {
  createElement(type, props, ...children) {
    const { key = null, ...rest } = props ?? {};
    return element(type, children.length ? { ...rest, children } : rest, key);
  },
  cloneElement: (source, props, ...children) =>
    element(
      source.type,
      children.length ? { ...source.props, ...props, children } : { ...source.props, ...props },
      source.key,
    ),
  isValidElement: (value) => !!value && typeof value === "object" && value[ElementMarker] === true,
  Children: {
    toArray: (children) =>
      (Array.isArray(children) ? children : children === undefined ? [] : [children]).filter(
        (child) => child !== null && child !== undefined && child !== false,
      ),
  },
};

// Steam's back-stack Route, matched by the fingerprint the gate uses.
const SteamRoute = function () {
  return null;
};
Object.defineProperty(SteamRoute, "toString", {
  value: () => "function(e){ return jsx(G,{routePath:e.match?.path,disabled:false}) }",
});
// A decoy in the same module, so "exactly one export matches" is actually exercised.
const NotARoute = function () {
  return null;
};

const route = (path) => element(SteamRoute, { path }, path);
const stockRoutes = [
  route("/library/home"),
  route("/library"),
  route("/settings"),
  route("/media"),
];

// Steam's switch: takes the FIRST matching child. The fixture reproduces that rule, because it is
// the reason override and addition are different operations.
function RouteSwitch(props) {
  const first = props.children.find((child) => child.props?.path === props.location);
  return first ?? null;
}
// One intermediate component, so the gate has to descend rather than read a fixed index.
const Middle = (props) => element("div", { children: [element(RouteSwitch, props)] });
function Router(props) {
  return element(Middle, { location: props.location, children: stockRoutes.slice() });
}
Object.defineProperty(Router, "toString", {
  value: () => 'function(){ "Settings.Root()"; "TopLevelTransition"; }',
});

const memo = { $$typeof: Symbol.for("react.memo"), type: Router, compare: null };
// The gate finds the router through the React root, so the fixture provides one.
const rootNode = { type: Router, elementType: memo, child: null, sibling: null };
globalThis.document = {
  getElementById: (id) => (id === "root" ? { __reactContainer$fixture: rootNode } : null),
};

const globals = {
  getWebpackRuntime: () => {
    const require = (id) => (id === "backstack" ? { Jh: SteamRoute, other: NotARoute } : react);
    require.findUnique = (tokens) =>
      tokens.includes("router-backstack")
        ? ["backstack"]
        : tokens.includes("TopLevelTransition") && tokens.includes("Settings.Root()")
          ? ["router"]
          : ["react"];
    require.count = () => 1;
    return require;
  },
  createIconRenderer: () => () => null,
  request: () => Promise.resolve(),
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
  asset.slice(start, end) + "\nreturn createPageHost();",
)(...Object.values(globals));

// Resolves a location through the claimed router the way React would, stopping at the Route the
// switch selected — a Route renders its own page content, which is not what is being asserted here.
const selected = (location) => {
  const render = (node, depth = 0) => {
    if (!react.isValidElement(node) || depth > 24) return null;
    if (node.type === SteamRoute) return node;
    if (typeof node.type === "function") return render(node.type(node.props), depth + 1);
    for (const kid of react.Children.toArray(node.props?.children)) {
      const found = render(kid, depth + 1);
      if (found) return found;
    }
    return null;
  };
  return render(element(memo.type, { location }));
};

assert.equal(selected("/settings")?.props.path, "/settings", "the fixture router must resolve");
assert.equal(selected("/wsgm/artwork"), null, "an unregistered path must resolve to nothing");

const installed = gate.install();
assert.ok(installed.ok, `install failed: ${installed.error}`);
assert.ok(gate.status().claimed, "the router memo type must be claimed");
assert.ok(gate.status().routeResolved, "Steam's own Route must have been resolved");

// Claimed but with nothing published: Steam's routes must resolve exactly as before.
assert.equal(selected("/settings")?.props.path, "/settings", "an empty claim must change nothing");

// An addition.
globals.publish({ pages: [{ id: "artwork", path: "/wsgm/artwork", title: "Artwork" }] });
const added = selected("/wsgm/artwork");
assert.ok(added, "a registered page must resolve");
assert.equal(added.type, SteamRoute, "a page must be built with Steam's own back-stack Route");
assert.equal(selected("/settings")?.props.path, "/settings", "an addition must not shadow Steam's routes");
assert.match(gate.status().lastOutcome, /additions=1/);

// An addition must NOT win against a Steam route of the same path, because it goes behind them.
globals.publish({ pages: [{ id: "shadow", path: "/settings", title: "Not Settings" }] });
assert.equal(
  selected("/settings").key,
  "/settings",
  "an addition must lose to Steam's own route under a first-match switch",
);

// An override must win, because it goes in front.
globals.publish({ pages: [{ id: "mine", path: "/settings", title: "Mine", override: true }] });
assert.notEqual(
  selected("/settings").key,
  "/settings",
  "an override must win against Steam's own route",
);
assert.match(gate.status().lastOutcome, /overrides=1/);

// Malformed declarations are dropped rather than registered.
globals.publish({
  pages: [
    { id: "ok", path: "/wsgm/ok", title: "Fine" },
    { id: "relative", path: "wsgm/no-slash", title: "Relative" },
    { id: "catchall", path: "/", title: "Everything" },
    { id: 7, path: "/wsgm/bad-id", title: "Bad id" },
    { id: "no-title", path: "/wsgm/no-title" },
  ],
});
assert.equal(gate.status().pages, 1, "only the well-formed page may register");
assert.ok(selected("/wsgm/ok"), "the well-formed page must still resolve");
assert.equal(selected("/")?.props.path ?? null, null, "a catch-all path must never be registered");

// Steam's route list is observed, which is what the status reports.
assert.equal(gate.status().routeCount, 4, "the gate must report the routes it found");

const removed = gate.remove();
assert.ok(removed.ok, `remove failed: ${removed.error}`);
assert.equal(memo.type, Router, "removal must hand back exactly what was displaced");
assert.ok(!gate.status().claimed);
assert.equal(selected("/wsgm/ok"), null, "removal must unregister every page");
assert.equal(selected("/settings")?.props.path, "/settings", "removal must restore Steam's routing");

assert.ok(gate.install().ok, "the gate must be reinstallable");
assert.ok(gate.remove().ok);
assert.equal(memo.type, Router);

console.log("Custom pages: route-list discovery, override vs addition, validation and restoration passed.");
