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
const activation = [];
const overlays = [{ NavigateToRunningApp: () => activation.push("navigate") }];
const window = {
    __steamUiRunningApps: { live: true, ids: new Set([3405340, 4000000000]) },
    SteamUIStore: {
        SetRunningApp: (id) => activation.push(["select", id]),
        WindowStore: { GetOverlayInstances: () => overlays },
    },
    appStore: {
        GetAppOverviewByAppID: (id) => ({
            GetGameID: () => (id === 4000000000 ? "17179869184000000000" : String(id)),
        }),
    },
    SteamClient: { Apps: { RaiseWindowForGame: (id) => activation.push(["raise", id]) } },
};
const gate = instantiate(
    { window, getWebpackRuntime: () => runtime, registerGate() {}, subscribe: () => () => {} },
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

for (const id of [3405340, 4000000000]) {
    activation.length = 0;
    const result = await gate.dismiss(id);
    assert.equal(result.gameRestored, true);
    assert.deepEqual(
        activation,
        [
            ["select", id],
            "navigate",
            ["raise", id === 4000000000 ? "17179869184000000000" : String(id)],
        ],
        "wake uses Continue's activation order and preserves the full shortcut game ID",
    );
}
activation.length = 0;
assert.match((await gate.dismiss(123)).gameError, /positive Steam lifetime/);
assert.deepEqual(activation, [], "an exited or unknown game is never activated");
overlays.length = 0;
assert.match((await gate.dismiss(3405340)).gameError, /not ready/);
assert.deepEqual(activation, [], "missing overlay windows must not cause partial activation");
overlays.push({
    NavigateToRunningApp: () => {
        activation.push("failed navigation");
        throw new Error("navigation failed");
    },
});
assert.match((await gate.dismiss(3405340)).gameError, /navigation failed/);
assert.deepEqual(
    activation,
    [["select", 3405340], "failed navigation"],
    "partial activation is not retried or raised",
);

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
