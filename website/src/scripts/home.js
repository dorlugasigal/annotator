// The homepage: the live demo under the headline, the cards of What you can do, and the coffee and install
// sections. The header, the theme switch and the headings' arrivals are every page's (site.ts).
import { createInk } from "../engine/ink.js";
import { growingStage } from "../engine/stage.js";
// The app's Zoom, Shapes, Share region and Break timer as small live scenes, for "What you can do".
import { zoomScene, shapesScene, breakTimerScene, shareRegionScene } from "../engine/scenes.js";
// The app's own motion: its curves, its Pop and its Magic flight.
import { css as curve, poseKeyframes, magicFlight } from "../engine/app-motion.js";
import { R, translate, freshToken } from "../engine/annotation-core.js";

const root = document.documentElement;
const motion = root.classList.contains("motion");
// The page eases with the app's curves: "enter" for what arrives, "standard" for what moves between two states.
root.style.setProperty("--ease-out", curve.enter);
root.style.setProperty("--ease-in-out", curve.standard);
const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const smooth = (t) => { const x = clamp(t, 0, 1); return x * x * (3 - 2 * x); };
// A wait that ends early when its signal is aborted.
// The script's signal lives as long as the loop, so each wait takes its listener off again when it ends.
const wait = (ms, signal) => new Promise((done) => {
  if (signal?.aborted || ms <= 0) { done(); return; }
  const end = () => { clearTimeout(timer); signal?.removeEventListener("abort", end); done(); };
  const timer = setTimeout(end, ms);
  signal?.addEventListener("abort", end, { once: true });
});
// Runs `step` with 0 to 1 over `ms`. Without motion, or once aborted, it goes straight to the end.
function tween(ms, step, signal) {
  if (!motion || ms <= 0) { step(1); return Promise.resolve(); }
  return new Promise((done) => {
    const start = performance.now();
    const frame = (now) => {
      const t = signal?.aborted ? 1 : clamp((now - start) / ms, 0, 1);
      step(smooth(t));
      if (t < 1) requestAnimationFrame(frame); else done();
    };
    requestAnimationFrame(frame);
  });
}
const status = $("#status");

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
    const o = ink.layer.getBoundingClientRect(), b = box();
    return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h };
  },
});

// Scroll-driven values -----------------------------------------------------------------------------
// One handler, one frame at a time. Each channel has a goal read from the page and a shown value that follows it
// with a short lag, so a mouse wheel's steps become one smooth move. Goals are read first, then styles written.
const channels = [];
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
  if (moving) request(); else lastFrame = 0;
}
function request() { if (!queued) { queued = true; requestAnimationFrame(frame); } }
addEventListener("scroll", request, { passive: true });
addEventListener("resize", request);
// A tab that comes back to the front catches up with wherever the page is.
document.addEventListener("visibilitychange", () => { if (!document.hidden) { lastFrame = 0; request(); } });

// 1. The stage -------------------------------------------------------------------------------------
const desk = $("#desk"), dock = $("#dock"), scene = $(".stage-scene"), apps = $("#apps");
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset), with the app's
// Aurora gradient, the page's accent, as the color, curved arrows, the roughest line. It rests on the Arrow tool so its
// properties row shows that. The engine itself starts the row closed on a phone, which keeps the bar compact there.
const accent = "aurora";
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, tool: "arrow", gradient: accent, moreHref: "#install" });
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the desk's
// frame opens up to fill the window, and scrolling brings it back. The desk itself does not move: in index.html the
// panel, the cue's ring and the dock carry data-stage-frame and the mode chip data-stage-corner.
const stage = growingStage({ ink, surface: desk, tries: "[data-try]", motion: () => motion, invite: "Draw here" });
// Joining in ends the script and lays the desk flat (the frame grows only once it is).
stage.on("join", () => takeOver());

// The headline's mark ------------------------------------------------------------------------------
// The headline does what it says: the drawing engine marks "screen." once its words have landed, in the accent, the
// way the app draws. Its own small layer takes no input and has no toolbar. The mark is fitted to the word's letters,
// measured from the font rather than its line box, so it sits round them and never crosses them.
const heroWord = $(".hero-word"), heroHost = $(".hero-ink");
const heroInk = createInk({ surface: heroHost, interactive: false, appearance: root.dataset.theme, gradient: accent });
let measure = null;
function wordInk() {
  const style = getComputedStyle(heroWord), size = parseFloat(style.fontSize), text = heroWord.textContent;
  measure ||= document.createElement("canvas").getContext("2d");
  measure.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
  if ("letterSpacing" in measure) measure.letterSpacing = style.letterSpacing;
  const t = measure.measureText(text), line = parseFloat(style.lineHeight) || size;
  const baseline = (line - (t.fontBoundingBoxAscent + t.fontBoundingBoxDescent)) / 2 + t.fontBoundingBoxAscent;
  const x = heroWord.offsetLeft - heroHost.offsetLeft, y = heroWord.offsetTop - heroHost.offsetTop;
  return { x: x - t.actualBoundingBoxLeft, y: y + baseline - t.actualBoundingBoxAscent, w: t.actualBoundingBoxLeft + t.actualBoundingBoxRight, h: t.actualBoundingBoxAscent + t.actualBoundingBoxDescent };
}
function heroSteps(duration) {
  const letters = wordInk(), size = clamp(letters.h * 0.1, 3, 5.5);
  // An ellipse a little wider than the word, and just tall enough that its corners stay inside with room to spare.
  const px = letters.h * 0.5, a = letters.w / 2 + px, b = letters.h / 2 / Math.sqrt(Math.max(0.05, 1 - (letters.w / 2 / a) ** 2)) * 0.94;
  return [{ tool: "ellipse", around: standIn(heroInk, wordInk), padX: px, padY: b - letters.h / 2, rough: 0, size, duration }];
}

// Theme --------------------------------------------------------------------------------------------
// The switch is every page's (site.ts). The drawings and the scenes under "What you can do" follow it, while the new
// theme is put in place, so they are part of its reveal.
const liveScenes = [];
document.addEventListener("site:theme", ({ detail: { theme } }) => {
  ink.setAppearance(theme);
  heroInk.setAppearance(theme);
  for (const scene of liveScenes) scene.setAppearance(theme);
});

// The desk keeps room under the windows for the toolbar at its tallest (its properties row open), so the bar never
// covers a window and opening or closing the row does not move what the visitor drew on.
const OPEN_HEIGHT = { "expanded/1": 145, "minimal/1": 113, "minimal/2": 145 };
let reserved = 0, barShape = "";
ink.on("layout", ({ height, size, rows }) => {
  if (!height || ink.collapsed) return;
  const shape = `${size}/${rows}`, phone = matchMedia("(max-width: 599px)").matches;
  const room = Math.max(height, phone ? 0 : OPEN_HEIGHT[shape] || 0, shape === barShape ? reserved : 0);
  barShape = shape;
  if (room === reserved) return;
  reserved = room;
  requestAnimationFrame(() => desk.style.setProperty("--tb-h", `${room}px`));
});
ink.fit();

// The lean. Fully leaning at the top of the page, flat once the stage has come up the screen. As soon as the visitor
// draws, it goes flat and stays flat: a pointer on a tilted plane would not draw where it points.
let userFlat = !motion;
const lean = channel(() => {
  if (userFlat) return 0;
  const top = scene.getBoundingClientRect().top + scrollY;
  return 1 - smooth(scrollY / clamp(top - innerHeight * 0.1, 200, innerHeight * 0.7));
}, (v) => {
  desk.style.setProperty("--tilt", v.toFixed(4));
  desk.style.setProperty("--lift", (4 * v * (1 - v)).toFixed(4));
  desk.classList.toggle("is-flat", v === 0);
});

// The sample apps ----------------------------------------------------------------------------------
// Two small apps with made-up content: a settings window and a message. Nothing leaves the page. In Use apps they
// answer the visitor; in Draw they are inert and the pointer is the tool. Drawings never follow what the apps do:
// that is how the real app behaves.
const switches = $$("[data-switch]"), save = $("[data-save]"), weekly = $('[data-switch="weekly"]'), askEl = $("#t-ask");
function setSwitch(button, on) { button.setAttribute("aria-checked", String(on)); }
function setSaved(saved) { save.dataset.saved = String(saved); }
for (const button of switches) button.addEventListener("click", () => { used(); setSwitch(button, button.getAttribute("aria-checked") !== "true"); setSaved(false); });
save.addEventListener("click", () => { used(); setSaved(true); });
// The sidebar: General and Privacy are quiet placeholders.
const panes = $$("[data-pane]"), paneTitle = $("[data-pane-title]"), paneRows = $("[data-pane-rows]"), paneBlank = $("[data-pane-blank]");
function showPane(button) {
  panes.forEach((b) => b.setAttribute("aria-pressed", String(b === button)));
  paneTitle.textContent = button.textContent;
  const notifications = button.dataset.pane === "notifications";
  paneRows.hidden = !notifications;
  paneBlank.hidden = notifications;
}
panes.forEach((button) => button.addEventListener("click", () => { used(); showPane(button); }));

const thread = $("[data-thread]"), reply = $("[data-reply]"), replyInput = reply.elements.reply;
function send(text) {
  const words = text.trim();
  if (!words) return;
  const bubble = document.createElement("li");
  bubble.className = "me";
  bubble.textContent = words;
  thread.append(bubble);
  // The thread keeps the first message and the last few replies.
  while (thread.children.length > 5) thread.children[1].remove();
  thread.scrollTop = thread.scrollHeight;
}
reply.addEventListener("submit", (e) => { e.preventDefault(); used(); send(replyInput.value); replyInput.value = ""; });

// Windows move by their title bars, with the pointer or the arrow keys, and stay on the stage above the toolbar. A
// grown stage gives them the whole frame; as it shrinks it gathers them back in.
const windows = $$("[data-win]"), offsets = new WeakMap();
let front = 1;
function moveWindow(win, dx, dy) {
  const base = localBox(win, desk), edge = 6, style = getComputedStyle(desk), r = stage.room;
  const floor = desk.clientHeight + r.bottom - parseFloat(style.getPropertyValue("--tb-h")) - parseFloat(style.getPropertyValue("--tb-bottom")) - edge;
  const x = clamp(dx, edge - r.left - base.x, Math.max(edge - r.left - base.x, desk.clientWidth + r.right - edge - base.w - base.x));
  const y = clamp(dy, edge - r.top - base.y, Math.max(edge - r.top - base.y, floor - base.h - base.y));
  offsets.set(win, [x, y]);
  win.style.setProperty("--dx", `${x}px`);
  win.style.setProperty("--dy", `${y}px`);
}
for (const win of windows) {
  const bar = $("[data-drag]", win);
  let grab = null;
  bar.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    const [x, y] = offsets.get(win) || [0, 0];
    grab = { id: e.pointerId, x: e.clientX, y: e.clientY, dx: x, dy: y };
    bar.setPointerCapture(e.pointerId);
    win.style.zIndex = ++front;
    win.classList.add("is-moving");
    if (e.isTrusted) takeOver(false);
    used();
  });
  bar.addEventListener("pointermove", (e) => { if (grab?.id === e.pointerId) moveWindow(win, grab.dx + e.clientX - grab.x, grab.dy + e.clientY - grab.y); });
  const drop = (e) => { if (grab?.id === e.pointerId) { grab = null; win.classList.remove("is-moving"); } };
  bar.addEventListener("pointerup", drop);
  bar.addEventListener("pointercancel", drop);
  bar.addEventListener("keydown", (e) => {
    const step = { ArrowLeft: [-16, 0], ArrowRight: [16, 0], ArrowUp: [0, -16], ArrowDown: [0, 16] }[e.key];
    if (!step) return;
    e.preventDefault();
    used();
    const [x, y] = offsets.get(win) || [0, 0];
    win.style.zIndex = ++front;
    moveWindow(win, x + step[0], y + step[1]);
  });
}
const refit = () => windows.forEach((win) => { const [x, y] = offsets.get(win) || [0, 0]; if (x || y) moveWindow(win, x, y); });
addEventListener("resize", refit);
stage.on("change", refit);
function resetApps() {
  showPane($('[data-pane="notifications"]'));
  switches.forEach((button) => setSwitch(button, button.dataset.first === "true"));
  setSaved(false);
  $$(".me", thread).forEach((bubble) => bubble.remove());
  replyInput.value = "";
  windows.forEach((win) => { offsets.delete(win); win.style.removeProperty("--dx"); win.style.removeProperty("--dy"); win.style.zIndex = ""; });
}
// Marks the script drew follow their targets when the page reflows, until an app is used: from then on they stay
// where they are on the screen, whatever changes under them.
function used() { for (const mark of ink.marks()) mark.anchor = null; ties.clear(); }
// Joining in starts drawing at once: the script stops, the stage lies flat, and the Pen is picked in Draw, with the
// toolbar open, whatever the script was showing.
function joinDrawing() {
  takeOver();
  ink.hideGhost();
  if (ink.state.hidden) ink.setHidden(false, { animate: false });
  if (ink.collapsed) ink.setCollapsed(false);
  ink.setTool("pen");
}
// A mouse or pen press on the stage while the script plays is the visitor's first stroke: it goes to the drawing
// layer as it is. (A window's title bar still moves the window.) A finger may be scrolling the page, so a touch
// joins in on its click instead, without drawing.
desk.addEventListener("pointerdown", (e) => {
  if (!e.isTrusted || !scripted() || e.button !== 0 || e.pointerType === "touch") return;
  if (ink.toolbar.contains(e.target) || e.target.closest?.("[data-drag], .stage-shrink")) return;
  e.preventDefault();
  joinDrawing();
  ink.layer.dispatchEvent(new PointerEvent("pointerdown", e));
}, true);
desk.addEventListener("click", (e) => {
  if (!e.isTrusted || !run || ink.toolbar.contains(e.target) || e.target.closest?.("[data-drag], .stage-shrink")) return;
  if (ink.state.mode !== "draw" || ink.state.tool !== "pen") joinDrawing(); else takeOver();
}, true);
apps.addEventListener("keydown", (e) => { if (e.isTrusted && e.key !== "Tab") takeOver(false); }, true);
resetApps();

// Where things are on the stage's own plane: an element in a window (with the window's move), or a toolbar button.
function planeBox(el) {
  const bar = ink.toolbar;
  if (bar.contains(el)) {
    let x = 0, y = 0, node = el;
    while (node && node !== bar) { x += node.offsetLeft; y += node.offsetTop; node = node.offsetParent; }
    // The toolbar is centred with a translate, which the layout's offsets leave out.
    const left = bar.offsetLeft - (bar.classList.contains("is-placed") ? 0 : bar.offsetWidth / 2);
    return { x: left + x, y: bar.offsetTop + y, w: el.offsetWidth, h: el.offsetHeight };
  }
  const box = localBox(el, desk), win = el.closest("[data-win]"), [dx, dy] = (win && offsets.get(win)) || [0, 0];
  return { x: box.x + dx, y: box.y + dy, w: box.w, h: box.h };
}
// The tool the visitor chose. A script that shows its tools in the toolbar puts back the tool it started with when
// it ends; if the visitor picked one meanwhile, theirs is put back instead.
let chosen = null;
ink.on("tool", (tool) => { if (!scripted()) chosen = tool; });
const keepChoice = () => { if (chosen && ink.state.tool !== chosen) ink.setTool(chosen, ink.state.mode !== "draw"); };

const replay = $("#stage-replay"), done = $("#stage-done");
const modeButton = $('[data-action="mode"]', ink.toolbar), pill = $(".ink-pill", ink.toolbar);
let run = null, appsOnScreen = false, seenWaiters = [];
const appsSeen = () => appsOnScreen && !document.hidden;
// True once the visitor has done something on the stage. Until then, while the script plays, every change of mode
// is the script's own.
let acted = false;
let building = false;
const scripted = () => (Boolean(run) || building) && !acted;
// The engine's own Shrink, show and hide and pointer click, in the app's motion. An engine without them changes
// state at once, and the page fades the drawings itself.
const appMotion = typeof ink.shrink === "function";
const setRunning = (running) => {
  replay.dataset.state = running ? "playing" : "";
  $("b", replay).textContent = running ? "Pause" : "Replay";
  // While the script plays, Draw is shown but the stage takes no presses: a swipe over it still scrolls the page.
  desk.classList.toggle("is-scripted", running);
};
// The script runs only while its stage can be seen: off screen, or with the tab in the background, it holds at the
// next pause and goes on from there.
document.addEventListener("visibilitychange", () => { if (appsSeen()) seenWaiters.splice(0).forEach((go) => go()); });
const whenSeen = (signal) => new Promise((go) => {
  if (appsSeen() || signal.aborted) { go(); return; }
  const end = () => { signal.removeEventListener("abort", end); seenWaiters.splice(seenWaiters.indexOf(end) >>> 0, 1); go(); };
  seenWaiters.push(end);
  signal.addEventListener("abort", end, { once: true });
});

// Hide and show as Control-2 does, in the app's own default style (Slide).
let scriptHid = false;
async function appHide(hidden, signal) {
  scriptHid = hidden;
  if (appMotion) { await ink.setHidden(hidden); return; }
  if (hidden) {
    desk.classList.add("is-wiping");
    await wait(320, signal);
    ink.setHidden(true);
  } else {
    ink.setHidden(false);
    desk.classList.remove("is-wiping");
    await wait(320, signal);
  }
}
async function wipe(signal) {
  if (!ink.marks().length) return;
  desk.classList.add("is-wiping");
  await wait(340, signal);
  if (!signal?.aborted) ink.clear();
  desk.classList.remove("is-wiping");
}
// A pause in the script, which also waits while the stage cannot be seen.
async function hold(ms, signal) { await wait(ms, signal); await whenSeen(signal); }
// The scripted demo ----------------------------------------------------------------------------------
// One round is the call the page is about: Dor asks where to turn off the weekly emails, and you show him. Every
// mark is made the way a person makes it in the app: the pointer presses the toolbar's own buttons and draws on the
// stage with pointer events, and the engine does the rest (Smart Draw, the arrow placed point by point, the counter,
// the laser and its trail). The marks take the gradient chosen in the toolbar.
const toolButton = (tool) => $(`[data-tool="${tool}"]`, ink.toolbar);
const box = (el) => planeBox(el);
const middle = (b) => [b.x + b.w / 2, b.y + b.h / 2];

// The scripted pointer on the drawing layer. Its places are in the stage's own layout, so it lands right on a tilted
// plane too, and its events reach the engine as a mouse's would. It stops the moment the visitor takes over.
const GHOST = 9002;
let driving = false, ghostAt = [0, 0];
function fireLayer(type, q, buttons = 0) {
  if (!driving) return;
  const o = ink.layer.getBoundingClientRect(), room = ink.room;
  ink.layer.dispatchEvent(new PointerEvent(type, { pointerId: GHOST, pointerType: "mouse", isPrimary: true, cancelable: true, clientX: o.left + (room?.left || 0) + q[0], clientY: o.top + (room?.top || 0) + q[1], button: 0, buttons }));
}
// The engine keeps the pointer for a stroke; the scripted pointer is not one the browser knows, so for its own
// strokes the layer's capture does nothing.
async function held(run) {
  const layer = ink.layer;
  layer.setPointerCapture = () => {};
  layer.releasePointerCapture = () => {};
  try { return await run(); } finally { delete layer.setPointerCapture; delete layer.releasePointerCapture; }
}
let showPointer = true;
const place = (p) => { ghostAt = p; if (showPointer) ink.placeGhost(p); };
async function reach(q) { if (showPointer) await ink.moveGhost(q); ghostAt = q; }
// The pointer glides to a place (or along a path) and the engine hears every step of the way.
async function glide(to, signal, { ms, buttons = 0, path = null } = {}) {
  const from = ghostAt, at = path || ((t) => [from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t]);
  // Without the pointer (the still picture), the same way is taken at once, in small steps.
  if (!showPointer) { for (let k = 1; k <= 32; k++) { const p = at(k / 32); ghostAt = p; fireLayer("pointermove", p, buttons); } return; }
  await tween(ms ?? clamp(Math.hypot(to[0] - from[0], to[1] - from[1]) * 1.5, 200, 620), (t) => { const p = at(t); place(p); fireLayer("pointermove", p, buttons); }, signal);
}
function click(q) {
  fireLayer("pointerdown", q, 1);
  fireLayer("pointerup", q, 0);
}
// A drag along a list of points, at an even speed.
async function stroke(points, signal, ms) {
  const lengths = [0];
  for (let i = 1; i < points.length; i++) lengths.push(lengths[i - 1] + Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]));
  const total = lengths[lengths.length - 1] || 1;
  const along = (t) => {
    const d = t * total;
    let i = 1;
    while (i < points.length - 1 && lengths[i] < d) i++;
    const k = clamp((d - lengths[i - 1]) / (lengths[i] - lengths[i - 1] || 1), 0, 1);
    return [points[i - 1][0] + (points[i][0] - points[i - 1][0]) * k, points[i - 1][1] + (points[i][1] - points[i - 1][1]) * k];
  };
  await reach(points[0]);
  await held(async () => {
    fireLayer("pointerdown", points[0], 1);
    await glide(null, signal, { ms, buttons: 1, path: along });
    fireLayer("pointerup", points[points.length - 1], 0);
  });
}
// The pointer goes to a button (of the toolbar or an app) and presses it.
async function press(el, action, signal, pause = 420) {
  if (!el) return;
  if (!showPointer) { action(); return; }
  if (signal.aborted) return;
  const b = box(el);
  await reach([b.x + b.w / 2, b.y + b.h * 0.55]);
  await hold(120, signal);
  if (signal.aborted) return;
  el.classList.add("is-pressed");
  action();
  await hold(150, signal);
  el.classList.remove("is-pressed");
  await hold(pause, signal);
}
const pick = (tool, signal, pause = 260) => (ink.state.tool === tool ? null : press(toolButton(tool), () => toolButton(tool).click(), signal, pause));

// Marks the script made follow what they point at when the page reflows, until an app is used: from then on they
// stay where they are on the screen, whatever changes under them. A mark is tied to one element, or (an arrow) its
// first point to one and its last to another, with the points between following both in proportion.
const ties = new Map();
function tie(before, from, to = from) {
  for (const m of ink.marks()) {
    if (before.has(m.id)) continue;
    ties.set(m.id, { from, to, a: box(from), b: box(to), frame: { ...m.frame }, points: m.points.map((p) => [...p]), between: from !== to });
  }
}
const ids = () => new Set(ink.marks().map((m) => m.id));
function refollow() {
  for (const [id, t] of ties) {
    const m = ink.marks().find((mark) => mark.id === id);
    if (!m) { ties.delete(id); continue; }
    const a = box(t.from), b = box(t.to), da = [a.x - t.a.x, a.y - t.a.y], db = [b.x - t.b.x, b.y - t.b.y];
    if (t.between && m.points.length === t.points.length) {
      const last = Math.max(1, t.points.length - 1);
      m.points = t.points.map((p, i) => { const k = i / last; return [p[0] + da[0] * (1 - k) + db[0] * k, p[1] + da[1] * (1 - k) + db[1] * k]; });
      m.frame = R.enclosing(m.points);
      m.revision++;
      m.geometryToken = freshToken();
    } else translate(m, [t.frame.x + da[0] - m.frame.x, t.frame.y + da[1] - m.frame.y]);
    ink.render(m);
  }
}
let refollowTimer = 0;
const refollowSoon = () => { clearTimeout(refollowTimer); refollowTimer = setTimeout(refollow, 140); };
addEventListener("resize", refollowSoon);
if (window.ResizeObserver) new ResizeObserver(refollowSoon).observe(apps);
document.fonts?.ready.then(refollowSoon);

// The arrow is set up before the script starts, as someone would have it in the toolbar: Clean, a thick line, a big
// head, Curved on. The properties row shows these values; the script only picks tools and draws.
const ARROW = { roughness: 0, width: 7, headScale: 1.5, curved: true };
ink.setStyle("arrow", ARROW);
// The ring is clean too: the Pen draws without roughness, so what Smart Draw makes of it is a smooth ellipse, and the
// shape tools start at Clean as well.
for (const tool of ["pen", "ellipse", "rect"]) ink.setStyle(tool, { roughness: 0 }); ink.setStyle("counter", { color: "#FFDD00" });

// Where things go. The ring sits round the weekly email's switch; the arrow comes from Dor's question in a wide arc,
// sideways when the windows sit side by side and up when they are stacked.
function ringLoop() {
  const t = box(weekly), [cx, cy] = middle(t), rx = t.w / 2 + 17, ry = t.h / 2 + 13, points = [];
  // A quick, loose loop by hand, a little more than once round; Smart Draw makes it an ellipse.
  for (let i = 0; i <= 30; i++) {
    const a = -2.5 + (i / 30) * Math.PI * 2.16, wob = 1 + Math.sin(i * 1.7) * 0.06 + (i / 30) * 0.05;
    points.push([cx + Math.cos(a) * rx * wob, cy + Math.sin(a) * ry * wob]);
  }
  return points;
}
// The arrow is a sweep with some character, placed point by point as in the app: five clicks along a smooth curve.
// Side by side it leaves her question heading down and left, swings up through the gap between the windows, crests
// above the ring and comes down onto it. Stacked, it rises from above her question in an S through the empty middle
// of the rows and comes into the ring from the left.
function arrowPoints() {
  const t = box(weekly), q = box(askEl), side = q.x > t.x + t.w + 20;
  const ringX = t.w / 2 + 17, ringY = t.h / 2 + 13, [cx, cy] = middle(t);
  let s, e, c1, c2;
  if (side) {
    s = [q.x - 12, q.y + q.h * 0.5];
    e = [cx + ringX * 0.7, cy - ringY * 0.75];
    const dx = Math.abs(s[0] - e[0]), dy = Math.max(60, Math.abs(s[1] - e[1]));
    c1 = [s[0] - dx * 0.5, s[1] + dy * 0.33];
    c2 = [e[0] + dx * 0.8, Math.max(8, e[1] - dy)];
  } else {
    s = [q.x + q.w - 16, q.y - 10];
    e = [t.x - 22, cy + 6];
    const h = s[1] - e[1];
    c1 = [Math.min(s[0] + 25, t.x - 26), s[1] - h * 0.4];
    c2 = [e[0] - 90, e[1] + h * 0.35];
  }
  const on = (k) => [0, 1].map((i) => (1 - k) ** 3 * s[i] + 3 * (1 - k) ** 2 * k * c1[i] + 3 * (1 - k) * k * k * c2[i] + k ** 3 * e[i]);
  return [0, 0.28, 0.52, 0.76, 1].map(on);
}
const counterOne = () => { const t = box(weekly); return [t.x - 20, t.y - 14]; };
const counterTwo = () => { const s = box(save); return [s.x - 4, s.y - 10]; };
function swirl(t) {
  const s = box(save), [cx, cy] = middle(s), rx = s.w / 2 + 20, ry = s.h / 2 + 16, a = -0.6 + t * Math.PI * 2 * 1.7;
  const k = 1 - 0.18 * t;
  return [cx + Math.cos(a) * rx * k, cy + Math.sin(a) * ry * k];
}
function checkPoints() {
  const s = box(save), x = s.x + s.w + 18, y = s.y + s.h * 0.5;
  return [[x, y - 2], [x + 4, y + 3], [x + 9, y + 9], [x + 15, y + 1], [x + 22, y - 9], [x + 29, y - 17]];
}

// The marks that point Dor to the switch: a ring, the arrow from her question, and the two numbers.
async function showTheWay(signal) {
  let before = ids();
  await pick("pen", signal);
  await stroke(ringLoop(), signal, 900);
  tie(before, weekly);
  await hold(420, signal);

  await pick("arrow", signal, 260);
  before = ids();
  const points = arrowPoints(), last = points.length - 1;
  await reach(points[0]);
  await held(async () => {
    click(points[0]);
    // Quick, sure moves between the clicks, and a beat before the last one; its second click finishes the arrow.
    for (let i = 1; i <= last; i++) {
      await hold(i === last ? 320 : 90, signal);
      const p = points[i];
      await glide(p, signal, { ms: clamp(Math.hypot(p[0] - ghostAt[0], p[1] - ghostAt[1]) * 1.6, 180, 340) });
      click(p);
    }
    await hold(110, signal);
    click(points[last]);
  });
  tie(before, askEl, weekly);
  await hold(520, signal);

  await pick("counter", signal);
  for (const [at, el] of [[counterOne(), weekly], [counterTwo(), save]]) {
    before = ids();
    await reach(at);
    await held(async () => click(at));
    tie(before, el);
    await hold(380, signal);
  }
}

// The scripted round. Its timings add up to about 20 seconds.
async function round(signal) {
  resetApps();
  if (ink.state.hidden) await ink.setHidden(false);
  if (ink.collapsed) await ink.setCollapsed(false);
  if (signal.aborted) return;
  ink.setMode("draw");
  await hold(420, signal);
  await showTheWay(signal);

  // The laser: a short swirl round Save changes, and the trail fades on its own.
  await pick("laser", signal, 300);
  const first = swirl(0);
  await reach(first);
  await held(async () => {
    fireLayer("pointerdown", first, 1);
    await glide(null, signal, { ms: 1500, buttons: 1, path: swirl });
    fireLayer("pointerup", swirl(1), 0);
  });
  await hold(700, signal);
  if (signal.aborted) return;

  // Use apps: the toolbar's own button, then the bar shrinks to one button as it does in the app. Dor's setting is
  // turned off and saved, and the drawings stay where they are.
  await press(modeButton, () => modeButton.click(), signal, 320);
  if (signal.aborted) return;
  await (appMotion ? ink.shrink() : ink.setCollapsed(true));
  await hold(360, signal);
  await press(weekly, () => weekly.click(), signal, 520);
  await press(save, () => save.click(), signal, 700);
  if (signal.aborted) return;

  // Back to Draw for a moment: a press on the one button brings the toolbar back, and the pen ticks it off.
  await press(pill, () => pill.click(), signal, 360);
  await pick("pen", signal, 200);
  await stroke(checkPoints(), signal, 420);
  ink.hideGhost();
  if (signal.aborted) return;
  await hold(2600, signal);
  if (signal.aborted) return;

  // The drawings and the toolbar go the app's way (Hide drawings and tools), the apps go back to how they were, and
  // the toolbar comes back for the next round.
  await appHide(true, signal);
  await hold(500, signal);
  if (signal.aborted) return;
  ink.clear();
  ties.clear();
  resetApps();
  await appHide(false, signal);
  await hold(700, signal);
}
async function loop(signal) {
  driving = true;
  while (!signal.aborted) {
    await whenSeen(signal);
    await wait(500, signal);
    if (signal.aborted) break;
    await round(signal);
  }
}
function start() {
  if (run) return;
  const abort = new AbortController();
  run = { abort, finished: null };
  setRunning(true);
  run.finished = loop(abort.signal).finally(async () => {
    driving = false;
    ink.hideGhost();
    keepChoice();
    // A script that was cut short while it had the drawings away brings them back (not when the visitor hid them).
    if (ink.state.hidden && scriptHid) await ink.setHidden(false, { animate: false });
    scriptHid = false;
    if (run?.abort !== abort) return;
    run = null;
    setRunning(false);
    desk.classList.remove("is-wiping");
    done.hidden = ink.state.mode !== "draw";
  });
}
async function stop() {
  if (!run) return;
  const { abort, finished } = run;
  abort.abort();
  // The stage takes presses again at once, so the visitor's first stroke is not lost while the script winds down.
  desk.classList.remove("is-scripted");
  await finished;
}
// The whole demo as a still picture, for visitors who asked for less motion: the same marks, made at once and
// without the pointer.
async function still() {
  const signal = AbortSignal.abort();
  building = true;
  driving = true;
  showPointer = false;
  ink.setMode("draw");
  await showTheWay(signal);
  ink.setMode("use");
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
  driving = false;
  showPointer = true;
  building = false;
}
async function restart() {
  await stop();
  if (ink.state.hidden) await ink.setHidden(false);
  if (ink.collapsed) await ink.setCollapsed(false);
  ink.setMode("use");
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
  ink.setStyle("arrow", ARROW);
  for (const tool of ["pen", "ellipse", "rect"]) ink.setStyle(tool, { roughness: 0 }); ink.setStyle("counter", { color: "#FFDD00" });
  ties.clear();
  chosen = null;
  acted = false;
  userFlat = !motion;
  request();
  await wipe();
  resetApps();
  if (motion) start(); else still();
}
replay.addEventListener("click", () => { if (run) { acted = true; stop(); } else restart(); });
done.addEventListener("click", () => ink.setMode("use"));

// The script stops for good when the visitor takes over: a press on the toolbar or in an app, draw mode by any
// route, or one of the app's keys. Drawing also brings the stage flat.
function takeOver(flat = true) {
  // What the script put away when the visitor stepped in comes back, so they never join an empty stage or a
  // toolbar shrunk to one button. What the visitor hides after that stays hidden.
  const hid = scripted() && ink.state.hidden, small = scripted() && ink.collapsed;
  acted = true;
  // A stroke or an arrow the scripted pointer had under way is let go, so the visitor's pointer never continues it.
  if (driving) { driving = false; ink.setTool(ink.state.tool, true); }
  stop().then(() => {
    if (hid && ink.state.hidden) ink.setHidden(false);
    if (small && ink.collapsed && !ink.state.hidden) ink.setCollapsed(false);
  });
  // Lying flat is immediate, so the first stroke lands where the pointer is.
  if (flat && !userFlat) { userFlat = true; lean.value = null; request(); }
}
ink.toolbar.addEventListener("pointerdown", (e) => { if (e.isTrusted) takeOver(); }, true);
// The three keys, pressed while the script plays: the script stops and the key does what it says, whatever the
// script was showing. (This runs before the engine hears the key.)
addEventListener("keydown", (e) => {
  if (!e.isTrusted || !scripted() || e.target.closest?.("input, textarea")) return;
  const draws = e.ctrlKey && e.shiftKey && e.code === "Digit2", hides = e.ctrlKey && !e.shiftKey && e.code === "Digit2";
  if (!draws && !hides && e.key !== "Escape") return;
  // Control-Shift-2 joins in with the Pen, as a press on the stage does; the engine then hears the key as usual.
  if (draws) joinDrawing(); else takeOver(false);
}, true);
// Hide drawings and tools leaves nothing on the stage, as the app leaves nothing on the desktop: once the engine's
// animation has played, the toolbar's one button goes too. Control-2 or Control-Shift-2 brings everything back, and
// for a mouse there is one small button in the stage's corner.
const showAll = $("#stage-show");
ink.on("hide", async (hidden) => {
  showAll.hidden = !hidden || scripted();
  if (!hidden) { delete desk.dataset.hidden; return; }
  await new Promise(requestAnimationFrame);
  const playing = [...ink.toolbar.getAnimations(), ...ink.layer.getAnimations({ subtree: true })].filter((a) => a.effect?.getTiming().iterations !== Infinity);
  await Promise.race([Promise.all(playing.map((a) => a.finished.catch(() => {}))), wait(1600)]);
  if (ink.state.hidden) desk.dataset.hidden = "";
});
showAll.addEventListener("click", () => { takeOver(false); ink.setMode("draw"); });
ink.on("shortcut", () => { if (!scripted()) takeOver(); });
const MODE_WORDS = { use: "Using apps.", draw: "Drawing." };
ink.on("mode", (mode) => {
  desk.dataset.mode = mode;
  apps.inert = mode === "draw";
  if (scripted()) return;
  if (mode === "draw") takeOver();
  done.hidden = mode !== "draw";
  status.textContent = MODE_WORDS[mode];
});
addEventListener("resize", () => setTimeout(() => ink.reflow(), 260));

// The script waits until the settings, where it draws first, are on screen.
new IntersectionObserver(([entry]) => {
  appsOnScreen = entry.isIntersecting;
  if (appsSeen()) seenWaiters.splice(0).forEach((go) => go());
}, { threshold: 0.6 }).observe($(".win-settings"));
// Drawing ends when the stage is out of view, so keys and touches go back to the page.
new IntersectionObserver(([entry]) => { if (!entry.isIntersecting) ink.setMode("use"); }).observe(scene);

const fontsReady = Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {});
// The first screen, inside about 1.2 s: the headline's words rise (style.css), the engine draws its mark once they
// have all but landed, and the stage settles under them.
const pageStart = performance.now();
Promise.all([document.fonts?.ready, document.fonts?.load('720 84px "Bricolage Grotesque"')]).catch(() => {}).then(async () => {
  if (motion) await wait(440 - (performance.now() - pageStart));
  await heroInk.perform(heroSteps(motion ? 520 : 0), { cursor: false });
});
fontsReady.then(async () => {
  if (motion) { await wait(1500); if (!acted) start(); } else still();
});

// "Try it here", from anywhere on the page: the stage comes into view with a clean screen, the Pen and Draw on.
async function tryHere() {
  // The script's drawings go at once, and the Pen is ready before the stage has even scrolled into view.
  ink.clear();
  ties.clear();
  joinDrawing();
  // The page answers at once: the stage comes into view and its edge lights up while the script winds down.
  const room = innerHeight - parseFloat(getComputedStyle(root).getPropertyValue("--header-h"));
  scene.scrollIntoView({ block: scene.offsetHeight < room - 24 ? "center" : "start", behavior: motion ? "smooth" : "auto" });
  desk.classList.remove("is-cued");
  void desk.offsetWidth;
  desk.classList.add("is-cued");
  $('[data-tool="pen"]', ink.toolbar)?.focus({ preventScroll: true });
  status.textContent = "This screen is live. Draw on it.";
}
for (const link of $$("[data-try]")) link.addEventListener("click", (e) => { e.preventDefault(); tryHere(); });
$(".stage-pulse").addEventListener("animationend", () => desk.classList.remove("is-cued"));

// 2. What you can do -------------------------------------------------------------------------------
// Each card holds one of the shared engine's scenes, in the page's gradient and theme. A scene plays while its card
// faces front, again and again with a short rest between runs, and starts over when the card comes back.
const SHARED = { zoom: zoomScene, shapes: shapesScene, share: shareRegionScene, break: breakTimerScene };
function shared(shot) {
  const scene = SHARED[shot.dataset.scene](shot, { appearance: root.dataset.theme, gradient: accent });
  liveScenes.push(scene);
  let turn = 0, shown = false;
  async function loop() {
    const mine = ++turn, playing = scene;
    while (mine === turn) {
      // Without motion one run is enough: it ends on the finished picture.
      if (!(await playing.play()) || !motion) return;
      await wait(1600);
    }
  }
  return {
    show() { if (shown) return; shown = true; loop(); },
    hide() { if (!shown) return; shown = false; turn++; scene.reset(); },
  };
}
const cards = $$(".do-deck .card");
// Goes to one of the cards: through the pinned deck with motion, or to it in the grid without.
let goToCard = (i) => cards[i].scrollIntoView({ block: "center" });
const visuals = cards.map((card) => shared($(".shot", card)));

if (motion) {
  // The pinned deck. Progress through the track turns into one number, 0 for the first card to 3 for the last; each
  // card rests flat for a while around its own number, and eases between.
  const track = $(".do-track"), tabList = $(".do-tabs"), tabs = $$("button", tabList), rest = 0.2;
  cards.forEach((card, i) => card.style.setProperty("--i", i));

  // A change of card is one short piece of motion (the timings are in style.css, under "A change of card"). The
  // frame arrives with the scroll; once it has all but landed the card becomes "current": the camera pushes in, an
  // ink stroke wipes the picture in, the title is written letter by letter, the line follows and an underline is
  // drawn. A card that starts to leave gives it all back, faster. Every layer is one transition on that one class, so
  // a fast scroll turns each round from wherever it is.
  for (const card of cards) {
    const title = $("h3", card), text = title.textContent.trim(), words = text.split(/\s+/);
    // The letters are for the eye; a screen reader gets the title whole.
    const written = document.createElement("span");
    written.setAttribute("aria-hidden", "true");
    title.setAttribute("aria-label", text);
    let letters = 0;
    words.forEach((word, i) => {
      // Each letter rises inside its word's mask.
      const mask = document.createElement("span");
      mask.className = "tw";
      for (const letter of word) {
        const one = document.createElement("span");
        one.textContent = letter;
        one.style.setProperty("--l", letters++);
        mask.append(one);
      }
      written.append(mask, i < words.length - 1 ? " " : "");
    });
    title.replaceChildren(written);
    // The ink wipe: a cover over the picture, with the stroke on its leading edge.
    const wipe = document.createElement("i");
    wipe.className = "wipe";
    wipe.setAttribute("aria-hidden", "true");
    wipe.append(document.createElement("b"));
    $(".frame", card).append(wipe);
  }
  // What carries over from one card to the next: the accent dot in the tabs flies to the new tab on the app's Magic
  // flight (the swoop its drawings make when they are shown), and a soft light behind the deck glides to the side
  // the new card's picture is on. The dot first appears with the app's Pop.
  // (The marker and its dot are <b>s: the tabs' own dots are styled as "i" inside the list.)
  const marker = document.createElement("b"), dot = document.createElement("b"), light = document.createElement("i");
  marker.className = "do-marker";
  marker.setAttribute("aria-hidden", "true");
  marker.append(dot);
  tabList.append(marker);
  light.className = "do-light";
  light.setAttribute("aria-hidden", "true");
  $(".do-pin").prepend(light);
  let flight = null, markerAt = -1;
  const middleIn = (el) => {
    const r = el.getBoundingClientRect(), list = tabList.getBoundingClientRect();
    return [r.left + r.width / 2 - list.left, r.top + r.height / 2 - list.top];
  };
  function moveMarker(index, animate = true) {
    // One animation per change: a new flight starts from wherever the dot had got to.
    const [fromX, fromY] = middleIn(dot), [x, y] = middleIn($("i", tabs[index])), first = markerAt < 0;
    flight?.cancel();
    flight = null;
    marker.style.translate = `${x.toFixed(1)}px ${y.toFixed(1)}px`;
    marker.classList.add("is-on");
    markerAt = index;
    if (!animate) return;
    if (first) {
      const pop = poseKeyframes("pop", { appearing: true, annotation: false });
      flight = dot.animate(pop, { duration: pop.duration, easing: pop.easing });
      return;
    }
    const dx = fromX - x, dy = fromY - y, far = Math.hypot(dx, dy);
    if (far < 1) return;
    // A long way goes on the Magic flight; between dots that sit side by side a small arc is enough.
    const path = far > 60 ? magicFlight([dx, dy], [0, 0], `tab ${index}`) : [
      { translate: `${dx.toFixed(1)}px ${dy.toFixed(1)}px`, scale: "1 1" },
      { translate: `${(dx / 2).toFixed(1)}px ${(dy / 2 - 9).toFixed(1)}px`, scale: "1.45 0.8" },
      { translate: "0px 0px", scale: "1 1" },
    ];
    flight = dot.animate(path, { duration: clamp(300 + far * 0.7, 320, 560), easing: path.easing || curve.standard });
  }
  let facing = -1, deckSeen = false, lastAt = 0;
  // The tabs, the marker and the light follow the nearest card.
  const face = (index) => {
    if (index === facing) return;
    facing = index;
    cards.forEach((card, i) => { card.inert = i !== index; });
    tabs.forEach((tab, i) => tab.setAttribute("aria-current", String(i === index)));
    if (index < 0) return;
    moveMarker(index);
    // Wide cards put the picture left, then right, in turn; the light goes to that side.
    light.style.setProperty("--lx", index % 2 ? 1 : -1);
  };
  // The title's underline is drawn by the engine, in the accent, a moment after the title is written; it goes when
  // the card leaves. Each card has its own small layer with no toolbar and no input.
  const underlines = cards.map((card) => {
    const host = $(".card-text", card), title = $("h3 > span", card);
    const pen = createInk({ surface: host, interactive: false, appearance: root.dataset.theme, gradient: accent });
    const under = { getBoundingClientRect() { const r = title.getBoundingClientRect(), w = Math.min(r.width, Math.max(96, r.width * 0.55)); return { left: r.left, top: r.top, width: w, height: r.height }; } };
    let timer = 0;
    return {
      pen,
      draw() { clearTimeout(timer); timer = setTimeout(() => { pen.clear(); pen.perform([{ tool: "underline", under, dy: 8, wave: 0.7, size: 3, duration: 420 }], { cursor: false, anchored: false }); }, 420); },
      lift() { clearTimeout(timer); pen.clear(); },
    };
  });
  // A card is current from the moment its frame has all but landed until it starts to leave (two thresholds, so it
  // cannot flicker between them). Its scene starts only once the frame lies flat, and stops when the card leaves.
  const current = cards.map(() => false);
  function settle(at) {
    lastAt = at;
    cards.forEach((card, i) => {
      const x = at - i, whole = Math.floor(Math.abs(x)), part = Math.abs(x) - whole;
      const away = whole + smooth((part - rest) / (1 - 2 * rest));
      const now = deckSeen && away < (current[i] ? 0.1 : 0.06);
      if (now !== current[i]) { current[i] = now; card.classList.toggle("is-current", now); if (now) underlines[i].draw(); else underlines[i].lift(); }
      // The scene starts once the frame lies flat (within a hair), and stops when the card leaves.
      if (now && away < 0.03) visuals[i].show(); else if (!now) visuals[i].hide();
    });
    if (deckSeen) face(Math.round(at));
  }
  const deck = channel(() => {
    const r = track.getBoundingClientRect();
    return clamp(-r.top / Math.max(1, r.height - innerHeight), 0, 1) * (cards.length - 1);
  }, (at) => {
    cards.forEach((card, i) => {
      const x = at - i, whole = Math.floor(Math.abs(x)), part = Math.abs(x) - whole;
      const eased = whole + smooth((part - rest) / (1 - 2 * rest));
      const u = x < 0 ? eased : 0, v = x > 0 ? eased : 0;
      card.style.setProperty("--u", u.toFixed(4));
      card.style.setProperty("--v", v.toFixed(4));
      card.style.visibility = u > 2.2 || v > 2.6 ? "hidden" : "";
    });
    settle(at);
  });
  new IntersectionObserver(([entry]) => {
    deckSeen = entry.isIntersecting;
    if (!deckSeen) { face(-1); cards.forEach((card) => { card.inert = false; }); }
    settle(deck.value ?? lastAt);
  }, { threshold: 0.25 }).observe($(".do-deck"));
  addEventListener("resize", () => { if (facing >= 0) moveMarker(facing, false); });
  document.fonts?.ready.then(() => { if (facing >= 0) moveMarker(facing, false); });
  goToCard = (i) => {
    const r = track.getBoundingClientRect();
    scrollTo({ top: r.top + scrollY + (r.height - innerHeight) * (i / (cards.length - 1)) + 1, behavior: "smooth" });
  };
  tabs.forEach((tab, i) => tab.addEventListener("click", () => goToCard(i)));
  if (window.ResizeObserver) new ResizeObserver(request).observe(track);
  // Scrolling decides where every card is, frame by frame. When it stops between two cards (about 150 ms after the
  // last scroll), the deck finishes the move to the nearer one, or to the next one in the direction of travel, so a
  // card is always left flat and whole with its words and scene.
  let idle = 0, lastY = scrollY, heading = 0;
  function settleDeck() {
    const r = track.getBoundingClientRect(), span = r.height - innerHeight;
    if (span <= 0 || r.top > 0 || r.bottom < innerHeight) return;
    const at = clamp(-r.top / span, 0, 1) * (cards.length - 1), below = Math.floor(at), part = at - below;
    if (part <= rest * 0.9 || part >= 1 - rest * 0.9) return;
    const to = part < 0.5 ? (heading > 0 && part > 0.3 ? below + 1 : below) : (heading < 0 && part < 0.7 ? below : below + 1);
    scrollTo({ top: r.top + scrollY + span * (to / (cards.length - 1)) + 1, behavior: "smooth" });
  }
  addEventListener("scroll", () => {
    if (scrollY !== lastY) heading = Math.sign(scrollY - lastY);
    lastY = scrollY;
    clearTimeout(idle);
    idle = setTimeout(settleDeck, 150);
  }, { passive: true });
} else {
  // Without motion the cards are a plain grid, and each scene shows its last frame.
  visuals.forEach((visual) => visual.show());
}

// The app's keys for two of the cards work here too, on a Mac, while the section is in view: Control-1 goes to Zoom
// in and Control-3 to Take a break. (Command-L is the browser's, and on Windows Control-1 and Control-3 pick tabs.)
let doInView = false;
new IntersectionObserver(([entry]) => { doInView = entry.isIntersecting; }, { threshold: 0.3 }).observe($("#do"));
addEventListener("keydown", (e) => {
  if (root.dataset.platform !== "mac" || !doInView || !e.ctrlKey || e.metaKey || e.altKey || e.shiftKey) return;
  const card = { Digit1: 0, Digit3: 3 }[e.code];
  if (card === undefined || e.target.closest?.('input, textarea, select, [contenteditable=""], [contenteditable="true"]')) return;
  e.preventDefault();
  goToCard(card);
});

if (motion) {
  // Where a section is on its way through: -1 just below the screen, 0 in the middle, 1 just above.
  const through = (el) => () => {
    const r = el.getBoundingClientRect();
    return clamp((innerHeight / 2 - (r.top + r.height / 2)) / (innerHeight / 2 + r.height / 2), -1, 1);
  };
  for (const el of $$("[data-through]")) channel(through(el), (v) => el.style.setProperty("--through", v.toFixed(4)));
}

// The live dot by the stage rests while the hero is off screen.
new IntersectionObserver(([entry]) => $(".hero").classList.toggle("is-away", !entry.isIntersecting)).observe($(".stage-title"));
// The cup's steam runs only while it is on screen.
const live = new IntersectionObserver((entries) => {
  for (const entry of entries) entry.target.classList.toggle("is-live", entry.isIntersecting);
}, { threshold: 0.3 });
live.observe($(".coffee"));

if (motion) {
  // The cup arrives with the app's Pop.
  const cup = $(".cup");
  cup.classList.add("awaits");
  new IntersectionObserver(([entry], observer) => {
    if (!entry.isIntersecting) return;
    observer.disconnect();
    const pop = poseKeyframes("pop", { appearing: true });
    cup.animate(pop, { duration: pop.duration, easing: pop.easing, fill: "backwards" });
    cup.classList.remove("awaits");
  }, { threshold: 0.6 }).observe(cup);
}

// Panels lean toward a mouse pointer.
if (motion && matchMedia("(hover: hover) and (pointer: fine)").matches) for (const el of $$("[data-tilt]")) {
  const most = el.offsetWidth > 700 ? 2.2 : el.offsetWidth > 420 ? 4 : 7;
  el.addEventListener("pointermove", (e) => {
    const r = el.getBoundingClientRect(), x = (e.clientX - r.left) / r.width, y = (e.clientY - r.top) / r.height;
    const ax = -(y - 0.5), ay = x - 0.5, length = Math.hypot(ax, ay) || 1;
    el.style.rotate = `${(ax / length).toFixed(3)} ${(ay / length).toFixed(3)} 0 ${(Math.min(1, length * 2) * most).toFixed(2)}deg`;
    el.style.setProperty("--mx", `${(x * 100).toFixed(1)}%`);
    el.style.setProperty("--my", `${(y * 100).toFixed(1)}%`);
    el.classList.add("is-tilting");
  });
  el.addEventListener("pointerleave", () => { el.classList.remove("is-tilting"); el.style.rotate = ""; });
}

// Install ------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const COPIED = "Copied. Paste it into Terminal and press Enter.";
for (const button of $$("[data-copy]")) {
  const label = $("p", button.closest(".command"));
  let timer = 0;
  button.addEventListener("click", async () => {
    const code = $("code", button.closest(".command"));
    let copied = true;
    try { await navigator.clipboard.writeText(code.textContent.trim()); }
    catch {
      const range = document.createRange();
      range.selectNodeContents(code);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      copied = document.execCommand("copy");
    }
    if (copied) label.textContent = COPIED;
    status.textContent = copied ? COPIED : "Selected. Copy it with your keyboard.";
    clearTimeout(timer);
    timer = setTimeout(() => { label.textContent = label.dataset.label; status.textContent = ""; }, 4000);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
const windowsCard = $('.platform[data-os="windows"]');
if (root.dataset.platform === "windows" && windowsCard) $(".platforms").prepend(windowsCard);

request();
