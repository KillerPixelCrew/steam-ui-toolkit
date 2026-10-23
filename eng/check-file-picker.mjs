// Exercise the emitted file picker's lifecycle against an inert Steam runtime and a scripted host.
//
// What matters is what the page promises a consumer: the picker settles exactly once, with the path
// the host validated or with null; the folder on screen is the one asked for last, however late an
// earlier listing answers; moving on cancels the listing in flight at the host; cancel, B, unmount
// and a consumer's AbortSignal all settle it as cancelled; a choice the host refuses is shown and
// never returned; a choice still being checked when the user moves on or closes is cancelled and
// its late answer dropped; and a folder with thousands of entries is drawn in pages that reach
// every row.
// This runs the emitted JavaScript with a hook runtime small enough to read beside it.
import assert from "node:assert/strict";
import { element, fragment, instantiate, loadAsset, tick } from "./check-harness.mjs";

const asset = loadAsset();

// A function component rendered by hand: hook slots persist across renders, a state setter
// schedules a re-render, effects run after the render that declared them and clean up on unmount.
const createRenderer = (react) => {
    let component = null;
    let props = null;
    let slots = [];
    let cursor = 0;
    let effects = [];
    let cleanups = [];
    let mounted = false;
    let tree = null;
    let rendering = false;
    let dirty = false;
    const render = () => {
        if (!mounted) return;
        rendering = true;
        do {
            dirty = false;
            cursor = 0;
            effects = [];
            tree = component(props);
        } while (dirty);
        rendering = false;
        for (const effect of effects) {
            const cleanup = effect();
            if (typeof cleanup === "function") cleanups.push(cleanup);
        }
    };
    Object.assign(react, {
        useState(initial) {
            const index = cursor++;
            if (!(index in slots)) slots[index] = initial;
            return [
                slots[index],
                (next) => {
                    const value = typeof next === "function" ? next(slots[index]) : next;
                    if (Object.is(value, slots[index])) return;
                    slots[index] = value;
                    if (rendering) dirty = true;
                    else render();
                },
            ];
        },
        useRef(initial) {
            const index = cursor++;
            if (!(index in slots)) slots[index] = { current: initial };
            return slots[index];
        },
        useEffect(effect, dependencies) {
            const index = cursor++;
            if (index in slots && Array.isArray(dependencies) && dependencies.length === 0) return;
            slots[index] = dependencies;
            effects.push(effect);
        },
    });
    return {
        mount(type, initialProps) {
            component = type;
            props = initialProps;
            slots = [];
            cleanups = [];
            mounted = true;
            render();
        },
        unmount() {
            mounted = false;
            tree = null;
            for (const cleanup of cleanups.splice(0)) cleanup();
        },
        get tree() {
            return tree;
        },
    };
};

const react = {
    createElement(type, props, ...children) {
        const { key = null, ...rest } = props ?? {};
        if (children.length) rest.children = children.flat();
        return element(type, rest, key);
    },
    isValidElement: (value) => !!value && typeof value === "object" && "props" in value,
};
const renderer = createRenderer(react);
const Focusable = () => null;
const Button = () => null;
const Primary = () => null;
const Modal = () => null;
const modals = [];
const ui = {
    react,
    focusable: Focusable,
    dialogButton: Button,
    dialogButtonPrimary: Primary,
    modalRoot: Modal,
    showModal: (modal) => modals.push(modal),
};

// The scripted host: every request is recorded and answered by hand, so a check decides when and in
// what order the host speaks. A cancel is recorded on the request it aborted.
const requests = [];
const host = {
    request(patchId, command, payload, _generation, signal) {
        const entry = { patchId, command, payload, cancelled: false };
        requests.push(entry);
        entry.promise = new Promise((resolve, reject) => {
            entry.resolve = resolve;
            entry.reject = reject;
            // Only flagged, not rejected: the real bridge rejects an aborted request, but a check
            // wants to answer it late anyway, as a worker that never saw the cancel would.
            if (signal) signal.addEventListener("abort", () => (entry.cancelled = true));
        });
        return entry.promise;
    },
};
const last = (command) => [...requests].reverse().find((entry) => entry.command === command);

const picker = instantiate(
    {
        window: {},
        request: (...args) => host.request(...args),
        showSteamModal: (uiArg, options) => {
            if (!uiArg?.showModal || !uiArg?.modalRoot) return false;
            modals.push(options);
            return true;
        },
        renderSteamUiSelectRow: (uiArg, props) =>
            uiArg.react.createElement(
                uiArg.dialogButton,
                {
                    key: props.key,
                    "data-title": props.title,
                    "aria-pressed": !!props.selected,
                    onClick: props.onClick,
                },
                props.status ?? null,
            ),
        steamUiKitStyle: () => element("style", {}),
        onSteamTriggers: (step) => (event) => step(event.detail.button === 8 ? 1 : -1),
    },
    fragment(asset, "file-picker.ts"),
    "({runSteamFilePicker, showSteamFilePicker, SteamFilePickerPageSize})",
);

// Every element under a node, in order, satisfying a predicate.
const find = (node, predicate, found = []) => {
    if (!react.isValidElement(node)) return found;
    if (predicate(node)) found.push(node);
    for (const child of [node.props?.children ?? []].flat()) find(child, predicate, found);
    return found;
};
const rows = (tree, pane) => {
    const list = find(tree, (node) => node.type === Focusable && node.props.className === pane)[0];
    assert.ok(list, `the ${pane} pane is drawn`);
    return find(list, (node) => node.type === Button && node.props["data-title"] !== undefined);
};
const button = (tree, label) => {
    const found = find(
        tree,
        (node) =>
            (node.type === Button || node.type === Primary) && node.props.children?.[0] === label,
    )[0];
    assert.ok(found, `a "${label}" button is drawn`);
    return found;
};
const root = (tree) =>
    find(
        tree,
        (node) => node.type === Focusable && node.props.className === "steam-ui-kit-picker",
    )[0];
const alertText = (tree) =>
    find(tree, (node) => node.props?.role === "alert")[0]?.props.children?.[0] ?? "";

// Opens a picker: the modal's render is mounted with a close that unmounts, as Steam's would.
const open = (options, run = picker.showSteamFilePicker) => {
    requests.length = 0;
    modals.length = 0;
    let settledWith;
    let settledTimes = 0;
    const promise = run(ui, options).then((value) => {
        settledTimes++;
        settledWith = value;
        return value;
    });
    const modal = modals[0];
    assert.ok(modal, "the picker opens a Steam modal");
    const close = () => renderer.unmount();
    renderer.mount(modal.render(close).type, { close });
    return {
        promise,
        modal,
        get settledTimes() {
            return settledTimes;
        },
        get settledWith() {
            return settledWith;
        },
    };
};

const places = {
    places: [
        { path: "C:\\", name: "Local disk (C:)", kind: "drive", detail: "" },
        { path: "E:\\", name: "SD card (E:)", kind: "drive", detail: "" },
    ],
};
const listing = (path, entries, parent = "", error = null) => ({ path, parent, entries, error });

// The picker passes its title to the shared modal frame; check-ui-kit covers the visible heading.

// Opening lists the places, then the start folder; the extensions a file picker asked for travel
// with every listing, and a folder picker asks for folders only. The modal is asked for its full
// size, Steam's own way of letting a dialog body grow past 600px, so the two panes keep their width.
{
    const session = open({
        mode: "file",
        extensions: [".exe", ".bat"],
        start: "D:\\Games",
        title: "Choose a program",
    });
    assert.equal(session.modal.wide, true, "the picker asks Steam's dialog for its full size");
    assert.equal(session.modal.title, "Choose a program");
    assert.equal(last("listPlaces").command, "listPlaces");
    last("listPlaces").resolve(places);
    await tick();
    const first = last("listFolder");
    assert.deepEqual(first.payload, { path: "D:\\Games", extensions: [".exe", ".bat"] });
    first.resolve(
        listing(
            "D:\\Games",
            [
                { name: "Doom", path: "D:\\Games\\Doom", folder: true },
                { name: "run.bat", path: "D:\\Games\\run.bat", folder: false },
            ],
            "D:\\",
        ),
    );
    await tick();
    assert.equal(
        rows(renderer.tree, "steam-ui-kit-picker-places").length,
        2,
        "both places are drawn",
    );
    assert.deepEqual(
        rows(renderer.tree, "steam-ui-kit-picker-files").map((row) => row.props["data-title"]),
        ["Doom", "run.bat"],
    );
    assert.equal(
        root(renderer.tree).props.onSecondaryButton,
        undefined,
        "a file picker has no 'use this folder'",
    );

    // Choosing a file asks the host whether it can still be returned, then settles with the host's
    // ordinary form of the path, not the row's text.
    rows(renderer.tree, "steam-ui-kit-picker-files")[1].props.onClick();
    const resolve = last("resolve");
    assert.deepEqual(resolve.payload, {
        path: "D:\\Games\\run.bat",
        kind: "file",
        extensions: [".exe", ".bat"],
    });
    resolve.resolve({ path: "D:\\Games\\run.bat", folder: false, error: null });
    assert.equal(await session.promise, "D:\\Games\\run.bat");
    assert.equal(session.settledTimes, 1);
    // Closing settled nothing twice: the unmount after a selection is not a cancellation.
    await tick();
    assert.equal(session.settledTimes, 1);
}

// A folder picker lists folders only, and "Use this folder" returns the validated folder on screen.
{
    const session = open({ mode: "folder" });
    assert.equal(session.modal.title, "Choose a folder", "the shared modal names the picker mode");
    last("listPlaces").resolve(places);
    await tick();
    assert.deepEqual(
        last("listFolder").payload,
        { path: "C:\\", extensions: [] },
        "without a start the first place opens, folders only",
    );
    last("listFolder").resolve(
        listing("C:\\", [{ name: "Emulation", path: "C:\\Emulation", folder: true }]),
    );
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    assert.equal(last("listFolder").payload.path, "C:\\Emulation");
    last("listFolder").resolve(listing("C:\\Emulation", [], "C:\\"));
    await tick();
    assert.equal(
        find(
            renderer.tree,
            (node) => node.type === "p" && node.props.children?.[0] === "This folder is empty.",
        ).length,
        1,
    );
    root(renderer.tree).props.onSecondaryButton();
    assert.deepEqual(last("resolve").payload, {
        path: "C:\\Emulation",
        kind: "folder",
        extensions: [],
    });
    last("resolve").resolve({ path: "C:\\Emulation", folder: true, error: null });
    assert.equal(await session.promise, "C:\\Emulation");
}

// A listing that answers after a later one was asked for is dropped, and the host was told to stop
// it the moment the user moved on.
{
    const session = open({ mode: "folder", start: "C:\\" });
    last("listPlaces").resolve(places);
    await tick();
    const a = last("listFolder");
    a.resolve(
        listing("C:\\", [
            { name: "A", path: "C:\\A", folder: true },
            { name: "B", path: "C:\\B", folder: true },
        ]),
    );
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    const slow = last("listFolder");
    assert.equal(slow.payload.path, "C:\\A");
    rows(renderer.tree, "steam-ui-kit-picker-files")[1].props.onClick();
    const fast = last("listFolder");
    assert.equal(fast.payload.path, "C:\\B");
    assert.equal(slow.cancelled, true, "moving on cancels the listing in flight at the host");
    fast.resolve(
        listing("C:\\B", [{ name: "b.txt", path: "C:\\B\\b.txt", folder: false }], "C:\\"),
    );
    await tick();
    assert.deepEqual(
        rows(renderer.tree, "steam-ui-kit-picker-files").map((row) => row.props["data-title"]),
        ["b.txt"],
    );
    // The slow host answers anyway, as a worker that did not see the cancel would.
    slow.resolve(
        listing("C:\\A", [{ name: "a.txt", path: "C:\\A\\a.txt", folder: false }], "C:\\"),
    );
    await tick();
    assert.deepEqual(
        rows(renderer.tree, "steam-ui-kit-picker-files").map((row) => row.props["data-title"]),
        ["b.txt"],
        "a stale listing never replaces the folder on screen",
    );
    assert.equal(
        find(renderer.tree, (node) => node.props?.role === "status")[0].props.children[0],
        "C:\\B",
    );
    // B settles as cancelled, once, and the modal closes.
    root(renderer.tree).props.onCancelButton();
    assert.equal(await session.promise, null);
    root(renderer.tree)?.props.onCancelButton?.();
    assert.equal(session.settledTimes, 1);
}

// A choice the host refuses is shown, the folder is listed again, and the picker stays open and
// unsettled; a later valid choice still goes through.
{
    const session = open({ mode: "file", start: "C:\\" }, picker.runSteamFilePicker);
    last("listPlaces").resolve(places);
    await tick();
    last("listFolder").resolve(
        listing("C:\\", [
            { name: "gone.exe", path: "C:\\gone.exe", folder: false },
            { name: "here.exe", path: "C:\\here.exe", folder: false },
        ]),
    );
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    // A second press while the first is checked asks nothing more.
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    assert.equal(requests.filter((entry) => entry.command === "resolve").length, 1);
    last("resolve").resolve({
        path: "C:\\gone.exe",
        folder: false,
        error: "This file no longer exists.",
    });
    await tick();
    assert.equal(session.settledTimes, 0, "a refused choice settles nothing");
    assert.equal(alertText(renderer.tree), "This file no longer exists.");
    const again = last("listFolder");
    assert.equal(again.payload.path, "C:\\", "the folder is listed again after a refusal");
    again.resolve(listing("C:\\", [{ name: "here.exe", path: "C:\\here.exe", folder: false }]));
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    last("resolve").resolve({ path: "C:\\here.exe", folder: false, error: null });
    assert.deepEqual(await session.promise, {
        outcome: "selected",
        path: "C:\\here.exe",
        folder: false,
    });
}

// A choice still being checked when the user moves on belongs to the folder left behind: the host
// is told to stop checking it, a late success never closes the picker or returns that path, and a
// late refusal never lands over the folder now on screen or lists it again. The folder on screen
// accepts a new choice while the old one is still pending, and that choice settles the picker once.
{
    const session = open({ mode: "file", start: "C:\\" }, picker.runSteamFilePicker);
    last("listPlaces").resolve(places);
    await tick();
    const rootEntries = [
        { name: "A", path: "C:\\A", folder: true },
        { name: "a.exe", path: "C:\\a.exe", folder: false },
    ];
    last("listFolder").resolve(listing("C:\\", rootEntries));
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[1].props.onClick();
    const lateSuccess = last("resolve");
    assert.equal(lateSuccess.payload.path, "C:\\a.exe");
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    assert.equal(lateSuccess.cancelled, true, "moving on cancels the choice at the host");
    last("listFolder").resolve(
        listing("C:\\A", [{ name: "b.exe", path: "C:\\A\\b.exe", folder: false }], "C:\\"),
    );
    await tick();
    // The new folder takes a choice although the old one has not answered yet.
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    const lateRefusal = last("resolve");
    assert.notEqual(
        lateRefusal,
        lateSuccess,
        "a choice in the new folder is asked while the old one is pending",
    );
    assert.equal(lateRefusal.payload.path, "C:\\A\\b.exe");
    lateSuccess.resolve({ path: "C:\\a.exe", folder: false, error: null });
    await tick();
    assert.equal(
        session.settledTimes,
        0,
        "a late success from the folder left behind settles nothing",
    );
    assert.ok(root(renderer.tree), "the picker stays open");
    assert.equal(
        find(renderer.tree, (node) => node.props?.role === "status")[0].props.children[0],
        "C:\\A",
    );
    button(renderer.tree, "Up").props.onClick();
    assert.equal(lateRefusal.cancelled, true, "going up cancels the pending choice at the host");
    const listings = requests.filter((entry) => entry.command === "listFolder").length;
    last("listFolder").resolve(listing("C:\\", rootEntries));
    await tick();
    lateRefusal.resolve({
        path: "C:\\A\\b.exe",
        folder: false,
        error: "This file no longer exists.",
    });
    await tick();
    assert.equal(
        alertText(renderer.tree),
        "",
        "a late refusal never lands over the folder that replaced it",
    );
    assert.equal(
        requests.filter((entry) => entry.command === "listFolder").length,
        listings,
        "a late refusal lists nothing again",
    );
    assert.equal(session.settledTimes, 0);
    // A choice in the folder on screen goes through as before.
    rows(renderer.tree, "steam-ui-kit-picker-files")[1].props.onClick();
    const valid = last("resolve");
    assert.notEqual(valid, lateRefusal, "a dropped choice no longer blocks the next one");
    valid.resolve({ path: "C:\\a.exe", folder: false, error: null });
    assert.deepEqual(await session.promise, {
        outcome: "selected",
        path: "C:\\a.exe",
        folder: false,
    });
    await tick();
    assert.equal(session.settledTimes, 1);
}

// Closing while a choice is being checked cancels it at the host and settles once as cancelled; the
// host's late success resurrects nothing. The same holds for a consumer's AbortSignal.
{
    const session = open({ mode: "file", start: "C:\\" });
    last("listPlaces").resolve(places);
    await tick();
    last("listFolder").resolve(
        listing("C:\\", [{ name: "a.exe", path: "C:\\a.exe", folder: false }]),
    );
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    const pending = last("resolve");
    root(renderer.tree).props.onCancelButton();
    assert.equal(pending.cancelled, true, "closing cancels the choice at the host");
    assert.equal(await session.promise, null);
    pending.resolve({ path: "C:\\a.exe", folder: false, error: null });
    await tick();
    assert.equal(session.settledTimes, 1, "a late success after closing settles nothing more");
    assert.equal(renderer.tree, null, "the modal stays closed");
}
{
    const controller = new AbortController();
    const session = open(
        { mode: "file", start: "C:\\", signal: controller.signal },
        picker.runSteamFilePicker,
    );
    last("listPlaces").resolve(places);
    await tick();
    last("listFolder").resolve(
        listing("C:\\", [{ name: "a.exe", path: "C:\\a.exe", folder: false }]),
    );
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    const pending = last("resolve");
    controller.abort();
    assert.equal(pending.cancelled, true, "the consumer's abort cancels the choice at the host");
    assert.deepEqual(await session.promise, { outcome: "cancelled" });
    pending.resolve({ path: "C:\\a.exe", folder: false, error: null });
    await tick();
    assert.equal(session.settledTimes, 1);
    assert.equal(renderer.tree, null);
}

// A folder that cannot be listed shows why, lists the places again so a pulled card disappears,
// keeps the other places reachable, and "Use this folder" refuses it.
{
    const session = open({ mode: "folder", start: "E:\\" });
    last("listPlaces").resolve(places);
    await tick();
    const sd = last("listFolder");
    assert.equal(sd.payload.path, "E:\\");
    sd.resolve(listing("E:\\", [{ name: "roms", path: "E:\\roms", folder: true }]));
    await tick();
    rows(renderer.tree, "steam-ui-kit-picker-files")[0].props.onClick();
    last("listFolder").resolve(
        listing("E:\\roms", [], "E:\\", "This drive is no longer available."),
    );
    await tick();
    assert.equal(alertText(renderer.tree), "This drive is no longer available.");
    const refreshed = last("listPlaces");
    assert.notEqual(refreshed, requests[0], "the places are asked for again after a listing error");
    refreshed.resolve({ places: places.places.slice(0, 1) });
    await tick();
    assert.equal(
        rows(renderer.tree, "steam-ui-kit-picker-places").length,
        1,
        "the pulled card is gone from the places",
    );
    button(renderer.tree, "Use this folder").props.onClick();
    assert.equal(
        last("resolve"),
        undefined,
        "an unlistable folder is never offered to the host as a choice",
    );
    assert.equal(session.settledTimes, 0);
    rows(renderer.tree, "steam-ui-kit-picker-places")[0].props.onClick();
    assert.equal(last("listFolder").payload.path, "C:\\", "another place can still be opened");
    renderer.unmount();
    assert.equal(await session.promise, null, "unmount settles as cancelled");
}

// A start the host cannot list falls back to the first place, without an error on screen.
{
    const session = open({ mode: "folder", start: "Z:\\missing" });
    last("listPlaces").resolve(places);
    await tick();
    last("listFolder").resolve(
        listing("Z:\\missing", [], "", "This drive is no longer available."),
    );
    await tick();
    assert.equal(last("listFolder").payload.path, "C:\\");
    last("listFolder").resolve(listing("C:\\", []));
    await tick();
    assert.equal(alertText(renderer.tree), "");
    renderer.unmount();
    await session.promise;
}

// A consumer's AbortSignal closes the picker and settles it as cancelled; an already aborted signal
// never opens one.
{
    const controller = new AbortController();
    const session = open({ mode: "file", signal: controller.signal });
    last("listPlaces").resolve(places);
    await tick();
    controller.abort();
    assert.equal(await session.promise, null);
    assert.equal(renderer.tree, null, "the modal closed");
    requests.length = 0;
    modals.length = 0;
    const aborted = new AbortController();
    aborted.abort();
    assert.equal(await picker.showSteamFilePicker(ui, { signal: aborted.signal }), null);
    assert.equal(modals.length, 0, "an aborted signal opens nothing");
}

// Steam's own modal cancellation (B at the modal, closing it) settles as cancelled once, and the
// unmount that follows settles nothing more.
{
    const session = open({ mode: "folder" });
    session.modal.onCancel();
    renderer.unmount();
    assert.equal(await session.promise, null);
    assert.equal(session.settledTimes, 1);
}

// Without Steam's modal components the picker reports a failure rather than hanging.
{
    const result = await picker.runSteamFilePicker({ react }, { mode: "file" });
    assert.equal(result.outcome, "failed");
    assert.equal(await picker.showSteamFilePicker({ react }, { mode: "file" }), null);
}

// A folder with thousands of entries is drawn in pages: every row is reachable through the page
// buttons and the triggers, the page is keyed so it takes focus fresh, and the host sent it whole.
{
    const session = open({ mode: "file", start: "D:\\roms" });
    last("listPlaces").resolve(places);
    await tick();
    const size = picker.SteamFilePickerPageSize;
    const entries = Array.from({ length: size * 2 + 7 }, (_, index) => ({
        name: `rom-${String(index).padStart(4, "0")}.chd`,
        path: `D:\\roms\\rom-${String(index).padStart(4, "0")}.chd`,
        folder: false,
    }));
    last("listFolder").resolve(listing("D:\\roms", entries, "D:\\"));
    await tick();
    const pane = () =>
        find(
            renderer.tree,
            (node) =>
                node.type === Focusable && node.props.className === "steam-ui-kit-picker-files",
        )[0];
    assert.equal(rows(renderer.tree, "steam-ui-kit-picker-files").length, size);
    assert.equal(pane().key, "D:\\roms#0");
    assert.equal(pane().props.autoFocus, true, "a new listing takes focus at its first row");
    button(renderer.tree, "Next page").props.onClick();
    assert.equal(
        rows(renderer.tree, "steam-ui-kit-picker-files")[0].props["data-title"],
        entries[size].name,
    );
    assert.equal(pane().key, "D:\\roms#1");
    root(renderer.tree).props.onButtonDown({ detail: { button: 8 } });
    assert.equal(
        rows(renderer.tree, "steam-ui-kit-picker-files").length,
        7,
        "the last page holds the rest",
    );
    assert.equal(
        rows(renderer.tree, "steam-ui-kit-picker-files")[6].props["data-title"],
        entries[entries.length - 1].name,
    );
    root(renderer.tree).props.onButtonDown({ detail: { button: 8 } });
    assert.equal(pane().key, "D:\\roms#2", "the last page stays");
    const status = find(renderer.tree, (node) => node.props?.role === "status").map(
        (node) => node.props.children[0],
    );
    assert.ok(
        status.includes(`Showing ${size * 2 + 1}-${entries.length} of ${entries.length}`),
        status.join(" | "),
    );
    button(renderer.tree, "Previous page").props.onClick();
    assert.equal(pane().key, "D:\\roms#1");
    // Entering a folder resets to its first page.
    button(renderer.tree, "Up").props.onClick();
    last("listFolder").resolve(
        listing("D:\\", [{ name: "roms", path: "D:\\roms", folder: true, link: true }]),
    );
    await tick();
    assert.equal(pane().key, "D:\\#0");
    assert.equal(
        find(renderer.tree, (node) => node.type === "span" && node.props.children?.[0] === "Link")
            .length,
        1,
        "a junction is shown as a link",
    );
    renderer.unmount();
    await session.promise;
}

console.log("File picker lifecycle, stale-listing, cancellation and paging checks passed.");
