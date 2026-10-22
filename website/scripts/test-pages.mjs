import { npmRun } from "./npm-run.mjs";

const env = { ANNOTATOR_E2E_BROWSERS: "chromium", ...process.env, SITE_URL: "https://dorlugasigal.github.io/annotator/" };
try {
  for (const script of ["build", "test", "test:e2e", "test:distribution"]) npmRun(script, env);
} finally {
  npmRun("build", process.env);
}
