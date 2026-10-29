import {test, expect} from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import {readFileSync} from "node:fs";

const adminRoot = new URL("../../../services/metrics/public/admin/", import.meta.url);
const font = new URL("../../public/fonts/bricolage-grotesque-latin.woff2", import.meta.url);
const config = readFileSync(new URL("../../../netlify.toml", import.meta.url), "utf8");
const csp = config.match(/Content-Security-Policy = "([^"]+)"/)[1];
const host = "https://admin.example.test";
const files = ["index.html", "admin.css", "admin.js", "admin-model.js"];
const addDays = (day, amount) => new Date(Date.parse(`${day}T00:00:00Z`) + amount * 86_400_000).toISOString().slice(0, 10);
const asset = (version, build, name) => `v${version}/${name ?? `annotator-${version}-${build}-universal.dmg`}`;

function day(date, {active, newInstalls = 0, wau, mau, sessions, clicks, windows = 0, extra = {}}) {
  const mac = active - windows;
  return {
    day: date, generatedAt: `${addDays(date, 1)}T00:15:00.000Z`,
    app: {active, newInstalls, wau, mau, versions: {"0.1.0-beta.16 (34)": active},
      os: {"macOS 26": mac, ...(windows ? {"Windows 11": windows} : {})},
      architectures: {arm64: mac, ...(windows ? {"windows-x86_64": windows} : {})},
      launches: {"1": active}, features: {drawing: {"1": active}, zoom: {"1": 1}}, ...extra},
    web: {sessions, routes: {home: sessions}, referrers: {github: sessions}, downloads: {preview: clicks}},
    retention: {},
  };
}

const copies = (count, versions, os, architectures) => ({count, versions, os, architectures});

/**
 * Two weeks: the 7 days before the period, then a busier period where one Windows copy appears,
 * a little of today, and a release (0.1.0-beta.17) that came out today.
 */
function summaryFixture({homebrew = [{day:"2026-09-23", counts:{}}]} = {}) {
  const summaries = [];
  for (let index = 0; index < 14; index++) {
    const date = addDays("2026-09-10", index);
    summaries.push(index < 7
      ? day(date, {active:2, newInstalls:date === "2026-09-13" ? 1 : 0, wau:4, mau:6, sessions:10, clicks:1})
      : day(date, {active:3, newInstalls:[7, 10, 13].includes(index) ? 1 : 0, wau:5, mau:8, sessions:20, clicks:2,
        windows:index >= 12 ? 1 : 0}));
  }
  const windows = copies(1, {"0.1.0-beta.16 (34)":1}, {"Windows 11":1}, {"windows-x86_64":1});
  summaries.at(-1).app.platforms = {macos:{active:2, newInstalls:1}, windows:{active:1, newInstalls:0}};
  summaries.at(-1).app.unique = {
    d7:{macos:copies(4, {"0.1.0-beta.16 (34)":3, "0.1.0-beta.15 (33)":1}, {"macOS 26":3, "macOS 15":1}, {arm64:3, x86_64:1}), windows},
    d30:{macos:copies(7, {"0.1.0-beta.16 (34)":5, "0.1.0-beta.15 (33)":2}, {"macOS 26":5, "macOS 15":2}, {arm64:6, x86_64:1}), windows},
  };
  const today = day("2026-09-24", {active:2, wau:4, mau:8, sessions:6, clicks:0, windows:1, extra:{unique:{
    d7:{macos:copies(3, {"0.1.0-beta.17 (35)":3}, {}, {}), windows:copies(1, {"0.1.0-beta.17 (35)":1}, {}, {})},
    d30:{macos:copies(7, {"0.1.0-beta.17 (35)":3, "0.1.0-beta.16 (34)":4}, {}, {}), windows:copies(1, {"0.1.0-beta.17 (35)":1}, {}, {})},
  }}});
  today.generatedAt = "2026-09-24T09:00:00.000Z";
  const zip = asset("0.1.0-beta.16", 34, "annotator-0.1.0-beta.16-34-universal.zip");
  const windowsZip = asset("0.1.0-beta.16", 34, "Annotator-0.1.0-beta.16-windows-x64.zip");
  const snapshot = (date, assets, capturedAt = `${date}T00:15:04Z`) => ({day:date, capturedAt, assets,
    total:Object.values(assets).reduce((sum, value) => sum + value, 0)});
  return {
    generatedAt:"2026-09-24T09:30:00Z", summaries:[...summaries, today],
    github:[
      snapshot("2026-09-10", {[asset("0.1.0-beta.16", 34)]:2, [zip]:1}),
      snapshot("2026-09-17", {[asset("0.1.0-beta.16", 34)]:5, [zip]:3, [windowsZip]:1}),
      snapshot("2026-09-24", {[asset("0.1.0-beta.16", 34)]:9, [zip]:4, [windowsZip]:3, [asset("0.1.0-beta.17", 35)]:1}, "2026-09-24T09:00:04Z"),
    ],
    homebrew,
    health:{day:"2026-09-24",status:"finished",aggregated:["2026-09-16","2026-09-24"],removed:0,errors:[],finishedAt:"2026-09-24T00:15:18Z"},
  };
}

async function adminFixture(page, {networkFailure = false, summary = summaryFixture(), refresh = {github:"updated"}, hash = ""} = {}) {
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
  await page.goto(`${host}/admin/${hash}`);
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
  // The last 30 days by default; only 14 of them have counts, so nothing is compared.
  await expect(page.getByRole("radio",{name:"Last 30 days"})).toBeChecked();
  await expect(page.locator("#range-text")).toContainText("Aug 25 – Sep 23");
  await expect(page.locator("#range-note")).toContainText("Only 14 of these 30 days have counts");
  await expect(page.locator("#copies")).toHaveText("8");
  await expect(page.locator("#copies-change")).toBeHidden();
  await expect(page.locator("#copies-caption")).toHaveText("opted-in copies used Annotator in these 30 days");
  await expect(page.locator("#copies-mac")).toHaveText("7");
  await expect(page.locator("#copies-windows")).toHaveText("1");
  await expect(page.locator("#today")).toHaveText("2");
  await expect(page.locator("#step-visits [data-value]")).toHaveText("210");
  await expect(page.locator("#step-clicks [data-value]")).toHaveText("21");
  await expect(page.locator("#step-clicks [data-extra]")).toContainText("10% of visits");
  await expect(page.locator("#step-disk [data-value]")).toHaveText("8");
  await expect(page.locator("#step-optins [data-value]")).toHaveText("4");
  await expect(page.locator("#download-kinds")).toContainText("Mac disk image");
  await expect(page.locator("#download-kinds")).toContainText("Windows ZIP");
  await expect(page.locator("#homebrew")).toContainText("Not reported");
  await expect(page.locator("#features")).toContainText("Drawing");
  await expect(page.locator("#features")).toContainText("My shapes");
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

test("the period picker compares the last 7 days with the 7 days before, and the address keeps the choice", async ({page}) => {
  const problems = watch(page);
  await adminFixture(page);
  await signIn(page);
  await page.getByRole("radio",{name:"Last 7 days"}).check();
  await expect(page).toHaveURL(/#7-days$/);
  await expect(page.locator("#range-text")).toContainText("Sep 17 – Sep 23, compared with Sep 10 – Sep 16.");
  await expect(page.locator("#range-note")).toBeHidden();
  await expect(page.locator("#copies")).toHaveText("5");
  await expect(page.locator("#copies-change")).toHaveAttribute("data-trend","up");
  await expect(page.locator("#copies-change")).toContainText("Up, 1 · was 4");
  await expect(page.locator("#copies-caption")).toHaveText("opted-in copies used Annotator in these 7 days");
  // The week before had no Windows system, so its copies were all Macs.
  await expect(page.locator('[data-platform="windows"] .delta')).toContainText("Up, 1 · was 0");
  await expect(page.locator("#per-day-change")).toContainText("was 2");
  await expect(page.locator("#step-visits [data-change]")).toContainText("100% · was 70");
  await expect(page.locator("#step-clicks [data-extra]")).toContainText("No change · was 10%");
  await expect(page.locator("#step-disk [data-change]")).toContainText("Up, 2 · was 3");
  await expect(page.locator("#step-disk [data-extra]")).toContainText("On GitHub since Sep 17");
  await expect(page.locator("#step-optins [data-change]")).toContainText("Up, 2 · was 1");
  await expect(page.locator('#download-kinds [data-kind="macZip"]')).toContainText("+1");
  await expect(page.locator('#download-kinds [data-kind="macZip"] .delta')).toHaveAttribute("data-trend","down");
  await expect(page.locator('#download-kinds [data-kind="windowsZip"]')).toContainText("3 in all");
  await expect(page.locator("#features li").filter({hasText:"Zoom"})).toContainText("Down, was 50%");
  await page.reload();
  await expect(page.locator("#copies")).toHaveText("5");
  await expect(page.getByRole("radio",{name:"Last 7 days"})).toBeChecked();
  expect(problems).toEqual([]);
});

test("versions compare with the newest release out by the period's last day, and show a newer one with today's count", async ({page}) => {
  await adminFixture(page, {hash:"#7-days"});
  await signIn(page);
  const adoption = page.locator("#adoption");
  await expect(adoption).toContainText("80%");
  await expect(adoption).toContainText("of copies ran the newest release, 0.1.0-beta.16 (4 of 5).");
  await expect(adoption).toContainText("Mac 3 of 4 · Windows 1 of 1");
  await expect(adoption).toContainText("0.1.0-beta.17 came out after Sep 23. Counting today so far, 4 of 4 (100%) run it.");
  await expect(page.locator("#versions li").first()).toContainText("0.1.0-beta.16 newest");
});

test("the daily chart can be read with the keyboard and a screen reader, next to the days before", async ({page}) => {
  await adminFixture(page, {hash:"#7-days"});
  await signIn(page);
  const chart = page.locator("#active-chart");
  await expect(chart).toHaveAttribute("aria-label", /arrow keys/);
  await expect(page.locator("#chart-table tr")).toHaveCount(8);
  await chart.focus();
  await expect(page.locator("#chart-tip")).toBeVisible();
  await expect(page.locator("#chart-tip strong")).toHaveText("3 copies");
  await expect(page.locator("#chart-tip")).toContainText("1 new · 1 on Windows");
  await expect(page.locator("#chart-tip .tip-before")).toHaveText("Sep 16: 2 copies");
  await chart.press("ArrowLeft");
  await expect(page.locator("#chart-live")).toContainText("Sep 22: 3 copies active, 1 on Windows. Sep 15: 2 copies.");
  await chart.press("Home");
  await expect(page.locator("#chart-live")).toContainText("Sep 17: 3 copies active, 1 new. Sep 10: 2 copies.");
  await page.getByRole("radio",{name:"Last 30 days"}).check();
  await chart.focus();
  await chart.press("Home");
  await expect(page.locator("#chart-live")).toContainText("No count stored for this day");
  await chart.press("End");
  await expect(page.locator("#chart-live")).toContainText("Sep 23: 3 copies active");
});

test("Refresh recounts yesterday and today and says when GitHub couldn't be read", async ({page}) => {
  const requests = await adminFixture(page, {refresh:{github:"failed", error:"GitHub releases returned HTTP 403."}});
  await signIn(page);
  await page.getByRole("button",{name:"Refresh",exact:true}).click();
  await expect(page.locator("#status")).toContainText("Recounted yesterday and today");
  await expect(page.locator("#status")).toContainText("GitHub didn't answer (GitHub releases returned HTTP 403.)");
  expect(requests).toContain("POST /api/admin/summary");
  await expect(page.getByRole("button",{name:"Refresh",exact:true})).toBeEnabled();
});

test("a day counted before it ended, reported Homebrew counts and an empty store all read honestly", async ({page}) => {
  const early = summaryFixture({homebrew:[{day:"2026-09-23", counts:{"30d":12,"90d":30,"365d":41}}]});
  early.summaries.find(summary => summary.day === "2026-09-23").generatedAt = "2026-09-23T20:00:00.000Z";
  await adminFixture(page, {summary:early});
  await signIn(page);
  await expect(page.locator("#range-note")).toContainText("Sep 23 is still being counted.");
  await expect(page.locator("#homebrew")).toContainText("12");
  await expect(page.locator("#homebrew")).not.toContainText("Not reported");
  await page.unrouteAll({behavior:"ignoreErrors"});
  await adminFixture(page, {summary:{generatedAt:"2026-09-24T00:00:00Z", summaries:[], github:[], homebrew:[], health:null}});
  await signIn(page);
  await expect(page.locator("#copies")).toHaveText("–");
  await expect(page.locator("#copies")).toHaveClass(/is-empty/);
  await expect(page.locator("#range-note")).toContainText("No counts yet.");
  await expect(page.locator('[data-platform="macos"]')).toContainText("No counts yet.");
  await expect(page.locator("#adoption")).toContainText("No versions reported yet.");
  await expect(page.locator(".health-state")).toHaveAttribute("data-level","missing");
  await expect(page.locator("#releases")).toContainText("No release downloads counted yet.");
});

test("Windows copies show as Windows systems and chips, apart from Macs", async ({page}) => {
  await adminFixture(page);
  await signIn(page);
  await expect(page.locator("#platforms-title")).toHaveText("Mac and Windows");
  const windows = page.locator('[data-platform="windows"]'), mac = page.locator('[data-platform="macos"]');
  await expect(windows).toContainText("Windows 11");
  await expect(windows).toContainText("Windows x64");
  await expect(windows).not.toContainText("Apple silicon");
  await expect(windows).not.toContainText("Intel");
  await expect(mac).toContainText("macOS 26");
  await expect(mac).toContainText("Apple silicon");
  await expect(mac).toContainText("Intel");
  await expect(mac).not.toContainText("Windows");
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
