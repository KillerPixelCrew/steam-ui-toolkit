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
    const dialogButton = uniqueSteamExport(fields, (value) => isSteamDialogButton(value, "Secondary"));
    const dialogButtonPrimary = uniqueSteamExport(fields, (value) => isSteamDialogButton(value, "Primary"));
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

/**
 * Resolves native page controls using shared module fingerprints and export shapes.
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
                  {bHideMainWindowForPopouts: false, ...props},
                  undefined,
                  {bHideActions: props.bHideActionIcons},
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

    return {
        ...fields,
        focusable,
        tabs,
        modalRoot,
        showModal,
        checkbox,
    };
};

// Valve's panel pieces, the ones every Quick Access tab is built from: PanelSection, which draws a
// titled section, and PanelSectionRow, which lays one control out inside it. Both come from the one
// layout module that names them together; null when either is not a unique match there.
const PanelLayoutTokens = ["PanelSectionTitle", "PanelSectionRow", "spinner"] as const;
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
            !!value && typeof value === "object" && !!value.$$typeof && typeof value.render === "function",
    );
    return section && row ? {section, row} : null;
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
        isFolded: (open, id) =>
            overrides.has(id) ? overrides.get(id) : !(open && open.has(id)),
        // `changed` redraws the root that asked, at once and again if the host refuses.
        setFolded(id, folded, changed) {
            overrides.set(id, folded);
            changed();
            void request(SteamFoldsPatchId, "setFolded", {id, folded}).catch(() => {});
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
 * @param ui Steam's resolved React and native control components.
 * @param options Optional title/class, content renderer and cancellation callback.
 * @returns True when shown, false when modal components are unavailable.
 */
const showSteamModal = (
    ui,
    options: {title?: string; className?: string; render: (close: () => void) => any; onCancel?: () => void},
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
            {className: options.className, onCancel: cancel, closeModal: cancel, strTitle: title},
            options.render(close),
        );
    }
    ui.showModal(react.createElement(SteamModal, {}), window, {strTitle: title});
    return true;
};

// Steam's gamepad button codes, as a Focusable's onButtonDown reports them in event.detail.button.
const SteamGamepadButton = Object.freeze({TriggerLeft: 7, TriggerRight: 8} as const);

/**
 * Creates a handler that consumes LT/RT and leaves other buttons to Steam.
 * @param step Receives -1 for LT and +1 for RT.
 * @returns A Steam Focusable onButtonDown handler.
 */
const onSteamTriggers = (step: (delta: number) => void) => (event: any) => {
    const button = event?.detail?.button;
    if (button !== SteamGamepadButton.TriggerLeft && button !== SteamGamepadButton.TriggerRight) return;
    event?.stopPropagation?.();
    step(button === SteamGamepadButton.TriggerRight ? 1 : -1);
};

// Closes whichever side panel is open, so a route followed from inside one is not rendered behind
// it. Valve's own main-window instance owns the operation; SteamNativeSurfaceCommands drives the
// same MenuStore.CloseSideMenus for the keyboard overlay. Reports whether the panel is now closed,
// which for a caller that was never in a panel is trivially true.
const closeSteamSideMenus = () => {
    const menus = window.SteamUIStore?.WindowStore?.MainWindowInstance?.MenuStore;
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
    typeof route === "string" && route.startsWith("/") && route !== "/" && !/[\u0000-\u001f\u007f]/u.test(route);

/**
 * Pushes an absolute non-root route through the existing Steam history.
 * @param route Route without control characters; no length bound is imposed.
 * @returns False for an invalid route or missing history; true after push. History exceptions propagate.
 */
const navigateSteamRoute = (route) => {
    if (!isNavigableRoute(route)) {
        return false;
    }
    const history = window.tempNavStore?.m_history;
    if (!history || typeof history.push !== "function") return false;
    history.push(route);
    return true;
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
const localizedOr = (localize: ((token: string) => unknown) | null, token: string, fallback: string) => {
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
                } catch {
                }
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
    accent ? react.createElement("span", {style: {color: SteamAccentColor}}, text) : text || undefined;

/**
 * Builds props that retain an existing React element's key.
 * @param element Element whose identity is preserved.
 * @param props Replacement props; defaults to the element's current props.
 * @returns Original props for a null key; otherwise a shallow props copy containing the key.
 */
const keyed = (element, props = element.props) =>
    element.key === null ? props : {...props, key: element.key};

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
    return next ? {...portal, children: next} : portal;
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
        if (visit(node) === true) return {visited, stopped: true};
        queue.push(node.child, node.sibling);
    }
    return {visited, stopped: false};
};

// The mounted fibers drawing one component: those whose element type is the memo or function that
// was claimed. The walk covers the tree on screen, so each mounted instance answers once.
const mountedFibersOf = (roots, elementType, bound: number) => {
    const fibers: any[] = [];
    walkFibers(roots, bound, (fiber) => {
        if (fiber.elementType === elementType) fibers.push(fiber);
    });
    return fibers;
};

// Where a fiber's real props are kept while an adoption render is forced; see adoptMountedType.
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
        if (side) side.memoizedProps = {[AdoptedPropsKey]: side.memoizedProps};
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

// Brings a claim on a component's `type` to the instances already on screen.
//
// A claim on a memo's `type` reaches the next mount only: when React mounts a memo it resolves the
// function once and caches it on the fiber as `type`, and every later render of that fiber reads
// the cache, not the memo. Big Picture mounts its router, Home, the Quick Access view and the menu
// at boot and keeps them, so a claim alone is inert until the user happens to remount one; the
// carousel (2026-09-22) and every page gate (2026-09-24) shipped that way. Answers the fibers
// adopted and whether a render was requested; without a class ancestor the adoption still holds
// and takes effect on the instance's next render.
const adoptMountedType = (roots, elementType, replacement, bound: number) => {
    const fibers: any[] = [];
    let scheduled = false;
    for (const fiber of mountedFibersOf(roots, elementType, bound)) {
        if (fiber.type === replacement) continue;
        retargetFiber(fiber, replacement);
        invalidateFiberProps(fiber);
        fibers.push(fiber);
        scheduled = requestRender(fiber) || scheduled;
    }
    return {fibers, adopted: fibers.length, scheduled};
};

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

// One claimed component's mounted instances, for the life of a gate's install.
//
// Adoption walks the tree once and keeps the fibers it adopted. Everything after that is over that
// list rather than the tree: a publication asks them to render again (the wrapper reads the gate's
// state from a closure, so the props have not changed and a memo with equal props bails out exactly
// as it did before adoption); status counts them; release hands them back. Publications arrive
// several times a second while a user browses artwork and status is read on every verify, so a
// tree walk on either was a full 60000-node pass on the UI thread for a number that never changed.
// A fiber React has since unmounted is dropped when next seen; the claim on the memo's `type`
// reaches any instance mounted after adoption on its own.
const createMountedAdoption = (bound = MaximumMountedNodes) => {
    // Each adopted fiber with whether it had a parent when adopted; see fiberAttached.
    let entries: {fiber: any; hadParent: boolean}[] = [];
    let replacement = null;
    let scheduled = false;
    const live = () => {
        entries = entries.filter(({fiber, hadParent}) => !hadParent || fiberAttached(fiber));
        return entries.map(({fiber}) => fiber);
    };
    return {
        adopt: (elementType, wrapper) => {
            replacement = wrapper;
            const result = adoptMountedType(reactRootFibers(), elementType, wrapper, bound);
            entries = result.fibers.map((fiber) => ({fiber, hadParent: fiberAttached(fiber)}));
            scheduled = result.scheduled;
            return result;
        },
        rerender: () => {
            let asked = 0;
            for (const fiber of live()) {
                invalidateFiberProps(fiber);
                if (requestRender(fiber)) asked++;
            }
            return asked;
        },
        release: (original) => {
            for (const {fiber} of entries) {
                if (fiber.type === replacement) retargetFiber(fiber, original);
            }
            entries = [];
            replacement = null;
            scheduled = false;
        },
        // `stale` is an adopted instance still drawing something other than the wrapper: a render
        // that has not happened yet, or a type React reset underneath us.
        status: () => {
            const fibers = live();
            return {
                adopted: fibers.length,
                scheduled,
                stale: fibers.filter((fiber) => fiber.type !== replacement).length,
            };
        },
    };
};

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

// The mounted instances of a component that has no public handle at all, kept by the fiber.
//
// Steam's main-menu popup host is such a component: a module-local function the popup mounts
// directly under a React root, exported nowhere, with the menu's memo export absent from that
// render path entirely. The only handle is the mounted fiber, which decky-loader's tabs hook adopts
// the same way: each fiber whose source carries the tokens has its `type` swapped for the wrapper
// `wrapFor` builds for its original. `adopt` walks only while no adopted host is still mounted, so
// running it on every publication catches a host Steam recreated without paying a tree walk for
// the one it did not.
const createSourceAdoption = (
    tokens: readonly string[],
    wrapFor: (original: any) => any,
    bound = MaximumMountedNodes,
) => {
    // Each adopted fiber's original, the wrapper it was given and whether it had a parent when
    // adopted; see fiberAttached.
    const adopted = new Map<any, {original: any; wrapper: any; hadParent: boolean}>();
    const prune = () => {
        for (const [fiber, {hadParent}] of [...adopted]) {
            if (hadParent && !fiberAttached(fiber)) adopted.delete(fiber);
        }
    };
    return {
        adopt: () => {
            prune();
            if (adopted.size > 0) return 0;
            let count = 0;
            walkFibers(reactRootFibers(), bound, (fiber) => {
                const type = fiber.type;
                if (adopted.has(fiber) || !sourceMatches(type, tokens)) return false;
                const wrapper = wrapFor(type);
                adopted.set(fiber, {original: type, wrapper, hadParent: fiberAttached(fiber)});
                retargetFiber(fiber, wrapper);
                invalidateFiberProps(fiber);
                requestRender(fiber);
                count++;
                return false;
            });
            return count;
        },
        release: () => {
            // Only a fiber still drawing our wrapper is handed back; a type something else set since
            // is not ours to overwrite.
            for (const [fiber, {original, wrapper}] of adopted) {
                if (fiber.type === wrapper) retargetFiber(fiber, original);
            }
            adopted.clear();
        },
        count: () => {
            prune();
            return adopted.size;
        },
    };
};

// Hands adopted instances back to the function the claim displaced. No render is requested: the
// original draws again whenever the page next renders, and a wrapper left on screen until then
// passes Steam's tree through once its gate is removed.
const releaseMountedType = (roots, elementType, replacement, original, bound: number) => {
    let released = 0;
    for (const fiber of mountedFibersOf(roots, elementType, bound)) {
        if (fiber.type !== replacement) continue;
        retargetFiber(fiber, original);
        released++;
    }
    return released;
};
