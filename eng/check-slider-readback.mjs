// Exercise only the emitted slider echo hook with inert React state.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("const useEchoedValue =");
const end = asset.indexOf("const useTrailingCommit =", start);
assert.ok(start >= 0 && end > start);
const useEcho = new Function(asset.slice(start, end) + "\nreturn useEchoedValue;")();
const slots = [];
let cursor = 0;
const runtime = {
  react: {
    useState(initial) {
      const index = cursor++;
      if (!(index in slots)) slots[index] = initial;
      return [
        slots[index],
        (value) => {
          slots[index] = value;
        },
      ];
    },
  },
};
const render = (observed) => {
  cursor = 0;
  return useEcho(runtime, observed);
};
const writes = [];
const commit = (value) => writes.push(value);

let slider = render(30);
slider.onChange(30);
slider.onChangeComplete(30, commit);
assert.deepEqual(writes, [], "a programmatic refresh must not dispatch a write");
slider = render(17);
slider.onChangeComplete(17, commit);
assert.equal(render(17).value, 17);
assert.deepEqual(writes, [], "AutoTDP readback must not become manual intent");
slider.onChange(19);
assert.equal(render(17).value, 19);
slider.onChangeComplete(19, commit);
assert.deepEqual(writes, [19]);
slider = render(19);
slider.onChangeComplete(19, commit);
slider.onChangeComplete(Number.NaN, commit);
slider.onChangeComplete(Number.POSITIVE_INFINITY, commit);
assert.deepEqual(writes, [19], "acknowledgments and invalid values must not repeat a write");
console.log("Slider readback stays separate from user writes.");
