import assert from "node:assert/strict";
import { createReact, element, gateSource, instantiate, loadAsset, sharedFragments, withSource } from "./check-harness.mjs";

const react = createReact();
const jsx = { jsx: (type, props, key) => element(type, props, key), jsxs: (type, props, key) => element(type, props, key) };
const original = jsx.jsx;
const nativeRenderer = withSource(() => null, 'function Native(){controller:"notification",method:0}');
const template = withSource((props) => props, 'function Template(){console.log("Missing activate function")}');
const tray = [];
const store = {
  m_nNextTestNotificationID: 10000,
  m_rgNotificationTray: tray,
  m_rgNotificationToasts: [],
  m_rgPendingToasts: [],
  ProcessNotification(info, notification, kind) {
    assert.equal(kind, 0);
    info.fnTray(notification, tray);
    this.m_rgNotificationToasts.push(notification);
  },
  RemoveGroupFromTray(group) { const at = tray.indexOf(group); if (at >= 0) tray.splice(at, 1); },
  DispatchNextToast() {},
};
const loads = [];
let rendererId = "25666";
const modules = {
  "7474": { store }, "25666": { nativeRenderer, template }, "3285": { nativeRenderer, template },
  "21728": { Header: "header", Title: "title", Body: "body" }, react, jsx,
};
const key = (tokens) => tokens.includes("useState") ? "react" : tokens.includes(".jsxs") ? "jsx"
  : tokens.includes("m_nNextTestNotificationID") ? "7474"
  : tokens.includes("Missing activate function") ? rendererId
  : tokens.includes("StandardTemplateContainer") ? "21728" : null;
const runtime = (id) => { loads.push(id); assert.ok(Object.hasOwn(modules, id)); return modules[id]; };
runtime.findUnique = (tokens) => key(tokens) ? [key(tokens), ""] : null;
runtime.resolve = (tokens) => key(tokens) ? modules[key(tokens)] : null;
runtime.count = (tokens) => key(tokens) ? 1 : 0;
const routes = [];
const gate = instantiate({
  window: { tempNavStore: { m_history: { push: (route) => routes.push(route) } } },
  getWebpackRuntime: () => runtime, registerGate() {}, subscribe() { return () => {}; },
}, `${sharedFragments(loadAsset())}\n${gateSource(loadAsset(), "gates/update-notifications.ts")}`, "createUpdateNotifications()");

assert.equal(gate.probe().outcome, "ready");
assert.equal(gate.install().ok, true);
assert.equal(gate.status().held, true);
const notice = { id: "vendor/version", title: "Driver update", body: "A verified update is available", route: "/settings/display" };
assert.equal(gate.show(notice), true);
assert.equal(gate.show(notice), true);
assert.equal(tray.length, 1, "a revision must not duplicate its notice");
assert.equal(gate.show({ ...notice, id: "invalid", route: "https://example.com" }), false);
const native = { eType: 7, notifications: [{ data: { type: () => 1 } }] };
assert.equal(jsx.jsx(nativeRenderer, { group: native }).type, nativeRenderer, "Valve notices keep their renderer");
const custom = jsx.jsx(nativeRenderer, { group: tray[0], location: 3 });
assert.notEqual(custom.type, nativeRenderer);
const rendered = custom.type(custom.props);
assert.equal(rendered.type, template, "the notice uses Valve's native template");
rendered.props.onActivate();
assert.deepEqual(routes, ["/settings/display"]);
assert.equal(tray.length, 0);
assert.equal(gate.remove().ok, true);
assert.equal(jsx.jsx, original);
assert.equal(store.m_rgNotificationToasts.length, 0);
rendered.props.onActivate();
assert.deepEqual(routes, ["/settings/display"], "a retired notice cannot navigate");
assert.equal(custom.type(custom.props), null, "a retired custom renderer is inert");
rendererId = "3285";
assert.equal(gate.probe().outcome, "ready", "the recorded Beta renderer is supported");
rendererId = "unexpected";
const before = loads.length;
assert.equal(gate.probe().outcome, "incompatible");
assert.ok(!loads.slice(before).includes("unexpected"), "an unrecognized factory is never executed");
console.log("Native update notices: exact module contracts, native template, route activation, deduplication, refusal, owned cleanup and restoration passed.");
