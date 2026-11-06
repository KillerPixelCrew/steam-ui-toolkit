import assert from "node:assert/strict";
import {
    createReact,
    element,
    gateSource,
    instantiate,
    loadAsset,
    sharedFragments,
    withSource,
} from "./check-harness.mjs";

const react = createReact();
const jsx = {
    jsx: (type, props, key) => element(type, props, key),
    jsxs: (type, props, key) => element(type, props, key),
};
const original = jsx.jsx;
const nativeRenderer = withSource(
    () => null,
    'function Native(){controller:"notification",method:0}',
);
const template = withSource(
    (props) => props,
    'function Template(){console.log("Missing activate function")}',
);
const tray = [];
let accept = true;
const store = {
    m_nNextTestNotificationID: 10000,
    m_rgNotificationTray: tray,
    m_rgNotificationToasts: [],
    m_rgPendingToasts: [],
    // Steam queues a client notice as the notification object itself.
    ProcessNotification(info, notification, kind) {
        assert.equal(kind, 0);
        if (!accept) return;
        info.fnTray(notification, tray);
        this.m_rgNotificationToasts.push(notification);
    },
    RemoveGroupFromTray(group) {
        const at = tray.indexOf(group);
        if (at >= 0) tray.splice(at, 1);
    },
    DispatchNextToast() {},
};
// Neutral fixture names: the gate must use whatever the fingerprint resolves to.
const rendererModule = { nativeRenderer, template };
const modules = {
    "fixture-store": { store },
    "fixture-renderer": rendererModule,
    "fixture-classes": { Header: "header", Title: "title", Body: "body" },
    react,
    jsx,
};
let rendererAnswer = "fixture-renderer";
const key = (tokens) =>
    tokens.includes("useState")
        ? "react"
        : tokens.includes(".jsxs")
          ? "jsx"
          : tokens.includes("m_nNextTestNotificationID")
            ? "fixture-store"
            : tokens.includes("Missing activate function")
              ? rendererAnswer
              : tokens.includes("StandardTemplateContainer")
                ? "fixture-classes"
                : null;
const loads = [];
const runtime = (id) => {
    loads.push(id);
    assert.ok(Object.hasOwn(modules, id), `only a fingerprinted module is loaded, not ${id}`);
    return modules[id];
};
runtime.findUnique = (tokens) => (key(tokens) ? [key(tokens), ""] : null);
runtime.resolve = (tokens) => (key(tokens) ? modules[key(tokens)] : null);
runtime.count = (tokens) => (key(tokens) ? 1 : 0);
const routes = [];
const overlayRoutes = [];
const overlay = {
    IsGamepadUIOverlayWindow: () => true,
    Navigate: (route) => overlayRoutes.push(route),
    MenuStore: { GetOpenSideMenu: () => 1 },
};
const steamWindows = [];
const gate = instantiate(
    {
        window: {
            tempNavStore: { m_history: { push: (route) => routes.push(route) } },
            SteamUIStore: { WindowStore: { SteamUIWindows: steamWindows } },
        },
        getWebpackRuntime: () => runtime,
        registerGate() {},
        subscribe() {
            return () => {};
        },
    },
    `${sharedFragments(loadAsset())}\n${gateSource(loadAsset(), "gates/notices.ts")}`,
    "createNotices()",
);

assert.equal(gate.probe().outcome, "ready");
assert.equal(gate.install().ok, true);
assert.equal(gate.status().held, true);
const notice = {
    id: "vendor/version",
    title: "Driver update",
    body: "A verified update is available",
    route: "/settings/display",
};

accept = false;
assert.equal(gate.show(notice), false, "a notice Steam drops is refused");
accept = true;
assert.equal(gate.show(notice), true, "a dropped notice can be shown later");
assert.equal(gate.show(notice), true);
assert.equal(tray.length, 1, "a notice already in the tray is not shown twice");
assert.equal(gate.show({ ...notice, id: "invalid", route: "https://example.com" }), false);
assert.equal(gate.show({ ...notice, id: "root", route: "/" }), false);

const native = { eType: 7, notifications: [{ data: { type: () => 1 } }] };
assert.equal(
    jsx.jsx(nativeRenderer, { group: native }).type,
    nativeRenderer,
    "Valve notices keep their renderer",
);
const custom = jsx.jsx(nativeRenderer, { group: tray[0], location: 3 });
assert.notEqual(custom.type, nativeRenderer);
const rendered = custom.type(custom.props);
assert.equal(rendered.type, template, "the notice uses Valve's native template");
rendered.props.onActivate();
assert.deepEqual(routes, ["/settings/display"]);
assert.equal(tray.length, 0);
assert.equal(gate.show(notice), true, "an opened notice can be shown again");

// Over a running game the notice is opened in the game's overlay window.
steamWindows.push(overlay);
const overGame = jsx.jsx(nativeRenderer, { group: tray[0], location: 3 });
overGame.type(overGame.props).props.onActivate();
assert.deepEqual(overlayRoutes, ["/settings/display"]);
assert.deepEqual(
    routes,
    ["/settings/display"],
    "the main window's history is left alone over a game",
);
steamWindows.pop();

assert.equal(gate.show(notice), true);
assert.equal(gate.remove().ok, true);
assert.equal(jsx.jsx, original);
assert.equal(tray.length, 0, "removal takes every owned notice out of the tray");
assert.equal(
    store.m_rgNotificationToasts.length,
    0,
    "removal takes every owned toast out of the queue",
);
rendered.props.onActivate();
assert.deepEqual(routes, ["/settings/display"], "a retired notice cannot navigate");
assert.equal(custom.type(custom.props), null, "a retired custom renderer is inert");

rendererModule.second = withSource(
    () => null,
    'function Other(){controller:"notification",method:1}',
);
assert.equal(gate.probe().outcome, "incompatible", "two renderer exports are not one match");
delete rendererModule.second;
rendererAnswer = null;
const before = loads.length;
assert.equal(gate.probe().outcome, "incompatible");
assert.deepEqual(
    loads.slice(before).filter((id) => id === null),
    [],
    "an unmatched fingerprint loads nothing",
);
rendererAnswer = "fixture-renderer";
assert.equal(gate.probe().outcome, "ready");
console.log(
    "Native notices: fingerprinted modules, one-pass exports, native template, route activation over a game, tray deduplication, dropped and reopened notices, refusal, owned cleanup and restoration passed.",
);
