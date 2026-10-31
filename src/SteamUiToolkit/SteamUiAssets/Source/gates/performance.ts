/**
 * Projects host performance state into Steam performance RPCs and semantic write commands.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createPerfNamespace() {
    const patchId = "steam-ui.performance";
    // Why the last settings update could not be decoded or the last state write failed.
    let lastError = "";

    const store = () => window.SystemPerfStore ?? null;

    // The perf store's module, by the source tokens that make it the perf store, and the singleton
    // reached through the one exported class with a Get() whose body declares the state, because the
    // state is written into a client that is already running. Never a module id: naming one is what
    // refused this gate on the September 2026 beta.
    const PerfStoreTokens = ["SteamClient.System.Perf", "RegisterForStateChanges", "m_msgState"];
    const isPerfStoreClass = (value) =>
        typeof value === "function" && typeof value.Get === "function" && String(value).includes("m_msgState");

    // The message class is never named here — it is taken from an instance the store builds, so
    // this stays correct across minification and client updates. An object argument is still
    // accepted because that is what a caller other than the store would pass. Anything that cannot
    // be decoded answers null, and the update is refused rather than sent as an empty delta that the
    // host would accept as "nothing changed".
    const decodeSettingsUpdate = (payload) => {
        if (typeof payload !== "string") {
            const decoded = payload?.toObject?.() ?? payload;
            if (decoded && typeof decoded === "object" && !Array.isArray(decoded)) return decoded;
            lastError = "settings update could not be decoded: not a message";
            return null;
        }
        try {
            const constructor = store()?.CreateSettingsUpdateRequest?.()?.constructor;
            if (typeof constructor?.deserializeBinary !== "function") {
                lastError = "settings update could not be decoded: no deserializeBinary";
                return null;
            }

            const binary = atob(payload);
            const bytes = new Uint8Array(binary.length);
            for (let index = 0; index < binary.length; index += 1) {
                bytes[index] = binary.charCodeAt(index);
            }

            const decoded = constructor.deserializeBinary(bytes).toObject();
            if (decoded && typeof decoded === "object" && !Array.isArray(decoded)) return decoded;
            lastError = "settings update could not be decoded: not a message";
            return null;
        } catch (error) {
            lastError = "settings update could not be decoded: " + String(error);
            return null;
        }
    };

    // The four fields the gate writes, as Steam's store held them before the first publication:
    // presence and value, so removal hands back exactly that rather than an invented empty state.
    const DisplacedFields = ["limits", "settings", "current_game_id", "active_profile_game_id"];
    let displaced: { field: string; present: boolean; value: unknown }[] | null = null;
    let displacedTarget: any = null;

    const onState = (state) => {
        if (!gate.installed() || !state) return;
        const target = store();
        if (!target || !target.m_msgState) return;
        try {
            if (!displaced) {
                displacedTarget = target.m_msgState;
                displaced = DisplacedFields.map((field) => ({
                    field,
                    present: Object.hasOwn(displacedTarget, field),
                    value: displacedTarget[field],
                }));
            }
            target.m_msgState.limits = state.limits ?? {};
            target.m_msgState.settings = {
                global: state.global ?? {},
                per_app: state.perApp ?? {},
            };
            // Steam identifies the per-game profile by comparing these two: equal means the running
            // game's own profile is the one being edited. "No game" is 769 — the Steam client's own
            // pseudo-app, the id Valve's components compare against — never "0".
            target.m_msgState.current_game_id = state.currentGameId ?? "769";
            target.m_msgState.active_profile_game_id = state.activeProfileGameId ?? "769";
        } catch (error) {
            lastError = String(error);
        }
    };

    // Every setter builds a protobuf delta and hands it to UpdateSettings, so that one method is
    // where all of them arrive. The delta is decoded on the host's side rather than here, because the
    // message shapes belong to the client and this half only forwards.
    const buildApi = () => ({
        // Decode first, always. SystemPerfStore's setters all end in
        // `UpdateSettings(request.serializeBase64String())`, so what arrives here is a BASE64
        // STRING, not the message — live-verified 2026-08-30 by round-tripping a request built by
        // the store itself. Forwarding it verbatim made the host's reader reject every write as
        // "carried no delta object", which is why no control on the Performance tab did anything:
        // the overlay-level selector snapped back to off, the frame cap never took, VRR never
        // toggled. Decoding through the message's OWN deserializeBinary keeps the wire format the
        // client's business; toObject() then emits snake_case field names, which is what the host reads.
        UpdateSettings: (payload) => {
            const delta = decodeSettingsUpdate(payload);
            if (delta === null) return Promise.reject(new Error(lastError));
            return request(patchId, "updateSettings", { delta });
        },
        RegisterForStateChanges: () => ({
            unregister: () => {},
        }),
        RegisterForDiagnosticInfoChanges: () => ({
            unregister: () => {},
        }),
    });

    // Pure. A real backend at SteamClient.System.Perf is a client this gate stands aside for; one
    // carrying our marker is our own. The store module and its class are the client's shape; a store
    // that has not been constructed or published yet is a client still starting.
    const find = (runtime) => {
        const system = window.SteamClient?.System;
        if (!system) return gateNotReady("SteamClient.System unavailable");
        const perfStore = runtime.count(PerfStoreTokens);
        const namespace = system.Perf;
        const perfNamespaceAbsent = !namespace || claimed(namespace, { marker: ownedMarker, original: ownedMarker });
        const facts: Record<string, unknown> = { perfStore, perfNamespaceAbsent, storeSingletonReachable: false };
        if (perfStore !== 1) return gateIncompatible("the performance store module was not a unique match", facts);
        if (!perfNamespaceAbsent) return gateIncompatible("a native performance backend is present", facts);
        let holder;
        try {
            holder = runtime.exported(PerfStoreTokens, isPerfStoreClass);
        } catch (error) {
            return gateIncompatible(`the performance store class was not a unique match: ${String(error)}`, facts);
        }
        const singleton = holder.Get();
        if (!singleton) return gateNotReady("the performance store is not constructed yet", facts);
        if (!("m_msgState" in singleton)) {
            return gateIncompatible("the performance store has no m_msgState", facts);
        }
        facts.storeSingletonReachable = true;
        if (!store()) return gateNotReady("SystemPerfStore is not published yet", facts);
        return gateReady({ system }, facts);
    };

    const gate = defineGate({
        name: "perf",
        find,
        bind: ({ system }, ledger) => {
            lastError = "";
            // Last out: back to what the store held before the first publication, so every control
            // returns to rendering what it did before the host answered rather than keeping the host's
            // last answer. A failed restoration keeps the gate installed, and the next removal retries it.
            ledger.add("performance state", () => {
                if (displacedTarget && displaced) {
                    try {
                        for (const { field, present, value } of displaced) {
                            if (present) displacedTarget[field] = value;
                            else delete displacedTarget[field];
                        }
                    } catch (error) {
                        return { ok: false, error: "perf state restoration failed: " + String(error) };
                    }
                }
                displaced = null;
                displacedTarget = null;
                return { ok: true };
            });
            // Stand aside for a real backend, reclaim one of our own — the primitive's rules, and it
            // marks the namespace before publishing it so nothing can observe an unmarked one. An
            // orphaned Perf namespace is worse than an orphaned audio one: it leaves SystemPerfStore
            // holding half-written state, which renders Valve's controls with no values behind them.
            const supplied = ledger.namespace(system, "Perf", ownedMarker, buildApi);
            if (!supplied.ok) return supplied.error;
            ledger.subscribe(patchId, onState);
        },
        status: () => {
            const target = store();
            return {
                namespacePresent: !!window.SteamClient?.System?.Perf,
                limitsPresent: !!target?.msgLimits,
                // Which controls can draw at all, since each reads its own availability out of limits.
                frameLimitOptions: target?.msgLimits?.fps_limit_options?.length ?? 0,
                vrrSupported: target?.msgLimits?.is_vrr_supported === true,
                operationError: lastError,
            };
        },
    });
    return gate;
}

registerGate("perf", createPerfNamespace());
