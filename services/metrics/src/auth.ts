import { createHmac, randomBytes, scryptSync, timingSafeEqual } from "node:crypto";
import type { KeyValueStore } from "./store.ts";

export const SESSION_COOKIE = "annotator_admin";
export const SESSION_SECONDS = 8 * 3600;
// Circuit breaker for distributed guessing. Per-client limits come from Netlify's rateLimit config.
const MAX_FAILURES = 200;
const FAILURE_WINDOW_SECONDS = 900;
const SCRYPT = { N: 16384, r: 8, p: 1 };

export function hashPassword(password: string, salt = randomBytes(16)): string {
  if (password.length < 6 || password.length > 256) throw new Error("Use a passphrase with 6 to 256 characters.");
  const hash = scryptSync(password, salt, 32, SCRYPT);
  return `scrypt$${SCRYPT.N}$${SCRYPT.r}$${SCRYPT.p}$${salt.toString("base64url")}$${hash.toString("base64url")}`;
}

export function verifyPassword(password: string, encoded: string): boolean {
  const parts = encoded.split("$");
  const [kind, n, r, p, salt, expected] = parts;
  if (parts.length !== 6 || kind !== "scrypt" || Number(n) !== SCRYPT.N ||
      Number(r) !== SCRYPT.r || Number(p) !== SCRYPT.p ||
      !salt || !/^[A-Za-z0-9_-]{22}$/.test(salt) ||
      !expected || !/^[A-Za-z0-9_-]{43}$/.test(expected)) throw new Error("ADMIN_PASSWORD_HASH has an invalid format.");
  const target = Buffer.from(expected, "base64url");
  const actual = scryptSync(password, Buffer.from(salt, "base64url"), target.length, { N: Number(n), r: Number(r), p: Number(p) });
  return timingSafeEqual(actual, target);
}

function signature(secret: string, value: string): string {
  return createHmac("sha256", secret).update(`annotator-admin-v1:${value}`).digest("base64url");
}

export function createSession(secret: string, nowSeconds = Math.floor(Date.now() / 1000)): string {
  if (secret.length < 32) throw new Error("ADMIN_SESSION_SECRET must contain at least 32 characters.");
  const expires = String(nowSeconds + SESSION_SECONDS);
  return `${expires}.${signature(secret, expires)}`;
}

export function validSession(secret: string, token: string | undefined, nowSeconds = Math.floor(Date.now() / 1000)): boolean {
  if (!token || secret.length < 32) return false;
  const parts = token.split(".");
  if (parts.length !== 2) return false;
  const [expires, provided] = parts;
  if (!expires || !provided || !/^\d+$/.test(expires) || Number(expires) <= nowSeconds) return false;
  const expected = Buffer.from(signature(secret, expires));
  const actual = Buffer.from(provided);
  return actual.length === expected.length && timingSafeEqual(actual, expected);
}

export function readCookie(header: string | null, name: string): string | undefined {
  for (const part of (header ?? "").split(";")) {
    const [key, ...value] = part.trim().split("=");
    if (key === name) return value.join("=");
  }
  return undefined;
}

export function sessionCookie(token: string): string {
  return `${SESSION_COOKIE}=${token}; Path=/; Max-Age=${SESSION_SECONDS}; HttpOnly; Secure; SameSite=Strict`;
}

export const clearedCookie = `${SESSION_COOKIE}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Strict`;

function failureKey(nowSeconds: number): string {
  return `auth/failures/${Math.floor(nowSeconds / FAILURE_WINDOW_SECONDS)}`;
}

export async function loginBlocked(store: KeyValueStore, nowSeconds = Math.floor(Date.now() / 1000)): Promise<boolean> {
  return Number(await store.get(failureKey(nowSeconds)) ?? "0") >= MAX_FAILURES;
}

export async function recordFailure(store: KeyValueStore, nowSeconds = Math.floor(Date.now() / 1000)): Promise<void> {
  const key = failureKey(nowSeconds);
  await store.set(key, String(Number(await store.get(key) ?? "0") + 1));
  const previous = failureKey(nowSeconds - FAILURE_WINDOW_SECONDS * 2);
  await store.delete(previous);
}
