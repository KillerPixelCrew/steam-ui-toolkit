// Exercise the emitted UI flows against an inert Steam runtime.
//
// A flow is what a host page is made of: the collection a user browses, an item's detail with the
// action that acquires it, the list a user manages, and a section added to one of Steam's own
// Settings pages. What matters is what the user sees in each state (loading, failed, empty, nothing
// matched, more to load), that a search is sent once and only when it changed, that the first look
// fetches once, that focus returns to the item that was opened, and that every Settings section
// shares one transform on the shared useMemo claim, released with the last of them.
import assert from "node:assert/strict";
import {
    createReact,
    element,
    helperFragments,
    instantiate,
    loadAsset,
    withSource,
} from "./check-harness.mjs";

const asset = loadAsset();

// Hook slots per component instance, with effects run on demand, so a remount is a fresh instance.
const instance = () => ({ states: [], refs: [], effects: [], ran: [], index: 0 });
let current = instance();
const react = createReact({
    useState(initial) {
        const slot = current.index++;
        if (!(slot in current.states)) current.states[slot] = typeof initial === "function" ? initial() : initial;
        const owner = current;
        return [
            owner.states[slot],
            (next) => {
                owner.states[slot] = typeof next === "function" ? next(owner.states[slot]) : next;
            },
        ];
    },
    useRef(initial) {
        const slot = current.index++;
        if (!(slot in current.refs)) current.refs[slot] = { current: initial };
        return current.refs[slot];
    },
    useEffect(effect, deps) {
        const slot = current.index++;
        const previous = current.effects[slot];
        const changed =
            !previous || !deps || deps.length !== previous.deps.length || deps.some((dep, at) => dep !== previous.deps[at]);
        current.effects[slot] = { effect: changed ? effect : null, deps: deps ?? [], cleanup: previous?.cleanup };
    },
    useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
});
const draw = (node, owner) => {
    current = owner;
    current.index = 0;
    const tree = node.type(node.props);
    for (const entry of owner.effects) {
        if (entry?.effect) {
            entry.cleanup?.();
            entry.cleanup = entry.effect() ?? undefined;
            entry.effect = null;
        }
    }
    return tree;
};
const unmount = (owner) => {
    for (const entry of owner.effects) entry?.cleanup?.();
};

const named = (name) => withSource(Object.defineProperty(() => null, "name", { value: name }), name);
const Focusable = named("Focusable");
const Button = named("Button");
const Primary = named("Primary");
const TextField = named("TextField");
const Dropdown = named("Dropdown");
const Section = named("Section");
const Modal = named("Modal");
const ui = {
    react,
    focusable: Focusable,
    dialogButton: Button,
    dialogButtonPrimary: Primary,
    textField: TextField,
    dropdownControl: Dropdown,
    settingsSection: Section,
    modalRoot: Modal,
    showModal: () => {},
};

const kit = instantiate(
    { window: {}, document: undefined },
    helperFragments(asset),
    "({SteamUiBrowse, renderSteamUiCollection, steamUiAcquireLabel, renderSteamUiAcquire, renderSteamUiItem," +
        " renderSteamUiManage, registerSteamSettingsSection, interceptMemo, releaseMemo, memoIntercepted})",
);

// The vocabulary is the host's too (SteamUiBrowseCommands).
assert.deepEqual({ ...kit.SteamUiBrowse }, {
    setTab: "setTab",
    browse: "browse",
    more: "more",
    open: "open",
    closeDetail: "closeDetail",
    dismiss: "dismiss",
});

const children = (node) => react.Children.toArray(node?.props?.children);
const byClass = (node, className, found = []) => {
    if (!react.isValidElement(node)) return found;
    if (String(node.props?.className ?? "").split(" ").includes(className)) found.push(node);
    children(node).forEach((child) => byClass(child, className, found));
    return found;
};
const card = (item) => element(Focusable, { className: "card", title: item.name });

// A collection: its first look fetches once, the search is a draft until the field is left with a
// change, Load More counts what is shown, and the states say what they are.
{
    const fetched = [];
    const searched = [];
    let more = 0;
    let retried = 0;
    const props = (overrides = {}) => ({
        id: "themes:browse",
        items: [
            { id: "a", name: "Alpha" },
            { id: "b", name: "Beta" },
        ],
        itemKey: (item) => item.id,
        render: card,
        search: { value: "", onSubmit: (text) => searched.push(text) },
        choices: [
            {
                label: "Sort",
                options: [
                    { data: "new", label: "Newest" },
                    { data: "old", label: "Oldest" },
                ],
                selected: "new",
                onChange: (data) => searched.push(`sort:${data}`),
            },
        ],
        empty: { text: "Nothing here yet." },
        noMatch: { text: "Nothing matched." },
        more: { onMore: () => more++, total: 9 },
        onFirstShow: () => fetched.push(1),
        onRetry: () => retried++,
        ...overrides,
    });
    const owner = instance();
    let collection = kit.renderSteamUiCollection(ui, props());
    let tree = draw(collection, owner);
    assert.equal(tree.props.className, "steam-ui-kit-collection");
    assert.equal(fetched.length, 1, "the first look fetches");
    tree = draw(kit.renderSteamUiCollection(ui, props()), owner);
    assert.equal(fetched.length, 1, "a later publication does not fetch again");

    const toolbar = byClass(tree, "steam-ui-kit-toolbar")[0];
    const [sortTool, searchTool] = children(toolbar);
    const sort = children(sortTool).at(-1);
    sort.props.onChange({ data: "new" });
    sort.props.onChange({ data: "old" });
    assert.deepEqual(searched, ["sort:old"], "choosing the current option sends nothing");
    const field = children(searchTool).at(-1);
    assert.equal(field.type, TextField);
    field.props.onChange({ target: { value: "dark" } });
    tree = draw(kit.renderSteamUiCollection(ui, props()), owner);
    const typed = children(children(byClass(tree, "steam-ui-kit-toolbar")[0])[1]).at(-1);
    assert.equal(typed.props.value, "dark", "a draft survives a publication that did not change the query");
    typed.props.onBlur();
    assert.deepEqual(searched, ["sort:old", "dark"], "leaving the field submits the changed search");
    tree = draw(kit.renderSteamUiCollection(ui, props({ search: { value: "dark", onSubmit: (t) => searched.push(t) } })), owner);
    children(children(byClass(tree, "steam-ui-kit-toolbar")[0])[1]).at(-1).props.onBlur();
    assert.deepEqual(searched, ["sort:old", "dark"], "an unchanged search is not sent again");

    const grid = byClass(tree, "steam-ui-kit-grid")[0];
    assert.equal(grid.type, Focusable, "cards sit in the kit's grid");
    assert.deepEqual(
        // React's cloneElement takes the key from the props it is given; the stand-in keeps it there.
        children(grid).map((item) => item.key ?? item.props.key),
        ["a", "b"],
        "each item is keyed by its own id",
    );
    const foot = byClass(tree, "steam-ui-kit-more")[0];
    const moreButton = children(foot)[0];
    assert.deepEqual(moreButton.props.children, ["Load More (2 of 9)"]);
    moreButton.props.onClick();
    assert.equal(more, 1);

    // Focus returns to the item that had it when the collection comes back.
    children(grid)[1].props.onFocus();
    unmount(owner);
    const back = instance();
    tree = draw(kit.renderSteamUiCollection(ui, props()), back);
    const returned = children(byClass(tree, "steam-ui-kit-grid")[0]);
    assert.equal(returned[1].props.autoFocus, true, "the item opened last takes focus again");
    assert.equal(returned[0].props.autoFocus, undefined);

    // Loading below what is there, an error with its retry, and the two empty states.
    tree = draw(kit.renderSteamUiCollection(ui, props({ loading: true, loadingText: "Asking the store…" })), instance());
    assert.equal(byClass(tree, "loading").length, 1, "loading is said under the items");
    assert.equal(byClass(tree, "steam-ui-kit-more").length, 0, "no Load More while loading");
    tree = draw(kit.renderSteamUiCollection(ui, props({ items: [], error: "The store did not answer." })), instance());
    const failed = byClass(tree, "error")[0];
    assert.equal(failed.props.role, "alert");
    children(byClass(failed, "steam-ui-kit-actions")[0])[0].props.onClick();
    assert.equal(retried, 1, "a failure offers its retry");
    assert.equal(byClass(tree, "empty").length, 0, "a failed list does not also say it is empty");
    tree = draw(kit.renderSteamUiCollection(ui, props({ items: [], more: null })), instance());
    assert.deepEqual(children(byClass(tree, "empty")[0]), ["Nothing here yet."]);
    tree = draw(kit.renderSteamUiCollection(ui, props({ items: [], more: null, filtered: true })), instance());
    assert.deepEqual(children(byClass(tree, "noMatch")[0]), ["Nothing matched."], "a search that left nothing says so");

    // A list layout, actions that pick a file, and a status stack.
    tree = draw(
        kit.renderSteamUiCollection(ui, {
            ...props({ id: "sounds:installed", layout: "list", search: null, choices: [], more: null }),
            status: ["Scanning."],
            actions: [{ id: "refresh", label: "Refresh", onClick: () => {} }],
        }),
        instance(),
    );
    assert.equal(byClass(tree, "steam-ui-kit-list")[0].props["flow-children"], "column");
    assert.equal(byClass(tree, "steam-ui-kit-status").length, 1);
    assert.equal(children(byClass(tree, "steam-ui-kit-toolbar")[0])[0].type, Button);
}

// An item's detail: the acquire box says what it will do and refuses while busy, the description
// says it is loading or why it failed, and a removal asks first.
{
    assert.equal(kit.steamUiAcquireLabel(false, false), "Install");
    assert.equal(kit.steamUiAcquireLabel(true, false), "Update");
    assert.equal(kit.steamUiAcquireLabel(true, true), "Reinstall");
    let acquired = 0;
    const box = kit.renderSteamUiAcquire(ui, {
        title: "Install Dark",
        meta: "12 Downloads",
        label: "Install",
        onAcquire: () => acquired++,
        note: "Downloads into the themes folder.",
    });
    const button = children(box).find((child) => child.type === Primary);
    button.props.onClick();
    assert.equal(acquired, 1);
    const busy = kit.renderSteamUiAcquire(ui, { title: "x", label: "Install", busy: true, onAcquire: () => acquired++ });
    const busyButton = children(busy).find((child) => child.type === Primary);
    assert.deepEqual(busyButton.props.children, ["Working…"]);
    busyButton.props.onClick();
    assert.equal(acquired, 1, "a busy acquire does nothing");

    const confirms = [];
    const confirmUi = { ...ui, confirmModal: named("Confirm"), showModal: (modal) => confirms.push(modal) };
    let removed = 0;
    let backed = 0;
    const detail = kit.renderSteamUiItem(confirmUi, {
        title: "Dark",
        badge: "v1",
        byline: "By Night",
        loading: true,
        sections: [{ title: "Targets", content: "chips" }, null],
        acquire: { title: "Install Dark", label: "Install", onAcquire: () => {} },
        remove: { label: "Remove", title: "Remove Theme", text: "Remove Dark?", onRemove: () => removed++ },
        onBack: () => backed++,
    });
    assert.equal(detail.props.onCancelButton !== undefined, true, "B goes back");
    detail.props.onCancelButton();
    assert.equal(backed, 1);
    const [main, aside] = children(detail);
    const mainKids = children(main);
    assert.equal(mainKids[1].props.className, "steam-ui-kit-item-byline");
    assert.deepEqual(children(children(mainKids[2])[1]), ["Loading…"], "a loading description says so");
    assert.deepEqual(children(children(mainKids[3])[0]), ["Targets"]);
    const removal = children(aside).find((child) => child.type === Button && child.props.children?.[0] === "Remove");
    removal.props.onClick();
    assert.equal(removed, 0, "removal asks first");
    confirms[0].props.onOK();
    assert.equal(removed, 1);
    const failed = kit.renderSteamUiItem(ui, { title: "x", error: "No details.", onBack: () => {} });
    const failedLine = children(children(children(failed)[0])[1])[1];
    assert.equal(failedLine.props.role, "alert");
}

// The list a user manages: add actions, rows with their status and the selected one marked, an
// unusable item saying why on its row, and the empty state when there is nothing.
{
    const opened = [];
    const manage = kit.renderSteamUiManage(ui, {
        title: "Installed packs",
        items: [
            { id: "a", name: "Chime" },
            { id: "b", name: "Broken", error: "The pack has no sounds." },
        ],
        itemKey: (item) => item.id,
        label: (item) => item.name,
        detail: () => "By Night",
        error: (item) => item.error,
        status: (item) => (item.id === "a" ? "Selected" : null),
        selected: "a",
        onOpen: (item) => opened.push(item.id),
        add: [{ id: "import", label: "Import a ZIP…", pick: { title: "Choose a ZIP", extensions: [".zip"] }, onPick: () => {} }],
        empty: { text: "No sound packs installed." },
    });
    const [actions, section] = children(manage);
    assert.equal(actions.props.className, "steam-ui-kit-actions compact");
    assert.equal(section.type, Section);
    const [first, second] = children(section);
    assert.equal(first.props["aria-pressed"], true, "the selected item is marked");
    const secondDetail = children(children(second)[0]).at(-1);
    assert.equal(secondDetail.props.className, "steam-ui-kit-select-row-detail error", "an unusable item says why");
    second.props.onClick();
    assert.deepEqual(opened, ["b"]);
    const none = kit.renderSteamUiManage(ui, {
        items: [],
        itemKey: (item) => item.id,
        label: (item) => item.name,
        onOpen: () => {},
        empty: { text: "No sound packs installed." },
    });
    assert.equal(children(none)[0].props.className, "steam-ui-kit-empty empty");
}

// Sections on Steam's own Settings pages: one transform on the shared useMemo claim for every
// section, placed in front of a named Valve section, inside one, or last, and released with the last.
{
    const originalUseMemo = (factory) => factory();
    react.useMemo = originalUseMemo;
    const StartupMovie = withSource(() => element("section", { label: "Startup movie" }), 'f("#Settings_Customization_StartupVideo")');
    const Screensaver = withSource(
        () => element("section", { label: "Screensaver", children: [element("row", {})] }),
        '"#Settings_Customization_Screensaver" ForceScreensaver',
    );
    const List = () => null;
    const Customization = () =>
        element(List, { children: [element(StartupMovie, {}, "startup"), element(Screensaver, {}, "screensaver")] });
    const System = () => element(List, { children: [] });
    const pages = [
        { route: "/settings/system", content: element(System, {}) },
        { route: "/settings/customization", content: element(Customization, {}) },
    ];
    const pageList = () => react.useMemo(() => pages, []);
    const runtime = {
        exported: (_tokens, predicate) => {
            const table = { Settings: { Customization: () => "/settings/customization" } };
            if (!predicate(table)) throw new Error("absent");
            return table;
        },
    };
    const ledger = () => {
        const entries = [];
        return {
            add: (name, release, held) => entries.push({ name, release, held }),
            unwind: () => entries.splice(0).reverse().map((entry) => entry.release()),
            held: () => entries.every((entry) => entry.held()),
        };
    };
    const Hub = () => "hub";
    const Rows = () => "rows";
    const hub = kit.registerSteamSettingsSection({
        page: "Customization",
        key: "hub",
        before: ['"#Settings_Customization_StartupVideo"'],
        render: Hub,
    });
    const rows = kit.registerSteamSettingsSection({
        page: "Customization",
        key: "rows",
        into: ['"#Settings_Customization_Screensaver"', "ForceScreensaver"],
        render: Rows,
    });
    const last = kit.registerSteamSettingsSection({ page: "System", key: "last", render: Hub });
    assert.deepEqual(hub.find(runtime), { route: "/settings/customization" });
    assert.match(last.find(runtime).incompatible, /route table/u, "a page the table does not name is incompatible");

    const hubLedger = ledger();
    assert.equal(hub.attach(hubLedger, react, "/settings/customization"), undefined);
    assert.ok(kit.memoIntercepted(react, "steam-ui.settings-sections"), "the first section takes the claim");
    const rowsLedger = ledger();
    assert.equal(rows.attach(rowsLedger, react, "/settings/customization"), undefined);
    assert.equal(rows.attach(ledger(), createReact(), "/x") , undefined, "attaching twice is a no-op");
    assert.equal(
        kit.registerSteamSettingsSection({ page: "Customization", key: "other", render: Hub }).attach(ledger(), createReact(), "/x"),
        "another React holds the Settings sections",
    );
    assert.ok(hubLedger.held() && rowsLedger.held());

    let list = pageList();
    assert.equal(list[0], pages[0], "other pages pass through");
    assert.equal(pageList(), list, "the same list maps to the same result");
    const content = list[1].content;
    assert.equal(content.type.name, "SteamUiSettingsPage", "the page is wrapped once for every section");
    let tree = content.type(content.props);
    let kids = children(tree);
    assert.equal(kids[0].type, Hub, "the hub goes in front of Steam's Startup Movie section");
    assert.equal(kids[0].key, "hub");
    assert.equal(kids[1].type, StartupMovie);
    assert.equal(kids[2].type.name, "SteamUiSettingsSectionHost", "the rows go inside the Screensaver section");
    const inside = kids[2].type(kids[2].props);
    assert.equal(children(inside).at(-1).type, Rows, "appended after Steam's own rows");
    assert.equal(hub.status().lastOutcome, "rendered");
    assert.equal(rows.status().lastOutcome, "rendered inside its section");

    // The hub leaves: the claim stays for the rows, and the page draws without the hub.
    hubLedger.unwind();
    assert.equal(hub.attached(), false);
    assert.ok(kit.memoIntercepted(react, "steam-ui.settings-sections"), "the claim stays while a section is attached");
    list = pageList();
    tree = list[1].content.type(list[1].content.props);
    kids = children(tree);
    assert.equal(kids[0].type, StartupMovie, "a detached section is no longer drawn");
    // The last one leaves: the claim is released and Steam's list is Steam's again.
    rowsLedger.unwind();
    assert.equal(kit.memoIntercepted(react, "steam-ui.settings-sections"), false, "the last section releases the claim");
    assert.equal(react.useMemo, originalUseMemo, "useMemo is Steam's again");
    assert.equal(pageList(), pages);
    // A page already mounted with the wrapper passes Steam's own tree through.
    assert.equal(children(content.type(content.props))[0].type, StartupMovie);

    // With no Valve section to stand in front of, a section is last and says so.
    const lost = kit.registerSteamSettingsSection({
        page: "Customization",
        key: "lost",
        before: ["#Settings_Nowhere"],
        render: Hub,
    });
    const lostLedger = ledger();
    lost.attach(lostLedger, react, "/settings/customization");
    list = pageList();
    kids = children(list[1].content.type(list[1].content.props));
    assert.equal(kids.at(-1).type, Hub);
    assert.match(lost.status().lastOutcome, /not found; it is last/u);
    lostLedger.unwind();
    assert.equal(react.useMemo, originalUseMemo);
}

console.log(
    "UI flows: collection states, search drafts, first-show fetch, paging and focus return; acquire, item detail," +
        " manage list; Settings sections on one shared claim passed.",
);
