import {readFileSync} from "node:fs";

/**
 * Browsers that name the visitor's system. With a Windows listing, the homepage offers that system's download first and
 * starts its install steps there (scripts/platform.ts). Playwright's Desktop Chrome reports Windows, so a test that needs
 * one system's layout says which.
 */
export const visitors = {
  mac: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  windows: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  linux: "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  iphone: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
};

/** The Store ID of the build's Windows listing, or null while the site has none. */
export const storeId = JSON.parse(readFileSync(process.env.WINDOWS_LISTING_FILE ||
  new URL("../../src/data/windows-listing.json", import.meta.url), "utf8")).storeId;

/**
 * The homepage's default reading, as tests/e2e/visual-homepage.spec.mjs counts it: visible prose outside closed
 * disclosures, without control labels (buttons and tabs), tool-name chips, release labels (availability, version and
 * the Windows listing line), Terminal commands, screen-reader-only text and the install picture, which draws the steps
 * listed beside it.
 */
export const readingWords = page => page.locator("main").evaluate(root => {
  const clone = root.cloneNode(true);
  clone.querySelectorAll("details:not([open]), [hidden], video, [data-take-toggle], [data-take-error], button, [role=tab], " +
    ".tool-group ul, .hero-availability, .install-version, .windows-availability, pre, .install-scene, .screen-reader-only").forEach(element => element.remove());
  return clone.textContent.trim().split(/\s+/).length;
});
