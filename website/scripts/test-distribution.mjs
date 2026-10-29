import { fileURLToPath } from "node:url";
import { npmRun } from "./npm-run.mjs";

// distribution is a release with a disk image, published-zip one with only a ZIP, and published-legacy the last release
// with a pre-release label and a build number in its file names, which stays the newest until 0.2.0 ships.
const fixtureName = process.argv[2] || "distribution";
if (!["distribution", "published-zip", "published-legacy"].includes(fixtureName)) {
  throw new Error("Choose the distribution, published-zip or published-legacy test fixture.");
}
const fixture = fileURLToPath(new URL(`../tests/fixtures/${fixtureName}.json`, import.meta.url));
const env = { ANNOTATOR_E2E_BROWSERS: "chromium", ...process.env, DISTRIBUTION_METADATA_FILE: fixture };
try {
  for (const script of ["build", "test", "test:e2e"]) npmRun(script, env);
} finally {
  // Never leave fake download links in the normal build output.
  npmRun("build", process.env);
}
