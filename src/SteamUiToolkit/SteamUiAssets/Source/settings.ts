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
        case "range":
            return h(ui.sliderField, {
                key,
                ...common,
                value: draft !== undefined ? draft : (row.number ?? 0),
                min: row.minimum ?? 0,
                max: row.maximum ?? 100,
                step: row.step ?? 1,
                showValue: true,
                valueSuffix: row.suffix ?? undefined,
                // Every step while the slider moves only redraws it; the value is sent once, when it
                // settles, so a sweep across the range is one write rather than dozens.
                onChange: (value) => change(row, value, false),
                onChangeComplete: (value) => send(value),
            });
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
