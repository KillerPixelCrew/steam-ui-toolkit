// Offline fixtures over the emitted gate and the real member-ownership primitives.
import assert from "node:assert/strict";
import { gateSource, instantiate, loadAsset, slice } from "./check-harness.mjs";

const asset = loadAsset();
const ownership = slice(asset, "const defineHidden", "const supplyNamespace");
const code = gateSource(asset, "createSoundOverrides", "soundOverrides");
const original = function (url, ...args) {
  return [this, url, ...args];
};
const prototype = { PlayAudioURLWithRepeats: original };
const manager = Object.create(prototype);
let publish;
let decoded;
const instantiateGate = (failContext = false) =>
  instantiate(
    {
      window: { location: { href: "about:blank" } },
      getWebpackRuntime: () => ({
        exported: () => ({ GamepadUIAudio: { AudioPlaybackManager: manager } }),
      }),
      subscribe: (_, callback) => {
        publish = callback;
        return () => {};
      },
      endSubscription: (value) => {
        value?.();
        return null;
      },
      AudioContext: class {
        constructor() {
          if (failContext) throw new Error("Audio context unavailable");
        }
        async decodeAudioData(bytes) {
          if (new Uint8Array(bytes)[0] === 0) throw new Error("corrupt");
        }
        async close() {
          decoded?.();
        }
      },
      fetch: async (url) => ({
        arrayBuffer: async () => Uint8Array.from(Buffer.from(url.split(",")[1], "base64")).buffer,
      }),
    },
    ownership + code,
    "createSoundOverrides()",
  );

const data = "data:audio/wav;base64,AQ==";
const publishAndDecode = (sounds) =>
  new Promise((resolve) => {
    decoded = resolve;
    publish({ sounds });
  });
const gate = instantiateGate();
assert.equal(gate.install().ok, true);
await publishAndDecode({ "navigation.wav": [data], "broken.wav": ["data:audio/wav;base64,AA=="] });
assert.deepEqual(manager.PlayAudioURLWithRepeats("/sounds/navigation.wav", 3), [manager, data, 3]);
assert.equal(manager.PlayAudioURLWithRepeats("/sounds/unknown.wav")[1], "/sounds/unknown.wav");
assert.equal(manager.PlayAudioURLWithRepeats("/sounds/broken.wav")[1], "/sounds/broken.wav");
assert.equal(manager.PlayAudioURLWithRepeats("/voice/navigation.wav")[1], "/voice/navigation.wav");
await publishAndDecode({});
assert.equal(
  manager.PlayAudioURLWithRepeats("/sounds/navigation.wav")[1],
  "/sounds/navigation.wav",
);
assert.equal(gate.remove().ok, true);
assert.equal(manager.PlayAudioURLWithRepeats, original);
assert.equal(Object.hasOwn(manager, "PlayAudioURLWithRepeats"), false);
assert.equal(gate.remove().ok, true);

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
assert.match(unavailable.status().lastError, /Audio context unavailable/u);
unavailable.remove();
console.log(
  "Sound overrides: exact resources, corrupt fallback, defaults, inherited restoration and orphan reclaim passed.",
);
