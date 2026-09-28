// Exercise the emitted theme-styles gate against inert popup documents.
//
// CSSLoader chooses the documents a block goes into by title, URL or root classes, appends one
// <style> per block and re-injects when a window is reloaded. This proves the gate does the same
// over Steam's popup manager: the right blocks in the right documents in the published order, an
// unchanged publication touching nothing, a changed block rebuilt, a window opened later caught
// on the next pass, and removal leaving no node behind. This runs the emitted JavaScript.
import assert from "node:assert/strict";
import { gateSource, instantiate, loadAsset, sharedFragments } from "./check-harness.mjs";

const asset = loadAsset();
const subscriptions = new Map();
let pass = null;

// A document small enough to read beside the gate: a head that keeps its children in order and
// answers the one query the gate makes.
const fakePopup = (title, url, classes = []) => {
  const head = {
    classList: [],
    children: [],
    append(node) {
      node.parentNode = this;
      this.children.push(node);
    },
    querySelectorAll(selector) {
      const [, cls] = selector.split(".");
      return this.children.filter((node) => node.className === cls);
    },
  };
  const doc = {
    title,
    head,
    documentElement: { classList: classes },
    body: { classList: [] },
    createElement: (tag) => ({
      tag,
      id: "",
      className: "",
      dataset: {},
      textContent: "",
      remove() {
        const at = head.children.indexOf(this);
        if (at >= 0) head.children.splice(at, 1);
      },
    }),
  };
  return { m_strName: title, m_popup: { document: doc, location: { href: url } } };
};
const nodesOf = (popup) => popup.m_popup.document.head.children.map((node) => [node.id, node.dataset.steamUiHash]);

const main = fakePopup("SP", "https://steamloopback.host/routes/library/home?Valve%20Steam%20Gamepad");
const quickAccess = fakePopup("QuickAccess_uid1", "about:blank?createflags=274");
const popups = [main, quickAccess];
const window = { g_PopupManager: { GetPopups: () => popups } };
const globals = {
  window,
  subscribe: (id, callback) => {
    subscriptions.set(id, callback);
    return () => subscriptions.delete(id);
  },
  setInterval: (callback) => {
    pass = callback;
    return 1;
  },
  clearInterval: () => {
    pass = null;
  },
};
const code = sharedFragments(asset) + gateSource(asset, "createThemeStyles", "themeStyles");
const { gate, createThemeStyles } = instantiate(
  globals,
  code,
  "({gate:createThemeStyles(),createThemeStyles})",
);

// Without Steam's popup manager there is nothing to install into, and the refusal says so.
delete window.g_PopupManager;
const refused = createThemeStyles();
assert.equal(refused.install().ok, false, "no popup manager refuses installation");
assert.match(refused.status().lastError, /popup manager/u);
window.g_PopupManager = { GetPopups: () => popups };

assert.equal(gate.install().ok, true);
assert.equal(gate.status().resolved, true);
assert.equal(gate.status().installed, true);
assert.ok(pass, "a pass is scheduled for windows opened or navigated later");

// Blocks land in the documents their targets name, in the published order, and nowhere else.
const publish = (styles, revision = 1) => subscriptions.get("steam-ui.theme-styles")({ styles, revision });
publish([
  { id: "base", css: "body{}", targets: ["~Valve%20Steam%20Gamepad~", "QuickAccess.*"], hash: "h1" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" },
  { id: "desk", css: ".desk{}", targets: ["Steam|SteamLibraryWindow"], hash: "h3" },
  { id: "bad id!", css: "", targets: ["SP"], hash: "h4" },
  { id: "notargets", css: "", targets: [], hash: "h5" },
]);
assert.deepEqual(nodesOf(main), [["steam-ui-theme-base", "h1"]], "the main window gets the block for its URL");
assert.deepEqual(
  nodesOf(quickAccess),
  [
    ["steam-ui-theme-base", "h1"],
    ["steam-ui-theme-menu", "h2"],
  ],
  "Quick Access gets both blocks whose title pattern it matches, in order",
);
assert.equal(gate.status().styles, 3, "an unusable block is refused at the publication boundary");
assert.equal(main.m_popup.document.head.children[0].className, "steam-ui-theme-style");
assert.equal(main.m_popup.document.head.children[0].textContent, "body{}");

// The same publication again touches nothing: the nodes keep their identity.
const before = main.m_popup.document.head.children.slice();
publish([
  { id: "base", css: "body{}", targets: ["~Valve%20Steam%20Gamepad~", "QuickAccess.*"], hash: "h1" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" },
  { id: "desk", css: ".desk{}", targets: ["Steam|SteamLibraryWindow"], hash: "h3" },
], 2);
pass();
assert.deepEqual(main.m_popup.document.head.children, before, "an unchanged block is not rebuilt");

// A changed hash rebuilds the document, and a block whose targets no longer name it goes.
publish([
  { id: "base", css: "body{color:red}", targets: ["~Valve%20Steam%20Gamepad~"], hash: "h9" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" },
], 3);
assert.deepEqual(nodesOf(main), [["steam-ui-theme-base", "h9"]]);
assert.equal(main.m_popup.document.head.children[0].textContent, "body{color:red}");
assert.deepEqual(nodesOf(quickAccess), [["steam-ui-theme-menu", "h2"]]);

// A window Steam opens after the publication is styled on the next pass, and one whose root
// carries the class a target names is matched by it.
const mainMenu = fakePopup("MainMenu_uid1", "about:blank", ["bpm"]);
popups.push(mainMenu);
publish([
  { id: "base", css: "body{color:red}", targets: ["~Valve%20Steam%20Gamepad~"], hash: "h9" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*", "!bpm"], hash: "h2" },
], 4);
assert.deepEqual(nodesOf(mainMenu), [["steam-ui-theme-menu", "h2"]], "a root class is a target");
const late = fakePopup("QuickAccess_uid2", "about:blank");
popups.push(late);
assert.deepEqual(nodesOf(late), [], "a new window has nothing until a pass looks at it");
pass();
assert.deepEqual(nodesOf(late), [["steam-ui-theme-menu", "h2"]], "the next pass styles a new window");
assert.equal(gate.status().documents, 4);
assert.equal(gate.status().nodes, 4);

// A pattern that does not compile matches nothing rather than throwing the pass away.
publish([{ id: "broken", css: "", targets: ["(["], hash: "h0" }], 5);
assert.equal(gate.status().lastError, "");
assert.equal(gate.status().nodes, 0);

// Removal takes every owned node out of every window and stops looking.
publish([{ id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" }], 6);
assert.equal(gate.status().nodes, 2);
const removed = gate.remove();
assert.equal(removed.ok, true);
assert.equal(removed.nodes, 2);
assert.equal(pass, null, "removal clears the pass");
for (const popup of popups) assert.deepEqual(nodesOf(popup), [], "no owned node survives removal");
assert.equal(gate.status().installed, false);
assert.equal(gate.remove().absent, true);

console.log("Theme styles: targets, order, unchanged blocks, late windows and removal passed.");
