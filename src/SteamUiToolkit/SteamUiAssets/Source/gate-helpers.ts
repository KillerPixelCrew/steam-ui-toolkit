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

// Valve's localize-with-fallback, chosen by what its source does rather than by parameter names:
// it passes the token alone to LocalizeString and returns the token when no string exists. The
// tokens "LocalizeString(e)" and "void 0===r?e" held until the September 2026 beta's minifier
// renamed the parameters and flipped the comparison. Its siblings differ in what they do: the quiet
// variant passes !0, the presence test compares with null, and the formatting variant builds
// elements.
const isLocalizer = (source: string) =>
    source.includes(".LocalizeString(") &&
    source.includes("void 0") &&
    !source.includes("!0)") &&
    !source.includes("!=null") &&
    !source.includes("createElement");

// mobx-react-lite's useObserver, found by its shape in the module that carries the startup check,
// or null. Wanted by the surfaces that use it, never required.
const findUseObserver = (runtime) => {
    const observer = runtime.findUnique(ObserverTokens);
    if (!observer) return null;
    const exports = runtime(observer[0]);
    const hooks = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return typeof value === "function" && value.length === 2 && String(value).includes('"observed"');
    });
    return hooks.length === 1 ? exports[hooks[0]] : null;
};

// A webpack class map, unwrapped when the module is an ES default export.
const classMapOf = (exported) => (exported && exported.__esModule ? exported.default : exported);

// An element's props with its key carried along. The key lives on the element, not in props, and
// dropping it would re-key the node inside its parent's child list on every render.
const keyed = (element, props = element.props) =>
    element.key === null ? props : {...props, key: element.key};

// Maps an element's children and clones it only when one changed; a child mapped to null is
// dropped. An element with no children, or with more than `maximum`, is returned as it is.
const mapChildren = (react, element, map: (child: any) => unknown, maximum = Infinity) => {
    const kids = react.Children.toArray(element.props?.children);
    if (!kids.length || kids.length > maximum) return element;
    let changed = false;
    const next: unknown[] = [];
    for (const kid of kids) {
        const replacement = map(kid);
        changed ||= replacement !== kid;
        if (replacement !== null) next.push(replacement);
    }
    return changed ? react.cloneElement(element, {}, ...next) : element;
};

// Renders a plain function component through a wrapper, so what it returns can be changed as well:
// a component's children do not exist until it renders. The wrapper `wrap` builds is cached against
// the component, because a fresh type on every render would remount the subtree. Class components,
// memo and forwardRef objects are left alone, since they cannot be called directly and wrapping
// them would change identity for refs; for those, and for anything that is not an element of a
// function type, this answers null.
const descendInto = (react, element, cache, wrap: (type: any) => unknown) => {
    const type = element.type;
    if (typeof type !== "function" || type.prototype?.isReactComponent) return null;
    let wrapper = cache.get(type);
    if (!wrapper) {
        wrapper = wrap(type);
        cache.set(type, wrapper);
    }
    return react.createElement(wrapper, keyed(element));
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

// Ends a gate's bridge subscription, if it holds one, and answers the cleared handle.
const endSubscription = (unsubscribe: (() => void) | null) => {
    unsubscribe?.();
    return null;
};
