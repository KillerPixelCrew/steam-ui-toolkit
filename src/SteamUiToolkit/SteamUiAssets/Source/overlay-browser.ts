/** Resolves trusted lookup chrome's handoff to a Steam-owned Big Picture or game browser. */
function resolveSteamOverlayBrowser(runtime) {
    const useWindow = runtime.exported(
        ["createContext(null)", "VirtualKeyboardManager", "instance"],
        (value) => typeof value === "function" && String(value).includes(".useContext("),
    ) as () => any;
    const routes = runtime.exported(
        ["GameAPIOSK:", "/gameapiosk"],
        (value) => typeof value?.GamepadUI?.ExternalWeb === "function",
    ) as any;
    const externalPath = routes?.GamepadUI?.ExternalWeb();
    if (
        typeof useWindow !== "function" ||
        typeof externalPath !== "string" ||
        !/^\/[a-z0-9/_-]+$/iu.test(externalPath) ||
        externalPath.startsWith("//") ||
        externalPath.length > 512
    )
        return null;
    let disposed = false;
    const retained = new WeakMap<object, any>();
    const subscriptions = new Set<WeakRef<any>>();
    const validId = (value) => Number.isInteger(value) && value > 0 && value <= 0xffffffff;
    const identify = (instance) => {
        if (disposed) return null;
        const store = (window as any).SteamUIStore?.WindowStore;
        if (!instance || !store) return null;
        if (instance.IsMainGamepadUIWindow?.() === true) {
            const windows = store.SteamUIWindows;
            if (!Array.isArray(windows) || instance.IsOverlayWindow?.() === true) return null;
            const matches = windows.filter(
                (candidate) => candidate?.IsMainGamepadUIWindow?.() === true,
            );
            return matches.length === 1 && matches[0] === instance
                ? { appId: null, processId: null, gameId: null, title: "", kind: "main" }
                : null;
        }
        const windows = store.OverlayWindows;
        if (!Array.isArray(windows) || !windows.includes(instance)) return null;
        const gamepad = instance.IsGamepadUIOverlayWindow?.() === true;
        const desktop = instance.IsDesktopOverlayWindow?.() === true;
        if (gamepad === desktop) return null;
        const info = instance.params?.browserInfo;
        if (
            !validId(info?.m_unPID) ||
            !validId(info?.m_unAppID) ||
            typeof info?.m_gameID !== "string" ||
            !/^[1-9][0-9]{0,19}$/u.test(info.m_gameID) ||
            BigInt(info.m_gameID) > 0xffffffffffffffffn
        )
            return null;
        const matches = windows.filter((candidate) => {
            const other = candidate?.params?.browserInfo;
            return (
                other?.m_unPID === info.m_unPID &&
                other?.m_unAppID === info.m_unAppID &&
                other?.m_gameID === info.m_gameID &&
                (candidate.IsGamepadUIOverlayWindow?.() === true) === gamepad &&
                (candidate.IsDesktopOverlayWindow?.() === true) === desktop
            );
        });
        if (matches.length !== 1 || matches[0] !== instance) return null;
        let title = "";
        try {
            const name = (window as any).appStore?.GetAppOverviewByAppID(
                info.m_unAppID,
            )?.display_name;
            if (typeof name === "string") title = name.slice(0, 512);
        } catch {
            // Optional metadata must not substitute another window's game identity.
        }
        return {
            appId: info.m_unAppID,
            processId: info.m_unPID,
            gameId: info.m_gameID,
            title,
            kind: gamepad ? "gamepad" : "desktop",
        };
    };
    const allowed = (value) => {
        if (value === "about:blank") return true;
        if (typeof value !== "string" || value.length > 4096) return false;
        try {
            const url = new URL(value);
            return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password;
        } catch {
            return false;
        }
    };
    const sameTarget = (left, right) =>
        left &&
        right &&
        left.appId === right.appId &&
        left.processId === right.processId &&
        left.gameId === right.gameId &&
        left.kind === right.kind;
    const release = (entry) => {
        entry.stop?.();
        entry.stop = null;
        entry.saved = null;
        subscriptions.delete(entry.reference);
    };
    const watch = (instance, target) => {
        const history = instance.History;
        if (
            typeof history?.listen !== "function" ||
            typeof history?.push !== "function" ||
            typeof instance.Navigate !== "function"
        )
            return null;
        let entry = retained.get(instance);
        if (entry && (entry.history !== history || !sameTarget(entry.target, target))) {
            release(entry);
            retained.delete(instance);
            entry = null;
        }
        if (entry) return entry;
        // Neither this registry nor the callback keeps a closed overlay instance alive.
        for (const reference of subscriptions) {
            if (!reference.deref()) subscriptions.delete(reference);
        }
        const owner = new WeakRef(instance);
        entry = { history, target, saved: null, stop: null, reference: null };
        const current = entry;
        entry.reference = new WeakRef(entry);
        const capture = (location) => {
            const active = owner.deref();
            if (
                !active ||
                !sameTarget(identify(active), current.target) ||
                active.History !== history
            ) {
                release(current);
                if (active) retained.delete(active);
                return;
            }
            if (location?.pathname !== externalPath) return;
            const state = location.state;
            const initial = state?.initialIndex;
            const final = state?.finalIndex;
            // Steam supplies these indices after creating its browser session. Its loading state
            // can temporarily have finalIndex one below initialIndex; never invent either index.
            if (
                !Number.isSafeInteger(initial) ||
                initial < 0 ||
                (final !== undefined &&
                    (!Number.isSafeInteger(final) || final < 0 || final < initial - 1)) ||
                (state.url !== undefined && !allowed(state.url))
            ) {
                current.saved = null;
                return;
            }
            const saved: any = { initialIndex: initial };
            if (final !== undefined) saved.finalIndex = final;
            if (state.url !== undefined) saved.url = state.url;
            current.saved = saved;
        };
        const stop = history.listen(capture);
        if (typeof stop !== "function") return null;
        entry.stop = stop;
        retained.set(instance, entry);
        subscriptions.add(entry.reference);
        capture(history.location);
        return retained.get(instance) ?? null;
    };
    const open = (instance, url?: string, home = "about:blank") => {
        const target = identify(instance);
        if (!target || (url !== undefined && !allowed(url)) || !allowed(home)) return false;
        if (target.kind === "gamepad" || target.kind === "main") {
            if (typeof instance.Navigator?.ExternalWeb !== "function") return false;
            const entry = watch(instance, target);
            if (!entry) return false;
            if (url === undefined && entry.saved) {
                // ExternalWeb(undefined) loads Steam's store. Restore the native route's own
                // session state through its window navigator so the retained page is resumed.
                instance.Navigate(externalPath, false, false, { ...entry.saved });
            } else {
                entry.saved = null;
                instance.Navigator.ExternalWeb(url ?? home);
            }
            return true;
        }
        const browser = instance.DesktopOverlay;
        if (
            typeof browser?.AddWebPageRequest !== "function" ||
            typeof browser?.SetWindowVisibility !== "function" ||
            typeof browser?.BringWindowToFront !== "function"
        )
            return false;
        if (url !== undefined) browser.AddWebPageRequest(url, true);
        else {
            browser.LoadWebRequestsIfNeeded?.();
            if (!browser.web_requests?.length) browser.AddWebPageRequest(home, true);
            else browser.SetWindowVisibility("Browser", 1);
        }
        browser.BringWindowToFront("Browser");
        return true;
    };
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        for (const reference of subscriptions) {
            const entry = reference.deref();
            if (entry) release(entry);
        }
        subscriptions.clear();
    };
    return { useWindow, identify, open, dispose };
}
