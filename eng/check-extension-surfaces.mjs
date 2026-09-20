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
const requests = [];
const react = createReact({ useState: () => [0, () => {}], useEffect: () => {} });
const NativePanel = withSource(() => null, "onActivate onCancel focusableIfEmpty focusClassName");
const originalRoot = withSource(() => element("tabs", { tabs: [] }), "QuickAccessMenuBrowserView");
const memo = { type: originalRoot };
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
    return Promise.resolve();
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
const replacement = createExtensions();
assert.equal(replacement.install().ok, true, "a fresh gate resolves its durable owned original");
assert.equal(replacement.remove().ok, true);
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
