// Bluetooth is a WebUI transport service whose backend does not exist on Windows. The service,
// its message shapes and every operation are present — GetState round-trips and answers
// is_service_available:false with empty adapters and devices — so the host replaces the stub's
// methods rather than implementing the service. `*Handler` exports are message descriptors,
// not registration hooks, so implementing it is not on offer.
//
// The second gate matters here as much as the first: availability is read through react-query
// with staleTime Infinity, so replacing the methods changes nothing until that cache is
// invalidated. Live-verified 2026-08-30 that the stub's methods are writable and configurable and
// that the query client's invalidateQueries is reachable.
//
// The stub was module 60517, export RF, when verified. The September 2026 beta renumbered the
// module, so it is found by its service method name and by its shape.
function createBluetoothService() {
  const patchId = "steam-ui.bluetooth";
  const queryKey = ["BluetoothManagerService", "State"];
  const methodKeys = {
    marker: "__steamUiOwnedBluetoothService",
    original: "__steamUiOriginalBluetoothServiceMethod",
  } as const;
  const replaced = new Set<string>();
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
  // and the host only carries them through from the state it was given.
  let latest: {
    is_service_available: boolean;
    adapters: any[];
    devices: any[];
  } = { is_service_available: false, adapters: [], devices: [] };

  // Resolved once, at install. Every state push invalidates through the same resolver, and removal
  // hands the methods back on the stub they were claimed on.
  let req: any = null;
  let stub: any = null;
  const serviceStub = (req) => {
    try {
      return req.exported(
        ["BluetoothManager.GetState#1"],
        (value) =>
          !!value &&
          typeof value === "object" &&
          typeof value.GetState === "function" &&
          typeof value.Pair === "function",
      );
    } catch {
      return null;
    }
  };

  const invalidate = () => invalidateQuery(req, queryKey);

  // The host sends its own field names and the mapping into Steam's lives here, so the client's
  // schema stays in the half that has to change when the client is rebuilt.
  const onState = (state) => {
    if (!installed || !state) return;
    const devices = Array.isArray(state.devices) ? state.devices : [];
    latest = {
      is_service_available: state.available === true,
      // One synthetic adapter, because the panel needs something to hang the radio toggle on and
      // Windows exposes no adapter identity the host could pass through truthfully.
      adapters:
        state.available === true
          ? [
              {
                id: 1,
                mac: "",
                name: "Bluetooth",
                is_enabled: state.enabled === true,
                is_discovering: state.discovering === true,
              },
            ]
          : [],
      devices: devices.map((device) => ({
        id: device.id,
        mac: device.mac ?? "",
        name: device.name ?? device.id,
        etype: device.eType ?? 0,
        is_paired: device.isPaired === true,
        is_connected: device.isConnected === true,
        operation_in_progress: device.operationInProgress === true,
        // Steam sorts by signal and shows a battery when one is reported. The host knows neither, and
        // a fabricated strength would order the list by a number that means nothing.
        strength_raw: 0,
        battery_percent: null,
        should_hide_hint: false,
      })),
    };
    invalidate();
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    req = getWebpackRuntime("bluetooth-service");
    stub = serviceStub(req);
    if (!stub || typeof stub.GetState !== "function") {
      lastError = "BluetoothManagerService stub unavailable";
      return { ok: false, error: lastError };
    }

    const forward = (command) => (payload) =>
      request(patchId, command, payload ?? null).then(
        () => { lastError = ""; return transportReply({ success: true }); },
        (error) => {
          lastError = String(error);
          return transportFailure({ success: false, error: lastError });
        },
      );
    // A member claim per method, so the stub's own method is what removal hands back and a bridge
    // replaced in place reclaims its predecessor's overlay instead of wrapping it.
    const replace = (name, replacement) => {
      const claim = claimMember(stub, name, methodKeys, () => replacement);
      if (!claim.ok) throw new Error(claim.error);
      replaced.add(name);
    };

    try {
      replace("GetState", () => Promise.resolve(transportReply(latest)));
      replace("GetDeviceDetails", (payload) => {
        const id = payload?.device ?? payload?.id;
        const device = latest.devices.find((entry) => entry.id === id) ?? null;
        return Promise.resolve(transportReply({ device }));
      });
      replace("GetAdapterDetails", () =>
        Promise.resolve(transportReply({ adapter: latest.adapters[0] ?? null })),
      );
      replace("SetDiscovering", forward("setDiscovering"));
      replace("Pair", forward("pair"));
      replace("CancelPair", forward("cancelPair"));
      replace("Connect", forward("connect"));
      replace("Disconnect", forward("disconnect"));
      replace("Forget", forward("forget"));
      replace("SetTrusted", forward("setTrusted"));
      replace("SetWakeAllowed", forward("setWakeAllowed"));
    } catch (error) {
      lastError = String(error);
      for (const name of replaced) releaseMember(stub, name, methodKeys);
      replaced.clear();
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, onState);
    invalidate();
    return { ok: true, installed: true, replaced: replaced.size };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    unsubscribe = endSubscription(unsubscribe);

    for (const name of replaced) {
      const released = releaseMember(stub, name, methodKeys);
      if (!released.ok) {
        lastError = released.error ?? "Bluetooth service method release failed";
        return { ok: false, error: lastError };
      }
    }

    replaced.clear();
    latest = { is_service_available: false, adapters: [], devices: [] };
    invalidate();
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    replaced: replaced.size,
    available: latest.is_service_available,
    devices: latest.devices.length,
    lastError,
  });

  return { install, remove, status };
}

registerGate("bluetooth", createBluetoothService());
