import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { siteConfig } from "../site-config.mjs";
import { readdir } from "node:fs/promises";
import {createHash} from "node:crypto";
import { inspectMP4, jpegFrame, jpegSize } from "./media-inspect.mjs";
import { parseWindowsListing } from "../src/lib/windows-listing.ts";

test("site URLs allow the selected Pages subpath and future root domain", () => {
  assert.deepEqual(siteConfig("https://dorlugasigal.github.io/annotator"),
    { site: "https://dorlugasigal.github.io", base: "/annotator/" });
  assert.deepEqual(siteConfig("https://example.com"), { site: "https://example.com", base: "/" });
  for (const value of ["http://example.com", "https://user:pass@example.com", "https://example.com/a?x=1",
                       "https://example.com/a%2fb", "https://example.com:444/a", "https://example.com/a/../b"]) {
    assert.throws(() => siteConfig(value));
  }
  for (const [value, base] of [["https://example.com/", "/"], ["https://example.com/a/b", "/a/b/"], ["https://example.com/a_b-1/c/", "/a_b-1/c/"]]) {
    assert.equal(siteConfig(value).base, base);
  }
  for (const value of ["https://example.com//a", "https://example.com/a//b", "https://example.com/a.b"]) {
    assert.throws(() => siteConfig(value));
  }
  // A long path that almost matches is rejected at once, without exponential backtracking.
  const started = performance.now();
  assert.throws(() => siteConfig(`https://example.com/${"-".repeat(64)}!`));
  assert.ok(performance.now() - started < 1000);
});

test("browser test port override is bounded and preserves the default", async () => {
  const previous = process.env.ANNOTATOR_E2E_PORT;
  try {
    delete process.env.ANNOTATOR_E2E_PORT;
    const baseline = (await import("../playwright.config.mjs?port-default")).default;
    assert.match(baseline.use.baseURL, /^http:\/\/127\.0\.0\.1:4178\//);
    process.env.ANNOTATOR_E2E_PORT = "4397";
    const isolated = (await import("../playwright.config.mjs?port-isolated")).default;
    assert.match(isolated.use.baseURL, /^http:\/\/127\.0\.0\.1:4397\//);
    assert.equal(isolated.webServer.command, "npm run preview -- --port 4397");
    for (const value of ["0", "80", "65536", "4397; echo invalid"]) {
      process.env.ANNOTATOR_E2E_PORT = value;
      await assert.rejects(import(`../playwright.config.mjs?invalid=${encodeURIComponent(value)}`),
        /ANNOTATOR_E2E_PORT must be a port/);
    }
  } finally {
    if (previous === undefined) delete process.env.ANNOTATOR_E2E_PORT;
    else process.env.ANNOTATOR_E2E_PORT = previous;
  }
});

test("generated HTML local links and assets stay inside the configured base path", async () => {
  const base = siteConfig().base;
  const root = fileURLToPath(new URL("../dist/", import.meta.url));
  async function inspect(dir) {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const path = `${dir}/${entry.name}`;
      if (entry.isDirectory()) { await inspect(path); continue; }
      if (!entry.name.endsWith(".html")) continue;
      const html = await readFile(path, "utf8");
      for (const match of html.matchAll(/(?:href|src|poster)="(\/[^"]*)"/g)) {
        assert.ok(!match[1].startsWith("//") && match[1].startsWith(base), `${path}: ${match[1]} escapes ${base}`);
      }
    }
  }
  await inspect(root);
});

test("static site describes free tools and optional external support", async () => {
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  assert.match(home, /All tools are free/);
  assert.match(home, /No account/);
  assert.match(home, /No paid tiers/);
  assert.match(home, /href="https:\/\/buymeacoffee\.com\/dorlugasigal"/);
  assert.match(home, /Coffee is optional/);
  assert.match(home, /Buy me a coffee/);
  assert.doesNotMatch(home, /[Bb]uy me a beer|[Pp]rivate preview|[Pp]rivate development/);
  assert.doesNotMatch(home, /Purchasing isn't open yet|Pro feature limits|optional paid features|action="[^"]*(?:checkout|payment)/);
  assert.doesNotMatch(home, /<iframe\b/);
  const stage = JSON.parse(await readFile(new URL("../src/data/stage-demos.json", import.meta.url), "utf8"));
  // One clip per tool on the stage, plus Settings and region sharing.
  assert.equal((home.match(/<video[^>]+data-take-video/g) || []).length, Object.keys(stage.demos).length + 3);
  assert.match(home, /noindex,nofollow/);
  assert.match(home, /id="main"/);
});

test("page videos have direct sources and no track children that iPhone WebKit rejects", async () => {
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const metadata = JSON.parse(await readFile(new URL("../src/data/showcase-takes.json", import.meta.url), "utf8"));
  const stage = JSON.parse(await readFile(new URL("../src/data/stage-demos.json", import.meta.url), "utf8"));
  assert.doesNotMatch(home, /<track\b/);
  // The Settings clips come from the showcase recordings, not the stage encodings.
  for (const name of ["pointer", "shortcuts"]) {
    const take = metadata.takes[name];
    assert.ok(home.includes(`${take.file}?v=${take.sha256.slice(0, 12)}`), name);
  }
  for (const [name, demo] of Object.entries(stage.demos)) {
    const clip = home.match(new RegExp(`<figure[^>]+data-native-take="${name}"[\\s\\S]*?</figure>`))?.[0];
    assert.ok(clip, `${name} is on the stage`);
    for (const entry of [demo.desktop, demo.phone]) {
      const version = entry.sha256.slice(0, 12);
      assert.ok(clip.includes(`${entry.file}?v=${version}`), `${name}: ${entry.file} is checksum bound`);
      assert.ok(clip.includes(`${entry.poster}?v=${version}`), `${name}: ${entry.poster} is checksum bound`);
    }
    assert.match(clip, new RegExp(`<source media="\\(max-width: 600px\\)" src="[^"]*${demo.phone.file}\\?v=`));
    assert.match(clip, new RegExp(`<a href="[^"]*${demo.desktop.file}\\?v=${demo.desktop.sha256.slice(0, 12)}">`), `${name} direct link`);
  }
  for (const video of home.match(/<video[^>]+data-take-video[^>]*>/g) || []) {
    assert.match(video, /\bcontrols\b/);
    assert.match(video, /\bmuted\b/);
    assert.match(video, /\bplaysinline\b/);
    assert.match(video, /crossorigin="anonymous"/);
    assert.doesNotMatch(video, /\b(?:autoplay|loop)\b/, "Scripts decide autoplay and looping from motion settings.");
  }
});

test("support and privacy explain the voluntary contribution without embedded payment code", async () => {
  const support = await readFile(new URL("../dist/support/index.html", import.meta.url), "utf8");
  const privacy = await readFile(new URL("../dist/privacy/index.html", import.meta.url), "utf8");
  const ai = await readFile(new URL("../dist/ai/index.html", import.meta.url), "utf8");
  assert.match(support, /href="https:\/\/buymeacoffee\.com\/dorlugasigal"/);
  assert.match(privacy, /does not collect card details or load a payment widget/);
  assert.doesNotMatch(ai, /Pro feature limits/);
  for (const html of [support, privacy]) {
    assert.doesNotMatch(html, /<(?:script|iframe)[^>]+src="https:\/\/[^"]*(?:buymeacoffee|stripe|paypal)/);
  }
});

test("support, privacy and compatibility pages exist without fake contact endpoints", async () => {
  for (const page of ["docs", "privacy", "support", "downloads"]) {
    const html = await readFile(new URL(`../dist/${page}/index.html`, import.meta.url), "utf8");
    assert.match(html, /<html lang="en"/);
    assert.doesNotMatch(html, /mailto:.*@example|action=".*signup/);
  }
  const docs = await readFile(new URL("../dist/docs/index.html", import.meta.url), "utf8");
  assert.match(docs, /Quitting Annotator, or an app crash, removes its covers/);
});

test("app downloads appear only from verified distribution metadata", async () => {
  const data = JSON.parse(await readFile(process.env.DISTRIBUTION_METADATA_FILE ||
    new URL("../src/data/distribution.json", import.meta.url), "utf8"));
  const html = await readFile(new URL("../dist/downloads/index.html", import.meta.url), "utf8");
  const entries = [data.stable, data.preview].filter(Boolean);
  if (!entries.length) {
    assert.match(html, /This page has no verified download details/);
    assert.doesNotMatch(html, /href="[^"]*\/releases\/download\//);
  }
  for (const entry of entries) {
    assert.ok(html.includes(entry.downloadURL));
    assert.ok(html.includes(entry.sha256));
    // A release with a disk image offers it first, and keeps the ZIP and both checksums on the page.
    if (entry.diskImageURL) {
      assert.ok(html.indexOf(entry.diskImageURL) < html.indexOf(`>ZIP archive<`));
      assert.ok(html.includes(entry.diskImageSHA256));
    }
    assert.ok(html.includes(entry.releaseVersion));
    const url = new URL(entry.downloadURL);
    const repository = url.pathname.split("/").slice(1, 3).join("/");
    assert.ok(html.includes(`https://github.com/${repository}/releases/tag/v${entry.releaseVersion}`),
      "Each download needs notes for that exact version and repository.");
  }
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const featured = data.stable || data.preview;
  const downloadURL = featured?.diskImageURL ?? featured?.downloadURL ?? "https://github.com/dorlugasigal/annotator/releases";
  assert.ok(home.includes(`class="button download-action" href="${downloadURL}"`));
  // The first step names the file the button downloads.
  assert.ok(home.includes(featured?.diskImageURL ? "Download and open the disk image." : "Download and unzip."));
  assert.match(home, /id="homebrew"/);
  if (entries.length) {
    assert.match(home, /Download for Mac/);
    assert.doesNotMatch(home, /not available yet|no verified download details|private preview/i);
    // The homepage names the track and version beside the download; the build number stays on Downloads.
    const stage = featured.releaseVersion.match(/-(alpha|beta|rc)\./)?.[1];
    const label = featured.track === "stable" ? "Version" :
      {alpha: "Public alpha", beta: "Public beta", rc: "Release candidate"}[stage];
    assert.ok(home.includes(`${label} ${featured.releaseVersion} · `));
    // One command: Homebrew adds the homebrew-annotator tap by name, so there is no tap step.
    assert.doesNotMatch(html, /brew tap /);
    assert.match(html, /brew install --cask dorlugasigal\/annotator\/annotator/);
    // The old annotator-preview migration steps were removed at the owner's request.
    assert.doesNotMatch(html, /annotator-preview|migration steps/);
  } else {
    assert.doesNotMatch(html, /brew install --cask/);
  }
  // Requirements follow verified architectures: Intel appears only once a published release runs on it.
  const docs = await readFile(new URL("../dist/docs/index.html", import.meta.url), "utf8");
  if (featured?.architecture === "arm64 x86_64") {
    assert.match(home, /Free · macOS 14\+ · Apple Silicon and Intel/);
    assert.match(home, /Needs macOS 14 or later, on an <span class="nowrap">Apple Silicon or Intel Mac\.<\/span>/);
    assert.match(home, /For macOS 14 and later on Apple Silicon and Intel Macs\./);
    assert.match(docs, /on a Mac with Apple Silicon or an Intel processor/);
  } else {
    assert.match(home, /Free · macOS 14\+ · Apple Silicon(?! and Intel)/);
    assert.match(home, /Needs macOS 14 or later and an <span class="nowrap">Apple M-series chip\.<\/span>/);
    assert.doesNotMatch(home + docs, /Intel Mac|Intel processor|Apple Silicon and Intel/);
  }
  const everyIntel = entries.length > 0 && entries.every(entry => entry.architecture === "arm64 x86_64");
  assert.equal(html.includes("on a Mac with Apple Silicon or an Intel processor"), everyIntel);
});

test("the Windows listing setting is null or the Store ID of one listing", async () => {
  assert.equal(parseWindowsListing({ storeId: null }), null);
  assert.deepEqual(parseWindowsListing({ storeId: "9NBLGGH4R315" }), {
    storeId: "9NBLGGH4R315",
    listingURL: "https://apps.microsoft.com/detail/9NBLGGH4R315",
    wingetCommand: "winget install --id 9NBLGGH4R315 --source msstore",
  });
  for (const value of [null, [], {}, { storeId: undefined }, { storeId: "" }, { storeId: 12 }, { storeId: "9nblggh4r315" },
    { storeId: "9NBLGGH4R31" }, { storeId: "9NBLGGH4R3155" }, { storeId: "9NBLGGH4R31 " }, { storeId: "../9NBLGGH4R3" },
    { storeId: null, listingURL: "https://apps.microsoft.com/detail/9NBLGGH4R315" }]) {
    assert.throws(() => parseWindowsListing(value), undefined, JSON.stringify(value));
  }
  // The fixture's made-up ID can never ship as the real listing.
  const committed = JSON.parse(await readFile(new URL("../src/data/windows-listing.json", import.meta.url), "utf8"));
  const fixture = JSON.parse(await readFile(new URL("./fixtures/windows-listing.json", import.meta.url), "utf8"));
  assert.ok(parseWindowsListing(fixture));
  assert.notEqual(committed.storeId, fixture.storeId);
});

// WIN-PRV-01: the privacy page and the Windows app say the same about usage counts. The app sends them only once
// UsageMetrics.ServiceAcceptsWindows is true; until then the page says Windows sends none and the app's switch stays off.
test("the privacy page's Windows usage counts follow the app's switch", async () => {
  const metrics = await readFile(new URL("../../apps/windows/src/Annotator.Workspace/Metrics/UsageMetrics.cs", import.meta.url), "utf8");
  const settings = await readFile(new URL("../../apps/windows/src/Annotator.Workspace/Settings/SettingsCopy.cs", import.meta.url), "utf8");
  const page = await readFile(new URL("../src/pages/privacy/index.astro", import.meta.url), "utf8");
  const accepts = metrics.match(/public const bool ServiceAcceptsWindows = (true|false);/);
  assert.ok(accepts, "UsageMetrics.ServiceAcceptsWindows is a constant in the Windows app");
  assert.equal(accepts[1], "false",
    "Windows may send usage counts now: say on the privacy page what Annotator for Windows sends, then update this test.");
  assert.match(page, /\{windows && <p>Annotator for Windows doesn't send usage counts yet\. [^<]*Share usage counts stays off and says counts aren't available\.<\/p>\}/);
  assert.match(settings, /UsageCountsUnavailable = "Usage counts aren't available on Windows yet\. Nothing is sent\.";/);
});

// Astro escapes some characters in text; compare the words people read.
const decodeHTML = html => html.replace(/&#39;/g, "'").replace(/&quot;/g, '"').replace(/&lt;/g, "<")
  .replace(/&gt;/g, ">").replace(/&amp;/g, "&");
// Removing a tag can join the text around it into a new tag, so repeat until none is left.
const textContent = html => {
  let text = html;
  while (/<[^>]+>/.test(text)) text = text.replace(/<[^>]+>/g, "");
  return text;
};
// Anything that offers, describes or names Windows. Spec 013 (WIN-DST-08) allows it only with a published listing.
const windowsClaim = /\bWindows\b|winget|msstore|Microsoft Store|apps\.microsoft\.com|Partner Center|Snipping Tool|22H2|ARM64|Ctrl\+/;

test("Windows appears only with a published Microsoft Store listing", async () => {
  const listing = parseWindowsListing(JSON.parse(await readFile(process.env.WINDOWS_LISTING_FILE ||
    new URL("../src/data/windows-listing.json", import.meta.url), "utf8")));
  const root = fileURLToPath(new URL("../dist/", import.meta.url));
  const pages = [];
  const outputs = [];
  async function collect(dir) {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const path = `${dir}/${entry.name}`;
      // The combined Netlify build copies in the owner's private metrics dashboard, which names Windows systems.
      if (entry.isDirectory() && path === `${root}/admin`) continue;
      if (entry.isDirectory()) { await collect(path); continue; }
      outputs.push(path);
      if (entry.name.endsWith(".html")) pages.push([path, await readFile(path, "utf8")]);
    }
  }
  await collect(root);
  assert.ok(pages.length >= 6);
  if (!listing) {
    for (const [path, html] of pages) assert.doesNotMatch(decodeHTML(html), windowsClaim, `${path} mentions Windows without a listing`);
    // Nothing else in the build names Windows either: no script, style or data file.
    for (const path of outputs.filter(path => /\.(?:js|css|json|svg|txt|xml|webmanifest|vtt)$/.test(path))) {
      assert.doesNotMatch(await readFile(path, "utf8"), windowsClaim, `${path} mentions Windows without a listing`);
    }
    assert.deepEqual(outputs.filter(path => /windows/i.test(path.slice(root.length))), [], "Windows files are only built with a listing");
    return;
  }
  const page = async name => decodeHTML(await readFile(new URL(`../dist/${name}`, import.meta.url), "utf8"));
  const [home, downloads, docs, ai, privacy, support] = await Promise.all(
    ["index.html", "downloads/index.html", "docs/index.html", "ai/index.html", "privacy/index.html", "support/index.html"].map(page));
  // The Store link is a text button beside the Mac download, with no Store badge artwork and no download count.
  const storeLinks = home.match(/<a[^>]*href="https:\/\/apps\.microsoft\.com\/[^"]*"[^>]*>/g) ?? [];
  assert.equal(storeLinks.length, 1);
  assert.ok(storeLinks[0].includes(`href="${listing.listingURL}"`));
  assert.match(storeLinks[0], /class="button store-action"/);
  assert.doesNotMatch(storeLinks[0], /data-download-track/, "The counting service accepts only Mac release tracks.");
  assert.doesNotMatch(home, /<img[^>]*(?:badge|microsoft)/i);
  // Both systems are offered behind one switch that starts on the visitor's system; without scripts both show, Mac first.
  assert.match(home, /data-platform-scope data-platform-default="visitor"[^>]*>[\s\S]*?aria-label="Install on" data-platform-switch hidden/);
  const macChoice = home.indexOf('class="install-choice" data-platform-part="mac"');
  const windowsChoice = home.indexOf('class="install-choice" data-platform-part="windows"');
  assert.ok(macChoice > 0 && macChoice < windowsChoice);
  assert.ok(home.indexOf('class="button download-action"') < home.indexOf(listing.listingURL));
  assert.match(home, /Get it for Windows/);
  assert.match(home, /Microsoft Store · Windows 11, version 22H2 or later · x64 and ARM64/);
  assert.match(home, /<p class="hero-availability">Free · macOS 14\+ · [^<]* · Windows 11<\/p>/);
  // Each install command stays with its system's download: Homebrew with the Mac, winget with Windows.
  const commands = [...home.matchAll(/<code data-copy-command>([\s\S]*?)<\/code>/g)].map(match => textContent(match[1]));
  assert.deepEqual(commands, ["brew install --cask dorlugasigal/annotator/annotator", listing.wingetCommand]);
  assert.match(home.slice(macChoice, windowsChoice), /<section class="brew" id="homebrew"/);
  assert.ok(home.indexOf('class="store-command"') > windowsChoice);
  assert.match(home, /data-copied-message="Copied\. Paste into Terminal and press Enter\."/);
  // The Windows install steps are text only: no picture or video stands in for the Mac's install animation.
  const windowsGuide = home.slice(home.indexOf('data-install-guide="windows"'));
  assert.doesNotMatch(windowsGuide.slice(0, windowsGuide.indexOf("</ol>")), /<(?:img|video|picture|figure)\b/);
  for (const step of ["Get it from the Microsoft Store or with winget.", "Open Annotator from Start.", "It runs from the system tray.",
    "Drag Annotator to Applications.", "It runs from the menu bar."]) assert.ok(home.includes(step), step);
  // The product videos are the Mac recordings for both systems.
  assert.match(home, /The Windows app has the same tools and looks the same\./);
  assert.doesNotMatch(home, /data-shot-panel=|Show the app on|\/windows\/[a-z-]+\.jpg/);
  assert.match(home, /How these videos were made/);  // Draw uses the same keys on both systems, so the keycaps use their names; the tool library adds Ctrl+L.
  assert.match(home, /aria-label="Control Shift 2"><kbd>Ctrl<\/kbd><kbd>Shift<\/kbd><kbd>2<\/kbd>/);
  assert.match(home, /<kbd>⌘L<\/kbd>\/<kbd>Ctrl\+L<\/kbd>/);
  assert.match(home, /press Return \(Enter on Windows\)/);
  assert.match(home, /On Windows, updates come from the Microsoft Store\./);
  assert.match(home, /For macOS 14 and later on [^.]+\. Also for Windows 11\./);
  for (const html of [downloads, docs]) {
    assert.match(html, /id="windows"/);
    assert.match(html, /Requires Windows 11, version 22H2 or later, on an x64 or ARM64 PC\. To check, open Settings > System > About\./);
    assert.ok(html.includes(`href="${listing.listingURL}"`));
    // Downloads and Help give the winget command with the Store ID (WIN-DST-02), as a block that never breaks inside an argument.
    assert.ok(html.includes(`aria-label="winget install command"><code>${listing.wingetCommand}</code></pre>`));
  }
  assert.match(downloads, /VoiceOver and Narrator support and testing on other hardware are incomplete\./);
  assert.match(downloads, /need Screen Recording permission on a Mac and screen access on Windows\./);
  // Help lists the Windows keys of the keyboard mapping in docs/parity.md under the Mac keys.
  const spec = await readFile(new URL("../../docs/parity.md", import.meta.url), "utf8");
  for (const [mac, keys, specRow] of [
    ["Control-1", "Ctrl+1"], ["Control-2", "Ctrl+2"], ["Control-Shift-2", "Ctrl+Shift+2"],
    ["Control-Option-2", "Ctrl+Alt+Shift+2"], ["Control-W / Control-B", "Ctrl+W / Ctrl+B"],
    ["Command-L", "Ctrl+L"], ["Command-Return", "Ctrl+Enter"],
    ["Delete", "Backspace or Delete", "| Delete or Forward Delete | Backspace or Delete |"],
    ["Command-V", "Ctrl+V", "| Command-C / X / V | Ctrl+C / X / V |"],
    ["Command-Z / Shift-Command-Z", "Ctrl+Z / Ctrl+Shift+Z or Ctrl+Y", "| Shift-Command-Z | Ctrl+Shift+Z and Ctrl+Y |"],
  ]) {
    assert.ok(spec.includes(specRow ?? `| ${mac} | ${keys} |`), `docs/parity.md maps ${mac} to ${keys}`);
    assert.ok(docs.includes(`<dt>${mac}<span class="windows-key">Windows: ${keys}</span></dt>`), `Help shows ${keys} for ${mac}`);
  }
  // Keys named in Help's text get their Windows keys where they appear, as the same mapping gives them.
  for (const [mac, keys] of [["Command-L", "Ctrl+L"], ["Command-Return", "Ctrl+Enter"], ["Command-A", "Ctrl+A"]]) {
    assert.ok(spec.includes(`| ${mac} | ${keys} |`), `docs/parity.md maps ${mac} to ${keys}`);
    assert.ok(docs.includes(`${mac} (${keys} on Windows)`), `Help's text shows ${keys} beside ${mac}`);
  }
  assert.match(docs, /Control-W toggles Whiteboard and Control-B toggles Blackboard \(Ctrl\+W and Ctrl\+B on Windows\)/);
  assert.match(docs, /Press Control-Shift-2 \(Ctrl\+Shift\+2 on Windows\) or click Resume drawing/);
  assert.match(docs, /<strong>Control-Shift-2<\/strong> \(<strong>Ctrl\+Shift\+2<\/strong> on Windows\)/);
  assert.match(docs, /On Windows, these are <strong>Ctrl\+2<\/strong> and <strong>Ctrl\+Alt\+Shift\+2<\/strong>\./);
  assert.match(docs, /Snipping Tool \(Win\+Shift\+S\)/);
  assert.match(docs, /system tray, near the clock/);
  assert.match(docs, /<a href="#windows">Windows<\/a>/);
  // Help answers the Mac-only parts for Windows where they come up: permissions, sign-in, motion and installing.
  assert.match(docs, /<p id="windows-permissions">On Windows, drawing, global shortcuts and Alt\+scroll over other apps need no permission\./);
  assert.match(docs, /Privacy & security > Screenshots and screen recording/);
  assert.match(docs, /On Windows, the switch in Settings > General is Launch at sign-in\./);
  assert.match(docs, /Windows Settings > Apps > Startup/);
  assert.match(docs, /On Windows, when animation effects are off, a short fade replaces any movement\./);
  assert.match(docs, /On Windows, zoom needs screen access, and Alt\+scroll needs no permission\./);
  assert.match(docs, /On a Mac, keep Annotator in the Applications folder/);
  assert.match(docs, /On Windows, the Microsoft Store installs Annotator and its updates/);
  assert.match(docs, /Annotator for Windows requires Windows 11, version 22H2 or later/);
  assert.match(docs, /That app needs its own macOS or Windows permissions/);
  assert.match(ai, /On Windows, it uses Snipping Tool \(Win\+Shift\+S\) and its recorder instead\./);
  assert.match(ai, /draw and edit in the Mac and Windows apps/);
  assert.match(ai, /need Screen Recording on a Mac and screen access on Windows\./);
  assert.match(support, /It includes app and macOS or Windows versions/);
  assert.match(support, />Mac and Windows requirements</);
  // WIN-PRV-01 and WIN-PRV-02: what the Store and Windows collect, and no usage counts from Windows yet.
  assert.match(privacy, /<h1>What stays on your computer\.<\/h1>/);
  assert.match(privacy, /PNG export saves an image on your computer\./);
  assert.match(privacy, /Annotator for Windows doesn't send usage counts yet\. Share usage counts stays off and says counts aren't available\./);
  assert.match(privacy, /Partner Center, we can see how many people got and installed Annotator/);
  assert.match(privacy, /Settings > Privacy & security > Diagnostics & feedback/);
  assert.match(privacy, /A crash report can include part of Annotator's memory/);
  assert.match(privacy, /On a Mac, app errors use local system logs/);
  assert.match(privacy, /href="https:\/\/www\.microsoft\.com\/en-us\/privacy\/privacystatement"/);
});


test("region sharing explains ultrawide sharing below the drawing shortcut section", async () => {
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const docs = await readFile(new URL("../dist/docs/index.html", import.meta.url), "utf8");
  assert.match(home, /Share your ultrawide clearly/);
  assert.match(home, /Show half, a third or any area, so text stays readable/);
  assert.match(home, /including notifications/);
  assert.doesNotMatch(home, /Next beta only|Coming in the next beta/);
  assert.ok(home.indexOf('id="region-sharing"') > home.indexOf('id="return-title"'));
  assert.match(home, /Hiding the toolbar keeps the region on/);
  assert.match(docs, /id="region-sharing"/);
  const data = JSON.parse(await readFile(process.env.DISTRIBUTION_METADATA_FILE ||
    new URL("../src/data/distribution.json", import.meta.url), "utf8"));
  const release = data.stable || data.preview;
  if (release && BigInt(release.build) >= 20n) {
    assert.match(docs, /Share left half or Share right half/);
    assert.match(docs, /The faint fill clears when sharing starts/);
    assert.match(docs, /The visible toolbar appears when it is inside the region/);
    assert.match(docs, /Control-2(?: \(Ctrl\+2 on Windows\))? hides drawings, covers and tools in the shared view too/);
    assert.match(docs, /keeps updating when covered/);
    assert.doesNotMatch(docs, /including your drawings and covers/);
  } else {
    assert.doesNotMatch(docs, /Control-2(?: \(Ctrl\+2 on Windows\))? hides drawings, covers and tools in the shared view too/);
  }
  if (docs.includes("Share &gt; Window &gt; Annotator - Shared Region")) {
    assert.match(docs, /Share &gt; Window &gt; Annotator - Shared Region/);
    assert.match(docs, /does not start the meeting&#39;s share, send screen pixels itself, record video or install a camera driver|does not start the meeting's share, send screen pixels itself, record video or install a camera driver/);
  }
});

test("region-sharing footage is a fresh, silent recording of the native chooser", async () => {
  const {take} = JSON.parse(await readFile(new URL("../src/data/region-sharing.json", import.meta.url), "utf8"));
  const video = await readFile(new URL(`../public${take.file}`, import.meta.url));
  const poster = await readFile(new URL(`../public${take.poster}`, import.meta.url));
  assert.equal(createHash("sha256").update(video).digest("hex"), take.sha256);
  assert.equal(take.imageWorkspace, false);
  assert.equal(take.speed, 1);
  assert.match(take.input, /Scripted native accessibility/);
  assert.deepEqual(take.actions.map(action => action.control), ["Right half", "Center third", "Top left", "Left half"]);
  const media = inspectMP4(video);
  assert.equal(media.tracks.length, 1);
  assert.equal(media.tracks[0].handler, "vide");
  assert.equal(media.tracks[0].codec, "avc1");
  assert.equal(media.tracks[0].width, take.width);
  assert.equal(media.tracks[0].height, take.height);
  assert.equal(media.fastStart, true);
  assert.ok(Math.abs(media.duration - take.duration) < 0.1);
  assert.deepEqual(jpegSize(poster), {width: take.width, height: take.height});
});

test("help headings, privacy boundaries and release links describe the current product", async () => {
  const docs = await readFile(new URL("../dist/docs/index.html", import.meta.url), "utf8");
  const privacy = await readFile(new URL("../dist/privacy/index.html", import.meta.url), "utf8");
  const ai = await readFile(new URL("../dist/ai/index.html", import.meta.url), "utf8");
  const support = await readFile(new URL("../dist/support/index.html", import.meta.url), "utf8");
  const readme = await readFile(new URL("../../README.md", import.meta.url), "utf8");
  for (const anchor of ["preview", "limitations", "permissions", "save", "shortcuts", "settings", "compatibility", "ai"]) {
    assert.match(docs, new RegExp(`id="${anchor}"`));
  }
  assert.ok(docs.indexOf('id="preview"') < docs.indexOf('id="shapes"'));
  assert.match(docs, /original pasted images/);
  assert.match(docs, /Clear drawings \(keep effects\)/);
  assert.doesNotMatch(docs, /unless you explicitly use window following|Anchored slide|Quick, Reveal|keep censors/);
  assert.match(privacy, /(?:GitHub Pages|Netlify) hosts this website/);
  assert.match(privacy, /have not confirmed/);
  assert.match(privacy, /Share usage counts is off by default/);
  assert.match(privacy, /screenshots and text to its model provider/);
  assert.doesNotMatch(privacy, /before the site goes live|Future usage and crash reporting|DEVELOPMENT POLICY/);
  assert.ok(ai.indexOf("screenshots and text") < ai.indexOf("Download the AI skill"));
  assert.ok(support.indexOf("Stop sharing first") < support.indexOf("What to include"));
  assert.match(support, /before submission/);
  assert.match(readme, /https:\/\/getannotator\.app\/downloads\//);
  assert.doesNotMatch(readme, /releases\/download\/v|Download alpha\.2|not yet been verified\. Use the ZIP/);
});

// Windows has no unzip; its built-in bsdtar reads ZIP archives with the same results.
function readZip(unzipArgs, tarArgs, options) {
  try {
    return execFileSync("unzip", unzipArgs, options);
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
    return execFileSync("tar", tarArgs, options);
  }
}

test("AI skill download contains the current canonical instructions", async () => {
  const archive = fileURLToPath(new URL("../public/downloads/annotator-skill.zip", import.meta.url));
  const contents = readZip(["-p", archive, "annotator/SKILL.md"], ["-xOf", archive, "annotator/SKILL.md"]);
  const source = await readFile(new URL("../../integrations/computer-use/annotator/SKILL.md", import.meta.url));
  assert.deepEqual(contents, source, "Rebuild the skill ZIP after editing its instructions.");
  // WIN-AI-01: the skill the site ships is the one with the Windows app's section.
  assert.match(contents.toString("utf8"), /^## On Windows$/m);
  const entries = readZip(["-Z1", archive], ["-tf", archive], { encoding: "utf8" }).trim().split(/\r?\n/);
  assert.deepEqual(entries.filter((entry) => !entry.endsWith("/")), ["annotator/SKILL.md"]);
  const page = await readFile(new URL("../dist/ai/index.html", import.meta.url), "utf8");
  assert.match(page, /downloads\/annotator-skill\.zip/);
  assert.match(page, /\/skills reload/);
});

test("rejected recuts and simulated demo frames are absent", async () => {
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  assert.doesNotMatch(home, /data-scene-cursor|data-arrow-path|data-motion-scene|data-native-feature|scene-heading|feature-(?:draw|arrow|blur)\.(?:mp4|jpg)/);
  assert.match(home, /Desktop diagram walkthrough/);
  assert.match(home, /earlier app recording/);
  const mediaFiles = await readdir(new URL("../public/demo/", import.meta.url));
  assert.equal(mediaFiles.filter(file => /^feature-(draw|arrow|blur)\./.test(file)).length, 0);
});

test("new showcase media is fresh, bounded, silent-source documented and checksum bound", async () => {
  const metadata = JSON.parse(await readFile(new URL("../src/data/showcase-takes.json", import.meta.url), "utf8"));
  assert.deepEqual(Object.keys(metadata.takes).sort(), ["arrow", "blur", "draw", "pointer", "shortcuts", "style", "tools"]);
  for (const [name, take] of Object.entries(metadata.takes)) {
    assert.equal(take.imageWorkspace, false);
    assert.equal(take.speed, 1);
    assert.deepEqual(take.encoding, {
      codec: "h264", profile: "Main", level: 32, pixelFormat: "yuv420p", frameRate: 30,
    });
    assert.match(take.input, /scripted pointer/i);
    assert.ok(take.duration > 3 && take.duration < (name === "tools" ? 25 : 18));
    const data = await readFile(new URL(`../public${take.file}`, import.meta.url));
    assert.equal(createHash("sha256").update(data).digest("hex"), take.sha256);
    assert.match(take.sourceFile, new RegExp(`^${name}-[A-F0-9-]+\\.mp4$`));
    assert.match(await readFile(new URL(`../public/demo/showcase-${name}.vtt`, import.meta.url), "utf8"), /^WEBVTT/);
    const [x, y, width, height] = take.crop;
    for (const action of take.actions) {
      if (!action.rect) continue;
      assert.ok(action.rect.x >= x && action.rect.y >= y);
      assert.ok(action.rect.x + action.rect.width <= x + width);
      assert.ok(action.rect.y + action.rect.height <= y + height);
    }
    if (name !== "pointer" && name !== "shortcuts") assert.ok(take.preparedControls.length > 0);
    if (["draw", "arrow", "blur", "style", "tools"].includes(name)) {
      assert.equal(take.toolbarAnchorStable, true);
      assert.equal(take.toolbarPropertiesMode, "full");
      assert.ok(take.toolbarBottomInset >= 80);
      assert.ok(take.height - Math.max(0, take.toolbarBottomInset - 32) >=
        take.toolbarSamples.reduce((bottom, sample) => Math.max(bottom,
          sample.rect.y + sample.rect.height - take.crop[1]), 0));
      assert.ok(take.toolbarSamples.length > 30);
      const initial = take.toolbarSamples[0].rect;
      for (const sample of take.toolbarSamples)
        for (const key of ["x", "y", "width"]) assert.ok(Math.abs(sample.rect[key] - initial[key]) <= 2);
    }
    if (name === "style") {
      const typing=take.actions.filter(action=>action.gesture==="type-character");
      assert.equal(typing.map(action=>action.text).join(""),"A useful idea.");
      assert.equal(typing.length,14);
      assert.ok(typing.at(-1).time - typing[0].time > 0.8);
    }
    if (name === "tools") assert.ok(take.actions.some(action=>action.gesture==="scroll-tools"));
    if (name === "shortcuts") {
      assert.ok(take.actions.some(action=>action.gesture==="drag-into-toolbar"));
      assert.ok(take.actions.some(action=>action.gesture==="drag-out-of-toolbar"));
      assert.ok(take.actions.some(action=>action.gesture==="type-shortcut"));
      assert.ok(take.recorded > metadata.recorded, "The toolbar clip is newer than the other showcase clips.");
    }
  }
});

test("social metadata retains the alpha indexing policy and uses configured URLs", async () => {
  const html = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  assert.match(html, /property="og:title"/);
  assert.match(html, /name="twitter:card" content="summary_large_image"/);
  assert.match(html, /noindex,nofollow/);
  const {site, base} = siteConfig();
  if (site) {
    assert.ok(html.includes(`rel="canonical" href="${site}${base}"`));
    assert.ok(html.includes(`content="${site}${base}social-card.png"`));
  }
  const png = await readFile(new URL("../public/social-card.png", import.meta.url));
  assert.equal(png.readUInt32BE(16), 1200);
  assert.equal(png.readUInt32BE(20), 630);
});

test("fresh dark-toolbar takes match their provenance and approved actions", async () => {
  const metadata = JSON.parse(await readFile(new URL("../src/data/product-takes.json", import.meta.url), "utf8"));
  assert.equal(metadata.recorded,"2026-09-24");
  for(const [name,take] of Object.entries(metadata.takes)) {
    assert.equal(take.imageWorkspace,false);
    assert.equal(take.toolbarAppearance,"dark");
    assert.equal(take.speed,1);
    assert.ok(take.toolbarClearance >= 64, "Browser controls must not cover the recorded toolbar.");
    const data = await readFile(new URL(`../public${take.file}`, import.meta.url));
    assert.equal(createHash("sha256").update(data).digest("hex"),take.sha256);
    assert.equal(data.subarray(4,8).toString(),"ftyp");
    assert.ok(take.duration > 5 && take.duration < 35);
    assert.match(take.sourceFile,new RegExp(`^${name}-[A-F0-9-]+\\.mp4$`));
    assert.match(await readFile(new URL(`../public/demo/fresh-${name}.vtt`,import.meta.url),"utf8"),/^WEBVTT/);
    const controls = take.actions.map(action=>action.control).filter(Boolean);
    for(const rejected of ["Coral","Red","Done","End arrowhead","End: Filled triangle"]) assert.ok(!controls.includes(rejected));
    const [x,y,w,h] = take.crop;
    for(const {rect} of take.actions.filter(action=>action.rect)) {
      assert.ok(rect.x >= x && rect.y >= y && rect.x+rect.width <= x+w && rect.y+rect.height <= y+h);
    }
  }
  assert.equal(metadata.takes.draw.ink,"teal");
  assert.equal(metadata.takes.arrow.ink,"blue");
  assert.equal(metadata.takes.arrow.actions.filter(action=>action.gesture==="arrow-point").length,4);
  assert.ok(metadata.takes.arrow.actions.some(action=>action.gesture==="edit-arrow-point"));
});

// Matches Astro's escaping of text and attribute values.
const escapeHTML = value => value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
  .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
const heroReel = async () => JSON.parse(await readFile(new URL("../src/data/hero-reel.json", import.meta.url), "utf8"));
const reelEncodings = reel => Object.entries({desktop: reel.desktop, phone: reel.phone}).filter(([, entry]) => entry);

test("hero reel manifest matches its encoded files and posters", async () => {
  const reel = await heroReel();
  assert.equal(reel.schemaVersion, 1);
  assert.ok(["placeholder", "final"].includes(reel.status));
  assert.equal(reel.loop, true);
  assert.equal(typeof reel.audio, "boolean");
  if (reel.audio) {
    for (const field of ["title", "artist", "source", "license"]) assert.ok(reel.music?.[field]?.trim(), `music ${field} is credited`);
    assert.match(reel.music.url, /^https:\/\//);
  } else assert.equal(reel.music, undefined, "A silent reel has no music credit.");
  assert.ok(reel.description.trim().length > 20 && reel.provenance.trim().length > 20);
  for (const [name, entry] of reelEncodings(reel)) {
    const data = await readFile(new URL(`../public${entry.file}`, import.meta.url));
    assert.equal(data.length, entry.bytes, `${name} byte count`);
    assert.equal(createHash("sha256").update(data).digest("hex"), entry.sha256, `${name} checksum`);
    const video = inspectMP4(data);
    assert.ok(video.fastStart, `${name} needs moov before mdat for fast start`);
    assert.deepEqual(video.tracks.map(track => track.handler), reel.audio ? ["vide", "soun"] : ["vide"],
      reel.audio ? `${name} has one video track and one sound track` : `${name} must be silent, with one video track`);
    assert.equal(video.tracks[0].codec, "avc1", `${name} must be H.264`);
    if (reel.audio) assert.equal(video.tracks[1].codec, "mp4a", `${name} sound must be AAC`);
    assert.deepEqual([video.tracks[0].width, video.tracks[0].height], [entry.width, entry.height]);
    assert.ok(Math.abs(video.duration - entry.duration) < 0.1, `${name} duration ${video.duration}`);
    assert.ok(entry.duration >= 18 && entry.duration <= 40, `${name} should be the 18- to 40-second reel`);
    assert.ok(entry.fps > 0 && entry.fps <= 60);
    assert.ok(entry.width % 2 === 0 && entry.height % 2 === 0);
    if (name === "desktop") assert.equal(entry.width * 9, entry.height * 16, "desktop reel is 16:9");
    else assert.equal(entry.width * 5, entry.height * 4, "phone reel is 4:5");
    const poster = jpegSize(await readFile(new URL(`../public${entry.poster}`, import.meta.url)));
    assert.deepEqual([poster.width, poster.height], [entry.width, entry.height], `${name} poster matches the video`);
  }
});

test("hero reel markup is checksum bound, starts muted and is playable without scripts", async () => {
  const reel = await heroReel();
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const prefix = siteConfig().base.replace(/\/$/, "");
  const video = home.match(/<video[^>]+data-reel-video[^>]*>[\s\S]*?<\/video>/)?.[0];
  assert.ok(video, "The homepage needs the hero reel video.");
  const tag = video.match(/<video[^>]*>/)[0];
  for (const attribute of [/\bcontrols\b/, /\bmuted\b/, /\bplaysinline\b/, /crossorigin="anonymous"/, /preload="metadata"/])
    assert.match(tag, attribute);
  assert.doesNotMatch(tag, /\b(?:autoplay|loop)\b/, "Scripts decide autoplay and looping from motion settings.");
  assert.ok(tag.includes(`aria-label="${escapeHTML(reel.description)}"`));
  assert.doesNotMatch(video, /<track\b/);
  assert.ok(home.includes(`id="reel-provenance">${escapeHTML(reel.provenance)}<`));
  const sources = [...video.matchAll(/<source([^>]*)>/g)].map(match => match[1]);
  assert.equal(sources.length, reel.phone ? 2 : 1);
  for (const [name, entry] of reelEncodings(reel)) {
    const version = entry.sha256.slice(0, 12);
    const source = sources.find(attributes => attributes.includes(`src="${prefix}${entry.file}?v=${version}"`));
    assert.ok(source, `${name} source is bound to its checksum`);
    assert.equal(/media="\(max-width: 600px\)"/.test(source), name === "phone");
    const preload = home.match(new RegExp(`<link rel="preload" as="image" href="${prefix}${entry.poster}\\?v=${version}"[^>]*>`))?.[0];
    assert.ok(preload, `${name} poster is preloaded`);
    if (reel.phone) assert.match(preload, name === "phone" ? /media="\(max-width: 600px\)"/ : /media="not all and \(max-width: 600px\)"/);
    assert.ok(home.includes(`${prefix}${entry.poster}?v=${version}`));
  }
  assert.ok(video.includes(`<a href="${prefix}${reel.desktop.file}?v=${reel.desktop.sha256.slice(0, 12)}">`), "direct fallback link");
  const sound = home.match(/<button[^>]+data-reel-sound[^>]*>/)?.[0];
  if (reel.audio) {
    assert.ok(sound, "A reel with sound has a Sound button.");
    assert.match(sound, /\bhidden\b/, "The Sound button needs scripts, so it starts hidden; native controls can unmute.");
    assert.match(sound, /aria-pressed="false"/, "The reel starts muted.");
    assert.ok(home.includes(`href="${escapeHTML(reel.music.url)}"`), "The music is credited with a link.");
  } else assert.equal(sound, undefined, "A silent reel has no Sound button.");
});

test("no website page gives a video a text track", async () => {
  const root = fileURLToPath(new URL("../dist/", import.meta.url));
  async function inspect(dir) {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const path = `${dir}/${entry.name}`;
      if (entry.isDirectory()) await inspect(path);
      else if (entry.name.endsWith(".html")) assert.doesNotMatch(await readFile(path, "utf8"), /<track\b/, path);
    }
  }
  await inspect(root);
});

test("motion styles keep content visible without scripts and never loop forever", async () => {
  const assets = fileURLToPath(new URL("../dist/_astro/", import.meta.url));
  const css = (await Promise.all((await readdir(assets)).filter(name => name.endsWith(".css"))
    .map(name => readFile(`${assets}/${name}`, "utf8")))).join("\n");
  assert.doesNotMatch(css, /\binfinite\b/, "No CSS animation may run forever.");
  const blocks = [];
  const stack = [];
  let start = 0;
  for (let index = 0; index < css.length; index++) {
    if (css[index] === "{") { stack.push(css.slice(start, index).trim()); start = index + 1; }
    else if (css[index] === "}") { const prelude = stack.pop(); blocks.push({prelude, parents: [...stack]}); start = index + 1; }
    else if (css[index] === ";") start = index + 1;
  }
  const hidden = blocks.filter(block => block.prelude.includes(":not(.is-revealed)"));
  assert.ok(hidden.length > 0, "Scroll entrances are expected on the homepage.");
  for (const block of hidden) {
    for (const selector of block.prelude.split(",")) assert.match(selector.trim(), /^\.js-motion /, selector);
    assert.ok(block.parents.some(parent => /prefers-reduced-motion:\s*no-preference/.test(parent)), block.prelude);
  }
  assert.ok(blocks.some(block => /prefers-reduced-motion:\s*reduce/.test(block.prelude)), "Reduced motion needs overrides.");
});

const stageDemos = async () => JSON.parse(await readFile(new URL("../src/data/stage-demos.json", import.meta.url), "utf8"));

test("stage demo manifest matches its encoded files, posters and native actions", async () => {
  const stage = await stageDemos();
  assert.equal(stage.schemaVersion, 1);
  assert.equal(stage.audio, false);
  assert.match(stage.input, /scripted pointer/i);
  assert.match(stage.edits, /Nothing is drawn over the recording/);
  const names = Object.keys(stage.demos);
  assert.deepEqual(names, ["circle", "arrow", "highlight", "smart", "shapes", "spotlight", "zoom", "blur"]);
  for (const [name, demo] of Object.entries(stage.demos)) {
    assert.match(demo.sourceFile, new RegExp(`^${demo.take}-[A-F0-9-]+\\.mp4$`), `${name} names its native take`);
    assert.match(demo.sourceSHA256, /^[a-f0-9]{64}$/);
    assert.ok(demo.source[1] > demo.source[0]);
    // Pointer travel and waits are shortened; the drawing itself stays close to real time.
    assert.ok(demo.speed.moving[0] >= 0.5 && demo.speed.moving[1] <= 1.5, `${name} speed ${demo.speed.moving}`);
    assert.ok(demo.actions.length > 0, `${name} shows a native action`);
    // A skip must be short and explained, and the clip's speed figures leave it out.
    for (const skip of demo.skipped) {
      assert.ok(skip.source[1] - skip.source[0] <= 0.1 && skip.reason.trim().length > 10, `${name} skip ${JSON.stringify(skip)}`);
    }
    assert.equal(demo.skipped.length, name === "zoom" ? 1 : 0);
    for (const [variant, entry] of Object.entries({desktop: demo.desktop, phone: demo.phone})) {
      const label = `${name} ${variant}`;
      const data = await readFile(new URL(`../public${entry.file}`, import.meta.url));
      assert.equal(data.length, entry.bytes, `${label} byte count`);
      assert.equal(createHash("sha256").update(data).digest("hex"), entry.sha256, `${label} checksum`);
      const video = inspectMP4(data);
      assert.ok(video.fastStart, `${label} needs moov before mdat`);
      assert.deepEqual(video.tracks.map(track => track.handler), ["vide"], `${label} is silent, with one video track`);
      assert.equal(video.tracks[0].codec, "avc1", `${label} is H.264`);
      assert.deepEqual([video.tracks[0].width, video.tracks[0].height], [entry.width, entry.height]);
      assert.deepEqual(entry.encoding, {codec: "h264", profile: "High", level: 42, pixelFormat: "yuv420p", frameRate: 60});
      assert.ok(Math.abs(video.duration - entry.duration) < 0.05, `${label} duration ${video.duration}`);
      assert.ok(entry.duration >= 3 && entry.duration <= 10, `${label} is a short clip`);
      assert.ok(entry.loopSeam < 1.5, `${label} loops without a jump`);
      assert.ok(entry.posterTime > 0 && entry.posterTime < entry.duration);
      if (variant === "desktop") assert.deepEqual([entry.width, entry.height], [1728, 1080]);
      else assert.deepEqual([entry.width, entry.height], [1080, 1350]);
      const poster = jpegSize(await readFile(new URL(`../public${entry.poster}`, import.meta.url)));
      assert.deepEqual([poster.width, poster.height], [entry.width, entry.height], `${label} poster size`);
      for (const action of demo.actions) assert.ok(action.clipTime >= 0 && action.clipTime < entry.duration, `${label} ${JSON.stringify(action)}`);
    }
    assert.equal(demo.desktop.duration, demo.phone.duration, `${name} desktop and phone clips share one timeline`);
  }
  const controls = Object.values(stage.demos).flatMap(demo => demo.actions.map(action => action.control).filter(Boolean));
  for (const rejected of ["Red", "Coral"]) assert.ok(!controls.includes(rejected));
  assert.ok(stage.demos.zoom.actions.some(action => action.control === "Screen zoom"));
  assert.ok(stage.demos.zoom.actions.some(action => action.control === "Increase live zoom"));
  assert.ok(stage.demos.smart.actions.some(action => action.gesture === "smart-rectangle"));
  assert.equal(stage.demos.smart.actions.filter(action => action.gesture === "type-character").map(action => action.text).join(""), "Make it bigger");
  assert.ok(stage.demos.shapes.actions.some(action => action.gesture === "scroll-tools"));
  assert.equal(stage.demos.arrow.actions.filter(action => action.gesture === "arrow-point").length, 3);
  assert.ok(stage.demos.arrow.actions.some(action => action.gesture === "edit-arrow-point"));
  assert.ok(stage.demos.blur.actions.some(action => action.gesture === "blur"));
});

test("hero reel chapters sit on the reel's beat grid and were checked against this encoding", async () => {
  const reel = await heroReel();
  const chapters = JSON.parse(await readFile(new URL("../src/data/hero-reel-chapters.json", import.meta.url), "utf8"));
  assert.equal(chapters.schemaVersion, 1);
  // A new reel encoding needs someone to check that its cuts still fall on these beats.
  assert.equal(chapters.checked.reel, reel.desktop.sha256.slice(0, 12),
    "The reel changed: check the chapter beats in hero-reel-chapters.json against it, then update checked.");
  const bpm = 60 / (reel.desktop.duration / chapters.beats);
  assert.ok(Math.abs(bpm - chapters.checked.bpm) < 0.5, `The reel runs at ${bpm.toFixed(2)} BPM`);
  assert.equal(chapters.chapters[0].beat, 1);
  const beats = chapters.chapters.map(chapter => chapter.beat);
  assert.deepEqual([...beats].sort((a, b) => a - b), beats);
  assert.equal(new Set(beats).size, beats.length);
  assert.ok(beats.at(-1) <= chapters.beats);
  assert.ok(chapters.chapters.filter(chapter => chapter.kind === "part").length >= 3);
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const list = home.match(/<ol[^>]+data-reel-chapters[^>]*>[\s\S]*?<\/ol>/)?.[0];
  assert.ok(list, "The reel lists its parts.");
  assert.match(list.match(/<ol[^>]*>/)[0], /\bhidden\b/, "Parts can only seek with scripts, so they start hidden.");
  const beat = reel.desktop.duration / chapters.beats;
  for (const chapter of chapters.chapters) {
    const start = chapter.beat === 1 ? 0 : Number(((chapter.beat - 1) * beat).toFixed(3));
    assert.ok(list.includes(`data-chapter="${chapter.id}" data-start="${start}"`), `${chapter.id} starts at ${start}`);
    if (chapter.kind === "part") assert.ok(list.includes(`data-chapter-start="${start}"`), `${chapter.id} can be chosen`);
  }
});

test("the tool overview names real tools from the app", async () => {
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const block = home.match(/<section class="stage-more"[\s\S]*?<\/section>/)?.[0];
  assert.ok(block, "The tool stage ends with an overview of the other tools.");
  // Tool labels, library tiles and tabs, and effect modes, as written in the app's source.
  const sources = await Promise.all(["../../apps/macos/Sources/AnnotationCore/Element.swift", "../../apps/macos/Annotator/ToolLibrary.swift",
    "../../apps/macos/Annotator/CoverPropertiesView.swift"].map(path => readFile(new URL(path, import.meta.url), "utf8")));
  const app = sources.join("\n");
  const groups = [...block.matchAll(/<h4[^>]*>([^<]+)<\/h4>\s*<ul[^>]*>([\s\S]*?)<\/ul>/g)]
    .map(([, name, list]) => ({name, tools: [...list.matchAll(/<li[^>]*>([^<]+)<\/li>/g)].map(match => match[1])}));
  assert.ok(groups.length >= 5 && groups.every(group => group.tools.length), "Each group lists tools.");
  for (const group of groups.filter(group => group.name !== "Style")) {
    for (const tool of group.tools) {
      assert.ok(app.includes(`"${tool}"`) || app.includes(`"${tool} `), `${group.name}: "${tool}" is not a tool name in the app`);
    }
  }
  const count = Number(block.match(/more than (\d+) tools/)?.[1]);
  const listed = new Set(groups.filter(group => group.name !== "Style").flatMap(group => group.tools)).size;
  assert.ok(count > 0 && listed + 8 >= count, "The tool count is backed by the listed tools and the eight clips.");
});
