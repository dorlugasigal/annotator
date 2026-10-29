import test from "node:test";
import assert from "node:assert/strict";
import {
  ARCH_LABELS, addDays, compare, compareVersions, copiesOn, daily, downloadKind, featureRows, featureUsage, githubDownloads,
  homebrewInstalls, jobHealth, newestReleaseBy, niceMax, overview, period, platformDay, pooledRetention, ranked, retentionRows, segments,
  smoothPath, sumDays, versionOf, versionUse, windowDays,
} from "../public/admin/admin-model.js";

function day(date, app = {}, { web = {}, retention = {}, generatedAt = `${addDays(date, 1)}T00:15:00.000Z` } = {}) {
  return {
    day: date, generatedAt,
    app: { active: 0, newInstalls: 0, wau: 0, mau: 0, versions: {}, os: {}, architectures: {}, launches: {}, features: {}, ...app },
    web: { sessions: 0, routes: {}, referrers: {}, downloads: {}, ...web }, retention,
  };
}
const byDayOf = (summaries) => new Map(summaries.map((summary) => [summary.day, summary]));
const copies = (count, versions = {}, extra = {}) => ({ count, versions, os: {}, architectures: {}, ...extra });

test("days without a stored summary are gaps, not zeros", () => {
  const points = daily([day("2026-09-26", { active: 1 })], "2026-09-27", 3, (s) => s.app.active);
  assert.deepEqual(points, [{ day: "2026-09-25", value: null }, { day: "2026-09-26", value: 1 }, { day: "2026-09-27", value: null }]);
  assert.deepEqual(segments(points).map((run) => run.map((point) => point.day)), [["2026-09-26"]]);
});

test("a period is full UTC days that end yesterday, next to the same number of days before", () => {
  const span = period("2026-09-24T10:00:00Z", 7);
  assert.equal(span.today, "2026-09-24");
  assert.equal(span.end, "2026-09-23");
  assert.deepEqual([span.days[0], span.days.at(-1), span.days.length], ["2026-09-17", "2026-09-23", 7]);
  assert.deepEqual([span.previous[0], span.previous.at(-1)], ["2026-09-10", "2026-09-16"]);
  assert.equal(period("2026-09-24T00:05:00Z", 30).previous[0], "2026-07-26");
});

test("sums leave out missing days, an unknown day makes them unknown, and a comparison needs both sides", () => {
  const byDay = byDayOf([day("2026-09-22", { active: 2 }), day("2026-09-23", { active: 3 })]);
  assert.deepEqual(sumDays(byDay, ["2026-09-21", "2026-09-22", "2026-09-23"], (s) => s.app.active), { value: 5, covered: 2 });
  assert.deepEqual(sumDays(byDay, ["2026-09-20"], (s) => s.app.active), { value: null, covered: 0 });
  assert.equal(sumDays(byDay, ["2026-09-22", "2026-09-23"], (s) => (s.day === "2026-09-23" ? null : 1)).value, null);
  assert.deepEqual(compare(12, 8), { previous: 8, diff: 4, ratio: .5, trend: "up" });
  assert.equal(compare(3, 0).ratio, null);
  assert.equal(compare(4, 4).trend, "same");
  assert.equal(compare(5, null), null);
  assert.equal(compare(undefined, 5), null);
});

test("older summaries read as Mac until a day has a Windows system", () => {
  assert.deepEqual(platformDay(day("2026-09-23", { active: 3, newInstalls: 1, os: { "macOS 26": 3 } })),
    { macos: { active: 3, newInstalls: 1 }, windows: { active: 0, newInstalls: 0 } });
  assert.deepEqual(platformDay(day("2026-09-23", { active: 3, newInstalls: 1, os: { "macOS 26": 2, "Windows 11": 1 } })),
    { macos: { active: 2, newInstalls: null }, windows: { active: 1, newInstalls: null } });
  const split = { macos: { active: 1, newInstalls: 0 }, windows: { active: 2, newInstalls: 1 } };
  assert.equal(platformDay(day("2026-09-23", { platforms: split })), split);
});

test("copies by platform come from the summary, or from a fully counted window with no Windows system", () => {
  const byDay = byDayOf(windowDays("2026-09-23", 7).map((date) => day(date, { active: 1, os: { "macOS 26": 1 }, wau: 4, mau: 9 })));
  assert.deepEqual(copiesOn(byDay, "2026-09-23", 7), { total: 4, macos: 4, windows: 0, details: null });
  byDay.delete("2026-09-20");
  assert.deepEqual(copiesOn(byDay, "2026-09-23", 7), { total: 4, macos: null, windows: null, details: null });
  const unique = { d7: { macos: copies(3), windows: copies(1) }, d30: { macos: copies(7), windows: copies(2) } };
  byDay.set("2026-09-23", day("2026-09-23", { wau: 4, mau: 9, unique }));
  assert.deepEqual([copiesOn(byDay, "2026-09-23", 7).windows, copiesOn(byDay, "2026-09-23", 30).macos], [1, 7]);
  assert.equal(copiesOn(byDay, "2026-09-23", 30).total, 9);
  assert.equal(copiesOn(byDay, "2026-09-01", 7), null);
});

test("versions sort in release order, so a stable release follows its betas", () => {
  const sorted = ["0.2.0-alpha.1", "0.1.0", "unknown", "0.1.0-beta.10", "v0.1.1", "0.1.0-beta.2", "0.1.0-alpha.9"].sort(compareVersions);
  assert.deepEqual(sorted, ["unknown", "0.1.0-alpha.9", "0.1.0-beta.2", "0.1.0-beta.10", "0.1.0", "v0.1.1", "0.2.0-alpha.1"]);
  assert.equal(compareVersions("v0.1.0-beta.17", "0.1.0-beta.17"), 0);
  assert.equal(versionOf("0.1.0-beta.17 (35)"), "0.1.0-beta.17");
});

test("copies on the newest release include newer builds, by platform", () => {
  const unique = {
    macos: copies(4, { "0.1.0-beta.17 (35)": 2, "0.1.0-beta.16 (34)": 1, "0.1.0-beta.18 (36)": 1 }),
    windows: copies(2, { "0.1.0-beta.9 (27)": 2 }),
  };
  const use = versionUse(unique, "0.1.0-beta.17");
  assert.deepEqual([use.newest, use.fromGitHub, use.on, use.of, use.share], ["0.1.0-beta.17", true, 3, 6, .5]);
  assert.deepEqual(use.platforms, { macos: { on: 3, of: 4 }, windows: { on: 0, of: 2 } });
  assert.deepEqual(use.rows.map((row) => [row.label, row.value, row.current]),
    [["0.1.0-beta.18", 1, true], ["0.1.0-beta.17", 2, true], ["0.1.0-beta.16", 1, false], ["0.1.0-beta.9", 2, false]]);
  assert.deepEqual([versionUse(unique, null).newest, versionUse(unique, null).fromGitHub], ["0.1.0-beta.18", false]);
});

test("GitHub downloads split the Mac disk image, the Mac ZIP and the Windows ZIPs over the period and the one before", () => {
  assert.deepEqual(["v1/annotator-1-2-universal.dmg", "v1/annotator-1-2-universal.zip", "v1/Annotator-1-windows-arm64.zip",
    "v1/Annotator-1-windows-x64.zip"].map(downloadKind), ["macDiskImage", "macZip", "windowsZip", "windowsZip"]);
  const assets = (dmg, macZip, windowsZip) => ({
    "v0.1.0-beta.17/annotator-0.1.0-beta.17-35-universal.dmg": dmg,
    "v0.1.0-beta.17/annotator-0.1.0-beta.17-35-universal.zip": macZip,
    "v0.1.0-beta.17/Annotator-0.1.0-beta.17-windows-x64.zip": windowsZip,
    "v0.1.0/annotator-0.1.0-40-universal.zip": 1,
    "v0.1.0-beta.9/annotator-0.1.0-beta.9-27-universal.zip": 3,
  });
  const snap = (date, files, capturedAt) => ({ day: date, total: Object.values(files).reduce((a, b) => a + b, 0), assets: files,
    ...(capturedAt ? { capturedAt } : {}) });
  const snapshots = [snap("2026-09-13", assets(1, 1, 0)), snap("2026-09-20", assets(2, 2, 3)), snap("2026-09-26", assets(4, 3, 5)),
    snap("2026-09-27", assets(6, 4, 9), "2026-09-27T10:00:00Z")];
  const downloads = githubDownloads(snapshots, 7);
  assert.equal(downloads.since, "2026-09-20");
  assert.equal(downloads.capturedAt, "2026-09-27T10:00:00Z");
  assert.deepEqual(downloads.kinds.macDiskImage, { total: 6, gained: 4, previous: 1, daily: [null, null, null, null, null, null, 2] });
  assert.deepEqual([downloads.kinds.macZip.total, downloads.kinds.macZip.gained, downloads.kinds.macZip.previous], [8, 2, 1]);
  assert.deepEqual([downloads.kinds.windowsZip.total, downloads.kinds.windowsZip.gained, downloads.kinds.windowsZip.previous], [9, 6, 3]);
  assert.deepEqual(downloads.releases.map((row) => row.label), ["0.1.0", "0.1.0-beta.17", "0.1.0-beta.9"]);
  assert.equal(downloads.newest, "0.1.0");
  // Without a snapshot exactly a week back, the gain starts at the nearest older one and has nothing to compare with.
  const gap = githubDownloads(snapshots.filter((snapshot) => snapshot.day !== "2026-09-20"), 7);
  assert.deepEqual([gap.since, gap.kinds.macDiskImage.gained, gap.kinds.macDiskImage.previous], ["2026-09-13", 5, null]);
  const young = githubDownloads(snapshots.slice(2), 7);
  assert.deepEqual([young.since, young.kinds.macDiskImage.gained], ["2026-09-26", 2]);
  assert.deepEqual([githubDownloads(snapshots.slice(3), 7).since, githubDownloads(snapshots.slice(3), 7).kinds.macDiskImage.gained], [null, null]);
  assert.equal(githubDownloads([]), null);
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

test("feature rows list every feature, unused ones too, with the share in the days before", () => {
  const current = [day("2026-09-23", { active: 4, features: { drawing: { "1": 3 }, zoom: { "2-5": 1 } } })];
  const rows = featureRows(current, [day("2026-09-16", { active: 2, features: { drawing: { "1": 1 } } })]);
  assert.equal(rows.activeDays, 4);
  assert.equal(rows.rows.length, 10);
  assert.deepEqual(rows.rows.slice(0, 3).map((row) => [row.key, row.share, row.previousShare]),
    [["drawing", .75, .5], ["zoom", .25, 0], ["coverOrEffect", 0, 0]]);
  assert.equal(featureRows(current).rows[0].previousShare, null);
});

test("Windows chips read as Windows, never as Apple silicon or Intel", () => {
  const rows = ranked({ arm64: 3, "windows-arm64": 2, "windows-x86_64": 1 }, ARCH_LABELS);
  assert.deepEqual(rows.map((row) => row.label), ["Apple silicon", "Windows on Arm", "Windows x64"]);
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

test("retention lists the newest groups first and pools the rate over a period", () => {
  const summaries = [
    day("2026-09-26", {}, { retention: { d7: { cohort: "2026-09-19", size: 2, retained: 1 } } }),
    day("2026-09-27", {}, { retention: { d7: { cohort: "2026-09-20", size: 4, retained: 3 }, d30: { cohort: "2026-08-28", size: 3, retained: 1 } } }),
  ];
  assert.deepEqual(retentionRows(summaries).map((row) => [row.period, row.cohort, row.rate]),
    [[7, "2026-09-20", .75], [30, "2026-08-28", 1 / 3], [7, "2026-09-19", .5]]);
  const byDay = byDayOf(summaries);
  assert.deepEqual(pooledRetention(byDay, ["2026-09-26", "2026-09-27"], "d7"),
    { size: 6, retained: 4, rate: 4 / 6, first: "2026-09-19", last: "2026-09-20" });
  assert.equal(pooledRetention(byDay, ["2026-09-25"], "d7"), null);
});

/** Two weeks of counts: the 7 days before the period, then a busier period, and a little of today. */
function twoWeeks() {
  const previous = windowDays("2026-09-16", 7).map((date, index) => day(date,
    { active: 2, newInstalls: index === 3 ? 1 : 0, wau: 4, mau: 6, os: { "macOS 26": 2 }, features: { drawing: { "1": 1 } } },
    { web: { sessions: 10, downloads: { stable: 1 } } }));
  const current = windowDays("2026-09-23", 7).map((date, index) => day(date, {
    active: 3, newInstalls: index % 3 === 0 ? 1 : 0, wau: 5, mau: 8, os: { "macOS 26": 2, "Windows 11": 1 },
    platforms: { macos: { active: 2, newInstalls: index % 3 === 0 ? 1 : 0 }, windows: { active: 1, newInstalls: 0 } },
    features: { drawing: { "1": 3 }, zoom: { "1": 1 } },
  }, { web: { sessions: 20, routes: { home: 20 }, referrers: { github: 20 }, downloads: { stable: 3, preview: 1 } } }));
  current.at(-1).app.unique = {
    d7: {
      macos: copies(4, { "0.1.0-beta.17 (35)": 3, "0.1.0-beta.16 (34)": 1 }, { os: { "macOS 26": 4 }, architectures: { arm64: 4 } }),
      windows: copies(1, { "0.1.0-beta.16 (34)": 1 }, { os: { "Windows 11": 1 }, architectures: { "windows-x86_64": 1 } }),
    },
    d30: { macos: copies(7), windows: copies(1) },
  };
  const today = day("2026-09-24", { active: 2, unique: { d7: { macos: copies(5, { "0.1.0-beta.17 (35)": 5 }), windows: copies(1, { "0.1.0-beta.17 (35)": 1 }) } } },
    { generatedAt: "2026-09-24T09:00:00.000Z" });
  const github = ["2026-09-10", "2026-09-17", "2026-09-24"].map((date, index) => ({
    day: date, total: 0, assets: { "v0.1.0-beta.17/annotator-0.1.0-beta.17-35-universal.dmg": [2, 5, 12][index] },
  }));
  return { generatedAt: "2026-09-24T09:30:00Z", summaries: [...previous, ...current, today], github, homebrew: [], health: null };
}

test("the overview compares a fully counted period with the one before it", () => {
  const view = overview(twoWeeks(), 7);
  assert.equal(view.comparable, true);
  assert.equal(view.covered, 7);
  assert.equal(view.stillCounting, false);
  assert.deepEqual([view.people.copies.value, view.people.copies.change.previous, view.people.copies.change.trend], [5, 4, "up"]);
  assert.deepEqual([view.people.perDay.value, view.people.perDay.change.previous], [3, 2]);
  assert.deepEqual(view.people.today, { active: 2, countedAt: "2026-09-24T09:00:00.000Z" });
  assert.deepEqual([view.platforms.macos.copies.value, view.platforms.windows.copies.value], [4, 1]);
  // The week before had no Windows system on any day, so its copies were all Macs.
  assert.deepEqual([view.platforms.macos.copies.change.previous, view.platforms.windows.copies.change.previous], [4, 0]);
  assert.deepEqual([view.platforms.windows.perDay.value, view.platforms.macos.newInstalls.value], [1, 3]);
  assert.deepEqual(view.platforms.windows.architectures.map((row) => row.label), ["Windows x64"]);
  assert.equal(view.everWindows, true);
  const { visits, clicks, diskImages, optIns } = view.funnel;
  assert.deepEqual([visits.value, visits.change.previous, clicks.value, clicks.change.previous], [140, 70, 28, 7]);
  assert.deepEqual([clicks.rate, clicks.previousRate], [.2, .1]);
  assert.deepEqual([diskImages.value, diskImages.change.previous, optIns.value, optIns.change.previous], [7, 3, 3, 1]);
  assert.equal(visits.daily.length, 7);
  assert.deepEqual([view.versions.newest, view.versions.on, view.versions.of], ["0.1.0-beta.17", 3, 5]);
  assert.deepEqual([view.versions.today.on, view.versions.today.of], [6, 6]);
  assert.deepEqual(view.features.rows.slice(0, 2).map((row) => [row.key, row.share, row.previousShare]), [["drawing", 1, .5], ["zoom", 1 / 3, 0]]);
  assert.deepEqual(view.website.tracks.map((row) => [row.label, row.value]), [["Stable", 21], ["Testing preview", 7]]);
  assert.deepEqual([view.chart[0].day, view.chart[0].previousDay, view.chart[0].previous, view.chart[0].windows], ["2026-09-17", "2026-09-10", 2, 1]);
});

test("a period is held to the newest release out by its last day, and a later release is shown with today's count", () => {
  assert.equal(newestReleaseBy([{ day: "2026-09-22", assets: { "v0.1.0-beta.16/a.dmg": 1, "v0.1.0-beta.9/a.dmg": 1 } }], "2026-09-23"), "0.1.0-beta.16");
  assert.equal(newestReleaseBy([{ day: "2026-09-24", assets: { "v0.1.0-beta.17/a.dmg": 1 } }], "2026-09-23"), null);
  const data = twoWeeks();
  const unique = data.summaries.find((summary) => summary.day === "2026-09-23").app.unique;
  unique.d7.macos.versions = { "0.1.0-beta.16 (34)": 3, "0.1.0-beta.15 (33)": 1 };
  unique.d7.windows.versions = { "0.1.0-beta.16 (34)": 1 };
  data.github = [
    { day: "2026-09-22", total: 2, assets: { "v0.1.0-beta.16/annotator-0.1.0-beta.16-34-universal.dmg": 2 } },
    { day: "2026-09-24", total: 4, assets: { "v0.1.0-beta.16/annotator-0.1.0-beta.16-34-universal.dmg": 3,
      "v0.1.0-beta.17/annotator-0.1.0-beta.17-35-universal.dmg": 1 } },
  ];
  const { versions } = overview(data, 7);
  assert.deepEqual([versions.newest, versions.on, versions.of, versions.later], ["0.1.0-beta.16", 4, 5, "0.1.0-beta.17"]);
  assert.deepEqual([versions.today.newest, versions.today.on, versions.today.of], ["0.1.0-beta.17", 6, 6]);
  // Copies that ran a release GitHub listed only later still count it as out during the period.
  data.github = [data.github[1]];
  assert.deepEqual([overview(data, 7).versions.newest, overview(data, 7).versions.later], ["0.1.0-beta.16", "0.1.0-beta.17"]);
});

test("a short history, a missing night or a day counted early never reads as growth", () => {
  const data = twoWeeks();
  const short = overview({ ...data, summaries: data.summaries.slice(7) }, 7);
  assert.equal(short.comparable, false);
  assert.equal(short.people.copies.value, 5);
  assert.equal(short.people.copies.change, null);
  assert.equal(short.funnel.visits.change, null);
  assert.equal(short.features.rows[0].previousShare, null);
  const gap = overview({ ...data, summaries: data.summaries.filter((summary) => summary.day !== "2026-09-23") }, 7);
  assert.deepEqual([gap.covered, gap.comparable, gap.asOf, gap.people.copies.value], [6, false, "2026-09-22", 5]);
  const early = twoWeeks();
  early.summaries.find((summary) => summary.day === "2026-09-23").generatedAt = "2026-09-23T18:00:00.000Z";
  assert.equal(overview(early, 7).stillCounting, true);
  const empty = overview({ generatedAt: "2026-09-24T09:30:00Z", summaries: [], github: [], homebrew: [] }, 30);
  assert.deepEqual([empty.hasData, empty.people.copies.value, empty.versions, empty.github, empty.everWindows], [false, null, null, null, false]);
});

test("the chart axis never shrinks below 4, and the curve never dips past its points", () => {
  assert.deepEqual([0, 1, 4, 5, 23, 180].map(niceMax), [4, 4, 4, 5, 25, 200]);
  const points = [{ x: 0, y: 100 }, { x: 10, y: 0 }, { x: 20, y: 100 }, { x: 30, y: 100 }];
  const ys = smoothPath(points).match(/-?\d+(\.\d+)?/g).map(Number).filter((_, index) => index % 2 === 1);
  assert.ok(Math.min(...ys) >= 0 && Math.max(...ys) <= 100, `control points stay inside the data range: ${ys}`);
  assert.equal(smoothPath([{ x: 5, y: 7 }]), "M5,7");
  assert.equal(smoothPath([]), "");
});
