import { createHmac } from "node:crypto";
import type { KeyValueStore } from "./store.ts";
import { dayString } from "./days.ts";
import type { AppPayload, WebPayload } from "./validate.ts";

export function pseudonym(secret: string, installation: string): string {
  return createHmac("sha256", secret).update(`annotator-install-v1:${installation}`).digest("base64url").slice(0, 32);
}

// p is the platform; records stored before Windows existed have none and are macOS.
export interface StoredAppDay {
  v: string; b: string; os: number; a: string; p?: "macos" | "windows"; s: string; l: string; f: Record<string, string>;
}

export async function storeApp(store: KeyValueStore, secret: string, payload: AppPayload): Promise<number> {
  if (secret.length < 32) throw new Error("METRICS_HMAC_SECRET must contain at least 32 characters.");
  const id = pseudonym(secret, payload.installation);
  for (const day of payload.days) {
    const record: StoredAppDay = {
      v: payload.version, b: payload.build, os: payload.osMajor, a: payload.architecture, p: payload.platform,
      s: payload.since, l: day.launches, f: day.features,
    };
    await store.set(`app/${day.day}/${id}`, JSON.stringify(record));
  }
  return payload.days.length;
}

export interface StoredWebSession { r: string; ref: string }

export async function storeWeb(store: KeyValueStore, payload: WebPayload, now = new Date()): Promise<void> {
  const day = dayString(now);
  if (payload.event === "session") {
    await store.set(`web/${day}/${payload.session}`, JSON.stringify({ r: payload.route, ref: payload.referrer }));
  } else {
    await store.set(`dl/${day}/${payload.session}/${payload.track}`, JSON.stringify({ r: payload.route }));
  }
}
