// Exercise the emitted storage gate against an inert transport fixture.
//
// The risk here is not resolution — that is checked live and pinned by the probe — but what the
// claim does to a method that carries ALL of Steam's service traffic. So the assertions that matter
// are: unrelated messages reach the original untouched with their arguments and `this` intact, the
// storage answers have the shape Steam's own hooks destructure, and removal puts Valve's method
// back so nothing is left in the path.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const asset = readFileSync(process.argv[2] ?? "dist/prelude.js", "utf8");
const start = asset.indexOf("function createStorageService()");
assert.ok(start >= 0, "the emitted asset must contain the storage gate");
const end = asset.indexOf('registerGate("storage"', start);
assert.ok(end > start, "the storage gate must register itself");

// Valve's transport: SendMsg lives on the prototype, as it does on the live client.
const forwarded = [];
class Transport {
  SendMsg(name, request, response, options) {
    forwarded.push({ name, request, options, self: this });
    return Promise.resolve({ BSuccess: () => true, Body: () => ({ original: true }) });
  }
}
const transport = new Transport();
const provider = { GetDefaultTransport: () => transport };

const requests = [];
const globals = {
  getWebpackRuntime: () => {
    const require = (id) => (id === "transport" ? { OI: () => provider } : {});
    require.findUnique = (tokens) =>
      tokens.includes("GetDefaultTransport")
        ? ["transport"]
        : tokens.includes("StorageDeviceManager.IsServiceAvailable#1")
          ? ["service"]
          : null;
    require.count = () => 1;
    return require;
  },
  createIconRenderer: () => () => null,
  request: (patchId, command, payload) => {
    requests.push({ command, payload });
    return Promise.resolve();
  },
  claimMember: (host, member, keys, replacement) => {
    const original = host[member];
    const next = replacement(original);
    Object.defineProperty(next, keys.marker, { value: true, configurable: true });
    Object.defineProperty(next, keys.original, { value: original, configurable: true });
    Object.defineProperty(host, member, { value: next, writable: true, configurable: true });
    return { ok: true, reclaimed: false };
  },
  releaseMember: (host, member, keys) => {
    if (Object.prototype.hasOwnProperty.call(host, member) && host[member]?.[keys.marker]) {
      delete host[member];
    }
    return { ok: true };
  },
  memberClaimed: (host, member, keys) => host?.[member]?.[keys.marker] === true,
  subscribe: (patchId, listener) => {
    globals.publish = listener;
    return () => {
      globals.publish = null;
    };
  },
  publish: null,
};

const gate = new Function(
  ...Object.keys(globals),
  asset.slice(start, end) + "\nreturn createStorageService();",
)(...Object.values(globals));

assert.ok(!Object.prototype.hasOwnProperty.call(transport, "SendMsg"), "fixture must start unclaimed");

const installed = gate.install();
assert.ok(installed.ok, `install failed: ${installed.error}`);
assert.ok(gate.status().claimed, "SendMsg must be claimed");
assert.ok(
  Object.prototype.hasOwnProperty.call(transport, "SendMsg"),
  "the claim must be an own property so removal can delete it",
);

// The gate that decides whether the whole storage UI appears.
const available = await transport.SendMsg("StorageDeviceManager.IsServiceAvailable#1", {}, null, {});
assert.equal(available.BSuccess(), true);
assert.equal(available.Body().is_available(), true, "the service must report itself available");

// Nothing published yet: an empty state is still a truthful answer, and refusing to answer would
// leave Steam's spinner up forever.
let state = (await transport.SendMsg("StorageDeviceManager.GetState#1", {}, null, {})).Body().toObject().state;
assert.deepEqual(state.drives, []);
assert.deepEqual(state.block_devices, []);
assert.equal(state.is_adopt_supported, false);

globals.publish({
  drives: [{ id: "disk0", formattable: true, unformatted: false }],
  blockDevices: [
    { id: "vol0", driveId: "disk0", mountPaths: ["D:\\"], hasSteamLibrary: true },
  ],
  adoptSupported: true,
  unmountSupported: true,
  trimSupported: false,
  trimRunning: false,
});
state = (await transport.SendMsg("StorageDeviceManager.GetState#1", {}, null, {})).Body().toObject().state;
// The field names are Steam's own, read off its generated message classes. A rename here shows up
// as an empty page rather than an error, which is why they are pinned.
assert.deepEqual(state.drives, [{ id: "disk0", is_formattable: true, is_unformatted: false }]);
assert.deepEqual(state.block_devices, [
  { block_device_id: "vol0", drive_id: "disk0", mount_paths: ["D:\\"], has_steam_library: true },
]);
assert.equal(state.is_adopt_supported, true);
assert.equal(state.is_unmount_supported, true);

// Actions are forwarded to the host, never performed here.
await transport.SendMsg("StorageDeviceManager.Adopt#1", { toObject: () => ({ drive_id: "disk0" }) }, null, {});
await transport.SendMsg(
  "StorageDeviceManager.Unmount#1",
  { toObject: () => ({ block_device_id: "vol0", drive_id: "disk0" }) },
  null,
  {},
);
await transport.SendMsg("StorageDeviceManager.TrimAll#1", {}, null, {});
assert.deepEqual(
  requests.map((r) => r.command),
  ["adopt", "unmount", "trimall"],
  "every action must reach the host",
);
assert.equal(requests[0].payload.driveId, "disk0", "the drive id must survive the request decode");
assert.equal(requests[1].payload.blockDeviceId, "vol0");

// An unknown storage method fails rather than silently succeeding.
const unknown = await transport.SendMsg("StorageDeviceManager.Nonsense#1", {}, null, {});
assert.equal(unknown.BSuccess(), false, "an unhandled storage method must not report success");

// Everything else is Valve's, untouched: same arguments, same receiver.
assert.equal(forwarded.length, 0, "no storage message may have reached the original");
const other = await transport.SendMsg("Player.GetGameBadgeLevels#1", { a: 1 }, "resp", { ePrivilege: 1 });
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
assert.ok(gate.remove().ok);

console.log("Storage service: availability, state shape, action forwarding, pass-through and restoration passed.");
