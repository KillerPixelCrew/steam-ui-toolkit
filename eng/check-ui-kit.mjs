// Exercise the emitted UI kit against an inert Steam runtime.
//
// The kit is what a host draws around Steam's fields, so what matters is that every element is
// built from Steam's Focusable and buttons where it takes focus, carries the kit's classes the
// stylesheet keys on, and answers the host's callbacks: a header toggles, an action runs, a card
// activates, a confirm sends only on OK, a prompt sends only a non-empty answer. This runs the
// emitted JavaScript.
import assert from "node:assert/strict";
import { createReact, instantiate, loadAsset, sharedFragments, slice, withSource } from "./check-harness.mjs";

const asset = loadAsset();
const hooks = { states: [], index: 0 };
const react = createReact({
  useState(initial) {
    const slot = hooks.index++;
    if (!(slot in hooks.states)) hooks.states[slot] = initial;
    return [hooks.states[slot], (next) => (hooks.states[slot] = typeof next === "function" ? next(hooks.states[slot]) : next)];
  },
});
const named = (name) => withSource(Object.defineProperty(() => null, "name", { value: name }), name);
const Focusable = named("Focusable");
const Button = named("Button");
const Primary = named("Primary");
const Small = named("Small");
const TextField = named("TextField");
const Modal = named("Modal");
const shown = [];
const ui = {
  react,
  focusable: Focusable,
  dialogButton: Button,
  dialogButtonPrimary: Primary,
  smallButton: Small,
  textField: TextField,
  modalRoot: Modal,
  showModal: (modal) => shown.push(modal),
};

const kit = instantiate(
  { window: {} },
  sharedFragments(asset) + slice(asset, "const SteamUiIconShapes =", "function createAudioNamespace"),
  "({steamUiKitStyle, renderSteamUiHeader, renderSteamUiGroup, renderSteamUiActions," +
    " renderSteamUiSwatch, renderSteamUiCard, renderSteamUiGrid, renderSteamUiEmpty, renderSteamUiBanner," +
    " renderSteamUiToolbar, renderSteamUiTool, renderSteamUiChips, renderSteamUiBox, renderSteamUiGallery, renderSteamUiVideo," +
    " renderSteamUiTabbedPage, renderSteamUiDetail, SteamUiTabbedPageRequired," +
    " showSteamUiConfirm, showSteamUiPrompt, SteamUiKitStyles})",
);

// The stylesheet is one element a root renders, and every class an element uses has a rule.
const style = kit.steamUiKitStyle(react);
assert.equal(style.type, "style");
for (const cls of ["page", "pane", "header", "header-icon", "group", "group-body", "blocks", "valve", "battery", "actions", "nested", "swatch", "card", "grid", "banner", "toolbar", "chips", "box", "gallery", "detail", "detail-main", "detail-aside", "page-banner", "modal-body"]) {
  assert.ok(kit.SteamUiKitStyles.includes(`.steam-ui-kit-${cls}`), `a rule for steam-ui-kit-${cls}`);
}

// A header is Steam's Focusable, says which way it folds, and toggles on activation.
let toggled = 0;
const header = kit.renderSteamUiHeader(ui, { title: "Themes", detail: "2 of 2 enabled", collapsed: true, onToggle: () => toggled++ });
assert.equal(header.type, Focusable);
assert.equal(header.props.className, "steam-ui-kit-header");
assert.equal(header.props.onOKActionDescription, "Expand");
header.props.onActivate();
assert.equal(toggled, 1);
assert.equal(kit.renderSteamUiHeader(ui, { title: "T", collapsed: false, onToggle: () => {} }).props.className, "steam-ui-kit-header open");
// The caret is the kit's own, and a heading that does not fold is a plain div with no caret at all.
assert.equal(header.props.children.at(-1).props.className, "steam-ui-kit-header-caret");
assert.equal(header.props.children.at(-1).props.children[0].type, "svg");
assert.equal(kit.renderSteamUiHeader(ui, { title: "S", sub: true, onToggle: () => {} }).props.className, "steam-ui-kit-header open sub");
const fixed = kit.renderSteamUiHeader(ui, { title: "Profile scope", icon: "glyph" });
assert.equal(fixed.type, "div");
assert.equal(fixed.props.className, "steam-ui-kit-header open plain");
assert.equal(fixed.props.children[0].props.className, "steam-ui-kit-header-icon");
assert.equal(fixed.props.children.length, 2, "no caret on a heading that does not fold");

// A group is the heading over a body that stays mounted while folded; without a title it is a
// plain box, and hidden takes it out of layout.
let groupToggled = 0;
const group = kit.renderSteamUiGroup(ui, { key: "k", title: "Power profiles", detail: "Balanced", collapsed: true, onToggle: () => groupToggled++ }, "row-a", "row-b");
assert.equal(group.type, "div");
assert.equal(group.key, "k");
assert.equal(group.props.className, "steam-ui-kit-group closed");
assert.equal(group.props.children[0].type, Focusable);
assert.equal(group.props.children[0].props.className, "steam-ui-kit-header");
group.props.children[0].props.onActivate();
assert.equal(groupToggled, 1);
assert.equal(group.props.children[1].props.className, "steam-ui-kit-group-body");
assert.deepEqual(group.props.children[1].props.children, ["row-a", "row-b"], "a folded body keeps its rows");
assert.equal(kit.renderSteamUiGroup(ui, { title: "Fixed" }).props.className, "steam-ui-kit-group");
assert.equal(kit.renderSteamUiGroup(ui, { title: "Fixed" }).props.children[0].type, "div");
assert.equal(kit.renderSteamUiGroup(ui, { title: "Gone", hidden: true, onToggle: () => {} }).props.className, "steam-ui-kit-group hidden");
const plain = kit.renderSteamUiGroup(ui, {}, "button");
assert.equal(plain.props.className, "steam-ui-kit-group plain");
assert.equal(plain.props.children[0], null, "no heading without a title");

// Actions are Steam's buttons in the kit's grid; a long label spans the row.
const ran = [];
const actions = kit.renderSteamUiActions(ui, [
  { id: "a", label: "Browse…", onClick: () => ran.push("a") },
  { id: "b", label: "A label longer than eighteen", onClick: () => ran.push("b") },
]);
assert.equal(actions.type, Focusable);
assert.equal(actions.props.className, "steam-ui-kit-actions");
assert.equal(actions.props.children[0].type, Button);
assert.equal(actions.props.children[0].props.className, undefined);
assert.equal(actions.props.children[1].props.className, "steam-ui-kit-wide");
actions.props.children[0].props.onClick();
assert.deepEqual(ran, ["a"]);

// A swatch keeps its colour.
assert.equal(kit.renderSteamUiSwatch(react, "#ff0000").props.style.background, "#ff0000");
// The stylesheet element is one per React, so a re-rendering root hands React the same one.
assert.equal(kit.steamUiKitStyle(react), style);

// A card is one Focusable with its image, stats, badge, title and meta, and activates as one.
let opened = 0;
const card = kit.renderSteamUiCard(ui, {
  image: "https://x/1.jpg",
  stats: [{ text: "12" }, { text: "3" }],
  badge: { text: "Update", warn: true },
  title: "Dark Deck",
  meta: ["v2.1", "By Squishy"],
  onActivate: () => opened++,
});
assert.equal(card.type, Focusable);
assert.equal(card.props.className, "steam-ui-kit-card");
const shot = card.props.children[0];
assert.equal(shot.props.children[0].props.src, "https://x/1.jpg");
assert.equal(shot.props.children[1].props.children.length, 2);
assert.equal(shot.props.children[2].props.className, "steam-ui-kit-badge warn");
assert.deepEqual(card.props.children[1].props.children, ["Dark Deck"]);
assert.equal(card.props.children.length, 4);
card.props.onActivate();
assert.equal(opened, 1);
assert.equal(kit.renderSteamUiGrid(ui, [card]).props.className, "steam-ui-kit-grid");
assert.equal(kit.renderSteamUiEmpty(react, "Nothing", true).props.className, "steam-ui-kit-empty error");

// A video preview plays the movie muted on a loop, shows the still without one, and says so without either.
const video = kit.renderSteamUiVideo(react, { src: "https://x/1.webm", poster: "https://x/1.jpg" });
assert.equal(video.props.className, "steam-ui-kit-hero video");
assert.equal(video.props.children[0].type, "video");
assert.equal(video.props.children[0].props.muted, true);
assert.equal(video.props.children[0].props.loop, true);
assert.equal(kit.renderSteamUiVideo(react, { poster: "https://x/1.jpg" }).props.children[0].type, "img");
assert.equal(kit.renderSteamUiVideo(react, { empty: "Nothing" }).props.children[0].props.className, "steam-ui-kit-hero-empty");

// A tabbed page draws only the active tab, falls back to the first for an unknown one, and shows a
// banner only with text; a detail is Steam's Focusable, left with B, with Back last in its aside.
const Tabs = "Tabs";
const pageUi = { ...ui, tabs: Tabs };
const shownTabs = [];
const page = kit.renderSteamUiTabbedPage(pageUi, {
  id: "p", label: "P", tabs: [{ id: "a", title: "A" }, { id: "b", title: "B" }], active: "zzz",
  onTab: () => {}, content: (tab) => { shownTabs.push(tab); return tab; }, banner: { text: "", onDismiss: () => {} },
});
assert.equal(page.props.className, "steam-ui-kit-page");
const tabsElement = page.props.children.find((child) => child?.type === Tabs);
assert.equal(tabsElement.props.activeTab, "a");
assert.deepEqual(tabsElement.props.tabs.map((tab) => tab.content), ["a", null]);
assert.deepEqual(shownTabs, ["a"]);
assert.ok(!page.props.children.some((child) => child?.props?.className === "steam-ui-kit-page-banner"));
let backed = 0;
const detail = kit.renderSteamUiDetail(ui, { title: "T", badge: "v1", main: ["m"], aside: ["box"], onBack: () => backed++ });
assert.equal(detail.type, Focusable);
detail.props.onCancelButton();
assert.equal(backed, 1);
assert.equal(detail.props.children[1].props.children.at(-1).type, Button);
assert.ok(kit.SteamUiTabbedPageRequired.includes("tabs"));

// A banner dismisses with the small button; a toolbar and chips are focusable rows of Steam's buttons.
let dismissed = 0;
const banner = kit.renderSteamUiBanner(ui, { text: "Installed.", onDismiss: () => dismissed++ });
assert.equal(banner.props.className, "steam-ui-kit-banner");
assert.equal(banner.props.children[1].type, Small);
banner.props.children[1].props.onClick();
assert.equal(dismissed, 1);
assert.equal(kit.renderSteamUiBanner(ui, { text: "x", error: true, onDismiss: () => {} }).props.className, "steam-ui-kit-banner error");
const toolbar = kit.renderSteamUiToolbar(ui, kit.renderSteamUiTool(ui, "Sort", "dropdown"), kit.renderSteamUiTool(ui, null, "search", true));
assert.equal(toolbar.type, Focusable);
assert.equal(toolbar.props.children[0].props.children[0].props.children[0], "Sort");
assert.equal(toolbar.props.children[1].props.className, "steam-ui-kit-tool grow");
const chips = kit.renderSteamUiChips(ui, [{ label: "Library", onClick: () => ran.push("chip") }]);
assert.equal(chips.props.children[0].type, Button);
chips.props.children[0].props.onClick();
assert.deepEqual(ran, ["a", "chip"]);
const box = kit.renderSteamUiBox(react, "Install", "body");
assert.equal(box.props.children[0].props.className, "steam-ui-kit-box-title");

// A gallery picks a thumbnail and counts; one image needs no thumbnails.
let picked = -1;
const gallery = kit.renderSteamUiGallery(ui, { images: ["a", "b", "c"], index: 1, onSelect: (at) => (picked = at) });
assert.equal(gallery.props.children[0].props.className, "steam-ui-kit-thumbs");
assert.equal(gallery.props.children[0].props.children[1].props.className, "steam-ui-kit-thumb current");
gallery.props.children[0].props.children[2].props.onActivate();
assert.equal(picked, 2);
assert.deepEqual(gallery.props.children[1].props.children[1].props.children, ["2/3"]);
assert.equal(kit.renderSteamUiGallery(ui, { images: ["a"], index: 0, onSelect: () => {} }).props.children[0], null);

// A confirm sends only on OK; a prompt sends only a non-empty answer.
let confirmed = 0;
kit.showSteamUiConfirm(ui, { title: "Delete Theme", text: "Sure?", confirmLabel: "Delete", onConfirm: () => confirmed++ });
assert.equal(shown.length, 1);
let closed = 0;
const renderModal = (modal) => {
  hooks.index = 0;
  const frame = modal.type({ closeModal: () => closed++ });
  assert.equal(frame.type, Modal, "the body sits in Steam's dialog");
  return frame.props.children[0];
};
let body = renderModal(shown[0]);
const [cancel, ok] = body.props.children[1].props.children;
cancel.props.onClick();
assert.equal(confirmed, 0);
assert.equal(closed, 1);
ok.props.onClick();
assert.equal(confirmed, 1);
assert.equal(closed, 2);
const answers = [];
kit.showSteamUiPrompt(ui, { title: "Create Profile", label: "Profile Name", confirmLabel: "Create", onConfirm: (value) => answers.push(value) });
const prompt = renderModal(shown[1]);
let promptBody = prompt.type(prompt.props);
promptBody.props.children[2].props.children[1].props.onClick();
assert.deepEqual(answers, [], "an empty answer is not sent");
promptBody.props.children[1].props.onChange({ target: { value: "  Night " } });
hooks.index = 0;
promptBody = prompt.type(prompt.props);
promptBody.props.children[2].props.children[1].props.onClick();
assert.deepEqual(answers, ["Night"], "the answer is sent trimmed");

console.log("UI kit: stylesheet, header, actions, card, banner, toolbar, chips, gallery, confirm and prompt passed.");
