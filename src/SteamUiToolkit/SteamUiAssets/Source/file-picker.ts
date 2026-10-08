// Controller-accessible file selection; the host owns filesystem enumeration.
const SteamFilePickerPatchId = "steam-ui.file-picker";

/**
 * Opens a contained two-pane file picker using native Steam controls.
 * @param ui Steam's resolved React, buttons, focus and modal components.
 * @param options Title, folder/file mode, extension filters and optional starting path.
 * @returns The accepted path or null; cancellation and late listings cannot settle it twice.
 */
const showSteamFilePicker = (ui, options: any = {}) =>
    new Promise<string | null>((resolve) => {
        const react = ui?.react;
        const mode = options.mode === "file" ? "file" : "folder";
        const extensions: string[] = Array.isArray(options.extensions) ? options.extensions : [];
        const title = options.title ?? (mode === "folder" ? "Choose a folder" : "Choose a file");
        let settled = false;
        const settle = (value: string | null) => {
            if (!settled) {
                settled = true;
                resolve(value);
            }
        };

        function Picker({close}: {close: () => void}) {
            const h = react.createElement;
            const [places, setPlaces] = react.useState([] as any[]);
            const [listing, setListing] = react.useState(null as any);
            const [error, setError] = react.useState("");
            const [loading, setLoading] = react.useState(false);
            const requested = react.useRef(0);
            const finish = (path: string | null) => {
                if (settled) return;
                settle(path);
                close();
            };
            const open = (path: string) => {
                const ticket = ++requested.current;
                setLoading(true);
                setError("");
                void request(SteamFilePickerPatchId, "listFolder", {
                    path, extensions: mode === "folder" ? [] : extensions.length ? extensions : [".*"]
                }).then((answer: any) => {
                    if (ticket !== requested.current) return;
                    setLoading(false);
                    if (answer) {
                        setListing(answer);
                        setError(answer.error ?? "");
                    }
                }, (failure: any) => {
                    if (ticket !== requested.current) return;
                    setLoading(false);
                    setError(String(failure?.message ?? failure));
                });
            };
            react.useEffect(() => {
                void request(SteamFilePickerPatchId, "listPlaces", {}).then((answer: any) => {
                    if (requested.current < 0) return;
                    const found = answer?.places ?? [];
                    setPlaces(found);
                    const first = options.start || found[0]?.path;
                    if (first && requested.current === 0) open(first);
                }, (failure: any) => {
                    if (requested.current >= 0) setError(String(failure?.message ?? failure));
                });
                return () => {
                    requested.current = -1;
                    settle(null);
                };
            }, []);
            const current = listing?.path ?? "";
            const up = () => listing?.parent && open(listing.parent);
            const useCurrent = () => {
                if (mode === "folder" && current && !listing?.error && !loading) finish(current);
            };
            const entries = listing?.entries ?? [];
            return h(ui.focusable, {
                className:"steam-ui-kit-picker", "flow-children":"column",
                onCancelButton:()=>finish(null),
                onSecondaryButton:useCurrent,
                onSecondaryActionDescription:mode === "folder" ? "Use this folder" : undefined,
                onOptionsButton:up, onOptionsActionDescription:"Up one level"
            },
                steamUiKitStyle(react),
                h(ui.focusable, {className:"steam-ui-kit-picker-actions", "flow-children":"row"},
                    h(ui.dialogButton, {disabled:!listing?.parent || loading,onClick:up}, "Up"),
                    h("div", {className:"steam-ui-kit-picker-path",role:"status"},
                        loading ? (current || "Places") + " (loading)" : current)),
                h("div", {className:"steam-ui-kit-picker-body"},
                    h(ui.focusable, {className:"steam-ui-kit-picker-places", "flow-children":"column"},
                        h("div", {className:"steam-ui-kit-picker-heading"}, "Places"),
                        ...places.map((place: any)=>renderSteamUiSelectRow(ui,{
                            key:place.path,title:place.name,detail:place.detail,selected:current === place.path,
                            onClick:()=>open(place.path)
                        }))),
                    h(ui.focusable, {className:"steam-ui-kit-picker-files","flow-children":"column"},
                        h("div", {className:"steam-ui-kit-picker-heading"}, "Files and folders"),
                        ...entries.map((entry: any)=>renderSteamUiSelectRow(ui,{
                            key:entry.path,title:entry.name,
                            status:h("span", {className:"steam-ui-kit-muted"},entry.folder ? "Folder" : "File"),
                            onClick:()=>entry.folder ? open(entry.path) : finish(entry.path)
                        })),
                        entries.length === 0 && !loading && !error ? h("p", {}, "This folder is empty.") : null)),
                error ? h("p", {className:"steam-ui-kit-sheet-error",role:"alert"},error) : null,
                h(ui.focusable, {className:"steam-ui-kit-picker-actions","flow-children":"row"},
                    h(ui.dialogButton, {onClick:()=>finish(null)}, "Cancel"),
                    mode === "folder" ? h(ui.dialogButtonPrimary ?? ui.dialogButton, {
                        onClick:useCurrent,disabled:!current || !!listing?.error || loading
                    }, "Use this folder") : null));
        }
        if (!showSteamModal(ui, {title,render:(close)=>react.createElement(Picker,{close}),onCancel:()=>settle(null)}))
            settle(null);
    });
