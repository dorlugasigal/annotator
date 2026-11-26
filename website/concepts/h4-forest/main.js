import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
import { breakTimerScene, shareRegionScene, toolbarScene } from "/concepts/shared/scenes.js";
import { ease, DURATIONS, workspaceTiming, distanceOrder, poseKeyframes, dissolve } from "/concepts/shared/app-motion.js";
import * as fx from "./fx.js";
import { createApps } from "./apps.js";

const root = document.documentElement;
const motion = root.classList.contains("motion");
const coarse = matchMedia("(pointer: coarse)").matches;
const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const smooth = (t) => t * t * (3 - 2 * t);
// Colors are the page's own tokens, so the stylesheet stays the one place colors are written down.
const token = (name) => getComputedStyle(root).getPropertyValue(name).trim();
const tween = (duration, step) => new Promise((done) => {
  const start = performance.now();
  const frame = (now) => { const t = clamp((now - start) / duration); step(t); if (t < 1) requestAnimationFrame(frame); else done(); };
  requestAnimationFrame(frame);
});

// Where things are -------------------------------------------------------------------------------
// The engine measures with getBoundingClientRect, which is wrong on a plane that leans in space. These give it
// positions in the plane's own layout instead, so marks land on their targets at any angle. A sample window that
// was moved counts with how far it was moved.
let shiftOf = () => null;
function localBox(element, surface) {
  let x = 0, y = 0, node = element;
  while (node && node !== surface) {
    const shift = shiftOf(node);
    x += node.offsetLeft + (shift ? shift[0] : 0); y += node.offsetTop + (shift ? shift[1] : 0);
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
      // The engine measures from the surface's corner. While the stage is grown the layer reaches past it (ink.room).
      getBoundingClientRect() { const o = ink.layer.getBoundingClientRect(), room = ink.room, b = rect(element); return { left: o.left + (room?.left || 0) + b.x, top: o.top + (room?.top || 0) + b.y, width: b.w, height: b.h }; },
    }),
    point: (element, ax = .5, ay = .5, dx = 0, dy = 0) => () => { const b = rect(element); return [b.x + b.w * ax + dx, b.y + b.h * ay + dy]; },
  };
}
const markEl = (ink, mark) => ink.layer.querySelector(`[data-id="${mark.id}"]`);
const boxOf = (ink, mark) => { const b = ink.box(mark); return { x: b.x, y: b.y, width: Math.max(1, b.w), height: Math.max(1, b.h) }; };

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

// Hero stage --------------------------------------------------------------------------------------
const stage = $("#stage"), screen = $("#screen"), board = $("#board"), dock = $("#dock"), dockFx = $("#dock-fx"), stageFx = $("#stage-fx"), trails = $(".fx-svg", stageFx);
const number = $("#ring-target"), plot = $("#plot"), dip = $("#dip"), secret = $("#private");
// Room on each side of the toolbar inside the stage's frame.
const barMargin = () => (innerWidth < 600 ? 10 : 22);
// The toolbar keeps the state the engine gives it (its properties row included), so no colors or sizes are set here.
// Its selected stroke color is the app's Forest gradient, the same one the page uses as its accent.
const ink = createInk({ surface: board, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme === "light" ? "light" : "dark", gradient: "forest", tool: "pen", moreHref: "#install", visibility: "magic", toolbarMargin: barMargin() });
addEventListener("resize", () => ink.fit({ margin: barMargin() }));
const at = anchorsFor(ink, board);
// The screen keeps room above the apps for the toolbar. It remembers the tallest the toolbar has been at this
// size, so opening and closing the properties row moves nothing on the page. While the stage is grown the room
// is left as it is (the apps must not move); only what sits under the toolbar, the mode chip, follows it.
const tallest = {};
ink.on("layout", ({ height, size, rows }) => {
  if (!height || ink.collapsed) return;
  const key = `${size}|${rows}`, small = (screen.dataset.stage || "small") === "small";
  if (height <= (tallest[key] || 0) && tallest.shown === key && small === tallest.small) return;
  tallest[key] = Math.max(height, tallest[key] || 0);
  tallest.shown = key;
  tallest.small = small;
  const room = tallest[key];
  requestAnimationFrame(() => { screen.style.setProperty("--tb-now", `${room}px`); if (small) screen.style.setProperty("--tb-h", `${room}px`); schedule(); });
});
ink.fit();

// What the page's own script does to the engine is not the visitor's doing. `quiet` marks it.
let scripted = 0;
const quiet = (fn) => { scripted += 1; try { return fn(); } finally { scripted -= 1; } };

// The sample apps. The first time the visitor uses one, the loop stops and the drawings let go of what they were
// drawn around: from then on they stay where they are on the screen, as they do in the app.
const apps = createApps({ board, onUse() { takeOver(); engage(true); for (const mark of ink.marks()) mark.anchor = null; } });
shiftOf = (node) => (node.classList?.contains("window") ? apps.shiftOf(node) : null);

// The stage leans back and a little to one side at the top of the page, and comes flat as it is scrolled up. It
// is also flat whenever the visitor is using it, because a flat plane is where the pointer and the ink agree.
let engaged = false, lifted = !motion, tilt = 0;
if (motion) {
  const lean = follow((k) => {
    const narrow = innerWidth < 600;
    tilt = k;
    stage.style.setProperty("--tilt", (k * (narrow ? 9 : 13)).toFixed(3));
    stage.style.setProperty("--yaw", (k * (narrow ? 0 : -5)).toFixed(3));
    stage.style.setProperty("--scale", (1 - k * .035).toFixed(4));
    stage.classList.toggle("is-live", k > 0);
  });
  const lift = follow((k) => stage.style.setProperty("--lift", k.toFixed(4)), 150);
  readers.push(({ y, h }) => {
    const top = stage.getBoundingClientRect().top + y, range = clamp(top - 70, 180, Math.max(180, h * .42));
    const k = engaged ? 0 : 1 - smooth(clamp(y / range));
    lean.target = k;
    lift.target = lifted ? k : 0;
  });
}
const engage = (on) => { if (engaged !== on) { engaged = on; schedule(); } };

// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the stage's
// frame opens up to fill the window, and scrolling brings it back (shared/stage.js). The apps do not move: in
// index.html the desk and the dock carry data-stage-frame, and the mode chip's row data-stage-corner.
const grow = growingStage({ ink, surface: board, root: screen, tries: "[data-try]", invite: "Draw here", motion: () => motion, isFlat: () => tilt === 0 });
apps.setRoom(() => grow.room);
grow.on("change", () => apps.refit());
// Joining in ends the script and lays the stage flat (the frame grows only once it is).
grow.on("join", () => { takeOver(); engage(true); });
// Back at its own size, the toolbar's room is measured again.
grow.on("change", ({ state }) => { if (state === "small") ink.fit(); });

// Whether the stage is in front of the visitor. When it leaves, drawing ends (so keys and touches go back to the
// page) and the toolbar shrinks to its one button; when it comes back the toolbar grows again.
const onStage = {
  near: false, waiting: [],
  check() { if (this.near && !document.hidden) this.waiting.splice(0).forEach((done) => done()); },
  ready() { return this.near && !document.hidden ? Promise.resolve() : new Promise((done) => this.waiting.push(done)); },
};
// The toolbar's Shrink is the engine's own (the app's morph). The page only says when.
const setBar = (collapsed) => (ink.collapsed === collapsed ? Promise.resolve() : collapsed ? ink.shrink() : ink.expand());
let shrunkByPage = false;
readers.push(({ h }) => {
  const b = stage.getBoundingClientRect();
  const near = onStage.near ? b.top < h - 110 && b.bottom > 90 : b.top < h - 200 && b.bottom > 170;
  if (near === onStage.near) return;
  onStage.near = near;
  if (near) {
    if (shrunkByPage) { shrunkByPage = false; setBar(false); }
    if (ink.state.mode === "draw") engage(true);
    onStage.check();
  } else {
    quiet(() => ink.setMode("use"));
    engage(false);
    if (motion && !ink.collapsed && stage.classList.contains("is-ready")) { shrunkByPage = true; setBar(true); }
  }
});
document.addEventListener("visibilitychange", () => onStage.check());

// The app's show and hide, on the stage: every drawing and the toolbar, in one of the app's styles.
const middle = (box) => [box.x + box.width / 2, box.y + box.height / 2];
function toolbarBox(surface) {
  const b = localBox(ink.toolbar, screen), s = surface === screen ? { x: 0, y: 0 } : localBox(surface, screen);
  // An unmoved toolbar is centered with `translate`, which layout positions do not include.
  return { x: b.x - s.x - (ink.toolbar.classList.contains("is-placed") ? 0 : b.w / 2), y: b.y - s.y, width: b.w, height: b.h };
}
function stageMove(style, appearing) {
  const g = fx.group(), marks = ink.marks().map((mark) => ({ id: `mark-${mark.id}`, el: markEl(ink, mark), box: boxOf(ink, mark) })).filter((mark) => mark.el);
  const timing = workspaceTiming(style, appearing, marks.length), target = middle(toolbarBox(board));
  const area = { x: 0, y: 0, width: board.clientWidth, height: board.clientHeight };
  const order = distanceOrder(marks.map((mark) => ({ id: mark.id, center: middle(mark.box), target })));
  const inkColors = [token("--forest-1"), token("--forest-2"), token("--forest-3")];
  for (const { id, el, box } of marks) {
    const delay = timing.drawingDelay(order.indexOf(id));
    if (style === "magic") fx.magic(g, el, { box, target, appearing, id, delay, trail: trails });
    else if (style === "corners") fx.corners(g, el, { box, area, appearing, id, delay });
    else if (style === "dissolve") fx.dissolved(g, el, { host: stageFx, box, appearing, palette: inkColors, delay, limit: 320, spread: 80 });
    else fx.posed(g, el, style, { appearing, id, delay });
  }
  const inDock = toolbarBox(dock), inScreen = toolbarBox(screen), delay = timing.toolbarDelay;
  dock.style.transformOrigin = `${middle(inDock)[0]}px ${middle(inDock)[1]}px`;
  g.undo(() => { dock.style.transformOrigin = ""; });
  if (style === "magic") fx.poof(g, dock, { appearing, host: dockFx, center: middle(inScreen), delay });
  else if (style === "corners") fx.slide(g, dock, { appearing, by: -(inScreen.y + inScreen.height + 24), delay });
  else if (style === "dissolve") fx.dissolved(g, dock, { host: dockFx, box: inScreen, appearing, palette: [token("--bar"), token("--bar"), token("--faint"), token("--forest-2")], delay, limit: 900, spread: 90, masked: false });
  else fx.posed(g, dock, style, { appearing, annotation: false, delay });
  return g;
}

const small = () => screen.clientWidth < 620;
const beside = () => at.rect(plot).y < at.rect(number).y + at.rect(number).h;
const LABEL = "why the dip?";
let pen = null;
function labelWidth() {
  pen ||= document.createElement("canvas").getContext("2d");
  pen.font = getComputedStyle(ink.layer).getPropertyValue("--ink-text").trim().replace(/\/[\d.]+/, "");
  return pen.measureText(LABEL).width + 8;
}
function heroSteps() {
  const size = small() ? 3 : 4, ring = { get x() { return small() ? 10 : 15; }, get y() { return small() ? 8 : 10; } };
  // The ring and the arrow name no color: they are drawn in whatever the toolbar has selected.
  return [
    { tool: "ellipse", around: at.box(number), get padX() { return ring.x; }, get padY() { return ring.y; }, size, duration: 850, pause: 240 },
    {
      tool: "arrow", size, duration: 800, pause: 240,
      // Beside the chart the arrow leaves the ring's right side; above the chart it leaves the lower left, clear of the badge under the number.
      from: () => { const n = at.rect(number); return beside() ? [n.x + n.w + ring.x + 8, n.y + n.h / 2 - 2] : [n.x - ring.x * .4, n.y + n.h + ring.y * .7 + 4]; },
      to: () => { const d = at.rect(dip); return beside() ? [d.x - 8, d.y - 12] : [d.x + 8, d.y - 12]; },
      get bend() { return beside() ? .22 : .16; },
    },
    { tool: "text", color: token("--forest-2"), text: LABEL, speed: 46, pause: 340, at: () => { const d = at.rect(dip); return [d.x - labelWidth() / 2, d.y + (small() ? 8 : 12)]; } },
    { tool: "blur", over: at.box(secret), padX: 6, padY: 3, duration: 650 },
  ];
}
const instant = (steps) => steps.map((step) => ({ ...step, duration: 0, pause: 0, speed: 0 }));

// The mode, where the stage says it ----------------------------------------------------------------
const chip = $("#mode-chip"), done = $("#done"), replay = $("#replay");
function showChip(mode) {
  chip.dataset.mode = mode;
  chip.lastElementChild.textContent = mode === "draw" ? "Drawing" : "Using apps";
}

// The loop ------------------------------------------------------------------------------------------
// In Draw it rings a number and points at the chart. In Use apps it changes the week, moves a window and writes a
// reply, and the drawings stay. Back in Draw it adds a note. Then the toolbar shrinks and grows, the drawings are
// hidden and shown in one of the app's styles, and it starts again. It stops for good when the visitor takes over.
const STYLES = ["magic", "pop", "drop", "corners", "focus", "dissolve"];
const STOP = Symbol("stop");
const demo = { run: 0, playing: false, ids: new Set(), busy: Promise.resolve(), controller: null, picked: null, round: 0, mode: "use", moves: new Set(), arrived: !motion };
function showControls() {
  replay.hidden = !motion;
  replay.classList.toggle("is-off", !demo.playing);
  replay.lastElementChild.textContent = demo.playing ? "Pause" : "Replay";
  done.hidden = demo.playing || ink.state.mode !== "draw";
}
function forget() {
  for (const id of demo.ids) ink.remove(id);
  demo.ids.clear();
}
// The script changes the mode the way a person would, with the toolbar's own button. `shown` false changes it at once.
async function scriptMode(mode, shown = motion) {
  demo.mode = mode;
  // On a touch screen a finger on the stage still scrolls the page while the script is the one drawing.
  stage.classList.toggle("is-scripted", coarse && mode === "draw");
  if (ink.state.mode === mode) return;
  if (!shown || ink.collapsed) { quiet(() => ink.setMode(mode)); return; }
  scripted += 1;
  try {
    await ink.perform([{ mode }], { cursor: true });
    // The pointer left the stage's sheet to press the button. Put away, it is back on the sheet for what comes next.
    ink.hideGhost();
  } finally { scripted -= 1; }
}
function stopDemo(pausing) {
  stage.classList.add("is-ready");
  if (!demo.playing) return;
  demo.playing = false;
  demo.run += 1;
  demo.controller?.abort();
  ink.hideGhost();
  // Anything that was mid-move is put back: drawings and toolbar are where they belong, and seen.
  for (const g of demo.moves) g.cancel();
  demo.moves.clear();
  stage.classList.remove("is-scripted");
  if (onStage.near && !ink.hidden) { shrunkByPage = false; setBar(false); }
  quiet(() => { if (pausing) ink.setMode("use"); });
  lifted = true;
  showControls();
}
function takeOver() { stopDemo(false); }
async function playDemo() {
  const run = ++demo.run, live = () => run === demo.run;
  // Every wait is a place the loop can stop, and a place it holds while the stage is off screen.
  const step = async (ms = 0) => {
    if (ms) await wait(ms);
    if (!live()) throw STOP;
    if (!onStage.near || document.hidden) { await onStage.ready(); if (!live()) throw STOP; if (ink.state.mode !== demo.mode) await scriptMode(demo.mode, false); }
  };
  const draw = async (steps) => {
    demo.controller = new AbortController();
    const made = await ink.perform(steps, { cursor: motion, showTool: motion, signal: demo.controller.signal });
    made.forEach((mark) => demo.ids.add(mark.id));
    await step();
  };
  const move = async (g) => { demo.moves.add(g); await g.done(); await step(); return g; };
  const point = async (element, ax = .5, ay = .5) => { const p = at.point(element, ax, ay)(); await ink.moveGhost(p); await step(); return p; };
  // A click is the element's own click, so the sample app does what it does for a person.
  const click = async (element) => { await point(element); ink.clickGhost(); await step(150); element.click(); await step(280); };
  demo.playing = true;
  showControls();
  await demo.busy;
  let release;
  demo.busy = new Promise((free) => { release = free; });
  try {
    while (live()) {
      await step();
      forget();
      apps.reset();
      demo.picked = null;
      const steps = heroSteps(), style = STYLES[demo.round % STYLES.length];
      if (!motion) {
        // Without motion the finished drawing is simply there, once.
        await scriptMode("use", false);
        stage.classList.add("is-ready");
        (await ink.perform(instant(steps), { cursor: false })).forEach((mark) => demo.ids.add(mark.id));
        demo.playing = false; demo.run += 1;
        break;
      }
      await scriptMode("draw", demo.arrived);
      await step(demo.arrived ? 380 : 0);
      if (!demo.arrived) {
        // The page's first show is the app's Magic: the toolbar poofs in and the drawings fly out of it.
        demo.arrived = true;
        (await ink.perform(instant(steps.slice(0, 2)), { cursor: false })).forEach((mark) => demo.ids.add(mark.id));
        const g = stageMove("magic", true);
        stage.classList.add("is-ready");
        (await move(g)).cancel();
        lifted = true;
        schedule();
        await step(900);
      } else await draw(steps.slice(0, 2));
      ink.hideGhost();
      await step(500);

      await scriptMode("use");
      await step(650);
      await click(apps.previous);
      await step(1250);
      await click(apps.next);
      await step(500);
      if (apps.chatShown()) {
        const from = await point(apps.chatBar, .3, .5);
        ink.clickGhost();
        apps.raise(apps.chat);
        await tween(640, (t) => { if (!live()) return; const [x, y] = apps.moveTo(apps.chat, -44 * ease.standard(t), 22 * ease.standard(t)); ink.placeGhost([from[0] + x, from[1] + y]); });
        await step(320);
        await point(apps.input, .3, .5);
        ink.clickGhost();
        await step(200);
        for (const letter of "Checking now") { apps.input.value += letter; await step(52); }
        await step(240);
        await click(apps.sendButton);
      }
      ink.hideGhost();
      await step(600);

      await scriptMode("draw");
      await step(380);
      await draw(steps.slice(2));
      ink.hideGhost();
      // A script puts the toolbar back on the tool it started with. If the visitor chose one meanwhile, theirs wins.
      if (demo.picked && ink.state.tool !== demo.picked) ink.setTool(demo.picked, true);
      await step(1500);

      // A calm moment: the toolbar shrinks to its one button, and grows back.
      await setBar(true);
      await step(1250);
      await setBar(false);
      await step(800);

      // Hide the drawings and the toolbar, look at the screen without them, and show them again.
      const hidden = await move(stageMove(style, false));
      await step(850);
      const shown = stageMove(style, true);
      demo.moves.delete(hidden);
      hidden.cancel();
      (await move(shown)).cancel();
      demo.moves.delete(shown);
      demo.round += 1;
      await step(1700);

      // Erased, as the app erases: a quick fade, a touch smaller.
      const gone = fx.group();
      for (const id of demo.ids) { const el = ink.layer.querySelector(`[data-id="${id}"]`); if (el) gone.add(fx.play(el, [{ opacity: 1 }, { opacity: 0 }], { duration: DURATIONS.eraseFade * 1000 * 2, easing: "linear" })); }
      await move(gone);
      forget();
      gone.cancel();
      demo.moves.delete(gone);
      await step(600);
    }
  } catch (error) { if (error !== STOP) throw error; } finally { release(); }
}

ink.on("tool", (tool) => { demo.picked = tool; });
ink.on("mark", takeOver);
ink.on("shortcut", (mode, info) => { if (!info?.scripted) takeOver(); });
ink.on("mode", (mode) => {
  showChip(mode);
  apps.setReachable(mode !== "draw");
  if (!scripted) {
    takeOver();
    // Drawing chosen from further down the page (the app's keys) leaves the stage alone until it is on screen.
    if (mode === "draw" ? onStage.near : !onStage.near) engage(mode === "draw");
  }
  showControls();
});
ink.toolbar.addEventListener("pointerdown", () => { takeOver(); engage(true); }, true);
ink.toolbar.addEventListener("focusin", () => engage(true));
// A tap on the stage while the script is drawing on a touch screen: the visitor wants it. It is theirs, in Draw.
screen.addEventListener("click", () => { if (stage.classList.contains("is-scripted")) { takeOver(); engage(true); } });
replay.addEventListener("click", () => {
  if (demo.playing) { stopDemo(true); return; }
  quiet(() => ink.setMode("use"));
  playDemo();
});
done.addEventListener("click", () => ink.setMode("use"));

// "Try it": from anywhere on the page, the stage comes into view and grows (the shared piece does both), the loop
// stops, and the Pen is ready to draw.
function tryIt(event) {
  event?.preventDefault();
  takeOver();
  const start = () => {
    shrunkByPage = false;
    setBar(false);
    ink.setTool("pen");
    engage(true);
    ink.toolbar.querySelector('[data-tool="pen"]')?.focus({ preventScroll: true });
  };
  schedule();
  // The stage has to be in front before drawing starts, or the page would take the mode back.
  let tries = 0;
  const whenThere = () => { if ((onStage.near && grow.state !== "small") || ++tries > 180) start(); else requestAnimationFrame(whenThere); };
  requestAnimationFrame(whenThere);
}
for (const element of $$("[data-try]")) element.addEventListener("click", tryIt);

// Theme -------------------------------------------------------------------------------------------
// The script in the page's head has already set the theme. The switch changes it, remembers the choice and tells
// everything that keeps colors of its own: the toolbar and the browser's own chrome.
const THEME_KEY = "annotator.concept-h4.theme";
const themeSwitch = $("#theme-switch"), themeMeta = $('meta[name="theme-color"]');
let theme = root.dataset.theme === "light" ? "light" : "dark";
// The shared engine's live scenes in "What you can do". Each keeps colors of its own, like the toolbar.
const liveScenes = [];
function showTheme(next) {
  theme = next;
  root.dataset.theme = next;
  themeSwitch.setAttribute("aria-checked", String(next === "light"));
  themeMeta.content = token("--page");
  ink.setAppearance(next);
  for (const scene of liveScenes) scene.setAppearance(next);
}
// The colors change in one step, with nothing easing, so a view transition's new picture is the new theme whole.
function swapColors(next) {
  root.classList.add("theme-switching");
  showTheme(next);
  void root.offsetWidth;
  root.classList.remove("theme-switching");
}
// The old theme's colors, read from the page before it changes: a coarse grid of what is behind and what is written
// at each place. The particles of the Dissolve take their colors from it.
function samplePage() {
  const columns = 14, rows = 10, palette = [], index = new Map(), cells = [], seen = new Map();
  const slot = (color) => { if (!index.has(color)) { index.set(color, palette.length); palette.push(color); } return index.get(color); };
  const behind = (element) => {
    for (let node = element; node; node = node.parentElement) {
      if (!seen.has(node)) seen.set(node, getComputedStyle(node).backgroundColor);
      const color = seen.get(node);
      if (color && !/rgba?\(0, 0, 0, 0\)|transparent/.test(color)) return color;
    }
    return token("--page");
  };
  for (let row = 0; row < rows; row++) for (let column = 0; column < columns; column++) {
    const element = document.elementFromPoint(((column + .5) / columns) * innerWidth, ((row + .5) / rows) * innerHeight) || document.body;
    cells.push([slot(behind(element)), slot(getComputedStyle(element).color)]);
  }
  const accents = [slot(token("--forest-2")), slot(token("--forest-3"))];
  return {
    palette,
    pick(u, v, id) {
      const cell = cells[Math.min(rows - 1, Math.floor(v * rows)) * columns + Math.min(columns - 1, Math.floor(u * columns))], r = dissolve.random(id + 4001);
      return r < .8 ? cell[0] : r < .95 ? cell[1] : accents[id % 2];
    },
  };
}
let particles = null, fadeTimer = 0;
function startParticles(colors) {
  particles?.cancel();
  const g = particles = fx.group(), layer = g.node(document.createElement("div"));
  layer.className = "dissolve";
  document.body.append(layer);
  return () => {
    // The sweep starts at the switch's corner and crosses to the far one, like the mask over the old picture.
    fx.burst(g, layer, { x: 0, y: 0, width: innerWidth, height: innerHeight }, { ...colors, fixed: true, duration: .68, sweep: .5, spread: 64, limit: Math.min(6500, Math.round((innerWidth * innerHeight) / 190)), along: (u, v) => .5 + .5 * (v - u) });
    g.done().then(() => { if (particles === g) { g.cancel(); particles = null; } });
  };
}
// The switch itself does the toolbar's Shrink: to one button for light, back to the bar for dark.
let switchMove = null;
function morphSwitch(next) {
  switchMove?.cancel();
  const g = switchMove = fx.group(), bar = $(".ts-bar", themeSwitch), one = $(".ts-one", themeSwitch), light = next === "light";
  fx.morph(g, { outgoing: light ? bar : one, incoming: light ? one : bar, expanding: !light, shape: [[themeSwitch, { width: light ? "70px" : "36px" }, { width: light ? "36px" : "70px" }]] });
  g.done().then(() => { if (switchMove === g) { g.cancel(); switchMove = null; } });
}
function setTheme(next) {
  if (next === theme) return;
  try { localStorage.setItem(THEME_KEY, next); } catch { /* storage is off */ }
  if (!motion) { swapColors(next); return; }
  morphSwitch(next);
  const burst = startParticles(samplePage());
  if (document.startViewTransition && !new URLSearchParams(location.search).has("fade")) {
    const transition = document.startViewTransition(() => swapColors(next));
    changing = transition;
    transition.ready.then(burst, () => {});
    transition.finished.finally(() => { if (changing === transition) changing = null; });
    return;
  }
  root.classList.add("theme-fading");
  showTheme(next);
  burst();
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
  particles?.cancel();
  particles = null;
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
  if (under && (target.contains(under) || under.contains(target))) under.closest("a, button, input")?.click();
}, true);
showTheme(theme);

// Section headers -----------------------------------------------------------------------------------
// Each starts as the toolbar's one button, with its name on it, and grows into the bar with the Shrink's timing
// the first time it is scrolled to. Scrolled back below the screen, it is the button again.
if (motion && window.IntersectionObserver) {
  const heads = $$("[data-morph]").map((head) => ({ head, pill: $(".sec-pill", head), full: $(".sec-full", head), chrome: $(".sec-chrome", head), move: null }));
  const cut = ({ head, pill }) => [Math.max(0, head.offsetWidth - pill.offsetWidth), Math.max(0, head.offsetHeight - pill.offsetHeight)];
  const measure = (item) => { const [right, bottom] = cut(item); item.head.style.setProperty("--cr", `${right}px`); item.head.style.setProperty("--cb", `${bottom}px`); };
  function open(item) {
    const { head, pill, full, chrome } = item, [right, bottom] = cut(item), radius = parseFloat(getComputedStyle(head).getPropertyValue("--radius")) || 14;
    if (!head.classList.contains("is-shut")) return;
    const g = item.move = fx.group();
    fx.morph(g, {
      outgoing: pill, incoming: full, expanding: true,
      shape: [
        [chrome, { clipPath: `inset(0px ${right}px ${bottom}px 0px round ${radius}px)` }, { clipPath: `inset(0px 0px 0px 0px round ${radius}px)` }],
        [chrome.firstElementChild, { clipPath: `inset(1px ${right + 1}px ${bottom + 1}px 1px round ${radius - 1}px)` }, { clipPath: `inset(1px 1px 1px 1px round ${radius - 1}px)` }],
      ],
    });
    head.classList.remove("is-shut");
    g.done().then(() => { if (item.move === g) { g.cancel(); item.move = null; } });
  }
  const watch = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const item = heads.find((each) => each.head === entry.target);
      if (entry.isIntersecting) open(item);
      else if (entry.boundingClientRect.top > innerHeight * .5) { item.move?.cancel(); item.move = null; measure(item); item.head.classList.add("is-shut"); }
    }
  }, { rootMargin: "0px 0px -14% 0px" });
  const sizes = window.ResizeObserver ? new ResizeObserver((entries) => { for (const entry of entries) { const item = heads.find((each) => each.head === entry.target); if (item.head.classList.contains("is-shut")) measure(item); } }) : null;
  for (const item of heads) { measure(item); item.head.classList.add("is-shut"); watch.observe(item.head); sizes?.observe(item.head); }
  document.fonts?.ready.then(() => heads.forEach((item) => { if (item.head.classList.contains("is-shut")) measure(item); }));
}

// What you can do ---------------------------------------------------------------------------------
const what = $("#what"), items = $$(".item", what), stepsBar = $("#steps"), scenesToggle = $("#scenes-toggle");
// A small sample screen that a pointer draws on, again and again while its example is the one shown. `script`
// draws one round; it gets the scene, its anchors, and `go`, which waits and throws when the scene has to stop.
function createScene(surface, script) {
  const scene = createInk({ surface, interactive: false, size: 3, gradient: "forest" }), a = anchorsFor(scene, surface);
  let active = false, running = false, turnId = 0, controller = null;
  const clear = () => { for (const mark of [...scene.marks()]) scene.remove(mark.id); scene.setSpotlight(false); scene.hideGhost(); };
  async function run() {
    if (running) return;
    running = true;
    while (active) {
      const mine = ++turnId, go = async (ms = 0) => { if (ms) await wait(ms); if (!active || mine !== turnId) throw STOP; };
      controller = new AbortController();
      const perform = async (steps) => { const made = await scene.perform(steps, { cursor: true, signal: controller.signal }); await go(); return made; };
      try {
        clear();
        await script({ scene, a, go, perform, live: true });
        scene.hideGhost();
        await go(3400);
        const gone = fx.group();
        for (const mark of scene.marks()) { const el = markEl(scene, mark); if (el) gone.add(fx.play(el, [{ opacity: 1 }, { opacity: 0 }], { duration: 260, easing: "linear" })); }
        await gone.done();
        clear();
        gone.cancel();
        await go(500);
      } catch (error) { if (error !== STOP) throw error; }
    }
    running = false;
  }
  // Without motion there is nothing to play: the finished drawing is simply there, and drawn again when the page
  // changes size.
  if (!motion) {
    const still = () => { clear(); script({ scene, a, go: async () => {}, perform: (steps) => scene.perform(steps, { cursor: false, anchored: false }), live: false }); };
    let timer = 0;
    addEventListener("resize", () => { clearTimeout(timer); timer = setTimeout(still, 200); });
    document.fonts?.ready.then(still) ?? still();
  }
  return {
    set(on) { if (!motion || active === on) return; active = on; if (on) run(); else { turnId += 1; controller?.abort(); scene.hideGhost(); } },
    reset() { if (!active) clear(); },
  };
}
// A stroke of the pen, drawn along its length with the pointer on its tip.
async function penStroke(scene, points, { duration = 700, size = 3, live = true, rough } = {}) {
  const mark = scene.add({ type: "pen", points, size, ...(rough === undefined ? {} : { rough }) });
  if (!live) return mark;
  const path = scene.layer.querySelector(`[data-id="${mark.id}"] .ink-stroke`);
  if (!path) return mark;
  const length = path.getTotalLength();
  path.style.strokeDasharray = `${length} ${length}`;
  path.style.strokeDashoffset = length;
  await scene.moveGhost(points[0]);
  await tween(duration, (t) => {
    const k = ease.standard(t), tip = path.getPointAtLength(length * k);
    path.style.strokeDashoffset = length * (1 - k);
    scene.placeGhost([tip.x, tip.y]);
  });
  path.style.strokeDasharray = path.style.strokeDashoffset = "";
  return mark;
}
// A box the way a hand draws it fast: round corners, a wobble, and a little past where it began.
function roughLoop({ x, y, w, h }) {
  const points = [], corner = Math.min(w, h) * .22, wobble = (i) => Math.sin(i * 2.7) * 2.6 + Math.cos(i * 1.3) * 1.8;
  const outline = [[x + corner, y], [x + w - corner, y], [x + w, y + corner], [x + w, y + h - corner], [x + w - corner, y + h], [x + corner, y + h], [x, y + h - corner], [x, y + corner], [x + corner, y - 2], [x + corner * 2.2, y + 1]];
  outline.forEach((p, i) => {
    const next = outline[i + 1];
    points.push([p[0] + wobble(i), p[1] + wobble(i + 5)]);
    if (next) points.push([(p[0] + next[0]) / 2 + wobble(i + 9), (p[1] + next[1]) / 2 + wobble(i + 2)]);
  });
  return points;
}
// "Draw on anything", on a small report: a ring around a number, a rough box that Smart Draw tidies, an arrow from
// one to the other, and a note.
async function drawScene({ scene, a, go, perform, live }) {
  const surface = $("#scene-draw"), ring = $("[data-ring]", surface), plot = a.rect($(".mini-plot", surface)), low = a.rect($("[data-dip]", surface));
  const frame = { x: low.x - plot.w * .13, y: low.y - plot.h * .4, w: plot.w * .26, h: plot.h * .6 };
  await perform([{ wait: 350 }, { tool: "ellipse", around: a.box(ring), padX: 12, padY: 8, duration: 800, pause: 260 }]);
  // The box as the pen leaves it. Then Smart Draw swaps it for the neat one (the app's snap: 0.22 s, the shape coming
  // in from just past half way).
  const sketch = await penStroke(scene, roughLoop(frame), { duration: 850, live });
  await go(240);
  const neat = scene.add({ type: "rect", ...frame, rough: 0, round: true, size: 3 });
  if (live) {
    const morph = fx.group(), time = { duration: DURATIONS.snapMorph * 1000 };
    morph.add(fx.play(markEl(scene, sketch), [{ opacity: 1 }, { opacity: 0 }], { ...time, easing: "cubic-bezier(.2, 0, 0, 1)" }));
    morph.add(fx.play(markEl(scene, neat), [{ opacity: 0 }, { opacity: 0, offset: .55 }, { opacity: 1 }], time));
    await morph.done();
    scene.remove(sketch.id);
    morph.cancel();
  } else scene.remove(sketch.id);
  await go(320);
  await perform([
    { tool: "arrow", from: a.point(ring, .15, 1, 0, 12), to: () => [frame.x + frame.w * .72, frame.y - 9], bend: -.2, duration: 700, pause: 260 },
    { tool: "text", text: "Thursday?", speed: 55, at: () => [frame.x + frame.w + 10, frame.y + frame.h * .5 - 10] },
  ]);
}
// The other three pictures are the shared engine's live scenes: the app's Share region, Break timer and toolbar.
// Each plays while its example is the one shown, again and again with a short pause, and starts over when it comes back.
const SHARED = { share: shareRegionScene, break: breakTimerScene, toolbar: toolbarScene };
function sharedScene(frame) {
  const scene = SHARED[frame.dataset.scene](frame, { appearance: theme, gradient: "forest" });
  liveScenes.push(scene);
  let active = false, turnId = 0;
  async function run() {
    const mine = ++turnId;
    while (active && mine === turnId) {
      if (!(await scene.play()) || !active || mine !== turnId) return;
      await wait(2400);
    }
  }
  // Without motion there is nothing to play: with Reduce Motion a scene shows its finished picture.
  if (!motion) scene.play();
  return {
    set(on) { if (!motion || active === on) return; active = on; if (on) run(); else { turnId += 1; scene.stop(); } },
    reset() { if (!active) scene.reset(); },
  };
}
const scenes = new Map(items.map((item) => { const frame = $(".item-media", item); return [item, frame.dataset.scene ? sharedScene(frame) : createScene(frame, drawScene)]; }));

let scenesPaused = !motion;
if (motion) {
  // The sequence. The section is three steps taller than the screen; each step swaps the example. The old one leaves
  // with its show style's hide and the new one arrives with its show, at the app's speeds, however fast the scroll.
  const last = items.length - 1, wrap = $(".what-wrap", what);
  const host = wrap.appendChild(Object.assign(document.createElement("div"), { className: "what-fx" }));
  const light = host.appendChild(document.createElementNS("http://www.w3.org/2000/svg", "svg"));
  light.setAttribute("class", "fx-svg");
  host.setAttribute("aria-hidden", "true");
  stepsBar.hidden = false;
  scenesToggle.hidden = false;
  const buttons = items.map((item, i) => {
    const button = document.createElement("button");
    button.type = "button";
    button.setAttribute("aria-label", $("h3", item).textContent);
    button.addEventListener("click", () => goTo(i));
    stepsBar.append(button);
    $(".item-text", item).prepend(Object.assign(document.createElement("p"), { className: "item-meta", textContent: `${i + 1} / ${items.length}` }));
    return button;
  });
  const metrics = () => { const top = what.getBoundingClientRect().top + scrollY; return { top, step: (what.offsetHeight - innerHeight) / last }; };
  const goTo = (i) => { const { top, step } = metrics(); scrollTo({ top: Math.round(top + i * step) + 1, behavior: "smooth" }); };
  const place = (element) => { const o = host.getBoundingClientRect(), b = element.getBoundingClientRect(); return { x: b.left - o.left, y: b.top - o.top, width: b.width, height: b.height }; };
  // What an example's panel breaks into: its own tones, lifted so they show against the page behind it.
  const paper = () => [token("--surface-3"), token("--surface-3"), token("--faint"), token("--win-muted"), token("--text"), token("--forest-2")];

  function moveItem(i, appearing) {
    const item = items[i], media = $(".item-media", item), style = item.dataset.style, id = `item-${i}`, g = fx.group(), box = place(media);
    // With Magic the example flies out of its own step in the header, as a drawing flies out of the toolbar.
    if (style === "magic") fx.magic(g, media, { box, target: middle(place(buttons[i])), appearing, id, trail: light });
    else if (style === "corners") fx.corners(g, media, { box, area: { x: 0, y: 0, width: host.clientWidth, height: host.clientHeight }, appearing, id });
    else if (style === "dissolve") fx.dissolved(g, media, { host, box, appearing, palette: paper(), spread: 90 });
    else fx.posed(g, media, style, { appearing, id });
    // The words do not fly. They rise on the enter curve, a step apart, and leave with the app's quick fade.
    [...$(".item-text", item).children].forEach((line, n) => {
      if (appearing) g.add(fx.play(line, [{ opacity: 0, translate: "0 12px" }, { opacity: 1, translate: "0 0" }], { duration: DURATIONS.toolbarShow * 1000, delay: 90 + n * 65, easing: "cubic-bezier(.2, 0, 0, 1)" }));
      else g.add(fx.play(line, [{ opacity: 1 }, { opacity: 0 }], { duration: DURATIONS.fadeOut * 1000, easing: "cubic-bezier(.42, 0, 1, 1)" }));
    });
    return g;
  }
  let shown = -1, wanted = -1, busy = false, current = null;
  const playing = () => !scenesPaused && shown >= 0 && !busy;
  const refresh = () => items.forEach((item, i) => scenes.get(item).set(playing() && i === shown));
  async function sync() {
    if (busy) return;
    busy = true;
    while (shown !== wanted) {
      if (shown >= 0) {
        const leaving = shown;
        scenes.get(items[leaving]).set(false);
        current = moveItem(leaving, false);
        await current.done();
        items[leaving].classList.remove("is-live");
        current.cancel();
        scenes.get(items[leaving]).reset();
        shown = -1;
      }
      if (wanted >= 0) {
        const arriving = wanted;
        items[arriving].classList.add("is-live");
        current = moveItem(arriving, true);
        // Someone scrolling on does not wait for a show to finish.
        await current.done();
        current.cancel();
        shown = arriving;
      }
    }
    current = null;
    busy = false;
    refresh();
  }
  const want = (i) => {
    if (i === wanted) return;
    wanted = i;
    buttons.forEach((button, n) => { if (n === i) button.setAttribute("aria-current", "true"); else button.removeAttribute("aria-current"); });
    if (busy) current?.speed(2.4); else sync();
  };
  readers.push(({ y, h }) => {
    const { top, step } = metrics(), raw = (y - top) / step;
    const inside = y + h * .62 > top && y < top + what.offsetHeight - h * .5;
    // A step is taken a little before its place, and held a little after, so it does not flicker at the line.
    const nearest = wanted >= 0 && Math.abs(raw - wanted) < .58 ? wanted : clamp(Math.round(raw), 0, last);
    want(inside ? nearest : -1);
  });
  scenesToggle.addEventListener("click", () => {
    scenesPaused = !scenesPaused;
    scenesToggle.classList.toggle("is-off", scenesPaused);
    scenesToggle.setAttribute("aria-label", scenesPaused ? "Play the examples" : "Pause the examples");
    refresh();
  });
}

// Arriving -------------------------------------------------------------------------------------------
// Things arrive with the app's own shows, as calm as the toolbar's: a rise on the enter curve, and Drop for the two
// places to get the app. Groups arrive a step apart (--i).
if (motion && window.IntersectionObserver) {
  const arrive = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      const element = entry.target, style = element.dataset.enter || "rise", delay = Number(element.style.getPropertyValue("--i") || 0) * .065;
      arrive.unobserve(element);
      if (style === "rise") fx.rise(element, { delay });
      else fx.play(element, poseKeyframes(style, { appearing: true, annotation: false }), { delay: delay * 1000, fill: "backwards" });
      element.classList.add("is-in");
    }
  }, { rootMargin: "0px 0px -8% 0px", threshold: .12 });
  $$("[data-enter]").forEach((element) => arrive.observe(element));
  // The steam over the cup dissolves, the app's way, and comes back.
  const cupStage = $("#cup-stage"), steam = $(".steam", cupStage);
  let steaming = false, cupSeen = false;
  const breathe = async () => {
    if (steaming) return;
    steaming = true;
    while (cupSeen) {
      await wait(1700);
      if (!cupSeen) break;
      const scale = cupStage.clientWidth / 200, box = { x: 62 * scale, y: 24 * scale, width: 76 * scale, height: 50 * scale }, palette = [token("--muted"), token("--faint")];
      for (const appearing of [false, true]) {
        const g = fx.group();
        fx.dissolved(g, steam, { host: cupStage, box, appearing, palette, limit: 90, spread: 44 });
        await g.done();
        steam.style.opacity = appearing ? "" : "0";
        g.cancel();
        if (!appearing) await wait(500);
      }
    }
    steam.style.opacity = "";
    steaming = false;
  };
  new IntersectionObserver(([entry]) => { cupSeen = entry.isIntersecting; if (cupSeen) breathe(); }, { threshold: .5 }).observe(cupStage);
}

// A download button's arrow flies into place on a small Magic flight: once when the button shows up, and again
// under the pointer.
const ARROW = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2.500v9M4 8l4 4 4-4M3 14h10"/></svg>';
for (const button of $$("[data-arrow]")) {
  const arrow = Object.assign(document.createElement("span"), { className: "arrow", innerHTML: ARROW });
  button.prepend(arrow);
  if (!motion) continue;
  const glyph = arrow.firstElementChild, trail = arrow.appendChild(document.createElementNS("http://www.w3.org/2000/svg", "svg"));
  trail.setAttribute("class", "fx-svg");
  let flying = null;
  const fly = () => {
    if (flying) return;
    const g = flying = fx.group();
    fx.magic(g, glyph, { box: { x: 0, y: 0, width: 16, height: 16 }, target: [-30, -26], appearing: true, id: button.textContent, trail });
    g.done().then(() => { g.cancel(); flying = null; });
  };
  button.addEventListener("pointerenter", fly);
  button.addEventListener("focus", fly);
  if (window.IntersectionObserver) { const seen = new IntersectionObserver(([entry]) => { if (entry.isIntersecting) { seen.disconnect(); setTimeout(fly, 500); } }, { threshold: .9 }); seen.observe(button); }
}

// Install -------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const status = $("#status");
for (const button of $$("[data-copy]")) {
  const label = $("p", button.closest(".command")), words = label.textContent;
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
    if (!copied) { status.textContent = "It’s selected. Copy it with your keyboard."; return; }
    button.textContent = "Copied";
    label.textContent = status.textContent = "Copied. Paste it into Terminal and press Enter.";
    clearTimeout(timer);
    timer = setTimeout(() => { button.textContent = "Copy"; label.textContent = words; status.textContent = ""; }, 4000);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

// Start ---------------------------------------------------------------------------------------------
showChip(ink.state.mode);
schedule();
Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {}).then(async () => {
  if (motion) await wait(520);
  schedule();
  playDemo();
});
