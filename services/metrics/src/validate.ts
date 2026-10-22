import { addDays, dayString, isDay } from "./days.ts";

export const BUCKETS = ["0", "1", "2-5", "6-20", "21+"] as const;
export const FEATURES = [
  "drawing", "editing", "textOrShape", "coverOrEffect", "zoom", "imageInsertion",
  "boardOpen", "boardSave", "pngExport", "reusableShape",
] as const;
export const ROUTES = ["home", "downloads", "docs", "privacy", "support", "ai", "other"] as const;
export const REFERRERS = ["direct", "search", "social", "github", "other"] as const;
export const TRACKS = ["stable", "preview"] as const;
// Mac clients send no platform and are read as macOS; Windows clients send "windows".
export const PLATFORMS = ["macos", "windows"] as const;
export const MAX_BODY_BYTES = 8_192;

export class ValidationError extends Error {
  readonly status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

export interface AppDay { day: string; launches: string; features: Record<string, string> }
export interface AppPayload {
  schema: 1; installation: string; since: string; version: string; build: string;
  osMajor: number; architecture: string; platform: (typeof PLATFORMS)[number]; days: AppDay[];
}
export interface WebPayload {
  event: "session" | "download"; session: string;
  route: (typeof ROUTES)[number]; referrer: (typeof REFERRERS)[number]; track?: (typeof TRACKS)[number];
}

function object(value: unknown, allowed: string[], name: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new ValidationError(400, `${name} must be an object.`);
  for (const key of Object.keys(value)) if (!allowed.includes(key)) throw new ValidationError(400, `Unsupported field: ${key}.`);
  return value as Record<string, unknown>;
}

function oneOf<T extends string>(value: unknown, options: readonly T[], name: string): T {
  if (typeof value !== "string" || !options.includes(value as T)) throw new ValidationError(400, `Invalid ${name}.`);
  return value as T;
}

function pattern(value: unknown, regex: RegExp, name: string): string {
  if (typeof value !== "string" || !regex.test(value)) throw new ValidationError(400, `Invalid ${name}.`);
  return value;
}

export function parseBody(text: string): unknown {
  if (Buffer.byteLength(text) > MAX_BODY_BYTES) throw new ValidationError(413, "The request is too large.");
  try { return JSON.parse(text); } catch { throw new ValidationError(400, "The request is not valid JSON."); }
}

export function validateApp(value: unknown, now = new Date()): AppPayload {
  const body = object(value,
    ["schema", "installation", "since", "version", "build", "osMajor", "architecture", "platform", "days"], "Payload");
  if (body.schema !== 1) throw new ValidationError(400, "Unsupported schema.");
  const installation = pattern(body.installation, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/, "installation");
  const version = pattern(body.version, /^[0-9A-Za-z.+-]{1,32}$/, "version");
  const build = pattern(body.build, /^\d{1,9}$/, "build");
  const platform = body.platform === undefined ? "macos" : oneOf(body.platform, PLATFORMS, "platform");
  if (!Number.isInteger(body.osMajor) || (body.osMajor as number) < 11 || (body.osMajor as number) > 99)
    throw new ValidationError(400, "Invalid osMajor.");
  // Windows builds exist only for x64 and ARM64.
  const architecture = oneOf(body.architecture, platform === "windows" ? ["arm64", "x86_64"] : ["arm64", "x86_64", "other"],
    "architecture");
  const today = dayString(now);
  const earliest = addDays(today, -7);
  if (!isDay(body.since) || body.since > today) throw new ValidationError(400, "Invalid since.");
  const since = body.since;
  if (!Array.isArray(body.days) || body.days.length < 1 || body.days.length > 8) throw new ValidationError(400, "Invalid days.");
  const seen = new Set<string>();
  const days = body.days.map((entry) => {
    const item = object(entry, ["day", "launches", "features"], "Day");
    if (!isDay(item.day) || item.day < earliest || item.day < since || item.day > today || seen.has(item.day))
      throw new ValidationError(400, "Invalid day.");
    seen.add(item.day);
    const features = object(item.features, [...FEATURES], "Features");
    const clean: Record<string, string> = {};
    for (const [name, bucket] of Object.entries(features)) clean[name] = oneOf(bucket, BUCKETS, "feature bucket");
    return { day: item.day, launches: oneOf(item.launches, BUCKETS, "launches"), features: clean };
  });
  return { schema: 1, installation, since, version, build, osMajor: body.osMajor as number, architecture, platform, days };
}

export function validateWeb(value: unknown): WebPayload {
  const body = object(value, ["event", "session", "route", "referrer", "track"], "Payload");
  const event = oneOf(body.event, ["session", "download"], "event");
  const payload: WebPayload = {
    event,
    session: pattern(body.session, /^[a-z0-9]{16,40}$/, "session"),
    route: oneOf(body.route, ROUTES, "route"),
    referrer: oneOf(body.referrer, REFERRERS, "referrer"),
  };
  if (event === "download") payload.track = oneOf(body.track, TRACKS, "track");
  else if (body.track !== undefined) throw new ValidationError(400, "Unsupported field: track.");
  return payload;
}
