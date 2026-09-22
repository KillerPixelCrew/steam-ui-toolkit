// Exercise the emitted Home carousel gate against an inert React fixture.
//
// Resolution is checked live and pinned by the patch's probe. What this proves is what the shipped
// bytes do once they hold Home: that Home is found through the router's route list by content, that
// a Home already on screen is adopted into the claim and asked to render, that the carousel's
// `games` array is replaced for both the background and the box carousel, that the box carousel's
// overscan goes back to the component's default instead of the whole list, that the order follows
// the documented rules, that games on a disconnected library leave, that uninstalled games are
// greyed, that the list is not rebuilt when nothing changed, and that removal hands Home back, on
// the memo and on the adopted fiber alike.
import assert from "node:assert/strict";
import {
  createReact,
  element,
  find as findIn,
  gateSource,
  instantiate,
  loadAsset,
  named,
  sharedFragments,
  withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const subscriptions = [];
const react = createReact({
  useSyncExternalStore: (subscribe, snapshot) => {
    subscriptions.push(subscribe);
    return snapshot();
  },
});

// Steam's data. Timestamps are small numbers; only their order matters.
const overviews = new Map();
const overview = (appid, fields = {}) => {
  const value = {
    appid,
    local_per_client_data: { installed: fields.installed === true },
    rt_last_time_locally_played: fields.locally ?? 0,
    rt_last_time_played: fields.played ?? 0,
    rt_purchased_time: fields.purchased ?? 0,
    rt_last_time_played_or_installed: fields.installedAt ?? 0,
    minutes_playtime_forever: fields.playtime ?? 10,
    BIsShortcut: () => fields.shortcut === true,
    BIsMusicAlbum: () => fields.album === true,
    BIsApplicationOrTool: () => fields.tool === true,
  };
  overviews.set(appid, value);
  return value;
};
overview(1, { installed: true, locally: 900 }); // running
overview(10, { installed: true, locally: 800 });
overview(11, { installed: true, played: 700 }); // played on another device
overview(12, { installed: true, installedAt: 600 });
overview(13, { installed: true, installedAt: 650 });
overview(20, { purchased: 850 }); // new purchase, not installed
overview(21, { installed: true, purchased: 750 }); // new purchase, installed, unplayed
overview(30, { played: 500 }); // owned, uninstalled
overview(31, { purchased: 400 }); // owned, uninstalled, never played
overview(40, { installed: true, locally: 950 }); // on the card
overview(50, { installed: true, album: true });
overview(51, { installed: true, tool: true, playtime: 0 });
overview(60, { installed: false, shortcut: true, locally: 300 }); // a played shortcut
const appsOf = (...ids) => ids.map((id) => overviews.get(id));
const collections = {
  "local-install": { visibleApps: appsOf(1, 10, 11, 12, 13, 21, 40, 50, 51) },
  "recent-purchased": { visibleApps: appsOf(20, 21) },
  "my-games": { visibleApps: appsOf(1, 10, 11, 12, 13, 20, 21, 30, 31, 40) },
};
const windowFixture = {
  collectionStore: { GetCollection: (id) => collections[id] ?? null },
  appStore: { GetAppOverviewByAppID: (id) => overviews.get(id) ?? null },
};

// Steam's list, as its own hook hands it to the carousel: running game, separator, then its mix.
let steamGames = [1, 0, 40, 10, 20, 60];

// The components between Home and the virtualized grid, reduced to the props the gate reads.
const Background = () => null;
const BoxCarousel = () => null;
const RecentGames = (props) =>
  element(BoxCarousel, { games: props.games, overscan: props.games.length, name: "Recent games" });
const Carousel = withSource(
  (props) =>
    element("section", {
      children: [
        props.showBackground ? element(Background, { games: steamGames, refOnItemFocus: { current: null } }) : null,
        element("div", {
          children: [
            element("h2", { children: "Recent games" }),
            element(RecentGames, {
              games: steamGames,
              autoFocus: props.autoFocus,
              onItemFocus: () => {},
              showFeaturedItem: true,
            }),
          ],
        }),
      ],
    }),
  'function(){ "#Showcase_RecentGames"; RecentGamesContainer; }',
);
const CarouselMemo = react.memo(Carousel);
class ErrorBoundary {}
ErrorBoundary.prototype.isReactComponent = {};
const Home = withSource(
  () =>
    element("div", {
      children: [
        element(ErrorBoundary, { children: element(CarouselMemo, { autoFocus: true, showBackground: true }) }),
        element("div", { className: "tabs" }),
      ],
    }),
  'function(){ "HomeTabsActive"; "HomeActiveTab"; }',
);
const HomeMemo = react.memo(Home);

// SharedJSContext's React tree: an unrelated route list first, then the router switch whose children
// are the routes, so the gate has to match the list by content rather than take the first one.
//
// Home is already on screen, as Big Picture starts on it: the switch is a class whose instance can
// be asked to render, and under it the Home fiber and its alternate both cache Home's original
// function as their `type`, the way React resolves a memo once at mount.
const route = (path, page) => element(() => null, { path, children: page }, path);
const homeElement = element(HomeMemo, {});
const switchInstance = {
  isReactComponent: {},
  renders: 0,
  forceUpdate() {
    switchInstance.renders++;
  },
};
const homeFiber = {
  elementType: HomeMemo,
  type: Home,
  memoizedProps: homeElement.props,
  child: null,
  sibling: null,
  return: null,
  alternate: null,
};
const homeAlternate = { ...homeFiber, alternate: homeFiber };
homeFiber.alternate = homeAlternate;
const providerFiber = { memoizedProps: { value: 1, children: homeElement }, child: homeFiber, sibling: null, return: null };
homeFiber.return = providerFiber;
homeAlternate.return = providerFiber;
const switchFiber = {
  memoizedProps: {
    children: [
      route("/settings", element(() => null, {})),
      route("/library/home", homeElement),
      route("/library", element(() => null, {})),
    ],
  },
  stateNode: switchInstance,
  child: providerFiber,
  sibling: null,
  return: null,
};
providerFiber.return = switchFiber;
const rootFiber = {
  memoizedProps: { children: [route("/desktop", element(() => null, {})), route("/a", null), route("/b", null)] },
  child: { memoizedProps: {}, child: switchFiber, sibling: null },
  sibling: null,
};
// The container names the fiber React created the root with; the root's `current` is the tree on
// screen, and the gate must read that one.
const staleRootFiber = { stateNode: { current: rootFiber }, memoizedProps: {}, child: null, sibling: null };
const documentFixture = {
  getElementById: (id) => (id === "root" ? { __reactContainer$fixture: staleRootFiber } : null),
  body: { children: [{ id: "unrelated" }] },
};

// mobx-react-lite's useObserver: runs the function and returns what it returned.
const observed = [];
const useObserver = withSource(
  function (fn, name) {
    observed.push(name);
    return fn();
  },
  'function _e(X,ge){return ge===void 0&&(ge="observed"),Oe(X,ge)}',
);
const observerExports = { q3: useObserver, PA: () => null };

const requests = [];
const globals = {
  window: windowFixture,
  document: documentFixture,
  getWebpackRuntime: () => {
    const require = (id) => (id === "observer" ? observerExports : react);
    require.findUnique = (tokens) =>
      tokens.some((token) => token.includes("mobx-react-lite"))
        ? ["observer"]
        : tokens.includes("HomeTabsActive")
          ? ["home"]
          : ["react"];
    return require;
  },
  request: (patchId, command, payload) => {
    requests.push({ command, payload });
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
  `${sharedFragments(asset)}\n${gateSource(asset, "createHomeCarousel", "homeCarousel")}`,
  "createHomeCarousel()",
);

const find = (node, predicate) => findIn(react, node, predicate);

// Renders Home through its (claimed) memo, then the carousel it holds, then the box carousel.
const renderCarousel = () => {
  const home = HomeMemo.type({});
  const carousel = find(home, (node) => node.type === CarouselMemo || named("SteamUiHomeCarousel")(node))[0];
  assert.ok(carousel, "Home must still render a carousel");
  const tree = carousel.type.type(carousel.props);
  const background = find(tree, (node) => node.type === Background)[0];
  const recent = find(tree, (node) => node.type === RecentGames || named("SteamUiRecentGames")(node))[0];
  const rendered = recent.type(recent.props);
  const box = find(rendered, (node) => node.type === BoxCarousel)[0];
  const style = find(rendered, (node) => node.type === "style")[0];
  // The fixture's createElement stores varargs children as an array, so the rule text is joined.
  return { carousel, background, box, css: [style?.props.children ?? ""].flat().join("") };
};

// Before the gate: Steam's own list and the whole-list overscan.
let view = renderCarousel();
assert.equal(view.carousel.type, CarouselMemo);
assert.deepEqual(view.box.props.games, steamGames);
assert.equal(view.box.props.overscan, steamGames.length, "the fixture must reproduce Home's overscan");

const installed = gate.install();
assert.ok(installed.ok, `install failed: ${installed.error}`);
assert.ok(gate.status().claimed, "Home's memo type must be claimed");
assert.ok(gate.status().tracking, "Steam's observer hook must resolve");

// The Home on screen is adopted: both fibers now call the wrapper, the memo's bail-out cannot skip
// the render because the cached props no longer compare equal, and the switch was asked to render.
assert.equal(installed.adopted, 1);
assert.equal(homeFiber.type, HomeMemo.type, "a mounted Home must be adopted into the claim");
assert.equal(homeAlternate.type, HomeMemo.type, "its alternate must be adopted as well");
assert.notEqual(homeFiber.memoizedProps, homeElement.props, "the adopted fiber must not bail out of its next render");
assert.equal(homeFiber.memoizedProps.__steamUiAdoptedProps, homeElement.props, "the real props stay reachable");
assert.equal(switchInstance.renders, 1, "the route switch must be asked to render the adopted Home");
assert.deepEqual(gate.status().mounted, { adopted: 1, scheduled: true, stale: 0 });
assert.equal(
  find(homeFiber.type(homeElement.props), named("SteamUiHomeCarousel")).length,
  1,
  "the adopted Home must draw the wrapped carousel on that render",
);

// Nothing published yet: nothing is disconnected and uninstalled games stay out.
view = renderCarousel();
assert.ok(named("SteamUiHomeCarousel")(view.carousel), "the carousel must be wrapped");
assert.deepEqual(
  view.box.props.games,
  [1, 0, 40, 20, 10, 21, 11, 60, 13, 12],
  "running prefix, then played and purchased by time, then never played by install time",
);
assert.equal(view.background.props.games, view.box.props.games, "the background must follow the same list");
assert.equal(view.box.props.overscan, undefined, "overscan must go back to the component's default");
assert.match(view.css, /\[data-id="20"\] img/, "a purchase not yet installed must be greyed");
assert.doesNotMatch(view.css, /data-id="21"/, "an installed purchase must not be greyed");
assert.ok(observed.includes("SteamUiHomeCarousel"), "the inputs must be read inside Steam's observer");
assert.equal(requests.length, 1);
assert.deepEqual(requests[0].payload, {
  items: 10,
  purchases: 2,
  installed: 6,
  uninstalled: 0,
  excluded: 0,
  tracking: true,
  fallback: false,
});

// Unchanged inputs: the same list object, and no second report.
const again = renderCarousel();
assert.equal(again.box.props.games, view.box.props.games, "an unchanged render must not rebuild the list");
assert.equal(requests.length, 1, "an unchanged list must not be reported again");

// The card is pulled and uninstalled games are wanted. The newest played game left with the card,
// so the pin moves to the newest played game still attached, ahead of the newer purchase.
let heard = 0;
subscriptions.at(-1)(() => heard++);
globals.publish({ includeUninstalled: true, disconnectedAppIds: [40] });
assert.equal(heard, 1, "a publication must re-render the carousel");
view = renderCarousel();
assert.deepEqual(view.box.props.games, [1, 0, 10, 20, 21, 11, 60, 13, 12, 30, 31]);
assert.match(view.css, /data-id="30"/);
assert.match(view.css, /data-id="31"/);
assert.doesNotMatch(view.css, /data-id="40"/, "a disconnected game must not appear at all");
assert.equal(requests.at(-1).payload.excluded, 1);
assert.equal(requests.at(-1).payload.uninstalled, 2);

// An uninstall recomputes Steam's collection; the new collection object is what rebuilds the list.
overviews.get(11).local_per_client_data.installed = false;
collections["local-install"] = { visibleApps: appsOf(1, 10, 12, 13, 21, 40, 50, 51) };
view = renderCarousel();
assert.deepEqual(view.box.props.games, [1, 0, 10, 20, 21, 60, 13, 12, 11, 30, 31]);
assert.match(view.css, /data-id="11"/, "an uninstalled game must move to the greyed section");

// Nothing on the attached libraries: Steam's own list stands in rather than a blank Home.
globals.publish({ includeUninstalled: false, disconnectedAppIds: [10, 12, 13, 20, 21, 40, 60] });
view = renderCarousel();
assert.deepEqual(view.box.props.games, steamGames);
assert.equal(view.css, "", "the fallback must not grey Steam's own list");
assert.equal(requests.at(-1).payload.fallback, true);

// A malformed publication is bounded and filtered rather than trusted.
globals.publish({ includeUninstalled: "yes", disconnectedAppIds: [-1, 2.5, "40", 40] });
assert.equal(gate.status().includeUninstalled, false);
assert.equal(gate.status().disconnected, 1);

// Removal hands Home back, and a carousel still on screen hands back Steam's own list.
const mounted = renderCarousel().carousel;
const removed = gate.remove();
assert.ok(removed.ok, `remove failed: ${removed.error}`);
assert.equal(HomeMemo.type, Home, "removal must restore Home's own type");
assert.ok(!gate.status().claimed);
assert.equal(homeFiber.type, Home, "removal must hand the adopted fiber back to Home's own function");
assert.equal(homeAlternate.type, Home);
assert.equal(switchInstance.renders, 1, "removal must not ask for a render; Home's next render is Steam's");
assert.deepEqual(gate.status().mounted, { adopted: 0, scheduled: false, stale: 0 });
const stale = mounted.type.type(mounted.props);
assert.equal(find(stale, (node) => node.type === RecentGames).length, 1, "a removed wrapper must pass through");
view = renderCarousel();
assert.equal(view.carousel.type, CarouselMemo);
assert.equal(view.box.props.overscan, steamGames.length);

assert.ok(gate.install().ok, "the gate must be reinstallable");
view = renderCarousel();
assert.equal(view.box.props.overscan, undefined);
assert.equal(homeFiber.type, HomeMemo.type, "a reinstall must adopt the mounted Home again");
assert.equal(switchInstance.renders, 2);
assert.ok(gate.remove().ok);
assert.equal(HomeMemo.type, Home);
assert.equal(homeFiber.type, Home);

console.log("Home carousel gate: ok");
