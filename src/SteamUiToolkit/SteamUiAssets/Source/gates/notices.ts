// Native Steam notices: a host-supplied title, body and route drawn with Valve's own notification
// template in the toast and the notification tray, opened by mouse, touch or controller activation.
// The store, renderer and template classes are each found by a unique source fingerprint (mapped
// from Stable and Beta, 2026-11-01); nothing here names a module id.
function createNotices() {
    const patchId = "steam-ui.notices";
    const marker = "__steamUiNotice";
    const StoreTokens = ["m_nNextTestNotificationID", "ProcessNotification(", "RemoveGroupFromTray("];
    const RendererTokens = ['controller:"notification",method:', "Missing activate function"];
    const ClassTokens = ["StandardTemplateContainer", "ShortTemplate", "TwoLine"];
    // Valve's client-sourced notification (eSource k_Client = 1) of the generic type, which goes
    // straight into the toast queue and the tray; a server-sourced one would wait for server data.
    const ClientSource = 1;
    const GenericType = 7;
    const ToastMilliseconds = 5000;
    let handles: any = null;

    const noticeOf = (group) => group?.notifications?.[0]?.[marker] ?? null;
    // Steam's tray is the one record of what is shown: a notice Steam dropped, or one the user
    // dismissed, opened or cleared, is simply not there, so the same id can be shown again.
    const trayGroups = (store, id?) =>
        (Array.isArray(store?.m_rgNotificationTray) ? store.m_rgNotificationTray : []).filter((group) => {
            const notice = noticeOf(group);
            return notice !== null && (id === undefined || notice.id === id);
        });

    const NativeNotice = (props) => {
        if (!handles) return null;
        const notification = props.group.notifications[0];
        const notice = notification[marker];
        const { react, template, classes, store } = handles;
        const activate = () => {
            if (!gate.status().held) return;
            // Over a running game the overlay window that drew the notice navigates itself.
            navigateSteamRoute(notice.route, steamInteractionWindow());
            props.onDismiss?.();
            store.RemoveGroupFromTray(props.group);
        };
        return react.createElement(template, {
            location: props.location,
            onActivate: activate,
            onDismiss: props.onDismiss,
            bNewIndicator: notification.bNewIndicator,
            children: [
                react.createElement(
                    "div",
                    { className: classes.Header, key: "header" },
                    react.createElement("div", { className: classes.Title }, notice.title),
                ),
                react.createElement("div", { className: classes.Body, key: "body" }, notice.body),
            ],
        });
    };

    const transform = (_create, type, props) => {
        if (!handles || type !== handles.renderer || !noticeOf(props?.group)) return undefined;
        return handles.react.createElement(NativeNotice, props);
    };

    const validNotice = (notice) =>
        typeof notice?.id === "string" &&
        notice.id.length > 0 &&
        typeof notice.title === "string" &&
        notice.title.length <= 160 &&
        typeof notice.body === "string" &&
        notice.body.length <= 800 &&
        isNavigableRoute(notice.route);

    // True once Steam holds the notice in its tray, false when it refused it or the gate is off.
    const show = (notice) => {
        if (!handles || !gate.status().held || !validNotice(notice)) return false;
        const store = handles.store;
        if (trayGroups(store, notice.id).length > 0) return true;
        const notification = {
            notificationID: store.m_nNextTestNotificationID++,
            rtCreated: Math.floor(Date.now() / 1000),
            eType: GenericType,
            eSource: ClientSource,
            bNewIndicator: true,
            nToastDurationMS: ToastMilliseconds,
            data: { type: () => 1 },
            [marker]: { id: notice.id, title: notice.title, body: notice.body, route: notice.route },
        };
        const group = { eType: notification.eType, notifications: [notification] };
        try {
            store.ProcessNotification(
                {
                    showToast: true,
                    sound: 6,
                    eFeature: 0,
                    toastDurationMS: ToastMilliseconds,
                    fnTray: (_notification, tray) => tray.unshift(group),
                },
                notification,
                0,
            );
        } catch {
            return false;
        }
        return store.m_rgNotificationTray.includes(group);
    };

    // The store is the module's one export carrying Valve's notification store members.
    const storeOf = (exports) =>
        uniqueSteamExport(
            exports,
            (value) =>
                typeof value?.ProcessNotification === "function" &&
                typeof value?.RemoveGroupFromTray === "function" &&
                Array.isArray(value?.m_rgNotificationTray) &&
                Number.isSafeInteger(value?.m_nNextTestNotificationID),
        );

    // The renderer and the template live in one module; one pass over its exports sorts both.
    const rendererExports = (exports) => {
        const renderers = new Set<any>();
        const templates = new Set<any>();
        for (const value of Object.values(exports ?? {})) {
            if (typeof value !== "function") continue;
            const source = String(value);
            if (source.includes(RendererTokens[0])) renderers.add(value);
            if (source.includes(RendererTokens[1])) templates.add(value);
        }
        return {
            renderer: renderers.size === 1 ? [...renderers][0] : null,
            template: templates.size === 1 ? [...templates][0] : null,
        };
    };

    const gate = defineGate({
        name: "notices",
        find: (runtime) => {
            const storeFactory = runtime.findUnique(StoreTokens);
            const rendererFactory = runtime.findUnique(RendererTokens);
            const classesFactory = runtime.findUnique(ClassTokens);
            const facts = {
                store: storeFactory ? 1 : 0,
                renderer: rendererFactory ? 1 : 0,
                classes: classesFactory ? 1 : 0,
            };
            if (!storeFactory || !rendererFactory || !classesFactory)
                return gateIncompatible("Steam's notification store, renderer or styles are not one match each", facts);
            const store = storeOf(runtime(storeFactory[0]));
            const { renderer, template } = rendererExports(runtime(rendererFactory[0]));
            const classes = runtime(classesFactory[0]);
            const react = resolveReact(runtime);
            const jsx = runtime.resolve([...JsxRuntimeTokens]);
            if (!store || !renderer || !template || !classes?.Title || !classes?.Body || !react || !jsx)
                return gateIncompatible("Steam's notification exports are unavailable", facts);
            return gateReady({ store, renderer, template, classes, react, jsx }, facts);
        },
        bind: (found, ledger) => {
            handles = found;
            const claim = ledger.elements(found.jsx, patchId, transform);
            if (!claim.ok) return claim.error;
            ledger.cleanup("owned notices", () => {
                for (const group of trayGroups(found.store)) found.store.RemoveGroupFromTray(group);
                // A client notice is queued as the notification object itself; the pending queue
                // holds server notices only and is cleared as well in case that ever changes.
                for (const field of ["m_rgNotificationToasts", "m_rgPendingToasts"]) {
                    const queue = found.store[field];
                    if (!Array.isArray(queue)) continue;
                    for (let i = queue.length - 1; i >= 0; --i) if (queue[i]?.[marker]) queue.splice(i, 1);
                }
                found.store.DispatchNextToast?.();
            });
        },
        unbind: () => {
            handles = null;
        },
        api: { show },
    });
    return gate;
}
registerGate("notices", createNotices());
