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
// The icon fixture hands back the requested name, so an assertion can say which glyph a row asked
// for without this file having to know how an svg element is built.
const control = api.createPowerProfileControl({ dropdown: "dropdown", icon: name => name, react: {
  useState: () => [false, value => pending.push(value)],
  createElement: (_type, props) => props,
} });
const row = control();
assert.equal(row.label, "Windows power profile");
assert.equal(row.icon, "power");
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
const presetControl = api.createPowerPresetControl({ dropdown: "dropdown", labelField: "labelField",
  icon: name => name, react: {
    Fragment: "fragment", useState: () => [false, () => {}],
    createElement: (type, props, ...children) => ({ type, props, children }),
  } });
state = { available: true, options, current: "Custom", ac: "a", battery: "b",
  scope: "Global", unsetLabel: "Manual selection" };
let rows = presetControl().children.filter(child => child?.type === "dropdown");
assert.deepEqual(rows.map(row => row.props.label), ["When plugged in", "On battery"]);
assert.deepEqual(rows.map(row => row.props.icon), ["plug", "battery"]);
// The profile in effect is a Valve label/value row, not a bare div: it carries a label, a glyph and
// the scope and status together as its description.
{
  const active = presetControl().children.find(child => child?.type === "labelField");
  assert.ok(active, "the active profile must render as a Valve field");
  assert.equal(active.props.label, "Active profile");
  assert.equal(active.props.icon, "check");
  assert.equal(active.props.description, "Global");
  assert.deepEqual(active.children, ["Custom"]);
  assert.ok(!presetControl().children.some(child => child?.type === "div"),
    "no unformatted div may survive beside Steam's own rows");
}
// A refusal must stay visible when there is no active profile to carry it. Readback can fail with
// no current profile at all, which left two disabled dropdowns and no reason for either.
{
  const failed = { ...state, available: false, current: "", statusText: "Readback failed" };
  const previous = state;
  state = failed;
  const tree = presetControl();
  assert.ok(!tree.children.some(child => child?.type === "labelField"),
    "no active profile means no active-profile row");
  assert.ok(JSON.stringify(tree).includes("Readback failed"),
    "the reason a profile could not be read must reach the panel anyway");
  // Same when the client could not resolve LabelField and the row is gone for a different reason.
  const withoutField = api.createPowerPresetControl({ dropdown: "dropdown", icon: name => name, react: {
    Fragment: "fragment", useState: () => [false, () => {}],
    createElement: (type, props, ...children) => ({ type, props, children }),
  } });
  state = { ...previous, available: false, statusText: "Readback failed" };
  assert.ok(JSON.stringify(withoutField()).includes("Readback failed"),
    "an unresolved LabelField must not take the reason with it");
  state = previous;
}
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
for (const ac of [true, false]) {
  state = { ...state, available: true, options: [...options, { id: "custom", label: "Custom" }],
    ac: ac ? "custom" : "a", battery: ac ? "b" : "custom" };
  rows = presetControl().children.filter(child => child?.type === "dropdown");
  assert.deepEqual(rows.map(row => row.props.selectedOption), ac ? ["custom", "b"] : ["a", "custom"]);
  assert.ok(rows[ac ? 0 : 1].props.rgOptions.some(option => option.data === "custom"));
  assert.ok(!rows[ac ? 1 : 0].props.rgOptions.some(option => option.data === "custom"));
  rows[ac ? 0 : 1].props.onChange({ data: "custom" });
  assert.equal(requests.length, before);
}
assert.equal(api.normalizePowerPresetState({ ...state, ac: "a", battery: "b" }), null);
state = { ...state, options: [], ac: "", battery: "" };
assert.equal(presetControl(), null);
assert.match(asset, /\["powerPreset", "steam-ui-power-preset", powerPresetControl, "perf"\]/);
console.log("Power-profile and assignment emitted dropdown checks passed.");

// The real section header composer, so the device sections are checked against the titles the panel
// actually receives rather than a stand-in. It reads its glyph off the runtime, so it needs nothing
// from the icon table itself.
const headerStart = asset.indexOf("const SectionIcons =");
const headerEnd = asset.indexOf("const appendControls =", headerStart);
assert.ok(headerStart >= 0 && headerEnd > headerStart);
const sectionTitle = new Function(
  asset.slice(headerStart, headerEnd) + "\nreturn sectionTitle;",
)();
// A header is an icon and its text in a row; a section with no glyph keeps the bare string.
const titleText = (title) => (typeof title === "string" ? title : title.children.at(-1));
const titleGlyph = (title) => (typeof title === "string" ? null : title.children[0]?.glyph);

// Every placement gets its own glyph, and every glyph gets a placement. A shape that appears twice
// tells a user scanning the panel that two different controls are the same one, which is worse than
// leaving a row bare; a shape nothing places is dead weight that survives until somebody notices it
// by eye, which is how `sun` outlived the two rows it used to sit on.
//
// The scan reads the emitted asset, so it is textual and has limits worth stating: it sees the
// section table and every call site that spells its glyph out, and the four names the preset and
// power-limit tables pass by variable are listed here by hand. A placement built from a computed
// name would be invisible to it. The set comparison below is what makes that survivable — a new
// placement that this cannot see also fails to account for one of the declared glyphs.
{
  const table = asset.indexOf("const SteamUiIconShapes =");
  const tableEnd = table + asset.slice(table).search(/\n[ \t]*function create/u);
  assert.ok(table >= 0 && tableEnd > table);
  const drawings = asset.slice(table, tableEnd);
  // Comments are emitted verbatim, and a commented-out call site is not a placement.
  const code = asset.replace(/^[ \t]*\/\/.*$/gmu, "");
  const sectionTable = code.slice(
    code.indexOf("const SectionIcons ="),
    code.indexOf("});", code.indexOf("const SectionIcons =")),
  );
  const used = [
    ...[...code.matchAll(/\bicon\(\s*["']([A-Za-z]+)["']/gu)].map((match) => match[1]),
    ...[...sectionTable.matchAll(/:\s*["']([A-Za-z]+)["']/gu)].map((match) => match[1]),
    "plug",
    "battery",
    "bolt",
    "boost",
  ];
  const repeated = [...new Set(used.filter((name, at) => used.indexOf(name) !== at))];
  assert.deepEqual(repeated, [], `glyphs used for more than one control: ${repeated.join(", ")}`);
  // Declared glyphs, read off the table's own keys rather than a number kept in step by hand.
  // Indentation differs between the two compositions - the prelude comes out of tsc as-is, a
  // consumer's asset is run through Prettier - so the key is anchored to its line, not a depth.
  const declared = [...drawings.matchAll(/^[ \t]*([A-Za-z][A-Za-z0-9]*): \[/gmu)].map(
    (match) => match[1],
  );
  assert.ok(declared.length >= 29, `only ${declared.length} glyphs were found in the table`);
  assert.deepEqual(
    [...used].sort(),
    [...declared].sort(),
    "every glyph must be placed exactly once and every placement must name a drawn glyph",
  );
}

// Optional native fields must not take down the remaining device controls.
const deviceStart = asset.indexOf("const rgbToHsv =");
const deviceEnd = asset.indexOf("// Steam's own FPS counter rows", deviceStart);
assert.ok(deviceStart >= 0 && deviceEnd > deviceStart);
const deviceState = {
  chargeLimit: { available: true, observed: 80, minimum: 60, maximum: 100, step: 1 },
  lightingBrightness: { available: true, observed: 100, minimum: 0, maximum: 100, step: 1 },
  lightingZones: [{ available: true, id: "buttons", label: "Buttons", observedColor: 0xffffff }],
};
const createDeviceControl = new Function("useSemanticState", "normalizeDeviceControlsState",
  "definitions", "request", "nextActionGeneration", "useTrailingCommit", "useEchoedValue",
  "note", "renderOutcomes", "isBusy", "localizeOr", "sectionTitle",
  asset.slice(deviceStart, deviceEnd) + "\nreturn createDeviceControlsControl;")(
  () => deviceState, value => value, { deviceControls: {} },
  () => { throw new Error("Rendering must not dispatch hardware writes"); }, () => 1,
  () => () => {}, (_runtime, value) => ({ value }), () => null, {}, () => false,
  (_runtime, _token, fallback) => fallback, sectionTitle);
for (const toggle of [undefined, "toggle"]) {
  for (const expanded of [false, true]) {
    const render = createDeviceControl({ toggle, row: "row", section: "section",
      slider: "slider", dropdown: "dropdown", icon: (glyph, size) => ({ glyph, size }), react: {
        Fragment: "fragment", useState: initial => [typeof initial === "boolean" ? expanded : initial, () => {}],
        createElement: (type, props, ...children) => {
          assert.ok(type, "An unresolved native component must never be rendered");
          return { type, props, children };
        },
      } });
    const tree = render();
    assert.deepEqual(tree.children.map(section => titleText(section.props.title)),
      ["Charging", "RGB lighting"]);
    assert.deepEqual(tree.children.map(section => titleGlyph(section.props.title)),
      ["batteryCharging", "colors"]);
    const fields = tree.children.flatMap(section => section.children.map(row => row.children[0]));
    assert.ok(fields.some(field => field.props.label === "Battery charge limit"
      && field.props.icon.glyph === "percent"));
    assert.ok(fields.some(field => field.props.label === "Lighting brightness"
      && field.props.icon.glyph === "bulb"));
    // A glyph on a header must not reappear on a row inside it, and no two rows may share one:
    // shape is how this panel is navigated before the label is read.
    const glyphs = tree.children.flatMap(section => [
      titleGlyph(section.props.title),
      ...section.children.map(row => row.children[0].props.icon?.glyph),
    ]).filter(Boolean);
    assert.equal(new Set(glyphs).size, glyphs.length, `device glyphs repeat: ${glyphs.join(", ")}`);
    assert.equal(fields.some(field => field.props.label === "Edit color"), !!toggle);
    assert.equal(fields.some(field => field.props.label === "Lighting zone"), !!toggle && expanded);
  }
}
console.log("Device controls retain charging and brightness without the optional color toggle.");

// Hardware observations, not Steam's saved TDP setting, drive both power sliders.
{
  const first = asset.indexOf("const normalizePowerLimitRange =");
  const last = asset.indexOf("const createDeviceControlsControl =", first);
  const echoFirst = asset.indexOf("const useEchoedValue =");
  const echoLast = asset.indexOf("const useTrailingCommit =", echoFirst);
  assert.ok(first >= 0 && last > first && echoFirst >= 0 && echoLast > echoFirst);
  const slots = [];
  let cursor = 0;
  const writes = [];
  const replies = [];
  const range = (watts) => ({
    available: true,
    minimumWatts: 8,
    maximumWatts: 37,
    stepWatts: 1,
    observedWatts: watts,
    progress: "",
    statusText: "",
  });
  let powerState = { sustained: range(23), boost: range(30) };
  const runtime = {
    slider: "slider",
    row: "row",
    icon: (name) => name,
    react: {
      Fragment: "fragment",
      useState(initial) {
        const index = cursor++;
        if (!(index in slots)) slots[index] = initial;
        return [
          slots[index],
          (value) => {
            slots[index] = value;
          },
        ];
      },
      useRef(initial) {
        const index = cursor++;
        if (!(index in slots)) slots[index] = { current: initial };
        return slots[index];
      },
      createElement: (type, props, ...children) => ({ type, props, children }),
    },
  };
  const powerApi = new Function(
    "normalizeText",
    "useSemanticState",
    "definitions",
    "request",
    "nextActionGeneration",
    "isBusy",
    "note",
    asset.slice(echoFirst, echoLast) +
      asset.slice(first, last) +
      "\nreturn { createPowerLimitControl, normalizePowerLimitState };",
  )(
    normalizeText,
    (_runtime, _kind, normalize) => normalize(powerState),
    {
      powerLimit: {
        patchId: "steam-ui.power-limit",
        primaryCommand: "setPrimaryLimit",
        boostCommand: "setBoostLimit",
      },
    },
    (...args) => {
      writes.push(args);
      return new Promise((resolve, reject) => replies.push({ resolve, reject }));
    },
    () => 1,
    (progress) => ["queued", "applying", "replacing"].includes(progress),
    () => null,
  );
  const control = powerApi.createPowerLimitControl(runtime);
  const render = () => {
    cursor = 0;
    return control()?.children.map((row) => row.children[0].props) ?? [];
  };
  const flush = () => new Promise((resolve) => setImmediate(resolve));
  let sliders = render();
  assert.deepEqual(
    sliders.map((slider) => slider.label),
    ["Sustained power (PL1)", "Boost power (PL2)"],
  );
  assert.deepEqual(
    sliders.map((slider) => slider.icon),
    ["bolt", "boost"],
  );
  // SliderField would otherwise place the glyph beside the track rather than the label.
  assert.ok(sliders.every((slider) => slider.iconLocation === "front"));
  assert.deepEqual(
    sliders.map((slider) => slider.value),
    [23, 30],
  );
  assert.equal(writes.length, 0);
  sliders[0].onChange(20);
  assert.equal(render()[0].value, 20);
  assert.equal(writes.length, 0, "dragging does not issue hardware writes");
  powerState = { sustained: range(37), boost: range(37) };
  render();
  sliders = render();
  assert.deepEqual(
    sliders.map((slider) => slider.value),
    [37, 37],
    "profile readback supersedes local drag echo",
  );
  assert.equal(writes.length, 0, "profile publication never echoes a hardware command");
  sliders[1].onChangeComplete(28);
  sliders[0].onChangeComplete(20);
  assert.equal(writes.length, 1, "both controls serialize against the pending command");
  assert.deepEqual(writes[0], ["steam-ui.power-limit", "setBoostLimit", { watts: 28 }, 1]);
  assert.ok(render().every((slider) => slider.disabled));
  replies.shift().resolve();
  await flush();
  powerState.boost = range(28);
  render();
  sliders = render();
  assert.deepEqual(
    sliders.map((slider) => slider.value),
    [37, 28],
  );
  sliders[0].onChangeComplete(20);
  assert.deepEqual(writes[1], ["steam-ui.power-limit", "setPrimaryLimit", { watts: 20 }, 1]);
  replies.shift().reject(new Error("Hardware outcome uncertain"));
  await flush();
  sliders = render();
  assert.equal(sliders[0].value, 37);
  assert.match(sliders[0].description, /uncertain/);
  render();
  assert.equal(writes.length, 2, "failure and re-render do not automatically retry");
  for (const invalid of [0, 38, 20.5, "20", null]) sliders[0].onChangeComplete(invalid);
  assert.equal(writes.length, 2);
  powerState.sustained = { ...range(23), progress: "applying" };
  assert.ok(render().every((slider) => slider.disabled));
  powerState.sustained = { ...range(23), available: false };
  assert.equal(render()[0].disabled, true);
  powerState = { sustained: range(23), boost: { ...range(28), observedWatts: null } };
  assert.equal(render().length, 1, "unknown boost readback cannot fabricate a value");
  powerState.sustained = { ...range(23), stepWatts: 2 };
  assert.equal(render().length, 0, "off-step observations are refused");
  assert.ok(!asset.includes("steamos_tdp_limit"), "no saved Steam TDP setting can be replayed");
}
console.log(
  "Power sliders: independent PL1/PL2 edits, profile readback, pending commands and refusal checks passed.",
);
