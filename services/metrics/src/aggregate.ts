import type { KeyValueStore } from "./store.ts";
import type { StoredAppDay, StoredWebSession } from "./collect.ts";
import { addDays, dayString } from "./days.ts";

export const RAW_RETENTION_DAYS = 45;
const REAGGREGATE_DAYS = 8;
// Blob reads in flight at once, so a recount fits in a function's time limit.
const READS_AT_ONCE = 8;

type Counts = Record<string, number>;
export type Platform = "macos" | "windows";

/** Copies seen in a window, each counted once with the newest summary it sent in that window. */
export interface CopyCounts { count: number; versions: Counts; os: Counts; architectures: Counts }

export interface DaySummary {
  day: string;
  app: {
    active: number; newInstalls: number; wau: number; mau: number;
    versions: Counts; os: Counts; architectures: Counts; launches: Counts;
    features: Record<string, Counts>;
    // Summaries stored before these two fields existed don't have them.
    platforms: Record<Platform, { active: number; newInstalls: number }>;
    unique: Record<"d7" | "d30", Record<Platform, CopyCounts>>;
  };
  web: { sessions: number; routes: Counts; referrers: Counts; downloads: Counts };
  retention: { d7?: Cohort; d30?: Cohort };
  generatedAt: string;
}

export interface Cohort { cohort: string; size: number; retained: number }

function bump(counts: Counts, key: string, amount = 1): void { counts[key] = (counts[key] ?? 0) + amount; }

// Records stored before the platform field existed have no p and are Macs'.
const platformOf = (record: StoredAppDay): Platform => (record.p === "windows" ? "windows" : "macos");
const versionKey = (record: StoredAppDay) => `${record.v} (${record.b})`;
const osKey = (record: StoredAppDay) => `${platformOf(record) === "windows" ? "Windows" : "macOS"} ${record.os}`;
// A Windows arm64 PC is not Apple silicon, so Windows chips keep their own keys.
const chipKey = (record: StoredAppDay) => (platformOf(record) === "windows" ? `windows-${record.a}` : record.a);

/** Reused across the days of one recount: each day's IDs, and records keyed "<day>/<id>". */
export interface AggregationCache { ids: Map<string, string[]>; records: Map<string, StoredAppDay> }
export const aggregationCache = (): AggregationCache => ({ ids: new Map(), records: new Map() });

async function inParallel<T, R>(items: T[], work: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const index = next++;
      results[index] = await work(items[index]!);
    }
  };
  await Promise.all(Array.from({ length: Math.min(READS_AT_ONCE, items.length) }, worker));
  return results;
}

async function ids(store: KeyValueStore, day: string, cache: AggregationCache): Promise<string[]> {
  const cached = cache.ids.get(day);
  if (cached) return cached;
  const found = (await store.list(`app/${day}/`)).map((key) => key.slice(`app/${day}/`.length));
  cache.ids.set(day, found);
  return found;
}

async function records<T>(store: KeyValueStore, prefix: string): Promise<[string, T][]> {
  const keys = await store.list(prefix);
  const values = await inParallel(keys, (key) => store.get(key));
  const out: [string, T][] = [];
  keys.forEach((key, index) => {
    const value = values[index];
    if (value) out.push([key.slice(prefix.length), JSON.parse(value) as T]);
  });
  return out;
}

async function appDay(store: KeyValueStore, day: string, cache: AggregationCache): Promise<[string, StoredAppDay][]> {
  const found = await records<StoredAppDay>(store, `app/${day}/`);
  cache.ids.set(day, found.map(([id]) => id));
  for (const [id, record] of found) cache.records.set(`${day}/${id}`, record);
  return found;
}

/** Each copy active in the 30 days ending on `day`, with the newest of those days it sent counts for. */
async function newestDays(store: KeyValueStore, day: string, cache: AggregationCache): Promise<Map<string, string>> {
  const newest = new Map<string, string>();
  for (let offset = 0; offset < 30; offset++) {
    const date = addDays(day, -offset);
    for (const id of await ids(store, date, cache)) if (!newest.has(id)) newest.set(id, date);
  }
  const missing = [...newest].filter(([id, date]) => !cache.records.has(`${date}/${id}`));
  await inParallel(missing, async ([id, date]) => {
    const value = await store.get(`app/${date}/${id}`);
    if (value) cache.records.set(`${date}/${id}`, JSON.parse(value) as StoredAppDay);
  });
  return newest;
}

function copies(): Record<Platform, CopyCounts> {
  const empty = (): CopyCounts => ({ count: 0, versions: {}, os: {}, architectures: {} });
  return { macos: empty(), windows: empty() };
}

async function cohortSize(store: KeyValueStore, cohort: string): Promise<number> {
  const stored = await store.get(`summary/${cohort}`);
  if (stored) return (JSON.parse(stored) as DaySummary).app.newInstalls;
  return (await records<StoredAppDay>(store, `app/${cohort}/`)).filter(([, r]) => r.s === cohort).length;
}

export async function aggregateDay(store: KeyValueStore, day: string, now = new Date(),
  cache: AggregationCache = aggregationCache()): Promise<DaySummary> {
  const app = await appDay(store, day, cache);
  const newest = await newestDays(store, day, cache);
  const weekStart = addDays(day, -6);
  const summary: DaySummary = {
    day,
    app: {
      active: app.length, newInstalls: 0,
      wau: [...newest.values()].filter((date) => date >= weekStart).length, mau: newest.size,
      versions: {}, os: {}, architectures: {}, launches: {}, features: {},
      platforms: { macos: { active: 0, newInstalls: 0 }, windows: { active: 0, newInstalls: 0 } },
      unique: { d7: copies(), d30: copies() },
    },
    web: { sessions: 0, routes: {}, referrers: {}, downloads: {} },
    retention: {},
    generatedAt: now.toISOString(),
  };
  for (const [, record] of app) {
    const platform = summary.app.platforms[platformOf(record)];
    platform.active++;
    if (record.s === day) { summary.app.newInstalls++; platform.newInstalls++; }
    bump(summary.app.versions, versionKey(record));
    bump(summary.app.os, osKey(record));
    bump(summary.app.architectures, chipKey(record));
    bump(summary.app.launches, record.l);
    for (const [feature, bucket] of Object.entries(record.f)) {
      if (bucket === "0") continue;
      summary.app.features[feature] ??= {};
      bump(summary.app.features[feature], bucket);
    }
  }
  for (const [id, date] of newest) {
    const record = cache.records.get(`${date}/${id}`);
    for (const window of date >= weekStart ? [summary.app.unique.d7, summary.app.unique.d30] : [summary.app.unique.d30]) {
      // A record removed after listing still counts, without details, so the platforms add up to the total.
      const counts = window[record ? platformOf(record) : "macos"];
      counts.count++;
      if (!record) continue;
      bump(counts.versions, versionKey(record));
      bump(counts.os, osKey(record));
      bump(counts.architectures, chipKey(record));
    }
  }
  for (const [label, offset] of [["d7", 7], ["d30", 30]] as const) {
    const cohort = addDays(day, -offset);
    const size = await cohortSize(store, cohort);
    if (size > 0) summary.retention[label] = { cohort, size, retained: app.filter(([, r]) => r.s === cohort).length };
  }
  const sessions = await records<StoredWebSession>(store, `web/${day}/`);
  summary.web.sessions = sessions.length;
  for (const [, session] of sessions) { bump(summary.web.routes, session.r); bump(summary.web.referrers, session.ref); }
  for (const key of await store.list(`dl/${day}/`)) bump(summary.web.downloads, key.split("/").at(-1) ?? "unknown");
  await store.set(`summary/${day}`, JSON.stringify(summary));
  return summary;
}

export async function removeExpired(store: KeyValueStore, today: string): Promise<number> {
  const cutoff = addDays(today, -(RAW_RETENTION_DAYS - 1));
  let removed = 0;
  for (const prefix of ["app/", "web/", "dl/"]) {
    for (const key of await store.list(prefix)) {
      if ((key.split("/")[1] ?? "") < cutoff) { await store.delete(key); removed++; }
    }
  }
  return removed;
}

export interface ExternalConfig { githubRepository: string; githubToken?: string; homebrewCask: string }
export type Fetcher = (url: string, init?: RequestInit) => Promise<Response>;

export async function snapshotGitHub(store: KeyValueStore, day: string, config: ExternalConfig, fetcher: Fetcher,
  now = new Date()): Promise<number> {
  const headers: Record<string, string> = { Accept: "application/vnd.github+json", "User-Agent": "annotator-metrics" };
  if (config.githubToken) headers.Authorization = `Bearer ${config.githubToken}`;
  const response = await fetcher(`https://api.github.com/repos/${config.githubRepository}/releases?per_page=100`, { headers });
  if (!response.ok) throw new Error(`GitHub releases returned HTTP ${response.status}.`);
  const releases = await response.json() as { tag_name: string; assets: { name: string; download_count: number }[] }[];
  const assets: Counts = {};
  for (const release of releases) for (const asset of release.assets) {
    if (asset.name.endsWith(".zip") || asset.name.endsWith(".dmg")) assets[`${release.tag_name}/${asset.name}`] = asset.download_count;
  }
  const total = Object.values(assets).reduce((sum, value) => sum + value, 0);
  await store.set(`external/github/${day}`, JSON.stringify({ total, assets, capturedAt: now.toISOString() }));
  return total;
}

// Refresh re-reads GitHub download counts, at most every 10 minutes, so a new release shows the same day.
export const GITHUB_REFRESH_SECONDS = 600;
export type GitHubRefresh = { github: "updated" | "recent" | "failed"; error?: string };

export async function refreshGitHub(store: KeyValueStore, day: string, config: ExternalConfig, fetcher: Fetcher,
  now = new Date()): Promise<GitHubRefresh> {
  const current = await store.get(`external/github/${day}`);
  const captured = current ? Date.parse((JSON.parse(current) as { capturedAt?: string }).capturedAt ?? "") : Number.NaN;
  if (Number.isFinite(captured) && now.getTime() - captured < GITHUB_REFRESH_SECONDS * 1000) return { github: "recent" };
  try {
    await snapshotGitHub(store, day, config, fetcher, now);
    return { github: "updated" };
  } catch (error) {
    return { github: "failed", error: (error as Error).message };
  }
}

export async function snapshotHomebrew(store: KeyValueStore, day: string, config: ExternalConfig, fetcher: Fetcher): Promise<Counts> {
  const result: Counts = {};
  for (const period of ["30d", "90d", "365d"]) {
    const response = await fetcher(`https://formulae.brew.sh/api/analytics/cask-install/${period}.json`);
    if (!response.ok) throw new Error(`Homebrew analytics returned HTTP ${response.status}.`);
    const report = await response.json() as { items: { cask: string; count: string }[] };
    const item = report.items.find((entry) => entry.cask === config.homebrewCask);
    if (item) {
      const count = Number(item.count.replaceAll(",", ""));
      if (!Number.isSafeInteger(count) || count < 0) throw new Error("Homebrew returned an invalid install count.");
      result[period] = count;
    }
  }
  await store.set(`external/homebrew/${day}`, JSON.stringify(result));
  return result;
}

export interface RunResult { day: string; aggregated: string[]; removed: number; errors: string[]; finishedAt: string }

export async function runDaily(store: KeyValueStore, config: ExternalConfig, fetcher: Fetcher, now = new Date()): Promise<RunResult> {
  const today = dayString(now);
  await store.set("health/last-run", JSON.stringify({ day: today, status: "started", startedAt: now.toISOString() }));
  const errors: string[] = [];
  const aggregated: string[] = [];
  const cache = aggregationCache();
  for (let offset = REAGGREGATE_DAYS; offset >= 0; offset--) {
    const day = addDays(today, -offset);
    await aggregateDay(store, day, now, cache);
    aggregated.push(day);
  }
  const removed = await removeExpired(store, today);
  try { await snapshotGitHub(store, today, config, fetcher, now); } catch (error) { errors.push((error as Error).message); }
  try { await snapshotHomebrew(store, today, config, fetcher); } catch (error) { errors.push((error as Error).message); }
  const result = { day: today, status: "finished", aggregated, removed, errors, finishedAt: new Date().toISOString() };
  await store.set("health/last-run", JSON.stringify(result));
  return result;
}

export async function dashboard(store: KeyValueStore, now = new Date(), days = 90) {
  const today = dayString(now);
  const summaries: DaySummary[] = [];
  const github: { day: string; total: number; assets: Counts; capturedAt?: string }[] = [];
  const homebrew: { day: string; counts: Counts }[] = [];
  for (let offset = days - 1; offset >= 0; offset--) {
    const day = addDays(today, -offset);
    const summary = await store.get(`summary/${day}`);
    if (summary) summaries.push(JSON.parse(summary) as DaySummary);
    const gh = await store.get(`external/github/${day}`);
    if (gh) github.push({ day, ...(JSON.parse(gh) as { total: number; assets: Counts; capturedAt?: string }) });
    const brew = await store.get(`external/homebrew/${day}`);
    if (brew) homebrew.push({ day, counts: JSON.parse(brew) as Counts });
  }
  const health = await store.get("health/last-run");
  return { generatedAt: now.toISOString(), summaries, github, homebrew, health: health ? JSON.parse(health) : null };
}
