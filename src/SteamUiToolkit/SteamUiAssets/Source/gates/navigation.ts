/**
 * Contributes navigation entries using native route/action rows and a reversible memo claim.
 * @returns The defineGate gate; remove must release it before its bridge is replaced.
 */

function createNavigationPanel() {
    const patchId = "steam-ui.navigation-panel";
    const claimKeys = {
        marker: "__steamUiNavigationPanelClaimed",
        original: "__steamUiNavigationPanelOriginal",
    } as const;
    // The popup's menu host, adopted by source, carries a claim of its own, so handing the hosts back
    // and handing the memo's instances back stay separate steps.
    const hostKeys = {
        marker: "__steamUiNavigationHostClaimed",
        original: "__steamUiNavigationHostOriginal",
    } as const;

    // The two tokens that identify the panel root. `#MainMenu_Title` occurs in exactly one module of
    // the 2581 the client loads, and `RunnningAppSeparator` — Valve's own typo — occurs in three, so
    // the pair is unique where neither is alone. Deliberately not the localized title: that changes
    // with the user's language, and this has to match on a client running in any of them.
    const PanelRootTokens = ["#MainMenu_Title", "RunnningAppSeparator"] as const;
    const OuterToken = "MainNavMenuContainer";

    const MaximumDescent = 12;

    let react;
    let icon;
    let memo: any = null;
    let panel: ClaimedComponent | null = null;
    let hosts: ClaimedComponent | null = null;

    // What the last render actually saw and did. Everything else can report success while the panel
    // shows exactly what Valve shipped, because insertion depends on the tree Steam rendered.
    let observed: { key: string; route: string | null; label: string }[] = [];
    let lastOutcome = "never rendered";
    // Published items refused for a route that is not one. Counted where they are refused, because
    // they never reach a render, and a row the host asked for must not vanish without a trace.
    let rejectedRoutes = 0;

    // The host's desired additions and hidden entries, replaced whole on each publication.
    let desired: {
        items: {
            id: string;
            label: string;
            icon?: string;
            glyph?: string;
            route?: string;
            before?: string;
            after?: string;
            position?: "start" | "end";
        }[];
        hidden: string[];
    } = { items: [], hidden: [] };

    const descendCache = new Map();
    const panelCache = new Map();

    // A rendered entry's identity. `route` is the descriptor's own destination and the anchor an
    // "insert after Library" is written against; the React key is Valve's descriptor key and is what
    // survives when an entry has no route at all, such as the power button.
    const identify = (element) => {
        const route = typeof element?.props?.route === "string" ? element.props.route : null;
        // Children.toArray prefixes keys with their nested array path, e.g. .0:$power.
        const rawKey = typeof element?.key === "string" ? element.key : "";
        const key = rawKey.slice(rawKey.lastIndexOf("$") + 1);
        return { key, route, label: textOf(element?.props?.label) ?? "" };
    };

    const matchesAnchor = (element, anchor) => {
        if (typeof anchor !== "string" || !anchor) return false;
        const identity = identify(element);
        return identity.route === anchor || identity.key === anchor;
    };

    // Valve's own entry components, taken from the entries this render already holds. Both are local
    // to the menu module, so a rendered sibling is the only place they can be had - and drawing an
    // added row with them is what makes it Steam's row rather than a copy of one: the same focus
    // bar, active dot, icon box and label, and the same gamepad activation. `onGamepadFocus` is the
    // panel's own handler, which clears the focused running app the way every native entry does.
    const nativeEntries = (children) => {
        let route = null;
        let action = null;
        let onGamepadFocus;
        for (const child of children) {
            if (!react.isValidElement(child) || typeof child.type !== "function") continue;
            const props: any = child.props ?? {};
            if (!route && typeof props.route === "string") {
                route = child.type;
            } else if (
                !action &&
                typeof props.action === "function" &&
                !("route" in props) &&
                !("app" in props) &&
                !("stream" in props)
            ) {
                action = child.type;
            }
            if (!onGamepadFocus && typeof props.onGamepadFocus === "function") {
                onGamepadFocus = props.onGamepadFocus;
            }
        }
        return { route, action, onGamepadFocus };
    };

    // The row's glyph: the host's own path data, or a toolkit glyph by name, or none.
    const iconOf = (item) =>
        (item.glyph ? renderSteamGlyph(react, item.glyph) : null) ??
        (item.icon && icon ? icon(item.icon) : null);

    // The main menu is a side panel, which closes before any page it opens.
    const activate = (id) => activateSteamPanelEntry(patchId, id);

    // One added entry, drawn by Valve's own component. An entry with a route uses the route entry,
    // which matches the route for its active state and navigates with Valve's own action exactly as
    // Library does; the route is held to the bounds navigateSteamRoute applies, and is only followed
    // when the user selects the row. Anything else uses the action entry and asks the host.
    // Without the component it needs there is no row: an imitation would be a control that looks
    // like Steam's and behaves like something else, so it is counted instead.
    const renderItem = (item, native) => {
        const common = {
            key: `steam-ui-nav-${item.id}`,
            label: item.label,
            icon: iconOf(item),
            onGamepadFocus: native.onGamepadFocus,
        };
        if (item.route && isNavigableRoute(item.route) && native.route) {
            return react.createElement(native.route, {
                ...common,
                route: item.route,
                active: "if-within-route",
            });
        }
        if (!item.route && native.action) {
            return react.createElement(native.action, {
                ...common,
                action: item.frontendAction ?? (() => activate(item.id)),
            });
        }
        return null;
    };

    // Applies the host's list to the panel's own children.
    //
    // Order of operations matters and is fixed: hide first, then insert. Anchoring an insertion to an
    // entry that was just hidden would otherwise place it against something the user cannot see, and
    // "after Library" would silently become "at the end" depending on an unrelated setting.
    const applyEntries = (children) => {
        const kept: unknown[] = [];
        observed = [];
        let hidden = 0;
        for (const child of children) {
            const identity = identify(child);
            if (react.isValidElement(child) && (identity.route || identity.key)) {
                observed.push(identity);
                if (
                    desired.hidden.includes(identity.route ?? "") ||
                    desired.hidden.includes(identity.key)
                ) {
                    hidden++;
                    continue;
                }
            }
            kept.push(child);
        }

        const pending = [
            ...desired.items,
            ...pluginFrontendItems("menu").map((slot) => slot.value),
        ];
        // From every child, hidden ones included: hiding Power must not cost the action entry.
        const native = nativeEntries(children);
        const placed = new Set<string>();
        const result: unknown[] = [];
        let unrendered = 0;
        const place = (item) => {
            placed.add(item.id);
            const row = renderItem(item, native);
            if (row) result.push(row);
            else unrendered++;
        };
        for (const item of pending) {
            if (item.position === "start") place(item);
        }

        for (const child of kept) {
            for (const item of pending) {
                if (!placed.has(item.id) && matchesAnchor(child, item.before)) place(item);
            }
            result.push(child);
            for (const item of pending) {
                if (!placed.has(item.id) && matchesAnchor(child, item.after)) place(item);
            }
        }

        // Anything left over goes at the end, including an entry whose anchor is not in this panel.
        // Dropping it would be the silent-control failure the guidance forbids: the caller asked for a
        // row and would have no way to tell that Steam simply does not have the item it named.
        let orphaned = 0;
        for (const item of pending) {
            if (placed.has(item.id)) continue;
            if (item.before || item.after) orphaned++;
            place(item);
        }

        lastOutcome =
            `entries=${observed.length} hidden=${hidden} added=${pending.length - unrendered} ` +
            `orphaned=${orphaned} unrendered=${unrendered}`;
        return result;
    };

    // Wraps the panel root so its OUTPUT can be changed. Cached against the original, because a fresh
    // component identity on every render would remount the whole menu each time React reconciles it.
    const wrapPanelRoot = (original) => {
        let wrapped = panelCache.get(original);
        if (wrapped) return wrapped;
        wrapped = function SteamUiNavigationPanel(props) {
            const tree = original(props);
            if (!react.isValidElement(tree)) return tree;
            const children = react.Children.toArray(tree.props?.children);
            if (!children.length) {
                lastOutcome = `panel had ${children.length} children; left alone`;
                return tree;
            }
            return react.cloneElement(tree, {}, ...applyEntries(children));
        };
        panelCache.set(original, wrapped);
        return wrapped;
    };

    const isPanelRoot = (type) => sourceMatches(type, PanelRootTokens);

    // Descends the rendered tree to the panel root. Function components on the way down are replaced
    // by wrappers that render the original and keep descending (descendInto); anything else is
    // descended through its children.
    const navigationDescender = (type) =>
        function SteamUiNavigationDescend(props) {
            return descend(type(props), 0);
        };
    const descend = (element, depth) => {
        if (depth > MaximumDescent || !react.isValidElement(element)) return element;
        if (isPanelRoot(element.type)) {
            return react.createElement(wrapPanelRoot(element.type), keyed(element));
        }
        return (
            descendInto(react, element, descendCache, navigationDescender) ??
            mapChildren(react, element, (kid) => descend(kid, depth + 1))
        );
    };

    // The popup's menu host, which has no public handle: a module-local function the popup mounts
    // directly under a React root, exported nowhere, with the memo this gate claims absent from its
    // render path. Recognised by three prop names its author destructures and adopted by the fiber,
    // as decky-loader adopts the Quick Access view. It persists while the menu is closed and
    // re-renders when `open` flips, so an adoption shows on the next open.
    const MenuHostTokens = ["MainNavMenuContainer", "onFocusNavDeactivated", "popup:"] as const;

    // Pure: the menu module, the panel root and the one memo export whose component draws the outer
    // container, selected by what it draws and never by its minified export name, through the gate's
    // own claim, with a `type` the claim can replace and restore. The memo is an export, so nothing
    // has to be mounted for the gate to hold; what is on screen is adopted at install.
    const find = (runtime) => {
        const resolvedReact = resolveReact(runtime);
        if (!resolvedReact) return gateIncompatible("React runtime was not a unique match", { react: 0 });
        const menuFactory = runtime.findUnique([PanelRootTokens[0], OuterToken]);
        if (!menuFactory) {
            return gateIncompatible("main menu module was not a unique match", { menuModule: 0, react: 1 });
        }
        // The panel root itself, matched the way the gate matches it while descending.
        const panelRoot = runtime.count([...PanelRootTokens]);
        const exports = runtime(menuFactory[0]);
        const candidates = Object.keys(exports).filter((name) => {
            const value = exports[name];
            return (
                value &&
                typeof value === "object" &&
                sourceMatches(unclaimedValue(value.type, claimKeys), [OuterToken])
            );
        });
        const found = candidates.length === 1 ? exports[candidates[0]] : null;
        const descriptor = found ? Object.getOwnPropertyDescriptor(found, "type") : null;
        const facts = {
            menuModule: 1,
            panelRoot,
            memoExports: candidates.length,
            // Writable and configurable, or the claim could neither replace nor restore it.
            claimable: descriptor?.writable === true && descriptor.configurable === true,
            // Already ours is compatible: a find that demanded the pre-claim shape would make a
            // successful apply fail its own next probe.
            claimed: !!found && memberClaimed(found, "type", claimKeys),
            react: 1,
        };
        if (panelRoot !== 1) return gateIncompatible("main menu panel root was not a unique match", facts);
        if (!found) {
            return gateIncompatible(`main menu export was ${candidates.length ? "ambiguous" : "absent"}`, facts);
        }
        if (!facts.claimed && !facts.claimable) {
            return gateIncompatible("the main menu memo's type is not writable and configurable", facts);
        }
        return gateReady({ react: resolvedReact, memo: found }, facts);
    };

    // What every claimed component draws: its original, then the descent to the panel root.
    const descending = (original) =>
        function SteamUiNavigationRoot(props) {
            return descend(original(props), 0);
        };

    const gate = defineGate({
        name: "navigation-panel",
        find,
        bind: (handles, ledger) => {
            react = handles.react;
            icon = createIconRenderer(react);
            memo = handles.memo;
            // The memo object is the public handle, and every consumer holds the same one, so claiming
            // its `type` reaches the panel wherever it is rendered without patching a single caller.
            // The claim reaches the next mount only; what is already on screen is adopted.
            const claimedPanel = ledger.component({ host: memo, keys: claimKeys, wrap: descending });
            if (!claimedPanel.ok) return claimedPanel.error;
            const claimedHosts = ledger.component({
                keys: hostKeys,
                match: (_fiber, type) => sourceMatches(type, MenuHostTokens),
                wrap: descending,
            });
            if (!claimedHosts.ok) return claimedHosts.error;
            panel = claimedPanel;
            hosts = claimedHosts;
            ledger.subscribe(patchId, (state) => {
                const items = Array.isArray(state?.items) ? state.items : [];
                const hidden = Array.isArray(state?.hidden) ? state.hidden : [];
                const named = items.filter(
                    (item) => item && typeof item.id === "string" && typeof item.label === "string",
                );
                const routable = named.filter(
                    (item) => item.route == null || isNavigableRoute(item.route),
                );
                rejectedRoutes = named.length - routable.length;
                const next = {
                    items: routable,
                    hidden: hidden.filter((value) => typeof value === "string"),
                };
                // A host Steam has recreated since install is adopted here; it sits under a React root
                // with no class above it, so its entries show when the menu next opens. Only while no
                // adopted host is still mounted, so a publication costs no tree walk otherwise.
                if (claimedHosts.status().adopted === 0) claimedHosts.rerender();
                // The wrappers read `desired` from their closure, so a publication changes nothing
                // React can see on its own, and an unchanged one needs no render at all.
                if (!publicationChanged(desired, next)) return;
                desired = next;
                claimedPanel.rerender();
            });
            ledger.cleanup("plugin frontends", subscribePluginFrontends(() => claimedPanel.rerender()));
        },
        unbind: () => {
            panel = null;
            hosts = null;
            desired = { items: [], hidden: [] };
            descendCache.clear();
            panelCache.clear();
            lastOutcome = "removed";
        },
        status: () => ({
            claimed: memberClaimed(memo, "type", claimKeys),
            // Everything above can be true while the panel shows exactly what Valve shipped, because
            // insertion depends on the tree Steam rendered. This is the part that says what happened.
            entries: observed,
            items: desired.items.length,
            // Whether the claim reached the panels already on screen, and how many popup menu hosts
            // are adopted by source, the path the memo claim never reaches. Nothing adopted is inert.
            mounted: {
                ...(panel?.status() ?? { adopted: 0, scheduled: false, stale: 0 }),
                hosts: hosts?.status().adopted ?? 0,
            },
            rejectedRoutes,
            hidden: desired.hidden.length,
            lastOutcome,
        }),
    });
    return gate;
}

registerGate("navigationPanel", createNavigationPanel());
