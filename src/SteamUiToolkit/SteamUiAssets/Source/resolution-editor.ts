// A size editor built around Steam's inline list-builder input and native buttons.
// Selection is local; only an explicit add, remove, percentage or refresh action reaches the host.
const SteamUiResolutionEditor = ({ui, row, send, busy = false}) => {
    const react = ui.react;
    const h = react.createElement;
    const [size, setSize] = react.useState("");
    const [selected, setSelected] = react.useState(row.choices?.[0]?.value ?? "");
    const choices = row.choices ?? [];
    const editor = row.resolutionEditor ?? {};
    const disabled = !!row.disabled || busy;
    react.useEffect(() => {
        if (!choices.some((item) => item.value === selected)) setSelected(choices[0]?.value ?? "");
    }, [choices, selected]);
    if (!ui.listBuilder) {
        return h(ui.valueField, {name: row.label, value: "Steam's native list is unavailable."});
    }
    const dimensions = size.match(/^\s*(\d{3,5})\s*[x×X]\s*(\d{3,5})\s*$/);
    const width = Number(dimensions?.[1]);
    const height = Number(dimensions?.[2]);
    const valid = dimensions && width >= 100 && width <= 15999 && height >= 100 && height <= 15999;
    const button = (label, blocked, onClick) => h(ui.smallButton, {disabled: disabled || blocked, onClick}, label);
    const [selectedWidth, selectedHeight] = selected.split("x").map(Number);
    return h("div", {className: "steam-ui-kit-resolution-editor", role: "group", "aria-label": row.label},
        h("div", {className: "steam-ui-kit-resolution-list", "aria-label": row.label},
            h(ui.listBuilder, {label: row.label, value: size, disabled, maxLength: 13,
                placeholder: "Width × height", "aria-label": "Custom resolution in pixels",
                onChange: (event) => setSize(event?.target?.value ?? "")},
                h(ui.focusable, {className: "steam-ui-kit-resolution-items", "flow-children": "column"},
                ...choices.map((item) => h(ui.smallButton, {key: item.value, disabled, "aria-label": item.label,
                    "aria-pressed": item.value === selected, onClick: (event) => {
                        event?.stopPropagation?.();
                        event?.preventDefault?.();
                        setSelected(item.value);
                    }}, item.label)))),
            choices.length === 0 ? h("p", null, "No resolutions available.") : null),
        h(ui.focusable, {className: "steam-ui-kit-resolution-actions", "flow-children": "row"},
            button("Add", !valid, () => send({operation: "add", width, height})),
            button("Remove", !(editor.removable ?? []).includes(selected),
                () => send({operation: "remove", width: selectedWidth, height: selectedHeight})),
            button("Refresh", false, () => send({operation: "refresh"})),
            button("Restore original list", !editor.restoreAvailable, () => send({operation: "restore"})),
            button("Add 5% steps", !editor.nativeWidth || !editor.nativeHeight,
                () => send({operation: "steps", width: editor.nativeWidth, height: editor.nativeHeight}))),
        h("p", {className: "steam-ui-kit-resolution-note"}, row.description),
        h("p", {className: "steam-ui-kit-resolution-note"}, editor.nativeWidth && editor.nativeHeight
            ? `5% steps: native ${editor.nativeWidth} × ${editor.nativeHeight}, 100% down to 50%.`
            : "Native size unavailable; add custom dimensions instead."));
};
