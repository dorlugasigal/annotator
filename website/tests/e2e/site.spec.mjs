import {test, expect} from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import {readFileSync} from "node:fs";
import {siteConfig} from "../../site-config.mjs";

const base = siteConfig().base.replace(/\/$/, "");
const sitePath = path => base + path;
const routes = ["/", "/docs/", "/privacy/", "/support/", "/ai/", "/downloads/"];
const {release} = JSON.parse(readFileSync(process.env.DISTRIBUTION_METADATA_FILE ||
  new URL("../../src/data/distribution.json", import.meta.url), "utf8"));
// The Mac button offers the disk image when the release has one, otherwise the ZIP, or GitHub's release list.
const download = release?.diskImageURL || release?.downloadURL || "https://github.com/dorlugasigal/annotator/releases";
const repository = "https://github.com/dorlugasigal/annotator";
const coffee = "https://buymeacoffee.com/dorlugasigal";

test("pages, fonts and the AI skill load without missing files or console errors", async ({page, request}) => {
  const missing = [], errors = [];
  page.on("response", response => { if (response.status() >= 400) missing.push(response.url()); });
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  page.on("pageerror", error => errors.push(error.message));
  for (const path of routes) {
    await page.goto(sitePath(path));
    await page.evaluate(() => document.fonts.ready);
    // Down the whole page, so every section's scenes and drawings start.
    await page.evaluate(async () => {
      for (let top = 0; top <= document.documentElement.scrollHeight; top += innerHeight / 2) {
        scrollTo({top, behavior: "instant"});
        await new Promise(resolve => setTimeout(resolve, 60));
      }
    });
  }
  expect(missing).toEqual([]);
  expect(errors).toEqual([]);
  expect(await page.evaluate(() => document.fonts.check('16px "Instrument Sans"'))).toBe(true);
  const skill = await request.get(sitePath("/downloads/annotator-skill.zip"));
  expect(skill.status()).toBe(200);
  expect((await skill.body()).subarray(0, 2).toString()).toBe("PK");
});

test("every page has one main heading and no sideways scrolling, from phones to wide screens", async ({page}) => {
  for (const width of [320, 390, 820, 1440, 2560]) {
    await page.setViewportSize({width, height: 900});
    for (const path of routes) {
      const response = await page.goto(sitePath(path));
      expect([200, 304]).toContain(response.status());
      await expect(page.locator("h1")).toHaveCount(1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${path} at ${width}px`).toBe(true);
    }
  }
});

// Audit finished states: let every heading and group arrive, then wait for motion that ends.
async function settle(page) {
  await page.evaluate(async () => {
    for (let top = 0; top <= document.documentElement.scrollHeight; top += innerHeight / 2) {
      scrollTo({top, behavior: "instant"});
      await new Promise(resolve => setTimeout(resolve, 80));
    }
    scrollTo({top: 0, behavior: "instant"});
    await new Promise(resolve => setTimeout(resolve, 80));
    await Promise.all(document.getAnimations()
      .filter(animation => animation.timeline === document.timeline && animation.effect?.getTiming().iterations !== Infinity)
      .map(animation => animation.finished.catch(() => undefined)));
  });
}

for (const theme of ["dark", "light"]) for (const path of routes) {
  test(`${path} passes the accessibility checks in the ${theme} theme`, async ({page}) => {
    await page.goto(sitePath(`${path}?theme=${theme}`));
    await settle(page);
    await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
    const result = await new AxeBuilder({page}).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
    expect(result.violations.map(v => ({id: v.id, targets: v.nodes.map(n => n.target)}))).toEqual([]);
  });
}

test("download buttons target the verified disk image, ZIP or public release list, and count only real downloads", async ({page}) => {
  await page.goto(sitePath("/"));
  const buttons = page.locator(".download-mac");
  await expect(buttons).toHaveCount(2);
  for (const button of await buttons.all()) {
    await expect(button).toHaveAttribute("href", download);
    await expect(button).toHaveText("Download for Mac");
    expect(await button.getAttribute("data-download")).toBe(release ? "" : null);
  }
  // The version is named on its own: no release stage, track or build number.
  if (release) await expect(page.locator("#install .version")).toHaveText(`Version ${release.releaseVersion} · Release notes`);
  else await expect(page.locator("#install .version")).toHaveCount(0);
  await expect(page.locator('a[href*="checkout"], form[action]')).toHaveCount(0);
  // A disk image link appears only for a release whose verified metadata includes one.
  expect(new Set(await page.locator('a[href$=".dmg"]').evaluateAll(links => links.map(link => link.href))))
    .toEqual(new Set(release?.diskImageURL ? [release.diskImageURL] : []));
});

test("the download buttons answer hover and keyboard focus the same way, and keep their words readable", async ({page}) => {
  await page.goto(sitePath("/?motion=off"));
  const button = page.locator("#install .download-mac");
  await button.scrollIntoViewIfNeeded();
  const look = () => button.evaluate(element => {
    const layer = getComputedStyle(element, "::before");
    return {position: layer.backgroundPosition, color: getComputedStyle(element).color};
  });
  const rest = await look();
  await button.hover();
  const hovered = await look();
  expect(hovered.position).not.toBe(rest.position);
  await page.mouse.move(0, 0);
  // Keyboard focus: a key press first, so the browser treats the focus that follows as the keyboard's.
  await page.keyboard.press("Shift");
  await button.focus();
  expect(await button.evaluate(element => element.matches(":focus-visible"))).toBe(true);
  expect(await look()).toEqual(hovered);
  // With reduced motion the button changes color only: nothing moves.
  expect(await button.evaluate(element => getComputedStyle(element).transform)).toBe("none");
});

test("downloads, Help and the theme are usable with the keyboard", async ({page}) => {
  await page.route("https://github.com/**", route => route.fulfill({contentType: "text/html", body: "<h1>Download fixture</h1>"}));
  await page.goto(sitePath("/"));
  const header = page.locator(".site-header");
  const get = header.getByRole("link", {name: "Download", exact: true});
  await get.focus();
  await get.press("Enter");
  await expect(page).toHaveURL(/#install$/);
  const button = page.locator("#install .download-mac");
  await button.focus();
  await button.press("Enter");
  await expect(page).toHaveURL(download);
  await page.goto(sitePath("/"));
  const themeSwitch = header.getByRole("switch", {name: "Dark theme"});
  await themeSwitch.focus();
  await themeSwitch.press("Space");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await page.locator(".site-footer").getByRole("link", {name: "Help", exact: true}).click();
  await expect(page.locator(".site-footer").getByRole("link", {name: "Help", exact: true})).toHaveAttribute("aria-current", "page");
  const topics = page.getByRole("navigation", {name: "On this page"});
  for (const link of await topics.getByRole("link").all()) await expect(page.locator(await link.getAttribute("href"))).toHaveCount(1);
});

test("the theme switch changes every page and keeps the choice", async ({page}) => {
  await page.goto(sitePath("/"));
  const html = page.locator("html"), themeSwitch = page.getByRole("switch", {name: "Dark theme"});
  await expect(html).toHaveAttribute("data-theme", "dark");
  await expect(themeSwitch).toHaveAttribute("aria-checked", "true");
  await themeSwitch.click();
  await expect(html).toHaveAttribute("data-theme", "light");
  await expect(themeSwitch).toHaveAttribute("aria-checked", "false");
  await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute("content", "#f9f8fa");
  // The demo's toolbar follows the page.
  await expect(page.locator("#desk .ink-toolbar")).toHaveAttribute("data-appearance", "light");
  expect(await page.evaluate(() => localStorage.getItem("annotator.site.theme"))).toBe("light");
  for (const path of ["/docs/", "/privacy/"]) {
    await page.goto(sitePath(path));
    await expect(html).toHaveAttribute("data-theme", "light");
    expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe("rgb(249, 248, 250)");
  }
  await page.getByRole("switch", {name: "Dark theme"}).click();
  await page.reload();
  await expect(html).toHaveAttribute("data-theme", "dark");
  expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe("rgb(14, 14, 17)");
});

test("the header and footer link to GitHub, Help and Buy Me a Coffee at every width", async ({page}) => {
  for (const width of [1440, 820, 390, 320]) {
    await page.setViewportSize({width, height: 800});
    await page.goto(sitePath("/"));
    const header = page.locator(".site-header"), footer = page.locator(".site-footer");
    await expect(header.getByRole("link", {name: "Buy me a coffee"})).toHaveAttribute("href", coffee);
    await expect(header.getByRole("link", {name: "Buy me a coffee"})).toBeVisible();
    await expect(header.getByRole("link", {name: "Download", exact: true})).toBeVisible();
    await expect(header.getByRole("switch", {name: "Dark theme"})).toBeVisible();
    // Narrow screens keep the main actions in the header; the footer always has the rest.
    if (width >= 760) await expect(header.getByRole("link", {name: "GitHub", exact: true})).toHaveAttribute("href", repository);
    for (const [name, href] of [["Help", sitePath("/docs/")], ["Downloads", sitePath("/downloads/")], ["Privacy", sitePath("/privacy/")],
      ["Report a problem", sitePath("/support/")], ["GitHub", repository], ["Buy me a coffee", coffee]]) {
      await expect(footer.getByRole("link", {name, exact: true})).toHaveAttribute("href", href);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }
});

test("download details still have exact release notes and Homebrew commands", async ({page}) => {
  await page.setViewportSize({width: 320, height: 900});
  await page.goto(sitePath("/downloads/"));
  if (release) {
    await expect(page.locator("h2", {hasText: /^Version /})).toHaveText([`Version ${release.releaseVersion}`]);
    await expect(page.locator(`a[href="${release.downloadURL}"]`)).toHaveCount(1);
    const owner = new URL(release.downloadURL).pathname.split("/").slice(1, 3).join("/");
    await expect(page.getByRole("link", {name: `Read release notes and known limits for ${release.releaseVersion}`, exact: true}))
      .toHaveAttribute("href", `https://github.com/${owner}/releases/tag/v${release.releaseVersion}`);
    await expect(page.getByRole("region", {name: "Homebrew install command"}))
      .toHaveText("brew install --cask dorlugasigal/annotator/annotator");
  } else await expect(page.locator("article")).toContainText("This page has no verified download details.");
});

test("Homebrew copies only on request, and says how to paste it", async ({page, browserName}) => {
  test.skip(browserName !== "chromium", "Only Chromium lets a test grant clipboard access.");
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.goto(sitePath("/"));
  const card = page.locator('.platform[data-os="mac"]');
  await card.getByRole("button", {name: "Copy the Homebrew command"}).click();
  await expect(card.locator(".command p")).toHaveText("Copied. Paste it into Terminal and press Enter.");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe("brew install --cask dorlugasigal/annotator/annotator");
});

test("without JavaScript every section, download and link is there, and the theme switch isn't offered", async ({browser, baseURL}) => {
  const context = await browser.newContext({javaScriptEnabled: false});
  try {
    const page = await context.newPage();
    await page.goto(baseURL);
    await expect(page.locator("h1")).toHaveText("Draw on your screen.");
    for (const heading of ["What you can do", "Good to know", "Free, but I won't say no to a coffee.", "Get Annotator"]) {
      await expect(page.getByRole("heading", {name: heading, exact: true})).toBeVisible();
    }
    for (const card of await page.locator(".card").all()) await expect(card).toBeVisible();
    await expect(page.locator(".hero .download-mac")).toBeVisible();
    await expect(page.locator(".theme-switch")).toBeHidden();
    // Content never waits for a script to be shown.
    expect(await page.locator("main h2, main p, main dt").evaluateAll(nodes => nodes.filter(node => getComputedStyle(node).opacity === "0").length)).toBe(0);
    await page.locator(".site-footer").getByRole("link", {name: "Help", exact: true}).click();
    await expect(page).toHaveURL(/\/docs\/$/);
  } finally { await context.close(); }
});

test("script failure leaves the page readable and its links working", async ({page}) => {
  await page.route("**/*.js", route => route.abort());
  await page.goto(sitePath("/"));
  await expect(page.locator(".hero .download-mac")).toHaveAttribute("href", download);
  await page.locator(".site-footer").getByRole("link", {name: "Help", exact: true}).click();
  await expect(page).toHaveURL(/\/docs\/$/);
});
