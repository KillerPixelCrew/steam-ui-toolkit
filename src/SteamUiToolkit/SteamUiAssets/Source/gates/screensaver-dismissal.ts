// Dismisses Big Picture's screensaver the way Steam itself does on user input: the saver view calls
// `Screensaver.ForceScreensaver({enabled: false})` from its blocked-input and input handlers
// (sp.js, Stable and Beta, 2026-11-07). After a wake nothing has given Steam input yet, so a saver
// that was showing when the machine slept can stay over a dark screen; a host calls `dismiss` once
// for that wake. The service stub is found by its authored job names and its shape, never by id.
function createScreensaverDismissal() {
    const ServiceTokens = ["Screensaver.GetActiveState#1", "Screensaver.ForceScreensaver#1"];
    let service: any = null;
    let dismissals = 0;
    let lastError = "";

    const isService = (value) =>
        typeof value?.GetActiveState === "function" && typeof value?.ForceScreensaver === "function";

    // The saver's state as Steam reports it, then Steam's own dismissal. The state only says whether
    // there was anything to dismiss; the dismissal is sent either way, as Steam's input path does.
    const dismiss = async (resumeAppId = 0) => {
        if (!service || !gate.status().held) return { dismissed: false, active: null };
        let active: boolean | null = null;
        try {
            const answer = await service.GetActiveState({});
            const value = answer?.Body?.()?.active?.();
            active = typeof value === "boolean" ? value : null;
        } catch (error) {
            lastError = "Screensaver state unreadable: " + String(error);
        }
        try {
            await service.ForceScreensaver({ enabled: false });
        } catch (error) {
            lastError = "Screensaver dismissal failed: " + String(error);
            return { dismissed: false, active };
        }
        dismissals++;
        if (Number.isInteger(resumeAppId) && resumeAppId > 0 && resumeAppId <= 4294967295) {
            try {
                const lifetime = window.__steamUiRunningApps;
                if (lifetime?.live !== true || !lifetime.ids.has(resumeAppId))
                    throw new Error("the pre-sleep game no longer has a positive Steam lifetime");
                const ui = window.SteamUIStore;
                const app = window.appStore?.GetAppOverviewByAppID(resumeAppId);
                const gameId = app?.GetGameID?.();
                const overlays = ui?.WindowStore?.GetOverlayInstances?.(resumeAppId);
                if (!gameId || typeof ui?.SetRunningApp !== "function"
                    || !Array.isArray(overlays) || overlays.length === 0
                    || overlays.some((overlay) => typeof overlay?.NavigateToRunningApp !== "function")
                    || typeof window.SteamClient?.Apps?.RaiseWindowForGame !== "function")
                    throw new Error("Steam's running-game activation path is not ready");
                // Steam's ResumeGameInProgress action: select the running app, restore its overlay
                // routes, then raise its existing game window. No launch or renderer reinjection.
                ui.SetRunningApp(resumeAppId);
                overlays.forEach((overlay) => overlay.NavigateToRunningApp());
                window.SteamClient.Apps.RaiseWindowForGame(gameId);
                lastError = "";
                return { dismissed: true, active, gameRestored: true };
            } catch (error) {
                lastError = "Running-game activation failed: " + String(error);
                return { dismissed: true, active, gameRestored: false, gameError: lastError };
            }
        }
        return { dismissed: true, active };
    };

    const gate = defineGate({
        name: "screensaver-dismissal",
        find: (runtime) => {
            const facts = { service: runtime.count(ServiceTokens) };
            if (facts.service !== 1)
                return gateIncompatible("Steam's Screensaver service is not one match", facts);
            let found = null;
            try {
                found = runtime.exported(ServiceTokens, isService);
            } catch {
                // Absent or ambiguous; reported below.
            }
            if (!found) return gateIncompatible("Steam's Screensaver service stub is not reachable", facts);
            return gateReady({ service: found }, facts);
        },
        // Nothing in Steam is changed until a host asks for a dismissal, so there is nothing to undo.
        bind: (found) => {
            service = found.service;
        },
        unbind: () => {
            service = null;
        },
        status: () => ({ dismissals, lastError }),
        api: { dismiss },
    });
    return gate;
}
registerGate("screensaverDismissal", createScreensaverDismissal());
