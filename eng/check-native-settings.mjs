// Native Settings ownership and dynamic page augmentation, against the emitted gate. The probe is the
// gate's own find: Steam's modules not yet loadable are not ready, a contract matched twice is
// incompatible, and an installed gate answers from what it holds. Release hands back every Settings
// root drawing the claim, including one Steam mounted after install through the JSX transform.
import assert from "node:assert/strict";
import {
  assertRemoveRetries,
  createReact,
  element,
  failingHost,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
  substitute,
  withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const { host: react, failNext } = failingHost(
  createReact({
    useMemo: (factory) => factory(),
    useSyncExternalStore: (_subscribe, revision) => revision(),
    useEffect: () => {},
  }),
);
const jsxRuntime = { jsx: element, jsxs: element };
const originalMemo = react.useMemo;
const originalJsx = jsxRuntime.jsx;
const Section = () => null;
const NativeBody = () => null;
const nativeContent = element(NativeBody, {});
const descriptor = (id, visible = true) => ({
  route: `/settings/${id}`,
  visible,
  title: id,
  content: nativeContent,
});
const map = {
  Display: descriptor("display"),
  Power: descriptor("power", false),
  Audio: descriptor("audio"),
  Controller: descriptor("controller"),
  Internet: descriptor("internet"),
};
const NativeRoot = withSource(function NativeRoot(props) {
  return react.useMemo(() => props?.descriptors ?? map);
}, '#Settings_Title show-icon "Settings"');
let renderRequests = 0;
const parent = { stateNode: { isReactComponent: true, forceUpdate: () => renderRequests++ } };
const fiber = { type: NativeRoot, elementType: NativeRoot, memoizedProps: {}, return: parent };
const rootHost = { __reactContainer$fixture: fiber };
const popupDocument = { getElementById: () => rootHost, body: { children: [] } };
const popup = { m_popup: { document: popupDocument } };
// Steam lists the same popup twice; one document is one root.
const popups = [popup, popup];
const sharedRootHost = { __reactContainer$fixture: { type: () => null } };
const ui = Object.fromEntries(
  [
    "settingsSection",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "valueField",
    "dialogButton",
    "smallButton",
    "showModal",
    "modalRoot",
    "confirmModal",
  ].map((key) => [key, Section]),
);
ui.react = react;
let publication;
const requests = [];
// How many modules each fingerprint matches; one everywhere is a compatible client.
const counts = {};
let modulesLoaded = true;
const runtime = {
  count: (tokens) => counts[tokens[0]] ?? 1,
  findUnique: () => ["fixture", ""],
  exported: () => NativeRoot,
  resolve: () => jsxRuntime,
};
const exposed = instantiate(
  {
    window: { g_PopupManager: { GetPopups: () => popups } },
    document: { getElementById: () => sharedRootHost, body: { children: [] } },
    getWebpackRuntime: () => {
      if (!modulesLoaded) throw new Error("Steam modules unavailable");
      return runtime;
    },
    fixtureComponents: () => ui,
    useSteamSettingDrafts: () => ({
      change: (send) => send,
      row: (row) => row,
      draft: () => undefined,
    }),
    renderSteamSettingRow: (_ui, row, _draft, change, action) =>
      element("row", { row, change, action }),
    steamUiKitStyle: () => element("style", {}),
    subscribe: (_id, callback) => {
      publication = callback;
      return () => {};
    },
    request: (...args) => {
      requests.push(args);
      return Promise.resolve();
    },
  },
  substitute(sharedFragments(asset), "resolveSteamUiComponents", "fixtureComponents") +
    gateSource(asset, "gates/native-settings.ts"),
  "({gate:createNativeSettings(), interceptMemo, releaseMemo})",
);
const gate = exposed.gate;
const row = { key: "device/charge", kind: "boolean", label: "Charge limit", checked: true };
const state = (pages, revision = 1) => ({ pages, revision });
const page = (id, rows = [row]) => ({ id, sections: [{ id: "device", title: "Device", rows }] });

modulesLoaded = false;
assert.equal(gate.probe().outcome, "notReady", "a webpack runtime not yet capturable is not ready");
modulesLoaded = true;
for (const token of ["#Settings_Page_Display", "#Settings_Title", "react.transitional.element"]) {
  counts[token] = 2;
  const probe = gate.probe();
  assert.equal(probe.outcome, "incompatible", `an ambiguous ${token} module is incompatible`);
  delete counts[token];
}
let probe = gate.probe();
assert.equal(probe.outcome, "ready", probe.reason);
assert.deepEqual(probe.facts, { descriptors: 1, root: 1, react: 1, jsx: 1, fields: 1, focusable: 1 });
assert.equal(fiber.type, NativeRoot, "a probe adopts nothing");
assert.equal(jsxRuntime.jsx, originalJsx, "a probe claims nothing");

assert.equal(gate.install().ok, true);
assert.notEqual(fiber.type, NativeRoot, "an already mounted popup-native root is adopted");
assert.equal(gate.status().held, true, "the standard status shows every claim held");
counts["#Settings_Title"] = 2;
assert.equal(gate.probe().outcome, "ready", "an installed gate answers from what it holds");
delete counts["#Settings_Title"];
assert.equal(
  renderRequests,
  1,
  "duplicate popup documents produce only one native ancestor render",
);
publication(state([page("power")]));
let result = fiber.type({});
assert.equal(result.Power.visible, true, "only the native Power descriptor is revealed");
assert.equal(map.Power.visible, false, "Steam's original descriptor is untouched");
const waiting = { ...map, Display: { ...map.Display, visible: false } };
assert.equal(
  fiber.type({ descriptors: waiting }).Power.visible,
  false,
  "host rows cannot reveal Power before Steam's native services are ready",
);
assert.equal(
  result.Display.route,
  map.Display.route,
  "an empty slot retains the native page route",
);
assert.equal(
  result.Display.content.props.children[0],
  nativeContent,
  "an empty slot retains native content",
);
assert.equal(
  result.Display.content.type,
  "div",
  "native content and host sections share one wrapper instead of sitting side by side",
);
assert.equal(
  result.Display.content.props.className,
  "DialogBody steam-ui-kit-settings-body",
  "the wrapper is the page's scrolling body, so Steam's own body inside it keeps its height",
);
assert.equal(
  result.Display.content.props.children.at(-1).type,
  "style",
  "the wrapper carries the kit stylesheet even while its page has no host rows",
);
assert.equal(result.Internet, map.Internet);
assert.equal(result.Power.route, map.Power.route, "Steam retains route and navigation ownership");
assert.equal(
  result.Power.content.props.children[0],
  nativeContent,
  "native fields are retained before host sections",
);
const host = result.Power.content.props.children[1];
const controllerSlot = result.Controller.content.props.children[1];
assert.equal(
  controllerSlot.type(controllerSlot.props),
  null,
  "the Controller slot mounts while it has no rows",
);
const beforeControllerPublication = renderRequests;
publication(state([page("power"), page("controller", [{ ...row, key: "device/controller" }])], 2));
assert.equal(
  renderRequests,
  beforeControllerPublication,
  "Controller capability arrival requires no outer root render",
);
assert.equal(
  fiber.type({}).Controller.content.props.children[1],
  controllerSlot,
  "Controller retains its already mounted slot",
);
assert.ok(
  controllerSlot.type(controllerSlot.props),
  "Controller's existing empty slot renders the new capability rows",
);
const sectionTree = host.type(host.props);
const field = sectionTree.props.children.find((child) => child?.type === Section).props.children[0];
field.props.change(row, false);
assert.equal(requests.length, 1);
assert.equal(requests[0][0], "steam-ui.native-settings");
assert.deepEqual(requests[0][2], { key: "device/charge", value: false });

// The same original map produces one stable decorated map until page availability changes.
assert.equal(fiber.type({}), result);
const beforePowerRetraction = renderRequests;
publication(state([page("controller", [{ ...row, key: "device/controller" }])], 3));
result = fiber.type({});
assert.ok(
  renderRequests > beforePowerRetraction,
  "Power retraction refreshes the popup-native root",
);
assert.equal(
  result.Power.visible,
  map.Power.visible,
  "retracting host Power sections restores native visibility",
);
assert.equal(host.type(host.props), null, "a mounted section retracts after capability loss");
const beforePowerReturn = renderRequests;
publication(state([page("power")], 4));
assert.ok(renderRequests > beforePowerReturn, "Power reappearance refreshes the popup-native root");
assert.equal(
  fiber.type({}).Power.visible,
  true,
  "Power reappears without navigating or remounting the native root",
);
publication(state([page("controller"), page("controller")], 5));
assert.match(
  gate.status().publicationError,
  /publication is invalid/u,
  "duplicate pages are refused whole",
);
assert.equal(
  fiber.type({}).Power.visible,
  map.Power.visible,
  "an invalid publication restores native Power visibility",
);
assert.equal(
  controllerSlot.type(controllerSlot.props),
  null,
  "an invalid publication retracts stale actionable rows",
);
publication(null);
assert.equal(host.type(host.props), null, "null state empties every mounted host slot");

// A Settings root Steam mounts after install is created through the JSX transform, so it carries the
// claim's wrapper on both of its fibers; the list adopted at install never saw it.
const created = jsxRuntime.jsx(NativeRoot, {});
assert.notEqual(created.type, NativeRoot, "a later Settings root is created with the claim's wrapper");
let laterRenders = 0;
const laterParent = { stateNode: { isReactComponent: true, forceUpdate: () => laterRenders++ } };
const laterRoot = { type: created.type, elementType: created.type, memoizedProps: {}, return: laterParent };
laterRoot.alternate = { ...laterRoot, alternate: laterRoot };
const laterHost = { __reactContainer$later: laterRoot };
const laterDocument = { getElementById: () => laterHost, body: { children: [] } };
popups.push({ m_popup: { document: laterDocument } });

// Shared claim removal leaves an unrelated surface's transform in place.
assert.equal(exposed.interceptMemo(react, "fixture-other", (value) => value).ok, true);
const rendersBeforeRemoval = renderRequests;
assert.equal(gate.remove().ok, true);
assert.equal(fiber.type, NativeRoot, "the mounted root is handed back exactly");
assert.ok(renderRequests > rendersBeforeRemoval, "the handed-back root is asked to render");
assert.equal(laterRoot.type, NativeRoot, "a root mounted after install is handed back");
assert.equal(laterRoot.alternate.type, NativeRoot, "on both of its fibers");
assert.equal(laterRenders, 1, "and asked to render");
assert.equal(gate.status().claims, 0, "removal leaves nothing on the ledger");
popups.pop();
assert.equal(jsxRuntime.jsx, originalJsx);
assert.notEqual(react.useMemo, originalMemo);
exposed.releaseMemo(react, "fixture-other");
assert.equal(react.useMemo, originalMemo);
assert.equal(
  fiber.type({}),
  map,
  "removal restores the exact native descriptor map without host slots",
);

assert.equal(gate.install().ok, true);
assertRemoveRetries(gate, failNext, "native Settings");
assert.equal(fiber.type, NativeRoot);
assert.equal(gate.status().claimsRemaining, false);
console.log(
  "Native Settings: mounted adoption, native fields, dynamic omission, Power visibility and claim restoration passed.",
);
