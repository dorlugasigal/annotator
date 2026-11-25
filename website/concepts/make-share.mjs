#!/usr/bin/env node
// Packs the website concepts into ONE HTML file that opens by double-click (file://, no server, no internet).
// Run from the repository root:  node website/concepts/make-share.mjs [--out <file>] [--verbose]
//
// How the file works:
// - The gallery is the first view. Each concept page is written into an empty <iframe> with document.write,
//   below a slim bar. That keeps the page in the same origin as the file, so pictures and video stay usable
//   in WebGL, and "#install" links scroll inside the page. (A blob: or srcdoc document does not: see the notes
//   near mount() below.)
// - Every binary asset is stored once as base64 and turned into a blob: address the first time it is needed.
// - A small script at the top of each concept page maps the addresses the page was written for
//   (/public/demo/x.mp4) to those blob: addresses.
// Nothing is hard-coded: concepts come from the gallery's cards, and files from what each page really refers to.
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, statSync, writeFileSync } from "node:fs";
import { basename, dirname, extname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const SELF = fileURLToPath(import.meta.url);
const CONCEPTS_DIR = dirname(SELF);
const SITE_DIR = resolve(CONCEPTS_DIR, "..");
const SITE = "https://getannotator.app";
const GALLERY = "/concepts/index.html";
const DEFAULT_OUT = join(CONCEPTS_DIR, "dist", "annotator-website-concepts.html");
const CACHE_DIR = join(CONCEPTS_DIR, "dist", ".cache");
const TARGET_MB = 25, LIMIT_MB = 35;
// Preview sizes for the recordings. Change these if the file gets too big; the cache follows them.
const VIDEO = { wide: 1280, tall: 720, clipCrf: 27, longCrf: 29, longSeconds: 15, longFps: 30, audioKbps: 80, preset: "slow" };
// Marks a place in stored text that the viewer fills in when it opens a page. These two characters are from the
// private-use range, so they do not appear in real source files.
const OPEN = "\uE000", CLOSE = "\uE001";
const mark = (kind, value = "") => `${OPEN}${kind}:${value}${CLOSE}`;
const NOTE = "These are design options for the Annotator website. Everything runs offline inside this one file: open a design, scroll it and draw on it. It is best viewed on a computer.";

const TYPES = {
  ".mp4": "video/mp4", ".webm": "video/webm", ".mov": "video/quicktime", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png",
  ".gif": "image/gif", ".webp": "image/webp", ".avif": "image/avif", ".svg": "image/svg+xml", ".ico": "image/x-icon", ".woff2": "font/woff2",
  ".woff": "font/woff", ".ttf": "font/ttf", ".otf": "font/otf", ".vtt": "text/vtt", ".json": "application/json", ".glb": "model/gltf-binary",
  ".gltf": "model/gltf+json", ".bin": "application/octet-stream", ".hdr": "application/octet-stream", ".ktx2": "image/ktx2",
  ".wasm": "application/wasm", ".mp3": "audio/mpeg", ".wav": "audio/wav", ".ogg": "audio/ogg", ".m4a": "audio/mp4", ".txt": "text/plain",
};
const typeOf = (path) => TYPES[extname(path).toLowerCase()];

// ---------------------------------------------------------------------------------------------------------------
// Small helpers

const warnings = [];
const warn = (text) => { if (!warnings.includes(text)) { warnings.push(text); console.warn(`  ! ${text}`); } };
const fail = (text) => { const error = new Error(text); error.plain = true; return error; };
const fileOf = (path) => {
  const file = resolve(SITE_DIR, `.${path}`);
  if (file !== SITE_DIR && !file.startsWith(SITE_DIR + sep)) throw fail(`${path} points outside the website folder.`);
  return file;
};
const stats = new Map();
const statOf = (path) => {
  if (!stats.has(path)) { let found = null; try { found = statSync(fileOf(path)); } catch { /* not there */ } stats.set(path, found); }
  return stats.get(path);
};
const isFile = (path) => Boolean(statOf(path)?.isFile());
const isDir = (path) => Boolean(statOf(path)?.isDirectory());
const readText = (path) => {
  const text = readFileSync(fileOf(path), "utf8");
  if (text.includes(OPEN) || text.includes(CLOSE)) throw fail(`${path} uses a character this tool keeps for itself (U+E000 or U+E001).`);
  return text;
};
const sha = (...parts) => { const hash = createHash("sha256"); for (const part of parts) hash.update(part); return hash.digest("hex"); };
const megabytes = (bytes) => `${(bytes / 1048576).toFixed(2)} MB`;
const decodeEntities = (text) => text.replace(/&(amp|quot|#39|#x27|apos|lt|gt);/g, (_, name) => ({ amp: "&", quot: '"', "#39": "'", "#x27": "'", apos: "'", lt: "<", gt: ">" })[name]);
const escapeAttribute = (text) => text.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;");
const escapeHtml = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

// Where an address in a page leads: nowhere to follow (null), another website, or a path on this website.
function resolveRef(reference, base) {
  const value = String(reference).trim();
  if (!value || /^(?:#|data:|blob:|javascript:|about:|mailto:|tel:|sms:)/i.test(value)) return null;
  let url;
  try { url = new URL(value, SITE + base); } catch { return null; }
  if (url.origin !== SITE) return { external: url.href };
  let path = url.pathname;
  try { path = decodeURIComponent(path); } catch { /* keep it as written */ }
  return { path, search: url.search, hash: url.hash };
}

// The page a path opens: a packed concept page, the gallery, or nothing.
function pageOf(path) {
  let page = path.endsWith("/") ? `${path}index.html` : path;
  if (isDir(page)) page += "/index.html";
  if (!page.startsWith("/concepts/") || !page.endsWith(".html") || !isFile(page)) return null;
  return page;
}

// ---------------------------------------------------------------------------------------------------------------
// Reading HTML: enough of a tokenizer to find start tags, their attributes and the raw text of scripts and styles.

function scanHtml(html) {
  const tags = [], length = html.length, space = /\s/;
  let at = 0;
  while (at < length) {
    const open = html.indexOf("<", at);
    if (open < 0) break;
    if (html.startsWith("<!--", open)) { const end = html.indexOf("-->", open + 4); at = end < 0 ? length : end + 3; continue; }
    const head = /^<(\/?)([a-zA-Z][^\s/>]*)/.exec(html.slice(open, open + 64));
    if (!head) { at = open + 1; continue; }
    const tag = { name: head[2].toLowerCase(), closing: head[1] === "/", attrs: [], start: open, nameEnd: open + head[0].length, end: length };
    let p = tag.nameEnd;
    while (p < length) {
      while (p < length && (space.test(html[p]) || html[p] === "/")) p++;
      if (p >= length || html[p] === ">") break;
      const start = p;
      while (p < length && !space.test(html[p]) && html[p] !== "=" && html[p] !== ">" && html[p] !== "/") p++;
      const name = html.slice(start, p).toLowerCase();
      let q = p, raw = null;
      while (q < length && space.test(html[q])) q++;
      if (html[q] === "=") {
        q++;
        while (q < length && space.test(html[q])) q++;
        if (html[q] === '"' || html[q] === "'") {
          const close = html.indexOf(html[q], q + 1), stop = close < 0 ? length : close;
          raw = html.slice(q + 1, stop); p = Math.min(length, stop + 1);
        } else {
          p = q;
          while (p < length && !space.test(html[p]) && html[p] !== ">") p++;
          raw = html.slice(q, p);
        }
      }
      if (name) tag.attrs.push({ name, value: raw === null ? "" : decodeEntities(raw), start, end: p });
      else p++;
    }
    tag.end = Math.min(length, p + 1);
    tags.push(tag);
    at = tag.end;
    if (!tag.closing && (tag.name === "script" || tag.name === "style" || tag.name === "textarea" || tag.name === "title")) {
      const close = new RegExp(`</${tag.name}[\\s>]`, "gi");
      close.lastIndex = at;
      const found = close.exec(html);
      tag.contentStart = at; tag.contentEnd = found ? found.index : length;
      at = tag.contentEnd;
    }
  }
  return tags;
}
const attribute = (tag, name) => tag.attrs.find((each) => each.name === name);
const valueOf = (tag, name) => attribute(tag, name)?.value;
function applyEdits(text, edits) {
  let out = text;
  for (const [start, end, replacement] of edits.sort((a, b) => b[0] - a[0] || b[1] - a[1])) out = out.slice(0, start) + replacement + out.slice(end);
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// What goes into the file

const assets = new Map();   // site path -> { file, critical }, binary files stored as base64
const styles = new Map();   // site path -> CSS text with marks
const scripts = new Map();  // site path -> classic script text
const modules = new Map();  // module id -> { code, deps, dynamic, source }
const pages = new Map();    // site path -> HTML with marks
const importMap = new Map();
const moduleQueue = [];
const concepts = [];
let verbose = false;

function addAsset(path, critical = false) {
  const known = assets.get(path);
  if (known) { known.critical ||= critical; return; }
  assets.set(path, { critical });
}

// CSS: every url() that is a file on this website becomes a mark.
function packCss(css, base, where, critical = false) {
  if (/@import\b/.test(css)) warn(`${where} uses @import. That is not packed; put the rules in the file itself.`);
  return css.replace(/url\(\s*(?:"([^"]*)"|'([^']*)'|([^)"'\s]*))\s*\)/g, (all, double, single, bare) => {
    const reference = double ?? single ?? bare, found = resolveRef(reference, base);
    if (!found) return all;
    if (found.external) { warn(`${where} loads ${found.external} from the internet. It will not work offline.`); return all; }
    if (!isFile(found.path)) { warn(`${where} refers to ${reference}, which was not found.`); return all; }
    addAsset(found.path, critical);
    return `url("${mark("A", found.path)}")`;
  });
}
function addStyle(path) {
  if (styles.has(path)) return;
  styles.set(path, "");
  styles.set(path, packCss(readText(path), path, path).replace(/<\/(style)/gi, "<\\/$1"));
}

// A page that calls location.reload() would load this whole file inside its own frame, so the call goes to the viewer.
const packScript = (code) => code.replace(/(?<![.\w$])(?:(?:window|self|globalThis|document)\s*\.\s*)?location\s*\.\s*reload\s*\(/g, "__shareReload(");
function addScript(path) {
  if (!scripts.has(path)) scripts.set(path, packScript(readText(path)).replace(/<\/(script)/gi, "<\\/$1"));
}
function addModule(id, source, base) {
  if (modules.has(id)) return;
  modules.set(id, { source: source ?? readText(id), base: base ?? id, code: "", deps: [], dynamic: [] });
  moduleQueue.push(id);
}

// ---------------------------------------------------------------------------------------------------------------
// Modules. Node's own parser lists the static imports, so nothing is guessed; a pattern only finds where they are.

function parseImports(sources) {
  const run = spawnSync(process.execPath, ["--experimental-vm-modules", "--disable-warning=ExperimentalWarning", SELF, "--scan-imports"],
    { input: JSON.stringify(sources), encoding: "utf8", maxBuffer: 256 * 1048576 });
  if (run.status !== 0 || !run.stdout) throw fail(`Could not read the scripts' imports with Node.${run.stderr ? `\n${run.stderr.trim()}` : ""}`);
  return JSON.parse(run.stdout);
}
async function scanImportsFromStdin() {
  const vm = await import("node:vm");
  let input = "";
  for await (const chunk of process.stdin) input += chunk;
  const out = {};
  for (const [id, source] of Object.entries(JSON.parse(input))) {
    try {
      const parsed = new vm.SourceTextModule(source, { identifier: id });
      out[id] = { specifiers: [...new Set((parsed.moduleRequests?.map((request) => request.specifier)) ?? parsed.dependencySpecifiers)] };
    } catch (error) { out[id] = { error: String(error?.message || error) }; }
  }
  process.stdout.write(JSON.stringify(out));
}
function resolveSpecifier(specifier, base, where) {
  let target = specifier;
  if (importMap.has(specifier)) target = importMap.get(specifier);
  else {
    const prefix = [...importMap.keys()].filter((key) => key.endsWith("/") && specifier.startsWith(key)).sort((a, b) => b.length - a.length)[0];
    if (prefix) target = importMap.get(prefix) + specifier.slice(prefix.length);
    else if (!/^(?:\/|\.\/|\.\.\/)/.test(specifier) && !/^[a-z][a-z0-9+.-]*:/i.test(specifier)) throw fail(`${where} imports "${specifier}", and no page gives that name an address in an import map.`);
  }
  const found = resolveRef(target, base);
  if (!found || found.external) throw fail(`${where} imports "${specifier}" from the internet. It cannot be packed.`);
  if (!isFile(found.path)) throw fail(`${where} imports "${specifier}", but ${found.path} was not found.`);
  return found.path;
}
const escapeRegExp = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
function packModules() {
  while (moduleQueue.length) {
    const batch = moduleQueue.splice(0), parsed = parseImports(Object.fromEntries(batch.map((id) => [id, modules.get(id).source])));
    for (const id of batch) {
      const entry = modules.get(id), result = parsed[id];
      if (result.error) throw fail(`${id} could not be read as a module: ${result.error}`);
      let code = entry.source;
      for (const specifier of result.specifiers) {
        const target = resolveSpecifier(specifier, entry.base, id);
        // "from 'x'" or a bare "import 'x'", with either quote.
        const place = new RegExp(`((?:\\bfrom|(?<![.\\w$])import)\\s*)(["'])${escapeRegExp(specifier)}\\2`, "g");
        let count = 0;
        code = code.replace(place, (_, head) => { count++; return `${head}"${mark("M", target)}"`; });
        if (!count) throw fail(`${id} imports "${specifier}", but the import was not found in its text.`);
        if (!entry.deps.includes(target)) entry.deps.push(target);
        addModule(target);
      }
      code = code.replace(/(?<![.\w$])import\s*\(\s*(["'`])([^"'`\n$]+)\1\s*\)/g, (_, quote, specifier) => {
        const target = resolveSpecifier(specifier, entry.base, id);
        if (!entry.dynamic.includes(target)) entry.dynamic.push(target);
        addModule(target);
        return `__shareImport(${JSON.stringify(target)})`;
      });
      if (/(?<![.\w$])import\s*\(/.test(code)) warn(`${id} has an import() with a worked-out address. That module cannot be packed.`);
      entry.code = packScript(code);
    }
  }
  // A blob: module can only name modules that already have an address, so a loop of static imports cannot be built.
  const state = new Map();
  const visit = (id, trail) => {
    if (state.get(id) === "done") return;
    if (state.get(id) === "open") throw fail(`These modules import each other in a loop, which cannot be packed: ${[...trail, id].join(" -> ")}`);
    state.set(id, "open");
    for (const dep of modules.get(id).deps) visit(dep, [...trail, id]);
    state.set(id, "done");
  };
  for (const id of modules.keys()) visit(id, []);
}

// ---------------------------------------------------------------------------------------------------------------
// Pages

const RESOURCE_ATTRIBUTES = {
  src: ["img", "video", "audio", "source", "track", "embed", "input"], poster: ["video"], data: ["object"],
  href: ["image", "use", "feimage"], "xlink:href": ["image", "use", "feimage"],
};
const DROPPED_LINKS = ["icon", "shortcut", "apple-touch-icon", "mask-icon", "preload", "modulepreload", "prefetch", "preconnect", "dns-prefetch", "manifest"];

function packPage(page, concept) {
  if (pages.has(page)) return;
  pages.set(page, "");
  concept.pages.push(page);
  const html = readText(page), tags = scanHtml(html), edits = [];
  concept.own.push(html);
  let shimAt = -1, inlineModules = 0;
  const closeOf = (index) => (tags[index + 1]?.closing && tags[index + 1].name === tags[index].name ? tags[index + 1].end : tags[index].contentEnd);
  const setAttribute = (tag, name, value) => {
    const found = attribute(tag, name), text = `${name}="${escapeAttribute(value)}"`;
    if (found) edits.push([found.start, found.end, text]); else edits.push([tag.nameEnd, tag.nameEnd, ` ${text}`]);
  };
  const packReference = (tag, name, reference) => {
    const found = resolveRef(reference, page);
    if (!found) return reference;
    if (found.external) { warn(`${page} loads ${found.external} from the internet. It will not work offline.`); return reference; }
    if (!isFile(found.path)) { warn(`${page} refers to ${reference} in <${tag.name} ${name}>, which was not found.`); return reference; }
    addAsset(found.path);
    return mark("A", found.path);
  };

  tags.forEach((tag, index) => {
    if (tag.closing) return;
    if (tag.name === "head" && shimAt < 0) shimAt = tag.end;
    if (tag.name === "base") warn(`${page} has a <base> tag. Relative addresses may not be packed correctly.`);
    if (tag.name === "iframe" && valueOf(tag, "src")) warn(`${page} has an <iframe>. Its content is not packed.`);

    if (tag.name === "script") {
      const type = (valueOf(tag, "type") || "").trim().toLowerCase(), source = attribute(tag, "src"), content = html.slice(tag.contentStart, tag.contentEnd);
      if (type === "importmap") {
        let map = {};
        try { map = JSON.parse(content).imports || {}; } catch { warn(`${page} has an import map that could not be read.`); }
        for (const [name, address] of Object.entries(map)) {
          const found = resolveRef(address, page), target = found?.path ? found.path : address;
          if (importMap.has(name) && importMap.get(name) !== target) throw fail(`Two pages give "${name}" different addresses in their import maps.`);
          importMap.set(name, target);
        }
        edits.push([tag.start, closeOf(index), ""]);
      } else if (type === "module") {
        let id;
        if (source) {
          const found = resolveRef(source.value, page);
          if (!found?.path || !isFile(found.path)) { warn(`${page} loads the module ${source.value}, which was not found.`); return; }
          id = found.path;
          addModule(id);
        } else {
          id = `${page}#module-${++inlineModules}`;
          addModule(id, content, page);
          concept.own.push(content);
        }
        concept.entries.push(id);
        edits.push([tag.start, closeOf(index), `<script type="module" src="${mark("M", id)}"></script>`]);
      } else if (!type || /^(?:text|application)\/(?:java|ecma)script$/.test(type)) {
        if (source) {
          const found = resolveRef(source.value, page);
          if (found?.external) { warn(`${page} loads ${found.external} from the internet. It will not work offline.`); return; }
          if (!found?.path || !isFile(found.path)) { warn(`${page} loads the script ${source.value}, which was not found.`); return; }
          addScript(found.path);
          concept.scripts.push(found.path);
          // A script that waits (defer, async) keeps its timing by loading from an address; the others run in place.
          const waits = attribute(tag, "defer") ? " defer" : attribute(tag, "async") ? " async" : "";
          edits.push([tag.start, closeOf(index), waits ? `<script src="${mark("S", found.path)}"${waits}></script>`
            : `<script data-share-src="${escapeAttribute(source.value)}">${mark("J", found.path)}</script>`]);
        } else {
          concept.own.push(content);
          const packed = packScript(content);
          if (packed !== content) edits.push([tag.contentStart, tag.contentEnd, packed]);
        }
      }
      return;
    }

    if (tag.name === "style") {
      const content = html.slice(tag.contentStart, tag.contentEnd), packed = packCss(content, page, page);
      if (packed !== content) edits.push([tag.contentStart, tag.contentEnd, packed]);
      return;
    }

    if (tag.name === "link") {
      const rel = (valueOf(tag, "rel") || "").toLowerCase().split(/\s+/), href = valueOf(tag, "href");
      if (rel.includes("stylesheet") && href) {
        const found = resolveRef(href, page);
        if (found?.external) { warn(`${page} loads ${found.external} from the internet. It will not work offline.`); return; }
        if (!found?.path || !isFile(found.path)) { warn(`${page} links the stylesheet ${href}, which was not found.`); return; }
        addStyle(found.path);
        const media = valueOf(tag, "media");
        edits.push([tag.start, tag.end, `<style data-share-href="${escapeAttribute(href)}"${media ? ` media="${escapeAttribute(media)}"` : ""}>${mark("C", found.path)}</style>`]);
      } else if (rel.some((each) => DROPPED_LINKS.includes(each))) edits.push([tag.start, tag.end, ""]);
      return;
    }

    if (tag.name === "a" || tag.name === "area") {
      const href = valueOf(tag, "href"), found = href == null ? null : resolveRef(href, page);
      if (!found) return;
      const next = found.path && pageOf(found.path);
      if (next) {
        // A link to another page of the same concept: pack that page too. The click is handled when the page runs.
        if (next.startsWith(`/concepts/${concept.folder}/`)) packPage(next, concept);
        return;
      }
      // Anything else is the real website or another website, and opens in a new tab.
      if (found.path) setAttribute(tag, "href", SITE + encodeURI(found.path) + found.search + found.hash);
      setAttribute(tag, "target", "_blank");
      const rel = new Set((valueOf(tag, "rel") || "").split(/\s+/).filter(Boolean));
      rel.add("noopener");
      setAttribute(tag, "rel", [...rel].join(" "));
      return;
    }

    for (const each of tag.attrs) {
      if (each.name === "style") {
        if (!each.value.includes("url(")) continue;
        const packed = packCss(each.value, page, page);
        if (packed !== each.value) setAttribute(tag, "style", packed);
      } else if (RESOURCE_ATTRIBUTES[each.name]?.includes(tag.name)) {
        const packed = packReference(tag, each.name, each.value);
        if (packed !== each.value) setAttribute(tag, each.name, packed);
      } else if ((each.name === "srcset" || each.name === "imagesrcset") && each.value.trim()) {
        const packed = each.value.split(",").map((part) => {
          const [reference, ...rest] = part.trim().split(/\s+/);
          return reference ? [packReference(tag, each.name, reference), ...rest].join(" ") : "";
        }).filter(Boolean).join(", ");
        if (packed !== each.value) setAttribute(tag, each.name, packed);
      } else if (each.name.startsWith("data-") && typeOf(each.value.trim())) {
        // A file named in a data attribute is most likely loaded later by script.
        const found = resolveRef(each.value, page);
        if (found?.path && isFile(found.path)) addAsset(found.path);
      }
    }
  });

  const shim = mark("SHIM");
  if (shimAt >= 0) edits.push([shimAt, shimAt, shim]);
  else { const start = /^\s*<!doctype[^>]*>/i.exec(html)?.[0].length ?? 0; edits.push([start, start, shim]); }
  pages.set(page, applyEdits(html, edits));
}

// ---------------------------------------------------------------------------------------------------------------
// Files that scripts ask for at run time. Scripts build addresses like `/public/demo/stage-${name}.mp4`, so the exact
// list cannot be read from the text. This finds each address start, lists the files that begin that way, and keeps
// the ones whose remaining name is made of words the concept's own pages and scripts contain.

function packScriptAssets(concept) {
  const seen = new Set(), sources = [];
  const collect = (id) => {
    if (seen.has(id)) return;
    seen.add(id);
    const entry = modules.get(id);
    sources.push({ text: entry.source, base: entry.base });
    if (id.startsWith(`/concepts/${concept.folder}/`) && !id.includes("#")) concept.own.push(entry.source);
    for (const dep of [...entry.deps, ...entry.dynamic]) collect(dep);
  };
  concept.entries.forEach(collect);
  for (const path of concept.scripts) { const text = readText(path); sources.push({ text, base: path }); concept.own.push(text); }
  for (const text of concept.own) sources.push({ text, base: `/concepts/${concept.folder}/index.html` });

  // Words of two letters or more (or one digit); a lone "-" or a one-letter name would let any file name through.
  const words = new Set((concept.own.join("\n").match(/[A-Za-z0-9_-]+/g) || []).filter((word) => /^\d$/.test(word) || (word.length > 1 && /[A-Za-z0-9]/.test(word))));
  const madeOfWords = (text, parts = 3) => {
    if (!text) return true;
    if (!parts) return false;
    for (let cut = text.length; cut > 0; cut--) if (words.has(text.slice(0, cut)) && madeOfWords(text.slice(cut), parts - 1)) return true;
    return false;
  };
  const home = `/concepts/${concept.folder}/`;
  for (const { text, base } of sources) {
    for (const [, quote, start, stop] of text.matchAll(/(["'`])((?:\.{0,2}\/)?[\w@%+~./-]*)(\$\{|["'`])/g)) {
      if (!start) continue;
      const open = stop === "${" || stop !== quote;
      for (const from of new Set([home, base])) {
        const found = resolveRef(start, from);
        if (!found?.path) continue;
        if (!open && typeOf(found.path) && isFile(found.path)) { addAsset(found.path); continue; }
        // An address that goes on after this text. Only a start that names a folder below the site root counts.
        if (!start.includes("/") || (!open && !start.startsWith("/"))) continue;
        const folder = found.path.slice(0, found.path.lastIndexOf("/") + 1), stem = found.path.slice(folder.length);
        if (folder === "/" || !isDir(folder)) continue;
        for (const name of readdirSync(fileOf(folder))) {
          const type = extname(name), path = folder + name;
          if (!name.startsWith(stem) || !typeOf(name) || assets.has(path) || !isFile(path)) continue;
          if (!words.has(type.slice(1)) || !madeOfWords(name.slice(stem.length, name.length - type.length))) continue;
          addAsset(path);
          concept.guessed.push(path);
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// The gallery

function packGallery() {
  if (!isFile(GALLERY)) throw fail("website/concepts/index.html was not found.");
  const html = readText(GALLERY), tags = scanHtml(html), edits = [];
  let css = "", title = "Annotator website: design options", language = "en", bodyStart = 0, bodyEnd = html.length, noteAt = -1, icon = "";
  tags.forEach((tag, index) => {
    if (tag.name === "body") { if (tag.closing) bodyEnd = tag.start; else bodyStart = tag.end; return; }
    if (tag.closing) { if (tag.name === "h1" && noteAt < 0) noteAt = tag.end; return; }
    if (tag.name === "html") language = valueOf(tag, "lang") || language;
    if (tag.name === "title") title = html.slice(tag.contentStart, tag.contentEnd).trim();
    if (tag.name === "style") css += packCss(html.slice(tag.contentStart, tag.contentEnd), GALLERY, GALLERY, true);
    if (tag.name === "link" && (valueOf(tag, "rel") || "").toLowerCase().includes("icon")) {
      const found = resolveRef(valueOf(tag, "href") || "", GALLERY);
      if (found?.path && isFile(found.path) && typeOf(found.path)) icon = `data:${typeOf(found.path)};base64,${readFileSync(fileOf(found.path)).toString("base64")}`;
    }
    if (tag.name === "img") {
      const source = valueOf(tag, "src"), found = source ? resolveRef(source, GALLERY) : null;
      if (!found?.path) return;
      if (!isFile(found.path)) { warn(`The gallery picture ${source} was not found. Its card has no picture.`); edits.push([tag.start, tag.end, ""]); return; }
      addAsset(found.path, true);
      const place = attribute(tag, "src");
      edits.push([place.start, place.end, `data-share-src="${escapeAttribute(found.path)}"`]);
    }
    if (tag.name === "a") {
      const href = valueOf(tag, "href"), found = href ? resolveRef(href, GALLERY) : null;
      const folder = found?.path && /^\/concepts\/([^/]+)\/(?:index\.html)?$/.exec(found.path)?.[1];
      if (!folder || !isDir(`/concepts/${folder}`)) return;
      const close = tags.findIndex((each, at) => at > index && each.closing && each.name === "a");
      if (!isFile(`/concepts/${folder}/index.html`)) {
        warn(`${folder} has no index.html yet. It is left out, and its card is removed.`);
        edits.push([tag.start, close < 0 ? tag.end : tags[close].end, ""]);
        return;
      }
      if (concepts.some((each) => each.folder === folder)) return;
      const heading = tags.findIndex((each, at) => at > index && (close < 0 || at < close) && !each.closing && /^h[1-6]$/.test(each.name));
      const label = heading < 0 ? "" : decodeEntities(html.slice(tags[heading].end, tags[heading + 1].start).replace(/<[^>]*>/g, "")).trim();
      concepts.push({ folder, name: label || folder, index: `/concepts/${folder}/index.html`, pages: [], entries: [], scripts: [], own: [], guessed: [] });
      const place = attribute(tag, "href");
      edits.push([place.start, place.end, `href="#${escapeAttribute(folder)}" data-share-open="${escapeAttribute(folder)}"`]);
    }
  });
  const note = `\n  <p class="share-note">${escapeHtml(NOTE)}</p>`;
  edits.push(noteAt >= bodyStart && noteAt <= bodyEnd ? [noteAt, noteAt, note] : [bodyStart, bodyStart, note]);
  const inBody = edits.filter(([start, end]) => start >= bodyStart && end <= bodyEnd).map(([start, end, text]) => [start - bodyStart, end - bodyStart, text]);
  return { css, title, language, icon, body: applyEdits(html.slice(bodyStart, bodyEnd), inBody) };
}

// ---------------------------------------------------------------------------------------------------------------
// Recordings: smaller copies for a preview, kept in a cache named after the source and the settings.

let ffmpegPath = null;
function findFfmpeg() {
  if (ffmpegPath) return ffmpegPath;
  for (const candidate of [process.env.FFMPEG, "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "ffmpeg"].filter(Boolean)) {
    if (spawnSync(candidate, ["-version"], { encoding: "utf8" }).status === 0) return (ffmpegPath = candidate);
  }
  throw fail("ffmpeg was not found. Install it (for example: brew install ffmpeg) or set FFMPEG=/path/to/ffmpeg, then run this again.");
}
function probeVideo(file) {
  const ffmpeg = findFfmpeg(), sibling = join(dirname(ffmpeg), "ffprobe"), ffprobe = ffmpeg.includes(sep) && existsSync(sibling) ? sibling : "ffprobe";
  const run = spawnSync(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", file], { encoding: "utf8" });
  if (run.status !== 0) throw fail(`ffprobe could not read ${file}.${run.stderr ? ` ${run.stderr.trim()}` : ""}`);
  const info = JSON.parse(run.stdout), picture = info.streams.find((stream) => stream.codec_type === "video");
  if (!picture) throw fail(`${file} has no picture track.`);
  const [frames, per] = String(picture.r_frame_rate || "30/1").split("/").map(Number);
  return { width: picture.width, height: picture.height, fps: frames / (per || 1), seconds: Number(info.format.duration) || 0, sound: info.streams.some((stream) => stream.codec_type === "audio") };
}
function packVideo(path) {
  const file = fileOf(path), source = readFileSync(file), settings = JSON.stringify(VIDEO);
  const cached = join(CACHE_DIR, `${basename(path, extname(path))}-${sha(source, settings).slice(0, 16)}.mp4`);
  if (existsSync(cached)) return { bytes: readFileSync(cached), fresh: false };
  const info = probeVideo(file), long = info.seconds > VIDEO.longSeconds;
  const width = Math.min(info.width, info.width >= info.height ? VIDEO.wide : VIDEO.tall) & ~1;
  const filters = [`scale=${width}:-2:flags=lanczos`, ...(long && info.fps > VIDEO.longFps ? [`fps=${VIDEO.longFps}`] : [])];
  mkdirSync(CACHE_DIR, { recursive: true });
  const part = `${cached}.part.mp4`;
  const run = spawnSync(findFfmpeg(), ["-v", "error", "-y", "-i", file, "-map", "0:v:0", ...(info.sound ? ["-map", "0:a:0"] : []), "-vf", filters.join(","),
    "-c:v", "libx264", "-preset", VIDEO.preset, "-crf", String(long ? VIDEO.longCrf : VIDEO.clipCrf), "-pix_fmt", "yuv420p",
    ...(info.sound ? ["-c:a", "aac", "-b:a", `${VIDEO.audioKbps}k`, "-ac", "2"] : ["-an"]),
    "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-movflags", "+faststart", part], { encoding: "utf8" });
  if (run.status !== 0) throw fail(`ffmpeg could not make a preview of ${path}.${run.stderr ? `\n${run.stderr.trim()}` : ""}`);
  let bytes = readFileSync(part);
  // If the preview is not smaller, the original goes in.
  if (bytes.length >= source.length) { bytes = source; writeFileSync(part, source); }
  renameSync(part, cached);
  return { bytes, fresh: true };
}

// ---------------------------------------------------------------------------------------------------------------
// The code that runs in the browser. These two functions are never called here: their text is written into the file.

/* eslint-disable no-var, prefer-arrow-callback */

// Runs at the top of every concept page, before the page's own scripts.
function conceptShim(config) {
  var shell = window.parent.__annotatorShare, PAGE = config.page, SITE = config.site, pageUrl = SITE + PAGE;
  var shellFolder = String(window.parent.location.href).split(/[?#]/)[0].replace(/[^/]*$/, "");
  var warned = Object.create(null);
  window.__annotatorSharePage = PAGE;

  function missing(path) {
    if (warned[path]) return;
    warned[path] = true;
    console.warn("[Annotator designs] Missing asset: " + path + " is not packed in this file. " + PAGE + " asked for it.");
  }
  // The place on the website that an address means, or null when it is not on the website.
  function locate(value) {
    if (value == null) return null;
    var text = String(value).trim(), url;
    if (!text || /^(?:#|blob:|data:|javascript:|about:|mailto:|tel:|sms:)/i.test(text)) return null;
    try { url = new URL(text, pageUrl); } catch (error) { return null; }
    if (url.protocol === "file:") {
      // The page worked the address out against this file's own location.
      try {
        url = url.href.indexOf(shellFolder) === 0 ? new URL(url.href.slice(shellFolder.length), pageUrl)
          : new URL(url.pathname.replace(/^\/[A-Za-z]:/, "") + url.search + url.hash, SITE);
      } catch (error) { return null; }
    }
    return url.origin === SITE ? url : null;
  }
  function pathOf(url) { try { return decodeURIComponent(url.pathname); } catch (error) { return url.pathname; } }
  // The packed address for a website address. Anything else comes back unchanged.
  function packed(value, quiet, asModule) {
    var url = locate(value);
    if (!url) return value;
    var path = pathOf(url), address = shell.fileUrl(path, asModule);
    if (address) return address;
    if (!quiet) missing(path);
    return value;
  }
  function packedSet(value, quiet) {
    return String(value).split(",").map(function (part) {
      var pieces = part.trim().split(/\s+/);
      if (pieces[0]) pieces[0] = packed(pieces[0], quiet);
      return pieces.join(" ");
    }).join(", ");
  }
  function packedCss(value, quiet) {
    if (typeof value !== "string" || value.indexOf("url(") < 0) return value;
    return value.replace(/url\(\s*(?:"([^"]*)"|'([^']*)'|([^)"'\s]+))\s*\)/g, function (all, double, single, bare) {
      var reference = double != null ? double : single != null ? single : bare, address = packed(reference, quiet);
      return address === reference ? all : 'url("' + address + '")';
    });
  }
  function packedHtml(html) {
    if (typeof html !== "string" || !/(?:src|poster|href|data)\s*=|url\(/i.test(html)) return html;
    return packedCss(html.replace(/(\s(?:src|poster|data|href|xlink:href)\s*=\s*)(?:"([^"]*)"|'([^']*)')/gi, function (all, head, double, single) {
      var reference = double != null ? double : single, address = packed(reference, true);
      return address === reference ? all : head + '"' + address + '"';
    }), true);
  }
  // What the page would have read back on the website.
  function original(value, absolute) {
    if (typeof value !== "string" || value.indexOf("blob:") !== 0) return value;
    var path = shell.pathOf(value);
    return path ? (absolute ? SITE + path : path) : value;
  }

  // Storage: the page gets its own, kept in memory by the viewer, so nothing throws and nothing is left on the computer.
  function storage(data) {
    var api = {
      getItem: function (key) { key = String(key); return key in data ? data[key] : null; },
      setItem: function (key, value) { data[String(key)] = String(value); },
      removeItem: function (key) { delete data[String(key)]; },
      clear: function () { Object.keys(data).forEach(function (key) { delete data[key]; }); },
      key: function (index) { var keys = Object.keys(data); return index < keys.length ? keys[index] : null; },
    };
    Object.defineProperty(api, "length", { get: function () { return Object.keys(data).length; } });
    return new Proxy(api, {
      get: function (target, key) { return key in target ? target[key] : typeof key === "string" && key in data ? data[key] : undefined; },
      set: function (target, key, value) { data[String(key)] = String(value); return true; },
      has: function (target, key) { return key in target || key in data; },
      deleteProperty: function (target, key) { delete data[key]; return true; },
    });
  }
  var stores = shell.storage(PAGE.split("/")[2]);
  ["localStorage", "sessionStorage"].forEach(function (name) {
    var store = storage(stores[name]);
    try { Object.defineProperty(window, name, { configurable: true, get: function () { return store; } }); } catch (error) { /* the real one stays */ }
  });

  // Copying: try the browser's way, then the old way, and say so when neither works so the page can tell the visitor.
  function copyByHand(text) {
    var area = document.createElement("textarea"), active = document.activeElement, done = false;
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.cssText = "position:fixed;left:-9999px;top:0;opacity:0";
    (document.body || document.documentElement).appendChild(area);
    try { area.select(); done = document.execCommand("copy"); } catch (error) { done = false; }
    area.remove();
    try { if (active && active.focus) active.focus({ preventScroll: true }); } catch (error) { /* nothing to restore */ }
    return done;
  }
  (function () {
    var clipboard = navigator.clipboard, real = clipboard && clipboard.writeText ? clipboard.writeText.bind(clipboard) : null;
    function writeText(text) {
      var attempt;
      text = String(text);
      try { attempt = real ? real(text) : Promise.reject(); } catch (error) { attempt = Promise.reject(); }
      return attempt.catch(function () {
        if (!copyByHand(text)) throw new DOMException("Copying is not available here.", "NotAllowedError");
      });
    }
    try {
      if (clipboard) clipboard.writeText = writeText;
      else Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: writeText, readText: function () { return Promise.reject(new DOMException("Reading the clipboard is not available here.", "NotAllowedError")); } } });
    } catch (error) { /* the page's own fallback still works */ }
  })();
  ["pushState", "replaceState"].forEach(function (name) {
    var real = history[name];
    history[name] = function () { try { return real.apply(this, arguments); } catch (error) { return undefined; } };
  });

  // Attributes and properties that hold an address.
  var getAttribute = Element.prototype.getAttribute, setAttribute = Element.prototype.setAttribute, setAttributeNS = Element.prototype.setAttributeNS;
  var ADDRESS = { src: 1, poster: 1, href: 1, "xlink:href": 1, data: 1, srcset: 1, imagesrcset: 1, style: 1 };
  var LOADS = { IMG: 1, VIDEO: 1, AUDIO: 1, SOURCE: 1, TRACK: 1, EMBED: 1, INPUT: 1, SCRIPT: 1, OBJECT: 1, LINK: 1, image: 1, feImage: 1, use: 1 };
  function forAttribute(element, name, value, quiet) {
    var tag = element.tagName;
    if (name === "style") return packedCss(value, quiet);
    if (name === "srcset" || name === "imagesrcset") return packedSet(value, quiet);
    if ((name === "href" || name === "xlink:href") && tag !== "image" && tag !== "use" && tag !== "feImage"
      && !(tag === "LINK" && /stylesheet|icon|preload/i.test(element.rel || ""))) return value;
    if (name === "data" && tag !== "OBJECT") return value;
    if (name === "src" && (tag === "IFRAME" || tag === "FRAME")) return value;
    return packed(value, quiet, tag === "SCRIPT" && element.type === "module");
  }
  Element.prototype.setAttribute = function (name, value) {
    var key = typeof name === "string" ? name.toLowerCase() : name;
    if (ADDRESS[key] === 1 && value != null) value = forAttribute(this, key, String(value), false);
    return setAttribute.call(this, name, value);
  };
  Element.prototype.setAttributeNS = function (namespace, name, value) {
    var key = typeof name === "string" ? name.toLowerCase() : name;
    if (ADDRESS[key] === 1 && value != null) value = forAttribute(this, key, String(value), false);
    return setAttributeNS.call(this, namespace, name, value);
  };
  Element.prototype.getAttribute = function (name) {
    var value = getAttribute.call(this, name);
    return value !== null && ADDRESS[name] === 1 && name !== "style" ? original(value, false) : value;
  };
  function patch(type, property, convert, readOnly) {
    var owner = window[type] && window[type].prototype, found = owner && Object.getOwnPropertyDescriptor(owner, property);
    if (!found || !found.get) return;
    Object.defineProperty(owner, property, {
      configurable: true, enumerable: found.enumerable,
      get: function () { return original(found.get.call(this), true); },
      set: readOnly || !found.set ? found.set : function (value) { found.set.call(this, convert.call(this, value)); },
    });
  }
  var one = function (value) { return packed(value, false); }, many = function (value) { return packedSet(value, false); };
  [["HTMLImageElement", "src"], ["HTMLSourceElement", "src"], ["HTMLMediaElement", "src"], ["HTMLVideoElement", "poster"], ["HTMLTrackElement", "src"],
    ["HTMLEmbedElement", "src"], ["HTMLObjectElement", "data"], ["HTMLInputElement", "src"], ["HTMLLinkElement", "href"]].forEach(function (each) { patch(each[0], each[1], one); });
  patch("HTMLScriptElement", "src", function (value) { return packed(value, false, this.type === "module"); });
  patch("HTMLImageElement", "srcset", many);
  patch("HTMLSourceElement", "srcset", many);
  patch("HTMLImageElement", "currentSrc", null, true);
  patch("HTMLMediaElement", "currentSrc", null, true);
  patch("SVGAnimatedString", "baseVal", function (value) { return packed(value, true); });
  if (window.Audio) {
    var RealAudio = window.Audio;
    window.Audio = function Audio(source) { var audio = new RealAudio(); if (arguments.length) audio.src = source; return audio; };
    window.Audio.prototype = RealAudio.prototype;
  }

  // Markup written as text.
  ["innerHTML", "outerHTML"].forEach(function (property) {
    var found = Object.getOwnPropertyDescriptor(Element.prototype, property);
    if (!found || !found.set) return;
    Object.defineProperty(Element.prototype, property, { configurable: true, enumerable: found.enumerable, get: found.get, set: function (html) { found.set.call(this, packedHtml(html)); } });
  });
  var insertAdjacentHTML = Element.prototype.insertAdjacentHTML;
  Element.prototype.insertAdjacentHTML = function (where, html) { return insertAdjacentHTML.call(this, where, packedHtml(html)); };

  // Styles set from script.
  (function () {
    var owner = window.CSSStyleDeclaration && CSSStyleDeclaration.prototype;
    if (!owner) return;
    var setProperty = owner.setProperty;
    owner.setProperty = function (name, value, priority) { return setProperty.call(this, name, packedCss(value, false), priority == null ? "" : priority); };
    var names = ["cssText", "background", "backgroundImage", "background-image", "borderImage", "border-image", "borderImageSource", "border-image-source", "listStyle", "list-style",
      "listStyleImage", "list-style-image", "content", "cursor", "mask", "maskImage", "mask-image", "webkitMask", "webkitMaskImage", "-webkit-mask", "-webkit-mask-image",
      "filter", "clipPath", "clip-path", "shapeOutside", "shape-outside", "src"];
    for (var chain = document.documentElement.style; chain && chain !== Object.prototype; chain = Object.getPrototypeOf(chain)) {
      names.forEach(function (name) {
        var found = Object.getOwnPropertyDescriptor(chain, name);
        if (!found || !found.set || !found.configurable) return;
        Object.defineProperty(chain, name, { configurable: true, enumerable: found.enumerable, get: found.get, set: function (value) { found.set.call(this, packedCss(value, false)); } });
      });
    }
    var sheet = window.CSSStyleSheet && CSSStyleSheet.prototype;
    ["insertRule", "replaceSync", "replace"].forEach(function (name) {
      var real = sheet && sheet[name];
      if (real) sheet[name] = function (text) { var list = Array.prototype.slice.call(arguments); list[0] = packedCss(text, false); return real.apply(this, list); };
    });
    if (window.FontFace) {
      var RealFontFace = window.FontFace;
      window.FontFace = function FontFace(family, source, descriptors) { return new RealFontFace(family, typeof source === "string" ? packedCss(source, false) : source, descriptors); };
      window.FontFace.prototype = RealFontFace.prototype;
    }
  })();

  // Requests made from script. A packed file is answered straight from memory; a missing one gets a 404, not an error.
  var realFetch = window.fetch;
  if (realFetch) window.fetch = function (input, init) {
    var url = locate(typeof input === "string" ? input : input && input.url ? input.url : input);
    if (!url) return realFetch.apply(this, arguments);
    var path = pathOf(url), blob = shell.blobFor(path);
    if (blob) return Promise.resolve(new Response(blob, { status: 200, statusText: "OK", headers: { "content-type": blob.type || "application/octet-stream", "content-length": String(blob.size) } }));
    missing(path);
    return Promise.resolve(new Response("Not found", { status: 404, statusText: "Not Found", headers: { "content-type": "text/plain" } }));
  };
  var open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, address) {
    var list = Array.prototype.slice.call(arguments);
    list[1] = packed(address, false);
    return open.apply(this, list);
  };
  var realOpen = window.open;
  window.open = function (address) {
    var list = Array.prototype.slice.call(arguments), url = locate(address);
    if (url) { var page = shell.route(pathOf(url)); if (page !== null) { shell.go(page, url.hash); return null; } list[0] = url.href; }
    return realOpen.apply(window, list);
  };

  // A safety net for what the patches above cannot see: markup made by the parser, and styles set through properties.
  function mend(element, name) {
    var value = getAttribute.call(element, name);
    if (!value || value.indexOf("blob:") === 0) return;
    var next = forAttribute(element, name, value, LOADS[element.tagName] !== 1 && name !== "style");
    if (next !== value) setAttribute.call(element, name, next);
  }
  function mendLink(link) {
    var href = getAttribute.call(link, "href"), url = href && href.charAt(0) !== "#" ? locate(href) : null;
    if (!url || shell.route(pathOf(url)) !== null || shell.fileUrl(pathOf(url))) return;
    // A link to the real website: give it the full address and a new tab.
    if (href !== url.href) setAttribute.call(link, "href", url.href);
    if (link.target !== "_blank") link.target = "_blank";
    if (!/\bnoopener\b/.test(link.rel || "")) link.rel = ((link.rel || "") + " noopener").trim();
  }
  function mendElement(element) {
    var tag = element.tagName;
    if (tag === "STYLE") { var css = element.textContent, next = packedCss(css, false); if (next !== css) element.textContent = next; return; }
    if (tag === "A" || tag === "AREA") { mendLink(element); if (element.hasAttribute("style")) mend(element, "style"); return; }
    var attributes = element.attributes;
    for (var index = attributes.length - 1; index >= 0; index--) { var name = attributes[index].name; if (ADDRESS[name] === 1) mend(element, name); }
  }
  new MutationObserver(function (records) {
    for (var index = 0; index < records.length; index++) {
      var record = records[index];
      if (record.type === "attributes") { if (record.target.tagName === "A" || record.target.tagName === "AREA") mendLink(record.target); else mend(record.target, record.attributeName); continue; }
      if (record.type === "characterData") { var holder = record.target.parentNode; if (holder && holder.tagName === "STYLE") mendElement(holder); continue; }
      for (var each = 0; each < record.addedNodes.length; each++) {
        var node = record.addedNodes[each];
        if (node.nodeType === 3) { if (node.parentNode && node.parentNode.tagName === "STYLE") mendElement(node.parentNode); continue; }
        if (node.nodeType !== 1) continue;
        mendElement(node);
        var inside = node.querySelectorAll('[src],[poster],[srcset],[style*="url("],a[href],area[href],link[href],image,use,object[data],style');
        for (var at = 0; at < inside.length; at++) mendElement(inside[at]);
      }
    }
  }).observe(document, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ["src", "poster", "href", "xlink:href", "srcset", "data", "style"] });

  // Links. In-page links move inside this page, links to packed pages open in the viewer, the rest is the real website.
  function follow(event) {
    if (event.defaultPrevented || (event.type === "auxclick" && event.button !== 1) || (event.type === "click" && event.button)) return;
    var link = event.target && event.target.closest ? event.target.closest("a[href], area[href]") : null, href = link && getAttribute.call(link, "href");
    if (href == null) return;
    href = href.trim();
    if (href.charAt(0) === "#") {
      // replace(): the move does not add a step to the browser's Back button.
      event.preventDefault();
      try { location.replace(href); } catch (error) { var target = document.getElementById(decodeURIComponent(href.slice(1))); if (target) target.scrollIntoView(); }
      return;
    }
    var url = locate(href);
    if (!url) return;
    var page = shell.route(pathOf(url));
    if (page === PAGE) { event.preventDefault(); try { location.replace(url.hash || "#"); } catch (error) { /* stay here */ } return; }
    if (page !== null) { event.preventDefault(); shell.go(page, url.hash); return; }
    if (link.target === "_blank" && /^https?:/i.test(href)) return;
    event.preventDefault();
    window.open(url.href, "_blank", "noopener");
  }
  window.addEventListener("click", follow);
  window.addEventListener("auxclick", follow);

  window.__shareReload = function () { shell.reload(); };
  window.__shareImport = function (id) {
    var address = shell.fileUrl(id, true);
    if (!address) { missing(id); return Promise.reject(new Error("The module " + id + " is not packed in this file.")); }
    return import(address);
  };
}

// Runs once in the file itself: the gallery, the bar, and the store that the concept pages read from.
function shellRuntime(shimSource) {
  var doc = document, root = doc.documentElement;
  var index = JSON.parse(doc.getElementById("share-index").textContent), concepts = index.concepts, assets = index.assets;
  var TOKEN = /\uE000([A-Z]+):([^\uE001]*)\uE001/g;
  var texts = null, bytes = {}, addresses = {}, origins = {}, textAddresses = {}, stores = {}, waiting = [], parsed = false;
  var gallery, viewer, stage, nameLabel, countLabel, frame = null, current = null, galleryScroll = 0, serial = 0, recoveries = 0;

  // If this file ends up inside its own frame (a page moved the frame to it), tell the outer copy to put the page back.
  if (window !== window.top) { try { window.parent.postMessage({ annotatorShare: "frame-moved" }, "*"); } catch (error) { /* not ours */ } }

  function decode(text) {
    if (Uint8Array.fromBase64) return Uint8Array.fromBase64(text);
    var binary = atob(text), out = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
    return out;
  }
  function assetBlob(path) {
    var entry = assets[path];
    if (!entry) return null;
    if (!bytes[entry[0]]) {
      var holder = doc.getElementById("share-blob-" + entry[0]);
      if (!holder) return null;
      bytes[entry[0]] = decode(holder.textContent);
      holder.textContent = "";
    }
    return new Blob([bytes[entry[0]]], { type: entry[1] });
  }
  function remember(path, blob) {
    var address = URL.createObjectURL(blob);
    origins[address] = path;
    return address;
  }
  function assetUrl(path) {
    if (addresses[path]) return addresses[path];
    var blob = assetBlob(path);
    return blob ? (addresses[path] = remember(path, blob)) : null;
  }
  function store() {
    if (!texts) { var holder = doc.getElementById("share-texts"); if (!holder) return null; texts = JSON.parse(holder.textContent); holder.textContent = ""; }
    return texts;
  }
  function fill(text, page) {
    return text.replace(TOKEN, function (all, kind, value) {
      if (kind === "A") return assetUrl(value) || value;
      if (kind === "C") return fill(texts.css[value], page);
      if (kind === "J") return texts.js[value];
      if (kind === "S") return textUrl(value);
      if (kind === "M") return moduleUrl(value);
      if (kind === "SHIM") return "<script>(" + shimSource + ")(" + JSON.stringify({ page: page, site: index.site }).replace(/</g, "\\u003c") + ");<\/script>";
      return all;
    });
  }
  function moduleUrl(id) {
    if (textAddresses["m:" + id]) return textAddresses["m:" + id];
    var code = store().modules[id];
    if (code == null) return null;
    return (textAddresses["m:" + id] = remember(id, new Blob([fill(code), "\n//# sourceURL=annotator-designs://" + id], { type: "text/javascript" })));
  }
  function textBlob(path) {
    var all = store();
    if (!all) return null;
    if (all.css[path] != null) return new Blob([fill(all.css[path])], { type: "text/css" });
    if (all.js[path] != null) return new Blob([all.js[path]], { type: "text/javascript" });
    if (all.modules[path] != null) return new Blob([fill(all.modules[path])], { type: "text/javascript" });
    if (all.pages[path] != null) return new Blob([fill(all.pages[path], path)], { type: "text/html" });
    return null;
  }
  function textUrl(path) {
    if (textAddresses["t:" + path]) return textAddresses["t:" + path];
    var blob = textBlob(path);
    return blob ? (textAddresses["t:" + path] = remember(path, blob)) : null;
  }
  function fileUrl(path, asModule) {
    if (assets[path]) return assetUrl(path);
    var all = store();
    if (!all) return null;
    return all.modules[path] != null && (asModule || all.js[path] == null) ? moduleUrl(path) : textUrl(path);
  }
  function route(path) {
    var all = store(), page = /\/$/.test(path) ? path + "index.html" : path;
    if (all && all.pages[page] != null) return page;
    if (all && all.pages[page + "/index.html"] != null) return page + "/index.html";
    return page === index.gallery || page + "/index.html" === index.gallery ? "" : null;
  }
  function conceptAt(page) {
    for (var i = 0; i < concepts.length; i++) if (page.indexOf("/concepts/" + concepts[i].folder + "/") === 0) return i;
    return -1;
  }
  function whenParsed(run) { if (parsed) run(); else waiting.push(run); }

  // A concept page is written into an empty frame. That frame shares this file's origin and address, so:
  // pictures and video from this file can be drawn in WebGL, "#install" moves inside the page, and the page can ask
  // this script for files. A blob: document gets a different origin when the file is opened from disk in Chrome, and a
  // srcdoc document sends "#install" links to this file's own address.
  function mount(page, hash) {
    unmount();
    var element = doc.createElement("iframe"), mine = ++serial, written;
    element.className = "share-frame";
    element.title = concepts[current.index].name + " design";
    element.setAttribute("allow", "autoplay; fullscreen; clipboard-write");
    stage.appendChild(element);
    frame = element;
    // A second document in the frame means a page left for another address. Put the page back.
    element.addEventListener("load", function () {
      if (mine !== serial) return;
      var ours = false;
      try { ours = element.contentWindow.__annotatorSharePage === page; } catch (error) { ours = false; }
      if (!ours) recover(); else if (hash && hash.length > 1) settle(element, hash);
    });
    written = element.contentDocument;
    written.open();
    written.write(fill(store().pages[page], page));
    written.close();
    if (hash && hash.length > 1) { try { element.contentWindow.location.replace(hash); } catch (error) { /* settle() scrolls instead */ } }
  }
  function settle(element, hash) {
    try {
      var target = element.contentDocument.getElementById(decodeURIComponent(hash.slice(1)));
      if (target) target.scrollIntoView();
    } catch (error) { /* the page moved on */ }
  }
  function unmount() {
    serial++;
    if (frame) frame.remove();
    frame = null;
  }
  function recover() {
    if (!current || ++recoveries > 3) return;
    setTimeout(function () { if (current) mount(current.page, ""); }, 0);
  }

  function render(target, focus) {
    recoveries = 0;
    var at = target ? conceptAt(target.page) : -1;
    if (at < 0 || !store() || store().pages[target.page] == null) {
      unmount();
      viewer.hidden = true; gallery.hidden = false;
      root.classList.remove("share-viewing");
      doc.title = index.title;
      if (current) window.scrollTo(0, galleryScroll);
      current = null;
      return;
    }
    if (!current) galleryScroll = window.pageYOffset;
    current = { index: at, page: target.page };
    gallery.hidden = true; viewer.hidden = false;
    root.classList.add("share-viewing");
    nameLabel.textContent = concepts[at].name;
    countLabel.textContent = (at + 1) + " of " + concepts.length;
    doc.title = concepts[at].name + " · " + index.title;
    mount(target.page, target.hash || "");
    if (focus && frame) { try { frame.focus({ preventScroll: true }); } catch (error) { /* focus stays where it was */ } }
  }
  function go(page, hash, focus) {
    whenParsed(function () {
      var target = page ? { page: page, hash: hash || "" } : null;
      try { history.pushState({ share: target }, ""); } catch (error) { /* Back will not step through designs */ }
      render(target, focus);
    });
  }
  function step(by) {
    if (!current) return;
    var next = (current.index + by + concepts.length) % concepts.length;
    go(concepts[next].index, "");
  }

  // "#h-studio" at the end of the file's address opens that design. The name is then taken off the address: a concept
  // frame takes this file's address, and its own "#" links start from it.
  function openNamed() {
    var name = "", target = null;
    try { name = decodeURIComponent(location.hash.slice(1)); } catch (error) { name = ""; }
    for (var i = 0; i < concepts.length; i++) if (concepts[i].folder === name) target = { page: concepts[i].index, hash: "" };
    if (!target) return;
    try { history.replaceState({ share: target }, "", location.href.split("#")[0]); } catch (error) { /* the address stays */ }
    render(target, false);
  }

  window.__annotatorShare = {
    fileUrl: fileUrl,
    blobFor: function (path) { return assets[path] ? assetBlob(path) : textBlob(path); },
    pathOf: function (address) { return origins[address] || null; },
    route: route,
    // Called from inside a concept page. The move waits a tick so that it runs from this file's own script: a frame
    // that is written from the script of a frame being removed comes out empty.
    go: function (page, hash) { setTimeout(function () { go(page, hash, true); }, 0); },
    reload: function () { if (current) { var page = current.page; setTimeout(function () { if (current && current.page === page) mount(page, ""); }, 0); } },
    storage: function (folder) { return stores[folder] || (stores[folder] = { localStorage: Object.create(null), sessionStorage: Object.create(null) }); },
    // The gallery's own styles and pictures. They sit near the top of the file, so this can run before the rest is read.
    writeStyles: function () { doc.write("<style>" + fill(index.css) + "<\/style>"); },
    showGallery: function () {
      gallery = doc.getElementById("share-gallery"); viewer = doc.getElementById("share-viewer"); stage = doc.getElementById("share-stage");
      nameLabel = doc.getElementById("share-name"); countLabel = doc.getElementById("share-count");
      Array.prototype.forEach.call(gallery.querySelectorAll("img[data-share-src]"), function (image) { var address = assetUrl(image.getAttribute("data-share-src")); if (address) image.src = address; });
      gallery.addEventListener("click", function (event) {
        var card = event.target.closest ? event.target.closest("a[data-share-open]") : null;
        if (!card || event.defaultPrevented || event.button || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        go("/concepts/" + card.getAttribute("data-share-open") + "/index.html", "", true);
      });
      doc.getElementById("share-back").addEventListener("click", function () { go(null); });
      // Safari does not give a button the focus when it is clicked, so the arrow keys would do nothing after a click.
      doc.getElementById("share-previous").addEventListener("click", function () { this.focus(); step(-1); });
      doc.getElementById("share-next").addEventListener("click", function () { this.focus(); step(1); });
      // Left and right work while the bar has the focus. Inside a design, every key belongs to the design.
      doc.getElementById("share-bar").addEventListener("keydown", function (event) {
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
        if (event.key === "ArrowLeft") { event.preventDefault(); step(-1); } else if (event.key === "ArrowRight") { event.preventDefault(); step(1); }
      });
      window.addEventListener("popstate", function (event) { whenParsed(function () { render(event.state && event.state.share, false); }); });
      window.addEventListener("message", function (event) { if (frame && event.source === frame.contentWindow && event.data && event.data.annotatorShare === "frame-moved") recover(); });
    },
    ready: function () {
      parsed = true;
      var saved = history.state && history.state.share;
      if (saved && conceptAt(saved.page) >= 0) render(saved, false); else openNamed();
      // A "#name" added later (a card opened in a new tab, or typed) opens that design too.
      window.addEventListener("hashchange", openNamed);
      waiting.splice(0).forEach(function (run) { run(); });
    },
  };
}

/* eslint-enable no-var, prefer-arrow-callback */

const SHELL_CSS = `
html.share-viewing, html.share-viewing body { height: 100%; overflow: hidden; }
#share-gallery[hidden], #share-viewer[hidden] { display: none; }
.share-note { width: fit-content; max-width: 100%; margin: 0 0 18px; padding: 10px 16px; border-radius: 14px; background: #ffb58c1f; box-shadow: inset 0 0 0 1px #ffb58c4d; color: #f2f3f7; font-size: 17px; }
#share-viewer { position: fixed; inset: 0; z-index: 2147483000; display: flex; flex-direction: column; background: #0d0f14; }
#share-bar { flex: none; display: flex; align-items: center; gap: 4px; height: 40px; padding: 0 8px; border-bottom: 1px solid #ffffff1f; background: #12151c; color: #dfe2ea; font: 500 13px/1 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; letter-spacing: 0; }
#share-bar button { all: unset; box-sizing: border-box; display: inline-flex; align-items: center; justify-content: center; gap: 6px; height: 28px; min-width: 28px; padding: 0 10px; border-radius: 7px; cursor: pointer; white-space: nowrap; }
#share-bar button:hover { background: #ffffff1a; }
#share-bar button:active { background: #ffffff2b; }
#share-bar button:focus-visible { outline: 2px solid #ffb58c; outline-offset: 1px; }
#share-bar .share-mark { font-size: 16px; line-height: 1; }
#share-title { flex: 1; min-width: 0; display: flex; align-items: baseline; justify-content: center; gap: 8px; padding: 0 6px; white-space: nowrap; }
#share-name { overflow: hidden; color: #fff; font-weight: 650; text-overflow: ellipsis; }
#share-count { color: #9aa1b2; font-variant-numeric: tabular-nums; }
#share-stage { flex: 1; min-height: 0; }
.share-frame { display: block; width: 100%; height: 100%; border: 0; background: #fff; }
@media (max-width: 560px) { #share-bar .share-wide { display: none; } #share-bar button { padding: 0 8px; } }
`;

// ---------------------------------------------------------------------------------------------------------------
// Putting the file together

const forScript = (code) => code.replace(/<\/(script)/gi, "<\\/$1").replace(/<!--/g, "<\\!--");
const forJson = (value) => JSON.stringify(value).replace(/</g, "\\u003c");

function build(out) {
  const started = Date.now();
  console.log("Packing the website concepts into one file.");
  const gallery = packGallery();
  if (!concepts.length) throw fail("The gallery has no cards that lead to a concept folder with an index.html.");
  for (const concept of concepts) packPage(concept.index, concept);
  packModules();
  for (const concept of concepts) packScriptAssets(concept);

  // Binary files, each stored once even when several paths have the same content.
  const blobs = [], byContent = new Map(), assetIndex = {}, sizes = { video: 0, pictures: 0, fonts: 0, other: 0 };
  let encoded = 0, reused = 0, sourceVideo = 0;
  for (const [path, entry] of assets) {
    let content;
    if (extname(path).toLowerCase() === ".mp4") {
      const packed = packVideo(path);
      content = packed.bytes; sourceVideo += statOf(path).size;
      if (packed.fresh) encoded++; else reused++;
    } else content = readFileSync(fileOf(path));
    const key = sha(content), type = typeOf(path) || "application/octet-stream";
    if (!byContent.has(key)) {
      byContent.set(key, blobs.length);
      blobs.push({ text: content.toString("base64"), critical: false });
      sizes[type.startsWith("video/") ? "video" : type.startsWith("image/") ? "pictures" : type.startsWith("font/") ? "fonts" : "other"] += blobs.at(-1).text.length;
    }
    const id = byContent.get(key);
    blobs[id].critical ||= entry.critical;
    assetIndex[path] = [id, type];
  }

  const moduleTexts = Object.fromEntries([...modules].map(([id, entry]) => [id, entry.code]));
  const texts = { pages: Object.fromEntries(pages), css: Object.fromEntries(styles), js: Object.fromEntries(scripts), modules: moduleTexts };
  const index = {
    site: SITE, gallery: GALLERY, title: gallery.title, css: gallery.css + SHELL_CSS, assets: assetIndex,
    concepts: concepts.map(({ folder, name, index: page }) => ({ folder, name, index: page })),
  };
  const blobTag = (blob, id) => `<script type="text/plain" id="share-blob-${id}">${blob.text}</script>`;
  const runtime = forScript(`(${shellRuntime.toString()})(${JSON.stringify(conceptShim.toString())});`);
  const bar = `<div id="share-viewer" hidden>
<div id="share-bar" role="toolbar" aria-label="Designs">
<button type="button" id="share-back"><span class="share-mark" aria-hidden="true">←</span><span>All designs</span></button>
<div id="share-title" aria-live="polite"><span id="share-name"></span><span id="share-count"></span></div>
<button type="button" id="share-previous" aria-label="Previous design"><span class="share-mark" aria-hidden="true">‹</span><span class="share-wide" aria-hidden="true">Previous</span></button>
<button type="button" id="share-next" aria-label="Next design"><span class="share-wide" aria-hidden="true">Next</span><span class="share-mark" aria-hidden="true">›</span></button>
</div>
<div id="share-stage"></div>
</div>`;
  const parts = [
    `<!doctype html>\n<html lang="${escapeAttribute(gallery.language)}">\n<head>\n<meta charset="utf-8">\n<meta name="viewport" content="width=device-width, initial-scale=1">\n`,
    `<title>${escapeHtml(gallery.title)}</title>\n`,
    gallery.icon ? `<link rel="icon" href="${gallery.icon}">\n` : "",
    `<script type="application/json" id="share-index">${forJson(index)}</script>\n`,
    blobs.map((blob, id) => (blob.critical ? `${blobTag(blob, id)}\n` : "")).join(""),
    `<script>${runtime}</script>\n<script>__annotatorShare.writeStyles();</script>\n`,
    `<noscript><style>#share-gallery .card { pointer-events: none; }</style></noscript>\n</head>\n<body>\n`,
    `<noscript><p style="padding:24px;font:18px system-ui,sans-serif;color:#fff;background:#0d0f14">This file needs JavaScript to show the designs. Turn it on in your browser, then open the file again.</p></noscript>\n`,
    `<div id="share-gallery">${gallery.body}</div>\n${bar}\n<script>__annotatorShare.showGallery();</script>\n`,
    `<script type="application/json" id="share-texts">${forJson(texts)}</script>\n`,
    blobs.map((blob, id) => (blob.critical ? "" : `${blobTag(blob, id)}\n`)).join(""),
    `<script>__annotatorShare.ready();</script>\n</body>\n</html>\n`,
  ];
  const html = parts.join("");
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, html);

  const size = Buffer.byteLength(html), length = (value) => Buffer.byteLength(forJson(value));
  const vendor = [...modules].filter(([id]) => id.includes("/vendor/")), own = [...modules].filter(([id]) => !id.includes("/vendor/"));
  const rows = [
    [`Recordings (${[...assets.keys()].filter((path) => path.endsWith(".mp4")).length} clips, from ${megabytes(sourceVideo)} of footage)`, sizes.video],
    ["Pictures (posters, gallery pictures, icons)", sizes.pictures],
    ["Fonts", sizes.fonts],
    ...(sizes.other ? [["Other files", sizes.other]] : []),
    ["3D library", length(Object.fromEntries(vendor.map(([id, entry]) => [id, entry.code])))],
    ["Drawing engine and page scripts", length(Object.fromEntries(own.map(([id, entry]) => [id, entry.code]))) + length(texts.js)],
    ["Styles", length(texts.css) + Buffer.byteLength(gallery.css)],
    ["Pages", length(texts.pages) + Buffer.byteLength(gallery.body)],
    ["Viewer", Buffer.byteLength(runtime) + Buffer.byteLength(SHELL_CSS) + Buffer.byteLength(bar)],
  ];
  console.log(`\nDesigns in the file (${concepts.length}):`);
  concepts.forEach((concept, at) => console.log(`  ${at + 1}. ${concept.name} (${concept.folder}): ${concept.pages.map((page) => basename(page)).join(", ")}`));
  if (verbose) {
    console.log("\nFiles found from addresses that scripts build while running:");
    for (const concept of concepts) console.log(`  ${concept.folder}: ${concept.guessed.map((path) => basename(path)).join(", ") || "none"}`);
    console.log("\nFiles in the store:");
    for (const [path, [id]] of Object.entries(assetIndex)) console.log(`  ${megabytes(blobs[id].text.length).padStart(9)}  ${path}`);
  }
  console.log(`\nRecordings: ${encoded} made smaller now, ${reused} taken from the cache (${relative(process.cwd(), CACHE_DIR)}).`);
  console.log("\nWhat is in the file:");
  for (const [label, bytes] of rows) console.log(`  ${megabytes(bytes).padStart(9)}  ${label}`);
  console.log(`  ${megabytes(size).padStart(9)}  Total`);
  console.log(`\nWrote ${relative(process.cwd(), out) || out} (${megabytes(size)}) in ${((Date.now() - started) / 1000).toFixed(1)} s.`);
  if (warnings.length) console.log(`${warnings.length} warning${warnings.length === 1 ? "" : "s"} above. Read them before you send the file.`);
  if (size > LIMIT_MB * 1048576) throw fail(`The file is over the ${LIMIT_MB} MB limit. Lower the sizes in VIDEO at the top of this script (a higher clipCrf or longCrf makes smaller recordings).`);
  if (size > TARGET_MB * 1048576) console.warn(`  ! The file is over the ${TARGET_MB} MB target. A higher clipCrf or longCrf in VIDEO at the top of this script makes it smaller.`);
}

async function main() {
  const args = process.argv.slice(2);
  if (args.includes("--scan-imports")) return scanImportsFromStdin();
  if (args.includes("--help") || args.includes("-h")) {
    console.log("Packs the website concepts into one HTML file that opens from disk.\n\n  node website/concepts/make-share.mjs [--out <file>] [--verbose]\n\n  --out <file>  Where to write the file. Default: website/concepts/dist/annotator-website-concepts.html\n  --verbose     Also list every packed file.");
    return;
  }
  let out = DEFAULT_OUT;
  for (let at = 0; at < args.length; at++) {
    if (args[at] === "--out" && args[at + 1]) out = resolve(args[++at]);
    else if (args[at].startsWith("--out=")) out = resolve(args[at].slice(6));
    else if (args[at] === "--verbose") verbose = true;
    else throw fail(`Unknown option: ${args[at]}. Run with --help to see the options.`);
  }
  build(out);
}

main().catch((error) => {
  console.error(error?.plain ? `\nStopped: ${error.message}` : error);
  process.exitCode = 1;
});
