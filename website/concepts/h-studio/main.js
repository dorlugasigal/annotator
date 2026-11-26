import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
import { breakTimerScene, shareRegionScene, toolbarScene } from "/concepts/shared/scenes.js";
import { css as CURVE, clickPulse } from "/concepts/shared/app-motion.js";

const root = document.documentElement;
const motion = root.classList.contains("motion");
const hovers = matchMedia("(hover: hover)").matches;
const coarse = matchMedia("(pointer: coarse)").matches;
const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const smooth = (t) => t * t * (3 - 2 * t);
const ease = (t) => (t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
// Ink colors are the page's own tokens, so the stylesheet stays the one place colors are written down.
const token = (name) => getComputedStyle(root).getPropertyValue(name).trim();
const OCEAN = token("--ocean-2");
// The page eases with the app's own curves (MotionCore.swift, through the shared port).
root.style.setProperty("--ease-out", CURVE.enter);
root.style.setProperty("--ease-in-out", CURVE.standard);
root.style.setProperty("--ease-leave", CURVE.exit);
// One of the app's keyframe lists, played on an element of the page.
const playKeys = (element, list, options = {}) => element.animate(list, { duration: list.duration, easing: list.easing, ...options });
// What a stopped script throws, so a sequence of steps ends at the next one.
const STOP = Symbol("stop");
function tween(ms, step) {
  return new Promise((done) => {
    const start = performance.now();
    const frame = (now) => {
      const t = clamp((now - start) / ms);
      step(ease(t));
      if (t < 1) requestAnimationFrame(frame); else done();
    };
    requestAnimationFrame(frame);
  });
}

// Where things are -------------------------------------------------------------------------------
// The engine measures with getBoundingClientRect, which is wrong on a plane that leans in space. These give it
// positions in the plane's own layout instead, so marks land on their targets at any angle.
function localBox(element, surface) {
  let x = 0, y = 0, node = element;
  while (node && node !== surface) {
    x += node.offsetLeft; y += node.offsetTop;
    // The toolbar is centered with a translate, which its offsets do not include.
    if (node.classList.contains("ink-toolbar") && !node.classList.contains("is-placed")) x -= node.offsetWidth / 2;
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
    // Stands in for an element where a step needs one ("around", "over"). The engine measures from the surface's
    // corner, which stays put while a growing stage lets the ink layer reach past it.
    box: (element) => ({
      getBoundingClientRect() { const o = surface.getBoundingClientRect(), b = rect(element); return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h }; },
    }),
    point: (element, ax = .5, ay = .5, dx = 0, dy = 0) => () => { const b = rect(element); return [b.x + b.w * ax + dx, b.y + b.h * ay + dy]; },
  };
}
// The size a piece of ink text will have, so it can be centered on something.
function measureInk(ink, text) {
  const probe = document.createElement("div");
  probe.className = "ink-mark ink-text";
  probe.style.visibility = "hidden";
  probe.textContent = text;
  $(".ink-over", ink.layer).append(probe);
  const size = { w: probe.offsetWidth, h: probe.offsetHeight };
  probe.remove();
  return size;
}
const leave = async (ink, ids) => {
  if (!motion || !ids.length) return;
  for (const id of ids) ink.layer.querySelector(`[data-id="${id}"]`)?.classList.add("is-leaving");
  await wait(480);
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

const header = $(".site-header"), ambient = $(".ambient"), status = $("#status");
readers.push(({ y }) => {
  header.classList.toggle("is-stuck", y > 8);
  if (motion) ambient.style.setProperty("--scroll", Math.round(y));
});

// The sample app -----------------------------------------------------------------------------------
// A small weekly report that really works: step through the weeks, and point at a day for its number. The data
// is made up and stays on the page.
const WEEKS = [
  { name: "Week 45", range: "2 to 8 November", visitors: ["11,620", "+1%"], orders: ["91", "+3%"], signups: ["387", "+2%"], days: [54, 56, 53, 57, 56, 55, 56], before: [53, 54, 55, 54, 55, 54, 54],
    notes: ["The autumn sale ended on Sunday.", "Two help articles were rewritten."] },
  { name: "Week 46", range: "9 to 15 November", visitors: ["12,000", "+3%"], orders: ["94", "+3%"], signups: ["402", "+4%"], days: [55, 58, 56, 57, 60, 57, 59], before: [54, 56, 53, 57, 56, 55, 56],
    notes: ["A steady week: nothing shipped.", "The pricing test started on Friday."] },
  { name: "Week 47", range: "16 to 22 November", visitors: ["12,480", "+4%"], orders: ["96", "+2%"], signups: ["318", "\u221221%"], days: [52, 56, 53, 19, 44, 46, 48], before: [55, 58, 56, 57, 60, 57, 59],
    notes: ["The newsletter went out on Tuesday.", "The new sign-up form shipped on Thursday."] },
];
const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const LAST = WEEKS.length - 1;
const report = $("#report"), weekPrev = $("#week-prev"), weekNext = $("#week-next"), pointsBox = $("#points");
const plotX = (i) => 24 + i * 62, plotY = (value) => 136 - (value / 70) * 108;
const curve = (days) => days.map((value, i) => {
  const x = plotX(i), y = plotY(value).toFixed(1);
  return i ? `C${x - 31} ${plotY(days[i - 1]).toFixed(1)} ${x - 31} ${y} ${x} ${y}` : `M${x} ${y}`;
}).join("");
// Chrome eases a path's shape when it is set as a style; the attribute is for the browsers that do not.
const shape = (path, d) => { path.setAttribute("d", d); path.style.d = `path("${d}")`; };
const points = DAYS.map((day, i) => {
  const point = document.createElement("button");
  point.type = "button";
  point.className = "pt";
  point.style.setProperty("--x", (plotX(i) / 4.2).toFixed(2));
  point.innerHTML = '<span class="tip"></span>';
  point.addEventListener("click", () => points.forEach((other) => other.classList.toggle("is-shown", other === point && !point.classList.contains("is-shown"))));
  point.addEventListener("blur", () => point.classList.remove("is-shown"));
  pointsBox.append(point);
  return point;
});
let week = LAST;
function showWeek(index) {
  week = clamp(index, 0, LAST);
  const data = WEEKS[week];
  $("#week-name").textContent = data.name;
  $("#week-range").textContent = data.range;
  for (const key of ["visitors", "orders", "signups"]) {
    const [value, change] = data[key], delta = $(`[data-delta="${key}"]`);
    $(`[data-metric="${key}"]`).textContent = value;
    delta.textContent = change;
    delta.className = `delta ${change.startsWith("+") ? "up" : "down"}`;
  }
  $$("#notes li").forEach((item, i) => { item.textContent = data.notes[i]; });
  shape($("#plot-line"), curve(data.days));
  shape($("#plot-last"), curve(data.before));
  shape($("#plot-area"), `${curve(data.days)}V136H24Z`);
  points.forEach((point, i) => {
    point.style.setProperty("--y", (plotY(data.days[i]) / 1.6).toFixed(2));
    point.setAttribute("aria-label", `${DAYS[i]}: ${data.days[i]} sign-ups`);
    point.firstElementChild.textContent = `${DAYS[i].slice(0, 3)} \u00b7 ${data.days[i]}`;
  });
  weekPrev.disabled = week === 0;
  weekNext.disabled = week === LAST;
}
// The arrow of the scripted demo points at the lowest day of the last week.
const lowest = WEEKS[LAST].days.indexOf(Math.min(...WEEKS[LAST].days));
Object.assign($("#dip").style, { left: `${plotX(lowest) / 4.2}%`, top: `${plotY(WEEKS[LAST].days[lowest]) / 1.6}%` });
showWeek(LAST);
requestAnimationFrame(() => report.classList.add("is-live"));

// Hero stage --------------------------------------------------------------------------------------
const stage = $("#stage"), screen = $("#screen"), board = $("#board"), number = $("#ring-target"), plot = $("#plot"), dip = $("#dip"), secret = $("#private");
// The toolbar keeps the state the engine gives it (its properties row included), so no colors or sizes are set here.
// Its selected stroke color is the app's Ocean gradient, the same one the page uses as its accent. Hide drawings and
// tools (the toolbar's button, or Control-2) moves in the app's Magic style, as the scripted demo's does.
const ink = createInk({ surface: board, toolbarParent: $("#dock"), toolbarPosition: "absolute", appearance: root.dataset.theme === "light" ? "light" : "dark", gradient: "ocean", tool: "pen", visibility: "magic", moreHref: "#install" });
const at = anchorsFor(ink, board);
// Whether this engine shows, hides and shrinks things the way the app does (it then answers with a promise).
const appMoves = Boolean(ink.setCollapsed(ink.collapsed)?.then);
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the screen's
// frame opens up to fill the window, and scrolling brings it back. The board does not move: in index.html the desk and
// the toolbar's plane carry data-stage-frame and the two tags data-stage-corner. The frame grows only once the screen
// lies flat (`lean` and `lift` are the two followers further down). Until the visitor joins in, a "Draw here" pill
// follows their pointer over the stage.
let lean = null, lift = null;
const growing = growingStage({ ink, surface: board, root: screen, tries: "[data-try]", motion: () => motion, isFlat: () => !lean || (lean.value === 0 && lift.value === 0), invite: "Draw here" });
// The screen keeps room under the window for the toolbar. It remembers the tallest the toolbar has been at this
// size, so opening and closing the properties row moves nothing on the page. A grown frame has all the room it
// needs, so the board keeps its size until the frame is small again.
const tallest = {};
function reserve() {
  const key = tallest.now;
  if (!key || growing.state !== "small" || (tallest.shown === key && tallest.room === tallest[key])) return;
  tallest.shown = key;
  tallest.room = tallest[key];
  const room = tallest[key];
  requestAnimationFrame(() => { screen.style.setProperty("--tb-h", `${room}px`); schedule(); });
}
ink.on("layout", ({ height, size, rows }) => {
  if (!height || ink.collapsed) return;
  const key = `${size}|${rows}`;
  tallest[key] = Math.max(height, tallest[key] || 0);
  tallest.now = key;
  reserve();
});
growing.on("change", ({ state }) => { if (state === "small") { reserve(); schedule(); } });
ink.fit();

// The stage leans back at the top of the page and comes flat as it is scrolled up. It is also flat whenever
// the visitor is using it, because a flat plane is where the pointer and the ink agree.
let engaged = false, lifted = !motion;
if (motion) {
  lean = follow((k) => {
    stage.style.setProperty("--tilt", (k * (innerWidth < 600 ? 13 : 19)).toFixed(3));
    stage.style.setProperty("--scale", (1 - k * .055).toFixed(4));
    stage.classList.toggle("is-live", k > 0);
  });
  lift = follow((k) => stage.style.setProperty("--lift", k.toFixed(4)), 150);
  readers.push(({ y, h }) => {
    const top = stage.getBoundingClientRect().top + y, range = clamp(top - 90, 200, Math.max(200, h * .5));
    const k = engaged || growing.state !== "small" ? 0 : 1 - smooth(clamp(y / range));
    lean.target = k;
    lift.target = lifted ? k : 0;
  });
}
const engage = (on) => { if (engaged !== on) { engaged = on; schedule(); } };

// Draw or Use apps. The stage says which, and the sample app only answers in Use apps.
const modeChip = $("#mode-chip"), done = $("#done");
function showMode(mode) {
  const drawing = mode === "draw";
  modeChip.dataset.mode = mode;
  $("#mode-chip-name").textContent = drawing ? "Drawing" : "Using apps";
  report.inert = drawing;
  showDone();
}
// "Done drawing" is Esc as a button, for a visitor who is drawing (and for touch screens, which have no Esc).
function showDone() { done.hidden = !(ink.state.mode === "draw" && !demo.playing); }
done.addEventListener("click", () => ink.setMode("use"));
// What the scripted demo does to the toolbar and the sample app is told apart from what the visitor does.
let byScript = 0;
const scripted = (action) => { byScript += 1; try { return action(); } finally { byScript -= 1; } };

const small = () => screen.clientWidth < 620;
const stacked = () => at.rect(plot).y > at.rect(number).y + at.rect(number).h;
const LABEL = "why the dip?";
function heroSteps() {
  const size = small() ? 3 : 4, ring = { get x() { return small() ? 10 : 17; }, get y() { return small() ? 8 : 11; } };
  // The ring and the arrow name no color: they are drawn in whatever the toolbar has selected.
  return {
    ring: { tool: "ellipse", around: at.box(number), get padX() { return ring.x; }, get padY() { return ring.y; }, size, duration: 850, pause: 240 },
    arrow: {
      tool: "arrow", size, duration: 800, pause: 200,
      // Beside the chart the arrow leaves the ring's right side; above the chart it leaves the lower left, clear of the badge under the number.
      from: () => { const n = at.rect(number); return stacked() ? [n.x - ring.x * .4, n.y + n.h + ring.y * .7 + 4] : [n.x + n.w + ring.x + 8, n.y + n.h / 2 - 2]; },
      to: () => { const d = at.rect(dip); return stacked() ? [d.x + 8, d.y - 10] : [d.x - 6, d.y - 11]; },
      get bend() { return stacked() ? .16 : .24; },
    },
    label: { tool: "text", color: OCEAN, text: LABEL, speed: 46, pause: 320, at: () => { const d = at.rect(dip); return [d.x - measureInk(ink, LABEL).w / 2, d.y + (small() ? 7 : 12)]; } },
    blur: { tool: "blur", over: at.box(secret), padX: 6, padY: 3, duration: 650 },
  };
}

// The demo's own pointer. The engine's pointer lives in the ink, under the toolbar; this one can go anywhere on
// the stage, so it presses the toolbar's buttons and the sample app's. The two hand over to each other in place.
const pointer = { el: $("#stage-pointer"), x: 0, y: 0, z: 0, on: false };
const middle = (element) => { const b = localBox(element, screen); return [b.x + b.w / 2, b.y + b.h / 2]; };
// A day's point is placed by a transform, which layout offsets leave out, so its place is worked out from the chart's.
const dayPlace = (i) => { const b = localBox(plot, screen); return [b.x + (b.w * plotX(i)) / 420, b.y + (b.h * plotY(WEEKS[week].days[i])) / 160]; };
function pointerAt(x, y, z) {
  Object.assign(pointer, { x, y, z });
  pointer.el.style.setProperty("--x", `${x.toFixed(1)}px`);
  pointer.el.style.setProperty("--y", `${y.toFixed(1)}px`);
  pointer.el.style.setProperty("--z", z.toFixed(3));
  cueAside();
}
// The "Draw here" pill steps out from under the engine's pointer by itself. This does the same for the demo's own.
function cueAside() {
  const cue = growing.invite;
  if (!cue) return;
  let under = false;
  if (pointer.on) {
    const p = pointer.el.getBoundingClientRect(), c = cue.getBoundingClientRect(), pad = 28;
    under = p.left < c.right + pad && p.right > c.left - pad && p.top < c.bottom + pad && p.bottom > c.top - pad;
  }
  cue.classList.toggle("is-aside", under);
}
function pointerTake(toward) {
  const ghost = $(".ink-ghost", ink.layer);
  if (ghost.classList.contains("is-on")) pointerAt(Number(ghost.dataset.x), Number(ghost.dataset.y), 0);
  else if (!pointer.on) pointerAt(toward[0] + 90, toward[1] + 70, 0);
  ink.hideGhost();
  pointer.el.classList.add("is-on");
  pointer.on = true;
}
function pointerGive() { ink.placeGhost([pointer.x, pointer.y]); pointerHide(); }
function pointerHide() { pointer.el.classList.remove("is-on", "is-down"); pointer.on = false; cueAside(); }
// `plane` is 0 for the sample window and 1 for the toolbar, which floats above it while the stage leans.
async function glide(to, plane = 0) {
  const from = [pointer.x, pointer.y, pointer.z];
  await tween(clamp(Math.hypot(to[0] - from[0], to[1] - from[1]) * 1.7, 240, 640), (k) => pointerAt(from[0] + (to[0] - from[0]) * k, from[1] + (to[1] - from[1]) * k, from[2] + (plane - from[2]) * k));
}
async function press(element) {
  pointer.el.classList.add("is-down");
  element?.classList.add("is-pressed");
  // The ring the app draws round the pointer when it clicks.
  const ring = pointer.el.firstElementChild;
  playKeys(ring, clickPulse());
  ring.animate([{ opacity: .9 }, { opacity: 0 }], { duration: 450, easing: CURVE.fade });
  await wait(140);
  pointer.el.classList.remove("is-down");
  element?.classList.remove("is-pressed");
}

// The scripted demo ---------------------------------------------------------------------------------
// One round tells the app's idea: draw, then use the app underneath while the drawings stay, then draw again.
// It runs while the stage is on screen and stops for good the moment the visitor does anything on the stage.
const replay = $("#replay"), onStage = watcher(screen, .25);
const demo = { run: 0, cut: 0, playing: false, ids: new Set(), busy: Promise.resolve(), controller: null, picked: null, hid: false };
function showControl() {
  replay.hidden = false;
  replay.classList.toggle("is-off", !demo.playing);
  replay.lastElementChild.textContent = demo.playing ? "Pause" : "Replay";
  showDone();
}
function forget() {
  for (const id of demo.ids) ink.remove(id);
  demo.ids.clear();
}
// Hide and show are the app's own (Control-2): the drawings go, and the toolbar shrinks to its one button. The
// demo uses the app's Magic style, where each drawing flies off on a swoop.
async function hideAll() {
  if (appMoves) { await scripted(() => ink.setHidden(true, { style: "magic" })); return; }
  await leave(ink, ink.marks().map((mark) => mark.id));
  scripted(() => ink.setHidden(true));
}
// Back to how the stage rests, at once: nothing leaving, the toolbar open, and what the script hid back in place.
// What the visitor hid (the toolbar's Hide button, or Control-2) stays hidden.
function tidy() {
  pointerHide();
  ink.hideGhost();
  points.forEach((point) => point.classList.remove("is-shown"));
  $$(".is-leaving", ink.layer).forEach((mark) => mark.classList.remove("is-leaving"));
  if (ink.state.hidden && !demo.hid) return;
  scripted(() => { if (ink.state.hidden) ink.setHidden(false, { animate: false }); if (ink.collapsed) ink.setCollapsed(false, { animate: false }); });
}
ink.on("hide", (hidden) => { demo.hid = hidden && byScript > 0; });
function stopDemo() {
  if (!demo.playing) return;
  demo.playing = false;
  demo.run += 1;
  demo.controller?.abort();
  stage.classList.remove("is-demo");
  tidy();
  showControl();
}
async function draw(steps, signal) {
  demo.picked = null;
  const made = await ink.perform(steps, { cursor: true, showTool: true, signal });
  made.forEach((mark) => demo.ids.add(mark.id));
  // A script puts the toolbar back on the tool it started with. If the visitor chose one meanwhile, theirs wins.
  if (demo.picked && ink.state.tool !== demo.picked) ink.setTool(demo.picked, true);
}
async function round(ok, signal) {
  const steps = heroSteps(), modeButton = $(".ink-mode", ink.toolbar), pill = $(".ink-pill", ink.toolbar), day = points[lowest];
  // Draw: a ring around the number that dropped and an arrow to the day it happened.
  pointerTake(middle(modeButton));
  await ok(glide(middle(modeButton), 1));
  await ok(press());
  scripted(() => ink.setMode("draw"));
  await ok(wait(420));
  pointerGive();
  await ok(draw([steps.ring, steps.arrow], signal));
  // Use apps: the toolbar shrinks to one button, the report answers the pointer, and the drawings stay where they are.
  pointerTake(middle(modeButton));
  await ok(glide(middle(modeButton), 1));
  await ok(press());
  scripted(() => ink.setMode("use"));
  await ok(wait(380));
  setTimeout(() => { if (demo.playing && ink.state.mode === "use" && pointer.on) scripted(() => ink.setCollapsed(true)); }, 260);
  await ok(glide(middle(weekPrev)));
  await ok(press(weekPrev));
  scripted(() => weekPrev.click());
  await ok(wait(1250));
  await ok(glide(middle(weekNext)));
  await ok(press(weekNext));
  scripted(() => weekNext.click());
  await ok(wait(520));
  await ok(glide(dayPlace(lowest)));
  day.classList.add("is-shown");
  await ok(wait(1300));
  day.classList.remove("is-shown");
  // Draw again: pressing the one button brings the toolbar back, as it does in the app.
  await ok(glide(middle(pill), 1));
  await ok(press());
  await ok(scripted(() => ink.setCollapsed(false)));
  scripted(() => ink.setMode("draw"));
  await ok(wait(appMoves ? 160 : 460));
  pointerGive();
  await ok(draw([steps.label, steps.blur], signal));
  ink.hideGhost();
  await ok(wait(3000));
  await ok(hideAll());
  await ok(wait(700));
}
async function playDemo() {
  const run = ++demo.run;
  demo.playing = true;
  stage.classList.add("is-demo");
  showControl();
  await demo.busy;
  let release;
  demo.busy = new Promise((done) => { release = done; });
  try {
    while (run === demo.run) {
      await onStage.ready();
      if (run !== demo.run) break;
      // A round starts clean: no drawings, the last week, Use apps, and the toolbar coming back the way the app shows it.
      forget();
      showWeek(LAST);
      await scripted(() => (ink.state.hidden ? ink.setHidden(false, { style: "magic" }) : ink.collapsed ? ink.setCollapsed(false) : null));
      scripted(() => ink.setMode("use"));
      await wait(500);
      const cut = ++demo.cut, live = () => run === demo.run && cut === demo.cut;
      const ok = async (step) => { const value = await step; if (!live()) throw STOP; return value; };
      demo.controller = new AbortController();
      try { if (live()) await round(ok, demo.controller.signal); }
      catch (error) { if (error !== STOP) throw error; }
      if (run === demo.run) tidy();
    }
  } finally { release(); }
}
ink.on("tool", (tool) => { if (!byScript) demo.picked = tool; });
ink.on("mark", () => stopDemo());
ink.on("shortcut", () => { if (!byScript) stopDemo(); });
ink.on("mode", (mode) => {
  showMode(mode);
  if (byScript) return;
  if (mode === "draw") stopDemo();
  // Drawing chosen while the stage is off screen (the app's keys work anywhere) leaves it alone until it is in view.
  if (mode === "draw" ? onStage.near : !onStage.near) engage(mode === "draw");
});
ink.toolbar.addEventListener("pointerdown", () => { stopDemo(); engage(true); }, true);
ink.toolbar.addEventListener("focusin", () => engage(true));
replay.addEventListener("click", () => {
  if (demo.playing) { stopDemo(); scripted(() => ink.setMode("use")); return; }
  scripted(() => ink.setMode("use"));
  playDemo();
});
if (window.IntersectionObserver) new IntersectionObserver(([entry]) => {
  onStage.near = entry.isIntersecting;
  if (entry.isIntersecting) { if (ink.state.mode === "draw" && !demo.playing) engage(true); return; }
  // Out of view the demo waits, drawing ends so keys and touches go back to the page, and the stage may lean again.
  if (demo.playing) { demo.cut += 1; demo.controller?.abort(); } else ink.setMode("use");
  engage(false);
}).observe(screen);

// Try it here: from anywhere on the page, the stage is the visitor's, flat and with the Pen ready. The growing stage
// brings it into view and opens it up once the pointer has lifted.
function tryIt() {
  stopDemo();
  ink.setTool("pen");
  engage(true);
  stage.classList.remove("is-cued");
  void stage.offsetWidth;
  stage.classList.add("is-cued");
}
// Joining in ends the script and lays the screen flat: the frame grows only once it is.
growing.on("join", () => { stopDemo(); engage(true); });
for (const control of $$("[data-try]")) control.addEventListener("click", (e) => { e.preventDefault(); tryIt(); });
// While the demo plays, the ink lets presses through. A press on the sample app in Use apps uses the app; a
// press anywhere else on the stage hands it over, ready to draw. A touch that scrolls the page is left alone.
const usesApp = () => { stopDemo(); scripted(() => ink.setMode("use")); };
screen.addEventListener(coarse ? "click" : "pointerdown", (e) => {
  if (!demo.playing || byScript || e.target.closest(".ink-toolbar, [data-try]")) return;
  if (e.target.closest("#report button")) usesApp(); else tryIt();
});
report.addEventListener("focusin", () => { if (demo.playing && !byScript) usesApp(); });
for (const [button, by] of [[weekPrev, -1], [weekNext, 1]]) {
  button.addEventListener("click", () => {
    showWeek(week + by);
    if (!byScript) status.textContent = `${WEEKS[week].name}, ${WEEKS[week].range}`;
  });
}

Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {}).then(async () => {
  if (!motion) {
    // Without motion there is no demo to watch: the finished drawing is there, and the stage is the visitor's.
    const steps = heroSteps();
    await ink.perform([steps.ring, steps.arrow, steps.label, steps.blur], { cursor: false });
    stage.classList.remove("is-demo");
    return;
  }
  await wait(1250);
  lifted = true;
  schedule();
  await wait(650);
  playDemo();
});

// Theme -------------------------------------------------------------------------------------------
// The script in the page's head has already set the theme. The switch changes it, remembers the choice and tells
// everything that keeps colors of its own: the toolbar and the browser's own chrome.
const THEME_KEY = "annotator.concept-h.theme";
const themeSwitch = $("#theme-switch"), coin = $(".coin", themeSwitch), coinLift = $(".coin-lift", themeSwitch), themeMeta = $('meta[name="theme-color"]');
let theme = root.dataset.theme === "light" ? "light" : "dark", turns = theme === "light" ? 1 : 0;
// The shared engine's live scenes in "What you can do" keep an appearance of their own, like the toolbar.
const liveScenes = [];
function showTheme(next) {
  theme = next;
  root.dataset.theme = next;
  themeSwitch.setAttribute("aria-checked", String(next === "light"));
  themeMeta.content = token("--page");
  ink.setAppearance(next);
  liveScenes.forEach((scene) => scene.setAppearance(next));
}
// The colors change in one step, with nothing easing, so a view transition's new picture is the new theme whole.
function swapColors(next) {
  root.classList.add("theme-switching");
  showTheme(next);
  void root.offsetWidth;
  root.classList.remove("theme-switching");
}
let fadeTimer = 0;
function setTheme(next) {
  if (next === theme) return;
  try { localStorage.setItem(THEME_KEY, next); } catch { /* storage is off */ }
  // The coin always turns the same way, so a second press carries on round instead of going back.
  turns += 1;
  coin.style.setProperty("--turn", `${turns * 180}deg`);
  if (!motion) { swapColors(next); return; }
  coinLift.animate([{ transform: "translateY(0) scale(1)" }, { transform: "translateY(-3px) scale(1.16)", offset: .42 }, { transform: "translateY(0) scale(1)" }], { duration: 540, easing: "cubic-bezier(.3, 0, .3, 1)" });
  if (document.startViewTransition && !new URLSearchParams(location.search).has("fade")) {
    const transition = document.startViewTransition(() => swapColors(next));
    changing = transition;
    transition.finished.finally(() => { if (changing === transition) changing = null; });
    return;
  }
  root.classList.add("theme-fading");
  showTheme(next);
  clearTimeout(fadeTimer);
  fadeTimer = setTimeout(() => root.classList.remove("theme-fading"), 340);
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
// The first card has a small sample screen that a pointer draws on, again and again while the card is in front. The
// other three are frames for the shared engine's live scenes (data-scene in index.html).
const what = $("#what"), deck = $("#deck"), tiles = $$(".tile", deck), deckSpace = $(".deck-space"), stepsBar = $("#deck-steps"), clipsToggle = $("#clips-toggle");
function createScene(surface, { play, still, leaves = "fade" }) {
  const scene = createInk({ surface, interactive: false, size: 3, gradient: "ocean" });
  const ctx = { scene, surface, at: anchorsFor(scene, surface), ok: (step) => step, signal: undefined };
  let active = false, running = false, pass = 0, controller = null;
  function clear() {
    scene.hideGhost();
    scene.marks().map((mark) => mark.id).forEach((id) => scene.remove(id));
    if (scene.state.hidden) scene.setHidden(false, { animate: false });
  }
  // The drawings leave the way they leave in the app, in one of the app's styles.
  const empty = () => (appMoves ? scene.setHidden(true, { style: leaves }) : leave(scene, scene.marks().map((mark) => mark.id)));
  async function run() {
    if (running) return;
    running = true;
    while (active) {
      const mine = ++pass;
      controller = new AbortController();
      ctx.signal = controller.signal;
      ctx.ok = async (step) => { const value = await step; if (!active || mine !== pass) throw STOP; return value; };
      try {
        await ctx.ok(wait(350));
        await ctx.ok(play(ctx));
        scene.hideGhost();
        await ctx.ok(wait(2800));
        await ctx.ok(empty());
      } catch (error) { if (error !== STOP) throw error; }
      clear();
      if (active) await wait(500);
    }
    running = false;
  }
  // Without motion there is nothing to play: the finished drawing is simply there.
  if (!motion) (still || play)(ctx);
  return { set(on) { if (!motion || on === active) return; active = on; if (on) run(); else { pass += 1; controller?.abort(); } } };
}
const play = (scene, steps, signal) => scene.perform(steps, { cursor: motion, signal });
// Draw on anything: a ring round the number, a box round the tall bar, an arrow from one to the other, and a note.
// The ring, the box and the arrow name no color, so they are drawn in the scene's gradient, the page's own.
const drawScene = $("#scene-draw");
const drawSteps = ({ at, surface }) => {
  const ring = $("[data-ring]", surface), peak = $("[data-peak]", surface), pad = () => Math.max(4, surface.clientWidth * .011);
  return [
    { tool: "ellipse", around: at.box(ring), get padX() { return ring.offsetHeight * .5; }, get padY() { return ring.offsetHeight * .3; }, duration: 700, pause: 200 },
    { tool: "rect", around: at.box(peak), get padX() { return pad(); }, get padY() { return pad(); }, duration: 560, pause: 200 },
    { tool: "arrow", from: () => { const r = at.rect(ring); return [r.x + r.w + r.h * .75, r.y + r.h * .6]; }, to: () => { const p = at.rect(peak); return [p.x + p.w / 2 - 2, p.y - pad() - 8]; }, bend: .3, duration: 640, pause: 200 },
    { tool: "text", text: "ask Maya", color: OCEAN, speed: 42, at: () => { const p = at.rect(peak); return [p.x + p.w + pad() * 2.6, p.y + pad()]; } },
  ];
};
const scenes = new Map([[drawScene.closest(".tile"), createScene(drawScene, {
  leaves: "pop",
  play: (ctx) => play(ctx.scene, drawSteps(ctx), ctx.signal),
  still: (ctx) => ctx.scene.perform(drawSteps(ctx), { cursor: false }),
})]]);
// The other three pictures are the shared engine's (shared/scenes.js), in the page's gradient and theme. Each plays
// while its card is in front, again and again with a short pause between, and starts over when the card comes back.
// Without motion each is asked for once, and shows its finished picture.
const SHARED = { share: shareRegionScene, break: breakTimerScene, toolbar: toolbarScene };
for (const frame of $$("[data-scene]", deck)) {
  const scene = SHARED[frame.dataset.scene](frame, { appearance: theme, gradient: "ocean" });
  liveScenes.push(scene);
  let active = false, pass = 0;
  const loop = async () => {
    const mine = ++pass, live = () => active && mine === pass;
    while (live() && (await scene.play()) && live()) await wait(1100);
  };
  scenes.set(frame.closest(".tile"), { set(on) { if (!motion || on === active) return; active = on; if (on) loop(); else { pass += 1; scene.stop(); scene.reset(); } } });
  if (!motion) scene.play();
}

let examplesPaused = !motion, front = -1;
const refreshTiles = () => tiles.forEach((tile, i) => scenes.get(tile)?.set(!examplesPaused && i === front));
if (motion) {
  clipsToggle.hidden = false;
  const label = () => {
    clipsToggle.classList.toggle("is-off", examplesPaused);
    clipsToggle.lastElementChild.textContent = examplesPaused ? "Play" : "Pause";
  };
  label();
  clipsToggle.addEventListener("click", () => { examplesPaused = !examplesPaused; label(); refreshTiles(); });

  // The deck. The section is one step taller than the screen for every card after the first; each step brings the
  // next card to the front.
  const last = tiles.length - 1;
  stepsBar.hidden = false;
  const stepButtons = tiles.map((tile, i) => {
    const button = document.createElement("button");
    button.type = "button";
    button.setAttribute("aria-label", `Show: ${$("h3", tile).textContent}`);
    button.addEventListener("click", () => goTo(i));
    stepsBar.append(button);
    return button;
  });
  const metrics = () => { const top = what.getBoundingClientRect().top + scrollY; return { top, step: (what.offsetHeight - innerHeight) / last }; };
  const goTo = (i) => { const { top, step } = metrics(); scrollTo({ top: Math.round(top + i * step) + 1, behavior: "smooth" }); };
  tiles.forEach((tile, i) => tile.addEventListener("click", () => { if (i !== front) goTo(i); }));

  let inDeck = false, place = 0;
  // The card in front is the one that plays. Between two cards, and away from the deck, none does.
  const updateFront = () => {
    const nearest = Math.round(place), now = inDeck && Math.abs(place - nearest) < .2 ? nearest : -1;
    if (now === front) return;
    front = now;
    tiles.forEach((tile, i) => tile.classList.toggle("is-front", i === front));
    refreshTiles();
  };
  const turnDeck = follow((p) => {
    place = p;
    const nearest = Math.round(p);
    tiles.forEach((tile, i) => {
      const d = i - p, ad = Math.abs(d);
      tile.style.setProperty("--d", d.toFixed(4));
      tile.style.setProperty("--ad", Math.min(ad, 1.6).toFixed(4));
      tile.style.setProperty("--a", clamp(d, -1, 1).toFixed(4));
      tile.style.setProperty("--o", clamp(1 - (ad - 1) / .7).toFixed(3));
      tile.style.setProperty("--z", String(100 - Math.round(ad * 10)));
      tile.style.visibility = ad > 1.75 ? "hidden" : "";
      stepButtons[i].style.setProperty("--fill", clamp(p - i + 1).toFixed(3));
      if (i === nearest) stepButtons[i].setAttribute("aria-current", "true"); else stepButtons[i].removeAttribute("aria-current");
    });
    updateFront();
  }, 110, .0004);
  const enter = follow((e) => deckSpace.style.setProperty("--enter", e.toFixed(4)));
  readers.push(({ y, h }) => {
    const { top, step } = metrics(), raw = clamp((y - top) / step, 0, last), whole = Math.floor(raw);
    // Each card rests for a moment before the next one starts to turn.
    turnDeck.target = Math.min(last, whole + smooth(clamp((raw - whole - .14) / .72)));
    enter.target = smooth(clamp((y + h - top) / (h * .8)));
    inDeck = y + h * .55 > top && y < top + what.offsetHeight - h * .45;
    updateFront();
  });
}

// Cards that turn toward the pointer --------------------------------------------------------------
tiles.forEach((tile) => $(".tile-inner", tile).setAttribute("data-tilt", ""));
if (motion && hovers) {
  for (const card of $$("[data-tilt]")) {
    let pending = null;
    const apply = () => {
      const e = pending;
      pending = null;
      if (!e) return;
      const box = card.getBoundingClientRect(), x = clamp((e.clientX - box.left) / box.width), y = clamp((e.clientY - box.top) / box.height);
      card.style.setProperty("--ry", ((x - .5) * 7).toFixed(2));
      card.style.setProperty("--rx", ((.5 - y) * 6).toFixed(2));
      card.style.setProperty("--mx", `${(x * 100).toFixed(1)}%`);
      card.style.setProperty("--my", `${(y * 100).toFixed(1)}%`);
      card.classList.add("is-near");
    };
    card.addEventListener("pointermove", (e) => {
      if (e.pointerType === "touch" || (card.closest(".tile") && !card.closest(".tile").classList.contains("is-front"))) return;
      if (!pending) requestAnimationFrame(apply);
      pending = e;
    });
    card.addEventListener("pointerleave", () => {
      pending = null;
      card.classList.remove("is-near");
      card.style.setProperty("--rx", 0);
      card.style.setProperty("--ry", 0);
    });
  }
}

showMode(ink.state.mode);

// A plane that follows the scroll: the cup -------------------------------------------------------------------
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
  const restart = (element, name) => { element.classList.remove(name); void element.offsetWidth; element.classList.add(name); };
  const coffee = $(".coffee-panel");
  const arrive = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const element = entry.target;
      arrive.unobserve(element);
      element.classList.add("is-in");
      if (element === coffee) element.classList.add("is-steaming");
      // Once it has landed, a card answers the pointer without the delay it arrived with.
      setTimeout(() => element.classList.add("is-settled"), 1000 + Number(element.style.getPropertyValue("--i") || 0) * 70);
    }
  }, { rootMargin: "0px 0px -10% 0px", threshold: .15 });
  $$("[data-enter], [data-words]").forEach((element) => arrive.observe(element));
  coffee.addEventListener("pointerenter", () => restart(coffee, "is-steaming"));
}
// A drawn underline for the header's links.
for (const link of $$(".nav-link")) link.insertAdjacentHTML("beforeend", '<svg class="scribble" viewBox="0 0 100 8" preserveAspectRatio="none" aria-hidden="true"><path stroke="url(#ocean)" d="M2 5C16 1 30 7 46 4S78 2 98 5"/></svg>');

// Install -------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
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
    const words = copied ? "Copied. Paste it into Terminal and press Enter." : "I couldn\u2019t copy it, so it\u2019s selected for you.";
    if (copied) label.textContent = words;
    status.textContent = words;
    clearTimeout(timer);
    timer = setTimeout(() => { label.textContent = label.dataset.label; status.textContent = ""; }, 3200);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

schedule();
