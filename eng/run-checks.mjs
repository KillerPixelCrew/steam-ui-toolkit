// Builds the prelude and runs every emitted-asset check against it, stopping at the first failure.
// Each check runs in its own process, because several install globals a fixture needs.
//
//   node eng/run-checks.mjs               build dist/prelude.js, then check it
//   node eng/run-checks.mjs <asset path>  check an asset composed elsewhere, without building
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import { repositoryRoot } from "./check-harness.mjs";

const checks = [
  "check-ownership-claims.mjs",
  "check-power-profile.mjs",
  "check-startup.mjs",
  "check-service-gates.mjs",
  "check-navigation-panel.mjs",
  "check-pages.mjs",
  "check-storage.mjs",
  "check-library.mjs",
  "check-home-carousel.mjs",
  "check-screensaver.mjs",
];

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
console.log(`All ${checks.length} emitted-asset checks passed.`);
