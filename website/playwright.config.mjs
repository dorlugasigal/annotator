import {visitors} from "./tests/e2e/visitors.mjs";
import { defineConfig, devices } from "@playwright/test";
import { siteConfig } from "./site-config.mjs";

const base = siteConfig().base;
const port = process.env.ANNOTATOR_E2E_PORT || "4178";
if (!/^\d+$/.test(port) || Number(port) < 1024 || Number(port) > 65535) {
  throw new Error("ANNOTATOR_E2E_PORT must be a port between 1024 and 65535.");
}

export default defineConfig({
  testDir: "./tests/e2e",
  testMatch: "**/*.spec.mjs",
  fullyParallel: true,
  workers: process.env.CI ? 2 : 4,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  outputDir: "../build/website-e2e",
  reporter: [["list"], ["html", { outputFolder: "../build/website-e2e-report", open: "never" }]],
  use: {
    baseURL: `http://127.0.0.1:${port}${base}`,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: `npm run preview -- --port ${port}`,
    url: `http://127.0.0.1:${port}${base}`,
    reuseExistingServer: false,
    timeout: 30_000,
  },
  // Fixture and Pages modes change URLs and download data, not rendering, so
  // they set ANNOTATOR_E2E_BROWSERS=chromium; root mode covers every engine.
  projects: [
    // Desktop Chrome reports Windows; with a Windows listing that changes which install method shows first, so the default visitor is a Mac and tests that need Windows say so (tests/e2e/visitors.mjs).
    { name: "chromium", use: { ...devices["Desktop Chrome"], userAgent: visitors.mac } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } },
    { name: "mobile", use: { ...devices["iPhone 13"] } },
  ].filter(project => !process.env.ANNOTATOR_E2E_BROWSERS ||
    process.env.ANNOTATOR_E2E_BROWSERS.split(",").includes(project.name)),
});
