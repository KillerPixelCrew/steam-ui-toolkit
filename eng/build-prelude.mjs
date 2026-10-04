// Compiles the injected fragments to the JavaScript a consumer's asset is built on.
//
// The library ships TypeScript, and a consumer composes these fragments with its own before
// compiling the whole thing in one pass: the injected script is evaluated in a single CDP call, so
// it has to be one script. This build exists so everything shipped here can be checked HERE,
// against the bytes it emits, rather than only inside whatever consumes it. The fragment order and
// the compile live in steam-ui-fragments.mjs, which a consumer's builder imports too.
//
// It is also the proof that the bundle stands alone: it stopped compiling the moment the bridge
// still named a consumer's gates, which is how that coupling was found.
//
// Two outputs:
//
//   dist/prelude.js   the bridge, the ownership primitives, the RPC helpers, every gate and the
//                     component host, with the IIFE LEFT OPEN: a consumer's fragments, epilogue.ts
//                     and the close follow it
//   dist/steam-ui.js  the complete asset with epilogue.ts and the IIFE closed, which a consumer with
//                     no fragments of its own can inject as-is
//
// Type-stripping only: no bundling, no minification. The emitted script is meant to be read beside
// the page it is injected into.
//
//   node eng/build-prelude.mjs
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { compileSteamUiAsset, fragmentMarker } from "./steam-ui-fragments.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const preludePath = join(repositoryRoot, "dist", "prelude.js");
const completePath = join(repositoryRoot, "dist", "steam-ui.js");

const complete = await compileSteamUiAsset({
  typescript: join(repositoryRoot, "node_modules", "typescript", "lib", "tsc.js"),
});

// The open prelude is everything before the epilogue's marker, which leaves the IIFE open exactly
// where a consumer's fragments go.
const epilogue = complete.indexOf(fragmentMarker("epilogue.ts"));
if (epilogue < 0) {
  throw new Error("The compiled bundle has no epilogue.ts fragment.");
}
const prelude = complete.slice(0, complete.lastIndexOf("\n", epilogue) + 1).trimEnd() + "\n";

await mkdir(dirname(preludePath), { recursive: true });
await writeFile(preludePath, prelude, "utf8");
await writeFile(completePath, complete, "utf8");
console.log(`Prelude built: ${preludePath}`);
console.log(`Complete asset built: ${completePath}`);
