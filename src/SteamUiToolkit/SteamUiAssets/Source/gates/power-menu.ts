// Steam's own "Switch to Desktop" in the Big Picture power menu, answered by the host.
//
// Mapped from the installed client on 2026-09-28. The power menu is a module-private mobx observer
// function component: nothing exports it and its render cannot be claimed, so its root is found
// where it passes through the JSX runtime (interceptElements in ownership.ts). Valve draws the
// entry only when `TS.IN_GAMESCOPE` is set and then calls SteamOS's session service, which does
// nothing on Windows; spoofing that platform flag would also change every other branch of the menu.
// This gate draws the entry itself instead, with the item and separator types Steam's menu already
// rendered, Steam's localized `#SwitchToDesktop` label and Valve's destructive tone, at the end of
// the menu where Valve places it. Selecting it asks the host, which owns the switch.
//
// The root is recognised by its direct children, never by its localized label: one of them is the
// Sleep or Shutdown entry, and `#Quit_Shutdown` occurs nowhere else in the client. The entry is
// drawn only while the host publishes `visible`, so the host decides when a desktop exists to
// return to.
function createPowerMenu() {
    const patchId = "steam-ui.power-menu";
    const TransformName = "powerMenu";
    const PowerTokens = new Set(["#Sleep", "#Quit_Sleep", "#Shutdown", "#Quit_Shutdown"]);
    const LabelToken = "#SwitchToDesktop";
    const EntryKey = "steam-ui-power-menu-desktop";
    const MaximumChildren = 48;
    const MaximumDepth = 4;

    let runtime;
    let react: any = null;
    let jsxRuntime: any = null;
    let localize: ((token: string) => unknown) | null = null;
    let installed = false;
    let visible = false;
    let revision = 0;
    let unsubscribe: (() => void) | null = null;
    let lastOutcome = "never rendered";
    let lastError = "";

    const isPowerEntry = (child) =>
        react.isValidElement(child) && PowerTokens.has(child.props?.strDisplayNameLocToken);

    // Steam's plain menu item: selectable, labelled by its children, not one of the confirming
    // entries that carry a localization token instead. Found among what the menu rendered, inside
    // the fragments Valve groups its sections in.
    const findItemType = (children, depth = 0) => {
        if (depth > MaximumDepth) return null;
        for (const child of children) {
            if (!react.isValidElement(child)) continue;
            const props: any = child.props ?? {};
            if (child.type === react.Fragment) {
                const found = findItemType(react.Children.toArray(props.children), depth + 1);
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
            const first = react.Children.toArray((child.props as any)?.children)[0];
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
        void request(patchId, "switchToDesktop", {}, nextActionGeneration(patchId)).catch(() => {
            // A refusal stays host-authoritative and must not make Steam's menu fail.
        });
    };

    const transform = (create, type, props, key) => {
        if (!visible || typeof props?.onCancel !== "function" || typeof props.label !== "string") return undefined;
        if (!installed || !Array.isArray(props.children)) return undefined;
        const children = props.children;
        if (children.length > MaximumChildren || !children.some(isPowerEntry)) return undefined;
        if (children.some((child) => child?.key === EntryKey)) return undefined;

        const flat = react.Children.toArray(children);
        const itemType = findItemType(flat);
        if (!itemType) {
            lastOutcome = "menu item type absent";
            return undefined;
        }
        const separatorType = findSeparator(flat);
        const entry = react.createElement(
            itemType,
            {tone: "destructive", onSelected: activate},
            localizedOr(localize, LabelToken, "Switch to Desktop"),
        );
        const section = separatorType
            ? react.createElement(react.Fragment, {key: EntryKey}, react.createElement(separatorType), entry)
            : react.createElement(react.Fragment, {key: EntryKey}, entry);
        lastOutcome = `appended${separatorType ? "" : " without separator"}`;
        return create(type, {...props, children: [...children, section]}, key);
    };

    const resolve = () => {
        runtime = getWebpackRuntime("power-menu");
        react = resolveReact(runtime);
        if (!react) {
            lastError = "React runtime was not a unique match";
            return false;
        }
        if (!runtime.findUnique(["#Quit_Shutdown", LabelToken])) {
            lastError = "Power menu module was not a unique match";
            return false;
        }
        jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
        if (typeof jsxRuntime?.jsx !== "function" || typeof jsxRuntime?.jsxs !== "function") {
            lastError = "JSX runtime lacks jsx or jsxs";
            return false;
        }
        // Wanted, not required: without it the label is the English string.
        localize = resolveSteamLocalizer(runtime);
        return true;
    };

    const install = () => {
        if (installed) return {ok: true, alreadyInstalled: true};
        if (!attemptResolution(resolve, (error) => (lastError = "Power menu resolution failed: " + String(error)))) {
            return {ok: false, error: lastError};
        }
        installed = true;
        const claim = interceptElements(jsxRuntime, TransformName, transform);
        if (!claim.ok) {
            installed = false;
            lastError = claim.error ?? "the JSX runtime could not be intercepted";
            return {ok: false, error: lastError};
        }
        lastError = "";
        unsubscribe = subscribe(patchId, (state) => {
            visible = state?.visible === true;
            revision = Number.isSafeInteger(state?.revision) ? state.revision : 0;
        });
        return {ok: true, installed: true};
    };

    const remove = () => {
        if (!installed) return {ok: true, absent: true};
        const released = releaseElements(jsxRuntime, TransformName);
        if (!released.ok) {
            lastError = released.error ?? "Power menu release failed";
            return {ok: false, error: lastError};
        }
        installed = false;
        unsubscribe = endSubscription(unsubscribe);
        visible = false;
        revision = 0;
        lastOutcome = "removed";
        return {ok: true, removed: true};
    };

    const status = () => ({
        ok: true,
        installed,
        resolved: !!react && !!jsxRuntime,
        claimed: elementsIntercepted(jsxRuntime, TransformName),
        localized: !!localize,
        visible,
        revision,
        lastOutcome,
        lastError,
    });

    return {install, remove, status};
}

registerGate("powerMenu", createPowerMenu());
