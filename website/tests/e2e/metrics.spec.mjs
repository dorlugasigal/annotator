import {test, expect} from "@playwright/test";
import {readFile} from "node:fs/promises";
import {siteConfig} from "../../site-config.mjs";

const origin = "https://counts.example.test";
const base = siteConfig().base;
const mime = {".html":"text/html",".js":"text/javascript",".css":"text/css",".woff2":"font/woff2",".svg":"image/svg+xml",".jpg":"image/jpeg",".mp4":"video/mp4",".vtt":"text/vtt"};

async function fixture(page, events) {
  await page.route(`${origin}/**`, async route => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/metrics/web") {
      events.push(JSON.parse(route.request().postData()));
      await route.fulfill({status:202, contentType:"application/json", body:'{"accepted":true}'});
      return;
    }
    if (!url.pathname.startsWith(base)) throw new Error("Fixture request escaped the site base.");
    let relative = url.pathname.slice(base.length);
    if (!relative || relative.endsWith("/")) relative += "index.html";
    if (relative.includes("..")) throw new Error("Unsafe fixture path.");
    let body = await readFile(new URL(`../../dist/${relative}`, import.meta.url));
    if (relative.endsWith(".html")) {
      body = Buffer.from(body.toString().replace("<body ", `<body data-metrics-endpoint="${origin}" `));
    }
    const extension = relative.slice(relative.lastIndexOf("."));
    await route.fulfill({status:200, contentType:mime[extension] || "application/octet-stream", body});
  });
}

test("configured counts send one coarse event per tab and never break ordinary navigation", async ({page}) => {
  const events = [];
  await fixture(page, events);
  await page.goto(origin + base + "?private=query");
  await expect.poll(() => events.length).toBe(1);
  await page.getByRole("link",{name:"Help",exact:true}).click();
  await expect(page).toHaveURL(origin + base + "docs/");
  await page.waitForTimeout(100);
  expect(events).toHaveLength(1);
  expect(Object.keys(events[0]).sort()).toEqual(["event","referrer","route","session"]);
  expect(events[0].route).toBe("home");
  expect(JSON.stringify(events)).not.toContain("private");
  expect(await page.context().cookies()).toEqual([]);
  expect(await page.evaluate(() => localStorage.length)).toBe(0);
  expect(await page.evaluate(() => sessionStorage.length)).toBe(1);
});

test("Global Privacy Control suppresses all website counts", async ({page}) => {
  await page.addInitScript(() => Object.defineProperty(navigator,"globalPrivacyControl",{value:true}));
  const events = [];
  await fixture(page, events);
  await page.goto(origin + base);
  await page.getByRole("link",{name:"Help",exact:true}).click();
  expect(events).toEqual([]);
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
});
