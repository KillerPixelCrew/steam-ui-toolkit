// The one list of what an injected Steam UI asset is made of, and the one compile that makes it.
//
// The prelude this repository builds and every consumer's composed asset come from here, so the
// fragment order cannot drift between them. Importing this module runs nothing; a builder calls
// `compileSteamUiAsset` with its own fragment directories and its own TypeScript.
//
// Order:
//
//   types.ts    declarations only. It sits above the bundle marker and is erased from the emitted
//               asset, so it types the script and ships nothing.
//   bridge.ts   opens the IIFE and carries the orchestration: the reuse check, the request and
//               subscribe machinery, the gate registry and the publication of the window property.
//   ownership.ts, rpc.ts
//               the claim primitives and the RPC replies every gate builds on.
//   the other top-level fragments, sorted: the shared helpers.
//   gates/*.ts, sorted. Every gate registers itself with a top-level call.
//   components.ts
//               the Quick Access row host, after the gates so the asset reads as helpers, then gates,
//               then the rows they render.
//   each consumer directory's *.ts, sorted.
//   epilogue.ts returns the bridge's install result, so it follows every registration.
//
// Within a group the order is sorted, so the emitted asset is byte-stable whatever order the
// filesystem reports, and adding a fragment is a new file and nothing else. Every fragment after
// bridge.ts opens with a `// @fragment <label>` line that survives the compile and Prettier, so a
// check takes a whole fragment out of the emitted asset by name rather than by its neighbours.
import { spawnSync } from "node:child_process";
import { readdirSync } from "node:fs";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const toolkitRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

/** The toolkit's TypeScript fragments. */
export const toolkitSourceDirectory = join(
  toolkitRoot,
  "src",
  "SteamUiToolkit",
  "SteamUiAssets",
  "Source",
);

/** Everything above this line in bridge.ts types the script; the emitted asset starts after it. */
export const bundleMarker = "// @steam-ui-bundle-start";

const leading = ["types.ts", "bridge.ts", "ownership.ts", "rpc.ts"];
const trailing = ["components.ts", "epilogue.ts"];
// Fragments that carry no marker: types.ts is erased, and bridge.ts starts the asset.
const unmarked = ["types.ts", "bridge.ts"];

/**
 * Creates a stable fragment boundary retained in composed source and emitted JavaScript.
 * @param {string} label Toolkit-relative or consumer-prefixed fragment label.
 * @returns {string} A JavaScript line comment identifying the fragment.
 */
export const fragmentMarker = (label) => `// @fragment ${label}`;

const discover = (directory, exclude = []) => {
  let entries;
  try {
    entries = readdirSync(directory, { withFileTypes: true });
  } catch (error) {
    if (error.code === "ENOENT") return [];
    throw error;
  }
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(".ts") && !exclude.includes(entry.name))
    .map((entry) => join(directory, entry.name))
    .sort();
};

const labelled = (root, prefix) => (path) => ({
  path,
  label: prefix + relative(root, path).split(sep).join("/"),
});

/**
 * The ordered fragments of an asset: the toolkit's, with each extra directory's inserted after the
 * component host. Toolkit fragments are labelled by their path under Source/ ("ownership.ts",
 * "gates/audio.ts"); a consumer's by its path under the first extra directory, prefixed
 * "consumer/". A directory that does not exist contributes nothing.
 * @param {string[]} extraDirectories Consumer directories, in composition order.
 * @returns {{path: string, label: string}[]} Ordered source paths and stable fragment labels.
 */
export const steamUiFragments = (extraDirectories = []) => {
  const toolkit = labelled(toolkitSourceDirectory, "");
  const consumer = labelled(extraDirectories[0] ?? toolkitSourceDirectory, "consumer/");
  return [
    ...leading.map((name) => toolkit(join(toolkitSourceDirectory, name))),
    ...discover(toolkitSourceDirectory, [...leading, ...trailing]).map(toolkit),
    ...discover(join(toolkitSourceDirectory, "gates")).map(toolkit),
    toolkit(join(toolkitSourceDirectory, "components.ts")),
    ...extraDirectories.flatMap((directory) => discover(directory)).map(consumer),
    toolkit(join(toolkitSourceDirectory, "epilogue.ts")),
  ];
};

/**
 * Compiles the fragments to the emitted asset: type-stripped, unformatted, starting at the bundle
 * marker and closing the IIFE bridge.ts opens. `typescript` is the path of the caller's tsc.js.
 * Throws when the compile fails or erased a fragment marker: TypeScript drops the comments that lead
 * a declaration it erases, so a fragment must open with runtime code, not a `type` alias.
 * @param {object} options Build inputs; this function does not write generated repository files.
 * @param {string[]} [options.extraDirectories=[]] Consumer fragment directories.
 * @param {string} options.typescript Absolute path to the caller's tsc.js.
 * @returns {Promise<string>} Complete compiled JavaScript; temporary compiler files are removed.
 */
export const compileSteamUiAsset = async ({ extraDirectories = [], typescript }) => {
  const fragments = steamUiFragments(extraDirectories);
  const texts = await Promise.all(fragments.map(({ path }) => readFile(path, "utf8")));
  const source =
    fragments
      .map(({ label }, index) =>
        unmarked.includes(label) ? texts[index] : `\n${fragmentMarker(label)}\n${texts[index]}`,
      )
      .join("") + "\n})();\n";

  const temporary = await mkdtemp(join(tmpdir(), "steam-ui-asset-"));
  let compiled;
  try {
    const input = join(temporary, "input");
    const output = join(temporary, "output");
    await mkdir(input);
    await mkdir(output);
    const combined = join(input, "steam-ui.ts");
    await writeFile(combined, source, "utf8");
    const project = join(temporary, "tsconfig.json");
    await writeFile(
      project,
      JSON.stringify({
        // The toolkit's compiler settings, because its fragments are most of the program and the
        // type-stripping-only contract is its rule.
        extends: join(toolkitSourceDirectory, "tsconfig.json"),
        compilerOptions: { outDir: output, rootDir: input },
        files: [combined],
      }),
      "utf8",
    );
    // No shell: this is the running Node with an explicit script path, and a shell would only add
    // quoting hazards on paths that already contain spaces.
    const result = spawnSync(process.execPath, [typescript, "--project", project], {
      encoding: "utf8",
      maxBuffer: Infinity,
    });
    if (result.error) throw result.error;
    if (result.status !== 0) {
      throw new Error(`tsc failed:\n${`${result.stdout ?? ""}${result.stderr ?? ""}`.trim()}`);
    }
    compiled = await readFile(join(output, "steam-ui.js"), "utf8");
  } finally {
    await rm(temporary, { recursive: true, force: true });
  }

  const start = compiled.indexOf(bundleMarker);
  if (start < 0) {
    throw new Error(`bridge.ts must contain "${bundleMarker}" so the emitted asset has an exact start.`);
  }
  const asset = compiled.slice(start + bundleMarker.length).trimStart();
  const lost = fragments
    .filter(({ label }) => !unmarked.includes(label))
    .filter(({ label }) => !asset.includes(`${fragmentMarker(label)}\n`))
    .map(({ label }) => label);
  if (lost.length > 0) {
    throw new Error(
      `The compile erased the fragment marker of ${lost.join(", ")}. A fragment must open with ` +
        "runtime code: TypeScript drops the comments that lead a type declaration.",
    );
  }
  return asset;
};
