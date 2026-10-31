/**
 * Adds host actions to Steam game context menus while preserving native entries.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
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

    let react;
    let menuComponent: any = null;
    let itemType: any = null;
    let submenuType: any = null;
    let jsxRuntime;
    // The ledger of the install in progress: the native menu class is claimed when it is first
    // created, after the install, and those claims are unwound with the rest.
    let held: GateLedger | null = null;
    let desired: { items: any[]; revision: number } = { items: [], revision: 0 };
    let lastOutcome = "never rendered";
    // Why the last native menu could not be used, kept apart from the gate's own install error.
    let menuError = "";
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
            if (gate.installed()) local.changed();
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
            menuError = "Game menu app lookup failed: " + String(error);
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
        if (!gate.installed() || ambiguous || nativeRefused || !itemType || desired.items.length === 0)
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
                        if (!gate.installed() || ambiguous || nativeRefused) return;
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

    // Pure: React and the JSX runtime, which observation needs and nothing else. The native menu
    // class is recognised by its methods when Steam first creates one, so where Steam's private
    // factory lives is required by nothing.
    const find = (runtime) => {
        const resolvedReact = resolveReact(runtime);
        if (!resolvedReact) return gateIncompatible("React runtime was not a unique match", { react: 0 });
        let jsx;
        try {
            jsx = runtime.resolve([...JsxRuntimeTokens]);
        } catch (error) {
            return gateIncompatible(`JSX runtime was not a unique match: ${String(error)}`, { react: 1, jsx: 0 });
        }
        if (typeof jsx?.jsx !== "function" || typeof jsx?.jsxs !== "function") {
            return gateIncompatible("JSX runtime was not a unique match", { react: 1, jsx: 0 });
        }
        return gateReady({ react: resolvedReact, jsx }, { react: 1, jsx: 1 });
    };

    const observeAction = (instance, original, args) => {
        const actions = react.Children.toArray(instance.props?.additionalActions);
        const own = actions.find((action) => action?.type === SteamUiGameContextMenuItems);
        if (own) own.props.context.instance = instance;
        const result = original.apply(instance, args);
        if (!Array.isArray(result?.rgCanAdd) || !Array.isArray(result?.rgCanRemove)) {
            if (gate.installed()) {
                nativeRefused = true;
                menuError = "Observed game menu returned incompatible collection actions";
                notifyAfterRender();
            }
            return result;
        }
        if (gate.installed()) nativeObserved = true;
        const nativeItems = [
            ...(Array.isArray(result?.rgCanAdd) ? result.rgCanAdd : []),
            ...(Array.isArray(result?.rgCanRemove) ? result.rgCanRemove : []),
        ];
        const found = nativeItems.map((element) => findItemType(element)).find(Boolean);
        if (gate.installed() && found && found !== itemType) {
            itemType = found;
            // A native action is produced during its parent's render. Notify mounted host children
            // after that render, never synchronously update a sibling from inside it.
            notifyAfterRender();
        } else if (gate.installed() && !itemType) {
            lastOutcome = "native action item type absent";
        }
        return result;
    };

    const claimMenuAction = (candidate) => {
        if (!candidate || !candidate.prototype || menuComponent === candidate) {
            return;
        }
        if (menuComponent) {
            menuError = "Game context menu component changed while claimed";
            return;
        }
        const ledger = held;
        if (!ledger) return;
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
            menuError = "Game context menu action claim failed: " + claim.error;
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
                        gate.installed() &&
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
            menuError = "Game context submenu claim failed: " + manageClaim.error;
            return;
        }
        // Both methods go on the install's ledger as one claim, so removal hands them back with the
        // rest and a failed release of either is retried.
        const prototype = candidate.prototype;
        ledger.add(
            "native menu methods",
            () => {
                const action = releaseMember(prototype, ActionMethod, actionClaimKeys);
                return action.ok ? releaseMember(prototype, ManageMethod, actionClaimKeys) : action;
            },
            () =>
                memberClaimed(prototype, ActionMethod, actionClaimKeys) &&
                memberClaimed(prototype, ManageMethod, actionClaimKeys),
        );
        menuComponent = candidate;
        menuError = "";
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
            menuError = "Competing native game menu classes were observed";
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
            if (gate.installed()) local.changed();
            if (typeof cleanup === "function") {
                return () => {
                    context.instance = null;
                    cleanup();
                    if (gate.installed()) local.changed();
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

    const gate = defineGate({
        name: "game-context-menu",
        find,
        bind: (handles, ledger) => {
            react = handles.react;
            jsxRuntime = handles.jsx;
            held = ledger;
            // Released last, after every claim taken on its account.
            ledger.cleanup("menu state", () => {
                held = null;
            });
            const claim = ledger.elements(jsxRuntime, patchId, captureMenu);
            if (!claim.ok) return claim.error ?? "Game context menu JSX interception failed";
            // Popup hosts may construct native menu classes through React instead of JSX.
            const createClaim = ledger.member(
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
            if (!createClaim.ok) return "Game context menu React interception failed: " + createClaim.error;
            ledger.subscribe(patchId, (state) => {
                const items = Array.isArray(state?.items) ? state.items.filter(validItem) : [];
                desired = {
                    items,
                    revision: Number.isSafeInteger(state?.revision) ? state.revision : 0,
                };
                local.changed();
            });
            return { observing: true };
        },
        // Observation holds while one native menu class is in use: competing classes or an
        // incompatible native result are refusals the host must see, not a working menu.
        holds: () => !ambiguous && !nativeRefused,
        unbind: () => {
            held = null;
            desired = { items: [], revision: 0 };
            menuComponent = null;
            itemType = null;
            submenuType = null;
            ambiguous = false;
            nativeObserved = false;
            nativeRefused = false;
            menuError = "";
            lastOutcome = "removed";
            local.changed();
        },
        status: () => {
            const installed = gate.installed();
            return {
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
                menuError,
            };
        },
    });
    return gate;
}

registerGate("gameContextMenu", createGameContextMenu());
