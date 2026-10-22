import test from "node:test";
import assert from "node:assert/strict";
import {
  ARCH_LABELS, daily, featureUsage, githubDownloads, homebrewInstalls, jobHealth, niceMax, ranked, retentionRows, segments, smoothPath,
} from "../public/admin/admin-model.js";

const day = (date, app = {}, extra = {}) => ({
  day: date, app: { active: 0, newInstalls: 0, wau: 0, mau: 0, versions: {}, os: {}, architectures: {}, launches: {}, features: {}, ...app },
  web: { sessions: 0, routes: {}, referrers: {}, downloads: {} }, retention: {}, ...extra,
});

test("days without a stored summary are gaps, not zeros", () => {
  const points = daily([day("2026-09-26", { active: 1 })], "2026-09-27", 3, (s) => s.app.active);
  assert.deepEqual(points, [{ day: "2026-09-25", value: null }, { day: "2026-09-26", value: 1 }, { day: "2026-09-27", value: null }]);
  assert.deepEqual(segments(points).map((run) => run.map((point) => point.day)), [["2026-09-26"]]);
});

test("feature use is a share of active install-days, largest first", () => {
  const usage = featureUsage([
    day("2026-09-26", { active: 3, features: { drawing: { "1": 2, "2-5": 1 }, zoom: { "1": 1 } } }),
    day("2026-09-27", { active: 1, features: { drawing: { "1": 1 } } }),
  ]);
  assert.equal(usage.activeDays, 4);
  assert.deepEqual(usage.rows.map(({ key, value, share }) => [key, value, share]), [["drawing", 4, 1], ["zoom", 1, .25]]);
  assert.equal(usage.rows[0].label, "Drawing");
  assert.deepEqual(ranked({ b: 2, a: 2, none: 0 }).map((row) => row.key), ["a", "b"]);
});

test("Windows chips read as Windows, never as Apple silicon or Intel", () => {
  const rows = ranked({ arm64: 3, "windows-arm64": 2, "windows-x86_64": 1 }, ARCH_LABELS);
  assert.deepEqual(rows.map((row) => row.label), ["Apple silicon", "Windows on Arm", "Windows x64"]);
});

test("GitHub downloads split the disk image from the ZIP and compare with a week earlier", () => {
  const assets = (dmg, zip) => ({ "v0.1.0-alpha.10/annotator.dmg": dmg, "v0.1.0-alpha.10/annotator.zip": zip, "v0.1.0-alpha.9/annotator.zip": 3 });
  const downloads = githubDownloads([
    { day: "2026-09-19", total: 5, assets: assets(1, 1) },
    { day: "2026-09-20", total: 7, assets: assets(2, 2) },
    { day: "2026-09-27", total: 12, assets: assets(5, 4), capturedAt: "2026-09-27T10:00:00Z" },
  ]);
  assert.deepEqual(downloads.kinds, { dmg: 5, zip: 7 });
  assert.equal(downloads.change, 5);
  assert.equal(downloads.since, "2026-09-20");
  assert.equal(downloads.capturedAt, "2026-09-27T10:00:00Z");
  assert.deepEqual(downloads.releases.map((row) => row.label), ["0.1.0-alpha.10", "0.1.0-alpha.9"]);
  assert.equal(githubDownloads([{ day: "2026-09-27", total: 1, assets: {} }]).change, null);
  assert.equal(githubDownloads([]), null);
});

test("Homebrew without counts reads as not reported, never as zero installs", () => {
  assert.deepEqual(homebrewInstalls([]), { status: "pending" });
  assert.deepEqual(homebrewInstalls([{ day: "2026-09-27", counts: {} }]), { status: "unreported", day: "2026-09-27" });
  assert.equal(homebrewInstalls([{ day: "2026-09-27", counts: { "30d": 4 } }]).status, "reported");
});

test("the nightly job is late after 26 hours and stalled if it never finished", () => {
  const now = new Date("2026-09-27T12:00:00Z");
  const finished = (finishedAt, errors = []) => ({ status: "finished", finishedAt, errors, aggregated: [], removed: 0 });
  assert.equal(jobHealth(null, now).level, "missing");
  assert.equal(jobHealth(finished("2026-09-27T00:15:00Z"), now).level, "ok");
  assert.equal(jobHealth(finished("2026-09-26T08:00:00Z"), now).level, "stale");
  assert.equal(jobHealth(finished("2026-09-27T00:15:00Z", ["GitHub releases returned HTTP 403."]), now).level, "errors");
  assert.equal(jobHealth({ status: "started", startedAt: "2026-09-27T11:50:00Z" }, now).level, "running");
  assert.equal(jobHealth({ status: "started", startedAt: "2026-09-27T00:15:00Z" }, now).level, "stalled");
});

test("retention lists the newest cohorts first with their rate", () => {
  const rows = retentionRows([
    day("2026-09-26", {}, { retention: { d7: { cohort: "2026-09-19", size: 2, retained: 1 } } }),
    day("2026-09-27", {}, { retention: { d7: { cohort: "2026-09-20", size: 4, retained: 3 }, d30: { cohort: "2026-08-28", size: 3, retained: 1 } } }),
  ]);
  assert.deepEqual(rows.map((row) => [row.period, row.cohort, row.rate]), [[7, "2026-09-20", .75], [30, "2026-08-28", 1 / 3], [7, "2026-09-19", .5]]);
});

test("the chart axis never shrinks below 4, and the curve never dips past its points", () => {
  assert.deepEqual([0, 1, 4, 5, 23, 180].map(niceMax), [4, 4, 4, 5, 25, 200]);
  const points = [{ x: 0, y: 100 }, { x: 10, y: 0 }, { x: 20, y: 100 }, { x: 30, y: 100 }];
  const ys = smoothPath(points).match(/-?\d+(\.\d+)?/g).map(Number).filter((_, index) => index % 2 === 1);
  assert.ok(Math.min(...ys) >= 0 && Math.max(...ys) <= 100, `control points stay inside the data range: ${ys}`);
  assert.equal(smoothPath([{ x: 5, y: 7 }]), "M5,7");
  assert.equal(smoothPath([]), "");
});
