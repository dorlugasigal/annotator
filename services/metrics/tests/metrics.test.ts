import test from "node:test";
import assert from "node:assert/strict";
import { MemoryStore } from "../src/store.ts";
import { ValidationError, validateApp, validateWeb } from "../src/validate.ts";
import { pseudonym } from "../src/collect.ts";
import { aggregateDay, removeExpired, runDaily, snapshotHomebrew } from "../src/aggregate.ts";
import { createSession, hashPassword, validSession, verifyPassword } from "../src/auth.ts";
import { handleApp, handleLogin, handleSummary, handleWeb, type Env } from "../src/http.ts";

const NOW = new Date("2026-09-23T12:00:00Z");
const SECRET = "s".repeat(40);
const INSTALL = "11111111-2222-4333-8444-555555555555";

function appBody(overrides: Record<string, unknown> = {}) {
  return {
    schema: 1, installation: INSTALL, since: "2026-09-23", version: "0.1.0", build: "11", osMajor: 15,
    architecture: "arm64", days: [{ day: "2026-09-23", launches: "1", features: { drawing: "2-5", zoom: "1" } }],
    ...overrides,
  };
}

function env(extra: Env = {}): Env {
  return { METRICS_HMAC_SECRET: SECRET, ADMIN_SESSION_SECRET: "a".repeat(40), ...extra };
}

function post(path: string, body: unknown, headers: Record<string, string> = {}) {
  return new Request(`https://metrics.example.test${path}`, {
    method: "POST", headers: { "Content-Type": "application/json", ...headers }, body: JSON.stringify(body),
  });
}

test("app payloads reject unknown, content-bearing or out-of-range fields", () => {
  assert.equal(validateApp(appBody(), NOW).days.length, 1);
  const bad = [
    appBody({ text: "secret" }),
    appBody({ installation: "not-a-uuid" }),
    appBody({ schema: 2 }),
    appBody({ osMajor: 15.4 }),
    appBody({ since: "2026-99-99" }),
    appBody({ days: [] }),
    appBody({ days: [{ day: "2026-09-01", launches: "1", features: {} }] }),
    appBody({ days: [{ day: "2026-09-23", launches: "7", features: {} }] }),
    appBody({ days: [{ day: "2026-09-23", launches: "1", features: { fileName: "1" } }] }),
    appBody({ days: [{ day: "2026-09-23", launches: "1", features: {}, geometry: [] }] }),
  ];
  for (const body of bad) assert.throws(() => validateApp(body, NOW), ValidationError);
});

test("Windows payloads name their platform; Mac payloads without one stay macOS", () => {
  assert.equal(validateApp(appBody(), NOW).platform, "macos");
  assert.equal(validateApp(appBody({ platform: "macos" }), NOW).platform, "macos");
  const windows = validateApp(appBody({ platform: "windows", osMajor: 11, architecture: "x86_64" }), NOW);
  assert.equal(windows.platform, "windows");
  assert.equal(windows.osMajor, 11);
  assert.equal(validateApp(appBody({ platform: "windows", osMajor: 11, architecture: "arm64" }), NOW).architecture, "arm64");
  for (const overrides of [
    { platform: "linux" },
    { platform: "Windows" },
    { platform: 1 },
    { platform: null },
    { platform: "windows", osMajor: 10, architecture: "x86_64" },
    { platform: "windows", osMajor: 11, architecture: "other" },
  ]) assert.throws(() => validateApp(appBody(overrides), NOW), ValidationError, JSON.stringify(overrides));
});

test("web payloads allow only coarse categories", () => {
  assert.equal(validateWeb({ event: "session", session: "abcdefghijklmnop", route: "home", referrer: "search" }).route, "home");
  assert.equal(validateWeb({ event: "download", session: "abcdefghijklmnop", route: "downloads", referrer: "direct", track: "preview" }).track, "preview");
  for (const body of [
    { event: "session", session: "abcdefghijklmnop", route: "https://x.test/?q=1", referrer: "direct" },
    { event: "session", session: "short", route: "home", referrer: "direct" },
    { event: "download", session: "abcdefghijklmnop", route: "home", referrer: "direct" },
    { event: "session", session: "abcdefghijklmnop", route: "home", referrer: "direct", userAgent: "x" },
  ]) assert.throws(() => validateWeb(body), ValidationError);
});

test("the raw installation ID is never stored and resends are idempotent", async () => {
  const store = new MemoryStore();
  for (let i = 0; i < 2; i++) {
    const response = await handleApp(post("/api/metrics/app", appBody()), { store, env: env(), now: () => NOW });
    assert.equal(response.status, 202);
  }
  const keys = [...store.values.keys()];
  assert.deepEqual(keys, [`app/2026-09-23/${pseudonym(SECRET, INSTALL)}`]);
  const dump = JSON.stringify([...store.values]);
  assert.ok(!dump.includes(INSTALL));
  assert.notEqual(pseudonym(SECRET, INSTALL), pseudonym("t".repeat(40), INSTALL));
});

test("a Windows summary is stored with its platform and counted apart from Macs", async () => {
  const store = new MemoryStore();
  const windows = "99999999-8888-4777-8666-555555555555";
  const response = await handleApp(post("/api/metrics/app",
    appBody({ installation: windows, platform: "windows", osMajor: 11, architecture: "arm64" })), { store, env: env(), now: () => NOW });
  assert.equal(response.status, 202);
  assert.equal((await handleApp(post("/api/metrics/app", appBody()), { store, env: env(), now: () => NOW })).status, 202);
  const stored = JSON.parse((await store.get(`app/2026-09-23/${pseudonym(SECRET, windows)}`))!);
  assert.equal(stored.p, "windows");
  assert.equal(JSON.parse((await store.get(`app/2026-09-23/${pseudonym(SECRET, INSTALL)}`))!).p, "macos");
  await store.set("app/2026-09-23/before-windows", JSON.stringify({ v: "0.1.0", b: "11", os: 26, a: "x86_64", s: "2026-09-01", l: "1", f: {} }));
  const summary = await aggregateDay(store, "2026-09-23", NOW);
  assert.equal(summary.app.active, 3);
  assert.deepEqual(summary.app.os, { "Windows 11": 1, "macOS 15": 1, "macOS 26": 1 });
  assert.deepEqual(summary.app.architectures, { "windows-arm64": 1, arm64: 1, x86_64: 1 });
  assert.ok(!JSON.stringify([...store.values]).includes(windows));
});

test("collection returns explicit errors for bad requests and missing configuration", async () => {
  const store = new MemoryStore();
  assert.equal((await handleApp(post("/api/metrics/app", { nope: 1 }), { store, env: env(), now: () => NOW })).status, 400);
  const plain = new Request("https://m.test/api/metrics/app", { method: "POST", headers: { "Content-Type": "text/plain" }, body: "{}" });
  assert.equal((await handleApp(plain, { store, env: env(), now: () => NOW })).status, 415);
  const big = post("/api/metrics/app", { pad: "x".repeat(9000) });
  assert.equal((await handleApp(big, { store, env: env(), now: () => NOW })).status, 413);
  assert.equal((await handleApp(post("/api/metrics/app", appBody()), { store, env: {}, now: () => NOW })).status, 503);
  assert.equal(store.values.size, 0);
});

test("website collection is same-origin and keeps no cookies or CORS grant", async () => {
  const store = new MemoryStore();
  const body = { event: "session", session: "abcdefghijklmnop", route: "home", referrer: "direct" };
  const denied = await handleWeb(post("/api/metrics/web", body, { Origin: "https://evil.test" }), { store, env: env(), now: () => NOW });
  assert.equal(denied.status, 403);
  const allowed = await handleWeb(post("/api/metrics/web", body, { Origin: "https://metrics.example.test", "Content-Type": "text/plain" }),
    { store, env: env(), now: () => NOW });
  assert.equal(allowed.status, 202);
  assert.equal(allowed.headers.get("set-cookie"), null);
  assert.equal(allowed.headers.get("access-control-allow-origin"), null);
  assert.equal((await handleWeb(post("/api/metrics/web", body), { store, env: env(), now: () => NOW })).status, 403);
  assert.deepEqual([...store.values.keys()], ["web/2026-09-23/abcdefghijklmnop"]);
});

test("aggregation computes active, new, WAU/MAU, versions, features and retention", async () => {
  const store = new MemoryStore();
  const record = (since: string, f: Record<string, string> = {}) => JSON.stringify({ v: "0.1.0", b: "11", os: 15, a: "arm64", s: since, l: "1", f });
  await store.set("app/2026-09-16/old", record("2026-09-16"));
  await store.set("app/2026-09-16/gone", record("2026-09-16"));
  await store.set("app/2026-09-20/weekly", record("2026-09-01"));
  await store.set("app/2026-09-23/old", record("2026-09-16", { drawing: "2-5", zoom: "0" }));
  await store.set("app/2026-09-23/fresh", record("2026-09-23", { drawing: "1" }));
  await store.set("web/2026-09-23/abcdefghijklmnop", JSON.stringify({ r: "home", ref: "search" }));
  await store.set("dl/2026-09-23/abcdefghijklmnop/preview", JSON.stringify({ r: "downloads" }));
  await aggregateDay(store, "2026-09-16", NOW);
  const summary = await aggregateDay(store, "2026-09-23", NOW);
  assert.equal(summary.app.active, 2);
  assert.equal(summary.app.newInstalls, 1);
  assert.equal(summary.app.wau, 3);
  assert.equal(summary.app.mau, 4);
  assert.deepEqual(summary.app.versions, { "0.1.0 (11)": 2 });
  assert.deepEqual(summary.app.features, { drawing: { "2-5": 1, "1": 1 } });
  assert.deepEqual(summary.retention.d7, { cohort: "2026-09-16", size: 2, retained: 1 });
  assert.equal(summary.web.sessions, 1);
  assert.deepEqual(summary.web.downloads, { preview: 1 });
  assert.ok(!JSON.stringify(summary).includes("fresh"));
});

test("raw records older than 45 days are removed but summaries remain", async () => {
  const store = new MemoryStore();
  await store.set("app/2026-08-01/x", "{}");
  await store.set("web/2026-08-01/x", "{}");
  await store.set("app/2026-09-01/y", "{}");
  await store.set("app/2026-08-09/expired-boundary", "{}");
  await store.set("app/2026-08-10/retained-boundary", "{}");
  await store.set("summary/2026-08-01", "{}");
  assert.equal(await removeExpired(store, "2026-09-23"), 3);
  assert.deepEqual([...store.values.keys()].sort(), [
    "app/2026-08-10/retained-boundary", "app/2026-09-01/y", "summary/2026-08-01"
  ]);
});

test("daily run snapshots GitHub and Homebrew and reports source failures", async () => {
  const store = new MemoryStore();
  const fetcher = async (url: string) => {
    if (new URL(url).hostname === "api.github.com") return Response.json([
      { tag_name: "v0.1.0-alpha.3", assets: [{ name: "Annotator.zip", download_count: 12 }, { name: "appcast.xml", download_count: 99 }] },
      { tag_name: "v0.1.0-alpha.2", assets: [{ name: "Annotator.zip", download_count: 5 }] },
    ]);
    return new Response("unavailable", { status: 503 });
  };
  const result = await runDaily(store, { githubRepository: "o/r", homebrewCask: "o/r/annotator" }, fetcher, NOW);
  assert.equal(result.aggregated.length, 9);
  assert.deepEqual(JSON.parse((await store.get("external/github/2026-09-23"))!).total, 17);
  assert.equal(result.errors.length, 1);
  assert.match(result.errors[0]!, /Homebrew/);
  assert.ok(await store.get("health/last-run"));
});

test("Homebrew parsing reads comma-formatted counts for a third-party tap cask", async () => {
  const store = new MemoryStore();
  const fetcher = async () => Response.json({ items: [{ cask: "other", count: "9" }, { cask: "o/r/annotator", count: "1,204" }] });
  assert.deepEqual(await snapshotHomebrew(store, "2026-09-23", { githubRepository: "o/r", homebrewCask: "o/r/annotator" }, fetcher),
    { "30d": 1204, "90d": 1204, "365d": 1204 });
});

test("an unreported Homebrew cask is unavailable, not zero installs", async () => {
  const store = new MemoryStore();
  const config = {githubRepository:"o/r",homebrewCask:"o/r/annotator"};
  assert.deepEqual(await snapshotHomebrew(store,"2026-09-23",config,
    async () => Response.json({items:[{cask:"other",count:"10"}]})),{});
  await assert.rejects(snapshotHomebrew(store,"2026-09-23",config,
    async () => Response.json({items:[{cask:"o/r/annotator",count:"invalid"}]})), /invalid install count/);
});

test("Refresh re-reads GitHub downloads at most every 10 minutes and survives a GitHub failure", async () => {
  const store = new MemoryStore();
  const config = env({ ADMIN_PASSWORD_HASH: hashPassword("owner-passphrase") });
  let calls = 0, status = 200, clock = NOW;
  const fetcher = async () => {
    calls++;
    if (status !== 200) return new Response("limited", { status });
    return Response.json([{ tag_name: "v0.1.0-alpha.6", assets: [
      { name: "annotator-0.1.0-alpha.6-14-arm64.dmg", download_count: 4 },
      { name: "annotator-0.1.0-alpha.6-14-arm64.zip", download_count: 2 }] }]);
  };
  const deps = { store, env: config, now: () => clock, fetcher };
  const login = await handleLogin(post("/api/admin/login", { password: "owner-passphrase" }), deps);
  const cookie = login.headers.get("set-cookie")!.split(";")[0]!;
  const refresh = () => handleSummary(new Request("https://metrics.example.test/api/admin/summary",
    { method: "POST", headers: { Cookie: cookie, Origin: "https://metrics.example.test" } }), deps);
  const first = await (await refresh()).json();
  assert.deepEqual(first.refresh, { github: "updated" });
  assert.equal(first.github.at(-1).total, 6);
  assert.equal(first.github.at(-1).capturedAt, NOW.toISOString());
  clock = new Date(NOW.getTime() + 5 * 60_000);
  assert.deepEqual((await (await refresh()).json()).refresh, { github: "recent" });
  assert.equal(calls, 1);
  clock = new Date(NOW.getTime() + 11 * 60_000);
  status = 403;
  const failed = await refresh();
  assert.equal(failed.status, 200);
  const body = await failed.json();
  assert.deepEqual(body.refresh, { github: "failed", error: "GitHub releases returned HTTP 403." });
  // The earlier snapshot stays, with the time it was taken.
  assert.equal(body.github.at(-1).total, 6);
  assert.equal(body.github.at(-1).capturedAt, NOW.toISOString());
  // Loading the page does not fetch GitHub.
  await handleSummary(new Request("https://metrics.example.test/api/admin/summary", { headers: { Cookie: cookie } }), deps);
  assert.equal(calls, 2);
});

test("password hashes and sessions verify without storing the password", () => {
  const hash = hashPassword("correct horse");
  assert.ok(!hash.includes("correct horse"));
  assert.ok(verifyPassword("correct horse", hash));
  assert.ok(!verifyPassword("wrong", hash));
  const secret = "k".repeat(40);
  const token = createSession(secret, 1000);
  assert.ok(validSession(secret, token, 1001));
  assert.ok(!validSession(secret, token, 1000 + 8 * 3600 + 1));
  assert.ok(!validSession("z".repeat(40), token, 1001));
  assert.ok(!validSession(secret, token.replace(/.$/, "x"), 1001));
});

test("dashboard data requires a valid session and login is throttled", async () => {
  const store = new MemoryStore();
  const config = env({ ADMIN_PASSWORD_HASH: hashPassword("owner-passphrase") });
  const deps = { store, env: config, now: () => NOW };
  assert.equal((await handleSummary(new Request("https://m.test/api/admin/summary"), deps)).status, 401);
  const login = await handleLogin(post("/api/admin/login", { password: "owner-passphrase" }), deps);
  assert.equal(login.status, 200);
  const cookie = login.headers.get("set-cookie")!;
  assert.match(cookie, /HttpOnly; Secure; SameSite=Strict/);
  const summary = await handleSummary(new Request("https://m.test/api/admin/summary", { headers: { Cookie: cookie.split(";")[0]! } }), deps);
  assert.equal(summary.status, 200);
  const crossSite = await handleLogin(post("/api/admin/login", { password: "owner-passphrase" }, { Origin: "https://evil.test" }), deps);
  assert.equal(crossSite.status, 403);
  assert.equal((await handleLogin(post("/api/admin/login", { password: "nope" }), deps)).status, 401);
  await store.set(`auth/failures/${Math.floor(NOW.getTime() / 1000 / 900)}`, "200");
  assert.equal((await handleLogin(post("/api/admin/login", { password: "owner-passphrase" }), deps)).status, 429);
});

test("chunked bodies are bounded before JSON parsing and rejected without storage", async () => {
  const store = new MemoryStore();
  let cancelled = false;
  const stream = new ReadableStream<Uint8Array>({
    pull(controller) { controller.enqueue(new Uint8Array(4096)); },
    cancel() { cancelled = true; },
  });
  const options: RequestInit & {duplex:"half"} = {
    method: "POST", body: stream, duplex: "half", headers: {"Content-Type":"application/json"},
  };
  const request = new Request("https://metrics.example.test/api/metrics/app", options);
  const response = await handleApp(request, {store, env:env(), now:() => NOW});
  assert.equal(response.status, 413);
  assert.equal(cancelled, true);
  assert.equal(store.values.size, 0);
});

test("app summaries cannot precede opt-in or exceed the eight-day window", () => {
  for (const overrides of [
    {since:"2026-09-24"},
    {since:"2026-09-23", days:[{day:"2026-09-22", launches:"1", features:{}}]},
    {since:"2026-01-01", days:[{day:"2026-09-15", launches:"1", features:{}}]},
    {since:"2026-01-01", days:[{day:"2026-09-24", launches:"1", features:{}}]},
  ]) assert.throws(() => validateApp(appBody(overrides), NOW), ValidationError);
  assert.equal(validateApp(appBody({since:"2026-01-01", days:[{day:"2026-09-16", launches:"1", features:{}}]}), NOW).days.length, 1);
});

test("passphrases under 6 characters, malformed hash settings and extra session segments fail", () => {
  assert.throws(() => hashPassword("12345"), /6 to 256 characters/);
  // The owner allowed a 6-digit code.
  assert.equal(verifyPassword("123456", hashPassword("123456")), true);
  const encoded = hashPassword("longer passphrase");
  assert.throws(() => verifyPassword("longer passphrase", encoded.replace("$16384$", "$999999999$")), /invalid format/);
  assert.equal(validSession(SECRET, createSession(SECRET, 1000) + ".extra", 1001), false);
});
