// Exercise the emitted dropdown with inert React and bridge fixtures, never a live Steam session.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("const normalizePowerProfileState =");
const end = asset.indexOf("const createControllerControl =", start);
assert.ok(start >= 0 && end > start);
let state;
const requests = [];
const pending = [];
const api = new Function("normalizeText", "useSemanticState", "note", "definitions",
  "renderOutcomes", "request", "nextActionGeneration",
  asset.slice(start, end) + "\nreturn { normalizePowerProfileState, createPowerProfileControl };")(
  value => typeof value === "string" ? value : "",
  (_runtime, _kind, normalize) => normalize(state), () => null,
  { powerProfile: { patchId: "steam-ui.power-profile", command: "setPowerProfile" } }, {},
  (...args) => { requests.push(args); return Promise.resolve(); }, () => 1);
const options = [{ id: "a", label: "Balanced" }, { id: "b", label: "Balanced" }];
state = { available: true, options, current: "a", statusText: "Ready" };
const control = api.createPowerProfileControl({ dropdown: "dropdown", react: {
  useState: () => [false, value => pending.push(value)],
  createElement: (_type, props) => props,
} });
const row = control();
assert.equal(row.label, "Power profile");
assert.equal(row.selectedOption, "a");
row.onChange({ data: "unknown" });
row.onChange({ data: "a" });
assert.equal(requests.length, 0);
row.onChange({ data: "b" });
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(requests[0], ["steam-ui.power-profile", "setPowerProfile", { target: "b" }, 1]);
assert.deepEqual(pending, [true, false]);
state = { ...state, current: "missing" };
assert.equal(control().selectedOption, undefined);
state = { ...state, available: false, statusText: "Readback failed" };
assert.equal(control().disabled, true);
assert.equal(control().description, "Readback failed");
for (const badOptions of [[...options, options[0]], [{ id: 123, label: "Bad" }],
  [{ id: "a", label: "" }], Array.from({ length: 65 }, (_, i) => ({ id: String(i), label: "x" }))]) {
  assert.equal(api.normalizePowerProfileState({ ...state, options: badOptions }), null);
}
assert.match(asset, /\["powerProfile", "steam-ui-power-profile", powerProfileControl, "perf"\]/);
console.log("Power-profile emitted dropdown checks passed.");
