// Pure helpers for the owner dashboard: labels, derived measurements and chart geometry.
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
export const TRACK_LABELS = { stable: "Stable", preview: "Preview (alpha)", unknown: "Unknown" };
export const ARCH_LABELS = {
  arm64: "Apple silicon", x86_64: "Intel", other: "Other", "windows-arm64": "Windows on Arm", "windows-x86_64": "Windows x64",
};

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

/** Feature use over a week, as install-days out of the active install-days. */
export function featureUsage(week) {
  const activeDays = week.reduce((sum, summary) => sum + summary.app.active, 0);
  const features = {};
  for (const summary of week) {
    for (const [name, buckets] of Object.entries(summary.app.features ?? {})) features[name] = (features[name] ?? 0) + total(buckets);
  }
  return {
    activeDays,
    rows: ranked(features, FEATURE_LABELS).map((row) => ({ ...row, share: activeDays ? row.value / activeDays : 0 })),
  };
}

/** GitHub release downloads: total, change over about a week, disk image versus ZIP, and per release. */
export function githubDownloads(snapshots) {
  const latest = snapshots.at(-1);
  if (!latest) return null;
  const base = [...snapshots].reverse().find((snapshot) => snapshot.day <= addDays(latest.day, -7)) ?? snapshots[0];
  const kinds = { dmg: 0, zip: 0 };
  const releases = {};
  for (const [name, count] of Object.entries(latest.assets ?? {})) {
    const [tag] = name.split("/");
    releases[tag] = (releases[tag] ?? 0) + count;
    if (name.endsWith(".dmg")) kinds.dmg += count;
    else kinds.zip += count;
  }
  return {
    total: latest.total, day: latest.day, capturedAt: latest.capturedAt ?? null,
    change: base === latest ? null : latest.total - base.total, since: base.day,
    kinds,
    releases: Object.entries(releases)
      .map(([tag, value]) => ({ key: tag, label: tag.replace(/^v/, ""), value }))
      .sort((a, b) => b.label.localeCompare(a.label, "en", { numeric: true })),
  };
}

/** Homebrew publishes nothing for taps it treats as private, which is different from zero installs. */
export function homebrewInstalls(snapshots) {
  const latest = snapshots.at(-1);
  if (!latest) return { status: "pending" };
  const counts = latest.counts ?? {};
  return Object.keys(counts).length ? { status: "reported", day: latest.day, counts } : { status: "unreported", day: latest.day };
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
