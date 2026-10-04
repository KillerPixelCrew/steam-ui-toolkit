// Exercise the emitted power menu gate against an inert React fixture shaped like Steam's power
// menu: a root with a label, onCancel and the Sleep and Shutdown entries, and a fragment section
// that opens with Valve's separator and holds its plain items.
//
// What this proves is what the shipped bytes do once they hold the JSX runtime: the entry appears
// only while the host publishes it, only in the power menu, once, at the end in its own separated
// section, drawn with the item type the menu rendered and Steam's localized label; selecting it
// sends the one empty command; and removal hands the runtime back.
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
} from "./check-harness.mjs";

const asset = loadAsset();
const react = createReact({ singleChild: true });
function jsxProduction(type, props, key) {
  return element(type, props, key ?? null);
}
const { host: runtime, failNext } = failingHost({ jsx: jsxProduction, jsxs: jsxProduction });
const originalJsx = runtime.jsx;
const localize = (token) => (token === "#SwitchToDesktop" ? "Zum Desktop wechseln" : token);
Object.defineProperty(localize, "toString", {
  value: () => "function F(k,...E){let R=C.LocalizeString(k);return R===void 0?k:R}",
});
const modules = { react, runtime, menu: {}, localization: { we: localize } };
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
    require.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
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
  nextActionGeneration: () => 1,
  registerGate: () => {},
};
const gate = instantiate(
  globals,
  `${sharedFragments(asset)}\n${gateSource(asset, "createPowerMenu", "powerMenu")}`,
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
    onCancel() {},
    children: [
      [],
      false,
      runtime.jsx(Confirmed, { strDisplayNameLocToken: "#Quit_Sleep", onSelected() {} }),
      runtime.jsx(Confirmed, { strDisplayNameLocToken: "#Quit_Shutdown", onSelected() {} }),
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

assert.equal(gate.install().ok, true);
assert.ok(gate.status().claimed && gate.status().resolved && gate.status().localized);
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
console.log("Power menu emitted checks passed.");
