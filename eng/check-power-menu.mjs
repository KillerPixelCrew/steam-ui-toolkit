// Exercise the emitted power menu gate against an inert React fixture shaped like Steam's power
// menu as the 2026-10-20 client draws it: a root with a label and no onCancel (the main menu's Power
// entry passes none), Sleep as a plain item, the confirming Shutdown and Restart entries, and a
// fragment section that opens with Valve's separator and holds its plain items.
//
// What this proves is what the shipped bytes do once they hold the JSX runtime: the entry appears
// only while the host publishes it, only in the power menu, once, at the end in its own separated
// section, drawn with the item type the menu rendered and Steam's localized label; selecting it
// sends the one empty command; and removal hands the runtime back. The probe is the gate's own find:
// it counts the menu module without loading it, refuses an absent or ambiguous one as incompatible,
// and while installed answers from the runtime it holds.
import assert from "node:assert/strict";
import {
  assertRemoveRetries,
  createReact,
  element,
  failingHost,
  Fragment,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
  withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const react = createReact({ singleChild: true });
function jsxProduction(type, props, key) {
  return element(type, props, key ?? null);
}
const { host: runtime, failNext } = failingHost({ jsx: jsxProduction, jsxs: jsxProduction });
const originalJsx = runtime.jsx;
const localize = (token) => (token === "#SwitchToDesktop" ? "Zum Desktop wechseln" : token);
withSource(localize, "function F(k,...E){let R=C.LocalizeString(k);return R===void 0?k:R}");
let menuLoads = 0;
const modules = {
  react,
  runtime,
  get menu() {
    menuLoads++;
    return {};
  },
  localization: { we: localize },
};
// How many modules carry the power menu's tokens, which a probe must find exactly once.
let menuModules = 1;
const moduleFor = (tokens) =>
  tokens.includes("useState")
    ? "react"
    : tokens.includes(".jsxs")
      ? "runtime"
      : tokens.includes("#Quit_Shutdown")
        ? "menu"
        : tokens.includes("LocalizeString")
          ? "localization"
          : null;

let publish = null;
const requests = [];
const globals = {
  window: {},
  getWebpackRuntime: () => {
    const require = (id) => modules[id];
    require.count = (tokens) => (moduleFor(tokens) === "menu" ? menuModules : moduleFor(tokens) ? 1 : 0);
    require.findUnique = (tokens) => (require.count(tokens) === 1 ? [moduleFor(tokens), ""] : null);
    require.resolve = (tokens) => modules[moduleFor(tokens)];
    return require;
  },
  subscribe: (patchId, listener) => {
    assert.equal(patchId, "steam-ui.power-menu");
    publish = listener;
    return () => {
      publish = null;
    };
  },
  request: (...args) => {
    requests.push(args);
    return Promise.reject(new Error("refused"));
  },
  registerGate: () => {},
};
const gate = instantiate(
  globals,
  `${sharedFragments(asset)}\n${gateSource(asset, "gates/power-menu.ts")}`,
  "createPowerMenu()",
);

// Valve's components, as the menu renders them.
const Menu = () => null;
const Item = () => null;
const Confirmed = () => null;
const Separator = () => null;
const Empty = () => null;
const powerMenu = () =>
  runtime.jsxs(Menu, {
    label: "Power",
    onCancel: undefined,
    children: [
      false,
      [],
      false,
      runtime.jsx(Item, { onSelected() {}, children: "Sleep" }),
      runtime.jsx(Confirmed, { strDisplayNameLocToken: "#Quit_Shutdown", onSelected() {} }),
      runtime.jsx(Confirmed, { strDisplayNameLocToken: "#Quit_Restart", onSelected() {} }),
      runtime.jsx(Empty, {}),
      runtime.jsxs(Fragment, {
        children: [
          runtime.jsx(Separator, {}),
          runtime.jsxs(Fragment, {
            children: [runtime.jsx(Item, { onSelected() {}, children: "Minimize" })],
          }),
          runtime.jsx(Confirmed, { strDisplayNameLocToken: "#ExitSteam", onSelected() {} }),
        ],
      }),
    ],
  });
const entryOf = (tree) => tree.props.children.find((child) => child?.key === "steam-ui-power-menu-desktop");

// The probe counts the menu module and never loads it; absent and ambiguous are both incompatible.
for (const [count, outcome] of [
  [0, "incompatible"],
  [2, "incompatible"],
  [1, "ready"],
]) {
  menuModules = count;
  const probe = gate.probe();
  assert.equal(probe.outcome, outcome, `a menu module counted ${count} times must be ${outcome}`);
  assert.equal(probe.facts.menuModule, count, "the probe reports the count it saw");
}
assert.equal(menuLoads, 0, "a probe must not load the menu module");
assert.equal(gate.status().installed, false, "a probe installs nothing");

assert.equal(gate.install().ok, true);
assert.ok(gate.status().claimed && gate.status().resolved && gate.status().localized);
assert.equal(gate.status().held, true, "the standard status shows the claim held");
menuModules = 2;
assert.equal(gate.probe().outcome, "ready", "an installed gate answers from what it holds");
menuModules = 1;
assert.equal(entryOf(powerMenu()), undefined, "nothing before the host publishes");

publish({ visible: true });
const tree = powerMenu();
const section = entryOf(tree);
assert.ok(section, "the entry is appended once published");
assert.equal(tree.props.children.at(-1), section, "at the end of the menu, where Valve places it");
assert.equal(section.type, Fragment);
const [separator, entry] = section.props.children;
assert.equal(separator.type, Separator, "the section opens with Valve's separator");
assert.equal(entry.type, Item, "drawn with the plain item type the menu rendered");
assert.equal(entry.props.children, "Zum Desktop wechseln", "labelled with Steam's own string");
assert.equal(entry.props.tone, "destructive");

entry.props.onSelected();
await new Promise((resolve) => setImmediate(resolve));
assert.deepEqual(requests.at(-1).slice(0, 3), ["steam-ui.power-menu", "switchToDesktop", {}]);

// Valve's root renders an inner menu with the same props. It is not the learned type, so the entry
// the root received is not appended a second time.
const Inner = () => null;
const inner = runtime.jsxs(Inner, { labelId: "id", ...tree.props, onCancel() {} });
assert.equal(
  inner.props.children.filter((child) => child?.key === "steam-ui-power-menu-desktop").length,
  1,
  "the inner menu keeps the root's single entry",
);

// Another menu with a label and onCancel but none of the power entries stays Steam's.
const other = runtime.jsxs(Menu, {
  label: "Other",
  onCancel() {},
  children: [runtime.jsx(Item, { onSelected() {}, children: "Something" })],
});
assert.equal(entryOf(other), undefined, "a menu of the same type without the power entries stays Steam's");

publish({ visible: false });
assert.equal(entryOf(powerMenu()), undefined, "withdrawn when the host hides it");
publish({ visible: true });
assert.equal(powerMenu().props.children.filter((child) => child?.key === "steam-ui-power-menu-desktop").length, 1, "drawn once on a later render");

assert.equal(gate.remove().ok, true);
assert.equal(runtime.jsx, originalJsx, "the runtime is handed back");
assert.equal(gate.status().claims, 0, "removal leaves nothing on the ledger");
assert.equal(gate.remove().absent, true);

// However many entries the menu has, it is still the power menu.
assert.equal(gate.install().ok, true);
publish({ visible: true });
const long = runtime.jsxs(Menu, {
  label: "Power",
  onCancel() {},
  children: [
    runtime.jsx(Confirmed, { strDisplayNameLocToken: "#Quit_Sleep", onSelected() {} }),
    ...Array.from({ length: 59 }, (_, index) =>
      runtime.jsx(Item, { onSelected() {}, children: `Item ${index}` }, `item-${index}`),
    ),
  ],
});
assert.ok(entryOf(long), "a menu with 60 children is recognised and gets the entry");
assertRemoveRetries(gate, failNext, "power menu");
assert.equal(runtime.jsx, originalJsx, "a retried removal hands the runtime back");

// The elements gate rides the same JSX runtime; its probe has the standard shape too.
let modulesLoaded = true;
const elements = instantiate(
  {
    ...globals,
    getWebpackRuntime: () => {
      if (!modulesLoaded) throw new Error("Steam modules unavailable");
      return globals.getWebpackRuntime();
    },
  },
  `${sharedFragments(asset)}\n${gateSource(asset, "gates/elements.ts")}`,
  "createElementsGate()",
);
assert.deepEqual(elements.probe(), { ok: true, outcome: "ready", facts: { jsx: 1 } });
modulesLoaded = false;
assert.equal(elements.probe().outcome, "notReady", "an uncapturable webpack runtime is not ready");
modulesLoaded = true;
assert.equal(elements.register("check", () => undefined).ok, true);
assert.equal(elements.registered("check"), true);
assert.equal(elements.unregister("check").ok, true);
assert.equal(runtime.jsx, originalJsx, "the last transform hands the runtime back");
console.log("Power menu emitted checks passed.");
