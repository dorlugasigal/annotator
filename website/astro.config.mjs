import { defineConfig } from "astro/config";
import { siteConfig } from "./site-config.mjs";

export default defineConfig({
  output: "static",
  ...siteConfig(),
  vite: { build: { assetsInlineLimit: 0 } },
});
