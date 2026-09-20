import assert from "node:assert/strict";
import {
  createReact,
  element,
  gateSource,
  instantiate,
  loadAsset,
  readSource,
  sharedFragments,
  withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const subscriptions = new Map();
let refuseRequests = false;
const requests = [];
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
const NativePanel = withSource(() => null, "onActivate onCancel focusableIfEmpty focusClassName");
const originalRoot = withSource(() => element("tabs", { tabs: [] }), "QuickAccessMenuBrowserView");
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
const runtime = (id) => (id === "react" ? react : { memo });
runtime.findUnique = (tokens) => [tokens.includes("useState") ? "react" : "qam"];
runtime.resolve = () => jsx;
runtime.exported = (_tokens, predicate) =>
  nativeAvailable && predicate(NativePanel) ? NativePanel : null;
const globals = {
  getWebpackRuntime: () => runtime,
  subscribe: (id, callback) => {
    subscriptions.set(id, callback);
    return () => subscriptions.delete(id);
  },
  request: (...args) => {
    requests.push(args);
    return refuseRequests ? Promise.reject(new Error("refused")) : Promise.resolve();
  },
  nextActionGeneration: () => 1,
  createIconRenderer: () => () => null,
};
const code =
  sharedFragments(asset) +
  gateSource(asset, "createExtensionsTab", "extensionsTab") +
  gateSource(asset, "createGameContextMenu", "gameContextMenu");
const { extensions, menu, createExtensions } = instantiate(
  globals,
  code,
  "({extensions:createExtensionsTab(),menu:createGameContextMenu(),createExtensions:createExtensionsTab})",
);
assert.equal(extensions.install().ok, true);
assert.equal(memo.type.__steamUiExtensionsTabOriginal.kind, "steam-ui-property-snapshot-v1");
assert.equal(memo.type.__steamUiExtensionsTabOriginal.value, originalRoot);
const probeSource = readSource("src/SteamUiToolkit/Surfaces/SteamExtensionsTabSurface.cs");
const candidatesSource = probeSource.slice(
  probeSource.indexOf("const candidates="),
  probeSource.indexOf("const memo=candidates"),
);
const probeCandidates = new Function("exports", `${candidatesSource}; return candidates;`);
assert.deepEqual(probeCandidates({ memo }), ["memo"], "the next C# probe accepts the claimed memo");
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
const tab = memo.type({}).props.tabs[0];
const panel = tab.panel.type();
const row = panel.props.children[1][0];
const action = row.props.children[2];
assert.equal(action.type, NativePanel, "actions participate in Steam's focus graph");
action.props.onActivate();
assert.deepEqual(requests[0].slice(0, 3), [
  "steam-ui.extensions-tab",
  "activate",
  { id: "run-one" },
]);
// An item the host could never act on is refused at the publication boundary rather than rendered:
// the configure command rejects a negative revision, so a row offering one is a dead control.
subscriptions.get("steam-ui.extensions-tab")({
  items: [
    { id: "one", name: "One", version: "1", status: "Ready", configurationRevision: -1 },
    { id: "two", name: "Two", version: "1", status: "Ready", configurationRevision: 2 },
  ],
});
assert.equal(extensions.status().items, 1, "a negative configuration revision is refused");

// A typed draft survives re-renders of the publication it was typed against, and no longer.
const renderPanel = () => {
  hookIndex = 0;
  return memo.type({}).props.tabs[0].panel.type();
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
const inputOf = (panel) => panel.props.children[1][0].props.children[2].props.children[1];
subscriptions.get("steam-ui.extensions-tab")(textItem(3, "alpha"));
assert.equal(inputOf(renderPanel()).props.value, "alpha");
inputOf(renderPanel()).props.onChange({ currentTarget: { value: "beta" } });
assert.equal(inputOf(renderPanel()).props.value, "beta", "the draft survives a re-render");
subscriptions.get("steam-ui.extensions-tab")(textItem(4, "gamma"));
assert.equal(
  inputOf(renderPanel()).props.value,
  "gamma",
  "a newer configuration revision replaces the draft",
);

// A refused save drops the draft too, so the box stops showing and resending a rejected value.
subscriptions.get("steam-ui.extensions-tab")(textItem(5, "delta"));
inputOf(renderPanel()).props.onChange({ currentTarget: { value: "epsilon" } });
assert.equal(inputOf(renderPanel()).props.value, "epsilon");
refuseRequests = true;
renderPanel().props.children[1][0].props.children[2].props.children[2].props.onActivate();
await Promise.resolve();
await Promise.resolve();
refuseRequests = false;
assert.equal(inputOf(renderPanel()).props.value, "delta", "a refused save drops the draft");

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
assert.equal(createExtensions().install().ok, false, "missing native controls refuse installation");
assert.equal(memo.type, originalRoot);

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
