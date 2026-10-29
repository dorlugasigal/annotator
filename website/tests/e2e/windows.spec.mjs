import {test, expect} from "@playwright/test";
import {siteConfig} from "../../site-config.mjs";
import {readingWords, storeId, visitors} from "./visitors.mjs";

const base = siteConfig().base.replace(/\/$/, "");
const sitePath = path => base + path;
const routes = ["/", "/docs/", "/privacy/", "/support/", "/ai/", "/downloads/"];
// Anything that offers, describes or names Windows. Spec 013 (WIN-DST-08) allows it only with a published listing.
const claims = /\bWindows\b|winget|msstore|Microsoft Store|apps\.microsoft\.com|Partner Center|Snipping Tool|22H2|ARM64|Ctrl\+/;
// Which system's download and steps the install section shows.
const shown = page => page.locator("#install [data-platform-part]:visible").evaluateAll(parts => [...new Set(parts.map(part => part.dataset.platformPart))]);
async function asVisitor(browser, userAgent, work, options = {}) {
  const context = await browser.newContext({userAgent, ...options});
  try { await work(await context.newPage()); } finally { await context.close(); }
}

if (!storeId) {
  test("without a published Store listing, no page offers, describes or shows Windows", async ({page, browser}) => {
    for (const path of routes) {
      await page.goto(sitePath(path));
      expect(await page.content(), path).not.toMatch(claims);
      await expect(page.locator('a[href*="apps.microsoft.com"], .store-action, .store-command, .windows-key, [data-platform-switch]'), path).toHaveCount(0);
    }
    await page.goto(sitePath("/"));
    await expect(page.locator(".shortcut-pair .keycaps").first().locator("kbd")).toHaveText(["⌃", "⇧", "2"]);
    // A Windows visitor gets the same page: the Mac download and its steps.
    await asVisitor(browser, visitors.windows, async visitor => {
      await visitor.goto(sitePath("/"));
      await expect(visitor.locator("#install .download-action")).toBeVisible();
      await expect(visitor.locator('[data-install-guide="mac"]')).toBeVisible();
      await expect(visitor.locator("[data-install-guide=windows]")).toHaveCount(0);
    });
  });
} else {
  const listingURL = `https://apps.microsoft.com/detail/${storeId}`;
  const winget = `winget install --id ${storeId} --source msstore`;

  test("the install section starts on the visitor's system, with a switch for the other", async ({browser}) => {
    for (const [system, userAgent, first] of [["Windows", visitors.windows, "windows"], ["Mac", visitors.mac, "mac"],
      ["Linux", visitors.linux, "mac"], ["iPhone", visitors.iphone, "mac"]]) {
      await asVisitor(browser, userAgent, async page => {
        await page.goto(sitePath("/"));
        expect(await shown(page), system).toEqual([first]);
        const group = page.locator("#install").getByRole("group", {name: "Install on", exact: true});
        await expect(group.getByRole("button", {name: first === "windows" ? "Windows" : "Mac", exact: true})).toHaveAttribute("aria-pressed", "true");
        if (first === "windows") {
          await expect(page.locator("#install").getByRole("link", {name: "Get it for Windows", exact: true})).toBeVisible();
          await expect(page.locator("#install .store-command")).toBeVisible();
          await expect(page.locator("#install .download-action")).toBeHidden();
        } else {
          await expect(page.locator("#install .download-action")).toBeVisible();
          await expect(page.locator("#install #homebrew")).toBeVisible();
          await expect(page.locator("#install .store-command")).toBeHidden();
        }
      });
    }
  });

  test("the switch changes the downloads and the steps together, with mouse or keyboard, and remembers the choice", async ({browser}) => {
    await asVisitor(browser, visitors.windows, async page => {
      await page.goto(sitePath("/"));
      const install = page.locator("#install");
      const group = install.getByRole("group", {name: "Install on", exact: true});
      const mac = group.getByRole("button", {name: "Mac", exact: true});
      const windows = group.getByRole("button", {name: "Windows", exact: true});
      await expect(windows).toHaveAttribute("aria-pressed", "true");
      // Windows has no install video: the Store, the winget command and plain steps, with no picture.
      const guide = install.locator('[data-install-guide="windows"]');
      await expect(guide.locator(".install-steps li")).toHaveText([
        "Get it from the Microsoft Store or with winget.", "Open Annotator from Start.",
        "Pick a tool. Draw. It runs from the system tray."]);
      await expect(install.locator('[data-platform-part="windows"] :is(img, video, picture, figure)')).toHaveCount(0);
      await mac.click();
      expect(await shown(page)).toEqual(["mac"]);
      const scene = install.locator(".install-scene");
      await expect(scene).toBeVisible();
      // The picture draws when it scrolls into view; on a phone it starts below the fold.
      await scene.scrollIntoViewIfNeeded();
      await expect(scene).toHaveClass(/is-revealed/);
      await windows.focus();
      await windows.press("Enter");
      expect(await shown(page)).toEqual(["windows"]);
      await mac.press("Space");
      expect(await shown(page)).toEqual(["mac"]);
      // The choice lasts for the visit, also on Downloads.
      await page.reload();
      expect(await shown(page)).toEqual(["mac"]);
      await page.goto(sitePath("/downloads/"));
      await expect(page.locator("#windows")).toBeHidden();
      await expect(page.locator("#homebrew")).toBeVisible();
    });
  });

  test("a published listing links the Store without a badge or a download count", async ({browser}) => {
    await asVisitor(browser, visitors.windows, async page => {
      await page.route("https://apps.microsoft.com/**", route => route.fulfill({contentType: "text/html", body: "<h1>Store listing fixture</h1>"}));
      await page.goto(sitePath("/"));
      await expect(page.locator(".hero-availability")).toContainText(/ · Windows 11$/);
      const install = page.locator("#install .install-copy");
      await expect(page.locator(".download-action")).toHaveCount(1);
      const link = install.getByRole("link", {name: "Get it for Windows", exact: true});
      await expect(link).toHaveAttribute("href", listingURL);
      // The counting service counts only Mac downloads, and the Store link uses no badge artwork.
      await expect(link).not.toHaveAttribute("data-download");
      await expect(install.locator('[data-platform-part="windows"] img')).toHaveCount(0);
      await expect(install.locator(".windows-availability p")).toHaveText("Microsoft Store · Windows 11, version 22H2 or later · x64 and ARM64");
      await expect(install.locator(".store-command [data-copy-command]")).toHaveText(winget);
      await link.focus();
      await link.press("Enter");
      await expect(page).toHaveURL(listingURL);
    });
  });

  test("winget copies only on request and names the Windows key", async ({browser}) => {
    await asVisitor(browser, visitors.windows, async page => {
      await page.addInitScript(() => {
        window.__copied = [];
        Object.defineProperty(navigator, "clipboard", {configurable: true, value: {
          writeText: async value => { window.__copied.push(value); },
        }});
      });
      await page.goto(sitePath("/"));
      expect(await page.evaluate(() => window.__copied)).toEqual([]);
      const block = page.locator("#install .store-command");
      await block.getByRole("button", {name: "Copy winget command", exact: true}).click();
      expect(await page.evaluate(() => window.__copied)).toEqual([winget]);
      await expect(block.locator("[data-copy-status]")).toHaveText("Copied. Paste into Terminal and press Enter.");
      // The prompt is drawn, so it is not part of the copied text.
      expect(await block.locator("[data-copy-command] > span").evaluate(line => getComputedStyle(line, "::before").content)).toBe('">"');
      // Homebrew keeps its own command and its Mac wording.
      await page.locator("#install").getByRole("button", {name: "Mac", exact: true}).click();
      await page.getByRole("button", {name: "Copy Homebrew command", exact: true}).click();
      expect(await page.evaluate(() => window.__copied)).toEqual([winget, "brew install --cask dorlugasigal/annotator/annotator"]);
      await expect(page.locator("#homebrew [data-copy-status]")).toHaveText("Copied. Paste into Terminal and press Return.");
    });
  });

  test("the product videos are the Mac recordings for everyone, with no Windows pictures", async ({browser}) => {
    await asVisitor(browser, visitors.windows, async page => {
      await page.goto(sitePath("/"));
      const stage = page.locator("#workspace");
      await expect(stage.getByRole("group", {name: "Show the app on"})).toHaveCount(0);
      await expect(stage.getByRole("tablist", {name: "Annotator tools"})).toBeVisible();
      await expect(stage).toContainText("Each clip is a recording of the real app.");
      await expect(stage.locator("img[src*='/windows/'], [data-shot-panel]")).toHaveCount(0);
      const details = page.locator(".recording-details");
      await details.locator("summary").click();
      await expect(details.locator("summary")).toHaveText("How these videos were made");
      await expect(details).toContainText("The Windows app has the same tools and looks the same.");
    });
  });
  test("the homepage stays short for a Mac and a Windows visitor", async ({browser}) => {
    for (const userAgent of [visitors.mac, visitors.windows]) {
      await asVisitor(browser, userAgent, async page => {
        await page.goto(sitePath("/"));
        expect(await readingWords(page), userAgent).toBeLessThan(150);
      });
    }
  });

  test("pages that list shortcuts show the Windows keys", async ({page}) => {
    await page.goto(sitePath("/"));
    const draw = page.locator(".shortcut-pair .keycaps").first();
    await expect(draw).toHaveAttribute("aria-label", "Control Shift 2");
    await expect(draw.locator("kbd")).toHaveText(["Ctrl", "Shift", "2"]);
    await expect(page.locator(".stage-more kbd")).toHaveText(["⌘L", "Ctrl+L"]);
    await page.getByRole("tab", {name: "Arrow", exact: true}).click();
    await expect(page.locator("[data-native-take=arrow] .player-caption")).toHaveText(
      "Click to place points and press Return (Enter on Windows). Drag a point to bend the arrow.");
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
    await expect(section.getByRole("link", {name: "Get Annotator from the Microsoft Store", exact: true})).toHaveAttribute("href", listingURL);
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

  test("without scripts, both systems' downloads and steps show", async ({browser, baseURL}) => {
    const context = await browser.newContext({javaScriptEnabled: false, userAgent: visitors.windows});
    try {
      const page = await context.newPage();
      await page.goto(baseURL);
      await expect(page.locator("#install .download-action")).toBeVisible();
      await expect(page.locator("#install .store-command")).toBeVisible();
      await expect(page.locator("[data-platform-switch]")).toHaveCount(1);
      for (const toggle of await page.locator("[data-platform-switch]").all()) await expect(toggle).toBeHidden();
      await expect(page.locator('[data-install-guide="mac"]')).toBeVisible();
      await expect(page.locator('[data-install-guide="windows"]')).toBeVisible();
    } finally { await context.close(); }
  });
}
