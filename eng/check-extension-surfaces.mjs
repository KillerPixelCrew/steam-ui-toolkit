import assert from "node:assert/strict";
import {
  createReact,
  declarations,
  element,
  fragments,
  gateSource,
  helperLabels,
  instantiate,
  loadAsset,
  readSource,
  sharedFragments,
  tick,
  withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const subscriptions = new Map();
let refuseRequests = false;
const requests = [];
// What a host command answers with. An action that opens a page answers with a route; every other
// action answers with nothing, which is what the existing rows assert against.
let nextAnswer;
// Steam's two stores the route follow touches, and the order it touched them in. Navigating while
// the Quick Access panel is still open renders the page behind it, so the order is the point.
const navigation = [];
const window = {
  SteamUIStore: {
    WindowStore: {
      MainWindowInstance: {
        MenuStore: { CloseSideMenus: () => navigation.push("closed") },
      },
    },
  },
  tempNavStore: { m_history: { push: (route) => navigation.push(route) } },
};
// Real hook cells, in render order: the tab keeps typed drafts in useState, and the reconciliation
// this check pins is only visible across renders that remember what the previous one stored.
const hookCells = [];
let hookIndex = 0;
const react = createReact({
  useState: (initial) => {
    const index = hookIndex++;
    if (hookCells.length <= index) {
      hookCells.push(typeof initial === "function" ? initial() : initial);
    }
    return [
      hookCells[index],
      (next) => {
        hookCells[index] = typeof next === "function" ? next(hookCells[index]) : next;
      },
    ];
  },
  useEffect: () => {},
});
// Steam's components, each with the source text the resolvers fingerprint it by. The tab draws
// with these and nothing else, so the check runs the path a real client takes.
const named = (name, source) =>
  withSource(Object.defineProperty(() => null, "name", { value: name }), source);
const NativePanel = named("Focusable", "onActivate onCancel focusableIfEmpty focusClassName");
const Slider = named("Slider", "onChangeComplete notchCount valueSuffix explainerTitle");
const Dropdown = named("Dropdown", "contextMenuPositionOptions childrenContainerWidth menuLabel");
const Toggle = named("Toggle", "OnToggleChange this.Toggle()");
const Button = named("Button", '"DialogButton","_DialogLayout","Secondary"');
const Primary = named("Primary", '"DialogButton","_DialogLayout","Primary"');
const TextField = named("TextField", "class TextField");
TextField.validateUrl = () => true;
TextField.validateEmail = () => true;
const ValueField = withSource(
  Object.defineProperty((props) => props && null, "name", { value: "ValueField" }),
  'return <Field label={name} focusable inlineWrap="shift-children-below" {...rest}>',
);
const SmallButton = named("SmallButton", '"DialogButton _DialogLayout Small"');
const PanelSection = named("PanelSection", "PanelSectionTitle spinner");
const PanelRow = { $$typeof: Symbol.for("react.forward_ref"), render: () => null };
// Valve's own tab array, kept across renders the way the client keeps it: the gate pushes into it
// in place, so the array is what proves the tab is added once and taken out on removal.
// One of Valve's tabs, with the title element Valve's tabs carry, so the check can see ours drawn
// with that element rather than a bare div.
const qamTabs = [{ key: 5, strTitle: "Leistung", title: element("div", { className: "valve-title", children: ["Leistung"] }) }];
const originalRoot = withSource(
  () => element("tabs", { tabs: qamTabs }),
  "QuickAccessMenuBrowserView",
);
let releaseBlocked = false;
// A host whose property redefinition can be made to throw, which is what a release failure looks
// like from inside the gate.
const memo = new Proxy(
  { type: originalRoot },
  {
    defineProperty(target, property, descriptor) {
      if (releaseBlocked && property === "type") throw new Error("release blocked");
      return Reflect.defineProperty(target, property, descriptor);
    },
  },
);
const jsx = {
  jsx: (type, props) => element(type, props),
  jsxs: (type, props) => element(type, props),
};
const originalJsx = jsx.jsx;
let nativeAvailable = true;
const modules = {
  react: { tokens: ["react.transitional.element", "useState", "cloneElement", "createElement"], exports: react },
  fields: {
    tokens: ["DialogSlider_Container", "DropDownField", "SliderField"],
    exports: { Slider, Dropdown, Toggle, Button, Primary, TextField, ValueField, SmallButton },
  },
  focusable: { tokens: ["focusableIfEmpty", "onActivate", '"Panel"'], exports: { NativePanel } },
  layout: { tokens: ["PanelSectionTitle", "PanelSectionRow", "spinner"], exports: { PanelSection, PanelRow } },
  qam: { tokens: ["QuickAccessMenuBrowserView"], exports: { memo } },
  // The game menu module is found, never read: the menu gate claims its class through the JSX
  // runtime instead.
  gameMenu: {
    tokens: ["GetTargetApps", "BuildManageSubmenu", "GetPrimaryActionMenuItem"],
    exports: {},
  },
};
const moduleFor = (tokens) =>
  Object.keys(modules).find(
    (id) =>
      (nativeAvailable || id !== "layout") &&
      tokens.every((token) => modules[id].tokens.includes(token)),
  );
const runtime = (id) => modules[id].exports;
runtime.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
runtime.resolve = () => jsx;
runtime.exported = (tokens, predicate) => {
  const id = moduleFor(tokens);
  if (!id) throw new Error("absent");
  const fits = Object.values(modules[id].exports).filter((value) => predicate(value));
  if (fits.length !== 1) throw new Error("ambiguous");
  return fits[0];
};
const globals = {
  getWebpackRuntime: () => runtime,
  subscribe: (id, callback) => {
    subscriptions.set(id, callback);
    return () => subscriptions.delete(id);
  },
  request: (...args) => {
    requests.push(args);
    return refuseRequests ? Promise.reject(new Error("refused")) : Promise.resolve(nextAnswer);
  },
  nextActionGeneration: () => 1,
  createIconRenderer: () => () => null,
  window,
};
// The settings renderer and the kit sit after the icons, outside the shared fragments; the tab
// draws its settings with them. The icon renderer is the fixture's own, so only the glyph helpers
// are taken from the icons fragment.
const helpers = helperLabels(asset);
const code =
  sharedFragments(asset) +
  declarations(asset, "icons.ts", ["SteamGlyphPattern", "steamGlyphCaches", "renderSteamGlyph"]) +
  "\n" +
  fragments(asset, helpers.slice(helpers.indexOf("icons.ts") + 1)) +
  gateSource(asset, "gates/extensions-tab.ts") +
  gateSource(asset, "gates/game-context-menu.ts");
const { extensions, menu, createExtensions, unclaimedValue } = instantiate(
  globals,
  code,
  "({extensions:createExtensionsTab(),menu:createGameContextMenu()," +
    "createExtensions:createExtensionsTab,unclaimedValue})",
);
assert.equal(extensions.install().ok, true);
assert.equal(memo.type.__steamUiExtensionsTabOriginal.kind, "steam-ui-property-snapshot-v1");
assert.equal(memo.type.__steamUiExtensionsTabOriginal.value, originalRoot);
const probeSource = readSource("src/SteamUiToolkit/Surfaces/SteamExtensionsTabSurface.cs");
const candidatesSource = probeSource.slice(
  probeSource.indexOf("const candidates="),
  probeSource.indexOf("const memo=candidates"),
);
// The probe's `unwrap` is emitted by SteamUiProbeJs.Unwrap; here it is the asset's own unclaim,
// which the C# fragment mirrors.
const unwrap = (value) =>
  unclaimedValue(value, {
    marker: "__steamUiExtensionsTabClaimed",
    original: "__steamUiExtensionsTabOriginal",
  });
const probeCandidates = new Function("exports", "unwrap", `${candidatesSource}; return candidates;`);
assert.deepEqual(
  probeCandidates({ memo }, unwrap),
  ["memo"],
  "the next C# probe accepts the claimed memo",
);
subscriptions.get("steam-ui.extensions-tab")({
  items: [
    {
      id: "one",
      name: "One",
      version: "1",
      status: "Ready",
      actions: [{ id: "run-one", label: "Run" }],
    },
  ],
});
memo.type({});
memo.type({});
const ours = qamTabs.filter((candidate) => candidate?.steamUiExtensionsTab === true);
assert.equal(ours.length, 1, "the tab is pushed into Valve's own array, once across renders");
const tab = ours[0];
// Steam keys its tabs by number and selects by that number, and its tabs carry a string title.
assert.equal(typeof tab.key, "number", "the tab must be keyed the way Steam keys its own");
assert.equal(typeof tab.strTitle, "string", "the tab must carry the string title Valve's tabs do");
assert.equal(tab.title.props.className, "valve-title", "the heading is drawn with Valve's own title element");
assert.deepEqual(tab.title.props.children, ["Extensions"]);
// The panel carries its one style rule and then its list of sections: one Steam PanelSection per
// extension, headed by its folding header row, holding a PanelSectionRow for the actions once the
// host says the section is open.
subscriptions.get("steam-ui.panel-folds")({ open: ["extensions:one"] });
const panel = tab.panel.type();
const section = panel.props.children[1][0];
assert.equal(section.type, PanelSection, "an extension is drawn as Steam's PanelSection");
assert.equal(section.props.title, undefined, "the section is headed by the kit's header, not Steam's title");
assert.equal(section.props.children[0].props.children[0].props.children[0].props.children[0].props.children[0], "One");
const actionRow = section.props.children[1];
assert.equal(actionRow.type, PanelRow, "each line sits in Steam's PanelSectionRow");
assert.equal(actionRow.props.children[0].type, NativePanel, "the actions share one focusable row");
const action = actionRow.props.children[0].props.children[0];
assert.equal(action.type, Button, "an action is Steam's DialogButton");
action.props.onClick();
assert.deepEqual(requests[0].slice(0, 3), [
  "steam-ui.extensions-tab",
  "activate",
  { id: "run-one" },
]);
await tick();
assert.deepEqual(navigation, [], "an action that answers with nothing navigates nowhere");

// An action may answer with a page to open. The panel has to close first, or the page renders
// behind it and a controller user cannot tell the button did anything.
nextAnswer = { route: "/wsgm/library-import" };
action.props.onClick();
await tick();
assert.deepEqual(
  navigation,
  ["closed", "/wsgm/library-import"],
  "the Quick Access panel closes before the route is followed",
);

// The route bound is the shared helper's, so a host that answers with something unusable navigates
// nowhere rather than handing it to Steam's router.
navigation.length = 0;
nextAnswer = { route: "not-a-route" };
action.props.onClick();
await tick();
assert.deepEqual(navigation, ["closed"], "an unusable route is refused by the shared bound");
nextAnswer = undefined;
navigation.length = 0;
// An item the host could never act on is refused at the publication boundary rather than rendered:
// the configure command rejects a negative revision, so a row offering one is a dead control.
subscriptions.get("steam-ui.extensions-tab")({
  items: [
    { id: "one", name: "One", version: "1", status: "Ready", configurationRevision: -1 },
    { id: "two", name: "Two", version: "1", status: "Ready", configurationRevision: 2 },
  ],
});
assert.equal(extensions.status().items, 1, "a negative configuration revision is refused");

// No count caps anything: a theme set with far more settings than any fixed bound, as forty CSS
// themes and their colour pickers produce, keeps its section.
subscriptions.get("steam-ui.extensions-tab")({
  items: [
    {
      id: "themes",
      name: "Themes",
      version: "",
      status: "Ready",
      configurationRevision: 1,
      settings: Array.from({ length: 200 }, (_, index) => ({
        key: `setting-${index}`,
        label: `Setting ${index}`,
        kind: "boolean",
        booleanValue: false,
      })),
      actions: Array.from({ length: 100 }, (_, index) => ({ id: `action-${index}`, label: `Action ${index}` })),
    },
  ],
});
assert.equal(extensions.status().items, 1, "an item with 200 settings is drawn");

// A typed draft survives re-renders of the publication it was typed against, and no longer.
const renderPanel = () => {
  hookIndex = 0;
  return memo.type({}).props.tabs.find((tab) => tab.steamUiExtensionsTab).panel.type();
};
const textItem = (revision, textValue) => ({
  items: [
    {
      id: "one",
      name: "One",
      version: "1",
      status: "Ready",
      configurationRevision: revision,
      settings: [{ key: "token", label: "Token", kind: "text", textValue }],
    },
  ],
});
// A text setting is Steam's TextField, in the row after the detail and the (empty) actions row.
const inputOf = (panel) => panel.props.children[1][0].props.children[2].props.children[0];
subscriptions.get("steam-ui.extensions-tab")(textItem(3, "alpha"));
assert.equal(inputOf(renderPanel()).type, TextField, "a text setting is Steam's TextField");
assert.equal(inputOf(renderPanel()).props.value, "alpha");
inputOf(renderPanel()).props.onChange({ target: { value: "beta" } });
assert.equal(inputOf(renderPanel()).props.value, "beta", "the draft survives a re-render");
subscriptions.get("steam-ui.extensions-tab")(textItem(4, "gamma"));
assert.equal(
  inputOf(renderPanel()).props.value,
  "gamma",
  "a newer configuration revision replaces the draft",
);

// Leaving the box sends it once; a refused save drops the draft, so the box stops showing and
// resending a rejected value.
subscriptions.get("steam-ui.extensions-tab")(textItem(5, "delta"));
inputOf(renderPanel()).props.onChange({ target: { value: "epsilon" } });
assert.equal(inputOf(renderPanel()).props.value, "epsilon");
refuseRequests = true;
const sentBefore = requests.length;
inputOf(renderPanel()).props.onBlur();
assert.deepEqual(requests.at(-1).slice(0, 3), [
  "steam-ui.extensions-tab",
  "configure",
  { id: "one", key: "token", value: "epsilon", revision: 5 },
]);
assert.equal(requests.length, sentBefore + 1);
await Promise.resolve();
await Promise.resolve();
refuseRequests = false;
assert.equal(inputOf(renderPanel()).props.value, "delta", "a refused save drops the draft");

// A choice is Steam's dropdown, and an order is Steam's move buttons sending the joined list.
subscriptions.get("steam-ui.extensions-tab")({
  items: [
    {
      id: "one",
      name: "One",
      version: "1",
      status: "Ready",
      configurationRevision: 6,
      settings: [
        { key: "mode", label: "Mode", kind: "text", choices: ["a", "b"], textValue: "a" },
        { key: "tabs", label: "Tabs", kind: "order", choices: ["x", "y"], textValue: "y,x" },
      ],
    },
  ],
});
const rendered = renderPanel().props.children[1][0].props.children;
assert.equal(rendered[2].props.children[0].type, Dropdown, "a choice is Steam's dropdown");
assert.equal(rendered[2].props.children[0].props.layout, "below", "the dropdown goes under its label in the panel");
const order = rendered[3].props.children[0];
const firstMove = order.props.children.flat()[1].props.value.props.children[1];
assert.equal(firstMove.type, SmallButton, "an order moves with Steam's small buttons");
firstMove.props.onClick();
assert.deepEqual(requests.at(-1)[2], { id: "one", key: "tabs", value: "x,y", revision: 6 });

// Every section folds under the shared fold surface, and starts folded: its header carries its
// name, its detail and a caret, and its rows are drawn only while it is open. A switch's settings
// fold under a small heading of their own. Folding asks the host and shows at once.
const themes = (revision) => ({
  items: [
    {
      id: "wsgm.themes",
      name: "Themes",
      version: "",
      status: "Ready",
      detail: "3 enabled",
      configurationRevision: revision,
      actions: [{ id: "browse", label: "Browse themes…" }],
      settings: [
        { key: "theme:dark", label: "Dark Deck", kind: "boolean", booleanValue: true, description: "v2.1 · Squishy" },
        { key: "patch:dark:accent", label: "Accent", kind: "text", choices: ["red", "blue"], choiceLabels: ["Red", "Blue"], textValue: "red", parent: "theme:dark" },
        { key: "patch:dark:blur", label: "Blur", kind: "number", choices: ["Off", "Low", "High"], numberValue: 1, parent: "theme:dark" },
        { key: "theme:light", label: "Light Deck", kind: "boolean", booleanValue: false },
        { key: "patch:light:x", label: "Hidden while off", kind: "text", textValue: "", parent: "theme:light" },
        { key: "patch:orphan", label: "No such parent", kind: "text", textValue: "", parent: "theme:none" },
        { key: "colour", label: "Highlight", kind: "color", textValue: "#ff0000" },
      ],
    },
  ],
  revision,
});
subscriptions.get("steam-ui.extensions-tab")(themes(9));
let themesPanel = renderPanel();
let themesSection = themesPanel.props.children[1][0];
assert.equal(themesSection.props.title, undefined, "a section is not titled by Steam's section");
const headerRow = themesSection.props.children[0];
assert.equal(headerRow.type, PanelRow);
const headerButton = headerRow.props.children[0];
assert.equal(headerButton.type, NativePanel, "the header is Steam's Focusable, so a controller can land on it");
assert.equal(headerButton.props.onOKActionDescription, "Expand");
assert.equal(headerButton.props.children[0].props.children[0].props.children[0], "Themes");
assert.equal(headerButton.props.children[0].props.children[1].props.children[0], "Ready · 3 enabled");
assert.equal(themesSection.props.children.length, 1, "a section starts folded and draws its header and nothing else");
headerButton.props.onActivate();
assert.deepEqual(requests.at(-1).slice(0, 3), [
  "steam-ui.panel-folds",
  "setFolded",
  { id: "extensions:wsgm.themes", folded: false },
]);
themesPanel = renderPanel();
themesSection = themesPanel.props.children[1][0];
let bodyRows = themesSection.props.children.slice(1).flat().filter(Boolean);
assert.equal(bodyRows[0].props.children[0].props.children[0].type, Button, "unfolded at once: the action is drawn");
const themeToggle = bodyRows[1].props.children[0];
assert.equal(themeToggle.type, Toggle);
assert.equal(themeToggle.props.description, "v2.1 · Squishy", "a setting's description reaches Steam's field");
// A switch's settings start folded under a small heading of their own, indented like them.
const settingsHeading = bodyRows[2].props.children[0];
assert.equal(settingsHeading.props.className, "steam-ui-kit-nested");
const settingsFold = settingsHeading.props.children[0];
assert.equal(settingsFold.type, NativePanel, "the settings heading is Steam's Focusable");
assert.equal(settingsFold.props.className, "steam-ui-kit-header sub");
assert.equal(settingsFold.props.onOKActionDescription, "Expand");
assert.equal(settingsFold.props.children[0].props.children[0].props.children[0], "2 settings");
const lightToggle = bodyRows[3].props.children[0];
assert.equal(lightToggle.type, Toggle);
assert.equal(bodyRows.length, 5, "folded settings, a child of a switch that is off, and one with no parent switch, are not drawn");
assert.equal(bodyRows[4].props.children[0].type, TextField, "without a modal a colour is its text");
assert.equal(bodyRows[4].props.children[0].props.value, "#ff0000");
settingsFold.props.onActivate();
assert.deepEqual(requests.at(-1).slice(0, 3), [
  "steam-ui.panel-folds",
  "setFolded",
  { id: "extensions:wsgm.themes:theme:dark", folded: false },
]);
themesPanel = renderPanel();
themesSection = themesPanel.props.children[1][0];
bodyRows = themesSection.props.children.slice(1).flat().filter(Boolean);
assert.equal(bodyRows.length, 7, "opened at once: the switch's settings follow their heading");
assert.equal(bodyRows[2].props.children[0].props.children[0].props.className, "steam-ui-kit-header open sub");
const nested = bodyRows[3].props.children[0];
assert.equal(nested.props.className, "steam-ui-kit-nested", "a child setting is drawn indented with the kit");
assert.equal(nested.props.children[0].type, Dropdown);
assert.deepEqual(
  nested.props.children[0].props.rgOptions.map((option) => [option.data, option.label]),
  [["red", "Red"], ["blue", "Blue"]],
  "a choice is sent back by its value and shown by its label",
);
const blur = bodyRows[4].props.children[0].props.children[0];
assert.equal(blur.type, Slider, "a number with choices is a slider");
assert.equal(blur.props.max, 2);
assert.equal(blur.props.showValue, false);
assert.deepEqual(
  blur.props.notchLabels.map((notch) => notch.label),
  ["Off", "Low", "High"],
  "the choices name the notches",
);
blur.props.onChangeComplete(2);
assert.deepEqual(requests.at(-1)[2], { id: "wsgm.themes", key: "patch:dark:blur", value: 2, revision: 9 });
bodyRows[5].props.children[0].props.onChange(true);
themesPanel = renderPanel();
themesSection = themesPanel.props.children[1][0];
assert.equal(
  themesSection.props.children.slice(1).flat().filter(Boolean).length,
  8,
  "switching a parent on shows its settings heading before the host answers",
);
// The host's word on a fold wins once it agrees, and a publication that no longer lists the
// section folds it again: the local fold is only ever a preview of the host's.
subscriptions.get("steam-ui.panel-folds")({ open: ["extensions:wsgm.themes", "extensions:wsgm.themes:theme:dark"] });
assert.equal(renderPanel().props.children[1][0].props.children.slice(1).flat().filter(Boolean).length, 8);
subscriptions.get("steam-ui.panel-folds")({ open: [] });
themesPanel = renderPanel();
assert.equal(themesPanel.props.children[1][0].props.children.length, 1, "the host's publication folds it again");
subscriptions.get("steam-ui.extensions-tab")({ items: [], revision: 11 });

const replacement = createExtensions();
assert.equal(replacement.install().ok, true, "a fresh gate resolves its durable owned original");

// A release that throws leaves the gate owning its claim. Forgetting first would answer `absent`
// on every later remove() while Steam kept running the wrapper, with no way to retry the cleanup.
releaseBlocked = true;
assert.equal(replacement.remove().ok, false, "a failed release is reported, not swallowed");
assert.equal(replacement.status().installed, true, "the gate still owns its claim");
assert.equal(replacement.status().claimed, true);
releaseBlocked = false;
assert.equal(replacement.remove().ok, true, "the retry completes the cleanup");
assert.equal(memo.type, originalRoot);

nativeAvailable = false;
const refused = createExtensions();
assert.equal(refused.install().ok, false, "missing native components refuse installation");
assert.match(refused.status().lastError, /panel section and row/u, "the refusal names what is missing");
assert.equal(memo.type, originalRoot);
nativeAvailable = true;

// No document or MutationObserver exists in this fixture, just as no visible DOM is available in
// SharedJSContext. Class discovery must happen before React creates the first instance.
class GameMenu {
  GetTargetApps() {
    return [{ appid: 42 }];
  }
  BuildManageSubmenu() {}
  GetPrimaryActionMenuItem() {}
  render() {
    return element("menu", {
      children: [
        element("item", {
          onSelected() {
            return "AppProperties";
          },
        }),
      ],
    });
  }
}
const originalRender = GameMenu.prototype.render;
assert.equal(menu.install().ok, true);
subscriptions.get("steam-ui.game-context-menu")({ items: [{ id: "art", label: "Artwork" }] });
jsx.jsx(GameMenu, {});
assert.equal(jsx.jsx, originalJsx, "the capture transform is withdrawn once the menu class is held");
assert.equal(menu.status().observing, true, "a held menu class still counts as observing");
const first = new GameMenu().render();
const inserted = first.props.children.flat()[0];
assert.equal(inserted.props.children[0], "Artwork", "first opening already includes commands");
inserted.props.onSelected();
assert.deepEqual(requests.at(-1).slice(0, 3), [
  "steam-ui.game-context-menu",
  "activate",
  { appId: 42, id: "art" },
]);
assert.equal(menu.status().menuClaimed, true);
assert.equal(menu.remove().ok, true);
assert.equal(GameMenu.prototype.render, originalRender);
assert.equal(jsx.jsx, originalJsx);
console.log("Extension surfaces emitted checks passed.");
