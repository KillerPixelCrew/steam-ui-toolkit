// Exercise the emitted settings renderer against an inert Steam runtime.
//
// What matters is that every row is one of Steam's own components, fed what Steam's component
// expects, and that a change reaches the host exactly once and only when it should: a confirmation
// that was cancelled sends nothing, a slider sends when it settles, and a secret is never shown.
// This runs the emitted JavaScript, not the TypeScript source.
import assert from "node:assert/strict";
import {
  createReact,
  instantiate,
  loadAsset,
  sharedFragments,
  slice,
  withSource,
} from "./check-harness.mjs";

const asset = loadAsset();

// A hook runtime just large enough for one function component: state by call order, and effects
// that run when their dependencies change.
const hooks = { states: [], effects: [], index: 0 };
const react = createReact({
  useState(initial) {
    const slot = hooks.index++;
    if (!(slot in hooks.states)) hooks.states[slot] = initial;
    const set = (next) => {
      hooks.states[slot] = typeof next === "function" ? next(hooks.states[slot]) : next;
    };
    return [hooks.states[slot], set];
  },
  useEffect(effect, dependencies) {
    const slot = hooks.index++;
    const previous = hooks.effects[slot];
    if (!previous || dependencies.some((value, index) => value !== previous[index])) {
      hooks.effects[slot] = dependencies;
      effect();
    }
  },
});

// Steam's components, each with the source text the resolver fingerprints it by.
const named = (name, source) => withSource(Object.defineProperty(() => null, "name", { value: name }), source);
const Slider = named("Slider", "onChangeComplete notchCount valueSuffix explainerTitle");
const Dropdown = named("Dropdown", "contextMenuPositionOptions childrenContainerWidth menuLabel");
const Toggle = named("Toggle", "OnToggleChange this.Toggle()");
const Button = named("Button", '"DialogButton","_DialogLayout","Secondary"');
const Primary = named("Primary", '"DialogButton","_DialogLayout","Primary"');
const TextField = named("TextField", "class TextField");
TextField.validateUrl = () => true;
TextField.validateEmail = () => true;
const Section = named("Section", 'className:"DialogSettingsSection"');
const ValueField = named("ValueField", 'label:t,focusable:!0,inlineWrap:"shift-children-below"');
const SmallButton = named("SmallButton", '"DialogButton _DialogLayout Small"');
const Focusable = named("Focusable", "focusableIfEmpty onActivate onCancel focusClassName");
const RoutedPages = named("RoutedPages", "function(e){const{pages:t,disableRouteReporting:c}=e}");
const Confirm = named("Confirm", "strMiddleButtonText bProgressDialog bAlertDialog");
const shown = [];
const ShowModal = named("ShowModal", "props.bDisableBackgroundDismiss");

const modules = {
  react: { tokens: ["react.transitional.element", "useState", "cloneElement", "createElement"], exports: react },
  fields: {
    tokens: ["DialogSlider_Container", "DropDownField", "SliderField"],
    exports: { Slider, Dropdown, Toggle, Button, Primary, TextField, Section, ValueField, SmallButton },
  },
  focusable: { tokens: ["focusableIfEmpty", "onActivate", '"Panel"'], exports: { Focusable } },
  modal: { tokens: ["props.bDisableBackgroundDismiss"], exports: { ShowModal } },
  pages: { tokens: ["disableRouteReporting"], exports: { RoutedPages } },
  confirm: { tokens: ["strMiddleButtonText", "bProgressDialog", "bAlertDialog"], exports: { Confirm } },
};
const moduleFor = (tokens) =>
  Object.keys(modules).find((id) => tokens.every((token) => modules[id].tokens.includes(token)));
const runtime = (id) => modules[id].exports;
runtime.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
runtime.exported = (tokens, predicate) => {
  const id = moduleFor(tokens);
  if (!id) throw new Error("absent");
  const fits = Object.values(modules[id].exports).filter((value) => predicate(value));
  if (fits.length !== 1) throw new Error("ambiguous");
  return fits[0];
};

const api = instantiate(
  { window: {} },
  `${sharedFragments(asset)}\n${slice(asset, "const SteamGlyphPattern", "function createAudioNamespace")}`,
  "{ resolveSteamSettingsComponents, SteamSettingsRequired, renderSteamSettings }",
);

// Resolution: every component the page draws is Steam's, and all of them resolve.
const resolved = api.resolveSteamSettingsComponents(runtime);
assert.ok(resolved, "the settings components must resolve");
for (const name of api.SteamSettingsRequired) {
  assert.ok(resolved[name], `${name} must resolve`);
}
assert.equal(resolved.routedPages, RoutedPages, "the sidebar must be Steam's routed pages");
assert.equal(resolved.confirmModal, Confirm, "the confirmation must be Steam's confirm modal");
assert.equal(resolved.settingsSection, Section);
assert.equal(resolved.valueField, ValueField);
assert.equal(resolved.smallButton, SmallButton);

// The host's page, one row of every kind.
const ui = {
  ...resolved,
  showModal: (modal) => shown.push(modal),
};
const pages = [
  {
    id: "steam",
    title: "Steam",
    glyph: "M1 1h2v2H1Z",
    sections: [
      {
        title: "Integration",
        rows: [
          {
            key: "cef",
            kind: "boolean",
            label: "CEF",
            checked: true,
            confirm: { when: false, title: "Turn off?", description: "It goes away.", confirmLabel: "Turn off" },
          },
          { key: "mode", kind: "choice", label: "Mode", text: "a", choices: [{ value: "a", label: "A" }, { value: "b", label: "B" }] },
          { key: "level", kind: "range", label: "Level", number: 3, minimum: 0, maximum: 10, step: 1 },
          { key: "name", kind: "text", label: "Name", text: "old" },
          { key: "key", kind: "secret", label: "Key", text: "Set" },
          { key: "order", kind: "order", label: "Order", order: ["a", "b"], choices: [{ value: "a", label: "First" }] },
          { key: "run", kind: "action", label: "Run", buttonLabel: "Go" },
          { key: "state", kind: "note", label: "State", text: "Active" },
          { key: "future", kind: "hologram", label: "Future" },
        ],
      },
    ],
  },
];
const changes = [];
const actions = [];
let revision = 1;
const render = () => {
  hooks.index = 0;
  const view = api.renderSteamSettings(ui, {
    route: "/host/settings",
    pages,
    revision,
    onChange: (row, value) => changes.push([row.key, value]),
    onAction: (row) => actions.push(row.key),
  });
  return view.type(view.props);
};
const rowsOf = (tree) => tree.props.pages[0].content.props.children[0].props.children;
const row = (tree, key) =>
  rowsOf(tree).find((node) => node.key === `steam-setting-${key}`) ??
  assert.fail(`no row ${key}`);

let tree = render();
assert.equal(tree.type, RoutedPages, "the page must be Steam's routed sidebar");
assert.equal(tree.props.pages[0].route, "/host/settings/steam", "each page must sit below the route");
assert.equal(tree.props.pages[0].title, "Steam");
assert.equal(tree.props.pages[0].icon.props.children[0].props.d, "M1 1h2v2H1Z", "the glyph is the page's icon");
assert.equal(tree.props.pages[0].content.props.children[0].type, Section, "sections must be Steam's");
assert.equal(tree.props.pages[0].content.props.children[0].props.label, "Integration");

// Kinds map to Steam's own fields.
assert.equal(row(tree, "cef").type, Toggle);
assert.equal(row(tree, "mode").type, Dropdown);
assert.deepEqual(row(tree, "mode").props.rgOptions, [
  { data: "a", label: "A" },
  { data: "b", label: "B" },
]);
assert.equal(row(tree, "level").type, Slider);
assert.equal(row(tree, "name").type, TextField);
assert.equal(row(tree, "run").type, ValueField);
assert.equal(row(tree, "run").props.value.type, Button, "an action is Steam's dialog button");
assert.equal(row(tree, "state").props.value, "Active", "a note shows its text");
assert.equal(row(tree, "future").props.value, "", "an unknown kind is its label and nothing that sends");

// A confirmation is asked in Steam's modal, and cancelling sends nothing.
row(tree, "cef").props.onChange(false);
assert.deepEqual(changes, [], "a change needing confirmation must not be sent before it is confirmed");
assert.equal(shown.length, 1);
assert.equal(shown[0].type, Confirm);
assert.equal(shown[0].props.bDestructiveWarning, true);
shown[0].props.onCancel();
assert.deepEqual(changes, [], "a cancelled confirmation must send nothing");
row(tree, "cef").props.onChange(false);
shown[1].props.onOK();
assert.deepEqual(changes, [["cef", false]], "a confirmed change is sent once");

// The other direction needs no confirmation.
changes.length = 0;
row(tree, "cef").props.onChange(true);
assert.deepEqual(changes, [["cef", true]]);

// A choice sends its value; a slider sends when it settles, not on every step.
changes.length = 0;
row(tree, "mode").props.onChange({ data: "b", label: "B" });
row(tree, "level").props.onChange(4);
row(tree, "level").props.onChange(5);
assert.deepEqual(changes, [["mode", "b"]], "moving the slider must not send");
row(tree, "level").props.onChangeComplete(6);
assert.deepEqual(changes, [["mode", "b"], ["level", 6]]);

// Text is sent when the field loses focus, and only if it changed.
changes.length = 0;
tree = render();
row(tree, "name").props.onBlur();
assert.deepEqual(changes, [], "an unchanged field must not send");
row(tree, "name").props.onChange({ target: { value: "new" } });
tree = render();
assert.equal(row(tree, "name").props.value, "new", "a typed value is kept while it is being edited");
row(tree, "name").props.onBlur();
assert.deepEqual(changes, [["name", "new"]]);

// A secret starts empty and shows only whether one is set.
changes.length = 0;
assert.equal(row(tree, "key").props.value, "", "a secret must never be shown");
assert.equal(row(tree, "key").props.placeholder, "Set");
assert.equal(row(tree, "key").props.type, "password");
row(tree, "key").props.onBlur();
assert.deepEqual(changes, [], "an untouched secret must not be sent");
row(tree, "key").props.onChange({ target: { value: "x" } });
row(tree, "key").props.onChange({ target: { value: "" } });
tree = render();
row(tree, "key").props.onBlur();
assert.deepEqual(changes, [["key", ""]], "a secret typed into and emptied is cleared");
changes.length = 0;

// An order moves one value and sends the whole list; the ends cannot move further.
changes.length = 0;
const order = row(tree, "order");
const [heading, first, second] = order.props.children;
assert.equal(heading.props.name, "Order");
assert.equal(first.props.name, "First", "a value is shown by its label");
assert.equal(second.props.name, "b", "a value without a label is shown as itself");
const [up, down] = first.props.value.props.children;
assert.equal(up.type, SmallButton, "moving is done with Steam's small buttons");
assert.equal(up.props.disabled, true, "the first value cannot move up");
down.props.onClick();
assert.deepEqual(changes, [["order", ["b", "a"]]]);

// An action asks the host.
row(tree, "run").props.value.props.onClick();
assert.deepEqual(actions, ["run"]);

// A new publication replaces every draft with the host's own values.
revision = 2;
render(); // the effect clears the drafts, as React runs it after the commit
tree = render();
assert.equal(row(tree, "name").props.value, "old", "a new revision must drop the drafts typed before it");

console.log("Settings: native components, kinds, confirmation, drafts and secrets passed.");
