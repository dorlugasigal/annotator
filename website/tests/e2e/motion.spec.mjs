import {test, expect} from "@playwright/test";
import {siteConfig} from "../../site-config.mjs";
import {visitors} from "./visitors.mjs";

const base = siteConfig().base.replace(/\/$/, "");
const home = base + "/";
// Counts the animation frames the page asks for, so a test can tell when nothing runs.
const countFrames = () => {
  window.__frames = 0;
  const next = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = callback => next(time => { window.__frames++; callback(time); });
};
const framesIn = async (page, ms) => {
  const before = await page.evaluate(() => window.__frames);
  await page.waitForTimeout(ms);
  return await page.evaluate(() => window.__frames) - before;
};
const running = page => page.evaluate(() => document.getAnimations().filter(animation => animation.playState === "running")
  .map(animation => animation.animationName || animation.effect?.target?.className?.baseVal || animation.effect?.target?.className || "script"));
const desk = page => page.locator("#desk");
const marks = page => page.locator("#desk .ink-mark");

test.use({userAgent: visitors.mac});

test("reduced motion shows the finished page at once and nothing keeps running", async ({page}) => {
  await page.addInitScript(countFrames);
  await page.emulateMedia({reducedMotion: "reduce"});
  await page.goto(home);
  await expect(page.locator("html")).not.toHaveClass(/\bmotion\b/);
  // The demo is a still picture: the same marks, drawn at once, and no scripted pointer.
  await expect.poll(() => marks(page).count()).toBeGreaterThan(2);
  await expect(page.locator("#stage-replay")).toHaveText("Replay");
  await expect(desk(page)).toHaveAttribute("data-mode", "use");
  // The cards are a plain grid, each with its scene's last picture; the tabs that drive the pinned deck aren't there.
  await expect(page.locator(".do-tabs")).toBeHidden();
  for (const card of await page.locator(".card").all()) await expect(card).toBeVisible();
  expect(await page.locator(".do-track").evaluate(element => element.offsetHeight)).toBeLessThan(await page.locator(".do-deck").evaluate(element => element.offsetHeight) + 400);
  await page.waitForTimeout(1500);
  expect(await running(page)).toEqual([]);
  expect(await framesIn(page, 1000)).toBe(0);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).scrollBehavior)).toBe("auto");
});

test("off screen, the demo and the cards rest: only the header's live dot keeps breathing", async ({page}) => {
  await page.addInitScript(countFrames);
  await page.setViewportSize({width: 1440, height: 900});
  await page.goto(home);
  await expect(page.locator("#stage-replay")).toHaveText("Pause");
  await page.evaluate(() => scrollTo({top: document.documentElement.scrollHeight, behavior: "instant"}));
  await page.waitForTimeout(2500);
  expect(await framesIn(page, 1000)).toBe(0);
  expect(await running(page)).toEqual(["beat"]);
});

test("the demo plays its story while it is seen, and Pause stops it", async ({page}) => {
  await page.goto(home);
  await expect(page.locator("#stage-replay")).toHaveText("Pause");
  await expect(desk(page)).toHaveAttribute("data-mode", "draw", {timeout: 10_000});
  await expect.poll(() => marks(page).count(), {timeout: 15_000}).toBeGreaterThan(0);
  await page.locator("#stage-replay").click();
  await expect(page.locator("#stage-replay")).toHaveText("Replay");
  await page.locator("#stage-replay").click();
  await expect(page.locator("#stage-replay")).toHaveText("Pause");
});

test("a press on the stage draws with the Pen; Escape and Control-Shift-2 switch between using apps and drawing", async ({page, isMobile}) => {
  test.skip(isMobile, "A finger scrolls past the stage; it joins in with a tap instead.");
  await page.setViewportSize({width: 1440, height: 900});
  await page.goto(home);
  // While the story plays, a press on the stage is the visitor's first stroke. (Before it starts, the apps take clicks.)
  await expect(desk(page)).toHaveClass(/is-scripted/, {timeout: 10_000});
  const box = await desk(page).boundingBox();
  const before = await marks(page).count();
  await page.mouse.move(box.x + box.width * .5, box.y + box.height * .55);
  await page.mouse.down();
  for (let i = 1; i <= 12; i++) await page.mouse.move(box.x + box.width * .5 + i * 12, box.y + box.height * .55 + Math.sin(i) * 20);
  await page.mouse.up();
  await expect(desk(page)).toHaveAttribute("data-mode", "draw");
  await expect(page.locator('#desk button[data-tool="pen"]')).toHaveAttribute("aria-pressed", "true");
  await expect.poll(() => marks(page).count()).toBe(before + 1);
  await expect(page.locator("#mode-chip")).toHaveText("Drawing", {useInnerText: true});
  await page.keyboard.press("Escape");
  await expect(desk(page)).toHaveAttribute("data-mode", "use");
  await expect(page.locator("#mode-chip")).toHaveText("Using apps", {useInnerText: true});
  await expect(page.locator("#status")).toHaveText("Using apps.");
  await page.keyboard.press("Control+Shift+2");
  await expect(desk(page)).toHaveAttribute("data-mode", "draw");
  await page.locator("#stage-done").click();
  await expect(desk(page)).toHaveAttribute("data-mode", "use");
});

test("Try it here brings the stage into view with the Pen ready", async ({page}) => {
  await page.goto(home);
  await page.locator(".hero .get:visible").getByRole("link", {name: "Try it here"}).click();
  await expect(desk(page)).toHaveAttribute("data-mode", "draw");
  await expect(page.locator('#desk button[data-tool="pen"]')).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("#status")).toHaveText("This screen is live. Draw on it.");
  await expect(page.locator(".stage-scene")).toBeInViewport();
});

test("in Use apps the sample apps answer the visitor, and the windows move with the arrow keys", async ({page}) => {
  // With reduced motion the demo is a still picture, so the apps wait for the visitor.
  await page.emulateMedia({reducedMotion: "reduce"});
  await page.goto(home);
  await expect(desk(page)).toHaveAttribute("data-mode", "use");
  const weekly = page.getByRole("switch", {name: "Weekly summary email"});
  const was = await weekly.getAttribute("aria-checked");
  await weekly.click();
  await expect(weekly).toHaveAttribute("aria-checked", was === "true" ? "false" : "true");
  await page.getByRole("button", {name: "Save changes"}).click();
  await expect(page.locator("[data-save]")).toHaveAttribute("data-saved", "true");
  await page.getByRole("textbox", {name: "Reply to Dor"}).fill("Settings, Notifications.");
  await page.getByRole("button", {name: "Send"}).click();
  await expect(page.locator("[data-thread] .me")).toHaveText("Settings, Notifications.");
  const start = await page.locator(".win-mail").boundingBox();
  await page.locator(".win-mail [data-drag]").focus();
  await page.keyboard.press("ArrowLeft");
  await page.keyboard.press("ArrowLeft");
  await expect.poll(async () => (await page.locator(".win-mail").boundingBox()).x).toBeLessThan(start.x - 20);
});

test("the cards' tabs, and Control-1 and Control-3 on a Mac, go to each card", async ({page, isMobile}) => {
  test.skip(isMobile, "Phones show the cards without tabs.");
  await page.setViewportSize({width: 1440, height: 900});
  await page.goto(home);
  await page.locator("#do").scrollIntoViewIfNeeded();
  await page.getByRole("button", {name: "Take a break"}).click();
  await expect(page.locator(".card.is-current h3")).toHaveAttribute("aria-label", "Take a break", {timeout: 6000});
  await expect(page.getByRole("button", {name: "Take a break"})).toHaveAttribute("aria-current", "true");
  await page.keyboard.press("Control+1");
  await expect(page.locator(".card.is-current h3")).toHaveAttribute("aria-label", "Zoom in", {timeout: 6000});
  await page.keyboard.press("Control+3");
  await expect(page.locator(".card.is-current h3")).toHaveAttribute("aria-label", "Take a break", {timeout: 6000});
  // A screen reader hears each title whole, not letter by letter.
  await expect(page.locator(".card.is-current h3")).toHaveAccessibleName("Take a break");
});

test("the header gains its line once the page scrolls", async ({page}) => {
  await page.goto(base + "/docs/");
  await expect(page.locator("html")).not.toHaveClass(/is-scrolled/);
  await page.evaluate(() => scrollTo({top: 400, behavior: "instant"}));
  await expect(page.locator("html")).toHaveClass(/is-scrolled/);
  await page.evaluate(() => scrollTo({top: 0, behavior: "instant"}));
  await expect(page.locator("html")).not.toHaveClass(/is-scrolled/);
});
