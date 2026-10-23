import assert from "node:assert/strict";
import { createReact, helperFragments, instantiate, loadAsset } from "./check-harness.mjs";

const hooks = { states: [], index: 0 };
const react = createReact({
    useState(initial) {
        const slot = hooks.index++;
        if (!(slot in hooks.states)) hooks.states[slot] = initial;
        return [
            hooks.states[slot],
            (value) => {
                hooks.states[slot] = value;
            },
        ];
    },
    useEffect() {},
});
const ListBuilder = () => null;
const TextField = () => null;
const Button = () => null;
const api = instantiate({ window: {} }, helperFragments(loadAsset()), "{SteamUiResolutionEditor}");
const ui = {
    react,
    listBuilder: ListBuilder,
    textField: TextField,
    smallButton: Button,
    focusable: "Panel",
};
const row = {
    label: "Resolutions",
    choices: [
        { value: "2560x1600", label: "2560 × 1600 · native" },
        { value: "1760x1100", label: "1760 × 1100" },
    ],
    resolutionEditor: {
        removable: ["1760x1100"],
        nativeWidth: 2560,
        nativeHeight: 1600,
        restoreAvailable: true,
    },
};
const sent = [];
const render = (busy = false) => {
    hooks.index = 0;
    return api.SteamUiResolutionEditor({ ui, row, send: (value) => sent.push(value), busy });
};
const nodes = (tree) => [
    tree,
    ...(tree?.props?.children ?? []).flatMap((child) => (child ? nodes(child) : [])),
];
const button = (tree, name) =>
    nodes(tree).find((node) => node.type === Button && node.props.children[0] === name);
let tree = render();
assert.equal(
    nodes(tree).filter((node) => node.type === ListBuilder).length,
    1,
    "the editor uses the native list",
);
assert.equal(button(tree, "Remove").props.disabled, true, "native entries cannot be removed");
nodes(tree)
    .find((node) => node.type === ListBuilder)
    .props.onChange({ target: { value: "1760 × 1100" } });
assert.equal(sent.length, 0, "typing does not write");
tree = render();
assert.equal(button(tree, "Add").props.disabled, false);
button(tree, "Add").props.onClick();
assert.deepEqual(JSON.parse(JSON.stringify(sent.pop())), {
    operation: "add",
    width: 1760,
    height: 1100,
});
button(tree, "1760 × 1100").props.onClick();
assert.equal(sent.length, 0, "list selection does not write or activate a display mode");
tree = render();
assert.equal(button(tree, "Remove").props.disabled, false);
button(tree, "Remove").props.onClick();
assert.deepEqual(JSON.parse(JSON.stringify(sent.pop())), {
    operation: "remove",
    width: 1760,
    height: 1100,
});
button(tree, "Add 5% steps").props.onClick();
assert.deepEqual(JSON.parse(JSON.stringify(sent.pop())), {
    operation: "steps",
    width: 2560,
    height: 1600,
});
tree = render(true);
assert.ok(
    nodes(tree)
        .filter((node) => node.type === Button)
        .every((node) => node.props.disabled),
);
tree = render();
button(tree, "Restore original list").props.onClick();
assert.deepEqual(JSON.parse(JSON.stringify(sent.pop())), { operation: "restore" });
row.resolutionEditor.restoreAvailable = false;
assert.equal(button(render(), "Restore original list").props.disabled, true);
console.log(
    "Resolution editor: native list, explicit add/remove, preserved selection and percentage basis passed.",
);
