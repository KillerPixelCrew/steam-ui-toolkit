// Controller-accessible file selection; the host owns directory enumeration.
const SteamFilePickerPatchId = "steam-ui.file-picker";
// How many rows the file list draws at once. Every entry stays reachable: LT/RT and the page buttons
// move through the rest, and the host always answers the whole folder.
const SteamFilePickerPageSize = 200;

/** What a page asks the picker for. */
type SteamFilePickerOptions = {
    /** The modal title; "Choose a file" or "Choose a folder" when absent. */
    title?: string;
    /** `file` chooses a file, `folder` chooses the folder on screen, `any` allows both. Default `folder`. */
    mode?: "file" | "folder" | "any";
    /** File extensions with their dot, such as `.exe`; empty or absent offers every file type. */
    extensions?: string[];
    /** Where to open first; a place the host cannot list falls back to the first drive. */
    start?: string;
    /** Aborting closes the picker and settles it as cancelled. */
    signal?: AbortSignal;
};

/** How a picker settled: once, with exactly one of these. */
type SteamFilePickerResult =
    | { outcome: "selected"; path: string; folder: boolean }
    | { outcome: "cancelled" }
    | { outcome: "failed"; reason: string };

const steamFilePickerFailure = (failure: any) => String(failure?.message ?? failure);
const steamFilePickerCancelled = (failure: any) =>
    /cancelled/u.test(steamFilePickerFailure(failure));

/**
 * Opens a contained two-pane file picker using native Steam controls and reports how it settled.
 * @param ui Steam's resolved React, buttons, focus and modal components.
 * @param options Title, mode, extension filters, starting path and cancellation signal.
 * @returns The validated selection, a cancellation or a failure; it settles exactly once.
 */
const runSteamFilePicker = (ui, options: SteamFilePickerOptions = {}) =>
    new Promise<SteamFilePickerResult>((resolve) => {
        const react = ui?.react;
        const mode = options.mode === "file" || options.mode === "any" ? options.mode : "folder";
        const extensions: string[] =
            mode === "folder"
                ? []
                : Array.isArray(options.extensions) && options.extensions.length
                  ? options.extensions
                  : [".*"];
        const title = options.title ?? (mode === "folder" ? "Choose a folder" : "Choose a file");
        let settled = false;
        const settle = (value: SteamFilePickerResult) => {
            if (!settled) {
                settled = true;
                resolve(value);
            }
        };
        if (options.signal?.aborted) {
            settle({ outcome: "cancelled" });
            return;
        }

        function Picker({ close }: { close: () => void }) {
            const h = react.createElement;
            const [places, setPlaces] = react.useState([] as any[]);
            const [listing, setListing] = react.useState(null as any);
            const [error, setError] = react.useState("");
            const [loading, setLoading] = react.useState(false);
            const [page, setPage] = react.useState(0);
            // The listing on screen is the one asked for last: each request takes a ticket, and an
            // answer whose ticket is stale is dropped. -1 once the picker is gone, so nothing lands.
            const requested = react.useRef(0);
            // The listing in flight, so moving on cancels it at the host instead of letting a slow
            // drive finish work nobody will see.
            const inFlight = react.useRef(null as AbortController | null);
            // The choice being checked: the ticket of the listing it was made from and its cancel. A
            // second press on that listing does nothing. Moving on cancels it at the host and leaves
            // its ticket behind, so a late answer neither returns a path from a folder no longer on
            // screen nor shows its refusal over the folder that replaced it.
            const choosing = react.useRef(
                null as { ticket: number; controller: AbortController } | null,
            );
            const dropChoice = () => {
                choosing.current?.controller.abort();
                choosing.current = null;
            };
            const finish = (result: SteamFilePickerResult) => {
                if (settled) return;
                settle(result);
                close();
            };
            const cancel = () => finish({ outcome: "cancelled" });
            const refreshPlaces = (thenOpen: boolean) => {
                void request(SteamFilePickerPatchId, "listPlaces", {}).then(
                    (answer: any) => {
                        if (requested.current < 0) return;
                        const found: any[] = answer?.places ?? [];
                        setPlaces(found);
                        if (thenOpen && requested.current === 0) {
                            const first = options.start || found[0]?.path;
                            if (first) list(first, found[0]?.path);
                        }
                    },
                    (failure: any) => {
                        if (requested.current >= 0) setError(steamFilePickerFailure(failure));
                    },
                );
            };
            // Lists a folder. `fallback` is where to go instead when this one cannot be listed: the
            // start a page asked for is a preference, not a requirement. `notice` is a message that
            // stays on screen through the listing, such as why a choice was refused.
            const list = (path: string, fallback?: string, notice = "") => {
                const ticket = ++requested.current;
                inFlight.current?.abort();
                dropChoice();
                const controller = new AbortController();
                inFlight.current = controller;
                setLoading(true);
                setError(notice);
                void request(
                    SteamFilePickerPatchId,
                    "listFolder",
                    { path, extensions },
                    undefined,
                    controller.signal,
                ).then(
                    (answer: any) => {
                        if (ticket !== requested.current) return;
                        inFlight.current = null;
                        if (answer?.error && fallback && fallback !== path) {
                            list(fallback);
                            return;
                        }
                        setLoading(false);
                        if (answer) {
                            setListing(answer);
                            setPage(0);
                            setError(answer.error ?? notice);
                            // A folder that went away may have taken its drive with it.
                            if (answer.error) refreshPlaces(false);
                        }
                    },
                    (failure: any) => {
                        if (ticket !== requested.current || steamFilePickerCancelled(failure))
                            return;
                        inFlight.current = null;
                        setLoading(false);
                        setError(steamFilePickerFailure(failure));
                    },
                );
            };
            // Asks the host whether the entry can still be returned: it may be gone, or no longer
            // the kind it was, since the folder was listed. A refusal is shown and the folder is
            // listed again so the screen matches the disk; the picker never answers a stale path.
            // The answer counts only while the listing it was chosen from is still the one on
            // screen: the picker is gone at -1, and any later listing has moved on.
            const choose = (path: string, kind: "file" | "folder" | "any") => {
                const ticket = requested.current;
                if (settled || ticket < 0 || choosing.current?.ticket === ticket) return;
                const controller = new AbortController();
                choosing.current = { ticket, controller };
                const done = () => {
                    if (choosing.current?.ticket === ticket) choosing.current = null;
                    return ticket === requested.current && !settled;
                };
                void request(
                    SteamFilePickerPatchId,
                    "resolve",
                    { path, kind, extensions },
                    undefined,
                    controller.signal,
                ).then(
                    (answer: any) => {
                        if (!done()) return;
                        if (!answer || answer.error) {
                            const reason = answer?.error ?? "That cannot be chosen.";
                            if (listing?.path) list(listing.path, undefined, reason);
                            else setError(reason);
                            return;
                        }
                        finish({ outcome: "selected", path: answer.path, folder: !!answer.folder });
                    },
                    (failure: any) => {
                        if (done() && !steamFilePickerCancelled(failure))
                            setError(steamFilePickerFailure(failure));
                    },
                );
            };
            react.useEffect(() => {
                refreshPlaces(true);
                const signal = options.signal;
                const onAbort = () => cancel();
                signal?.addEventListener("abort", onAbort, { once: true });
                return () => {
                    requested.current = -1;
                    inFlight.current?.abort();
                    inFlight.current = null;
                    dropChoice();
                    signal?.removeEventListener("abort", onAbort);
                    settle({ outcome: "cancelled" });
                };
            }, []);
            const current: string = listing?.path ?? "";
            const up = () => listing?.parent && list(listing.parent);
            const refresh = () => {
                refreshPlaces(false);
                if (current) list(current);
            };
            const useCurrent = () => {
                if (mode === "file") return;
                if (!current || loading) {
                    setError("Wait for the folder to finish loading.");
                    return;
                }
                if (listing?.error) {
                    setError(listing.error);
                    return;
                }
                choose(current, "folder");
            };
            const activate = (entry: any) => {
                if (entry.folder) list(entry.path);
                else choose(entry.path, mode === "any" ? "any" : "file");
            };
            const entries: any[] = listing?.entries ?? [];
            const pages = Math.max(1, Math.ceil(entries.length / SteamFilePickerPageSize));
            const shown = Math.min(page, pages - 1);
            const first = shown * SteamFilePickerPageSize;
            const visible = entries.slice(first, first + SteamFilePickerPageSize);
            const turn = (delta: number) =>
                setPage(Math.min(pages - 1, Math.max(0, shown + delta)));
            const kindOf = (entry: any) => (entry.link ? "Link" : entry.folder ? "Folder" : "File");
            return h(
                ui.focusable,
                {
                    className: "steam-ui-kit-picker",
                    "flow-children": "column",
                    onCancelButton: cancel,
                    onSecondaryButton: mode === "file" ? undefined : useCurrent,
                    onSecondaryActionDescription: mode === "file" ? undefined : "Use this folder",
                    onOptionsButton: up,
                    onOptionsActionDescription: "Up one level",
                    onButtonDown: pages > 1 ? onSteamTriggers(turn) : undefined,
                },
                steamUiKitStyle(react),
                h(
                    ui.focusable,
                    { className: "steam-ui-kit-picker-actions", "flow-children": "row" },
                    h(ui.dialogButton, { onClick: up }, "Up"),
                    h(ui.dialogButton, { onClick: refresh }, "Refresh"),
                    h(
                        "div",
                        { className: "steam-ui-kit-picker-path", role: "status" },
                        loading ? (current || "Places") + " (loading)" : current,
                    ),
                ),
                h(
                    "div",
                    { className: "steam-ui-kit-picker-body" },
                    h(
                        ui.focusable,
                        { className: "steam-ui-kit-picker-places", "flow-children": "column" },
                        h("div", { className: "steam-ui-kit-picker-heading" }, "Places"),
                        ...places.map((place: any) =>
                            renderSteamUiSelectRow(ui, {
                                key: place.path,
                                title: place.name,
                                detail: place.detail,
                                selected: current === place.path,
                                onClick: () => list(place.path),
                            }),
                        ),
                    ),
                    // Keyed by folder and page, so a new listing mounts fresh and takes focus at its
                    // first row, while a refresh of the same folder keeps the row that had it.
                    h(
                        ui.focusable,
                        {
                            key: current + "#" + shown,
                            className: "steam-ui-kit-picker-files",
                            "flow-children": "column",
                            autoFocus: !!current,
                        },
                        h("div", { className: "steam-ui-kit-picker-heading" }, "Files and folders"),
                        ...visible.map((entry: any) =>
                            renderSteamUiSelectRow(ui, {
                                key: entry.path,
                                title: entry.name,
                                status: h(
                                    "span",
                                    { className: "steam-ui-kit-muted" },
                                    kindOf(entry),
                                ),
                                onClick: () => activate(entry),
                            }),
                        ),
                        entries.length === 0 && !loading && !error && current
                            ? h("p", {}, "This folder is empty.")
                            : null,
                    ),
                ),
                pages > 1
                    ? h(
                          ui.focusable,
                          { className: "steam-ui-kit-picker-actions", "flow-children": "row" },
                          h(ui.dialogButton, { onClick: () => turn(-1) }, "Previous page"),
                          h(
                              "div",
                              {
                                  className: "steam-ui-kit-picker-path steam-ui-kit-picker-count",
                                  role: "status",
                              },
                              `Showing ${first + 1}-${first + visible.length} of ${entries.length}`,
                          ),
                          h(ui.dialogButton, { onClick: () => turn(1) }, "Next page"),
                      )
                    : null,
                error
                    ? h("p", { className: "steam-ui-kit-sheet-error", role: "alert" }, error)
                    : null,
                h(
                    ui.focusable,
                    { className: "steam-ui-kit-picker-actions", "flow-children": "row" },
                    h(ui.dialogButton, { onClick: cancel }, "Cancel"),
                    mode === "file"
                        ? null
                        : h(
                              ui.dialogButtonPrimary ?? ui.dialogButton,
                              { onClick: useCurrent },
                              "Use this folder",
                          ),
                ),
            );
        }
        // Steam's dialog folds a body to 600px unless it is allowed its full size; the two panes
        // need the width the stylesheet gives the picker, bounded by the viewport.
        if (
            !showSteamModal(ui, {
                title,
                wide: true,
                render: (close) => react.createElement(Picker, { close }),
                onCancel: () => settle({ outcome: "cancelled" }),
            })
        ) {
            settle({ outcome: "failed", reason: "Steam's modal components are unavailable." });
        }
    });

/**
 * Opens the picker and resolves with the chosen path, or null when it was cancelled, closed, aborted
 * or could not open. Pages that only need the path use this; `runSteamFilePicker` tells them apart.
 * @param ui Steam's resolved React, buttons, focus and modal components.
 * @param options Title, mode, extension filters, starting path and cancellation signal.
 * @returns The validated path or null; it settles exactly once.
 */
const showSteamFilePicker = (ui, options: SteamFilePickerOptions = {}) =>
    runSteamFilePicker(ui, options).then((result) =>
        result.outcome === "selected" ? result.path : null,
    );
