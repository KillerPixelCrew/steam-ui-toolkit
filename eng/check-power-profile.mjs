// Exercise the emitted Quick Access rows with inert React and bridge fixtures, never a live Steam
// session: the power-profile and assignment dropdowns, the section headers and their glyphs, the
// device controls, section layout, the power sliders and the slider echo they share.
import assert from "node:assert/strict";
import {
    createHooks,
    declarations,
    fragment,
    instantiate,
    loadAsset,
    statement,
    tick,
} from "./check-harness.mjs";

const asset = loadAsset();
// Declarations taken whole, by name, from the component host's whole fragment.
const host = (...names) => declarations(asset, "components.ts", names);
const normalizeText = instantiate({}, host("normalizeText"), "normalizeText");
// A host's Quick Access layout, as WSGM publishes it: the fixture every section check draws, so the
// checks assert the rendering of host data rather than any layout of the toolkit's own.
const hostLayout = {
    performance: [
        {
            id: "Profile scope",
            title: "Profile scope",
            icon: "profile",
            folds: false,
            kinds: ["valveProfileHeader"],
        },
        {
            id: "Power profiles",
            title: "Power profiles",
            icon: "sliders",
            folds: true,
            kinds: ["powerPreset", "powerProfile", "hybridCores", "cpuBoost"],
        },
        {
            id: "Display and frame rate",
            title: "Display and frame rate",
            icon: "timer",
            folds: true,
            kinds: ["valveOverlayLevel", "frameLimit", "vrr"],
        },
        {
            id: "Power limits",
            title: "Power limits",
            icon: "gauge",
            folds: true,
            kinds: ["powerLimit", "autoTdp"],
        },
        {
            id: "Controller",
            title: "Controller",
            icon: "controller",
            folds: true,
            kinds: ["controllerTarget"],
        },
    ],
    performanceEnd: [
        { id: "Reset", title: "", icon: "reset", folds: false, kinds: ["valveReset"] },
    ],
    quickSettings: [
        {
            id: "Display",
            title: "Display",
            icon: "display",
            folds: true,
            kinds: ["resolution", "valveRefreshRate"],
        },
        { id: "Audio", title: "Audio", icon: "audio", folds: true, kinds: ["audioFormat"] },
    ],
    quickSettingsEnd: [
        {
            id: "Charging",
            title: "Charging",
            icon: "batteryCharging",
            folds: true,
            kinds: ["charging"],
        },
        {
            id: "RGB lighting",
            title: "RGB lighting",
            icon: "colors",
            folds: true,
            kinds: ["lighting"],
        },
    ],
    hideValveFpsRows: true,
    accentLabel: "Game override",
};
// The states the host has accepted, holding the layout the marked rows read their label from.
const acceptedStates = new Map([["quickAccessLayout", hostLayout]]);
// The accent helper every row shares, over the shipped colour helper. Fixtures carry no accent unless
// a check says so.
const accentHelpers = instantiate(
    {
        normalizeText,
        acceptedStates,
        ...instantiate(
            {},
            declarations(asset, "gate-helpers.ts", ["SteamAccentColor", "steamAccentDescription"]),
            "{ steamAccentDescription }",
        ),
    },
    host("accentDescription"),
    "{ accentDescription }",
);
{
    // A marked row's description is one span in Steam's accent colour, led by the host's label, and an
    // unmarked row keeps its plain text. Nothing is drawn beside the control.
    const runtime = {
        react: { createElement: (type, props, ...children) => ({ type, props, children }) },
    };
    const marked = accentHelpers.accentDescription(runtime, true, "Ready");
    assert.equal(marked.type, "span");
    assert.equal(marked.props.style.color, "#1a9fff");
    assert.deepEqual(marked.children, ["Game override · Ready"]);
    assert.deepEqual(accentHelpers.accentDescription(runtime, true, "").children, [
        "Game override",
    ]);
    assert.equal(accentHelpers.accentDescription(runtime, false, "Ready"), "Ready");
    assert.equal(accentHelpers.accentDescription(runtime, false, ""), undefined);
    // The label is the host's: without a layout a marked row is only coloured.
    acceptedStates.delete("quickAccessLayout");
    assert.deepEqual(accentHelpers.accentDescription(runtime, true, "Ready").children, ["Ready"]);
    acceptedStates.set("quickAccessLayout", hostLayout);
}
// The host's own command sender over a fixture request, so a row's write carries the action
// generation exactly as the shipped host attaches it.
const createSender = (request) =>
    instantiate({ request, nextActionGeneration: () => 1 }, host("sendCommand"), "sendCommand");
let state;
const requests = [];
const pending = [];
// What the host refuses the next row write with, when a check sets it.
let refusal = null;
const api = instantiate(
    {
        normalizeText,
        ...accentHelpers,
        useSemanticState: (_runtime, _kind, normalize) => normalize(state),
        note: () => null,
        definitions: {
            powerProfile: { patchId: "steam-ui.power-profile", command: "setPowerProfile" },
            hybridCores: { patchId: "steam-ui.hybrid-cores", command: "setHybridCores" },
            cpuBoost: { patchId: "steam-ui.cpu-boost", command: "setCpuBoost" },
            powerPreset: {
                patchId: "steam-ui.power-preset",
                acCommand: "setAcPowerPreset",
                batteryCommand: "setBatteryPowerPreset",
            },
        },
        renderOutcomes: {},
        sendCommand: createSender((...args) => {
            requests.push(args);
            return refusal ? Promise.reject(new Error(refusal)) : Promise.resolve();
        }),
        drew: () => {},
        summarize: () => {},
    },
    host(
        "normalizePowerProfileState",
        "refusalText",
        "sendPending",
        "createChoiceControl",
        "createPowerProfileControl",
        "createHybridCoreControl",
        "normalizeCpuBoostState",
        "createCpuBoostControl",
        "normalizePowerPresetState",
        "createPowerPresetControl",
    ),
    "{ normalizePowerProfileState, createPowerProfileControl, createHybridCoreControl, normalizeCpuBoostState, createCpuBoostControl, normalizePowerPresetState, createPowerPresetControl }",
);
const options = [
    { id: "a", label: "Balanced" },
    { id: "b", label: "Balanced" },
];
const longLabel = api.normalizePowerProfileState({
    available: true,
    options: [{ id: "a", label: "x".repeat(10000) }],
    current: "a",
});
// Labels arrive whole; nothing is cut.
assert.equal(longLabel.options[0].label.length, 10000);
state = { available: true, options, current: "a", statusText: "Ready" };
// The icon fixture hands back the requested name, so an assertion can say which glyph a row asked
// for without this file having to know how an svg element is built.
// The row's pending flag is recorded; its refusal text, cleared as each write starts, is not.
const control = api.createPowerProfileControl({
    dropdown: "dropdown",
    icon: (name) => name,
    react: {
        useState: (initial) => [
            initial,
            (value) => typeof value === "boolean" && pending.push(value),
        ],
        createElement: (_type, props) => props,
    },
});
const row = control();
assert.equal(row.label, "Windows power profile");
assert.equal(row.icon, "power");
assert.equal(row.selectedOption, "a");
row.onChange({ data: "unknown" });
row.onChange({ data: "a" });
assert.equal(requests.length, 0);
row.onChange({ data: "b" });
await tick();
assert.deepEqual(requests[0], ["steam-ui.power-profile", "setPowerProfile", { target: "b" }, 1]);
assert.deepEqual(pending, [true, false]);
// A refused write shows why in the row's description, whole, and the next write clears it.
{
    const hooks = createHooks();
    const refusing = api.createPowerProfileControl({
        dropdown: "dropdown",
        icon: (name) => name,
        react: {
            useState: hooks.useState,
            createElement: (_type, props) => props,
        },
    });
    const draw = () => {
        hooks.reset();
        return refusing();
    };
    refusal = "The profile is managed by the device";
    draw().onChange({ data: "b" });
    await tick();
    assert.equal(draw().description, "The profile is managed by the device");
    assert.equal(draw().disabled, false, "a refused write is no longer pending");
    refusal = null;
    draw().onChange({ data: "b" });
    await tick();
    assert.equal(draw().description, "Ready", "the next write clears the refusal");
}
state = { ...state, current: "missing" };
assert.equal(control().selectedOption, undefined);
state = { ...state, available: false, statusText: "Readback failed" };
assert.equal(control().disabled, true);
assert.equal(control().description, "Readback failed");
// No options is nothing to choose: a processor with one kind of core publishes exactly that, and
// neither dropdown may stand there empty and disabled.
{
    const previous = state;
    state = { available: false, options: [], current: "", statusText: "One kind of core" };
    const reactFixture = {
        dropdown: "dropdown",
        icon: (name) => name,
        react: {
            useState: () => [false, () => {}],
            createElement: (_type, props) => props,
        },
    };
    assert.equal(control(), null);
    assert.equal(api.createHybridCoreControl(reactFixture)(), null);
    state = { ...previous, available: true };
    assert.equal(api.createHybridCoreControl(reactFixture)().label, "Processor cores");
    // The boost row is the same dropdown with the host's mark in its description.
    assert.equal(api.createCpuBoostControl(reactFixture)().label, "CPU boost mode");
    assert.equal(api.createCpuBoostControl(reactFixture)().icon, "turbo");
    assert.equal(api.createCpuBoostControl(reactFixture)().description, state.statusText);
    state = { ...state, accent: true };
    assert.deepEqual(
        api.createCpuBoostControl(reactFixture)().description,
        accentHelpers.accentDescription(reactFixture, true, state.statusText),
    );
    assert.equal(api.normalizeCpuBoostState({ ...state, accent: "yes" }).accent, false);
    state = { ...state, options: [] };
    assert.equal(api.createCpuBoostControl(reactFixture)(), null);
    state = previous;
}
for (const badOptions of [
    [...options, options[0]],
    [{ id: 123, label: "Bad" }],
    [{ id: "a", label: "" }],
]) {
    assert.equal(api.normalizePowerProfileState({ ...state, options: badOptions }), null);
}
// Any number of options is a valid list.
assert.equal(
    api.normalizePowerProfileState({
        ...state,
        options: Array.from({ length: 65 }, (_, i) => ({ id: String(i), label: "x" })),
    }).options.length,
    65,
);
// An unselectable option is listed only while it is the current value, and never sent.
{
    const previous = state;
    state = {
        available: true,
        options: [...options, { id: "x", label: "Unknown", selectable: false }],
        current: "a",
        statusText: "",
    };
    assert.ok(
        !control().rgOptions.some((option) => option.data === "x"),
        "not listed while not current",
    );
    state = { ...state, current: "x" };
    assert.equal(control().selectedOption, "x");
    assert.ok(
        control().rgOptions.some((option) => option.data === "x"),
        "listed while current",
    );
    state = { ...state, current: "a" };
    const sent = requests.length;
    control().onChange({ data: "x" });
    assert.equal(requests.length, sent, "an unselectable option is never sent");
    state = previous;
}
assert.match(asset, /\["powerProfile", "steam-ui-power-profile", powerProfileControl, "perf"\]/);
const presetControl = api.createPowerPresetControl({
    dropdown: "dropdown",
    labelField: "labelField",
    icon: (name) => name,
    react: {
        Fragment: "fragment",
        useState: () => [false, () => {}],
        createElement: (type, props, ...children) => ({ type, props, children }),
    },
});
state = {
    available: true,
    options,
    current: "Custom",
    ac: "a",
    battery: "b",
    scope: "Global",
    unsetLabel: "Manual selection",
};
let rows = presetControl().children.filter((child) => child?.type === "dropdown");
assert.deepEqual(
    rows.map((row) => row.props.label),
    ["When plugged in", "On battery"],
);
assert.deepEqual(
    rows.map((row) => row.props.icon),
    ["plug", "battery"],
);
// The profile in effect is a Valve label/value row, not a bare div: it carries a label, a glyph and
// the scope and status together as its description.
{
    const active = presetControl().children.find((child) => child?.type === "labelField");
    assert.ok(active, "the active profile must render as a Valve field");
    assert.equal(active.props.label, "Active profile");
    assert.equal(active.props.icon, "check");
    assert.equal(active.props.description, "Global");
    assert.deepEqual(active.children, ["Custom"]);
    assert.ok(
        !presetControl().children.some((child) => child?.type === "div"),
        "no unformatted div may survive beside Steam's own rows",
    );
}
// A refusal must stay visible when there is no active profile to carry it. Readback can fail with
// no current profile at all, which left two disabled dropdowns and no reason for either.
{
    const failed = { ...state, available: false, current: "", statusText: "Readback failed" };
    const previous = state;
    state = failed;
    const tree = presetControl();
    assert.ok(
        !tree.children.some((child) => child?.type === "labelField"),
        "no active profile means no active-profile row",
    );
    assert.ok(
        JSON.stringify(tree).includes("Readback failed"),
        "the reason a profile could not be read must reach the panel anyway",
    );
    // Same when the client could not resolve LabelField and the row is gone for a different reason.
    const withoutField = api.createPowerPresetControl({
        dropdown: "dropdown",
        icon: (name) => name,
        react: {
            Fragment: "fragment",
            useState: () => [false, () => {}],
            createElement: (type, props, ...children) => ({ type, props, children }),
        },
    });
    state = { ...previous, available: false, statusText: "Readback failed" };
    assert.ok(
        JSON.stringify(withoutField()).includes("Readback failed"),
        "an unresolved LabelField must not take the reason with it",
    );
    state = previous;
}
assert.deepEqual(
    rows.map((row) => row.props.selectedOption),
    ["a", "b"],
);
rows[0].props.onChange({ data: "b" });
await tick();
assert.deepEqual(requests.at(-1), [
    "steam-ui.power-preset",
    "setAcPowerPreset",
    { target: "b" },
    1,
]);
rows[1].props.onChange({ data: "" });
await tick();
assert.deepEqual(requests.at(-1), [
    "steam-ui.power-preset",
    "setBatteryPowerPreset",
    { target: null },
    1,
]);
const before = requests.length;
rows[0].props.onChange({ data: "missing" });
assert.equal(requests.length, before);
state = { ...state, available: false };
rows = presetControl().children.filter((child) => child?.type === "dropdown");
assert.ok(rows.every((row) => row.props.disabled));
rows[0].props.onChange({ data: "b" });
assert.equal(requests.length, before);
assert.equal(api.normalizePowerPresetState({ ...state, ac: "missing" }), null);
assert.ok(
    api.normalizePowerPresetState({
        ...state,
        options: [...options, { id: "none", label: "None" }],
    }),
);
// An option the host marks unselectable is listed only in the dropdown whose current value it is, and
// never sent.
for (const ac of [true, false]) {
    state = {
        ...state,
        available: true,
        options: [...options, { id: "custom", label: "Custom", selectable: false }],
        ac: ac ? "custom" : "a",
        battery: ac ? "b" : "custom",
    };
    rows = presetControl().children.filter((child) => child?.type === "dropdown");
    assert.deepEqual(
        rows.map((row) => row.props.selectedOption),
        ac ? ["custom", "b"] : ["a", "custom"],
    );
    assert.ok(rows[ac ? 0 : 1].props.rgOptions.some((option) => option.data === "custom"));
    assert.ok(!rows[ac ? 1 : 0].props.rgOptions.some((option) => option.data === "custom"));
    rows[ac ? 0 : 1].props.onChange({ data: "custom" });
    assert.equal(requests.length, before);
}
// No assignment holding it is not a malformed state: the row still draws, and lists it nowhere.
state = { ...state, ac: "a", battery: "b" };
rows = presetControl().children.filter((child) => child?.type === "dropdown");
assert.equal(rows.length, 2);
assert.ok(rows.every((row) => !row.props.rgOptions.some((option) => option.data === "custom")));
assert.ok(
    !host("normalizePowerPresetState", "createPowerPresetControl").includes('"custom"'),
    "the toolkit names no preset id of its own",
);
state = { ...state, options: [], ac: "", battery: "" };
assert.equal(presetControl(), null);
assert.match(asset, /\["powerPreset", "steam-ui-power-preset", powerPresetControl, "perf"\]/);
console.log("Power-profile and assignment emitted dropdown checks passed.");

// The real section composer, so the sections are checked against the titles, glyphs and summaries
// the panel hands the kit for the host's layout rather than a stand-in. The kit's group is a fixture
// that keeps what it was given: the check is about the panel's decisions, not the kit's markup,
// which check-ui-kit.mjs covers.
const groupFixture = (_ui, props, ...children) => ({ type: "group", props, children });
const summaries = {};
const folded = new Set();
const foldRequests = [];
const sectionFixtures = {
    summaries,
    renderSteamUiGroup: groupFixture,
    isFolded: (_folds, id) => folded.has(id),
    setFolded: (id, fold) => foldRequests.push([id, fold]),
};
const { hostSection, untitledSection, normalizeQuickAccessLayout } = instantiate(
    { normalizeText, ...sectionFixtures },
    host(
        "normalizeQuickAccessSections",
        "normalizeQuickAccessLayout",
        "untitledSection",
        "sectionIcon",
        "sectionSummary",
        "hostSection",
        "controlRows",
        "describe",
    ),
    "{ hostSection, untitledSection, normalizeQuickAccessLayout }",
);
const layoutState = normalizeQuickAccessLayout(hostLayout);
assert.deepEqual(
    layoutState.performance.map((section) => section.id),
    ["Profile scope", "Power profiles", "Display and frame rate", "Power limits", "Controller"],
);
assert.equal(layoutState.hideValveFpsRows, true);
assert.equal(layoutState.accentLabel, "Game override");
// A section without an id or a kind list is skipped, not the whole layout.
assert.deepEqual(
    normalizeQuickAccessLayout({
        ...hostLayout,
        quickSettings: [{ title: "No id", kinds: [] }, ...hostLayout.quickSettings],
    }).quickSettings.map((section) => section.id),
    ["Display", "Audio"],
);
assert.equal(normalizeQuickAccessLayout(null), null);

// Every placement in this host layout names a drawn glyph and has its own shape. The toolkit's
// palette also serves other host layouts: this consumer need not place every available glyph.
//
// The scan reads the emitted asset, so it is textual and has limits worth stating: it sees every
// call site that spells its glyph out, the section glyphs come from the host's layout fixture, and
// the four names the preset and power-limit tables pass by variable are listed here by hand. A
// placement built from a computed name would be invisible to it; the renderer's behavior checks
// below cover the dynamic rows separately.
{
    const drawings = fragment(asset, "icons.ts");
    // Comments are emitted verbatim, and a commented-out call site is not a placement.
    const code = asset.replace(/^[ \t]*\/\/.*$/gmu, "");
    const used = [
        ...[...code.matchAll(/\bicon\(\s*["']([A-Za-z]+)["']/gu)].map((match) => match[1]),
        ...[
            ...hostLayout.performance,
            ...hostLayout.performanceEnd,
            ...hostLayout.quickSettings,
            ...hostLayout.quickSettingsEnd,
        ].map((section) => section.icon),
        "plug",
        "battery",
        "bolt",
        "boost",
    ];
    const repeated = [...new Set(used.filter((name, at) => used.indexOf(name) !== at))];
    assert.deepEqual(repeated, [], `glyphs used for more than one control: ${repeated.join(", ")}`);
    // Declared glyphs, read off the table's own keys rather than a number kept in step by hand.
    // Indentation differs between the two compositions - the prelude comes out of tsc as-is, a
    // consumer's asset is run through Prettier - so the key is anchored to its line, not a depth.
    const declared = [...drawings.matchAll(/^[ \t]*([A-Za-z][A-Za-z0-9]*): \[/gmu)].map(
        (match) => match[1],
    );
    assert.ok(declared.length >= 29, `only ${declared.length} glyphs were found in the table`);
    assert.deepEqual(
        used.filter((name) => !declared.includes(name)),
        [],
        "every placement must name a drawn glyph",
    );
}

// Optional native fields must not take down the remaining device controls.
const deviceState = {
    chargeLimit: { available: true, observed: 80, minimum: 60, maximum: 100, step: 1 },
    lightingBrightness: { available: true, observed: 100, minimum: 0, maximum: 100, step: 1 },
    lightingZones: [{ available: true, id: "buttons", label: "Buttons", observedColor: 0xffffff }],
};
const deviceSections = [...layoutState.quickSettings, ...layoutState.quickSettingsEnd];
const createDeviceControl = instantiate(
    {
        normalizeText,
        acceptedStates,
        untitledSection,
        ...accentHelpers,
        useSemanticState: () => deviceState,
        normalizeDeviceControlsState: (value) => value,
        normalizePanelFoldsState: (value) => value,
        definitions: { deviceControls: {}, panelFolds: {} },
        sendCommand: createSender(() => {
            throw new Error("Rendering must not dispatch hardware writes");
        }),
        useTrailingCommit: () => () => {},
        useEchoedValue: (_runtime, value) => ({ value }),
        note: () => null,
        renderOutcomes: {},
        isBusy: () => false,
        localizeOr: (_runtime, _token, fallback) => fallback,
        hostSection,
        drew: () => {},
        summarize: (kind, text) => {
            summaries[kind] = text;
        },
    },
    host(
        "rgbToHsv",
        "hsvToRgb",
        "rgbCss",
        "normalizePowerLimitRange",
        "normalizePowerLimitState",
        "createPowerLimitControl",
        "createDeviceControlsControl",
    ),
    "createDeviceControlsControl",
);
for (const toggle of [undefined, "toggle"]) {
    for (const expanded of [false, true]) {
        const render = createDeviceControl({
            toggle,
            row: "row",
            section: "section",
            slider: "slider",
            dropdown: "dropdown",
            icon: (glyph, size) => ({ glyph, size }),
            react: {
                Fragment: "fragment",
                useState: (initial) => [
                    typeof initial === "boolean" ? expanded : initial,
                    () => {},
                ],
                createElement: (type, props, ...children) => {
                    assert.ok(type, "An unresolved native component must never be rendered");
                    return { type, props, children };
                },
            },
        });
        const tree = render({ sections: deviceSections });
        assert.deepEqual(
            tree.children.map((section) => section.props.title),
            ["Charging", "RGB lighting"],
        );
        assert.deepEqual(
            tree.children.map((section) => section.props.icon.glyph),
            ["batteryCharging", "colors"],
        );
        // Each section folds, under its own title, and its heading reports what its rows hold.
        assert.deepEqual(
            tree.children.map((section) => section.props.detail),
            ["Limit 80%", "100% · Buttons"],
        );
        assert.ok(tree.children.every((section) => typeof section.props.onToggle === "function"));
        tree.children[0].props.onToggle();
        assert.deepEqual(foldRequests.pop(), ["Charging", true]);
        const fields = tree.children.flatMap((section) =>
            section.children.map((row) => row.children[0]),
        );
        assert.ok(
            fields.some(
                (field) =>
                    field.props.label === "Battery charge limit" &&
                    field.props.icon.glyph === "percent",
            ),
        );
        assert.ok(
            fields.some(
                (field) =>
                    field.props.label === "Lighting brightness" &&
                    field.props.icon.glyph === "bulb",
            ),
        );
        // A glyph on a header must not reappear on a row inside it, and no two rows may share one:
        // shape is how this panel is navigated before the label is read.
        const glyphs = tree.children
            .flatMap((section) => [
                section.props.icon.glyph,
                ...section.children.map((row) => row.children[0].props.icon?.glyph),
            ])
            .filter(Boolean);
        assert.equal(
            new Set(glyphs).size,
            glyphs.length,
            `device glyphs repeat: ${glyphs.join(", ")}`,
        );
        assert.equal(
            fields.some((field) => field.props.label === "Edit color"),
            !!toggle,
        );
        assert.equal(
            fields.some((field) => field.props.label === "Lighting zone"),
            !!toggle && expanded,
        );
        // Without a layout each group is untitled; a layout that names neither kind draws neither.
        const untitled = render({ sections: null });
        assert.deepEqual(
            untitled.children.map((section) => section.props.title),
            [undefined, undefined],
        );
        assert.deepEqual(render({ sections: [] }).children, [null, null]);
        if (toggle) {
            // The Edit color toggle names the marked zones after the host's label, in plain text.
            deviceState.lightingZones = [{ ...deviceState.lightingZones[0], accent: true }];
            const edit = render({ sections: deviceSections })
                .children[1].children.map((row) => row.children[0])
                .find((field) => field.props.label === "Edit color");
            assert.equal(edit.props.description, "Game override · Buttons");
            deviceState.lightingZones = [{ ...deviceState.lightingZones[0], accent: false }];
        }
    }
}
// A range with no reading still draws, at its minimum with its number hidden.
{
    const previous = deviceState.chargeLimit;
    deviceState.chargeLimit = { ...previous, observed: null, desired: null };
    const render = createDeviceControl({
        row: "row",
        section: "section",
        slider: "slider",
        dropdown: "dropdown",
        icon: (glyph, size) => ({ glyph, size }),
        react: {
            Fragment: "fragment",
            useState: (initial) => [initial, () => {}],
            createElement: (type, props, ...children) => ({ type, props, children }),
        },
    });
    const charge = render({ sections: deviceSections }).children[0].children[0].children[0].props;
    assert.equal(charge.label, "Battery charge limit");
    assert.equal(charge.value, 60);
    assert.equal(charge.showValue, false);
    deviceState.chargeLimit = previous;
    // The normalizer keeps the slider for an off-step or out-of-range reading, clamped into the range.
    const normalizeDeviceRange = instantiate(
        { normalizeText },
        host("clampReading", "normalizeDeviceRange"),
        "normalizeDeviceRange",
    );
    const range = {
        available: true,
        minimum: 60,
        maximum: 100,
        step: 5,
        desired: null,
        observed: 83,
    };
    assert.equal(normalizeDeviceRange(range).observed, 83, "an off-step reading is shown as is");
    assert.equal(normalizeDeviceRange({ ...range, observed: 120 }).observed, 100);
    assert.equal(normalizeDeviceRange({ ...range, observed: null }).observed, null);
    assert.equal(
        normalizeDeviceRange({ ...range, step: 0 }),
        null,
        "an invalid descriptor draws nothing",
    );
}
console.log("Device controls retain charging and brightness without the optional color toggle.");

// A section whose rows all drew nothing leaves layout but stays mounted, so its rows keep their
// subscriptions and can bring it back. Valve's rows report nothing and keep theirs shown.
{
    const controlNames = [
        "valveProfileHeaderControl",
        "valveProfileToggleControl",
        "valveOverlayLevelControl",
        "frameLimitControl",
        "powerProfileControl",
        "hybridCoreControl",
        "cpuBoostControl",
        "powerPresetControl",
        "vrrControl",
        "powerLimitControl",
        "autoTdpControl",
        "resolutionControl",
        "audioFormatControl",
        "valveRefreshRateControl",
        "controllerControl",
        "valveResetControl",
    ];
    const registrations = new Map();
    const drawnKinds = new Set();
    const { appendControls, useRows } = instantiate(
        {
            normalizeText,
            ...sectionFixtures,
            registrations,
            drawnKinds,
            appendDiagnostics: {},
            withNativeRowsHidden: (_runtime, tree) => ({ filtered: tree }),
            steamUiKitStyle: () => ({ type: "style" }),
            deviceControlsControl: undefined,
        },
        host(
            "normalizeQuickAccessSections",
            "normalizeQuickAccessLayout",
            "untitledSection",
            "sectionIcon",
            "sectionSummary",
            "hostSection",
            "controlRows",
            "describe",
            "appendControls",
        ),
        "{ appendControls, useRows: (rows) => { controlRows = rows; } }",
    );
    // The row table resolveControls builds once the controls resolve: its whole assignment, read from
    // the asset with each control standing in as its own name.
    const table = statement(host("resolveControls"), "controlRows = [");
    useRows(
        instantiate(
            Object.fromEntries(controlNames.map((name) => [name, name])),
            "",
            table.slice("controlRows = ".length, -1),
        ),
    );
    const runtime = {
        section: "section",
        row: "row",
        icon: () => null,
        react: {
            Fragment: "fragment",
            isValidElement: () => false,
            createElement: (type, props, ...children) => ({ type, props, children }),
        },
    };
    // The panel is the kit's stylesheet, then Valve's tree under the battery class, then the
    // groups, the host's settings sections and the closing groups; Quick Settings leads with its
    // groups and wraps Valve's sections as blocks.
    const groupsOf = (placement, layout = layoutState) => {
        const tree = appendControls(runtime, "native", placement, null, layout);
        assert.equal(tree.children[0].type, "style");
        if (placement === "perf") {
            assert.equal(tree.children[1].props.className, "steam-ui-kit-battery");
            return [...tree.children[2].children, ...tree.children.slice(4)];
        }
        const valve = tree.children.findIndex(
            (child) => child?.props?.className === "steam-ui-kit-valve",
        );
        assert.ok(valve > 0);
        return tree.children.slice(1, valve);
    };
    const layout = (placement) =>
        Object.fromEntries(
            groupsOf(placement).map((group) => {
                assert.equal(group.type, "group");
                assert.ok(group.children.length > 0, "a hidden section keeps its rows mounted");
                return [
                    group.props.title ?? group.props.key,
                    group.props.hidden ? "none" : "contents",
                ];
            }),
        );
    for (const kind of [
        "valveProfileHeader",
        "powerProfile",
        "hybridCores",
        "powerLimit",
        "controllerTarget",
        "valveReset",
        "resolution",
    ])
        registrations.set(kind, kind);
    drawnKinds.add("powerProfile");
    assert.deepEqual(layout("perf"), {
        "Profile scope": "contents",
        "Power profiles": "contents",
        "Power limits": "none",
        Controller: "none",
        Reset: "contents",
    });
    drawnKinds.add("powerLimit");
    assert.equal(layout("perf")["Power limits"], "contents");
    drawnKinds.delete("powerProfile");
    assert.equal(layout("perf")["Power profiles"], "none", "no drawn row, no section");
    assert.deepEqual(layout("quickSettings"), { Display: "none" });
    registrations.set("valveRefreshRate", "valveRefreshRate");
    assert.deepEqual(layout("quickSettings"), { Display: "contents" });
    registrations.set("audioFormat", "audioFormat");
    assert.deepEqual(layout("quickSettings"), { Display: "contents", Audio: "none" });
    drawnKinds.add("audioFormat");
    assert.deepEqual(layout("quickSettings"), { Display: "contents", Audio: "contents" });
    // Profile scope stays open, Reset has no heading, and every other group folds under its title
    // with its glyph and its rows' summary on the heading.
    summaries.powerProfile = "Balanced";
    summaries.hybridCores = "Automatic";
    folded.add("Power profiles");
    const perf = Object.fromEntries(
        groupsOf("perf").map((group) => [group.props.title ?? group.props.key, group.props]),
    );
    assert.equal(perf["Profile scope"].onToggle, undefined);
    assert.equal(perf.Reset.title, undefined);
    assert.equal(perf["Power profiles"].collapsed, true);
    assert.equal(perf["Power profiles"].detail, "Balanced · Automatic");
    assert.equal(perf["Power profiles"].icon, null, "the fixture runtime draws no glyph");
    perf["Power profiles"].onToggle();
    assert.deepEqual(foldRequests.pop(), ["Power profiles", false]);
    assert.equal(perf.Controller.collapsed, false);
    folded.clear();
    // Steam's FPS rows are hidden only when the layout asks.
    assert.deepEqual(
        appendControls(runtime, "native", "perf", null, layoutState).children[1].children,
        [{ filtered: "native" }],
    );
    assert.deepEqual(
        appendControls(runtime, "native", "perf", null, { ...layoutState, hideValveFpsRows: false })
            .children[1].children,
        ["native"],
    );
    // Without a layout every row of a tab draws in one untitled group, and Valve's tree is untouched.
    const bare = appendControls(runtime, "native", "perf", null, null);
    assert.deepEqual(bare.children[1].children, ["native"]);
    const [group] = bare.children[2].children;
    assert.equal(group.props.title, undefined);
    // Six registered Performance kinds, Valve's profile header drawing two rows.
    assert.equal(group.children.length, 7, "every registered Performance row");
    // A kind no section names is not drawn while a layout is published.
    const narrow = {
        ...layoutState,
        performance: layoutState.performance.slice(0, 1),
        performanceEnd: [],
    };
    assert.deepEqual(
        groupsOf("perf", narrow).map((section) => section.props.key),
        ["Profile scope"],
    );
}
console.log("Host sections leave layout while every row under them draws nothing.");

// Hardware observations, not Steam's saved TDP setting, drive both power sliders.
{
    const hooks = createHooks();
    const writes = [];
    const replies = [];
    const range = (watts) => ({
        available: true,
        minimumWatts: 8,
        maximumWatts: 37,
        stepWatts: 1,
        observedWatts: watts,
        progress: "",
        statusText: "",
    });
    let powerState = { sustained: range(23), boost: range(30) };
    const runtime = {
        slider: "slider",
        row: "row",
        icon: (name) => name,
        react: {
            Fragment: "fragment",
            useState: hooks.useState,
            useRef: hooks.useRef,
            createElement: (type, props, ...children) => ({ type, props, children }),
        },
    };
    const powerApi = instantiate(
        {
            normalizeText,
            ...accentHelpers,
            useSemanticState: (_runtime, _kind, normalize) => normalize(powerState),
            definitions: {
                powerLimit: {
                    patchId: "steam-ui.power-limit",
                    primaryCommand: "setPrimaryLimit",
                    boostCommand: "setBoostLimit",
                },
            },
            sendCommand: createSender((...args) => {
                writes.push(args);
                return new Promise((resolve, reject) => replies.push({ resolve, reject }));
            }),
            isBusy: (progress) => ["queued", "applying", "replacing"].includes(progress),
            note: () => null,
            drew: () => {},
            summarize: () => {},
        },
        host(
            "useEchoedValue",
            "clampReading",
            "normalizePowerLimitRange",
            "normalizePowerLimitState",
            "createPowerLimitControl",
        ),
        "{ createPowerLimitControl, normalizePowerLimitState }",
    );
    const control = powerApi.createPowerLimitControl(runtime);
    const render = () => {
        hooks.reset();
        return control()?.children.map((row) => row.children[0].props) ?? [];
    };
    let sliders = render();
    assert.deepEqual(
        sliders.map((slider) => slider.label),
        ["Sustained power (PL1)", "Boost power (PL2)"],
    );
    assert.deepEqual(
        sliders.map((slider) => slider.icon),
        ["bolt", "boost"],
    );
    // SliderField would otherwise place the glyph beside the track rather than the label.
    assert.ok(sliders.every((slider) => slider.iconLocation === "front"));
    assert.deepEqual(
        sliders.map((slider) => slider.value),
        [23, 30],
    );
    assert.equal(writes.length, 0);
    sliders[0].onChange(20);
    assert.equal(render()[0].value, 20);
    assert.equal(writes.length, 0, "dragging does not issue hardware writes");
    powerState = { sustained: range(37), boost: range(37) };
    render();
    sliders = render();
    assert.deepEqual(
        sliders.map((slider) => slider.value),
        [37, 37],
        "profile readback supersedes local drag echo",
    );
    assert.equal(writes.length, 0, "profile publication never echoes a hardware command");
    sliders[1].onChangeComplete(28);
    sliders[0].onChangeComplete(20);
    assert.equal(writes.length, 1, "both controls serialize against the pending command");
    assert.deepEqual(writes[0], ["steam-ui.power-limit", "setBoostLimit", { watts: 28 }, 1]);
    assert.ok(render().every((slider) => slider.disabled));
    replies.shift().resolve();
    await tick();
    powerState.boost = range(28);
    render();
    sliders = render();
    assert.deepEqual(
        sliders.map((slider) => slider.value),
        [37, 28],
    );
    sliders[0].onChangeComplete(20);
    assert.deepEqual(writes[1], ["steam-ui.power-limit", "setPrimaryLimit", { watts: 20 }, 1]);
    replies.shift().reject(new Error("Hardware outcome uncertain"));
    await tick();
    sliders = render();
    assert.equal(sliders[0].value, 37);
    assert.match(sliders[0].description, /uncertain/);
    render();
    assert.equal(writes.length, 2, "failure and re-render do not automatically retry");
    for (const invalid of [0, 38, 20.5, "20", null]) sliders[0].onChangeComplete(invalid);
    assert.equal(writes.length, 2);
    powerState.sustained = { ...range(23), progress: "applying" };
    assert.ok(render().every((slider) => slider.disabled));
    powerState.sustained = { ...range(23), available: false };
    assert.equal(render()[0].disabled, true);
    // No range row is gated on a reading.
    powerState = { sustained: range(23), boost: { ...range(28), observedWatts: null } };
    sliders = render();
    assert.equal(sliders.length, 2, "an unknown boost reading keeps its slider");
    assert.equal(sliders[1].value, 8, "with no reading the slider sits at its minimum");
    assert.equal(sliders[1].showValue, false, "and shows no number");
    powerState.sustained = { ...range(23), stepWatts: 2 };
    assert.equal(render().length, 2, "an off-step reading keeps the slider");
    assert.equal(render()[0].value, 23, "and is shown as is");
    // The device's range is the authority: the toolkit has no ceiling of its own.
    powerState = { sustained: { ...range(230), maximumWatts: 250 }, boost: range(30) };
    assert.equal(render()[0].value, 230);
    assert.ok(!asset.includes("steamos_tdp_limit"), "no saved Steam TDP setting can be replayed");
}
console.log(
    "Power sliders: independent PL1/PL2 edits, profile readback, pending commands and refusal checks passed.",
);

// The slider echo on its own: readback and acknowledgments never become a user's write.
{
    const hooks = createHooks();
    const useEcho = instantiate({}, host("useEchoedValue"), "useEchoedValue");
    const runtime = { react: { useState: hooks.useState } };
    const render = (observed) => {
        hooks.reset();
        return useEcho(runtime, observed);
    };
    const writes = [];
    const commit = (value) => writes.push(value);

    let slider = render(30);
    slider.onChange(30);
    slider.onChangeComplete(30, commit);
    assert.deepEqual(writes, [], "a programmatic refresh must not dispatch a write");
    slider = render(17);
    slider.onChangeComplete(17, commit);
    assert.equal(render(17).value, 17);
    assert.deepEqual(writes, [], "AutoTDP readback must not become manual intent");
    slider.onChange(19);
    assert.equal(render(17).value, 19);
    slider.onChangeComplete(19, commit);
    assert.deepEqual(writes, [19]);
    slider = render(19);
    slider.onChangeComplete(19, commit);
    slider.onChangeComplete(Number.NaN, commit);
    slider.onChangeComplete(Number.POSITIVE_INFINITY, commit);
    assert.deepEqual(writes, [19], "acknowledgments and invalid values must not repeat a write");
    console.log("Slider readback stays separate from user writes.");
}
