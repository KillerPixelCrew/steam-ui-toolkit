/** Resolves trusted chrome's handoff to a Steam-owned Big Picture or game browser. */
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
    let releaseChrome: (() => void) | null = null;
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
    const open = (instance, url?: string, home = "about:blank", replace = false) => {
        const target = identify(instance);
        if (!target || (url !== undefined && !allowed(url)) || !allowed(home)) return false;
        if (target.kind === "gamepad" || target.kind === "main") {
            if (typeof instance.Navigator?.ExternalWeb !== "function") return false;
            const entry = watch(instance, target);
            if (!entry || (replace && typeof entry.history.replace !== "function")) return false;
            if (url === undefined && entry.saved) {
                // ExternalWeb(undefined) loads Steam's store. Restore the native route's own
                // session state through its window navigator so the retained page is resumed.
                instance.Navigate(externalPath, replace, false, { ...entry.saved });
            } else {
                entry.saved = null;
                if (replace) instance.Navigate(externalPath, true, false, { url: url ?? home });
                else instance.Navigator.ExternalWeb(url ?? home);
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
    const installChrome = (ui, searchPrefix: () => string) => {
        if (disposed) return false;
        if (releaseChrome) return true;
        const react = ui.react;
        const frame = runtime.exported(
            ["BrowserBackstack", "SetCurrentBrowserAndBackstack", "SetGoBackOverride"],
            // Valve exports the zero-argument browser layer and this one-prop error-boundary
            // wrapper. Select the wrapper's shape without depending on its minified export name.
            (value) => typeof value === "function" && value.length === 1,
        );
        const bar = runtime.exported(
            ["URLInput", "onEnterKeyPress", "bCanChangeURL", "GetGameInputSupportLevel"],
            (value) => sourceMatches(value, ["bCanChangeURL", "DisplayURL", "onEnterKeyPress"]),
        );
        const jsx = runtime.resolve([...JsxRuntimeTokens]);
        const nativeEditable = new WeakMap<object, any>();
        const mounted = new Set<() => void>();
        const useChrome = () => {
            const [, invalidate] = react.useState(0);
            react.useEffect(() => {
                const refresh = () => invalidate((revision) => revision + 1);
                if (disposed) refresh();
                else mounted.add(refresh);
                return () => mounted.delete(refresh);
            }, []);
        };
        const normalize = (value, searchOnly = false) => {
            const text = String(value ?? "").trim();
            if (!text || text.length > 4096)
                throw new Error("Enter an address or search (up to 4096 characters).");
            if (!searchOnly && allowed(text)) return text;
            if (!searchOnly && text.includes("://")) throw new Error("Only HTTP and HTTPS addresses are allowed.");
            if (!searchOnly && !/\s/u.test(text)) {
                try {
                    const candidate = new URL(`https://${text}`);
                    if (
                        (candidate.hostname.includes(".") ||
                            ["localhost", "[::1]"].includes(candidate.hostname)) &&
                        allowed(candidate.href)
                    )
                        return candidate.href;
                } catch {
                    // An ordinary query need not be a valid URL.
                }
            }
            if (!searchOnly && /^[a-z][a-z0-9+.-]*:/iu.test(text))
                throw new Error("Only HTTP and HTTPS addresses are allowed.");
            const prefix = searchPrefix();
            if (typeof prefix !== "string" || !allowed(prefix))
                throw new Error("The search provider is unavailable.");
            const address = prefix + encodeURIComponent(text);
            if (!allowed(address)) throw new Error("The encoded search address is too long.");
            return address;
        };
        const submit = (input, action, searchOnly = false) => {
            if (disposed) return undefined;
            const element = input?.element ?? input;
            try {
                const address = normalize(element?.value, searchOnly);
                element.value = address;
                element.setCustomValidity?.("");
                element.removeAttribute?.("aria-invalid");
                return action();
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                element?.setCustomValidity?.(message);
                element?.setAttribute?.("aria-invalid", "true");
                element?.reportValidity?.();
                return undefined;
            }
        };
        const gameSearch = (instance, field: () => any, select: (title: string) => void) => {
            const target = identify(instance);
            if (!target?.title) return null;
            return react.createElement(ui.smallButton ?? ui.dialogButton, {
                key: "steam-ui-native-game-search",
                "aria-label": `Search ${target.title}`,
                title: `Search ${target.title}`,
                style: { flexShrink: 0, whiteSpace: "nowrap" },
                onClick: () => {
                    if (disposed || !sameTarget(target, identify(instance))) return;
                    const input = field();
                    const element = input?.element ?? input;
                    if (!element) return;
                    const query = `${target.title} `;
                    element.value = query;
                    select(target.title);
                    element.setCustomValidity?.("");
                    element.removeAttribute?.("aria-invalid");
                    element.focus();
                    element.click();
                    element.setSelectionRange?.(query.length, query.length);
                },
            }, "Game search");
        };
        function SteamUiEditableBrowserFrame(props) {
            useChrome();
            if (!disposed) {
                nativeEditable.set(props.browser, {
                    present: Object.hasOwn(props, "bCanChangeURL"),
                    value: props.bCanChangeURL,
                });
            }
            return react.createElement(frame, disposed ? props : { ...props, bCanChangeURL: true });
        }
        // Keep Valve's actual input and its OSK callbacks. Only its explicit commit is normalized;
        // web content stays in Steam's separate browser view and receives no bridge or host objects.
        function SteamUiEditableBrowserBar(props) {
            useChrome();
            const instance = useWindow();
            const gameQuery = react.useRef(null);
            if (disposed) {
                const original = nativeEditable.get(props.browser);
                const restored = { ...props };
                if (original?.present) restored.bCanChangeURL = original.value;
                else delete restored.bCanChangeURL;
                return bar(restored);
            }
            let input;
            let keyboardHandle;
            const visit = (element) => {
                if (!react.isValidElement(element)) return element;
                const fields = element.props;
                const nativeRef = fields.ref ?? element.ref;
                if (typeof fields.onEnterKeyPress === "function" && typeof nativeRef === "function") {
                    const commit = (...args) => submit(input, () => fields.onEnterKeyPress(...args),
                        !!gameQuery.current && String(input?.element?.value ?? input?.value).startsWith(gameQuery.current));
                    return react.cloneElement(element, {
                        "aria-label": "Search or enter an address",
                        placeholder: "Search or enter an address",
                        maxLength: 4096,
                        ref: (value) => {
                            input = value;
                            nativeRef(value);
                        },
                        refKeyboardHandle: (value) => {
                            keyboardHandle = value;
                            const original = fields.refKeyboardHandle;
                            if (typeof original === "function") original(value);
                            else if (original) original.current = value;
                        },
                        onEnterKeyPress: commit,
                        // Valve consumes onEnterKeyPress in its virtual-keyboard props. Its
                        // Dialog.Input forwards onKeyDown through to the real DOM input.
                        onKeyDown: (event) => {
                            if (disposed) return undefined;
                            const forwarded = fields.onKeyDown?.(event);
                            if (
                                event.key !== "Enter" || event.isComposing ||
                                event.nativeEvent?.isComposing || event.keyCode === 229 ||
                                event.nativeEvent?.keyCode === 229 || event.defaultPrevented
                            )
                                return forwarded;
                            event.preventDefault();
                            event.stopPropagation?.();
                            if (event.repeat) return forwarded;
                            const result = commit();
                            const element = input?.element ?? input;
                            // React does not consume VKClose. Use this field's existing native
                            // keyboard handle only while it still owns the focused input.
                            if (
                                result === "VKClose" && !disposed && element &&
                                element.ownerDocument?.activeElement === element
                            )
                                keyboardHandle?.HideVirtualKeyboard?.();
                            return result;
                        },
                        onChange: (...args) => {
                            if (disposed) return undefined;
                            const element = input?.element ?? input;
                            element?.setCustomValidity?.("");
                            element?.removeAttribute?.("aria-invalid");
                            return fields.onChange?.(...args);
                        },
                    });
                }
                return fields.children === undefined
                    ? element
                    : react.cloneElement(element, {}, react.Children.map(fields.children, visit));
            };
            const native = visit(bar({ ...props, bCanChangeURL: true }));
            const action = gameSearch(instance, () => input, (title) => { gameQuery.current = title; });
            return action ? react.cloneElement(native, {}, native.props.children, action) : native;
        }
        function SteamUiEditableDesktopInput({ nativeType, nativeRef, ...props }) {
            useChrome();
            const instance = useWindow();
            const gameQuery = react.useRef(null);
            if (disposed) return react.createElement(nativeType, { ...props, ref: nativeRef });
            let input;
            const control = react.createElement(nativeType, {
                ...props,
                "aria-label": "Search or enter an address",
                placeholder: "Search or enter an address",
                maxLength: 4096,
                ref: (value) => {
                    input = value;
                    nativeRef?.(value);
                },
                onKeyPress: (event) => {
                    if (disposed) return undefined;
                    if (event.key !== "Enter") return props.onKeyPress(event);
                    event.preventDefault();
                    return submit(event.target, () => props.onKeyPress(event),
                        !!gameQuery.current && String(event.target?.value).startsWith(gameQuery.current));
                },
                onChange: (event) => {
                    if (disposed) return undefined;
                    event.target?.setCustomValidity?.("");
                    event.target?.removeAttribute?.("aria-invalid");
                    return props.onChange?.(event);
                },
            });
            return react.createElement(react.Fragment, null, control,
                gameSearch(instance, () => input, (title) => { gameQuery.current = title; }));
        }
        const name = "steam-ui.native-browser-address";
        const result = interceptElements(jsx, name, (create, type, props, key) => {
            if (
                type === frame &&
                props?.external === true &&
                props.path === externalPath &&
                props.browser?.name === "ExternalWeb"
            )
                return create(SteamUiEditableBrowserFrame, props, key);
            if (
                type === bar && props?.external === true &&
                props.browser?.name === "ExternalWeb" && nativeEditable.has(props.browser)
            )
                return create(SteamUiEditableBrowserBar, props, key);
            // The desktop overlay already enables editing. Its native toolbar commits through a
            // key event instead of the gamepad field's onEnterKeyPress callback.
            if (
                props?.type === "input" &&
                sourceMatches(props.onKeyPress, ['"Enter"', ".Browser.LoadURL("])
            ) {
                const { ref, ...nativeProps } = props;
                return create(SteamUiEditableDesktopInput, { ...nativeProps, nativeType: type, nativeRef: ref }, key);
            }
            return undefined;
        });
        if (!result.ok) return false;
        releaseChrome = () => {
            releaseElements(jsx, name);
            for (const refresh of mounted) refresh();
            mounted.clear();
        };
        return true;
    };
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        releaseChrome?.();
        releaseChrome = null;
        for (const reference of subscriptions) {
            const entry = reference.deref();
            if (entry) release(entry);
        }
        subscriptions.clear();
    };
    return { useWindow, identify, open, installChrome, dispose };
}
