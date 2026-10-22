import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const cwd = fileURLToPath(new URL("..", import.meta.url));
const posix = path => path.replaceAll("\\", "/");
const restored = { script: "build", fixture: "", site: "", windows: "" };

function runHelper(helper, fail = "", cleanupFails = false) {
  const dir = mkdtempSync(join(tmpdir(), "annotator-ci-"));
  try {
    const log = join(dir, "commands.jsonl");
    const fakeNpm = `import { appendFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
const script = process.argv[3];
const fixture = process.env.DISTRIBUTION_METADATA_FILE || "";
const site = process.env.SITE_URL || "";
const windows = process.env.WINDOWS_LISTING_FILE || "";
appendFileSync(process.env.CI_TEST_LOG, JSON.stringify({ script, fixture, site, windows }) + "\\n");
if (script === "test:distribution") {
  process.exit(spawnSync(process.execPath, ["scripts/test-distribution.mjs"], {
    env: process.env, stdio: "inherit"
  }).status ?? 1);
}
if (script === process.env.CI_TEST_FAIL && (fixture || site || windows)) process.exit(23);
if (process.env.CI_TEST_CLEANUP_FAIL === "true" && script === "build" && !fixture && !site && !windows) process.exit(24);
`;
    if (process.platform === "win32") {
      // The helpers start npm through cmd.exe on Windows, which finds this npm.cmd first on PATH.
      writeFileSync(join(dir, "fake-npm.mjs"), fakeNpm);
      writeFileSync(join(dir, "npm.cmd"), `@"${process.execPath}" "${join(dir, "fake-npm.mjs")}" %*\r\n`);
    } else {
      writeFileSync(join(dir, "npm"), `#!${process.execPath}\n${fakeNpm}`, { mode: 0o755 });
    }
    const result = spawnSync(process.execPath, [`scripts/${helper}.mjs`], {
      cwd,
      env: { ...process.env, PATH: `${dir}${delimiter}${process.env.PATH}`,
        SITE_URL: "", DISTRIBUTION_METADATA_FILE: "", WINDOWS_LISTING_FILE: "", CI_TEST_LOG: log,
        CI_TEST_FAIL: fail, CI_TEST_CLEANUP_FAIL: String(cleanupFails) },
      encoding: "utf8",
    });
    assert.ifError(result.error);
    const commands = readFileSync(log, "utf8").trim().split("\n").map(JSON.parse);
    return { status: result.status, commands };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test("distribution helper runs one published root mode then restores closed output", () => {
  const { status, commands } = runHelper("test-distribution");
  assert.equal(status, 0);
  assert.deepEqual(commands.map(c => c.script), ["build", "test", "test:e2e", "build"]);
  assert.ok(commands.slice(0, 3).every(c => posix(c.fixture).endsWith("/tests/fixtures/distribution.json") && !c.site && !c.windows));
  assert.deepEqual(commands.at(-1), restored);
});

test("pages helper runs exactly closed and published subpath modes then restores", () => {
  const { status, commands } = runHelper("test-pages");
  assert.equal(status, 0);
  const browsers = commands.filter(c => c.script === "test:e2e");
  assert.equal(browsers.length, 2);
  assert.ok(browsers.every(c => c.site === "https://dorlugasigal.github.io/annotator/" && !c.windows));
  assert.equal(browsers[0].fixture, "");
  assert.ok(posix(browsers[1].fixture).endsWith("/tests/fixtures/distribution.json"));
  assert.deepEqual(commands.at(-1), restored);
});

test("windows helper runs the listing fixture at the root with releases and under /annotator/ without, then restores", () => {
  const { status, commands } = runHelper("test-windows");
  assert.equal(status, 0);
  assert.deepEqual(commands.map(c => c.script), ["build", "test", "test:e2e", "build", "test", "test:e2e", "build"]);
  assert.ok(commands.slice(0, 6).every(c => posix(c.windows).endsWith("/tests/fixtures/windows-listing.json")));
  assert.ok(commands.slice(0, 3).every(c => posix(c.fixture).endsWith("/tests/fixtures/distribution.json") && !c.site));
  assert.ok(commands.slice(3, 6).every(c => !c.fixture && c.site === "https://dorlugasigal.github.io/annotator/"));
  assert.deepEqual(commands.at(-1), restored);
});

for (const helper of ["test-distribution", "test-pages", "test-windows"]) {
  for (const stage of ["build", "test", "test:e2e"]) {
    test(`${helper} preserves ${stage} failure and attempts restoration`, () => {
      const { status, commands } = runHelper(helper, stage);
      assert.notEqual(status, 0);
      assert.deepEqual(commands.at(-1), restored);
    });
  }
  test(`${helper} exposes restoration failure`, () => {
    assert.notEqual(runHelper(helper, "", true).status, 0);
  });
}
