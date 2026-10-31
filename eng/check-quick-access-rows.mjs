// Exercise the emitted Quick Access component host with inert React and bridge fixtures, never a live
// Steam session: the host's rows drawn from SteamSettingsRow descriptors (fields, glyphs, label
// tokens, unselectable choices, refusals through drafts), their placement by the host's layout and
// the folded headings' summaries, the titled groups no layout names, Valve's components gated on the
// performance publication, the frame-limit row, the lighting editor and the slider echo they share.
import assert from "node:assert/strict";
import { declarations, fragment, instantiate, loadAsset, statement, tick } from "./check-harness.mjs";

const asset = loadAsset();
// Declarations taken whole, by name, from the component host's whole fragment.
const host = (...names) => declarations(asset, "components.ts", names);
const settings = (...names) => declarations(asset, "settings.ts", names);
const helpers = (...names) => declarations(asset, "gate-helpers.ts", names);

// Hook slots that persist across renders, with React's functional updates and effects that run when
// their dependencies change. Nothing reconciles: a render is a call.
const createHooks = () => {
    const slots = [];
    let cursor = 0;
    return {
        useState(initial) {
            const index = cursor++;
            if (!(index in slots)) slots[index] = typeof initial === "function" ? initial() : initial;
            return [
                slots[index],
                (value) => {
                    slots[index] = typeof value === "function" ? value(slots[index]) : value;
                },
            ];
        },
        useRef(initial) {
            const index = cursor++;
            if (!(index in slots)) slots[index] = { current: initial };
            return slots[index];
        },
        useEffect(effect, deps) {
            const index = cursor++;
            const previous = slots[index];
            if (
                !previous ||
                !deps ||
                deps.length !== previous.length ||
                deps.some((dep, at) => dep !== previous[at])
            ) {
                slots[index] = deps ?? [];
                effect();
            }
        },
        reset() {
            cursor = 0;
        },
    };
};
const createElement = (type, props, ...children) => ({ type, props: props ?? {}, children });

// A host's Quick Access layout, as WSGM publishes it: the fixture every placement check draws, so the
// checks assert the rendering of host data rather than any layout of the toolkit's own.
const hostLayout = {
    performance: [
        {
            id: "Profile scope",
            title: "Profile scope",
            icon: "profile",
            folds: false,
            kinds: ["valveProfileHeader", "valveOverlayLevel"],
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
            kinds: ["frameLimit", "vrr"],
        },
        {
            id: "Power limits",
            title: "Power limits",
            icon: "gauge",
            folds: true,
            kinds: ["powerLimit", "autoTdp"],
        },
    ],
    performanceEnd: [{ id: "Reset", title: "", icon: "reset", folds: false, kinds: ["valveReset"] }],
    quickSettings: [
        {
            id: "Display",
            title: "Display",
            icon: "display",
            folds: true,
            kinds: ["resolution", "valveRefreshRate"],
        },
    ],
    quickSettingsEnd: [
        {
            id: "RGB lighting",
            title: "RGB lighting",
            icon: "colors",
            folds: true,
            kinds: ["lighting", "lightingZones"],
        },
    ],
    hideValveFpsRows: true,
    accentLabel: "Game override",
};

const normalizeText = instantiate({}, host("normalizeText"), "normalizeText");
const acceptedStates = new Map();
const renderOutcomes = {};
const summaries = {};
const drawnKinds = new Set();
const noteFixture = (kind, reason) => {
    renderOutcomes[kind] = reason;
    return null;
};

// The real layout normalizer and rows normalizer.
const { normalizeQuickAccessLayout } = instantiate(
    { normalizeText },
    host("normalizeQuickAccessSections", "normalizeQuickAccessLayout"),
    "{ normalizeQuickAccessLayout }",
);
const rowsApi = instantiate(
    { normalizeText, acceptedStates },
    host("rowsOf", "normalizeRowsState", "rowsOfSection", "rowsSummary", "publishedSection"),
    "{ normalizeRowsState, rowsOfSection, rowsSummary, publishedSection }",
);
const layoutState = normalizeQuickAccessLayout(hostLayout);
assert.deepEqual(
    layoutState.performance.map((section) => section.id),
    ["Profile scope", "Power profiles", "Display and frame rate", "Power limits"],
);
assert.equal(layoutState.accentLabel, "Game override");
// A section without an id or a kind list is skipped, not the whole layout.
assert.deepEqual(
    normalizeQuickAccessLayout({
        ...hostLayout,
        quickSettings: [{ title: "No id", kinds: [] }, ...hostLayout.quickSettings],
    }).quickSettings.map((section) => section.id),
    ["Display"],
);
assert.equal(normalizeQuickAccessLayout(null), null);

// The rows publication: a malformed group or row costs itself, never the rest.
{
    assert.equal(rowsApi.normalizeRowsState(null), null);
    assert.equal(rowsApi.normalizeRowsState({ sections: [] }), null, "a revision is required");
    const state = rowsApi.normalizeRowsState({
        revision: 3,
        sections: [
            { id: "", rows: [] },
            { id: "powerProfile", rows: [{ key: "a", kind: "choice" }, { kind: "choice" }, null] },
            { id: "powerProfile", rows: [{ key: "dup", kind: "choice" }] },
            {
                id: "settings.gpu",
                title: "Graphics",
                glyph: "M0 0h24v24H0Z",
                rows: [],
                groups: [{ id: "adapter", title: "Adapter", rows: [{ key: "g", kind: "boolean" }] }, 7],
            },
        ],
    });
    assert.equal(state.revision, 3);
    assert.deepEqual(
        state.sections.map((section) => section.id),
        ["powerProfile", "settings.gpu"],
    );
    assert.deepEqual(
        state.sections[0].rows.map((row) => row.key),
        ["a"],
    );
    assert.deepEqual(
        rowsApi.rowsOfSection(state.sections[1]).map((row) => row.key),
        ["g"],
    );
    assert.equal(state.sections[1].groups[0].title, "Adapter");
    // Labels and values arrive whole; nothing is cut.
    const long = rowsApi.normalizeRowsState({
        revision: 1,
        sections: [{ id: "x", rows: [{ key: "k", kind: "note", label: "x".repeat(10000) }] }],
    });
    assert.equal(long.sections[0].rows[0].label.length, 10000);
}
console.log("Quick Access rows: the publication is validated one group and one row at a time.");

// The settings renderer with Quick Access's fields: glyphs by name, Steam's string for a label token,
// unselectable choices listed only while current and never sent.
const render = instantiate(
    {
        ...instantiate(
            {},
            helpers("SteamAccentColor", "steamAccentDescription", "textOf"),
            "{ steamAccentDescription, textOf }",
        ),
    },
    settings(
        "steamSettingDescription",
        "steamSettingLabel",
        "steamSettingIcon",
        "confirmSteamSetting",
        "renderSteamSettingRow",
        "steamSettingPublished",
        "useSteamSettingDrafts",
    ),
    "{ renderSteamSettingRow, useSteamSettingDrafts }",
);
const localizedTokens = { "#QuickAccess_Tab_Perf_EnableVRR": "Variable Refresh Rate (localized)" };
const qamUi = {
    react: { createElement, Fragment: "fragment" },
    toggleField: "toggle",
    dropdown: "dropdown",
    sliderField: "slider",
    textField: "text",
    valueField: "value",
    smallButton: "small",
    dialogButton: "button",
    // The fixture glyph hands back the requested name, and draws nothing for an unknown one.
    icon: (name) => (name === "power" || name === "pulse" || name === "bolt" ? `glyph:${name}` : null),
    // Steam's localizer answers an element wrapping its string, and hands an unknown token back.
    localize: (token) => ({ props: { children: localizedTokens[token] ?? token } }),
};
{
    const sent = [];
    const change = (row, value) => sent.push([row.key, value]);
    const choice = render.renderSteamSettingRow(
        qamUi,
        {
            key: "power.scheme",
            kind: "choice",
            label: "Windows power profile",
            icon: "power",
            text: "custom",
            choices: [
                { value: "balanced", label: "Balanced", selectable: true },
                { value: "custom", label: "Custom", selectable: false },
                { value: "high", label: "High performance" },
            ],
        },
        undefined,
        change,
        () => {},
    );
    assert.equal(choice.type, "dropdown");
    assert.equal(choice.props.icon, "glyph:power");
    assert.equal(choice.props.label, "Windows power profile");
    // The current unselectable value is listed, so the field says what is in force.
    assert.deepEqual(
        choice.props.rgOptions.map((option) => option.data),
        ["balanced", "custom", "high"],
    );
    choice.props.onChange({ data: "custom" });
    choice.props.onChange({ data: "high" });
    assert.deepEqual(sent, [["power.scheme", "high"]], "an unselectable choice is never sent");
    // Not current, it is not listed at all.
    const other = render.renderSteamSettingRow(
        qamUi,
        {
            key: "power.scheme",
            kind: "choice",
            label: "Windows power profile",
            text: "balanced",
            choices: [
                { value: "balanced", label: "Balanced" },
                { value: "custom", label: "Custom", selectable: false },
            ],
        },
        undefined,
        change,
        () => {},
    );
    assert.deepEqual(
        other.props.rgOptions.map((option) => option.data),
        ["balanced"],
    );
    assert.equal(other.props.icon, undefined, "a row without a glyph draws none");

    // A label token the client localizes replaces the label; one it does not leaves the label.
    const toggle = render.renderSteamSettingRow(
        qamUi,
        {
            key: "display.vrr",
            kind: "boolean",
            label: "Variable refresh rate",
            labelToken: "#QuickAccess_Tab_Perf_EnableVRR",
            icon: "pulse",
            checked: true,
        },
        undefined,
        change,
        () => {},
    );
    assert.equal(toggle.type, "toggle");
    assert.equal(toggle.props.label.props.children, "Variable Refresh Rate (localized)");
    assert.equal(toggle.props.icon, "glyph:pulse");
    const unknown = render.renderSteamSettingRow(
        qamUi,
        {
            key: "k",
            kind: "boolean",
            label: "Fallback",
            labelToken: "#No_Such_Token",
            icon: "noSuchGlyph",
        },
        undefined,
        change,
        () => {},
    );
    assert.equal(unknown.props.label, "Fallback");
    assert.equal(unknown.props.icon, undefined, "an unknown glyph name draws nothing");
    // Settings pages pass no localizer or glyph renderer, and their rows keep their own label.
    const page = render.renderSteamSettingRow(
        { ...qamUi, icon: undefined, localize: undefined },
        { key: "k", kind: "boolean", label: "Plain", labelToken: "#QuickAccess_Tab_Perf_EnableVRR", icon: "pulse" },
        undefined,
        change,
        () => {},
    );
    assert.equal(page.props.label, "Plain");
    assert.equal(page.props.icon, undefined);
    // A labelled slider keeps its glyph with its label.
    const slider = render.renderSteamSettingRow(
        qamUi,
        {
            key: "power.sustained",
            kind: "range",
            label: "TDP",
            icon: "bolt",
            number: 15,
            minimum: 5,
            maximum: 30,
            step: 1,
            suffix: " W",
        },
        undefined,
        change,
        () => {},
    );
    assert.equal(slider.props.iconLocation, "front");
    assert.equal(slider.props.valueSuffix, " W");
    // No reading: the slider sits at its minimum with no number until it is moved.
    const unread = render.renderSteamSettingRow(
        qamUi,
        { key: "power.boost", kind: "range", label: "Boost", minimum: 5, maximum: 30, step: 1 },
        undefined,
        change,
        () => {},
    );
    assert.equal(unread.props.value, 5);
    assert.equal(unread.props.showValue, false);
}
console.log("Quick Access rows: fields carry glyphs, label tokens and unselectable choices.");

// One host row group through the real renderer and draft keeping: rows in Steam's panel rows, the
// set command, and a refusal shown on the row it answered until the next write.
{
    const hooks = createHooks();
    const requests = [];
    let refusal = null;
    const groupFixture = (_ui, props, ...children) => ({ type: "group", props, children });
    const createRowSectionControl = instantiate(
        {
            normalizeText,
            acceptedStates,
            renderOutcomes,
            note: noteFixture,
            RowsPatchId: "steam-ui.quick-access-rows",
            sendCommand: (...args) => {
                requests.push(args);
                return refusal ? Promise.reject(new Error(refusal)) : Promise.resolve({ ok: true });
            },
            renderSteamSettingRow: render.renderSteamSettingRow,
            useSteamSettingDrafts: render.useSteamSettingDrafts,
            renderSteamUiGroup: groupFixture,
        },
        host(
            "rowsOf",
            "rowsOfSection",
            "publishedSection",
            "RowFields",
            "missingRowField",
            "createRowSectionControl",
        ),
        "createRowSectionControl",
    );
    const runtime = {
        row: "row",
        react: { createElement, Fragment: "fragment", useState: hooks.useState, useEffect: hooks.useEffect },
    };
    const Section = createRowSectionControl(runtime, { ...qamUi, textField: null });
    const publish = (revision, sections) =>
        acceptedStates.set("rows", rowsApi.normalizeRowsState({ revision, sections }));
    const scheme = (text) => ({
        key: "power.scheme",
        kind: "choice",
        label: "Windows power profile",
        text,
        summary: text,
        choices: [
            { value: "balanced", label: "Balanced" },
            { value: "high", label: "High" },
        ],
    });
    publish(1, [
        {
            id: "powerProfile",
            rows: [scheme("balanced"), { key: "name", kind: "text", label: "Name", text: "x" }],
        },
    ]);
    const draw = () => {
        hooks.reset();
        return Section({ id: "powerProfile" });
    };
    let tree = draw();
    // The row whose field this client did not resolve is left out and named.
    assert.equal(tree.children.filter(Boolean).length, 1);
    assert.equal(renderOutcomes["row name"], "Steam textField was not resolved");
    assert.equal(tree.children[0].type, "row");
    assert.equal(tree.children[0].props.key, "power.scheme");
    const field = () => draw().children[0].children[0];
    assert.equal(field().props.layout, "below", "a dropdown fits the narrow panel below its label");
    refusal = "Windows refused the profile.";
    field().props.onChange({ data: "high" });
    assert.deepEqual(requests.pop(), [
        "steam-ui.quick-access-rows",
        "set",
        { key: "power.scheme", value: "high" },
    ]);
    assert.equal(field().props.selectedOption, "high", "the pick shows while it is sent");
    await tick();
    assert.equal(field().props.description, "Windows refused the profile.");
    assert.equal(field().props.selectedOption, "balanced", "a refused pick goes back to the value");
    refusal = null;
    field().props.onChange({ data: "high" });
    await tick();
    assert.equal(field().props.description, undefined, "the next write clears the refusal");
    publish(2, [{ id: "powerProfile", rows: [scheme("high")] }]);
    assert.equal(field().props.selectedOption, "high");
    // Sub-groups follow the rows, each a kit group under its own title.
    publish(3, [
        {
            id: "powerProfile",
            rows: [],
            groups: [{ id: "cpu", title: "CPU", rows: [{ key: "boost", kind: "boolean", label: "Boost" }] }],
        },
    ]);
    tree = draw();
    assert.equal(tree.children[0].type, "group");
    assert.equal(tree.children[0].props.title, "CPU");
    assert.equal(tree.children[0].children[0].children[0].type, "toggle");
    // An action row sends true.
    publish(4, [{ id: "powerProfile", rows: [{ key: "reset", kind: "action", label: "Reset", buttonLabel: "Reset" }] }]);
    draw().children[0].children[0].props.value.props.onClick();
    assert.deepEqual(requests.pop()[2], { key: "reset", value: true });
    assert.equal(Section({ id: "absent" }), null);
}
console.log("Quick Access rows: refusals stay on their row through drafts, and writes are set commands.");

// The composer: what the layout places where, the folded headings, the titled groups no layout
// names, Valve's components gated on the performance publication, and Reset last.
const groupFixture = (_ui, props, ...children) => ({ type: "group", props, children });
const plugins = { on: false };
const folded = new Set();
const foldRequests = [];
const composer = instantiate(
    {
        normalizeText,
        acceptedStates,
        summaries,
        drawnKinds,
        appendDiagnostics: {},
        renderSteamUiGroup: groupFixture,
        renderSteamGlyph: (_react, d) => `path:${d}`,
        isFolded: (_folds, id) => folded.has(id),
        setFolded: (id, fold) => foldRequests.push([id, fold]),
        withNativeRowsHidden: (_runtime, tree) => ({ filtered: tree }),
        steamUiKitStyle: () => ({ type: "style" }),
        pluginFrontendElements: (placement) => (plugins.on ? [{ type: "plugin", placement }] : []),
    },
    host(
        "rowsOf",
        "rowsOfSection",
        "rowsSummary",
        "publishedSection",
        "untitledSection",
        "sectionIcon",
        "sectionSummary",
        "hostSection",
        "describe",
        "namedKinds",
        "titledSection",
        "appendControls",
    ),
    "{ appendControls }",
);
{
    // The bespoke table buildControls builds, read whole from the asset with each control standing in
    // as its own name.
    const table = statement(host("buildControls"), "const bespoke = [");
    const names = {
        valve: {
            profileHeader: "profileHeader",
            profileToggle: "profileToggle",
            reset: "reset",
            refreshRate: "refreshRate",
        },
        overlayLevel: "overlayLevel",
        frameLimit: "frameLimit",
        lightingZones: "lightingZones",
    };
    const bespoke = instantiate(names, "", table.slice("const bespoke = ".length, -1));
    assert.deepEqual(
        bespoke.map((entry) => entry[0]),
        [
            "valveProfileHeader",
            "valveProfileHeader",
            "valveOverlayLevel",
            "frameLimit",
            "lightingZones",
            "valveRefreshRate",
            "valveReset",
        ],
        "one table of what stays bespoke",
    );
    const controls = {
        controlRuntime: {
            row: "row",
            icon: (name, size) => `icon:${name}:${size}`,
            react: { Fragment: "fragment", isValidElement: () => false, createElement },
        },
        bespoke,
        rowSection: "rowSection",
    };
    const groupsOf = (placement) => {
        const tree = composer.appendControls(controls, "native", placement);
        assert.equal(tree.children[0].type, "style");
        return tree.children;
    };
    const publishRows = (sections) =>
        acceptedStates.set("rows", rowsApi.normalizeRowsState({ revision: 1, sections }));
    acceptedStates.clear();
    acceptedStates.set("layout", layoutState);
    // Nothing published: Valve's tab is handed back untouched.
    publishRows([]);
    assert.equal(composer.appendControls(controls, "native", "perf"), "native");
    plugins.on = true;
    // Valve's components mount only while the performance state is published: they read it.
    acceptedStates.set("performance", true);
    publishRows([
        {
            id: "powerProfile",
            rows: [{ key: "power.scheme", kind: "choice", label: "Profile", summary: "Balanced" }],
        },
        {
            id: "hybridCores",
            rows: [{ key: "power.hybrid", kind: "choice", label: "Cores", summary: "Automatic" }],
        },
        { id: "unnamed", rows: [{ key: "u", kind: "note", label: "Untitled and unplaced" }] },
        {
            id: "settings.gpu",
            title: "Intel graphics",
            glyph: "M0 0h24v24H0Z",
            rows: [{ key: "gpu/a", kind: "boolean", label: "Sharpening", summary: "On" }],
        },
        { id: "lighting", rows: [{ key: "device/brightness", kind: "range", label: "Brightness", summary: "100%" }] },
    ]);
    folded.add("Power profiles");
    let perf = groupsOf("perf");
    assert.equal(perf[1].props.className, "steam-ui-kit-battery");
    assert.deepEqual(perf[1].children, [{ filtered: "native" }], "the layout asks for Steam's FPS rows to go");
    const own = perf[2].children;
    assert.deepEqual(
        own.map((group) => [group.props.key, group.props.hidden === true]),
        [
            ["Profile scope", false],
            ["Power profiles", false],
        ],
        "a section with none of its kinds published is left out",
    );
    const profiles = own[1];
    assert.equal(profiles.props.collapsed, true);
    assert.equal(profiles.props.detail, "Balanced · Automatic", "the folded heading joins the rows' summaries");
    assert.equal(profiles.props.icon, "icon:sliders:18");
    assert.deepEqual(
        profiles.children.map((child) => child.props.id),
        ["powerProfile", "hybridCores"],
        "groups draw in the order the section names their kinds",
    );
    profiles.props.onToggle();
    assert.deepEqual(foldRequests.pop(), ["Power profiles", false]);
    // Valve's header and toggle are one kind, each in a panel row, and the overlay level follows.
    assert.deepEqual(
        own[0].children.map((child) => [child.type, child.props.key]),
        [
            ["row", "steam-ui-valve-profile-header"],
            ["row", "steam-ui-valve-profile-toggle"],
            ["row", "steam-ui-valve-overlay-level"],
        ],
    );
    assert.equal(own[0].props.onToggle, undefined, "Profile scope does not fold");
    // Then the titled group no layout names, folding under its own id with its own glyph, then the
    // plugins' rows, then the closing sections: Reset is the last control.
    assert.equal(perf[3].props.title, "Intel graphics");
    assert.equal(perf[3].props.key, "settings.gpu");
    assert.equal(perf[3].props.icon, "path:M0 0h24v24H0Z");
    assert.equal(perf[3].props.detail, "On");
    assert.equal(perf[3].children[0].props.id, "settings.gpu");
    perf[3].props.onToggle();
    assert.deepEqual(foldRequests.pop(), ["settings.gpu", true]);
    assert.equal(perf[4].type, "plugin");
    assert.equal(perf.at(-1).props.key, "Reset");
    assert.equal(perf.at(-1).children[0].props.key, "steam-ui-valve-reset");
    assert.ok(
        !JSON.stringify(perf).includes('"unnamed"'),
        "an untitled group no layout names is not drawn while a layout is published",
    );
    // A bespoke row draws while its own state is published, and counts as drawn once it says so.
    acceptedStates.set("frameLimit", { available: true });
    perf = groupsOf("perf");
    const frame = perf[2].children.find((group) => group.props.key === "Display and frame rate");
    assert.equal(frame.props.hidden, true, "a row that has not drawn yet keeps its section out of layout");
    assert.equal(frame.children[0].children[0].type, "frameLimit");
    drawnKinds.add("frameLimit");
    summaries.frameLimit = "60 fps cap";
    const shown = groupsOf("perf")[2].children.find((group) => group.props.key === "Display and frame rate");
    assert.equal(shown.props.hidden, false);
    assert.equal(shown.props.detail, "60 fps cap");
    // Without the performance state Valve's components are not mounted at all.
    acceptedStates.set("performance", null);
    perf = groupsOf("perf");
    assert.ok(!JSON.stringify(perf).includes("steam-ui-valve-"));
    // Quick Settings: the lighting group's generic brightness and the bespoke editor share a section,
    // and its heading joins both summaries in kind order.
    acceptedStates.set("lightingZones", { zones: [] });
    drawnKinds.add("lightingZones");
    summaries.lightingZones = "Buttons";
    const quick = groupsOf("quickSettings");
    const valve = quick.findIndex((child) => child?.props?.className === "steam-ui-kit-valve");
    assert.ok(valve >= 1);
    const lighting = quick.slice(valve + 1).find((child) => child.type === "group");
    assert.equal(lighting.props.title, "RGB lighting");
    assert.equal(lighting.props.detail, "100% · Buttons");
    assert.deepEqual(
        lighting.children.map((child) => child.props.key),
        ["steam-ui-rows-lighting", "steam-ui-lighting-zones"],
        "the lighting editor lays out its own rows, not inside another",
    );
    assert.equal(quick.at(-1).type, "plugin");
    // Without a layout every group draws on Performance, the untitled ones in one untitled group.
    acceptedStates.delete("layout");
    perf = groupsOf("perf");
    assert.deepEqual(perf[1].children, ["native"], "without a layout Valve's tree is untouched");
    const [bare] = perf[2].children;
    assert.equal(bare.props.title, undefined);
    assert.ok(bare.children.some((child) => child.props.id === "unnamed"));
    assert.ok(perf.some((child) => child.props?.title === "Intel graphics"));
    acceptedStates.clear();
    drawnKinds.clear();
    folded.clear();
    for (const key of Object.keys(summaries)) delete summaries[key];
}
console.log("Quick Access rows: the layout places host groups and bespoke rows, and Reset stays last.");

// The accent helper the bespoke rows share, over the shipped colour helper.
const accentHelpers = instantiate(
    {
        normalizeText,
        acceptedStates,
        ...instantiate(
            {},
            helpers("SteamAccentColor", "steamAccentDescription"),
            "{ steamAccentDescription }",
        ),
    },
    host("accentDescription"),
    "{ accentDescription }",
);
{
    const runtime = { react: { createElement: (type, props, ...children) => ({ type, props, children }) } };
    acceptedStates.set("layout", layoutState);
    const marked = accentHelpers.accentDescription(runtime, true, "Ready");
    assert.equal(marked.type, "span");
    assert.equal(marked.props.style.color, "#1a9fff");
    assert.deepEqual(marked.children, ["Game override · Ready"]);
    assert.equal(accentHelpers.accentDescription(runtime, false, "Ready"), "Ready");
    // The label is the host's: without a layout a marked row is only coloured.
    acceptedStates.delete("layout");
    assert.deepEqual(accentHelpers.accentDescription(runtime, true, "Ready").children, ["Ready"]);
}

// The frame-limit row: one slider that is the cap while a limit is set and the refresh rate once it
// is switched off, writing to its own publication's commands.
{
    const hooks = createHooks();
    const requests = [];
    const frameApi = instantiate(
        {
            normalizeText,
            acceptedStates,
            ...accentHelpers,
            FrameLimitPatchId: "steam-ui.frame-limit",
            sendCommand: (...args) => {
                requests.push(args);
                return Promise.resolve();
            },
            note: noteFixture,
            drew: () => {},
            summarize: (kind, text) => {
                summaries[kind] = text;
            },
            refusalText: (reason) => String(reason?.message ?? reason),
            localizeOr: (_runtime, _token, fallback) => fallback,
        },
        host(
            "validEnum",
            "normalizePerformanceCommon",
            "normalizeFrameLimitState",
            "isBusy",
            "useEchoedValue",
            "currentRefreshNotch",
            "createFrameLimitControl",
        ),
        "{ normalizeFrameLimitState, createFrameLimitControl }",
    );
    const runtime = {
        toggle: "toggle",
        slider: "slider",
        icon: (name) => name,
        react: { createElement, Fragment: "fragment", useState: hooks.useState },
    };
    const Frame = frameApi.createFrameLimitControl(runtime);
    const base = {
        available: true,
        progress: "idle",
        minimumFps: 30,
        maximumFps: 120,
        desiredFps: 60,
        observedFps: 60,
        limitEnabled: true,
        refreshForCap: { 60: 60 },
        refreshMinHz: 60,
        refreshMaxHz: 120,
        currentRefreshHz: 120,
        refreshRates: [60, 120],
    };
    acceptedStates.set("frameLimit", frameApi.normalizeFrameLimitState(base));
    const draw = () => {
        hooks.reset();
        return Frame();
    };
    let [slider, toggle] = draw().children;
    assert.equal(slider.type, "slider");
    assert.equal(slider.props.label, "Frame rate limit");
    assert.equal(slider.props.valueSuffix, " FPS (60 Hz)");
    assert.equal(slider.props.value, 60);
    assert.equal(summaries.frameLimit, "60 fps cap");
    slider.props.onChangeComplete(72);
    assert.deepEqual(requests.pop(), ["steam-ui.frame-limit", "setFrameLimit", { value: 72 }]);
    toggle.props.onChange(true);
    assert.deepEqual(requests.pop(), ["steam-ui.frame-limit", "setFrameLimit", { value: 0 }]);
    // Off, the slider is the refresh rate, notched to the modes the display accepted.
    acceptedStates.set("frameLimit", frameApi.normalizeFrameLimitState({ ...base, limitEnabled: false }));
    [slider] = draw().children;
    assert.equal(slider.props.label, "Refresh rate");
    assert.equal(slider.props.notchCount, 2);
    slider.props.onChangeComplete(0);
    assert.deepEqual(requests.pop(), ["steam-ui.frame-limit", "setRefreshRate", { value: 60 }]);
    assert.equal(frameApi.normalizeFrameLimitState({ ...base, progress: "bogus" }), null);
    acceptedStates.clear();
}
console.log("Quick Access rows: the frame-limit row writes its cap and refresh rate.");

// The inline lighting editor: closed it is one switch; open, the zone, a preview and three colour
// sliders whose release queues one colour for the zone.
{
    const hooks = createHooks();
    const requests = [];
    const lightingApi = instantiate(
        {
            normalizeText,
            acceptedStates,
            ...accentHelpers,
            LightingZonesPatchId: "steam-ui.lighting-zones",
            sendCommand: (...args) => {
                requests.push(args);
                return Promise.resolve();
            },
            // The trailing commit sends at once here; its coalescing is the hook's own business.
            useTrailingCommit: (_runtime, _delay, commit) => commit,
            note: noteFixture,
            drew: () => {},
            summarize: (kind, text) => {
                summaries[kind] = text;
            },
            refusalText: (reason) => String(reason?.message ?? reason),
            localizeOr: (_runtime, _token, fallback) => fallback,
        },
        host(
            "normalizeLightingZonesState",
            "isBusy",
            "useEchoedValue",
            "rgbToHsv",
            "hsvToRgb",
            "rgbCss",
            "createLightingZonesControl",
        ),
        "{ normalizeLightingZonesState, createLightingZonesControl }",
    );
    assert.equal(
        lightingApi.normalizeLightingZonesState({ zones: [{ id: "a", label: "A", observedColor: 0x1000000 }] }),
        null,
    );
    const runtime = {
        toggle: "toggle",
        slider: "slider",
        dropdown: "dropdown",
        row: "row",
        icon: (name) => name,
        react: { createElement, Fragment: "fragment", useState: hooks.useState },
    };
    const Lighting = lightingApi.createLightingZonesControl(runtime);
    acceptedStates.set(
        "lightingZones",
        lightingApi.normalizeLightingZonesState({
            zones: [
                { id: "buttons", label: "Buttons", available: true, observedColor: 0xff0000, accent: true },
                { id: "stick", label: "Stick", available: true, observedColor: 0x00ff00 },
            ],
        }),
    );
    acceptedStates.set("layout", layoutState);
    const draw = () => {
        hooks.reset();
        return Lighting();
    };
    let rows = draw().children;
    assert.equal(rows.length, 1);
    const edit = rows[0].children[0];
    assert.equal(edit.props.label, "Edit color");
    assert.equal(edit.props.description, "Game override · Buttons");
    assert.equal(summaries.lightingZones, "Buttons");
    edit.props.onChange(true);
    rows = draw().children;
    assert.deepEqual(
        rows.map((row) => row.props.key),
        [
            "steam-ui-lighting-edit",
            "steam-ui-lighting-zone",
            "steam-ui-lighting-preview",
            "steam-ui-lighting-hue",
            "steam-ui-lighting-saturation",
            "steam-ui-lighting-color-brightness",
        ],
    );
    const hue = rows[3].children[0];
    assert.equal(hue.props.value, 0);
    hue.props.onChangeComplete(120);
    assert.deepEqual(requests.pop(), [
        "steam-ui.lighting-zones",
        "setLightingColor",
        { zone: "buttons", color: 0x00ff00 },
    ]);
    acceptedStates.set("lightingZones", lightingApi.normalizeLightingZonesState({ zones: [] }));
    assert.equal(draw(), null);
    assert.equal(renderOutcomes.lightingZones, "no available lighting zone");
    acceptedStates.clear();
}
console.log("Quick Access rows: the lighting editor queues one colour per zone.");

// Glyphs: every glyph the asset names in its own code, and every glyph the host layout names, is one
// the table draws, and an unknown name draws nothing. A host row names its glyph as data, which the
// renderer resolves through the same table, so a name the table lacks leaves the row without one.
{
    const drawings = fragment(asset, "icons.ts");
    // Comments are emitted verbatim, and a commented-out call site is not a placement.
    const code = asset.replace(/^[ \t]*\/\/.*$/gmu, "");
    const literal = [...code.matchAll(/\bicon\(\s*["']([A-Za-z]+)["']/gu)].map((match) => match[1]);
    const used = [
        ...literal,
        ...[
            ...hostLayout.performance,
            ...hostLayout.performanceEnd,
            ...hostLayout.quickSettings,
            ...hostLayout.quickSettingsEnd,
        ].map((section) => section.icon),
    ];
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
    const renderIcon = instantiate(
        {},
        declarations(asset, "icons.ts", ["SteamUiIconShapes", "createIconRenderer"]),
        "createIconRenderer",
    )({ createElement });
    assert.equal(renderIcon("noSuchGlyph"), null);
    assert.equal(renderIcon("power").type, "svg");
    assert.equal(renderIcon("power"), renderIcon("power"), "a glyph is built once per size");
}
console.log("Quick Access rows: glyph names resolve through the one glyph table.");

// The slider echo on its own: readback and acknowledgments never become a user's write.
{
    const hooks = createHooks();
    const useEcho = instantiate({}, host("useEchoedValue"), "useEchoedValue");
    const runtime = { react: { useState: hooks.useState } };
    const draw = (observed) => {
        hooks.reset();
        return useEcho(runtime, observed);
    };
    const writes = [];
    const commit = (value) => writes.push(value);

    let slider = draw(30);
    slider.onChange(30);
    slider.onChangeComplete(30, commit);
    assert.deepEqual(writes, [], "a programmatic refresh must not dispatch a write");
    slider = draw(17);
    slider.onChangeComplete(17, commit);
    assert.equal(draw(17).value, 17);
    assert.deepEqual(writes, [], "readback must not become manual intent");
    slider.onChange(19);
    assert.equal(draw(17).value, 19);
    slider.onChangeComplete(19, commit);
    assert.deepEqual(writes, [19]);
    slider = draw(19);
    slider.onChangeComplete(19, commit);
    slider.onChangeComplete(Number.NaN, commit);
    slider.onChangeComplete(Number.POSITIVE_INFINITY, commit);
    assert.deepEqual(writes, [19], "acknowledgments and invalid values must not repeat a write");
    console.log("Slider readback stays separate from user writes.");
}
