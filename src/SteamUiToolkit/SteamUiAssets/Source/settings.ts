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
//                                          field (inlineWrap "shift-children-below", focusable), and
//                                          the small button (classes DialogButton, _DialogLayout, Small),
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
    // The name/value field sets `inlineWrap` to "shift-children-below" and makes the row focusable.
    // The field layout it draws through reads the same props, but takes a second argument beside
    // them; a component takes its props alone.
    const valueField = uniqueSteamExport(fields, (value) => {
        if (typeof value !== "function" || value.length !== 1) return false;
        const source = String(value);
        return ["inlineWrap", '"shift-children-below"', "focusable"].every((token) => source.includes(token));
    });
    const smallButton = uniqueSteamExport(fields, (value) => isSteamDialogButton(value, "Small"));
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

// A colour as hue, saturation, lightness and alpha, read from the hex, rgb(a) and hsl(a) forms a
// theme's colour takes (comma or space separated), and written back as hsla() the way CSSLoader's
// colour picker writes it. Anything else, a named colour or a variable, answers null, and the row
// keeps it as editable text rather than opening it as some other colour.
const clampSteamColorPart = (value: number, max: number) => Math.min(max, Math.max(0, value));
type SteamColor = {h: number; s: number; l: number; a: number};
// An alpha written as a fraction or as a percentage, or 1 when the colour gives none.
const steamColorAlpha = (value: string | undefined, percent: string | undefined) =>
    value === undefined ? 1 : clampSteamColorPart(Number(value) / (percent ? 100 : 1), 1);
// Red, green and blue from 0 to 1 as hue, saturation and lightness.
const steamColorFromRgb = (r: number, g: number, b: number, a: number): SteamColor => {
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
const SteamColorSeparator = String.raw`\s*(?:,\s*|\s+)`;
const SteamColorAlpha = String.raw`(?:\s*(?:,|\/)\s*([\d.]+)(%?))?\s*`;
const SteamHslPattern = new RegExp(
    String.raw`^hsla?\(\s*([\d.]+)(?:deg)?${SteamColorSeparator}([\d.]+)%${SteamColorSeparator}([\d.]+)%` +
        String.raw`${SteamColorAlpha}\)$`,
    "iu",
);
const SteamRgbPattern = new RegExp(
    String.raw`^rgba?\(\s*([\d.]+)(%?)${SteamColorSeparator}([\d.]+)(%?)${SteamColorSeparator}([\d.]+)(%?)` +
        String.raw`${SteamColorAlpha}\)$`,
    "iu",
);
const parseSteamColor = (text: string): SteamColor | null => {
    const value = String(text ?? "").trim();
    const hsl = SteamHslPattern.exec(value);
    if (hsl) {
        return {
            h: clampSteamColorPart(Number(hsl[1]), 360),
            s: clampSteamColorPart(Number(hsl[2]), 100),
            l: clampSteamColorPart(Number(hsl[3]), 100),
            a: steamColorAlpha(hsl[4], hsl[5]),
        };
    }
    const rgb = SteamRgbPattern.exec(value);
    if (rgb) {
        const channel = (number: string, percent: string) =>
            clampSteamColorPart(Number(number) / (percent ? 100 : 255), 1);
        return steamColorFromRgb(
            channel(rgb[1], rgb[2]),
            channel(rgb[3], rgb[4]),
            channel(rgb[5], rgb[6]),
            steamColorAlpha(rgb[7], rgb[8]),
        );
    }
    const hex = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/iu.exec(value);
    if (!hex) return null;
    let digits = hex[1];
    if (digits.length <= 4) digits = [...digits].map((digit) => digit + digit).join("");
    return steamColorFromRgb(
        parseInt(digits.slice(0, 2), 16) / 255,
        parseInt(digits.slice(2, 4), 16) / 255,
        parseInt(digits.slice(4, 6), 16) / 255,
        digits.length === 8 ? parseInt(digits.slice(6, 8), 16) / 255 : 1,
    );
};
const formatSteamColor = (color) => `hsla(${color.h}, ${color.s}%, ${color.l}%, ${color.a})`;

// Edits a colour in Steam's modal with Steam's sliders. Save sends it once; Cancel and B send nothing.
const showSteamColorEditor = (ui, title: string, current: SteamColor, send: (value: string) => void) => {
    const react = ui.react;
    const h = react.createElement;
    function SteamColorEditor(props: any) {
        const [color, setColor] = react.useState(current);
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

// A row the host marks draws its description in Steam's accent blue, the way a marked Quick Access
// row does. The words are the host's; the toolkit adds none and offers no control to unmark it.
const steamSettingDescription = (ui, row) =>
    steamAccentDescription(ui.react, row.description, row.accent === true);

// One row, by kind. `draft` is what the user has changed and the host has not yet republished,
// so a toggle does not flick back while its write is in flight; `change` records a draft and sends
// the value; `action` asks the host to run a row's action.
const renderSteamSettingRow = (ui, row, draft, change, action) => {
    const h = ui.react.createElement;
    const key = `steam-setting-${row.key}`;
    const common = {label: row.label, description: steamSettingDescription(ui, row), disabled: !!row.disabled};
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
                // A row may ask for the control under its label rather than beside it, which is
                // how a dropdown fits a narrow panel.
                layout: row.layout === "below" ? "below" : undefined,
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
            // modal, or the value is not a colour the sliders can hold, the text itself is editable,
            // so the value is never out of reach and never replaced by a colour it was not. Text
            // being typed stays a text field until it is sent.
            const current = String(draft !== undefined ? draft : (row.text ?? ""));
            const parsed = draft === undefined ? parseSteamColor(current) : null;
            if (!ui.showModal || !ui.modalRoot || !parsed) {
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
                description: steamSettingDescription(ui, row),
                focusable: false,
                value: h(
                    ui.focusable,
                    {"flow-children": "row", style: {display: "flex", alignItems: "center", gap: "8px"}},
                    renderSteamUiSwatch(ui.react, current),
                    h("span", null, current),
                    h(
                        ui.smallButton,
                        {disabled: !!row.disabled, onClick: () => showSteamColorEditor(ui, row.label, parsed, send)},
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
                h(ui.valueField, {name: row.label, value: null, description: steamSettingDescription(ui, row), focusable: false}),
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
                description: steamSettingDescription(ui, row),
                focusable: false,
                value: h(
                    ui.dialogButton,
                    {disabled: !!row.disabled, onClick: () => action(row)},
                    row.buttonLabel ?? row.label,
                ),
            });
        case "note":
            return h(ui.valueField, {key, name: row.label, value: row.text ?? "", description: steamSettingDescription(ui, row)});
        default:
            // A kind this build does not know is shown as its label and nothing else, never as a
            // control that would send a value the host did not describe.
            return h(ui.valueField, {key, name: row.label, value: "", description: steamSettingDescription(ui, row)});
    }
};

// What a row was published with, as text, so a list compares by its entries.
const steamSettingPublished = (row) =>
    JSON.stringify(
        row.kind === "boolean"
            ? !!row.checked
            : row.kind === "range"
              ? (row.number ?? null)
              : row.kind === "order"
                ? (row.order ?? null)
                : (row.text ?? null),
    );

// The drafts of a set of host rows: what the user changed that the host has not published yet, so a
// toggle does not flick back while its write is in flight and typed text stays while it is typed.
//
// A draft remembers what its row was published with when it was made and is shown only while the
// row still carries that, so a publication that changes another row leaves it alone. A committed
// draft is written through `send`, which answers the write's request: a refusal drops that row's
// draft and shows the reason as the row's description until the row is changed again or published
// with another value, and an accepted write's draft gives way to the next `revision`. A `send` that
// answers nothing counts as accepted.
//
// `change(send)` is the change a row renderer calls, `draft(row)` the value to draw it with and
// `row(row)` the row with its refusal, if any, as its description.
const useSteamSettingDrafts = (react, revision: unknown) => {
    const [state, setState] = react.useState({drafts: {}, refusals: {}});
    react.useEffect(
        () =>
            setState((previous) => {
                const drafts = {};
                for (const [key, draft] of Object.entries(previous.drafts) as [string, any][]) {
                    if (!draft.committed) drafts[key] = draft;
                }
                return {...previous, drafts};
            }),
        [revision],
    );

    // Only the draft that was sent is settled; one the user has replaced since stays as it is.
    const settle = (key: string, sent, refusal: string | null) =>
        setState((previous) => {
            if (previous.drafts[key] !== sent) return previous;
            const drafts = {...previous.drafts};
            if (refusal === null) {
                drafts[key] = {...sent, committed: true};
                return {...previous, drafts};
            }
            delete drafts[key];
            return {drafts, refusals: {...previous.refusals, [key]: {text: refusal, base: sent.base}}};
        });

    const change = (send: (row, value) => unknown) => (row, value, commit = true) => {
        const key = row.key;
        const draft = {value, base: steamSettingPublished(row), committed: false};
        setState((previous) => {
            const refusals = {...previous.refusals};
            delete refusals[key];
            return {drafts: {...previous.drafts, [key]: draft}, refusals};
        });
        if (!commit) return;
        let answer: any;
        try {
            answer = send(row, value);
        } catch (error) {
            answer = Promise.reject(error);
        }
        if (typeof answer?.then !== "function") {
            settle(key, draft, null);
            return;
        }
        answer.then(
            () => settle(key, draft, null),
            (reason) => settle(key, draft, String(reason?.message ?? reason)),
        );
    };

    const draft = (row) => {
        const entry = state.drafts[row.key];
        return entry && entry.base === steamSettingPublished(row) ? entry.value : undefined;
    };

    const shown = (row) => {
        const refusal = state.refusals[row.key];
        return refusal && refusal.base === steamSettingPublished(row) ? {...row, description: refusal.text} : row;
    };

    return {change, draft, row: shown};
};

// The whole page: Steam's routed sidebar, one page per host page, each a list of Steam sections.
// `route` is the page's own registered route; each page sits below it, so Steam's router keeps
// the sidebar's selection in the address and the page's registration covers all of them.
// `onChange` and `onAction` answer the request they made, so a refusal can be shown on its row.
function SteamSettingsView(props) {
    const {ui, route, pages, revision, onChange, onAction} = props;
    const react = ui.react;
    const h = react.createElement;
    const drafts = useSteamSettingDrafts(react, revision);
    const change = drafts.change((row, value) => onChange(row, value));
    const run = drafts.change((row) => onAction(row));
    const action = (row) => run(row, true);

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
                            renderSteamSettingRow(ui, drafts.row(row), drafts.draft(row), change, action),
                        ),
                    ),
                ),
            ),
        })),
    });
}

const renderSteamSettings = (ui, props) => ui.react.createElement(SteamSettingsView, {ui, ...props});
