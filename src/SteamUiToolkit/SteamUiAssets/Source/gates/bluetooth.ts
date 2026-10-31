/**
 * Supplies Steam Bluetooth service responses and notifications from host publications.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createBluetoothService() {
    const patchId = "steam-ui.bluetooth";
    const queryKey = ["BluetoothManagerService", "State"];
    const methodKeys = {
        marker: "__steamUiOwnedBluetoothService",
        original: "__steamUiOriginalBluetoothServiceMethod",
    } as const;
    // Every method the gate replaces, and the ones Steam's pairing UI calls, which the stub must have.
    const Replaced = [
        "GetState",
        "GetDeviceDetails",
        "GetAdapterDetails",
        "SetDiscovering",
        "Pair",
        "CancelPair",
        "Connect",
        "Disconnect",
        "Forget",
        "SetTrusted",
        "SetWakeAllowed",
    ] as const;
    const Operations = [
        "GetState",
        "SetDiscovering",
        "Pair",
        "CancelPair",
        "Connect",
        "Disconnect",
        "Forget",
        "SetTrusted",
        "SetWakeAllowed",
        "GetDeviceDetails",
    ] as const;
    // Why the last forwarded operation failed.
    let lastError = "";
    // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
    // and the host only carries them through from the state it was given.
    let latest: {
        is_service_available: boolean;
        adapters: any[];
        devices: any[];
    } = {is_service_available: false, adapters: [], devices: []};

    // The resolver and the stub the gate was installed against: every state push invalidates through
    // the same resolver, and removal hands the methods back on the stub they were claimed on.
    let req: any = null;
    let stub: any = null;
    const isServiceStub = (value) =>
        !!value &&
        typeof value === "object" &&
        typeof value.GetState === "function" &&
        typeof value.Pair === "function";

    const invalidate = () => invalidateQuery(req, queryKey);

    // The host sends its own field names and the mapping into Steam's lives here, so the client's
    // schema stays in the half that has to change when the client is rebuilt.
    const onState = (state) => {
        if (!gate.installed() || !state) return;
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

    const forward = (command) => (payload) =>
        request(patchId, command, payload ?? null).then(
            () => {
                lastError = "";
                return transportReply({success: true});
            },
            (error) => {
                lastError = String(error);
                return transportFailure({success: false, error: lastError});
            },
        );
    const replacements = {
        GetState: () => Promise.resolve(transportReply(latest)),
        GetDeviceDetails: (payload) => {
            const id = payload?.device ?? payload?.id;
            const device = latest.devices.find((entry) => entry.id === id) ?? null;
            return Promise.resolve(transportReply({device}));
        },
        GetAdapterDetails: () => Promise.resolve(transportReply({adapter: latest.adapters[0] ?? null})),
        SetDiscovering: forward("setDiscovering"),
        Pair: forward("pair"),
        CancelPair: forward("cancelPair"),
        Connect: forward("connect"),
        Disconnect: forward("disconnect"),
        Forget: forward("forget"),
        SetTrusted: forward("setTrusted"),
        SetWakeAllowed: forward("setWakeAllowed"),
    };
    const replacedCount = () =>
        stub ? Replaced.filter((name) => memberClaimed(stub, name, methodKeys)).length : 0;

    // Pure. The stub and the query client by what they are: the September 2026 beta renumbered the
    // modules this gate used to name. The query client must be reachable, because availability rides
    // a react-query with infinite stale time, and without an invalidation the row keeps reading the
    // unavailable answer whatever the methods return.
    const find = (runtime) => {
        let found;
        try {
            found = runtime.exported(["BluetoothManager.GetState#1"], isServiceStub);
        } catch (error) {
            return gateIncompatible(`BluetoothManagerService stub unavailable: ${String(error)}`);
        }
        const missing = Operations.filter((name) => typeof found[name] !== "function");
        const descriptor = Object.getOwnPropertyDescriptor(found, "GetState");
        const facts = {
            operationsPresent: missing.length === 0,
            missing,
            methodsWritable:
                claimed(found.GetState, methodKeys) ||
                (descriptor?.writable === true && descriptor.configurable === true),
            queryCacheReachable: !!resolveQueryClient(runtime),
        };
        if (missing.length) return gateIncompatible(`Bluetooth service lacks ${missing.join(", ")}`, facts);
        if (!facts.methodsWritable) return gateIncompatible("Bluetooth service methods are not writable", facts);
        if (!facts.queryCacheReachable) return gateIncompatible("the query client was not a unique match", facts);
        return gateReady({runtime, service: found}, facts);
    };

    const gate = defineGate({
        name: "bluetooth",
        find,
        bind: ({runtime, service}, ledger) => {
            req = runtime;
            stub = service;
            // Last out: once every method is Steam's again, the panel reads its own answer.
            ledger.cleanup("bluetooth state", () => {
                latest = {is_service_available: false, adapters: [], devices: []};
                invalidate();
            });
            // A member claim per method, so the stub's own method is what removal hands back and a
            // bridge replaced in place reclaims its predecessor's overlay instead of wrapping it.
            for (const name of Replaced) {
                const claim = ledger.member(stub, name, methodKeys, () => replacements[name]);
                if (!claim.ok) return claim.error;
            }
            ledger.subscribe(patchId, onState);
            invalidate();
            return {replaced: replacedCount()};
        },
        holds: () => replacedCount() > 0,
        unbind: () => {
            req = null;
            stub = null;
        },
        status: () => ({
            replaced: replacedCount(),
            available: latest.is_service_available,
            devices: latest.devices.length,
            operationError: lastError,
        }),
    });
    return gate;
}

registerGate("bluetooth", createBluetoothService());
