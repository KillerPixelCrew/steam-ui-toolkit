// What the gates that walk Steam's React output have in common, and the lifecycle steps every gate
// repeats.
//
// Constants and functions only. Nothing here runs while the bundle is evaluated, and gates call it
// only once the whole bundle has run, so this fragment's place in the discovered order does not
// matter. Fingerprints for a module more than one surface resolves live here once, so two gates
// cannot drift onto different spellings of the same module.

// Steam's React module, by the four names only it carries together.
const ReactTokens = [
    "react.transitional.element",
    "useState",
    "cloneElement",
    "createElement",
] as const;
// The module holding Steam's SliderField, DropDownField and ToggleField.
const FieldTokens = ["DialogSlider_Container", "DropDownField", "SliderField"] as const;
// DropDownField within that module, by the markers of its own body.
const DropdownMarkers = [
    "contextMenuPositionOptions",
    "childrenContainerWidth",
    "menuLabel",
] as const;
// Steam's library item class map, by three class names only it carries together: the library
// capsule is styled by it and the library badge reads its tile classes from it.
const SteamLibraryClassTokens = [
    'ControllerSupportIcon:"',
    'LibraryItemIcons:"',
    'LibraryItemBox:"',
] as const;
// The JSX runtime module: `jsx` and `jsxs` beside React's element marker.
const JsxRuntimeTokens = ["react.transitional.element", ".jsx", ".jsxs"] as const;
// The client settings store: the store class's own getter and its deferred-settings set.
const SettingsTokens = ["get clientSettings()", "m_setDeferredSettings"] as const;
// mobx-react-lite's own startup check, present once in the client.
const ObserverTokens = ["mobx-react-lite requires React with Hooks support"] as const;
// The localization module.
const LocalizationTokens = [
    "Attempting to localize token",
    "Unable to find localization token",
    "LocalizeString",
] as const;

// Steam's React exports, or null when the module is not a unique match.
const resolveReact = (runtime) => {
    const factory = runtime.findUnique(ReactTokens);
    return factory ? runtime(factory[0]) : null;
};

// Steam's Panel joins its navigation graph and maps onActivate to mouse and gamepad OK.
// Generic button forwardRefs have identical closure bodies, so their source cannot identify them.
const NativeFocusableTokens = ["focusableIfEmpty", "onActivate", '"Panel"'] as const;
const resolveNativeFocusable = (runtime) =>
    runtime.exported([...NativeFocusableTokens], (value) => {
        if (typeof value !== "function") return false;
        const source = String(value);
        return ["onActivate", "onCancel", "focusableIfEmpty", "focusClassName"].every((token) =>
            source.includes(token),
        );
    });

// Native Steam controls shared by plugin pages and toolkit-owned surfaces. Component export names
// are minified and change between client builds, so every control is selected from a uniquely
// fingerprinted provider by its own behavior. Consumers must treat a null optional control as an
// unavailable capability rather than replacing it with an imitation.
const uniqueSteamExport = (exports, predicate) => {
    const matches = new Set<any>();
    for (const name of Object.keys(exports ?? {})) {
        try {
            const value = exports[name];
            if (predicate(value)) matches.add(value);
        } catch {
            // An export whose getter or shape test throws is not the requested component.
        }
    }
    return matches.size === 1 ? [...matches][0] : null;
};

const sourceOfSteamComponent = (value) => {
    if (typeof value === "function") return String(value);
    return typeof value?.render === "function" ? String(value.render) : "";
};

// The class names a component's source writes in its string literals, whether its author passed
// them to the class-name helper one by one or as one string. The quotes and the separators between
// the arguments are the minifier's; the names are the author's.
const steamClassNamesOf = (source: string) => {
    const names = new Set<string>();
    for (const literal of source.matchAll(/"([^"\\]*)"|'([^'\\]*)'/gu)) {
        for (const name of (literal[1] ?? literal[2]).split(/\s+/u)) {
            if (name) names.add(name);
        }
    }
    return names;
};

// One of Steam's dialog buttons by the classes it draws with: DialogButton and _DialogLayout, and
// the variant (Secondary, Primary, Small) that tells the buttons apart.
const isSteamDialogButton = (value, variant: string) => {
    const names = steamClassNamesOf(sourceOfSteamComponent(value));
    return names.has("DialogButton") && names.has("_DialogLayout") && names.has(variant);
};

const optionalSteamExport = (runtime, tokens, predicate) => {
    try {
        return runtime.exported(tokens, predicate);
    } catch {
        return null;
    }
};

/**
 * Resolves React and native field components for row renderers.
 * @param runtime Shared module resolver.
 * @returns Available field components, or null when foundational module evidence is absent.
 */
const resolveSteamFieldComponents = (runtime) => {
    const react = resolveReact(runtime);
    const fieldsFactory = runtime.findUnique(FieldTokens);
    if (!react || !fieldsFactory) return null;

    const fields = runtime(fieldsFactory[0]);
    const sliderField = uniqueSteamExport(fields, (value) => {
        if (typeof value !== "function") return false;
        const source = String(value);
        return ["onChangeComplete", "notchCount", "valueSuffix", "explainerTitle"].every((token) =>
            source.includes(token),
        );
    });
    const dropdown = uniqueSteamExport(fields, (value) => {
        if (typeof value !== "function") return false;
        const source = String(value);
        return DropdownMarkers.every((token) => source.includes(token));
    });
    // The bare dropdown that DropDownField wraps in a labelled row: a toolbar wants the button on
    // its own. Chosen the way decky-frontend-lib chooses it, by the two prototype members only it
    // declares, tested by name so no getter runs. Wanted, not required: a page that lacks it draws
    // the labelled field.
    const dropdownControl = uniqueSteamExport(
        fields,
        (value) =>
            typeof value === "function" &&
            !!value.prototype &&
            "SetSelectedOption" in value.prototype &&
            "BuildMenu" in value.prototype,
    );
    const toggleField = uniqueSteamExport(fields, (value) => {
        const source = sourceOfSteamComponent(value);
        return source.includes("OnToggleChange") && source.includes("this.Toggle()");
    });
    const dialogButton = uniqueSteamExport(fields, (value) =>
        isSteamDialogButton(value, "Secondary"),
    );
    const dialogButtonPrimary = uniqueSteamExport(fields, (value) =>
        isSteamDialogButton(value, "Primary"),
    );
    // The class that DEFINES the validators, not one that merely inherits them. A class extending
    // TextField answers `typeof validateUrl === "function"` through its prototype chain, and the
    // 2026-09-24 client exports such a subclass beside the base: two fits, no unique match, no
    // textField, and every settings page that needs one went Degraded. Own properties name the base.
    const textField = uniqueSteamExport(
        fields,
        (value) =>
            typeof value === "function" &&
            Object.prototype.hasOwnProperty.call(value, "validateUrl") &&
            Object.prototype.hasOwnProperty.call(value, "validateEmail") &&
            typeof value.validateUrl === "function" &&
            typeof value.validateEmail === "function",
    );

    return {
        react,
        sliderField,
        dropdown,
        dropdownControl,
        toggleField,
        dialogButton,
        dialogButtonPrimary,
        textField,
    };
};

// Steam's routed settings sidebar, by the prop only it reads.
const SteamRoutedPagesTokens = ["disableRouteReporting"] as const;
// The generic confirm modal, by three props only its module names together.
const SteamConfirmModalTokens = ["strMiddleButtonText", "bProgressDialog", "bAlertDialog"] as const;

/**
 * Resolves every native control a host page, a settings page or a section draws with, from shared
 * module fingerprints and export shapes. It is the one resolver: a component a page does not need
 * costs nothing but its match, and what a page cannot draw without goes in its `required` list.
 * @param runtime Shared module resolver; matching factory loads may throw.
 * @returns The available component collection, or null when base fields cannot resolve.
 */
const resolveSteamUiComponents = (runtime) => {
    const fields = resolveSteamFieldComponents(runtime);
    if (!fields) return null;

    const focusable = resolveNativeFocusable(runtime);

    // The tabbed page is the module's one export that wraps a function component (a memo, here an
    // observer), the rest being plain functions and a context; two such exports are refused.
    const tabsFactory = runtime.findUnique([".TabRowTabs", "activeTab:"]);
    const tabs = tabsFactory
        ? uniqueSteamExport(runtime(tabsFactory[0]), (value) => typeof value?.type === "function")
        : null;
    const modalRoot = optionalSteamExport(
        runtime,
        ["Either closeModal or onCancel should be passed to GenericDialog. Classes: "],
        (value) =>
            typeof value === "function" &&
            String(value).includes(
                "Either closeModal or onCancel should be passed to GenericDialog",
            ),
    );
    const showModalRaw = optionalSteamExport(
        runtime,
        ["props.bDisableBackgroundDismiss"],
        (value) =>
            typeof value === "function" &&
            String(value).includes("props.bDisableBackgroundDismiss") &&
            !value?.prototype?.Cancel,
    );
    const showModal = showModalRaw
        ? (modal, parent = window, props: any = {}) =>
              showModalRaw(
                  modal,
                  parent,
                  props.strTitle ?? "",
                  { bHideMainWindowForPopouts: false, ...props },
                  undefined,
                  { bHideActions: props.bHideActionIcons },
              )
        : null;

    // Steam's own checkbox, the DialogCheckbox its dialogs tick options with. It lives in its own
    // module beside the toggle's base class and takes the same props: label, description, checked,
    // onChange, disabled. Chosen by what its author wrote - the class name it draws and the two
    // methods decky-frontend-lib also picks it by - never by how the minifier joined them. Wanted,
    // not required: a page that needs it says so, and `steamCheckbox` falls back to the toggle.
    const checkbox = optionalSteamExport(
        runtime,
        ["DialogCheckbox_Container"],
        (value) =>
            typeof value === "function" &&
            !!value.prototype &&
            "SetChecked" in value.prototype &&
            "Toggle" in value.prototype &&
            String(value).includes('"DialogCheckbox"'),
    );

    // The settings page's own fields: its section, its name/value row, the small button, the inline
    // list builder, the routed sidebar and Steam's confirm modal.
    const fieldModule = runtime(runtime.findUnique(FieldTokens)[0]);
    const settingsSection = uniqueSteamExport(fieldModule, (value) =>
        sourceOfSteamComponent(value).includes('"DialogSettingsSection"'),
    );
    // The name/value field sets `inlineWrap` to "shift-children-below" and makes the row focusable.
    // The field layout it draws through reads the same props, but takes a second argument beside
    // them; a component takes its props alone.
    const valueField = uniqueSteamExport(fieldModule, (value) => {
        if (typeof value !== "function" || value.length !== 1) return false;
        const source = String(value);
        return ["inlineWrap", '"shift-children-below"', "focusable"].every((token) => source.includes(token));
    });
    const smallButton = uniqueSteamExport(fieldModule, (value) => isSteamDialogButton(value, "Small"));
    // Steam's inline list-builder input is independent of the popup menu lifecycle.
    const listBuilder = uniqueSteamExport(fieldModule, (value) => {
        const source = sourceOfSteamComponent(value);
        return source.includes("DialogListBuilderInput_List") && source.includes("OnBackgroundClick");
    });
    const routedPages = optionalSteamExport(
        runtime,
        [...SteamRoutedPagesTokens],
        (value) =>
            typeof value === "function" &&
            String(value).includes("disableRouteReporting") &&
            String(value).includes("pages"),
    );
    // Wanted, not required: `showSteamUiConfirm` draws the kit's own confirmation without it.
    const confirmModal = optionalSteamExport(runtime, [...SteamConfirmModalTokens], (value) => {
        const source = sourceOfSteamComponent(value);
        return SteamConfirmModalTokens.every((token) => source.includes(token));
    });

    return {
        ...fields,
        focusable,
        tabs,
        modalRoot,
        showModal,
        checkbox,
        settingsSection,
        valueField,
        smallButton,
        listBuilder,
        routedPages,
        confirmModal,
    };
};

// Valve's panel pieces, the ones every Quick Access tab is built from: PanelSection, which draws a
// titled section, and PanelSectionRow, which lays one control out inside it. Both come from the one
// layout module that names them together; null when either is not a unique match there.
const PanelLayoutTokens = ["PanelSectionTitle", "PanelSectionRow", "spinner"] as const;

// Steam's route table, which names every Big Picture path including the external web browser.
const RouteTableTokens = ["GameAPIOSK:", "/gameapiosk"] as const;
/**
 * Resolves both native Quick Access section and row components.
 * @param runtime Shared module resolver.
 * @returns The pair, or null unless both exports match uniquely.
 */
const resolveSteamPanelComponents = (runtime) => {
    const factory = runtime.findUnique(PanelLayoutTokens);
    if (!factory) return null;
    const layout = runtime(factory[0]);
    const section = uniqueSteamExport(layout, (value) => {
        if (typeof value !== "function") return false;
        const source = String(value);
        return source.includes("PanelSectionTitle") && source.includes("spinner");
    });
    const row = uniqueSteamExport(
        layout,
        (value) =>
            !!value &&
            typeof value === "object" &&
            !!value.$$typeof &&
            typeof value.render === "function",
    );
    return section && row ? { section, row } : null;
};

// The folds of the Quick Access tabs' sections, one mechanism for all of them. The host publishes
// the sections the user opened under `SteamFoldsPatchId`, so every section starts folded, and a
// heading asks for a change with `setFolded`. A fold is shown at once: the override holds until
// the host's next publication agrees with it, so a host that keeps folds has the last word, and a
// host without the module leaves them to last the session. Ids are the host's to keep and the
// gate's to name: a Performance or Quick Settings section by its title, an Extensions tab item as
// `extensions:<item>`, a switch's settings under it as `extensions:<item>:<key>`.
const SteamFoldsPatchId = "steam-ui.panel-folds";
const createSteamFolds = () => {
    const overrides = new Map<string, boolean>();
    return {
        // The host's list, as a set of the open ids, or null for a state that is not one.
        normalize(value) {
            if (!value || typeof value !== "object" || !Array.isArray(value.open)) return null;
            const open = new Set<string>(
                value.open.filter((id) => typeof id === "string" && id.length > 0),
            );
            for (const [id, folded] of overrides) {
                if (open.has(id) === !folded) overrides.delete(id);
            }
            return open;
        },
        isFolded: (open, id) => (overrides.has(id) ? overrides.get(id) : !(open && open.has(id))),
        // `changed` redraws the root that asked, at once and again if the host refuses.
        setFolded(id, folded, changed) {
            overrides.set(id, folded);
            changed();
            void request(SteamFoldsPatchId, "setFolded", { id, folded }).catch(() => {});
        },
    };
};

/**
 * Selects a native checkbox with a toggle fallback.
 * @param ui Steam's resolved React and native control components.
 * @returns The checkbox/toggle component, or null when neither exists.
 */
const steamCheckbox = (ui) => ui?.checkbox ?? ui?.toggleField ?? null;

/**
 * Draws a native toolbar dropdown, falling back to a labelled field.
 * @param ui Steam's resolved React and native control components.
 * @param props Native dropdown props, including options, current selection, label and change handler.
 * @returns The dropdown element.
 */
const renderSteamDropdown = (ui, props) =>
    ui.dropdownControl
        ? ui.react.createElement(ui.dropdownControl, {
              rgOptions: props.rgOptions,
              selectedOption: props.selectedOption,
              onChange: props.onChange,
              disabled: props.disabled,
              menuLabel: props.label,
          })
        : ui.react.createElement(ui.dropdown, {
              label: props.label,
              rgOptions: props.rgOptions,
              selectedOption: props.selectedOption,
              onChange: props.onChange,
              disabled: props.disabled,
              layout: "below",
          });

/**
 * Opens a native modal and supplies a close callback to its content.
 *
 * Steam's dialog keeps its body to 600px unless the dialog is allowed its full size, in which case
 * the body may grow to the content it holds, within 88% of the viewport. `wide` asks for that
 * through the dialog's own `bAllowFullSize` prop, so a two-pane body is not folded into a column;
 * the backdrop, focus, B and dismissal are the same dialog's either way.
 * @param ui Steam's resolved React and native control components.
 * @param options Optional title/class, content renderer, cancellation callback and whether the
 * dialog may take its full size.
 * @returns True when shown, false when modal components are unavailable.
 */
const showSteamModal = (
    ui,
    options: {
        title?: string;
        className?: string;
        wide?: boolean;
        render: (close: () => void) => any;
        onCancel?: () => void;
    },
) => {
    if (!ui?.showModal || !ui?.modalRoot) return false;
    const react = ui.react;
    const title = options.title ?? "";
    function SteamModal(props: any) {
        const close = props?.closeModal ?? (() => {});
        const cancel = () => {
            options.onCancel?.();
            close();
        };
        return react.createElement(
            ui.modalRoot,
            {
                className:
                    [options.className, options.wide ? "steam-ui-kit-modal-wide" : ""]
                        .filter(Boolean)
                        .join(" ") || undefined,
                onCancel: cancel,
                closeModal: cancel,
                strTitle: title,
                ...(options.wide ? { bAllowFullSize: true } : {}),
            },
            // Modal portals do not inherit the originating document's toolkit stylesheet.
            steamUiKitStyle(react),
            title
                ? react.createElement("h2", { className: "steam-ui-kit-modal-title" }, title)
                : null,
            options.render(close),
        );
    }
    ui.showModal(react.createElement(SteamModal, {}), window, { strTitle: title });
    return true;
};

// Steam's gamepad button codes, as a Focusable's onButtonDown reports them in event.detail.button.
const SteamGamepadButton = Object.freeze({ TriggerLeft: 7, TriggerRight: 8 } as const);

/**
 * Creates a handler that consumes LT/RT and leaves other buttons to Steam.
 * @param step Receives -1 for LT and +1 for RT.
 * @returns A Steam Focusable onButtonDown handler.
 */
const onSteamTriggers = (step: (delta: number) => void) => (event: any) => {
    const button = event?.detail?.button;
    if (button !== SteamGamepadButton.TriggerLeft && button !== SteamGamepadButton.TriggerRight)
        return;
    event?.stopPropagation?.();
    step(button === SteamGamepadButton.TriggerRight ? 1 : -1);
};

/**
 * The documents of Steam's popup windows, as its popup manager lists them, with each popup's name.
 * A navigating popup may refuse its document; that popup is skipped and read again by the next caller.
 * @returns Each readable popup document once per popup; empty when the manager is absent.
 */
const steamPopupDocuments = () => {
    const found: { document: any; name: string }[] = [];
    let popups: any[] = [];
    try {
        const manager = window.g_PopupManager;
        if (typeof manager?.GetPopups === "function") popups = Array.from(manager.GetPopups() ?? []);
    } catch {
        return found;
    }
    for (const popup of popups) {
        try {
            const doc = popup?.m_popup?.document;
            if (doc) found.push({ document: doc, name: String(popup?.m_strName ?? "") });
        } catch {}
    }
    return found;
};

/**
 * The Steam window an interaction happened in: Big Picture's main window or one game's overlay window.
 * In a game, Quick Access and the side menus are drawn in that game's overlay window, so a page opened
 * from them belongs there rather than in the hidden main window. Resolve it when the user acts: the
 * window that holds the element, else the one with a side menu open, else the one whose document has
 * focus.
 * @param element An element of the control that was used, when the caller has one.
 * @returns The window instance, or null when none can be told, which keeps the main window.
 */
const steamInteractionWindow = (element?) => {
    const windows = window.SteamUIStore?.WindowStore?.SteamUIWindows;
    if (!Array.isArray(windows)) return null;
    const find = (test) =>
        windows.find((candidate) => {
            try {
                return !!candidate && test(candidate);
            } catch (_) {
                return false;
            }
        }) ?? null;
    const view = element?.ownerDocument?.defaultView;
    return (
        (view ? find((candidate) => candidate.BrowserWindow === view) : null) ??
        find((candidate) => (candidate.MenuStore?.GetOpenSideMenu?.() ?? 0) !== 0) ??
        find((candidate) => candidate.BrowserWindow?.document?.hasFocus?.() === true)
    );
};

// Closes whichever side panel is open, so a route followed from inside one is not rendered behind
// it. Valve's own window instance owns the operation: the given one (see steamInteractionWindow),
// else the main window; SteamNativeSurfaceCommands drives the same MenuStore.CloseSideMenus for the
// keyboard overlay. Reports whether the panel is now closed, which for a caller that was never in a
// panel is trivially true.
const closeSteamSideMenus = (owner?) => {
    const menus = (owner ?? window.SteamUIStore?.WindowStore?.MainWindowInstance)?.MenuStore;
    if (typeof menus?.CloseSideMenus !== "function") return false;
    try {
        menus.CloseSideMenus();
        return true;
    } catch (_) {
        return false;
    }
};

// Whether an array of elements is a router's route list: found by content, an array of more than two
// children holding a route for a path every client has. However many routes the client has.
const isSteamRouteList = (react, value, knownRoute: string) =>
    Array.isArray(value) &&
    value.length > 2 &&
    value.some((item) => react.isValidElement(item) && item.props?.path === knownRoute);

// An absolute route other than the root.
// Absolute, not the root, and free of control characters, as the C# side refuses them too. No length
// limit: a long route is still a route.
const isNavigableRoute = (route) =>
    typeof route === "string" &&
    route.startsWith("/") &&
    route !== "/" &&
    !/[\u0000-\u001f\u007f]/u.test(route);

/**
 * Pushes an absolute non-root route through the existing Steam history.
 * @param route Route without control characters; no length bound is imposed.
 * @param owner The window the request came from (steamInteractionWindow). A game's gamepad overlay
 * window navigates itself, as Steam opens its own pages over a game; any other or none uses the main
 * window's history.
 * @returns False for an invalid route or missing history; true after push. History exceptions propagate.
 */
const navigateSteamRoute = (route, owner?) => {
    if (!isNavigableRoute(route)) {
        return false;
    }
    if (owner?.IsGamepadUIOverlayWindow?.() === true && typeof owner.Navigate === "function") {
        owner.Navigate(route);
        return true;
    }
    const history = window.tempNavStore?.m_history;
    if (!history || typeof history.push !== "function") return false;
    history.push(route);
    return true;
};

/**
 * Asks the host to activate one entry drawn in a Steam side panel, and opens the page it may answer
 * with. The panel is closed first: a page opened behind it is, on a controller, a dead button.
 * @param patchId Command namespace that owns the entry.
 * @param id The entry's id.
 */
const activateSteamPanelEntry = (patchId, id) => {
    // Read while the panel is open: over a game the panel, and the page, are the game overlay's.
    const owner = steamInteractionWindow();
    void request(patchId, "activate", { id }).then(
        (answer: any) => {
            if (answer?.route && closeSteamSideMenus(owner)) navigateSteamRoute(answer.route, owner);
        },
        () => {
            // The host's refusal is already logged, and the entry stays truthful on the next state
            // publication. A rejected press must not tear down the panel.
        },
    );
};

// Whether a source calls `call` and every such call passes a single argument. Counting the
// arguments reads what the author wrote, however the minifier then names and spells them.
const callsWithOneArgument = (source: string, call: string) => {
    let found = false;
    for (let at = source.indexOf(call); at >= 0; at = source.indexOf(call, at + call.length)) {
        let depth = 0;
        for (let index = at + call.length; index < source.length; index++) {
            const char = source[index];
            if (char === "(" || char === "[" || char === "{") {
                depth++;
            } else if (char === ")" || char === "]" || char === "}") {
                if (depth === 0) break;
                depth--;
            } else if (char === "," && depth === 0) {
                return false;
            }
        }
        found = true;
    }
    return found;
};

// Valve's localize-with-fallback, chosen by what its source does: it hands LocalizeString the token
// and nothing else, and builds no elements. Its siblings in the module differ in exactly that: the
// quiet variant and the presence test pass LocalizeString a second argument, and the formatting
// variant builds elements around the string. An earlier match on how the minifier spelled the
// comparisons broke with the September 2026 beta.
const isLocalizer = (source: string) =>
    callsWithOneArgument(source, ".LocalizeString(") && !source.includes("createElement");

// Valve's localize-with-fallback from the localization module, by its shape (isLocalizer), or null
// when the module or the function is not a unique match. When the minifier broke the older
// name-based match, every Quick Access row refused with "React, fields, layout or localization
// runtime was not a unique match".
const resolveSteamLocalizer = (runtime): ((token: string) => unknown) | null => {
    const localization = runtime.findUnique([...LocalizationTokens]);
    if (!localization) return null;
    return uniqueSteamExport(runtime(localization[0]), (value) => {
        if (typeof value !== "function") return false;
        const source = String(value);
        return !source.startsWith("class") && isLocalizer(source);
    });
};

// Steam's string for a token, or the fallback when the localizer is absent or has no string.
const localizedOr = (
    localize: ((token: string) => unknown) | null,
    token: string,
    fallback: string,
) => {
    try {
        const text = localize?.(token);
        if (typeof text === "string" && text && text !== token) return text;
    } catch {
        // The fallback stands in for a localizer that did not answer.
    }
    return fallback;
};

// The text a label carries, or null. A label is sometimes a plain string and sometimes what Steam's
// localizer returns, which is a React element wrapping the string rather than the string itself.
const textOf = (value) => {
    if (typeof value === "string") return value;
    return value && typeof value === "object" && typeof value.props?.children === "string"
        ? value.props.children
        : null;
};

// State a gate keeps outside Steam's stores, read by its components through React's
// useSyncExternalStore. `changed` advances the revision and tells every subscriber; a listener that
// throws does not stop the others.
const createLocalStore = () => {
    let revision = 0;
    const listeners = new Set<() => void>();
    return {
        changed() {
            revision += 1;
            for (const listener of [...listeners]) {
                try {
                    listener();
                } catch {}
            }
        },
        subscribe(listener: () => void) {
            listeners.add(listener);
            return () => listeners.delete(listener);
        },
        revision: () => revision,
    };
};

// mobx-react-lite's useObserver, found by its shape in the module that carries the startup check,
// or null. Wanted by the surfaces that use it, never required.
const findUseObserver = (runtime) => {
    const observer = runtime.findUnique(ObserverTokens);
    if (!observer) return null;
    const exports = runtime(observer[0]);
    const hooks = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return (
            typeof value === "function" &&
            value.length === 2 &&
            String(value).includes('"observed"')
        );
    });
    return hooks.length === 1 ? exports[hooks[0]] : null;
};

// A webpack class map, unwrapped when the module is an ES default export.
const classMapOf = (exported) => (exported && exported.__esModule ? exported.default : exported);

// Steam's accent blue, which a row the host marks draws its description in. The Quick Access rows
// and the settings pages share it, so a marked value reads the same on both.
const SteamAccentColor = "#1a9fff";
// A description, as one span in the accent colour when the host marks the row, and as its plain
// text otherwise. The words are the host's; an empty unmarked description draws nothing.
const steamAccentDescription = (react, text, accent) =>
    accent
        ? react.createElement("span", { style: { color: SteamAccentColor } }, text)
        : text || undefined;

/**
 * Builds props that retain an existing React element's key.
 * @param element Element whose identity is preserved.
 * @param props Replacement props; defaults to the element's current props.
 * @returns Original props for a null key; otherwise a shallow props copy containing the key.
 */
const keyed = (element, props = element.props) =>
    element.key === null ? props : { ...props, key: element.key };

// A portal is not an element: isValidElement answers false, and its children sit on the portal
// itself rather than under props. Steam's Quick Access menu draws its whole body through one into
// the popup window, so a descent that treats a portal as a leaf never reaches the tab list beneath
// it (2026-09-24). React reads a portal by `$$typeof`, `children` and `containerInfo`, so a shallow
// copy with mapped children is a portal to it.
const PortalType = Symbol.for("react.portal");
// What React marks a memo with, the one way a memo object is told apart from other exports.
const ReactMemoType = Symbol.for("react.memo");
const isPortal = (value) => !!value && typeof value === "object" && value.$$typeof === PortalType;

// Maps a child list; answers the new list, or null when no child changed. A child mapped to null
// is dropped. Shared by element and portal mapping so the two cannot drift on those rules.
const mapEach = (react, children, map: (child: any) => unknown) => {
    const kids = react.Children.toArray(children);
    if (!kids.length) return null;
    let changed = false;
    const next: unknown[] = [];
    for (const kid of kids) {
        const replacement = map(kid);
        changed ||= replacement !== kid;
        if (replacement !== null) next.push(replacement);
    }
    return changed ? next : null;
};

const mapPortalChildren = (react, portal, map: (child: any) => unknown) => {
    const next = mapEach(react, portal.children, map);
    return next ? { ...portal, children: next } : portal;
};

// Maps an element's children and clones it only when one changed. An element with no children is
// returned as it is.
const mapChildren = (react, element, map: (child: any) => unknown) => {
    const next = mapEach(react, element.props?.children, map);
    return next ? react.cloneElement(element, {}, ...next) : element;
};

// Renders a plain function component through a wrapper, so what it returns can be changed as well:
// a component's children do not exist until it renders. The wrapper `wrap` builds is cached against
// the component, because a fresh type on every render would remount the subtree. Class components,
// memo and forwardRef objects are left alone, since they cannot be called directly and wrapping
// them would change identity for refs; for those, and for anything that is not an element of a
// function type, this answers null.
// The wrapper built for a type, once: a fresh identity on every render would remount the subtree.
const cachedWrapper = (cache: Map<any, any>, type, wrap: (type: any) => unknown) => {
    let wrapper = cache.get(type);
    if (!wrapper) {
        wrapper = wrap(type);
        cache.set(type, wrapper);
    }
    return wrapper;
};

const descendInto = (react, element, cache, wrap: (type: any) => unknown) => {
    const type = element.type;
    if (typeof type !== "function" || type.prototype?.isReactComponent) return null;
    return react.createElement(cachedWrapper(cache, type, wrap), keyed(element));
};

// Runs a gate's resolution. A throw is handed to `failed` to record under the gate's own wording; a
// resolution that answers false has already recorded why.
const attemptResolution = (resolve: () => boolean, failed: (error: unknown) => void) => {
    try {
        return resolve();
    } catch (error) {
        failed(error);
        return false;
    }
};

/**
 * Invokes an optional subscription disposer; cleanup exceptions propagate.
 * @param unsubscribe Current disposer, or null.
 * @returns Null, suitable for assigning back to the owner's subscription field.
 */
const endSubscription = (unsubscribe: (() => void) | null) => {
    unsubscribe?.();
    return null;
};

// React's mounted trees, for the gates that find a module-local component by where it is drawn.
//
// One root fiber per container React attached to under the document: the `#root` host and any
// other body child that carries a container key. SharedJSContext keeps a second, empty container
// beside `#root` on the September 2026 client. Each container names the fiber root React created;
// the root's `current` is the tree on screen, and after the first commit that is not always the
// fiber the container key was written with. A supplied document lets a gate read a known Steam
// popup's roots without changing the default SharedJSContext lookup.
const reactRootFibers = (doc = typeof document === "undefined" ? null : document) => {
    // A page always has a document; an emitted-asset check may not, and then nothing is mounted.
    if (!doc) return [];
    const hosts: any[] = [];
    const root = doc.getElementById("root");
    if (root) hosts.push(root);
    for (const child of Array.from(doc.body?.children ?? [])) {
        if (child !== root) hosts.push(child);
    }
    const roots: any[] = [];
    for (const host of hosts) {
        const key = Object.keys(host).find((name) => name.startsWith("__reactContainer$"));
        if (!key) continue;
        const fiber = host[key];
        roots.push(fiber?.stateNode?.current ?? fiber);
    }
    return roots;
};

// Walks mounted fibers breadth-first over the child and sibling links, bounded, until `visit`
// answers true. Breadth-first because a router sits near the top of its tree, and a depth-first
// walk can spend the whole bound inside the first large subtree — a mounted library grid — before
// it gets there. Answers how many fibers were seen and whether the walk stopped on a match.
const walkFibers = (roots, bound: number, visit: (fiber: any) => boolean | void) => {
    const queue: any[] = roots.slice();
    let visited = 0;
    for (let head = 0; head < queue.length && visited < bound; head++) {
        const node = queue[head];
        if (!node) continue;
        visited++;
        if (visit(node) === true) return { visited, stopped: true };
        queue.push(node.child, node.sibling);
    }
    return { visited, stopped: false };
};

// Where a fiber's real props are kept while an adoption render is forced; see claimComponent.
const AdoptedPropsKey = "__steamUiAdoptedProps";
const MaximumAncestors = 64;

// Asks the nearest class component above a fiber to render again. Steam's router switch is a
// class, so a claimed page under it re-renders the way a navigation renders it. `forceUpdate` is
// React's public API for exactly this. Answers whether an instance was found and asked.
const requestRender = (fiber) => {
    let node = fiber.return;
    for (let depth = 0; node && depth < MaximumAncestors; depth++) {
        const instance = node.stateNode;
        if (instance?.isReactComponent && typeof instance.forceUpdate === "function") {
            try {
                instance.forceUpdate();
                return true;
            } catch {
                return false;
            }
        }
        node = node.return;
    }
    return false;
};

// The three writes that bring a claim to a fiber already on screen, each to a plain field:
//   - `type` on the fiber and its alternate, so the next render calls the replacement. The
//     replacement must add no hooks of its own: the fiber keeps the hook list the original built,
//     and React refuses a render that ends with more hooks than the last.
//   - `memoizedProps` swapped for an object that shallow-compares unequal to the real props, so
//     React's memo bail-out cannot skip the render. React writes the real props back when it
//     renders; until then they stay reachable under AdoptedPropsKey.
//   - a render requested from the nearest class ancestor (requestRender), so it happens now.
const invalidateFiberProps = (fiber) => {
    for (const side of [fiber, fiber.alternate]) {
        if (side) side.memoizedProps = { [AdoptedPropsKey]: side.memoizedProps };
    }
};
const retargetFiber = (fiber, type) => {
    for (const side of [fiber, fiber.alternate]) {
        if (side) side.type = type;
    }
};
// Whether a fiber has a parent link on either side; a fiber sitting directly under a React root
// never had one, so only one that had a parent and lost it has been detached.
const fiberAttached = (fiber) => !!(fiber.return || fiber.alternate?.return);

// The node bound mounted trees are walked under. The router sits about a hundred levels down the
// live tree and the popups are shallower, so this is generous; it exists to stop a cyclic or
// pathological tree, not to limit a legitimate search.
const MaximumMountedNodes = 60000;

// Whether a publication carries something the wrappers have not drawn yet. Publications repeat
// the same state every round, several times a second while the host has anything to say, and a
// gate that re-rendered on each one asked the router's class ancestor to render again each time.
// That render re-runs every route under it: Steam's controller configurator restarts its edit
// session on each render of its route and threw away the user's bindings every few seconds
// (2026-09-26). A wrapper reads its state from a closure, so the only publications that need a
// render are the ones that changed it.
const publicationChanged = (previous, next) => JSON.stringify(previous) !== JSON.stringify(next);

// One claimed component and every mounted instance running it, for the life of a gate's install.
//
// A claim on a memo's `type` reaches the next mount only: when React mounts a memo it resolves the
// function once and caches it on the fiber as `type`, and every later render of that fiber reads the
// cache, not the memo. Big Picture mounts its router, Home, the Quick Access view and the menu at boot
// and keeps them, so a claim alone is inert until the user happens to remount one; the carousel
// (2026-09-22) and every page gate (2026-09-24) shipped that way. So the mounted instances are
// adopted: each fiber drawing the component has its `type` retargeted on both sides, its props
// invalidated so a memo cannot bail out, and a render asked of its nearest class ancestor.
//
//   host   the memo (or any object) whose `type` member is claimed; its mounted instances are the
//          fibers whose elementType is the host. Null when the component has no public handle at
//          all, such as the main menu's popup host or the router's own switch.
//   keys   the claim's durable marker keys. Every wrapper this builds carries them, the host's and
//          each `match` one alike, so a later evaluation recognises it, never stacks a second
//          wrapper on it, and release finds it wherever React kept it.
//   wrap   builds the wrapper around the displaced function. It must add no hooks of its own: an
//          adopted fiber keeps the hook list its original built.
//   match  also adopts a fiber this answers true for, given the fiber and its type with any claim
//          of ours seen through; each original gets its own cached wrapper.
//   roots  the fiber roots to walk; SharedJSContext's own React roots by default.
//   bound  how many fibers one walk visits at most.
//
// Release hands back every fiber whose type is a wrapper of this claim, on both fiber sides: the
// ones adopted at install and every instance React mounted from the claimed memo since, which a list
// kept at install cannot know about. Each handed-back fiber is invalidated and asked to render, so
// the original draws again now rather than on whatever render happens next.
const claimComponent = (options: {
    host?: any;
    keys: ClaimKeys;
    wrap: (original: any) => any;
    match?: (fiber: any, type: any) => boolean;
    roots?: () => any[];
    bound?: number;
}) => {
    const { host = null, keys, wrap, match, bound = MaximumMountedNodes } = options;
    const roots = options.roots ?? (() => reactRootFibers());
    // A wrapper for each original a `match` fiber drew, built once: a fresh identity per render
    // would remount the subtree.
    const wrappers = new Map<any, any>();
    // Each adopted fiber with whether it had a parent when adopted; see fiberAttached.
    let adopted = new Map<any, boolean>();
    let hostWrapper: any = null;
    let scheduled = false;

    const marked = (original) => {
        let wrapper = wrappers.get(original);
        if (!wrapper) {
            wrapper = wrap(original);
            defineHidden(wrapper, keys.marker, true);
            defineHidden(wrapper, keys.original, {
                kind: "steam-ui-property-snapshot-v1",
                hadOwn: true,
                value: original,
            });
            wrappers.set(original, wrapper);
        }
        return wrapper;
    };
    // What a fiber should draw while the claim holds, or null for a fiber that is not this claim's.
    const replacementFor = (fiber) => {
        if (host && fiber.elementType === host) return hostWrapper;
        if (!match) return null;
        const original = unclaimedValue(fiber.type, keys);
        if (typeof original !== "function" || !match(fiber, original)) return null;
        return marked(original);
    };
    const adopt = () => {
        let count = 0;
        walkFibers(roots(), bound, (fiber) => {
            const replacement = replacementFor(fiber);
            if (!replacement) return;
            if (fiber.type !== replacement) {
                retargetFiber(fiber, replacement);
                invalidateFiberProps(fiber);
                scheduled = requestRender(fiber) || scheduled;
            }
            if (!adopted.has(fiber)) count++;
            adopted.set(fiber, fiberAttached(fiber));
        });
        return count;
    };
    const live = () => {
        for (const [fiber, hadParent] of [...adopted]) {
            if (hadParent && !fiberAttached(fiber)) adopted.delete(fiber);
        }
        return [...adopted.keys()];
    };

    let reclaimed = false;
    if (host) {
        const claim = claimMember(host, "type", keys, (original) => {
            if (typeof original !== "function") throw new TypeError("claimed component is not a function");
            return wrap(original);
        });
        if (!claim.ok) return { ok: false as const, error: claim.error };
        reclaimed = claim.reclaimed;
        hostWrapper = host.type;
    }
    adopt();

    return {
        ok: true as const,
        reclaimed,
        /** The marked wrapper for an original, so another path that creates it draws the same one. */
        wrapperFor: marked,
        /**
         * Asks every adopted instance to render again; `rescan` first walks the tree for instances
         * mounted since, which a `match` claim with none of its instances left also does on its own.
         * @returns How many instances were asked.
         */
        rerender: (rescan = false) => {
            if (rescan || (match && live().length === 0)) adopt();
            let asked = 0;
            for (const fiber of live()) {
                invalidateFiberProps(fiber);
                if (requestRender(fiber)) asked++;
            }
            return asked;
        },
        /** Whether the host still carries this claim's wrapper; a claim without a host holds while installed. */
        held: () => !host || memberClaimed(host, "type", keys),
        // `stale` is an adopted instance still drawing something other than its wrapper: a render that
        // has not happened yet, or a type React reset underneath us.
        status: () => {
            const fibers = live();
            return {
                adopted: fibers.length,
                scheduled,
                stale: fibers.filter((fiber) => !claimed(fiber.type, keys)).length,
            };
        },
        release: () => {
            if (host) {
                const released = releaseMember(host, "type", keys);
                if (!released.ok) return released;
            }
            const handed = new Set<any>();
            const handBack = (fiber) => {
                let changed = false;
                for (const side of [fiber, fiber.alternate]) {
                    if (side && claimed(side.type, keys)) {
                        side.type = unclaimedValue(side.type, keys);
                        changed = true;
                    }
                }
                if (changed) handed.add(fiber);
            };
            walkFibers(roots(), bound, handBack);
            for (const fiber of adopted.keys()) handBack(fiber);
            for (const fiber of handed) {
                invalidateFiberProps(fiber);
                requestRender(fiber);
            }
            adopted = new Map();
            wrappers.clear();
            hostWrapper = null;
            scheduled = false;
            return { ok: true, released: handed.size };
        },
    };
};

/** A component claim that took: what `claimComponent` and `ledger.component` answer on success. */
type ClaimedComponent = Extract<ReturnType<typeof claimComponent>, { ok: true }>;

/** What a gate's `find` answers: its handles, or why there are none yet, or why there will be none. */
type GateFound<Handles> =
    | { ready: true; handles: Handles; facts?: Record<string, unknown> }
    | { notReady: string; facts?: Record<string, unknown> }
    | { incompatible: string; facts?: Record<string, unknown> };

/** Everything a gate holds, released in reverse order of taking. */
type GateLedger = ReturnType<typeof createGateLedger>["ledger"];

/** The answer of every gate's `probe()`, which the host parses as one shape. */
type GateProbe = {
    ok: boolean;
    outcome: "ready" | "notReady" | "incompatible";
    reason?: string;
    installed?: boolean;
    facts?: Record<string, unknown>;
};

// The record of what one install took. Each entry is released in reverse order on a failed install
// and on removal; an entry whose release fails stays, with everything taken before it, so the next
// removal retries exactly what is left. `held` proves an entry is still in place for verification.
const createGateLedger = () => {
    const entries: { name: string; release: () => unknown; held: () => boolean }[] = [];
    let reclaimed = false;
    const add = (name: string, release: () => unknown, held: () => boolean = () => true) => {
        entries.push({ name, release, held });
    };
    // A claim primitive's outcome, recorded only when it took.
    const taken = <Outcome extends { ok: boolean; reclaimed?: boolean }>(
        name: string,
        outcome: Outcome,
        release: () => unknown,
        held: () => boolean,
    ) => {
        if (outcome.ok) {
            reclaimed ||= outcome.reclaimed === true;
            add(name, release, held);
        }
        return outcome;
    };
    const ledger = {
        /** Records something taken outside the primitives below, with how to give it back. */
        add,
        /** Runs `cleanup` on release; for timers, listeners and nodes a gate created. */
        cleanup: (name: string, cleanup: () => void) =>
            add(name, () => {
                cleanup();
                return { ok: true };
            }),
        member: (host, member: string, keys: ClaimKeys, replacement: (original: unknown) => unknown) =>
            taken(
                `member ${member}`,
                claimMember(host, member, keys, replacement),
                () => releaseMember(host, member, keys),
                () => memberClaimed(host, member, keys),
            ),
        value: (host, field: string, keys: ClaimKeys, next: unknown) =>
            taken(
                `value ${field}`,
                claimValue(host, field, keys, next),
                () => releaseValue(host, field, keys),
                () => claimed(host, keys),
            ),
        accessor: (host, property: string, keys: ClaimKeys, getter: () => unknown) =>
            taken(
                `accessor ${property}`,
                claimAccessor(host, property, keys, getter),
                () => releaseAccessor(host, property, keys),
                () => claimed(Object.getOwnPropertyDescriptor(host ?? {}, property)?.get, keys),
            ),
        namespace: (host, name: string, marker: string, factory: () => object) =>
            taken(
                `namespace ${name}`,
                supplyNamespace(host, name, marker, factory),
                () => withdrawNamespace(host, name, marker),
                () => claimed(host?.[name], { marker, original: marker }),
            ),
        memo: (host, name: string, transform: (value: unknown) => unknown) =>
            taken(
                `memo ${name}`,
                interceptMemo(host, name, transform),
                () => releaseMemo(host, name),
                () => memoIntercepted(host, name),
            ),
        elements: (host, name: string, transform: ElementTransform) =>
            taken(
                `elements ${name}`,
                interceptElements(host, name, transform),
                () => releaseElements(host, name),
                () => elementsIntercepted(host, name),
            ),
        component: (options: Parameters<typeof claimComponent>[0]) => {
            const component = claimComponent(options);
            if (component.ok) {
                reclaimed ||= component.reclaimed;
                add("component", component.release, component.held);
            }
            return component;
        },
        /** Subscribes to a publication until release. */
        subscribe: (patchId: string, callback: (state: any) => void) => {
            const unsubscribe = subscribe(patchId, callback);
            add(`subscription ${patchId}`, () => {
                unsubscribe();
                return { ok: true };
            });
        },
        /** Subscribes to a publication's refusals until release. */
        subscribeRefusal: (patchId: string, callback: (reason: string | null) => void) => {
            const unsubscribe = subscribeRefusal(patchId, callback);
            add(`refusals ${patchId}`, () => {
                unsubscribe();
                return { ok: true };
            });
        },
    };
    const unwind = (): { ok: boolean; error?: string } => {
        while (entries.length) {
            const entry = entries[entries.length - 1];
            let result: any;
            try {
                result = entry.release();
            } catch (error) {
                return { ok: false, error: `${entry.name}: ${String(error)}` };
            }
            if (result && typeof result === "object" && result.ok === false) {
                return { ok: false, error: `${entry.name}: ${result.error ?? "release failed"}` };
            }
            entries.pop();
        }
        reclaimed = false;
        return { ok: true };
    };
    const held = () =>
        entries.every((entry) => {
            try {
                return entry.held() === true;
            } catch {
                return false;
            }
        });
    return { ledger, unwind, held, size: () => entries.length, reclaimed: () => reclaimed };
};

/**
 * Builds one gate's lifecycle from what differs between gates: how it finds its handles, what it
 * claims with them and what it reports.
 *
 * `find(runtime)` is pure: it reads Steam and answers `{ready, handles, facts}`, `{notReady}` when
 * what it needs is not mounted or constructed yet (Big Picture closed, Quick Access never opened),
 * or `{incompatible}` when the client does not have it (a module that moved or matches twice). It
 * writes nothing, so `probe()` is `find` without binding. While the gate is installed `probe()`
 * answers from the handles it holds: a find that sees its own claims could otherwise call a holding
 * gate incompatible, and the manager would retract it on every poll.
 *
 * `bind(handles, ledger)` takes everything through the ledger and answers a refusal string, or
 * extra fields for the install result. A refusal or a throw unwinds what was taken, in reverse.
 * Removal unwinds the same way and stays installed when a step fails, so it can be retried.
 * `unbind()` resets what the gate keeps for itself once nothing is held any more.
 *
 * `status()` has one shape for every gate, which the host verifies with one predicate:
 * `installed`, `resolved`, `held` (every ledger entry still in place and `holds(handles)`),
 * `claims` (ledger entries outstanding) and `lastError`, after the gate's own `status(handles)`.
 * @returns The gate: install, remove, status, probe, `installed()`, and any `api` members.
 */
function defineGate<Handles, Api extends Record<string, unknown> = {}>(definition: {
    name: string;
    find: (runtime: any) => GateFound<Handles>;
    bind: (handles: Handles, ledger: GateLedger) => string | void | Record<string, unknown>;
    holds?: (handles: Handles) => boolean;
    unbind?: () => void;
    status?: (handles: Handles | null) => Record<string, unknown>;
    api?: Api;
}) {
    const record = createGateLedger();
    let handles: Handles | null = null;
    let facts: Record<string, unknown> = {};
    let installed = false;
    let lastError = "";

    const search = (): GateFound<Handles> => {
        let runtime;
        try {
            runtime = getWebpackRuntime(definition.name);
        } catch (error) {
            return { notReady: `Steam modules unavailable: ${String(error)}` };
        }
        try {
            return definition.find(runtime) ?? { incompatible: "find answered nothing" };
        } catch (error) {
            return { incompatible: `${definition.name} find failed: ${String(error)}` };
        }
    };
    const answer = (found: GateFound<Handles>): GateProbe => {
        if ("ready" in found && found.ready === true) {
            return { ok: true, outcome: "ready", facts: found.facts ?? {} };
        }
        if ("notReady" in found) {
            return { ok: false, outcome: "notReady", reason: found.notReady, facts: found.facts ?? {} };
        }
        const refused = found as { incompatible: string; facts?: Record<string, unknown> };
        return {
            ok: false,
            outcome: "incompatible",
            reason: String(refused.incompatible),
            facts: refused.facts ?? {},
        };
    };
    const resetLocal = () => {
        handles = null;
        facts = {};
        try {
            definition.unbind?.();
        } catch {
            // What a gate keeps for itself is reset again by the next install.
        }
    };

    const probe = (): GateProbe =>
        installed ? { ok: true, outcome: "ready", installed: true, facts } : answer(search());

    const install = () => {
        if (installed) return { ok: true, alreadyInstalled: true };
        if (record.size()) {
            const residue = record.unwind();
            if (!residue.ok) {
                lastError = `an earlier install could not be rolled back: ${residue.error}`;
                return { ok: false, error: lastError };
            }
        }
        const found = search();
        if (!("ready" in found && found.ready === true)) {
            const refused = answer(found);
            lastError = refused.reason ?? refused.outcome;
            return { ok: false, error: lastError, outcome: refused.outcome };
        }
        handles = found.handles;
        facts = found.facts ?? {};
        // Installed while binding, so a wrapper Steam renders during the bind already draws.
        installed = true;
        let refusal: string | null = null;
        let extras: Record<string, unknown> = {};
        try {
            const bound = definition.bind(found.handles, record.ledger);
            if (typeof bound === "string") refusal = bound;
            else if (bound && typeof bound === "object") extras = bound;
        } catch (error) {
            refusal = String(error);
        }
        if (refusal !== null) {
            installed = false;
            const rolledBack = record.unwind();
            resetLocal();
            lastError = rolledBack.ok ? refusal : `${refusal}; rollback failed: ${rolledBack.error}`;
            return { ok: false, error: lastError };
        }
        lastError = "";
        return { ok: true, installed: true, reclaimed: record.reclaimed(), ...extras };
    };

    const remove = () => {
        if (!installed && !record.size()) return { ok: true, absent: true };
        const released = record.unwind();
        if (!released.ok) {
            lastError = released.error ?? `${definition.name} release failed`;
            return { ok: false, error: lastError };
        }
        installed = false;
        resetLocal();
        return { ok: true, removed: true };
    };

    const holds = () => {
        if (!installed || !handles || !record.held()) return false;
        try {
            return definition.holds ? definition.holds(handles) === true : true;
        } catch {
            return false;
        }
    };

    const status = () => {
        let own: Record<string, unknown> = {};
        try {
            own = definition.status?.(handles) ?? {};
        } catch (error) {
            own = { statusError: String(error) };
        }
        return {
            ...own,
            ok: true,
            gate: definition.name,
            installed,
            resolved: !!handles,
            held: holds(),
            claims: record.size(),
            lastError,
        };
    };

    return {
        ...((definition.api ?? {}) as Api),
        install,
        remove,
        status,
        probe,
        /** Whether the gate is installed: from the start of its bind until its removal. */
        installed: () => installed,
    };
}

// Shorthands for what `find` answers.
const gateReady = <Handles>(handles: Handles, facts: Record<string, unknown> = {}) => ({
    ready: true as const,
    handles,
    facts,
});
const gateNotReady = (reason: string, facts: Record<string, unknown> = {}) => ({ notReady: reason, facts });
const gateIncompatible = (reason: string, facts: Record<string, unknown> = {}) => ({
    incompatible: reason,
    facts,
});

// Whether every token is in a function's source. The source is taken once per function: a walk
// over a mounted tree meets the same few component types thousands of times.
const sourceTexts = new WeakMap<Function, string>();
const sourceMatches = (fn, tokens: readonly string[]) => {
    if (typeof fn !== "function") return false;
    let source = sourceTexts.get(fn);
    if (source === undefined) {
        source = String(fn);
        sourceTexts.set(fn, source);
    }
    return tokens.every((token) => source.includes(token));
};
