import assert from "node:assert/strict";
import { gateSource, instantiate, loadAsset, sharedFragments } from "./check-harness.mjs";

// The Screensaver service stub as Stable and Beta build it: GetActiveState answers a protobuf
// message whose Body().active() is the state, and ForceScreensaver({enabled}) is the call the saver
// view makes on input (sp.js `Ju(){...ForceScreensaver({enabled:!1})}`).
let active = true;
const forced = [];
const service = {
    GetActiveState: async () => ({ Body: () => ({ active: () => active }) }),
    ForceScreensaver: async (options) => {
        forced.push(options);
        if (options?.enabled === false) active = false;
    },
};
const tokens = ["Screensaver.GetActiveState#1", "Screensaver.ForceScreensaver#1"];
let matches = 1;
const runtime = () => assert.fail("a module is loaded only through exported()");
runtime.count = (asked) => (asked.join() === tokens.join() ? matches : 0);
runtime.exported = (asked, predicate) => {
    assert.deepEqual(asked, tokens);
    assert.equal(
        predicate({ GetActiveState() {} }),
        false,
        "a stub without ForceScreensaver is not the service",
    );
    return predicate(service) ? service : null;
};
const gate = instantiate(
    { window: {}, getWebpackRuntime: () => runtime, registerGate() {}, subscribe: () => () => {} },
    `${sharedFragments(loadAsset())}\n${gateSource(loadAsset(), "gates/screensaver-dismissal.ts")}`,
    "createScreensaverDismissal()",
);

assert.deepEqual(
    await gate.dismiss(),
    { dismissed: false, active: null },
    "an uninstalled gate does nothing",
);
assert.equal(forced.length, 0);
assert.equal(gate.probe().outcome, "ready");
assert.equal(gate.install().ok, true);
assert.deepEqual(
    await gate.dismiss(),
    { dismissed: true, active: true },
    "a showing saver is dismissed",
);
assert.deepEqual(forced, [{ enabled: false }], "the dismissal is Steam's own input call");
assert.deepEqual(
    await gate.dismiss(),
    { dismissed: true, active: false },
    "an idle saver is left as it is",
);
assert.equal(gate.status().dismissals, 2);

service.GetActiveState = async () => {
    throw new Error("no state");
};
assert.deepEqual(
    await gate.dismiss(),
    { dismissed: true, active: null },
    "an unreadable state still dismisses",
);
const failing = service.ForceScreensaver;
service.ForceScreensaver = async () => {
    throw new Error("refused");
};
assert.deepEqual(
    await gate.dismiss(),
    { dismissed: false, active: null },
    "a refused dismissal is reported",
);
service.ForceScreensaver = failing;

assert.equal(gate.remove().ok, true);
forced.length = 0;
assert.deepEqual(
    await gate.dismiss(),
    { dismissed: false, active: null },
    "a removed gate does nothing",
);
assert.equal(forced.length, 0);
matches = 2;
assert.equal(gate.probe().outcome, "incompatible", "two Screensaver services are not one match");
console.log(
    "Screensaver dismissal: fingerprinted service, Steam's own ForceScreensaver(false), state report, failures and removal passed.",
);
