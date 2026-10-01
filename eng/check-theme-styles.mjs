// Exercise the emitted theme-styles gate against inert Steam windows.
//
// CSSLoader chooses the documents a block goes into by title, URL or root classes, appends one
// <style> per block and re-injects when a window is reloaded. This proves the gate does the same
// over every window Steam renders into: the ones its popup manager lists and the ones reachable
// only as React portal containers, which on Windows is where Quick Access and the main menu are.
// A title target has to match the window's own name too, because the Big Picture window's title
// is localized there. The right blocks land in the right documents in the published order, an
// unchanged publication touches nothing, a changed block is rebuilt, a window Steam announces later
// is styled without any polling, and removal leaves no node behind. This runs the emitted JavaScript.
import assert from "node:assert/strict";
import { gateSource, instantiate, loadAsset, sharedFragments } from "./check-harness.mjs";

const asset = loadAsset();
const subscriptions = new Map();
let created = null;

// A document small enough to read beside the gate: a head that keeps its children in order and
// answers the one query the gate makes, and a window that knows its own name.
const fakeDocument = (name, title, url, classes = []) => {
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
    nodeType: 9,
    title,
    head,
    location: { href: url },
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
  doc.defaultView = { name, location: { href: url } };
  return doc;
};
const fakePopup = (name, title, url, classes) => {
  const doc = fakeDocument(name, title, url, classes);
  return { m_strName: name, m_popup: { document: doc, location: { href: url } } };
};
const nodesOf = (doc) => doc.head.children.map((node) => [node.id, node.dataset.steamUiHash]);

// The Windows client of 2026-09-28: the popup manager lists the Big Picture window, named
// "SP BPM_uid0" with a localized title and a URL without CSSLoader's markers, and a context menu;
// Quick Access and the main menu are portal containers only.
const bigPicture = fakePopup("SP BPM_uid0", "Big-Picture-Modus", "https://steamloopback.host/index.html?PLATFORM=windows");
const contextMenu = fakePopup("contextmenu_13_uid0", "Menu", "https://steamloopback.host/index.html?PLATFORM=windows");
const popups = [bigPicture, contextMenu];
const quickAccess = fakeDocument("QuickAccess_uid17", "QuickAccess_uid17", "https://steamloopback.host/routes/library/home");
const mainMenu = fakeDocument("MainMenu_uid17", "MainMenu_uid17", "https://steamloopback.host/routes/library/home", ["bpm"]);
// SharedJSContext's own React root: a tree with one portal per window, plus a portal into a
// container the gate must ignore (its own document).
const sharedDocument = fakeDocument("", "SharedJSContext", "https://steamloopback.host/routes/library/home");
const portalContainers = [{ nodeType: 1, ownerDocument: quickAccess }, sharedDocument];
const portal = (container) => ({ tag: 4, stateNode: { containerInfo: container }, child: null, sibling: null });
const tree = { tag: 3, child: portal(portalContainers[0]), sibling: null };
tree.child.sibling = { tag: 0, child: portal(portalContainers[1]), sibling: null };
const host = { __reactContainer$abc: { stateNode: { current: tree } } };
sharedDocument.getElementById = (id) => (id === "root" ? host : null);
sharedDocument.body.children = [];

// Steam's popup manager announces each window it creates; the gate keeps no timer.
const popupManager = () => ({
  GetPopups: () => popups,
  AddPopupCreatedCallback: (callback) => {
    created = callback;
    return {
      Unregister: () => {
        created = null;
      },
    };
  },
});
const window = { g_PopupManager: popupManager() };
const globals = {
  window,
  document: sharedDocument,
  subscribe: (id, callback) => {
    subscriptions.set(id, callback);
    return () => subscriptions.delete(id);
  },
  setInterval: () => {
    throw new Error("the gate must not poll");
  },
};
const code = sharedFragments(asset) + gateSource(asset, "createThemeStyles", "themeStyles");
const { gate, createThemeStyles } = instantiate(
  globals,
  code,
  "({gate:createThemeStyles(),createThemeStyles})",
);

// Without Steam's popup manager and without a mounted tree there is nothing to install into.
delete window.g_PopupManager;
const savedRoot = host.__reactContainer$abc;
delete host.__reactContainer$abc;
const refused = createThemeStyles();
assert.equal(refused.install().ok, false, "no way to the windows refuses installation");
assert.match(refused.status().lastError, /popup manager/u);
host.__reactContainer$abc = savedRoot;
window.g_PopupManager = popupManager();

assert.equal(gate.install().ok, true);
assert.equal(gate.status().resolved, true);
assert.equal(gate.status().installed, true);
assert.ok(created, "the gate listens for the windows Steam creates");
assert.equal(gate.status().watchingPopups, true);

// Blocks land in the documents their targets name, in the published order, and nowhere else. The
// Big Picture window is named by CSSLoader's `SP` through the window's name, not its localized
// title; Quick Access is reached through a portal and matched by its name.
const publish = (styles, revision = 1) => subscriptions.get("steam-ui.theme-styles")({ styles, revision });
publish([
  { id: "base", css: "body{}", targets: ["SP( BPM_uid\\d+)?", "QuickAccess.*"], hash: "h1" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" },
  { id: "desk", css: ".desk{}", targets: ["Steam|SteamLibraryWindow"], hash: "h3" },
  { id: "ctx", css: ".ctx{}", targets: [".*Menu"], hash: "h4" },
  { id: "bad id!", css: "", targets: ["SP"], hash: "h5" },
  { id: "notargets", css: "", targets: [], hash: "h6" },
]);
assert.deepEqual(nodesOf(bigPicture.m_popup.document), [["steam-ui-theme-base", "h1"]], "the Big Picture window is matched by its name");
assert.deepEqual(
  nodesOf(quickAccess),
  [
    ["steam-ui-theme-base", "h1"],
    ["steam-ui-theme-menu", "h2"],
  ],
  "a window reached only through a portal gets both blocks whose pattern it matches, in order",
);
assert.deepEqual(nodesOf(contextMenu.m_popup.document), [["steam-ui-theme-ctx", "h4"]], "a title still matches");
assert.deepEqual(nodesOf(sharedDocument), [], "SharedJSContext's own document is never styled");
assert.equal(gate.status().styles, 4, "an unusable block is refused at the publication boundary");
assert.equal(gate.status().windows, 3);
assert.equal(bigPicture.m_popup.document.head.children[0].className, "steam-ui-theme-style");
assert.equal(bigPicture.m_popup.document.head.children[0].textContent, "body{}");
assert.deepEqual(
  gate.windows().map((entry) => [entry.name, entry.nodes]),
  [["SP BPM_uid0", 1], ["contextmenu_13_uid0", 1], ["QuickAccess_uid17", 2]],
  "the diagnostics name every window and what it holds",
);

// The same publication again touches nothing: the nodes keep their identity.
const before = bigPicture.m_popup.document.head.children.slice();
publish([
  { id: "base", css: "body{}", targets: ["SP( BPM_uid\\d+)?", "QuickAccess.*"], hash: "h1" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" },
  { id: "desk", css: ".desk{}", targets: ["Steam|SteamLibraryWindow"], hash: "h3" },
  { id: "ctx", css: ".ctx{}", targets: [".*Menu"], hash: "h4" },
], 2);
created(bigPicture);
assert.deepEqual(bigPicture.m_popup.document.head.children, before, "an unchanged block is not rebuilt");

// A changed hash rebuilds the document, and a block whose targets no longer name it goes.
publish([
  { id: "base", css: "body{color:red}", targets: ["SP( BPM_uid\\d+)?"], hash: "h9" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*"], hash: "h2" },
], 3);
assert.deepEqual(nodesOf(bigPicture.m_popup.document), [["steam-ui-theme-base", "h9"]]);
assert.equal(bigPicture.m_popup.document.head.children[0].textContent, "body{color:red}");
assert.deepEqual(nodesOf(quickAccess), [["steam-ui-theme-menu", "h2"]]);
assert.deepEqual(nodesOf(contextMenu.m_popup.document), []);

// A window Steam opens after the publication is styled when Steam announces it; a new portal is
// found on the next publication; one whose root carries the class a target names is matched.
tree.child.sibling.sibling = portal({ nodeType: 1, ownerDocument: mainMenu });
publish([
  { id: "base", css: "body{color:red}", targets: ["SP( BPM_uid\\d+)?"], hash: "h9" },
  { id: "menu", css: ".menu{}", targets: ["QuickAccess.*", "!bpm"], hash: "h2" },
], 4);
assert.deepEqual(nodesOf(mainMenu), [["steam-ui-theme-menu", "h2"]], "a root class is a target");
const late = fakePopup("QuickAccess_uid18", "QuickAccess_uid18", "about:blank");
popups.push(late);
assert.deepEqual(nodesOf(late.m_popup.document), [], "a new window has nothing until Steam announces it");
created(late);
assert.deepEqual(nodesOf(late.m_popup.document), [["steam-ui-theme-menu", "h2"]], "an announced window is styled");
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
assert.equal(created, null, "removal stops listening for new windows");
for (const doc of [bigPicture.m_popup.document, contextMenu.m_popup.document, quickAccess, mainMenu, late.m_popup.document]) {
  assert.deepEqual(nodesOf(doc), [], "no owned node survives removal");
}
assert.equal(gate.status().installed, false);
assert.equal(gate.remove().absent, true);

console.log("Theme styles: popup and portal windows, names and titles, order, unchanged blocks, late windows and removal passed.");
