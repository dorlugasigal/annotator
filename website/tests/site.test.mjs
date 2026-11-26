import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { siteConfig } from "../site-config.mjs";
import { readdir } from "node:fs/promises";
import {createHash} from "node:crypto";
import { parseWindowsListing } from "../src/lib/windows-listing.ts";
import { parseDistribution } from "../src/lib/distribution-metadata.ts";

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
  const home = decodeHTML(await readFile(new URL("../dist/index.html", import.meta.url), "utf8"));
  assert.match(home, /All tools are free/);
  assert.match(home, /with no account and nothing to pay for/);
  assert.match(home, /It's optional and it doesn't unlock anything\./);
  assert.match(home, /href="https:\/\/buymeacoffee\.com\/dorlugasigal"/);
  assert.match(home, /Coffee's optional/);
  assert.match(home, /Buy me a coffee/);
  assert.doesNotMatch(home, /[Bb]uy me a beer|[Pp]rivate preview|[Pp]rivate development/);
  assert.doesNotMatch(home, /Purchasing isn't open yet|Pro feature limits|optional paid features|action="[^"]*(?:checkout|payment)/);
  assert.doesNotMatch(home, /<iframe\b/);
  // The demo is drawn live on the page: no videos and no third-party widgets.
  assert.doesNotMatch(home, /<video\b/);
  assert.match(home, /noindex,nofollow/);
  assert.match(home, /id="main"/);
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
  const {release} = JSON.parse(await readFile(process.env.DISTRIBUTION_METADATA_FILE ||
    new URL("../src/data/distribution.json", import.meta.url), "utf8"));
  const html = await readFile(new URL("../dist/downloads/index.html", import.meta.url), "utf8");
  const home = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  if (!release) {
    assert.match(html, /This page has no verified download details/);
    assert.doesNotMatch(html, /href="[^"]*\/releases\/download\//);
  } else {
    assert.ok(html.includes(release.downloadURL));
    assert.ok(html.includes(release.sha256));
    // A release with a disk image offers it first, and keeps the ZIP and both checksums on the page.
    if (release.diskImageURL) {
      assert.ok(html.indexOf(release.diskImageURL) < html.indexOf(`>ZIP archive<`));
      assert.ok(html.includes(release.diskImageSHA256));
    }
    // One release, named by its version alone.
    assert.ok(html.includes(`<h2>Version ${release.releaseVersion}</h2>`));
    const url = new URL(release.downloadURL);
    const repository = url.pathname.split("/").slice(1, 3).join("/");
    assert.ok(html.includes(`https://github.com/${repository}/releases/tag/v${release.releaseVersion}`),
      "The download needs notes for that exact version and repository.");
  }
  // There are no release stages, tracks or build numbers to show, even for an older release with a pre-release label.
  for (const page of [home, html]) {
    assert.doesNotMatch(page, /Public alpha|Public beta|Release candidate|Testing preview|prerelease|\bStable\b|\bBuild \d/i);
  }
  // The hero's and the install section's Mac buttons offer the release's file, counted as a download, or GitHub's release
  // list while the build has no verified release.
  const downloadURL = release?.diskImageURL ?? release?.downloadURL ?? "https://github.com/dorlugasigal/annotator/releases";
  const macButtons = home.match(/<a class="btn btn-accent download-mac"[^>]*>/g) ?? [];
  assert.equal(macButtons.length, 2);
  for (const button of macButtons) {
    assert.ok(button.includes(`href="${downloadURL}"`), button);
    assert.equal(/\bdata-download\b/.test(button), Boolean(release), button);
  }
  assert.match(home, /Download for Mac/);
  assert.match(home, /<code>brew install --cask dorlugasigal\/annotator\/annotator<\/code>/);
  if (release) {
    assert.doesNotMatch(home, /not available yet|no verified download details|private preview/i);
    // The homepage names the version beside the download.
    assert.ok(home.includes(`<p class="version">Version ${release.releaseVersion} · `));
    // One command: Homebrew adds the homebrew-annotator tap by name, so there is no tap step.
    assert.doesNotMatch(html, /brew tap /);
    assert.match(html, /brew install --cask dorlugasigal\/annotator\/annotator/);
    // The old annotator-preview migration steps were removed at the owner's request.
    assert.doesNotMatch(html, /annotator-preview|migration steps/);
  } else {
    assert.doesNotMatch(html, /brew install --cask/);
    assert.doesNotMatch(home, /class="version"/);
  }
  // Requirements follow verified architectures: Intel appears only once a published release runs on it.
  const docs = await readFile(new URL("../dist/docs/index.html", import.meta.url), "utf8");
  const intel = release?.architecture === "arm64 x86_64";
  if (intel) {
    assert.match(home, /macOS 14 or later, on Apple Silicon or Intel\. It lives in your menu bar\./);
    assert.match(docs, /on a Mac with Apple Silicon or an Intel processor/);
  } else {
    assert.match(home, /macOS 14 or later, on Apple Silicon\. It lives in your menu bar\./);
    assert.doesNotMatch(home + docs, /Intel Mac|Intel processor|Apple Silicon and Intel|Apple Silicon or Intel/);
  }
  assert.equal(html.includes("on a Mac with Apple Silicon or an Intel processor"), intel);
});

test("release metadata is null or one release with a plain version, or a legacy label with its build in the file names", async () => {
  const releases = "https://github.com/dorlugasigal/annotator/releases/download";
  const plain = {
    releaseVersion: "0.2.0", downloadURL: `${releases}/v0.2.0/annotator-0.2.0-universal.zip`, sha256: "a".repeat(64),
    architecture: "arm64 x86_64", diskImageURL: `${releases}/v0.2.0/annotator-0.2.0-universal.dmg`,
    diskImageSHA256: "c".repeat(64), diskImageSize: 4427901,
  };
  const legacy = {
    ...plain, releaseVersion: "0.1.0-beta.17", downloadURL: `${releases}/v0.1.0-beta.17/annotator-0.1.0-beta.17-35-universal.zip`,
    diskImageURL: `${releases}/v0.1.0-beta.17/annotator-0.1.0-beta.17-35-universal.dmg`,
  };
  const zipOnly = {releaseVersion: "0.2.1", downloadURL: `${releases}/v0.2.1/annotator-0.2.1-arm64.zip`, sha256: "b".repeat(64)};
  assert.equal(parseDistribution({release: null}), null);
  assert.deepEqual(parseDistribution({release: plain}), {
    releaseVersion: "0.2.0", downloadURL: plain.downloadURL, sha256: plain.sha256, architecture: "arm64 x86_64",
    diskImage: {url: plain.diskImageURL, sha256: plain.diskImageSHA256, size: plain.diskImageSize},
  });
  assert.equal(parseDistribution({release: legacy}).releaseVersion, "0.1.0-beta.17");
  for (const label of ["0.1.0-alpha.2", "0.1.0-rc.1"]) {
    const name = `annotator-${label}-10-arm64.zip`;
    assert.equal(parseDistribution({release: {...zipOnly, releaseVersion: label, downloadURL: `${releases}/v${label}/${name}`}})
      .releaseVersion, label);
  }
  // Data without an architecture is from the oldest releases, which ran on Apple Silicon only.
  assert.deepEqual(parseDistribution({release: zipOnly}), {...zipOnly, architecture: "arm64", diskImage: null});
  const invalid = [
    null, [], {}, {release: undefined}, {release: []}, {release: "0.2.0"},
    // The old two-track file, and the fields that went with it.
    {stable: null, preview: null}, {release: null, preview: null}, {release: {...plain, build: "36"}}, {release: {...plain, track: "stable"}},
    ...["0.2", "v0.2.0", "0.2.0.1", "00.2.0", "0.2.0+36", "0.2.0-preview.1", "0.2.0-beta.0", "0.2.0-beta"].map(releaseVersion =>
      ({release: {...plain, releaseVersion}})),
    {release: {...plain, sha256: "A".repeat(64)}}, {release: {...plain, sha256: "a".repeat(63)}},
    {release: {...plain, architecture: "x86_64"}}, {release: {...plain, architecture: null}},
    // A plain version never names a build in its files, and a legacy label always does.
    {release: {...plain, downloadURL: `${releases}/v0.2.0/annotator-0.2.0-36-universal.zip`, diskImageURL: `${releases}/v0.2.0/annotator-0.2.0-36-universal.dmg`}},
    {release: {...legacy, downloadURL: `${releases}/v0.1.0-beta.17/annotator-0.1.0-beta.17-universal.zip`, diskImageURL: `${releases}/v0.1.0-beta.17/annotator-0.1.0-beta.17-universal.dmg`}},
    ...["0", "035", "3x", ""].map(build => ({release: {...zipOnly, releaseVersion: "0.1.0-beta.17",
      downloadURL: `${releases}/v0.1.0-beta.17/annotator-0.1.0-beta.17-${build}-arm64.zip`}})),
    // The file must be that version's archive for its architecture, in that version's GitHub release.
    ...[`${releases}/v0.2.1/annotator-0.2.0-universal.zip`, `${releases}/v0.2.0/annotator-0.2.0-arm64.zip`,
      `${releases}/v0.2.0/annotator-0.2.0-universal.dmg`, `${releases}/v0.2.0/Annotator-0.2.0-universal.zip`,
      "http://github.com/dorlugasigal/annotator/releases/download/v0.2.0/annotator-0.2.0-universal.zip",
      "https://example.com/dorlugasigal/annotator/releases/download/v0.2.0/annotator-0.2.0-universal.zip",
      "https://user:token@github.com/dorlugasigal/annotator/releases/download/v0.2.0/annotator-0.2.0-universal.zip",
      "https://github.com:443/dorlugasigal/annotator/releases/download/v0.2.0/annotator-0.2.0-universal.zip",
      `${releases}/v0.2.0/annotator-0.2.0-universal.zip?download=1`, `${releases}/v0.2.0/annotator-0.2.0-universal.zip#top`,
      "https://github.com/a/b/c/releases/download/v0.2.0/annotator-0.2.0-universal.zip",
      "https://github.com/dorlugasigal/../releases/download/v0.2.0/annotator-0.2.0-universal.zip"]
      .map(downloadURL => ({release: {...plain, downloadURL, diskImageURL: downloadURL.replace(/\.zip/, ".dmg")}})),
    // A disk image comes with its checksum and size, next to the same release's ZIP.
    {release: {...zipOnly, diskImageURL: `${releases}/v0.2.1/annotator-0.2.1-arm64.dmg`}},
    {release: {...plain, diskImageURL: `${releases}/v0.2.0/annotator-0.2.0-arm64.dmg`}},
    {release: {...plain, diskImageURL: `${releases}/v0.1.0/annotator-0.2.0-universal.dmg`}},
    {release: {...plain, diskImageURL: `${plain.diskImageURL}?x=1`}}, {release: {...plain, diskImageURL: null}},
    {release: {...plain, diskImageSHA256: "c".repeat(65)}},
    ...[0, -1, 1.5, "4427901"].map(diskImageSize => ({release: {...plain, diskImageSize}})),
  ];
  for (const value of invalid) assert.throws(() => parseDistribution(value), undefined, JSON.stringify(value));
  // The committed file is regenerated when the website is published, and every fixture is one valid release.
  parseDistribution(JSON.parse(await readFile(new URL("../src/data/distribution.json", import.meta.url), "utf8")));
  for (const name of ["distribution", "published-zip", "published-legacy"]) {
    assert.ok(parseDistribution(JSON.parse(await readFile(new URL(`./fixtures/${name}.json`, import.meta.url), "utf8"))), name);
  }
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
  // The Store links are text buttons and links, with no Store badge artwork and no download count.
  const storeLinks = home.match(/<a[^>]*href="https:\/\/apps\.microsoft\.com\/[^"]*"[^>]*>/g) ?? [];
  assert.equal(storeLinks.length, 3, "The hero's link for Mac visitors, its button for Windows visitors and the install card");
  for (const link of storeLinks) {
    assert.ok(link.includes(`href="${listing.listingURL}"`));
    assert.doesNotMatch(link, /data-download\b/, "The counting service counts only Mac downloads.");
  }
  assert.doesNotMatch(home, /<img[^>]*(?:badge|microsoft)/i);
  // The hero offers the visitor's system first: without scripts, the Mac's; the page's head picks the Windows one.
  const macHero = home.indexOf('<p class="get" data-for="mac">'), windowsHero = home.indexOf('<p class="get" data-for="windows">');
  assert.ok(macHero > 0 && macHero < windowsHero);
  assert.match(home.slice(windowsHero), /^<p class="get" data-for="windows">\s*<a class="btn btn-accent store-link"[^>]*>Get it for Windows/);
  assert.match(home, /It's free, for Mac and Windows\./);
  // Both systems have a card in Get Annotator, Mac first; each install command stays with its system.
  const macCard = home.indexOf('data-os="mac"'), windowsCard = home.indexOf('data-os="windows"');
  assert.ok(macCard > 0 && macCard < windowsCard);
  const commands = [...home.matchAll(/<div class="command">[\s\S]*?<code>([\s\S]*?)<\/code>/g)].map(match => textContent(match[1]));
  assert.deepEqual(commands, ["brew install --cask dorlugasigal/annotator/annotator", listing.wingetCommand]);
  assert.match(home, /Windows 11, version 22H2 or later\. It lives in the system tray, near the clock\./);
  // The cards' keys for Windows visitors.
  assert.match(home, /aria-label="Control 1"><kbd>Ctrl<\/kbd><kbd>1<\/kbd>/);
  assert.match(home, /aria-label="Control L"><kbd>Ctrl<\/kbd><kbd>L<\/kbd>/);
  assert.match(home, /aria-label="Control 3"><kbd>Ctrl<\/kbd><kbd>3<\/kbd>/);
  assert.match(home, /for macOS 14 and later and Windows 11\./);
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
  assert.match(privacy, /<h1[^>]*>What stays on your computer\.<\/h1>/);
  assert.match(privacy, /PNG export saves an image on your computer\./);
  assert.match(privacy, /Annotator for Windows doesn't send usage counts yet\. Share usage counts stays off and says counts aren't available\./);
  assert.match(privacy, /Partner Center, we can see how many people got and installed Annotator/);
  assert.match(privacy, /Settings > Privacy & security > Diagnostics & feedback/);
  assert.match(privacy, /A crash report can include part of Annotator's memory/);
  assert.match(privacy, /On a Mac, app errors use local system logs/);
  assert.match(privacy, /href="https:\/\/www\.microsoft\.com\/en-us\/privacy\/privacystatement"/);
});


test("Help and Privacy explain region sharing as it works now", async () => {
  const docs = await readFile(new URL("../dist/docs/index.html", import.meta.url), "utf8");
  assert.match(docs, /id="region-sharing"/);
  // Help and Privacy describe region sharing as it works now, whatever release the site offers.
  assert.match(docs, /Share left half or Share right half/);
  assert.match(docs, /The faint fill clears when sharing starts/);
  assert.match(docs, /The visible toolbar appears when it is inside the region/);
  assert.match(docs, /Control-2(?: \(Ctrl\+2 on Windows\))? hides drawings, covers and tools in the shared view too/);
  assert.match(docs, /keeps updating when covered/);
  assert.match(docs, /<dd>Zoom, live pixel effects and region sharing need to see your screen\./);
  assert.doesNotMatch(docs, /including your drawings and covers|needs build \d+/);
  assert.match(docs, /Share &gt; Window &gt; Annotator - Shared Region/);
  assert.match(docs, /does not start the meeting&#39;s share, send screen pixels itself, record video or install a camera driver|does not start the meeting's share, send screen pixels itself, record video or install a camera driver/);
  const privacy = await readFile(new URL("../dist/privacy/index.html", import.meta.url), "utf8");
  assert.match(privacy, /Zoom, live pixel effects and region sharing need Screen Recording permission/);
  assert.match(privacy, /Region sharing composes a chosen desktop area into a local window\./);
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

test("social metadata keeps the noindex policy and uses configured URLs", async () => {
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

// The built style sheets as rules: each with its selector, the at-rules around it and its declarations.
async function builtRules() {
  const assets = fileURLToPath(new URL("../dist/_astro/", import.meta.url));
  const css = (await Promise.all((await readdir(assets)).filter(name => name.endsWith(".css"))
    .map(name => readFile(`${assets}/${name}`, "utf8")))).join("\n");
  const rules = [], stack = [];
  let start = 0;
  for (let index = 0; index < css.length; index++) {
    if (css[index] === "{") { stack.push(css.slice(start, index).trim()); start = index + 1; }
    else if (css[index] === "}") {
      const prelude = stack.pop();
      rules.push({prelude, parents: [...stack], body: css.slice(start, index)});
      start = index + 1;
    }
  }
  return {css, rules: rules.filter(rule => !rule.prelude.startsWith("@") && !/^(?:from|to|\d)/.test(rule.prelude))};
}

test("motion is only for visitors who allow it, keeps content visible without scripts, and loops only where it pauses", async () => {
  const {rules} = await builtRules();
  // Words that rise, groups that arrive, the cup and the pinned deck start hidden or moved only under .motion, which the
  // page's head sets when scripts run and reduced motion is off.
  const staged = rules.filter(rule => /(?:^|[\s,])(?:\[data-rise\] \.w|\.depth>\*|\.depth > \*|\.cup\.awaits|\.steam path|\.do-track|\.card\b)/.test(rule.prelude) &&
    /opacity:\s*0(?:[;\s]|$)|position:\s*sticky|height:\s*calc/.test(rule.body));
  assert.ok(staged.length >= 4, "The homepage's entrances are expected.");
  for (const rule of staged) for (const selector of rule.prelude.split(",")) assert.match(selector.trim(), /^\.motion\b/, selector);
  // Reduced motion: no transitions at all.
  assert.ok(rules.some(rule => /html:not\(\.motion\) \*/.test(rule.prelude) && /transition-duration:\s*0s\s*!important/.test(rule.body)));
  // A loop runs only under .motion, and only where a script pauses or ends it off screen: the live dots and the steam.
  const loops = rules.filter(rule => /\binfinite\b/.test(rule.body));
  assert.ok(loops.length > 0);
  for (const rule of loops) {
    for (const selector of rule.prelude.split(",")) assert.match(selector.trim(), /^\.motion (?:\.nav-try i:after|\.nav-try i::after|\.stage-title i:after|\.stage-title i::after|\.coffee\.is-live \.steam path)/, selector);
  }
  assert.ok(rules.some(rule => /\.hero\.is-away \.stage-title i/.test(rule.prelude) && /paused/.test(rule.body)), "The stage's live dot rests off screen.");
});

test("the accent is the app's Aurora gradient, with words and rings that keep AA contrast", async () => {
  const site = await readFile(new URL("../src/styles/site.css", import.meta.url), "utf8");
  const core = await readFile(new URL("../src/engine/annotation-core.js", import.meta.url), "utf8");
  const token = (name, scope = site) => scope.match(new RegExp(`${name}:\\s*(#[0-9a-f]{6})`, "i"))?.[1];
  const hex = value => [1, 3, 5].map(i => parseInt(value.slice(i, i + 2), 16));
  const linear = v => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
  const luminance = c => 0.2126 * linear(c[0]) + 0.7152 * linear(c[1]) + 0.0722 * linear(c[2]);
  const contrast = (a, b) => { const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05); };
  // The stops are the engine's Aurora preset, which is the app's GradientPreset.aurora.
  const aurora = core.match(/preset\("Aurora", (\[[^\]]+\]), (\[[^\]]+\]), (\[[^\]]+\])\)/).slice(1).map(part => JSON.parse(part).map(v => Math.round(v * 255)));
  const stops = ["--aurora-a", "--aurora-b", "--aurora-c"].map(name => hex(token(name)));
  assert.deepEqual(stops, aurora);
  const words = hex(token("--on-accent"));
  for (let i = 0; i < stops.length - 1; i++) for (let k = 0; k <= 10; k++) {
    const color = stops[i].map((v, c) => v + (stops[i + 1][c] - v) * k / 10);
    assert.ok(contrast(color, words) >= 4.5, `Words on the gradient at stop ${i} + ${k / 10}`);
  }
  const light = site.slice(site.indexOf('html[data-theme="light"]'));
  const dark = site.slice(0, site.indexOf('html[data-theme="light"]'));
  assert.ok(contrast(stops[1], hex(token("--page", dark))) >= 4.5, "The flat accent on the dark page");
  assert.ok(contrast(hex(token("--accent", light)), hex(token("--page", light))) >= 4.5, "The flat accent on the light page");
  const home = await readFile(new URL("../src/scripts/home.js", import.meta.url), "utf8");
  assert.match(home, /const accent = "aurora";/);
});

test("the drawing engine still matches the app's conformance vectors", () => {
  for (const name of ["test-core", "test-motion", "test-recognition"]) {
    const output = execFileSync(process.execPath, [fileURLToPath(new URL(`../src/engine/${name}.mjs`, import.meta.url))], {encoding: "utf8"});
    assert.match(output, /All \d+ vectors pass\./, name);
  }
});

test("nothing on the site loads from another origin", async () => {
  const root = fileURLToPath(new URL("../dist/", import.meta.url));
  async function inspect(dir) {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const path = `${dir}/${entry.name}`;
      if (entry.isDirectory()) { if (path !== `${root}/admin`) await inspect(path); continue; }
      if (entry.name.endsWith(".html")) {
        const html = await readFile(path, "utf8");
        // Links may lead away; nothing the page loads may.
        assert.doesNotMatch(html, /<(?:script|img|iframe|video|audio|source)\b[^>]*\bsrc="(?:https?:)?\/\//, path);
        assert.doesNotMatch(html, /<link\b(?![^>]*rel="canonical")[^>]*href="(?:https?:)?\/\//, path);
      }
      if (entry.name.endsWith(".css")) assert.doesNotMatch(await readFile(path, "utf8"), /url\(["']?(?:https?:)?\/\//, path);
      if (entry.name.endsWith(".js")) assert.doesNotMatch(await readFile(path, "utf8"), /\bfetch\(\s*["'`]https?:|\bimport\(\s*["'`]https?:/, path);
    }
  }
  await inspect(root);
});
