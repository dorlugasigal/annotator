import {test, expect} from "@playwright/test";
import {readFileSync} from "node:fs";
import {siteConfig} from "../../site-config.mjs";

const base = siteConfig().base.replace(/\/$/, "");
const reel = JSON.parse(readFileSync(new URL("../../src/data/hero-reel.json", import.meta.url), "utf8"));
const phoneQuery = "(max-width: 600px)";

function parts(page) {
  const root = page.locator("[data-hero-reel]");
  return {root, video: root.locator("[data-reel-video]"), toggle: root.locator("[data-reel-toggle]"),
    poster: root.locator("[data-reel-poster]"), frame: root.locator("[data-reel-frame]")};
}
async function encodingFor(page) {
  const phone = await page.evaluate(query => matchMedia(query).matches, phoneQuery);
  return phone && reel.phone ? reel.phone : reel.desktop;
}
const paused = video => video.evaluate(element => element.paused);
const show = locator => locator.evaluate(element => element.scrollIntoView({block: "center", behavior: "instant"}));
const leave = page => page.locator("#install").evaluate(element => element.scrollIntoView({block: "start", behavior: "instant"}));
async function setHidden(page, hidden) {
  await page.evaluate(value => {
    Object.defineProperty(document, "hidden", {configurable: true, get: () => value});
    document.dispatchEvent(new Event("visibilitychange"));
  }, hidden);
}

test("the hero reel autoplays muted in a loop with the encoding made for this screen", async ({page}) => {
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {root, video, toggle, frame} = parts(page);
  await show(frame);
  await expect(root).toHaveAttribute("data-ready", "true");
  await expect.poll(() => paused(video)).toBe(false);
  await expect.poll(() => video.evaluate(element => element.getVideoPlaybackQuality().totalVideoFrames)).toBeGreaterThan(1);
  const entry = await encodingFor(page);
  const source = new URL(await video.evaluate(element => element.currentSrc));
  expect(source.pathname).toBe(`${base}${entry.file}`);
  expect(source.searchParams.get("v")).toBe(entry.sha256.slice(0, 12));
  expect(await video.evaluate(element => [element.muted, element.loop, element.controls])).toEqual([true, true, false]);
  expect(await video.evaluate(element => [element.videoWidth, element.videoHeight])).toEqual([entry.width, entry.height]);
  await expect.poll(() => video.evaluate(element => element.duration)).toBeCloseTo(entry.duration, 0);
  await expect(video).toHaveAttribute("crossorigin", "anonymous");
  await expect(video).toHaveAttribute("aria-label", reel.description);
  await expect(video.locator("track")).toHaveCount(0);
  await expect(video).toHaveAttribute("aria-describedby", "reel-provenance");
  await expect(page.locator("#reel-provenance")).toHaveText(reel.provenance);
  await expect(parts(page).poster).toBeHidden();
  const box = await frame.boundingBox();
  expect(Math.abs(box.width / box.height - entry.width / entry.height)).toBeLessThan(.02);
  // Moving content longer than five seconds needs a pause control that is always shown.
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveCSS("opacity", "1");
  await expect(toggle).toHaveAccessibleName("Pause the reel");
  await expect(toggle).toContainText("Pause");
  const control = await toggle.boundingBox();
  expect(control.width).toBeGreaterThanOrEqual(44);
  expect(control.height).toBeGreaterThanOrEqual(44);
  // How the reel was made is in the page's one recording disclosure, next to the tool clips.
  await page.locator(".recording-details > summary").click();
  await expect(page.locator("#reel-provenance")).toBeVisible();
  if (reel.music) await expect(page.locator(".recording-details")).toContainText(reel.music.artist);
});

test("a keyboard pause holds across scrolling, and automatic pauses resume", async ({page}) => {
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {video, toggle, frame} = parts(page);
  await show(frame);
  await expect.poll(() => paused(video)).toBe(false);
  await toggle.focus();
  await toggle.press("Enter");
  await expect.poll(() => paused(video)).toBe(true);
  await expect(toggle).toHaveAccessibleName("Play the reel");
  await leave(page);
  await show(frame);
  await page.waitForTimeout(600);
  expect(await paused(video)).toBe(true);
  await toggle.press("Enter");
  await expect.poll(() => paused(video)).toBe(false);
  await leave(page);
  await expect.poll(() => paused(video)).toBe(true);
  await show(frame);
  await expect.poll(() => paused(video)).toBe(false);
  await setHidden(page, true);
  await expect.poll(() => paused(video)).toBe(true);
  await setHidden(page, false);
  await expect.poll(() => paused(video)).toBe(false);
});

test("reduced motion keeps the reel still until Play and never loops it", async ({page}) => {
  await page.emulateMedia({reducedMotion: "reduce"});
  await page.goto(base + "/");
  const {video, toggle, poster, frame} = parts(page);
  await show(frame);
  await page.waitForTimeout(600);
  expect(await paused(video)).toBe(true);
  expect(await video.evaluate(element => element.loop)).toBe(false);
  // The poster stays until a frame is decoded; the reel's first frame is the same end card.
  expect(await page.evaluate(() => !document.querySelector("[data-reel-poster]").hidden ||
    document.querySelector("[data-reel-video]").readyState >= HTMLMediaElement.HAVE_CURRENT_DATA)).toBe(true);
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAccessibleName("Play the reel");
  await expect(page.locator(".reel-progress")).toBeHidden();
  await toggle.click();
  await expect.poll(() => paused(video)).toBe(false);
  expect(await video.evaluate(element => element.loop)).toBe(false);
  await video.evaluate(async element => {
    if (element.readyState < 1) await new Promise(resolve => element.addEventListener("loadedmetadata", resolve, {once: true}));
    await new Promise(resolve => { element.addEventListener("seeked", resolve, {once: true}); element.currentTime = element.duration - .3; });
  });
  await expect.poll(() => video.evaluate(element => element.ended)).toBe(true);
  await expect(toggle).toHaveAccessibleName("Replay the reel");
});

test("blocked autoplay offers Play and does not retry on every scroll", async ({page}) => {
  await page.addInitScript(() => {
    window.__reelAttempts = 0;
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function () {
      if (this.matches("[data-reel-video]") && !window.__allowReel) {
        window.__reelAttempts++;
        return Promise.reject(new DOMException("Autoplay is blocked.", "NotAllowedError"));
      }
      return play.call(this);
    };
  });

  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {video, toggle, frame} = parts(page);
  await show(frame);
  await expect(page.locator("[data-reel-status]")).toHaveText("Tap Play to watch.");
  await expect(toggle).toHaveAccessibleName("Play the reel");
  const attempts = await page.evaluate(() => window.__reelAttempts);
  expect(attempts).toBeGreaterThan(0);
  await leave(page);
  await show(frame);
  await page.waitForTimeout(600);
  expect(await page.evaluate(() => window.__reelAttempts)).toBe(attempts);
  await page.evaluate(() => { window.__allowReel = true; });
  await toggle.click();
  await expect.poll(() => paused(video)).toBe(false);
  await expect(page.locator("[data-reel-status]")).toBeHidden();
});

test("automatic playback waits for video data without losing the user's pause choice", async ({page}) => {
  await page.addInitScript(() => {
    window.__earlyReelAttempts = 0;
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function () {
      if (this.matches("[data-reel-video]") && this.readyState < HTMLMediaElement.HAVE_CURRENT_DATA) {
        window.__earlyReelAttempts++;
        return Promise.reject(new DOMException("Video data is not ready.", "NotAllowedError"));
      }
      return play.call(this);
    };
  });
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {video, toggle, frame} = parts(page);
  await show(frame);
  await toggle.click({trial: true});
  await expect(toggle).toHaveAccessibleName("Pause the reel");
  await expect.poll(() => paused(video)).toBe(false);
  await expect.poll(() => video.evaluate(element => element.getVideoPlaybackQuality().totalVideoFrames)).toBeGreaterThan(1);
  expect(await page.evaluate(() => window.__earlyReelAttempts)).toBe(0);
  await toggle.click();
  await expect.poll(() => paused(video)).toBe(true);
  await video.evaluate(element => element.dispatchEvent(new Event("loadeddata")));
  expect(await paused(video)).toBe(true);
  await expect(toggle).toHaveAccessibleName("Play the reel");
});

test("a failed reel keeps its poster, an error and a direct link", async ({page}) => {
  await page.route(/\/demo\/annotator-reel[^/]*\.mp4/, route => route.abort());
  await page.goto(base + "/");
  const {root, poster, frame} = parts(page);
  await show(frame);
  await expect(page.locator("[data-reel-error]")).toBeVisible();
  await expect(root).toHaveAttribute("data-playback", "error");
  await expect(poster).toBeVisible();
  const entry = await encodingFor(page);
  const link = new URL(await page.locator("[data-reel-error] a").evaluate(element => element.href));
  expect(link.pathname).toBe(`${base}${entry.file}`);
  expect(link.searchParams.get("v")).toBe(entry.sha256.slice(0, 12));
});

test("only the poster for this screen is requested", async ({page}) => {
  const posters = new Set();
  page.on("request", request => { if (/\/demo\/annotator-reel[^/]*\.jpg/.test(request.url())) posters.add(new URL(request.url()).pathname + new URL(request.url()).search); });
  await page.goto(base + "/");
  await expect(parts(page).root).toHaveAttribute("data-ready", "true");
  const entry = await encodingFor(page);
  expect([...posters]).toEqual([`${base}${entry.poster}?v=${entry.sha256.slice(0, 12)}`]);
});

test("without JavaScript the reel keeps its poster, native controls and a direct link", async ({browser, baseURL}) => {
  const context = await browser.newContext({javaScriptEnabled: false});
  try {
    const page = await context.newPage();
    await page.goto(baseURL);
    const {video, toggle, poster} = parts(page);
    await expect(video).toHaveAttribute("controls", "");
    await expect(video).not.toHaveAttribute("autoplay");
    await expect(toggle).toBeHidden();
    await expect(page.locator("[data-reel-sound]")).toBeHidden();
    await expect(video).toHaveJSProperty("muted", true);
    await expect(poster.locator("img")).toBeVisible();
    await page.locator(".recording-details > summary").click();
    await expect(page.locator("#reel-provenance")).toBeVisible();
    await expect(video.locator("a")).toHaveAttribute("href", `${base}${reel.desktop.file}?v=${reel.desktop.sha256.slice(0, 12)}`);
  } finally { await context.close(); }
});

test("a manual pause survives page restoration", async ({page}) => {
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {video, toggle, frame} = parts(page);
  await show(frame);
  await expect.poll(() => paused(video)).toBe(false);
  await toggle.click();
  await expect.poll(() => paused(video)).toBe(true);
  await page.evaluate(() => {
    dispatchEvent(new PageTransitionEvent("pagehide", {persisted: true}));
    dispatchEvent(new PageTransitionEvent("pageshow", {persisted: true}));
  });
  await page.waitForTimeout(600);
  expect(await paused(video)).toBe(true);
});

test("crossing the phone breakpoint loads the encoding made for the new width", async ({page}) => {
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.setViewportSize({width: 1280, height: 800});
  await page.goto(base + "/");
  const {root, video, frame} = parts(page);
  await expect(root).toHaveAttribute("data-ready", "true");
  const current = async () => new URL(await video.evaluate(element => element.currentSrc)).pathname;
  await expect.poll(current).toBe(`${base}${reel.desktop.file}`);
  await page.setViewportSize({width: 390, height: 844});
  const entry = reel.phone ?? reel.desktop;
  await expect.poll(current).toBe(`${base}${entry.file}`);
  await show(frame);
  await expect.poll(() => paused(video)).toBe(false);
  await expect.poll(() => video.evaluate(element => [element.videoWidth, element.videoHeight])).toEqual([entry.width, entry.height]);
  const box = await frame.boundingBox();
  expect(Math.abs(box.width / box.height - entry.width / entry.height)).toBeLessThan(.02);
});

test("the reel starts muted, and the Sound button turns its music on and off", async ({page}) => {
  test.skip(!reel.audio, "This reel has no sound.");
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {video, frame} = parts(page);
  const sound = page.locator("[data-reel-sound]");
  await show(frame);
  await expect.poll(() => paused(video)).toBe(false);
  await expect(sound).toBeVisible();
  await expect(sound).toHaveAccessibleName("Sound");
  await expect(sound).toHaveAttribute("aria-pressed", "false");
  expect(await video.evaluate(element => element.muted)).toBe(true);
  const box = await sound.boundingBox();
  expect(box.width).toBeGreaterThanOrEqual(44);
  expect(box.height).toBeGreaterThanOrEqual(44);
  await sound.click();
  await expect(sound).toHaveAttribute("aria-pressed", "true");
  expect(await video.evaluate(element => element.muted)).toBe(false);
  expect(await paused(video)).toBe(false);
  await sound.focus();
  await sound.press("Enter");
  await expect(sound).toHaveAttribute("aria-pressed", "false");
  expect(await video.evaluate(element => element.muted)).toBe(true);
  // Sound never keeps playing on its own: pausing the reel silences it.
  await sound.click();
  await parts(page).toggle.click();
  await expect.poll(() => paused(video)).toBe(true);
});

test("a browser that refuses to play with sound keeps the reel playing muted", async ({page}) => {
  test.skip(!reel.audio, "This reel has no sound.");
  await page.addInitScript(() => {
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function () {
      if (this.matches("[data-reel-video]") && !this.muted)
        return Promise.reject(new DOMException("Sound needs a click.", "NotAllowedError"));
      return play.call(this);
    };
  });
  await page.emulateMedia({reducedMotion: "no-preference"});
  await page.goto(base + "/");
  const {video, toggle, frame} = parts(page);
  const sound = page.locator("[data-reel-sound]");
  await show(frame);
  await expect.poll(() => paused(video)).toBe(false);
  // paused turns false as soon as play() is called. Unmuting before frames advance lets WebKit refuse
  // that pending play, so wait for real playback before turning the sound on.
  await expect.poll(() => video.evaluate(element => element.currentTime)).toBeGreaterThan(0);
  await sound.click();
  await expect(sound).toHaveAttribute("aria-pressed", "true");
  await toggle.click();
  await expect.poll(() => paused(video)).toBe(true);
  await toggle.click();
  await expect.poll(() => paused(video)).toBe(false);
  await expect(sound).toHaveAttribute("aria-pressed", "false");
  expect(await video.evaluate(element => element.muted)).toBe(true);
  await expect(page.locator("[data-reel-status]")).toBeHidden();
});
