// Runs the emitted service gates, Bluetooth, brightness, performance, audio and network, against
// inert Steam stubs and a fake bridge. The gates are instantiated over the asset's own ownership
// primitives, RPC replies and gate helpers, so the claims and replies exercised here are the shipped
// ones.
//
// Each gate's probe is its own find, so the probe is exercised here too: against Steam's real module
// resolver over a scripted webpack runtime, a unique service and one this gate already owns are
// ready; an absent, ambiguous or incomplete one, a real native backend and an unclaimable member are
// incompatible; a store or field that is simply not constructed yet is not ready; and no probe runs a
// factory, invokes an export or calls a service method. An installed gate answers its probe from what
// it holds.
import assert from "node:assert/strict";
import {
    assertRemoveRetries,
    failingHost,
    fragment,
    gateSource,
    instantiate,
    loadAsset,
    sharedFragments,
    tick,
    withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const shared = sharedFragments(asset);
const resolverSource = fragment(asset, "module-resolver.ts");
const OwnedMarker = "__steamUiOwnedNamespace";

// A gate over a scripted webpack runtime and Steam's real module resolver. `modules` maps a module
// id to the source text its factory is fingerprinted by and the exports it would return; `loaded`
// records every module the gate actually loaded, and no factory ever runs.
const overModules = (label, create, window, modules) => {
    const loaded = [];
    const runtime = (id) => {
        loaded.push(id);
        return modules[id].exports;
    };
    runtime.m = {};
    for (const [id, { source }] of Object.entries(modules)) {
        runtime.m[id] = new Function(`// ${source}\nthrow Error("factory executed")`);
    }
    window.webpackChunksteamui = { push: (chunk) => chunk[2](runtime) };
    const gate = instantiate(
        {
            window,
            ownedMarker: OwnedMarker,
            request: async () => {},
            subscribe: () => () => {},
        },
        `${resolverSource}\nconst getWebpackRuntime = () => createSteamUiModuleResolver("check");\n` +
            `${shared}\n${gateSource(asset, label)}`,
        `${create}()`,
    );
    return { gate, loaded };
};

const forbidden = () => {
    throw new Error("service operation invoked");
};
const unrelated = { source: "unrelated module", exports: { danger: forbidden } };

// One probe per mode: the service module is unique, already ours, absent, matched by two modules,
// exported twice under different values, or missing something the gate needs.
const probeModes = (service, label, create, setup) => {
    const modes = ["unique", "owned", "absent", "ambiguous", "ambiguous-export", "incomplete", ...setup.extra];
    for (const mode of modes) {
        const window = { SteamClient: { System: {} } };
        let calls = 0;
        const make = () => setup.make(mode, window, () => calls++);
        const modules = { unrelated, ...(setup.modules?.(mode) ?? {}) };
        if (mode !== "absent") {
            const value = make();
            const exports = { first: value, alias: value };
            if (mode === "ambiguous-export") exports.second = make();
            modules.service = { source: setup.source, exports };
            if (mode === "ambiguous") modules.twin = { source: setup.source, exports: { first: make() } };
        }
        setup.window?.(mode, window);
        const { gate, loaded } = overModules(label, create, window, modules);
        const probe = gate.probe();
        const expected = setup.expect?.(mode) ?? (mode === "unique" || mode === "owned" ? "ready" : "incompatible");
        assert.equal(probe.outcome, expected, `${service} ${mode}: ${probe.reason ?? ""}`);
        assert.equal(loaded.includes("unrelated"), false, `${service} ${mode} must not load an unrelated module`);
        if (mode === "absent" || mode === "ambiguous") {
            assert.equal(loaded.includes("service"), false, `${service} ${mode} must not load a non-unique module`);
        }
        if (setup.calls) assert.equal(calls, setup.calls(mode), `${service} ${mode} singleton reads`);
        assert.equal(gate.status().installed, false, `${service} ${mode}: a probe must install nothing`);
    }
};

probeModes("audio", "gates/audio.ts", "createAudioNamespace", {
    source: "SteamClient.System.Audio RegisterForDeviceAdded m_bAvailable",
    extra: ["native", "no-system"],
    make: (mode) => (mode === "incomplete" ? {} : { m_bAvailable: false, RegisterOrUpdateDevice: forbidden }),
    window: (mode, window) => {
        if (mode === "owned") window.SteamClient.System.Audio = { [OwnedMarker]: true };
        if (mode === "native") window.SteamClient.System.Audio = {};
        if (mode === "no-system") delete window.SteamClient.System;
    },
    expect: (mode) =>
        mode === "no-system" ? "notReady" : mode === "unique" || mode === "owned" ? "ready" : "incompatible",
});

probeModes("performance", "gates/performance.ts", "createPerfNamespace", {
    source: "SteamClient.System.Perf RegisterForStateChanges m_msgState",
    extra: ["native", "unconstructed", "unpublished"],
    make: (mode, window, count) => {
        function Store() {
            throw new Error("m_msgState export constructed");
        }
        Store.Get = () => {
            count();
            if (mode === "unconstructed") return null;
            return mode === "incomplete" ? {} : { m_msgState: {} };
        };
        return Store;
    },
    window: (mode, window) => {
        if (mode === "owned") window.SteamClient.System.Perf = { [OwnedMarker]: true };
        if (mode === "native") window.SteamClient.System.Perf = {};
        if (mode !== "unpublished") window.SystemPerfStore = { m_msgState: {} };
    },
    expect: (mode) =>
        mode === "unconstructed" || mode === "unpublished"
            ? "notReady"
            : mode === "unique" || mode === "owned"
              ? "ready"
              : "incompatible",
    // The class's Get is the one thing read, and only from a unique module, unique class and no
    // native backend in the way.
    calls: (mode) => (["absent", "ambiguous", "ambiguous-export", "native"].includes(mode) ? 0 : 1),
});

probeModes("brightness", "gates/brightness.ts", "createBrightnessGate", {
    source: "m_flDisplayBrightness is_display_brightness_available",
    extra: ["native", "unmounted", "no-flag"],
    make: (mode, window, count) => {
        function Store() {
            throw new Error("m_flDisplayBrightness export constructed");
        }
        Store.Get = () => {
            count();
            // Quick Settings has not built its display section yet.
            if (mode === "unmounted") return {};
            if (mode === "no-flag") return { m_msgSettings: {} };
            return {
                m_msgSettings: {
                    is_display_brightness_available: mode === "owned" || mode === "native",
                    __steamUiBrightnessRevealed: mode === "owned",
                },
            };
        };
        return Store;
    },
    window: (mode, window) => {
        window.SteamClient.System.Display = { SetBrightness: forbidden };
        if (mode !== "incomplete") window.SteamClient.System.Display.RegisterForBrightnessChanges = forbidden;
    },
    expect: (mode) =>
        mode === "unmounted" || mode === "no-flag"
            ? "notReady"
            : mode === "unique" || mode === "owned"
              ? "ready"
              : "incompatible",
    calls: (mode) => (["absent", "ambiguous", "ambiguous-export"].includes(mode) ? 0 : 1),
});

const BluetoothOperations = [
    "GetState",
    "SetDiscovering",
    "Pair",
    "CancelPair",
    "Connect",
    "Disconnect",
    "Forget",
    "SetTrusted",
    "SetWakeAllowed",
    "GetDeviceDetails",
];
probeModes("bluetooth", "gates/bluetooth.ts", "createBluetoothService", {
    source: "BluetoothManager.GetState#1",
    extra: ["locked", "missing-cache", "ambiguous-cache"],
    make: (mode) => {
        const service = {};
        for (const name of BluetoothOperations) service[name] = forbidden;
        if (mode === "owned") {
            service.GetState = () => {
                throw new Error("owned GetState invoked");
            };
            service.GetState.__steamUiOwnedBluetoothService = true;
            service.GetState.__steamUiOriginalBluetoothServiceMethod = {
                kind: "steam-ui-property-snapshot-v1",
                hadOwn: true,
                value: forbidden,
            };
        }
        if (mode === "incomplete") delete service.Forget;
        if (mode === "locked") Object.defineProperty(service, "GetState", { writable: false, configurable: false });
        return service;
    },
    modules: (mode) => {
        const cache = { invalidateQueries: forbidden, getQueryState: forbidden };
        const modules = {};
        if (mode !== "missing-cache") modules.cache = { source: "ReactQueryDevtools offlineFirst", exports: { cache } };
        if (mode === "ambiguous-cache") modules.cacheTwin = { source: "ReactQueryDevtools offlineFirst", exports: {} };
        return modules;
    },
});

// The network gate loads no module at all. A hidden getter and one this gate owns are ready; a native
// getter already reading true and a non-configurable one are incompatible; no store yet is not ready.
for (const mode of ["hidden", "owned", "native", "locked", "unpublished"]) {
    const get = () => mode === "owned" || mode === "native";
    get.__steamUiOwnedGetter = mode === "owned";
    const proto = {};
    Object.defineProperty(proto, "networkManagementAvailable", { get, configurable: mode !== "locked" });
    const window = mode === "unpublished" ? {} : { SystemNetworkStore: Object.create(proto) };
    const noModules = () => {
        throw new Error("modules loaded");
    };
    const gate = instantiate(
        {
            window,
            getWebpackRuntime: () => Object.assign(noModules, { count: noModules, findUnique: noModules, exported: noModules }),
            request: async () => {},
            subscribe: () => () => {},
        },
        `${shared}\n${gateSource(asset, "gates/network.ts")}`,
        "createNetworkGate()",
    );
    const expected = { hidden: "ready", owned: "ready", native: "incompatible", locked: "incompatible", unpublished: "notReady" };
    const probe = gate.probe();
    assert.equal(probe.outcome, expected[mode], `network ${mode}: ${probe.reason ?? ""}`);
    if (mode !== "unpublished") assert.equal(typeof probe.facts.hasWirelessDevice, "boolean");
}
console.log("Service probes: unique and owned are ready, unconstructed is not ready, the rest are incompatible.");

// Every standard status field the host verifies with one predicate.
const assertHolds = (gate, name) => {
    const status = gate.status();
    assert.equal(status.installed, true, `${name}: installed`);
    assert.equal(status.held, true, `${name}: held`);
    assert.ok(status.claims > 0, `${name}: claims on the ledger`);
    assert.equal(gate.probe().outcome, "ready", `${name}: an installed gate answers its probe from its handles`);
    assert.equal(gate.probe().installed, true);
};
const assertRemoved = (gate, name) => {
    const status = gate.status();
    assert.equal(status.installed, false, `${name}: uninstalled`);
    assert.equal(status.claims, 0, `${name}: nothing left on the ledger`);
};

// --- Bluetooth: actions keep the device's identity, its progress and the backend's failures ------
{
    const operations = {};
    for (const name of BluetoothOperations) operations[name] = () => {};
    // GetAdapterDetails is replaced too, but the stub need not have it.
    const { host: rf, failNext } = failingHost(operations);
    const queryClient = { invalidated: 0, invalidateQueries() { this.invalidated++; }, getQueryState() {} };
    // The stub as the gate finds it: by its service method name and its shape, never by module id or
    // export name.
    const runtime = () => {
        const require = () => {
            throw new Error("the gate must not name a module id");
        };
        require.exported = (tokens, predicate) => {
            if (tokens.includes("ReactQueryDevtools")) return queryClient;
            assert.deepEqual(tokens, ["BluetoothManager.GetState#1"]);
            assert.equal(predicate({ GetState() {} }), false, "an object without Pair is not the stub");
            assert.equal(predicate(rf), true);
            return rf;
        };
        return require;
    };
    let publish;
    let failure = false;
    const calls = [];
    const gate = instantiate(
        {
            getWebpackRuntime: runtime,
            request: async (_, command, payload) => {
                calls.push([command, payload]);
                if (failure) throw new Error("device unavailable");
            },
            subscribe: (_, callback) => {
                publish = callback;
                return () => {};
            },
        },
        `${shared}\n${gateSource(asset, "gates/bluetooth.ts")}`,
        "createBluetoothService()",
    );
    assert.equal(gate.install().ok, true);
    assertHolds(gate, "Bluetooth");
    assert.equal(gate.status().replaced, 11);
    publish({
        available: true,
        enabled: true,
        devices: [{ id: "logical", operationInProgress: true }],
    });
    assert.ok(queryClient.invalidated > 0, "a publication invalidates Steam's cached answer");
    assert.equal((await rf.GetState()).Body().toObject().devices[0].operation_in_progress, true);
    assert.equal((await rf.GetDeviceDetails({ device: "logical" })).Body().toObject().device.id, "logical");
    assert.equal((await rf.Pair({ device: "logical" })).BSuccess(), true);
    assert.deepEqual(calls, [["pair", { device: "logical" }]]);
    failure = true;
    const result = await rf.Connect({ device: "logical" });
    assert.equal(result.BSuccess(), false);
    assert.equal(result.BFailed(), true);
    assert.match(gate.status().operationError, /device unavailable/);
    assertRemoveRetries(gate, failNext, "Bluetooth");
    assertRemoved(gate, "Bluetooth");
    assert.equal(rf.Connect, operations.Connect, "the stub's own method is handed back");
    assert.equal(Object.hasOwn(rf, "GetAdapterDetails"), false, "a method the stub never had is gone again");
    console.log("Bluetooth actions preserve identity, progress, backend failures and removal retry.");
}

// --- Brightness: confirmed readback against an inert Steam observable ---------------------------
{
    function fixture() {
        let publish;
        const requests = [];
        const nativeSetter = () => {
            throw new Error("native stub must not run");
        };
        const { host: display, failNext } = failingHost({
            SetBrightness: nativeSetter,
            RegisterForBrightnessChanges: () => {},
        });
        const observable = {
            m_currentValue: 1,
            Set(value) {
                this.m_currentValue = value;
                // A focused Steam slider can echo a programmatic refresh synchronously or later.
                display.SetBrightness(value);
                Promise.resolve().then(() => display.SetBrightness(value));
            },
        };
        const store = {
            m_msgSettings: { is_display_brightness_available: false },
            m_flDisplayBrightness: observable,
        };
        // The store class as the gate finds it: by its module's tokens and by its own body, never by
        // module id or export name.
        const DisplayStore = function DisplayStore() {};
        DisplayStore.Get = () => store;
        withSource(DisplayStore, "class Au{static Get(){}m_msgSettings={};m_flDisplayBrightness=(0,a.Jc)(1)}");
        const runtime = () => {
            const require = () => {
                throw new Error("the gate must not name a module id");
            };
            require.exported = (tokens, predicate) => {
                assert.ok(tokens.includes("m_flDisplayBrightness"));
                assert.equal(predicate({ Get: () => store }), false, "a plain object is not the store class");
                assert.equal(predicate(DisplayStore), true);
                return DisplayStore;
            };
            return require;
        };
        const create = instantiate(
            {
                window: { SteamClient: { System: { Display: display } } },
                getWebpackRuntime: runtime,
                subscribe: (_id, callback) => {
                    publish = callback;
                    return () => {
                        publish = null;
                    };
                },
                request: (...args) => new Promise((resolve, reject) => requests.push({ args, resolve, reject })),
            },
            `${shared}\n${gateSource(asset, "gates/brightness.ts")}`,
            "createBrightnessGate",
        );
        const gate = create();
        assert.equal(gate.install().ok, true);
        assertHolds(gate, "brightness");
        assert.equal(gate.status().available, true);
        return {
            gate,
            display,
            observable,
            store,
            requests,
            publish: (value) => publish(value),
            nativeSetter,
            failNext,
        };
    }
    const f = fixture();
    f.publish({ percent: 100, revision: 1 });
    const down = f.display.SetBrightness(0.31);
    assert.equal(f.requests.length, 1);
    f.publish({ percent: 100, revision: 1 });
    f.requests[0].resolve({ percent: 31, revision: 2 });
    await down;
    await tick();
    assert.equal(f.observable.m_currentValue, 0.31, "matching readback must update Steam's initial 100% observable");
    assert.equal(f.requests.length, 1, "neither synchronous nor deferred projection echoes write hardware");
    f.publish({ percent: 100, revision: 1 });
    assert.equal(f.observable.m_currentValue, 0.31, "older readback cannot restore 100%");
    f.publish({ percent: 45, revision: 3 });
    await tick();
    assert.equal(f.observable.m_currentValue, 0.45);
    assert.equal(f.requests.length, 1, "external brightness readback has no write side effect");

    const up = f.display.SetBrightness(0.6);
    const latest = f.display.SetBrightness(0.25);
    f.requests[2].resolve({ percent: 25, revision: 5 });
    await latest;
    f.requests[1].resolve({ percent: 60, revision: 4 });
    await up;
    await tick();
    assert.equal(f.observable.m_currentValue, 0.25, "late command completion cannot override the latest request");
    const failed = f.display.SetBrightness(0.8);
    f.requests[3].reject(new Error("readback failed"));
    await failed;
    await tick();
    assert.equal(f.observable.m_currentValue, 0.25);
    assert.match(f.gate.status().operationError, /readback failed/);
    assert.equal(f.requests.length, 4, "failed writes are not retried");
    await f.display.SetBrightness(NaN);
    await f.display.SetBrightness(2);
    assert.equal(f.requests.length, 4, "invalid input cannot become a default write");
    const removed = f.display.SetBrightness(0.7);
    assert.equal(f.gate.remove().ok, true);
    assertRemoved(f.gate, "brightness");
    assert.equal(f.store.m_msgSettings.is_display_brightness_available, false, "removal hides the row again");
    f.requests[4].resolve({ percent: 70, revision: 6 });
    await removed;
    assert.equal(f.display.SetBrightness, f.nativeSetter);
    assert.equal(f.observable.m_currentValue, 0.25, "late completion after removal is ignored");
    assert.equal(f.gate.install().ok, true);
    f.publish({ percent: 20, revision: 1 });
    await tick();
    assert.equal(f.observable.m_currentValue, 0.2, "reinstall accepts a fresh host revision sequence");
    assertRemoveRetries(f.gate, f.failNext, "brightness");
    assert.equal(f.display.SetBrightness, f.nativeSetter, "a retried removal hands the setter back");
    assertRemoved(f.gate, "brightness");
    console.log(
        "Brightness: confirmed readback, focused echoes, stale revisions, overlapping writes, failure, reinstall and removal retry pass.",
    );
}

// --- Performance: undecodable updates are refused, removal restores what it displaced -----------
{
    const { host: system, failNext } = failingHost({});
    let publish;
    const requests = [];
    class SettingsUpdate {
        static deserializeBinary(bytes) {
            return { toObject: () => ({ bytes: [...bytes] }) };
        }
    }
    const displacedState = failingHost({ current_game_id: "seeded" });
    const store = {
        // A field Steam's store already held before the first publication, and three it did not.
        m_msgState: displacedState.host,
        CreateSettingsUpdateRequest: () => new SettingsUpdate(),
    };
    const PerfStore = withSource(function PerfStore() {}, "class Ps{static Get(){}m_msgState={}}");
    PerfStore.Get = () => store;
    const runtime = () => {
        const require = () => {
            throw new Error("the gate must not name a module id");
        };
        require.count = (tokens) => (tokens.includes("SteamClient.System.Perf") ? 1 : 0);
        require.exported = (tokens, predicate) => {
            assert.ok(tokens.includes("m_msgState"));
            assert.equal(predicate({ Get: () => store }), false, "a plain object is not the store class");
            assert.equal(predicate(PerfStore), true);
            return PerfStore;
        };
        return require;
    };
    const gate = instantiate(
        {
            window: { SteamClient: { System: system }, SystemPerfStore: store },
            ownedMarker: OwnedMarker,
            getWebpackRuntime: runtime,
            request: async (_, command, payload) => {
                requests.push([command, payload]);
            },
            subscribe: (_, callback) => {
                publish = callback;
                return () => {};
            },
        },
        `${shared}\n${gateSource(asset, "gates/performance.ts")}`,
        "createPerfNamespace()",
    );
    assert.equal(gate.install().ok, true);
    assertHolds(gate, "performance");
    assert.equal(gate.status().namespacePresent, true);
    publish({ limits: { fps_limit_options: [30, 60] }, global: { fps: 60 }, currentGameId: "480" });
    assert.equal(store.m_msgState.current_game_id, "480");
    assert.deepEqual(store.m_msgState.settings, { global: { fps: 60 }, per_app: {} });

    await system.Perf.UpdateSettings("AQI=");
    assert.deepEqual(requests, [["updateSettings", { delta: { bytes: [1, 2] } }]]);
    await assert.rejects(system.Perf.UpdateSettings("not base64!"), /could not be decoded/u);
    await assert.rejects(system.Perf.UpdateSettings(null), /could not be decoded/u);
    await assert.rejects(system.Perf.UpdateSettings([]), /could not be decoded/u);
    assert.equal(requests.length, 1, "an undecodable update is refused, never sent as an empty delta");

    failNext();
    assert.equal(gate.remove().ok, false, "namespace withdrawal failure is reported");
    assert.equal(gate.status().installed, true, "withdrawal failure retains the gate");
    const originalState = store.m_msgState;
    store.m_msgState = { current_game_id: "replacement" };
    assertRemoveRetries(gate, displacedState.failNext, "performance state restoration");
    assertRemoved(gate, "performance");
    assert.equal(originalState.current_game_id, "seeded", "restore the displaced store, even after it was replaced");
    assert.equal(store.m_msgState.current_game_id, "replacement", "a replacement store is untouched");
    assert.equal(Object.hasOwn(system, "Perf"), false);
    for (const field of ["limits", "settings", "active_profile_game_id"]) {
        assert.equal(Object.hasOwn(originalState, field), false, `removal deletes ${field} again`);
    }
    console.log("Performance: decoded updates, refused undecodable ones, displaced state and removal retry pass.");
}

// --- Audio: volume direction reaches the host the right way round, the store is handed back ------
{
    const { host: system, failNext } = failingHost({});
    const requests = [];
    let publish;
    const audioStore = {
        m_bAvailable: false,
        m_activeOutputDeviceId: 4294967295,
        m_activeInputDeviceId: 4294967295,
        m_mapAudioDevices: new Map(),
        RegisterOrUpdateDevice(device) {
            this.m_mapAudioDevices.set(device.id, { getDeviceVolume: () => 0.5, m_mapVolumes: new Map() });
        },
    };
    const runtime = () => {
        const require = () => {
            throw new Error("the gate must not name a module id");
        };
        require.count = (tokens) => (tokens.includes("SteamClient.System.Audio") ? 1 : 0);
        require.exported = (tokens, predicate) => {
            assert.ok(tokens.includes("m_bAvailable"));
            assert.equal(predicate({ m_bAvailable: false }), false, "a store needs its ingestion method");
            assert.equal(predicate(audioStore), true);
            return audioStore;
        };
        return require;
    };
    const gate = instantiate(
        {
            window: { SteamClient: { System: system } },
            ownedMarker: OwnedMarker,
            getWebpackRuntime: runtime,
            request: async (_, command, payload) => {
                requests.push([command, payload]);
            },
            subscribe: (_, callback) => {
                publish = callback;
                return () => {};
            },
        },
        `${shared}\n${gateSource(asset, "gates/audio.ts")}`,
        "createAudioNamespace()",
    );
    assert.equal(gate.install().ok, true);
    assertHolds(gate, "audio");
    // The client's own enum: Input is 0 and Output is 1.
    await system.Audio.SetDeviceVolume(1, 1, 0.42);
    await system.Audio.SetDeviceVolume(1, 0, 0.3);
    await system.Audio.SetDeviceVolume(1, 5, 0.9);
    assert.deepEqual(requests, [
        ["setVolume", { percent: 42, input: false }],
        ["setVolume", { percent: 30, input: true }],
    ]);
    publish({
        devices: [{ id: "{speaker}", name: "Speakers", hasOutput: true }],
        activeOutputDeviceId: "{speaker}",
        volumePercent: 50,
    });
    assert.equal(audioStore.m_bAvailable, true, "the running store is told audio is available");
    assert.equal(audioStore.m_mapAudioDevices.size, 1);
    assertRemoveRetries(gate, failNext, "audio");
    assertRemoved(gate, "audio");
    assert.equal(Object.hasOwn(system, "Audio"), false);
    assert.equal(audioStore.m_bAvailable, false, "removal hands the store back its own availability");
    assert.equal(audioStore.m_activeOutputDeviceId, 4294967295);
    assert.equal(audioStore.m_mapAudioDevices.size, 0, "devices this gate registered leave the store");
    console.log("Audio: volume directions, store restoration and removal retry pass.");
}

// --- Network: scan observation and a removal that survives a store without its own methods -------
{
    const { host: network, failNext } = failingHost({
        StartScanningForNetworks() {
            return "started";
        },
        StopScanningForNetworks() {
            return "stopped";
        },
    });
    const proto = {};
    const originalGetter = () => false;
    Object.defineProperty(proto, "networkManagementAvailable", {
        get: originalGetter,
        configurable: true,
    });
    // No IsAnyDeviceConnected or IsAnyDeviceConnecting: removal must still hand everything back.
    const store = Object.assign(Object.create(proto), { m_mapNetworkAccessPoints: new Map() });
    const requests = [];
    const gate = instantiate(
        {
            window: { SteamClient: { System: { Network: network } }, SystemNetworkStore: store },
            getWebpackRuntime: () => ({}),
            request: async (_, command) => {
                requests.push(command);
            },
            subscribe: () => () => {},
        },
        `${shared}\n${gateSource(asset, "gates/network.ts")}`,
        "createNetworkGate()",
    );
    assert.equal(gate.install().ok, true);
    assertHolds(gate, "network");
    assert.equal(store.networkManagementAvailable, true);
    assert.equal(gate.status().scanWrapped, true);
    assert.equal(network.StartScanningForNetworks(), "started", "the original still runs");
    assert.equal(network.StopScanningForNetworks(), "stopped");
    await tick();
    assert.deepEqual(requests, ["startScan", "stopScan"]);
    assertRemoveRetries(gate, failNext, "network");
    assertRemoved(gate, "network");
    assert.equal(gate.status().scanWrapped, false);
    assert.equal(Object.getOwnPropertyDescriptor(proto, "networkManagementAvailable").get, originalGetter);
    assert.equal(store.networkManagementAvailable, false);
    console.log("Network: scan observation, store without refresh methods and removal retry pass.");
}
