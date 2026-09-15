// What the emitted-asset checks share: loading the asset, cutting fragments out of it by the markers
// the emitter leaves, instantiating a gate over inert fixtures, and a React stand-in small enough to
// read beside the gate it drives.
//
// Every check runs the SHIPPED bytes rather than the TypeScript source, and the markers here are
// statement starts that survive both compositions: the prelude as tsc emits it and a consumer's
// asset after Prettier. Node built-ins only, so a check runs in an offline build with no packages.
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

/** The text from one marker up to the next occurrence of another, asserting both exist. */
export const slice = (asset, from, to) => {
  const start = asset.indexOf(from);
  const end = start < 0 ? -1 : asset.indexOf(to, start);
  assert.ok(start >= 0 && end > start, `the emitted asset must contain ${from} … ${to}`);
  return asset.slice(start, end);
};

/** The text from one marker up to the first gate after it, at whatever indentation was emitted. */
export const sliceToGate = (asset, from) => {
  const start = asset.indexOf(from);
  const gate = start < 0 ? -1 : asset.slice(start).search(/\n[ \t]*function create/u);
  assert.ok(start >= 0 && gate > 0, `the emitted asset must contain ${from} before a gate`);
  return asset.slice(start, start + gate);
};

/** One gate's factory, from its declaration to its registration. */
export const gateSource = (asset, factory, gateName) =>
  slice(asset, `function ${factory}()`, `registerGate("${gateName}"`);

/**
 * The ownership primitives, the RPC replies and the shared gate helpers, in emitted order. Gates are
 * instantiated over these real definitions rather than stand-ins, so a check exercises the claims the
 * asset actually makes.
 */
export const sharedFragments = (asset) => slice(asset, "const defineHidden", "const SteamUiIconShapes =");

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
