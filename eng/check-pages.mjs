// Exercise the emitted custom-page gate against an inert React fixture.
//
// Resolution is checked live and pinned by the patch probe. What this proves is what the gate does
// once it holds the router: that it finds the route list by content rather than by an index chain,
// that overrides go in front of Steam's routes and additions behind them (which is what makes them
// behave differently under a first-match switch), that pages are built with Steam's own Route, and
// that removal hands the router back untouched.
import assert from "node:assert/strict";
import {
  createReact,
  element,
  gateSource,
  instantiate,
  loadAsset,
  slice,
  sharedFragments,
} from "./check-harness.mjs";

const asset = loadAsset();
const react = createReact();

// Steam's back-stack Route, matched by the markers the gate uses. The body is the live one read
// from the client on 2026-09-24, minified locals and all: the fingerprint this replaced described
// the code between the two markers and assumed a one-character local, so it stopped matching when
// the client emitted `be`, and the fixture said nothing because it had a one-character local too.
const SteamRoute = function () {
  return null;
};
Object.defineProperty(SteamRoute, "toString", {
  value: () =>
    'function Y(he){const{children:Z,...q}=he,pe=be=>typeof Z==="function"?Z(be):Z;' +
    "return(0,h.jsx)(D.qh,{...q,children:be=>(0,h.jsx)(Q,{routePath:be.match?.path," +
    "disabled:!be.match,children:pe(be)})})}",
});
// The route-tracking component that really does sit in that module. It names the same prop but
// never reads a match, so it proves the second marker is doing work rather than riding along.
const NotARoute = function () {
  return null;
};
Object.defineProperty(NotARoute, "toString", {
  value: () =>
    "function Q(he){const{children:Z,routePath:q,disabled:pe}=he;" +
    "return E.y.ReportRouteMatch(q),(0,h.jsx)(k.Provider,{value:!0,children:Z})}",
});
// An alias of the Route under a second export name. Counting by value rather than by name is what
// keeps a re-export from reading as ambiguity and refusing an otherwise compatible client.
const SteamRouteAlias = SteamRoute;

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
Object.defineProperty(RouteSwitch, "toString", {
  value: () => 'function fd(){ "computedMatch"; "TopLevelTransition"; }',
});
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
const routeSwitchNode = {
  type: RouteSwitch,
  elementType: RouteSwitch,
  child: null,
  sibling: null,
  alternate: { type: RouteSwitch },
};
const rootNode = { type: Router, elementType: memo, child: routeSwitchNode, sibling: null };
globalThis.document = {
  getElementById: (id) => (id === "root" ? { __reactContainer$fixture: rootNode } : null),
};

// Whether the registry still finds the Route export. The gate must work either way.
let routeLookupWorks = true;

const globals = {
  getWebpackRuntime: () => {
    const require = (id) =>
      id === "backstack" ? { Jh: SteamRoute, Kp: SteamRouteAlias, other: NotARoute } : react;
    require.findUnique = (tokens) =>
      tokens.includes("router-backstack")
        ? ["backstack"]
        : tokens.includes("TopLevelTransition") && tokens.includes("Settings.Root()")
          ? ["router"]
          : ["react"];
    require.count = () => 1;
    // The shared resolver's own semantics: aliases of one value count once, and no fit or two
    // distinct fits throws rather than guessing.
    require.exported = (tokens, predicate) => {
      // Simulates the 2026-09-24 client, where the Route export stopped matching its fingerprint.
      if (!routeLookupWorks && tokens.includes("router-backstack"))
        throw new Error("Steam export absent: router-backstack");
      const exports = require(require.findUnique(tokens)[0]);
      const fits = new Set();
      for (const name of Object.keys(exports)) if (predicate(exports[name])) fits.add(exports[name]);
      if (fits.size !== 1) throw new Error(`Steam export ${fits.size ? "ambiguous" : "absent"}`);
      return [...fits][0];
    };
    return require;
  },
  createIconRenderer: () => () => null,
  request: () => Promise.resolve(),
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
  `${sharedFragments(asset)}\n${slice(asset, "const steamPageRenderers", "function createPageHost()")}\n${gateSource(asset, "createPageHost", "pages")}`,
  "createPageHost()",
);

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

// That render went through the claimed switch, so the Route now comes from Steam's own route list
// rather than from the registry lookup.
assert.equal(gate.status().routeSource, "borrowed", "the Route must be borrowed once Steam renders");
assert.match(gate.status().lastOutcome, /route=borrowed/);

// An addition.
globals.publish({ pages: [{ id: "artwork", path: "/wsgm/artwork", title: "Artwork" }] });
const added = selected("/wsgm/artwork");
assert.ok(added, "a registered page must resolve");
assert.equal(added.type, SteamRoute, "a page must be built with Steam's own back-stack Route");
assert.equal(
  selected("/settings")?.props.path,
  "/settings",
  "an addition must not shadow Steam's routes",
);
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
assert.equal(
  selected("/settings")?.props.path,
  "/settings",
  "removal must restore Steam's routing",
);

assert.ok(gate.install().ok, "the gate must be reinstallable");
assert.ok(gate.remove().ok);
assert.equal(memo.type, Router);

// The 2026-09-24 outage, as a fixture: the registry no longer finds the Route export at all. The
// gate must still install and must still build pages, because the Route it needs is in the route
// list Steam handed it. Refusing here is what left Change Artwork, Import games and the in-Steam
// settings page all rendering an empty client on a client that was otherwise entirely compatible.
routeLookupWorks = false;
const blind = instantiate(
  globals,
  `${sharedFragments(asset)}\n${slice(asset, "const steamPageRenderers", "function createPageHost()")}\n${gateSource(asset, "createPageHost", "pages")}`,
  "createPageHost()",
);

const blindInstall = blind.install();
assert.ok(
  blindInstall.ok,
  `install must survive a Route lookup that finds nothing: ${blindInstall.error}`,
);
assert.ok(blind.status().claimed, "the router must still be claimed");
assert.match(blind.status().routeLookupError, /router-backstack/u, "the failed lookup must be named");

globals.publish({ pages: [{ id: "artwork", path: "/wsgm/artwork", title: "Artwork" }] });
const blindPage = selected("/wsgm/artwork");
assert.ok(blindPage, "a page must register with no Route export to be found");
assert.equal(blindPage.type, SteamRoute, "the borrowed Route must be Steam's own back-stack Route");
assert.equal(blind.status().routeSource, "borrowed");
assert.ok(blind.status().routeResolved, "a borrowed Route counts as resolved");

assert.ok(blind.remove().ok);
assert.equal(memo.type, Router);
assert.equal(blind.status().routeSource, "none", "removal must drop the borrowed Route");
routeLookupWorks = true;

console.log(
  "Custom pages: borrowed Route, route-list discovery, override vs addition, install without a " +
    "Route lookup, validation and restoration passed.",
);
