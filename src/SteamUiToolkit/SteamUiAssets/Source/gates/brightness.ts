/**
 * Binds Steam brightness controls to host display state and brightness commands.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createBrightnessGate() {
    const patchId = "steam-ui.brightness";
    const field = "is_display_brightness_available";
    // A string key on the settings message, because the probe reads it from a separate CDP
    // evaluation where nothing from this scope is reachable. Without it this gate ran the
    // self-incompatibility teardown loop the audio namespace already paid for: the probe required
    // the flag to be hidden, a successful apply made it visible, and the patch manager tore down
    // its own work every poll — the row flickered in and out on a ~25-second cycle on the device.
    const availability = {
        marker: "__steamUiBrightnessRevealed",
        original: "__steamUiOriginalBrightnessAvailability",
    };
    const setter = {
        marker: "__steamUiOwnedSetBrightness",
        original: "__steamUiOriginalSetBrightness",
    };
    // Why the last brightness write or state projection failed.
    let lastError = "";
    let lastPercent: number | null = null;
    let lastRevision = -1;
    let applyingState = false;
    let requestVersion = 0;
    let pendingWrite = false;
    let confirmedState: { percent: number; revision: number } | null = null;
    // The display store the gate was installed against: a singleton, held from its find.
    let displayStore: any = null;

    // The display settings store by what it is: the one module holding the brightness observable,
    // and the one exported class on it with a singleton Get() whose body declares that observable.
    // Client builds renumber the module (the September 2026 beta did), so no id is kept.
    const DisplayStoreTokens = ["m_flDisplayBrightness", "is_display_brightness_available"];
    const isDisplayStoreClass = (value) =>
        typeof value === "function" &&
        typeof value.Get === "function" &&
        String(value).includes("m_flDisplayBrightness");

    const onState = (state) => {
        if (!gate.installed() || !state) return;
        const percent = Number(state.percent);
        const revision = Number(state.revision);
        if (
            !Number.isInteger(percent) ||
            percent < 0 ||
            percent > 100 ||
            !Number.isSafeInteger(revision) ||
            revision < 0 ||
            revision < lastRevision
        )
            return;
        lastRevision = revision;
        confirmedState = {percent, revision};
        if (pendingWrite) return;
        try {
            const observable = displayStore?.m_flDisplayBrightness;
            applyingState = true;
            if (observable?.Set && Math.abs((observable.m_currentValue ?? -1) - percent / 100) > 0.004) {
                observable.Set(percent / 100);
            }
            lastPercent = percent;
        } catch (error) {
            lastError = "brightness state apply failed: " + String(error);
        } finally {
            applyingState = false;
        }
    };

    // The slider's writes, taken over at the one method it calls. Same replace-not-stack rule as
    // the Manager's GetState: the overlay carries the stub it replaced, so a bridge replaced in
    // place unwinds to the client's own method instead of wrapping a dead closure.
    const setBrightness = () => (flBrightness) => {
        if (!gate.installed() || applyingState) return Promise.resolve();
        const value = Number(flBrightness);
        if (!Number.isFinite(value) || value < 0 || value > 1) return Promise.resolve();
        const percent = Math.round(value * 100);
        if (!pendingWrite && percent === lastPercent) return Promise.resolve();
        const version = ++requestVersion;
        pendingWrite = true;
        return request(patchId, "setBrightness", {percent})
            .then((readback) => {
                if (!gate.installed() || version !== requestVersion) return;
                pendingWrite = false;
                onState(readback);
                // A later external observation can already have arrived while this request completed.
                if (confirmedState) onState(confirmedState);
            })
            .catch((error) => {
                if (!gate.installed() || version !== requestVersion) return;
                pendingWrite = false;
                lastError = "brightness write failed: " + String(error);
                if (confirmedState) onState(confirmedState);
            });
    };

    // Pure. The settings message is built when Quick Settings first mounts its display section, so
    // a store or a message without the flag yet is a client still loading: not ready, never an
    // incompatible build. A client already reporting brightness available needs nothing from the
    // host and is left alone, unless the flag is this gate's own earlier reveal, surviving a bridge
    // replaced in place: refusing that is the teardown trap. Revealing the row also needs the native
    // backend, or the result is a slider that moves and changes nothing.
    const find = (runtime) => {
        let storeClass;
        try {
            storeClass = runtime.exported(DisplayStoreTokens, isDisplayStoreClass);
        } catch (error) {
            return gateIncompatible(`the display store was not a unique match: ${String(error)}`);
        }
        const store = storeClass.Get() ?? null;
        const message = store?.m_msgSettings ?? null;
        const display = window.SteamClient?.System?.Display;
        const facts = {
            fieldPresent: !!message && field in message,
            revealable: !!message && (message[field] !== true || claimed(message, availability)),
            backendPresent:
                !!display &&
                typeof display.SetBrightness === "function" &&
                typeof display.RegisterForBrightnessChanges === "function",
        };
        if (!message) return gateNotReady("display settings message unavailable", facts);
        if (!facts.fieldPresent) return gateNotReady("display settings have no brightness flag yet", facts);
        if (!facts.backendPresent) {
            return gateIncompatible("SteamClient.System.Display brightness backend unavailable", facts);
        }
        if (!facts.revealable) return gateIncompatible("the client already reports brightness available", facts);
        return gateReady({store, message, display}, facts);
    };

    const gate = defineGate({
        name: "brightness",
        find,
        bind: ({store, message, display}, ledger) => {
            displayStore = store;
            lastPercent = null;
            lastRevision = -1;
            confirmedState = null;
            lastError = "";
            // Late completions after removal are ignored, and a reinstall starts a fresh sequence.
            ledger.cleanup("brightness requests", () => {
                ++requestVersion;
                pendingWrite = false;
            });
            const revealed = ledger.value(message, field, availability, true);
            if (!revealed.ok) return revealed.error;
            // Revealing a slider whose writes go into the stub is the broken state this gate shipped
            // with; a setter that cannot be claimed unwinds the reveal with it.
            const owned = ledger.member(display, "SetBrightness", setter, setBrightness);
            if (!owned.ok) return owned.error;
            ledger.subscribe(patchId, onState);
            return {available: message[field] === true};
        },
        holds: ({message}) => message[field] === true,
        unbind: () => {
            displayStore = null;
        },
        status: (handles) => ({
            available: handles ? handles.message[field] === true : false,
            setterOwned: memberClaimed(window.SteamClient?.System?.Display, "SetBrightness", setter),
            lastPercent,
            lastRevision,
            pendingWrite,
            observable: displayStore?.m_flDisplayBrightness?.m_currentValue ?? null,
            operationError: lastError,
        }),
    });
    return gate;
}

registerGate("brightness", createBrightnessGate());
