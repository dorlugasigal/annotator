import {test, expect} from "@playwright/test";
import {readFileSync} from "node:fs";
import {siteConfig} from "../../site-config.mjs";

const base=siteConfig().base.replace(/\/$/,"");
const settings=JSON.parse(readFileSync(new URL("../../src/data/showcase-takes.json",import.meta.url),"utf8")).takes;
const stage=JSON.parse(readFileSync(new URL("../../src/data/stage-demos.json",import.meta.url),"utf8")).demos;
const tabs={circle:"Circle",arrow:"Arrow",highlight:"Highlight",smart:"Smart Draw",shapes:"Shapes",spotlight:"Spotlight",zoom:"Zoom",blur:"Blur"};
const media=(page,name)=>{
  const root=page.locator(`[data-native-take="${name}"]`);
  const video=root.locator("[data-take-video]");
  return {root,frame:video,video};
};
// Stage clips have a 4:5 phone encoding; the Settings clips have one file for every screen.
async function encodingFor(page,name){
  if(settings[name]&&!stage[name])return {...settings[name],playbackRate:1.25};
  const phone=await page.evaluate(()=>matchMedia("(max-width: 600px)").matches);
  return {...(phone?stage[name].phone:stage[name].desktop),playbackRate:1};
}

test("preview serves the exact requested tail of the MP4",async({request})=>{
  const {file:path}=stage.zoom.desktop;
  const file=readFileSync(new URL(`../../public${path}`,import.meta.url));
  const response=await request.get(`${base}${path}`,{headers:{Range:"bytes=-128"}});
  expect(response.status()).toBe(206);
  expect(response.headers()["content-range"]).toBe(`bytes ${file.length-128}-${file.length-1}/${file.length}`);
  expect(response.headers()["content-length"]).toBe("128");
  expect(await response.body()).toEqual(file.subarray(-128));
});

test("in-page native players decode every recording and keep real source metadata",async({page})=>{
  await page.goto(base+"/");
  for(const name of [...Object.keys(stage),"pointer","shortcuts"]){
    if(tabs[name]) await page.getByRole("tab",{name:tabs[name],exact:true}).click();
    const {root,frame,video}=media(page,name);
    const entry=await encodingFor(page,name);
    await frame.scrollIntoViewIfNeeded();
    await expect(root).toHaveAttribute("data-ready","true");
    await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(false);
    await expect.poll(()=>video.evaluate(value=>value.duration)).toBeCloseTo(entry.duration,1);
    await expect.poll(()=>video.evaluate(value=>value.getVideoPlaybackQuality().totalVideoFrames)).toBeGreaterThan(1);
    await expect(video).toHaveAttribute("crossorigin","anonymous");
    const source=new URL(await video.evaluate(value=>value.currentSrc));
    expect(source.pathname).toBe(`${base}${entry.file}`);
    expect(source.searchParams.get("v")).toBe(entry.sha256.slice(0,12));
    if(stage[name]) expect(await video.evaluate(value=>[value.videoWidth,value.videoHeight])).toEqual([entry.width,entry.height]);
    await expect.poll(()=>video.evaluate(value=>value.playbackRate)).toBe(entry.playbackRate);
    await expect(video).toHaveAttribute("aria-label",/.+/);
    await expect(video.locator("track")).toHaveCount(0);
    await expect(root.locator("[data-take-poster]")).toBeHidden();
    await expect(root).toHaveAttribute("data-presented","true");
  }
});

test("choosing a tool shows its finished poster at once, never the browser's controls",async({page})=>{
  await page.emulateMedia({reducedMotion:"no-preference"});
  await page.goto(base+"/");
  await page.locator("#workspace").evaluate(element=>element.scrollIntoView({block:"start",behavior:"instant"}));
  const names=["zoom","smart","blur"];
  // The stage loads every example's poster before the visitor reaches it (product-stage.ts), while the panels are
  // still hidden. Wait for that first: four parallel browsers can take two seconds to download them, and a click
  // before then measures the download, not the stage.
  await expect.poll(()=>page.evaluate(wanted=>wanted.map(name=>{
    const take=document.querySelector(`[data-native-take="${name}"]`), image=take.querySelector("[data-take-poster] img");
    return {hidden:take.closest("[data-stage-panel]").hidden, finished:image.complete&&image.naturalWidth>0};
  }),names),{timeout:15_000}).toEqual(names.map(()=>({hidden:true,finished:true})));
  for(const name of names){
    const {root,video}=media(page,name);
    // Record the first frame after the click, before any video data: the finished poster is shown and the native
    // controls are gone. A later round trip can land after the clip's first frames, when its fade-in has begun.
    await root.evaluate(element=>{
      delete window.__firstFrame;
      window.addEventListener("click",()=>requestAnimationFrame(()=>{
        const video=element.querySelector("video"), poster=element.querySelector("[data-take-poster]"), image=poster.querySelector("img");
        window.__firstFrame={controls:video.controls, poster:!poster.hidden, finished:image.complete&&image.naturalWidth>0,
          opacity:getComputedStyle(video).opacity};
      }),{capture:true,once:true});
    });
    await page.getByRole("tab",{name:tabs[name],exact:true}).click();
    const first=await (await page.waitForFunction(()=>window.__firstFrame)).jsonValue();
    expect(first,name).toEqual({controls:false,poster:true,finished:true,opacity:"0"});
    await expect(root.locator("[data-take-status]")).toBeHidden();
    await expect.poll(()=>video.evaluate(value=>!value.paused&&value.currentTime>0)).toBe(true);
    await expect(root).toHaveAttribute("data-presented","true");
  }
});

test("compact controls pause, resume and preserve manual pause offscreen",async({page})=>{
  await page.goto(base+"/");
  const {root,frame,video}=media(page,"circle");
  await frame.scrollIntoViewIfNeeded();
  await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(false);
  const button=root.locator("[data-take-toggle]");
  await button.click();
  await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(true);
  await page.locator("#install").scrollIntoViewIfNeeded();
  await frame.scrollIntoViewIfNeeded();
  await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(true);
  await button.click();
  await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(false);
  await page.locator("#install").scrollIntoViewIfNeeded();
  await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(true);
});

test("reduced motion keeps recordings manual and non-looping",async({page})=>{
  await page.emulateMedia({reducedMotion:"reduce"});
  await page.goto(base+"/");
  for(const [name,rate] of [["circle",1],["pointer",1.25]]){
    const {root,frame,video}=media(page,name);
    await frame.scrollIntoViewIfNeeded();
    await page.waitForTimeout(300);
    await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(true);
    expect(await video.evaluate(value=>value.loop)).toBe(false);
    await expect(root.locator("[data-take-poster]")).toBeVisible();
    await root.locator("[data-take-toggle]").click();
    await expect.poll(()=>video.evaluate(value=>value.paused)).toBe(false);
    await expect.poll(()=>video.evaluate(value=>value.playbackRate)).toBe(rate);
    expect(await video.evaluate(value=>value.loop)).toBe(false);
  }
});

test("a failed clip keeps its poster, its native controls and a direct link for this screen",async({page})=>{
  await page.route(/\/demo\/stage-arrow[^/]*\.mp4/,route=>route.abort());
  await page.goto(base+"/");
  await page.getByRole("tab",{name:"Arrow",exact:true}).click();
  const {root,frame,video}=media(page,"arrow");
  await frame.scrollIntoViewIfNeeded();
  await expect(root.locator("[data-take-error]")).toBeVisible();
  await expect(root).toHaveAttribute("data-playback","error");
  await expect(root.locator("[data-take-poster]")).toBeVisible();
  await expect(video).toHaveAttribute("controls","");
  const entry=await encodingFor(page,"arrow");
  const link=new URL(await root.locator("[data-take-error] a").evaluate(element=>element.href));
  expect(link.pathname).toBe(`${base}${entry.file}`);
  expect(link.searchParams.get("v")).toBe(entry.sha256.slice(0,12));
});

test("script-free visits retain native controls on every clip",async({browser,baseURL})=>{
  const context=await browser.newContext({javaScriptEnabled:false});
  try{
    const page=await context.newPage();await page.goto(baseURL);
    await expect(page.locator("[data-stage-tabs]")).toBeHidden();
    await page.locator('[data-stage-panel="arrow"] summary').click();
    for(const frame of await page.locator("[data-take-video]").all()){
      await expect(frame).toHaveAttribute("controls","");
    }
    for(const button of await page.locator("[data-take-toggle]").all()) await expect(button).toBeHidden();
    await expect(page.locator('[data-stage-panel="arrow"] [data-take-poster] img')).toBeVisible();
  }finally{await context.close();}
});

test("example icons are vectors and playback controls are touch sized",async({page})=>{
  await page.goto(base+"/");
  await expect(page.locator("[data-stage-tab] svg")).toHaveCount(Object.keys(stage).length);
  for(const name of ["circle","pointer"]){
    const box=await page.locator(`[data-native-take=${name}] [data-take-toggle]`).boundingBox();
    expect(box.width).toBeGreaterThanOrEqual(44);
    expect(box.height).toBeGreaterThanOrEqual(44);
  }
});
