// Steam's native controller-idle clock does not include Windows session input outside its views.
// Merge that second real producer at the Screensaver service's existing notification boundary.
// Neither the native clock, controller events nor the user's timeout preferences are changed.
function createScreensaverActivity(
    readSettings: () => any,
    hasBattery: () => boolean = () => true,
    onActive: (active: boolean) => void = () => {},
) {
    const notice = "Screensaver.NotifyActiveStateChanged#1";
    const nativeTokens = ["Screensaver.GetActiveState#1", "Screensaver.ForceScreensaver#1"];
    const registryTokens = [
        "Multiple attempts to set a default WebUI message handler registry: overriding previous",
    ];
    const protoTokens = ["InitFromMsg(", "SetBodyJSON(", "SerializeBody("];
    const keys = {
        marker: "__steamUiScreensaverActivity",
        original: "__steamUiScreensaverActivityOriginal",
    };
    let native: any;
    let registry: any;
    let proto: any;
    let packet: any;
    let installed = false;
    let generation = 0;
    let nativeObservation = 0;
    let nativeActive = false;
    let displayed: boolean | null = null;
    let previewStarted = 0;
    let preview = false;
    let sample: any = null;
    let sampled = 0;
    let expiry: ReturnType<typeof setTimeout> | null = null;
    let lastError = "";

    const effective = () => {
        if (!installed || !nativeActive || preview || !sample || performance.now() - sampled > 5000)
            return nativeActive;
        const values = readSettings();
        const setting =
            sample.onBattery === true && hasBattery()
                ? "system_idle_screensaver_battery_sec"
                : "system_idle_screensaver_ac_sec";
        const timeout = values?.[setting];
        if (!Number.isInteger(timeout) || timeout <= 0) return nativeActive;
        return sample.ageMilliseconds + performance.now() - sampled >= timeout * 1000;
    };

    // Existing subscribers may predate our install. Dispatch only this named service's existing
    // handlers, using Valve's own protobuf and packet implementations, including on removal.
    const reconcile = (force = false) => {
        const active = effective();
        if (!packet || (!force && displayed === active)) return;
        displayed = active;
        if (installed) onActive(active);
        const message = proto.InitFromObject(native.NotifyActiveStateChangedHandler.request, {
            active,
        });
        message.Hdr().set_target_job_name(notice);
        const incoming = proto.InitFromPacket(
            native.NotifyActiveStateChangedHandler.request,
            new packet(message.Serialize()),
        );
        const handlers = registry.m_mapServiceMethodHandlers.get(notice);
        for (const handler of handlers?.slice() ?? []) {
            try {
                handler.invoke(incoming, () => {});
            } catch (error) {
                lastError = "Screensaver subscriber failed: " + String(error);
            }
        }
    };

    const observe = (activity) => {
        if (!installed) return;
        if (expiry !== null) clearTimeout(expiry);
        expiry = null;
        const valid =
            activity &&
            Number.isSafeInteger(activity.revision) &&
            activity.revision > 0 &&
            Number.isInteger(activity.ageMilliseconds) &&
            activity.ageMilliseconds >= 0 &&
            activity.ageMilliseconds <= 0xffffffff;
        if (!valid) {
            sample = null;
            reconcile();
            return;
        }
        const changed = sample && sample.revision !== activity.revision;
        sample = activity;
        sampled = performance.now();
        if (preview && changed && Date.now() - activity.ageMilliseconds > previewStarted + 250) {
            preview = false;
            void Promise.resolve(native.ForceScreensaver({ enabled: false })).catch((error) => {
                lastError = String(error);
            });
        }
        reconcile();
        // A lost host must restore native behavior. This is an expiring observation lease, not
        // another input poller; the session host remains the sole source of Windows samples.
        expiry = setTimeout(() => {
            expiry = null;
            sample = null;
            reconcile();
        }, 5100);
    };

    const stop = () => {
        installed = false;
        generation++;
        if (expiry !== null) clearTimeout(expiry);
        expiry = null;
        sample = null;
        preview = false;
        const releases = [
            releaseMember(registry, "DispatchMsgToHandlers", keys),
            releaseMember(native, "GetActiveState", keys),
            releaseMember(native, "ForceScreensaver", keys),
        ];
        reconcile(true);
        packet = null;
        displayed = null;
        return releases.find((value) => !value.ok) ?? { ok: true };
    };

    const start = (runtime) => {
        if (installed) return { ok: true };
        const current = ++generation;
        try {
            native = runtime.exported(
                nativeTokens,
                (value) =>
                    typeof value?.GetActiveState === "function" &&
                    typeof value?.ForceScreensaver === "function" &&
                    !!value?.NotifyActiveStateChangedHandler?.request,
            );
            const getRegistry = runtime.exported(
                registryTokens,
                (value) => typeof value === "function",
            ) as any;
            registry = getRegistry().GetDefaultHandlerRegistry();
            proto = runtime.exported(
                protoTokens,
                (value) =>
                    typeof value?.InitFromMsg === "function" &&
                    typeof value?.InitFromObject === "function" &&
                    typeof value?.InitFromPacket === "function",
            );
            if (
                !(registry?.m_mapServiceMethodHandlers instanceof Map) ||
                typeof registry.DispatchMsgToHandlers !== "function"
            )
                throw new Error("Screensaver notification registry shape changed");
            const dispatch = claimMember(
                registry,
                "DispatchMsgToHandlers",
                keys,
                (original: any) =>
                    function (this: unknown, message, ...rest) {
                        if (installed && message.Hdr().target_job_name() === notice) {
                            packet = message.m_netPacket?.constructor;
                            if (typeof packet !== "function")
                                return original.call(this, message, ...rest);
                            const decoded = proto.InitFromMsg(
                                native.NotifyActiveStateChangedHandler.request,
                                message,
                            );
                            nativeObservation++;
                            nativeActive = decoded.Body().active() === true;
                            if (!nativeActive) preview = false;
                            reconcile(true);
                            return true;
                        }
                        return original.call(this, message, ...rest);
                    },
            );
            if (!dispatch.ok) throw new Error(dispatch.error);
            const get = claimMember(
                native,
                "GetActiveState",
                keys,
                (original: any) =>
                    function (this: unknown, ...args) {
                        const requested = generation;
                        const observation = nativeObservation;
                        return Promise.resolve(original.apply(this, args)).then((message) => {
                            if (installed && requested === generation) {
                                packet = message.m_netPacket?.constructor ?? packet;
                                if (observation === nativeObservation)
                                    nativeActive = message.Body().active() === true;
                                message.Body().set_active(effective());
                            }
                            return message;
                        });
                    },
            );
            if (!get.ok) throw new Error(get.error);
            const force = claimMember(
                native,
                "ForceScreensaver",
                keys,
                (original: any) =>
                    function (this: unknown, options, ...rest) {
                        preview = options?.enabled === true;
                        if (preview) previewStarted = Date.now();
                        // Native activation may already be true while Windows input keeps it hidden.
                        // Force(true) emits no native change in that state, so Preview must publish the
                        // effective transition itself. The same applies when ending a preview.
                        reconcile();
                        return original.call(this, options, ...rest);
                    },
            );
            if (!force.ok) throw new Error(force.error);
            installed = true;
            void native
                .GetActiveState({})
                .then(() => {
                    if (installed && current === generation) reconcile(true);
                })
                .catch((error) => {
                    lastError = String(error);
                });
            return { ok: true };
        } catch (error) {
            lastError = String(error);
            stop();
            return { ok: false, error: lastError };
        }
    };

    return {
        start,
        observe,
        stop,
        status: () => ({ installed, nativeActive, displayed, preview, lastError }),
    };
}
