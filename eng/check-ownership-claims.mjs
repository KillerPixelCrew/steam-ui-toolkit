// Runs the ownership primitives out of the SHIPPED asset against the scenarios that cost device
// sessions: reclaiming a previous bridge's work instead of tearing it down, and handing back
// exactly what was displaced.
//
// The primitives are extracted from the generated JavaScript rather than the TypeScript source, so
// this tests the bytes that are actually injected. It caught a real defect the day it was written:
// `typeof next === "object"` excluded FUNCTIONS, and every member claim replaces a method — so the
// marker was never written, the release found nothing of ours, and an overlaid method outlived its
// own removal. Four of these checks fail against that version.
//
// Node built-ins only, so it runs in an offline release build with no node_modules.
//
//   node eng/check-ownership-claims.mjs [asset path]
import assert from "node:assert/strict";
import { loadAsset, sharedFragments } from "./check-harness.mjs";

// The primitives with the RPC replies and gate helpers, as whole fragments and no gate: gates
// register themselves with a top-level call, and evaluating one here would fail on a `registerGate`
// that only exists inside the real bridge.
const primitives = sharedFragments(loadAsset());

const harness = `
${primitives}
return { claimValue, releaseValue, claimMember, releaseMember, memberClaimed, claimAccessor, releaseAccessor, supplyNamespace, withdrawNamespace, claimed };
`;
const api = new Function(harness)();

// --- value claim: the brightness availability flag -------------------------------------------
const keys = { marker: "__mark", original: "__orig" };
{
  const host = { flag: false };
  const first = api.claimValue(host, "flag", keys, true);
  assert.ok(first.ok && host.flag === true, "value: claims a hidden flag");
  assert.ok(!Object.keys(host).includes("__mark"), "value: marker is not enumerable");

  // The teardown trap: a second bridge sees its predecessor's work and must reclaim, not refuse.
  const second = api.claimValue(host, "flag", keys, true);
  assert.ok(second.ok && second.reclaimed, "value: reclaims its own work rather than refusing");

  api.releaseValue(host, "flag", keys);
  assert.ok(host.flag === false, "value: restores the original");
  assert.ok(!("__mark" in host) && !("__orig" in host), "value: removes its markers");
}
{
  // A client that already reports available needs nothing; claiming would invent an original.
  const host = { flag: true };
  const outcome = api.claimValue(host, "flag", keys, true);
  assert.ok(!outcome.ok, "value: stands aside when the client already set it");
}
{
  const proto = { flag: false };
  const host = Object.create(proto);
  const outcome = api.claimValue(host, "flag", keys, true);
  assert.ok(outcome.ok && host.flag === true, "value: claims an inherited field");
  api.releaseValue(host, "flag", keys);
  assert.ok(
    host.flag === false && !Object.hasOwn(host, "flag"),
    "value: release reveals the inherited field without leaving a shadow",
  );
}
{
  const host = {};
  Object.defineProperty(host, "flag", {
    value: undefined,
    configurable: true,
    enumerable: false,
    writable: true,
  });
  const before = Object.getOwnPropertyDescriptor(host, "flag");
  api.claimValue(host, "flag", keys, true);
  api.releaseValue(host, "flag", keys);
  const after = Object.getOwnPropertyDescriptor(host, "flag");
  assert.ok(
    Object.hasOwn(host, "flag") &&
      after.value === undefined &&
      after.enumerable === before.enumerable &&
      after.configurable === before.configurable &&
      after.writable === before.writable,
    "value: restores an own undefined field and its exact descriptor",
  );
}
{
  // An accessor-backed field, shaped like a MobX observable: the value lives in the store's own
  // map and the property is a getter/setter pair over it. Redefining or deleting that accessor
  // destroys the map entry while leaving the getter behind, so every later read throws — the
  // Quick Access Menu crash of 2026-09-01. The claim must go through the setter, and so must
  // the rollback of a claim that failed.
  const values = new Map([["flag", false]]);
  const host = {};
  Object.defineProperty(host, "flag", {
    get() {
      return values.get("flag").valueOf();
    },
    set(v) {
      values.set("flag", v);
    },
    configurable: true,
    enumerable: true,
  });
  const before = Object.getOwnPropertyDescriptor(host, "flag");
  const claim = api.claimValue(host, "flag", keys, true);
  assert.ok(
    claim.ok && host.flag === true,
    "value: claims an accessor-backed field through its setter",
  );
  const during = Object.getOwnPropertyDescriptor(host, "flag");
  assert.ok(
    during.get === before.get && during.set === before.set,
    "value: the accessor survives the claim",
  );
  api.releaseValue(host, "flag", keys);
  const after = Object.getOwnPropertyDescriptor(host, "flag");
  let readable = true;
  try {
    readable = host.flag === false;
  } catch {
    readable = false;
  }
  assert.ok(
    readable &&
      after.get === before.get &&
      after.set === before.set &&
      values.get("flag") === false,
    "value: release hands the value back and leaves the accessor readable",
  );
}
{
  // A read-only accessor cannot be claimed; the refusal must leave it exactly as found.
  const host = {};
  Object.defineProperty(host, "flag", { get: () => false, configurable: true });
  const outcome = api.claimValue(host, "flag", keys, true);
  const after = Object.getOwnPropertyDescriptor(host, "flag");
  assert.ok(
    !outcome.ok && typeof after.get === "function" && host.flag === false && !("__mark" in host),
    "value: refuses a read-only accessor without touching it",
  );
}

// --- member claim: an overlaid METHOD ---------------------------------------------------------
{
  let called = 0;
  const host = {
    Set: (v) => {
      called = v;
      return "native";
    },
  };
  const native = host.Set;
  const claim = api.claimMember(host, "Set", keys, () => (v) => {
    called = v * 2;
    return "ours";
  });
  assert.ok(claim.ok, "member: claims a function");
  assert.ok(api.memberClaimed(host, "Set", keys), "member: marks the replacement");
  host.Set(5);
  assert.ok(called === 10, "member: the overlay runs");

  api.releaseMember(host, "Set", keys);
  assert.ok(host.Set === native, "member: restores the native method");
  host.Set(5);
  assert.ok(called === 5, "member: the native method runs after release");
}
{
  // A wrap that calls through, reclaimed by a second bridge: must not stack.
  const order = [];
  const host = { Go: () => order.push("native") };
  const wrapOnce = (original) => () => {
    order.push("wrap");
    return original();
  };
  api.claimMember(host, "Go", keys, wrapOnce);
  api.claimMember(host, "Go", keys, wrapOnce);
  host.Go();
  assert.ok(order.join(",") === "wrap,native", "member: reclaim replaces rather than stacking");
}
{
  const proto = { Go: () => "native" };
  const host = Object.create(proto);
  api.claimMember(host, "Go", keys, () => () => "ours");
  api.releaseMember(host, "Go", keys);
  assert.ok(
    host.Go() === "native" && !Object.hasOwn(host, "Go"),
    "member: release reveals an inherited method without leaving a shadow",
  );
}
{
  const host = {};
  Object.defineProperty(host, "Maybe", {
    value: undefined,
    configurable: true,
    enumerable: false,
    writable: true,
  });
  api.claimMember(host, "Maybe", keys, () => () => "ours");
  api.releaseMember(host, "Maybe", keys);
  const descriptor = Object.getOwnPropertyDescriptor(host, "Maybe");
  assert.ok(
    Object.hasOwn(host, "Maybe") &&
      descriptor.value === undefined &&
      descriptor.enumerable === false,
    "member: restores an own undefined member instead of deleting it",
  );
}

// --- supplied namespace: the Perf and audio backends the client does not have -------------------
{
  const marker = "__ns";
  const system = {};
  const supplied = api.supplyNamespace(system, "Audio", marker, () => ({ GetDevices: () => 1 }));
  assert.ok(supplied.ok && !!system.Audio, "namespace: supplies one where the client has none");
  assert.ok(!Object.keys(system.Audio).includes(marker), "namespace: marker is not enumerable");

  // The trap this one paid for: an orphaned namespace outlives the bridge behind it, because the
  // bridge dies with the JS context and SteamClient does not. Refusing here stranded the client.
  const second = api.supplyNamespace(system, "Audio", marker, () => ({ GetDevices: () => 2 }));
  assert.ok(second.ok && second.reclaimed, "namespace: reclaims its own orphan rather than refusing");
  assert.ok(system.Audio.GetDevices() === 2, "namespace: the reclaim actually replaced it");

  api.withdrawNamespace(system, "Audio", marker);
  assert.ok(!("Audio" in system), "namespace: withdrawal deletes it");
}
{
  // A client that grows a real backend must not be shadowed, nor deleted by our cleanup.
  const real = { GetDevices: () => "real" };
  const system = { Audio: real };
  const outcome = api.supplyNamespace(system, "Audio", "__ns", () => ({}));
  assert.ok(!outcome.ok, "namespace: stands aside for a real backend");
  api.withdrawNamespace(system, "Audio", "__ns");
  assert.ok(system.Audio === real, "namespace: withdrawal leaves a real backend alone");
}
{
  // Reclaim against a previous bridge's non-writable definition. Assignment would throw here under
  // "use strict", which is why the primitive defines rather than assigns.
  const marker = "__ns";
  const system = {};
  api.supplyNamespace(system, "Perf", marker, () => ({ n: 1 }));
  const descriptor = Object.getOwnPropertyDescriptor(system, "Perf");
  assert.ok(
    !descriptor.writable,
    "namespace: defined non-writable, as a previous bridge would leave it",
  );
  const again = api.supplyNamespace(system, "Perf", marker, () => ({ n: 2 }));
  assert.ok(again.ok && system.Perf.n === 2, "namespace: reclaims a non-writable definition");
}

// --- accessor claim: the network availability getter -------------------------------------------
{
  const proto = {};
  Object.defineProperty(proto, "avail", { get: () => false, configurable: true });
  const claim = api.claimAccessor(proto, "avail", keys, () => true);
  assert.ok(claim.ok && proto.avail === true, "accessor: claims a prototype getter");

  const second = api.claimAccessor(proto, "avail", keys, () => true);
  assert.ok(second.ok && second.reclaimed, "accessor: reclaims its own work");

  api.releaseAccessor(proto, "avail", keys);
  assert.ok(proto.avail === false, "accessor: restores the original getter");
}
{
  const locked = {};
  Object.defineProperty(locked, "avail", { get: () => false, configurable: false });
  const outcome = api.claimAccessor(locked, "avail", keys, () => true);
  assert.ok(!outcome.ok, "accessor: stands aside on a non-configurable property");
}

console.log("Ownership claims: every claim, reclaim, release and stand-aside scenario passed.");
