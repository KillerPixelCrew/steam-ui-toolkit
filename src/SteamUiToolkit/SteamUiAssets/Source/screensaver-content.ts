// Trusted chooser/configuration chrome only. Pack HTML/JS never enters this Steam execution context.
function createScreensaverContent(readSettings: () => any) {
    const patchId = "steam-ui.screensaver";
    const keys = {
        marker: "__steamUiScreensaverSources",
        original: "__steamUiScreensaverSourcesOriginal",
    };
    const local = createLocalStore();
    const catalog = createLocalStore();
    let state: any = null;
    let native: any = null;
    let setSetting: any = null;
    let ui: any = null;
    let installed = false;
    let retiring = false;
    let active = false;
    let fallback = "";
    let catalogKey = "";
    let lastPlayback = "";
    let lastError = "";
    let lastSelection = 0;
    let lastDismiss: number | null = null;
    let pendingSelection: any = null;
    let generation = 0;

    const act = (action, payload = {}) => request(patchId, "content", { action, ...payload });
    const selected = () => String(readSettings()?.screensaver_current_id || "steam-gameslideshow");
    const currentSource = () => state?.sources?.find((source) => source.selectorId === selected());
    const reportPlayback = () => {
        if (!installed || retiring || !state) return;
        const source = currentSource();
        const reading = { active, id: source?.id ?? "", selectorId: selected() };
        const signature = JSON.stringify(reading);
        if (signature === lastPlayback) return;
        lastPlayback = signature;
        void act("playback", reading).catch((error) => {
            if (lastPlayback === signature) lastPlayback = "";
            lastError = String(error);
            local.changed();
        });
    };

    const finishSelection = (operation, error: string | null) => {
        if (pendingSelection !== operation) return;
        pendingSelection = null;
        void act("selectionResult", { revision: operation.revision, error }).catch((failure) => {
            lastError = String(failure);
            local.changed();
        });
    };
    const reconcileSelection = () => {
        if (!installed || retiring || !pendingSelection) return;
        const operation = pendingSelection;
        if (selected() === operation.selectorId) {
            if (operation.preview) {
                void Promise.resolve(native.ForceScreensaver({ enabled: true })).catch((error) => {
                    lastError = String(error);
                    local.changed();
                });
            }
            finishSelection(operation, null);
        } else if (performance.now() >= operation.deadline) {
            finishSelection(
                operation,
                "Steam did not confirm the selection. Check the current choice before trying again.",
            );
        }
    };

    const normalize = (value) => {
        if (!value || !Array.isArray(value.sources) || value.sources.length > 32) return null;
        const ids = new Set();
        const selectors = new Set();
        for (const source of value.sources) {
            if (
                !source ||
                typeof source.id !== "string" ||
                !source.id ||
                source.id.length > 64 ||
                ids.has(source.id) ||
                typeof source.selectorId !== "string" ||
                !source.selectorId.startsWith("uioverride-") ||
                source.selectorId.length > 2048 ||
                selectors.has(source.selectorId) ||
                typeof source.name !== "string" ||
                !source.name ||
                source.name.length > 128 ||
                typeof source.folder !== "string" ||
                source.folder.length > 4096 ||
                !Number.isInteger(source.intervalSeconds) ||
                source.intervalSeconds < 2 ||
                source.intervalSeconds > 3600 ||
                !Number.isInteger(source.framesPerSecond) ||
                source.framesPerSecond < 1 ||
                source.framesPerSecond > 60 ||
                typeof source.shuffle !== "boolean" ||
                typeof source.fill !== "boolean" ||
                typeof source.available !== "boolean" ||
                typeof source.detail !== "string"
            )
                return null;
            ids.add(source.id);
            selectors.add(source.selectorId);
        }
        return value;
    };

    const update = (value) => {
        const next = value === null || value === undefined ? null : normalize(value);
        if (value && !next) {
            lastError = "The screensaver source state was rejected.";
            return;
        }
        state = next;
        const key = JSON.stringify(
            next?.sources?.map((source) => [source.id, source.selectorId]) ?? [],
        );
        if (key !== catalogKey) {
            catalogKey = key;
            catalog.changed();
        }
        if (next && installed) {
            if (lastDismiss === null) lastDismiss = next.dismissRevision;
            else if (
                Number.isSafeInteger(next.dismissRevision) &&
                next.dismissRevision > lastDismiss
            ) {
                lastDismiss = next.dismissRevision;
                void Promise.resolve(native.ForceScreensaver({ enabled: false })).catch((error) => {
                    lastError = String(error);
                });
            }
            if (
                typeof next.selectionRequest === "string" &&
                Number.isSafeInteger(next.selectionRevision) &&
                next.selectionRevision > lastSelection
            ) {
                lastSelection = next.selectionRevision;
                const selectorId = next.selectionRequest;
                const allowed =
                    ["steam-gameslideshow", "steam-bouncinglogo"].includes(selectorId) ||
                    next.sources.some((source) => source.selectorId === selectorId);
                const operation = {
                    revision: next.selectionRevision,
                    selectorId,
                    preview: next.previewSelection === true,
                    deadline: performance.now() + 5000,
                };
                pendingSelection = operation;
                if (!allowed)
                    finishSelection(operation, "The requested screensaver is unavailable.");
                else {
                    try {
                        void Promise.resolve(
                            setSetting("screensaver_current_id", selectorId),
                        ).catch((error) => {
                            finishSelection(operation, String(error).slice(0, 512));
                        });
                    } catch (error) {
                        finishSelection(operation, String(error).slice(0, 512));
                    }
                }
            }
            reconcileSelection();
            reportPlayback();
        }
        local.changed();
    };

    function Controls() {
        const react = ui.react;
        const h = react.createElement;
        react.useSyncExternalStore(local.subscribe, local.revision);
        const [name, setName] = react.useState("");
        const [editing, setEditing] = react.useState("");
        const [busy, setBusy] = react.useState(false);
        if (!installed || !state) return null;
        const row = (props, change = (_value) => {}, action = () => {}) =>
            renderSteamSettingRow(ui, props, undefined, (_row, value) => change(value), action);
        const perform = async (action, payload = {}) => {
            setBusy(true);
            try {
                await act(action, payload);
                lastError = "";
            } catch (error) {
                lastError = String(error);
            } finally {
                setBusy(false);
                local.changed();
            }
        };
        const add = async (pack) => {
            const current = generation;
            const chosen = await runSteamFilePicker(ui, {
                mode: "folder",
                title: pack ? "Choose screensaver pack" : "Choose image folder",
            });
            if (!installed || current !== generation || chosen.outcome !== "selected") return;
            const label =
                name.trim() ||
                chosen.path
                    .replace(/[\\/]+$/u, "")
                    .split(/[\\/]/u)
                    .pop() ||
                "Screensaver";
            await perform(pack ? "addPack" : "addImages", { name: label, folder: chosen.path });
        };
        const source =
            state.sources.find((item) => item.id === editing) ??
            state.sources.find((item) => item.id === state.selectedId) ??
            state.sources[0];
        const configure = (option, value) =>
            void perform("configure", { id: source.id, option, value });
        const controls: any[] = [
            row({
                key: "sources-policy",
                kind: "note",
                label: "Custom sources",
                text: state.error || lastError || state.status,
            }),
            row(
                {
                    key: "source-name",
                    kind: "text",
                    label: "New source name",
                    text: name,
                    maximumLength: 128,
                    description: "Optional; the folder name is used when this is empty.",
                    disabled: busy,
                },
                (value) => setName(String(value)),
            ),
            row(
                {
                    key: "add-images",
                    kind: "action",
                    label: "Image folder",
                    description: "PNG, JPEG, WebP, GIF and BMP; files are read in place.",
                    buttonLabel: "Add folder",
                    disabled: busy,
                },
                undefined,
                () => void add(false),
            ),
            row(
                {
                    key: "add-pack",
                    kind: "action",
                    label: "Local scene or image pack",
                    description:
                        "A folder containing screensaver.json. Scenes run in an isolated browser without system access.",
                    buttonLabel: "Add pack",
                    disabled: busy,
                },
                undefined,
                () => void add(true),
            ),
            row(
                {
                    key: "refresh-sources",
                    kind: "action",
                    label: "Refresh sources",
                    description: "Reload changed files and retry unavailable or repaired sources.",
                    buttonLabel: "Refresh",
                    disabled: busy,
                },
                undefined,
                () => void perform("refresh"),
            ),
        ];
        if (source) {
            controls.push(
                row(
                    {
                        key: "edit-source",
                        kind: "choice",
                        label: "Source options",
                        text: source.id,
                        choices: state.sources.map((item) => ({
                            value: item.id,
                            label: item.name,
                        })),
                        disabled: busy,
                    },
                    (value) => setEditing(String(value)),
                ),
                row({
                    key: "source-detail",
                    kind: "note",
                    label: source.name,
                    text: source.detail + " · " + source.folder,
                }),
                row(
                    {
                        key: "use-source",
                        kind: "action",
                        label: "Use " + source.name,
                        buttonLabel: "Select",
                        disabled: busy,
                    },
                    undefined,
                    () => void perform("select", { id: source.id }),
                ),
                row(
                    {
                        key: "preview-source",
                        kind: "action",
                        label: "Preview " + source.name,
                        buttonLabel: "Preview",
                        disabled: busy || !source.available,
                    },
                    undefined,
                    () => void perform("preview", { id: source.id }),
                ),
            );
            if (source.type !== "scene") {
                controls.push(
                    row(
                        {
                            key: "shuffle-source",
                            kind: "boolean",
                            label: "Shuffle images",
                            checked: source.shuffle,
                            disabled: busy,
                        },
                        (value) => configure("shuffle", !!value),
                    ),
                    row(
                        {
                            key: "fill-source",
                            kind: "boolean",
                            label: "Fill display",
                            description: "Crop to fill; off fits the whole image.",
                            checked: source.fill,
                            disabled: busy,
                        },
                        (value) => configure("fill", !!value),
                    ),
                    row(
                        {
                            key: "interval-source",
                            kind: "choice",
                            label: "Image interval",
                            text: String(source.intervalSeconds),
                            disabled: busy,
                            choices: [
                                ...new Set([
                                    2,
                                    5,
                                    10,
                                    15,
                                    30,
                                    60,
                                    120,
                                    300,
                                    900,
                                    3600,
                                    source.intervalSeconds,
                                ]),
                            ]
                                .sort((a, b) => a - b)
                                .map((value) => ({
                                    value: String(value),
                                    label:
                                        value < 60 ? `${value} seconds` : `${value / 60} minutes`,
                                })),
                        },
                        (value) => configure("intervalSeconds", Number(value)),
                    ),
                );
            } else {
                controls.push(
                    row(
                        {
                            key: "scene-rate",
                            kind: "choice",
                            label: "Scene animation rate",
                            text: String(source.framesPerSecond),
                            disabled: busy,
                            description:
                                "Limits animation callbacks to reduce idle rendering work.",
                            choices: [...new Set([10, 15, 24, 30, 60, source.framesPerSecond])]
                                .sort((a, b) => a - b)
                                .map((value) => ({ value: String(value), label: `${value} fps` })),
                        },
                        (value) => configure("framesPerSecond", Number(value)),
                    ),
                );
            }
            controls.push(
                row(
                    {
                        key: "remove-source",
                        kind: "action",
                        label: "Remove source",
                        description: "Remove it from the chooser; keep its files.",
                        buttonLabel: "Remove",
                        disabled: busy,
                    },
                    undefined,
                    () => void perform("remove", { id: source.id }),
                ),
            );
        }
        return h(react.Fragment, null, ...controls);
    }

    const start = (runtime) => {
        if (installed) return { ok: true };
        try {
            native = runtime.exported(
                ["Screensaver.GetActiveState#1", "Screensaver.GetLocalScreensavers#1"],
                (value) =>
                    typeof value?.GetLocalScreensavers === "function" &&
                    typeof value?.ForceScreensaver === "function",
            );
            setSetting = runtime.exported(
                [...SettingsTokens],
                (value) =>
                    typeof value === "function" &&
                    String(value).includes("SteamClient.Settings.SetSetting") &&
                    String(value).includes("serializeBinaryToWriter"),
            );
            ui = resolveSteamSettingsComponents(runtime);
            const claim = claimMember(
                native,
                "GetLocalScreensavers",
                keys,
                (original: any) =>
                    function (this: unknown, ...args) {
                        return Promise.resolve(original.apply(this, args)).then((response) => {
                            const body = response.Body();
                            const stock = body.screensavers();
                            if (!Array.isArray(stock)) return response;
                            fallback =
                                stock.find((item) => item.id() === "steam-bouncinglogo")?.path() ??
                                "";
                            if (!/^[a-z0-9.-]+\.steamscreensavers\.host$/iu.test(fallback))
                                return response;
                            const ids = new Set(stock.map((item) => item.id()));
                            const additions = (state?.sources ?? [])
                                .filter((source) => !ids.has(source.selectorId))
                                .map((source) => ({
                                    id: () => source.selectorId,
                                    path: () => fallback,
                                }));
                            const projected = new Proxy(body, {
                                get: (target, member) => {
                                    if (member === "screensavers")
                                        return () => [...stock, ...additions];
                                    const value = Reflect.get(target, member, target);
                                    return typeof value === "function" ? value.bind(target) : value;
                                },
                            });
                            return new Proxy(response, {
                                get: (target, member) => {
                                    if (member === "Body") return () => projected;
                                    const value = Reflect.get(target, member, target);
                                    return typeof value === "function" ? value.bind(target) : value;
                                },
                            });
                        });
                    },
            );
            if (!claim.ok) throw new Error(claim.error);
            installed = true;
            retiring = false;
            generation++;
            lastError = "";
            return { ok: true };
        } catch (error) {
            lastError = String(error);
            releaseMember(native, "GetLocalScreensavers", keys);
            return { ok: false, error: lastError };
        }
    };
    const stop = () => {
        retiring = true;
        generation++;
        pendingSelection = null;
        if (installed && state)
            void act("playback", { active: false, id: "", selectorId: selected() }).catch(() => {});
        const result = releaseMember(native, "GetLocalScreensavers", keys);
        if (!result.ok) return result;
        installed = false;
        state = null;
        lastPlayback = "";
        lastSelection = 0;
        lastDismiss = null;
        fallback = "";
        local.changed();
        catalog.changed();
        return { ok: true };
    };
    return {
        start,
        update,
        stop,
        catalog,
        active: (value) => {
            active = value;
            reportPlayback();
        },
        render: () =>
            installed && state && ui?.react && ui?.textField && ui?.dialogButton && ui?.showModal
                ? ui.react.createElement(Controls, { key: "steam-ui-screensaver-content" })
                : null,
        status: () => ({
            installed,
            sources: state?.sources?.length ?? 0,
            fallback,
            lastError,
            controls: !!ui?.textField,
        }),
    };
}
