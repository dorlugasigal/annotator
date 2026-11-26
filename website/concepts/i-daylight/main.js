import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
// The app's Share region and Break timer as small live scenes, for "What you can do".
import { breakTimerScene, shareRegionScene } from "/concepts/shared/scenes.js";
// The app's own motion: its curves and its Pop.
import { css as curve, poseKeyframes } from "/concepts/shared/app-motion.js";

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
const wait = (ms, signal) => new Promise((done) => {
  if (signal?.aborted || ms <= 0) { done(); return; }
  const timer = setTimeout(done, ms);
  signal?.addEventListener("abort", () => { clearTimeout(timer); done(); }, { once: true });
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
  root.classList.toggle("is-scrolled", scrollY > 8);
  if (moving) request(); else lastFrame = 0;
}
function request() { if (!queued) { queued = true; requestAnimationFrame(frame); } }
addEventListener("scroll", request, { passive: true });
addEventListener("resize", request);

// 1. The stage -------------------------------------------------------------------------------------
const desk = $("#desk"), dock = $("#dock"), scene = $(".stage-scene"), apps = $("#apps");
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset), with the app's
// Dusk gradient as the
// color, curved arrows, the roughest line. It rests on the Arrow tool so its properties row shows that. The engine
// itself starts the row closed on a phone, which keeps the bar compact there.
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, tool: "arrow", gradient: "dusk", moreHref: "#install" });
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the desk's
// frame opens up to fill the window, and scrolling brings it back. The desk itself does not move: in index.html the
// panel, the cue's ring and the dock carry data-stage-frame and the mode chip data-stage-corner.
const stage = growingStage({ ink, surface: desk, tries: "[data-try]", motion: () => motion, invite: "Draw here" });
// Joining in ends the script and lays the desk flat (the frame grows only once it is).
stage.on("join", () => takeOver());

// Theme --------------------------------------------------------------------------------------------
// Dark unless the visitor chose light. The choice is kept; the page's head applies it before the first paint.
const themeSwitch = $(".theme-switch"), themeColor = $('meta[name="theme-color"]');
const PAGE_COLOR = { dark: "#0e0e11", light: "#f9f8fa" };
// The shared scenes under "What you can do" follow the theme too.
const liveScenes = [];
function showTheme(theme) {
  root.dataset.theme = theme;
  themeSwitch.setAttribute("aria-checked", String(theme === "dark"));
  themeColor.content = PAGE_COLOR[theme];
  ink.setAppearance(theme);
  for (const scene of liveScenes) scene.setAppearance(theme);
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
  // The new theme opens in a circle from the switch, far enough to reach the farthest corner.
  const r = themeSwitch.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
  root.style.setProperty("--vt-x", `${x}px`);
  root.style.setProperty("--vt-y", `${y}px`);
  root.style.setProperty("--vt-r", `${Math.ceil(Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y)))}px`);
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

// The lean. Fully leaning at the top of the page, flat once the stage has come up the screen. As soon as the visitor
// draws, it goes flat and stays flat: a pointer on a tilted plane would not draw where it points.
let userFlat = !motion;
channel(() => {
  if (userFlat) return 0;
  const top = scene.getBoundingClientRect().top + scrollY;
  return 1 - smooth(scrollY / clamp(top - innerHeight * 0.1, 200, innerHeight * 0.7));
}, (v) => {
  desk.style.setProperty("--tilt", v.toFixed(4));
  desk.style.setProperty("--lift", (4 * v * (1 - v)).toFixed(4));
  desk.classList.toggle("is-flat", v === 0);
});

// The sample apps ----------------------------------------------------------------------------------
// Three small apps with made-up data. Nothing leaves the page. In Use apps they answer the visitor; in Draw they
// are inert and the pointer is the tool. Drawings never follow what the apps do: that is how the real app behaves.
const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const WEEKS = {
  this: { total: 318, note: "21% fewer than last week", days: [62, 58, 64, 55, 41, 22, 16] },
  last: { total: 402, note: "3% more than the week before", days: [66, 61, 68, 63, 59, 45, 40] },
};
const numberEl = $("#t-number"), lineEl = $("#t-line"), deltaEl = $("[data-delta]"), barsEl = $("[data-bars]");
for (const day of DAYS) {
  const bar = document.createElement("button");
  bar.type = "button";
  bar.className = "bar";
  bar.innerHTML = `<b class="tip" aria-hidden="true"></b><i></i><span aria-hidden="true">${day[0]}</span>`;
  barsEl.append(bar);
}
const weekButtons = $$("[data-week]");
function showWeek(name) {
  const week = WEEKS[name];
  numberEl.textContent = week.total;
  deltaEl.textContent = week.note;
  weekButtons.forEach((button) => button.setAttribute("aria-pressed", String(button.dataset.week === name)));
  $$(".bar", barsEl).forEach((bar, i) => {
    bar.style.setProperty("--v", week.days[i]);
    bar.classList.toggle("low", week.days[i] < 50);
    bar.setAttribute("aria-label", `${DAYS[i]}: ${week.days[i]} sign-ups`);
    $(".tip", bar).textContent = week.days[i];
  });
}
weekButtons.forEach((button) => button.addEventListener("click", () => { used(); showWeek(button.dataset.week); }));

const pages = $$(".slide-page"), slideCount = $("[data-slide-count]"), slideButtons = $$("[data-slide-go]");
let slide = 0;
function showSlide(index) {
  slide = clamp(index, 0, pages.length - 1);
  pages.forEach((page, i) => page.classList.toggle("is-on", i === slide));
  slideCount.textContent = `${slide + 1} of ${pages.length}`;
  slideButtons.forEach((button) => { button.disabled = slide + Number(button.dataset.slideGo) < 0 || slide + Number(button.dataset.slideGo) >= pages.length; });
}
slideButtons.forEach((button) => button.addEventListener("click", () => { used(); showSlide(slide + Number(button.dataset.slideGo)); }));

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
  showWeek("this");
  showSlide(0);
  $$(".me", thread).forEach((bubble) => bubble.remove());
  replyInput.value = "";
  windows.forEach((win) => { offsets.delete(win); win.style.removeProperty("--dx"); win.style.removeProperty("--dy"); win.style.zIndex = ""; });
}
// Marks the script drew follow their targets when the page reflows, until an app is used: from then on they stay
// where they are on the screen, whatever changes under them.
function used() { for (const mark of ink.marks()) mark.anchor = null; }
// A click on the stage while the script plays is the visitor taking over: the script stops and the apps are theirs.
// (A click, not a press, so a finger that scrolls the page over the stage changes nothing.)
desk.addEventListener("click", (e) => {
  if (!e.isTrusted || !run || ink.toolbar.contains(e.target)) return;
  takeOver(false);
  if (ink.state.mode !== "draw") return;
  // The script was showing Draw, so the apps did not hear this click: switch to Use apps and pass it on.
  ink.setMode("use");
  const control = document.elementFromPoint(e.clientX, e.clientY)?.closest("button, input");
  if (!control || !apps.contains(control)) return;
  control.focus({ preventScroll: true });
  if (control.matches("button")) control.click();
}, true);
apps.addEventListener("keydown", (e) => { if (e.isTrusted && e.key !== "Tab") takeOver(false); }, true);
showWeek("this");
showSlide(0);

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
const number = standIn(ink, () => planeBox(numberEl));

// The scripted demo ----------------------------------------------------------------------------------
// The ring and the arrow name no color: they take the gradient selected in the toolbar. Both are drawn at the
// toolbar's strongest roughness, and the arrow bows well clear of a straight line.
function markSizes() {
  const small = desk.clientWidth < 600;
  return { small, px: small ? 12 : 17, py: small ? 8 : 11, size: small ? 3 : 3.5 };
}
function drawSteps() {
  const { px, py, size } = markSizes();
  // The arrow runs from the line on the slide to the number: sideways when the windows sit side by side, down when
  // they are stacked.
  const ends = () => {
    const l = planeBox(lineEl), n = planeBox(numberEl);
    return n.x > l.x + l.w
      ? [[l.x + l.w + 10, l.y + l.h * 0.3], [n.x - px - 9, n.y + n.h * 0.42]]
      : [[l.x + l.w - 4, l.y + l.h + 9], [n.x + n.w * 0.8, n.y - py - 14]];
  };
  return [
    { tool: "ellipse", around: number, padX: px, padY: py, size, rough: 6, duration: 820, pause: 240 },
    { tool: "arrow", from: () => ends()[0], to: () => ends()[1], bend: 0.42, size, rough: 6, duration: 760, pause: 260 },
  ];
}
function labelSteps() {
  const { small, px } = markSizes();
  return [{ tool: "text", at: { el: number, at: "right", dx: px + 12, dy: small ? -15 : -18 }, text: "why the dip?", pause: 200 }];
}

// The tool the visitor chose. A script that shows its tools in the toolbar puts back the tool it started with when
// it ends; if the visitor picked one meanwhile, theirs is put back instead.
let chosen = null;
ink.on("tool", (tool) => { chosen = tool; });
const keepChoice = () => { if (chosen && ink.state.tool !== chosen) ink.setTool(chosen, ink.state.mode !== "draw"); };

const replay = $("#stage-replay"), done = $("#stage-done");
const modeButton = $('[data-action="mode"]', ink.toolbar), pill = $(".ink-pill", ink.toolbar);
let run = null, chartSeen = false, seenWaiters = [];
// True once the visitor has done something on the stage. Until then, while the script plays, every change of mode
// is the script's own.
let acted = false;
const scripted = () => Boolean(run) && !acted;
// The engine's own Shrink, show and hide and pointer click, in the app's motion. An engine without them changes
// state at once, and the page fades the drawings itself.
const appMotion = typeof ink.shrink === "function";
const setRunning = (running) => {
  replay.dataset.state = running ? "playing" : "";
  $("b", replay).textContent = running ? "Pause" : "Replay";
  // While the script plays, Draw is shown but the stage takes no presses: a swipe over it still scrolls the page.
  desk.classList.toggle("is-scripted", running);
};
const whenSeen = (signal) => new Promise((go) => {
  if (chartSeen || signal.aborted) { go(); return; }
  seenWaiters.push(go);
  signal.addEventListener("abort", go, { once: true });
});

// Hide and show in the app's Magic style: each drawing flies off on a swoop and the toolbar goes in a puff of
// sparks, then they come back.
async function appHide(hidden, signal) {
  if (appMotion) { await ink.setHidden(hidden, { style: "magic" }); return; }
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
// The pointer goes to something on the stage and presses it.
async function press(el, action, signal, pause = 520) {
  if (signal.aborted) return;
  const box = planeBox(el);
  await ink.moveGhost([box.x + box.w / 2, box.y + box.h * 0.55]);
  await wait(140, signal);
  if (signal.aborted) return;
  el.classList.add("is-pressed");
  ink.clickGhost?.();
  action();
  await wait(170, signal);
  el.classList.remove("is-pressed");
  await wait(pause, signal);
}
// One round tells the product's story: draw, use the apps while the drawings stay, draw again.
async function round(signal) {
  const perform = (steps) => (signal.aborted ? null : ink.perform(steps, { cursor: true, showTool: true, signal }));
  resetApps();
  if (ink.state.hidden) await ink.setHidden(false);
  if (ink.collapsed) await ink.setCollapsed(false);
  if (signal.aborted) return;
  ink.setMode("draw");
  await wait(500, signal);
  await perform(drawSteps());

  // Use apps: the toolbar's own button, then the bar shrinks to one button as it does in the app.
  await press(modeButton, () => modeButton.click(), signal, 380);
  if (signal.aborted) return;
  await (appMotion ? ink.shrink() : ink.setCollapsed(true));
  await wait(520, signal);
  if (signal.aborted) return;
  used();
  await press($('[data-week="last"]'), () => showWeek("last"), signal, 900);
  await press($('[data-week="this"]'), () => showWeek("this"), signal, 500);
  if (signal.aborted) return;
  const field = planeBox(replyInput);
  await ink.moveGhost([field.x + 22, field.y + field.h * 0.55]);
  ink.clickGhost?.();
  for (const letter of "On it.") {
    if (signal.aborted) return;
    replyInput.value += letter;
    await wait(70, signal);
  }
  await press($("button", reply), () => { send(replyInput.value); replyInput.value = ""; }, signal, 500);
  if (signal.aborted) return;
  if (desk.clientWidth >= 600) {
    // A window moves and the drawings do not: they belong to the screen, not to the window.
    const win = $(".win-mail"), bar = planeBox($("[data-drag]", win)), from = [bar.x + bar.w * 0.5, bar.y + bar.h / 2];
    await ink.moveGhost(from);
    ink.clickGhost?.();
    win.classList.add("is-moving");
    await tween(620, (t) => { moveWindow(win, -34 * t, 26 * t); ink.placeGhost([from[0] - 34 * t, from[1] + 26 * t]); }, signal);
    win.classList.remove("is-moving");
    await wait(480, signal);
  }

  // Back to Draw: a press on the one button brings the toolbar back, as in the app.
  await press(pill, () => pill.click(), signal, 520);
  await perform(labelSteps());
  ink.hideGhost();
  keepChoice();
  if (signal.aborted) return;
  await wait(2400, signal);
  if (signal.aborted) return;

  // Hide and show, as Control-2 does in the app: the drawings come back as they were.
  await appHide(true, signal);
  await wait(700, signal);
  await appHide(false, signal);
  await wait(1100, signal);
  if (signal.aborted) return;
  await wipe(signal);
  await wait(500, signal);
}
async function loop(signal) {
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
    ink.hideGhost();
    keepChoice();
    // A script that was cut short while the drawings were away brings them back.
    if (ink.state.hidden) await ink.setHidden(false, { animate: false });
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
// The whole demo as a still picture, for visitors who asked for less motion.
async function still() {
  await ink.perform([...drawSteps(), ...labelSteps()], { cursor: false });
  ink.reflow();
}
async function restart() {
  await stop();
  if (ink.state.hidden) await ink.setHidden(false);
  if (ink.collapsed) await ink.setCollapsed(false);
  ink.setMode("use");
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
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
  stop().then(() => {
    if (hid && ink.state.hidden) ink.setHidden(false);
    if (small && ink.collapsed) ink.setCollapsed(false);
  });
  if (flat && !userFlat) { userFlat = true; request(); }
}
ink.toolbar.addEventListener("pointerdown", (e) => { if (e.isTrusted) takeOver(); }, true);
// The three keys, pressed while the script plays: the script stops and the key does what it says, whatever the
// script was showing. (This runs before the engine hears the key.)
addEventListener("keydown", (e) => {
  if (!e.isTrusted || !scripted()) return;
  const draws = e.ctrlKey && e.shiftKey && e.code === "Digit2", hides = e.ctrlKey && !e.shiftKey && e.code === "Digit2";
  if (!draws && !hides && e.key !== "Escape") return;
  takeOver(draws);
  if (draws && ink.state.mode === "draw") ink.setMode("use");
}, true);
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

// The script waits until the chart, where it draws first, is on screen.
new IntersectionObserver(([entry]) => {
  chartSeen = entry.isIntersecting;
  if (chartSeen) seenWaiters.splice(0).forEach((go) => go());
}, { threshold: 0.6 }).observe($(".win-chart"));
// Drawing ends when the stage is out of view, so keys and touches go back to the page.
new IntersectionObserver(([entry]) => { if (!entry.isIntersecting) ink.setMode("use"); }).observe(scene);

const fontsReady = Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {});
fontsReady.then(async () => {
  if (motion) { await wait(1500); if (!acted) start(); } else still();
});

// "Try it here", from anywhere on the page: the stage comes into view with a clean screen, the Pen and Draw on.
async function tryHere() {
  acted = true;
  userFlat = true;
  request();
  // The page answers at once: the stage comes into view and its edge lights up while the script winds down.
  const room = innerHeight - parseFloat(getComputedStyle(root).getPropertyValue("--header-h"));
  scene.scrollIntoView({ block: scene.offsetHeight < room - 24 ? "center" : "start", behavior: motion ? "smooth" : "auto" });
  desk.classList.remove("is-cued");
  void desk.offsetWidth;
  desk.classList.add("is-cued");
  await stop();
  if (ink.state.hidden) await ink.setHidden(false, { animate: false });
  if (ink.collapsed) await ink.setCollapsed(false);
  await wipe();
  ink.setTool("pen");
  $('[data-tool="pen"]', ink.toolbar)?.focus({ preventScroll: true });
  status.textContent = "This screen is live. Draw on it.";
}
for (const link of $$("[data-try]")) link.addEventListener("click", (e) => { e.preventDefault(); tryHere(); });
$(".stage-pulse").addEventListener("animationend", () => desk.classList.remove("is-cued"));

// 2. What you can do -------------------------------------------------------------------------------
// Each card holds one of the shared engine's scenes, in the page's gradient and theme. A scene plays while its card
// faces front, again and again with a short rest between runs, and starts over when the card comes back. A card
// whose scene is not wired yet keeps its empty frame.
const SHARED = { share: shareRegionScene, break: breakTimerScene };
function shared(shot) {
  if (!SHARED[shot.dataset.scene]) return { show() {}, hide() {} };
  const scene = SHARED[shot.dataset.scene](shot, { appearance: root.dataset.theme, gradient: "dusk" });
  liveScenes.push(scene);
  let turn = 0, shown = false;
  async function loop() {
    const mine = ++turn;
    while (mine === turn) {
      // Without motion one run is enough: it ends on the finished picture.
      if (!(await scene.play()) || !motion) return;
      await wait(1600);
    }
  }
  return {
    show() { if (shown) return; shown = true; loop(); },
    hide() { if (!shown) return; shown = false; turn++; scene.reset(); },
  };
}
const cards = $$(".do-deck .card");
const visuals = cards.map((card) => shared($(".shot", card)));

if (motion) {
  // The pinned deck. Progress through the track turns into one number, 0 for the first card to 3 for the last; each
  // card rests flat for a while around its own number, and eases between.
  const track = $(".do-track"), tabList = $(".do-tabs"), tabs = $$("button", tabList), rest = 0.2;
  cards.forEach((card, i) => card.style.setProperty("--i", i));

  // A change of card is choreographed in layers. The frame arrives with the scroll; once it has all but landed the
  // card becomes "current" and its words follow in CSS: the title word by word, the line under it, then a drawn
  // accent. A card that starts to leave gives them up in the reverse order, faster. Each layer is one transition on
  // one class, so a fast scroll turns it round from wherever it is.
  for (const card of cards) {
    const title = $("h3", card), words = title.textContent.trim().split(/\s+/);
    title.textContent = "";
    words.forEach((word, i) => {
      // Each word rises inside its own mask.
      const mask = document.createElement("span"), inner = document.createElement("span");
      mask.className = "tw";
      mask.style.setProperty("--w", i);
      inner.textContent = word;
      mask.append(inner);
      title.append(mask, i < words.length - 1 ? " " : "");
    });
    const accent = document.createElement("i");
    accent.className = "card-accent";
    accent.setAttribute("aria-hidden", "true");
    accent.append(document.createElement("b"));
    title.after(accent);
  }
  // What carries over from one card to the next: the accent dot in the tabs hops to the new tab, and a soft light
  // behind the deck glides to the side the new card's picture is on.
  // (The marker is a <b>: the tabs' own dots are styled as "i" inside the list.)
  const marker = document.createElement("b"), light = document.createElement("i");
  marker.className = "do-marker";
  marker.setAttribute("aria-hidden", "true");
  tabList.append(marker);
  light.className = "do-light";
  light.setAttribute("aria-hidden", "true");
  $(".do-pin").prepend(light);
  let hop = null, markerAt = -1;
  const dotOf = (index) => {
    const dot = $("i", tabs[index]).getBoundingClientRect(), list = tabList.getBoundingClientRect();
    return [dot.left + dot.width / 2 - list.left, dot.top + dot.height / 2 - list.top];
  };
  function moveMarker(index, animate = true) {
    const [x, y] = dotOf(index), to = `${x.toFixed(1)}px ${y.toFixed(1)}px`;
    // One animation per change: a new hop starts from wherever the last one had got to.
    const from = getComputedStyle(marker).translate;
    hop?.cancel();
    hop = null;
    marker.style.translate = to;
    marker.classList.add("is-on");
    if (!animate || markerAt < 0 || from === "none" || from === to) { markerAt = index; return; }
    const [fx, fy] = from.split(" ").map(parseFloat), far = Math.abs(x - fx);
    markerAt = index;
    hop = marker.animate([
      { translate: from, scale: "1 1" },
      { translate: `${((fx + x) / 2).toFixed(1)}px ${(Math.min(fy || y, y) - clamp(far * 0.16, 6, 16)).toFixed(1)}px`, scale: "1.5 0.8" },
      { translate: to, scale: "1 1" },
    ], { duration: clamp(260 + far * 0.9, 300, 480), easing: curve.standard });
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
  // A card is current from the moment its frame has all but landed until it starts to leave (two thresholds, so it
  // cannot flicker between them). Its scene starts only once the frame lies flat, and stops when the card leaves.
  const current = cards.map(() => false);
  function settle(at) {
    lastAt = at;
    cards.forEach((card, i) => {
      const x = at - i, whole = Math.floor(Math.abs(x)), part = Math.abs(x) - whole;
      const away = whole + smooth((part - rest) / (1 - 2 * rest));
      const now = deckSeen && away < (current[i] ? 0.1 : 0.06);
      if (now !== current[i]) { current[i] = now; card.classList.toggle("is-current", now); }
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
  tabs.forEach((tab, i) => tab.addEventListener("click", () => {
    const r = track.getBoundingClientRect();
    scrollTo({ top: r.top + scrollY + (r.height - innerHeight) * (i / (cards.length - 1)) + 1, behavior: "smooth" });
  }));
} else {
  // Without motion the cards are a plain grid, and each scene shows its last frame.
  visuals.forEach((visual) => visual.show());
}

if (motion) {
  // Where a section is on its way through: -1 just below the screen, 0 in the middle, 1 just above.
  const through = (el) => () => {
    const r = el.getBoundingClientRect();
    return clamp((innerHeight / 2 - (r.top + r.height / 2)) / (innerHeight / 2 + r.height / 2), -1, 1);
  };
  for (const el of $$("[data-through]")) channel(through(el), (v) => el.style.setProperty("--through", v.toFixed(4)));
}

// Arrivals -----------------------------------------------------------------------------------------
// Headings are split into words so each can rise on its own; the text stays the same for a screen reader.
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
for (const group of $$(".depth")) [...group.children].forEach((child, i) => child.style.setProperty("--i", i));
const arrive = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) { entry.target.classList.add("is-in"); arrive.unobserve(entry.target); }
}, { rootMargin: "0px 0px -10% 0px", threshold: 0.15 });
$$("[data-rise], .depth").forEach((el) => arrive.observe(el));

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
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

matchMedia("(prefers-reduced-motion: reduce)").addEventListener("change", () => location.reload());
request();
