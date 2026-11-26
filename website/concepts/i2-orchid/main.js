import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
// The app's own motion: its click pulse, its Magic flight and its Pop, as keyframes for the page's own elements.
import { clickPulse, magicFlight, travelFade, poseKeyframes, visibilityDuration } from "/concepts/shared/app-motion.js";

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
// Starts a CSS animation that hangs on a class again from its beginning.
const again = (el, name) => { el.classList.remove(name); void el.offsetWidth; el.classList.add(name); };
// Plays one of the app's keyframe lists on an element.
const playMotion = (el, list, more = {}) => el.animate(list, { duration: list.duration, easing: list.easing, fill: "backwards", ...more });
const CORAL = "#F04538", BLUE = "#2B73E0", TEAL = "#00969E", PURPLE = "#7D54B8";
const TAU = Math.PI * 2;

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
const standIn = (el, ink, surface) => ({
  getBoundingClientRect() {
    // The engine measures from the surface's corner: its layer's, less the room the layer has past the surface.
    const o = ink.layer.getBoundingClientRect(), room = ink.room, b = localBox(el, surface);
    return { left: o.left + (room?.left || 0) + b.x, top: o.top + (room?.top || 0) + b.y, width: b.w, height: b.h };
  },
});

// The frame loop -----------------------------------------------------------------------------------
// One loop for everything that moves from script. Each task is called with the time since the last frame and
// answers whether it still has somewhere to go; the loop sleeps when none has.
const tasks = [];
let queued = false, lastFrame = 0;
function frame(now) {
  queued = false;
  const dt = Math.min(64, lastFrame ? now - lastFrame : 16);
  lastFrame = now;
  let moving = false;
  for (const task of tasks) if (task(dt, now)) moving = true;
  root.classList.toggle("is-scrolled", scrollY > 8);
  if (moving) request(); else lastFrame = 0;
}
function request() { if (!queued) { queued = true; requestAnimationFrame(frame); } }
addEventListener("scroll", request, { passive: true });
addEventListener("resize", request);

// Scroll-driven values. Each channel has a goal read from the page and a shown value that follows it with a short
// lag, so a mouse wheel's steps become one smooth move. Goals are read first, then styles written.
const channels = [];
function channel(goal, apply) { const c = { goal, apply, value: null, shown: null, to: 0 }; channels.push(c); return c; }
tasks.push((dt) => {
  const k = 1 - Math.exp(-dt / 85);
  for (const c of channels) c.to = c.goal();
  let moving = false;
  for (const c of channels) {
    const gap = c.to - (c.value ?? c.to);
    c.value = c.value === null || Math.abs(gap) < 0.0008 ? c.to : c.value + gap * k;
    if (c.value !== c.to) moving = true;
    if (c.value !== c.shown) { c.shown = c.value; c.apply(c.value); }
  }
  return moving;
});

// A small damped spring: `at` follows `to`. `step` answers whether it is still moving.
function spring(stiffness, damping, at = 0, rest = 0.001) {
  return {
    at, to: at, speed: 0,
    step(dt) {
      if (this.at === this.to && !this.speed) return false;
      for (let left = dt / 1000; left > 0; left -= 0.008) {
        const h = Math.min(left, 0.008);
        this.speed += (stiffness * (this.to - this.at) - damping * this.speed) * h;
        this.at += this.speed * h;
      }
      if (Math.abs(this.to - this.at) < rest && Math.abs(this.speed) < rest * 8) { this.at = this.to; this.speed = 0; }
      return true;
    },
  };
}

// 1. The stage -------------------------------------------------------------------------------------
const desk = $("#desk"), dock = $("#dock"), scene = $(".stage-scene"), apps = $("#apps");
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset): a gradient as the
// color (here the app's Orchid), curved arrows, the roughest line. It rests on the Arrow tool so its properties row
// shows that. The engine itself starts the row closed on a phone, which keeps the bar compact there.
// Hide and Show (the toolbar's button, Control-2) move in the app's Magic style.
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, gradient: "orchid", tool: "arrow", visibility: "magic", moreHref: "#install" });
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the desk's
// frame opens up to fill the window, and scrolling brings it back. The desk itself does not move: in index.html the
// panel, the cue's ring and the dock carry data-stage-frame, and the two tags on the rim data-stage-corner.
const stage = growingStage({ ink, surface: desk, tries: "[data-try]", motion: () => motion, isFlat: () => desk.classList.contains("is-flat") });
// Joining in ends the script, lays the desk flat (the frame grows only once it is) and retires "Your turn".
stage.on("join", () => { takeOver(); turnTaken = true; showTurn(); });

// Theme --------------------------------------------------------------------------------------------
// Dark unless the visitor chose light. The choice is kept; the page's head applies it before the first paint.
const themeSwitch = $(".theme-switch"), themeColor = $('meta[name="theme-color"]');
const PAGE_COLOR = { dark: "#111014", light: "#faf8fb" };
const themed = [ink];
function showTheme(theme) {
  root.dataset.theme = theme;
  themeSwitch.setAttribute("aria-checked", String(theme === "dark"));
  themeColor.content = PAGE_COLOR[theme];
  for (const engine of themed) engine.setAppearance(theme);
}
let themeTurn = 0;
function setTheme(theme) {
  try { localStorage.setItem("annotator.site.theme", theme); } catch { /* storage is off: the choice lasts for this visit */ }
  const turn = ++themeTurn;
  if (!motion) { showTheme(theme); return; }
  // The knob stretches as it sets off and squashes as it lands.
  $(".ts-knob", themeSwitch).animate({ scale: ["1 1", "1.5 .78", ".9 1.08", "1 1"], offset: [0, 0.34, 0.74, 1] }, { duration: 560, easing: "ease-out" });
  if (!document.startViewTransition) {
    // No view transitions here: the colors cross-fade for a moment.
    root.classList.add("theme-fade");
    showTheme(theme);
    setTimeout(() => { if (turn === themeTurn) root.classList.remove("theme-fade"); }, 360);
    return;
  }
  // The new theme grows out of the switch as a soft blob, far enough to pass the farthest corner.
  const r = themeSwitch.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
  const reach = Math.ceil(Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y)));
  root.style.setProperty("--vt-x", `${x}px`);
  root.style.setProperty("--vt-y", `${y}px`);
  root.style.setProperty("--vt-r", `${reach}px`);
  root.style.setProperty("--vt-o", `${Math.round(reach * 0.07)}px`);
  root.classList.add("theme-reveal");
  const transition = document.startViewTransition(() => showTheme(theme));
  const stopPassing = passThrough(transition);
  const done = () => { stopPassing(); if (turn === themeTurn) root.classList.remove("theme-reveal"); };
  transition.finished.then(done, done);
}
// While the reveal runs, the browser shows a picture of the page and sends every press to the page's root, so
// nothing under the pointer can be pressed. A press ends the reveal at once and is handed to what is really there,
// so a visitor can pick a tool or start drawing the moment after they switch.
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
  // The click that belongs to a press arrives after the reveal has ended, so the listeners stay a moment longer.
  return () => setTimeout(() => { removeEventListener("pointerdown", down, true); removeEventListener("click", click, true); }, 700);
}
themeSwitch.addEventListener("click", () => setTheme(root.dataset.theme === "dark" ? "light" : "dark"));
showTheme(root.dataset.theme);

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

// How far the desk is from flat: 1 at the top of the page, 0 once the stage has come up the screen. As soon as the
// visitor takes over, it goes flat and stays flat: a pointer on a tilted plane would not draw where it points.
let userFlat = !motion, tiltNow = 0, sceneRect = null;
channel(() => {
  sceneRect = scene.getBoundingClientRect();
  if (userFlat) return 0;
  const top = sceneRect.top + scrollY;
  return 1 - smooth(scrollY / clamp(top - innerHeight * 0.1, 200, innerHeight * 0.7));
}, (v) => {
  tiltNow = v;
  scene.style.setProperty("--tilt", v.toFixed(4));
});

// The sample apps ----------------------------------------------------------------------------------
// Each window on the desk is a small app with made-up data that never leaves the page. In Use apps they react to
// the visitor (and to the scripted pointer); in Draw the ink layer is over them and they are inert.
const numberEl = $("#t-number"), lineEl = $("#t-line"), deltaEl = $("#t-delta");
const WEEKS = {
  this: { total: 318, delta: "21% fewer than last week", days: [62, 58, 64, 55, 41, 22, 16] },
  last: { total: 402, delta: "8% more than the week before", days: [61, 57, 66, 63, 59, 52, 44] },
};
const weekButtons = $$("[data-week]"), bars = $$("#bars .day");
function showWeek(key) {
  const week = WEEKS[key];
  for (const button of weekButtons) button.setAttribute("aria-pressed", String(button.dataset.week === key));
  if (numberEl.textContent !== String(week.total)) { numberEl.textContent = week.total; again(numberEl, "is-new"); }
  deltaEl.textContent = week.delta;
  bars.forEach((bar, i) => {
    const count = week.days[i];
    bar.style.setProperty("--v", count);
    bar.classList.toggle("low", count < 45);
    $("b", bar).textContent = count;
    bar.setAttribute("aria-label", `${bar.dataset.day}: ${count} sign-ups`);
  });
}
for (const button of weekButtons) button.addEventListener("click", () => showWeek(button.dataset.week));

const slides = $$("[data-slide]"), slideCount = $("#slide-count");
let slideAt = 0;
function showSlide(index) {
  slideAt = (index + slides.length) % slides.length;
  slides.forEach((slide, i) => { slide.hidden = i !== slideAt; });
  slideCount.textContent = `Slide ${slideAt + 1} of ${slides.length}`;
}
for (const button of $$("[data-step]")) button.addEventListener("click", () => showSlide(slideAt + Number(button.dataset.step)));

const reply = $("#mail-reply"), sent = $("#mail-sent");
reply.addEventListener("submit", (e) => {
  e.preventDefault();
  const text = reply.elements.reply.value.trim();
  if (!text) return;
  const bubble = document.createElement("span");
  bubble.textContent = text;
  sent.replaceChildren(bubble);
  reply.elements.reply.value = "";
});

// A window moves by its title bar, inside the desk. Drawings do not move with it, as in the app.
const floatEls = $$(".float", desk), dragged = floatEls.map(() => [0, 0]);
let frontmost = 1;
floatEls.forEach((el, i) => {
  const bar = $("[data-drag]", el);
  bar.addEventListener("pointerdown", (e) => {
    if (e.button || !e.isTrusted) return;
    e.preventDefault();
    bar.setPointerCapture(e.pointerId);
    const start = [e.clientX, e.clientY], from = [...dragged[i]], box = localBox(el, desk), edge = 8;
    el.classList.add("is-dragging");
    el.style.zIndex = ++frontmost;
    const move = (ev) => {
      dragged[i] = [
        clamp(from[0] + ev.clientX - start[0], Math.min(0, edge - box.x), Math.max(0, desk.clientWidth - edge - box.x - box.w)),
        clamp(from[1] + ev.clientY - start[1], Math.min(0, edge - box.y), Math.max(0, desk.clientHeight - edge - box.y - box.h)),
      ];
      el.style.translate = `${dragged[i][0]}px ${dragged[i][1]}px`;
    };
    const end = () => {
      el.classList.remove("is-dragging");
      bar.removeEventListener("pointermove", move);
      bar.removeEventListener("pointerup", end);
      bar.removeEventListener("pointercancel", end);
    };
    bar.addEventListener("pointermove", move);
    bar.addEventListener("pointerup", end);
    bar.addEventListener("pointercancel", end);
  });
});
function resetApps() {
  showWeek("this");
  showSlide(0);
  sent.replaceChildren();
  reply.elements.reply.value = "";
  floatEls.forEach((el, i) => { dragged[i] = [0, 0]; el.style.translate = ""; el.style.zIndex = ""; });
}
showWeek("this");

// The scripted demo --------------------------------------------------------------------------------
const on = (el) => standIn(el, ink, desk);
const number = on(numberEl);
// Which window each scripted mark belongs to, and the point of it that the mark is held by. While the windows
// float, the marks are carried with them (see the float below). `hand` is what the pointer is busy with now.
const carried = new WeakMap(), scriptIds = new Set();
let hand = null;
const SLIDE = 0, CHART = 1;
const middle = (el) => () => { const b = localBox(el, desk); return [b.x + b.w / 2, b.y + b.h / 2]; };

// The script changes mode as the visitor would, with the engine's own switch. These changes are told apart from
// the visitor's, which end the demo.
let scripted = false, scriptDrawing = false, deskSeen = false;
const byScript = (change) => { scripted = true; try { return change(); } finally { scripted = false; } };
// The engine's changes that move (hide, show, shrink) answer with a promise where it plays them in the app's motion.
const settled = (change, least, signal) => Promise.all([Promise.resolve(byScript(change)), wait(least, signal)]);
function scriptMode(mode) {
  // Draw takes the keyboard, so the script only switches to it while the stage is on screen.
  if (mode === "draw" && !deskSeen) return;
  scriptDrawing = mode === "draw";
  byScript(() => ink.setMode(mode));
}
const chip = $("#mode-chip"), chipWord = $("[data-chip-word]", chip);
// Switches mode and presses the mode's keys on the stage's chip, one after another.
async function pressMode(mode, signal) {
  scriptMode(mode);
  const keys = $$(`[data-chip="${mode}"] kbd`, chip);
  for (const key of keys) { key.classList.add("is-down"); await wait(80, signal); }
  await wait(190, signal);
  keys.forEach((key) => key.classList.remove("is-down"));
}
// The pointer goes to a control of a sample app and presses it.
async function tap(el, win, signal) {
  hand = [{ win, at: middle(el) }];
  const [x, y] = middle(el)();
  await ink.moveGhost([x - 2, y + 3], 560);
  if (signal.aborted) return;
  // The ring round a press swells as the app's pointer highlight does when it clicks.
  el.classList.add("is-tapped");
  if (motion) playMotion(el, clickPulse(), { pseudoElement: "::after" });
  await wait(150, signal);
  if (!signal.aborted) el.click();
  await wait(340, signal);
  el.classList.remove("is-tapped");
}
// The story: in Draw, ring the number and point at it; in Use apps, look at last week and come back while the
// drawings stay where they are; in Draw again, write what it was. The ring and the arrow name no color: they take
// the one selected in the toolbar, at its strongest roughness, and the arrow bows well clear of a straight line.
function story(signal) {
  const small = desk.clientWidth < 600, px = small ? 12 : 17, py = small ? 8 : 11, size = small ? 3 : 3.5;
  // The arrow runs from the line on the slide to the number: sideways when the windows sit side by side, down when
  // they are stacked.
  const ends = () => {
    const l = localBox(lineEl, desk), n = localBox(numberEl, desk);
    return n.x > l.x + l.w
      ? [[l.x + l.w + 10, l.y + l.h * 0.3], [n.x - px - 9, n.y + n.h * 0.42]]
      : [[l.x + l.w - 4, l.y + l.h + 9], [n.x + n.w * 0.8, n.y - py - 14]];
  };
  const onNumber = [{ win: CHART, at: middle(numberEl) }];
  const draw = (step, holds) => { carried.set(step, holds); return [{ run: () => { hand = holds; } }, step]; };
  const [lastWeek, thisWeek] = [weekButtons[1], weekButtons[0]];
  if (!motion) return [
    ...draw({ tool: "ellipse", around: number, padX: px, padY: py, size, rough: 6 }, onNumber),
    ...draw({ tool: "arrow", from: () => ends()[0], to: () => ends()[1], bend: 0.42, size, rough: 6 }, [{ win: SLIDE, at: () => ends()[0] }, { win: CHART, at: () => ends()[1] }]),
    ...draw({ tool: "text", at: { el: number, at: "right", dx: px + 12, dy: small ? -15 : -18 }, text: "was 402", color: BLUE }, onNumber),
  ];
  return [
    { run: () => pressMode("draw", signal) },
    { wait: 260 },
    ...draw({ tool: "ellipse", around: number, padX: px, padY: py, size, rough: 6, duration: 820, pause: 220 }, onNumber),
    ...draw({ tool: "arrow", from: () => ends()[0], to: () => ends()[1], bend: 0.42, size, rough: 6, duration: 760, pause: 380 }, [{ win: SLIDE, at: () => ends()[0] }, { win: CHART, at: () => ends()[1] }]),
    { run: () => pressMode("use", signal) },
    { wait: 300 },
    { run: () => tap(lastWeek, CHART, signal) },
    { wait: 1100 },
    { run: () => tap(thisWeek, CHART, signal) },
    { wait: 420 },
    { run: () => pressMode("draw", signal) },
    { wait: 260 },
    ...draw({ tool: "text", at: { el: number, at: "right", dx: px + 12, dy: small ? -15 : -18 }, text: "was 402", color: BLUE, speed: 60, pause: 420 }, onNumber),
    { run: () => pressMode("use", signal) },
  ];
}

// The tool the visitor chose. A script that shows its tools in the toolbar puts back the tool it started with when
// it ends; if the visitor picked one meanwhile, theirs is put back instead.
let chosen = null;
ink.on("tool", (tool) => { chosen = tool; });
const keepChoice = () => { if (chosen && ink.state.tool !== chosen) ink.setTool(chosen, ink.state.mode !== "draw"); };

const replay = $("#stage-replay"), done = $("#stage-done"), turn = $("#your-turn");
let run = null, rounds = 0, turnTaken = false, seenWaiters = [];
// What the script has done to the toolbar and the drawings that it has not undone yet: "shrunk", "hidden" or nothing.
let scriptLeft = "";
const setRunning = (running) => {
  replay.dataset.state = running ? "playing" : "";
  $("b", replay).textContent = running ? "Pause" : "Replay";
};
// "Your turn" shows once the demo has played through, and whenever it is not playing, until the visitor joins in.
function showTurn() {
  const hide = turnTaken || (Boolean(run) && rounds === 0);
  if (turn.hidden === hide) return;
  turn.hidden = hide;
  if (!hide && motion) playMotion(turn, poseKeyframes("pop", { appearing: true, annotation: false }));
}
const whenSeen = (signal) => new Promise((go) => {
  if (deskSeen || signal?.aborted) { go(); return; }
  seenWaiters.push(go);
  signal?.addEventListener("abort", go, { once: true });
});
function clearScript() {
  for (const id of scriptIds) ink.remove(id);
  scriptIds.clear();
}
async function wipe(signal) {
  if (!scriptIds.size) return;
  desk.classList.add("is-wiping");
  await wait(340, signal);
  // A visitor who joins in while the drawings fade keeps them.
  if (!signal?.aborted) clearScript();
  desk.classList.remove("is-wiping");
}
// The app's Shrink: the toolbar becomes its one button, and grows back.
async function shrinkAndGrow(signal) {
  scriptLeft = "shrunk";
  await settled(() => ink.setCollapsed(true), 450, signal);
  await wait(900, signal);
  if (signal.aborted) return;
  await settled(() => ink.setCollapsed(false), 450, signal);
  scriptLeft = "";
}
// The app's Hide and Show (Control-2 in the app): the drawings and the toolbar go, then both come back, in one of
// the styles the app offers. The first round is Magic; later rounds take the next style in turn.
const SHOWS = ["magic", "pop", "drop", "focus"];
async function hideAndShow(signal) {
  const style = SHOWS[(rounds - 1) % SHOWS.length];
  scriptLeft = "hidden";
  await settled(() => ink.setHidden(true, { style, pill: false }), 800, signal);
  await wait(420, signal);
  if (signal.aborted) return;
  await settled(() => ink.setHidden(false, { style }), 800, signal);
  scriptLeft = "";
}
async function loop(signal) {
  while (!signal.aborted) {
    await whenSeen(signal);
    await wait(700, signal);
    if (signal.aborted) break;
    resetApps();
    const made = await ink.perform(story(signal), { cursor: true, showTool: true, signal });
    for (const mark of made) scriptIds.add(mark.id);
    ink.hideGhost();
    hand = null;
    keepChoice();
    if (signal.aborted) break;
    ink.reflow();
    rounds++;
    showTurn();
    await wait(1500, signal);
    if (signal.aborted) break;
    await shrinkAndGrow(signal);
    await wait(900, signal);
    if (signal.aborted) break;
    await hideAndShow(signal);
    await wait(1100, signal);
    if (signal.aborted) break;
    await wipe(signal);
    await wait(500, signal);
  }
}
function start() {
  if (run) return;
  const abort = new AbortController();
  const finished = loop(abort.signal).finally(() => {
    ink.hideGhost();
    hand = null;
    keepChoice();
    // What the script hid or shrank comes back. What the visitor hid (Control-2, the Hide button) stays hidden.
    if (scriptLeft === "hidden" && ink.state.hidden) byScript(() => ink.setHidden(false));
    else if (scriptLeft === "shrunk" && ink.collapsed && !ink.state.hidden && !toolbarPressed) byScript(() => ink.setCollapsed(false));
    scriptLeft = "";
    if (run?.abort === abort) { run = null; setRunning(false); showTurn(); }
  });
  run = { abort, finished };
  setRunning(true);
  showTurn();
}
// Stops the demo. A second call while it is still winding down waits for the same end.
let winding = Promise.resolve();
async function stop() {
  if (run) {
    const { abort, finished } = run;
    run = null;
    winding = finished;
    abort.abort();
    setRunning(false);
    showTurn();
  }
  await winding;
}
async function restart() {
  await stop();
  byScript(() => ink.setMode("use"));
  showMode("use", false);
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
  chosen = null;
  userFlat = !motion;
  request();
  await wipe();
  if (motion) start();
  else { resetApps(); for (const mark of await ink.perform(story(), { cursor: false })) scriptIds.add(mark.id); ink.reflow(); }
}
replay.addEventListener("click", async () => {
  if (!run) { restart(); return; }
  await stop();
  if (scriptDrawing) scriptMode("use");
});
done.addEventListener("click", () => ink.setMode("use"));

// What the stage says about its mode: the chip on its rim, the apps that react or do not, the way out of drawing,
// and the two keys further down the page.
function showMode(mode, own) {
  if (chip.dataset.mode !== mode) again(chip, "is-new");
  chip.dataset.mode = mode;
  chipWord.textContent = mode === "draw" ? "Drawing" : "Using apps";
  apps.inert = mode === "draw";
  done.hidden = mode !== "draw" || !own;
  setKeysMode(mode);
}
// The demo ends for good when the visitor takes over: a press on the toolbar or on the stage, a key in a sample
// app, or a change of mode by any route. Nothing starts it again but Replay.
function takeOver() {
  stop();
  scriptDrawing = false;
  if (!userFlat) { userFlat = true; request(); }
  done.hidden = ink.state.mode !== "draw";
}
// A press on the toolbar's one button opens it by itself; the demo's ending does not open it first.
let toolbarPressed = false;
ink.toolbar.addEventListener("pointerdown", () => { toolbarPressed = true; takeOver(); setTimeout(() => { toolbarPressed = false; }, 400); }, true);
ink.on("mode", (mode) => {
  const own = !scripted;
  if (own) { if (run || mode === "draw") takeOver(); scriptDrawing = false; }
  showMode(mode, own);
});
ink.on("mark", () => { turnTaken = true; showTurn(); });
// A press on the stage while the demo plays ends the demo. If the script was drawing, the press is not a stroke:
// the desk may still be tilted, and the visitor has not chosen to draw.
desk.addEventListener("pointerdown", (e) => {
  if (!e.isTrusted || !run || ink.toolbar.contains(e.target) || turn.contains(e.target)) return;
  const drawing = scriptDrawing;
  takeOver();
  if (drawing) { scriptMode("use"); e.preventDefault(); e.stopPropagation(); }
}, true);
// The first time the visitor uses a sample app, the scripted drawings let go of what they were drawn on: from then
// on they stay where they are on the screen, whatever changes or moves under them.
function appUsed(e) {
  if (!e.isTrusted || (e.type === "keydown" && e.key === "Tab")) return;
  takeOver();
  for (const mark of ink.marks()) if (mark.anchor) mark.anchor = null;
}
apps.addEventListener("pointerdown", appUsed, true);
apps.addEventListener("keydown", appUsed, true);
addEventListener("resize", () => setTimeout(() => ink.reflow(), 260));

new IntersectionObserver(([entry]) => {
  deskSeen = entry.isIntersecting && entry.intersectionRatio >= 0.3;
  if (deskSeen) seenWaiters.splice(0).forEach((go) => go());
  // Drawing ends when the stage is out of view, so keys and touches go back to the page.
  if (!entry.isIntersecting && !trying) { if (run) scriptMode("use"); else ink.setMode("use"); }
}, { threshold: [0, 0.3] }).observe(scene);

const fontsReady = Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {});
fontsReady.then(async () => {
  // The windows have surfaced by now, so the pointer draws on things that are where they will stay.
  if (motion) { await wait(1900); if (!userFlat) start(); }
  else { for (const mark of await ink.perform(story(), { cursor: false })) scriptIds.add(mark.id); ink.reflow(); showTurn(); }
});

// Try it here. From anywhere on the page: the demo stops and its drawings go, Draw is on with the Pen, and a ring
// of light goes round the stage once. The growing stage hears the same click: it brings the stage into view and
// opens its frame.
let trying = false;
async function tryIt(e) {
  e?.preventDefault();
  trying = true;
  await stop();
  takeOver();
  clearScript();
  ink.setTool("pen");
  showMode("draw", true);
  if (ink.state.hidden) await ink.setHidden(false, { animate: false });
  await Promise.race([whenSeen(), wait(1600)]);
  trying = false;
  ink.toolbar.querySelector('[data-tool="pen"]')?.focus({ preventScroll: true });
  again(desk, "is-cued");
  setTimeout(() => desk.classList.remove("is-cued"), 1600);
}
for (const el of $$("[data-try]")) el.addEventListener("click", tryIt);

// The float. While the desk is tilted, each sample window hangs over it at a height of its own, drifts a few pixels
// on its own slow cycle and leans toward the pointer on a spring. All of it is scaled by how tilted the desk is, so
// scrolling the stage up the screen brings the desk flat and the windows down onto their grid.
// Everything is written as transforms from here, and the same numbers are kept as matrices: that is how a mark
// drawn on the flat ink layer can be moved to where its window is seen, so the ring stays round its number.
if (motion) {
  // These four must say what style.css says for .stage-scene (perspective, its origin) and .stage-plane (its origin).
  const PERSPECTIVE = 1700, EYE = [0.5, 0.3], PIVOT = [0.5, 0.62];
  const POSE = { rx: 15, ry: -8, rz: 1.8, shrink: 0.075 };
  // For each window: where it rests while floating (x, y, height over the desk), how far it sways on each of those
  // and on its two leans (degrees), how many seconds each sway takes, and how stiff its lean toward the pointer is.
  const SPEC = [
    { rest: [-12, 2, 44], sway: [5, 7, 8, 1.1, 1.4], period: [11, 8.6, 13, 9.4, 12.2], phase: [0.4, 2.1, 4, 1.2, 3.3], stiffness: 44 },
    { rest: [5, -7, 88], sway: [6, 8, 10, 1.4, 1.2], period: [9.2, 12.4, 10.2, 13, 8.2], phase: [3.1, 0.2, 1.4, 5.2, 2.4], stiffness: 30 },
    { rest: [15, 9, 58], sway: [5, 6, 9, 1.2, 1.6], period: [12.6, 9.6, 8.4, 11, 14], phase: [5, 3.6, 0.7, 2.6, 0.1], stiffness: 56 },
  ];
  const floats = floatEls.map((el, i) => {
    const spec = SPEC[i], damping = 2 * 0.5 * Math.sqrt(spec.stiffness);
    return { ...spec, win: $(".win", el), shadow: $(".win-shadow", el), center: [0, 0], screen: [0, 0], seen: null,
      lean: [0, 1, 2, 3].map(() => spring(spec.stiffness, damping, 0, 0.004)) };
  });
  const ghost = $(".ink-ghost", ink.layer), ghostAt = [0, 0], holdsOf = new Map();
  let size = [1, 1], depth = 1, stacked = false, flat = false, pointer = null, eye = null;
  function measure() {
    size = [desk.offsetWidth, desk.offsetHeight];
    depth = innerWidth < 600 ? 0.55 : innerWidth < 1100 ? 0.8 : 1;
    stacked = size[0] < 600;
    for (const f of floats) { const b = localBox(f.win, desk); f.center = [b.x + b.w / 2, b.y + b.h / 2]; }
    // What the scene's perspective does to a point of the desk's box.
    const lens = new DOMMatrix();
    lens.m34 = -1 / PERSPECTIVE;
    eye = new DOMMatrix().translateSelf(size[0] * EYE[0], size[1] * EYE[1]).multiplySelf(lens).translateSelf(-size[0] * EYE[0], -size[1] * EYE[1]);
    request();
  }
  new ResizeObserver(measure).observe(desk);
  addEventListener("pointermove", (e) => { pointer = e.pointerType === "touch" ? null : [e.clientX, e.clientY]; }, { passive: true });
  root.addEventListener("pointerleave", () => { pointer = null; });

  // Where a point of a plane is seen, and the point of another plane that is seen at the same place.
  const see = (m, q) => { const p = m.transformPoint(new DOMPoint(q[0], q[1])); return [p.x / p.w, p.y / p.w]; };
  const under = (m, s) => {
    const a = m.m11 - s[0] * m.m14, b = m.m21 - s[0] * m.m24, c = s[0] * m.m44 - m.m41;
    const d = m.m12 - s[1] * m.m14, e = m.m22 - s[1] * m.m24, f = s[1] * m.m44 - m.m42, det = a * e - b * d;
    return [(c * e - b * f) / det, (a * f - c * d) / det];
  };
  // The move that takes a mark's holds from where they were drawn to where their windows are seen now: a shift for
  // one hold; a shift, a turn and a stretch for two (an arrow between two windows).
  function carry(holds, layerSeen) {
    const from = holds.map((hold) => hold.at()), to = holds.map((hold, i) => under(layerSeen, see(floats[hold.win].seen, from[i])));
    if (holds.length === 1) return { origin: from[0], shift: [to[0][0] - from[0][0], to[0][1] - from[0][1]], turn: 0, grow: 1 };
    const mid = (p) => [(p[0][0] + p[1][0]) / 2, (p[0][1] + p[1][1]) / 2], span = (p) => [p[1][0] - p[0][0], p[1][1] - p[0][1]];
    const a = mid(from), b = mid(to), u = span(from), v = span(to);
    return { origin: a, shift: [b[0] - a[0], b[1] - a[1]], turn: Math.atan2(v[1], v[0]) - Math.atan2(u[1], u[0]), grow: Math.hypot(v[0], v[1]) / (Math.hypot(u[0], u[1]) || 1) };
  }
  const moved = (move, p) => {
    const x = (p[0] - move.origin[0]) * move.grow, y = (p[1] - move.origin[1]) * move.grow, cos = Math.cos(move.turn), sin = Math.sin(move.turn);
    return [move.origin[0] + move.shift[0] + x * cos - y * sin, move.origin[1] + move.shift[1] + x * sin + y * cos];
  };
  // A mark can have a part on each of the engine's two planes, made at different times.
  const partsOf = (id) => $$(`:scope > .ink-layer [data-id="${id}"]`, desk);
  function settle() {
    flat = true;
    desk.classList.add("is-flat");
    desk.style.transform = "";
    for (const f of floats) { f.win.style.transform = ""; f.shadow.style.transform = ""; f.shadow.style.opacity = ""; }
    for (const el of $$(":scope > .ink-layer [data-id]", desk)) Object.assign(el.style, { translate: "", rotate: "", scale: "", transformOrigin: "" });
    holdsOf.clear();
    ghost.style.translate = "";
    ghostAt[0] = ghostAt[1] = 0;
  }

  tasks.push((dt, now) => {
    const t = tiltNow, rect = sceneRect || scene.getBoundingClientRect();
    // Lean toward a pointer that is over or near the stage.
    const near = t > 0 && !userFlat && pointer && pointer[0] > rect.left - 120 && pointer[0] < rect.right + 120 && pointer[1] > rect.top - 160 && pointer[1] < rect.bottom + 120;
    let leaning = false;
    for (const f of floats) {
      const dx = near ? clamp((pointer[0] - rect.left - f.screen[0]) / 520, -1, 1) : 0, dy = near ? clamp((pointer[1] - rect.top - f.screen[1]) / 420, -1, 1) : 0;
      // Once the visitor has taken over the windows come to rest quickly, so the desk is flat and plain before a
      // first stroke ends and before its frame grows.
      [dx * 7 * depth * t, dy * 6 * depth * t, -dy * 4.5 * t, dx * 5.5 * t].forEach((to, i) => { f.lean[i].to = to; if (f.lean[i].step(t && !userFlat ? dt : dt * 3)) leaning = true; });
    }
    if (t === 0 && !leaning) { if (!flat) settle(); return false; }
    if (flat) { flat = false; desk.classList.remove("is-flat"); }
    if (!eye) measure();

    const s = now / 1000, turnBy = t * (depth < 0.6 ? 0.6 : 1), [w, h] = size;
    const rx = POSE.rx * turnBy, ry = POSE.ry * turnBy, rz = POSE.rz * turnBy, scale = 1 - POSE.shrink * t;
    // The toolbar hovers a little over the desk; less where it is as wide as the desk, so it stays inside it.
    const zl = 118 * depth * t, zd = 58 * depth * t * clamp((w - 600) / 500, 0.25, 1);
    desk.style.transform = `rotateX(${rx.toFixed(3)}deg) rotateY(${ry.toFixed(3)}deg) rotateZ(${rz.toFixed(3)}deg) scale(${scale.toFixed(4)})`;
    desk.style.setProperty("--zl", `${zl.toFixed(2)}px`);
    desk.style.setProperty("--zd", `${zd.toFixed(2)}px`);
    const deskLook = eye.translate(w * PIVOT[0], h * PIVOT[1]).rotateAxisAngleSelf(1, 0, 0, rx).rotateAxisAngleSelf(0, 1, 0, ry).rotateAxisAngleSelf(0, 0, 1, rz).scaleSelf(scale, scale, 1).translateSelf(-w * PIVOT[0], -h * PIVOT[1]);
    for (const f of floats) {
      const wave = (i) => Math.sin((s * TAU) / f.period[i] + f.phase[i]) * f.sway[i];
      // Windows that are stacked keep their rows: they shift sideways only, so one does not slide over the next.
      const x = (f.rest[0] + wave(0)) * depth * t + f.lean[0].at, y = (stacked ? wave(1) * 0.4 : f.rest[1] + wave(1)) * depth * t + f.lean[1].at, z = (f.rest[2] + wave(2)) * depth * t;
      const ax = wave(3) * t + f.lean[2].at, ay = wave(4) * t + f.lean[3].at;
      f.win.style.transform = `translate3d(${x.toFixed(2)}px, ${y.toFixed(2)}px, ${z.toFixed(2)}px) rotateX(${ax.toFixed(3)}deg) rotateY(${ay.toFixed(3)}deg)`;
      // The shadow stays on the desk: the higher the window, the further off, the larger and the fainter it is.
      f.shadow.style.transform = `translate3d(${(x + z * 0.16).toFixed(2)}px, ${(y + z * 0.5).toFixed(2)}px, 0) scale(${(1 + z * 0.0012).toFixed(4)})`;
      f.shadow.style.opacity = (Math.min(1, z / 70) * 0.5).toFixed(3);
      f.seen = deskLook.translate(f.center[0], f.center[1]).translateSelf(x, y, z).rotateAxisAngleSelf(1, 0, 0, ax).rotateAxisAngleSelf(0, 1, 0, ay).translateSelf(-f.center[0], -f.center[1]);
      f.screen = see(f.seen, f.center);
    }
    // Carry each scripted mark with its window, and the pointer with what it is busy with. A mark keeps its holds
    // after it has let go of its anchor, so it does not jump while the desk comes flat.
    const layerSeen = deskLook.translate(0, 0, zl);
    for (const m of ink.marks()) {
      const holds = holdsOf.get(m.id) || (m.anchor && carried.get(m.anchor));
      if (!holds) continue;
      holdsOf.set(m.id, holds);
      const move = carry(holds, layerSeen);
      for (const el of partsOf(m.id)) {
        el.style.translate = `${move.shift[0].toFixed(2)}px ${move.shift[1].toFixed(2)}px`;
        if (holds.length > 1) Object.assign(el.style, { transformOrigin: `${move.origin[0].toFixed(2)}px ${move.origin[1].toFixed(2)}px`, rotate: `${move.turn.toFixed(5)}rad`, scale: move.grow.toFixed(5) });
      }
    }
    if (hand && ghost.classList.contains("is-on")) {
      const at = [Number(ghost.dataset.x) || 0, Number(ghost.dataset.y) || 0], to = moved(carry(hand, layerSeen), at), k = 1 - Math.exp(-dt / 110);
      ghostAt[0] += (to[0] - at[0] - ghostAt[0]) * k;
      ghostAt[1] += (to[1] - at[1] - ghostAt[1]) * k;
      ghost.style.translate = `${ghostAt[0].toFixed(2)}px ${ghostAt[1].toFixed(2)}px`;
    }
    return true;
  });
  // A soft light on the desk follows the pointer.
  const panel = $(".desk-panel", desk);
  scene.addEventListener("pointermove", (e) => {
    const r = panel.getBoundingClientRect();
    desk.style.setProperty("--px", `${(((e.clientX - r.left) / r.width) * 100).toFixed(1)}%`);
    desk.style.setProperty("--py", `${(((e.clientY - r.top) / r.height) * 100).toFixed(1)}%`);
  }, { passive: true });
} else desk.classList.add("is-flat");

// 2. What you can do -------------------------------------------------------------------------------
// Every card's picture is a small sample screen that the pointer draws on, live, with the engine.
const measureText = document.createElement("canvas").getContext("2d");
const textSize = (shot) => Math.round(clamp(shot.clientWidth * 0.06, 15, 38));
const em = (shot) => parseFloat(getComputedStyle(shot).fontSize) || 16;
const SCENES = {
  // Ring the number, then a bowed arrow to the dip, both in the selected gradient.
  point: (t, shot) => [
    { tool: "ellipse", around: t("kpi"), padX: em(shot) * 0.7, padY: em(shot) * 0.36, size: 3, rough: 6, duration: 760, pause: 260 },
    { tool: "arrow", from: { el: t("kpi"), at: "bottom", dx: em(shot) * -0.4, dy: em(shot) * 0.85 }, to: { el: t("dip"), dx: -3, dy: -7 }, bend: -0.34, size: 3, rough: 6, duration: 700 },
  ],
  // Smart Draw: a rough box round the button becomes a neat one, then a note is typed beside it.
  box: (t, shot, layer) => {
    const unit = em(shot), size = textSize(shot), pay = $('[data-t="pay"]', shot), box = { tool: "rect", around: t("pay"), pad: unit * 0.6, size: 3 };
    const beside = () => { const b = localBox(pay, shot); return [b.x + b.w + unit * 1.7, b.y + b.h / 2 - size * 0.56]; };
    measureText.font = `600 ${size}px "Caveat"`;
    const note = beside()[0] + measureText.measureText("make it bigger").width + 12 <= shot.clientWidth ? "make it bigger" : "bigger";
    return [
      { ...box, rough: 6, duration: 900, pause: 460 },
      { run: () => { const rough = layer.marks().find((mark) => mark.type === "rect"); if (rough) layer.remove(rough.id); } },
      { ...box, rough: 0, round: true, duration: 1, pause: 520 },
      { tool: "text", at: beside, text: note, speed: 50 },
    ];
  },
  blur: (t) => [
    { tool: "blur", over: t("email"), pad: 5, amount: 13, duration: 700, pause: 380 },
    { tool: "blur", over: t("card"), pad: 5, amount: 13, duration: 700 },
  ],
  // Spotlight: everything dims but a circle round the pointer, which visits a few of the pictures.
  spot: (t, shot, layer) => [{ run: async () => {
    const at = (name) => { const b = localBox($(`[data-t="${name}"]`, shot), shot); return [b.x + b.w / 2, b.y + b.h / 2]; };
    const el = layer.layer, pointer = $(".ink-ghost", el);
    const light = (p) => { el.style.setProperty("--px", `${p[0]}px`); el.style.setProperty("--py", `${p[1]}px`); };
    layer.setSpotlight(true);
    el.style.setProperty("--ink-spot-size", `${Math.round(shot.clientHeight * 0.21)}px`);
    el.classList.add("has-pointer");
    light(at("s2"));
    if (!motion) return;
    let following = true;
    const follow = () => { if (!following) return; light([Number(pointer.dataset.x), Number(pointer.dataset.y)]); requestAnimationFrame(follow); };
    layer.placeGhost(at("s2"));
    follow();
    for (const name of ["s6", "s4", "s1", "s5"]) { await wait(420); await layer.moveGhost(at(name), 900); }
    following = false;
  } }],
  steps: (t) => ["p1", "p2", "p3"].map((pin, i) => ({ tool: "counter", at: { el: t(pin) }, n: i + 1, color: BLUE, pause: 360 })),
  system: (t, shot) => {
    // A label sits in the middle of its shape, whatever size the hand-written text is at this width.
    const label = (pin, text, color) => ({ tool: "text", text, color, speed: 50, pause: 120, at: () => {
      const b = localBox($(`[data-t="${pin}"]`, shot), shot), size = textSize(shot);
      measureText.font = `600 ${size}px "Caveat"`;
      return [b.x + b.w / 2 - measureText.measureText(text).width / 2 - 4, b.y + b.h / 2 - size * 0.56];
    } });
    const link = (a, b) => ({ tool: "arrow", from: { el: t(a), at: "right", dx: 7 }, to: { el: t(b), at: "left", dx: -9 }, bend: 0, color: PURPLE, size: 3, duration: 380, pause: 100 });
    return [
      { tool: "rect", around: t("n1"), pad: 0, color: BLUE, size: 3, duration: 520 }, label("n1", "app", BLUE), link("n1", "n2"),
      { tool: "rect", around: t("n2"), pad: 0, color: TEAL, size: 3, duration: 520 }, label("n2", "API", TEAL), link("n2", "n3"),
      { tool: "ellipse", around: t("n3"), pad: 0, color: CORAL, size: 3, duration: 620 }, label("n3", "data", CORAL),
    ];
  },
};
function sketch(card, shot) {
  const layer = createInk({ surface: shot, interactive: false, gradient: "orchid" });
  const target = (name) => standIn($(`[data-t="${name}"]`, shot), layer, shot);
  const fit = () => layer.layer.style.setProperty("--ink-text", `600 ${textSize(shot)}px/1.1 "Caveat", "Bradley Hand", cursive`);
  const empty = () => { layer.clear(); layer.setSpotlight(false); layer.layer.classList.remove("has-pointer"); layer.hideGhost(); };
  let busy = false, shown = false;
  async function play() {
    if (busy) return;
    busy = true;
    empty();
    shot.classList.remove("is-leaving");
    fit();
    await fontsReady;
    await wait(motion ? 420 : 0);
    await layer.perform(SCENES[shot.dataset.scene](target, shot, layer), { cursor: motion });
    await wait(500);
    layer.hideGhost();
    layer.reflow();
    busy = false;
    if (!shown && motion) leave();
  }
  // A card that goes back into the hand loses its drawing quietly, so it starts clean the next time.
  async function leave() {
    shot.classList.add("is-leaving");
    await wait(280);
    if (!shown && !busy) empty();
  }
  addEventListener("resize", () => { fit(); setTimeout(() => layer.reflow(), 260); });
  $(".ctl", card).addEventListener("click", play);
  if (motion) $(".frame", card).addEventListener("pointerenter", (e) => { if (e.pointerType === "mouse" && shown) play(); });
  return { show() { if (shown) return; shown = true; play(); }, hide() { if (!shown) return; shown = false; if (motion && !busy) leave(); } };
}
const cards = $$(".do-deck .card");
const visuals = cards.map((card) => sketch(card, $(".shot", card)));

if (motion) {
  // The fan. Progress through the track turns into one number, 0 for the first card to 5 for the last. Each card
  // has a spring that says how far it is dealt (0 in the hand, 1 at the front): it is told to go to 1 for a while
  // around its own number and to 0 away from it, and the spring's small overshoot is what makes the deal land softly.
  const track = $(".do-track"), pin = $(".do-pin"), fan = $(".do-deck"), tabs = $$(".do-tabs button"), hold = 0.22;
  const deals = cards.map((card, i) => { card.style.setProperty("--i", i); return spring(210, 21, i ? 0 : 1, 0.0004); });
  let front = -1, playing = -1, deckSeen = false;
  // The card at the front is the only one whose inside can be used; a card in the hand is one big button.
  const face = (index) => {
    if (index === front) return;
    front = index;
    cards.forEach((card, i) => {
      card.classList.toggle("is-front", i === index);
      for (const part of card.children) part.inert = i !== index;
    });
    tabs.forEach((tab, i) => tab.setAttribute("aria-current", String(i === index)));
  };
  const play = (index) => {
    if (index === playing) return;
    playing = index;
    visuals.forEach((visual, i) => (i === index ? visual.show : visual.hide)());
  };
  const go = (i) => {
    const r = track.getBoundingClientRect();
    scrollTo({ top: r.top + scrollY + (r.height - innerHeight) * (i / (cards.length - 1)) + 1, behavior: "smooth" });
  };
  const deck = channel(() => {
    const r = track.getBoundingClientRect();
    return clamp(-r.top / Math.max(1, r.height - innerHeight), 0, 1) * (cards.length - 1);
  }, (at) => {
    // A card has tucked back by the time the next one sets off, so two are never in the air at once.
    deals.forEach((deal, i) => { deal.to = 1 - smooth((Math.abs(at - i) - hold) / (0.5 - hold)); });
    face(Math.round(at));
    play(deckSeen ? front : -1);
  });
  tasks.push((dt) => {
    let moving = false;
    deals.forEach((deal, i) => {
      if (deal.step(dt)) moving = true;
      if (deal.at === deal.shown) return;
      deal.shown = deal.at;
      cards[i].style.setProperty("--d", deal.at.toFixed(4));
      cards[i].style.zIndex = deal.at > 0.02 ? 10 + Math.round(deal.at * 10) : i;
    });
    return moving;
  });
  new IntersectionObserver(([entry]) => {
    deckSeen = entry.isIntersecting;
    play(deckSeen ? Math.round(deck.value ?? 0) : -1);
  }, { threshold: 0.25 }).observe(fan);
  tabs.forEach((tab, i) => tab.addEventListener("click", () => go(i)));
  cards.forEach((card, i) => card.addEventListener("click", () => { if (i !== front) go(i); }));
  // The hand is held so the tops of its cards show above the bottom of the screen, whatever the card's size.
  const fitFan = () => {
    const height = cards[0].offsetHeight, small = parseFloat(getComputedStyle(fan).getPropertyValue("--fan-s")) || 0.36;
    const peek = clamp(pin.clientHeight * 0.15, 92, 150);
    fan.style.setProperty("--fan-drop", `${Math.round(pin.clientHeight - peek + (small * height) / 2 - (fan.offsetTop + height / 2))}px`);
  };
  new ResizeObserver(fitFan).observe(fan);
  addEventListener("resize", fitFan);
  fitFan();
} else {
  // Without motion the cards are a plain grid, and each scene shows its last frame.
  visuals.forEach((visual) => visual.show());
}

// 3. The two keys ----------------------------------------------------------------------------------
const keysSection = $("#keys"), modeWord = $("[data-mode-word]");
function setKeysMode(mode) {
  keysSection.dataset.mode = mode;
  modeWord.textContent = mode === "draw" ? "Right now you're drawing." : "Right now you're using the app.";
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
  const keys = $$(".key", set);
  for (const key of keys) { key.classList.add("is-down"); await wait(motion ? 70 : 0); }
  // As the keys themselves: Control-Shift-2 is Draw, and Esc is the way back.
  ink.setMode(set.dataset.set === "draw" ? "draw" : "use");
  await wait(160);
  for (const key of keys) { key.classList.remove("is-down"); await wait(motion ? 50 : 0); }
});

if (motion) {
  // Where a section is on its way through: -1 just below the screen, 0 in the middle, 1 just above.
  const through = (el) => () => {
    const r = el.getBoundingClientRect();
    return clamp((innerHeight / 2 - (r.top + r.height / 2)) / (innerHeight / 2 + r.height / 2), -1, 1);
  };
  for (const el of $$("[data-through]")) channel(through(el), (v) => el.style.setProperty("--through", v.toFixed(4)));
}

// Arrivals -----------------------------------------------------------------------------------------
// Headings are split into words so each can pop in on its own; the text stays the same for a screen reader.
for (const heading of $$("[data-rise]")) {
  let i = 0;
  for (const node of [...heading.childNodes]) {
    if (node.nodeType === Node.TEXT_NODE) {
      const parts = node.textContent.split(/(\s+)/).filter(Boolean).map((part) => {
        if (/^\s+$/.test(part)) return document.createTextNode(" ");
        const word = document.createElement("span");
        word.className = "w";
        word.style.setProperty("--i", i++);
        word.textContent = part;
        return word;
      });
      node.replaceWith(...parts);
    } else { node.classList.add("w"); node.style.setProperty("--i", i++); }
  }
}
for (const group of $$(".pop")) [...group.children].forEach((child, i) => child.style.setProperty("--i", i));
const arrive = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) { entry.target.classList.add("is-in"); arrive.unobserve(entry.target); }
}, { rootMargin: "0px 0px -10% 0px", threshold: 0.15 });
$$("[data-rise], .pop").forEach((el) => arrive.observe(el));

// "There's more in the app": the four pebbles arrive on the app's Magic flight, one after another.
const more = $(".also-grid");
if (motion) new IntersectionObserver(([entry], observer) => {
  if (!entry.isIntersecting) return;
  observer.disconnect();
  more.classList.add("is-in");
  [...more.children].forEach((item, i) => {
    const timing = { duration: visibilityDuration("magic", true) * 1000 * 1.3, delay: i * 90 };
    playMotion(item, magicFlight({ x: (1.5 - i) * 64, y: 150, scale: 0.9 }, [0, 0], `more ${i}`), timing);
    playMotion(item, travelFade(true, true), timing);
  });
}, { rootMargin: "0px 0px -10% 0px", threshold: 0.15 }).observe(more);

// "A toolbar that's yours": the engine's own toolbar with a few tools taken out, shrinking to its one button and
// growing back as the app's does. It is a picture of the bar, so nothing in it can be pressed.
const tile = $("#tb-live"), tileFrame = tile.closest(".ill");
try {
  const small = createInk({ surface: tile, interactive: false, toolbarPosition: "absolute", appearance: root.dataset.theme, gradient: "orchid", tool: "arrow", properties: false,
    toolbarItems: ["rect", "ellipse", "arrow", "pen", "text"], hiddenControls: ["library", "board", "undo", "redo", "clearAll", "properties", "settings", "hide"] });
  tile.append(small.toolbar);
  small.toolbar.inert = true;
  themed.push(small);
  tileFrame.classList.add("has-live");
  let barWidth = 0;
  const fitTile = () => {
    small.fit();
    if (!small.collapsed && small.toolbar.offsetWidth) { barWidth = small.toolbar.offsetWidth; tile.style.height = `${small.toolbar.offsetHeight + 44}px`; }
    if (barWidth) tile.style.scale = Math.min(1, (tileFrame.clientWidth * 0.86) / barWidth).toFixed(4);
  };
  new ResizeObserver(fitTile).observe(tileFrame);
  fitTile();
  if (motion) (async () => {
    for (;;) {
      await wait(1700);
      if (!tileFrame.classList.contains("is-live")) continue;
      await Promise.resolve(small.setCollapsed(true));
      await wait(1500);
      await Promise.resolve(small.setCollapsed(false));
      await wait(900);
    }
  })();
} catch { /* the drawing of a toolbar in the page stays */ }

// Loops and drifting lights run only while they are on screen.
const live = new IntersectionObserver((entries) => {
  for (const entry of entries) entry.target.classList.toggle("is-live", entry.isIntersecting);
}, { threshold: 0.3 });
$$(".ill, .coffee").forEach((el) => live.observe(el));
const lit = new IntersectionObserver((entries) => {
  for (const entry of entries) entry.target.classList.toggle("is-live", entry.isIntersecting);
});
$$(".orbs").forEach((el) => lit.observe(el));

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

// Buttons are a little magnetic: under a mouse they drift a few pixels toward the pointer, and spring back.
if (motion && matchMedia("(hover: hover) and (pointer: fine)").matches) {
  const pulled = new Set();
  for (const el of $$(".btn, .chip:not(#stage-done), .nav-coffee, .nav-try, .theme-switch")) {
    const magnet = { el, x: spring(190, 15, 0, 0.02), y: spring(190, 15, 0, 0.02) };
    el.addEventListener("pointermove", (e) => {
      const r = el.getBoundingClientRect(), reach = Math.min(5.5, r.height * 0.12);
      magnet.x.to = clamp((e.clientX - (r.left - magnet.x.at + r.width / 2)) / (r.width / 2), -1, 1) * reach * 1.3;
      magnet.y.to = clamp((e.clientY - (r.top - magnet.y.at + r.height / 2)) / (r.height / 2), -1, 1) * reach;
      pulled.add(magnet);
      request();
    });
    el.addEventListener("pointerleave", () => { magnet.x.to = magnet.y.to = 0; request(); });
  }
  tasks.push((dt) => {
    let moving = false;
    for (const magnet of pulled) {
      const busy = [magnet.x.step(dt), magnet.y.step(dt)].some(Boolean);
      magnet.el.style.translate = magnet.x.at || magnet.y.at ? `${magnet.x.at.toFixed(2)}px ${magnet.y.at.toFixed(2)}px` : "";
      if (busy) moving = true; else if (!magnet.x.to && !magnet.y.to) pulled.delete(magnet);
    }
    return moving;
  });
}

// Install ------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const status = $("#status");
for (const button of $$("[data-copy]")) {
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
    // What to do next is said where the command's name was, and comes back to it.
    const says = $("p", button.closest(".command"));
    button.textContent = copied ? "Copied" : "Copy";
    says.textContent = status.textContent = copied ? "Copied. Paste it into Terminal and press Enter." : "It's selected. Copy it with your keyboard.";
    clearTimeout(button.timer);
    button.timer = setTimeout(() => { button.textContent = "Copy"; says.textContent = says.dataset.says; status.textContent = ""; }, 3200);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

matchMedia("(prefers-reduced-motion: reduce)").addEventListener("change", () => location.reload());
request();
