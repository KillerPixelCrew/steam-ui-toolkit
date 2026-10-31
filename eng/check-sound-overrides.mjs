// Offline fixtures over the emitted gate and the real member-ownership primitives.
import assert from "node:assert/strict";
import {
  assertRemoveRetries,
  failingHost,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
} from "./check-harness.mjs";

const asset = loadAsset();
const code = gateSource(asset, "gates/sound-overrides.ts");
// Whether Steam's audio store has the playback manager the claim takes.
let storeShape = "present";
const original = function (url, ...args) {
  return [this, url, ...args];
};
const prototype = { PlayAudioURLWithRepeats: original };
const { host: manager, failNext } = failingHost(Object.create(prototype));
let publish;
let decoded;
const reports = [];
const instantiateGate = (failContext = false) =>
  instantiate(
    {
      window: { location: { href: "about:blank" } },
      getWebpackRuntime: () => ({
        exported: () => {
          if (storeShape === "absent") throw new Error("Steam export absent: m_GamepadUIAudioStore");
          return { GamepadUIAudio: { AudioPlaybackManager: storeShape === "present" ? manager : {} } };
        },
      }),
      subscribe: (_, callback) => {
        publish = callback;
        return () => {};
      },
      request: async (_, command, payload) => {
        assert.equal(command, "status");
        reports.push(payload);
        if (!payload.loading) decoded?.();
        return { ok: true };
      },
      AudioContext: class {
        constructor() {
          if (failContext) throw new Error("Audio context unavailable");
        }
        async decodeAudioData(bytes) {
          if (new Uint8Array(bytes)[0] === 0) throw new Error("corrupt");
        }
        async close() {}
      },
      fetch: async (url) => ({
        arrayBuffer: async () => Uint8Array.from(Buffer.from(url.split(",")[1], "base64")).buffer,
      }),
    },
    sharedFragments(asset) + code,
    "createSoundOverrides()",
  );

const data = "data:audio/wav;base64,AQ==";
const publishAndDecode = (sounds) =>
  new Promise((resolve) => {
    decoded = resolve;
    publish({ sounds, revision: 7 });
  }).then(() => Promise.resolve());
const gate = instantiateGate();
// The probe is the gate's find: a store without the manager, or no store, is incompatible.
for (const shape of ["absent", "methodless"]) {
  storeShape = shape;
  const probe = gate.probe();
  assert.equal(probe.outcome, "incompatible", `a ${shape} audio manager must be incompatible`);
  assert.equal(probe.facts.manager, false);
}
storeShape = "present";
assert.equal(gate.probe().outcome, "ready");
assert.equal(gate.status().installed, false, "a probe installs nothing");
assert.equal(gate.install().ok, true);
assert.equal(gate.status().held, true, "the standard status shows the claim held");
storeShape = "absent";
assert.equal(gate.probe().outcome, "ready", "an installed gate answers from the manager it holds");
storeShape = "present";
await publishAndDecode({ "navigation.wav": [data], "broken.wav": ["data:audio/wav;base64,AA=="] });
assert.deepEqual(manager.PlayAudioURLWithRepeats("/sounds/navigation.wav", 3), [manager, data, 3]);
assert.equal(manager.PlayAudioURLWithRepeats("/sounds/unknown.wav")[1], "/sounds/unknown.wav");
assert.equal(manager.PlayAudioURLWithRepeats("/sounds/broken.wav")[1], "/sounds/broken.wav");
assert.equal(manager.PlayAudioURLWithRepeats("/voice/navigation.wav")[1], "/voice/navigation.wav");
assert.equal(reports.at(-1).revision, 7);
assert.equal(reports.at(-1).loading, false);
assert.equal(reports.at(-1).resources, 1);
assert.match(reports.at(-1).error, /Unreadable sound/u);
await publishAndDecode({});
assert.equal(
  manager.PlayAudioURLWithRepeats("/sounds/navigation.wav")[1],
  "/sounds/navigation.wav",
);
assert.equal(gate.install().alreadyInstalled, true, "a second install answers alreadyInstalled");

// Any number of resources and variants loads; an entry with an invalid name or a malformed URL is
// rejected on its own and says so, and the valid entries still load.
const many = {};
for (let index = 0; index < 200; index++) many[`sound${index}.wav`] = Array(20).fill(data);
many["bad name.wav"] = [data];
many["malformed.wav"] = ["data:text/plain;base64,AQ=="];
await publishAndDecode(many);
assert.equal(gate.status().resources, 200, "every valid resource loads, however many there are");
assert.equal(manager.PlayAudioURLWithRepeats("/sounds/sound199.wav")[1], data);
assert.match(gate.status().decodeError, /^Rejected sound: /u);
await publishAndDecode({});

assertRemoveRetries(gate, failNext, "sound overrides");
assert.equal(manager.PlayAudioURLWithRepeats, original);
assert.equal(Object.hasOwn(manager, "PlayAudioURLWithRepeats"), false);
assert.equal(gate.status().claims, 0, "removal leaves nothing on the ledger");
assert.equal(gate.remove().absent, true);

// A fresh injection reclaims the owned member rather than wrapping an orphaned closure.
const first = instantiateGate();
first.install();
const replacement = instantiateGate();
replacement.install();
await publishAndDecode({ "navigation.wav": [data] });
assert.equal(manager.PlayAudioURLWithRepeats("/sounds/navigation.wav")[1], data);
replacement.remove();
assert.equal(manager.PlayAudioURLWithRepeats, original);
const unavailable = instantiateGate(true);
unavailable.install();
publish({ sounds: { "navigation.wav": [data] } });
await Promise.resolve();
assert.equal(
  manager.PlayAudioURLWithRepeats("/sounds/navigation.wav")[1],
  "/sounds/navigation.wav",
);
assert.match(unavailable.status().decodeError, /Audio context unavailable/u);
unavailable.remove();
console.log(
  "Sound overrides: exact resources, corrupt fallback, defaults, inherited restoration and orphan reclaim passed.",
);
