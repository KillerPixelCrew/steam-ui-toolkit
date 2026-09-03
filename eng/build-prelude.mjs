// Compiles the injected fragments to the JavaScript a consumer's asset is built on.
//
// The library ships TypeScript, and a consumer concatenates these fragments with its own before
// compiling the whole thing in one pass — the injected script is evaluated in a single CDP call, so
// it has to be one script. This build exists so everything shipped here can be checked HERE,
// against the bytes it emits, rather than only inside whatever consumes it.
//
// It is also the proof that the bundle stands alone: it stopped compiling the moment the bridge
// still named a consumer's gates, which is how that coupling was found. Now that the gates and the
// component host live here, this is the compile that proves they need nothing from a consumer.
//
// Two outputs:
//
//   dist/prelude.js   the bridge, the ownership primitives, the RPC helpers, every gate and the
//                     component host, with the IIFE LEFT OPEN — a consumer appends its own
//                     fragments and epilogue.ts and closes it
//   dist/steam-ui.js  the same with epilogue.ts appended and the IIFE closed: the complete asset a
//                     consumer with no fragments of its own can inject as-is
//
// Type-stripping only: no bundling, no minification. The emitted script is meant to be read beside
// the page it is injected into.
//
//   node eng/build-prelude.mjs
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sourceDirectory = join(repositoryRoot, "src", "SteamUiToolkit", "SteamUiAssets", "Source");

// types.ts is declarations only and sits above the marker, so it types the compile and ships
// nothing. bridge.ts opens the IIFE; the shared helpers follow it. Gates are discovered from their
// directory and sorted, so adding one is a new file and nothing else; components.ts is the row
// host and comes last so the asset reads as helpers, then gates, then the rows they render.
const gates = (await readdir(join(sourceDirectory, "gates"), { withFileTypes: true }))
  .filter((entry) => entry.isFile() && entry.name.endsWith(".ts"))
  .map((entry) => join(sourceDirectory, "gates", entry.name))
  .sort();
const fragments = [
  ...["types.ts", "bridge.ts", "ownership.ts", "rpc.ts"].map((name) => join(sourceDirectory, name)),
  ...gates,
  join(sourceDirectory, "components.ts"),
];
const epiloguePath = join(sourceDirectory, "epilogue.ts");
const marker = "// @steam-ui-bundle-start";
const preludePath = join(repositoryRoot, "dist", "prelude.js");
const completePath = join(repositoryRoot, "dist", "steam-ui.js");

// The compile closes the IIFE the same way a consumer would: epilogue, then the close.
const closing = "})();\n";

const temporary = await mkdtemp(join(tmpdir(), "steam-ui-toolkit-"));
let compiled;
try {
  const input = join(temporary, "input");
  const output = join(temporary, "output");
  await mkdir(input);
  await mkdir(output);
  const combined = join(input, "steam-ui.ts");
  const source =
    (await Promise.all([...fragments, epiloguePath].map((path) => readFile(path, "utf8")))).join(
      "",
    ) + closing;
  await writeFile(combined, source, "utf8");
  const project = join(temporary, "tsconfig.json");
  await writeFile(
    project,
    JSON.stringify({
      extends: join(sourceDirectory, "tsconfig.json"),
      compilerOptions: { outDir: output, rootDir: input },
      files: [combined],
    }),
    "utf8",
  );
  // No shell: this is `node` with an explicit script path, and a shell would only add quoting
  // hazards on paths that already contain spaces.
  const result = spawnSync(
    "node",
    [join(repositoryRoot, "node_modules", "typescript", "lib", "tsc.js"), "--project", project],
    { cwd: repositoryRoot, encoding: "utf8" },
  );
  if (result.status !== 0) {
    throw new Error(`tsc failed:\n${`${result.stdout ?? ""}${result.stderr ?? ""}`.trim()}`);
  }
  compiled = await readFile(join(output, "steam-ui.js"), "utf8");
} finally {
  await rm(temporary, { recursive: true, force: true });
}

const markerIndex = compiled.indexOf(marker);
if (markerIndex < 0) {
  throw new Error(`bridge.ts must contain "${marker}" so the emitted prelude has an exact start.`);
}

const complete = compiled.slice(markerIndex + marker.length).trimStart();

// The open prelude is everything before the epilogue's return. That return is the last statement
// of the bundle, so cutting at it leaves the IIFE open exactly where a consumer's fragments go.
const returnIndex = complete.lastIndexOf("return installResult;");
if (returnIndex < 0) {
  throw new Error("The compiled bundle did not end with epilogue.ts's return.");
}
const prelude = complete.slice(0, returnIndex).trimEnd() + "\n";

await mkdir(dirname(preludePath), { recursive: true });
await writeFile(preludePath, prelude, "utf8");
await writeFile(completePath, complete, "utf8");
console.log(`Prelude built: ${preludePath}`);
console.log(`Complete asset built: ${completePath}`);
