import { createInk } from "/concepts/shared/ink.js";

const root = document.documentElement;
const $ = (id) => document.getElementById(id);
const reduced = matchMedia("(prefers-reduced-motion: reduce)");
const system = navigator.userAgentData?.platform || navigator.platform || "";
const onWindows = /win/i.test(system), onApple = /mac|iphone|ipad/i.test(system);
const STORE = "https://apps.microsoft.com/detail/9NMZ345ZJ6JK";

// The visitor's platform: the download in the first screen, the keys as they are printed on that keyboard, the open tab.
if (onWindows) {
  Object.assign($("get-primary"), { href: STORE, textContent: "Get it from Microsoft Store" });
  Object.assign($("get-other"), { href: "/downloads/", textContent: "Also for Mac" });
}
if (onApple) $("draw-keys").innerHTML = "<kbd>⌃</kbd><kbd>⇧</kbd><kbd>2</kbd>";

// The screen ---------------------------------------------------------------------------------------
const screen = $("screen");
const theme = root.dataset.theme;
// On a phone the toolbar starts as its one button, so the first screen stays calm.
const small = matchMedia("(max-width: 575px)").matches;
const ink = createInk({
  surface: screen, toolbarParent: screen, toolbarPosition: "absolute",
  appearance: theme === "light" || theme === "dark" ? theme : "auto",
  properties: false, tool: "pen", color: "#F04538", useLabel: "Use page",
  ...(small ? { collapsed: true, pillLabel: "Draw on this screen" } : {}),
});
// The hint sits just above the toolbar, whatever height the toolbar has.
const follow = () => screen.style.setProperty("--tb-h", `${ink.toolbar.offsetHeight}px`);
ink.on("layout", follow);
follow();
const folded = () => { screen.classList.toggle("is-collapsed", ink.collapsed); screen.classList.toggle("is-open", !ink.collapsed); };
ink.on("collapse", folded);
folded();
$("back").addEventListener("click", () => ink.setMode("use"));
// The ring stays on its word while the window is resized (the engine alone follows a moment later).
addEventListener("resize", () => ink.reflow());
// Off screen, the demo stops listening to keys and leaves draw mode. The drawings stay.
new IntersectionObserver(([entry]) => ink.setEnabled(entry.isIntersecting)).observe(screen);

// Once, on load: the toolbar arrives, then a pointer draws one ring around a word of the headline.
// A visitor who has already started drawing does not get the ring.
const intro = new AbortController();
ink.on("mode", () => intro.abort());
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
async function open() {
  const still = reduced.matches, ring = { tool: "ellipse", around: $("ring-target"), pad: 0, size: 3, color: "#F04538" };
  const whole = () => ink.perform([{ ...ring, duration: 0 }], { cursor: false });
  if (still) { await whole(); return; }
  ink.toolbar.animate([{ opacity: 0, transform: "translateY(10px)" }, { opacity: 1, transform: "none" }], { duration: 450, delay: 120, easing: "cubic-bezier(.2, .7, .2, 1)", fill: "backwards" });
  await document.fonts?.ready;
  await wait(1000);
  if (intro.signal.aborted) return;
  const drawn = ink.perform([{ ...ring, duration: 900 }], { signal: intro.signal }).then(() => true);
  // Where the browser draws no frames (a tab in the background), the ring would wait half drawn. It is shown whole instead.
  if (await Promise.race([drawn, wait(3500).then(() => false)])) await wait(450);
  else {
    const half = ink.marks().find((mark) => mark.anchored && mark.tool === "ellipse");
    if (half) ink.remove(half.id);
    await whole();
  }
  ink.hideGhost();
}
open();

// The recording plays only while it is in view, and never on its own when the visitor asked for less motion.
const clip = $("clip"), toggle = $("clip-toggle"), narrow = matchMedia("(max-width: 640px)");
let wanted = !reduced.matches, inView = false;
function pickClip() {
  const name = narrow.matches ? "stage-arrow-phone" : "stage-arrow";
  if (clip.dataset.name === name) return;
  clip.dataset.name = name;
  clip.poster = `/public/demo/${name}.jpg`;
  clip.src = `/public/demo/${name}.mp4`;
}
function syncClip() {
  toggle.textContent = wanted ? "Pause" : "Play";
  toggle.setAttribute("aria-label", wanted ? "Pause the recording" : "Play the recording");
  if (wanted && inView) clip.play().catch(() => { wanted = false; syncClip(); });
  else clip.pause();
}
pickClip();
narrow.addEventListener("change", () => { pickClip(); syncClip(); });
toggle.addEventListener("click", () => { wanted = !wanted; syncClip(); });
new IntersectionObserver(([entry]) => { inView = entry.isIntersecting; syncClip(); }, { threshold: 0.35 }).observe(clip);
syncClip();

// Install: one tab for each platform, and a copy button for each command.
const tabs = [...document.querySelectorAll('[role="tab"]')];
function choose(tab, focus) {
  for (const each of tabs) {
    const on = each === tab;
    each.setAttribute("aria-selected", String(on));
    each.tabIndex = on ? 0 : -1;
    $(each.getAttribute("aria-controls")).hidden = !on;
  }
  if (focus) tab.focus();
}
tabs.forEach((tab, i) => {
  tab.addEventListener("click", () => choose(tab));
  tab.addEventListener("keydown", (e) => {
    const step = { ArrowRight: 1, ArrowLeft: -1 }[e.key];
    if (!step) return;
    e.preventDefault();
    choose(tabs[(i + step + tabs.length) % tabs.length], true);
  });
});
choose($(onWindows ? "tab-windows" : "tab-mac"));

const status = $("status");
for (const button of document.querySelectorAll("[data-copy]")) {
  let timer = 0;
  button.addEventListener("click", async () => {
    const code = button.closest(".command").querySelector("code");
    let said = "Copied";
    try { await navigator.clipboard.writeText(code.textContent); }
    catch {
      // Without clipboard access the command is selected, ready to be copied by hand.
      getSelection().selectAllChildren(code);
      said = "Selected";
    }
    button.textContent = said;
    status.textContent = said === "Copied" ? "Command copied." : "Command selected. Copy it with your keyboard.";
    clearTimeout(timer);
    timer = setTimeout(() => { button.textContent = "Copy"; status.textContent = ""; }, 1800);
  });
}
