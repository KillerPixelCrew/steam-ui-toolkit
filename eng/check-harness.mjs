// What the emitted-asset checks share: loading the asset, cutting fragments out of it by the
// `// @fragment <label>` markers steam-ui-fragments.mjs leaves, instantiating a gate over inert
// fixtures, and a React stand-in small enough to read beside the gate it drives.
//
// Every check runs the SHIPPED bytes rather than the TypeScript source. The fragment markers survive
// both compositions, the prelude as tsc emits it and a consumer's asset after Prettier, so a check
// names the fragments it needs instead of slicing between whatever happens to sit next to them.
// Node built-ins only, so a check runs in an offline build with no packages.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

/** Reads a file by its path from the repository root. */
export const readSource = (path) => readFileSync(join(repositoryRoot, path), "utf8");

/** The asset named on the command line, or the prelude this repository builds. */
export const loadAsset = (path = process.argv[2]) =>
  readFileSync(path ?? join(repositoryRoot, "dist", "prelude.js"), "utf8");

const fragmentMarkers = (asset) => [...asset.matchAll(/^[ \t]*\/\/ @fragment (\S+)[ \t]*$/gmu)];

/** The labels of the asset's fragments in emitted order, such as "ownership.ts" or "gates/audio.ts". */
export const fragmentLabels = (asset) => fragmentMarkers(asset).map((marker) => marker[1]);

/**
 * One whole fragment by its label, from its marker to the next, at whatever indentation was emitted.
 * "bridge.ts" opens the asset and carries no marker, so it is the text before the first one: the
 * IIFE it opens is left open.
 */
export const fragment = (asset, label) => {
  const markers = fragmentMarkers(asset);
  if (label === "bridge.ts") {
    assert.ok(markers.length > 0, "the emitted asset must contain fragments after the bridge");
    return asset.slice(0, markers[0].index);
  }
  const at = markers.findIndex((marker) => marker[1] === label);
  assert.ok(at >= 0, `the emitted asset must contain the ${label} fragment`);
  const end = at + 1 < markers.length ? markers[at + 1].index : asset.length;
  return asset.slice(markers[at].index, end);
};

const parses = (code) => {
  try {
    new Function(code);
    return true;
  } catch (error) {
    if (error instanceof SyntaxError) return false;
    throw error;
  }
};

/**
 * The whole statement that opens with `from` in `source`. It ends at the first line-ending `;` or
 * `}` after which the parser accepts it, so it is complete whatever follows it: an inner line end
 * leaves a bracket, string or comment open and does not parse.
 */
export const statement = (source, from) => {
  const start = source.indexOf(from);
  assert.ok(start >= 0, `the emitted asset must contain ${from}`);
  for (const end of source.slice(start).matchAll(/[;}](?=[ \t]*(?:\r?\n|$))/gu)) {
    const text = source.slice(start, start + end.index + 1);
    if (parses(text)) return text;
  }
  assert.fail(`${from} must open a complete statement`);
};

/**
 * The whole declaration of `name` in `source`, a `const`, `let` or `function` at the shallowest
 * depth that declares it, which must be unique there.
 */
export const declaration = (source, name) => {
  const found = [
    ...source.matchAll(new RegExp(`^([ \\t]*)(?:(?:const|let) ${name} =|function ${name}\\()`, "gmu")),
  ];
  assert.ok(found.length > 0, `the emitted asset must declare ${name}`);
  const depth = Math.min(...found.map((match) => match[1].length));
  const outermost = found.filter((match) => match[1].length === depth);
  assert.equal(outermost.length, 1, `${name} must be declared once at its depth`);
  return statement(source.slice(outermost[0].index + depth), outermost[0][0].slice(depth));
};

/**
 * Named declarations taken whole from one whole fragment, joined in the order given. A check names
 * what it instantiates, so a declaration added, moved or renamed beside them changes nothing it
 * reads, and one it names that is gone fails by name.
 */
export const declarations = (asset, label, names) => {
  const source = fragment(asset, label);
  return names.map((name) => declaration(source, name)).join("\n");
};

/** Several whole fragments, joined in the order given. */
export const fragments = (asset, labels) => labels.map((label) => fragment(asset, label)).join("\n");

/**
 * The labels of the toolkit's top-level helper fragments in emitted order: everything after the
 * bridge that is not a gate, the component host, the epilogue or a consumer's fragment.
 */
export const helperLabels = (asset) =>
  fragmentLabels(asset).filter(
    (label) => !label.includes("/") && label !== "components.ts" && label !== "epilogue.ts",
  );

/**
 * One gate fragment, whole, without its top-level registrations, so a check constructs each gate
 * itself over its own fixtures.
 */
export const gateSource = (asset, label) => {
  const source = fragment(asset, label);
  const gates = source.replace(/^[ \t]*registerGate\("[^"]+", [A-Za-z]+\(\)\);[ \t]*\r?$/gmu, "");
  assert.notEqual(gates, source, `the ${label} fragment must register its gate`);
  return gates;
};

/**
 * The ownership primitives, the RPC replies, the file picker and the shared gate helpers. Gates are
 * instantiated over these real definitions rather than stand-ins, so a check exercises the claims the
 * asset actually makes.
 */
export const sharedFragments = (asset) =>
  fragments(asset, ["ownership.ts", "rpc.ts", "file-picker.ts", "gate-helpers.ts"]);

/**
 * Evaluates emitted code with named globals in scope and returns an expression over it. A global must
 * not share a name with anything the code declares.
 */
export const instantiate = (globals, code, result) =>
  new Function(...Object.keys(globals), `${code}\nreturn ${result};`)(...Object.values(globals));

export const ElementMarker = Symbol("element");
export const MemoType = Symbol.for("react.memo");
export const Fragment = Symbol.for("react.fragment");

/** A plain element the stand-in React recognizes. */
export const element = (type, props, key = null) => ({
  [ElementMarker]: true,
  type,
  props: props ?? {},
  key,
});

/**
 * A React stand-in with the APIs gates use. Nothing reconciles: a render is a call of an element's
 * type. `singleChild` keeps one child unwrapped as React does; `cloneReplacesChildren` gives a clone
 * an empty child list when none is passed, which some fixtures were written against.
 */
export const createReact = ({ singleChild = false, cloneReplacesChildren = false, ...extra } = {}) => ({
  Fragment,
  createElement(type, props, ...children) {
    const { key = null, ...rest } = props ?? {};
    if (children.length) rest.children = singleChild && children.length === 1 ? children[0] : children;
    return element(type, rest, key);
  },
  cloneElement: (source, props, ...children) =>
    element(
      source.type,
      children.length || cloneReplacesChildren
        ? { ...source.props, ...props, children }
        : { ...source.props, ...props },
      source.key,
    ),
  isValidElement: (value) => !!value && typeof value === "object" && value[ElementMarker] === true,
  Children: {
    toArray: (children) =>
      (Array.isArray(children) ? children : children === undefined ? [] : [children]).filter(
        (child) => child !== null && child !== undefined && child !== false,
      ),
  },
  memo: (type, compare) => ({ $$typeof: MemoType, type, compare: compare ?? null }),
  ...extra,
});

/** Gives a function the source text a gate fingerprints it by. */
export const withSource = (fn, source) => {
  Object.defineProperty(fn, "toString", { value: () => source });
  return fn;
};

/** Every element under a node, in order, that satisfies a predicate. */
export const find = (react, node, predicate, found = []) => {
  if (!react.isValidElement(node)) return found;
  if (predicate(node)) found.push(node);
  react.Children.toArray(node.props?.children).forEach((child) => find(react, child, predicate, found));
  return found;
};

/** Whether an element's function type, or the type a memo wraps, has this name. */
export const named = (name) => (node) =>
  !!node &&
  typeof node === "object" &&
  ((typeof node.type === "function" && node.type.name === name) ||
    (node.type?.$$typeof === MemoType && node.type.type?.name === name));

/** Lets pending promise continuations and immediates run. */
export const tick = () => new Promise((resolve) => setImmediate(resolve));

/** Hook slots that persist across renders, reset to the first slot before each render. */
export const createHooks = () => {
  const slots = [];
  let cursor = 0;
  return {
    useState(initial) {
      const index = cursor++;
      if (!(index in slots)) slots[index] = initial;
      return [
        slots[index],
        (value) => {
          slots[index] = value;
        },
      ];
    },
    useRef(initial) {
      const index = cursor++;
      if (!(index in slots)) slots[index] = { current: initial };
      return slots[index];
    },
    reset() {
      cursor = 0;
    },
  };
};

/**
 * A host whose next property definition or deletion throws once `failNext()` has been called. A
 * check builds the object a gate claims on with it, so the gate's release can be made to fail.
 */
export const failingHost = (target = {}) => {
  let fail = false;
  const refuse = () => {
    fail = false;
    throw new TypeError("release refused by the fixture");
  };
  const host = new Proxy(target, {
    defineProperty(object, key, descriptor) {
      if (fail) refuse();
      return Reflect.defineProperty(object, key, descriptor);
    },
    deleteProperty(object, key) {
      if (fail) refuse();
      return Reflect.deleteProperty(object, key);
    },
  });
  return {
    host,
    failNext() {
      fail = true;
    },
  };
};

/**
 * Removal must be retryable: `failOnce` makes the gate's next release fail, so the first `remove()`
 * has to report the failure and stay installed rather than forget what it still holds, and the
 * second has to release what is left and succeed.
 */
export const assertRemoveRetries = (gate, failOnce, name = "gate") => {
  failOnce();
  const first = gate.remove();
  assert.equal(first.ok, false, `${name}: a failed release must be reported`);
  if (typeof gate.status === "function" && "installed" in gate.status()) {
    assert.equal(gate.status().installed, true, `${name}: a failed release must leave the gate installed`);
  }
  const second = gate.remove();
  assert.equal(second.ok, true, `${name}: the next remove must retry the release`);
  assert.notEqual(second.absent, true, `${name}: the retry must not answer absent`);
  if (typeof gate.status === "function" && "installed" in gate.status()) {
    assert.equal(gate.status().installed, false, `${name}: a successful retry must uninstall`);
  }
};
