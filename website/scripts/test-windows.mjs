import { fileURLToPath } from "node:url";
import { npmRun } from "./npm-run.mjs";

const fixture = name => fileURLToPath(new URL(`../tests/fixtures/${name}.json`, import.meta.url));
// The Windows Store listing fixture, at the root with published Mac releases, then under /annotator/ without any.
const modes = [
  { SITE_URL: "", DISTRIBUTION_METADATA_FILE: fixture("distribution") },
  { SITE_URL: "https://dorlugasigal.github.io/annotator/", DISTRIBUTION_METADATA_FILE: "" },
];
try {
  for (const mode of modes) {
    const env = { ANNOTATOR_E2E_BROWSERS: "chromium", ...process.env, ...mode, WINDOWS_LISTING_FILE: fixture("windows-listing") };
    for (const script of ["build", "test", "test:e2e"]) npmRun(script, env);
  }
} finally {
  // Never leave the fixture listing in the normal build output.
  npmRun("build", process.env);
}
