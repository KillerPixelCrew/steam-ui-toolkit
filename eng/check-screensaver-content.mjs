import assert from "node:assert/strict";
import { instantiate, loadAsset, sharedFragments, substitute } from "./check-harness.mjs";

const settings = { screensaver_current_id: "" };
const stock = [
    { id: () => "steam-bouncinglogo", path: () => "steam-bouncinglogo.steamscreensavers.host" },
];
const body = { screensavers: () => stock, untouched: () => 7 };
const response = { Body: () => body, untouched: () => 8 };
const native = {
    GetLocalScreensavers: async () => response,
    ForceScreensaver: async (value) => forces.push(value.enabled),
};
const original = native.GetLocalScreensavers;
const forces = [];
const commands = [];
const writes = [];
let now = 0;
const setSetting = function (key, value) {
    writes.push([key, value]);
};
setSetting.toString = () => "SteamClient.Settings.SetSetting serializeBinaryToWriter";
const runtime = {
    exported: (tokens, predicate) => {
        const value = tokens.includes("Screensaver.GetActiveState#1") ? native : setSetting;
        assert.ok(predicate(value));
        return value;
    },
};
const source = {
    id: "local",
    selectorId: "uioverride-WSGM%3A%20Test",
    name: "Test",
    folder: "D:/Pictures",
    type: "images",
    available: true,
    detail: "2 images",
    shuffle: true,
    fill: false,
    intervalSeconds: 15,
    framesPerSecond: 30,
};
const state = {
    sources: [source],
    selectedId: "",
    selectionRequest: null,
    selectionRevision: 0,
    dismissRevision: 0,
};
const content = instantiate(
    {
        window: {},
        request: async (_patch, command, payload) => {
            assert.equal(command, "content");
            commands.push(payload);
        },
        fixtureComponents: () => null,
        performance: { now: () => now },
        settings,
    },
    substitute(sharedFragments(loadAsset()), "resolveSteamUiComponents", "fixtureComponents"),
    "createScreensaverContent(()=>settings)",
);
assert.equal(content.start(runtime).ok, true);
content.update(state);
assert.equal(commands.at(-1).selectorId, "steam-gameslideshow");
const projected = await native.GetLocalScreensavers({});
assert.equal(projected.untouched(), 8);
assert.equal(projected.Body().untouched(), 7);
assert.equal(projected.Body().screensavers().length, 2);
assert.equal(projected.Body().screensavers()[0], stock[0]);
assert.equal(projected.Body().screensavers()[1].id(), source.selectorId);
assert.equal(projected.Body().screensavers()[1].path(), stock[0].path());
assert.equal(response.Body().screensavers().length, 1);
content.update({
    ...state,
    selectionRequest: source.selectorId,
    selectionRevision: 1,
    previewSelection: true,
});
assert.deepEqual(writes, [["screensaver_current_id", source.selectorId]]);
content.update({
    ...state,
    selectionRequest: source.selectorId,
    selectionRevision: 1,
    previewSelection: true,
});
assert.equal(writes.length, 1);
settings.screensaver_current_id = source.selectorId;
content.update(state);
await Promise.resolve();
assert.deepEqual(forces, [true]);
assert.ok(
    commands.some(
        (command) =>
            command.action === "selectionResult" &&
            command.revision === 1 &&
            command.error === null,
    ),
);
content.active(true);
assert.equal(commands.at(-1).active, true);
assert.equal(commands.at(-1).id, "local");
content.update({ ...state, dismissRevision: 1 });
assert.deepEqual(forces, [true, false]);
content.update({ ...state, selectionRequest: "steam-bouncinglogo", selectionRevision: 2 });
now = 6000;
content.update(state);
await Promise.resolve();
assert.ok(
    commands.some(
        (command) =>
            command.action === "selectionResult" && command.revision === 2 && command.error,
    ),
);
assert.equal(content.stop().ok, true);
assert.equal(native.GetLocalScreensavers, original);
assert.equal(commands.at(-1).active, false);
assert.equal(await native.GetLocalScreensavers({}), response);
console.log(
    "Screensaver content: stock chooser preserved, sources projected, native setting acknowledged once, preview/timeout/dismissal and cleanup checked.",
);
