// Runs the emitted brightness gate against an inert Steam observable and bridge.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const ownershipStart = asset.indexOf("const defineHidden");
const ownershipEnd =
  ownershipStart + asset.slice(ownershipStart).search(/\n[ \t]*function create/u);
const gateStart = asset.indexOf("function createBrightnessGate()");
const gateEnd = asset.indexOf('registerGate("brightness"', gateStart);
assert.ok(ownershipStart >= 0 && ownershipEnd > ownershipStart && gateEnd > gateStart);

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
  const create = new Function(
    "window",
    "getWebpackRuntime",
    "subscribe",
    "request",
    asset.slice(ownershipStart, ownershipEnd) +
      "\n" +
      asset.slice(gateStart, gateEnd) +
      "\nreturn createBrightnessGate;",
  )(
    { SteamClient: { System: { Display: display } } },
    () => () => ({ mG: { Get: () => store } }),
    (_id, callback) => {
      publish = callback;
      return () => {
        publish = null;
      };
    },
    (...args) => new Promise((resolve, reject) => requests.push({ args, resolve, reject })),
  );
  const gate = create();
  assert.equal(gate.install().ok, true);
  return { gate, display, observable, requests, publish: (value) => publish(value), nativeSetter };
}
const tick = () => new Promise((resolve) => setImmediate(resolve));
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
