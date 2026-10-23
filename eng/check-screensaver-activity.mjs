import assert from "node:assert/strict";
import { instantiate, loadAsset, sharedFragments } from "./check-harness.mjs";

const notice = "Screensaver.NotifyActiveStateChanged#1";
class Packet {
    constructor(value) {
        this.value = value;
    }
}
class Message {
    constructor(value) {
        this.value = { ...value };
        this.m_netPacket = new Packet(value);
    }
    Body() {
        return {
            active: () => this.value.active,
            set_active: (value) => {
                this.value.active = value;
            },
        };
    }
    Hdr() {
        return {
            target_job_name: () => this.value.topic,
            set_target_job_name: (value) => {
                this.value.topic = value;
            },
        };
    }
    Serialize() {
        return this.value;
    }
}
const proto = {
    InitFromMsg: (_type, message) => new Message(message.m_netPacket.value),
    InitFromObject: (_type, value) => new Message(value),
    InitFromPacket: (_type, packet) => new Message(packet.value),
};
const delivered = [];
const handlers = [
    { invoke: (message) => delivered.push(proto.InitFromMsg(null, message).Body().active()) },
];
const registry = {
    m_mapServiceMethodHandlers: new Map([[notice, handlers]]),
    DispatchMsgToHandlers(message) {
        for (const handler of this.m_mapServiceMethodHandlers.get(
            message.Hdr().target_job_name(),
        ) ?? [])
            handler.invoke(message);
    },
};
let rawActive = false;
const forces = [];
const native = {
    NotifyActiveStateChangedHandler: { request: class {} },
    GetActiveState: async () => new Message({ active: rawActive }),
    ForceScreensaver: async ({ enabled }) => {
        forces.push(enabled);
    },
};
const originalDispatch = registry.DispatchMsgToHandlers;
const originalGet = native.GetActiveState;
const originalForce = native.ForceScreensaver;
const timers = new Map();
let timerId = 0;
let now = 10000;
const settings = { system_idle_screensaver_ac_sec: 300, system_idle_screensaver_battery_sec: 600 };
const runtime = {
    exported(tokens, predicate) {
        const values = tokens[0].startsWith("Screensaver.")
            ? [native]
            : tokens[0].startsWith("Multiple attempts")
              ? [() => ({ GetDefaultHandlerRegistry: () => registry })]
              : [proto];
        const fits = values.filter(predicate);
        assert.equal(fits.length, 1);
        return fits[0];
    },
};
const asset = loadAsset();
const activity = instantiate(
    {
        performance: { now: () => now },
        Date: { now: () => now },
        setTimeout: (callback) => {
            const id = ++timerId;
            timers.set(id, () => {
                timers.delete(id);
                callback();
            });
            return id;
        },
        clearTimeout: (id) => timers.delete(id),
        settings,
    },
    sharedFragments(asset),
    "createScreensaverActivity(() => settings)",
);
assert.ok(activity.start(runtime).ok);
await Promise.resolve();
await Promise.resolve();
const receive = (active, topic = notice) => {
    rawActive = active;
    registry.DispatchMsgToHandlers(new Message({ active, topic }));
};
activity.observe({ revision: 1, ageMilliseconds: 100, onBattery: false });
receive(true);
assert.equal(
    delivered.at(-1),
    false,
    "recent real Windows input prevents native controller-idle activation",
);
assert.deepEqual(
    forces,
    [],
    "automatic input reconciliation must produce no controller input or native forced-preview spam",
);
activity.observe({ revision: 1, ageMilliseconds: 300000, onBattery: false });
assert.equal(delivered.at(-1), true, "genuine idleness still starts the screensaver");
activity.observe({ revision: 2, ageMilliseconds: 0, onBattery: false });
assert.equal(
    delivered.at(-1),
    false,
    "keyboard/mouse activity dismisses an already active screensaver",
);
await native.ForceScreensaver({ enabled: true });
assert.equal(
    delivered.at(-1),
    true,
    "Preview must open even when native activation was already true but suppressed",
);
await native.ForceScreensaver({ enabled: false });
assert.equal(
    delivered.at(-1),
    false,
    "ending Preview restores the real session idle decision without a new native event",
);
activity.observe({ revision: 2, ageMilliseconds: 400000, onBattery: true });
assert.equal(delivered.at(-1), false, "the current battery timeout applies");
activity.observe({ revision: 2, ageMilliseconds: 600000, onBattery: true });
assert.equal(delivered.at(-1), true);
receive(false);
assert.equal(delivered.at(-1), false, "controller/touch native dismissal still wins");
receive(true);
await native.ForceScreensaver({ enabled: true });
activity.observe({ revision: 3, ageMilliseconds: 1, onBattery: false });
assert.equal(delivered.at(-1), true, "the click which starts Preview does not dismiss Preview");
now += 1000;
activity.observe({ revision: 4, ageMilliseconds: 0, onBattery: false });
assert.equal(delivered.at(-1), false);
assert.deepEqual(
    forces,
    [true, false, true, false],
    "later real input ends explicit forced preview once",
);
const unrelated = [];
registry.m_mapServiceMethodHandlers.set("Unrelated", [
    { invoke: (message) => unrelated.push(message.Body().active()) },
]);
receive(true, "Unrelated");
assert.deepEqual(unrelated, [true]);
activity.observe({ revision: 5, ageMilliseconds: 0, onBattery: false });
const expire = [...timers.values()].at(-1);
now += 6000;
expire();
assert.equal(delivered.at(-1), true, "a lost host observation restores native behavior");
activity.observe({ revision: 6, ageMilliseconds: 0, onBattery: false });
assert.ok(activity.stop().ok);
assert.equal(
    delivered.at(-1),
    true,
    "removal restores actual native state to existing subscribers",
);
assert.equal(timers.size, 0);
assert.equal(registry.DispatchMsgToHandlers, originalDispatch);
assert.equal(native.GetActiveState, originalGet);
assert.equal(native.ForceScreensaver, originalForce);
assert.ok(activity.start(runtime).ok);
assert.ok(activity.stop().ok);
console.log(
    "Screensaver activity: native and session idle inputs merged; preview, power source, expiry, cleanup and reinstallation checked.",
);
