export type Route = "home" | "downloads" | "docs" | "privacy" | "support" | "ai" | "other";
export type Referrer = "direct" | "search" | "social" | "github" | "other";

const SESSION_KEY = "annotator-metrics-session";
const SEARCH = /(^|\.)(google|bing|duckduckgo|yahoo|ecosia|kagi|baidu|yandex|brave)\./;
const SOCIAL = /(^|\.)(twitter|x|t|linkedin|lnkd|reddit|facebook|news\.ycombinator|mastodon|threads|bsky|youtube)\./;

export function metricsEndpoint(value: string | undefined): string | undefined {
  if (!value) return undefined;
  let url: URL;
  try { url = new URL(value); } catch { return undefined; }
  if (url.protocol !== "https:" || url.username || url.password || url.port || url.search || url.hash || url.pathname !== "/") return undefined;
  return new URL("/api/metrics/web", url).href;
}

export function routeCategory(pathname: string, base: string): Route {
  const trimmed = pathname.startsWith(base) ? pathname.slice(base.length) : pathname.replace(/^\//, "");
  const first = trimmed.split("/")[0] ?? "";
  if (first === "" || first === "index.html") return "home";
  return (["downloads", "docs", "privacy", "support", "ai"] as const).find((route) => route === first) ?? "other";
}

export function referrerCategory(referrer: string, currentHost: string): Referrer {
  if (!referrer) return "direct";
  let host: string;
  try { host = new URL(referrer).hostname.toLowerCase(); } catch { return "other"; }
  if (host === currentHost) return "direct";
  if (host === "github.com" || host.endsWith(".github.com")) return "github";
  if (SEARCH.test(host)) return "search";
  if (SOCIAL.test(host)) return "social";
  return "other";
}

function optedOut(): boolean {
  const nav = navigator as Navigator & { globalPrivacyControl?: boolean };
  return nav.globalPrivacyControl === true || navigator.doNotTrack === "1";
}

function send(endpoint: string, body: Record<string, string>): void {
  const text = JSON.stringify(body);
  void fetch(endpoint, { method: "POST", body: text, headers: { "Content-Type": "text/plain" }, keepalive: true, credentials: "omit" })
    .then(response => { if (!response.ok) console.info("Usage counts were not accepted. Browsing still works."); })
    .catch(() => { console.info("Usage counts could not be sent. Browsing still works."); });
}

export function start(): void {
  const endpoint = metricsEndpoint(document.body.dataset.metricsEndpoint);
  if (!endpoint || optedOut()) return;
  if (new URL(endpoint).origin !== location.origin) {
    console.error("Usage counts are disabled: the collector must use this site's origin.");
    return;
  }
  const base = document.body.dataset.siteBase ?? "/";
  const route = routeCategory(location.pathname, base);
  const referrer = referrerCategory(document.referrer, location.hostname);
  let session: string;
  let started = false;
  try {
    const stored = sessionStorage.getItem(SESSION_KEY);
    started = stored !== null;
    session = stored ?? [...crypto.getRandomValues(new Uint8Array(16))].map((b) => b.toString(16).padStart(2, "0")).join("");
    if (!started) sessionStorage.setItem(SESSION_KEY, session);
  } catch {
    console.info("Usage counts are disabled: tab storage is unavailable.");
    return;
  }
  if (!started) send(endpoint, { event: "session", session, route, referrer });
  for (const link of document.querySelectorAll<HTMLAnchorElement>("a[data-download-track]")) {
    link.addEventListener("click", () => {
      const track = link.dataset.downloadTrack;
      if (track === "stable" || track === "preview") send(endpoint, { event: "download", session, route, referrer, track });
    });
  }
}
