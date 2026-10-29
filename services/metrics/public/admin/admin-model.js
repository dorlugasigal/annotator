// Pure helpers for the owner dashboard: periods, comparisons, labels and chart geometry.
// No DOM access, so the service tests can check them directly.

export const FEATURE_LABELS = {
  drawing: "Drawing", editing: "Editing marks", textOrShape: "Text and shapes", coverOrEffect: "Covers and effects",
  zoom: "Zoom", imageInsertion: "Images", boardOpen: "Opening boards", boardSave: "Saving boards",
  pngExport: "PNG export", reusableShape: "My shapes",
};
export const ROUTE_LABELS = {
  home: "Home", downloads: "Downloads", docs: "Help", privacy: "Privacy", support: "Support", ai: "AI setup", other: "Other pages",
};
export const REFERRER_LABELS = {
  direct: "Direct or unknown", search: "Search engines", social: "Social sites", github: "GitHub", other: "Other sites",
};
export const TRACK_LABELS = { stable: "Stable", preview: "Testing preview", unknown: "Unknown" };
export const ARCH_LABELS = {
  arm64: "Apple silicon", x86_64: "Intel", other: "Other", "windows-arm64": "Windows on Arm", "windows-x86_64": "Windows x64",
};
export const PLATFORM_LABELS = { macos: "Mac", windows: "Windows" };
export const DOWNLOAD_LABELS = { macDiskImage: "Mac disk image", macZip: "Mac ZIP", windowsZip: "Windows ZIP" };
export const PERIODS = [7, 30];
export const PLATFORMS = ["macos", "windows"];
const DOWNLOAD_KINDS = Object.keys(DOWNLOAD_LABELS);
const WINDOW_KEYS = { 7: "d7", 30: "d30" };

export function addDays(day, amount) {
  const date = new Date(`${day}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + amount);
  return date.toISOString().slice(0, 10);
}

export const utcDay = (date) => date.toISOString().slice(0, 10);

/** The `length` days ending on `end`, oldest first. */
export function windowDays(end, length) {
  return Array.from({ length }, (_, index) => addDays(end, index - length + 1));
}

/** One point per day; days without a stored summary are null, so charts show a gap instead of a false zero. */
export function daily(summaries, end, length, pick) {
  const byDay = new Map(summaries.map((summary) => [summary.day, summary]));
  return windowDays(end, length).map((day) => ({ day, value: byDay.has(day) ? pick(byDay.get(day)) : null }));
}

export const total = (counts) => Object.values(counts ?? {}).reduce((sum, value) => sum + value, 0);

export function merge(target, counts) {
  for (const [key, value] of Object.entries(counts ?? {})) target[key] = (target[key] ?? 0) + value;
  return target;
}

/** Largest first; ties in label order. */
export function ranked(counts, labels = {}) {
  return Object.entries(counts ?? {})
    .filter(([, value]) => value > 0)
    .map(([key, value]) => ({ key, label: labels[key] ?? key, value }))
    .sort((a, b) => b.value - a.value || a.label.localeCompare(b.label));
}

/**
 * The chosen period: the `length` full UTC days that end yesterday, and the same number of days before them.
 * Today is still being counted, so it stays out of every period.
 */
export function period(generatedAt, length) {
  const today = utcDay(new Date(generatedAt));
  const end = addDays(today, -1);
  return { length, today, end, days: windowDays(end, length), previous: windowDays(addDays(end, -length), length) };
}

/** A daily value summed over days. Days without a summary are left out; an unknown day makes the sum unknown. */
export function sumDays(byDay, days, pick) {
  let value = 0, covered = 0;
  for (const day of days) {
    if (!byDay.has(day)) continue;
    const picked = pick(byDay.get(day));
    if (picked === null || picked === undefined) return { value: null, covered };
    value += picked;
    covered++;
  }
  return { value: covered ? value : null, covered };
}

/** The change from an earlier value. Null when either is unknown, so a missing past never reads as growth. */
export function compare(current, previous) {
  if (current === null || current === undefined || previous === null || previous === undefined) return null;
  const diff = current - previous;
  return { previous, diff, ratio: previous ? diff / previous : null, trend: diff > 0 ? "up" : diff < 0 ? "down" : "same" };
}

/** Each platform's active copies and new opt-ins on one day. Older summaries have no split: a day without a Windows system was all Mac. */
export function platformDay(summary) {
  const app = summary.app;
  if (app.platforms) return app.platforms;
  const windows = Object.entries(app.os ?? {}).reduce((sum, [key, value]) => sum + (key.startsWith("Windows") ? value : 0), 0);
  return {
    macos: { active: app.active - windows, newInstalls: windows ? null : app.newInstalls },
    windows: { active: windows, newInstalls: windows ? null : 0 },
  };
}

/**
 * Copies seen in the 7 or 30 days ending on `day`, by platform, with their newest version, system and chip.
 * Older summaries have no split: when every day of the window has counts and none has a Windows system, all were Macs.
 */
export function copiesOn(byDay, day, length) {
  const summary = byDay.get(day);
  if (!summary) return null;
  const count = length === 7 ? summary.app.wau : summary.app.mau;
  const unique = summary.app.unique?.[WINDOW_KEYS[length]];
  if (unique) return { total: count, macos: unique.macos.count, windows: unique.windows.count, details: unique };
  const onlyMac = windowDays(day, length).every((date) => byDay.has(date) && platformDay(byDay.get(date)).windows.active === 0);
  return { total: count, macos: onlyMac ? count : null, windows: onlyMac ? 0 : null, details: null };
}

function versionParts(value) {
  const text = String(value).trim().replace(/^v/i, "").split("+")[0];
  const dash = text.indexOf("-");
  const core = (dash < 0 ? text : text.slice(0, dash)).split(".").map((part) => (/^\d+$/.test(part) ? Number(part) : -1));
  return { core, pre: dash < 0 ? [] : text.slice(dash + 1).split(".") };
}

/** Release order, as in semantic versioning: 0.1.0-beta.2 < 0.1.0-beta.10 < 0.1.0. */
export function compareVersions(a, b) {
  const left = versionParts(a), right = versionParts(b);
  for (let i = 0; i < Math.max(left.core.length, right.core.length); i++) {
    const diff = (left.core[i] ?? 0) - (right.core[i] ?? 0);
    if (diff) return Math.sign(diff);
  }
  if (!left.pre.length || !right.pre.length) return (left.pre.length ? -1 : 0) + (right.pre.length ? 1 : 0);
  for (let i = 0; i < Math.max(left.pre.length, right.pre.length); i++) {
    const x = left.pre[i], y = right.pre[i];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    const xNumber = /^\d+$/.test(x), yNumber = /^\d+$/.test(y);
    if (xNumber && yNumber) {
      if (Number(x) !== Number(y)) return Math.sign(Number(x) - Number(y));
    } else if (xNumber !== yNumber) return xNumber ? -1 : 1;
    else if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

/** "0.1.0-beta.17 (35)" is version 0.1.0-beta.17, build 35. */
export const versionOf = (key) => /^(.*) \(\d+\)$/.exec(key)?.[1] ?? key;

/** How many copies run the newest release (or something newer), overall and by platform, with copies per version. */
export function versionUse(unique, newestRelease) {
  const byVersion = {};
  const platforms = {};
  for (const platform of PLATFORMS) {
    platforms[platform] = { on: 0, of: 0 };
    for (const [key, count] of Object.entries(unique[platform]?.versions ?? {})) {
      byVersion[versionOf(key)] = (byVersion[versionOf(key)] ?? 0) + count;
      platforms[platform].of += count;
    }
  }
  const seen = Object.keys(byVersion).sort((a, b) => compareVersions(b, a));
  const newest = newestRelease ?? seen[0] ?? null;
  const current = (version) => newest !== null && compareVersions(version, newest) >= 0;
  for (const platform of PLATFORMS) {
    for (const [key, count] of Object.entries(unique[platform]?.versions ?? {})) if (current(versionOf(key))) platforms[platform].on += count;
  }
  const on = platforms.macos.on + platforms.windows.on, of = platforms.macos.of + platforms.windows.of;
  return {
    newest, fromGitHub: newestRelease !== null, on, of, share: of ? on / of : null, platforms,
    rows: seen.map((version) => ({ key: version, label: version, value: byVersion[version], current: current(version) })),
  };
}

/** GitHub assets: the Mac disk image, the Mac ZIP (Homebrew and in-app updates) and the Windows ZIPs. */
export function downloadKind(asset) {
  if (/-windows-[^/]*\.zip$/i.test(asset)) return "windowsZip";
  return asset.endsWith(".dmg") ? "macDiskImage" : "macZip";
}

export function kindTotals(snapshot) {
  const totals = Object.fromEntries(DOWNLOAD_KINDS.map((kind) => [kind, 0]));
  for (const [name, count] of Object.entries(snapshot.assets ?? {})) totals[downloadKind(name)] += count;
  return totals;
}

/**
 * GitHub release downloads by kind: all-time totals, what each gained over the last `length` days and the days before.
 * Snapshots are running totals from each night and each Refresh. Without a snapshot exactly `length` days back, the gain
 * starts at the nearest older one (or the first one) and has nothing to compare with.
 */
export function githubDownloads(snapshots, length = 7) {
  const latest = snapshots.at(-1);
  if (!latest) return null;
  const byDay = new Map(snapshots.map((snapshot) => [snapshot.day, snapshot]));
  const target = addDays(latest.day, -length);
  const base = byDay.get(target) ?? [...snapshots].reverse().find((snapshot) => snapshot.day < target)
    ?? (snapshots[0] === latest ? null : snapshots[0]);
  const before = base?.day === target ? byDay.get(addDays(target, -length)) ?? null : null;
  const [now, then, earlier] = [latest, base, before].map((snapshot) => (snapshot ? kindTotals(snapshot) : null));
  const kinds = {};
  for (const kind of DOWNLOAD_KINDS) {
    kinds[kind] = {
      total: now[kind],
      gained: then ? now[kind] - then[kind] : null,
      previous: then && earlier ? then[kind] - earlier[kind] : null,
      daily: windowDays(latest.day, length).map((day) => {
        const start = byDay.get(addDays(day, -1)), end = byDay.get(day);
        return start && end ? kindTotals(end)[kind] - kindTotals(start)[kind] : null;
      }),
    };
  }
  const releases = {};
  for (const [name, count] of Object.entries(latest.assets ?? {})) {
    const [tag] = name.split("/");
    releases[tag] = (releases[tag] ?? 0) + count;
  }
  const rows = Object.entries(releases)
    .map(([tag, value]) => ({ key: tag, label: tag.replace(/^v/, ""), value }))
    .sort((a, b) => compareVersions(b.label, a.label));
  return {
    total: latest.total, day: latest.day, capturedAt: latest.capturedAt ?? null,
    since: base?.day ?? null, kinds, releases: rows, newest: rows[0]?.label ?? null,
  };
}

/** The newest release GitHub listed on or before `day`. */
export function newestReleaseBy(snapshots, day) {
  const snapshot = [...snapshots].reverse().find((candidate) => candidate.day <= day);
  const tags = Object.keys(snapshot?.assets ?? {}).map((name) => name.split("/")[0].replace(/^v/, ""));
  return tags.sort((a, b) => compareVersions(b, a))[0] ?? null;
}

/** Homebrew publishes nothing for taps it treats as private, which is different from zero installs. */
export function homebrewInstalls(snapshots) {
  const latest = snapshots.at(-1);
  if (!latest) return { status: "pending" };
  const counts = latest.counts ?? {};
  return Object.keys(counts).length ? { status: "reported", day: latest.day, counts } : { status: "unreported", day: latest.day };
}

/** Feature use over some days, as install-days out of the active install-days. */
export function featureUsage(summaries) {
  const activeDays = summaries.reduce((sum, summary) => sum + summary.app.active, 0);
  const features = {};
  for (const summary of summaries) {
    for (const [name, buckets] of Object.entries(summary.app.features ?? {})) features[name] = (features[name] ?? 0) + total(buckets);
  }
  return {
    activeDays,
    rows: ranked(features, FEATURE_LABELS).map((row) => ({ ...row, share: activeDays ? row.value / activeDays : 0 })),
  };
}

/** Every feature's share in the period, unused ones included, with its share in the days before when there is one. */
export function featureRows(current, previous = null) {
  const now = featureUsage(current), before = previous ? featureUsage(previous) : null;
  const value = (usage, key) => usage.rows.find((row) => row.key === key)?.value ?? 0;
  const keys = new Set([...Object.keys(FEATURE_LABELS), ...now.rows.map((row) => row.key)]);
  return {
    activeDays: now.activeDays,
    rows: [...keys].map((key) => ({
      key, label: FEATURE_LABELS[key] ?? key, value: value(now, key),
      share: now.activeDays ? value(now, key) / now.activeDays : 0,
      previousShare: before?.activeDays ? value(before, key) / before.activeDays : null,
    })).sort((a, b) => b.share - a.share || a.label.localeCompare(b.label)),
  };
}

/** The day-7 or day-30 return rate, pooled over the groups whose check day is one of `days`. */
export function pooledRetention(byDay, days, label) {
  let size = 0, retained = 0;
  const cohorts = [];
  for (const day of days) {
    const cohort = byDay.get(day)?.retention?.[label];
    if (!cohort) continue;
    size += cohort.size;
    retained += cohort.retained;
    cohorts.push(cohort.cohort);
  }
  return size ? { size, retained, rate: retained / size, first: cohorts[0], last: cohorts.at(-1) } : null;
}

export function retentionRows(summaries, limit = 6) {
  const rows = [];
  for (const summary of [...summaries].reverse()) {
    for (const [label, cohort] of Object.entries(summary.retention ?? {})) {
      if (rows.length >= limit) return rows;
      rows.push({ period: label === "d7" ? 7 : 30, ...cohort, rate: cohort.size ? cohort.retained / cohort.size : 0 });
    }
  }
  return rows;
}

/**
 * Everything the dashboard shows for one period. Comparisons need counts on every day of both periods,
 * so a short history or a missing night never reads as growth or decline.
 */
export function overview(data, length) {
  const summaries = data.summaries ?? [];
  const byDay = new Map(summaries.map((summary) => [summary.day, summary]));
  const span = period(data.generatedAt, length);
  const has = (day) => byDay.has(day);
  const covered = span.days.filter(has).length;
  const comparable = covered === length && span.previous.every(has);
  const inDays = (days) => days.filter(has).map((day) => byDay.get(day));
  const totals = (pick) => {
    const now = sumDays(byDay, span.days, pick);
    return { value: now.value, change: comparable ? compare(now.value, sumDays(byDay, span.previous, pick).value) : null };
  };
  const average = (pick) => {
    const mean = (sum) => (sum.value === null ? null : sum.value / sum.covered);
    const now = mean(sumDays(byDay, span.days, pick));
    return { value: now, change: comparable ? compare(now, mean(sumDays(byDay, span.previous, pick))) : null };
  };
  const trend = (pick) => daily(summaries, span.end, length, pick).map((point) => point.value);

  const asOf = [...span.days].reverse().find(has) ?? null;
  const copies = asOf ? copiesOn(byDay, asOf, length) : null;
  const copiesBefore = comparable ? copiesOn(byDay, span.previous.at(-1), length) : null;
  const copyCount = (key) => ({ value: copies?.[key] ?? null, change: compare(copies?.[key], copiesBefore?.[key]) });
  const todaySummary = byDay.get(span.today) ?? null;
  const endSummary = byDay.get(span.end);
  const github = githubDownloads(data.github ?? [], length);

  const visits = totals((summary) => summary.web.sessions);
  const clicks = totals((summary) => total(summary.web.downloads));
  const rate = (part, whole) => (part === null || part === undefined || !whole ? null : part / whole);
  const disk = github?.kinds.macDiskImage;

  const platforms = Object.fromEntries(PLATFORMS.map((platform) => {
    const details = copies?.details?.[platform];
    return [platform, {
      copies: copyCount(platform),
      perDay: average((summary) => platformDay(summary)[platform].active),
      newInstalls: totals((summary) => platformDay(summary)[platform].newInstalls),
      os: details ? ranked(details.os) : null,
      architectures: details ? ranked(details.architectures, ARCH_LABELS) : null,
    }];
  }));

  const windowKey = WINDOW_KEYS[length];
  // A period is held to the newest release out by its last day, or a newer release its copies already ran.
  let versions = null;
  if (copies?.details) {
    const released = new Set(github?.releases.map((row) => row.label) ?? []);
    const ran = PLATFORMS.flatMap((platform) => Object.keys(copies.details[platform]?.versions ?? {}).map(versionOf))
      .filter((version) => released.has(version));
    const newest = [newestReleaseBy(data.github ?? [], span.end), ...ran].filter(Boolean).sort((a, b) => compareVersions(b, a))[0] ?? null;
    versions = versionUse(copies.details, newest);
    versions.later = github?.newest && versions.fromGitHub && compareVersions(github.newest, versions.newest) > 0 ? github.newest : null;
    const todayUnique = todaySummary?.app.unique?.[windowKey];
    if (todayUnique) versions.today = versionUse(todayUnique, versions.later ?? versions.newest);
  }

  return {
    period: span, covered, comparable, asOf, hasData: summaries.length > 0,
    stillCounting: Boolean(endSummary && Date.parse(endSummary.generatedAt) < Date.parse(`${span.today}T00:00:00Z`)),
    people: {
      copies: copyCount("total"),
      perDay: average((summary) => summary.app.active),
      today: todaySummary ? { active: todaySummary.app.active, countedAt: todaySummary.generatedAt } : null,
    },
    chart: span.days.map((day, index) => {
      const summary = byDay.get(day), before = byDay.get(span.previous[index]);
      return {
        day, value: summary ? summary.app.active : null, newInstalls: summary?.app.newInstalls ?? 0,
        windows: summary ? platformDay(summary).windows.active : 0,
        previousDay: span.previous[index], previous: before ? before.app.active : null,
      };
    }),
    funnel: {
      visits: { ...visits, daily: trend((summary) => summary.web.sessions) },
      clicks: {
        ...clicks, daily: trend((summary) => total(summary.web.downloads)),
        rate: rate(clicks.value, visits.value),
        previousRate: clicks.change && visits.change ? rate(clicks.change.previous, visits.change.previous) : null,
      },
      diskImages: { value: disk?.gained ?? null, change: compare(disk?.gained, disk?.previous), daily: disk?.daily ?? [] },
      optIns: { ...totals((summary) => summary.app.newInstalls), daily: trend((summary) => summary.app.newInstalls) },
    },
    platforms,
    everWindows: summaries.some((summary) => platformDay(summary).windows.active > 0),
    versions,
    github,
    homebrew: homebrewInstalls(data.homebrew ?? []),
    features: featureRows(inDays(span.days), comparable ? inDays(span.previous) : null),
    website: (() => {
      const routes = {}, referrers = {}, tracks = {};
      for (const summary of inDays(span.days)) {
        merge(routes, summary.web.routes);
        merge(referrers, summary.web.referrers);
        merge(tracks, summary.web.downloads);
      }
      return { routes: ranked(routes, ROUTE_LABELS), referrers: ranked(referrers, REFERRER_LABELS), tracks: ranked(tracks, TRACK_LABELS) };
    })(),
    retention: Object.fromEntries(["d7", "d30"].map((label) => {
      const now = pooledRetention(byDay, span.days, label);
      const before = comparable ? pooledRetention(byDay, span.previous, label) : null;
      return [label, now ? { ...now, change: compare(now.rate, before?.rate) } : null];
    })),
    cohorts: retentionRows(inDays(span.days)),
  };
}

/** The nightly job runs at 00:15 UTC. Older than 26 hours means a run was missed. */
export function jobHealth(record, now) {
  if (!record) return { level: "missing" };
  const finished = record.status === "finished";
  const at = Date.parse(finished ? record.finishedAt : record.startedAt);
  const hours = (now.getTime() - at) / 3_600_000;
  const errors = record.errors ?? [];
  const level = !finished ? (hours > 0.5 ? "stalled" : "running") : errors.length ? "errors" : hours > 26 ? "stale" : "ok";
  return { level, at, hours, errors, aggregated: record.aggregated ?? [], removed: record.removed ?? 0, day: record.day };
}

/** A round axis maximum, never below 4, so one or two copies don't fill the whole chart. */
export function niceMax(value) {
  if (value <= 4) return 4;
  const power = 10 ** Math.floor(Math.log10(value));
  for (const step of [1, 2, 2.5, 5, 10]) if (step * power >= value) return step * power;
  return 10 * power;
}

const round = (value) => Math.round(value * 100) / 100;

/** A monotone cubic curve through the points (Fritsch-Carlson), so small counts never dip below zero. */
export function smoothPath(points) {
  const count = points.length;
  if (!count) return "";
  if (count === 1) return `M${round(points[0].x)},${round(points[0].y)}`;
  const dx = [], slope = [];
  for (let i = 0; i < count - 1; i++) {
    dx[i] = points[i + 1].x - points[i].x;
    slope[i] = (points[i + 1].y - points[i].y) / dx[i];
  }
  const tangent = [slope[0]];
  for (let i = 1; i < count - 1; i++) tangent[i] = slope[i - 1] * slope[i] <= 0 ? 0 : (slope[i - 1] + slope[i]) / 2;
  tangent[count - 1] = slope[count - 2];
  for (let i = 0; i < count - 1; i++) {
    if (slope[i] === 0) { tangent[i] = 0; tangent[i + 1] = 0; continue; }
    const a = tangent[i] / slope[i], b = tangent[i + 1] / slope[i], length = a * a + b * b;
    if (length > 9) {
      const scale = 3 / Math.sqrt(length);
      tangent[i] = scale * a * slope[i];
      tangent[i + 1] = scale * b * slope[i];
    }
  }
  let path = `M${round(points[0].x)},${round(points[0].y)}`;
  for (let i = 0; i < count - 1; i++) {
    const third = dx[i] / 3;
    path += `C${round(points[i].x + third)},${round(points[i].y + tangent[i] * third)} `
      + `${round(points[i + 1].x - third)},${round(points[i + 1].y - tangent[i + 1] * third)} `
      + `${round(points[i + 1].x)},${round(points[i + 1].y)}`;
  }
  return path;
}

/** Runs of consecutive days that have a value; each run becomes one stroke. */
export function segments(points) {
  const runs = [];
  let run = [];
  for (const point of points) {
    if (point.value === null) { if (run.length) runs.push(run); run = []; }
    else run.push(point);
  }
  if (run.length) runs.push(run);
  return runs;
}
