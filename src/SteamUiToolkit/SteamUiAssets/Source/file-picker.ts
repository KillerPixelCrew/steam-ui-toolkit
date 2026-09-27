// A folder and file picker for pages drawn inside Steam.
//
// Steam has no picker a page can open, and a Windows dialog opens behind Big Picture with no
// controller support. This draws one as a Steam modal from Steam's own components: its dialog
// frame, its focusable rows and its buttons. The host lists the file system through the
// steam-ui.file-picker commands (SteamFilePickerSurface on the C# side); nothing here reads the
// disk, and the page decides what to do with the path the user chose.
//
// Controller: A opens a folder or chooses a file, X uses the current folder, Y goes up a level,
// B cancels.

const SteamFilePickerPatchId = "steam-ui.file-picker";

const filePickerRequest = (command: string, payload: any = {}) =>
    request(SteamFilePickerPatchId, command, payload, nextActionGeneration(SteamFilePickerPatchId));

// Opens the picker. Resolves with the chosen path, or null when the user cancelled.
//
// ui       resolved Steam components: react, focusable, dialogButton, dialogButtonPrimary,
//          modalRoot, showModal
// options  { title, mode: "folder" | "file", extensions: [".lnk", ...], start: "D:\\Games" }
const showSteamFilePicker = (ui, options: any = {}) =>
    new Promise<string | null>((resolve) => {
        if (!ui?.showModal || !ui?.modalRoot) {
            resolve(null);
            return;
        }
        const react = ui.react;
        const mode = options.mode === "file" ? "file" : "folder";
        const extensions: string[] = Array.isArray(options.extensions) ? options.extensions : [];
        const title = options.title ?? (mode === "folder" ? "Choose a folder" : "Choose a file");
        let settled = false;
        const finish = (value: string | null, close: () => void) => {
            if (!settled) {
                settled = true;
                resolve(value);
            }
            close();
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

        function Picker(props: any) {
            const close = props.closeModal ?? (() => {});
            const [places, setPlaces] = react.useState([] as any[]);
            const [listing, setListing] = react.useState(null as any);
            const [error, setError] = react.useState("");
            const [loading, setLoading] = react.useState(false);

            const open = (path: string) => {
                setLoading(true);
                void filePickerRequest("listFolder", {
                    path,
                    extensions: mode === "file" ? extensions : [],
                }).then(
                    (answer: any) => {
                        setLoading(false);
                        if (!answer) return;
                        setListing(answer);
                        setError(answer.error ?? "");
                    },
                    (failure: any) => {
                        setLoading(false);
                        setError(String(failure?.message ?? failure ?? "The folder could not be listed."));
                    },
                );
            };

            react.useEffect(() => {
                void filePickerRequest("listPlaces").then(
                    (answer: any) => {
                        const found = answer?.places ?? [];
                        setPlaces(found);
                        const first = options.start || found[0]?.path;
                        if (first) open(first);
                    },
                    (failure: any) =>
                        setError(String(failure?.message ?? failure ?? "The drives could not be listed.")),
                );
            }, []);

            const current = listing?.path ?? "";
            const up = () => {
                if (listing?.parent) open(listing.parent);
            };
            const useCurrent = () => {
                if (mode === "folder" && current && !listing?.error) finish(current, close);
            };

            const placeRow = (place: any) =>
                react.createElement(
                    ui.dialogButton,
                    {
                        key: place.path,
                        style: rowStyle,
                        onClick: () => open(place.path),
                        onActivate: () => open(place.path),
                    },
                    react.createElement("span", {}, place.name),
                    place.detail
                        ? react.createElement("span", { style: { fontSize: "12px", opacity: 0.7 } }, place.detail)
                        : null,
                );

            const entryRow = (entry: any) => {
                const activate = () => (entry.folder ? open(entry.path) : finish(entry.path, close));
                return react.createElement(
                    ui.dialogButton,
                    {
                        key: entry.path,
                        style: { ...rowStyle, opacity: entry.folder || mode === "file" ? 1 : 0.6 },
                        onClick: activate,
                        onActivate: activate,
                    },
                    react.createElement("span", {}, entry.folder ? `${entry.name}\\` : entry.name),
                    react.createElement(
                        "span",
                        { style: { fontSize: "12px", opacity: 0.7 } },
                        entry.folder ? "Folder" : "File",
                    ),
                );
            };

            const entries = listing?.entries ?? [];
            return react.createElement(
                ui.modalRoot,
                { onCancel: () => finish(null, close), closeModal: () => finish(null, close), strTitle: title },
                react.createElement(
                    ui.focusable,
                    {
                        style: { display: "flex", flexDirection: "column", gap: "12px", minWidth: "min(900px, 80vw)" },
                        onCancelButton: () => finish(null, close),
                        onSecondaryButton: useCurrent,
                        onSecondaryActionDescription: mode === "folder" ? "Use this folder" : undefined,
                        onOptionsButton: up,
                        onOptionsActionDescription: "Up one level",
                    },
                    react.createElement(
                        "div",
                        { style: { fontSize: "14px", opacity: 0.8, wordBreak: "break-all" } },
                        loading ? `${current || "…"} (loading)` : current,
                    ),
                    react.createElement(
                        "div",
                        { style: { display: "flex", gap: "16px", minHeight: "320px", maxHeight: "55vh" } },
                        react.createElement(
                            ui.focusable,
                            {
                                "flow-children": "column",
                                style: { width: "34%", overflowY: "auto", display: "flex", flexDirection: "column" },
                            },
                            ...places.map(placeRow),
                        ),
                        react.createElement(
                            ui.focusable,
                            {
                                "flow-children": "column",
                                style: { flex: 1, overflowY: "auto", display: "flex", flexDirection: "column" },
                            },
                            listing?.parent
                                ? react.createElement(
                                      ui.dialogButton,
                                      { key: "..", style: rowStyle, onClick: up, onActivate: up },
                                      react.createElement("span", {}, ".."),
                                      react.createElement("span", { style: { fontSize: "12px", opacity: 0.7 } }, "Up"),
                                  )
                                : null,
                            ...entries.map(entryRow),
                            entries.length === 0 && !loading && !error
                                ? react.createElement("div", { style: { opacity: 0.7, padding: "8px" } }, "This folder is empty.")
                                : null,
                            listing?.truncated
                                ? react.createElement(
                                      "div",
                                      { style: { opacity: 0.7, padding: "8px" } },
                                      "Only the first items are shown.",
                                  )
                                : null,
                        ),
                    ),
                    error ? react.createElement("div", { style: { color: "#ff6d6d", fontSize: "14px" } }, error) : null,
                    react.createElement(
                        ui.focusable,
                        { "flow-children": "row", style: { display: "flex", gap: "8px", justifyContent: "flex-end" } },
                        react.createElement(
                            ui.dialogButton,
                            { onClick: () => finish(null, close), style: { width: "auto" } },
                            "Cancel",
                        ),
                        mode === "folder"
                            ? react.createElement(
                                  ui.dialogButtonPrimary,
                                  {
                                      onClick: useCurrent,
                                      disabled: !current || !!listing?.error,
                                      style: { width: "auto" },
                                  },
                                  current ? `Use ${current}` : "Use this folder",
                              )
                            : null,
                    ),
                ),
            );
        }

        ui.showModal(react.createElement(Picker, {}), window, { strTitle: title });
    });
