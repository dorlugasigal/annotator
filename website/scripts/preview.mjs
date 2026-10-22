import {statSync} from "node:fs";
import {resolve, sep} from "node:path";
import {fileURLToPath} from "node:url";
import {parseArgs} from "node:util";
import {siteConfig} from "../site-config.mjs";

export function normalizeMediaSuffixRange(request, directory, base = "/") {
  const range = request.headers.range;
  const suffix = typeof range === "string" && /^bytes=-(\d+)$/.exec(range);
  if (!suffix || !["GET", "HEAD"].includes(request.method)) return;
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
  } catch (error) {
    if (error instanceof URIError || error instanceof TypeError) return;
    throw error;
  }
  if (!pathname.startsWith(base) || !pathname.endsWith(".mp4")) return;
  const file = resolve(directory, pathname.slice(base.length));
  if (!file.startsWith(resolve(directory) + sep)) return;
  let stat;
  try {
    stat = statSync(file, {throwIfNoEntry: false});
  } catch (error) {
    console.error("Could not inspect a preview video for a byte-range request.", error);
    return;
  }
  if (!stat?.isFile()) return;
  const count = Number(suffix[1]);
  // Vite's preview handler treats "-N" as "0-N"; normalize it before that handler.
  request.headers.range = count > 0 && stat.size > 0
    ? `bytes=${Math.max(0, stat.size - count)}-${stat.size - 1}`
    : `bytes=${stat.size}-`;
}

if (import.meta.main) {
  const {values} = parseArgs({options: {port: {type: "string", default: "4321"}}});
  if (!/^\d+$/.test(values.port) || Number(values.port) < 1024 || Number(values.port) > 65535) {
    throw new Error("Preview port must be between 1024 and 65535.");
  }
  const {preview} = await import("astro");
  const server = await preview({
    root: fileURLToPath(new URL("../", import.meta.url)),
    server: {host: "127.0.0.1", port: Number(values.port)},
  });
  const directory = fileURLToPath(new URL("../dist/", import.meta.url));
  const base = siteConfig().base;
  server.server.prependListener("request", request => normalizeMediaSuffixRange(request, directory, base));
}
