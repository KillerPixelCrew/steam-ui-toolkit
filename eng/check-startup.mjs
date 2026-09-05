import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(resolve(root, path), "utf8");
const asset = readFileSync(process.argv[2] ?? resolve(root, "dist/prelude.js"), "utf8");
const probeSource = read("src/SteamUiToolkit/Surfaces/SteamUiProbeJs.cs");
const resolver = read("src/SteamUiToolkit/SteamUiAssets/Source/module-resolver.ts");
const preamble = probeSource
  .match(/=> \$\$"""\s*([\s\S]*?)\s*""";/u)[1]
  .replace("{{SteamUiModuleResolver.CreateExpression(chunkLabel)}}", `(${resolver})("test")`);
const start = asset.indexOf("function createSteamUiModuleResolver(");
const returnIndex = asset.indexOf("return requirePresent;", start);
const end = returnIndex < 0 ? -1 : asset.indexOf("}", returnIndex) + 1;
assert.ok(start >= 0 && end > start);

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
  `(()=>{${asset.slice(start, end)} return createSteamUiModuleResolver('test');})()`,
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
  f.factories.broken = () => {
    throw new Error("dependency missing");
  };
  assert.throws(() => guarded("broken"), /resolution failed/u);
  assert.throws(() => guarded("broken"), /previously failed/u);
  assert.equal(f.calls(), 3);
}

// Exercise the complete emitted host, including failures after React has resolved.
const hostStart = asset.indexOf("function createNativeComponentHost()");
const hostEnd = asset.indexOf('registerGate("nativeComponents"', hostStart);
assert.ok(hostStart >= 0 && hostEnd > hostStart);
const createHost = (window) =>
  runInNewContext(
    `${asset.slice(start, end)}
     const getWebpackRuntime = scope => createSteamUiModuleResolver(scope);
     ${asset.slice(hostStart, hostEnd)}
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
        // LocalizeString(e) void 0===r?e
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
for (const broken of ["react", "performance", "tdp"]) {
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

const network = read("src/SteamUiToolkit/Surfaces/SteamNetworkSurface.cs").match(
  /probeExpression: \$\$"""\s*([\s\S]*?)\s*"""/u,
)[1];
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
