import {test, expect} from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import {readFileSync} from "node:fs";

const adminRoot = new URL("../../../services/metrics/public/admin/", import.meta.url);
const font = new URL("../../public/fonts/bricolage-grotesque-latin.woff2", import.meta.url);
const config = readFileSync(new URL("../../../netlify.toml", import.meta.url), "utf8");
const csp = config.match(/Content-Security-Policy = "([^"]+)"/)[1];
const host = "https://admin.example.test";
const files = ["index.html", "admin.css", "admin.js", "admin-model.js"];

function summaryFixture({homebrew = [{day:"2026-09-22", counts:{}}]} = {}) {
  return {
    generatedAt:"2026-09-24T00:00:00Z", summaries:[
      {day:"2026-09-21", app:{active:2,newInstalls:0,wau:3,mau:5,versions:{"0.1.0-alpha.4 (12)":2},os:{"macOS 26":2},
        architectures:{arm64:2},features:{drawing:{"1":2}},launches:{"1":2}},
        web:{sessions:4,routes:{home:4},referrers:{github:4},downloads:{}},retention:{}},
      {day:"2026-09-22", app:{active:3,newInstalls:1,wau:4,mau:6,versions:{"0.1.0-alpha.4 (12)":3},os:{"macOS 26":3},
        architectures:{arm64:3},features:{drawing:{"2-5":2}},launches:{"1":3}},
        web:{sessions:5,routes:{home:5},referrers:{direct:5},downloads:{preview:2}},retention:{}},
    ],
    github:[{day:"2026-09-22", total:9, capturedAt:"2026-09-22T00:15:04Z",
      assets:{"v0.1.0-alpha.4/annotator-0.1.0-alpha.4-12-arm64.dmg":4,"v0.1.0-alpha.4/annotator-0.1.0-alpha.4-12-arm64.zip":5}}],
    homebrew,
    health:{day:"2026-09-23",status:"finished",aggregated:["2026-09-15","2026-09-23"],removed:0,errors:[],finishedAt:"2026-09-23T00:15:18Z"},
  };
}

async function adminFixture(page, {networkFailure = false, summary = summaryFixture(), refresh = {github:"updated"}} = {}) {
  let signedIn = false;
  const requests = [];
  await page.route(`${host}/**`, async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.startsWith("/api/")) {
      requests.push(`${route.request().method()} ${path}`);
      if (networkFailure) return route.abort();
      if (path === "/api/admin/login") signedIn = true;
      if (path === "/api/admin/logout") signedIn = false;
      const allowed = signedIn || path === "/api/admin/logout";
      const body = path === "/api/admin/summary" && allowed
        ? {...summary, ...(route.request().method() === "POST" ? {refresh} : {})} : {ok:allowed};
      return route.fulfill({status:allowed?200:401, contentType:"application/json", body:JSON.stringify(body)});
    }
    if (path === "/fonts/bricolage-grotesque-latin.woff2") return route.fulfill({status:200, body:readFileSync(font), contentType:"font/woff2"});
    const file = path === "/admin/" ? "index.html" : path.split("/").at(-1);
    if (!files.includes(file)) return route.fulfill({status:404,body:"not found"});
    return route.fulfill({status:200, body:readFileSync(new URL(file, adminRoot)),
      contentType:file.endsWith(".html")?"text/html":file.endsWith(".css")?"text/css":"text/javascript",
      headers:{"Content-Security-Policy":csp}});
  });
  await page.goto(`${host}/admin/`);
  return requests;
}

function watch(page) {
  const problems = [];
  page.on("pageerror", error => problems.push(error.message));
  page.on("console", message => { if (/Content Security Policy|Refused to/.test(message.text())) problems.push(message.text()); });
  return problems;
}

async function signIn(page) {
  await page.getByLabel("Passphrase").fill("local-fixture-only");
  await page.getByRole("button",{name:"Sign in",exact:true}).click();
  await expect(page.locator("#dashboard")).toBeVisible();
}

test("owner dashboard signs in, shows each measurement apart and signs out under its production CSP", async ({page}) => {
  const problems = watch(page);
  await adminFixture(page);
  await expect(page.locator("#gate")).toBeVisible();
  await expect(page.locator("#dashboard")).toBeHidden();
  await signIn(page);
  await expect(page.locator("#gate")).toBeHidden();
  await expect(page.locator("#password")).toHaveValue("");
  await expect(page.locator("#mau")).toHaveText("6");
  await expect(page.locator("#mau-caption")).toHaveText("opted-in copies used Annotator in the last 30 days");
  await expect(page.locator("#active")).toHaveText("3");
  await expect(page.locator("#active-label")).toContainText("Active on");
  await expect(page.locator("#tile-visits [data-value]")).toHaveText("9");
  await expect(page.locator("#tile-clicks [data-value]")).toHaveText("2");
  await expect(page.locator("#tile-github [data-value]")).toHaveText("9");
  await expect(page.locator("#download-kinds")).toContainText("Disk image");
  await expect(page.locator("#download-kinds")).toContainText("Also counts Homebrew installs");
  await expect(page.locator("#homebrew")).toContainText("Not reported");
  await expect(page.locator("#features")).toContainText("Drawing");
  await expect(page.locator(".health-state")).toHaveAttribute("data-level","ok");
  await expect(page.locator(".footnote")).toContainText("don't add them together");
  expect(await page.evaluate(() => document.fonts.check('16px "Bricolage Grotesque"'))).toBe(true);
  for(const width of [320,1440]) {
    await page.setViewportSize({width,height:900});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
  }
  // Check the resting page: the entrance fades text in, so wait until no animation runs.
  await page.waitForFunction(() => document.getAnimations().every(animation => animation.playState !== "running"));
  const result = await new AxeBuilder({page}).withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze();
  expect(result.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target)}))).toEqual([]);
  await page.getByRole("button",{name:"Sign out",exact:true}).click();
  await expect(page.locator("#gate")).toBeVisible();
  expect(problems).toEqual([]);
});

test("the daily chart can be read with the keyboard and a screen reader", async ({page}) => {
  await adminFixture(page);
  await signIn(page);
  const chart = page.locator("#active-chart");
  await expect(chart).toHaveAttribute("aria-label", /arrow keys/);
  await expect(page.locator("#chart-table tr")).toHaveCount(31);
  await chart.focus();
  await expect(page.locator("#chart-tip")).toBeVisible();
  await expect(page.locator("#chart-tip strong")).toHaveText("3 copies");
  await expect(page.locator("#chart-tip")).toContainText("1 new");
  await chart.press("ArrowLeft");
  await expect(page.locator("#chart-live")).toContainText("2 copies active");
  await chart.press("Home");
  await expect(page.locator("#chart-live")).toContainText("No count stored for this day");
  await chart.press("End");
  await expect(page.locator("#chart-live")).toContainText("3 copies active");
});

test("Refresh recounts today and says when GitHub couldn't be read", async ({page}) => {
  const requests = await adminFixture(page, {refresh:{github:"failed", error:"GitHub releases returned HTTP 403."}});
  await signIn(page);
  await page.getByRole("button",{name:"Refresh",exact:true}).click();
  await expect(page.locator("#status")).toContainText("GitHub didn't answer (GitHub releases returned HTTP 403.)");
  expect(requests).toContain("POST /api/admin/summary");
  await expect(page.getByRole("button",{name:"Refresh",exact:true})).toBeEnabled();
});

test("reported Homebrew counts and an empty store both read honestly", async ({page}) => {
  await adminFixture(page, {summary:summaryFixture({homebrew:[{day:"2026-09-22", counts:{"30d":12,"90d":30,"365d":41}}]})});
  await signIn(page);
  await expect(page.locator("#homebrew")).toContainText("12");
  await expect(page.locator("#homebrew")).not.toContainText("Not reported");
  await page.unrouteAll({behavior:"ignoreErrors"});
  await adminFixture(page, {summary:{generatedAt:"2026-09-24T00:00:00Z", summaries:[], github:[], homebrew:[], health:null}});
  await signIn(page);
  await expect(page.locator("#mau")).toHaveText("–");
  await expect(page.locator("#mau")).toHaveClass(/is-empty/);
  await expect(page.locator(".health-state")).toHaveAttribute("data-level","missing");
  await expect(page.locator("#releases")).toContainText("No release downloads counted yet.");
});

test("Windows copies show as Windows systems and chips, apart from Macs", async ({page}) => {
  const summary = summaryFixture();
  const latest = summary.summaries.at(-1);
  latest.app.os = {"macOS 26":2, "Windows 11":1};
  latest.app.architectures = {arm64:2, "windows-x86_64":1};
  await adminFixture(page, {summary});
  await signIn(page);
  await expect(page.locator("#app-title")).toHaveText("App versions and computers");
  await expect(page.locator("#os")).toContainText("Windows 11");
  await expect(page.locator("#os")).toContainText("macOS 26");
  await expect(page.locator("#chips")).toContainText("Windows x64");
  await expect(page.locator("#chips")).toContainText("Apple silicon");
  await expect(page.locator("#chips")).not.toContainText("Intel");
});

test("offline dashboard explains how to retry instead of showing an empty page", async ({page}) => {
  await adminFixture(page,{networkFailure:true});
  await expect(page.locator("#gate")).toBeVisible();
  await expect(page.locator("#login-error")).toContainText("could not be reached");
  await page.getByLabel("Passphrase").fill("local-fixture-only");
  await page.getByRole("button",{name:"Sign in",exact:true}).click();
  await expect(page.locator("#login-error")).toContainText("could not connect");
  await expect(page.locator("#password")).toHaveValue("");
});
