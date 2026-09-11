// Exercise the emitted Home carousel gate against an inert React fixture.
//
// Resolution is checked live and pinned by the patch's probe. What this proves is what the shipped
// bytes do once they hold Home: that Home is found through the router's route list by content, that
// the carousel's `games` array is replaced for both the background and the box carousel, that the
// box carousel's overscan goes back to the component's default instead of the whole list, that the
// order follows the documented rules, that games on a disconnected library leave, that uninstalled
// games are greyed, that the list is not rebuilt when nothing changed, and that removal hands Home
// back.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createHomeCarousel()");
assert.ok(start >= 0, "the emitted asset must contain the home carousel gate");
const end = asset.indexOf('registerGate("homeCarousel"', start);
assert.ok(end > start, "the home carousel gate must register itself");

const ElementMarker = Symbol("element");
const MemoType = Symbol.for("react.memo");
const element = (type, props, key = null) => ({ [ElementMarker]: true, type, props: props ?? {}, key });
const subscriptions = [];
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
  memo: (type, compare) => ({ $$typeof: MemoType, type, compare: compare ?? null }),
  useSyncExternalStore: (subscribe, snapshot) => {
    subscriptions.push(subscribe);
    return snapshot();
  },
};
const withSource = (fn, source) => {
  Object.defineProperty(fn, "toString", { value: () => source });
  return fn;
};

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

// Big Picture's React root lives in its popup window's document, not in SharedJSContext's #root:
// the router switch whose children are the routes is only reachable through that window. The
// SharedJSContext root holds an unrelated route list, so a gate that searched it alone finds nothing.
const route = (path, page) => element(() => null, { path, children: page }, path);
const switchFiber = {
  memoizedProps: {
    children: [
      route("/settings", element(() => null, {})),
      route("/library/home", element(HomeMemo, {})),
      route("/library", element(() => null, {})),
    ],
  },
  child: null,
  sibling: null,
};
const popupRoot = { memoizedProps: {}, child: { memoizedProps: {}, child: switchFiber, sibling: null }, sibling: null };
const desktopRoot = {
  memoizedProps: { children: [route("/desktop", element(() => null, {})), route("/a", null), route("/b", null)] },
  child: null,
  sibling: null,
};
const documentFixture = {
  getElementById: (id) => (id === "root" ? { __reactContainer$fixture: desktopRoot } : null),
};
windowFixture.SteamUIStore = {
  WindowStore: {
    GamepadUIMainWindowInstance: {
      BrowserWindow: {
        document: {
          getElementById: (id) => (id === "popup_target" ? { __reactContainer$popup: popupRoot } : null),
        },
      },
    },
  },
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
  asset.slice(start, end) + "\nreturn createHomeCarousel();",
)(...Object.values(globals));

const find = (node, predicate, found = []) => {
  if (!react.isValidElement(node)) return found;
  if (predicate(node)) found.push(node);
  react.Children.toArray(node.props?.children).forEach((child) => find(child, predicate, found));
  return found;
};
const named = (name) => (node) =>
  (typeof node.type === "function" && node.type.name === name) ||
  (node.type?.$$typeof === MemoType && node.type.type?.name === name);

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
const stale = mounted.type.type(mounted.props);
assert.equal(find(stale, (node) => node.type === RecentGames).length, 1, "a removed wrapper must pass through");
view = renderCarousel();
assert.equal(view.carousel.type, CarouselMemo);
assert.equal(view.box.props.overscan, steamGames.length);

assert.ok(gate.install().ok, "the gate must be reinstallable");
view = renderCarousel();
assert.equal(view.box.props.overscan, undefined);
assert.ok(gate.remove().ok);
assert.equal(HomeMemo.type, Home);

console.log("Home carousel gate: ok");
