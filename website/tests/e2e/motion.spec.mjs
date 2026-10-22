import {test, expect} from "@playwright/test";
import {siteConfig} from "../../site-config.mjs";
import {storeId, visitors} from "./visitors.mjs";

const base = siteConfig().base.replace(/\/$/, "");
// With a Windows listing, a Mac visitor sees the Mac install picture these tests check; windows.spec.mjs checks the
// Windows install steps.
if (storeId) test.use({userAgent: visitors.mac});
// The page performs no entrances (motion.css): these always rest in their finished state.
const content = ".hero-title, .hero-lede, .hero-cta, .reel-frame, .stage-intro, .stage-tabs, .stage-more, .settings-pair > *, .shortcut-strip, .install-copy h2, .install-steps";
// The reel's chapter bars follow playback: they pause with the reel instead of running to an end.
const running = () => document.getAnimations().filter(animation => animation.timeline === document.timeline &&
  animation.id !== "reel-chapter" && animation.playState === "running").length;
const finishedStates = elements => elements.map(element => {
  const style = getComputedStyle(element);
  return {name: element.className.baseVal ?? element.className, opacity: style.opacity, translate: style.translate,
    transform: style.transform, clipPath: style.clipPath};
});
const expectFinished = states => {
  for (const state of states) expect(state, state.name).toMatchObject({opacity: "1", translate: "none", transform: "none", clipPath: "none"});
};
const inkOffset = locator => locator.evaluate(element => parseFloat(getComputedStyle(element).strokeDashoffset));

test("reduced motion shows the finished page at once", async ({page}) => {
  await page.emulateMedia({reducedMotion: "reduce"});
  await page.goto(base + "/");
  await expect(page.locator("[data-hero-reel]")).toHaveAttribute("data-ready", "true");
  expect(await page.evaluate(() => document.getAnimations().length)).toBe(0);
  expectFinished(await page.locator(content).evaluateAll(finishedStates));
  await page.locator(".install-copy").evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => document.getAnimations().length)).toBe(0);
  expect(await inkOffset(page.locator(".install-ellipse path"))).toBe(0);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).scrollBehavior)).toBe("auto");
});

test("without JavaScript every section is visible and the mark is already drawn", async ({browser, baseURL}) => {
  const context = await browser.newContext({javaScriptEnabled: false, reducedMotion: "no-preference"});
  try {
    const page = await context.newPage();
    await page.goto(baseURL);
    await expect(page.locator("html")).not.toHaveClass(/js-motion/);
    expectFinished(await page.locator(content).evaluateAll(finishedStates));
    for (const element of await page.locator("[data-reveal]").all()) await expect(element).toHaveCSS("opacity", "1");
    expect(await inkOffset(page.locator(".install-ellipse path"))).toBe(0);
    expect(await page.locator(".site-header").evaluate(element => getComputedStyle(element, "::before").opacity)).toBe("1");
  } finally { await context.close(); }
});

test("the page performs no entrances; its marks are drawn once and nothing keeps running", async ({page}) => {
  await page.setViewportSize({width: 1440, height: 900});
  await page.addInitScript(() => {
    window.__frames = 0;
    const original = requestAnimationFrame.bind(window);
    window.requestAnimationFrame = callback => { window.__frames++; return original(callback); };
  });
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const install = page.locator(".install-copy");
  const ellipse = page.locator(".install-ellipse path");
  await expect(page.locator("html")).toHaveClass(/js-motion/);
  // Content below the fold is already in its finished state; only the ink waits to be drawn.
  expectFinished(await page.locator(content).evaluateAll(finishedStates));
  await expect(install).not.toHaveClass(/is-revealed/);
  expect(await inkOffset(ellipse)).toBeGreaterThan(1);
  await install.evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
  await expect(install).toHaveClass(/is-revealed/);
  await expect.poll(() => inkOffset(ellipse)).toBe(0);
  await expect.poll(() => page.evaluate(running)).toBe(0);
  await page.evaluate(() => scrollTo({top: 0, behavior: "instant"}));
  await page.waitForTimeout(300);
  await expect(install).toHaveClass(/is-revealed/);
  expect(await inkOffset(ellipse)).toBe(0);
  await page.locator("#install").evaluate(element => element.scrollIntoView({behavior: "instant"}));
  await page.waitForTimeout(700);
  const before = await page.evaluate(() => window.__frames);
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.__frames)).toBe(before);
});

// The install picture: its ink paths (arrow, arrowhead, circle) and the parts only its demonstration shows.
const sceneInk = page => page.locator(".install-scene .scene-ink").evaluateAll(paths =>
  paths.map(path => parseFloat(getComputedStyle(path).strokeDashoffset)));
const sceneExtras = page => page.locator(".install-scene :is(.scene-pointer, .scene-select, .scene-drop, .scene-ripple)")
  .evaluateAll(parts => parts.map(part => getComputedStyle(part).opacity));
const sceneAnimations = (page, state) => page.evaluate(wanted => document.getAnimations()
  .filter(animation => animation.effect?.target?.closest?.(".install-scene") && (!wanted || animation.playState === wanted)).length, state);

test("the install picture shows its steps once as it scrolls in, then rests as the finished drawing", async ({page}) => {
  await page.setViewportSize({width: 1440, height: 900});
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const scene = page.locator(".install-scene");
  await expect(page.locator("html")).toHaveClass(/js-motion/);
  expect(await sceneInk(page)).toEqual([1.05, 1.05, 1.05]);
  await scene.evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
  await expect(scene).toHaveClass(/is-arriving/);
  await expect.poll(() => page.locator(".scene-pointer").evaluate(element => Number(getComputedStyle(element).opacity))).toBeGreaterThan(.5);
  await expect.poll(() => sceneAnimations(page, "running"), {timeout: 8000}).toBe(0);
  expect(await sceneAnimations(page)).toBeGreaterThan(0);
  expect(await sceneInk(page)).toEqual([0, 0, 0]);
  expect(await sceneExtras(page)).toEqual(["0", "0", "0", "0"]);
  // Coming back does not play it again.
  await page.evaluate(() => scrollTo({top: 0, behavior: "instant"}));
  await scene.evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
  await page.waitForTimeout(300);
  expect(await sceneAnimations(page, "running")).toBe(0);
  expect(await sceneInk(page)).toEqual([0, 0, 0]);
});

test("the install picture is already finished on screen at load, with reduced motion and without JavaScript", async ({page, browser, baseURL}) => {
  // A viewport taller than the page puts every section on screen at load, so nothing is played.
  await page.setViewportSize({width: 1440, height: 16000});
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  await expect(page.locator(".install-scene")).toHaveClass(/is-revealed/);
  await expect(page.locator(".install-scene")).not.toHaveClass(/is-arriving/);
  expect(await sceneAnimations(page)).toBe(0);
  expect(await sceneInk(page)).toEqual([0, 0, 0]);

  await page.setViewportSize({width: 1440, height: 900});
  await page.emulateMedia({reducedMotion: "reduce"});
  await page.goto(base + "/");
  await page.locator(".install-scene").evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
  await expect(page.locator(".install-scene")).toHaveClass(/is-arriving/);
  await page.waitForTimeout(300);
  expect(await sceneAnimations(page)).toBe(0);
  expect(await sceneInk(page)).toEqual([0, 0, 0]);
  expect(await sceneExtras(page)).toEqual(["0", "0", "0", "0"]);

  const context = await browser.newContext({javaScriptEnabled: false, reducedMotion: "no-preference"});
  try {
    const plain = await context.newPage();
    await plain.goto(baseURL);
    expect(await sceneInk(plain)).toEqual([0, 0, 0]);
    expect(await sceneExtras(plain)).toEqual(["0", "0", "0", "0"]);
  } finally { await context.close(); }
});

test("nothing moves with scrolling", async ({page}) => {
  await page.setViewportSize({width: 1440, height: 900});
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const moved = () => page.locator(content).evaluateAll(elements =>
    elements.map(element => getComputedStyle(element).transform + getComputedStyle(element).translate));
  const rest = await moved();
  for (const target of ["#workspace", ".stage-more", ".settings-pair", "#install"]) {
    for (const block of ["start", "center", "end"]) {
      await page.locator(target).evaluate((element, position) => element.scrollIntoView({block: position, behavior: "instant"}), block);
      await page.waitForTimeout(100);
      expect(await moved(), `${target} ${block}`).toEqual(rest);
    }
  }
  expect(await page.evaluate(() => document.getAnimations().filter(animation => animation.timeline !== document.timeline).length)).toBe(0);
});

test("the header gains its surface once the page scrolls", async ({page}) => {
  await page.goto(base + "/");
  const surface = () => page.locator(".site-header").evaluate(element => getComputedStyle(element, "::before").opacity);
  await expect.poll(surface).toBe("0");
  await page.evaluate(() => scrollTo({top: 400, behavior: "instant"}));
  await expect(page.locator("html")).toHaveAttribute("data-scrolled", "");
  await expect.poll(surface).toBe("1");
  await page.evaluate(() => scrollTo({top: 0, behavior: "instant"}));
  await expect(page.locator("html")).not.toHaveAttribute("data-scrolled");
  await expect.poll(surface).toBe("0");
});

test("the stage indicator follows the chosen example and arrow keys match the layout", async ({page}) => {
  await page.setViewportSize({width: 1440, height: 900});
  await page.goto(base + "/");
  const tabs = page.getByRole("tablist", {name: "Annotator tools"});
  const smart = page.getByRole("tab", {name: "Smart Draw", exact: true});
  const shapes = page.getByRole("tab", {name: "Shapes", exact: true});
  const indicatorOn = async (tab, axis) => {
    const [pill, target] = await Promise.all([page.locator(".stage-indicator").boundingBox(), tab.boundingBox()]);
    return axis === "y" ? Math.abs(pill.y - target.y) < 2 && Math.abs(pill.height - target.height) < 2
      : Math.abs(pill.x - target.x) < 2 && Math.abs(pill.width - target.width) < 2;
  };
  await expect(tabs).toHaveAttribute("aria-orientation", "vertical");
  await smart.click();
  expect(await tabs.evaluate(element => element.style.getPropertyValue("--active-index"))).toBe("3");
  await expect.poll(() => indicatorOn(smart, "y")).toBe(true);
  await smart.press("ArrowDown");
  await expect(shapes).toBeFocused();
  await expect(shapes).toHaveAttribute("aria-selected", "true");
  await shapes.press("ArrowUp");
  await expect(smart).toBeFocused();
  await expect(page.locator("[data-product-stage] [data-native-take]:visible")).toHaveCount(1);
  // On narrower screens the tabs form a grid above the stage: the pill lands on the tab in both directions.
  await page.setViewportSize({width: 390, height: 844});
  await expect(tabs).toHaveAttribute("aria-orientation", "horizontal");
  await expect.poll(async () => await indicatorOn(smart, "x") && await indicatorOn(smart, "y")).toBe(true);
});

test("Features opens the product stage directly below the hero", async ({page}) => {
  await page.goto(base + "/");
  await expect(page.locator(".home-hero + section#workspace[data-product-stage]")).toHaveCount(1);
  await page.getByRole("link", {name: "Features", exact: true}).click();
  await expect(page).toHaveURL(/#workspace$/);
  await expect(page.getByRole("heading", {name: "See how each tool works.", exact: true})).toBeInViewport();
  await expect(page.getByRole("tablist", {name: "Annotator tools"})).toBeInViewport();
});

test("the homepage loads, scrolls and switches examples without console errors", async ({page}) => {
  const problems = [];
  // Headless WebKit cannot load the artwork for its own native media controls. That is browser UI, not page code.
  const nativeControlArtwork = /^Button failed to load, iconName = [a-z-]+-placard, /;
  page.on("console", message => {
    if (message.type() === "error" && !nativeControlArtwork.test(message.text())) problems.push(message.text());
  });
  page.on("pageerror", error => problems.push(error.message));
  await page.goto(base + "/");
  await expect(page.locator("[data-hero-reel]")).toHaveAttribute("data-ready", "true");
  for (const section of ["#workspace", ".stage-more", ".settings-story", ".shortcut-strip", "#install", ".footer"]) {
    await page.locator(section).evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
    await page.waitForTimeout(150);
  }
  for (const name of ["Arrow", "Highlight", "Smart Draw", "Shapes", "Spotlight", "Zoom", "Blur", "Circle"])
    await page.getByRole("tab", {name, exact: true}).click();
  await page.locator("[data-reel-frame]").evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
  await page.locator("[data-reel-toggle]").click();
  await page.locator("[data-reel-toggle]").click();
  if (await page.locator("[data-reel-sound]").count()) {
    await page.locator("[data-reel-sound]").click();
    await page.locator("[data-reel-sound]").click();
  }
  await page.waitForTimeout(300);
  expect(problems).toEqual([]);
});

test("same-page links glide once, while scripted scrolling stays instant", async ({page}) => {
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const watch = () => page.evaluate(() => {
    window.__smooth = [];
    new MutationObserver(() => window.__smooth.push(document.documentElement.hasAttribute("data-smooth-scroll")))
      .observe(document.documentElement, {attributes: true, attributeFilter: ["data-smooth-scroll"]});
  });
  const behavior = () => page.evaluate(() => getComputedStyle(document.documentElement).scrollBehavior);
  await watch();
  expect(await behavior()).toBe("auto");
  await page.getByRole("link", {name: "Install", exact: true}).click();
  await expect(page).toHaveURL(/#install$/);
  await expect.poll(() => page.evaluate(() => window.__smooth)).toEqual([true, false]);
  await expect(page.locator("#install")).toBeInViewport();
  expect(await behavior()).toBe("auto");
  await page.emulateMedia({reducedMotion: "reduce"});
  await page.evaluate(() => scrollTo({top: 0, behavior: "instant"}));
  await watch();
  await page.getByRole("link", {name: "Features", exact: true}).click();
  await expect(page).toHaveURL(/#workspace$/);
  await expect(page.getByRole("heading", {name: "See how each tool works.", exact: true})).toBeInViewport();
  expect(await page.evaluate(() => window.__smooth)).toEqual([]);
});
