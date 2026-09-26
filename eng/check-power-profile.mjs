// Exercise the emitted Quick Access rows with inert React and bridge fixtures, never a live Steam
// session: the power-profile and assignment dropdowns, the section headers and their glyphs, the
// device controls, section layout, the power sliders and the slider echo they share.
import assert from "node:assert/strict";
import {
  createHooks,
  instantiate,
  loadAsset,
  slice,
  sliceToGate,
  tick,
} from "./check-harness.mjs";

const asset = loadAsset();
const normalizeText = instantiate(
  {},
  `${slice(asset, "const normalizeText =", ";")};`,
  "normalizeText",
);
// The game-override helpers every row shares. Fixtures carry no override unless a check says so.
const overrideHelpers = instantiate(
  {},
  slice(asset, "const normalizeOverrideId =", "const normalizeVrrState ="),
  "{ normalizeOverrideId, overrideDescription }",
);
{
  // An overridden row's description is one span in Steam's accent colour, and a row without an
  // override keeps its plain text. Nothing is drawn beside the control.
  const runtime = {
    react: { createElement: (type, props, ...children) => ({ type, props, children }) },
  };
  const marked = overrideHelpers.overrideDescription(runtime, "FrameLimit", "Ready");
  assert.equal(marked.type, "span");
  assert.equal(marked.props.style.color, "#1a9fff");
  assert.deepEqual(marked.children, ["Game override · Ready"]);
  assert.deepEqual(overrideHelpers.overrideDescription(runtime, "FrameLimit", "").children, [
    "Game override",
  ]);
  assert.equal(overrideHelpers.overrideDescription(runtime, null, "Ready"), "Ready");
  assert.equal(overrideHelpers.overrideDescription(runtime, null, ""), undefined);
}
assert.equal(overrideHelpers.normalizeOverrideId("x".repeat(201)), null);
assert.equal(overrideHelpers.normalizeOverrideId(42), null);
assert.equal(overrideHelpers.normalizeOverrideId("   "), null);
assert.equal(overrideHelpers.normalizeOverrideId("FrameLimit"), "FrameLimit");
assert.ok(!asset.includes("useGlobal"), "no row may offer a Use global control");
// The host's own command sender over a fixture request, so a row's write carries the action
// generation exactly as the shipped host attaches it.
const createSender = (request) =>
  instantiate(
    { request, nextActionGeneration: () => 1 },
    slice(asset, "const sendCommand =", "const toggleCommand ="),
    "sendCommand",
  );
let state;
const requests = [];
const pending = [];
const api = instantiate(
  {
    normalizeText,
    ...overrideHelpers,
    useSemanticState: (_runtime, _kind, normalize) => normalize(state),
    note: () => null,
    definitions: {
      powerProfile: { patchId: "steam-ui.power-profile", command: "setPowerProfile" },
      hybridCores: { patchId: "steam-ui.hybrid-cores", command: "setHybridCores" },
      cpuBoost: { patchId: "steam-ui.cpu-boost", command: "setCpuBoost" },
      powerPreset: {
        patchId: "steam-ui.power-preset",
        acCommand: "setAcPowerPreset",
        batteryCommand: "setBatteryPowerPreset",
      },
    },
    renderOutcomes: {},
    sendCommand: createSender((...args) => {
      requests.push(args);
      return Promise.resolve();
    }),
    drew: () => {},
  },
  slice(asset, "const normalizePowerProfileState =", "const createControllerControl ="),
  "{ normalizePowerProfileState, createPowerProfileControl, createHybridCoreControl, normalizeCpuBoostState, createCpuBoostControl, normalizePowerPresetState, createPowerPresetControl }",
);
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
await tick();
assert.deepEqual(requests[0], ["steam-ui.power-profile", "setPowerProfile", { target: "b" }, 1]);
assert.deepEqual(pending, [true, false]);
state = { ...state, current: "missing" };
assert.equal(control().selectedOption, undefined);
state = { ...state, available: false, statusText: "Readback failed" };
assert.equal(control().disabled, true);
assert.equal(control().description, "Readback failed");
// No options is nothing to choose: a processor with one kind of core publishes exactly that, and
// neither dropdown may stand there empty and disabled.
{
  const previous = state;
  state = { available: false, options: [], current: "", statusText: "One kind of core" };
  const reactFixture = { dropdown: "dropdown", icon: name => name, react: {
    useState: () => [false, () => {}], createElement: (_type, props) => props } };
  assert.equal(control(), null);
  assert.equal(api.createHybridCoreControl(reactFixture)(), null);
  state = { ...previous, available: true };
  assert.equal(api.createHybridCoreControl(reactFixture)().label, "Processor cores");
  // The boost row is the same dropdown with the per-game marker in its description.
  assert.equal(api.createCpuBoostControl(reactFixture)().label, "CPU boost mode");
  assert.equal(api.createCpuBoostControl(reactFixture)().icon, "turbo");
  assert.equal(api.createCpuBoostControl(reactFixture)().description, state.statusText);
  state = { ...state, overrideId: "CpuBoost" };
  assert.deepEqual(api.createCpuBoostControl(reactFixture)().description,
    overrideHelpers.overrideDescription(reactFixture, "CpuBoost", state.statusText));
  assert.equal(api.normalizeCpuBoostState({ ...state, overrideId: 5 }).overrideId, null);
  state = { ...state, options: [] };
  assert.equal(api.createCpuBoostControl(reactFixture)(), null);
  state = previous;
}
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
await tick();
assert.deepEqual(requests.at(-1), ["steam-ui.power-preset", "setAcPowerPreset", { target: "b" }, 1]);
rows[1].props.onChange({ data: "" });
await tick();
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
const sectionTitle = instantiate(
  {},
  slice(asset, "const SectionIcons =", "const appendControls ="),
  "sectionTitle",
);
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
  const drawings = sliceToGate(asset, "const SteamUiIconShapes =");
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
const deviceState = {
  chargeLimit: { available: true, observed: 80, minimum: 60, maximum: 100, step: 1 },
  lightingBrightness: { available: true, observed: 100, minimum: 0, maximum: 100, step: 1 },
  lightingZones: [{ available: true, id: "buttons", label: "Buttons", observedColor: 0xffffff }],
};
const createDeviceControl = instantiate(
  {
    ...overrideHelpers,
    useSemanticState: () => deviceState,
    normalizeDeviceControlsState: (value) => value,
    definitions: { deviceControls: {} },
    sendCommand: createSender(() => {
      throw new Error("Rendering must not dispatch hardware writes");
    }),
    useTrailingCommit: () => () => {},
    useEchoedValue: (_runtime, value) => ({ value }),
    note: () => null,
    renderOutcomes: {},
    isBusy: () => false,
    localizeOr: (_runtime, _token, fallback) => fallback,
    sectionTitle,
    drew: () => {},
  },
  slice(asset, "const rgbToHsv =", "// Steam's own FPS counter rows"),
  "createDeviceControlsControl",
);
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

// A section whose rows all drew nothing leaves layout but stays mounted, so its rows keep their
// subscriptions and can bring it back. Valve's rows report nothing and keep theirs shown.
{
  const controlNames = ["valveProfileHeaderControl", "valveProfileToggleControl",
    "valveOverlayLevelControl", "frameLimitControl", "powerProfileControl", "hybridCoreControl",
    "cpuBoostControl", "powerPresetControl", "vrrControl", "powerLimitControl", "autoTdpControl", "resolutionControl",
    "audioFormatControl",
    "valveRefreshRateControl", "controllerControl", "valveResetControl"];
  const registrations = new Map();
  const drawnKinds = new Set();
  const { appendControls, useRows } = instantiate(
    {
      registrations,
      drawnKinds,
      appendDiagnostics: {},
      withNativeRowsHidden: (_runtime, tree) => tree,
      deviceControlsControl: undefined,
    },
    slice(asset, "const SectionIcons =", "const resolveControls ="),
    "{ appendControls, useRows: (rows) => { controlRows = rows; } }",
  );
  // The row table resolveControls builds once the controls resolve, read from the asset with each
  // control standing in as its own name.
  const table = slice(
    slice(asset, "const resolveControls =", "const install ="),
    "controlRows = [",
    "];",
  );
  useRows(
    instantiate(
      Object.fromEntries(controlNames.map((name) => [name, name])),
      "",
      `${table.slice("controlRows = ".length)}]`,
    ),
  );
  const runtime = { section: "section", row: "row", icon: () => null, react: {
    Fragment: "fragment", isValidElement: () => false,
    createElement: (type, props, ...children) => ({ type, props, children }) } };
  const layout = (placement) => {
    const tree = appendControls(runtime, "native", placement);
    const sections = placement === "perf" ? tree.children[1].children : [tree.children[0]];
    return Object.fromEntries(sections.map(wrapper => {
      assert.equal(wrapper.type, "div");
      assert.equal(wrapper.children[0].type, "section");
      assert.ok(wrapper.children[0].children.length > 0, "a hidden section keeps its rows mounted");
      return [wrapper.children[0].props.title, wrapper.props.style.display];
    }));
  };
  for (const kind of ["valveProfileHeader", "powerProfile", "hybridCores", "powerLimit",
    "controllerTarget", "valveReset", "resolution"]) registrations.set(kind, kind);
  drawnKinds.add("powerProfile");
  assert.deepEqual(layout("perf"), { "Profile scope": "contents", "Power profiles": "contents",
    "Power limits": "none", Controller: "none", Reset: "contents" });
  drawnKinds.add("powerLimit");
  assert.equal(layout("perf")["Power limits"], "contents");
  drawnKinds.delete("powerProfile");
  assert.equal(layout("perf")["Power profiles"], "none", "no drawn row, no section");
  assert.deepEqual(layout("quickSettings"), { Display: "none" });
  registrations.set("valveRefreshRate", "valveRefreshRate");
  assert.deepEqual(layout("quickSettings"), { Display: "contents" });
}
console.log("Host sections leave layout while every row under them draws nothing.");

// Hardware observations, not Steam's saved TDP setting, drive both power sliders.
{
  const hooks = createHooks();
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
      useState: hooks.useState,
      useRef: hooks.useRef,
      createElement: (type, props, ...children) => ({ type, props, children }),
    },
  };
  const powerApi = instantiate(
    {
      normalizeText,
      ...overrideHelpers,
      useSemanticState: (_runtime, _kind, normalize) => normalize(powerState),
      definitions: {
        powerLimit: {
          patchId: "steam-ui.power-limit",
          primaryCommand: "setPrimaryLimit",
          boostCommand: "setBoostLimit",
        },
      },
      sendCommand: createSender((...args) => {
        writes.push(args);
        return new Promise((resolve, reject) => replies.push({ resolve, reject }));
      }),
      isBusy: (progress) => ["queued", "applying", "replacing"].includes(progress),
      note: () => null,
      drew: () => {},
    },
    slice(asset, "const useEchoedValue =", "const useTrailingCommit =") +
      slice(asset, "const normalizePowerLimitRange =", "const createDeviceControlsControl ="),
    "{ createPowerLimitControl, normalizePowerLimitState }",
  );
  const control = powerApi.createPowerLimitControl(runtime);
  const render = () => {
    hooks.reset();
    return control()?.children.map((row) => row.children[0].props) ?? [];
  };
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
  await tick();
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
  await tick();
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

// The slider echo on its own: readback and acknowledgments never become a user's write.
{
  const hooks = createHooks();
  const useEcho = instantiate(
    {},
    slice(asset, "const useEchoedValue =", "const useTrailingCommit ="),
    "useEchoedValue",
  );
  const runtime = { react: { useState: hooks.useState } };
  const render = (observed) => {
    hooks.reset();
    return useEcho(runtime, observed);
  };
  const writes = [];
  const commit = (value) => writes.push(value);

  let slider = render(30);
  slider.onChange(30);
  slider.onChangeComplete(30, commit);
  assert.deepEqual(writes, [], "a programmatic refresh must not dispatch a write");
  slider = render(17);
  slider.onChangeComplete(17, commit);
  assert.equal(render(17).value, 17);
  assert.deepEqual(writes, [], "AutoTDP readback must not become manual intent");
  slider.onChange(19);
  assert.equal(render(17).value, 19);
  slider.onChangeComplete(19, commit);
  assert.deepEqual(writes, [19]);
  slider = render(19);
  slider.onChangeComplete(19, commit);
  slider.onChangeComplete(Number.NaN, commit);
  slider.onChangeComplete(Number.POSITIVE_INFINITY, commit);
  assert.deepEqual(writes, [19], "acknowledgments and invalid values must not repeat a write");
  console.log("Slider readback stays separate from user writes.");
}
