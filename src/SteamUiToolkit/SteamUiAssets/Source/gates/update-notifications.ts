// Native notification store/renderer contracts mapped from Stable and Beta, 2026-11-01.
// Literal ids are admitted only after the exact source fingerprint resolves to that id.
function createUpdateNotifications() {
    const patchId = "steam-ui.update-notifications";
    const storeTokens = ["m_nNextTestNotificationID", "ProcessNotification(", "RemoveGroupFromTray("];
    const rendererTokens = ['controller:"notification",method:', "Missing activate function"];
    let handles: any = null;
    const owned = new Map<string, any>();

    const NativeNotice = (props) => {
        const notification = props.group.notifications[0];
        const notice = notification.__steamUiUpdate;
        const { react, template, classes } = handles;
        const activate = () => {
            const history = (window as any).tempNavStore?.m_history;
            if (typeof history?.push === "function") history.push(notice.route);
            props.onDismiss?.();
            handles.store.RemoveGroupFromTray(props.group);
        };
        return react.createElement(template, {
            location: props.location, onActivate: activate, onDismiss: props.onDismiss,
            bNewIndicator: notification.bNewIndicator,
            children: [
                react.createElement("div", { className: classes.Header, key: "header" },
                    react.createElement("div", { className: classes.Title }, notice.title)),
                react.createElement("div", { className: classes.Body, key: "body" }, notice.body),
            ],
        });
    };

    const transform = (_create, type, props) => {
        if (!handles || type !== handles.renderer || !props?.group?.notifications?.[0]?.__steamUiUpdate)
            return undefined;
        return handles.react.createElement(NativeNotice, props);
    };

    const show = (notice) => {
        if (!handles || !gate.status().held || typeof notice?.id !== "string" || !notice.id
            || typeof notice.title !== "string" || notice.title.length > 160
            || typeof notice.body !== "string" || notice.body.length > 800
            || typeof notice.route !== "string" || !notice.route.startsWith("/")
            || /[\r\n\0]/u.test(notice.route)) return false;
        if (owned.has(notice.id)) return true;
        const store = handles.store;
        const notification = {
            notificationID: store.m_nNextTestNotificationID++, rtCreated: Math.floor(Date.now() / 1000),
            eType: 7, eSource: 1, bNewIndicator: true, nToastDurationMS: 5000,
            data: { type: () => 1 }, __steamUiUpdate: { ...notice },
        };
        const group = { eType: notification.eType, notifications: [notification] };
        try {
            store.ProcessNotification({
                showToast: true, sound: 6, eFeature: 0, toastDurationMS: 5000,
                fnTray: (_notification, tray) => tray.unshift(group),
            }, notification, 0);
            owned.set(notice.id, group);
            return store.m_rgNotificationTray.includes(group);
        } catch { return false; }
    };

    const gate = defineGate({
        name: "update-notifications",
        find: (runtime) => {
            const storeFactory = runtime.findUnique(storeTokens);
            const rendererFactory = runtime.findUnique(rendererTokens);
            const facts = { store: storeFactory?.[0], renderer: rendererFactory?.[0] };
            if (String(storeFactory?.[0]) !== "7474")
                return gateIncompatible("The native notification store contract changed", facts);
            const storeModule = runtime("7474");
            let rendererModule;
            if (String(rendererFactory?.[0]) === "25666") rendererModule = runtime("25666");
            else if (String(rendererFactory?.[0]) === "3285") rendererModule = runtime("3285");
            else return gateIncompatible("The native notification renderer contract changed", facts);
            const store: any = Object.values(storeModule).find((value: any) =>
                typeof value?.ProcessNotification === "function" && typeof value?.RemoveGroupFromTray === "function"
                && Array.isArray(value?.m_rgNotificationTray) && Number.isSafeInteger(value?.m_nNextTestNotificationID));
            const renderer = Object.values(rendererModule).find((value) =>
                typeof value === "function" && String(value).includes('controller:"notification",method:'));
            const template = Object.values(rendererModule).find((value) =>
                typeof value === "function" && String(value).includes("Missing activate function"));
            const classesFactory = runtime.findUnique(["StandardTemplateContainer", "ShortTemplate", "TwoLine"]);
            if (String(classesFactory?.[0]) !== "21728")
                return gateIncompatible("The native notification style contract changed", facts);
            const classes = runtime("21728");
            const react = resolveReact(runtime);
            const jsx = runtime.resolve([...JsxRuntimeTokens]);
            if (!store || !renderer || !template || !classes?.Title || !classes?.Body || !react || !jsx)
                return gateIncompatible("Native notification exports are unavailable", facts);
            return gateReady({ store, renderer, template, classes, react, jsx }, facts);
        },
        bind: (found, ledger) => {
            handles = found;
            const claim = ledger.elements(found.jsx, patchId, transform);
            if (!claim.ok) return claim.error;
            ledger.cleanup("owned notifications", () => {
                for (const group of owned.values()) found.store.RemoveGroupFromTray(group);
                for (const field of ["m_rgNotificationToasts", "m_rgPendingToasts"]) {
                    const queue = found.store[field];
                    if (!Array.isArray(queue)) continue;
                    for (let i = queue.length - 1; i >= 0; --i)
                        if (queue[i]?.__steamUiUpdate) queue.splice(i, 1);
                }
                found.store.DispatchNextToast?.();
                owned.clear();
            });
        },
        unbind: () => { handles = null; },
        api: { show },
    });
    return gate;
}
registerGate("updateNotifications", createUpdateNotifications());
