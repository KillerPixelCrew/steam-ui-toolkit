import assert from "node:assert/strict";
import { runInNewContext } from "node:vm";
import {
  fragment,
  gateSource,
  loadAsset,
  readSource,
  sharedFragments,
} from "./check-harness.mjs";

const asset = loadAsset();
const probeSource = readSource("src/SteamUiToolkit/Surfaces/SteamUiProbeJs.cs");
// The C# probes run the raw module-resolver.ts bytes inside SteamUiProbeJs's preamble, so the
// preamble is executed here exactly as a probe embeds it. A reshaped preamble fails here by name
// rather than leaving the probes' resolver unexercised.
const probeClose = probeSource.match(/Close = "([^"]*)";/u)?.[1];
const probePreamble = probeSource.match(/(?:=>|return)\s*\$\$"""\s*([\s\S]*?)\s*""";/u)?.[1];
const resolverSlot = "{{SteamUiModuleResolver.CreateExpression(chunkLabel)}}";
assert.ok(probeClose, "SteamUiProbeJs.Close must stay one string literal this check can run");
assert.ok(
  probePreamble?.includes(resolverSlot),
  "SteamUiProbeJs's preamble must stay one raw string that embeds the shared module resolver",
);
const resolver = readSource("src/SteamUiToolkit/SteamUiAssets/Source/module-resolver.ts");
const preamble = probePreamble.replace(resolverSlot, `(${resolver})("test")`);
// The emitted resolver, as the asset carries it.
const emittedResolver = fragment(asset, "module-resolver.ts");

// This is the loader shape read from the failing Steam session. Calling a missing factory
// caches empty exports even when that factory is registered later.
function fixture() {
  const cache = {};
  const factories = {};
  let calls = 0;
  function runtime(id) {
    calls++;
    if (cache[id]) return cache[id].exports;
    const module = (cache[id] = { exports: {} });
    factories[id].call(module.exports, module, module.exports, runtime);
    return module.exports;
  }
  runtime.m = factories;
  return {
    factories,
    cache,
    calls: () => calls,
    window: { webpackChunksteamui: { push: (chunk) => chunk[2](runtime) } },
  };
}

for (const source of [
  `${preamble} return req; }catch(error){throw error;} })()`,
  `(()=>{${emittedResolver} return createSteamUiModuleResolver('test');})()`,
]) {
  const f = fixture();
  const guarded = runInNewContext(source, { window: f.window }, { timeout: 1000 });
  assert.throws(() => guarded("late"), /module absent/u);
  assert.equal(f.calls(), 0);
  assert.equal(f.cache.late, undefined);
  f.factories.late = (_module, exports) => {
    exports.ready = true;
  };
  assert.equal(guarded("late").ready, true);
  assert.equal(f.calls(), 1);
  f.factories.unrelated = () => {
    throw new Error("unrelated module executed");
  };
  f.factories.match = (_module, exports) => {
    /* unique-token */ exports.ok = true;
  };
  assert.equal(guarded.count(["unique-token"]), 1);
  assert.equal(f.calls(), 1);
  assert.equal(guarded.resolve(["unique-token"]).ok, true);
  f.factories.duplicate = f.factories.match;
  assert.throws(() => guarded.resolve(["unique-token"]), /module ambiguous/u);
  assert.throws(() => guarded.resolve(["missing-token"]), /module absent/u);
  assert.throws(() => guarded.resolve([]), /fingerprint invalid/u);
  assert.equal(f.calls(), 2);
  // A factory that threw once (a dependency not ready during a cold start) is not remembered as
  // failed: Steam's loader keeps the exports it set and never runs it again, so the next resolution
  // hands those to the shape tests instead of refusing for the bridge's life.
  let brokenRuns = 0;
  f.factories.broken = (_module, exports) => {
    brokenRuns++;
    exports.ready = true;
    throw new Error("dependency missing");
  };
  assert.throws(() => guarded("broken"), /resolution failed/u);
  assert.equal(guarded("broken").ready, true, "the exports the factory set stay usable");
  assert.equal(brokenRuns, 1, "the factory ran once");
  assert.equal(f.calls(), 4);
  // Exports are chosen by shape: aliases of one value count once, two distinct fits are refused.
  f.factories.store = (_module, exports) => {
    /* export-token */
    const store = { kind: "store" };
    Object.defineProperty(exports, "a", { enumerable: true, get: () => store });
    Object.defineProperty(exports, "b", { enumerable: true, get: () => store });
    exports.hook = () => store;
    Object.defineProperty(exports, "broken", {
      enumerable: true,
      get: () => {
        throw new Error("getter failed");
      },
    });
  };
  assert.equal(guarded.exported(["export-token"], (value) => value?.kind === "store").kind, "store");
  assert.throws(() => guarded.exported(["export-token"], (value) => !!value), /export ambiguous/u);
  assert.throws(() => guarded.exported(["export-token"], () => false), /export absent/u);
  assert.throws(() => guarded.exported(["missing-token"], () => true), /module absent/u);
  assert.throws(() => guarded.exported(["export-token"], null), /predicate invalid/u);
}

// Exercise the complete emitted host, including failures after React has resolved.
const host = gateSource(asset, "components.ts");
// The row glyphs, which the host builds a renderer from before it resolves anything else. Taken from
// the asset rather than stubbed, so a fragment that stopped being emitted fails here instead of
// silently leaving every row without an icon.
const icons = fragment(asset, "icons.ts");
assert.match(icons, /const createIconRenderer =/u);
// The ownership primitives for the shared useMemo claim the host takes, and the gate helpers it
// walks Steam's tree and resolves its fields with.
const shared = sharedFragments(asset);
const createHost = (window) =>
  runInNewContext(
    `${emittedResolver}
     ${shared}
     ${icons}
     const getWebpackRuntime = scope => createSteamUiModuleResolver(scope);
     ${host}
     createNativeComponentHost();`,
    { window },
    { timeout: 1000 },
  );
function componentFixture() {
  const f = fixture();
  Object.assign(f.factories, {
    react(_module, exports) {
      // react.transitional.element useState cloneElement createElement
      exports.useMemo = function originalUseMemo() {};
    },
    fields(_module, exports) {
      // DialogSlider_Container DropDownField SliderField
      exports.slider = function () {
        // onChangeComplete notchCount valueSuffix explainerTitle
      };
      exports.dropdown = function () {
        // contextMenuPositionOptions childrenContainerWidth menuLabel
      };
    },
    layout(_module, exports) {
      // PanelSectionTitle PanelSectionRow spinner
      exports.section = function () {
        // PanelSectionTitle spinner
      };
      exports.row = { $$typeof: "test", render() {} };
    },
    localization(_module, exports) {
      // Attempting to localize token Unable to find localization token LocalizeString
      exports.localize = function () {
        // const text = LocalizationManager.LocalizeString(token); return text === undefined ? token : text
      };
    },
    performance(_module, exports) {
      // #QuickAccess_Tab_Perf_Common_Settings #QuickAccess_Tab_Perf_BatteryTimeRemaining
      exports.root = function () {
        // TS.ON_FRAME
        return null;
      };
    },
    tdp() {
      // #QuickAccess_Tab_Perf_TDPLimitEnabled #QuickAccess_Tab_Perf_TDPLimitUnits
    },
  });
  return f;
}
function assertRefused(host) {
  const result = host.install("autoTdp");
  assert.equal(result.ok, false);
  assert.equal(result.error, "native component runtime resolution failed");
  const status = host.status("autoTdp");
  assert.equal(status.lastError, result.error);
  assert.equal(status.registered, false);
  assert.equal(status.performanceRootWrapped, false);
}
assertRefused(createHost({}));
for (const broken of ["react", "performance"]) {
  const f = componentFixture();
  const tokens = String(f.factories[broken]);
  // Retain the real fingerprint but throw when that exact factory is resolved.
  f.factories[broken] = new Function(`/* ${tokens} */ throw new Error('dependency missing');`);
  const host = createHost(f.window);
  assertRefused(host);
  assert.ok(f.cache[broken], `the failure reached the ${broken} factory`);
  if (f.cache.react?.exports.useMemo)
    assert.equal(f.cache.react.exports.useMemo.name, "originalUseMemo");
  host.remove("autoTdp");
  host.dispose();
}
{
  const f = componentFixture();
  const host = createHost(f.window);
  assert.equal(host.install("autoTdp").ok, true);
  assert.equal(host.status("autoTdp").performanceRootWrapped, true);
  host.remove("autoTdp");
  assert.equal(f.cache.react.exports.useMemo.name, "originalUseMemo");
}

const network = readSource("src/SteamUiToolkit/Surfaces/SteamNetworkSurface.cs")
  .match(/new SteamGatePatch\([\s\S]*?\$\$"""\s*([\s\S]*?)\s*"""/u)[1]
  .replace("{{SteamUiProbeJs.Close}}", probeClose);
const window = {
  webpackChunksteamui: {
    push() {
      throw new Error("probe loaded modules");
    },
  },
};
assert.equal(JSON.parse(runInNewContext(network, { window })).error, "network store unavailable");
window.SystemNetworkStore = Object.create({
  get networkManagementAvailable() {
    return false;
  },
});
assert.equal(JSON.parse(runInNewContext(network, { window })).getterConfigurable, true);
assert.equal(JSON.parse(runInNewContext(network, { window })).currentlyHidden, true);
console.log(
  "Steam startup: missing factories stay uncached; network probe waits for Steam's singleton.",
);

// Exercise the shared bridge, so cached replay cannot interrupt any module's installation. The
// whole bridge fragment runs over a fixture window and configuration, and the checks below drive the
// bridge object it publishes.
const bridgeSource = fragment(asset, "bridge.ts");
// Every identity the asset names: a gate's own patch id, a publication it reads under another
// gate's id, and each row definition's patch id.
const ids = [
  ...new Set(
    [...asset.matchAll(/(?:const (?:patchId|publicationId) = |\bpatchId: )"([^"]+)"/gu)].map(
      (m) => m[1],
    ),
  ),
];
assert.ok(ids.includes("steam-ui.power-limit") && ids.length >= 6);
const bridgeConfig = { namespace: "__steamUiBridgeFixture", binding: "__steamUiBindingFixture",
  version: 1, assetHash: "fixture", vocabularyRevision: 1, contextGeneration: 1, documentGeneration: 1,
  maximumPending: 64, timeoutMilliseconds: 1000, allowed: Object.fromEntries(ids.map(id => [id, []])) };
const bridge = runInNewContext(
  `${bridgeSource}\nreturn bridge;\n})()`,
  { window: {}, __STEAM_UI_CONFIGURATION_JSON__: bridgeConfig },
  { timeout: 1000 },
);
for (const patchId of ids) {
  const publish = payload => bridge.deliver({ ...bridgeConfig, type: "state", patchId, payload });
  assert.equal(publish(1), true);
  let failures = 0;
  const stopBroken = bridge.subscribe(patchId, () => { failures++; throw new Error("consumer failed"); });
  const seen = [];
  const stopHealthy = bridge.subscribe(patchId, value => seen.push(value));
  assert.equal(publish(2), true);
  stopBroken();
  assert.equal(publish(3), true);
  stopHealthy();
  assert.equal(publish(4), true);
  assert.equal(failures, 2);
  assert.deepEqual(seen, [1, 2, 3]);
}
console.log(`Steam bridge: cached and live subscriber failures isolated for ${ids.length} module IDs.`);

// An envelope too large for one evaluation arrives in parts and reaches subscribers once, whole.
{
  const patchId = ids[0];
  const seen = [];
  const refusals = [];
  bridge.subscribe(patchId, (value) => seen.push(value));
  bridge.subscribeRefusal(patchId, (reason) => refusals.push(reason));
  const text = JSON.stringify({ ...bridgeConfig, type: "state", patchId, payload: { big: "x".repeat(50) } });
  const cut = [text.slice(0, 20), text.slice(20, 45), text.slice(45)];
  const part = (id, index, text, extra = {}) =>
    bridge.deliverPart({ ...bridgeConfig, id, index, count: cut.length, text, ...extra });
  assert.equal(part(7, 0, cut[0]), true);
  assert.equal(part(7, 1, cut[1]), true);
  assert.equal(seen.length, 1, "nothing is delivered before the last part");
  assert.equal(part(7, 2, cut[2]), true);
  assert.equal(seen.at(-1).big.length, 50, "the reassembled state reaches subscribers");

  // Two deliveries whose parts interleave both arrive: reassembly is keyed by delivery id.
  const delivered = seen.length;
  for (let index = 0; index < cut.length; index++) {
    assert.equal(part(8, index, cut[index]), true);
    assert.equal(part(9, index, cut[index]), true);
  }
  assert.equal(seen.length, delivered + 2, "both interleaved deliveries reach subscribers");

  // A part out of order drops only its own delivery; the other one being reassembled is untouched.
  assert.equal(part(11, 0, cut[0]), true);
  assert.equal(part(12, 0, cut[0]), true);
  assert.equal(part(11, 2, cut[2]), false, "a part out of order is refused");
  assert.equal(part(11, 1, cut[1]), false, "the interrupted set is gone");
  assert.equal(part(12, 1, cut[1]), true);
  assert.equal(part(12, 2, cut[2]), true, "the other delivery still completes");
  assert.equal(part(13, 1, cut[1]), false, "a part of an unknown delivery is refused");
  assert.equal(part(10, 0, cut[0], { documentGeneration: 99 }), false, "a stale document is refused");

  // A refusal reaches the surface, and the next state clears it.
  assert.equal(
    bridge.deliver({ ...bridgeConfig, type: "refused", patchId, reason: "too large" }),
    true,
  );
  assert.deepEqual(refusals, ["too large"]);
  assert.equal(bridge.deliver({ ...bridgeConfig, type: "state", patchId, payload: 5 }), true);
  assert.deepEqual(refusals, ["too large", null]);
}
console.log("Steam bridge: parted deliveries reassemble once, and refusals reach the surface.");
