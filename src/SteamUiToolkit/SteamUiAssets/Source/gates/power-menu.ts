/**
 * Projects host power-menu additions and visibility into Steam's existing menu.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createPowerMenu() {
    const patchId = "steam-ui.power-menu";
    // The confirming entries carry their token as a prop. In the 2026-10-20 client Sleep is a plain
    // item, so Shutdown and Restart identify the menu; the Sleep tokens stay for older clients.
    const PowerTokens = new Set([
        "#Sleep",
        "#Quit_Sleep",
        "#Shutdown",
        "#Quit_Shutdown",
        "#Restart",
        "#Quit_Restart",
    ]);
    const LabelToken = "#SwitchToDesktop";
    const EntryKey = "steam-ui-power-menu-desktop";
    const MaximumDepth = 4;

    let react: any = null;
    let jsxRuntime: any = null;
    let localize: ((token: string) => unknown) | null = null;
    let visible = false;
    // What the first power menu rendered, kept for the session: the menu's own type, which makes
    // every later element a single identity test, the item and separator types, and the label.
    let menuType: unknown = null;
    let itemType: unknown = null;
    let separatorType: unknown = null;
    let label = "";
    let lastOutcome = "never rendered";

    const isPowerEntry = (child) =>
        react.isValidElement(child) && PowerTokens.has(child.props?.strDisplayNameLocToken);

    // Steam's plain menu item: selectable, labelled by its children, not one of the confirming
    // entries that carry a localization token instead. Found among what the menu rendered, inside
    // the fragments Valve groups its sections in.
    const findItemType = (children, depth = 0) => {
        if (depth > MaximumDepth || !Array.isArray(children)) return null;
        for (const child of children) {
            if (!react.isValidElement(child)) continue;
            const props: any = child.props ?? {};
            if (child.type === react.Fragment) {
                const found = findItemType(props.children, depth + 1);
                if (found) return found;
            } else if (
                typeof props.onSelected === "function" &&
                props.strDisplayNameLocToken === undefined &&
                typeof props.children === "string"
            ) {
                return child.type;
            }
        }
        return null;
    };

    // Valve's separator opens each of its fragment sections and is the one element there with no
    // props at all. Wanted, not required: without it the entry is drawn unseparated.
    const findSeparator = (children) => {
        for (const child of children) {
            if (!react.isValidElement(child) || child.type !== react.Fragment) continue;
            const inner = (child.props as any)?.children;
            const first = Array.isArray(inner) ? inner[0] : inner;
            if (
                react.isValidElement(first) &&
                first.type !== react.Fragment &&
                typeof first.type !== "string" &&
                Object.keys(first.props ?? {}).length === 0
            ) {
                return first.type;
            }
        }
        return null;
    };

    const activate = () => {
        void request(patchId, "switchToDesktop", {}).catch(() => {
            // A refusal stays host-authoritative and must not make Steam's menu fail.
        });
    };
    const entryProps = {tone: "destructive", onSelected: activate};

    // Recognises the power menu the first time it renders and remembers what it drew with.
    const learn = (type, children) => {
        if (!children.some(isPowerEntry)) return false;
        const item = findItemType(children);
        if (!item) {
            lastOutcome = "menu item type absent";
            return false;
        }
        menuType = type;
        itemType = item;
        separatorType = findSeparator(children);
        label = localizedOr(localize, LabelToken, "Switch to Desktop");
        return true;
    };

    const transform = (create, type, props, key) => {
        if (!visible || (menuType !== null && type !== menuType)) return undefined;
        const children = props?.children;
        if (!Array.isArray(children)) return undefined;
        if (menuType === null) {
            // The root is recognised by its label and power entries alone. Its onCancel is whatever
            // opened the menu passed, and Steam's main menu Power entry and its system request pass
            // none; only the controller power chord does. The root is created before the inner menu
            // it renders with the same props, so the first match is the root.
            if (typeof props.label !== "string") return undefined;
            if (!learn(type, children)) return undefined;
        } else if (!children.some(isPowerEntry)) {
            // Valve's menu component draws other menus too.
            return undefined;
        }
        const section = react.createElement(
            react.Fragment,
            {key: EntryKey},
            separatorType ? react.createElement(separatorType) : null,
            react.createElement(itemType, entryProps, label),
        );
        lastOutcome = `appended${separatorType ? "" : " without separator"}`;
        return create(type, {...props, children: [...children, section]}, key);
    };

    // Pure: the menu module is counted, never loaded, and React and the JSX runtime must each be one
    // match. Nothing here waits on Steam drawing anything: the menu is recognised when it renders.
    const find = (runtime) => {
        const facts = {
            menuModule: runtime.count(["#Quit_Shutdown", LabelToken]),
            react: runtime.count([...ReactTokens]),
            jsx: runtime.count([...JsxRuntimeTokens]),
        };
        const resolvedReact = resolveReact(runtime);
        if (!resolvedReact) return gateIncompatible("React runtime was not a unique match", facts);
        if (facts.menuModule !== 1) return gateIncompatible("Power menu module was not a unique match", facts);
        if (facts.jsx !== 1) return gateIncompatible("JSX runtime was not a unique match", facts);
        const resolvedJsx = runtime.resolve([...JsxRuntimeTokens]);
        if (!resolvedJsx) return gateIncompatible("JSX runtime was not a unique match", facts);
        // Wanted, not required: without it the label is the English string.
        return gateReady(
            { react: resolvedReact, jsxRuntime: resolvedJsx, localize: resolveSteamLocalizer(runtime) },
            facts,
        );
    };

    return defineGate({
        name: "power-menu",
        find,
        bind: (handles, ledger) => {
            react = handles.react;
            jsxRuntime = handles.jsxRuntime;
            localize = handles.localize;
            const claim = ledger.elements(jsxRuntime, patchId, transform);
            if (!claim.ok) return claim.error ?? "the JSX runtime could not be intercepted";
            ledger.subscribe(patchId, (state) => {
                visible = state?.visible === true;
            });
        },
        unbind: () => {
            visible = false;
            menuType = itemType = separatorType = null;
            label = "";
            lastOutcome = "removed";
        },
        status: () => ({
            claimed: elementsIntercepted(jsxRuntime, patchId),
            localized: !!localize,
            recognised: menuType !== null,
            visible,
            lastOutcome,
        }),
    });
}

registerGate("powerMenu", createPowerMenu());
