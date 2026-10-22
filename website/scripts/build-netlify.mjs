import {cp, mkdir, readFile} from "node:fs/promises";
import {execFileSync} from "node:child_process";
import {fileURLToPath} from "node:url";
import {siteConfig} from "../site-config.mjs";

const site = process.env.SITE_URL || process.env.URL;
if (!site) throw new Error("Set SITE_URL to the approved Netlify website URL.");
const config = siteConfig(site);
if (config.base !== "/" || !config.site || new URL(config.site).hostname.endsWith(".github.io")) {
  throw new Error("The combined Netlify site needs an HTTPS root URL, not the legacy Pages site.");
}
const cwd = fileURLToPath(new URL("..", import.meta.url));
const metadata = JSON.parse(await readFile(new URL("../src/data/distribution.json", import.meta.url), "utf8"));
if (process.env.CONTEXT === "production" && !metadata.stable && !metadata.preview) {
  throw new Error("Generate verified release metadata before a production website build.");
}
const env = {
  ...process.env,
  SITE_URL: config.site,
  ANNOTATOR_SITE_PROVIDER: "netlify",
  PUBLIC_METRICS_URL: process.env.ANNOTATOR_WEB_METRICS_ENABLED === "true" ? config.site : "",
};
for (const script of ["typecheck", "build"]) execFileSync("npm", ["run", script], {cwd, env, stdio: "inherit"});
await mkdir(new URL("../dist/admin", import.meta.url), {recursive: true});
await cp(new URL("../../services/metrics/public/admin/", import.meta.url),
  new URL("../dist/admin/", import.meta.url), {recursive: true, errorOnExist: true});
console.log("Built the website and private dashboard together. Nothing was deployed.");
