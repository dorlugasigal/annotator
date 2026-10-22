import { fileURLToPath } from "node:url";
import { npmRun } from "./npm-run.mjs";

const fixtureName = process.argv[2] || "distribution";
if (!["distribution", "published-alpha"].includes(fixtureName)) {
  throw new Error("Choose the distribution or published-alpha test fixture.");
}
const fixture = fileURLToPath(new URL(`../tests/fixtures/${fixtureName}.json`, import.meta.url));
const env = { ANNOTATOR_E2E_BROWSERS: "chromium", ...process.env, DISTRIBUTION_METADATA_FILE: fixture };
try {
  for (const script of ["build", "test", "test:e2e"]) npmRun(script, env);
} finally {
  // Never leave fake download links in the normal build output.
  npmRun("build", process.env);
}
