import assert from "node:assert/strict";
import { fragment, instantiate, loadAsset } from "./check-harness.mjs";

const failures = [];
let gate;
const target = { addEventListener() {}, removeEventListener() {} };
const document = {
  defaultView: target,
  head: { appendChild() {} },
  createElement() {
    return { dataset: {}, ownerDocument: document, remove() {} };
  },
};
const react = { Component: class {}, createElement: (type, props) => ({ type, props }) };
const inert = () => null;
const globals = {
  document,
  window: {},
  setTimeout,
  clearTimeout,
  setInterval,
  clearInterval,
  registerGate: (_, value) => {
    gate = value;
  },
  resolveReact: () => react,
  getWebpackRuntime: inert,
  bridge: { gate: () => ({ install: () => ({ ok: true }), status: () => ({ installed: true }) }) },
  request: async (id, command, payload) => {
    failures.push({ id, command, payload });
  },
  subscribe: () => inert,
  resolveSteamSettingsComponents: () => ({ react }),
  renderSteamUiGroup: inert,
  renderSteamUiHeader: inert,
  renderSteamUiEmpty: inert,
  steamPageRenderers: new Map(),
};
instantiate(globals, fragment(loadAsset(), "plugin-frontends"), "null");
assert.equal(
  (
    await gate.load({
      id: "one.page",
      owner: "one",
      module: "page",
      script: "api.registerPage('page', {path:'/example',title:'Example'}, () => null);",
    })
  ).ok,
  true,
);
assert.equal(
  (await gate.load({ id: "other.page", owner: "other", module: "page", script: "" })).ok,
  true,
);
assert.equal(
  (
    await gate.load({
      id: "one.bad",
      owner: "one",
      module: "bad",
      script: "throw new Error('load failed');",
    })
  ).ok,
  false,
);
assert.equal(
  (await gate.status("one.page")).ok,
  false,
  "one failure removes every sibling in the owning package",
);
assert.equal((await gate.status("other.page")).ok, true, "another package remains available");
assert.equal(globals.steamPageRenderers.size, 0, "the failed owner's renderer is retracted");
assert.equal(failures[0].command, "failure");
assert.equal(failures[0].payload.module, "bad");
await gate.unload("one.page");
await gate.unload("one.bad");
await gate.unload("other.page");
assert.equal((await gate.status("other.page")).ok, false);
console.log("Plugin frontend owner isolation and teardown passed.");
