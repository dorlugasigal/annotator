import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
// The app's own motion: its curves, the toolbar's change to one button, the Pop style and the pointer's click.
import { css as appCurve, ease as appEase, toolbarMorph, poseKeyframes, clickPulse, visibilityDuration } from "/concepts/shared/app-motion.js";

const root = document.documentElement;
const motion = root.classList.contains("motion");
const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const smooth = (t) => { const x = clamp(t, 0, 1); return x * x * (3 - 2 * x); };
// A wait that ends early when its signal is aborted.
const wait = (ms, signal) => new Promise((done) => {
  if (signal?.aborted) { done(); return; }
  const timer = setTimeout(done, ms);
  signal?.addEventListener("abort", () => { clearTimeout(timer); done(); }, { once: true });
});
// A move over `ms`, on the app's standard curve. It stops where it is when its signal is aborted.
const glide = (ms, step, signal) => new Promise((done) => {
  if (!motion || ms <= 0) { step(1); done(); return; }
  const began = performance.now();
  const frame = (now) => {
    if (signal?.aborted) { done(); return; }
    const t = clamp((now - began) / ms, 0, 1);
    step(appEase.standard(t));
    if (t < 1) requestAnimationFrame(frame); else done();
  };
  requestAnimationFrame(frame);
});
const BLUE = "#2B73E0", TEAL = "#00969E", CORAL = "#F04538", PURPLE = "#7D54B8";

// Where an element sits inside a drawing surface, from the layout and not from the screen. The engine measures
// with getBoundingClientRect, which a tilted plane distorts; these stand-ins answer in the plane's own space, so
// scripted marks land on their targets at any angle and follow them when the page reflows.
function localBox(el, surface) {
  let x = 0, y = 0, node = el;
  while (node && node !== surface) {
    x += node.offsetLeft; y += node.offsetTop;
    node = node.offsetParent;
    if (node && node !== surface) { x += node.clientLeft; y += node.clientTop; }
  }
  return { x, y, w: el.offsetWidth, h: el.offsetHeight };
}
const standIn = (ink, box) => ({
  getBoundingClientRect() {
    // The layer's corner is the surface's corner, except while a grown stage gives the layer room past it.
    const o = ink.layer.getBoundingClientRect(), room = ink.room, b = box();
    return { left: o.left + (room?.left || 0) + b.x, top: o.top + (room?.top || 0) + b.y, width: b.w, height: b.h };
  },
});
// Where an element sits on the page, whatever is tilting or flipping at the moment.
const pageBox = (el) => localBox(el, null);

// Scroll-driven values -----------------------------------------------------------------------------
// One handler, one frame at a time. Each channel has a goal read from the page and a shown value that follows it
// with a short lag, so a mouse wheel's steps become one smooth move. Goals are read first, then styles written.
const channels = [], perFrame = [];
let queued = false, lastFrame = 0;
function channel(goal, apply) { const c = { goal, apply, value: null, shown: null, to: 0 }; channels.push(c); return c; }
function frame(now) {
  queued = false;
  const k = 1 - Math.exp(-Math.min(64, lastFrame ? now - lastFrame : 16) / 85);
  lastFrame = now;
  for (const c of channels) c.to = c.goal();
  let moving = false;
  for (const c of channels) {
    const gap = c.to - (c.value ?? c.to);
    c.value = c.value === null || Math.abs(gap) < 0.0008 ? c.to : c.value + gap * k;
    if (c.value !== c.to) moving = true;
    if (c.value !== c.shown) { c.shown = c.value; c.apply(c.value); }
  }
  for (const task of perFrame) task();
  root.classList.toggle("is-scrolled", scrollY > 8);
  if (moving) request(); else lastFrame = 0;
}
function request() { if (!queued) { queued = true; requestAnimationFrame(frame); } }
addEventListener("scroll", request, { passive: true });
addEventListener("resize", request);

// 1. The stage -------------------------------------------------------------------------------------
const desk = $("#desk"), dock = $("#dock"), scene = $(".stage-scene"), apps = $("#apps"), header = $(".site-header");
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset): curved arrows,
// the roughest line, and the app's Neon gradient as the color. It rests on the Arrow tool so its properties row
// shows that. The engine itself starts the row closed on a phone, which keeps the bar compact there.
// Hide drawings and tools, by the toolbar's button or Control-2, goes the app's Magic way.
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, tool: "arrow", gradient: "neon", visibility: "magic", moreHref: "#install" });
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the desk's
// frame opens up to fill the window, and scrolling brings it back. The desk itself does not move: in index.html the
// panel and the dock carry data-stage-frame and the mode chip data-stage-corner.
const stage = growingStage({ ink, surface: desk, tries: "[data-try]", motion: () => motion });
// Joining in ends the script, lays the desk flat (the frame grows only once it is) and retires "Your turn".
stage.on("join", () => { takeOver("join"); retire(); });
// Whether this engine shows and hides drawings and the toolbar in the app's styles (it then answers with a promise).
const appShows = ink.setHidden(false) instanceof Promise;

// Theme --------------------------------------------------------------------------------------------
// Dark unless the visitor chose light. The choice is kept; the page's head applies it before the first paint.
const themeSwitch = $(".theme-switch"), themeColor = $('meta[name="theme-color"]');
const PAGE_COLOR = { dark: "#0a0a0a", light: "#fafafa" }, themed = [ink];
function showTheme(theme) {
  root.dataset.theme = theme;
  themeSwitch.setAttribute("aria-checked", String(theme === "dark"));
  themeColor.content = PAGE_COLOR[theme];
  for (const instance of themed) instance.setAppearance(theme);
}
// The pixel dissolve: the screen is cut into squares, and each gets a moment to turn, later the farther it is from
// the switch, with some chance in it. Each step of the change is the outline of every square that has turned so far.
// It lasts as long as the app's Dissolve.
const PIXEL_STEPS = 12, PIXEL_MS = visibilityDuration("dissolve", true) * 1000;
function pixelFrames(x, y) {
  const w = innerWidth, h = innerHeight, cell = w < 600 ? 20 : 26, cols = Math.ceil(w / cell), rows = Math.ceil(h / cell);
  const far = Math.hypot(Math.max(x, w - x), Math.max(y, h - y)) || 1, when = [];
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) when.push(Math.hypot((c + 0.5) * cell - x, (r + 0.5) * cell - y) / far * 0.7 + Math.random() * 0.3);
  const frames = ['path("M0 0h0v0z")'];
  for (let step = 1; step < PIXEL_STEPS; step++) {
    const limit = step / PIXEL_STEPS;
    let d = "";
    for (let r = 0; r < rows; r++) {
      let from = -1;
      for (let c = 0; c <= cols; c++) {
        const turned = c < cols && when[r * cols + c] <= limit;
        if (turned && from < 0) from = c;
        else if (!turned && from >= 0) { d += `M${from * cell} ${r * cell}h${(c - from) * cell}v${cell}h${(from - c) * cell}z`; from = -1; }
      }
    }
    frames.push(`path("${d || "M0 0h0v0z"}")`);
  }
  frames.push(`path("M0 0h${cols * cell}v${rows * cell}h${-cols * cell}z")`);
  return frames;
}
let themeTurn = 0;
function setTheme(theme) {
  try { localStorage.setItem("annotator.site.theme", theme); } catch { /* storage is off: the choice lasts for this visit */ }
  const turn = ++themeTurn;
  if (!motion) { showTheme(theme); return; }
  if (!document.startViewTransition) {
    // No view transitions here: the colors cross-fade for a moment.
    root.classList.add("theme-fade");
    showTheme(theme);
    setTimeout(() => { if (turn === themeTurn) root.classList.remove("theme-fade"); }, 340);
    return;
  }
  const r = themeSwitch.getBoundingClientRect(), frames = pixelFrames(r.left + r.width / 2, r.top + r.height / 2);
  const transition = document.startViewTransition(() => showTheme(theme));
  const stopPassing = passThrough(transition);
  // The squares are a clip on the new page's picture, changed in steps. If that cannot start, the browser's own
  // cross-fade runs instead.
  transition.ready.then(() => {
    root.animate({ clipPath: frames }, { duration: PIXEL_MS, pseudoElement: "::view-transition-new(root)", fill: "both" });
    root.classList.add("theme-pixels");
  }).catch(() => {});
  const done = () => { stopPassing(); if (turn === themeTurn) root.classList.remove("theme-pixels"); };
  transition.finished.then(done, done);
}
// While a view transition runs, the browser shows a picture of the page and sends every press to the page's root,
// so nothing under the pointer can be pressed. A press ends the change at once and is handed to what is really
// there, so a visitor can pick a tool or start drawing the moment after they switch.
function passThrough(transition) {
  let pressed = null;
  const down = (e) => {
    if (e.target !== root || !e.isTrusted) return;
    transition.skipTransition();
    const under = document.elementFromPoint(e.clientX, e.clientY);
    if (!under || under === root) return;
    pressed = under;
    under.dispatchEvent(new PointerEvent("pointerdown", e));
  };
  const click = (e) => {
    if (e.target !== root || !e.isTrusted || !pressed) return;
    const under = document.elementFromPoint(e.clientX, e.clientY), from = pressed;
    pressed = null;
    if (under && (under === from || from.contains(under) || under.contains(from))) (under.closest("a, button") || under).click();
  };
  addEventListener("pointerdown", down, true);
  addEventListener("click", click, true);
  // The click that belongs to a press arrives after the change has ended, so the listeners stay a moment longer.
  return () => setTimeout(() => { removeEventListener("pointerdown", down, true); removeEventListener("click", click, true); }, 700);
}
themeSwitch.addEventListener("click", () => setTheme(root.dataset.theme === "dark" ? "light" : "dark"));
showTheme(root.dataset.theme);

// The desk keeps room under the windows for the toolbar at its tallest (its properties row open), so the bar never
// covers a window and opening or closing the row does not move what the visitor drew on.
const OPEN_HEIGHT = { "expanded/1": 145, "minimal/1": 113, "minimal/2": 145 };
let reserved = 0, barShape = "";
// Where the toolbar sits in the dock, for the shadow it casts on the desk while the stage is in its pose.
function barShadow() {
  const bar = ink.toolbar;
  if (!bar.offsetWidth || ink.collapsed) return;
  dock.style.setProperty("--bar-x", `${bar.offsetLeft - (bar.classList.contains("is-placed") ? 0 : bar.offsetWidth / 2)}px`);
  dock.style.setProperty("--bar-y", `${bar.offsetTop}px`);
  dock.style.setProperty("--bar-w", `${bar.offsetWidth}px`);
  dock.style.setProperty("--bar-h", `${bar.offsetHeight}px`);
}
ink.on("layout", ({ height, size, rows }) => {
  if (!height || ink.collapsed) return;
  barShadow();
  const shape = `${size}/${rows}`, phone = matchMedia("(max-width: 599px)").matches;
  const room = Math.max(height, phone ? 0 : OPEN_HEIGHT[shape] || 0, shape === barShape ? reserved : 0);
  barShape = shape;
  if (room === reserved) return;
  reserved = room;
  requestAnimationFrame(() => desk.style.setProperty("--tb-h", `${room}px`));
});
ink.fit();

// The pose. Isometric at the top of the page, flat once the stage has reached the middle of the screen. As soon as
// the visitor takes over, it goes flat and stays flat: a pointer on a tilted plane would not draw where it points.
// In the pose the whole stage is one thing to press, and a press brings it to the visitor.
const cover = document.createElement("i");
if (motion) {
  for (let k = 1; k <= 5; k++) {
    // The tile's thickness: a few slices under it, the lowest one a glow in the accent.
    const slice = document.createElement("i");
    slice.className = k === 5 ? "slab slab-glow" : "slab";
    slice.setAttribute("aria-hidden", "true");
    slice.style.setProperty("--k", k);
    $(".desk-panel").before(slice);
  }
  cover.className = "stage-cover";
  cover.setAttribute("aria-hidden", "true");
  scene.append(cover);
}
let userFlat = !motion;
channel(() => {
  if (userFlat) return 0;
  const middle = pageBox(scene).y + scene.offsetHeight / 2;
  // Close to flat is flat: the planes need room between them to keep their order.
  const pose = 1 - smooth(scrollY / clamp(middle - innerHeight / 2, 260, innerHeight * 0.9));
  return pose < 0.03 ? 0 : pose;
}, (v) => {
  desk.style.setProperty("--tilt", v.toFixed(4));
  desk.style.setProperty("--lift", (4 * v * (1 - v)).toFixed(4));
  desk.classList.toggle("is-flat", v === 0);
  scene.classList.toggle("is-posed", v > 0.1);
  if (v) barShadow();
});

// The sample apps ----------------------------------------------------------------------------------
// Three small apps with made-up data. Nothing here leaves the page. In Use apps they answer to the visitor; in Draw
// the ink layer lies over them. Drawings never follow what the apps do: that is how the real app behaves.
const wins = $$(".win", apps), offsets = new Map(wins.map((win) => [win, [0, 0]]));
const stacked = () => matchMedia("(max-width: 599px)").matches;
// Where something on the desk is, counting how far its window has been dragged.
const deskBox = (el) => {
  const b = localBox(el, desk), o = offsets.get(el.closest(".win"));
  return o ? { ...b, x: b.x + o[0], y: b.y + o[1] } : b;
};
let topWindow = 1;
function placeWin(win, x, y) {
  const b = localBox(win, desk), pad = 6;
  x = Math.round(clamp(x, Math.min(0, pad - b.x), Math.max(0, desk.clientWidth - b.w - b.x - pad)));
  y = Math.round(clamp(y, Math.min(0, pad - b.y), Math.max(0, desk.clientHeight - b.h - b.y - pad)));
  offsets.set(win, [x, y]);
  win.style.translate = x || y ? `${x}px ${y}px` : "";
}
for (const win of wins) {
  const bar = $(".win-bar", win);
  bar.addEventListener("pointerdown", (e) => {
    if (e.button || (e.pointerType === "touch" && stacked())) return;
    const [ox, oy] = offsets.get(win), sx = e.clientX, sy = e.clientY;
    try { bar.setPointerCapture(e.pointerId); } catch { /* a pointer that cannot be captured still drags while it is over the bar */ }
    win.classList.add("is-dragging");
    win.style.zIndex = ++topWindow;
    const move = (ev) => placeWin(win, ox + ev.clientX - sx, oy + ev.clientY - sy);
    const up = () => { win.classList.remove("is-dragging"); bar.removeEventListener("pointermove", move); bar.removeEventListener("pointerup", up); bar.removeEventListener("pointercancel", up); };
    bar.addEventListener("pointermove", move);
    bar.addEventListener("pointerup", up);
    bar.addEventListener("pointercancel", up);
  });
  bar.addEventListener("keydown", (e) => {
    const step = { ArrowLeft: [-16, 0], ArrowRight: [16, 0], ArrowUp: [0, -16], ArrowDown: [0, 16] }[e.key];
    if (!step) return;
    e.preventDefault();
    const [x, y] = offsets.get(win);
    win.style.zIndex = ++topWindow;
    placeWin(win, x + step[0], y + step[1]);
  });
}
// The deck.
const slides = $$(".slide-page"), deckCount = $(".deck-count b");
let slideAt = 0;
function showSlide(index) {
  slideAt = (index + slides.length) % slides.length;
  slides.forEach((page, i) => { page.classList.toggle("is-on", i === slideAt); page.inert = i !== slideAt; });
  deckCount.textContent = slideAt + 1;
}
for (const button of $$("[data-deck]")) button.addEventListener("click", () => showSlide(slideAt + Number(button.dataset.deck)));
// The chart.
const WEEKS = {
  45: { total: 387, delta: "3% more than the week before", days: [52, 55, 60, 58, 61, 50, 51] },
  46: { total: 402, delta: "4% more than the week before", days: [58, 60, 63, 61, 64, 48, 48] },
  47: { total: 318, delta: "21% fewer than the week before", days: [62, 58, 64, 55, 41, 22, 16] },
};
const numberEl = $("#t-number"), deltaEl = $("#t-delta"), lineEl = $("#t-line"), weekTabs = $$("[data-week]"), barEls = $$(".bars span");
function showWeek(week) {
  const data = WEEKS[week];
  weekTabs.forEach((tab) => tab.setAttribute("aria-pressed", String(tab.dataset.week === String(week))));
  numberEl.textContent = data.total;
  deltaEl.textContent = data.delta;
  barEls.forEach((bar, i) => { bar.style.setProperty("--v", data.days[i]); bar.classList.toggle("low", data.days[i] < 45); });
}
for (const tab of weekTabs) tab.addEventListener("click", () => showWeek(tab.dataset.week));
// The message.
const thread = $("#thread"), reply = $("#reply"), replyInput = $("input", reply);
reply.addEventListener("submit", (e) => {
  e.preventDefault();
  const text = replyInput.value.trim();
  if (!text) return;
  const bubble = document.createElement("p");
  bubble.className = "bubble me";
  bubble.textContent = text;
  thread.append(bubble);
  replyInput.value = "";
  thread.scrollTop = thread.scrollHeight;
});
function resetApps() {
  showSlide(0);
  showWeek(47);
  $$(".bubble.me", thread).forEach((bubble) => bubble.remove());
  $$(".checks input", apps).forEach((box) => { box.checked = false; });
  replyInput.value = "";
  topWindow = 1;
  for (const win of wins) { win.style.zIndex = ""; placeWin(win, 0, 0); }
}
showSlide(0);

// Draw and Use apps --------------------------------------------------------------------------------
// The chip on the stage's edge says which one is on. It changes the way the app's toolbar changes into its one
// button: the frame on the standard curve, the old face gone by half the time, the new one in from 35%.
const chip = $("#stage-mode"), done = $("#stage-done"), replay = $("#stage-replay"), turn = $("#stage-turn");
function showMode(mode) {
  const from = chip.dataset.mode;
  if (from === mode) return;
  const before = chip.offsetWidth, leaving = $(`.face[data-face="${from}"]`, chip), coming = $(`.face[data-face="${mode}"]`, chip);
  chip.getAnimations({ subtree: true }).forEach((animation) => animation.finish());
  chip.dataset.mode = mode;
  if (!motion) return;
  const after = chip.offsetWidth, duration = (after > before ? toolbarMorph.expandDuration : toolbarMorph.collapseDuration) * 1000;
  const sample = (opacity) => Array.from({ length: 21 }, (_, i) => ({ opacity: clamp(opacity(i / 20), 0, 1) }));
  leaving.classList.add("is-leaving");
  chip.classList.add("is-morphing");
  leaving.animate(sample(toolbarMorph.outgoingOpacity), { duration, fill: "both" });
  coming.animate(sample(toolbarMorph.incomingOpacity), { duration });
  chip.animate({ width: [`${before}px`, `${after}px`] }, { duration, easing: appCurve.standard }).finished
    .then(() => { leaving.getAnimations().forEach((animation) => animation.cancel()); leaving.classList.remove("is-leaving"); chip.classList.remove("is-morphing"); }, () => {});
}
// The page's own script switches the mode too. While it does, a visitor's presses still reach the apps.
let scripted = false;
function scriptMode(mode) { scripted = true; desk.classList.add("is-scripted"); ink.setMode(mode); }
function endScript() { scripted = false; desk.classList.remove("is-scripted"); syncMode(); }
function syncMode() {
  const drawing = ink.state.mode === "draw" && !scripted;
  done.hidden = !drawing;
  apps.inert = drawing;
  desk.classList.toggle("is-cued", drawing);
}
// "Your turn" arrives and leaves with the app's Pop. It shows when the script is idle and after its first round,
// and is gone for good once the visitor has joined in.
let drew = false, invited = false;
function invite(on) {
  on = on && !drew;
  if (on === invited) return;
  invited = on;
  desk.classList.toggle("is-inviting", on);
  turn.getAnimations().forEach((animation) => animation.cancel());
  if (on) turn.hidden = false;
  if (!motion) { turn.hidden = !on; return; }
  const keys = poseKeyframes("pop", { appearing: on, annotation: false });
  const move = turn.animate(keys, { duration: keys.duration, easing: keys.easing, fill: "both" });
  move.finished.then(() => { move.cancel(); if (!invited) turn.hidden = true; }, () => {});
}
function retire() { drew = true; invite(false); }
ink.on("mark", retire);

// The story the stage tells on its own: in Draw, ring the number and point at it; in Use apps, click around while
// the drawings stay put; back in Draw, add a note. Then the toolbar shrinks and grows, and everything is put away.
const number = standIn(ink, () => deskBox(numberEl)), line = standIn(ink, () => deskBox(lineEl));
function drawSteps() {
  const small = desk.clientWidth < 600, px = small ? 12 : 17, py = small ? 8 : 11, size = small ? 3 : 3.5;
  // The arrow runs from the line on the slide to the number: sideways when the windows sit side by side, down when
  // they are stacked.
  const ends = () => {
    const l = deskBox(lineEl), n = deskBox(numberEl);
    return n.x > l.x + l.w
      ? [[l.x + l.w + 10, l.y + l.h * 0.3], [n.x - px - 9, n.y + n.h * 0.42]]
      : [[l.x + l.w - 4, l.y + l.h + 9], [n.x + n.w * 0.8, n.y - py - 14]];
  };
  // The ring and the arrow name no color: they take the one selected in the toolbar. Both are drawn at the
  // toolbar's strongest roughness, and the arrow bows well clear of a straight line.
  return {
    marks: [
      { tool: "ellipse", around: number, padX: px, padY: py, size, rough: 6, duration: 820, pause: 240 },
      { tool: "arrow", from: () => ends()[0], to: () => ends()[1], bend: 0.42, size, rough: 6, duration: 760, pause: 200 },
    ],
    note: [{ tool: "text", at: { el: number, at: "right", dx: px + 12, dy: small ? -15 : -18 }, text: "why the dip?", color: BLUE, pause: 200 }],
  };
}
// Scripted marks follow their targets when the page reflows. Once an app has been used they stay where they are.
const dropAnchors = () => { for (const mark of ink.marks()) if (mark.anchor) mark.anchor = null; };
// Where the script's pointer presses, a ring swells and settles as the app's pointer does on a click.
function pulse([x, y]) {
  if (!motion) return;
  if (ink.clickGhost) { ink.clickGhost(); return; }
  const ring = document.createElement("i"), keys = clickPulse();
  ring.className = "tap";
  ring.style.translate = `${x}px ${y}px`;
  ink.layer.append(ring);
  ring.animate(keys, { duration: keys.duration, easing: keys.easing });
  ring.animate([{ opacity: 0 }, { opacity: 1, offset: 0.2 }, { opacity: 1, offset: 0.65 }, { opacity: 0 }], { duration: keys.duration }).finished.then(() => ring.remove(), () => ring.remove());
}
async function tap(el, signal) {
  const b = deskBox(el), p = [b.x + b.w / 2, b.y + b.h / 2];
  await ink.moveGhost(p);
  if (signal.aborted) return;
  pulse(p);
  await wait(150, signal);
  if (!signal.aborted) el.click();
}
// The keycap on the chip, and its twin in "Two keys to remember", go down for a moment.
async function pressCap(name, signal) {
  const keys = [$(`[data-cap="${name}"]`, chip), ...$$(`[data-set="${name}"] .key`)];
  keys.forEach((key) => key.classList.add("is-down"));
  await wait(240, signal);
  keys.forEach((key) => key.classList.remove("is-down"));
}
// Putting everything away between rounds: the app's hide and show, in one of its styles. An engine without them
// fades the drawings out instead.
let scriptHid = false, scriptShrank = false;
async function putAway(style, between, signal) {
  if (appShows && motion) {
    scriptHid = true;
    await ink.setHidden(true, { style, pill: false });
    // A visitor who took over meanwhile gets everything back as it was.
    if (!signal?.aborted) { between(); await wait(260, signal); }
    if (scriptHid) await ink.setHidden(false, { style });
    scriptHid = false;
    return;
  }
  desk.classList.add("is-wiping");
  await wait(340, signal);
  if (!signal?.aborted) between();
  desk.classList.remove("is-wiping");
}
const tidy = () => { ink.clear(); resetApps(); };
let rounds = 0;
async function round(signal) {
  const live = () => !signal.aborted, steps = drawSteps();
  const draw = (list) => ink.perform(list, { cursor: true, showTool: true, signal });
  scriptMode("draw");
  await draw(steps.marks);
  await wait(420, signal);
  if (!live()) return;
  // Esc: over to the apps. The drawings stay.
  await pressCap("use", signal);
  if (!live()) return;
  scriptMode("use");
  dropAnchors();
  await wait(320, signal);
  if (live()) await tap($('[data-week="46"]'), signal);
  await wait(760, signal);
  if (live()) await tap($('[data-week="47"]'), signal);
  await wait(420, signal);
  if (live()) await tap($('[data-deck="1"]'), signal);
  await wait(420, signal);
  if (live() && !stacked()) {
    const win = $(".win-mail"), bar = deskBox($(".win-bar", win)), from = [bar.x + bar.w * 0.3, bar.y + bar.h / 2], [ox, oy] = offsets.get(win);
    await ink.moveGhost(from);
    pulse(from);
    win.classList.add("is-dragging");
    await glide(640, (k) => { placeWin(win, ox + 22 * k, oy + 40 * k); ink.placeGhost([from[0] + 22 * k, from[1] + 40 * k]); }, signal);
    win.classList.remove("is-dragging");
    await wait(260, signal);
  }
  if (live()) {
    const box = deskBox(replyInput), at = [box.x + 18, box.y + box.h / 2];
    await ink.moveGhost(at);
    pulse(at);
    for (const letter of "On it") { if (!live()) break; replyInput.value += letter; await wait(70, signal); }
    await wait(160, signal);
  }
  if (live()) await tap($("button", reply), signal);
  await wait(520, signal);
  if (!live()) return;
  // Control Shift 2: back to drawing, for the note.
  await pressCap("draw", signal);
  if (!live()) return;
  scriptMode("draw");
  await draw(steps.note);
  ink.hideGhost();
  keepChoice();
  rounds++;
  invite(true);
  await wait(1700, signal);
  if (!live()) return;
  // A calm moment: the toolbar shrinks to its one button and grows back, as it does in the app.
  scriptShrank = true;
  await (ink.shrink ? ink.shrink() : ink.setCollapsed(true));
  await wait(900, signal);
  if (scriptShrank) await (ink.expand ? ink.expand() : ink.setCollapsed(false));
  scriptShrank = false;
  await wait(900, signal);
  if (!live()) return;
  await putAway(rounds % 2 ? "magic" : "pop", tidy, signal);
  await wait(500, signal);
}

// The tool the visitor chose. A script that shows its tools in the toolbar puts back the tool it started with when
// it ends; if the visitor picked one meanwhile, theirs is put back instead.
let chosen = null;
ink.on("tool", (tool) => { chosen = tool; });
const keepChoice = () => { if (chosen && ink.state.tool !== chosen) ink.setTool(chosen, ink.state.mode !== "draw"); };

let run = null, roundAbort = null, deskSeen = false, seenWaiters = [];
const setRunning = (running) => {
  replay.dataset.state = running ? "playing" : "";
  $("b", replay).textContent = running ? "Pause" : "Replay";
};
const whenSeen = (signal) => new Promise((go) => {
  if (deskSeen || signal.aborted) { go(); return; }
  seenWaiters.push(go);
  signal.addEventListener("abort", go, { once: true });
});
// What a round leaves behind when it is cut short: the toolbar back, whole, and the script's mode off. Only what
// the script itself hid or shrank comes back: a visitor who hid everything with Control-2 keeps it hidden.
function settle() {
  ink.hideGhost();
  if (scriptHid && ink.state.hidden) ink.setHidden(false, { animate: false });
  if (scriptShrank && ink.collapsed && !ink.state.hidden) ink.setCollapsed(false, { animate: false });
  scriptHid = scriptShrank = false;
  $$(".is-down", chip).forEach((key) => key.classList.remove("is-down"));
  wins.forEach((win) => win.classList.remove("is-dragging"));
}
async function loop(signal) {
  while (!signal.aborted) {
    await whenSeen(signal);
    await wait(700, signal);
    if (signal.aborted) break;
    // A round ends early when the stage leaves the screen or the window changes width, and starts again clean.
    roundAbort = new AbortController();
    const stopRound = () => roundAbort.abort();
    signal.addEventListener("abort", stopRound, { once: true });
    await round(roundAbort.signal);
    signal.removeEventListener("abort", stopRound);
    if (signal.aborted) break;
    if (roundAbort.signal.aborted) { settle(); scriptMode("use"); tidy(); }
  }
}
function start() {
  if (run) return;
  const abort = new AbortController();
  const finished = loop(abort.signal).finally(() => {
    settle();
    keepChoice();
    if (run?.abort !== abort) return;
    run = null;
    setRunning(false);
    // Paused in the middle of its own drawing, the stage goes back to the apps.
    if (scripted) { if (ink.state.mode === "draw") ink.setMode("use"); endScript(); }
    invite(true);
  });
  run = { abort, finished };
  setRunning(true);
}
async function stop() {
  if (!run) return;
  const { abort, finished } = run;
  abort.abort();
  setRunning(false);
  await finished;
}
async function restart() {
  await stop();
  ink.setMode("use");
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
  chosen = null;
  userFlat = !motion;
  request();
  if (ink.marks().length) await putAway("pop", tidy); else resetApps();
  if (motion) start(); else still();
}
// Without motion the stage shows the end of the story and waits.
async function still() {
  const steps = drawSteps();
  await ink.perform([...steps.marks, ...steps.note], { cursor: false });
  ink.reflow();
  invite(true);
}
replay.addEventListener("click", () => { if (run) stop(); else restart(); });
done.addEventListener("click", () => ink.setMode("use"));

// The loop stops for good when the visitor takes over: a press on the toolbar or on an app, a key, or Draw by any
// route. It never starts again by itself.
function takeOver(by) {
  const midScript = scripted;
  if (midScript) { scripted = false; desk.classList.remove("is-scripted"); }
  stop();
  if (!userFlat) { userFlat = true; request(); }
  if (by === "apps") { dropAnchors(); if (midScript && ink.state.mode === "draw") ink.setMode("use"); }
  syncMode();
  if (!run) invite(true);
}
ink.toolbar.addEventListener("pointerdown", () => takeOver("toolbar"), true);
for (const name of ["pointerdown", "keydown"]) apps.addEventListener(name, (e) => { if (e.isTrusted) takeOver("apps"); }, true);
ink.on("mode", (mode) => {
  if (mode === "draw" && !scripted) takeOver("draw");
  showMode(mode);
  setKeysMode(mode);
  syncMode();
});
// One of the app's keys pressed while the script holds the stage: the script stops and the key does what it says,
// whatever the script was showing. (This runs before the engine hears the key. Esc in Use apps is the one key the
// engine has nothing to do for, so it only stops the script.)
addEventListener("keydown", (e) => {
  if (!e.isTrusted || !scripted) return;
  const two = e.ctrlKey && !e.metaKey && !e.altKey && e.code === "Digit2";
  if (!two && e.key !== "Escape") return;
  if (two && e.target.closest?.("input, textarea, select") && apps.contains(e.target)) return;
  // What the script hid or shrank comes back first, so Control-2 hides it for the visitor and does not show it.
  settle();
  takeOver("keys");
}, true);

// "Try it": from anywhere on the page, the stage comes to the visitor, flat, in Draw with the Pen.
async function tryIt(event) {
  event?.preventDefault();
  const byKey = event?.detail === 0;
  takeOver("try");
  await stop();
  const top = pageBox(scene).y, bar = header.offsetHeight, room = innerHeight - bar, tall = scene.offsetHeight;
  scrollTo({ top: Math.max(0, tall + 72 < room ? top - bar - (room - tall) / 2 : top - bar - 34), behavior: motion ? "smooth" : "auto" });
  ink.setTool("pen");
  if (byKey) $('[data-tool="pen"]', ink.toolbar)?.focus({ preventScroll: true });
}
for (const el of $$("[data-try]")) el.addEventListener("click", tryIt);
// "Your turn" by keyboard, and a press on the stage in its pose, are controls the growing stage cannot see.
for (const el of [turn, cover]) el.addEventListener("click", (e) => { tryIt(e); stage.join(); });

let stageWidth = innerWidth;
addEventListener("resize", () => {
  setTimeout(() => ink.reflow(), 260);
  if (innerWidth === stageWidth) return;
  stageWidth = innerWidth;
  for (const win of wins) placeWin(win, ...offsets.get(win));
  roundAbort?.abort();
});
new IntersectionObserver(([entry]) => {
  deskSeen = entry.isIntersecting && entry.intersectionRatio >= 0.3;
  if (deskSeen) seenWaiters.splice(0).forEach((go) => go());
  if (entry.isIntersecting) return;
  // Off the screen, the script's round ends, and drawing ends too, so keys and touches go back to the page.
  roundAbort?.abort();
  if (!scripted) ink.setMode("use");
}, { threshold: [0, 0.3] }).observe(scene);

const fontsReady = Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {});
fontsReady.then(async () => {
  if (motion) { await wait(1500); if (!drew && userFlat === false) start(); else if (!run) invite(true); }
  else still();
});

// Tiles --------------------------------------------------------------------------------------------
// A tile flips up when it comes on screen. Tiles that arrive together go one after another, by row and then by
// column. A tile that is already above the screen (the page was opened part of the way down) lands at once.
const whenReady = (tile, then) => {
  if (!motion || tile.classList.contains("is-ready")) then();
  else tile.addEventListener("tile:ready", then, { once: true });
};
if (motion) {
  let waiting = $$("[data-flip]");
  const land = (tile) => { tile.classList.add("is-ready"); tile.dispatchEvent(new Event("tile:ready")); };
  for (const tile of waiting) tile.addEventListener("animationend", (e) => { if (e.target === tile && e.animationName === "flip-up") land(tile); });
  perFrame.push(() => {
    if (!waiting.length) return;
    const edge = scrollY + innerHeight * 0.95, batch = [];
    waiting = waiting.filter((tile) => {
      const box = pageBox(tile);
      if (box.y > edge) return true;
      if (box.y + box.h < scrollY) land(tile); else batch.push({ tile, box });
      return false;
    });
    batch.sort((a, b) => a.box.y - b.box.y || a.box.x - b.box.x);
    batch.forEach(({ tile }, i) => { tile.style.setProperty("--d", `${Math.min(i, 7) * 70}ms`); tile.classList.add("is-in"); });
  });
}

// The tile under a mouse pointer lights its edge near the pointer; tiles with data-tilt also lean toward it.
if (matchMedia("(hover: hover) and (pointer: fine)").matches) {
  let near = null, home = null;
  const leave = () => { near?.classList.remove("is-near"); near = null; };
  document.addEventListener("pointermove", (e) => {
    const tile = e.pointerType === "mouse" && e.target instanceof Element ? e.target.closest(".tile") : null;
    if (tile !== near) {
      leave();
      if (!tile || tile.classList.contains("is-open") || (motion && tile.hasAttribute("data-flip") && !tile.classList.contains("is-ready"))) return;
      near = tile;
      // Measured once, before the tile leans, so the lean cannot feed back into where the pointer seems to be.
      const r = tile.getBoundingClientRect();
      home = { x: r.left + scrollX, y: r.top + scrollY, w: r.width, h: r.height, most: r.width > 700 ? 1.4 : r.width > 420 ? 2.6 : 4.5 };
    }
    if (!near) return;
    const x = clamp((e.pageX - home.x) / home.w, 0, 1), y = clamp((e.pageY - home.y) / home.h, 0, 1);
    near.style.setProperty("--mx", `${(x * 100).toFixed(1)}%`);
    near.style.setProperty("--my", `${(y * 100).toFixed(1)}%`);
    if (motion && near.hasAttribute("data-tilt")) {
      near.style.setProperty("--rx", `${((0.5 - y) * 2 * home.most).toFixed(2)}deg`);
      near.style.setProperty("--ry", `${((x - 0.5) * 2 * home.most).toFixed(2)}deg`);
    }
    near.classList.add("is-near");
  }, { passive: true });
  root.addEventListener("mouseleave", leave);
  addEventListener("blur", leave);
}

// A light runs along a path, once: through the gaps of a section's grid as the section arrives.
let lights = 0;
function runLight(host, d, length, width) {
  if (!motion) return;
  const id = `run-${++lights}`, dash = clamp(260 / length, 0.08, 0.34).toFixed(3);
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", "run");
  svg.setAttribute("aria-hidden", "true");
  svg.style.setProperty("--run-ms", `${Math.round(clamp(length * 0.75, 1000, 2400))}ms`);
  svg.innerHTML = `<defs><linearGradient id="${id}" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="${width}" y2="0"><stop offset="0" style="stop-color: var(--neon-a)"/><stop offset=".5" style="stop-color: var(--neon-b)"/><stop offset="1" style="stop-color: var(--neon-c)"/></linearGradient></defs><path d="${d}" pathLength="1" stroke="url(#${id})" style="stroke-dasharray: ${dash} 2; --run-from: ${dash}"/>`;
  host.append(svg);
  svg.addEventListener("animationend", () => svg.remove());
  requestAnimationFrame(() => svg.classList.add("is-on"));
}
// Along a section's top edge, down the gap beside its first tile, then along the first gap that crosses the whole
// grid. On one column it goes round the first tile.
function gridLight(bento) {
  const gap = parseFloat(getComputedStyle(bento).columnGap) || 14, h = gap / 2, r = Math.min(h, 8), W = bento.clientWidth, H = bento.clientHeight;
  const boxes = $$(".tile", bento).map((tile) => localBox(tile, bento)), first = boxes[0], x = first.x + first.w + h;
  if (x > W) {
    const y = first.y + first.h + h;
    runLight(bento, `M${-h} ${-h}H${W + h - r}Q${W + h} ${-h} ${W + h} ${r - h}V${y - r}Q${W + h} ${y} ${W + h - r} ${y}H${-h}`, 2 * (W + gap) + y + h, W);
    return;
  }
  const across = boxes.filter((b) => b.x < x && b.x + b.w > x).map((b) => b.y - h), y = across.length ? Math.min(...across) : H + h;
  runLight(bento, `M${-h} ${-h}H${x - r}Q${x} ${-h} ${x} ${r - h}V${y - r}Q${x} ${y} ${x + r} ${y}H${W + h}`, W + gap + y + h, W);
}
if (motion) {
  const arrive = new IntersectionObserver((entries) => {
    for (const entry of entries) if (entry.isIntersecting) { arrive.unobserve(entry.target); setTimeout(gridLight, 520, entry.target); }
  }, { rootMargin: "0px 0px -16% 0px" });
  $$("[data-run]").forEach((bento) => arrive.observe(bento));
}

// 2. What you can do -------------------------------------------------------------------------------
// Each tile's picture is a small sample screen that the engine draws on. A scene gets its layer, its screen, a way
// to name targets on it and the size of one em there.
const measure = document.createElement("canvas").getContext("2d");
const textSize = (shot) => Math.round(clamp(shot.clientWidth * 0.06, 19, 38));
const SCENES = {
  // A ring round a number, then a curved arrow to the dip it explains.
  point: ({ layer, t, em, cursor }) => layer.perform([
    { tool: "ellipse", around: t("kpi"), padX: em * 0.75, padY: em * 0.5, size: 3, rough: 6, duration: 760, pause: 260 },
    { tool: "arrow", from: { el: t("kpi"), at: "bottom", dx: em * 0.3, dy: em * 0.9 }, to: { el: t("dip"), at: "top", dx: -em * 0.1, dy: -em * 0.55 }, bend: -0.3, size: 3, rough: 6, duration: 700 },
  ], { cursor }),
  // A rough box that Smart Draw makes neat, an arrow, and a typed note.
  box: async ({ layer, t, em, cursor }) => {
    const around = { tool: "rect", around: t("btn"), pad: em * 0.45, size: 3 };
    const [rough] = await layer.perform([{ ...around, rough: 7, duration: 760 }], { cursor });
    await wait(motion ? 300 : 0);
    if (rough && layer.marks().includes(rough)) {
      const b = layer.box(rough);
      layer.remove(rough.id);
      layer.add({ type: "rect", x: b.x, y: b.y, w: b.w, h: b.h, size: 3, rough: 0, round: 6, anchor: around });
    }
    await wait(motion ? 320 : 0);
    await layer.perform([
      { tool: "arrow", from: { el: t("note"), at: "left", dx: em * 2.4 }, to: { el: t("btn"), at: "right", dx: em * 0.45 + 9 }, bend: 0.22, size: 3, duration: 520, pause: 160 },
      { tool: "text", at: { el: t("note"), at: "top left", dx: em * 2.7, dy: -em * 0.5 }, text: "too small", color: BLUE, speed: 46 },
    ], { cursor });
  },
  blur: ({ layer, t, cursor }) => layer.perform([
    { tool: "blur", over: t("email"), pad: 5, amount: 13, duration: 700, pause: 380 },
    { tool: "blur", over: t("card"), pad: 5, amount: 13, duration: 700 },
  ], { cursor }),
  // The spotlight follows the pointer from picture to picture. The engine dims around its own pointer's place, which
  // a scene has to give it: --px and --py on the layer, and has-pointer for "the pointer is here".
  spot: async ({ layer, el, shot, em, cursor }) => {
    const middle = (name) => { const b = localBox(el(name), shot); return [b.x + b.w / 2, b.y + b.h * 0.44]; };
    const point = (p) => {
      layer.layer.style.setProperty("--px", `${p[0].toFixed(1)}px`);
      layer.layer.style.setProperty("--py", `${p[1].toFixed(1)}px`);
      if (cursor) layer.placeGhost(p);
    };
    let at = middle(cursor ? "g1" : "g5");
    layer.setStyle("spotlight", { radius: Math.round(em * 3.1) });
    point(at);
    layer.layer.classList.add("has-pointer");
    layer.setSpotlight(true);
    if (!cursor) return;
    for (const name of ["g5", "g3", "g6", "g2"]) {
      await wait(430);
      const from = at, to = middle(name);
      await glide(560, (k) => point([from[0] + (to[0] - from[0]) * k, from[1] + (to[1] - from[1]) * k]));
      at = to;
    }
  },
  steps: ({ layer, t, cursor }) => layer.perform(["p1", "p2", "p3"].map((pin, i) => ({ tool: "counter", at: { el: t(pin) }, n: i + 1, color: BLUE, pause: 360 })), { cursor }),
  system: ({ layer, t, el, shot, cursor }) => {
    // A label sits in the middle of its shape, whatever size the hand-written text is at this width.
    const label = (pin, text, color) => ({ tool: "text", text, color, speed: 50, pause: 120, at: () => {
      const b = localBox(el(pin), shot), size = textSize(shot);
      measure.font = `600 ${size}px "Caveat"`;
      return [b.x + b.w / 2 - measure.measureText(text).width / 2 - 4, b.y + b.h / 2 - size * 0.56];
    } });
    const link = (a, b) => ({ tool: "arrow", from: { el: t(a), at: "right", dx: 7 }, to: { el: t(b), at: "left", dx: -9 }, bend: 0, color: PURPLE, size: 3, duration: 380, pause: 100 });
    return layer.perform([
      { tool: "rect", around: t("n1"), pad: 0, color: BLUE, size: 3, duration: 520 }, label("n1", "app", BLUE), link("n1", "n2"),
      { tool: "rect", around: t("n2"), pad: 0, color: TEAL, size: 3, duration: 520 }, label("n2", "API", TEAL), link("n2", "n3"),
      { tool: "ellipse", around: t("n3"), pad: 0, color: CORAL, size: 3, duration: 620 }, label("n3", "data", CORAL),
    ], { cursor });
  },
};
function sketch(card, shot) {
  const layer = createInk({ surface: shot, interactive: false, gradient: "neon" });
  const el = (name) => $(`[data-t="${name}"]`, shot), t = (name) => standIn(layer, () => localBox(el(name), shot));
  const fit = () => layer.layer.style.setProperty("--ink-text", `600 ${textSize(shot)}px/1.1 "Caveat", "Bradley Hand", cursive`);
  const wipe = () => { layer.setSpotlight(false); layer.layer.classList.remove("has-pointer"); layer.clear(); };
  let busy = false, shown = false, again = false;
  async function play() {
    if (busy) { again = true; return; }
    busy = true;
    wipe();
    shot.classList.remove("is-leaving");
    fit();
    await fontsReady;
    await wait(motion ? 260 : 0);
    await SCENES[shot.dataset.scene]({ layer, shot, el, t, em: parseFloat(getComputedStyle(shot).fontSize), cursor: motion });
    await wait(motion ? 500 : 0);
    layer.hideGhost();
    layer.reflow();
    busy = false;
    if (again) { again = false; play(); } else if (!shown && motion) leave();
  }
  // A tile that has gone off screen loses its drawing quietly, so it starts clean the next time.
  async function leave() {
    shot.classList.add("is-leaving");
    await wait(280);
    if (!shown && !busy) wipe();
  }
  addEventListener("resize", () => { fit(); setTimeout(() => layer.reflow(), 260); });
  $(".ctl", card).addEventListener("click", play);
  if (motion) $(".frame", card).addEventListener("pointerenter", (e) => { if (e.pointerType === "mouse" && shown && !busy) play(); });
  return { play, show() { if (shown) return; shown = true; play(); }, hide() { if (!shown) return; shown = false; if (motion && !busy) leave(); } };
}
const cards = $$(".do-bento .card");
const visuals = cards.map((card) => sketch(card, $(".shot", card)));
// A visual plays while its tile is on screen. While one tile is open, only that one plays.
const seen = cards.map(() => !motion);
let openCard = null;
const syncVisuals = () => cards.forEach((card, i) => { if (seen[i] && (!openCard || openCard === card)) visuals[i].show(); else visuals[i].hide(); });
if (motion) {
  const watch = new IntersectionObserver((entries) => {
    for (const entry of entries) seen[cards.indexOf(entry.target.closest(".card"))] = entry.isIntersecting;
    syncVisuals();
  }, { threshold: 0.5 });
  cards.forEach((card) => whenReady(card, () => watch.observe($(".frame", card))));
} else syncVisuals();

// A tile opens in place: it leaves a slot of its own size behind, covers the bento and plays its visual as large
// as it goes. The move is a view transition (the tile, its picture and its words each travel); where the browser
// has none, the tile grows from where it was. Its button, a press outside it or Esc puts it back.
const doBento = $(".do-bento"), indexButtons = $$(".do-index button");
const toggleOf = (card) => $(".tile-open", card);
let slot = null, returnTo = null, morphs = 0;
function place(card) {
  const b = doBento.getBoundingClientRect(), h = card.offsetHeight, top = (innerHeight + header.offsetHeight - h) / 2 - b.top;
  card.style.setProperty("--open-top", `${Math.round(clamp(top, 0, Math.max(0, doBento.offsetHeight - h)))}px`);
}
function applyOpen(card) {
  const from = openCard;
  if (from) {
    from.classList.remove("is-open");
    from.style.removeProperty("--open-top");
    slot?.remove();
    slot = null;
    const button = toggleOf(from);
    button.setAttribute("aria-expanded", "false");
    button.setAttribute("aria-label", `Show ${button.dataset.name} bigger`);
  }
  if (card) {
    slot = document.createElement("i");
    slot.className = "tile-slot";
    slot.dataset.area = card.dataset.area;
    slot.setAttribute("aria-hidden", "true");
    slot.style.height = `${card.offsetHeight}px`;
    card.before(slot);
    card.classList.remove("is-near", "is-lit");
    card.classList.add("is-open");
    place(card);
    const button = toggleOf(card);
    button.setAttribute("aria-expanded", "true");
    button.setAttribute("aria-label", `Close ${button.dataset.name}`);
  }
  openCard = card;
  doBento.classList.toggle("has-open", Boolean(card));
  for (const tile of $$(".tile", doBento)) tile.inert = Boolean(card) && tile !== card;
  indexButtons.forEach((button, i) => button.setAttribute("aria-current", String(cards[i] === card)));
  syncVisuals();
}
function setOpen(card, { animate = true, focus = false } = {}) {
  if (card === openCard) return;
  const moving = card || openCard, turnNow = ++morphs;
  const parts = [[moving, "do-tile"], [$(".frame", moving), "do-frame"], [$(".card-text", moving), "do-text"]];
  const settleOpen = () => {
    // The visual plays again at its new size.
    visuals[cards.indexOf(moving)].play();
    if (card) {
      if (focus) toggleOf(card).focus({ preventScroll: true });
      card.scrollIntoView({ block: "nearest", behavior: motion ? "smooth" : "auto" });
    } else if (returnTo) { returnTo.focus({ preventScroll: true }); returnTo = null; }
  };
  if (!motion || !animate) { applyOpen(card); settleOpen(); return; }
  if (document.startViewTransition) {
    for (const [part, name] of parts) part.style.viewTransitionName = name;
    const transition = document.startViewTransition(() => applyOpen(card));
    const stopPassing = passThrough(transition);
    const end = () => { stopPassing(); if (turnNow === morphs) for (const [part] of parts) part.style.viewTransitionName = ""; settleOpen(); };
    transition.finished.then(end, end);
    return;
  }
  const before = moving.getBoundingClientRect();
  applyOpen(card);
  const after = moving.getBoundingClientRect();
  moving.animate([
    { transformOrigin: "0 0", transform: `translate(${before.left - after.left}px, ${before.top - after.top}px) scale(${before.width / after.width}, ${before.height / after.height})` },
    { transformOrigin: "0 0", transform: "none" },
  ], { duration: 420, easing: appCurve.enter });
  settleOpen();
}
cards.forEach((card) => {
  toggleOf(card).addEventListener("click", () => setOpen(openCard === card ? null : card));
  // A press anywhere on a closed tile opens it too, except on its own controls.
  card.addEventListener("click", (e) => { if (openCard !== card && !e.target.closest("button, a")) setOpen(card, { focus: true }); });
});
indexButtons.forEach((button, i) => {
  button.addEventListener("click", () => { returnTo = button; setOpen(cards[i], { focus: true }); });
  for (const [name, lit] of [["pointerenter", true], ["pointerleave", false], ["focus", true], ["blur", false]]) button.addEventListener(name, () => cards[i].classList.toggle("is-lit", lit));
});
doBento.addEventListener("click", (e) => { if (openCard && e.target === doBento) setOpen(null); });
addEventListener("keydown", (e) => { if (e.key === "Escape" && openCard) setOpen(null); });
let lastWidth = innerWidth;
addEventListener("resize", () => {
  if (innerWidth === lastWidth) { if (openCard) place(openCard); return; }
  lastWidth = innerWidth;
  if (openCard) setOpen(null, { animate: false });
});
new IntersectionObserver(([entry]) => { if (!entry.isIntersecting && openCard) setOpen(null, { animate: false }); }).observe(doBento);

// 3. Two keys --------------------------------------------------------------------------------------
const keysSection = $("#keys"), modeLine = $("[data-mode-line]");
function setKeysMode(mode) {
  keysSection.dataset.mode = mode;
  modeLine.textContent = mode === "draw" ? "Right now you're drawing." : "Right now you're using the app.";
}
const caps = $$(".key");
const capFor = (e) => caps.find((cap) => cap.dataset.key === e.key || cap.dataset.key === e.code);
addEventListener("keydown", (e) => capFor(e)?.classList.add("is-down"));
addEventListener("keyup", (e) => {
  capFor(e)?.classList.remove("is-down");
  // A key let go while another window had the focus never sends its keyup; letting go of a modifier clears the rest.
  if (!e.ctrlKey && !e.shiftKey) caps.forEach((cap) => cap.classList.remove("is-down"));
});
addEventListener("blur", () => caps.forEach((cap) => cap.classList.remove("is-down")));
for (const set of $$(".keyset")) set.addEventListener("click", async () => {
  const keys = $$(".key", set), draws = set.dataset.set === "draw";
  takeOver("keys");
  for (const key of keys) { key.classList.add("is-down"); await wait(motion ? 70 : 0); }
  // As in the app: Control-Shift-2 only starts drawing, Esc only goes back.
  ink.setMode(draws ? "draw" : "use");
  await wait(160);
  keys.forEach((key) => key.classList.remove("is-down"));
});

// 4. More in the app -------------------------------------------------------------------------------
// The toolbar in "A toolbar that's yours" is the app's own, small, and only for looking at: it shrinks to its one
// button and grows back while it is on screen.
const miniHost = $("#mini-bar"), miniTile = miniHost.parentElement;
const mini = createInk({ surface: miniHost, toolbarParent: miniHost, toolbarPosition: "absolute", appearance: root.dataset.theme, gradient: "neon", tool: "arrow", properties: false,
  tools: ["select", "rect", "ellipse", "arrow", "pen", "text"], hiddenControls: ["library", "board", "redo", "clearAll", "properties", "settings"] });
themed.push(mini);
// It hears the page's keys like any toolbar; it must not start drawing in its small tile.
mini.on("mode", (mode) => { if (mode === "draw") mini.setMode("use"); });
// Control-2 is for the demo at the top; this one stays.
mini.on("shortcut", (name) => { if (name === "hide") mini.setHidden(false, { animate: false }); });
let miniSize = [520, 76];
const fitMini = () => {
  if (!miniTile.clientWidth) return;
  miniHost.style.height = `${miniSize[1] + 24}px`;
  miniHost.style.setProperty("--fit", Math.min(1, (miniTile.clientWidth - 28) / miniSize[0], (miniTile.clientHeight - 24) / miniSize[1]).toFixed(3));
};
mini.on("layout", ({ width, height }) => { if (height && !mini.collapsed) { miniSize = [width, height]; fitMini(); } });
new ResizeObserver(fitMini).observe(miniTile);
mini.fit();
if (motion) setInterval(() => { if (miniTile.classList.contains("is-live") && !document.hidden) mini.setCollapsed(!mini.collapsed); }, 2400);
// More than forty tools: forty-one small squares in the section's first tile.
const moreGrid = document.createElement("div");
moreGrid.className = "more-grid";
moreGrid.setAttribute("aria-hidden", "true");
for (let i = 0; i < 41; i++) moreGrid.append(Object.assign(document.createElement("i"), { style: `--i: ${i}` }));
$(".also-head").append(moreGrid);

// Loops run only while they are on screen.
const live = new IntersectionObserver((entries) => {
  for (const entry of entries) entry.target.classList.toggle("is-live", entry.isIntersecting);
}, { threshold: 0.3 });
$$(".ill, .cup-tile, .also-head").forEach((el) => live.observe(el));

// The break timer counts down a few seconds, then starts again.
const timer = $("[data-timer]");
if (motion) {
  let left = 600;
  setInterval(() => {
    if (!timer.closest(".ill").classList.contains("is-live")) return;
    left = left <= 591 ? 600 : left - 1;
    timer.textContent = `${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")}`;
  }, 1000);
}

// 6. Coffee ----------------------------------------------------------------------------------------
// The cup brews once, when it comes on screen: its tile's border fills and the cup fills with it.
const cup = $(".cup-tile");
const setBrew = (p) => { cup.style.setProperty("--p", p.toFixed(3)); cup.classList.toggle("is-brewed", p >= 1); };
if (motion) {
  setBrew(0);
  whenReady(cup, () => new IntersectionObserver(([entry], watch) => {
    if (!entry.isIntersecting) return;
    watch.disconnect();
    const began = performance.now(), tick = (now) => {
      const p = clamp((now - began) / 3600, 0, 1);
      setBrew(p);
      if (p < 1) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }, { threshold: 0.6 }).observe(cup));
} else setBrew(1);

// 7. Install ---------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const status = $("#status");
for (const button of $$("[data-copy]")) {
  let timeout = 0;
  button.addEventListener("click", async () => {
    const command = button.closest(".command"), code = $("code", command), tile = button.closest(".tile");
    let copied = true;
    try { await navigator.clipboard.writeText(code.textContent.trim()); }
    catch {
      const range = document.createRange();
      range.selectNodeContents(code);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      copied = document.execCommand("copy");
    }
    button.textContent = copied ? "Copied" : "Copy";
    command.classList.toggle("is-copied", copied);
    tile.classList.toggle("is-lit", copied);
    status.textContent = copied ? "Copied. Paste it into Terminal and press Enter." : "That didn't copy. The command is selected, so you can copy it yourself.";
    clearTimeout(timeout);
    timeout = setTimeout(() => { button.textContent = "Copy"; status.textContent = ""; command.classList.remove("is-copied"); tile.classList.remove("is-lit"); }, 4200);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

matchMedia("(prefers-reduced-motion: reduce)").addEventListener("change", () => location.reload());
request();
