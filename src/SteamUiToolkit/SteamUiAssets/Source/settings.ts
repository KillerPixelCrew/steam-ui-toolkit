// A host's own settings, drawn as Steam draws its Settings page.
//
// Every element here is one of Steam's: the routed sidebar its Settings page is built on, its
// settings sections, and its toggle, dropdown, slider, text and value fields, buttons and confirm
// modal. Nothing is styled by this file, so a host's page looks and navigates exactly like
// Settings - and a component Steam no longer ships makes the page unavailable rather than
// replacing it with an imitation.
//
// Mapped against the live client on 2026-09-24:
//
//   module with `disableRouteReporting`   one export: the routed sidebar. Props { pages }, each page
//                                          { title, route, icon, content, visible }. It switches
//                                          pages with history.replace, so B leaves the whole page.
//   the field module (FieldTokens)         `DialogSettingsSection` (a titled section), the name/value
//                                          field (`inlineWrap:"shift-children-below"`, focusable),
//                                          and the small button (`DialogButton _DialogLayout Small`),
//                                          beside the toggle, dropdown, slider and text fields.
//   module with strMiddleButtonText,       one export: the generic confirm modal. Props { strTitle,
//     bProgressDialog and bAlertDialog     strDescription, strOKButtonText, bDestructiveWarning,
//                                          onOK, onCancel }.
//
// The rows are the host's, described by kind rather than by component, so any host page can
// publish them: see SteamSettingsRow on the C# side.

// The routed sidebar Steam's Settings page renders, by the one prop only it takes.
const SteamRoutedPagesTokens = ["disableRouteReporting"] as const;
// The generic confirm modal, by three props only its module names together.
const SteamConfirmModalTokens = ["strMiddleButtonText", "bProgressDialog", "bAlertDialog"] as const;

const resolveSteamSettingsComponents = (runtime) => {
    const ui = resolveSteamUiComponents(runtime);
    const fieldsFactory = runtime.findUnique(FieldTokens);
    if (!ui || !fieldsFactory) return null;
    const fields = runtime(fieldsFactory[0]);

    const settingsSection = uniqueSteamExport(fields, (value) =>
        sourceOfSteamComponent(value).includes('"DialogSettingsSection"'),
    );
    const valueField = uniqueSteamExport(fields, (value) => {
        const source = sourceOfSteamComponent(value);
        return source.includes('inlineWrap:"shift-children-below"') && source.includes("focusable:!0");
    });
    const smallButton = uniqueSteamExport(fields, (value) =>
        sourceOfSteamComponent(value).includes('"DialogButton _DialogLayout Small"'),
    );
    const routedPages = optionalSteamExport(
        runtime,
        [...SteamRoutedPagesTokens],
        (value) =>
            typeof value === "function" &&
            String(value).includes("disableRouteReporting") &&
            String(value).includes("pages"),
    );
    const confirmModal = optionalSteamExport(runtime, [...SteamConfirmModalTokens], (value) => {
        const source = sourceOfSteamComponent(value);
        return SteamConfirmModalTokens.every((token) => source.includes(token));
    });

    return {...ui, settingsSection, valueField, smallButton, routedPages, confirmModal};
};

// What a page needs from the resolution above to draw every row kind.
const SteamSettingsRequired = [
    "react",
    "focusable",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "dialogButton",
    "smallButton",
    "valueField",
    "settingsSection",
    "routedPages",
    "confirmModal",
    "showModal",
] as const;

// Asks before a change the host marked as needing it, in Steam's own confirm modal. Cancelling
// sends nothing, so the row keeps showing what the host last published.
const confirmSteamSetting = (ui, confirmation, proceed: () => void) => {
    const h = ui.react.createElement;
    ui.showModal(
        h(ui.confirmModal, {
            strTitle: confirmation.title,
            strDescription: confirmation.description,
            strOKButtonText: confirmation.confirmLabel,
            bDestructiveWarning: confirmation.destructive !== false,
            onOK: proceed,
            onCancel: () => {},
        }),
        window,
        {strTitle: confirmation.title},
    );
};

// A colour as hue, saturation, lightness and alpha, read from the hex and hsl(a) forms a theme's
// colour takes, and written back as hsla() the way CSSLoader's colour picker writes it.
const parseSteamColor = (text: string) => {
    const value = String(text ?? "").trim();
    const hsl = /^hsla?\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%\s*(?:,\s*([\d.]+)\s*)?\)$/iu.exec(value);
    if (hsl) {
        return {
            h: Math.min(360, Math.max(0, Number(hsl[1]))),
            s: Math.min(100, Math.max(0, Number(hsl[2]))),
            l: Math.min(100, Math.max(0, Number(hsl[3]))),
            a: hsl[4] === undefined ? 1 : Math.min(1, Math.max(0, Number(hsl[4]))),
        };
    }
    const hex = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/iu.exec(value);
    if (!hex) return {h: 0, s: 0, l: 100, a: 1};
    let digits = hex[1];
    if (digits.length <= 4) digits = [...digits].map((digit) => digit + digit).join("");
    const r = parseInt(digits.slice(0, 2), 16) / 255;
    const g = parseInt(digits.slice(2, 4), 16) / 255;
    const b = parseInt(digits.slice(4, 6), 16) / 255;
    const a = digits.length === 8 ? parseInt(digits.slice(6, 8), 16) / 255 : 1;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    let h = 0;
    let s = 0;
    if (max !== min) {
        const d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max === g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
    }
    return {h: Math.round(h), s: Math.round(s * 100), l: Math.round(l * 100), a: Math.round(a * 100) / 100};
};
const formatSteamColor = (color) => `hsla(${color.h}, ${color.s}%, ${color.l}%, ${color.a})`;

// Edits a colour in Steam's modal with Steam's sliders. Save sends it once; Cancel and B send nothing.
const showSteamColorEditor = (ui, title: string, current: string, send: (value: string) => void) => {
    const react = ui.react;
    const h = react.createElement;
    function SteamColorEditor(props: any) {
        const [color, setColor] = react.useState(parseSteamColor(current));
        const slider = (label, key, max, step = 1) =>
            h(ui.sliderField, {
                key,
                label,
                value: color[key],
                min: 0,
                max,
                step,
                showValue: true,
                onChange: (value) => setColor({...color, [key]: value}),
            });
        return h(
            "div",
            {className: "steam-ui-color-editor"},
            h("div", {
                className: "steam-ui-color-preview",
                style: {
                    height: "48px",
                    borderRadius: "4px",
                    marginBottom: "12px",
                    background: formatSteamColor(color),
                    border: "1px solid rgba(255,255,255,0.3)",
                },
            }),
            slider("Hue", "h", 360),
            slider("Saturation", "s", 100),
            slider("Lightness", "l", 100),
            slider("Opacity", "a", 1, 0.01),
            h(
                ui.focusable,
                {"flow-children": "row", style: {display: "flex", justifyContent: "flex-end", gap: "8px"}},
                h(ui.dialogButton, {onClick: () => props.close()}, "Cancel"),
                h(
                    ui.dialogButtonPrimary ?? ui.dialogButton,
                    {
                        onClick: () => {
                            send(formatSteamColor(color));
                            props.close();
                        },
                    },
                    "Save",
                ),
            ),
        );
    }
    return showSteamModal(ui, {
        title,
        className: "steam-ui-color-modal",
        render: (close) => h(SteamColorEditor, {close}),
    });
};

// One row, by kind. `draft` is what the user has changed and the host has not yet republished,
// so a toggle does not flick back while its write is in flight; `change` records a draft and sends
// the value; `action` asks the host to run a row's action.
const renderSteamSettingRow = (ui, row, draft, change, action) => {
    const h = ui.react.createElement;
    const key = `steam-setting-${row.key}`;
    const common = {label: row.label, description: row.description, disabled: !!row.disabled};
    const send = (value) => {
        const confirmation = row.confirm;
        if (confirmation && value === confirmation.when) {
            confirmSteamSetting(ui, confirmation, () => change(row, value));
        } else {
            change(row, value);
        }
    };

    switch (row.kind) {
        case "boolean":
            return h(ui.toggleField, {
                key,
                ...common,
                controlled: true,
                checked: draft !== undefined ? draft : !!row.checked,
                onChange: (value) => send(!!value),
            });
        case "choice":
            return h(ui.dropdown, {
                key,
                ...common,
                rgOptions: (row.choices ?? []).map((choice) => ({data: choice.value, label: choice.label})),
                selectedOption: draft !== undefined ? draft : row.text,
                onChange: (option) => send(option?.data),
            });
        case "range": {
            // A range with labels is one of them by index: Steam's slider names each notch and the
            // value is the notch, not a number worth printing beside the track.
            const labels = Array.isArray(row.labels) && row.labels.length > 1 ? row.labels : null;
            return h(ui.sliderField, {
                key,
                ...common,
                value: draft !== undefined ? draft : (row.number ?? 0),
                min: labels ? 0 : (row.minimum ?? 0),
                max: labels ? labels.length - 1 : (row.maximum ?? 100),
                step: labels ? 1 : (row.step ?? 1),
                showValue: !labels,
                valueSuffix: row.suffix ?? undefined,
                notchCount: labels ? labels.length : undefined,
                notchLabels: labels
                    ? labels.map((label, notchIndex) => ({notchIndex, label: String(label)}))
                    : undefined,
                notchTicksVisible: labels ? true : undefined,
                // Every step while the slider moves only redraws it; the value is sent once, when it
                // settles, so a sweep across the range is one write rather than dozens.
                onChange: (value) => change(row, value, false),
                onChangeComplete: (value) => send(value),
            });
        }
        case "color": {
            // A colour is shown as its swatch and its text, and edited in a modal of Steam's sliders,
            // the way CSSLoader's colour picker edits a theme's colour. Where this client has no
            // modal, the text itself is editable, so the value is never out of reach.
            const current = String(draft !== undefined ? draft : (row.text ?? ""));
            if (!ui.showModal || !ui.modalRoot) {
                return h(ui.textField, {
                    key,
                    ...common,
                    value: current,
                    onChange: (event) => change(row, event?.target?.value ?? "", false),
                    onBlur: () => {
                        if (draft !== undefined && draft !== row.text) send(draft);
                    },
                });
            }
            return h(ui.valueField, {
                key,
                name: row.label,
                description: row.description,
                focusable: false,
                value: h(
                    ui.focusable,
                    {"flow-children": "row", style: {display: "flex", alignItems: "center", gap: "8px"}},
                    h("div", {
                        className: "steam-ui-color-swatch",
                        style: {
                            width: "20px",
                            height: "20px",
                            borderRadius: "3px",
                            background: current,
                            border: "1px solid rgba(255,255,255,0.3)",
                        },
                    }),
                    h("span", null, current),
                    h(
                        ui.smallButton,
                        {disabled: !!row.disabled, onClick: () => showSteamColorEditor(ui, row.label, current, send)},
                        "Edit",
                    ),
                ),
            });
        }
        case "text":
        case "secret": {
            const secret = row.kind === "secret";
            return h(ui.textField, {
                key,
                ...common,
                // A secret is never published, so its box starts empty and says only whether one is set.
                value: draft !== undefined ? draft : secret ? "" : (row.text ?? ""),
                type: secret ? "password" : "text",
                placeholder: secret ? row.text : undefined,
                maxLength: row.maximumLength ?? undefined,
                onChange: (event) => change(row, event?.target?.value ?? "", false),
                // Sent only once typed into. A secret's box starts empty, so an empty draft is the user
                // clearing it, which is a change like any other; an untouched box sends nothing.
                onBlur: () => {
                    if (draft === undefined || (!secret && draft === row.text)) {
                        return;
                    }
                    send(draft);
                },
            });
        }
        case "order": {
            const values = draft !== undefined ? draft : (row.order ?? []);
            const labelOf = (value) => row.choices?.find((choice) => choice.value === value)?.label ?? value;
            const move = (index, offset) => {
                const next = values.slice();
                const [moved] = next.splice(index, 1);
                next.splice(index + offset, 0, moved);
                send(next);
            };
            return h(
                ui.react.Fragment,
                {key},
                h(ui.valueField, {name: row.label, value: null, description: row.description, focusable: false}),
                ...values.map((value, index) =>
                    h(ui.valueField, {
                        key: `${key}-${value}`,
                        name: labelOf(value),
                        indentLevel: 1,
                        focusable: false,
                        value: h(
                            ui.focusable,
                            {"flow-children": "row"},
                            h(
                                ui.smallButton,
                                {disabled: row.disabled || index === 0, onClick: () => move(index, -1)},
                                "Move up",
                            ),
                            h(
                                ui.smallButton,
                                {
                                    disabled: row.disabled || index === values.length - 1,
                                    onClick: () => move(index, 1),
                                },
                                "Move down",
                            ),
                        ),
                    }),
                ),
            );
        }
        case "action":
            return h(ui.valueField, {
                key,
                name: row.label,
                description: row.description,
                focusable: false,
                value: h(
                    ui.dialogButton,
                    {disabled: !!row.disabled, onClick: () => action(row)},
                    row.buttonLabel ?? row.label,
                ),
            });
        case "note":
            return h(ui.valueField, {key, name: row.label, value: row.text ?? "", description: row.description});
        default:
            // A kind this build does not know is shown as its label and nothing else, never as a
            // control that would send a value the host did not describe.
            return h(ui.valueField, {key, name: row.label, value: "", description: row.description});
    }
};

// The whole page: Steam's routed sidebar, one page per host page, each a list of Steam sections.
// `route` is the page's own registered route; each page sits below it, so Steam's router keeps
// the sidebar's selection in the address and the page's registration covers all of them.
function SteamSettingsView(props) {
    const {ui, route, pages, revision, onChange, onAction} = props;
    const react = ui.react;
    const h = react.createElement;
    const [drafts, setDrafts] = react.useState({});

    // A new publication is the host's word on every row, so drafts typed against the last one go.
    react.useEffect(() => setDrafts({}), [revision]);

    const change = (row, value, commit = true) => {
        setDrafts((previous) => ({...previous, [row.key]: value}));
        if (commit) onChange(row, value);
    };

    return h(ui.routedPages, {
        pages: (pages ?? []).map((page) => ({
            title: page.title,
            route: `${route}/${page.id}`,
            icon: page.glyph ? renderSteamGlyph(react, page.glyph) : undefined,
            content: h(
                react.Fragment,
                null,
                ...(page.sections ?? []).map((section, index) =>
                    h(
                        ui.settingsSection,
                        {key: `${page.id}-${index}`, label: section.title ?? undefined},
                        ...(section.rows ?? []).map((row) =>
                            renderSteamSettingRow(ui, row, drafts[row.key], change, onAction),
                        ),
                    ),
                ),
            ),
        })),
    });
}

const renderSteamSettings = (ui, props) => ui.react.createElement(SteamSettingsView, {ui, ...props});
