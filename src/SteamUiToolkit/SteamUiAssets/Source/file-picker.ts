// Controller-accessible file selection; filesystem listing is delegated to the host bridge.

const SteamFilePickerPatchId = "steam-ui.file-picker";

/**
 * Opens a controller-accessible picker backed by the file-picker bridge commands.
 * @param ui Steam's resolved React and native control components.
 * @param options Optional title, folder/file mode, extension filters and starting path.
 * @returns A promise for the selected path, or null on cancellation or unavailable modal components.
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

        const rowStyle = {
            display: "flex",
            alignItems: "center",
            justifyContent: "space-between",
            gap: "12px",
            width: "100%",
            textAlign: "left",
            margin: "0 0 2px",
        };

        // Declared once per picker, so the modal keeps one component for as long as it is open.
        function Picker({close}: {close: () => void}) {
            const finish = (value: string | null) => {
                settle(value);
                close();
            };
            const [places, setPlaces] = react.useState([] as any[]);
            const [listing, setListing] = react.useState(null as any);
            const [error, setError] = react.useState("");
            const [loading, setLoading] = react.useState(false);
            // The folder asked for last. A listing that answers after a later one was asked for, a
            // slow network drive overtaken by a local folder, is dropped rather than shown, so what
            // "Use this folder" accepts is always the folder on screen. -1 once the picker is gone,
            // so nothing that answers after that is applied.
            const requested = react.useRef(0);

            const open = (path: string) => {
                const ticket = ++requested.current;
                setLoading(true);
                void request(SteamFilePickerPatchId, "listFolder", {
                    path,
                    extensions: mode === "file" ? extensions : [],
                }).then(
                    (answer: any) => {
                        if (ticket !== requested.current) return;
                        setLoading(false);
                        if (!answer) return;
                        setListing(answer);
                        setError(answer.error ?? "");
                    },
                    (failure: any) => {
                        if (ticket !== requested.current) return;
                        setLoading(false);
                        setError(String(failure?.message ?? failure ?? "The folder could not be listed."));
                    },
                );
            };

            react.useEffect(() => {
                void request(SteamFilePickerPatchId, "listPlaces", {}).then(
                    (answer: any) => {
                        if (requested.current < 0) return;
                        const found = answer?.places ?? [];
                        setPlaces(found);
                        // The start folder opens only while nothing else has been asked for.
                        const first = options.start || found[0]?.path;
                        if (first && requested.current === 0) open(first);
                    },
                    (failure: any) => {
                        if (requested.current < 0) return;
                        setError(String(failure?.message ?? failure ?? "The drives could not be listed."));
                    },
                );
                // However the modal goes away, by a choice, Cancel, B, or Steam closing it, the
                // caller hears once: a choice already settled, and anything else is a cancel.
                return () => {
                    requested.current = -1;
                    settle(null);
                };
            }, []);

            const current = listing?.path ?? "";
            const up = () => {
                if (listing?.parent) open(listing.parent);
            };
            const useCurrent = () => {
                if (mode === "folder" && current && !listing?.error && !loading) finish(current);
            };

            // A DialogButton answers both the mouse and the controller's A through onClick; giving it
            // onActivate as well ran each choice twice.
            const placeRow = (place: any) =>
                react.createElement(
                    ui.dialogButton,
                    {key: place.path, style: rowStyle, onClick: () => open(place.path)},
                    react.createElement("span", {}, place.name),
                    place.detail
                        ? react.createElement("span", {style: {fontSize: "12px", opacity: 0.7}}, place.detail)
                        : null,
                );

            const entryRow = (entry: any) =>
                react.createElement(
                    ui.dialogButton,
                    {
                        key: entry.path,
                        style: {...rowStyle, opacity: entry.folder || mode === "file" ? 1 : 0.6},
                        onClick: () => (entry.folder ? open(entry.path) : finish(entry.path)),
                    },
                    react.createElement("span", {}, entry.folder ? `${entry.name}\\` : entry.name),
                    react.createElement(
                        "span",
                        {style: {fontSize: "12px", opacity: 0.7}},
                        entry.folder ? "Folder" : "File",
                    ),
                );

            const entries = listing?.entries ?? [];
            return react.createElement(
                ui.focusable,
                {
                    style: {display: "flex", flexDirection: "column", gap: "12px", minWidth: "min(900px, 80vw)"},
                    onCancelButton: () => finish(null),
                    onSecondaryButton: useCurrent,
                    onSecondaryActionDescription: mode === "folder" ? "Use this folder" : undefined,
                    onOptionsButton: up,
                    onOptionsActionDescription: "Up one level",
                },
                react.createElement(
                    "div",
                    {style: {fontSize: "14px", opacity: 0.8, wordBreak: "break-all"}},
                    loading ? `${current || "…"} (loading)` : current,
                ),
                react.createElement(
                    "div",
                    {style: {display: "flex", gap: "16px", minHeight: "320px", maxHeight: "55vh"}},
                    react.createElement(
                        ui.focusable,
                        {
                            "flow-children": "column",
                            style: {width: "34%", overflowY: "auto", display: "flex", flexDirection: "column"},
                        },
                        ...places.map(placeRow),
                    ),
                    react.createElement(
                        ui.focusable,
                        {
                            "flow-children": "column",
                            style: {flex: 1, overflowY: "auto", display: "flex", flexDirection: "column"},
                        },
                        listing?.parent
                            ? react.createElement(
                                  ui.dialogButton,
                                  {key: "..", style: rowStyle, onClick: up},
                                  react.createElement("span", {}, ".."),
                                  react.createElement("span", {style: {fontSize: "12px", opacity: 0.7}}, "Up"),
                              )
                            : null,
                        ...entries.map(entryRow),
                        entries.length === 0 && !loading && !error
                            ? react.createElement("div", {style: {opacity: 0.7, padding: "8px"}}, "This folder is empty.")
                            : null,
                    ),
                ),
                error ? react.createElement("div", {style: {color: "#ff6d6d", fontSize: "14px"}}, error) : null,
                react.createElement(
                    ui.focusable,
                    {"flow-children": "row", style: {display: "flex", gap: "8px", justifyContent: "flex-end"}},
                    react.createElement(
                        ui.dialogButton,
                        {onClick: () => finish(null), style: {width: "auto"}},
                        "Cancel",
                    ),
                    mode === "folder"
                        ? react.createElement(
                              ui.dialogButtonPrimary ?? ui.dialogButton,
                              {
                                  onClick: useCurrent,
                                  disabled: !current || !!listing?.error || loading,
                                  style: {width: "auto"},
                              },
                              current ? `Use ${current}` : "Use this folder",
                          )
                        : null,
                ),
            );
        }

        const shown = showSteamModal(ui, {
            title,
            render: (close) => react.createElement(Picker, {close}),
            onCancel: () => settle(null),
        });
        if (!shown) settle(null);
    });
