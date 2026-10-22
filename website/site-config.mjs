export function siteConfig(value = process.env.SITE_URL) {
  if (!value) return { site: undefined, base: "/" };
  if (/(?:\/|%2f)\.\.?(?:\/|%2f|$)/i.test(value) || value.includes("\\")) {
    throw new Error("SITE_URL cannot contain path traversal.");
  }
  const url = new URL(value);
  if (url.protocol !== "https:" || url.username || url.password || url.port || url.search || url.hash ||
      !/^\/(?:[A-Za-z0-9_-]+\/)*[A-Za-z0-9_-]*$/.test(url.pathname)) {
    throw new Error("SITE_URL must be an HTTPS URL with a simple optional site path.");
  }
  return { site: url.origin, base: url.pathname.replace(/\/?$/, "/") };
}
