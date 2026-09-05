// Exercise the emitted dropdown with inert React and bridge fixtures, never a live Steam session.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("const normalizePowerProfileState =");
const end = asset.indexOf("const createControllerControl =", start);
assert.ok(start >= 0 && end > start);
const textStart = asset.indexOf("const normalizeText =");
const textEnd = asset.indexOf(";", textStart);
assert.ok(textStart >= 0 && textEnd > textStart);
const normalizeText = new Function(asset.slice(textStart, textEnd + 1) + "return normalizeText;")();
let state;
const requests = [];
const pending = [];
const api = new Function("normalizeText", "useSemanticState", "note", "definitions",
  "renderOutcomes", "request", "nextActionGeneration",
  asset.slice(start, end) + "\nreturn { normalizePowerProfileState, createPowerProfileControl, normalizePowerPresetState, createPowerPresetControl };")(
  normalizeText,
  (_runtime, _kind, normalize) => normalize(state), () => null,
  { powerProfile: { patchId: "steam-ui.power-profile", command: "setPowerProfile" },
    powerPreset: { patchId: "steam-ui.power-preset", acCommand: "setAcPowerPreset", batteryCommand: "setBatteryPowerPreset" } }, {},
  (...args) => { requests.push(args); return Promise.resolve(); }, () => 1);
const options = [{ id: "a", label: "Balanced" }, { id: "b", label: "Balanced" }];
const longLabel = api.normalizePowerProfileState({ available: true,
  options: [{ id: "a", label: "x".repeat(10000) }], current: "a" });
assert.equal(longLabel.options[0].label.length, 240);
state = { available: true, options, current: "a", statusText: "Ready" };
const control = api.createPowerProfileControl({ dropdown: "dropdown", react: {
  useState: () => [false, value => pending.push(value)],
  createElement: (_type, props) => props,
} });
const row = control();
assert.equal(row.label, "Windows power profile");
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
const presetControl = api.createPowerPresetControl({ dropdown: "dropdown", react: {
  Fragment: "fragment", useState: () => [false, () => {}],
  createElement: (type, props, ...children) => ({ type, props, children }),
} });
state = { available: true, options, current: "Custom", ac: "a", battery: "b",
  scope: "Global", unsetLabel: "Manual selection" };
let rows = presetControl().children.filter(child => child?.type === "dropdown");
assert.deepEqual(rows.map(row => row.props.label), ["When plugged in", "On battery"]);
assert.deepEqual(rows.map(row => row.props.selectedOption), ["a", "b"]);
rows[0].props.onChange({ data: "b" });
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(requests.at(-1), ["steam-ui.power-preset", "setAcPowerPreset", { target: "b" }, 1]);
rows[1].props.onChange({ data: "" });
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(requests.at(-1), ["steam-ui.power-preset", "setBatteryPowerPreset", { target: null }, 1]);
const before = requests.length;
rows[0].props.onChange({ data: "missing" });
assert.equal(requests.length, before);
state = { ...state, available: false };
rows = presetControl().children.filter(child => child?.type === "dropdown");
assert.ok(rows.every(row => row.props.disabled));
rows[0].props.onChange({ data: "b" });
assert.equal(requests.length, before);
assert.equal(api.normalizePowerPresetState({ ...state, ac: "missing" }), null);
assert.ok(api.normalizePowerPresetState({ ...state, options: [...options, { id: "none", label: "None" }] }));
state = { ...state, options: [], ac: "", battery: "" };
assert.equal(presetControl(), null);
assert.match(asset, /\["powerPreset", "steam-ui-power-preset", powerPresetControl, "perf"\]/);
console.log("Power-profile and assignment emitted dropdown checks passed.");
