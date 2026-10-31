/**
 * Registers a host page: its renderer, its generation-bound gate and the routed frame it is drawn in.
 *
 * The gate is a `defineGate` gate: its probe resolves `components(runtime)` and requires every name
 * in `required`, and never calls `prepare`, which only the install runs. The frame is the page's
 * whole chrome: the kit stylesheet and the page's own, the offset below Steam's header, a banner for
 * the host's notice or error and for a command the page sent that was refused, Steam's tabs (the
 * active one host-owned or the page's own) with an optional footer action per tab, and the loading
 * or refusal state until the host has published. `Page` draws only the content.
 * @param definition Stable renderer/gate/patch identities, the frame and the page's content.
 * @returns Live page accessors; removal clears UI and state while the React instance may remain available.
 */
function registerSteamPage(definition: SteamPageDefinition): SteamPageContext {
    let ui: any = null;
    let react: any = null;
    let state: any = null;
    let refusal: string | null = null;
    // A page message of the frame's own: why a command the page sent was refused, or what the page
    // reported. Cleared by the next command and by Dismiss; the host's own banner is separate.
    let message: { text: string; error: boolean } | null = null;
    const tabs = definition.tabs ?? null;
    let localTab = tabs?.initial ?? "";
    const dismissCommand = definition.dismiss === undefined ? SteamUiBrowse.dismiss : definition.dismiss;
    const components = definition.components ?? resolveSteamUiComponents;
    const required = definition.required ?? SteamUiPageRequired;
    const title = definition.title;
    const id = definition.id ?? `steam-ui-page-${definition.template}`;
    // What the frame redraws on: the gate's resolution, its state and refusals, the page message,
    // the local tab and the removal.
    const local = createLocalStore();
    const notify = local.changed;

    const report = (text: unknown, error = true) => {
        const shown = text === null || text === undefined || text === "" ? null : steamUiErrorText(text);
        message = shown ? { text: shown, error } : null;
        notify();
    };
    const send = (command: string, payload: any = {}) => request(definition.patchId, command, payload);
    const act = (command: string, payload: any = {}) => {
        if (message) {
            message = null;
            notify();
        }
        return send(command, payload).catch((error) => {
            report(error);
            return undefined;
        });
    };
    const tabList = (): SteamPageTab[] => {
        if (!tabs) return [];
        try {
            const list = typeof tabs.list === "function" ? tabs.list(state, context) : tabs.list;
            return Array.isArray(list) ? list : [];
        } catch {
            return [];
        }
    };
    // The tab on screen: the host's while it owns it, the page's own otherwise, and the first one for
    // a value no tab carries.
    const activeTab = () => {
        const list = tabList();
        const wanted = tabs?.source === "host" ? String(state?.activeTab ?? "") : localTab;
        return list.some((tab) => tab.id === wanted) ? wanted : (list[0]?.id ?? "");
    };
    const setTab = (tab: string) => {
        if (!tabs || typeof tab !== "string") return;
        if (tabs.source === "host") {
            void act(tabs.command ?? SteamUiBrowse.setTab, { tab });
            return;
        }
        if (tab === localTab) return;
        localTab = tab;
        notify();
    };

    const context: SteamPageContext = {
        react: () => react,
        ui: () => ui,
        state: () => state,
        refusal: () => refusal,
        act,
        send,
        report,
        tab: activeTab,
        setTab,
        // A hook, so a modal drawn outside the page's tree redraws with the page's state.
        useState: () => {
            react.useSyncExternalStore(local.subscribe, local.revision, local.revision);
            return state;
        },
    };

    // Pure: the components and the required list, never `prepare`, which acquires resources.
    const find = (runtime) => {
        const resolved = components(runtime);
        const missing = required.filter((name) => !resolved?.[name]);
        if (missing.length) {
            return gateIncompatible(`Native Steam components unavailable: ${missing.join(", ")}`, {
                missing,
            });
        }
        return gateReady({ resolved, runtime }, { components: required.length });
    };

    const gate = defineGate({
        name: definition.gate,
        find,
        bind: ({ resolved, runtime }, ledger) => {
            const refused = definition.prepare?.(resolved, runtime) ?? null;
            if (refused) return refused;
            if (definition.release) ledger.cleanup("page resources", definition.release);
            ui = resolved;
            react = resolved.react;
            ledger.subscribe(definition.patchId, (next) => {
                state = next;
                notify();
            });
            ledger.subscribeRefusal(definition.patchId, (reason) => {
                refusal = reason;
                notify();
            });
            // A mounted page draws nothing from the moment the gate is removed, rather than the
            // controls it last had.
            ledger.cleanup("page state", () => {
                state = null;
                refusal = null;
                message = null;
                localTab = tabs?.initial ?? "";
                ui = null;
            });
            notify();
        },
        unbind: () => {
            ui = null;
            notify();
        },
        status: () => ({
            refused: refusal,
            published: state !== null,
            tab: tabs ? activeTab() : "",
            message: message?.text ?? "",
            ...(definition.status?.() ?? {}),
        }),
    });

    // The banner: the page's own message first, because it answers what the user just did, then the
    // host's notice or error. Dismiss clears the page's message here and sends the dismiss command
    // for the host's.
    const banner = () => {
        if (message) return { text: message.text, error: message.error, onDismiss: () => report(null) };
        if (!state || definition.banner === false) return null;
        const published = definition.banner ? definition.banner(state) : steamPageMessage(state);
        if (!published?.text) return null;
        const command = published.dismiss === undefined ? dismissCommand : published.dismiss;
        return {
            text: String(published.text),
            error: !!published.error,
            onDismiss: command ? () => void act(command) : undefined,
        };
    };

    const content = (props: any) => {
        const h = react.createElement;
        if (definition.waitForState !== false && state === null) {
            return renderSteamUiEmpty(
                ui,
                refusal
                    ? { kind: "unavailable", text: refusal }
                    : { kind: "loading", text: definition.loading ?? `Loading ${title}…` },
            );
        }
        if (!tabs) return h(definition.Page, { context, state, tab: "", page: props.page });
        const active = activeTab();
        return h(ui.tabs, {
            autoFocusContents: true,
            activeTab: active,
            onShowTab: setTab,
            tabs: tabList().map((tab) => ({
                id: tab.id,
                title: tab.title,
                // Only the active tab is drawn: a tab's content fetches what it shows when it first
                // appears, and a hidden one would fetch for nothing.
                content:
                    tab.id === active
                        ? h(definition.Page, { key: tab.id, context, state, tab: tab.id, page: props.page })
                        : null,
                footer: steamPageTabFooter(tab.footer),
            })),
        });
    };

    // One component for the life of the asset. The page host draws it on every router render, and a
    // component declared inside the renderer would be a new type each time: React would remount the
    // page and drop its state and the controller's focus.
    function SteamPageFrame(props: any) {
        react.useSyncExternalStore(local.subscribe, local.revision, local.revision);
        // After the hooks, so a render that finds the gate removed calls the same ones.
        if (!ui) {
            return react.createElement(
                "div",
                {
                    role: "status",
                    style: {
                        marginTop: definition.header === false ? 0 : "var(--basicui-header-height, 40px)",
                        padding: "24px 48px",
                        opacity: 0.8,
                    },
                },
                gate.status().lastError || "Loading…",
            );
        }
        const h = react.createElement;
        const shown = banner();
        const page = h(
            "div",
            {
                id,
                className: `steam-ui-kit-page${definition.header === false ? " flush" : ""}${tabs ? " tabbed" : ""}`,
                "aria-label": title,
            },
            steamUiKitStyle(react),
            definition.style ? h("style", { key: "page-style" }, definition.style) : null,
            shown
                ? h("div", { className: "steam-ui-kit-page-banner" }, renderSteamUiBanner(ui, shown))
                : null,
            content(props),
        );
        // The root must take focus so Back reaches page navigation before the router stack.
        return ui.focusable
            ? h(ui.focusable, { className: "steam-ui-page-root", autoFocus: true, style: { height: "100%" } }, page)
            : page;
    }

    // React comes from the page host before the gate has supplied it; Steam has one.
    registerSteamPageRenderer(definition.template, (hostReact, page) => {
        react ??= hostReact;
        return react.createElement(SteamPageFrame, { page });
    });
    registerGate(definition.gate, gate);
    return context;
}

// The host's message on a page that publishes `error` and `notice`: the error first.
const steamPageMessage = (state: any): SteamPageBanner | null =>
    state?.error
        ? { text: String(state.error), error: true }
        : state?.notice
          ? { text: String(state.notice) }
          : null;

// A tab's footer action as the props Steam's tabbed page hands its footer legend: the secondary (X)
// button by default, or the options (menu) button.
const steamPageTabFooter = (footer: SteamPageTab["footer"]) => {
    if (!footer || typeof footer.onActivate !== "function") return undefined;
    return footer.button === "options"
        ? { onOptionsButton: footer.onActivate, onOptionsActionDescription: footer.label }
        : { onSecondaryButton: footer.onActivate, onSecondaryActionDescription: footer.label };
};

// What a refused command says, as text: a bridge rejection carries its reason as the message.
const steamUiErrorText = (error: unknown) =>
    String((error as any)?.message ?? error ?? "").replace(/^Error: /u, "") || "The request failed.";

/** One tab of a page frame. */
type SteamPageTab = {
    /** Stable id, sent with the tab command when the host owns the tab. */
    id: string;
    /** The tab's heading. */
    title: string;
    /** A footer action while the tab is active: the secondary button by default, or the options button. */
    footer?: { label: string; onActivate: () => void; button?: "secondary" | "options" } | null;
};

/** A banner the host's state asks for. */
type SteamPageBanner = {
    /** The words; nothing is drawn without them. */
    text: string;
    /** Draws the error tone. */
    error?: boolean;
    /** The command Dismiss sends instead of the page's; false or null for no Dismiss button. */
    dismiss?: string | false | null;
};

/** Renderer, component requirements, frame and resource ownership for a registered host page. */
type SteamPageDefinition = {
    /** The renderer template the page host draws the page with. */
    template: string;
    /** The name the gate registers under. */
    gate: string;
    /** The bridge identity the page's state is published under and its commands go to. */
    patchId: string;
    /** The page's accessible name, and what its loading line names. */
    title: string;
    /** The page root's DOM id, which `style` scopes its rules by. Defaults to `steam-ui-page-<template>`. */
    id?: string;
    /** Resolves the components the page draws with; `resolveSteamUiComponents` unless the page needs another. */
    components?: (runtime: any) => any;
    /** The components the page cannot draw without; `SteamUiPageRequired` unless the page needs more. */
    required?: readonly string[];
    /** Acquires page resources; returns a refusal reason or null on success. */
    prepare?: (ui: any, runtime: any) => string | null;
    /** Releases what `prepare` resolved. Optional. */
    release?: () => void;
    /** Extra facts for the gate's status. Optional. */
    status?: () => Record<string, unknown>;
    /** The page's own stylesheet, drawn after the kit's. */
    style?: string;
    /** False for a page that draws under Steam's header itself; the frame offsets below it otherwise. */
    header?: boolean;
    /** What the frame says until the host first publishes. */
    loading?: string;
    /** False for a page that draws before the host publishes; the frame waits for state otherwise. */
    waitForState?: boolean;
    /** Steam's tabs: the host's `activeTab` with its tab command, or the page's own, with the tab list. */
    tabs?: {
        source: "host" | "local";
        list: SteamPageTab[] | ((state: any, context: SteamPageContext) => SteamPageTab[]);
        /** The local tab a page starts on; the first tab otherwise. */
        initial?: string;
        /** The command a host-owned tab change sends; `setTab` otherwise. */
        command?: string;
    };
    /** The host's banner for a state: its `error` or `notice` otherwise; false for none. */
    banner?: false | ((state: any) => SteamPageBanner | null);
    /** The command a host banner's Dismiss sends; `dismiss` otherwise, null for no Dismiss. */
    dismiss?: string | null;
    /** The page's content: a component declared once, drawn inside the frame with the active tab. */
    Page: (props: { context: SteamPageContext; state: any; tab: string; page: any }) => any;
};

/** Live accessors; callers must handle unavailable components and unpublished state. */
type SteamPageContext = {
    /** Steam's React, from the components or, before they resolve, from the page host. */
    react: () => any;
    /** The resolved components, or null while the gate is not installed. */
    ui: () => any;
    /** The latest state the host published for the page, or null. */
    state: () => any;
    /** Why the host's latest state could not be delivered, or null. */
    refusal: () => string | null;
    /**
     * Sends a page command. It clears the page's message first and, when the host refuses, shows the
     * refusal in the page's banner and resolves undefined; it never rejects.
     */
    act: (command: string, payload?: any) => Promise<any>;
    /** Sends a page command and hands back the host's answer or its refusal, for a caller that shows it. */
    send: (command: string, payload?: any) => Promise<any>;
    /** Shows a page message in the banner (an error unless `error` is false); null clears it. */
    report: (text: unknown, error?: boolean) => void;
    /** The tab on screen, or "" for a page without tabs. */
    tab: () => string;
    /** Changes the tab: the host's through its tab command, or the page's own. */
    setTab: (tab: string) => void;
    /** A hook that redraws its component with the page's state; it works inside a modal too. */
    useState: () => any;
};
