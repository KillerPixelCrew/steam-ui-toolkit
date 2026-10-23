// Trusted configuration UI. Covers are requested by source identity only; scene code never runs here.
function createScreensaverLibrary({ getUi, getState, local, act, ready }) {
    let closeManager: (() => void) | null = null;
    const kind = (source) =>
        source.type === "scene" ? "Scene pack" : source.pack ? "Image pack" : "Image folder";
    const duration = (seconds) =>
        seconds < 60 ? `${seconds} seconds` : seconds < 3600 ? `${seconds / 60} minutes` : "1 hour";
    const describe = (source) =>
        source.type === "scene"
            ? `${source.framesPerSecond} fps animation`
            : `${source.imageCount ?? 0} ${(source.imageCount ?? 0) === 1 ? "image" : "images"} · ${source.shuffle ? "Shuffle" : "Filename order"} · ${duration(source.intervalSeconds)}`;

    function useCover(source) {
        const react = getUi().react;
        const [image, setImage] = react.useState(null);
        react.useEffect(() => {
            const abort = new AbortController();
            setImage(null);
            if (source?.hasPreview) {
                void act("thumbnail", { id: source.id }, abort.signal)
                    .then((value) => {
                        if (
                            !abort.signal.aborted &&
                            typeof value === "string" &&
                            value.length < 100000 &&
                            value.startsWith("data:image/jpeg;base64,")
                        )
                            setImage(value);
                    })
                    .catch(() => {});
            }
            return () => abort.abort();
        }, [source?.id, source?.previewRevision]);
        return image;
    }

    function SourceCard({ source, selected, open }) {
        const ui = getUi();
        return renderSteamUiCard(ui, {
            key: source.id,
            image: useCover(source),
            placeholder: kind(source),
            title: source.name,
            badge: !source.available
                ? { text: "Needs attention", warn: true }
                : selected
                  ? { text: "Selected" }
                  : null,
            stats: [{ text: kind(source) }],
            meta: [source.available ? describe(source) : source.detail],
            onActivate: open,
        });
    }

    function SourceEditor({ source, selected, perform, busy, back, abort, fail }) {
        const ui = getUi();
        const h = ui.react.createElement;
        const [name, setName] = ui.react.useState(source.name);
        ui.react.useEffect(() => setName(source.name), [source.id, source.name]);
        const image = useCover(source);
        const row = (props, change = (_value) => {}, action = () => {}) =>
            renderSteamSettingRow(
                ui,
                { ...props, disabled: busy || props.disabled },
                undefined,
                (_row, value) => change(value),
                action,
            );
        const configure = (option, value) =>
            void perform("configure", { id: source.id, option, value });
        const relocate = async () => {
            const chosen = await runSteamFilePicker(ui, {
                mode: "folder",
                start: source.folder,
                title: "Choose source folder",
                signal: abort.signal,
            });
            if (!abort.signal.aborted && chosen.outcome === "selected")
                await perform("relocate", { id: source.id, folder: chosen.path });
            else if (!abort.signal.aborted && chosen.outcome === "failed") fail(chosen.reason);
        };
        const options =
            source.type === "scene"
                ? [
                      row(
                          {
                              key: "rate",
                              kind: "choice",
                              label: "Animation rate",
                              layout: "below",
                              text: String(source.framesPerSecond),
                              choices: [...new Set([10, 15, 24, 30, 60, source.framesPerSecond])]
                                  .sort((a, b) => a - b)
                                  .map((value) => ({
                                      value: String(value),
                                      label: `${value} fps`,
                                  })),
                          },
                          (value) => configure("framesPerSecond", Number(value)),
                      ),
                  ]
                : [
                      row(
                          {
                              key: "order",
                              kind: "choice",
                              label: "Image order",
                              layout: "below",
                              text: source.shuffle ? "shuffle" : "sequential",
                              choices: [
                                  { value: "shuffle", label: "Shuffle" },
                                  { value: "sequential", label: "Filename order" },
                              ],
                          },
                          (value) => configure("shuffle", value === "shuffle"),
                      ),
                      row(
                          {
                              key: "interval",
                              kind: "choice",
                              label: "Change image every",
                              layout: "below",
                              text: String(source.intervalSeconds),
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
                                      label: duration(value),
                                  })),
                          },
                          (value) => configure("intervalSeconds", Number(value)),
                      ),
                      row(
                          {
                              key: "sizing",
                              kind: "choice",
                              label: "Image sizing",
                              description: source.fill
                                  ? "Crop to fill the display"
                                  : "Show the whole image",
                              layout: "below",
                              text: source.fill ? "fill" : "fit",
                              choices: [
                                  { value: "fit", label: "Fit" },
                                  { value: "fill", label: "Fill" },
                              ],
                          },
                          (value) => configure("fill", value === "fill"),
                      ),
                  ];
        return renderSteamUiDetail(ui, {
            title: source.name,
            badge: selected ? "Selected" : kind(source),
            media: renderSteamUiImagePreview(ui, image, source.fill, kind(source)),
            main: [
                h(
                    "p",
                    { className: "steam-ui-kit-muted" },
                    source.available ? describe(source) : source.detail,
                ),
                renderSteamUiToolbar(
                    ui,
                    renderSteamUiTool(
                        ui,
                        null,
                        h(ui.textField, {
                            label: "Name",
                            value: name,
                            maxLength: 128,
                            disabled: busy,
                            onChange: (event) => setName(event?.target?.value ?? ""),
                        }),
                        true,
                    ),
                    h(
                        ui.dialogButton,
                        {
                            disabled: busy || !name.trim() || name.trim() === source.name,
                            onClick: () =>
                                void perform("rename", { id: source.id, name: name.trim() }),
                        },
                        "Rename",
                    ),
                ),
                row(
                    {
                        key: "folder",
                        kind: "action",
                        label: "Folder",
                        description: source.folder,
                        buttonLabel: "Change",
                    },
                    undefined,
                    () => void relocate(),
                ),
                renderSteamUiActions(
                    ui,
                    [
                        {
                            id: "remove",
                            label: "Remove from library",
                            disabled: busy,
                            onClick: () =>
                                showSteamUiConfirm(ui, {
                                    title: `Remove ${source.name}?`,
                                    text: "Remove this source from your screensaver library? Its files will be kept.",
                                    confirmLabel: "Remove",
                                    onConfirm: () =>
                                        void perform("remove", { id: source.id }).then((ok) => {
                                            if (ok) back();
                                        }),
                                }),
                        },
                    ],
                    { compact: true },
                ),
            ],
            aside: [
                renderSteamUiBox(
                    ui.react,
                    source.type === "scene" ? "Scene settings" : "Slideshow settings",
                    ...options,
                ),
            ],
            onBack: back,
            showBack: false,
        });
    }

    function Manager({ close }) {
        const ui = getUi();
        const react = ui.react;
        const h = react.createElement;
        react.useSyncExternalStore(local.subscribe, local.revision);
        const [editing, setEditing] = react.useState("");
        const [adding, setAdding] = react.useState(null);
        const [busy, setBusy] = react.useState(false);
        const [error, setError] = react.useState("");
        const [filter, setFilter] = react.useState("");
        const [abort] = react.useState(() => new AbortController());
        react.useEffect(() => {
            closeManager = close;
            return () => {
                abort.abort();
                if (closeManager === close) closeManager = null;
            };
        }, []);
        const state = getState();
        if (!ready() || !state) return null;
        const source = state.sources.find((item) => item.id === editing);
        const perform = async (action, payload = {}) => {
            setBusy(true);
            setError("");
            try {
                await act(action, payload);
                return !abort.signal.aborted;
            } catch (failure) {
                if (!abort.signal.aborted) setError(String(failure));
                return false;
            } finally {
                if (!abort.signal.aborted) setBusy(false);
            }
        };
        const back = () => {
            setEditing("");
            setAdding(null);
            setError("");
        };
        const choose = async (pack) => {
            const chosen = await runSteamFilePicker(ui, {
                mode: "folder",
                title: pack ? "Choose screensaver pack" : "Choose image folder",
                signal: abort.signal,
            });
            if (abort.signal.aborted) return;
            if (chosen.outcome === "failed") {
                setError(chosen.reason);
                return;
            }
            if (chosen.outcome !== "selected") return;
            const name =
                chosen.path
                    .replace(/[\\/]+$/u, "")
                    .split(/[\\/]/u)
                    .pop() || "My screensaver";
            setEditing("");
            setError("");
            setAdding({ pack, folder: chosen.path, name });
        };
        const add = async () => {
            if (
                adding &&
                (await perform(adding.pack ? "addPack" : "addImages", {
                    folder: adding.folder,
                    name: adding.name.trim(),
                }))
            )
                setAdding(null);
        };
        const visible = state.sources.filter(
            (item) => !filter || item.name.toLowerCase().includes(filter.toLowerCase()),
        );
        let body;
        if (adding) {
            body = h(
                "div",
                { className: "steam-ui-kit-modal-body" },
                h("h3", null, adding.pack ? "Import screensaver pack" : "Create image slideshow"),
                h(ui.textField, {
                    label: "Name",
                    value: adding.name,
                    maxLength: 128,
                    disabled: busy,
                    onChange: (event) => setAdding({ ...adding, name: event?.target?.value ?? "" }),
                }),
                renderSteamUiBox(
                    react,
                    "Folder",
                    h("p", { className: "steam-ui-kit-muted" }, adding.folder),
                ),
                h(
                    "p",
                    { className: "steam-ui-kit-muted" },
                    adding.pack
                        ? "Import the pack's images or scene. Files stay in this folder."
                        : "PNG, JPEG, WebP, GIF and BMP. Images are read from this folder without copying them.",
                ),
            );
        } else if (source) {
            body = h(SourceEditor, {
                source,
                selected: state.selectedId === source.id,
                perform,
                busy,
                back,
                abort,
                fail: setError,
            });
        } else if (!state.sources.length) {
            body = h(
                "div",
                { className: "steam-ui-kit-library-empty" },
                h("h3", null, "No custom screensavers yet"),
                h(
                    "p",
                    null,
                    "Add a folder of photos to create a slideshow, or import an image or scene pack. Your files stay in their folder.",
                ),
            );
        } else {
            body = visible.length
                ? renderSteamUiGrid(
                      ui,
                      visible.map((item) =>
                          h(SourceCard, {
                              key: item.id,
                              source: item,
                              selected: state.selectedId === item.id,
                              open: () => setEditing(item.id),
                          }),
                      ),
                  )
                : renderSteamUiEmpty(react, "No sources match this name.");
        }
        const toolbar =
            source || adding
                ? null
                : renderSteamUiActions(
                      ui,
                      [
                          {
                              id: "images",
                              label: "Add image folder",
                              primary: true,
                              disabled: busy,
                              onClick: () => void choose(false),
                          },
                          {
                              id: "pack",
                              label: "Import pack",
                              disabled: busy,
                              onClick: () => void choose(true),
                          },
                          {
                              id: "refresh",
                              label: "Refresh",
                              disabled: busy,
                              onClick: () => void perform("refresh"),
                          },
                      ],
                      { compact: true },
                  );
        const footer = adding
            ? [
                  { id: "cancel", label: "Back", disabled: busy, onClick: back },
                  {
                      id: "add",
                      label: adding.pack ? "Import pack" : "Add slideshow",
                      primary: true,
                      disabled: busy || !adding.name.trim(),
                      onClick: () => void add(),
                  },
              ]
            : source
              ? [
                    { id: "back", label: "All sources", onClick: back },
                    {
                        id: "preview",
                        label: "Preview fullscreen",
                        disabled: busy || !source.available,
                        onClick: () => void perform("preview", { id: source.id }),
                    },
                    {
                        id: "use",
                        label: state.selectedId === source.id ? "Selected" : "Use screensaver",
                        primary: true,
                        disabled: busy || !source.available || state.selectedId === source.id,
                        onClick: () => void perform("select", { id: source.id }),
                    },
                ]
              : [{ id: "done", label: "Done", onClick: close }];
        return h(
            ui.focusable,
            {
                className: "steam-ui-kit-library",
                onCancelButton: source || adding ? back : close,
                onCancelActionDescription: "Back",
                "flow-children": "column",
            },
            toolbar,
            state.error ? renderSteamUiBanner(ui, { text: state.error, error: true }) : null,
            !source && !adding && state.sources.length > 5
                ? h(ui.textField, {
                      label: "Search sources",
                      value: filter,
                      onChange: (event) => setFilter(event?.target?.value ?? ""),
                  })
                : null,
            error
                ? renderSteamUiBanner(ui, {
                      text: error,
                      error: true,
                      onDismiss: () => setError(""),
                  })
                : null,
            h("div", { className: "steam-ui-kit-library-body" }, body),
            h(
                "div",
                {
                    className:
                        "steam-ui-kit-library-footer" + (source || adding ? " with-back" : ""),
                },
                renderSteamUiActions(ui, footer, { compact: true }),
            ),
        );
    }

    const open = () => {
        if (closeManager) {
            closeManager();
            closeManager = null;
        }
        return showSteamModal(getUi(), {
            title: "Custom screensavers",
            wide: true,
            className: "steam-ui-kit-modal",
            render: (close) => getUi().react.createElement(Manager, { close }),
        });
    };
    return {
        open,
        dispose: () => {
            closeManager?.();
            closeManager = null;
        },
        render: () => {
            const state = getState();
            const ui = getUi();
            if (!ready() || !state || !ui) return null;
            const selected = state.sources.find((source) => source.id === state.selectedId);
            const count = state.sources.length;
            return renderSteamSettingRow(
                ui,
                {
                    key: "source-library",
                    kind: "action",
                    label: "Custom screensavers",
                    description: count
                        ? `${count} ${count === 1 ? "source" : "sources"}${selected ? ` · ${selected.name} selected` : ""}`
                        : "Create a slideshow from your photos or import a local scene pack.",
                    buttonLabel: count ? "Manage sources" : "Add sources",
                },
                undefined,
                () => {},
                open,
            );
        },
    };
}
