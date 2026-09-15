// Runs the emitted service gates, Bluetooth and brightness, against inert Steam stubs and a fake
// bridge. The gates are instantiated over the asset's own ownership primitives, RPC replies and gate
// helpers, so the claims and replies exercised here are the shipped ones.
import assert from "node:assert/strict";
import { gateSource, instantiate, loadAsset, sharedFragments, tick } from "./check-harness.mjs";

const asset = loadAsset();
const shared = sharedFragments(asset);

// --- Bluetooth: actions keep the device's identity, its progress and the backend's failures ------
{
  const rf = { GetState() {}, Pair() {} };
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
    `${shared}\n${gateSource(asset, "createBluetoothService", "bluetooth")}`,
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
  gate.remove();
  console.log("Bluetooth actions preserve identity, progress and backend failures.");
}

// --- Brightness: confirmed readback against an inert Steam observable ---------------------------
{
  function fixture() {
    let publish;
    const requests = [];
    const nativeSetter = () => {
      throw new Error("native stub must not run");
    };
    const display = { SetBrightness: nativeSetter };
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
      `${shared}\n${gateSource(asset, "createBrightnessGate", "brightness")}`,
      "createBrightnessGate",
    );
    const gate = create();
    assert.equal(gate.install().ok, true);
    return { gate, display, observable, requests, publish: (value) => publish(value), nativeSetter };
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
  console.log(
    "Brightness: confirmed readback, focused echoes, stale revisions, overlapping writes, failure and reinstall pass.",
  );
}
