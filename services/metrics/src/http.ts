import type { KeyValueStore } from "./store.ts";
import { MAX_BODY_BYTES, ValidationError, parseBody, validateApp, validateWeb } from "./validate.ts";
import { storeApp, storeWeb } from "./collect.ts";
import { aggregateDay, dashboard, refreshGitHub, runDaily, type Fetcher, type GitHubRefresh } from "./aggregate.ts";
import { dayString } from "./days.ts";
import {
  SESSION_COOKIE, clearedCookie, createSession, loginBlocked, readCookie, recordFailure, sessionCookie,
  validSession, verifyPassword,
} from "./auth.ts";

export interface Env {
  METRICS_HMAC_SECRET?: string;
  ADMIN_PASSWORD_HASH?: string;
  ADMIN_SESSION_SECRET?: string;
  METRICS_GITHUB_REPOSITORY?: string;
  METRICS_GITHUB_TOKEN?: string;
  METRICS_HOMEBREW_CASK?: string;
}

export interface Deps { store: KeyValueStore; env: Env; now?: () => Date; fetcher?: Fetcher }

const JSON_HEADERS = { "Content-Type": "application/json", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" };

function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { ...JSON_HEADERS, ...headers } });
}

function failure(error: unknown, headers: Record<string, string> = {}): Response {
  if (error instanceof ValidationError) return json(error.status, { error: error.message }, headers);
  console.error(JSON.stringify({ event: "metrics_failure", type: error instanceof Error ? error.name : "unknown" }));
  return json(503, { error: "The metrics service is unavailable. Retry later." }, headers);
}

async function readText(request: Request): Promise<string> {
  const declared = Number(request.headers.get("content-length") ?? "0");
  if (declared > MAX_BODY_BYTES) throw new ValidationError(413, "The request is too large.");
  const reader = request.body?.getReader();
  if (!reader) return "";
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_BODY_BYTES) {
        await reader.cancel();
        throw new ValidationError(413, "The request is too large.");
      }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  return Buffer.concat(chunks).toString("utf8");
}

function config(env: Env) {
  return {
    githubRepository: env.METRICS_GITHUB_REPOSITORY ?? "dorlugasigal/annotator",
    githubToken: env.METRICS_GITHUB_TOKEN,
    homebrewCask: env.METRICS_HOMEBREW_CASK ?? "dorlugasigal/annotator/annotator",
  };
}

export async function handleApp(request: Request, deps: Deps): Promise<Response> {
  if (request.method !== "POST") return json(405, { error: "Use POST." });
  try {
    if (!(request.headers.get("content-type") ?? "").startsWith("application/json"))
      throw new ValidationError(415, "JSON is required.");
    if (!deps.env.METRICS_HMAC_SECRET) throw new Error("METRICS_HMAC_SECRET is not configured.");
    const payload = validateApp(parseBody(await readText(request)), deps.now?.() ?? new Date());
    const stored = await storeApp(deps.store, deps.env.METRICS_HMAC_SECRET, payload);
    return json(202, { accepted: stored });
  } catch (error) { return failure(error); }
}

export async function handleWeb(request: Request, deps: Deps): Promise<Response> {
  const origin = request.headers.get("origin") ?? "";
  if (new URL(request.url).protocol !== "https:" || origin !== new URL(request.url).origin)
    return json(403, { error: "This origin is not allowed." });
  if (request.method !== "POST") return json(405, { error: "Use POST." });
  try {
    const type = request.headers.get("content-type") ?? "";
    if (!type.startsWith("text/plain") && !type.startsWith("application/json")) throw new ValidationError(415, "JSON is required.");
    await storeWeb(deps.store, validateWeb(parseBody(await readText(request))), deps.now?.() ?? new Date());
    return json(202, { accepted: true });
  } catch (error) { return failure(error); }
}

function authorized(request: Request, env: Env, now: Date): boolean {
  return validSession(env.ADMIN_SESSION_SECRET ?? "", readCookie(request.headers.get("cookie"), SESSION_COOKIE),
    Math.floor(now.getTime() / 1000));
}

function sameOrigin(request: Request): boolean {
  const origin = request.headers.get("origin");
  return origin === null || origin === new URL(request.url).origin;
}

export async function handleLogin(request: Request, deps: Deps): Promise<Response> {
  if (request.method !== "POST") return json(405, { error: "Use POST." });
  if (!sameOrigin(request)) return json(403, { error: "This origin is not allowed." });
  const now = deps.now?.() ?? new Date();
  const seconds = Math.floor(now.getTime() / 1000);
  try {
    if (!deps.env.ADMIN_PASSWORD_HASH || !deps.env.ADMIN_SESSION_SECRET) throw new Error("Admin access is not configured.");
    if (await loginBlocked(deps.store, seconds)) return json(429, { error: "Too many failed sign-ins. Wait 15 minutes and try again." });
    const body = parseBody(await readText(request)) as { password?: unknown };
    const password = typeof body?.password === "string" ? body.password : "";
    if (!password || password.length > 256 || !verifyPassword(password, deps.env.ADMIN_PASSWORD_HASH)) {
      await recordFailure(deps.store, seconds);
      return json(401, { error: "That password didn't work." });
    }
    return json(200, { ok: true }, { "Set-Cookie": sessionCookie(createSession(deps.env.ADMIN_SESSION_SECRET, seconds)) });
  } catch (error) { return failure(error); }
}

export function handleLogout(request: Request): Response {
  if (request.method !== "POST") return json(405, { error: "Use POST." });
  if (!sameOrigin(request)) return json(403, { error: "This origin is not allowed." });
  return json(200, { ok: true }, { "Set-Cookie": clearedCookie });
}

export async function handleSummary(request: Request, deps: Deps): Promise<Response> {
  const now = deps.now?.() ?? new Date();
  if (!authorized(request, deps.env, now)) return json(401, { error: "Sign in to view metrics." });
  try {
    let refresh: GitHubRefresh | undefined;
    if (request.method === "POST") {
      if (!sameOrigin(request)) return json(403, { error: "This origin is not allowed." });
      await aggregateDay(deps.store, dayString(now), now);
      refresh = await refreshGitHub(deps.store, dayString(now), config(deps.env), deps.fetcher ?? fetch, now);
    } else if (request.method !== "GET") return json(405, { error: "Use GET or POST." });
    return json(200, { ...(await dashboard(deps.store, now)), ...(refresh ? { refresh } : {}) });
  } catch (error) { return failure(error); }
}

export async function handleScheduled(deps: Deps): Promise<void> {
  const result = await runDaily(deps.store, config(deps.env), deps.fetcher ?? fetch, deps.now?.() ?? new Date());
  console.log(JSON.stringify({ event: "metrics_aggregated", day: result.day, removed: result.removed, errors: result.errors.length }));
}
