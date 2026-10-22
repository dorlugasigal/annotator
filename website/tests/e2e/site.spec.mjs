import {test, expect} from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import {readFileSync} from "node:fs";
import {siteConfig} from "../../site-config.mjs";

const base = siteConfig().base.replace(/\/$/, "");
const sitePath = path => base + path;
const metadata = JSON.parse(readFileSync(process.env.DISTRIBUTION_METADATA_FILE ||
  new URL("../../src/data/distribution.json", import.meta.url), "utf8"));
const release = metadata.stable || metadata.preview;
// The main button offers the disk image when the release has one, otherwise the ZIP.
const download = release?.diskImageURL || release?.downloadURL || "https://github.com/dorlugasigal/annotator/releases";
const demo = JSON.parse(readFileSync(new URL("../../src/data/native-demo.json", import.meta.url), "utf8"));
const stage = JSON.parse(readFileSync(new URL("../../src/data/stage-demos.json", import.meta.url), "utf8")).demos;
const settingsTakes = ["pointer", "shortcuts"];
const regionTake = JSON.parse(readFileSync(new URL("../../src/data/region-sharing.json", import.meta.url), "utf8")).take;

async function home(page) {
  await page.goto(sitePath("/"));
  await expect(page.locator("[data-native-take=circle]")).toHaveAttribute("data-ready","true");
}
const videoFor=(page,name)=>page.locator(`[data-native-take="${name}"] [data-take-video]`);
async function openFilm(page) {
  await page.locator(".long-demo > summary").click();
  await page.locator("#native-demo-video").scrollIntoViewIfNeeded();
}

test("local media and AI skill resolve without missing assets", async ({page,request}) => {
  const missing=[];
  page.on("response",response=>{if(response.status()>=400)missing.push(response.url());});
  await home(page);
  await page.evaluate(()=>document.fonts.ready);
  for(const tab of await page.getByRole("tab").all()) {
    await tab.click();
    // The outgoing clip stays visible during the wipe, so scroll to the chosen panel's clip.
    await page.locator(`#${await tab.getAttribute("aria-controls")} [data-take-video]`).scrollIntoViewIfNeeded();
  }
  // With a Windows listing, every Windows picture loads too.
  const windowsView=page.locator("#workspace").getByRole("button",{name:"Windows",exact:true});
  if(await windowsView.count()) {
    await windowsView.click();
    for(const tab of await page.getByRole("tablist",{name:"Annotator on Windows"}).getByRole("tab").all()) {
      await tab.click();
      const picture=page.locator(`#${await tab.getAttribute("aria-controls")} img`);
      await picture.scrollIntoViewIfNeeded();
      await expect.poll(()=>picture.evaluate(image=>image.complete&&image.naturalWidth>0)).toBe(true);
    }
  }
  // The install picture or, for a Windows visitor with a listing, the Windows install steps.
  await page.locator(".install-guide").scrollIntoViewIfNeeded();
  for (const frame of await page.locator(".settings-pair [data-take-video]").all()) await frame.scrollIntoViewIfNeeded();
  expect(missing).toEqual([]);
  const skill=await request.get(sitePath("/downloads/annotator-skill.zip"));
  expect(skill.status()).toBe(200);
  expect((await skill.body()).subarray(0,2).toString()).toBe("PK");
});

test("homepage is a product explanation, not a browser annotation editor",async({page})=>{
  await home(page);
  await expect(page.locator("canvas,[contenteditable=true],[data-drawing-surface],[data-playground],input")).toHaveCount(0);
  await expect(page.locator("[data-native-take] [data-take-video]")).toHaveCount(Object.keys(stage).length + settingsTakes.length + 1);
  await expect(page.locator("[data-product-stage] [data-native-take]:visible")).toHaveCount(1);
  const phone=await page.evaluate(()=>matchMedia("(max-width: 600px)").matches);
  for(const [take,entry] of Object.entries(stage)) {
    const url=new URL(await videoFor(page,take).evaluate(value=>value.currentSrc));
    const file=phone?entry.phone:entry.desktop;
    expect(url.pathname).toBe(sitePath(file.file));
    expect(url.searchParams.get("v")).toBe(file.sha256.slice(0,12));
  }
  for(const take of settingsTakes) {
    const url=new URL(await videoFor(page,take).evaluate(value=>value.currentSrc));
    expect(url.pathname).toBe(sitePath(`/demo/showcase-${take}.mp4`));
    expect(url.searchParams.get("v")).toMatch(/^[a-f0-9]{12}$/);
  }
  const regionURL = new URL(await videoFor(page, "region").evaluate(value => value.currentSrc));
  expect(regionURL.pathname).toBe(sitePath(regionTake.file));
  expect(regionURL.searchParams.get("v")).toBe(regionTake.sha256.slice(0, 12));
  await expect(page.locator(".long-demo")).not.toHaveAttribute("open");
  await expect(page.locator("#native-demo-video")).toHaveAttribute("controls","");
  await expect(page.locator("#native-demo-video")).toHaveAttribute("crossorigin","anonymous");
});

test("region-sharing demo uses the real chooser, stays silent and respects explicit playback", async ({page}) => {
  await page.emulateMedia({reducedMotion: "reduce"});
  await home(page);
  const section = page.locator("#region-sharing");
  const video = videoFor(page, "region");
  const toggle = section.locator("[data-take-toggle]");
  await section.scrollIntoViewIfNeeded();
  expect(await video.evaluate(element => element.paused)).toBe(true);
  expect(await video.evaluate(element => element.muted)).toBe(true);
  await toggle.click();
  await expect.poll(() => video.evaluate(element => element.paused)).toBe(false);
  await expect.poll(() => video.evaluate(element => element.getVideoPlaybackQuality().totalVideoFrames)).toBeGreaterThan(1);
  await toggle.click();
  await expect.poll(() => video.evaluate(element => element.paused)).toBe(true);
  await expect(toggle).toHaveAccessibleName(/Play Choose Right half/);
  await expect(video.locator("track")).toHaveCount(0);
  await section.locator("summary").click();
  await expect(section).toContainText("Hiding the toolbar keeps the region on");
  await expect(section).toContainText("including notifications");
  const videoURL = new URL(await video.evaluate(element => element.currentSrc));
  expect(videoURL.pathname).toBe(sitePath(regionTake.file));
  expect(videoURL.searchParams.get("v")).toBe(regionTake.sha256.slice(0, 12));
});

test("all routes retain requirements, accessible headings and bounded mobile layout",async({page})=>{
  for(const width of [320,390,768,1440,2560]){
    await page.setViewportSize({width,height:900});
    for(const path of ["/","/docs/","/privacy/","/support/","/ai/","/downloads/"]){
      const response=await page.goto(sitePath(path));
      expect([200,304]).toContain(response.status());
      await expect(page.locator("h1")).toHaveCount(1);
      expect(await page.locator("body").evaluate(e=>e.scrollWidth<=innerWidth+1)).toBe(true);
    }
  }
});

// Audit finished states: reveal every scroll entrance, then wait for time-based motion to end.
async function settle(page) {
  await page.evaluate(async()=>{
    for(let top=0;top<=document.documentElement.scrollHeight;top+=innerHeight/2){
      scrollTo({top,behavior:"instant"});
      await new Promise(resolve=>setTimeout(resolve,80));
    }
    scrollTo({top:0,behavior:"instant"});
    await new Promise(resolve=>setTimeout(resolve,80));
    // The reel's chapter bars follow playback and pause with it, so they are not waited for.
    await Promise.all(document.getAnimations()
      .filter(animation=>animation.timeline===document.timeline&&animation.id!=="reel-chapter"&&animation.effect?.getTiming().iterations!==Infinity)
      .map(animation=>animation.finished.catch(()=>undefined)));
  });
}

test("website and simplified playback controls pass accessibility checks",async({page})=>{
  for(const path of ["/","/docs/","/privacy/","/support/","/ai/","/downloads/"]){
    await page.goto(sitePath(path));
    await settle(page);
    // Every entrance on screen has finished. With a Windows listing, one system's install steps stay hidden until chosen.
    if(path==="/") await expect(page.locator("[data-reveal]:not(.is-revealed):visible")).toHaveCount(0);
    const result=await new AxeBuilder({page}).withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze();
    expect(result.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target)}))).toEqual([]);
  }
});

test("download buttons target the verified disk image, ZIP or public release list",async({page})=>{
  await home(page);
  const buttons=page.locator(".download-action");
  await expect(buttons).toHaveCount(1);
  for(const button of await buttons.all()) await expect(button).toHaveAttribute("href",download);
  await expect(page.locator(".hero-availability")).toContainText("macOS 14+");
  expect(await page.locator(".hero-availability").evaluate(e=>parseFloat(getComputedStyle(e).fontSize))).toBeGreaterThanOrEqual(14);
  if(release) await expect(page.locator(".install-version")).toContainText(release.releaseVersion);
  await expect(page.locator('a[href*="checkout"],form[action]')).toHaveCount(0);
  // A disk image link appears only for a release whose verified metadata includes one.
  expect(await page.locator('a[href$=".dmg"]').evaluateAll(links=>links.map(link=>link.href)))
    .toEqual(release?.diskImageURL?[release.diskImageURL]:[]);
});

test("download and help are usable with the keyboard",async({page})=>{
  await page.route("https://github.com/**",route=>route.fulfill({contentType:"text/html",body:"<h1>Download fixture</h1>"}));
  await home(page);
  const get=page.getByRole("link",{name:"Get Annotator",exact:true});
  await get.focus();await get.press("Enter");
  await expect(page).toHaveURL(/#install$/);
  await expect(page.getByRole("heading",{name:"Or use Homebrew",exact:true})).toBeVisible();
  const button=page.locator("#install .download-action");
  await button.focus();await button.press("Enter");
  await expect(page).toHaveURL(download);
  await home(page);
  await page.getByRole("link",{name:"Help",exact:true}).click();
  await expect(page.getByRole("link",{name:"Help",exact:true})).toHaveAttribute("aria-current","page");
  const topics=page.getByRole("navigation",{name:"On this page"});
  for(const link of await topics.getByRole("link").all()){
    const target=await link.getAttribute("href");
    await expect(page.locator(target)).toHaveCount(1);
  }
});

test("installation disclosures retain safety limits without a wall of text",async({page})=>{
  await home(page);
  // Requirements are shown beside the download; the disclosure keeps the sharing limits and updates.
  await expect(page.locator(".install-needs")).toHaveText(release?.architecture === "arm64 x86_64"
    ? "Needs macOS 14 or later, on an Apple Silicon or Intel Mac." : "Needs macOS 14 or later and an Apple M-series chip.");
  await expect(page.locator(".install-needs")).toBeVisible();
  const summary=page.getByText("Screen sharing and updates",{exact:true});
  await summary.click();
  const detail=page.locator("details").filter({has:summary});
  await expect(detail).toContainText("App or window sharing may miss overlays");
  await expect(detail).toContainText("Save your board");
  await summary.focus();await summary.press("Enter");
  await expect(detail).not.toHaveAttribute("open","");
  await page.getByRole("tab",{name:"Blur",exact:true}).click();
  await expect(page.locator("#blur-title")).toBeVisible();
  await expect(page.locator("#blur-title").locator("..")).toContainText("isn’t secure redaction");
  await expect(page.locator(".footer").getByRole("link",{name:"Buy me a coffee"}))
    .toHaveAttribute("href","https://buymeacoffee.com/dorlugasigal");
});

test("the header and footer link to the public GitHub repository",async({page})=>{
  const repository="https://github.com/dorlugasigal/annotator";
  for(const [width,headerIcon] of [[1440,true],[390,true],[360,true],[320,false]]){
    await page.setViewportSize({width,height:800});
    await page.goto(sitePath("/"));
    // Role queries skip hidden elements, so find the icon by class to check it at every width.
    await expect(page.locator(".site-header .header-github")).toHaveAttribute("href",repository);
    const icon=page.locator(".site-header").getByRole("link",{name:"Annotator on GitHub",exact:true});
    // The narrowest phones keep the three main links; the footer link is always there.
    if(headerIcon){
      await expect(icon,`${width}px`).toBeVisible();
      const [header,box]=await Promise.all([page.locator(".site-header").boundingBox(),icon.boundingBox()]);
      expect(box.x+box.width,`${width}px`).toBeLessThanOrEqual(header.x+header.width);
      expect(box.width).toBeGreaterThanOrEqual(44);
    } else await expect(page.locator(".site-header .header-github"),`${width}px`).toBeHidden();
    expect(await page.locator("body").evaluate(e=>e.scrollWidth<=innerWidth+1)).toBe(true);
    await expect(page.locator(".footer").getByRole("link",{name:"Source code",exact:true})).toHaveAttribute("href",repository);
  }
});

test("download details still have exact release notes and Homebrew commands",async({page})=>{
  await page.setViewportSize({width:320,height:900});
  await page.goto(sitePath("/downloads/"));
  for(const entry of [metadata.stable,metadata.preview].filter(Boolean)){
    await expect(page.locator(`a[href="${entry.downloadURL}"]`)).toHaveCount(1);
    const repository=new URL(entry.downloadURL).pathname.split("/").slice(1,3).join("/");
    await expect(page.getByRole("link",{name:`Read release notes and known limits for ${entry.releaseVersion}`,exact:true}))
      .toHaveAttribute("href",`https://github.com/${repository}/releases/tag/v${entry.releaseVersion}`);
  }
  if(release){
    await expect(page.getByRole("region",{name:"Homebrew install command"}))
      .toHaveText("brew install --cask dorlugasigal/annotator/annotator");
  }
});

test("no JavaScript retains all product posters and native video controls",async({browser,baseURL})=>{
  const context=await browser.newContext({javaScriptEnabled:false});
  try {
    const page=await context.newPage();await page.goto(baseURL);
    for(const frame of await page.locator("[data-take-video]").all())
      await expect(frame).toHaveAttribute("controls","");
    for(const button of await page.locator("[data-take-toggle]").all()) await expect(button).toBeHidden();
    await page.locator(".long-demo > summary").click();
    await expect(page.locator("#native-demo-video")).toBeVisible();
    await page.getByRole("link",{name:"Help",exact:true}).click();
    await expect(page).toHaveURL(/\/docs\/$/);
  } finally {await context.close();}
});

test("script failure does not remove playback or navigation",async({page})=>{
  await page.route("**/*",route=>route.request().resourceType()==="script"?route.abort():route.continue());
  await page.goto(sitePath("/"));
  await expect(page.locator("[data-take-video]").first()).toHaveAttribute("controls","");
  await page.getByRole("link",{name:"Help",exact:true}).click();
  await expect(page).toHaveURL(/\/docs\/$/);
});

test("the longer film is opt-in, chaptered and paused when closed",async({page})=>{
  await home(page);
  const video=page.locator("#native-demo-video");
  expect(await video.evaluate(e=>e.paused)).toBe(true);
  await openFilm(page);
  await page.locator(".native-demo-transcript > summary").click();
  const chapter=demo.chapters.find(chapter=>chapter.label==="Arrow preferences");
  await page.getByRole("button",{name:"Arrow preferences",exact:true}).click();
  await expect.poll(()=>video.evaluate(e=>e.paused)).toBe(false);
  expect(await video.evaluate(e=>e.currentTime)).toBeGreaterThanOrEqual(chapter.time);
  await page.locator(".long-demo > summary").click();
  await expect.poll(()=>video.evaluate(e=>e.paused)).toBe(true);
});

test("provenance and actual playback speed stay explicit",async({page})=>{
  await home(page);
  // The stage's one disclosure says how the reel and the clips were made, and their real speeds.
  await page.locator(".recording-details > summary").click();
  const made=page.locator(".recording-details");
  await expect(made).toContainText(/play at up to \d+(\.\d)?× speed/);
  await expect(made).toContainText("play at 1.25× speed");
  await expect(made).toContainText("The pointer follows a script");
  // The longer film keeps its own transcript and music credit.
  await openFilm(page);
  await page.locator(".native-demo-transcript > summary").click();
  await expect(page.locator(".native-demo-transcript")).toContainText("scripted pointer");
  await expect(page.locator(".native-demo-transcript")).toContainText("earlier app recording");
  await expect(page.locator(".native-demo-transcript")).toContainText("sped up");
  await expect(page.locator(".native-demo-credit")).toContainText("Punch Deck");
});

test("short clips pause offscreen and after page restoration",async({page})=>{
  await home(page);
  const video=videoFor(page,"arrow");
  await page.getByRole("tab",{name:"Arrow",exact:true}).click();
  await page.locator("[data-native-take=arrow] [data-take-video]").scrollIntoViewIfNeeded();
  await video.evaluate(e=>e.play());
  await page.locator("#install").scrollIntoViewIfNeeded();
  await expect.poll(()=>video.evaluate(e=>e.paused)).toBe(true);
  await page.evaluate(()=>{dispatchEvent(new PageTransitionEvent("pagehide"));dispatchEvent(new PageTransitionEvent("pageshow",{persisted:true}));});
  expect(await video.evaluate(e=>e.paused)).toBe(true);
});
