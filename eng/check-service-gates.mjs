// Runs the emitted service gates, Bluetooth, brightness, performance, audio and network, against
// inert Steam stubs and a fake bridge. The gates are instantiated over the asset's own ownership
// primitives, RPC replies and gate helpers, so the claims and replies exercised here are the shipped
// ones.
import assert from "node:assert/strict";
import {
  assertRemoveRetries,
  failingHost,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
  tick,
} from "./check-harness.mjs";

const asset = loadAsset();
const shared = sharedFragments(asset);

// --- Bluetooth: actions keep the device's identity, its progress and the backend's failures ------
{
  const { host: rf, failNext } = failingHost({ GetState() {}, Pair() {} });
  // The stub as the gate finds it: by its service method name and its shape, never by module id or
  // export name.
  const runtime = () => {
    const require = () => {
      throw new Error("the gate must not name a module id");
    };
    require.exported = (tokens, predicate) => {
      // The query client an action invalidates afterwards; this fixture has none.
      if (tokens.includes("ReactQueryDevtools")) return null;
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
  publish({ available: true, enabled: true, devices: [{ id: "logical", operationInProgress: true }] });
  assert.equal((await rf.GetState()).Body().toObject().devices[0].operation_in_progress, true);
  assert.equal((await rf.GetDeviceDetails({ device: "logical" })).Body().toObject().device.id, "logical");
  assert.equal((await rf.Pair({ device: "logical" })).BSuccess(), true);
  assert.deepEqual(calls, [["pair", { device: "logical" }]]);
  failure = true;
  const result = await rf.Connect({ device: "logical" });
  assert.equal(result.BSuccess(), false);
  assert.equal(result.BFailed(), true);
  assert.match(gate.status().lastError, /device unavailable/);
  assertRemoveRetries(gate, failNext, "Bluetooth");
  assert.equal(Object.hasOwn(rf, "Connect"), false, "a method the stub never had is gone again");
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
    const { host: display, failNext } = failingHost({ SetBrightness: nativeSetter });
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
    Object.defineProperty(DisplayStore, "toString", {
      value: () => "class Au{static Get(){}m_msgSettings={};m_flDisplayBrightness=(0,a.Jc)(1)}",
    });
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
    return {
      gate,
      display,
      observable,
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
  assert.equal(
    f.observable.m_currentValue,
    0.31,
    "matching readback must update Steam's initial 100% observable",
  );
  assert.equal(
    f.requests.length,
    1,
    "neither synchronous nor deferred projection echoes write hardware",
  );
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
  assert.equal(
    f.observable.m_currentValue,
    0.25,
    "late command completion cannot override the latest request",
  );
  const failed = f.display.SetBrightness(0.8);
  f.requests[3].reject(new Error("readback failed"));
  await failed;
  await tick();
  assert.equal(f.observable.m_currentValue, 0.25);
  assert.match(f.gate.status().lastError, /readback failed/);
  assert.equal(f.requests.length, 4, "failed writes are not retried");
  await f.display.SetBrightness(NaN);
  await f.display.SetBrightness(2);
  assert.equal(f.requests.length, 4, "invalid input cannot become a default write");
  const removed = f.display.SetBrightness(0.7);
  f.gate.remove();
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
  const store = {
    // A field Steam's store already held before the first publication, and three it did not.
    m_msgState: { current_game_id: "seeded" },
    CreateSettingsUpdateRequest: () => new SettingsUpdate(),
  };
  const gate = instantiate(
    {
      window: { SteamClient: { System: system }, SystemPerfStore: store },
      ownedMarker: "__steamUiOwnedNamespace",
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
  publish({ limits: { fps_limit_options: [30, 60] }, global: { fps: 60 }, currentGameId: "480" });
  assert.equal(store.m_msgState.current_game_id, "480");
  assert.deepEqual(store.m_msgState.settings, { global: { fps: 60 }, per_app: {} });

  await system.Perf.UpdateSettings("AQI=");
  assert.deepEqual(requests, [["updateSettings", { delta: { bytes: [1, 2] } }]]);
  await assert.rejects(system.Perf.UpdateSettings("not base64!"), /could not be decoded/u);
  await assert.rejects(system.Perf.UpdateSettings(null), /could not be decoded/u);
  assert.equal(requests.length, 1, "an undecodable update is refused, never sent as an empty delta");

  assertRemoveRetries(gate, failNext, "performance");
  assert.equal(Object.hasOwn(system, "Perf"), false);
  assert.equal(store.m_msgState.current_game_id, "seeded", "removal restores the displaced value");
  for (const field of ["limits", "settings", "active_profile_game_id"]) {
    assert.equal(Object.hasOwn(store.m_msgState, field), false, `removal deletes ${field} again`);
  }
  console.log("Performance: decoded updates, refused undecodable ones, displaced state and removal retry pass.");
}

// --- Audio: volume direction reaches the host the right way round --------------------------------
{
  const { host: system, failNext } = failingHost({});
  const requests = [];
  const gate = instantiate(
    {
      window: { SteamClient: { System: system } },
      ownedMarker: "__steamUiOwnedNamespace",
      getWebpackRuntime: () => {
        throw new Error("no audio store in this fixture");
      },
      request: async (_, command, payload) => {
        requests.push([command, payload]);
      },
      subscribe: () => () => {},
    },
    `${shared}\n${gateSource(asset, "gates/audio.ts")}`,
    "createAudioNamespace()",
  );
  assert.equal(gate.install().ok, true);
  // The client's own enum: Input is 0 and Output is 1.
  await system.Audio.SetDeviceVolume(1, 1, 0.42);
  await system.Audio.SetDeviceVolume(1, 0, 0.3);
  await system.Audio.SetDeviceVolume(1, 5, 0.9);
  assert.deepEqual(requests, [
    ["setVolume", { percent: 42, input: false }],
    ["setVolume", { percent: 30, input: true }],
  ]);
  assertRemoveRetries(gate, failNext, "audio");
  assert.equal(Object.hasOwn(system, "Audio"), false);
  console.log("Audio: volume directions and removal retry pass.");
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
      request: async (_, command) => {
        requests.push(command);
      },
      subscribe: () => () => {},
    },
    `${shared}\n${gateSource(asset, "gates/network.ts")}`,
    "createNetworkGate()",
  );
  assert.equal(gate.install().ok, true);
  assert.equal(store.networkManagementAvailable, true);
  assert.equal(network.StartScanningForNetworks(), "started", "the original still runs");
  assert.equal(network.StopScanningForNetworks(), "stopped");
  await tick();
  assert.deepEqual(requests, ["startScan", "stopScan"]);
  assertRemoveRetries(gate, failNext, "network");
  assert.equal(gate.status().scanWrapped, false);
  assert.equal(Object.getOwnPropertyDescriptor(proto, "networkManagementAvailable").get, originalGetter);
  assert.equal(store.networkManagementAvailable, false);
  console.log("Network: scan observation, store without refresh methods and removal retry pass.");
}
