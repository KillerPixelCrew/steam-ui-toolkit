/**
 * Registers a renderer and generation-bound gate for one host page. The gate is a `defineGate`
 * gate: its probe resolves `components(runtime)` and requires every name in `required`, and never
 * calls `prepare`, which only the install runs.
 * @param definition Stable renderer/gate/patch identities, component requirements and lifecycle callbacks.
 * @returns Live page accessors; removal clears UI and state while the React instance may remain available.
 */
function registerSteamPage(definition: SteamPageDefinition): SteamPageContext {
    let installed = false;
    let ui: any = null;
    let react: any = null;
    let state: any = null;
    let refusal: string | null = null;
    // What the frame redraws on: the gate's resolution, its state and refusals, and its removal.
    const local = createLocalStore();
    const notify = local.changed;

    const context: SteamPageContext = {
        react: () => react,
        ui: () => ui,
        state: () => state,
        refusal: () => refusal,
    };

    // Pure: the components and the required list, never `prepare`, which acquires resources.
    const find = (runtime) => {
        const resolved = definition.components(runtime);
        const missing = definition.required.filter((name) => !resolved?.[name]);
        if (missing.length) {
            return gateIncompatible(`Native Steam components unavailable: ${missing.join(", ")}`, {
                missing,
            });
        }
        return gateReady({ resolved, runtime }, { components: definition.required.length });
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
                ui = null;
            });
            notify();
        },
        unbind: () => {
            ui = null;
            notify();
        },
        status: () => ({ refused: refusal, ...(definition.status?.() ?? {}) }),
    });

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
                        marginTop: "var(--basicui-header-height, 40px)",
                        padding: "24px 48px",
                        opacity: 0.8,
                    },
                },
                gate.status().lastError || "Loading…",
            );
        }
        // The root must take focus so Back reaches page navigation before the router stack.
        return ui.focusable
            ? react.createElement(
                  ui.focusable,
                  {className: "steam-ui-page-root", autoFocus: true, style: {height: "100%"}},
                  react.createElement(definition.Page, {context, page: props.page}),
              )
            : react.createElement(definition.Page, {context, page: props.page});
    }

    // React comes from the page host before the gate has supplied it; Steam has one.
    registerSteamPageRenderer(definition.template, (hostReact, page) => {
        react ??= hostReact;
        return react.createElement(SteamPageFrame, {page});
    });
    registerGate(definition.gate, gate);
    return context;
}

/** Renderer, component requirements and resource ownership for a registered host page. */
type SteamPageDefinition = {
    /** The renderer template the page host draws the page with. */
    template: string;
    /** The name the gate registers under. */
    gate: string;
    /** The bridge identity the page's state is published under. */
    patchId: string;
    /** Resolves the components the page draws with, from Steam's own; null when they are not there. */
    components: (runtime: any) => any;
    /** The components the page cannot draw without; the probe and the install both require them. */
    required: readonly string[];
    /** Acquires page resources; returns a refusal reason or null on success. */
    prepare?: (ui: any, runtime: any) => string | null;
    /** Releases what `prepare` resolved. Optional. */
    release?: () => void;
    /** Extra facts for the gate's status. Optional. */
    status?: () => Record<string, unknown>;
    /** The page itself: a component declared once, drawn inside the frame once the gate holds. */
    Page: (props: any) => any;
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
};
