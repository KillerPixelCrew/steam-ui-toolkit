// Host settings use Steam's native routed sidebar and fields. Missing component contracts refuse
// installation; descriptors and command policy belong to the host.

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

/**
 * Opens a staged color editor; only Save dispatches the selected color.
 * @param ui Steam's resolved React and native control components.
 * @param title Modal title.
 * @param current Parsed hue/saturation/lightness/alpha value.
 * @param send Receives the saved hsla() color; cancellation never calls it.
 * @param alpha Whether opacity is editable; false fixes an opaque color.
 * @returns Whether the native modal could be shown.
 */
const showSteamColorEditor = (ui, title: string, current: SteamColor, send: (value: string) => void, alpha = true) => {
    const react = ui.react;
    const h = react.createElement;
    function SteamColorEditor(props: any) {
        const [color, setColor] = react.useState(alpha ? current : {...current, a: 1});
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
            alpha ? slider("Opacity", "a", 1, 0.01) : null,
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

// A row's label: Steam's own string for its token where the client has one, else the host's words.
// Steam's localizer answers with a React element wrapping the string and hands an unknown token back
// as itself, so the text inside is what is tested.
const steamSettingLabel = (ui, row) => {
    if (!row.labelToken || typeof ui.localize !== "function") return row.label;
    try {
        const localized = ui.localize(row.labelToken);
        const text = textOf(localized);
        return text && text[0] !== "#" && text !== row.labelToken ? localized : row.label;
    } catch {
        return row.label;
    }
};

// A row's glyph, by its toolkit name, where the surface draws glyphs; null for none or an unknown name.
const steamSettingIcon = (ui, row) =>
    row.icon && typeof ui.icon === "function" ? (ui.icon(row.icon) ?? undefined) : undefined;

/**
 * Draws an action row's own buttons. Each sends under its own key through `change`, after its prompt
 * or confirmation when it has one, so one row can name an item and carry the item's commands.
 * @param ui Steam's resolved React and native control components.
 * @param row The action row.
 * @param actions Its buttons.
 * @param change The draft keeper's commit, answered by the host.
 * @returns The focusable row of buttons.
 */
const renderSteamSettingActions = (ui, row, actions, change) => {
    const h = ui.react.createElement;
    // Several commands on one row use Steam's small buttons, as the order rows do, so they fit beside
    // the label; one keeps the dialog button every other action row has.
    const Button = actions.length > 1 ? (ui.smallButton ?? ui.dialogButton) : ui.dialogButton;
    const run = (item) => {
        const send = (value) => change({...row, key: item.key}, value);
        const prompt = item.prompt;
        if (prompt) {
            const shown = showSteamUiPrompt(ui, {
                title: prompt.title,
                text: prompt.text ?? undefined,
                label: prompt.label,
                initial: prompt.initial ?? "",
                confirmLabel: prompt.confirmLabel,
                onConfirm: (text) => send(text),
            });
            // Without a modal the suggestion is the answer, so the command still reaches the host.
            if (!shown && String(prompt.initial ?? "").trim()) send(String(prompt.initial).trim());
            return;
        }
        if (item.confirm) {
            showSteamUiConfirm(ui, {
                title: item.confirm.title,
                text: item.confirm.description,
                confirmLabel: item.confirm.confirmLabel,
                destructive: item.confirm.destructive !== false,
                focusCancel: item.confirm.destructive !== false,
                onConfirm: () => send(true),
            });
            return;
        }
        send(true);
    };
    return h(
        ui.focusable,
        {
            "flow-children": "row",
            style: {display: "flex", flexWrap: "wrap", gap: "8px", justifyContent: "flex-end"},
        },
        ...actions.map((item) =>
            h(
                Button,
                {
                    key: item.key,
                    disabled: !!row.disabled || !!item.disabled,
                    "aria-label": `${item.label} ${row.label}`,
                    onClick: () => run(item),
                },
                item.label,
            ),
        ),
    );
};

/**
 * Renders one typed host setting with native fields and shared refusal/draft handling.
 * @param ui Steam's resolved React and native control components.
 * @param row Published SteamSettingsRow descriptor.
 * @param draft Locally staged value, or undefined to use the published value.
 * @param change Handles edits and whether they should be committed.
 * @param action Handles action rows.
 * @param draftOf A columns row's staged value for one of its entries, by entry; none sees no drafts.
 * @returns The row element, or null for an unsupported row kind.
 */
const renderSteamSettingRow = (ui, row, draft, change, action, draftOf?: (entry) => any) => {
    const h = ui.react.createElement;
    const key = `steam-setting-${row.key}`;
    const label = steamSettingLabel(ui, row);
    const icon = steamSettingIcon(ui, row);
    const common = {label, icon, description: steamSettingDescription(ui, row), disabled: !!row.disabled};
    const send = (value) => {
        const confirmation = row.confirm;
        if (confirmation && value === confirmation.when) {
            // Cancelling sends nothing, so the row keeps showing what the host last published.
            showSteamUiConfirm(ui, {
                title: confirmation.title,
                text: confirmation.description,
                confirmLabel: confirmation.confirmLabel,
                destructive: confirmation.destructive !== false,
                onConfirm: () => change(row, value),
            });
        } else {
            change(row, value);
        }
    };

    switch (row.kind) {
        case "resolution-list":
            return h(SteamUiResolutionEditor, {key, ui, row, send, busy: draft !== undefined});
        case "curve": {
            const points = draft !== undefined ? draft : (row.curve ?? []);
            return h(ui.react.Fragment, {key},
                h(ui.valueField, {name: label, icon, description: common.description, focusable: false, value: null}),
                ...points.map((point, index) => h(ui.sliderField, {
                    key: `${key}-${point.input}`,
                    label: `${point.input} °C`,
                    value: point.output,
                    min: row.minimum ?? 0,
                    max: row.maximum ?? 100,
                    step: row.step ?? 1,
                    valueSuffix: row.suffix ?? "%",
                    showValue: true,
                    disabled: !!row.disabled,
                    onChange: (output) => {
                        // A fan must not slow down as temperature rises. Keep the existing input
                        // breakpoints and adjust adjoining duties when a node moves past them.
                        const next = points.map((entry, entryIndex) => ({...entry,
                            output: entryIndex < index ? Math.min(entry.output, output)
                                : entryIndex > index ? Math.max(entry.output, output) : output}));
                        change(row, next, false);
                    },
                })),
                renderSteamUiActions(ui, [
                        ...(row.curvePresets ?? []).map((preset) => ({id: `${key}-${preset.label}`, label: preset.label,
                            disabled: !!row.disabled || !points.length,
                            onClick: () => change(row, preset.points, false)})),
                        {id: `${key}-apply`, label: "Apply curve", disabled: !!row.disabled || !points.length || draft === undefined,
                            onClick: () => send(points)},
                ]),
            );
        }
        case "boolean":
            return h(ui.toggleField, {
                key,
                ...common,
                controlled: true,
                checked: draft !== undefined ? draft : !!row.checked,
                onChange: (value) => send(!!value),
            });
        case "choice": {
            const selected = draft !== undefined ? draft : row.text;
            // An unselectable choice names a state the user cannot pick: it is listed only while it is
            // the current value, and never sent.
            const choices = (row.choices ?? []).filter(
                (choice) => choice.selectable !== false || choice.value === selected,
            );
            const selectable = (value) =>
                choices.some((choice) => choice.value === value && choice.selectable !== false);
            return h(ui.dropdown, {
                key,
                ...common,
                rgOptions: choices.map((choice) => ({data: choice.value, label: choice.label})),
                selectedOption: selected,
                onChange: (option) => {
                    if (option && option.data !== selected && selectable(option.data)) send(option.data);
                },
                // A row may ask for the control under its label rather than beside it, which is
                // how a dropdown fits a narrow panel.
                layout: row.layout === "below" ? "below" : undefined,
            });
        }
        case "range": {
            // A range with labels is one of them by index: Steam's slider names each notch and the
            // value is the notch, not a number worth printing beside the track.
            const labels = Array.isArray(row.labels) && row.labels.length > 1 ? row.labels : null;
            // Steam's slider always draws a thumb. Without a value it rests at 0 where the range has one,
            // so an offset slider does not read as its strongest setting.
            const resting = Math.min(Math.max(0, row.minimum ?? 0), row.maximum ?? 100);
            const unknown = draft === undefined && row.number == null && !labels;
            // Steam's slider always draws a thumb and announces a number, so a range without a value
            // would read as wherever the thumb rests. While it is unknown the kit hides the thumb and the
            // fill and the slider announces "Unknown"; the first move shows the value the user chose.
            // React sets Steam's accessible value only when it changes, so the mark is applied by the
            // wrapper's ref, which runs again on each render, and the first real value replaces it.
            const markUnknown = (node) => {
                const slider = node?.querySelector?.("[role=slider]");
                if (!slider) return;
                slider.removeAttribute("aria-valuenow");
                slider.setAttribute("aria-valuetext", "Unknown");
            };
            // The wrapper stays for every range so the slider is never remounted, and keeps its focus,
            // when the first move makes the value known.
            return h("div", {
                key,
                className: unknown ? "steam-ui-kit-range steam-ui-kit-range-unknown" : "steam-ui-kit-range",
                ref: unknown ? markUnknown : undefined,
            }, h(ui.sliderField, {
                key,
                ...common,
                // A labelled slider keeps its glyph with the label, so every row of a section lines its
                // glyph up in one column; Steam would otherwise put it beside the track.
                iconLocation: icon ? "front" : undefined,
                value: draft !== undefined ? draft : (row.number ?? resting),
                min: labels ? 0 : (row.minimum ?? 0),
                max: labels ? labels.length - 1 : (row.maximum ?? 100),
                step: labels ? 1 : (row.step ?? 1),
                showValue: !labels,
                valueSuffix: row.suffix ?? undefined,
                ...(draft === undefined && row.number == null && !labels
                    ? {showValue: false, description: steamSettingDescription(ui, {...row,
                        description: [row.description, "Unknown · adjust to set a value"].filter(Boolean).join(" · ")})}
                    : {}),
                notchCount: labels ? labels.length : undefined,
                notchLabels: labels
                    ? labels.map((label, notchIndex) => ({notchIndex, label: String(label)}))
                    : undefined,
                notchTicksVisible: labels ? true : undefined,
                // Every step while the slider moves only redraws it; the value is sent once, when it
                // settles, so a sweep across the range is one write rather than dozens.
                onChange: (value) => change(row, value, false),
                onChangeComplete: (value) => send(value),
            }));
        }
        case "color": {
            // A colour is shown as its swatch alone, which carries the value as its accessible name
            // (hex text beside it wrapped in a narrow row), and edited in a modal of Steam's sliders,
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
                name: label,
                icon,
                description: common.description,
                focusable: false,
                value: h(
                    ui.focusable,
                    {"flow-children": "row", style: {display: "flex", alignItems: "center", gap: "8px"}},
                    renderSteamUiSwatch(ui.react, current, `${row.label}: ${current}`),
                    h(
                        ui.smallButton,
                        {
                            disabled: !!row.disabled,
                            "aria-label": `Edit ${row.label}`,
                            onClick: () => showSteamColorEditor(ui, row.label, parsed, send, row.colorAlpha !== false),
                        },
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
                h(ui.valueField, {name: label, icon, value: null, description: common.description, focusable: false}),
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
        case "action": {
            const actions = Array.isArray(row.actions) && row.actions.length > 0 ? row.actions : null;
            if (actions) {
                return h(ui.valueField, {
                    key,
                    name: label,
                    icon,
                    description: common.description,
                    focusable: false,
                    value: renderSteamSettingActions(ui, row, actions, change),
                });
            }
            return h(ui.valueField, {
                key,
                name: label,
                icon,
                description: common.description,
                focusable: false,
                value: h(
                    ui.dialogButton,
                    {disabled: !!row.disabled, onClick: () => action(row)},
                    row.buttonLabel ?? row.label,
                ),
            });
        }
        case "note":
            return h(ui.valueField, {key, name: label, icon, value: row.text ?? "", description: common.description});
        case "scope":
            return renderSteamUiScope(ui.react, {
                key,
                label,
                value: row.text ?? "",
                detail: row.description,
                accent: row.accent === true,
            });
        case "columns":
            return renderSteamUiColumns(ui, {
                key,
                label,
                description: common.description,
                columns: (row.columns ?? []).map((entry) => {
                    const selected = draftOf?.(entry) ?? entry.text;
                    const choices = (entry.choices ?? []).filter(
                        (choice) => choice.selectable !== false || choice.value === selected,
                    );
                    return {
                        key: entry.key,
                        label: entry.label,
                        control: renderSteamUiChoice(ui, {
                            label: `${row.label}: ${entry.label}`,
                            showLabel: false,
                            rgOptions: choices.map((choice) => ({data: choice.value, label: choice.label})),
                            selectedOption: selected,
                            disabled: !!row.disabled || !!entry.disabled,
                            onChange: (option) => {
                                const pick = choices.find((choice) => choice.value === option?.data);
                                if (pick && pick.value !== selected && pick.selectable !== false) change(entry, pick.value);
                            },
                        }),
                    };
                }),
            });
        default:
            // A kind this build does not know is shown as its label and nothing else, never as a
            // control that would send a value the host did not describe.
            return h(ui.valueField, {key, name: label, icon, value: "", description: common.description});
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
                : row.kind === "curve"
                  ? (row.curve ?? null)
                : (row.text ?? null),
    );

/**
 * Tracks per-row edits and refusals without letting older requests settle newer drafts.
 * @param react Steam's React instance used by the consuming root.
 * @param revision Published revision; a change releases committed drafts to the new state.
 * @returns change(send), draft(row) and row(row) helpers for this mounted component.
 */
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

    // An action row's own buttons send under their own keys; their refusal is still said on the row.
    const shown = (row) => {
        // A columns row's entries send as rows of their own, so each refusal is checked against its entry.
        const senders = [
            ...[row.key, ...(Array.isArray(row.actions) ? row.actions.map((action) => action.key) : [])].map(
                (key) => ({key, published: row}),
            ),
            ...(Array.isArray(row.columns) ? row.columns.map((entry) => ({key: entry.key, published: entry})) : []),
        ];
        const refusal = senders
            .map((sender) => ({entry: state.refusals[sender.key], published: sender.published}))
            .find(({entry, published}) => entry && entry.base === steamSettingPublished(published))?.entry;
        return refusal ? {...row, description: refusal.text} : row;
    };

    return {change, draft, row: shown};
};

// A section's subpage, or null for one drawn on the page. A level without a usable id or title is
// ignored, so its section stays reachable on the page rather than behind a row that cannot be named.
const steamSettingLevel = (section) => {
    const level = section?.level;
    return level && typeof level.id === "string" && level.id && typeof level.title === "string" && level.title
        ? level
        : null;
};

/**
 * Draws one settings page's sections with Steam's sections and fields. A section naming a level is
 * not drawn on the page: the page shows one row per level, where the level's first section would be,
 * and adjacent level rows share one section under the first one's group heading. While a level is
 * open, the page draws that level alone, under a heading whose Back (and B) closes it.
 * @param ui Steam's resolved React and native control components.
 * @param props The page id, its published sections, the draft keeper (`row`, `draft`), the change and
 * action handlers, the open level's id, the level last opened (whose row takes focus again) and the
 * callback that opens a level or, with null, closes it.
 * @returns The page's elements, or the open level.
 */
const renderSteamSettingSections = (
    ui,
    props: {
        id: string;
        sections: readonly any[];
        drafts: {row: (row) => any; draft: (row) => any};
        change: (row, value, commit?: boolean) => void;
        action: (row) => void;
        open?: string | null;
        last?: string | null;
        onOpen: (level: string | null) => void;
    },
) => {
    const h = ui.react.createElement;
    const sections = (props.sections ?? []).filter((section) => section?.rows?.length);
    const levels = new Map<string, {level: any; sections: any[]}>();
    for (const section of sections) {
        const level = steamSettingLevel(section);
        if (!level) continue;
        const entry = levels.get(level.id) ?? {level, sections: []};
        entry.sections.push(section);
        levels.set(level.id, entry);
    }
    const draw = (section, index) =>
        h(
            ui.settingsSection,
            {key: `${props.id}-${section.id ?? index}`, label: section.title ?? undefined},
            ...section.rows.map((row) =>
                renderSteamSettingRow(
                    ui,
                    props.drafts.row(row),
                    props.drafts.draft(row),
                    props.change,
                    props.action,
                    props.drafts.draft,
                ),
            ),
        );
    const open = props.open ? levels.get(props.open) : null;
    if (open) {
        const back = () => props.onOpen(null);
        return [
            renderSteamUiLevel(
                ui,
                {className: "steam-ui-kit-subpage", onBack: back},
                renderSteamUiSubpageHeader(ui, {title: open.level.title, detail: open.level.summary, onBack: back}),
                ...open.sections.map(draw),
            ),
        ];
    }
    const items: any[] = [];
    const placed = new Set<string>();
    let run: {key: string; label?: string; rows: any[]} | null = null;
    sections.forEach((section, index) => {
        const level = steamSettingLevel(section);
        if (!level) {
            run = null;
            items.push(draw(section, index));
            return;
        }
        if (placed.has(level.id)) return;
        placed.add(level.id);
        if (!run) {
            run = {key: `${props.id}-levels-${level.id}`, label: level.group ?? undefined, rows: []};
            items.push(run);
        }
        run.rows.push(
            renderSteamUiNavRow(ui, {
                key: `level-${level.id}`,
                title: level.title,
                summary: level.summary,
                icon: level.icon,
                badge: level.badge ? {text: level.badge, tone: "info"} : null,
                autoFocus: props.last === level.id,
                onActivate: () => props.onOpen(level.id),
            }),
        );
    });
    return items.map((item) =>
        item.rows ? h(ui.settingsSection, {key: item.key, label: item.label}, ...item.rows) : item,
    );
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
    // Each page's open subpage and the one last opened, by page id.
    const [levels, setLevels] = react.useState({});
    const opener = (id) => (level) =>
        setLevels((previous) => ({...previous, [id]: {open: level, last: level ?? previous[id]?.open ?? null}}));

    return h(ui.routedPages, {
        pages: (pages ?? []).map((page) => ({
            title: page.title,
            route: `${route}/${page.id}`,
            icon: page.glyph ? renderSteamGlyph(react, page.glyph) : undefined,
            content: h(
                react.Fragment,
                null,
                ...renderSteamSettingSections(ui, {
                    id: page.id,
                    sections: page.sections ?? [],
                    drafts,
                    change,
                    action,
                    open: levels[page.id]?.open ?? null,
                    last: levels[page.id]?.last ?? null,
                    onOpen: opener(page.id),
                }),
            ),
        })),
    });
}

/**
 * Creates a routed native Settings view over host-owned descriptors.
 * @param ui Steam's resolved React and native control components.
 * @param props Base route, pages, publication revision and setting/action callbacks.
 * @returns The settings view; the caller owns state persistence and command validation.
 */
const renderSteamSettings = (ui, props) => ui.react.createElement(SteamSettingsView, {ui, ...props});
