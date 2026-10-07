// @steam-ui-bundle-start
(() => {
    "use strict";
    // What the whole bundle evaluates to, set at the end of this file and returned by epilogue.ts
    // after every fragment has registered. The early reuse return below is the one path that leaves
    // before the fragments run, and it returns its own result directly.
    let installResult: string;
    const config: BridgeConfiguration = __STEAM_UI_CONFIGURATION_JSON__;
    const prior = window[config.namespace];
    if (
        prior &&
        prior.version === config.version &&
        // Neither generation changes when the host is updated, so without the asset hash a new build kept
        // running the previous build's script until Steam itself restarted.
        prior.assetHash === config.assetHash &&
        // The allow map is fixed at install, so a bridge reused across a vocabulary change would refuse
        // every command a module added since.
        prior.vocabularyRevision === config.vocabularyRevision &&
        prior.contextGeneration === config.contextGeneration &&
        prior.documentGeneration === config.documentGeneration &&
        // A prior bridge that can still hand out gates is one this build can stand aside for. Asking
        // for a specific gate by name would tie the reuse check to whichever surfaces the consumer
        // happens to have.
        typeof prior.gate === "function"
    ) {
        return JSON.stringify({ok: true, reused: true, version: prior.version});
    }
    // A prior bridge unwinds every gate it registered while their closures still hold what they
    // displaced; see dispose below. It names the gates that could not unwind, and the install result
    // carries them so the host can log them: nothing else would ever show that a gate stayed behind.
    const priorDispose = typeof prior?.dispose === "function" ? prior.dispose("generation replaced") : null;
    const priorDisposeFailures =
        Array.isArray(priorDispose) && priorDispose.length ? priorDispose.map(String) : undefined;

    const pending = new Map();
    const subscribers = new Map();
    const latestStates = new Map();
    // Why the host could not deliver a patch's state, until a state arrives again. A surface shows
    // it: the state it holds is the last one it was given, and without this it would pass for
    // current.
    const refusalSubscribers = new Map();
    const latestRefusals = new Map();
    // Deliveries being reassembled from parts, by delivery id. The host sends a large response and a
    // large state publication independently, so their parts can interleave; one shared slot made each
    // cancel the other. A set cut short is dropped rather than delivered half, and one the host
    // abandoned dies with the document.
    const assembling = new Map<number, { count: number; parts: string[] }>();
    let nextSequence = 0;
    let disposed = false;

    // A successful capture is shared; failed cold-start captures remain retryable.
    let webpackResolver: ReturnType<typeof createSteamUiModuleResolver> | undefined;
    /**
     * Returns this bridge's shared webpack resolver without evaluating unknown modules.
     * @param scope Diagnostic label used only on the first successful capture.
     * @returns The cached resolver; an unavailable runtime throws and can be retried later.
     */
    const getWebpackRuntime = (scope) => (webpackResolver ??= createSteamUiModuleResolver(scope));

    const allowed = (patchId, command) => {
        const commands = config.allowed[patchId];
        return Array.isArray(commands) && commands.includes(command);
    };
    const send = (envelope) => {
        if (disposed) throw new Error("Steam UI bridge disposed");
        const binding = window[config.binding];
        if (typeof binding !== "function") throw new Error("Steam UI Runtime binding unavailable");
        binding(JSON.stringify(envelope));
    };
    // Every request requires a positive generation, including automatic service notifications.
    const actionGenerations = new Map<string, number>();
    const nextActionGeneration = (patchId) => {
        const next = (actionGenerations.get(patchId) || 0) + 1;
        actionGenerations.set(patchId, next);
        return next;
    };
    const validActionGeneration = (patchId, actionGeneration) => {
        if (Number.isInteger(actionGeneration) && actionGeneration > 0) {
            actionGenerations.set(
                patchId,
                Math.max(actionGenerations.get(patchId) || 0, actionGeneration),
            );
            return actionGeneration;
        }
        return nextActionGeneration(patchId);
    };
    /**
     * Sends one allowlisted semantic request in the current bridge generation.
     * @param patchId Registered command namespace.
     * @param command Allowlisted command name.
     * @param payload JSON-serializable payload; undefined becomes null.
     * @param requestedGeneration Optional positive action generation; otherwise a new one is allocated.
     * @returns A promise for the backend payload; rejects on refusal, overload, timeout, disposal or send failure.
     */
    const request = (patchId, command, payload, requestedGeneration?: number) => {
        if (!allowed(patchId, command)) return Promise.reject(new Error("command not allowlisted"));
        if (pending.size >= config.maximumPending) return Promise.reject(new Error("bridge busy"));
        const actionGeneration = validActionGeneration(patchId, requestedGeneration);
        const sequence = ++nextSequence;
        const envelope = {
            version: config.version,
            type: "request",
            patchId,
            command,
            sequence,
            actionGeneration,
            contextGeneration: config.contextGeneration,
            documentGeneration: config.documentGeneration,
            payload: payload ?? null,
        };
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                pending.delete(sequence);
                try {
                    send({...envelope, type: "cancel"});
                } catch {
                }
                reject(new Error("Steam UI bridge request timed out"));
            }, config.timeoutMilliseconds);
            pending.set(sequence, {resolve, reject, timer, patchId, command});
            try {
                send(envelope);
            } catch (error) {
                clearTimeout(timer);
                pending.delete(sequence);
                reject(error);
            }
        });
    };
    /**
     * Subscribes to a patch state with synchronous cached replay and isolated callback errors.
     * @param patchId Registered publication identity.
     * @param callback Receives each complete delivered state, including cached state if available.
     * @returns An unsubscribe callback; after disposal a valid subscription returns a no-op.
     */
    const subscribe = (patchId, callback) => {
        if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
            throw new Error("subscription not allowlisted");
        if (disposed) return () => false;
        let set = subscribers.get(patchId);
        if (!set) subscribers.set(patchId, (set = new Set()));
        set.add(callback);
        // Cached replay has the same isolation as later publications. A consumer callback must
        // not prevent its installer from receiving the unsubscribe handle and finishing setup.
        if (latestStates.has(patchId)) {
            try {
                callback(latestStates.get(patchId));
            } catch {
            }
        }
        return () => set.delete(callback);
    };
    /**
     * Subscribes to publication failures and their recovery with synchronous cached replay.
     * @param patchId Registered publication identity.
     * @param callback Receives a refusal reason, or null when a later state succeeds.
     * @returns An unsubscribe callback; invalid identities or callbacks throw.
     */
    const subscribeRefusal = (patchId, callback) => {
        if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
            throw new Error("subscription not allowlisted");
        if (disposed) return () => false;
        let set = refusalSubscribers.get(patchId);
        if (!set) refusalSubscribers.set(patchId, (set = new Set()));
        set.add(callback);
        if (latestRefusals.has(patchId)) {
            try {
                callback(latestRefusals.get(patchId));
            } catch {
            }
        }
        return () => set.delete(callback);
    };
    const reportRefusal = (patchId, reason) => {
        if (reason === null ? !latestRefusals.has(patchId) : latestRefusals.get(patchId) === reason) return;
        if (reason === null) latestRefusals.delete(patchId);
        else latestRefusals.set(patchId, reason);
        for (const callback of [...(refusalSubscribers.get(patchId) ?? [])]) {
            try {
                callback(reason);
            } catch {
            }
        }
    };
    const deliver = (envelope) => {
        if (
            disposed ||
            !envelope ||
            envelope.version !== config.version ||
            envelope.contextGeneration !== config.contextGeneration ||
            envelope.documentGeneration !== config.documentGeneration
        )
            return false;
        if (envelope.type === "response") {
            const item = pending.get(envelope.sequence);
            if (!item || item.patchId !== envelope.patchId || item.command !== envelope.command)
                return false;
            clearTimeout(item.timer);
            pending.delete(envelope.sequence);
            if (envelope.ok) item.resolve(envelope.payload);
            else item.reject(new Error(String(envelope.error || "command rejected")));
            return true;
        }
        if (envelope.type === "state") {
            if (!Object.hasOwn(config.allowed, envelope.patchId)) return false;
            latestStates.set(envelope.patchId, envelope.payload);
            reportRefusal(envelope.patchId, null);
            const set = subscribers.get(envelope.patchId);
            if (!set) return true;
            for (const callback of [...set]) {
                try {
                    callback(envelope.payload);
                } catch {
                }
            }
            return true;
        }
        if (envelope.type === "refused") {
            if (!Object.hasOwn(config.allowed, envelope.patchId) || typeof envelope.reason !== "string")
                return false;
            reportRefusal(envelope.patchId, envelope.reason);
            return true;
        }
        return false;
    };
    // One part of an envelope too large for a single evaluation. A delivery's parts arrive in order,
    // each acknowledged before the next is sent, though another delivery's parts may come between
    // them; the last one delivers the reassembled envelope.
    const deliverPart = (part) => {
        if (
            disposed ||
            !part ||
            part.contextGeneration !== config.contextGeneration ||
            part.documentGeneration !== config.documentGeneration ||
            !Number.isSafeInteger(part.id) ||
            !Number.isSafeInteger(part.count) ||
            part.count < 2 ||
            !Number.isSafeInteger(part.index) ||
            part.index < 0 ||
            part.index >= part.count ||
            typeof part.text !== "string"
        )
            return false;
        if (part.index === 0) assembling.set(part.id, {count: part.count, parts: []});
        const entry = assembling.get(part.id);
        if (!entry || entry.count !== part.count || entry.parts.length !== part.index) {
            // Only this delivery is dropped; another one being reassembled is untouched.
            assembling.delete(part.id);
            return false;
        }
        entry.parts.push(part.text);
        if (entry.parts.length < entry.count) return true;
        const text = entry.parts.join("");
        assembling.delete(part.id);
        try {
            return deliver(JSON.parse(text));
        } catch {
            return false;
        }
    };
    // Returns the names of the gates that could not unwind, so the bridge replacing this one can
    // report them.
    const dispose = (reason) => {
        if (disposed) return [];
        disposed = true;
        const failures: string[] = [];
        // Resident gates own callbacks, service overlays and timers outside the bridge namespace.
        // Removing only the component host left the Manager gate polling every second after the bridge
        // that answered it had gone away, and left the other service wrappers calling dead closures.
        //
        // Every registered gate, not a list: a gate this file does not know about is exactly the case
        // a list gets wrong, and it is the normal case once a consumer adds one.
        for (const [name, gate] of gates) {
            const owned = gate as { remove?: () => unknown; dispose?: () => unknown };
            // Both, where present. `remove` unwinds what the gate installed in the client; `dispose`
            // releases what it holds inside this bridge, and the component host has only the latter.
            // A throw or an `{ok: false}` from either names the gate.
            let failed = false;
            for (const step of [owned.remove, owned.dispose]) {
                if (typeof step !== "function") continue;
                try {
                    const result = step.call(owned) as { ok?: unknown } | null | undefined;
                    if (result && typeof result === "object" && result.ok === false) failed = true;
                } catch {
                    failed = true;
                }
            }
            if (failed) failures.push(name);
        }
        gates.clear();
        for (const item of pending.values()) {
            clearTimeout(item.timer);
            item.reject(new Error(reason || "Steam UI bridge disposed"));
        }
        pending.clear();
        subscribers.clear();
        latestStates.clear();
        refusalSubscribers.clear();
        latestRefusals.clear();
        assembling.clear();
        actionGenerations.clear();
        webpackResolver = undefined;
        return failures;
    };

    // Stamped on every namespace the host defines on SteamClient, so a later probe can tell OUR namespace
    // from a real backend. Without it the two are indistinguishable and the compatibility check reads
    // its own successful install as "a native backend exists", refuses, and tears the patch down —
    // which is exactly what left this client with an empty audio page and a crashing Performance tab.
    //
    // A string key rather than a Symbol: it has to survive being read back from a probe evaluated in
    // a separate CDP call, where a Symbol from this scope is not reachable.
    const ownedMarker = "__steamUiOwnedNamespace";

    // Gates register themselves rather than being named here. The bridge used to construct each one
    // by name and publish it under a fixed property, which meant this file had to list every surface
    // its consumer happened to have — the one thing a reusable bridge cannot do.
    //
    // Registration is a top-level statement in each fragment, so it runs after this file and before
    // anything asks for a gate. It also inherits the reuse check for free: when this file returns
    // early because an identical bridge is already installed, the whole IIFE returns and no fragment
    // registers over it.
    const gates = new Map<string, unknown>();
    /**
     * Registers one gate implementation in this asset's shared bridge.
     * @param name Gate identity used by host patch expressions.
     * @param gate Gate lifecycle object retained until bridge disposal.
     */
    const registerGate = (name: string, gate: unknown) => {
        gates.set(name, gate);
    };
    const bridge = Object.freeze({
        version: config.version,
        assetHash: config.assetHash,
        vocabularyRevision: config.vocabularyRevision,
        contextGeneration: config.contextGeneration,
        documentGeneration: config.documentGeneration,
        request,
        subscribe,
        subscribeRefusal,
        deliver,
        deliverPart,
        dispose,
        // Looked up at call time, not captured: a gate registers after this object is frozen, and the
        // host asks for one long after that. Returning null for an unknown name rather than throwing
        // keeps a patch whose fragment failed to load reporting "gate absent" instead of an exception
        // with no name in it.
        gate: (name: string) => gates.get(name) ?? null,
    });
    Object.defineProperty(window, config.namespace, {
        value: bridge,
        configurable: true,
        enumerable: false,
        writable: false,
    });
    // NOT a return: every fragment after this file is concatenated into the same IIFE, so returning
    // the install result here would make each gate's top-level registerGate call unreachable and the
    // bridge would publish with an empty registry. epilogue.ts returns this once the bundle has run.
    installResult = JSON.stringify({ok: true, reused: false, version: config.version, priorDisposeFailures});
