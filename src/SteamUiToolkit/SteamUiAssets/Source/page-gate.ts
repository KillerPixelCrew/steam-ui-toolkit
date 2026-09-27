// A host's own page inside Steam: its gate, its state and its frame, declared once.
//
// Every page a host draws needs the same lifecycle: resolve Steam's components, refuse to install
// when one it draws is missing, subscribe to the host's state for the page, tell a mounted page
// when that state changes, arrives refused or goes away, and draw nothing of its own until all of
// that holds. Written out per page it was written three times and had already drifted: one page
// kept drawing its last state after its gate was removed.
//
// A page is declared with `registerSteamPage`, which registers the gate under `gate` and the
// renderer under `template` (see pages.ts), and hands back what the page reads while it renders.
// The page component is the host's; the frame around it is this file's, so a page that could not
// resolve says why instead of showing "Loading…" for ever.

type SteamPageDefinition = {
    // The renderer template the page host draws the page with.
    template: string;
    // The name the gate registers under.
    gate: string;
    // The bridge identity the page's state is published under.
    patchId: string;
    // Resolves the components the page draws with, from Steam's own; null when they are not there.
    components: (runtime: any) => any;
    // The components the page cannot draw without.
    required: readonly string[];
    // Resolves anything else the page needs once the components are in, answering why not when it
    // cannot. Optional.
    prepare?: (ui: any, runtime: any) => string | null;
    // Releases what `prepare` resolved. Optional.
    release?: () => void;
    // Extra facts for the gate's status. Optional.
    status?: () => Record<string, unknown>;
    // The page itself: a component declared once, drawn inside the frame once the gate holds.
    Page: (props: any) => any;
};

type SteamPageContext = {
    // Steam's React, from the components or, before they resolve, from the page host.
    react: () => any;
    // The resolved components, or null while the gate is not installed.
    ui: () => any;
    // The latest state the host published for the page, or null.
    state: () => any;
    // Why the host's latest state could not be delivered, or null.
    refusal: () => string | null;
};

function registerSteamPage(definition: SteamPageDefinition): SteamPageContext {
    let installed = false;
    let ui: any = null;
    let react: any = null;
    let state: any = null;
    let refusal: string | null = null;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;
    let unsubscribeRefusal: (() => void) | null = null;
    const listeners = new Set<() => void>();
    const notify = () => {
        for (const listener of [...listeners]) {
            try {
                listener();
            } catch {
            }
        }
    };

    const context: SteamPageContext = {
        react: () => react,
        ui: () => ui,
        state: () => state,
        refusal: () => refusal,
    };

    const resolve = () => {
        const runtime = getWebpackRuntime(definition.template);
        const resolved = definition.components(runtime);
        const missing = definition.required.filter((name) => !resolved?.[name]);
        if (missing.length) {
            lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
            return false;
        }
        const refused = definition.prepare?.(resolved, runtime) ?? null;
        if (refused) {
            lastError = refused;
            return false;
        }
        ui = resolved;
        react = resolved.react;
        return true;
    };

    const install = () => {
        if (installed) return {ok: true, alreadyInstalled: true};
        if (!attemptResolution(resolve, (error) => (lastError = String(error)))) {
            ui = null;
            notify();
            return {ok: false, error: lastError};
        }
        installed = true;
        lastError = "";
        unsubscribe = subscribe(definition.patchId, (next) => {
            state = next;
            notify();
        });
        unsubscribeRefusal = subscribeRefusal(definition.patchId, (reason) => {
            refusal = reason;
            notify();
        });
        notify();
        return {ok: true, installed: true};
    };

    // A mounted page draws nothing from now on, rather than the controls it last had.
    const remove = () => {
        installed = false;
        unsubscribe = endSubscription(unsubscribe);
        unsubscribeRefusal = endSubscription(unsubscribeRefusal);
        state = null;
        refusal = null;
        ui = null;
        definition.release?.();
        notify();
        return {ok: true, removed: true};
    };

    const status = () => ({
        ok: true,
        installed,
        resolved: !!ui,
        subscribed: !!unsubscribe,
        refused: refusal,
        lastError,
        ...(definition.status?.() ?? {}),
    });

    // One component for the life of the asset. The page host draws it on every router render, and a
    // component declared inside the renderer would be a new type each time: React would remount the
    // page and drop its state and the controller's focus.
    function SteamPageFrame(props: any) {
        const [, setRevision] = react.useState(0);
        react.useEffect(() => {
            const listener = () => setRevision((value: number) => value + 1);
            listeners.add(listener);
            return () => listeners.delete(listener);
        }, []);
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
                lastError || "Loading…",
            );
        }
        return react.createElement(definition.Page, {context, page: props.page});
    }

    // React comes from the page host before the gate has supplied it; Steam has one.
    registerSteamPageRenderer(definition.template, (hostReact, page) => {
        react ??= hostReact;
        return react.createElement(SteamPageFrame, {page});
    });
    registerGate(definition.gate, {install, remove, status});
    return context;
}
