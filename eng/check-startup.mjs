import assert from "node:assert/strict";
import { createContext, runInContext, runInNewContext } from "node:vm";
import { fragment, gateSource, loadAsset, sharedFragments } from "./check-harness.mjs";

const asset = loadAsset();
// The C# suite executes its actual probe expressions, including the embedded resolver. This check
// exercises the resolver carried by the emitted asset without scraping C# source declarations.
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
        runtime,
        factories,
        cache,
        calls: () => calls,
        window: { webpackChunksteamui: { push: (chunk) => chunk[2](runtime) } },
    };
}

// Separate evaluations share fingerprint work, but each lookup still checks the live registry.
{
    const f = fixture();
    const matching = () => {
        /* cached-fingerprint */
        throw new Error("fingerprint lookup executed a factory");
    };
    f.factories.match = matching;
    f.factories.other = () => {};
    const context = createContext({ window: f.window, runtime: f.runtime });
    const counters = runInContext(
        `(() => {
            const originalIncludes = String.prototype.includes;
            const originalKeys = Object.keys;
            let searches = 0, registries = 0;
            String.prototype.includes = function (...args) {
                searches++;
                return Reflect.apply(originalIncludes, this, args);
            };
            Object.keys = function (value) {
                if (value === runtime.m) registries++;
                return originalKeys(value);
            };
            return {
                read: () => ({ searches, registries }),
                restore: () => {
                    String.prototype.includes = originalIncludes;
                    Object.keys = originalKeys;
                }
            };
        })()`,
        context,
        { timeout: 1000 },
    );
    const createResolver = () =>
        runInContext(
            `(()=>{${emittedResolver} return createSteamUiModuleResolver('cached');})()`,
            context,
            { timeout: 1000 },
        );
    const tokens = ["cached-fingerprint"];
    try {
        const first = createResolver();
        assert.equal(first.count(tokens), 1);
        const cold = counters.read();
        assert.ok(cold.searches > 0, "the cold lookup searches factory sources");
        const second = createResolver();
        assert.equal(second.count(tokens), 1);
        assert.equal(first.findUnique(tokens)[0], "match");
        assert.equal(counters.read().searches, cold.searches, "warm evaluations share matches");
        assert.equal(
            counters.read().registries,
            cold.registries + 2,
            "warm lookups check the registry",
        );

        f.factories.duplicate = matching;
        assert.equal(second.count(tokens), 2);
        assert.equal(first.findUnique(tokens), null);
        assert.throws(() => first.resolve(tokens), /module ambiguous/u);
        delete f.factories.duplicate;
        assert.equal(first.count(tokens), 1);
        delete f.factories.match;
        assert.equal(second.count(tokens), 0, "removing the unique factory invalidates the match");
        assert.equal(first.findUnique(tokens), null);
        assert.throws(() => second.resolve(tokens), /module absent/u);

        f.factories.match = matching;
        assert.equal(first.count(tokens), 1);
        const ordered = counters.read().searches;
        const unchanged = f.factories.other;
        delete f.factories.other;
        f.factories.other = unchanged;
        assert.equal(second.count(tokens), 1);
        assert.ok(counters.read().searches > ordered, "factory order changes invalidate matches");
        const factoryCount = Object.keys(f.factories).length;
        f.factories.match = () => {};
        assert.equal(Object.keys(f.factories).length, factoryCount);
        assert.equal(second.count(tokens), 0, "replacement at the same id invalidates the match");
        const absent = counters.read().searches;
        assert.equal(first.findUnique(tokens), null);
        assert.equal(counters.read().searches, absent, "absent matches are cached too");

        f.runtime.m = { match: matching };
        assert.equal(first.count(tokens), 1, "a replacement registry is checked");
        f.runtime.m = f.factories;
        assert.equal(second.count(tokens), 0);
        const other = fixture();
        other.factories.match = matching;
        f.window.webpackChunksteamui.push = (chunk) => chunk[2](other.runtime);
        const third = createResolver();
        assert.equal(third.count(tokens), 1, "a new runtime has its own matches");
        assert.equal(first.count(tokens), 0, "the original runtime keeps its own matches");
        assert.equal(f.calls(), 0, "count and findUnique never load factories");
        assert.equal(other.calls(), 0);
    } finally {
        counters.restore();
    }
}

for (const source of [`(()=>{${emittedResolver} return createSteamUiModuleResolver('test');})()`]) {
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
    assert.equal(
        guarded.exported(["export-token"], (value) => value?.kind === "store").kind,
        "store",
    );
    assert.throws(
        () => guarded.exported(["export-token"], (value) => !!value),
        /export ambiguous/u,
    );
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
const settings = fragment(asset, "settings.ts");
assert.match(icons, /const createIconRenderer =/u);
// The ownership primitives for the shared useMemo claim the host takes, and the gate helpers it
// walks Steam's tree and resolves its fields with.
const shared = sharedFragments(asset);
const createHost = (window) =>
    runInNewContext(
        `${emittedResolver}
     ${shared}
     ${icons}
     ${settings}
     const getWebpackRuntime = scope => createSteamUiModuleResolver(scope);
     ${host}
     createNativeComponentHost();`,
        {
            window,
            subscribePluginFrontends: () => () => {},
            pluginFrontendElements: () => [],
        },
        { timeout: 1000 },
    );
function componentFixture() {
    const f = fixture();
    Object.assign(f.factories, {
        react(_module, exports) {
            // react.transitional.element useState cloneElement createElement
            exports.useMemo = function originalUseMemo() {};
            exports.useState = (initial) => [initial, () => {}];
            exports.createElement = (type, props, ...children) => ({ type, props, children });
            exports.cloneElement = (element, props) => ({ ...element, props });
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
    assert.match(result.error, /^native component runtime resolution failed/);
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
    const installed = host.install("autoTdp");
    assert.equal(installed.ok, true, JSON.stringify(installed));
    assert.equal(host.status("autoTdp").performanceRootWrapped, true);
    host.remove("autoTdp");
    assert.equal(f.cache.react.exports.useMemo.name, "originalUseMemo");
}

console.log("Steam startup: missing factories stay uncached.");

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
const bridgeConfig = {
    namespace: "__steamUiBridgeFixture",
    binding: "__steamUiBindingFixture",
    version: 1,
    assetHash: "fixture",
    vocabularyRevision: 1,
    contextGeneration: 1,
    documentGeneration: 1,
    maximumPending: 64,
    timeoutMilliseconds: 1000,
    allowed: Object.fromEntries(ids.map((id) => [id, []])),
};
const bridge = runInNewContext(
    `${bridgeSource}\nreturn bridge;\n})()`,
    { window: {}, __STEAM_UI_CONFIGURATION_JSON__: bridgeConfig },
    { timeout: 1000 },
);
for (const patchId of ids) {
    const publish = (payload) =>
        bridge.deliver({ ...bridgeConfig, type: "state", patchId, payload });
    assert.equal(publish(1), true);
    let failures = 0;
    const stopBroken = bridge.subscribe(patchId, () => {
        failures++;
        throw new Error("consumer failed");
    });
    const seen = [];
    const stopHealthy = bridge.subscribe(patchId, (value) => seen.push(value));
    assert.equal(publish(2), true);
    stopBroken();
    assert.equal(publish(3), true);
    stopHealthy();
    assert.equal(publish(4), true);
    assert.equal(failures, 2);
    assert.deepEqual(seen, [1, 2, 3]);
}
console.log(
    `Steam bridge: cached and live subscriber failures isolated for ${ids.length} module IDs.`,
);

// An envelope too large for one evaluation arrives in parts and reaches subscribers once, whole.
{
    const patchId = ids[0];
    const seen = [];
    const refusals = [];
    bridge.subscribe(patchId, (value) => seen.push(value));
    bridge.subscribeRefusal(patchId, (reason) => refusals.push(reason));
    const text = JSON.stringify({
        ...bridgeConfig,
        type: "state",
        patchId,
        payload: { big: "x".repeat(50) },
    });
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
    assert.equal(
        part(10, 0, cut[0], { documentGeneration: 99 }),
        false,
        "a stale document is refused",
    );

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
