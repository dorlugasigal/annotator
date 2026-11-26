import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
import { createAurora } from "./aurora.js";
import { createReport } from "./report.js";

const root = document.documentElement;
const motion = root.classList.contains("motion");
const hovers = matchMedia("(hover: hover)").matches;
const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const smooth = (t) => t * t * (3 - 2 * t);
// Ink colors are the page's own tokens, so the stylesheet stays the one place colors are written down.
const token = (name) => getComputedStyle(root).getPropertyValue(name).trim();
const LABEL_INK = token("--aurora-2");

// Where things are -------------------------------------------------------------------------------
// The engine measures with getBoundingClientRect, which is wrong on a plane that leans in space. These give it
// positions in the plane's own layout instead, so marks land on their targets at any angle.
function localBox(element, surface) {
  let x = 0, y = 0, node = element;
  while (node && node !== surface) {
    x += node.offsetLeft; y += node.offsetTop;
    const parent = node.offsetParent;
    if (parent && parent !== surface) { x += parent.clientLeft; y += parent.clientTop; }
    node = parent;
  }
  return { x, y, w: element.offsetWidth, h: element.offsetHeight };
}
function anchorsFor(ink, surface) {
  const rect = (element) => localBox(element, surface);
  return {
    rect,
    // Stands in for an element where a step needs one ("around", "over").
    box: (element) => ({
      getBoundingClientRect() { const o = surface.getBoundingClientRect(), b = rect(element); return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h }; },
    }),
  };
}
const leave = async (ink, ids) => {
  if (!motion || !ids.length) return;
  for (const id of ids) ink.layer.querySelector(`[data-id="${id}"]`)?.classList.add("is-leaving");
  await wait(520);
};
// Resolves when an element is on screen and the tab is in front.
function watcher(element, threshold = .35) {
  let seen = false, waiting = [];
  const check = () => { if (seen && !document.hidden) waiting.splice(0).forEach((done) => done()); };
  if (!window.IntersectionObserver) seen = true;
  else new IntersectionObserver(([entry]) => { seen = entry.isIntersecting; check(); }, { threshold }).observe(element);
  document.addEventListener("visibilitychange", check);
  return { get seen() { return seen; }, ready: () => (seen && !document.hidden ? Promise.resolve() : new Promise((done) => waiting.push(done))) };
}

// Scroll: one handler -----------------------------------------------------------------------------
// Each follower has a target that the scroll position sets, and a value that eases toward it, so a wheel's
// steps become one movement. Values are written to CSS variables; the styles do the rest.
const followers = [], readers = [];
function follow(apply, tau = 90, eps = .0006) {
  const follower = { value: null, target: 0, tau, eps, apply };
  followers.push(follower);
  return follower;
}
let queued = false, lastFrame = 0;
function schedule() { if (!queued) { queued = true; requestAnimationFrame(frame); } }
function frame(now) {
  queued = false;
  const dt = lastFrame ? Math.min(50, now - lastFrame) : 16;
  const view = { y: scrollY, h: innerHeight, w: innerWidth };
  for (const read of readers) read(view);
  let moving = false;
  for (const f of followers) {
    if (f.value === null) { f.value = f.target; f.apply(f.value); continue; }
    const gap = f.target - f.value;
    if (Math.abs(gap) < f.eps) { if (gap) { f.value = f.target; f.apply(f.value); } continue; }
    f.value += gap * (1 - Math.exp(-dt / f.tau));
    f.apply(f.value);
    moving = true;
  }
  lastFrame = moving ? now : 0;
  if (moving) schedule();
}
addEventListener("scroll", schedule, { passive: true });
addEventListener("resize", schedule);

const header = $(".site-header");
readers.push(({ y }) => header.classList.toggle("is-stuck", y > 8));

// Light --------------------------------------------------------------------------------------------
// Three fields of aurora: behind the hero, behind the carousel and inside the coffee panel.
const auroras = Object.fromEntries($$("canvas[data-aurora]").map((canvas) => [canvas.dataset.aurora, createAurora(canvas, { kind: canvas.dataset.aurora, animate: motion, theme: root.dataset.theme })]));
// The hero's light hangs down to the headline and is gone by the line of text under it, at any width.
const sky = $(".aurora-sky"), lede = $(".lede");
const placeSky = () => auroras.sky?.setFloor((lede.offsetTop - sky.offsetTop) / sky.offsetHeight);
placeSky();
addEventListener("resize", placeSky);
document.fonts?.ready.then(placeSky);

// Hero stage --------------------------------------------------------------------------------------
const hero = $(".hero"), stage = $("#stage"), screen = $("#screen"), board = $("#board"), appWindow = $("#app"), number = $("#ring-target"), plot = $("#plot"), dip = $("#dip"), secret = $("#private");
// The toolbar keeps the state the engine gives it (its properties row included), so no colors or sizes are set here.
// Its selected stroke color is the app's Aurora gradient, the same one the page's light is made of.
// Hide and Show move in the app's Magic style.
const ink = createInk({ surface: board, toolbarParent: $("#dock"), toolbarPosition: "absolute", appearance: root.dataset.theme === "light" ? "light" : "dark", gradient: "aurora", visibility: "magic", tool: "pen", moreHref: "#install" });
const at = anchorsFor(ink, board);
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the screen's
// frame opens up to fill the window, and scrolling brings it back (shared/stage.js). The board with the sample window
// does not move: in index.html the desk and the dock carry data-stage-frame, and the mode chip sits in the dock.
// Until then the shared "Draw here" cue goes with the pointer over the stage.
const growing = growingStage({ ink, surface: board, root: screen, tries: "[data-try]", invite: "Draw here", motion: () => motion, isFlat: () => !motion || (leaning < .001 && lifting < .001) });
// Joining in ends the script and lays the stage flat: the frame grows only once it is. It stays
// flat until the frame is small again.
let held = false;
growing.on("join", () => { held = true; stopDemo(); syncStage(); schedule(); });
growing.on("change", ({ state }) => { if (state === "small" && held) { held = false; ink.fit(); schedule(); } });
// The screen keeps room under the window for the toolbar. It remembers the tallest the toolbar has been at this
// size, so opening and closing the properties row moves nothing on the page.
const tallest = {};
ink.on("layout", ({ height, size, rows }) => {
  // The room is the small frame's: a grown frame has the whole window, and changing it there would resize the page behind.
  if (!height || ink.collapsed || growing.state !== "small") return;
  const key = `${size}|${rows}`;
  if (height <= (tallest[key] || 0) && tallest.shown === key) return;
  tallest[key] = Math.max(height, tallest[key] || 0);
  tallest.shown = key;
  const room = tallest[key];
  requestAnimationFrame(() => { screen.style.setProperty("--tb-h", `${room}px`); schedule(); });
});
// The toolbar keeps the same distance from the frame's sides at every width (the stylesheet's --dock-inset).
const fitBar = () => ink.fit({ margin: (parseFloat(getComputedStyle(screen).getPropertyValue("--dock-inset")) || 0) + 8 });
fitBar();
addEventListener("resize", fitBar);

// At the top of the page the stage floats turned a little away. Scrolling brings it round along a shallow arc
// until it faces the visitor, flat. It is also flat whenever the visitor is using it, because a flat plane is
// where the pointer and the ink agree.
let engaged = false, lifted = !motion, leaning = 1, lifting = 1;
if (motion) {
  const lean = follow((k) => {
    leaning = k;
    const narrow = innerWidth < 600, set = (name, value, digits = 3) => stage.style.setProperty(name, value.toFixed(digits));
    set("--tilt", k * (narrow ? 9 : 10));
    set("--yaw", k * (narrow ? -8 : -13));
    // The path: in from the side, bowing upward on the way.
    set("--ax", k * (narrow ? -6 : -22), 2);
    set("--ay", Math.sin(k * Math.PI) * (narrow ? -12 : -30), 2);
    set("--scale", 1 - k * (narrow ? .05 : .085), 4);
    stage.classList.toggle("is-live", k > 0);
  });
  const lift = follow((k) => { lifting = k; stage.style.setProperty("--lift", k.toFixed(4)); }, 150);
  // As the hero goes by, the colors of its light change places.
  const hue = follow((v) => { $(".pool", stage).style.setProperty("--hue", v.toFixed(4)); auroras.sky?.setShift(v); }, 160, .001);
  readers.push(({ y, h }) => {
    const top = stage.getBoundingClientRect().top + y, range = clamp(top - 90, 200, Math.max(200, h * .56));
    const k = engaged || held ? 0 : 1 - smooth(clamp(y / range));
    lean.target = k;
    lift.target = lifted ? k : 0;
    hue.target = clamp(y / Math.max(1, hero.offsetHeight - h * .25));
  });
}
const engage = (on) => { if (engaged !== on) { engaged = on; schedule(); } };

// The sample app, and whose turn it is ------------------------------------------------------------------------
// The window on the stage is a small app that works (report.js). The page's own script uses it and draws over it
// until the visitor does something on the stage with a real press or key: from then on the stage is theirs, and
// the script stops and does not start again by itself.
const replay = $("#replay"), done = $("#done"), onStage = watcher(screen);
const demo = { run: 0, playing: false, drawn: false, away: false, ids: new Set(), busy: Promise.resolve(), controller: null, picked: null };
// Drawings stay where they were put on the screen. A scripted mark follows its target while the page's layout
// changes; once the app under it has been used it stays put, as in the app.
const letGo = () => { for (const mark of ink.marks()) if (mark.anchor) mark.anchor = null; };
const report = createReport(appWindow, {
  animate: motion,
  // In Draw mode the pointer is the tool, and the app does not react.
  canUse: () => ink.state.mode !== "draw",
  onChange: letGo,
  onUse: (said) => { stopDemo(); if (said) status.textContent = said; },
});
function syncStage() {
  const drawing = ink.state.mode === "draw";
  // While the script runs, a touch on the stage scrolls the page as usual (see .is-scripted in the styles).
  screen.classList.toggle("is-scripted", demo.playing);
  // Under a visitor's ink the app takes no input from the keyboard either.
  appWindow.inert = drawing && !demo.playing;
  if (drawing) report.hideTip();
  // A plain way out of Draw, for a visitor without the keys.
  done.hidden = !drawing || demo.playing;
}

const small = () => screen.clientWidth < 620;
const stacked = () => at.rect(plot).y > at.rect(number).y + at.rect(number).h;
const LABEL = "why the dip?";
let pen = null;
function labelWidth() {
  pen ||= document.createElement("canvas").getContext("2d");
  pen.font = getComputedStyle(ink.layer).getPropertyValue("--ink-text").trim().replace(/\/[\d.]+/, "");
  return pen.measureText(LABEL).width + 8;
}
function heroSteps() {
  const size = small() ? 3 : 4, ring = { get x() { return small() ? 10 : 17; }, get y() { return small() ? 8 : 11; } };
  // The ring and the arrow name no color: they are drawn in whatever the toolbar has selected.
  return {
    ring: { tool: "ellipse", around: at.box(number), get padX() { return ring.x; }, get padY() { return ring.y; }, size, duration: 850, pause: 260 },
    arrow: {
      tool: "arrow", size, duration: 800, pause: 320,
      // Beside the chart the arrow leaves the ring's right side; above the chart it leaves the lower left, clear of the badge under the number.
      from: () => { const n = at.rect(number); return stacked() ? [n.x - ring.x * .4, n.y + n.h + ring.y * .7 + 4] : [n.x + n.w + ring.x + 8, n.y + n.h / 2 - 2]; },
      to: () => { const d = at.rect(dip); return stacked() ? [d.x + 8, d.y - 10] : [d.x - 6, d.y - 11]; },
      get bend() { return stacked() ? .16 : .24; },
    },
    label: { tool: "text", color: LABEL_INK, text: LABEL, speed: 46, pause: 360, at: () => { const d = at.rect(dip); return [d.x - labelWidth() / 2, d.y + (small() ? 7 : 12)]; } },
    blur: { tool: "blur", over: at.box(secret), padX: 6, padY: 3, duration: 650, pause: 300 },
  };
}

function showControl() {
  replay.hidden = false;
  replay.classList.toggle("is-off", !demo.playing);
  replay.lastElementChild.textContent = demo.playing ? "Pause" : "Replay";
}
function forget() {
  for (const id of demo.ids) ink.remove(id);
  demo.ids.clear();
}
function stopDemo() {
  if (!demo.playing) return;
  demo.playing = false;
  demo.run += 1;
  demo.controller?.abort();
  ink.hideGhost();
  $$(".is-leaving", ink.layer).forEach((mark) => mark.classList.remove("is-leaving"));
  $$(".is-pressed", appWindow).forEach((control) => control.classList.remove("is-pressed"));
  // What the script itself had put away comes back. What the visitor hid (Control-2, the Hide button) stays hidden.
  if (demo.away) {
    if (ink.state.hidden) ink.setHidden(false, { animate: false }); else if (ink.collapsed) ink.setCollapsed(false, { animate: false });
    demo.away = false;
  }
  showControl();
  syncStage();
}

// One round of the story. Draw: ring the number and point at the dip. Use apps: look at last week and come back,
// while the drawings stay where they are. Draw: add the label and cover the address. Then the app's own Shrink,
// its Hide and Show, and Clear.
async function round(signal) {
  const steps = heroSteps(), { prev, next } = report.controls, trash = ink.toolbar.querySelector('[data-action="clear"]');
  const play = async (list) => {
    const made = await ink.perform(list, { cursor: true, showTool: true, signal });
    made.forEach((mark) => demo.ids.add(mark.id));
    // A script puts the toolbar back on the tool it started with. If the visitor chose one meanwhile, theirs wins.
    if (demo.picked && ink.state.tool !== demo.picked) ink.setTool(demo.picked, true);
  };
  // While the script has the toolbar or the drawings put away, stopping it brings them back (stopDemo).
  const away = async (list) => { demo.away = true; await play(list); if (!signal.aborted) demo.away = false; };
  // The mode changes as if its key had been pressed: the chip on the stage shows the key going down.
  const say = (mode) => play([{ mode, via: "key", pause: 520 }]);
  // The pointer goes to one of the app's controls and presses it. The page moves the pointer itself, in the
  // plane's own layout, because the engine's click step measures on the screen and the stage may be leaning.
  const press = async (control) => {
    const box = at.rect(control), point = [box.x + box.w / 2, box.y + box.h / 2];
    await ink.moveGhost([point[0] - 1, point[1] + 2], 640);
    if (signal.aborted) return;
    // The app's click: the ring round the pointer swells and settles (CursorHighlightRenderer).
    ink.clickGhost();
    control.classList.add("is-pressed");
    await wait(150);
    if (!signal.aborted) control.click();
    await wait(170);
    control.classList.remove("is-pressed");
  };
  const beats = [
    () => say("draw"),
    () => play([steps.ring, steps.arrow]),
    () => say("use"),
    () => press(prev),
    () => wait(1300),
    () => press(next),
    () => wait(700),
    () => say("draw"),
    () => play([steps.label, steps.blur]),
    () => say("use"),
    () => { ink.hideGhost(); return wait(2600); },
    // The app's Shrink: the toolbar becomes its one button, and opens again.
    () => away([{ shrink: true }, { wait: 1100 }, { shrink: false }]),
    () => wait(900),
    // The app's Hide and Show (Control-2), in its Magic style: each drawing flies to the toolbar on a swoop of its
    // own and the toolbar goes in a puff of sparks; then they come back, each by another way.
    () => away([{ hide: true, style: "magic" }, { wait: 1000 }, { hide: false, style: "magic" }]),
    () => wait(1500),
    // The round ends as it would in the app: Clear, and everything goes at once.
    () => (trash?.getClientRects().length ? play([{ click: trash, pause: 500 }]) : wait(200)),
  ];
  for (const beat of beats) { if (signal.aborted) return; await beat(); }
}
async function playDemo() {
  const run = ++demo.run, live = () => run === demo.run;
  demo.playing = true;
  showControl();
  syncStage();
  await demo.busy;
  let release;
  demo.busy = new Promise((done) => { release = done; });
  try {
    while (live()) {
      await onStage.ready();
      if (!live()) break;
      forget();
      demo.picked = null;
      report.reset(motion);
      if (!motion) {
        // Without motion there is no story to watch: the finished drawing is simply there.
        const steps = heroSteps(), made = await ink.perform([steps.ring, steps.arrow, steps.label, steps.blur], { cursor: false });
        made.forEach((mark) => demo.ids.add(mark.id));
        if (live()) { demo.playing = false; demo.run += 1; replay.hidden = true; syncStage(); }
        break;
      }
      demo.controller = new AbortController();
      await round(demo.controller.signal);
      ink.hideGhost();
      // The visitor took over: everything stays as it is.
      if (!live()) break;
      // The stage went out of view in the middle of a round: it starts again when it is back.
      if (demo.away) { if (ink.state.hidden) await ink.setHidden(false); else if (ink.collapsed) await ink.setCollapsed(false); demo.away = false; }
      forget();
      await wait(700);
    }
  } finally { release(); }
}
ink.on("hide", syncStage);
ink.on("collapse", syncStage);
// A tool the visitor picks is theirs to keep (the script has stopped by then: see the toolbar's pointerdown).
ink.on("tool", (tool) => { if (!demo.playing) demo.picked = tool; });
ink.on("mark", () => { demo.drawn = true; stopDemo(); syncStage(); });
ink.on("mode", (mode) => {
  syncStage();
  if (demo.playing) return;
  // Drawing started with its key while the stage is off screen leaves the stage alone until it is on screen.
  if (mode === "draw" ? onStage.near : !onStage.near) engage(mode === "draw");
});
// The visitor's own input on the stage ends the script: a press on the toolbar, or a tool's key. The app's three
// keys (Control-Shift-2, Esc, Control-2) are the engine's; they join in, and joining in ends the script (above).
// Nothing moves under a pressed pointer: a press that reaches the toolbar while the stage still leans lays it flat
// when the pointer lifts (joining in does that); the keyboard reaching the toolbar lays it flat at once.
let pressing = false;
addEventListener("pointerdown", () => { pressing = true; }, true);
for (const name of ["pointerup", "pointercancel", "blur"]) addEventListener(name, () => { pressing = false; }, true);
ink.toolbar.addEventListener("pointerdown", (e) => { if (e.isTrusted) stopDemo(); }, true);
// A press on the screen while the script is drawing is the visitor's first stroke: it is made with their own tool
// (the Pen, until they choose another), not with the one the script happened to be holding.
board.addEventListener("pointerdown", (e) => {
  if (!e.isTrusted || !demo.playing || ink.state.mode !== "draw") return;
  stopDemo();
  ink.setTool(demo.picked || "pen");
}, true);
ink.toolbar.addEventListener("focusin", () => { if (!demo.playing && !pressing) engage(true); });
addEventListener("keydown", (e) => {
  if (e.isTrusted && demo.playing && ink.state.mode === "draw" && /^[0-9a-z]$/i.test(e.key) && !e.metaKey && !e.ctrlKey && !e.altKey) stopDemo();
}, true);
replay.addEventListener("click", () => {
  const playing = demo.playing;
  stopDemo();
  ink.setMode("use");
  if (!playing) playDemo();
});
done.addEventListener("click", () => ink.setMode("use"));
if (window.IntersectionObserver) new IntersectionObserver(([entry]) => {
  onStage.near = entry.isIntersecting;
  stage.classList.toggle("is-away", !entry.isIntersecting);
  if (entry.isIntersecting) { if (ink.state.mode === "draw" && !demo.playing) engage(true); return; }
  // Out of view, the script's round ends and drawing ends, so keys and touches go back to the page, and the stage
  // is free to float again.
  if (demo.playing) demo.controller?.abort();
  ink.setMode("use");
  engage(false);
}).observe(screen);

// "Try it": from anywhere on the page, the stage comes into view with the Pen in hand. Bringing it into view and
// opening its frame is the growing stage's part: it hears the same click.
const restart = (element, name) => { element.classList.remove(name); void element.offsetWidth; element.classList.add(name); };
function tryIt(e) {
  e.preventDefault();
  stopDemo();
  if (ink.collapsed) ink.setCollapsed(false);
  ink.setTool("pen");
  restart(stage, "is-called");
  ink.toolbar.querySelector('[data-tool="pen"]')?.focus({ preventScroll: true });
}
for (const link of $$("[data-try]")) link.addEventListener("click", tryIt);

Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {}).then(async () => {
  if (motion) await wait(1400);
  lifted = true;
  schedule();
  if (motion) await wait(700);
  // A visitor who has already started is not interrupted.
  if (ink.state.mode === "draw" || demo.drawn) return;
  playDemo();
});

// Theme -------------------------------------------------------------------------------------------
// The script in the page's head has already set the theme. The switch changes it, remembers the choice and tells
// everything that keeps colors of its own: the toolbar, the light and the browser's own chrome.
const THEME_KEY = "annotator.concept-h2.theme";
const themeSwitch = $("#theme-switch"), dial = $(".dial", themeSwitch), curtain = $("#curtain"), themeMeta = $('meta[name="theme-color"]');
let theme = root.dataset.theme === "light" ? "light" : "dark", turns = theme === "light" ? 1 : 0;
function showTheme(next) {
  theme = next;
  root.dataset.theme = next;
  themeSwitch.setAttribute("aria-checked", String(next === "light"));
  themeMeta.content = token("--page");
  ink.setAppearance(next);
  for (const aurora of Object.values(auroras)) aurora.setTheme(next);
}
// The colors change in one step, with nothing easing, so a view transition's new picture is the new theme whole.
function swapColors(next) {
  root.classList.add("theme-switching");
  showTheme(next);
  void root.offsetWidth;
  root.classList.remove("theme-switching");
}
// The band of light that rides on the edge of the new theme as it comes down the page.
const FALL = { duration: 650, easing: "cubic-bezier(.5, .1, .3, 1)" };
let falling = null;
function dropCurtain() {
  falling?.cancel();
  const strength = Number(token("--curtain")) || .5, from = -innerHeight * .62, to = innerHeight * .98;
  falling = curtain.animate([
    { transform: `translate3d(0, ${from}px, 0)`, opacity: 0 },
    { opacity: strength, offset: .18 },
    { opacity: strength, offset: .7 },
    { transform: `translate3d(0, ${to}px, 0)`, opacity: 0 },
  ], FALL);
}
let fadeTimer = 0;
function setTheme(next) {
  if (next === theme) return;
  try { localStorage.setItem(THEME_KEY, next); } catch { /* storage is off */ }
  // The wheel always turns the same way: what was up goes down on the right, and the other rises on the left.
  turns += 1;
  dial.style.setProperty("--turn", `${turns * 180}deg`);
  if (!motion) { swapColors(next); return; }
  dropCurtain();
  if (document.startViewTransition && !new URLSearchParams(location.search).has("fade")) {
    const transition = document.startViewTransition(() => swapColors(next));
    changing = transition;
    transition.finished.finally(() => { if (changing === transition) changing = null; });
    return;
  }
  root.classList.add("theme-fading");
  showTheme(next);
  clearTimeout(fadeTimer);
  fadeTimer = setTimeout(() => root.classList.remove("theme-fading"), 360);
}
themeSwitch.addEventListener("click", () => setTheme(theme === "light" ? "dark" : "light"));
// A page inside a view transition takes no pointer input: every press lands on the root element. So a press during
// the theme change ends the change at once and is passed on to what is under the pointer; nobody waits for it.
let changing = null, passedOn = null;
addEventListener("pointerdown", (e) => {
  if (!e.isTrusted) return;
  passedOn = null;
  if (!changing) return;
  changing.skipTransition();
  changing = null;
  falling?.cancel();
  const target = document.elementFromPoint(e.clientX, e.clientY);
  if (!target || target === root) return;
  passedOn = target;
  target.dispatchEvent(new PointerEvent("pointerdown", e));
}, true);
// The press that was passed on must not also start selecting the page's text.
addEventListener("mousedown", (e) => { if (passedOn && e.isTrusted && e.target === root) e.preventDefault(); }, true);
addEventListener("click", (e) => {
  const target = passedOn;
  passedOn = null;
  if (!target || !e.isTrusted || e.target !== root) return;
  const under = document.elementFromPoint(e.clientX, e.clientY);
  if (under && (target.contains(under) || under.contains(target))) under.closest("a, button")?.click();
}, true);
showTheme(theme);

// What you can do ---------------------------------------------------------------------------------
const what = $("#what"), arc = $("#arc"), tiles = $$(".tile", arc), arcSpace = $(".arc-space"), stepsBar = $("#arc-steps"), countLabel = $("#arc-count"), clipsToggle = $("#clips-toggle");
// The first card is a small sample screen that a pointer draws on, again and again while its card is the one in
// front. `play` draws one round. The other three cards hold empty frames (data-scene) for the shared scenes.
function createScene(surface, play) {
  const scene = createInk({ surface, interactive: false, size: 3, gradient: "aurora" }), a = anchorsFor(scene, surface);
  let active = false, running = false, ids = [];
  const made = (marks) => { for (const mark of [].concat(marks)) ids.push(mark.id); return marks; };
  const tools = { scene, a, made, draw: async (steps) => made(await scene.perform(steps, { cursor: motion })) };
  async function run() {
    if (running) return;
    running = true;
    while (active) {
      if (!ids.length) {
        await play(tools);
        scene.hideGhost();
      }
      if (!motion) break;
      await wait(2800);
      if (!active) break;
      await leave(scene, ids);
      ids.forEach((id) => scene.remove(id));
      ids = [];
      await wait(600);
    }
    running = false;
  }
  // Without motion there is nothing to play: the finished drawing is simply there.
  if (!motion) play(tools);
  return { set(on) { if (!motion) return; active = on; if (on) run(); } };
}
const pause = (ms) => (motion ? wait(ms) : Promise.resolve());
const sample = $("#sample-draw");
const scenes = new Map([
  // A ring round a number, a rough box that Smart Draw makes neat round the peak, an arrow from one to the other and
  // a few words beside it.
  [sample, async ({ scene, a, draw, made }) => {
    const number = $("[data-ring]", sample), peak = $("[data-peak]", sample), frame = { tool: "rect", around: a.box(peak), padX: 0, padY: 0 };
    // Distances are in the sample screen's own type size, so the drawing keeps its shape on a small card.
    const em = () => parseFloat(getComputedStyle($(".mini", sample)).fontSize);
    const [, rough] = await draw([
      { wait: 350 },
      { tool: "ellipse", around: a.box(number), get padX() { return .5 * em(); }, get padY() { return .32 * em(); }, duration: 750, pause: 240 },
      { ...frame, rough: 5, duration: 900 },
    ]);
    await pause(300);
    const b = a.rect(peak);
    made(scene.add({ type: "rect", x: b.x, y: b.y, w: b.w, h: b.h, size: 3, rough: 0, round: 9, anchor: frame }));
    // The app's snap: the shape is there as the stroke goes, 0.22 s.
    scene.layer.querySelector(`[data-id="${rough.id}"]`)?.classList.add("is-snapping");
    await pause(220);
    scene.remove(rough.id);
    await pause(260);
    await draw([
      { tool: "arrow", from: () => { const n = a.rect(number); return [n.x + n.w + .8 * em(), n.y + n.h / 2 - .1 * em()]; }, to: () => { const box = a.rect(peak); return [box.x - .4 * em(), box.y + box.h * .64]; }, bend: .22, duration: 700, pause: 180 },
      // The note sits to the right of the box.
      { tool: "text", color: LABEL_INK, text: "launch day", speed: 40, at: () => { const box = a.rect(peak); return [box.x + box.w + .7 * em(), box.y - .15 * em()]; } },
    ]);
  }],
].map(([surface, play]) => [surface.closest(".tile"), createScene(surface, play)]));

let clipsPaused = !motion, front = -1;
const shown = new Set();
const refreshTiles = () => tiles.forEach((tile, i) => scenes.get(tile)?.set(!clipsPaused && (motion ? i === front : shown.has(tile))));
// Without motion the examples are finished pictures: there is nothing to pause.
clipsToggle.hidden = !motion;
const labelClips = () => {
  clipsToggle.classList.toggle("is-off", clipsPaused);
  clipsToggle.setAttribute("aria-label", clipsPaused ? "Play the examples" : "Pause the examples");
};
labelClips();
clipsToggle.addEventListener("click", () => { clipsPaused = !clipsPaused; labelClips(); refreshTiles(); });

if (motion) {
  // The carousel. The section is three steps taller than the screen; each step turns the ring by one card.
  const last = tiles.length - 1;
  $("#arc-progress").hidden = false;
  const stepButtons = tiles.map((tile, i) => {
    const button = document.createElement("button");
    button.type = "button";
    button.setAttribute("aria-label", `Show: ${$("h3", tile).textContent}`);
    button.addEventListener("click", () => goTo(i));
    // Reached with the keyboard, a stop brings its card to the front, so Tab walks through the cards too. It waits a
    // frame, because the browser may first scroll the stop into view.
    button.addEventListener("focus", () => {
      if (!button.matches(":focus-visible")) return;
      requestAnimationFrame(() => { const { top, step } = metrics(); if (document.activeElement === button && Math.abs(scrollY - (top + i * step)) > 3) goTo(i); });
    });
    stepsBar.append(button);
    return button;
  });
  const metrics = () => { const top = what.getBoundingClientRect().top + scrollY; return { top, step: (what.offsetHeight - innerHeight) / last }; };
  const goTo = (i) => { const { top, step } = metrics(); scrollTo({ top: Math.round(top + i * step) + 1, behavior: "smooth" }); };
  tiles.forEach((tile, i) => tile.addEventListener("click", () => { if (i !== front) goTo(i); }));
  // With the keyboard: the stops are buttons, and the arrow keys walk along them.
  stepsBar.addEventListener("keydown", (e) => {
    const now = stepButtons.indexOf(document.activeElement);
    const to = e.key === "ArrowRight" ? now + 1 : e.key === "ArrowLeft" ? now - 1 : e.key === "Home" ? 0 : e.key === "End" ? last : null;
    if (now < 0 || to === null) return;
    e.preventDefault();
    stepButtons[clamp(to, 0, last)].focus({ preventScroll: true });
  });

  // The ring's radius is the one at which neighbors sit a gap apart at this card width and angle.
  const sizeRing = () => {
    const styles = getComputedStyle(what), angle = parseFloat(styles.getPropertyValue("--theta")) || 26, gap = parseFloat(styles.getPropertyValue("--arc-gap")) || 40;
    const width = tiles[0].offsetWidth;
    if (width) arc.style.setProperty("--R", ((width + gap) / 2 / Math.tan(angle * Math.PI / 360)).toFixed(1));
  };
  sizeRing();
  addEventListener("resize", sizeRing);

  let inRing = false, place = 0, entered = 1;
  // The card in front is the one that plays. Between two cards, and away from the carousel, none does.
  const updateFront = () => {
    const nearest = Math.round(place), now = inRing && Math.abs(place - nearest) < .2 ? nearest : -1;
    if (now === front) return;
    front = now;
    tiles.forEach((tile, i) => tile.classList.toggle("is-front", i === front));
    refreshTiles();
  };
  const layout = () => {
    const nearest = Math.round(place), swing = (1 - entered) * 1.15;
    tiles.forEach((tile, i) => {
      const d = i - place + swing, ad = Math.abs(d);
      tile.style.setProperty("--d", d.toFixed(4));
      tile.style.setProperty("--ad", Math.min(ad, 1).toFixed(4));
      tile.style.setProperty("--o", clamp(1 - (ad - 1.15) / .7).toFixed(3));
      tile.style.visibility = ad > 1.9 ? "hidden" : "";
      stepButtons[i].classList.toggle("is-past", i < nearest);
      if (i === nearest) stepButtons[i].setAttribute("aria-current", "true"); else stepButtons[i].removeAttribute("aria-current");
    });
    stepsBar.style.setProperty("--p", (place / last).toFixed(4));
    countLabel.textContent = String(nearest + 1);
    updateFront();
  };
  const turn = follow((p) => { place = p; layout(); }, 120, .0004);
  // Coming up the page, the ring swings round into place: its first card arrives from the side.
  const enter = follow((e) => { entered = e; arcSpace.style.setProperty("--enter", e.toFixed(4)); layout(); });
  const light = follow((v) => auroras.arc?.setShift(v), 200, .002);
  readers.push(({ y, h }) => {
    const { top, step } = metrics(), raw = clamp((y - top) / step, 0, last), whole = Math.floor(raw);
    // Each card rests for a moment before the ring starts to turn again.
    turn.target = Math.min(last, whole + smooth(clamp((raw - whole - .14) / .72)));
    enter.target = smooth(clamp((y + h - top) / (h * .85)));
    light.target = raw / last;
    inRing = y + h * .55 > top && y < top + what.offsetHeight - h * .45;
    updateFront();
  });
} else if (window.IntersectionObserver) {
  const seen = new IntersectionObserver((entries) => {
    for (const entry of entries) { if (entry.isIntersecting) shown.add(entry.target); else shown.delete(entry.target); }
    refreshTiles();
  }, { threshold: .5 });
  tiles.forEach((tile) => seen.observe(tile));
}

// Hairlines that catch the light ------------------------------------------------------------------
if (hovers) {
  for (const panel of $$("[data-light]")) {
    let pending = null;
    const apply = () => {
      const e = pending;
      pending = null;
      if (!e) return;
      const box = panel.getBoundingClientRect();
      panel.style.setProperty("--mx", `${(e.clientX - box.left).toFixed(1)}px`);
      panel.style.setProperty("--my", `${(e.clientY - box.top).toFixed(1)}px`);
      panel.classList.add("is-lit");
    };
    panel.addEventListener("pointermove", (e) => {
      if (e.pointerType === "touch") return;
      if (!pending) requestAnimationFrame(apply);
      pending = e;
    });
    panel.addEventListener("pointerleave", () => { pending = null; panel.classList.remove("is-lit"); });
  }
}

// The mode, on the stage ----------------------------------------------------------------------------
const modeChip = $("#mode-chip");
function showMode(mode) {
  modeChip.dataset.mode = mode;
  $("b", modeChip).textContent = mode === "draw" ? "Drawing" : "Using apps";
  for (const key of $$("[data-key-for]", modeChip)) key.hidden = key.dataset.keyFor !== mode;
  if (motion) restart(modeChip, "is-changing");
}
ink.on("mode", showMode);

// The cup follows the scroll ------------------------------------------------------------------------
if (motion) {
  for (const element of $$("[data-scroll]")) {
    const through = follow((p) => element.style.setProperty("--p", p.toFixed(4)));
    readers.push(({ h }) => {
      const box = element.getBoundingClientRect();
      if (box.top > h * 1.6 || box.bottom < -h * .6) return;
      through.target = clamp((h - box.top) / (h + box.height));
    });
  }
}

// Arriving -------------------------------------------------------------------------------------------
if (motion) {
  // Each word of a heading becomes a piece that can rise on its own. An element in a heading counts as one word.
  for (const heading of $$("[data-words]")) {
    const pieces = [];
    for (const node of [...heading.childNodes]) {
      if (node.nodeType === Node.TEXT_NODE) {
        for (const word of node.textContent.split(/\s+/).filter(Boolean)) {
          const span = document.createElement("span");
          span.textContent = word;
          pieces.push(span);
        }
      } else if (node.nodeType === Node.ELEMENT_NODE) pieces.push(node);
    }
    heading.textContent = "";
    pieces.forEach((piece, i) => {
      piece.classList.add("word");
      piece.style.setProperty("--w", i);
      heading.append(piece, i < pieces.length - 1 ? " " : "");
    });
  }
  const arrive = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const element = entry.target;
      arrive.unobserve(element);
      element.classList.add("is-in");
      setTimeout(() => element.classList.add("is-settled"), 1000 + Number(element.style.getPropertyValue("--i") || 0) * 80);
    }
  }, { rootMargin: "0px 0px -10% 0px", threshold: .15 });
  $$("[data-enter], [data-words], [data-sweep]").forEach((element) => arrive.observe(element));
  // The cup steams for as long as it is on screen.
  const coffee = $(".coffee-panel");
  new IntersectionObserver(([entry]) => coffee.classList.toggle("is-steaming", entry.isIntersecting), { threshold: .3 }).observe(coffee);
}

// Install -------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const status = $("#status");
for (const button of $$("[data-copy]")) {
  const command = button.closest(".command"), label = $("p", command), code = $("code", command);
  let timer = 0;
  button.addEventListener("click", async () => {
    let copied = true;
    try { await navigator.clipboard.writeText(code.textContent.trim()); }
    catch {
      const range = document.createRange();
      range.selectNodeContents(code);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      copied = document.execCommand("copy");
    }
    const said = copied ? "Copied. Paste it into Terminal and press Enter." : "Selected. Copy it, then paste it into Terminal and press Enter.";
    label.textContent = said;
    status.textContent = said;
    clearTimeout(timer);
    timer = setTimeout(() => { label.textContent = label.dataset.said; status.textContent = ""; }, 5000);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

schedule();
