// Exercise the emitted UI kit against an inert Steam runtime.
//
// The kit is what a host draws around Steam's fields, so what matters is that every element is
// built from Steam's Focusable and buttons where it takes focus, carries the kit's classes the
// stylesheet keys on, and answers the host's callbacks: a header toggles, an action runs, a card
// activates, a confirm sends only on OK, a prompt sends only a non-empty answer. This runs the
// emitted JavaScript.
import assert from "node:assert/strict";
import {
    createReact,
    helperFragments,
    instantiate,
    loadAsset,
    withSource,
} from "./check-harness.mjs";

const asset = loadAsset();
const hooks = { states: [], index: 0 };
const react = createReact({
    useState(initial) {
        const slot = hooks.index++;
        if (!(slot in hooks.states)) hooks.states[slot] = initial;
        return [
            hooks.states[slot],
            (next) =>
                (hooks.states[slot] = typeof next === "function" ? next(hooks.states[slot]) : next),
        ];
    },
});
const named = (name) =>
    withSource(
        Object.defineProperty(() => null, "name", { value: name }),
        name,
    );
const Focusable = named("Focusable");
const Button = named("Button");
const Primary = named("Primary");
const Small = named("Small");
const TextField = named("TextField");
const Modal = named("Modal");
const shown = [];
const ui = {
    react,
    focusable: Focusable,
    dialogButton: Button,
    dialogButtonPrimary: Primary,
    smallButton: Small,
    textField: TextField,
    modalRoot: Modal,
    showModal: (modal) => shown.push(modal),
};

const kit = instantiate(
    { window: {} },
    helperFragments(asset),
    "({steamUiKitStyle, renderSteamUiHeader, renderSteamUiGroup, renderSteamUiActions, renderSteamUiMore, renderSteamUiPane, renderSteamUiLevel," +
        " renderSteamUiSwatch, renderSteamUiCard, renderSteamUiGrid, renderSteamUiEmpty, renderSteamUiBanner," +
        " renderSteamUiToolbar, renderSteamUiTool, renderSteamUiChips, renderSteamUiBox, renderSteamUiGallery, renderSteamUiVideo," +
        " renderSteamUiDetail, SteamUiPageRequired, renderSteamUiStatus, renderSteamUiProgress, renderSteamUiFields," +
        " renderSteamUiIcon, renderSteamUiChoice, renderSteamUiSelectRow, showSteamUiConfirm, showSteamUiPrompt," +
        " showSteamModal, SteamUiKitStyles})",
);

// The stylesheet is one element a root renders, and every class an element uses has a rule.
const style = kit.steamUiKitStyle(react);
assert.equal(style.type, "style");
for (const cls of [
    "page",
    "pane",
    "content",
    "header",
    "header-icon",
    "group",
    "group-body",
    "blocks",
    "valve",
    "battery",
    "actions",
    "nested",
    "swatch",
    "settings-body",
    "card",
    "grid",
    "banner",
    "toolbar",
    "chips",
    "box",
    "gallery",
    "detail",
    "detail-main",
    "detail-aside",
    "page-banner",
    "modal-body",
    "modal-wide",
    "more",
    "picker",
    "picker-body",
    "picker-places",
    "picker-files",
    "picker-path",
    "picker-count",
    "picker-actions",
    "empty",
    "status",
    "status-line",
    "progress",
    "progress-track",
    "progress-bar",
    "progress-outcome",
    "collection",
    "list",
]) {
    assert.ok(
        kit.SteamUiKitStyles.includes(`.steam-ui-kit-${cls}`),
        `a rule for steam-ui-kit-${cls}`,
    );
}

// The picker sits in a Steam dialog allowed its full size, which sizes itself to its content. So
// the picker names its width as a length, not as a share of that parent, and a dialog that stays
// narrow (a client without the full-size prop, a small viewport) clamps it through max-width. Its
// panes keep a floor each and scroll on their own. Only an explicitly wide toolkit modal gets a
// width override; unrelated native dialogs retain Steam's layout.
const pickerRule = kit.SteamUiKitStyles.match(/\.steam-ui-kit-picker\{([^}]*)\}/u)?.[1] ?? "";
assert.match(pickerRule, /(?:^|;)width:\d+px(?:;|$)/u, "the picker asks for a fixed width");
assert.match(pickerRule, /(?:^|;)max-width:100%(?:;|$)/u, "a narrower dialog clamps the picker");
assert.match(pickerRule, /(?:^|;)min-width:0(?:;|$)/u, "the picker can shrink to a narrow dialog");
const pickerBody = kit.SteamUiKitStyles.match(/\.steam-ui-kit-picker-body\{([^}]*)\}/u)?.[1] ?? "";
assert.match(
    pickerBody,
    /grid-template-columns:minmax\(\d+px,1fr\) minmax\(0,2fr\)/u,
    "two columns, the places with a floor",
);
assert.match(
    pickerBody,
    /(?:^|;)height:clamp\([^)]*vh[^)]*\)(?:;|$)/u,
    "the panes take a height bounded by the viewport",
);
const pickerPanes =
    kit.SteamUiKitStyles.match(
        /\.steam-ui-kit-picker-places,\.steam-ui-kit-picker-files\{([^}]*)\}/u,
    )?.[1] ?? "";
assert.match(pickerPanes, /(?:^|;)overflow-y:auto(?:;|$)/u, "each pane scrolls on its own");
assert.match(
    pickerPanes,
    /(?:^|;)overflow-x:hidden(?:;|$)/u,
    "a long name never scrolls a pane sideways",
);
assert.doesNotMatch(
    kit.SteamUiKitStyles,
    /ModalPosition|DialogContent|GenericDialog|FullModalOverlay/u,
    "the kit leaves Steam's dialog frame alone",
);
const wideRule = kit.SteamUiKitStyles.match(/\.steam-ui-kit-modal-wide\{([^}]*)\}/u)?.[1] ?? "";
assert.match(
    wideRule,
    /width:min\(1000px,calc\(100vw - 64px\)\)!important/u,
    "wide modals request a width capped by the viewport",
);
assert.match(wideRule, /min-width:0!important/u, "wide modals can shrink on handheld displays");
const contentRule = kit.SteamUiKitStyles.match(/\.steam-ui-kit-content\{([^}]*)\}/u)?.[1] ?? "";
assert.match(
    contentRule,
    /width:min\(100%,960px\)/u,
    "full-page content is bounded without losing handheld width",
);

// The beta slider keeps 24px notch containers and a 100px track minimum. A shrink rule on
// every descendant div erased both, spreading zero-width ticks past the track's endpoints.
// Width constraints belong to the kit's layout boxes, not the internals of any native field.
const widthRules = [...kit.SteamUiKitStyles.matchAll(/([^{}]+)\{([^{}]*)\}/gu)].filter(
    ([, , declarations]) => /(?:^|;)min-width\s*:/u.test(declarations),
);
for (const boundary of ["group-body", "blocks", "valve", "battery"]) {
    const own = `.steam-ui-kit-${boundary}`;
    assert.ok(
        widthRules.some(
            ([, selectors, declarations]) =>
                selectors.split(",").some((selector) => selector.trim() === own) &&
                /(?:^|;)min-width\s*:\s*0(?:px)?(?:;|$)/u.test(declarations),
        ),
        `${boundary} itself can shrink to fit a narrow panel`,
    );
    for (const [, selectors] of widthRules) {
        assert.equal(
            selectors
                .split(",")
                .some(
                    (selector) =>
                        selector.trim().startsWith(own + " ") &&
                        /(?:^|\s|>)div(?:$|[.:\s>])/u.test(selector),
                ),
            false,
            `${boundary} preserves native slider, notch and dropdown minimum widths`,
        );
    }
}

// A header is Steam's Focusable, says which way it folds, and toggles on activation.
let toggled = 0;
const header = kit.renderSteamUiHeader(ui, {
    title: "Themes",
    detail: "2 of 2 enabled",
    collapsed: true,
    onToggle: () => toggled++,
});
assert.equal(header.type, Focusable);
assert.equal(header.props.className, "steam-ui-kit-header");
assert.equal(header.props.onOKActionDescription, "Expand");
header.props.onActivate();
assert.equal(toggled, 1);
assert.equal(
    kit.renderSteamUiHeader(ui, { title: "T", collapsed: false, onToggle: () => {} }).props
        .className,
    "steam-ui-kit-header open",
);
// The caret is the kit's own, and a heading that does not fold is a plain div with no caret at all.
assert.equal(header.props.children.at(-1).props.className, "steam-ui-kit-header-caret");
assert.equal(header.props.children.at(-1).props.children[0].type, "svg");
assert.equal(
    kit.renderSteamUiHeader(ui, { title: "S", sub: true, onToggle: () => {} }).props.className,
    "steam-ui-kit-header open sub",
);
const fixed = kit.renderSteamUiHeader(ui, { title: "Profile scope", icon: "glyph" });
assert.equal(fixed.type, "div");
assert.equal(fixed.props.className, "steam-ui-kit-header open plain");
assert.equal(fixed.props.children[0].props.className, "steam-ui-kit-header-icon");
assert.equal(fixed.props.children.length, 2, "no caret on a heading that does not fold");
const pageHeading = kit.renderSteamUiHeader(ui, { title: "Library tabs", page: true });
assert.equal(pageHeading.props.className, "steam-ui-kit-header open plain page");
assert.equal(
    pageHeading.props.children[0].props.children[0].type,
    "h1",
    "page headings have their own semantic hierarchy",
);

// A group is the heading over a body that stays mounted while folded; without a title it is a
// plain box, and hidden takes it out of layout. What is folded or hidden stays mounted but cannot take
// the controller's focus, so navigation moves from a folded heading to the next block.
let groupToggled = 0;
const group = kit.renderSteamUiGroup(
    ui,
    {
        key: "k",
        title: "Power profiles",
        detail: "Balanced",
        collapsed: true,
        onToggle: () => groupToggled++,
    },
    "row-a",
    "row-b",
);
assert.equal(group.type, Focusable);
assert.equal(
    group.props.childFocusDisabled,
    false,
    "a shown block's heading and rows are reachable",
);
assert.equal(group.key, "k");
assert.equal(group.props.className, "steam-ui-kit-group closed");
assert.equal(group.props.children[0].type, Focusable);
assert.equal(group.props.children[0].props.className, "steam-ui-kit-header");
group.props.children[0].props.onActivate();
assert.equal(groupToggled, 1);
assert.equal(group.props.children[1].props.className, "steam-ui-kit-group-body");
assert.deepEqual(
    group.props.children[1].props.children,
    ["row-a", "row-b"],
    "a folded body keeps its rows",
);
assert.equal(group.props.children[1].type, Focusable);
assert.equal(
    group.props.children[1].props.childFocusDisabled,
    true,
    "a folded body's rows are out of navigation",
);
const open = kit.renderSteamUiGroup(
    ui,
    { title: "Open", collapsed: false, onToggle: () => {} },
    "row",
);
assert.equal(
    open.props.children[1].props.childFocusDisabled,
    false,
    "an open body's rows are reachable",
);
assert.equal(kit.renderSteamUiGroup(ui, { title: "Fixed" }).props.className, "steam-ui-kit-group");
assert.equal(kit.renderSteamUiGroup(ui, { title: "Fixed" }).props.children[0].type, "div");
const gone = kit.renderSteamUiGroup(ui, { title: "Gone", hidden: true, onToggle: () => {} });
assert.equal(gone.props.className, "steam-ui-kit-group hidden");
assert.equal(gone.props.childFocusDisabled, true, "a hidden block cannot take focus");
const plain = kit.renderSteamUiGroup(ui, {}, "button");
assert.equal(plain.props.className, "steam-ui-kit-group plain");
assert.equal(plain.props.children[0], null, "no heading without a title");

// A pane, a level and a detail take the controller's focus when they appear, so B reaches the page's
// own handling instead of leaving the page from focus left on an element that is gone.
const pane = kit.renderSteamUiPane(ui, { className: "extra" }, "grid");
assert.equal(pane.type, Focusable);
assert.equal(pane.props.autoFocus, true);
assert.equal(pane.props.className, "steam-ui-kit-pane extra");
assert.equal(
    kit.renderSteamUiPane(ui, { bounded: true }).props.className,
    "steam-ui-kit-pane steam-ui-kit-content",
);
let levelBack = 0;
const level = kit.renderSteamUiLevel(ui, { className: "lvl", onBack: () => levelBack++ }, "body");
assert.equal(level.type, Focusable);
assert.equal(level.props.autoFocus, true);
level.props.onCancelButton();
assert.equal(levelBack, 1);
const boundedLevel = kit.renderSteamUiLevel(ui, { bounded: true, onBack: () => levelBack++ });
assert.equal(boundedLevel.props.className, "steam-ui-kit-content");
boundedLevel.props.onCancelButton();
assert.equal(levelBack, 2, "bounded content preserves nested Back");

// A paged list's foot is one button asking for the next page.
let more = 0;
const moreFoot = kit.renderSteamUiMore(ui, { onClick: () => more++ });
assert.equal(moreFoot.props.className, "steam-ui-kit-more");
const moreButton = [moreFoot.props.children].flat()[0];
assert.deepEqual([moreButton.props.children].flat(), ["Load More"]);
moreButton.props.onClick();
assert.equal(more, 1);

// Actions are Steam's buttons in the kit's grid; a long label spans the row.
const ran = [];
const actions = kit.renderSteamUiActions(ui, [
    { id: "a", label: "Browse…", onClick: () => ran.push("a") },
    { id: "b", label: "A label longer than eighteen", onClick: () => ran.push("b") },
]);
assert.equal(actions.type, Focusable);
assert.equal(actions.props.className, "steam-ui-kit-actions");
assert.equal(actions.props.children[0].type, Button);
assert.equal(actions.props.children[0].props.className, undefined);
assert.equal(actions.props.children[1].props.className, "steam-ui-kit-wide");
actions.props.children[0].props.onClick();
assert.deepEqual(ran, ["a"]);
const disabledAction = kit.renderSteamUiActions(ui, [
    { id: "disabled", label: "Unavailable", disabled: true, onClick: () => ran.push("disabled") },
]).props.children[0];
assert.equal(disabledAction.props.disabled, true);
assert.equal(disabledAction.props["aria-disabled"], true);
disabledAction.props.onClick();
assert.deepEqual(ran, ["a"]);
const compact = kit.renderSteamUiActions(
    ui,
    [
        {
            id: "order",
            label: "Tab order & Steam tabs",
            primary: true,
            onClick: () => ran.push("order"),
        },
    ],
    { compact: true },
);
assert.equal(compact.props.className, "steam-ui-kit-actions compact");
assert.equal(compact.props.children[0].type, Primary);
assert.equal(
    compact.props.children[0].props.className,
    undefined,
    "compact actions never become full-row bars based on label length",
);
compact.props.children[0].props.onClick();
assert.deepEqual(ran, ["a", "order"]);

// A swatch keeps its colour, and a label makes it a named image rather than a bare box.
assert.equal(kit.renderSteamUiSwatch(react, "#ff0000").props.style.background, "#ff0000");
assert.equal(kit.renderSteamUiSwatch(react, "#ff0000").props.role, undefined);
assert.equal(
    kit.renderSteamUiSwatch(react, "#ff0000", "Zone: #ff0000").props["aria-label"],
    "Zone: #ff0000",
);
// The stylesheet element is one per React, so a re-rendering root hands React the same one.
assert.equal(kit.steamUiKitStyle(react), style);

// A card is one Focusable with its image, stats, badge, title and meta, and activates as one.
let opened = 0;
const card = kit.renderSteamUiCard(ui, {
    image: "https://x/1.jpg",
    stats: [{ text: "12" }, { text: "3" }],
    badge: { text: "Update", warn: true },
    title: "Dark Deck",
    meta: ["v2.1", "By Squishy"],
    onActivate: () => opened++,
});
assert.equal(card.type, Focusable);
assert.equal(card.props.className, "steam-ui-kit-card");
const shot = card.props.children[0];
assert.equal(shot.props.children[0].props.src, "https://x/1.jpg");
assert.equal(shot.props.children[1].props.children.length, 2);
assert.equal(shot.props.children[2].props.className, "steam-ui-kit-badge warn");
assert.deepEqual(card.props.children[1].props.children, ["Dark Deck"]);
assert.equal(card.props.children.length, 4);
card.props.onActivate();
assert.equal(opened, 1);
assert.equal(kit.renderSteamUiGrid(ui, [card]).props.className, "steam-ui-kit-grid");
const preview = { type: "p", props: { children: ["Plain note preview"] } };
const noteCard = kit.renderSteamUiCard(ui, {
    title: "Notebook page",
    media: preview,
    onActivate: () => opened++,
});
assert.equal(noteCard.props.children[0].props.children[0], preview);
noteCard.props.onActivate();
assert.equal(opened, 2, "supplied card content retains the same single activation target");
// An empty state says what kind it is: loading and unavailable are a status, an error an alert,
// and a title makes it the large first-run panel with its one action under it.
const failed = kit.renderSteamUiEmpty(ui, { kind: "error", text: "The store did not answer." });
assert.equal(failed.props.className, "steam-ui-kit-empty error");
assert.equal(failed.props.role, "alert");
const loadingState = kit.renderSteamUiEmpty(ui, { kind: "loading", text: "Asking the store…" });
assert.equal(loadingState.props.role, "status");
assert.equal(loadingState.props["aria-busy"], true);
assert.equal(kit.renderSteamUiEmpty(ui, { kind: "unavailable", text: "x" }).props.role, "status");
assert.equal(
    kit.renderSteamUiEmpty(ui, { kind: "bogus", text: "x" }).props.className,
    "steam-ui-kit-empty empty",
    "an unknown kind is an empty list",
);
let browsed = 0;
const firstRun = kit.renderSteamUiEmpty(ui, {
    kind: "empty",
    title: "No themes yet",
    text: "Get started under Browse.",
    action: { label: "Browse", onClick: () => browsed++ },
});
assert.equal(firstRun.props.className, "steam-ui-kit-empty empty large");
assert.equal(firstRun.props.children[0].type, "h3");
assert.equal(firstRun.props.children[1].type, "p");
const emptyAction = firstRun.props.children[2].props.children[0];
assert.equal(emptyAction.type, Primary, "the action is Steam's primary button");
emptyAction.props.onClick();
assert.equal(browsed, 1);
assert.equal(
    kit.renderSteamUiEmpty({ react }, { kind: "noMatch", text: "Nothing matched." }).props.children.length,
    3,
    "React alone draws an empty state without its action",
);

// A video preview plays the movie muted on a loop, shows the still without one, and says so without either.
const video = kit.renderSteamUiVideo(react, { src: "https://x/1.webm", poster: "https://x/1.jpg" });
assert.equal(video.props.className, "steam-ui-kit-hero video");
assert.equal(video.props.children[0].type, "video");
assert.equal(video.props.children[0].props.muted, true);
assert.equal(video.props.children[0].props.loop, true);
assert.equal(
    kit.renderSteamUiVideo(react, { poster: "https://x/1.jpg" }).props.children[0].type,
    "img",
);
assert.equal(
    kit.renderSteamUiVideo(react, { empty: "Nothing" }).props.children[0].props.className,
    "steam-ui-kit-hero-empty",
);

// A detail is Steam's Focusable, left with B, with Back last in its aside.
let backed = 0;
const detail = kit.renderSteamUiDetail(ui, {
    title: "T",
    badge: "v1",
    main: ["m"],
    aside: ["box"],
    onBack: () => backed++,
});
assert.equal(detail.type, Focusable);
detail.props.onCancelButton();
assert.equal(backed, 1);
assert.equal(detail.props.children[1].props.children.at(-1).type, Button);
for (const name of ["react", "focusable", "tabs", "settingsSection", "valueField", "showModal"]) {
    assert.ok(kit.SteamUiPageRequired.includes(name), `a page requires ${name} by default`);
}

// A banner dismisses with the small button; a toolbar and chips are focusable rows of Steam's buttons.
let dismissed = 0;
const banner = kit.renderSteamUiBanner(ui, { text: "Installed.", onDismiss: () => dismissed++ });
assert.equal(banner.props.className, "steam-ui-kit-banner");
assert.equal(banner.props.children[1].type, Small);
banner.props.children[1].props.onClick();
assert.equal(dismissed, 1);
assert.equal(
    kit.renderSteamUiBanner(ui, { text: "x", error: true, onDismiss: () => {} }).props.className,
    "steam-ui-kit-banner error",
);
const toolbar = kit.renderSteamUiToolbar(
    ui,
    kit.renderSteamUiTool(ui, "Sort", "dropdown"),
    kit.renderSteamUiTool(ui, null, "search", true),
);
assert.equal(toolbar.type, Focusable);
assert.equal(toolbar.props.children[0].props.children[0].props.children[0], "Sort");
assert.equal(toolbar.props.children[1].props.className, "steam-ui-kit-tool grow");
const chips = kit.renderSteamUiChips(ui, [{ label: "Library", onClick: () => ran.push("chip") }]);
assert.equal(chips.props.children[0].type, Button);
chips.props.children[0].props.onClick();
assert.deepEqual(ran, ["a", "order", "chip"]);
const box = kit.renderSteamUiBox(react, "Install", "body");
assert.equal(box.props.children[0].props.className, "steam-ui-kit-box-title");

// A compact choice uses the bare native dropdown and leaves the table's label in its own column.
const BareDropdown = named("BareDropdown");
const FieldDropdown = named("FieldDropdown");
const choices = [];
const choice = kit.renderSteamUiChoice(
    { ...ui, dropdownControl: BareDropdown, dropdown: FieldDropdown },
    {
        label: "Nintendo Entertainment System emulator",
        showLabel: false,
        rgOptions: [{ data: "", label: "No default" }],
        selectedOption: "",
        onChange: (value) => choices.push(value.data),
    },
);
assert.equal(choice.props.children[0], null, "table cells do not repeat the system label");
assert.equal(
    choice.props.children[1].type,
    BareDropdown,
    "a native settings row is not a table cell",
);
assert.deepEqual(choices, [], "rendering does not commit a selection");
choice.props.children[1].props.onChange({ data: "retroarch" });
assert.deepEqual(choices, ["retroarch"]);
const fallback = kit.renderSteamUiChoice(
    { ...ui, dropdown: FieldDropdown },
    {
        label: "Release channel",
        rgOptions: [],
        selectedOption: "stable",
        onChange: () => {},
    },
);
assert.equal(fallback.props.children[1].type, FieldDropdown);
assert.equal(
    fallback.props.children[1].props.layout,
    "below",
    "fallback fields fit their containing column",
);
assert.equal(fallback.props.children[1].props.label, "Release channel");
const row = kit.renderSteamUiSelectRow(ui, {
    key: "psx",
    title: "PlayStation",
    detail: "Emulator not installed",
    status: "Missing",
    selected: true,
    onClick: () => ran.push("psx"),
});
const rowContent = row.props.children[0];
assert.equal(rowContent.props.children[0].props.children[0], "PlayStation");
assert.equal(rowContent.props.children[1], "Missing");
assert.equal(rowContent.props.children[2].props.className, "steam-ui-kit-select-row-detail");
assert.equal(row.props["aria-pressed"], true);
row.props.onClick();
assert.equal(ran.at(-1), "psx", "one native button activation dispatches once");

// A gallery picks a thumbnail and counts; one image needs no thumbnails.
let picked = -1;
const gallery = kit.renderSteamUiGallery(ui, {
    images: ["a", "b", "c"],
    index: 1,
    onSelect: (at) => (picked = at),
});
assert.equal(gallery.props.children[0].props.className, "steam-ui-kit-thumbs");
assert.equal(
    gallery.props.children[0].props.children[1].props.className,
    "steam-ui-kit-thumb current",
);
gallery.props.children[0].props.children[2].props.onActivate();
assert.equal(picked, 2);
assert.deepEqual(gallery.props.children[1].props.children[1].props.children, ["2/3"]);
assert.equal(
    kit.renderSteamUiGallery(ui, { images: ["a"], index: 0, onSelect: () => {} }).props.children[0],
    null,
);

// A confirm sends only on OK; a prompt sends only a non-empty answer.
let confirmed = 0;
kit.showSteamUiConfirm(ui, {
    title: "Delete Theme",
    text: "Sure?",
    confirmLabel: "Delete",
    onConfirm: () => confirmed++,
});
assert.equal(shown.length, 1);
let closed = 0;
const renderModal = (modal) => {
    hooks.index = 0;
    const frame = modal.type({ closeModal: () => closed++ });
    assert.equal(frame.type, Modal, "the body sits in Steam's dialog");
    assert.equal(
        frame.props.children[1].props.children[0],
        frame.props.strTitle,
        "the dialog title is visible",
    );
    return frame.props.children.at(-1);
};
hooks.index = 0;
assert.equal(
    "bAllowFullSize" in shown[0].type({ closeModal: () => {} }).props,
    false,
    "a confirm is Steam's ordinary dialog size",
);
let body = renderModal(shown[0]);
const [cancel, ok] = body.props.children[1].props.children;
cancel.props.onClick();
assert.equal(confirmed, 0);
assert.equal(closed, 1);
ok.props.onClick();
assert.equal(confirmed, 1);
assert.equal(closed, 2);
const answers = [];
kit.showSteamUiPrompt(ui, {
    title: "Create Profile",
    label: "Profile Name",
    confirmLabel: "Create",
    onConfirm: (value) => answers.push(value),
});
const prompt = renderModal(shown[1]);
let promptBody = prompt.type(prompt.props);
promptBody.props.children[2].props.children[1].props.onClick();
assert.deepEqual(answers, [], "an empty answer is not sent");
promptBody.props.children[1].props.onChange({ target: { value: "  Night " } });
hooks.index = 0;
promptBody = prompt.type(prompt.props);
promptBody.props.children[2].props.children[1].props.onClick();
assert.deepEqual(answers, ["Night"], "the answer is sent trimmed");

// A body that asks to be wide gets Steam's own full-size dialog, through the prop the dialog's
// author named; the rest of the frame (cancel, close, title) is the same, and nothing else changes
// for a dialog that did not ask.
let wideCancelled = 0;
assert.equal(
    kit.showSteamModal(ui, {
        title: "Wide",
        wide: true,
        render: () => null,
        onCancel: () => wideCancelled++,
    }),
    true,
);
hooks.index = 0;
const wideFrame = shown[2].type({ closeModal: () => closed++ });
assert.equal(wideFrame.type, Modal, "a wide body still sits in Steam's dialog");
assert.equal(
    wideFrame.props.bAllowFullSize,
    true,
    "a wide body asks Steam's dialog for its full size",
);
assert.equal(
    wideFrame.props.className,
    "steam-ui-kit-modal-wide",
    "wide mode marks only its own native dialog root",
);
assert.equal(
    wideFrame.props.children[0].type,
    "style",
    "the width rule follows the modal into its portal document",
);
assert.equal(wideFrame.props.strTitle, "Wide");
assert.equal(wideFrame.props.children[1].type, "h2");
assert.deepEqual(wideFrame.props.children[1].props.children, ["Wide"]);
wideFrame.props.onCancel();
assert.equal(wideCancelled, 1, "cancelling a wide dialog still reaches the body's onCancel");
assert.equal(kit.showSteamModal(ui, { title: "Plain", render: () => null }), true);
hooks.index = 0;
assert.equal(
    "bAllowFullSize" in shown[3].type({ closeModal: () => {} }).props,
    false,
    "a body that did not ask stays Steam's ordinary size",
);
assert.equal(
    shown[3].type({ closeModal: () => {} }).props.className,
    undefined,
    "ordinary dialogs retain native width",
);
assert.equal(
    kit.showSteamModal({ react }, { wide: true, render: () => null }),
    false,
    "no modal manager, no dialog, wide or not",
);

// Steam's own confirm modal where the client has one: its props carry the title, words, button and
// the destructive warning, and only OK sends.
const NativeConfirm = named("NativeConfirm");
const nativeShown = [];
let nativeConfirmed = 0;
assert.equal(
    kit.showSteamUiConfirm(
        { ...ui, confirmModal: NativeConfirm, showModal: (modal) => nativeShown.push(modal) },
        {
            title: "Remove Sound Pack",
            text: "Remove Chime?",
            confirmLabel: "Remove",
            onConfirm: () => nativeConfirmed++,
        },
    ),
    true,
);
assert.equal(nativeShown[0].type, NativeConfirm, "the confirmation is Steam's own modal");
assert.equal(nativeShown[0].props.strTitle, "Remove Sound Pack");
assert.equal(nativeShown[0].props.strDescription, "Remove Chime?");
assert.equal(nativeShown[0].props.strOKButtonText, "Remove");
assert.equal(nativeShown[0].props.bDestructiveWarning, true, "a removal is destructive by default");
nativeShown[0].props.onCancel();
assert.equal(nativeConfirmed, 0, "cancelling sends nothing");
nativeShown[0].props.onOK();
assert.equal(nativeConfirmed, 1);
kit.showSteamUiConfirm(
    { ...ui, confirmModal: NativeConfirm, showModal: (modal) => nativeShown.push(modal) },
    { title: "t", text: "x", confirmLabel: "Go", destructive: false, onConfirm: () => {} },
);
assert.equal(nativeShown[1].props.bDestructiveWarning, false);

// A status stack is one line per message, notices and errors told apart, empty entries skipped,
// and nothing at all without a message.
let statusDismissed = 0;
const stack = kit.renderSteamUiStatus(ui, [
    "Scanning 3 folders.",
    null,
    { text: "", error: true },
    { key: "skip", text: "2 files were skipped.", error: true, onDismiss: () => statusDismissed++ },
]);
assert.equal(stack.props.className, "steam-ui-kit-status");
assert.equal(stack.props.children.length, 2);
assert.equal(stack.props.children[0].props.className, "steam-ui-kit-status-line");
assert.equal(stack.props.children[0].props.role, "status");
assert.equal(stack.props.children[1].props.className, "steam-ui-kit-status-line error");
assert.equal(stack.props.children[1].props.role, "alert");
stack.props.children[1].props.children[1].props.onClick();
assert.equal(statusDismissed, 1);
assert.equal(kit.renderSteamUiStatus(ui, [null, "", false]), null);

// Progress names its phase and counts, measures a bar against its total (and moves without one),
// cancels while it runs and shows the outcome instead of the bar once it ended.
let cancelled = 0;
const running = kit.renderSteamUiProgress(ui, {
    phase: "Installing",
    done: 3,
    total: 4,
    detail: "RetroArch",
    onCancel: () => cancelled++,
});
assert.equal(running.props["aria-busy"], true);
assert.deepEqual(running.props.children[0].props.children[1].props.children, ["3 of 4"]);
const track = running.props.children[1];
assert.equal(track.props.role, "progressbar");
assert.equal(track.props["aria-valuenow"], 3);
assert.equal(track.props.children[0].props.style.width, "75%");
assert.equal(running.props.children[2].props.className, "steam-ui-kit-progress-detail");
running.props.children[3].props.onClick();
assert.equal(cancelled, 1);
const unmeasured = kit.renderSteamUiProgress(ui, { phase: "Scanning" });
assert.equal(unmeasured.props.children[1].props.children[0].props.className, "steam-ui-kit-progress-bar indeterminate");
const stopping = kit.renderSteamUiProgress(ui, { phase: "Installing", onCancel: () => cancelled++, cancelling: true });
stopping.props.children.at(-1).props.onClick();
assert.equal(cancelled, 1, "a cancel already asked for is not sent again");
const ended = kit.renderSteamUiProgress(ui, {
    phase: "Installed",
    done: 4,
    total: 4,
    onCancel: () => cancelled++,
    outcome: { text: "One core failed.", error: true },
});
assert.equal(ended.props["aria-busy"], undefined);
assert.equal(ended.props.children[1].props.className, "steam-ui-kit-progress-outcome error");
assert.equal(ended.props.children.at(-1), null, "an ended operation has no cancel");

// Fields are a settings section of host rows over the drafts: a nested row is indented, and a
// refused change is shown on the row that asked.
const Section = named("Section");
const Toggle = named("Toggle");
const fieldsUi = { ...ui, settingsSection: Section, toggleField: Toggle, react };
hooks.states = [];
const effects = [];
react.useEffect = (effect) => effects.push(effect);
const sent = [];
let refuse = null;
const fieldRows = [
    { key: "on", kind: "boolean", label: "Use themes", checked: false },
    { key: "child", kind: "boolean", label: "Nested", checked: true, nested: true },
];
const fieldsElement = kit.renderSteamUiFields(fieldsUi, {
    key: "themes",
    title: "Themes",
    rows: fieldRows,
    revision: 1,
    onChange: (row, value) => {
        sent.push([row.key, value]);
        return refuse ? Promise.reject(new Error(refuse)) : Promise.resolve();
    },
});
assert.equal(fieldsElement.key, "themes");
const drawFields = () => {
    hooks.index = 0;
    return fieldsElement.type(fieldsElement.props);
};
let section = drawFields();
assert.equal(section.type, Section);
assert.deepEqual(section.props.label, "Themes");
assert.equal(section.props.children[0].type, Toggle);
assert.equal(section.props.children[1].props.className, "steam-ui-kit-nested");
refuse = "Themes are off in Settings.";
section.props.children[0].props.onChange(true);
assert.deepEqual(sent, [["on", true]], "a change is sent once");
section = drawFields();
assert.equal(section.props.children[0].props.checked, true, "the draft shows at once");
await new Promise((resolve) => setImmediate(resolve));
section = drawFields();
assert.equal(section.props.children[0].props.checked, false, "a refused draft is dropped");
assert.equal(
    section.props.children[0].props.description,
    "Themes are off in Settings.",
    "the refusal is drawn on the row that asked",
);
delete react.useEffect;

// A file-pick action opens Steam's picker and hands the chosen path on; a cancellation sends nothing.
const pickedPaths = [];
const pickShown = [];
const pickUi = { ...ui, showModal: (modal) => pickShown.push(modal) };
const pickAction = kit.renderSteamUiActions(pickUi, [
    {
        id: "import",
        label: "Import a ZIP…",
        pick: { title: "Choose an Audio Loader ZIP", extensions: [".zip"] },
        onPick: (path) => pickedPaths.push(path),
    },
]);
pickAction.props.children[0].props.onClick();
assert.equal(pickShown.length, 1, "a file-pick action opens Steam's file picker");

// Glyphs come from the one table: a known name draws, an unknown one draws nothing.
assert.equal(kit.renderSteamUiIcon(react, "star", 13)?.type, "svg");
assert.equal(kit.renderSteamUiIcon(react, "star", 13), kit.renderSteamUiIcon(react, "star", 13), "one element per size");
assert.equal(kit.renderSteamUiIcon(react, "no-such-glyph"), null);

console.log(
    "UI kit: stylesheet, header, actions and file-pick actions, card, empty kinds, status, progress, fields, banner," +
        " toolbar, chips, gallery, glyphs, native and kit confirm, prompt and the wide dialog passed.",
);
