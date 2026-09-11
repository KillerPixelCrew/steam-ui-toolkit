import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createBluetoothService()");
const end = asset.indexOf('registerGate("bluetooth"', start);
assert.ok(start >= 0 && end > start);
const rf = { GetState() {}, Pair() {} };
// The stub as the gate finds it: by its service method name and its shape, never by module id or
// export name.
const runtime = () => {
  const require = () => {
    throw new Error("the gate must not name a module id");
  };
  require.exported = (tokens, predicate) => {
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
const gate = new Function("getWebpackRuntime", "transportReply", "invalidateQuery", "request", "subscribe", "claimed", "storedOriginal",
  asset.slice(start, end) + "\nreturn createBluetoothService();")(
  runtime,
  body => ({ BSuccess: () => true, BFailed: () => false, GetEResult: () => 1, Body: () => ({ toObject: () => body }) }),
  () => {},
  async (_, command, payload) => { calls.push([command, payload]); if (failure) throw new Error("device unavailable"); },
  (_, callback) => { publish = callback; return () => {}; },
  () => false, value => value);
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
