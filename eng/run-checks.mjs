// Builds the prelude and runs every emitted-asset check against it, stopping at the first failure.
// Each check runs in its own process, because several install globals a fixture needs.
//
//   node eng/run-checks.mjs               build dist/prelude.js, then check it
//   node eng/run-checks.mjs <asset path>  check an asset composed elsewhere, without building
import { spawnSync } from "node:child_process";
import { readdirSync } from "node:fs";
import { join } from "node:path";
import { repositoryRoot } from "./check-harness.mjs";

// Every eng/check-*.mjs is a check: a new one runs by existing, with no list to forget it in.
const checks = readdirSync(join(repositoryRoot, "eng"))
  .filter((name) => /^check-.+\.mjs$/u.test(name) && name !== "check-harness.mjs")
  .sort();

const run = (script, args) =>
  spawnSync(process.execPath, [join(repositoryRoot, "eng", script), ...args], { stdio: "inherit" })
    .status ?? 1;

let asset = process.argv[2];
if (!asset) {
  if (run("build-prelude.mjs", []) !== 0) {
    console.error("FAILED: build-prelude.mjs");
    process.exit(1);
  }
  asset = join(repositoryRoot, "dist", "prelude.js");
}
for (const check of checks) {
  const status = run(check, [asset]);
  if (status !== 0) {
    console.error(`FAILED: ${check}`);
    process.exit(status);
  }
}
console.log(`All ${checks.length} discovered emitted-asset checks passed.`);
