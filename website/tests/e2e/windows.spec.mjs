import {test, expect} from "@playwright/test";
import {siteConfig} from "../../site-config.mjs";
import {readingWords, storeId, visitors} from "./visitors.mjs";

const base = siteConfig().base.replace(/\/$/, "");
const sitePath = path => base + path;
const routes = ["/", "/docs/", "/privacy/", "/support/", "/ai/", "/downloads/"];
// Anything that offers, describes or names Windows. Spec 013 (WIN-DST-08) allows it only with a published listing.
const claims = /\bWindows\b|winget|msstore|Microsoft Store|apps\.microsoft\.com|Partner Center|Snipping Tool|22H2|ARM64|Ctrl\+/;
// The hero's first button, and the order of the install section's cards.
const heroButton = page => page.locator(".hero .get:visible .btn-accent");
const cardOrder = page => page.locator("#install .platform").evaluateAll(cards => cards.map(card => card.dataset.os));
async function asVisitor(browser, userAgent, work, options = {}) {
  const context = await browser.newContext({userAgent, ...options});
  try { await work(await context.newPage()); } finally { await context.close(); }
}

if (!storeId) {
  test("without a published Store listing, no page offers, describes or shows Windows", async ({page, browser}) => {
    for (const path of routes) {
      await page.goto(sitePath(path));
      expect(await page.content(), path).not.toMatch(claims);
      await expect(page.locator('a[href*="apps.microsoft.com"], .store-link, .windows-key, [data-platform-switch], [data-for="windows"], [data-os="windows"]'), path).toHaveCount(0);
    }
    // A Windows visitor gets the same page: the Mac download.
    await asVisitor(browser, visitors.windows, async visitor => {
      await visitor.goto(sitePath("/"));
      await expect(heroButton(visitor)).toHaveText("Download for Mac");
      await expect(visitor.locator("#install .download-mac")).toBeVisible();
    });
  });
} else {
  const listingURL = `https://apps.microsoft.com/detail/${storeId}`;
  const winget = `winget install --id ${storeId} --source msstore`;

  test("the hero and Get Annotator start on the visitor's system, with the other one close by", async ({browser}) => {
    for (const [system, userAgent, first] of [["Windows", visitors.windows, "windows"], ["Mac", visitors.mac, "mac"],
      ["Linux", visitors.linux, "mac"], ["iPhone", visitors.iphone, "mac"]]) {
      await asVisitor(browser, userAgent, async page => {
        await page.goto(sitePath("/"));
        await expect(page.locator("html"), system).toHaveAttribute("data-platform", first);
        const hero = page.locator(".hero .get:visible");
        if (first === "windows") {
          await expect(heroButton(page)).toHaveText("Get it for Windows");
          await expect(heroButton(page)).toHaveAttribute("href", listingURL);
          await expect(hero.getByRole("link", {name: "Download for Mac", exact: true})).toBeVisible();
          expect(await cardOrder(page)).toEqual(["windows", "mac"]);
        } else {
          await expect(heroButton(page)).toHaveText("Download for Mac");
          await expect(hero.getByRole("link", {name: "Get it for Windows", exact: true})).toHaveAttribute("href", listingURL);
          expect(await cardOrder(page)).toEqual(["mac", "windows"]);
        }
        // The visitor's own card has the filled button; the other card's is an outline until hovered.
        const words = os => page.locator(`#install .platform[data-os="${os}"] .btn-accent`).evaluate(element => getComputedStyle(element).color);
        expect(await words(first)).toBe("rgb(20, 20, 26)");
        expect(await words(first === "windows" ? "mac" : "windows")).not.toBe("rgb(20, 20, 26)");
      });
    }
  });

  test("choosing a system on Downloads carries to the homepage for the rest of the visit", async ({page}) => {
    await page.goto(sitePath("/downloads/"));
    await page.getByRole("group", {name: "Downloads for", exact: true}).getByRole("button", {name: "Windows", exact: true}).click();
    await page.goto(sitePath("/"));
    await expect(heroButton(page)).toHaveText("Get it for Windows");
    await page.goto(sitePath("/downloads/"));
    await page.getByRole("group", {name: "Downloads for", exact: true}).getByRole("button", {name: "Mac", exact: true}).click();
    await page.goto(sitePath("/"));
    await expect(heroButton(page)).toHaveText("Download for Mac");
  });

  test("Store links have no badge and are never counted as downloads", async ({page}) => {
    await page.goto(sitePath("/"));
    const links = page.locator('a[href*="apps.microsoft.com"]');
    expect(await links.count()).toBeGreaterThan(1);
    for (const link of await links.all()) {
      await expect(link).toHaveAttribute("href", listingURL);
      expect(await link.getAttribute("data-download")).toBeNull();
    }
    await expect(page.locator("img")).toHaveCount(0);
  });

  test("winget copies only on request", async ({browser, browserName}) => {
    test.skip(browserName !== "chromium", "Only Chromium lets a test grant clipboard access.");
    await asVisitor(browser, visitors.windows, async page => {
      await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
      await page.goto(sitePath("/"));
      const card = page.locator('.platform[data-os="windows"]');
      await expect(card.locator("code")).toHaveText(winget);
      await card.getByRole("button", {name: "Copy the winget command"}).click();
      await expect(card.locator(".command p")).toHaveText("Copied. Paste it into Terminal and press Enter.");
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(winget);
    });
  });

  test("the cards show a Windows visitor the Windows keys", async ({browser}) => {
    await asVisitor(browser, visitors.windows, async page => {
      await page.goto(sitePath("/"));
      const keys = page.locator('.card .keys[data-for="windows"]');
      await expect(keys).toHaveCount(3);
      await expect(page.locator('.card .keys[data-for="mac"]').first()).toBeHidden();
      await expect(keys.nth(0)).toHaveAttribute("aria-label", "Control 1");
      await expect(keys.nth(0).locator("kbd")).toHaveText(["Ctrl", "1"]);
      await expect(keys.nth(1).locator("kbd")).toHaveText(["Ctrl", "L"]);
    });
  });

  test("the homepage stays short for a Mac and a Windows visitor", async ({browser}) => {
    for (const userAgent of [visitors.mac, visitors.windows]) {
      await asVisitor(browser, userAgent, async page => {
        await page.goto(sitePath("/"));
        expect(await readingWords(page), userAgent).toBeLessThan(300);
      });
    }
  });

  test("Help lists the Windows keys beside the Mac keys", async ({page}) => {
    await page.goto(sitePath("/docs/"));
    await expect(page.locator(".start-steps")).toContainText("Press Control-Shift-2 (Ctrl+Shift+2 on Windows) to draw again.");
    for (const [mac, keys] of [["Control-Option-2", "Ctrl+Alt+Shift+2"], ["Command-Return", "Ctrl+Enter"], ["Delete", "Backspace or Delete"]]) {
      await expect(page.locator("dt", {hasText: mac}).locator(".windows-key")).toHaveText(`Windows: ${keys}`);
    }
    const article = page.locator("article");
    for (const text of ["Command-L (Ctrl+L on Windows) opens it again", "Escape or Command-Return (Ctrl+Enter on Windows) to finish",
      "Command-A (Ctrl+A on Windows) selects drawings across screens", "Control-W toggles Whiteboard and Control-B toggles Blackboard (Ctrl+W and Ctrl+B on Windows)",
      "Screenshot tool (Shift-Command-5) or, on Windows, Snipping Tool (Win+Shift+S) and capture"]) {
      await expect(article).toContainText(text);
    }
  });

  test("Downloads, Help, AI, Support and Privacy answer for Windows too", async ({page}) => {
    await page.goto(sitePath("/downloads/"));
    // A Mac visitor starts on the Mac downloads; the switch shows the Windows ones.
    await expect(page.locator("#windows")).toBeHidden();
    await page.getByRole("group", {name: "Downloads for", exact: true}).getByRole("button", {name: "Windows", exact: true}).click();
    await expect(page.locator("#homebrew")).toBeHidden();
    const section = page.locator("#windows");
    await expect(section.getByRole("link", {name: "Get it for Windows", exact: true})).toHaveAttribute("href", listingURL);
    await expect(section).toContainText("Requires Windows 11, version 22H2 or later, on an x64 or ARM64 PC.");
    await expect(page.getByRole("region", {name: "winget install command"})).toHaveText(winget);
    await expect(page.locator("article")).toContainText("need Screen Recording permission on a Mac and screen access on Windows.");
    await page.goto(sitePath("/docs/"));
    await page.getByRole("navigation", {name: "On this page"}).getByRole("link", {name: "Windows", exact: true}).click();
    await expect(page).toHaveURL(/#windows$/);
    await expect(page.locator("#windows")).toBeInViewport();
    await expect(page.getByRole("link", {name: "Get Annotator from the Microsoft Store", exact: true})).toHaveAttribute("href", listingURL);
    await expect(page.getByRole("region", {name: "winget install command"})).toHaveText(winget);
    await page.locator("article").getByRole("link", {name: "screen access", exact: true}).click();
    await expect(page.locator("#windows-permissions")).toBeInViewport();
    await expect(page.locator("#windows-permissions")).toContainText("On Windows, drawing, global shortcuts and Alt+scroll over other apps need no permission.");
    const help = page.locator("article");
    await expect(help).toContainText("On Windows, the switch in Settings > General is Launch at sign-in.");
    await expect(help).toContainText("On Windows, when animation effects are off, a short fade replaces any movement.");
    await expect(help).toContainText("On Windows, the Microsoft Store installs Annotator and its updates");
    await page.goto(sitePath("/ai/"));
    await expect(page.locator("article")).toContainText("On Windows, it uses Snipping Tool (Win+Shift+S) and its recorder instead.");
    await page.goto(sitePath("/support/"));
    await expect(page.locator("article")).toContainText("It includes app and macOS or Windows versions");
    await page.goto(sitePath("/privacy/"));
    await expect(page.locator("h1")).toHaveText("What stays on your computer.");
    const privacy = page.locator("article");
    await expect(privacy).toContainText("PNG export saves an image on your computer.");
    await expect(privacy).toContainText("Annotator for Windows doesn't send usage counts yet.");
    await expect(privacy).toContainText("we can see how many people got and installed Annotator from the Store");
    await expect(privacy.getByRole("heading", {name: "Microsoft Store and Windows", exact: true})).toBeVisible();
  });

  test("without scripts, both systems' cards show and the hero offers the Mac", async ({browser, baseURL}) => {
    const context = await browser.newContext({javaScriptEnabled: false, userAgent: visitors.windows});
    try {
      const page = await context.newPage();
      await page.goto(baseURL);
      await expect(heroButton(page)).toHaveText("Download for Mac");
      await expect(page.locator('#install .platform[data-os="mac"]')).toBeVisible();
      await expect(page.locator('#install .platform[data-os="windows"]')).toBeVisible();
      await expect(page.locator('#install .platform[data-os="windows"] code')).toHaveText(winget);
    } finally { await context.close(); }
  });
}
