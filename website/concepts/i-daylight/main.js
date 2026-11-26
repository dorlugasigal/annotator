import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
// The app's own motion: its curves, its toolbar Shrink, its Magic flight and its Pop.
import { css as curve, toolbarMorph, magicFlight, travelFade, visibilityDuration, poseKeyframes } from "/concepts/shared/app-motion.js";

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
const CORAL = "#F04538", BLUE = "#2B73E0", TEAL = "#00969E", PURPLE = "#7D54B8";
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
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset): a gradient as the
// color, curved arrows, the roughest line. It rests on the Arrow tool so its properties row shows that. The engine
// itself starts the row closed on a phone, which keeps the bar compact there.
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, tool: "arrow", moreHref: "#install" });
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, "Try it here"), the desk's
// frame opens up to fill the window, and scrolling brings it back. The desk itself does not move: in index.html the
// panel, the cue's ring and the dock carry data-stage-frame and the two corner pieces data-stage-corner.
const stage = growingStage({ ink, surface: desk, tries: "[data-try]", motion: () => motion });
// Joining in ends the script, lays the desk flat (the frame grows only once it is) and retires "Your turn".
stage.on("join", () => { takeOver(); takeTurn(); });

// Theme --------------------------------------------------------------------------------------------
// Dark unless the visitor chose light. The choice is kept; the page's head applies it before the first paint.
const themeSwitch = $(".theme-switch"), themeColor = $('meta[name="theme-color"]');
const PAGE_COLOR = { dark: "#0e0e10", light: "#faf9f6" };
function showTheme(theme) {
  root.dataset.theme = theme;
  themeSwitch.setAttribute("aria-checked", String(theme === "dark"));
  themeColor.content = PAGE_COLOR[theme];
  ink.setAppearance(theme);
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

// Windows move by their title bars, with the pointer or the arrow keys, and stay on the stage above the toolbar.
const windows = $$("[data-win]"), offsets = new WeakMap();
let front = 1;
function moveWindow(win, dx, dy) {
  const base = localBox(win, desk), edge = 6, style = getComputedStyle(desk);
  const floor = desk.clientHeight - parseFloat(style.getPropertyValue("--tb-h")) - parseFloat(style.getPropertyValue("--tb-bottom")) - edge;
  const x = clamp(dx, edge - base.x, Math.max(edge - base.x, desk.clientWidth - edge - base.w - base.x));
  const y = clamp(dy, edge - base.y, Math.max(edge - base.y, floor - base.h - base.y));
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
addEventListener("resize", () => windows.forEach((win) => { const [x, y] = offsets.get(win) || [0, 0]; if (x || y) moveWindow(win, x, y); }));
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

const replay = $("#stage-replay"), done = $("#stage-done"), yourTurn = $("#your-turn");
const modeButton = $('[data-action="mode"]', ink.toolbar), pill = $(".ink-pill", ink.toolbar);
let run = null, chartSeen = false, seenWaiters = [], turnTaken = false;
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
// "Your turn" shows once the first round is drawn and whenever the script is idle, until the visitor draws. It
// arrives with the app's Pop.
function offerTurn() {
  if (turnTaken || !yourTurn.hidden) return;
  yourTurn.hidden = false;
  if (!motion) return;
  const pop = poseKeyframes("pop", { appearing: true, annotation: false });
  yourTurn.animate(pop, { duration: pop.duration, easing: pop.easing });
}
function takeTurn() { turnTaken = true; yourTurn.hidden = true; }
ink.on("mark", takeTurn);

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
  offerTurn();
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
    offerTurn();
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
  offerTurn();
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
// route, or one of the two keys. Drawing also brings the stage flat.
function takeOver(flat = true) {
  acted = true;
  stop();
  if (flat && !userFlat) { userFlat = true; request(); }
}
ink.toolbar.addEventListener("pointerdown", (e) => { if (e.isTrusted) takeOver(); }, true);
// The two keys, pressed while the script plays: the script stops and the key does what it says, whatever the
// script was showing. (This runs before the engine hears the key.)
addEventListener("keydown", (e) => {
  if (!e.isTrusted || !scripted()) return;
  const draws = e.ctrlKey && e.shiftKey && e.code === "Digit2";
  if (!draws && e.key !== "Escape") return;
  takeOver(draws);
  if (draws && ink.state.mode === "draw") ink.setMode("use");
}, true);
ink.on("shortcut", () => { if (!scripted()) takeOver(); });
const MODE_WORDS = { use: "Using apps.", draw: "Drawing." };
ink.on("mode", (mode) => {
  desk.dataset.mode = mode;
  apps.inert = mode === "draw";
  setKeysMode(mode);
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
  takeTurn();
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
  status.textContent = "Your turn. Draw on the screen.";
}
for (const link of $$("[data-try]")) link.addEventListener("click", (e) => { e.preventDefault(); tryHere(); });
$(".stage-pulse").addEventListener("animationend", () => desk.classList.remove("is-cued"));

// 2. What you can do -------------------------------------------------------------------------------
// Each card has a small sample screen and a scene the engine draws on it. Marks that name no color take the
// engine's selected gradient, as on the stage.
const measure = document.createElement("canvas").getContext("2d");
const textSize = (shot) => Math.round(clamp(shot.clientWidth * 0.06, 19, 38));
const SCENES = {
  async point({ layer, t }) {
    await layer.perform([
      { tool: "ellipse", around: t("kpi"), padX: 13, padY: 8, size: 3, rough: 6, duration: 760, pause: 260 },
      { tool: "arrow", from: { el: t("kpi"), at: "bottom", dx: -6, dy: 12 }, to: { el: t("dip"), at: "top left", dx: -4, dy: -5 }, bend: 0.34, size: 3, rough: 6, duration: 720 },
    ], { cursor: motion });
  },
  // Smart Draw: a rough box is drawn, then it becomes a neat one.
  async box({ layer, t, box }) {
    const frame = { tool: "rect", around: t("btn"), pad: 9, size: 3 };
    const [rough] = await layer.perform([{ ...frame, rough: 6, duration: 820 }], { cursor: motion });
    await wait(motion ? 300 : 0);
    layer.remove(rough.id);
    await layer.perform([{ ...frame, rough: 0, round: true, duration: 0 }], { cursor: false });
    await wait(motion ? 260 : 0);
    const size = textSize(box.shot), words = "make it bigger";
    measure.font = `600 ${size}px "Caveat"`;
    const note = () => { const b = box("note"); return [b.x + b.w / 2 - measure.measureText(words).width / 2 - 4, b.y]; };
    await layer.perform([
      { tool: "text", at: note, text: words, speed: 46, pause: 160 },
      { tool: "arrow", from: () => { const b = box("note"); return [b.x + b.w / 2, b.y + size * 1.25]; }, to: { el: t("btn"), at: "right", dx: 20 }, bend: -0.3, size: 3, duration: 480 },
    ], { cursor: motion });
  },
  blur: ({ layer, t }) => layer.perform([
    { tool: "blur", over: t("email"), pad: 5, amount: 13, duration: 700, pause: 380 },
    { tool: "blur", over: t("card"), pad: 5, amount: 13, duration: 700 },
  ], { cursor: motion }),
  // Spotlight: the screen dims around the pointer as it goes from one tile to the next.
  async spotlight({ layer, box }) {
    const centre = (name) => { const b = box(name); return [b.x + b.w / 2, b.y + b.h / 2]; };
    const put = (p) => { layer.layer.style.setProperty("--px", `${p[0]}px`); layer.layer.style.setProperty("--py", `${p[1]}px`); layer.placeGhost(p); };
    layer.setStyle("spotlight", { radius: clamp(box.shot.clientWidth * 0.13, 40, 96) });
    let at = centre("s1");
    put(at);
    layer.layer.classList.add("has-pointer");
    layer.setSpotlight(true);
    await wait(motion ? 700 : 0);
    for (const name of ["s2", "s3"]) {
      const from = at, to = centre(name);
      await tween(760, (k) => put([from[0] + (to[0] - from[0]) * k, from[1] + (to[1] - from[1]) * k]));
      at = to;
      await wait(motion ? 620 : 0);
    }
  },
  steps: ({ layer, t }) => layer.perform(["p1", "p2", "p3"].map((pin, i) => ({ tool: "counter", at: { el: t(pin) }, n: i + 1, color: BLUE, pause: 360 })), { cursor: motion }),
  system({ layer, t, box }) {
    // A label sits in the middle of its shape, whatever size the hand-written text is at this width.
    const label = (pin, text, color) => ({ tool: "text", text, color, speed: 50, pause: 120, at: () => {
      const b = box(pin), size = textSize(box.shot);
      measure.font = `600 ${size}px "Caveat"`;
      return [b.x + b.w / 2 - measure.measureText(text).width / 2 - 4, b.y + b.h / 2 - size * 0.56];
    } });
    const link = (a, b) => ({ tool: "arrow", from: { el: t(a), at: "right", dx: 7 }, to: { el: t(b), at: "left", dx: -9 }, bend: 0, color: PURPLE, size: 3, duration: 380, pause: 100 });
    return layer.perform([
      { tool: "rect", around: t("n1"), pad: 0, color: BLUE, size: 3, duration: 520 }, label("n1", "app", BLUE), link("n1", "n2"),
      { tool: "rect", around: t("n2"), pad: 0, color: TEAL, size: 3, duration: 520 }, label("n2", "API", TEAL), link("n2", "n3"),
      { tool: "ellipse", around: t("n3"), pad: 0, color: CORAL, size: 3, duration: 620 }, label("n3", "data", CORAL),
    ], { cursor: motion });
  },
};
function sketch(card, shot) {
  const layer = createInk({ surface: shot, interactive: false });
  const box = (name) => localBox($(`[data-t="${name}"]`, shot), shot);
  box.shot = shot;
  const context = { layer, box, t: (name) => standIn(layer, () => box(name)) };
  const fit = () => layer.layer.style.setProperty("--ink-text", `600 ${textSize(shot)}px/1.1 "Caveat", "Bradley Hand", cursive`);
  const empty = () => { layer.setSpotlight(false); layer.hideGhost(); layer.clear(); };
  let busy = false, shown = false;
  async function play() {
    if (busy) return;
    busy = true;
    empty();
    shot.classList.remove("is-leaving");
    fit();
    await fontsReady;
    await wait(motion ? 260 : 0);
    await SCENES[shot.dataset.scene](context);
    await wait(motion ? 500 : 0);
    if (!layer.state.spotlight) layer.hideGhost();
    layer.reflow();
    busy = false;
    if (!shown && motion) leave();
  }
  // A card that no longer faces front loses its drawing quietly, so it starts clean the next time.
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
  // The pinned deck. Progress through the track turns into one number, 0 for the first card to 5 for the last; each
  // card rests flat for a while around its own number, and eases between.
  const track = $(".do-track"), tabs = $$(".do-tabs button"), rest = 0.2;
  let facing = -1, deckSeen = false;
  const face = (index) => {
    if (index === facing) return;
    facing = index;
    cards.forEach((card, i) => { card.inert = i !== index; (i === index ? visuals[i].show : visuals[i].hide)(); });
    tabs.forEach((tab, i) => tab.setAttribute("aria-current", String(i === index)));
  };
  cards.forEach((card, i) => card.style.setProperty("--i", i));
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
    if (deckSeen) face(Math.round(at));
  });
  new IntersectionObserver(([entry]) => {
    deckSeen = entry.isIntersecting;
    if (deckSeen) face(Math.round(deck.value ?? 0)); else { face(-1); cards.forEach((card) => { card.inert = false; }); }
  }, { threshold: 0.25 }).observe($(".do-deck"));
  tabs.forEach((tab, i) => tab.addEventListener("click", () => {
    const r = track.getBoundingClientRect();
    scrollTo({ top: r.top + scrollY + (r.height - innerHeight) * (i / (cards.length - 1)) + 1, behavior: "smooth" });
  }));
} else {
  // Without motion the cards are a plain grid, and each scene shows its last frame.
  visuals.forEach((visual) => visual.show());
}

// 3. The two keys ----------------------------------------------------------------------------------
const keysSection = $("#keys");
function setKeysMode(mode) { keysSection.dataset.mode = mode; }
const caps = $$(".key");
if (motion) for (const cap of caps) {
  // Each cap is a stack of slices, so it has sides from any angle; a press squeezes the stack.
  const body = document.createElement("span");
  body.className = "key-body";
  for (let k = 0; k <= 8; k++) { const slice = document.createElement("i"); slice.style.setProperty("--k", k); body.append(slice); }
  body.append($("b", cap));
  cap.append(body);
}
const capFor = (e) => caps.find((cap) => cap.dataset.key === e.key || cap.dataset.key === e.code);
addEventListener("keydown", (e) => capFor(e)?.classList.add("is-down"));
addEventListener("keyup", (e) => {
  capFor(e)?.classList.remove("is-down");
  // A key let go while another window had the focus never sends its keyup; letting go of a modifier clears the rest.
  if (!e.ctrlKey && !e.shiftKey) caps.forEach((cap) => cap.classList.remove("is-down"));
});
addEventListener("blur", () => caps.forEach((cap) => cap.classList.remove("is-down")));
for (const set of $$(".keyset")) set.addEventListener("click", async () => {
  const keys = $$(".key", set), fromScript = scripted();
  for (const key of keys) { key.classList.add("is-down"); await wait(motion ? 70 : 0); }
  takeOver();
  // Like the real keys: Control-Shift-2 turns drawing on, or off again; Esc always goes back to the apps.
  ink.setMode(set.dataset.set === "draw" && (fromScript || ink.state.mode !== "draw") ? "draw" : "use");
  await wait(160);
  keys.forEach((key) => key.classList.remove("is-down"));
});

if (motion) {
  // How far an element has come up the screen: 0 as its top edge enters at the bottom, 1 once it is centred.
  const entering = (el) => () => {
    const r = el.getBoundingClientRect(), from = innerHeight, to = Math.max(innerHeight * 0.12, (innerHeight - r.height) / 2);
    return smooth((from - r.top) / (from - to));
  };
  // Where a section is on its way through: -1 just below the screen, 0 in the middle, 1 just above.
  const through = (el) => () => {
    const r = el.getBoundingClientRect();
    return clamp((innerHeight / 2 - (r.top + r.height / 2)) / (innerHeight / 2 + r.height / 2), -1, 1);
  };
  const write = (el, name) => (v) => el.style.setProperty(name, v.toFixed(4));
  channel(entering(keysSection), write(keysSection, "--enter"));
  for (const el of $$("[data-through]")) channel(through(el), write(el, "--through"));
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

// Loops run only while they are on screen.
const live = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    entry.target.classList.toggle("is-live", entry.isIntersecting);
    if (entry.isIntersecting && entry.target === miniStage) cycleMini();
  }
}, { threshold: 0.3 });

// "A toolbar that's yours": a tool is taken off and put back with the app's Pop, then the bar shrinks to its one
// button and grows back with the toolbar's own morph: its two speeds, its curve, and its two faces that cross over.
const mini = $("[data-shrink]"), miniStage = mini.closest(".ill"), miniFace = $(".tb-pill", miniStage), miniTool = $(".tb-move", mini);
function morphMini(collapsing) {
  const steps = 24, gap = (mini.offsetWidth - miniFace.offsetWidth) / 2, list = (make) => Array.from({ length: steps + 1 }, (_, i) => make(i / steps));
  const timing = { duration: (collapsing ? toolbarMorph.collapseDuration : toolbarMorph.expandDuration) * 1000, fill: "forwards" };
  const closed = (t) => (collapsing ? toolbarMorph.shape(t) : 1 - toolbarMorph.shape(t));
  const open = parseFloat(getComputedStyle(mini).borderRadius) || 13, round = (parseFloat(getComputedStyle(miniFace).borderRadius) || 20) - open;
  const leaving = list((t) => ({ opacity: toolbarMorph.outgoingOpacity(t) })), arriving = list((t) => ({ opacity: toolbarMorph.incomingOpacity(t) }));
  mini.animate(list((t) => ({
    clipPath: `inset(-3px ${(gap * closed(t)).toFixed(2)}px round ${(open + round * closed(t)).toFixed(2)}px)`,
    opacity: collapsing ? toolbarMorph.outgoingOpacity(t) : toolbarMorph.incomingOpacity(t),
  })), timing);
  return miniFace.animate(collapsing ? arriving : leaving, timing).finished;
}
let miniCycling = false;
async function cycleMini() {
  if (!motion || miniCycling) return;
  miniCycling = true;
  while (miniStage.classList.contains("is-live")) {
    await wait(1000);
    const off = poseKeyframes("pop", { appearing: false }), on = poseKeyframes("pop", { appearing: true });
    await miniTool.animate(off, { duration: off.duration, easing: off.easing, fill: "forwards" }).finished;
    await wait(800);
    await miniTool.animate(on, { duration: on.duration, easing: on.easing, fill: "forwards" }).finished;
    await wait(1300);
    await morphMini(true);
    await wait(1500);
    await morphMini(false);
    await wait(900);
  }
  miniCycling = false;
}
$$(".ill, .coffee").forEach((el) => live.observe(el));

if (motion) {
  // The tiles under "There's more in the app" arrive the way drawings do when the app shows them in its Magic
  // style: each on its own swooping flight, one after another.
  const grid = $(".also-grid"), tiles = $$(".also-item", grid);
  grid.classList.add("awaits");
  new IntersectionObserver(([entry], observer) => {
    if (!entry.isIntersecting) return;
    observer.disconnect();
    const duration = visibilityDuration("magic", true) * 1000, fade = travelFade(true, true), box = grid.getBoundingClientRect();
    tiles.forEach((tile, i) => {
      const r = tile.getBoundingClientRect();
      // Each starts a little below and nearer the middle of the row, and smaller; the flight does the rest.
      const flight = magicFlight([(box.left + box.width / 2 - (r.left + r.width / 2)) * 0.3, 90, 0.74], [0, 0, 1], `tile ${i}`);
      const timing = { duration, delay: i * 65, easing: flight.easing, fill: "backwards" };
      tile.animate(flight, timing);
      tile.animate(fade, timing);
    });
    grid.classList.remove("awaits");
  }, { rootMargin: "0px 0px -12% 0px", threshold: 0.2 }).observe(grid);

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
