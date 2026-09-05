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
