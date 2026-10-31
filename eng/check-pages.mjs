// Exercise the emitted custom-page gate against an inert React fixture.
//
// The probe is the gate's own find: a router module with no router mounted is not ready, a missing
// router module is incompatible, and an installed gate answers from what it holds. What this proves
// beyond that is what the gate does once it holds the router: that it finds the route list by
// content rather than by an index chain, that overrides go in front of Steam's routes and additions
// behind them (which is what makes them behave differently under a first-match switch), that pages
// are built with Steam's own Route, and that removal hands the router and the switch back untouched,
// including a router mounted after install.
import assert from "node:assert/strict";
import {
    assertRemoveRetries,
    createReact,
    element,
    failingHost,
    fragment,
    gateSource,
    instantiate,
    loadAsset,
    sharedFragments,
    withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const react = createReact();

// Steam's back-stack Route, with the body read from the client on 2026-09-24, minified locals and
// all. The gate borrows it from the route list and verifies it against the markers.
const SteamRoute = function () {
    return null;
};
withSource(
    SteamRoute,
    'function Y(he){const{children:Z,...q}=he,pe=be=>typeof Z==="function"?Z(be):Z;' +
        "return(0,h.jsx)(D.qh,{...q,children:be=>(0,h.jsx)(Q,{routePath:be.match?.path," +
        "disabled:!be.match,children:pe(be)})})}",
);

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
withSource(RouteSwitch, 'function fd(){ "computedMatch"; "TopLevelTransition"; }');
// One intermediate component, so the gate has to descend rather than read a fixed index.
const Middle = (props) => element("div", { children: [element(RouteSwitch, props)] });
function Router(props) {
    return element(Middle, { location: props.location, children: stockRoutes.slice() });
}
withSource(Router, 'function(){ "Settings.Root()"; "TopLevelTransition"; }');

const { host: memo, failNext } = failingHost({
    $$typeof: Symbol.for("react.memo"),
    type: Router,
    compare: null,
});
// The gate finds the router through the React root, so the fixture provides one.
const routeSwitchNode = {
    type: RouteSwitch,
    elementType: RouteSwitch,
    child: null,
    sibling: null,
    alternate: { type: RouteSwitch },
};
const rootNode = { type: Router, elementType: memo, child: routeSwitchNode, sibling: null };
const mountedDocument = {
    getElementById: (id) => (id === "root" ? { __reactContainer$fixture: rootNode } : null),
};
globalThis.document = mountedDocument;

let routerModule = true;
const globals = {
    getWebpackRuntime: () => {
        const require = () => react;
        require.findUnique = (tokens) =>
            tokens.includes("TopLevelTransition") && tokens.includes("Settings.Root()")
                ? routerModule
                    ? ["router"]
                    : null
                : ["react"];
        require.count = () => 1;
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

const { gate, registerSteamPageRenderer } = instantiate(
    globals,
    `${sharedFragments(asset)}\n${gateSource(asset, "gates/pages.ts")}`,
    "({ gate: createPageHost(), registerSteamPageRenderer })",
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

// The router module is there and nothing is mounted: Big Picture closed or still loading.
globalThis.document = { getElementById: () => null };
let probe = gate.probe();
assert.equal(probe.outcome, "notReady", "an unmounted router must be not ready");
assert.equal(probe.facts.routerModule, 1);
assert.equal(probe.facts.routerFound, 0);
// No router module at all is a client that moved.
globalThis.document = mountedDocument;
routerModule = false;
probe = gate.probe();
assert.equal(probe.outcome, "incompatible", "a missing router module must be incompatible");
assert.equal(probe.facts.routerModule, 0);
routerModule = true;
probe = gate.probe();
assert.equal(probe.outcome, "ready", `the mounted router must probe ready: ${probe.reason}`);
assert.equal(probe.facts.routerFound, 1);
assert.equal(probe.facts.routeSwitch, 1);
assert.equal(probe.facts.claimable, true);
assert.equal(memo.type, Router, "a probe must not claim the router");
assert.equal(routeSwitchNode.type, RouteSwitch, "a probe must not adopt the switch");

const installed = gate.install();
assert.ok(installed.ok, `install failed: ${installed.error}`);
assert.ok(gate.status().claimed, "the router memo type must be claimed");
assert.equal(gate.status().held, true, "the standard status must show every claim held");
// While installed the probe answers from what the gate holds: the find that sees its own claims
// would otherwise call a holding gate incompatible.
globalThis.document = { getElementById: () => null };
assert.equal(gate.probe().outcome, "ready", "an installed gate must answer from its handles");
assert.equal(gate.probe().installed, true);
globalThis.document = mountedDocument;
assert.notEqual(routeSwitchNode.type, RouteSwitch, "the mounted switch must be adopted");
assert.equal(routeSwitchNode.alternate.type, routeSwitchNode.type, "on both of its fibers");

// The claim alone reaches the next mount only, and Steam's router is a memo with the default
// comparison, so a router already on screen would keep drawing Steam's own function until the user
// navigated. Install has to adopt the mounted instance, which is what makes the claim current.
assert.equal(gate.status().mounted.adopted, 1, "the mounted router must be adopted at install");
assert.equal(rootNode.type, memo.type, "the mounted fiber must run the claimed type");
assert.equal(gate.status().mounted.stale, 0, "no mounted router may still be drawing Steam's own");

// Claimed but with nothing published: Steam's routes must resolve exactly as before.
assert.equal(selected("/settings")?.props.path, "/settings", "an empty claim must change nothing");

// That render went through the claimed router, so the Route was borrowed from Steam's own route
// list and its source carries the back-stack markers.
assert.equal(
    gate.status().routeSource,
    "borrowed",
    "the Route must be borrowed once Steam renders",
);

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

// Steam's real back-stack Route invokes function children with its match (see its source above).
// Every invocation must forward that match's params, without mutating its page or match objects.
registerSteamPageRenderer("identity-fixture", (_react, page) => page);
const identityPage = {
    id: "identity",
    path: "/wsgm/launch-actions/:appid",
    title: "Launch Actions",
    template: "identity-fixture",
};
globals.publish({ pages: [identityPage] });
const identityRoute = selected(identityPage.path);
assert.equal(
    identityRoute.type,
    SteamRoute,
    "identity pages must retain Steam's native back-stack Route",
);
const renderMatch = react.Children.toArray(identityRoute.props.children)[0];
assert.equal(typeof renderMatch, "function", "the native Route must supply its own matched params");
const match = { params: { appid: "4294967295" } };
const body = renderMatch({ match });
const projected = body.type(body.props);
assert.deepEqual(
    projected,
    { ...identityPage, params: match.params },
    "all descriptor fields and exact AppID must survive",
);
projected.params.appid = "changed by consumer";
assert.equal(match.params.appid, "4294967295", "a consumer must not mutate Steam's route match");
assert.equal(identityPage.params, undefined, "the published descriptor must remain unchanged");
const reopened = renderMatch({ match: { params: { appid: "440" } } });
assert.equal(
    reopened.type(reopened.props).params.appid,
    "440",
    "a new match must not reuse the previous game identity",
);
assert.equal(
    renderMatch({ match: null }),
    null,
    "an unmatched route must not render a stale identity",
);

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

// A router React mounted after install from the claimed memo caches the wrapper on both of its
// fibers; nothing kept at install knows it, and release must find it all the same.
const laterRouter = { type: memo.type, elementType: memo, child: null, sibling: null, alternate: null };
laterRouter.alternate = { ...laterRouter, alternate: laterRouter };
routeSwitchNode.sibling = laterRouter;

const removed = gate.remove();
assert.ok(removed.ok, `remove failed: ${removed.error}`);
assert.equal(memo.type, Router, "removal must hand back exactly what was displaced");
assert.equal(rootNode.type, Router, "the adopted fiber must be handed back too");
assert.equal(routeSwitchNode.type, RouteSwitch, "the adopted switch must be handed back");
assert.equal(routeSwitchNode.alternate.type, RouteSwitch, "on both of its fibers");
assert.equal(laterRouter.type, Router, "a router mounted after install must be handed back");
assert.equal(laterRouter.alternate.type, Router, "on both of its fibers");
assert.equal(gate.status().claims, 0, "removal must leave nothing on the ledger");
routeSwitchNode.sibling = null;
assert.equal(gate.status().mounted.adopted, 0);
assert.ok(!gate.status().claimed);
assert.equal(selected("/wsgm/ok"), null, "removal must unregister every page");
assert.equal(
    selected("/settings")?.props.path,
    "/settings",
    "removal must restore Steam's routing",
);

assert.ok(gate.install().ok, "the gate must be reinstallable");
assertRemoveRetries(gate, failNext, "pages");
assert.equal(memo.type, Router);
assert.equal(gate.status().routeSource, "none", "removal must drop the borrowed Route");

// A host page declared with registerSteamPage: its gate refuses until Steam's components resolve and
// says which are missing, follows the host's state and refusals while installed, and its frame stops
// drawing the page once the gate is removed. The frame is the page's chrome: the stylesheets, the
// header offset, the loading and refusal state, the banner for the host's message and for a refused
// command, Steam's tabs with their footer actions, and the hook a modal reads the page's state with.
{
    const registered = new Map();
    const subscriptions = new Map();
    const refusals = new Map();
    let components = null;
    let released = 0;
    const requests = [];
    let rejectNext = null;
    const subscribedStores = [];
    const pageReact = createReact({
        useSyncExternalStore: (subscribe, snapshot) => {
            subscribedStores.push(subscribe);
            return snapshot();
        },
    });
    const { registerSteamPage, steamPageRenderers } = instantiate(
        {
            getWebpackRuntime: () => () => null,
            createIconRenderer: () => () => null,
            request: (patchId, command, payload) => {
                requests.push({ patchId, command, payload });
                if (rejectNext) {
                    const reason = rejectNext;
                    rejectNext = null;
                    return Promise.reject(new Error(reason));
                }
                return Promise.resolve({ ok: true });
            },
            subscribe: (patchId, listener) => {
                subscriptions.set(patchId, listener);
                return () => subscriptions.delete(patchId);
            },
            subscribeRefusal: (patchId, listener) => {
                refusals.set(patchId, listener);
                return () => refusals.delete(patchId);
            },
            registerGate: (name, value) => registered.set(name, value),
        },
        `${sharedFragments(asset)}\n${fragment(asset, "page-gate.ts")}\n${fragment(asset, "ui-flows.ts")}\n` +
            `${fragment(asset, "ui-kit.ts")}\n${gateSource(asset, "gates/pages.ts")}`,
        "{ registerSteamPage, steamPageRenderers }",
    );
    const children = (node) => pageReact.Children.toArray(node?.props?.children);
    // The frame's page root: the div inside the focus root, or the div itself without Focusable.
    const pageRoot = (frame) => {
        const root = frame.type(frame.props);
        return root.type === "div" ? root : children(root)[0];
    };
    const contentOf = (frame) => children(pageRoot(frame)).at(-1);
    const bannerOf = (frame) =>
        children(pageRoot(frame)).find((child) => child?.props?.className === "steam-ui-kit-page-banner");

    const Page = () => null;
    const definition = {
        template: "fixture-page",
        gate: "fixturePage",
        patchId: "fixture.page",
        title: "Fixture",
        components: () => components,
        required: ["react", "field"],
        release: () => {
            released++;
        },
        Page,
    };
    const context = registerSteamPage(definition);
    const pageGate = registered.get("fixturePage");
    assert.ok(pageGate, "a declared page must register its gate under its name");
    const render = steamPageRenderers.get("fixture-page");
    assert.equal(typeof render, "function", "a declared page must register its renderer");
    assert.throws(() => registerSteamPage(definition), /already registered/u);

    const frame = render(pageReact, { id: "fixture" });
    components = { react: pageReact };
    // The probe is components and the required list, never prepare: a missing component is
    // incompatible, and the page names it.
    let prepared = 0;
    definition.prepare = () => {
        prepared++;
        return null;
    };
    const missing = pageGate.probe();
    assert.equal(missing.outcome, "incompatible");
    assert.deepEqual(missing.facts.missing, ["field"]);
    const refusedInstall = pageGate.install();
    assert.equal(refusedInstall.ok, false);
    assert.equal(refusedInstall.error, "Native Steam components unavailable: field");
    const waiting = frame.type(frame.props);
    assert.equal(waiting.props.role, "status", "a page that cannot resolve says why");
    assert.deepEqual(waiting.props.children, ["Native Steam components unavailable: field"]);

    const Button = "Button";
    const Small = "Small";
    const Tabs = "Tabs";
    components = { react: pageReact, field: () => null, dialogButton: Button, smallButton: Small, tabs: Tabs };
    assert.equal(pageGate.probe().outcome, "ready");
    assert.equal(prepared, 0, "a probe must never prepare the page");
    assert.deepEqual(pageGate.install(), { ok: true, installed: true, reclaimed: false });
    assert.equal(prepared, 1, "the install prepares the page");
    assert.equal(pageGate.status().held, true);
    assert.equal(pageGate.status().claims, 4, "resources, state, refusals and the reset are on the ledger");

    // Until the host publishes, the frame says it is loading, or why the state could not arrive; the
    // page itself never has to guard against a missing state.
    const root = pageRoot(frame);
    assert.equal(root.props.className, "steam-ui-kit-page", "the frame offsets below Steam's header");
    assert.equal(root.props["aria-label"], "Fixture");
    assert.equal(root.props.id, "steam-ui-page-fixture-page");
    assert.equal(children(root)[0].type, "style", "the kit stylesheet lands in the page's document");
    let shown = contentOf(frame);
    assert.equal(shown.props.className, "steam-ui-kit-empty loading");
    assert.deepEqual(children(shown), ["Loading Fixture…"]);
    refusals.get("fixture.page")("too large");
    assert.equal(context.refusal(), "too large");
    shown = contentOf(frame);
    assert.equal(shown.props.className, "steam-ui-kit-empty unavailable", "a refused state is unavailable");
    assert.deepEqual(children(shown), ["too large"]);

    subscriptions.get("fixture.page")({ value: 1 });
    assert.deepEqual(context.state(), { value: 1 });
    const drawn = contentOf(frame);
    assert.equal(drawn.type, Page, "an installed page with state draws the host's component");
    assert.equal(drawn.props.context, context);
    assert.deepEqual(drawn.props.state, { value: 1 });
    assert.equal(drawn.props.tab, "");
    assert.deepEqual(drawn.props.page, { id: "fixture" });
    assert.equal(bannerOf(frame), undefined, "no message, no banner");

    // The host's error is the banner, and Dismiss sends the page's dismiss command.
    subscriptions.get("fixture.page")({ value: 2, error: "The store did not answer.", notice: "Installed." });
    let banner = children(bannerOf(frame))[0];
    assert.equal(banner.props.className, "steam-ui-kit-banner error", "an error outranks a notice");
    assert.deepEqual(children(children(banner)[0]), ["The store did not answer."]);
    children(banner)[1].props.onClick();
    assert.deepEqual(requests.splice(0), [{ patchId: "fixture.page", command: "dismiss", payload: {} }]);
    subscriptions.get("fixture.page")({ value: 3, notice: "Installed." });
    banner = children(bannerOf(frame))[0];
    assert.equal(banner.props.className, "steam-ui-kit-banner");

    // A refused command is shown in the banner, ahead of the host's notice; act resolves undefined
    // rather than rejecting, and the next command clears the message.
    rejectNext = "That theme is not installed.";
    assert.equal(await context.act("delete", { name: "x" }), undefined);
    banner = children(bannerOf(frame))[0];
    assert.equal(banner.props.className, "steam-ui-kit-banner error");
    assert.deepEqual(children(children(banner)[0]), ["That theme is not installed."]);
    assert.equal(pageGate.status().message, "That theme is not installed.");
    assert.deepEqual(await context.act("refresh"), { ok: true });
    assert.equal(pageGate.status().message, "", "the next command clears the page's message");
    assert.deepEqual(children(children(bannerOf(frame))[0])[0].props.children, ["Installed."]);
    context.report("Choose a ZIP archive.");
    children(children(bannerOf(frame))[0])[1].props.onClick();
    assert.equal(pageGate.status().message, "", "Dismiss clears the page's own message locally");
    assert.deepEqual(
        requests.splice(0).map((entry) => entry.command),
        ["delete", "refresh"],
        "dismissing the page's message sends nothing",
    );
    // send hands the refusal back to the caller that shows it, such as a settings row.
    rejectNext = "Refused.";
    await assert.rejects(context.send("set", { key: "k", value: 1 }), /Refused\./u);
    assert.equal(pageGate.status().message, "", "send leaves the banner alone");
    requests.length = 0;

    // A modal reads the page's state through the hook, which subscribes to the frame's store.
    subscribedStores.length = 0;
    assert.deepEqual(context.useState(), { value: 3, notice: "Installed." });
    assert.equal(subscribedStores.length, 1, "the hook subscribes to the frame's store");

    assert.deepEqual(pageGate.remove(), { ok: true, removed: true });
    assert.equal(pageGate.status().claims, 0);
    assert.equal(released, 1, "removal releases what the page prepared");
    assert.equal(subscriptions.has("fixture.page"), false, "removal ends the state subscription");
    assert.equal(refusals.has("fixture.page"), false, "removal ends the refusal subscription");
    assert.equal(context.state(), null);
    assert.equal(context.refusal(), null);
    assert.equal(
        frame.type(frame.props).props.role,
        "status",
        "a removed page draws nothing of its own",
    );

    // Tabs the host owns: the active tab is the host's, a change is the tab command, only the active
    // tab is drawn, and a tab's footer action reaches Steam's footer legend.
    const TabbedPage = () => null;
    let filters = 0;
    const tabbed = registerSteamPage({
        template: "fixture-tabbed",
        gate: "fixtureTabbed",
        patchId: "fixture.tabbed",
        title: "Tabbed",
        id: "fixture-tabbed",
        style: "#fixture-tabbed .x{}",
        header: false,
        components: () => components,
        required: ["react", "tabs"],
        tabs: {
            source: "host",
            list: [
                { id: "browse", title: "Browse", footer: { label: "Filter", onActivate: () => filters++ } },
                { id: "installed", title: "Installed" },
            ],
        },
        Page: TabbedPage,
    });
    assert.ok(registered.get("fixtureTabbed").install().ok);
    const tabbedFrame = steamPageRenderers.get("fixture-tabbed")(pageReact, null);
    subscriptions.get("fixture.tabbed")({ activeTab: "installed" });
    const tabbedRoot = pageRoot(tabbedFrame);
    assert.equal(tabbedRoot.props.className, "steam-ui-kit-page flush tabbed", "header:false draws flush");
    assert.deepEqual(children(children(tabbedRoot)[1]), ["#fixture-tabbed .x{}"], "the page's own style");
    let tabsElement = contentOf(tabbedFrame);
    assert.equal(tabsElement.type, Tabs);
    assert.equal(tabsElement.props.activeTab, "installed");
    assert.equal(tabsElement.props.tabs[0].content, null, "a hidden tab is not drawn");
    assert.equal(tabsElement.props.tabs[1].content.type, TabbedPage);
    assert.equal(tabsElement.props.tabs[1].content.props.tab, "installed");
    assert.equal(tabsElement.props.tabs[1].footer, undefined);
    tabsElement.props.tabs[0].footer.onSecondaryButton();
    assert.equal(filters, 1);
    assert.equal(tabsElement.props.tabs[0].footer.onSecondaryActionDescription, "Filter");
    tabsElement.props.onShowTab("browse");
    assert.deepEqual(requests.splice(0), [
        { patchId: "fixture.tabbed", command: "setTab", payload: { tab: "browse" } },
    ]);
    assert.equal(tabbed.tab(), "installed", "a host tab moves when the host publishes it");
    subscriptions.get("fixture.tabbed")({ activeTab: "nowhere" });
    assert.equal(tabbed.tab(), "browse", "a tab the list does not carry shows the first");

    // Tabs the page owns: a change is local and sends nothing.
    const local = registerSteamPage({
        template: "fixture-local",
        gate: "fixtureLocal",
        patchId: "fixture.local",
        title: "Local",
        components: () => components,
        required: ["react", "tabs"],
        tabs: { source: "local", initial: "b", list: [{ id: "a", title: "A" }, { id: "b", title: "B" }] },
        Page: TabbedPage,
    });
    assert.ok(registered.get("fixtureLocal").install().ok);
    const localFrame = steamPageRenderers.get("fixture-local")(pageReact, null);
    subscriptions.get("fixture.local")({});
    assert.equal(contentOf(localFrame).props.activeTab, "b", "a local page starts on its initial tab");
    contentOf(localFrame).props.onShowTab("a");
    assert.equal(local.tab(), "a");
    assert.equal(contentOf(localFrame).props.activeTab, "a");
    assert.deepEqual(requests, [], "a local tab change sends nothing");
}

console.log(
    "Custom pages: probe outcomes, borrowed Route, route-list discovery, override vs addition, " +
        "validation, restoration of later mounts, and the registerSteamPage lifecycle and frame passed.",
);
