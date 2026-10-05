// Exercise the emitted storage gate against an inert transport fixture.
//
// Exercise the shared provider selector as well as the claim: unknown exports must never run,
// ambiguity must refuse, unrelated messages keep their arguments and receiver, and removal restores
// Valve's method. This fixture does not establish live Steam compatibility.
import assert from "node:assert/strict";
import {
    assertRemoveRetries,
    failingHost,
    fragment,
    gateSource,
    instantiate,
    loadAsset,
    sharedFragments,
} from "./check-harness.mjs";

const asset = loadAsset();

// Valve's transport: SendMsg lives on the prototype, as it does on the live client.
const forwarded = [];
class Transport {
    SendMsg(name, request, response, options) {
        forwarded.push({ name, request, options, self: this });
        return Promise.resolve({ BSuccess: () => true, Body: () => ({ original: true }) });
    }
}
const { host: transport, failNext } = failingHost(new Transport());
let providerReads = 0;
const provider = {
    GetDefaultTransport: () => {
        providerReads++;
        return transport;
    },
};
// The installed accessor is an ordinary function returning a captured singleton. Neither this
// function nor its webpack binding carries transport tokens; the module does.
function currentProvider() {
    return provider;
}
let unknownCalls = 0;
class UnknownProvider {
    constructor() {
        unknownCalls++;
        throw new Error("unknown constructor invoked");
    }
    GetDefaultTransport() {
        unknownCalls++;
        throw new Error("unknown method invoked");
    }
}
function unknownExport() {
    unknownCalls++;
    throw new Error("unknown export invoked");
}
Object.defineProperty(unknownExport, "toString", { value: () => currentProvider.toString() });
const unrelatedExports = { UnknownProvider, unknownExport };
Object.defineProperty(unrelatedExports, "unsafeBinding", {
    enumerable: true,
    get() {
        unknownCalls++;
        throw new Error("unknown binding invoked");
    },
});
const moduleExports = Object.defineProperties(
    {},
    Object.getOwnPropertyDescriptors(unrelatedExports),
);
Object.defineProperty(moduleExports, "singletonBinding", {
    enumerable: true,
    get: () => currentProvider,
});
moduleExports.alias = currentProvider;

const runtimeFor = (exports, services = 1, transports = 1) => {
    const require = (id) => {
        assert.ok(
            id.startsWith("transport"),
            "only the uniquely identified transport module may load",
        );
        return exports;
    };
    require.m = {};
    for (let i = 0; i < services; i++) {
        require.m[`service${i}`] = new Function("// StorageDeviceManager.IsServiceAvailable#1");
    }
    for (let i = 0; i < transports; i++) {
        require.m[`transport${i}`] = new Function("// GetDefaultTransport m_transport");
    }
    const window = { webpackChunksteamui: { push: (chunk) => chunk[2](require) } };
    return instantiate(
        { window },
        fragment(asset, "module-resolver.ts"),
        'createSteamUiModuleResolver("storage-check")',
    );
};

const requests = [];
const globals = {
    getWebpackRuntime: () => runtimeFor(moduleExports),
    createIconRenderer: () => () => null,
    request: (patchId, command, payload) => {
        requests.push({ command, payload });
        return Promise.resolve();
    },
    subscribe: (patchId, listener) => {
        globals.publish = listener;
        return () => {
            globals.publish = null;
        };
    },
    publish: null,
};

// The gate claims SendMsg with the asset's own ownership primitives, so restoration below is the
// shipped release rather than a stand-in's.
const storageSource = `${sharedFragments(asset)}\n${gateSource(asset, "gates/storage.ts")}`;
const fixtureGate = (exports, services = 1, transports = 1) =>
    instantiate(
        { ...globals, getWebpackRuntime: () => runtimeFor(exports, services, transports) },
        storageSource,
        "createStorageService()",
    );

function anotherProvider() {
    return unresolvedSingleton;
}
const ambiguousExports = Object.defineProperties(
    {},
    Object.getOwnPropertyDescriptors(moduleExports),
);
ambiguousExports.second = anotherProvider;
for (const [exports, count] of [
    [unrelatedExports, 0],
    [ambiguousExports, 2],
]) {
    const refused = fixtureGate(exports);
    assert.equal(refused.install().error, `transport provider export not identified: ${count}`);
    assert.equal(refused.status().claimed, false);
    assert.equal(providerReads, 0, "ambiguity must be refused before invoking any accessor");
    assert.equal(unknownCalls, 0, "unknown exports and getters must never run");
}
for (const count of [0, 2]) {
    const refused = fixtureGate(moduleExports, 1, count);
    assert.equal(
        refused.install().error,
        `transport provider module was not a unique match: ${count}`,
    );
    assert.equal(providerReads, 0);
    const noService = fixtureGate(moduleExports, count);
    assert.equal(noService.install().error, "storage service module was not a unique match");
    assert.equal(providerReads, 0);
}
function emptyProvider() {
    return undefined;
}
const noTransport = fixtureGate({ emptyProvider });
assert.equal(noTransport.install().error, "transport provider has no GetDefaultTransport");
assert.equal(noTransport.status().claimed, false);
assert.equal(providerReads, 0);
assert.equal(unknownCalls, 0);

const gate = fixtureGate(moduleExports);

assert.ok(
    !Object.prototype.hasOwnProperty.call(transport, "SendMsg"),
    "fixture must start unclaimed",
);

const installed = gate.install();
assert.ok(installed.ok, `install failed: ${installed.error}`);
assert.equal(providerReads, 1, "only the identified accessor's provider may be used");
assert.equal(unknownCalls, 0);
assert.ok(gate.install().alreadyInstalled);
assert.equal(providerReads, 1, "an already installed gate must not call the accessor again");
assert.ok(gate.status().claimed, "SendMsg must be claimed");
assert.ok(
    Object.prototype.hasOwnProperty.call(transport, "SendMsg"),
    "the claim must be an own property so removal can delete it",
);

// The gate that decides whether the whole storage UI appears.
const available = await transport.SendMsg(
    "StorageDeviceManager.IsServiceAvailable#1",
    {},
    null,
    {},
);
assert.equal(available.BSuccess(), true);
assert.equal(available.Body().is_available(), true, "the service must report itself available");

// Nothing published yet: an empty state is still a truthful answer, and refusing to answer would
// leave Steam's spinner up forever.
let state = (await transport.SendMsg("StorageDeviceManager.GetState#1", {}, null, {}))
    .Body()
    .toObject().state;
assert.deepEqual(state.drives, []);
assert.deepEqual(state.block_devices, []);
assert.equal(state.is_adopt_supported, false);

globals.publish({
    drives: [
        {
            id: 1,
            model: "Card reader",
            vendor: "Realtek",
            sizeBytes: 64000000000,
            ejectable: true,
            formattable: true,
            unformatted: false,
        },
    ],
    blockDevices: [
        {
            id: 2,
            driveId: 1,
            label: "SDCard12",
            friendlyPath: "D:\\",
            sizeBytes: 63900000000,
            mountPaths: ["D:\\", "D:\\SteamLibrary"],
            hasSteamLibrary: true,
        },
    ],
    adoptSupported: true,
    unmountSupported: true,
    trimSupported: false,
    trimRunning: false,
});
state = (await transport.SendMsg("StorageDeviceManager.GetState#1", {}, null, {}))
    .Body()
    .toObject().state;
// The field names are Steam's own, read off its generated message classes, and every declared field
// is present: the client formats what it is given without checking, so a missing size renders
// "NaN B" and a missing adopt stage a spinner. The idle adopt stage is 1, not 0 (0 is Invalid).
assert.deepEqual(state.drives, [
    {
        id: 1,
        model: "Card reader",
        vendor: "Realtek",
        serial: "",
        is_ejectable: true,
        size_bytes: "64000000000",
        media_type: 0,
        is_unformatted: false,
        adopt_stage: 1,
        is_formattable: true,
        is_media_available: true,
    },
]);
assert.deepEqual(state.block_devices, [
    {
        id: 2,
        drive_id: 1,
        path: "D:\\",
        friendly_path: "D:\\",
        label: "SDCard12",
        size_bytes: "63900000000",
        is_formattable: false,
        is_read_only: false,
        is_root_device: false,
        content_type: 0,
        filesystem_type: 0,
        mount_paths: ["D:\\", "D:\\SteamLibrary"],
        is_unmounting: false,
        has_steam_library: true,
    },
]);
assert.equal(state.is_adopt_supported, true);
assert.equal(state.is_unmount_supported, true);

// Actions are forwarded to the host, never performed here. Requests arrive as Steam's envelope,
// whose Body() holds the message; the ids are Steam's uint32s and the Format Drive modal sends
// Adopt with the typed label and a validate flag.
const envelope = (fields) => ({ Body: () => ({ toObject: () => fields }) });
await transport.SendMsg(
    "StorageDeviceManager.Adopt#1",
    envelope({ drive_id: 1, label: "Games", validate: true }),
    null,
    {},
);
await transport.SendMsg(
    "StorageDeviceManager.Unmount#1",
    envelope({ block_device_id: 2, drive_id: 1 }),
    null,
    {},
);
await transport.SendMsg(
    "StorageDeviceManager.Eject#1",
    envelope({ block_device_id: 2, drive_id: 1 }),
    null,
    {},
);
await transport.SendMsg("StorageDeviceManager.Format#1", envelope({ drive_id: 1 }), null, {});
await transport.SendMsg("StorageDeviceManager.TrimAll#1", envelope({}), null, {});
assert.deepEqual(
    requests.map((r) => r.command),
    ["adopt", "unmount", "eject", "format", "trimall"],
    "every action must reach the host",
);
assert.deepEqual(
    requests[0].payload,
    { driveId: 1, blockDeviceId: 0, label: "Games", validate: true },
    "the adopt request must carry the drive, the typed label and the validate flag",
);
assert.equal(requests[1].payload.blockDeviceId, 2, "unmount must name the volume");
assert.equal(requests[1].payload.driveId, 1);
assert.deepEqual(
    requests[2].payload,
    requests[1].payload,
    "eject must carry the same volume and drive as unmount",
);
assert.deepEqual(requests[3].payload, { driveId: 1, blockDeviceId: 0, label: "", validate: false });

// An unknown storage method fails rather than silently succeeding.
const unknown = await transport.SendMsg("StorageDeviceManager.Nonsense#1", {}, null, {});
assert.equal(unknown.BSuccess(), false, "an unhandled storage method must not report success");

// Everything else is Valve's, untouched: same arguments, same receiver.
assert.equal(forwarded.length, 0, "no storage message may have reached the original");
const other = await transport.SendMsg("Player.GetGameBadgeLevels#1", { a: 1 }, "resp", {
    ePrivilege: 1,
});
assert.equal(other.Body().original, true, "an unrelated message must get Valve's own answer");
assert.equal(forwarded.length, 1);
assert.equal(forwarded[0].name, "Player.GetGameBadgeLevels#1");
assert.deepEqual(forwarded[0].request, { a: 1 });
assert.deepEqual(forwarded[0].options, { ePrivilege: 1 });
assert.equal(forwarded[0].self, transport, "the original must keep its receiver");
assert.equal(gate.status().forwarded, 1);

const removed = gate.remove();
assert.ok(removed.ok, `remove failed: ${removed.error}`);
assert.ok(!gate.status().claimed);
assert.ok(
    !Object.prototype.hasOwnProperty.call(transport, "SendMsg"),
    "removal must delete the own property so the prototype method shows through again",
);
// And the storage message now goes where it always would have: to Valve, which has no service.
await transport.SendMsg("StorageDeviceManager.GetState#1", {}, null, {});
assert.equal(forwarded.length, 2, "after removal nothing may be intercepted");

assert.ok(gate.install().ok, "the gate must be reinstallable");
assertRemoveRetries(gate, failNext, "storage");
assert.ok(
    !Object.prototype.hasOwnProperty.call(transport, "SendMsg"),
    "a retried removal restores SendMsg",
);

// A new bridge must reclaim an existing owned wrapper and retain Valve's original, rather than
// wrapping the previous bridge or restoring its stale handler at removal.
const previousGate = fixtureGate(moduleExports);
assert.ok(previousGate.install().ok);
const previousWrapper = transport.SendMsg;
const readsBeforeReclaim = providerReads;
const replacementGate = fixtureGate(moduleExports);
const reclaimed = replacementGate.install();
assert.ok(reclaimed.ok, `reclaim failed: ${reclaimed.error}`);
assert.equal(reclaimed.reclaimed, true);
assert.equal(providerReads, readsBeforeReclaim + 1);
assert.equal(unknownCalls, 0, "reclaim must not invoke unknown exports or getters");
assert.notEqual(transport.SendMsg, previousWrapper);
assert.equal(transport.SendMsg.__steamUiStorageOriginal.value, Transport.prototype.SendMsg);
assert.equal(replacementGate.status().claimed, true);
assert.equal(
    (await transport.SendMsg("StorageDeviceManager.IsServiceAvailable#1", {}, null, {}))
        .Body()
        .is_available(),
    true,
);
const forwardedBeforeReclaim = forwarded.length;
await transport.SendMsg("Player.GetGameBadgeLevels#1", {}, null, {});
assert.equal(
    forwarded.length,
    forwardedBeforeReclaim + 1,
    "reclaim must forward through Valve's original exactly once",
);
assert.equal(
    previousGate.status().forwarded,
    0,
    "the replacement must not call the previous wrapper",
);
assert.ok(replacementGate.remove().ok);
assert.equal(transport.SendMsg, Transport.prototype.SendMsg);
assert.equal(Object.prototype.hasOwnProperty.call(transport, "SendMsg"), false);
assert.ok(previousGate.remove().ok);
assert.equal(unknownCalls, 0);

console.log(
    "Storage service: availability, state shape, action forwarding, pass-through and restoration passed.",
);
