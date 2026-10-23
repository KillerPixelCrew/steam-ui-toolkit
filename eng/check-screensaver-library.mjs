import assert from "node:assert/strict";
import { fragment, instantiate, loadAsset } from "./check-harness.mjs";

const scopes = new Map();
let current;
const element = (type, props, ...children) => ({ type, props: props ?? {}, children });
const react = {
    createElement: element,
    useSyncExternalStore: (_subscribe, read) => read(),
    useState(initial) {
        const scope = current,
            index = scope.cursor++;
        if (!(index in scope.values))
            scope.values[index] = typeof initial === "function" ? initial() : initial;
        return [
            scope.values[index],
            (value) => {
                scope.values[index] =
                    typeof value === "function" ? value(scope.values[index]) : value;
            },
        ];
    },
    useEffect(effect, deps) {
        const scope = current,
            index = scope.cursor++;
        const previous = scope.effects[index];
        if (previous && deps.every((value, at) => value === previous.deps[at])) return;
        previous?.cleanup?.();
        scope.effects[index] = { effect, deps };
        scope.pending.push(index);
    },
};
const draw = (component, props, key = component.name) => {
    const scope = scopes.get(key) ?? { values: [], effects: [], pending: [], cursor: 0 };
    scopes.set(key, scope);
    scope.cursor = 0;
    const old = current;
    current = scope;
    const tree = component(props);
    for (const index of scope.pending.splice(0))
        scope.effects[index].cleanup = scope.effects[index].effect();
    current = old;
    return tree;
};
const unmount = () => {
    for (const scope of scopes.values()) for (const effect of scope.effects) effect?.cleanup?.();
    scopes.clear();
};
const nodes = (tree) =>
    !tree || typeof tree !== "object" ? [] : [tree, ...(tree.children ?? []).flatMap(nodes)];
const action = (tree, label) =>
    nodes(tree)
        .filter((node) => node.type === "actions")
        .flatMap((node) => node.props.actions)
        .find((item) => item.label === label);
const tick = async () => {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
};
let state = { sources: [], selectedId: "" };
let modal,
    closed = 0,
    choice = { outcome: "cancelled" };
const mutations = [],
    reads = [],
    pickers = [];
const ui = { react, dialogButton: "button", textField: "input", focusable: "focusable" };
const library = instantiate(
    {
        AbortController,
        showSteamModal: (_ui, options) => {
            modal = options;
            return true;
        },
        runSteamFilePicker: async (_ui, options) => {
            pickers.push(options);
            return choice;
        },
        showSteamUiConfirm: () => true,
        renderSteamSettingRow: (_ui, row, _draft, _change, activate) =>
            element("row", { row, activate }),
        renderSteamUiActions: (_ui, actions) => element("actions", { actions }),
        renderSteamUiGrid: (_ui, cards) => element("grid", {}, ...cards),
        renderSteamUiCard: (_ui, props) => element("card", props),
        renderSteamUiEmpty: (_react, text) => element("empty", { text }),
        renderSteamUiBanner: (_ui, props) => element("banner", props),
        renderSteamUiBox: (_react, title, ...children) => element("box", { title }, ...children),
        renderSteamUiDetail: (_ui, props) =>
            element("detail", props, ...props.main, ...props.aside),
        renderSteamUiImagePreview: (_ui, image, fill) => element("image", { image, fill }),
        renderSteamUiToolbar: (_ui, ...children) => element("toolbar", {}, ...children),
        renderSteamUiTool: (_ui, _label, control) => control,
        getState: () => state,
        ui,
        act: async (action, payload, signal) => {
            if (action === "thumbnail") {
                reads.push({ id: payload.id, signal });
                return "data:image/jpeg;base64,/9j/2Q==";
            }
            mutations.push({ action, ...payload });
            return null;
        },
    },
    fragment(loadAsset(), "screensaver-library.ts"),
    "createScreensaverLibrary({getUi:()=>ui,getState,local:{subscribe:()=>()=>{},revision:()=>0},act,ready:()=>true})",
);
const summary = library.render();
assert.equal(summary.props.row.buttonLabel, "Add sources");
assert.equal(reads.length, 0, "idle settings summary must not load any cover");
assert.equal(library.open(), true);
const close = () => {
    closed++;
    unmount();
};
const manager = modal.render(close);
let tree = draw(manager.type, manager.props);
assert.ok(action(tree, "Add image folder"));
choice = { outcome: "selected", path: "D:\\Photos\\Holidays" };
action(tree, "Add image folder").onClick();
await tick();
tree = draw(manager.type, manager.props);
assert.equal(nodes(tree).find((node) => node.type === "input").props.value, "Holidays");
assert.equal(mutations.length, 0, "choosing a folder must not register it before confirmation");
action(tree, "Back").onClick();
tree = draw(manager.type, manager.props);
assert.equal(mutations.length, 0, "Back from the add form writes nothing");
action(tree, "Add image folder").onClick();
await tick();
tree = draw(manager.type, manager.props);
nodes(tree)
    .find((node) => node.type === "input")
    .props.onChange({ target: { value: "My holidays" } });
tree = draw(manager.type, manager.props);
action(tree, "Add slideshow").onClick();
await tick();
assert.deepEqual(mutations, [
    { action: "addImages", folder: "D:\\Photos\\Holidays", name: "My holidays" },
]);
state = {
    sources: [
        {
            id: "photos",
            name: "My holidays",
            type: "images",
            available: true,
            hasPreview: true,
            previewRevision: "one",
            imageCount: 20,
            intervalSeconds: 15,
            shuffle: true,
            fill: false,
        },
    ],
    selectedId: "photos",
};
tree = draw(manager.type, manager.props);
const cardElement = nodes(tree).find(
    (node) => typeof node.type === "function" && node.type.name === "SourceCard",
);
let card = draw(cardElement.type, cardElement.props, "card");
await tick();
card = draw(cardElement.type, cardElement.props, "card");
assert.equal(card.props.badge.text, "Selected");
assert.match(card.props.image, /^data:image\/jpeg;base64,/);
assert.deepEqual(
    reads.map((item) => item.id),
    ["photos"],
);
draw(cardElement.type, cardElement.props, "card");
assert.equal(reads.length, 1, "unchanged cover identity must not reread the file");
card.props.onActivate();
tree = draw(manager.type, manager.props);
assert.ok(
    nodes(tree).some(
        (node) => typeof node.type === "function" && node.type.name === "SourceEditor",
    ),
);
tree.props.onCancelButton();
tree = draw(manager.type, manager.props);
assert.ok(
    nodes(tree).some((node) => node.type === "grid"),
    "nested Back returns to the source library",
);
assert.equal(closed, 0);
library.dispose();
assert.equal(closed, 1);
assert.ok(
    reads.every((item) => item.signal.aborted),
    "closing the manager cancels read-only cover requests",
);
assert.ok(
    pickers.every((item) => item.signal.aborted),
    "closing the manager cancels its folder pickers",
);
console.log(
    "Screensaver library: folder-first add/Back/confirmation, lazy covers, selection, nested Back and disposal passed.",
);
