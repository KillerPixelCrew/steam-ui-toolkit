/**
 * Supplies Steam networking RPCs and scan notifications from host publications.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createNetworkGate() {
    const property = "networkManagementAvailable";
    const patchId = "steam-ui.network";
    const availability = {
        marker: "__steamUiOwnedGetter",
        original: "__steamUiOriginalGetterDescriptor",
    };
    const scan = {
        marker: "__steamUiOwnedNetworkScan",
        original: "__steamUiOriginalNetworkScan",
    };
    // Why the last forwarded operation, publication or store refresh failed.
    let lastError = "";
    const connect = {
        marker: "__steamUiOwnedNetworkConnect",
        original: "__steamUiOriginalNetworkConnect",
    };
    const listing = {
        marker: "__steamUiOwnedAccessPointList",
        original: "__steamUiOriginalAccessPointList",
    };
    // A Windows join waits up to 25 seconds for the WLAN verdict before the host publishes it.
    const joinTimeoutMilliseconds = 40000;
    let syntheticKeys: string[] = [];
    // Access point id -> SSID for the networks this gate wrote, which Steam's backend has never seen.
    const syntheticSsids = new Map<number, string>();
    // One id per network for as long as it is listed. Steam's connecting dialog holds the entry it
    // opened with and waits for that entry to report connected, so an entry must be updated in place
    // under the same id, never recreated or renumbered when the list reorders.
    const syntheticIds = new Map<string, number>();
    let nextSyntheticId = 990001;
    let latestJoin: { attempt?: unknown; pending?: unknown; joined?: unknown; error?: unknown } | null = null;
    const joinWaiters = new Set<() => boolean>();

    // Steam publishes this singleton after its own module initialization. Requiring the module
    // before its chunk arrives leaves empty exports cached for the whole session.
    const store = () => window.SystemNetworkStore ?? null;

    const removeNetworkState = (refresh: boolean) => {
        const instance = store();
        if (instance) {
            for (const key of syntheticKeys) instance.m_mapNetworkAccessPoints?.delete(key);
            // Recomputed only through the store's own methods. A client without them, or one that
            // throws, keeps the flags as they are and reports it; it never blocks the rest.
            if (
                typeof instance.IsAnyDeviceConnected === "function"
                && typeof instance.IsAnyDeviceConnecting === "function"
            ) {
                try {
                    instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
                    instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
                } catch (error) {
                    lastError = "network store refresh failed: " + String(error);
                }
            }
        }
        syntheticKeys = [];
        syntheticSsids.clear();
        syntheticIds.clear();
        if (refresh) {
            try {
                window.SteamClient?.System?.Network?.ForceRefresh?.();
            } catch {
            }
        }
    };

    // One resident owner now reveals AND feeds the network surface. The previous standalone
    // indicator installed a second script against this same store, with its own version sentinel
    // and retry timer; bridge state gives the generation-aware gate the same verified connected AP
    // for the header. Scan lifetime remains an observation of Steam's page. Joining, leaving and
    // forgetting the networks written here go to the host: their access point ids exist only in this
    // store, so Steam's own backend would answer them with nothing.
    const onState = (state) => {
        latestJoin = state?.join && typeof state.join === "object" ? state.join : null;
        for (const waiter of [...joinWaiters]) waiter();
        const instance = store();
        const networks = Array.isArray(state?.networks) ? state.networks : [];
        if (!instance || !instance.m_WirelessDevice) {
            lastError = "network store has no wireless device";
            return;
        }
        if (networks.length === 0) {
            removeNetworkState(true);
            lastError = "";
            return;
        }

        try {
            const device = JSON.parse(JSON.stringify(instance.m_WirelessDevice));
            if (!device.wireless) device.wireless = {aps: [], esecurity_supported: 0};
            const listed = new Set<string>();
            const accessPoints: {
                id: number;
                esecurity: number;
                estrength: number;
                ssid: string;
                is_active: boolean;
                is_autoconnect: boolean;
                is_hidden: boolean;
            }[] = [];
            for (const network of networks) {
                const ssid = String(network.ssid || "");
                // The host joins by name, so a second row with the same name is one network to Steam.
                if (listed.has(ssid)) continue;
                listed.add(ssid);
                let id = syntheticIds.get(ssid);
                if (id === undefined) {
                    id = nextSyntheticId++;
                    syntheticIds.set(ssid, id);
                }
                accessPoints.push({
                    id,
                    esecurity: network.secured ? 16 : 0,
                    estrength: Math.max(1, Math.min(4, Number(network.strength) || 1)),
                    ssid,
                    is_active: network.connected === true,
                    is_autoconnect: network.connected === true,
                    is_hidden: false,
                });
            }
            for (const [ssid, id] of [...syntheticIds]) {
                if (listed.has(ssid)) continue;
                syntheticIds.delete(ssid);
                instance.m_mapNetworkAccessPoints.delete(`${device.id}:${id}`);
            }
            const keys = accessPoints.map((accessPoint) => `${device.id}:${accessPoint.id}`);
            // Keys from an earlier wireless device id belong to no listed network any more.
            for (const key of syntheticKeys) {
                if (!keys.includes(key)) instance.m_mapNetworkAccessPoints.delete(key);
            }
            device.estate = networks.some((network) => network.connected === true) ? 5 : device.estate;
            device.wireless.aps = accessPoints;
            syntheticSsids.clear();
            accessPoints.forEach((accessPoint) => {
                syntheticSsids.set(accessPoint.id, accessPoint.ssid);
                // The store updates an existing entry in place, which is what an open dialog observes.
                instance.SetDeviceInfo(device, accessPoint.id);
                const entry = instance.m_mapNetworkAccessPoints.get(`${device.id}:${accessPoint.id}`);
                if (entry) {
                    entry.MarkAsNotPresent = () => {
                    };
                    // Own members on entries this gate created; they go with the entries on removal.
                    entry.Disconnect = () => forward("disconnect", null);
                    entry.Forget = () => forward("forget", {ssid: accessPoint.ssid});
                }
            });
            instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
            instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
            syntheticKeys = keys;
            lastError = "";
        } catch (error) {
            lastError = String(error);
        }
    };

    // Steam's page reads each of these as a boolean outcome.
    const forward = (command: string, payload: unknown) =>
        request(patchId, command, payload).then(
            () => {
                lastError = "";
                return true;
            },
            (error) => {
                lastError = String(error);
                return false;
            },
        );

    // The host accepts a join at once and publishes how it ended, because a join outlasts a request.
    const joinOutcome = (attempt: number) =>
        new Promise<boolean>((resolve) => {
            let timer: ReturnType<typeof setTimeout> | undefined;
            const finish = (joined: boolean, error: string) => {
                clearTimeout(timer);
                joinWaiters.delete(check);
                lastError = error;
                resolve(joined);
            };
            const check = () => {
                if (!latestJoin || latestJoin.attempt !== attempt || latestJoin.pending !== false) return false;
                const joined = latestJoin.joined === true;
                finish(joined, joined ? "" : String(latestJoin.error || "the network was not joined"));
                return true;
            };
            if (check()) return;
            joinWaiters.add(check);
            timer = setTimeout(() => finish(false, "no join outcome was published"), joinTimeoutMilliseconds);
        });

    const join = async (ssid: string, password: string | null) => {
        try {
            const accepted = (await request(patchId, "connect", {ssid, password})) as { attempt?: unknown } | null;
            const attempt = Number(accepted?.attempt);
            if (!Number.isFinite(attempt)) {
                lastError = "the join was not given an attempt number";
                return false;
            }
            return await joinOutcome(attempt);
        } catch (error) {
            lastError = String(error);
            return false;
        }
    };

    // Steam's page joins through the store's Connect, which encodes the access point id for Steam's
    // backend. For a network this gate wrote, or one typed by name, the host joins it instead; every
    // other call goes through untouched. Wanted, not required: a store that refuses the claim keeps
    // joining through Steam's own backend, and the refusal is reported.
    const wrapConnect = (instance, ledger: GateLedger) => {
        const claim = ledger.member(instance, "Connect", connect, (original) => {
            const inner = original as (...a: unknown[]) => unknown;
            return function (this: unknown, ...args: unknown[]) {
                const params = args[0] as {
                    deviceWapId?: unknown;
                    customAP?: { ssid?: unknown };
                    credentials?: { password?: unknown };
                } | null;
                const ssid = params?.customAP
                    ? String(params.customAP.ssid ?? "")
                    : syntheticSsids.get(Number(params?.deviceWapId));
                if (!ssid) return inner.apply(this, args);
                const password = params?.credentials?.password;
                return join(ssid, typeof password === "string" && password !== "" ? password : null);
            };
        });
        if (!claim.ok) lastError = claim.error;
    };

    // Steam's Windows backend reports the connected access point of the wireless device without a
    // name, which the page lists as a second connected network under the generic Wi-Fi label. While
    // this gate feeds the networks, the page's list leaves out the wireless device's own entries;
    // wired and other devices stay.
    const claimListing = (proto: object, ledger: GateLedger) => {
        const current = Object.getOwnPropertyDescriptor(proto, "accessPoints");
        const displaced = claimed(current?.get, listing)
            ? (storedOriginal(current!.get, listing) as PropertyDescriptor | undefined)
            : current;
        const originalGet = displaced?.get;
        if (typeof originalGet !== "function") return;
        const claim = ledger.accessor(proto, "accessPoints", listing, function (this: any) {
            const all = originalGet.call(this);
            const wirelessId = this?.m_WirelessDevice?.id;
            if (syntheticSsids.size === 0 || wirelessId == null || !Array.isArray(all)) return all;
            return all.filter(
                (entry) => entry?.deviceId !== wirelessId || syntheticSsids.has(Number(entry?.deviceWapId)),
            );
        });
        if (!claim.ok) lastError = claim.error;
    };

    // Steam's own UI calls these when its network page opens and closes, so they are exactly the
    // signal for when a scan is worth running. The host's radio manager is otherwise driven by the host's
    // own panel, and a list refreshed only then would be stale on Steam's page — which is worse
    // than an empty one, because the user picks a network that is gone and the join fails silently.
    //
    // Both originals are always called through: this observes the lifetime, it does not take it
    // over, so a client that grows a working backend keeps behaving exactly as before.
    const wrapScanning = (ledger: GateLedger) => {
        const net = window.SteamClient?.System?.Network;
        if (!net) return;
        const wrap = (name: string, command: string) => {
            // Checked before claiming, not inside the factory: a client without this method is one this
            // gate leaves alone entirely, and claiming would mark and reassign something that is not a
            // method at all.
            const current = net[name];
            const existing = claimed(current, scan) ? storedOriginal(current, scan) : current;
            if (typeof existing !== "function") return;

            let inner: ((...a: unknown[]) => unknown) | null = null;
            ledger.member(net, name, scan, (original) => {
                inner = original as (...a: unknown[]) => unknown;
                return function (this: unknown, ...args: unknown[]) {
                    // A scan request that cannot reach the host must not stop Steam's own call. Promise
                    // rejection is handled explicitly; a try/catch only sees synchronous construction.
                    void request(patchId, command, null).catch(() => {
                    });

                    return inner!.apply(this, args);
                };
            });
        };

        wrap("StartScanningForNetworks", "startScan");
        wrap("StopScanningForNetworks", "stopScan");
    };

    const scanWrapped = () =>
        ["StartScanningForNetworks", "StopScanningForNetworks"].some((name) =>
            memberClaimed(window.SteamClient?.System?.Network, name, scan),
        );

    // Pure, and loads no module: Steam publishes the store singleton after its own module
    // initialization, and requiring the module before its chunk arrives leaves empty exports cached
    // for the whole session. A store not published yet is a client still starting. The getter lives
    // on the prototype and must be configurable and read false, or be this gate's own: a getter
    // installed here is not evidence that the client reports network management natively, and
    // reading it that way made this gate refuse itself after a successful apply.
    const find = () => {
        const instance = store();
        if (!instance) return gateNotReady("network store unavailable");
        const proto = Object.getPrototypeOf(instance);
        const descriptor = Object.getOwnPropertyDescriptor(proto, property);
        const facts = {
            getterConfigurable:
                !!descriptor && descriptor.configurable === true && typeof descriptor.get === "function",
            currentlyHidden: claimed(descriptor?.get, availability) || instance[property] === false,
            hasWirelessDevice: instance.hasWirelessDevice === true,
        };
        if (!facts.getterConfigurable) {
            return gateIncompatible("networkManagementAvailable is not a configurable getter", facts);
        }
        if (!facts.currentlyHidden) {
            return gateIncompatible("the client already reports network management available", facts);
        }
        return gateReady({instance, proto}, facts);
    };

    const gate = defineGate({
        name: "network",
        find,
        bind: ({instance, proto}, ledger) => {
            lastError = "";
            // Last out: the networks this gate wrote leave the store once nothing else is held.
            ledger.cleanup("network state", () => removeNetworkState(true));
            // The getter lives on the prototype, so that is what is replaced and restored. Defining it
            // on the instance would shadow rather than replace, and removal would leave the shadow.
            const revealed = ledger.accessor(proto, property, availability, () => true);
            if (!revealed.ok) return revealed.error;
            wrapScanning(ledger);
            wrapConnect(instance, ledger);
            claimListing(proto, ledger);
            ledger.subscribe(patchId, onState);
            return {available: instance[property] === true};
        },
        holds: ({instance}) => instance[property] === true,
        status: () => {
            const instance = store();
            return {
                available: instance ? instance[property] === true : false,
                // Reported because the row can be on while the list is empty: Steam's Windows backend
                // never populates wireless.aps, so an access point count of zero here means the host has not
                // supplied one, not that the machine cannot see any networks.
                accessPoints: Array.isArray(instance?.accessPoints) ? instance.accessPoints.length : -1,
                hasWirelessDevice: instance?.hasWirelessDevice === true,
                scanWrapped: scanWrapped(),
                connectClaimed: memberClaimed(instance, "Connect", connect),
                operationError: lastError,
            };
        },
    });
    return gate;
}

registerGate("network", createNetworkGate());
