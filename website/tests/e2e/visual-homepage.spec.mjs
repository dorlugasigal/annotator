import {test, expect} from "@playwright/test";
import {readFileSync} from "node:fs";
import {siteConfig} from "../../site-config.mjs";
import {readingWords} from "./visitors.mjs";
const base = siteConfig().base.replace(/\/$/, "");
const stage=JSON.parse(readFileSync(new URL("../../src/data/stage-demos.json",import.meta.url),"utf8")).demos;
const metadata=JSON.parse(readFileSync(process.env.DISTRIBUTION_METADATA_FILE||new URL("../../src/data/distribution.json",import.meta.url),"utf8"));
const release=metadata.stable||metadata.preview;
const videoFor=(page,name)=>page.locator(`[data-native-take="${name}"] [data-take-video]`);

test("homepage keeps the default reading short and installation direct", async ({page}) => {
  await page.goto(base + "/");
  // Only prose counts, so the budget is the same for every release track (see readingWords). With a Windows listing,
  // windows.spec.mjs counts it for a Mac and a Windows visitor too.
  expect(await readingWords(page)).toBeLessThan(150);
  await expect(page.locator(".site-header nav a")).toHaveCount(3);
  await page.getByRole("link", {name:"Install",exact:true}).click();
  await expect(page.locator('[data-install-guide="mac"] .install-steps li')).toHaveText([
    release?.diskImageURL ? "Download and open the disk image." : "Download and unzip.",
    "Drag Annotator to Applications.", "Open it. Pick a tool. Draw. It runs from the menu bar.",
  ]);
  await expect(page.locator(".install-scene")).toHaveAttribute("aria-hidden", "true");
  await expect(page.getByRole("heading",{name:"Or use Homebrew",exact:true})).toBeVisible();
  await expect(page.locator("#homebrew code")).toContainText("brew install --cask dorlugasigal/annotator/annotator");
});

test("Get Annotator reveals download choices and copies Homebrew only on request", async ({page}) => {
    await page.addInitScript(()=>{
      window.__brewCopy=null;
      Object.defineProperty(navigator,"clipboard",{configurable:true,value:{
        writeText: async value=>{window.__brewCopy=value;},
      }});
    });
    await page.goto(base + "/");
    expect(await page.evaluate(()=>window.__brewCopy)).toBe(null);
    await page.getByRole("link",{name:"Get Annotator",exact:true}).click();
    await expect(page).toHaveURL(/#install$/);
    await expect(page.locator("#install .download-action")).toBeVisible();
    await expect(page.locator("#homebrew [data-copy-command]")).toBeVisible();
    await page.getByRole("button",{name:"Copy Homebrew command",exact:true}).click();
    expect(await page.evaluate(()=>window.__brewCopy)).toBe("brew install --cask dorlugasigal/annotator/annotator");
    await expect(page.locator("#homebrew [data-copy-status]")).toHaveText("Copied. Paste into Terminal and press Return.");
    // One command. The prompt is drawn, so it is not part of the copied text.
    const lines=page.locator("#homebrew [data-copy-command] > span");
    await expect(lines).toHaveCount(1);
    expect(await lines.first().evaluate(element=>getComputedStyle(element,"::before").content)).toBe('"$"');
  });

test("blocked clipboard access explains how to copy manually", async ({page}) => {
    await page.addInitScript(()=>Object.defineProperty(navigator,"clipboard",{configurable:true,value:{
      writeText: async ()=>{throw new DOMException("Denied","NotAllowedError");},
    }}));
    await page.goto(base + "/");
    await page.getByRole("button",{name:"Copy Homebrew command",exact:true}).click();
    await expect(page.locator("#homebrew [data-copy-status]")).toContainText("Select the command and copy it.");
    await expect(page.getByRole("button",{name:"Copy Homebrew command",exact:true})).toBeEnabled();
  });

test("one product stage replaces the video list and works with keyboard tabs", async ({page}) => {
    await page.emulateMedia({reducedMotion:"reduce"});
    await page.goto(base + "/");
    await expect(page.getByRole("tab")).toHaveCount(Object.keys(stage).length);
    await expect(page.locator("[data-product-stage] [data-native-take]:visible")).toHaveCount(1);
    const first=page.getByRole("tab",{name:"Circle",exact:true});
    await first.focus(); await first.press("ArrowRight");
    await expect(page.getByRole("tab",{name:"Arrow",exact:true})).toBeFocused();
    await expect(page.locator("[data-native-take=arrow]")).toBeVisible();
    await expect(page.locator("[data-native-take=circle]")).toBeHidden();
    await page.getByRole("tab",{name:"Arrow",exact:true}).press("End");
    await expect(page.getByRole("tab",{name:"Blur",exact:true})).toBeFocused();
    await expect(page.locator("[data-product-stage] [data-native-take]:visible")).toHaveCount(1);
    await expect(page.locator(".settings-pair .native-feature")).toHaveCount(2);
    for (const card of await page.locator(".settings-pair .native-feature").all()) await expect(card.locator("[data-take-video]")).toHaveCount(1);
    // The stage ends with the other tools, named as they are in the app.
    await expect(page.getByRole("heading",{name:"And much more.",exact:true})).toBeVisible();
    await expect(page.locator(".stage-more")).toContainText("tool library");
    expect(await page.locator(".tool-group li").count()).toBeGreaterThan(20);
  });

test("the active demonstration repeats while hidden examples stay paused", async ({page}) => {
  await page.emulateMedia({reducedMotion:"no-preference"});
  await page.goto(base + "/");
  await page.getByRole("tab",{name:"Arrow",exact:true}).click();
  const frame=page.locator("[data-native-take=arrow] [data-take-video]");
  const video=videoFor(page,"arrow");
  await frame.scrollIntoViewIfNeeded();
  await expect.poll(()=>video.evaluate(element=>element.paused)).toBe(false);
  await expect.poll(()=>video.evaluate(element=>Number.isFinite(element.duration))).toBe(true);
  await video.evaluate(element=>{element.currentTime=element.duration-.05;});
  await expect.poll(()=>video.evaluate(element=>element.currentTime<1)).toBe(true);
  await page.getByRole("tab",{name:"Blur",exact:true}).click();
  await expect.poll(()=>video.evaluate(element=>element.paused)).toBe(true);
  await expect(page.locator("[data-product-stage] [data-native-take]:visible")).toHaveCount(1);
});

test("script-free feature choices remain available as native disclosures", async ({browser,baseURL}) => {
    const context=await browser.newContext({javaScriptEnabled:false});
    try {
      const page=await context.newPage(); await page.goto(baseURL);
      await expect(page.locator("[data-stage-tabs]")).toBeHidden();
      await page.locator('[data-stage-panel="arrow"] summary').click();
      const frame=page.locator('[data-stage-panel="arrow"] [data-take-video]');
      await expect(frame).toBeVisible();
      await expect(frame).toHaveAttribute("controls","");
    } finally {await context.close();}
  });

test("stage clips have one compact control that plays, pauses and replays with the keyboard", async ({page}) => {
  await page.emulateMedia({reducedMotion:"reduce"});
  await page.goto(base + "/");
  await page.getByRole("tab",{name:"Arrow",exact:true}).click();
  const root=page.locator("[data-native-take=arrow]");
  const video=root.locator("[data-take-video]"), button=root.locator("[data-take-toggle]");
  // Scripts replace the browser's controls at once, and the poster stays until playback shows a frame.
  await expect(video).not.toHaveAttribute("controls");
  await expect(video).toHaveAttribute("playsinline","");
  await expect(root.locator("[data-take-poster]")).toBeVisible();
  await expect(button).toHaveAccessibleName(/^Play /);
  await button.focus(); await button.press("Enter");
  await expect.poll(()=>video.evaluate(element=>element.paused)).toBe(false);
  // Stage clips play at their recorded rate; any speed-up is already in the encoding (see stage-demos.json).
  await expect.poll(()=>video.evaluate(element=>element.playbackRate)).toBe(Number(await video.getAttribute("data-playback-rate")));
  await expect(video).not.toHaveAttribute("controls");
  await expect(button).toHaveAccessibleName(/^Pause /);
  await button.press("Enter");
  await expect.poll(()=>video.evaluate(element=>element.paused)).toBe(true);
  await video.evaluate(async element=>{
    if(element.readyState<1) await new Promise(resolve=>element.addEventListener("loadedmetadata",resolve,{once:true}));
    await new Promise(resolve=>{
      element.addEventListener("seeked",resolve,{once:true});
      element.currentTime=Math.max(0,element.duration-.25);
    });
  });
  // Resume with the control, as a person would; a direct play() would race the kept manual pause.
  await button.press("Enter");
  await expect(button).toHaveAccessibleName(/^Replay /);
});

test("the hero reel leads the page and fits phone and desktop", async ({page}) => {
  await page.emulateMedia({reducedMotion:"reduce"});
  for (const width of [320,390,1440]) {
    await page.setViewportSize({width,height:900});
    await page.goto(base + "/");
    const frame=await page.locator("[data-reel-frame]").boundingBox();
    expect(frame.y).toBeLessThan(width<500?650:450);
    expect(frame.x).toBeGreaterThanOrEqual(0);
    expect(frame.x+frame.width).toBeLessThanOrEqual(width);
    expect(await page.locator("body").evaluate(element=>element.scrollWidth<=innerWidth+1)).toBe(true);
    const header=await page.locator(".site-header").boundingBox();
    const nav=await page.locator(".site-header nav").boundingBox();
    expect(nav.y+nav.height).toBeLessThanOrEqual(header.y+header.height+1);
    const hero=await page.locator(".home-hero").boundingBox();
    expect(hero.y).toBeGreaterThanOrEqual(header.y+header.height-1);
    await expect(page.locator(".home-hero + [data-product-stage]")).toHaveCount(1);
  }
});

test("the reel stays at least 65% visible on common laptop screens so it starts at once", async ({page}) => {
  await page.emulateMedia({reducedMotion:"no-preference"});
  for (const [width,height] of [[1280,720],[1366,768],[1440,790],[1536,864]]) {
    await page.setViewportSize({width,height});
    await page.goto(base + "/");
    await page.evaluate(()=>document.fonts.ready);
    await page.locator("[data-reel-frame]").evaluate(element=>Promise.all(element.getAnimations().map(animation=>animation.finished)));
    await expect.poll(()=>page.locator("[data-reel-video]").evaluate(element=>element.paused),`${width}x${height}`).toBe(false);
    const visible=await page.locator("[data-reel-frame]").evaluate(element=>{
      const bounds=element.getBoundingClientRect();
      return (Math.min(bounds.bottom,innerHeight)-Math.max(bounds.top,0))/bounds.height;
    });
    expect(visible,`${width}x${height}`).toBeGreaterThanOrEqual(.65);
    const width16=await page.locator("[data-reel-frame]").evaluate(element=>element.getBoundingClientRect().width);
    expect(width16,`${width}x${height} keeps the reel large`).toBeGreaterThanOrEqual(640);
  }
});
