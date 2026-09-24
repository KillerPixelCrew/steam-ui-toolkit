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

const optionalSteamExport = (runtime, tokens, predicate) => {
    try {
        return runtime.exported(tokens, predicate);
    } catch {
        return null;
    }
};

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
    const toggleField = uniqueSteamExport(fields, (value) => {
        const source = sourceOfSteamComponent(value);
        return source.includes("OnToggleChange") && source.includes("this.Toggle()");
    });
    const dialogButton = uniqueSteamExport(fields, (value) =>
        sourceOfSteamComponent(value).includes('"DialogButton","_DialogLayout","Secondary"'),
    );
    const dialogButtonPrimary = uniqueSteamExport(fields, (value) =>
        sourceOfSteamComponent(value).includes('"DialogButton","_DialogLayout","Primary"'),
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
        toggleField,
        dialogButton,
        dialogButtonPrimary,
        textField,
    };
};

const resolveSteamUiComponents = (runtime) => {
    const fields = resolveSteamFieldComponents(runtime);
    if (!fields) return null;

    const focusable = resolveNativeFocusable(runtime);

    const tabsFactory = runtime.findUnique([".TabRowTabs", "activeTab:"]);
    const tabs = tabsFactory
        ? uniqueSteamExport(
              runtime(tabsFactory[0]),
              (value) => value?.type && String(value.type).includes("(function()"),
          )
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

    return {
        ...fields,
        focusable,
        tabs,
        modalRoot,
        showModal,
    };
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

// An absolute route other than the root, short enough to be a route rather than a payload.
const isNavigableRoute = (route) =>
    typeof route === "string" && route.startsWith("/") && route !== "/" && route.length <= 256;

// Only a route returned by a successful host command is followed. Publications cannot inject a
// target, and the bounds keep this a router operation rather than an open-ended navigation API. A
// navigation entry's published route is the one exception, and it is followed by Valve's own entry
// only when the user selects that row.
const navigateSteamRoute = (route) => {
    if (!isNavigableRoute(route)) {
        return false;
    }
    const history = window.tempNavStore?.m_history;
    if (!history || typeof history.push !== "function") return false;
    history.push(route);
    return true;
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

// An element's props with its key carried along. The key lives on the element, not in props, and
// dropping it would re-key the node inside its parent's child list on every render.
const keyed = (element, props = element.props) =>
    element.key === null ? props : {...props, key: element.key};

// A portal is not an element: isValidElement answers false, and its children sit on the portal
// itself rather than under props. Steam's Quick Access menu draws its whole body through one into
// the popup window, so a descent that treats a portal as a leaf never reaches the tab list beneath
// it (2026-09-24). React reads a portal by `$$typeof`, `children` and `containerInfo`, so a shallow
// copy with mapped children is a portal to it.
const PortalType = Symbol.for("react.portal");
const isPortal = (value) => !!value && typeof value === "object" && value.$$typeof === PortalType;

// Maps a child list; answers the new list, or null when no child changed. A child mapped to null
// is dropped. Shared by element and portal mapping so the two cannot drift on those rules.
const mapEach = (react, children, map: (child: any) => unknown, maximum = Infinity) => {
    const kids = react.Children.toArray(children);
    if (!kids.length || kids.length > maximum) return null;
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

// Maps an element's children and clones it only when one changed. An element with no children, or
// with more than `maximum`, is returned as it is.
const mapChildren = (react, element, map: (child: any) => unknown, maximum = Infinity) => {
    const next = mapEach(react, element.props?.children, map, maximum);
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

// Ends a gate's bridge subscription, if it holds one, and answers the cleared handle.
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
// fiber the container key was written with.
const reactRootFibers = () => {
    // A page always has a document; an emitted-asset check may not, and then nothing is mounted.
    if (typeof document === "undefined") return [];
    const hosts: any[] = [];
    const root = document.getElementById("root");
    if (root) hosts.push(root);
    for (const child of Array.from(document.body?.children ?? [])) {
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
    // Each adopted fiber's original and whether it had a parent when adopted; see fiberAttached.
    const adopted = new Map<any, {original: any; hadParent: boolean}>();
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
                adopted.set(fiber, {original: type, hadParent: fiberAttached(fiber)});
                retargetFiber(fiber, wrapFor(type));
                invalidateFiberProps(fiber);
                requestRender(fiber);
                count++;
                return false;
            });
            return count;
        },
        release: () => {
            for (const [fiber, {original}] of adopted) retargetFiber(fiber, original);
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

// Mounted instances of a claimed memo still drawing something other than the memo's current
// `type`: an adoption whose render has not happened yet, or a mount the claim never reached.
const staleFibers = (roots, memo, bound: number) =>
    memo ? mountedFibersOf(roots, memo, bound).filter((fiber) => fiber.type !== memo.type).length : 0;
