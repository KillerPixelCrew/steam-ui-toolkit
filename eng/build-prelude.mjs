// Compiles the prelude fragments to the JavaScript a consumer's asset is built on.
//
// The library ships TypeScript, and a consumer concatenates these fragments with its own before
// compiling the whole thing in one pass — the injected script is evaluated in a single CDP call, so
// it has to be one script. This build exists so the prelude can be checked HERE, against the bytes
// it emits, rather than only inside whatever consumes it.
//
// It is also the proof that the prelude stands alone: it stopped compiling the moment the bridge
// still named its consumer's gates, which is how that coupling was found.
//
// Type-stripping only: no bundling, no minification. The emitted script is meant to be read beside
// the page it is injected into.
//
//   node eng/build-prelude.mjs
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sourceDirectory = join(repositoryRoot, "src", "SteamUiToolkit", "SteamUiAssets", "Source");

// types.ts is declarations only and sits above the marker, so it types the compile and ships
// nothing. bridge.ts opens the IIFE; the shared helpers follow it.
const fragments = ["types.ts", "bridge.ts", "ownership.ts", "rpc.ts"].map((name) =>
  join(sourceDirectory, name),
);
const marker = "// @wsgm-bundle-start";
const outputPath = join(repositoryRoot, "dist", "prelude.js");

// Closed only for the compile. The emitted prelude leaves the IIFE open, because the consumer's
// fragments go inside it and the consumer closes it.
const compileEpilogue = "})();\n";

const temporary = await mkdtemp(join(tmpdir(), "steam-ui-toolkit-"));
let compiled;
try {
  const input = join(temporary, "input");
  const output = join(temporary, "output");
  await mkdir(input);
  await mkdir(output);
  const combined = join(input, "prelude.ts");
  const source =
    (await Promise.all(fragments.map((path) => readFile(path, "utf8")))).join("") + compileEpilogue;
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
  compiled = await readFile(join(output, "prelude.js"), "utf8");
} finally {
  await rm(temporary, { recursive: true, force: true });
}

const markerIndex = compiled.indexOf(marker);
if (markerIndex < 0) {
  throw new Error(`bridge.ts must contain "${marker}" so the emitted prelude has an exact start.`);
}

let body = compiled.slice(markerIndex + marker.length).trimStart();
const closing = body.lastIndexOf(compileEpilogue.trim());
if (closing < 0) {
  throw new Error("The compiled prelude did not end with the closing the build appended.");
}
body = body.slice(0, closing).trimEnd() + "\n";

await mkdir(dirname(outputPath), { recursive: true });
await writeFile(outputPath, body, "utf8");
console.log(`Prelude built: ${outputPath}`);
