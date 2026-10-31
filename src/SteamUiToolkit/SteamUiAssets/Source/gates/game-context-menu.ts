/**
 * Adds host actions to Steam game context menus while preserving native entries.
 * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
 */

// Trusted host renderers receive only the native types observed on this exact menu.
const steamGameContextMenuRenderers = new Map<string, (context: any) => unknown>();
const registerSteamGameContextMenuRenderer = (
    template: string,
    render: (context: any) => unknown,
) => steamGameContextMenuRenderers.set(template, render);

function createGameContextMenu() {
    const patchId = "steam-ui.game-context-menu";
    const actionClaimKeys = {
        marker: "__steamUiGameContextMenuActionClaimed",
        original: "__steamUiGameContextMenuActionOriginal",
    } as const;
    const ActionMethod = "GetCollectionManagementActions";
    const createClaimKeys = {
        marker: "__steamUiGameContextMenuCreateClaimed",
        original: "__steamUiGameContextMenuCreateOriginal",
    } as const;
    const ManageMethod = "BuildManageSubmenu";
    const MenuTokens = ["GetTargetApps", "BuildManageSubmenu", "GetPrimaryActionMenuItem"];
    // How deep into Steam's own menu tree the item list is looked for; it bounds the walk, not the items.
    const MaximumDepth = 10;

    let runtime;
    let react;
    let menuComponent: any = null;
    let itemType: any = null;
    let submenuType: any = null;
    let jsxRuntime;
    let installed = false;
    let unsubscribe: (() => void) | null = null;
    let desired: { items: any[]; revision: number } = { items: [], revision: 0 };
    let lastOutcome = "never rendered";
    let lastError = "";
    let ambiguous = false;
    let nativeObserved = false;
    let nativeRefused = false;
    const local = createLocalStore();
    let itemNotificationQueued = false;
    const notifyAfterRender = () => {
        if (itemNotificationQueued) return;
        itemNotificationQueued = true;
        queueMicrotask(() => {
            itemNotificationQueued = false;
            if (installed) local.changed();
        });
    };

    const validItem = (item) =>
        item &&
        typeof item.id === "string" &&
        item.id.length > 0 &&
        typeof item.label === "string" &&
        item.label.length > 0;

    const appIdFor = (instance) => {
        try {
            const apps = instance?.GetTargetApps?.();
            const appId = Array.isArray(apps) && apps.length === 1 ? apps[0]?.appid : null;
            return Number.isSafeInteger(appId) && appId > 0 ? appId : null;
        } catch (error) {
            lastError = "Game menu app lookup failed: " + String(error);
            return null;
        }
    };

    // Borrow the type from Steam's own native action return, including one that arrives after an
    // initially empty/loading menu. Never invoke the method ourselves to discover a component.
    const findItemType = (element, depth = 0) => {
        if (depth > MaximumDepth || !react.isValidElement(element)) return null;
        if (typeof element.props?.onSelected === "function" && element.type) return element.type;
        for (const child of react.Children.toArray(element.props?.children)) {
            const found = findItemType(child, depth + 1);
            if (found) return found;
        }
        return null;
    };

    // Steam's MobX observer installs an immutable, instance-owned render on its first call. A
    // prototype wrapper therefore sees only that call. Put actions in the native additionalActions
    // slot instead, so the observer's own render includes them on every later data update.
    function SteamUiGameContextMenuItems({ context }) {
        react.useSyncExternalStore(local.subscribe, local.revision, local.revision);
        if (!installed || ambiguous || nativeRefused || !itemType || desired.items.length === 0)
            return null;
        const appId = appIdFor(context.instance);
        if (appId === null) return null;
        lastOutcome = `app=${appId} commands=${desired.items.length} native-additional-actions`;
        return desired.items.map((item) => {
            if (item.template) {
                const render = steamGameContextMenuRenderers.get(item.template);
                return submenuType && render
                    ? render({
                          react,
                          itemType,
                          submenuType,
                          appId,
                          label: item.label,
                          key: `steam-ui-game-context-menu-${item.id}`,
                          currentAppId: () => appIdFor(context.instance),
                      })
                    : null;
            }
            return react.createElement(
                itemType,
                {
                    key: `steam-ui-game-context-menu-${item.id}`,
                    onSelected: () => {
                        if (!installed || ambiguous || nativeRefused) return;
                        const currentAppId = appIdFor(context.instance);
                        if (currentAppId === null) return;
                        // The window the menu was used in; a game's overlay opens the page itself.
                        const owner = steamInteractionWindow();
                        void request(patchId, "activate", {
                            appId: currentAppId,
                            id: item.id,
                        }).then(
                            (answer: any) => {
                                if (answer?.route) navigateSteamRoute(answer.route, owner);
                            },
                            () => {
                                // A refusal stays host-authoritative and must not make Steam's menu fail.
                            },
                        );
                    },
                },
                item.label,
            );
        });
    }

    const resolve = () => {
        runtime = getWebpackRuntime("game-context-menu");
        react = resolveReact(runtime);
        if (!react) {
            lastError = "React runtime was not a unique match";
            return false;
        }
        jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
        if (!jsxRuntime) {
            lastError = "JSX runtime was not a unique match";
            return false;
        }
        return true;
    };

    const observeAction = (instance, original, args) => {
        const actions = react.Children.toArray(instance.props?.additionalActions);
        const own = actions.find((action) => action?.type === SteamUiGameContextMenuItems);
        if (own) own.props.context.instance = instance;
        const result = original.apply(instance, args);
        if (!Array.isArray(result?.rgCanAdd) || !Array.isArray(result?.rgCanRemove)) {
            if (installed) {
                nativeRefused = true;
                lastError = "Observed game menu returned incompatible collection actions";
                notifyAfterRender();
            }
            return result;
        }
        if (installed) nativeObserved = true;
        const nativeItems = [
            ...(Array.isArray(result?.rgCanAdd) ? result.rgCanAdd : []),
            ...(Array.isArray(result?.rgCanRemove) ? result.rgCanRemove : []),
        ];
        const found = nativeItems.map((element) => findItemType(element)).find(Boolean);
        if (installed && found && found !== itemType) {
            itemType = found;
            // A native action is produced during its parent's render. Notify mounted host children
            // after that render, never synchronously update a sibling from inside it.
            notifyAfterRender();
        } else if (installed && !itemType) {
            lastOutcome = "native action item type absent";
        }
        return result;
    };

    const claimMenuAction = (candidate) => {
        if (!candidate || !candidate.prototype || menuComponent === candidate) {
            return;
        }
        if (menuComponent) {
            lastError = "Game context menu component changed while claimed";
            return;
        }
        const claim = claimMember(
            candidate.prototype,
            ActionMethod,
            actionClaimKeys,
            (original: any) =>
                function SteamUiGameContextMenuAction(this: any, ...args: any[]) {
                    return observeAction(this, original, args);
                },
        );
        if (!claim.ok) {
            lastError = "Game context menu action claim failed: " + claim.error;
            return;
        }
        const manageClaim = claimMember(
            candidate.prototype,
            ManageMethod,
            actionClaimKeys,
            (original: any) =>
                function SteamUiGameContextMenuManage(this: any, ...args: any[]) {
                    const result = original.apply(this, args);
                    if (
                        installed &&
                        react.isValidElement(result) &&
                        typeof result.props?.label === "string"
                    ) {
                        const found = findItemType(result);
                        if (submenuType !== result.type || (found && itemType !== found)) {
                            submenuType = result.type;
                            if (found) itemType = found;
                            notifyAfterRender();
                        }
                    }
                    return result;
                },
        );
        if (!manageClaim.ok) {
            releaseMember(candidate.prototype, ActionMethod, actionClaimKeys);
            lastError = "Game context submenu claim failed: " + manageClaim.error;
            return;
        }
        menuComponent = candidate;
        lastError = "";
    };

    // Keep the exact class transform for later parent renders too. The ref retains the actual native
    // instance, so multi-select uses GetTargetApps rather than guessing from the overview prop.
    // MobX may put render on an accessor or an observer superclass. Do not read it: that executes
    // the observer getter before React supplies an instance. Match authored methods.
    const menuShaped = (prototype) => {
        const descriptor = (name) => {
            let owner = prototype;
            while (owner && owner !== Object.prototype) {
                const value = Object.getOwnPropertyDescriptor(owner, name);
                if (value) return value;
                owner = Object.getPrototypeOf(owner);
            }
        };
        return (
            MenuTokens.every((name) => {
                const method = descriptor(name);
                return typeof method?.value === "function" || typeof method?.get === "function";
            }) && typeof descriptor(ActionMethod)?.value === "function"
        );
    };
    // Every element Steam creates passes the capture, so each component type is judged once. A class
    // keeps the methods it was defined with, and this gate's own claims keep them functions.
    const shapes = new WeakMap<Function, boolean>();
    const captureMenu = (create, candidate, props, key) => {
        if (typeof candidate !== "function" || !candidate.prototype) return;
        let shaped = shapes.get(candidate);
        if (shaped === undefined) {
            shaped = menuShaped(candidate.prototype);
            shapes.set(candidate, shaped);
        }
        if (!shaped) return;
        if (menuComponent && candidate !== menuComponent) {
            ambiguous = true;
            lastError = "Competing native game menu classes were observed";
            notifyAfterRender();
            return;
        }
        if (ambiguous || nativeRefused) return;
        if (!menuComponent) claimMenuAction(candidate);
        if (candidate !== menuComponent) return;
        const context: { instance: any } = { instance: null };
        const nativeRef = props?.ref;
        const ref = (instance) => {
            context.instance = instance;
            const cleanup = typeof nativeRef === "function" ? nativeRef(instance) : undefined;
            if (nativeRef && typeof nativeRef === "object") nativeRef.current = instance;
            if (installed) local.changed();
            if (typeof cleanup === "function") {
                return () => {
                    context.instance = null;
                    cleanup();
                    if (installed) local.changed();
                };
            }
        };
        const nativeActions = react.Children.toArray(props?.additionalActions).filter(
            (action) => action?.type !== SteamUiGameContextMenuItems,
        );
        return create(
            candidate,
            {
                ...props,
                ref,
                additionalActions: [
                    ...nativeActions,
                    react.createElement(SteamUiGameContextMenuItems, { key: patchId, context }),
                ],
            },
            key,
        );
    };

    const install = () => {
        if (installed) return { ok: true, alreadyInstalled: true };
        if (
            !attemptResolution(
                resolve,
                (error) => (lastError = "Game context menu resolution failed: " + String(error)),
            )
        ) {
            return { ok: false, error: lastError };
        }
        const claim = interceptElements(jsxRuntime, patchId, captureMenu);
        if (!claim.ok) {
            lastError = claim.error ?? "Game context menu JSX interception failed";
            return { ok: false, error: lastError };
        }
        // Popup hosts may construct native menu classes through React instead of JSX.
        const createClaim = claimMember(
            react,
            "createElement",
            createClaimKeys,
            (original: any) =>
                function SteamUiGameContextMenuCreate(type, props, ...children) {
                    // Only a class can be the menu; anything else is created untouched, unexamined.
                    if (typeof type !== "function" || !type.prototype)
                        return original.call(react, type, props, ...children);
                    const supplied = children.length
                        ? { ...props, children: children.length === 1 ? children[0] : children }
                        : props;
                    const replacement = captureMenu(
                        (candidate, next, key) =>
                            original.call(
                                react,
                                candidate,
                                key === undefined ? next : { ...next, key },
                            ),
                        type,
                        supplied,
                        props?.key,
                    );
                    return replacement === undefined
                        ? original.call(react, type, props, ...children)
                        : replacement;
                },
        );
        if (!createClaim.ok) {
            releaseElements(jsxRuntime, patchId);
            lastError = "Game context menu React interception failed: " + createClaim.error;
            return { ok: false, error: lastError };
        }
        installed = true;
        lastError = "";
        unsubscribe = subscribe(patchId, (state) => {
            const items = Array.isArray(state?.items) ? state.items.filter(validItem) : [];
            desired = {
                items,
                revision: Number.isSafeInteger(state?.revision) ? state.revision : 0,
            };
            local.changed();
        });
        return { ok: true, installed: true, observing: true };
    };

    // Both claims go back before the gate forgets it holds them. Clearing `installed` and
    // `menuComponent` first would answer `absent` on every later remove() while the action claim and
    // the JSX interception were still live, with nothing left that names what to release.
    const remove = () => {
        if (!installed) return { ok: true, absent: true };
        const releasedElements = releaseElements(jsxRuntime, patchId);
        const releasedAction = releaseMember(
            menuComponent?.prototype,
            ActionMethod,
            actionClaimKeys,
        );
        const releasedManage = releaseMember(
            menuComponent?.prototype,
            ManageMethod,
            actionClaimKeys,
        );
        const releasedCreate = releaseMember(react, "createElement", createClaimKeys);
        if (
            !releasedAction.ok ||
            !releasedElements.ok ||
            !releasedManage.ok ||
            !releasedCreate.ok
        ) {
            lastError =
                releasedAction.error ??
                releasedElements.error ??
                releasedManage.error ??
                releasedCreate.error ??
                "Game context menu release failed";
            return { ok: false, error: lastError };
        }
        installed = false;
        unsubscribe = endSubscription(unsubscribe);
        desired = { items: [], revision: 0 };
        menuComponent = null;
        itemType = null;
        submenuType = null;
        ambiguous = false;
        nativeObserved = false;
        nativeRefused = false;
        lastOutcome = "removed";
        local.changed();
        return { ok: true, removed: true };
    };

    const status = () => ({
        ok: true,
        installed,
        resolved: !!runtime && !!react,
        observing:
            installed &&
            elementsIntercepted(jsxRuntime, patchId) &&
            memberClaimed(react, "createElement", createClaimKeys),
        ambiguous,
        nativeObserved,
        nativeRefused,
        itemTypeResolved: !!itemType,
        submenuTypeResolved: !!submenuType,
        nativeMenuState: !installed
            ? "removed"
            : ambiguous || nativeRefused
              ? "refused"
              : nativeObserved
                ? "native-menu-observed"
                : menuComponent
                  ? "native-class-held"
                  : "awaiting-native-menu",
        menuClaimed: memberClaimed(menuComponent?.prototype, ActionMethod, actionClaimKeys),
        items: desired.items.length,
        revision: desired.revision,
        lastOutcome,
        lastError,
    });

    return { install, remove, status };
}

registerGate("gameContextMenu", createGameContextMenu());
