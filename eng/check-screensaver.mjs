// Exercise the emitted Screensaver settings gate against an inert React fixture.
//
// Resolution is checked live and pinned by the patch's probe. What this proves is what the shipped
// bytes do once they hold the Settings page list: that the customization page is wrapped through the
// shared useMemo claim and nothing else in the list changes, that the host's rows land at the end of
// the Screensaver section and nowhere else, that Steam's timeouts are reported when they first
// arrive, when they change and when the page opens, that a choice is sent once and the row is
// disabled while it is pending, that a malformed publication is refused whole, that another surface
// holding the same claim keeps it when this gate is removed, and that removal hands everything back.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createScreensaverSettings()");
assert.ok(start >= 0, "the emitted asset must contain the screensaver settings gate");
const end = asset.indexOf('registerGate("screensaver"', start);
assert.ok(end > start, "the screensaver settings gate must register itself");
const ownershipStart = asset.indexOf("const defineHidden");
const ownershipEnd = asset.indexOf("const transportReply", ownershipStart);
assert.ok(ownershipStart >= 0 && ownershipEnd > ownershipStart, "the ownership primitives must be emitted");

const ElementMarker = Symbol("element");
const Fragment = Symbol.for("react.fragment");
const element = (type, props, key = null) => ({ [ElementMarker]: true, type, props: props ?? {}, key });
const withSource = (fn, source) => {
  Object.defineProperty(fn, "toString", { value: () => source });
  return fn;
};

let effects = [];
function originalUseMemo(factory) {
  return factory();
}
const react = {
  Fragment,
  useMemo: originalUseMemo,
  createElement(type, props, ...children) {
    const { key = null, ...rest } = props ?? {};
    return element(type, children.length ? { ...rest, children } : rest, key);
  },
  cloneElement: (source, props, ...children) =>
    element(
      source.type,
      children.length ? { ...source.props, ...props, children } : { ...source.props, ...props },
      source.key,
    ),
  isValidElement: (value) => !!value && typeof value === "object" && value[ElementMarker] === true,
  useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
  useEffect: (effect) => effects.push(effect),
};
const runEffects = () => effects.splice(0).forEach((effect) => effect());

// Valve's modules, reduced to what the gate resolves on.
const Dropdown = withSource(function Dropdown() {
  return null;
}, "function(){contextMenuPositionOptions childrenContainerWidth menuLabel}");
const Section = () => null;
const IdleRow = () => null;
const ScreensaverSection = withSource(
  function ScreensaverSection() {
    return element(Section, {
      label: "Screensaver",
      children: [element("current", {}), element("preview", {}), element(IdleRow, { location: "customizationsettings" })],
    });
  },
  'function Ms(){(0,d.we)("#Settings_Customization_Screensaver");Pi.b3.ForceScreensaver({enabled:!0})}',
);
const StartupSection = withSource(function StartupSection() {
  return element(Section, { label: "Startup movie" });
}, 'function as(){(0,d.we)("#Settings_Customization_StartupVideo")}');
const SectionList = () => null;
const CustomizationPage = () =>
  element(SectionList, {
    children: [element(StartupSection, {}), element(ScreensaverSection, {}), element("advanced", {})],
  });
const SystemPage = () => element(SectionList, { children: [] });

const settings = {
  clientSettings: { system_idle_screensaver_ac_sec: 300, system_idle_screensaver_battery_sec: 600 },
};
const windowFixture = { SystemPowerStore: { batteryState: { bHasBattery: false } } };
const useObserver = withSource(
  function (fn, _name) {
    return fn();
  },
  'function _e(X,ge){return ge===void 0&&(ge="observed"),Oe(X,ge)}',
);

const modules = {
  react,
  fields: { Vb: Dropdown, Xg: () => null },
  routes: { B: { Settings: { Customization: () => "/settings/customization" } }, C: () => "/routes" },
  settings: { rV: settings, VI: () => null },
  section: {},
  observer: { q3: useObserver, PA: () => null },
};
const moduleFor = (tokens) =>
  tokens.includes("useState")
    ? "react"
    : tokens.includes("SliderField")
      ? "fields"
      : tokens.includes("GameAPIOSK:")
        ? "routes"
        : tokens.includes("m_setDeferredSettings")
          ? "settings"
          : tokens.includes("ForceScreensaver")
            ? "section"
            : tokens.some((token) => token.includes("mobx-react-lite"))
              ? "observer"
              : null;

const timers = [];
const requests = [];
const globals = {
  window: windowFixture,
  setTimeout: (callback) => timers.push(callback),
  clearTimeout: (id) => {
    timers[id - 1] = null;
  },
  getWebpackRuntime: () => {
    const require = (id) => modules[id];
    require.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
    require.resolve = (tokens) => modules[moduleFor(tokens)];
    require.exported = (tokens, predicate) => {
      const fits = new Set(Object.values(modules[moduleFor(tokens)]).filter((value) => predicate(value)));
      assert.equal(fits.size, 1, `one export must fit ${tokens.join(", ")}`);
      return [...fits][0];
    };
    return require;
  },
  request: (patchId, command, payload) => {
    assert.equal(patchId, "steam-ui.screensaver");
    requests.push({ command, payload });
    return Promise.resolve();
  },
  subscribe: (patchId, listener) => {
    assert.equal(patchId, "steam-ui.screensaver");
    globals.publish = listener;
    return () => {
      globals.publish = null;
    };
  },
  publish: null,
};

const gate = new Function(
  ...Object.keys(globals),
  asset.slice(ownershipStart, ownershipEnd) +
    "\n" +
    asset.slice(start, end) +
    "\nreturn { gate: createScreensaverSettings(), interceptMemo, releaseMemo };",
)(...Object.values(globals));
const { interceptMemo, releaseMemo } = gate;
const screensaver = gate.gate;

const customization = { visible: true, title: "Customization", route: "/settings/customization", content: element(CustomizationPage, {}) };
const system = { visible: true, title: "System", route: "/settings/system", content: element(SystemPage, {}) };
const pages = [system, customization];
const pageList = () => react.useMemo(() => pages, []);

const named = (name) => (node) => react.isValidElement(node) && typeof node.type === "function" && node.type.name === name;
const renderPage = () => {
  const list = pageList();
  const content = list.find((item) => item.route === "/settings/customization").content;
  const tree = content.type(content.props);
  return { list, content, tree };
};
const renderSection = (tree) => {
  const section = tree.props.children.find((child) => child.type === ScreensaverSection || named("SteamUiScreensaverSection")(child));
  return section.type(section.props);
};
const renderRows = (section) => {
  const rows = section.props.children.at(-1);
  assert.ok(named("SteamUiScreensaverTimeouts")(rows), "the rows must be the section's last child");
  const rendered = rows.type(rows.props);
  runEffects();
  return rendered;
};

// Before the gate: Steam's list, Steam's page.
assert.equal(pageList(), pages);
assert.equal(react.useMemo, originalUseMemo);

let result = screensaver.install();
assert.ok(result.ok, `install failed: ${result.error}`);
let status = screensaver.status();
assert.ok(status.resolved && status.claimed, "the gate must resolve and hold the useMemo claim");
assert.equal(status.route, "/settings/customization");
assert.ok(status.tracking, "Steam's observer hook must resolve");
assert.deepEqual(requests.shift(), {
  command: "report",
  payload: { acSeconds: 300, batterySeconds: 600, battery: false },
});

// Only the customization page is wrapped, and the same list maps to the same result.
let view = renderPage();
assert.equal(view.list[0], system, "other pages must pass through untouched");
assert.ok(named("SteamUiCustomizationPage")(view.content), "the customization page must be wrapped");
assert.equal(pageList(), view.list, "an unchanged page list must keep its identity");
assert.equal(view.tree.props.children[0].type, StartupSection, "other sections must stay Steam's");
assert.ok(named("SteamUiScreensaverSection")(view.tree.props.children[1]), "the screensaver section must be wrapped");

// Opening the page refreshes the host; nothing is drawn until the host publishes rows.
let section = renderSection(view.tree);
assert.equal(section.props.children.length, 4, "the rows must be appended after Steam's own");
assert.equal(section.props.children[2].type, IdleRow, "Steam's idle row must stay where it was");
assert.equal(renderRows(section), null);
assert.deepEqual(requests.splice(0).map((entry) => entry.command), ["report"], "opening the page must report once");

const row = (fields = {}) => ({
  id: "ac",
  label: "Turn display off after",
  description: "",
  seconds: 600,
  options: [
    { seconds: 300, label: "5 min" },
    { seconds: 600, label: "10 min" },
    { seconds: 0, label: "Never" },
  ],
  available: true,
  ...fields,
});
globals.publish({ rows: [row()], revision: 1 });
let rows = renderRows(renderSection(renderPage().tree));
assert.equal(rows.type, Fragment);
let dropdown = rows.props.children[0];
assert.equal(dropdown.type, Dropdown, "rows must use Valve's own dropdown field");
assert.equal(dropdown.props.label, "Turn display off after");
assert.equal(dropdown.props.selectedOption, 600);
assert.equal(dropdown.props.controlled, true);
assert.deepEqual(dropdown.props.rgOptions, [
  { data: 300, label: "5 min" },
  { data: 600, label: "10 min" },
  { data: 0, label: "Never" },
]);
assert.equal(dropdown.props.disabled, false);
requests.length = 0;

// A choice is sent once, the row is disabled while it is pending, and the current value sends nothing.
dropdown.props.onChange({ data: 600 });
assert.equal(requests.length, 0, "choosing the current value must send nothing");
dropdown.props.onChange({ data: 300 });
dropdown.props.onChange({ data: 0 });
assert.deepEqual(requests.splice(0), [{ command: "setTimeout", payload: { row: "ac", seconds: 300 } }]);
rows = renderRows(renderSection(renderPage().tree));
assert.equal(rows.props.children[0].props.disabled, true, "a pending row must be disabled");
requests.length = 0;
await Promise.resolve();
await Promise.resolve();
rows = renderRows(renderSection(renderPage().tree));
assert.equal(rows.props.children[0].props.disabled, false, "a settled row must be enabled again");
requests.length = 0;

// A malformed publication is refused whole and the last good rows stay.
for (const malformed of [
  { rows: [row({ id: "AC" })] },
  { rows: [row({ seconds: -1 })] },
  { rows: [row({ options: [{ seconds: 300, label: "" }] })] },
  { rows: [row(), row()] },
  { rows: Array.from({ length: 5 }, (_, index) => row({ id: `r${index}` })) },
  { rows: "none" },
]) {
  globals.publish(malformed);
  assert.equal(screensaver.status().lastOutcome, "state received but rejected by validation");
  assert.equal(screensaver.status().rows, 1);
}

// A change to Steam's timeout is reported once, from the render that observed it.
settings.clientSettings.system_idle_screensaver_ac_sec = 900;
renderRows(renderSection(renderPage().tree));
assert.deepEqual(
  requests.filter((entry) => entry.command === "report").at(-1)?.payload,
  { acSeconds: 900, batterySeconds: 600, battery: false },
);
requests.length = 0;
// The fixture runs mount effects on every render, so the page-open report is the one that repeats;
// the change report must not.
renderRows(renderSection(renderPage().tree));
assert.equal(
  requests.filter((entry) => entry.command === "report").length,
  1,
  "an unchanged reading must not be reported as a change",
);
requests.length = 0;

// Another surface on the same claim keeps it when this gate is removed.
const tabs = [{ panel: element("tab", {}) }];
let tabTransforms = 0;
assert.ok(interceptMemo(react, "quickAccessTabs", (value) => {
  if (value === tabs) tabTransforms++;
  return value;
}).ok);
react.useMemo(() => tabs, []);
assert.equal(tabTransforms, 1, "both transforms must run on the one claim");

const mounted = renderPage();
result = screensaver.remove();
assert.ok(result.ok, `remove failed: ${result.error}`);
assert.equal(screensaver.status().claimed, false);
assert.notEqual(react.useMemo, originalUseMemo, "the other surface must keep the claim");
assert.equal(pageList(), pages, "the page list must be Steam's again");
assert.equal(mounted.content.type(mounted.content.props).props.children[1].type, ScreensaverSection, "a mounted page must pass Steam's tree through");
assert.ok(releaseMemo(react, "quickAccessTabs").ok);
assert.equal(react.useMemo, originalUseMemo, "the last transform must hand useMemo back");

// Settings that arrive after install are reported on the bounded retry, and reinstall works.
const saved = settings.clientSettings;
settings.clientSettings = {};
requests.length = 0;
timers.length = 0;
result = screensaver.install();
assert.ok(result.ok, `reinstall failed: ${result.error}`);
assert.equal(requests.length, 0, "nothing may be reported before Steam's settings exist");
assert.equal(timers.length, 1, "the first report must be retried");
settings.clientSettings = saved;
timers.shift()();
assert.deepEqual(requests.shift()?.payload, { acSeconds: 900, batterySeconds: 600, battery: false });
windowFixture.SystemPowerStore.batteryState.bHasBattery = true;
settings.clientSettings.system_idle_screensaver_battery_sec = undefined;
globals.publish({ rows: [row()], revision: 2 });
renderRows(renderSection(renderPage().tree));
assert.deepEqual(
  requests.filter((entry) => entry.command === "report").at(-1)?.payload,
  { acSeconds: 900, batterySeconds: null, battery: true },
);

// Two customization entries is ambiguous, and the list is left alone.
const twice = [customization, { ...customization }];
assert.equal(react.useMemo(() => twice, []), twice);
assert.ok(screensaver.remove().ok);
assert.equal(react.useMemo, originalUseMemo);

console.log(
  "Screensaver settings: rows appended to Steam's section only, reports and choices sent once, malformed state refused, shared claim kept and released.",
);
