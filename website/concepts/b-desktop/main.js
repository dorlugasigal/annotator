import { createInk } from "/concepts/shared/ink.js";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const reduce = matchMedia("(prefers-reduced-motion: reduce)");
const phone = matchMedia("(max-width: 820px)");
const tall = matchMedia("(max-width: 640px)");
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const pause = (ms) => sleep(reduce.matches ? 0 : ms);
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const lerp = (a, b, t) => a + (b - a) * t;
const two = (n) => String(n).padStart(2, "0");
const onWindows = /win/i.test(navigator.userAgentData?.platform || navigator.platform || "");
function tween(ms, step) {
  if (reduce.matches || ms <= 0) { step(1); return Promise.resolve(); }
  return new Promise((done) => {
    const start = performance.now();
    const frame = (now) => {
      const t = clamp((now - start) / ms, 0, 1);
      step(t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2);
      if (t < 1) requestAnimationFrame(frame); else done();
    };
    requestAnimationFrame(frame);
  });
}
function once(el, fn, threshold = 0.5) {
  const watcher = new IntersectionObserver(([entry]) => {
    if (!entry.isIntersecting) return;
    watcher.disconnect();
    fn();
  }, { threshold });
  watcher.observe(el);
}

const desktop = $("#desktop");
const sky = $(".sky");
const coachEl = $("#coach");
const MENU = 30;

// The day ----------------------------------------------------------------------------------------
// Every section has a time. The menu bar clock and the sky follow the section you are reading.
// Sky rows: minutes since midnight, which sky is showing (0 dawn, 1 day, 2 golden hour, 3 night),
// and where the sun is (percent of the screen, and how visible).
const SKY = [
  [570, 0, 68, 64, 1], [660, 1, 62, 27, 0.9], [790, 1, 50, 14, 0.9], [930, 1, 31, 27, 0.9], [1000, 2, 20, 62, 1],
  [1060, 2, 17, 78, 1], [1125, 3, 14, 106, 0], [1900, 3, 70, 106, 0], [2008, 0, 68, 64, 1],
];
const skyLayers = $$(".sky-layer"), stars = $(".sky-stars"), sun = $(".sky-sun"), clock = $("#clock");
const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
let stops = [], dayFrame = 0;
function measureDay() {
  stops = $$("[data-time]").map((el) => {
    const [h, m] = el.dataset.time.split(":").map(Number), box = el.getBoundingClientRect();
    return { minutes: h * 60 + m, y: box.top + scrollY + Math.min(box.height, innerHeight) / 2 };
  });
  // The last section may be too short to reach the middle of the screen: the day still ends there.
  const last = stops[stops.length - 1], end = document.documentElement.scrollHeight - innerHeight / 2 - 40;
  if (last && last.y > end) last.y = Math.max(end, stops[stops.length - 2].y + 1);
}
function minutesNow() {
  const y = scrollY + innerHeight / 2;
  if (!stops.length || y <= stops[0].y) return stops[0]?.minutes ?? 570;
  for (let i = 1; i < stops.length; i++) {
    if (y <= stops[i].y) return lerp(stops[i - 1].minutes, stops[i].minutes, (y - stops[i - 1].y) / (stops[i].y - stops[i - 1].y));
  }
  return stops[stops.length - 1].minutes;
}
function paintDay() {
  dayFrame = 0;
  const minutes = minutesNow(), whole = Math.round(minutes), time = `${two(Math.floor(whole / 60) % 24)}:${two(whole % 60)}`;
  clock.textContent = phone.matches ? time : `${DAYS[(new Date().getDay() + Math.floor(whole / 1440)) % 7]} ${time}`;
  let i = 1;
  while (i < SKY.length - 1 && minutes > SKY[i][0]) i++;
  const a = SKY[i - 1], b = SKY[i], t = clamp((minutes - a[0]) / (b[0] - a[0]), 0, 1);
  // Two skies cross-fade with the lower one kept solid, so the night behind never shows through.
  const shown = [0, 0, 0, 0];
  if (a[1] === b[1]) shown[a[1]] = 1;
  else if (b[1] > a[1]) { shown[a[1]] = 1; shown[b[1]] = t; }
  else { shown[b[1]] = 1; shown[a[1]] = 1 - t; }
  skyLayers.forEach((layer, n) => { layer.style.opacity = shown[n].toFixed(3); });
  stars.style.opacity = Math.max(0.5 * shown[0] * (1 - shown[1]), shown[3]).toFixed(3);
  sun.style.left = `${lerp(a[2], b[2], t).toFixed(2)}%`;
  sun.style.top = `${lerp(a[3], b[3], t).toFixed(2)}%`;
  sun.style.opacity = lerp(a[4], b[4], t).toFixed(3);
}
const paintSoon = () => { if (!dayFrame) dayFrame = requestAnimationFrame(paintDay); };
addEventListener("scroll", paintSoon, { passive: true });

// 09:30, the desktop: windows -------------------------------------------------------------------
// Positions are designed at one viewport per layout and stored as fractions of the free space, so
// windows spread out on a bigger desktop and overlap a little more on a smaller one.
const LAYOUTS = {
  wide: { box: { x: 84, y: 40, w: 1344, h: 750 }, at: { welcome: [112, 52], numbers: [652, 84], call: [1066, 492], today: [1212, 58] } },
  short: { box: { x: 84, y: 40, w: 1344, h: 630 }, at: { welcome: [126, 48], numbers: [584, 50], call: [960, 356], today: [1130, 54] } },
  roomy: { box: { x: 84, y: 40, w: 928, h: 710 }, at: { welcome: [92, 48], numbers: [522, 62], today: [536, 428], call: [744, 458] } },
  compact: { box: { x: 84, y: 40, w: 928, h: 618 }, at: { welcome: [92, 44], numbers: [522, 58], call: [738, 438], today: [806, 110] }, closed: ["today"] },
};
const STACK = ["numbers", "today", "call", "welcome"];
const wins = new Map();
let order = [], layout = LAYOUTS.wide, zoom = 1;

$$(".win[data-win]", desktop).forEach((el, i) => {
  const id = el.dataset.win, title = $(".win-title", el).textContent;
  el.style.setProperty("--i", i);
  const dots = $(".win-dots", el);
  dots.removeAttribute("aria-hidden");
  dots.insertAdjacentHTML("afterbegin",
    `<button type="button" class="dot dot-close" data-act="close" aria-label="Close ${title}"></button>` +
    `<button type="button" class="dot dot-min" data-act="min" aria-label="Minimise ${title}"></button>`);
  $$("i", dots).forEach((dot) => dot.setAttribute("aria-hidden", "true"));
  wins.set(id, { id, el, x: 0, y: 0, visible: true, open: true, moved: false, icon: $(`.dock-item[data-open="${id}"]`) });
});

const size = () => ({ W: desktop.clientWidth, H: desktop.clientHeight });
// On a small desktop the windows also shrink a little (never below 80%), so they keep their places.
function place(w, x, y) {
  const { W, H } = size(), width = w.el.offsetWidth * zoom;
  w.x = Math.round(clamp(x, -width * 0.6, W - width * 0.4));
  w.y = Math.round(clamp(y, MENU + 4, Math.max(MENU + 4, H - 140)));
  w.el.style.setProperty("--x", `${w.x - (w.el.offsetWidth * (1 - zoom)) / 2}px`);
  w.el.style.setProperty("--y", `${w.y - (w.el.offsetHeight * (1 - zoom)) / 2}px`);
}
// The strip at the bottom belongs to the toolbar, however tall it is right now.
function freeArea({ W, H }) {
  const bar = ink.collapsed ? 44 : ink.toolbarRect().height || 72;
  return { x: 84, y: 40, w: W - 96, h: H - 40 - Math.max(110, bar + 38) };
}
function home(w) {
  const { box, at } = layout, [x, y] = at[w.id], free = freeArea(size());
  const width = w.el.offsetWidth * zoom, height = w.el.offsetHeight * zoom;
  const spanW = Math.min(free.w, box.w * zoom * 1.2), spanH = Math.min(free.h, box.h * zoom * 1.16);
  const left = free.x + (free.w - spanW) / 2, top = free.y + (free.h - spanH) * 0.4;
  const fx = (x - box.x) / Math.max(1, box.w - w.el.offsetWidth), fy = (y - box.y) / Math.max(1, box.h - w.el.offsetHeight);
  return [left + fx * (spanW - width), Math.max(MENU + 8, top + fy * (spanH - height))];
}
function focusWin(w) {
  order = order.filter((other) => other !== w);
  order.push(w);
  order.forEach((other, i) => {
    other.el.style.zIndex = 10 + i;
    other.el.classList.toggle("is-front", other === w);
  });
}
const syncDock = () => wins.forEach((w) => w.icon?.classList.toggle("is-open", w.open));
// A window goes to its dock icon and comes back from it.
function flight(w, squash) {
  const r = w.el.getBoundingClientRect(), d = w.icon.getBoundingClientRect();
  const dx = (d.left + d.width / 2 - (r.left + r.width / 2)) / zoom, dy = (d.top + d.height / 2 - (r.top + r.height / 2)) / zoom;
  const end = { transform: `translate(${dx}px, ${dy}px) scale(.05)`, opacity: 0 };
  return squash
    ? [{ transform: "none", opacity: 1 }, { transform: `translate(${dx * 0.35}px, ${dy * 0.5}px) scale(.62, .3)`, opacity: 0.9, offset: 0.55 }, end]
    : [{ transform: "none", opacity: 1 }, end];
}
function fly(w, frames, options) {
  if (reduce.matches || !w.icon) return Promise.resolve();
  return w.el.animate(frames, options).finished.catch(() => {});
}
async function hideWin(w, kind) {
  if (!w.visible || phone.matches) return;
  w.visible = false;
  w.open = kind === "min";
  releaseMarks();
  syncDock();
  await fly(w, flight(w, kind === "min"), { duration: kind === "min" ? 520 : 400, easing: "cubic-bezier(.5, 0, .8, .45)" });
  if (w.visible) return;
  w.el.hidden = true;
  w.icon?.classList.add("is-landing");
  setTimeout(() => w.icon?.classList.remove("is-landing"), 450);
  const next = [...order].reverse().find((other) => other.visible);
  if (next) focusWin(next);
}
function showWin(w) {
  if (w.visible) {
    focusWin(w);
    if (!reduce.matches) w.el.animate([{ transform: "none" }, { transform: "scale(1.025)" }, { transform: "none" }], { duration: 260, easing: "ease-out" });
    return;
  }
  w.visible = w.open = true;
  w.el.hidden = false;
  if (!w.moved) place(w, ...home(w));
  focusWin(w);
  syncDock();
  fly(w, flight(w).reverse(), { duration: 460, easing: "cubic-bezier(.2, .9, .25, 1)" });
}
function arrange({ reset = false } = {}) {
  if (phone.matches) {
    desktop.classList.remove("is-compact");
    wins.forEach((w) => { w.el.hidden = false; w.visible = w.open = true; w.moved = false; });
    return;
  }
  const { W, H } = size();
  const name = W >= 1336 ? (H >= 860 ? "wide" : "short") : H >= 840 ? "roomy" : "compact";
  layout = LAYOUTS[name];
  desktop.classList.toggle("is-compact", name !== "wide");
  desktop.classList.toggle("is-tight", name === "compact");
  const free = freeArea({ W, H });
  zoom = clamp(Math.min(free.w / layout.box.w, free.h / layout.box.h), 0.8, 1);
  desktop.style.setProperty("--zoom", zoom.toFixed(3));
  wins.forEach((w) => {
    if (reset) {
      w.moved = false;
      // On the smallest desktop the agenda starts in the dock, so nothing sits under the toolbar.
      const wanted = !layout.closed?.includes(w.id);
      if (!wanted) { w.visible = w.open = false; w.el.hidden = true; }
      else if (!w.visible) { w.visible = w.open = true; w.el.hidden = false; fly(w, [{ opacity: 0 }, { opacity: 1 }], { duration: 300 }); }
    }
    if (!w.visible) return;
    if (w.moved) place(w, w.x, w.y); else place(w, ...home(w));
  });
  if (reset) STACK.forEach((id) => focusWin(wins.get(id)));
  syncDock();
}

desktop.addEventListener("pointerdown", (e) => {
  const el = e.target.closest?.(".win[data-win]");
  if (!el) return;
  const w = wins.get(el.dataset.win), bar = e.target.closest("[data-drag]");
  if (!phone.matches) focusWin(w);
  if (!bar || e.button !== 0 || phone.matches || e.target.closest("button, a")) return;
  e.preventDefault();
  const start = { px: e.clientX, py: e.clientY, x: w.x, y: w.y };
  let moved = false;
  bar.setPointerCapture(e.pointerId);
  el.classList.add("is-dragging");
  const move = (event) => {
    const dx = event.clientX - start.px, dy = event.clientY - start.py;
    if (!moved && Math.hypot(dx, dy) < 3) return;
    if (!moved) { moved = true; w.moved = true; releaseMarks(); }
    place(w, start.x + dx, start.y + dy);
  };
  const end = () => {
    bar.removeEventListener("pointermove", move);
    bar.removeEventListener("pointerup", end);
    bar.removeEventListener("pointercancel", end);
    el.classList.remove("is-dragging");
    if (moved) windowMoved();
  };
  bar.addEventListener("pointermove", move);
  bar.addEventListener("pointerup", end);
  bar.addEventListener("pointercancel", end);
});
desktop.addEventListener("focusin", (e) => {
  const el = e.target.closest?.(".win[data-win]");
  if (el && !phone.matches) focusWin(wins.get(el.dataset.win));
});
desktop.addEventListener("click", (e) => {
  const act = e.target.closest?.("[data-act]");
  if (act) return hideWin(wins.get(act.closest(".win").dataset.win), act.dataset.act);
  const open = e.target.closest?.("[data-open]");
  if (open) return showWin(wins.get(open.dataset.open));
  if (e.target.closest?.("[data-draw]")) startDrawing();
});

// The desktop's drawing engine, with the app's toolbar --------------------------------------------
const ink = createInk({
  surface: desktop, toolbarParent: desktop, toolbarPosition: "absolute", appearance: "dark", properties: false,
  collapsed: phone.matches, useLabel: "Use apps", pillLabel: "Draw here",
});
const menuInk = $("#menu-ink");
function startDrawing() {
  ink.setEnabled(true);
  ink.setCollapsed(false);
  ink.setMode("draw");
}
// Marks belong to the screen, not to a window. Until a window first moves they still follow their
// targets, so resizing the browser keeps the intro tidy.
function releaseMarks() {
  ink.marks().forEach((m) => { m.anchor = null; });
}
ink.on("mode", (mode) => {
  menuInk.setAttribute("aria-pressed", String(mode === "draw"));
  if (mode === "draw" && ink.collapsed) ink.setCollapsed(false);
  if (story === "esc" && mode === "use") { story = "drag"; coach("drag"); }
  else if (story === "drag" && mode === "draw") coach(null);
});
let barHeight = 0;
ink.on("layout", (bar) => {
  if (bar.height === barHeight) return;
  barHeight = bar.height;
  arrange();
  if (coachEl.classList.contains("is-on")) placeCoach();
});
menuInk.addEventListener("click", () => {
  if (scrollY > 60) scrollTo({ top: 0 });
  if (ink.state.mode === "draw") ink.setMode("use"); else startDrawing();
});
let introWaiting = false;
new IntersectionObserver(([entry]) => {
  const away = phone.matches ? !entry.isIntersecting : entry.intersectionRatio < 0.3;
  ink.setEnabled(!away);
  if (away) coach(null);
  if (introWaiting && !phone.matches && entry.intersectionRatio > 0.9) { introWaiting = false; setTimeout(intro, 400); }
}, { threshold: [0, 0.3, 0.92] }).observe(desktop);

// A plain click in draw mode does nothing in the app either. Say so, and let the main button
// through so nobody gets stuck on the way to the download.
let tap = null;
ink.layer.addEventListener("pointerdown", (e) => {
  tap = ink.state.mode === "draw" ? { x: e.clientX, y: e.clientY, at: performance.now() } : null;
});
ink.layer.addEventListener("pointerup", (e) => {
  const t = tap;
  tap = null;
  if (!t || Math.hypot(e.clientX - t.x, e.clientY - t.y) > 5 || performance.now() - t.at > 600) return;
  // After a shape the tool is Select, as in the app: a tap that picked a mark is an edit, not a lost click.
  if (ink.state.tool === "text" || (["select", "eraser"].includes(ink.state.tool) && ink.state.selected)) return;
  const below = document.elementsFromPoint(e.clientX, e.clientY).find((el) => !el.closest(".ink-layer"));
  const target = below?.closest("a, button, [data-drag]");
  if (!target || !desktop.contains(target)) return;
  if (target.matches("[data-through]")) { ink.setMode("use"); target.click(); }
  else coach("nudge", 4200);
});

// Coach line ------------------------------------------------------------------------------------
const COACH = {
  intro: "Now press <kbd>Esc</kbd> and drag a window. Your drawing stays on the screen.",
  drag: "Now drag a window by its title bar.",
  stayed: "The window moved. Your drawing stayed.",
  nudge: "You are drawing right now. Press <kbd>Esc</kbd> to use the desktop.",
};
let story = "idle", coachTimer = 0, coachNow = null;
function coach(name, ttl = 0) {
  clearTimeout(coachTimer);
  if (!name) { coachEl.classList.remove("is-on"); coachNow = null; return; }
  const previous = name === "nudge" ? coachNow : null;
  if (name !== "nudge") coachNow = name;
  coachEl.innerHTML = COACH[name];
  coachEl.classList.toggle("is-nudge", name === "nudge");
  placeCoach();
  coachEl.classList.add("is-on");
  if (ttl) coachTimer = setTimeout(() => coach(previous && previous !== "stayed" ? previous : null), ttl);
}
// The line sits just above the toolbar, wherever the toolbar is and however tall it is.
function placeCoach() {
  const bar = ink.toolbarRect(), d = desktop.getBoundingClientRect();
  if (phone.matches || !bar.width) { coachEl.style.left = coachEl.style.bottom = ""; return; }
  const half = coachEl.offsetWidth / 2 + 10, above = bar.top - d.top > 150;
  coachEl.style.left = `${clamp(bar.left + bar.width / 2 - d.left, half, d.width - half)}px`;
  coachEl.style.bottom = `${above ? d.bottom - bar.top + 14 : d.bottom - bar.bottom - 14 - coachEl.offsetHeight}px`;
  coachEl.classList.toggle("is-below", !above);
}
function windowMoved() {
  if (story !== "esc" && story !== "drag") return;
  story = "done";
  if (ink.marks().length) coach("stayed", 5200); else coach(null);
}

// The 9:30 script: first the wiggle everyone knows, then the ring ----------------------------------
const fig = $("#fig-activation"), dip = $("#dip"), chat = $$("#call-chat li");
const say = (n) => chat.forEach((line, i) => line.classList.toggle("is-on", i < n));
const onScreen = (el) => {
  const r = el.getBoundingClientRect(), d = desktop.getBoundingClientRect();
  return r.width > 0 && r.top >= Math.max(0, d.top) + MENU && r.bottom <= Math.min(innerHeight, d.bottom) - 8 && r.left >= 0 && r.right <= innerWidth;
};
async function wiggle(signal) {
  if (reduce.matches) return;
  const r = ink.rectOf(fig), x = r.x + r.w / 2, y = r.y + r.h / 2;
  for (const [dx, dy, line] of [[-96, 62], [-64, 84], [-108, 50, 2], [-72, 72], [34, 46], [8, 66], [46, 34], [-6, 30]]) {
    if (signal.aborted) return;
    if (line) say(line);
    await ink.moveGhost([x + dx, y + dy], 250);
  }
}
const numbersScene = (signal) => [
  { run: () => say(1) },
  { wait: 500 },
  { run: () => wiggle(signal) },
  { run: () => say(2) },
  { tool: "ellipse", around: fig, padX: 12, padY: 4, duration: 720, pause: 140 },
  { tool: "arrow", from: { el: fig, at: "bottom left", dx: 2, dy: 17 }, to: { el: dip, at: "top", dy: -9 }, bend: -0.3, duration: 560, pause: 120 },
  { tool: "text", at: { el: dip, at: "right", dx: 20, dy: -12 }, text: "why the dip?", pause: 240 },
  { run: () => say(3) },
];
let running = null, queue = Promise.resolve();
function perform(steps, signal) {
  queue = queue.then(async () => {
    if (signal.aborted || !steps.length) return;
    await ink.perform(steps, { cursor: !reduce.matches, showTool: true, signal });
    setTimeout(() => { if (!signal.aborted) ink.hideGhost(); }, 500);
  }).catch(() => {});
  return queue;
}
function intro() {
  const controller = new AbortController(), { signal } = controller;
  const ready = wins.get("numbers").visible && onScreen(fig) && onScreen(dip);
  running = controller;
  // Nothing to point at yet (the page was opened further down): wait until the desktop is back.
  introWaiting = !ready && wins.get("numbers").visible;
  if (!ready) return;
  startDrawing();
  perform(numbersScene(signal), signal).then(() => {
    if (signal.aborted) return;
    story = "esc";
    coach("intro");
  });
}
// On a phone the windows are a stack, so the scene plays when the numbers card is in view.
let phoneWatcher = null;
function phoneIntro() {
  running = new AbortController();
  const { signal } = running;
  phoneWatcher?.disconnect();
  phoneWatcher = new IntersectionObserver(([entry]) => {
    if (!entry.isIntersecting || !phone.matches) return;
    phoneWatcher.disconnect();
    perform(numbersScene(signal).filter((step) => step.tool), signal).then(() => { if (!signal.aborted) say(3); });
  }, { threshold: 0.98 });
  phoneWatcher.observe(wins.get("numbers").el);
}
async function resetDesktop() {
  running?.abort();
  await queue;
  ink.hideGhost();
  ink.setMode("use");
  ink.clear();
  ink.setTool("pen", true);
  coach(null);
  say(0);
  story = "idle";
  if (scrollY > 60) scrollTo({ top: 0 });
  if (phone.matches) return phoneIntro();
  desktop.classList.add("is-homing");
  arrange({ reset: true });
  await pause(700);
  desktop.classList.remove("is-homing");
  intro();
}
$("#reset").addEventListener("click", resetDesktop);

// Wallpaper parallax and dock -------------------------------------------------------------------
const drift = { x: 0, y: 0, tx: 0, ty: 0, frame: 0 };
function driftStep() {
  drift.x += (drift.tx - drift.x) * 0.07;
  drift.y += (drift.ty - drift.y) * 0.07;
  sky.style.setProperty("--wx", drift.x.toFixed(3));
  sky.style.setProperty("--wy", drift.y.toFixed(3));
  drift.frame = Math.abs(drift.tx - drift.x) + Math.abs(drift.ty - drift.y) > 0.003 ? requestAnimationFrame(driftStep) : 0;
}
desktop.addEventListener("pointermove", (e) => {
  if (reduce.matches || phone.matches || e.pointerType === "touch") return;
  drift.tx = (e.clientX / innerWidth) * 2 - 1;
  drift.ty = (e.clientY / innerHeight) * 2 - 1;
  if (!drift.frame) drift.frame = requestAnimationFrame(driftStep);
});
const dock = $(".dock"), dockItems = $$(".dock-item", dock);
dock.addEventListener("pointermove", (e) => {
  if (reduce.matches || e.pointerType === "touch") return;
  const top = dock.getBoundingClientRect().top;
  for (const item of dockItems) {
    const middle = item.offsetTop + item.offsetHeight / 2 + top;
    item.style.setProperty("--s", (1 + 0.3 * Math.max(0, 1 - Math.abs(e.clientY - middle) / 92)).toFixed(3));
  }
});
dock.addEventListener("pointerleave", () => dockItems.forEach((item) => item.style.removeProperty("--s")));

// Scripted scenes on the sample screens ------------------------------------------------------------
// Each one has its own engine with no toolbar. It plays when it scrolls into view and on Replay.
function makeScene(root, { run, before, ...options }) {
  const scene = createInk({ surface: $(".screen", root), interactive: false, size: 4, ...options });
  let controller = null, job = Promise.resolve();
  const play = () => {
    controller?.abort();
    const mine = controller = new AbortController();
    job = job.then(async () => {
      if (mine.signal.aborted) return;
      scene.hideGhost();
      scene.clear();
      before?.(scene);
      await pause(320);
      if (mine.signal.aborted) return;
      await run(scene, { cursor: !reduce.matches, signal: mine.signal }, mine.signal);
      setTimeout(() => { if (!mine.signal.aborted) scene.hideGhost(); }, 500);
    }).catch(() => {});
    return job;
  };
  $("[data-replay]", root)?.addEventListener("click", play);
  once(root, play, 0.55);
  return { ink: scene, play };
}

// 09:31, the wiggle: the same screen without and with Annotator
const wig = $("#wiggle-scene"), wigScreen = $(".screen", wig), wigSwitch = $("#wiggle-switch"), wigSay = $("#wiggle-say"), wigTarget = $("#wig-target");
const wigInk = createInk({ surface: wigScreen, interactive: false, size: 4 });
const SAY = { without: ["“This one…", " no, up…", " the red one.”"], with: ["“This one.”"] };
let wigTouched = false, wigTimer = 0, wigControl = null, wigJob = Promise.resolve();
function aimTiny() {
  const r = wigInk.rectOf(wigTarget);
  wigScreen.style.setProperty("--tx", `${Math.round(r.x + r.w / 2)}px`);
  wigScreen.style.setProperty("--ty", `${Math.round(r.y + r.h / 2)}px`);
}
function setSide(side) {
  wigControl?.abort();
  const mine = wigControl = new AbortController();
  clearInterval(wigTimer);
  aimTiny();
  wig.dataset.side = wigSwitch.dataset.side = side;
  $$("button", wigSwitch).forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.side === side)));
  wigSay.innerHTML = SAY[side].map((part) => `<i>${part}</i>`).join("");
  const parts = $$("i", wigSay);
  if (side === "without") {
    wigJob = wigJob.then(() => { wigInk.hideGhost(); wigInk.clear(); });
    let phase = 0;
    const speak = () => parts.forEach((part, i) => part.classList.toggle("is-said", reduce.matches || i <= phase % 3));
    speak();
    if (!reduce.matches) wigTimer = setInterval(() => { phase++; speak(); }, 1200);
    return;
  }
  parts[0].classList.add("is-said");
  wigJob = wigJob.then(async () => {
    if (mine.signal.aborted) return;
    wigInk.clear();
    await pause(300);
    // The label goes to the right of the pill when the screen has room for it.
    const pill = wigInk.rectOf(wigTarget), room = wigScreen.clientWidth - pill.x - pill.w;
    await wigInk.perform([
      { tool: "ellipse", around: wigTarget, padX: 10, padY: 7, duration: 650, pause: 120 },
      ...(room < 80
        ? [{ tool: "arrow", from: { el: wigTarget, at: "bottom left", dx: -62, dy: 40 }, to: { el: wigTarget, at: "bottom left", dx: -9, dy: 5 }, bend: 0.22, duration: 420 }]
        : [{ tool: "arrow", from: { el: wigTarget, at: "right", dx: 64, dy: -2 }, to: { el: wigTarget, at: "right", dx: 16 }, bend: 0.14, duration: 420, pause: 100 },
          ...(room < 166 ? [] : [{ tool: "text", at: { el: wigTarget, at: "right", dx: 68, dy: -20 }, text: "this one", pause: 200 }])]),
    ], { cursor: !reduce.matches, signal: mine.signal });
    setTimeout(() => { if (!mine.signal.aborted) wigInk.hideGhost(); }, 500);
  }).catch(() => {});
}
wigSwitch.addEventListener("click", (e) => {
  const button = e.target.closest("button");
  if (!button) return;
  wigTouched = true;
  setSide(button.dataset.side);
});
once(wig, () => {
  setSide(reduce.matches ? "with" : "without");
  if (!reduce.matches) setTimeout(() => { if (!wigTouched) setSide("with"); }, 4300);
}, 0.6);

// 09:45, three beats
const beatToggle = $("#beat-toggle");
const beatDraw = makeScene($('[data-beat="draw"]'), {
  run: (scene, options) => scene.perform([{ tool: "ellipse", around: $("#beat-word"), padX: 10, padY: 6, duration: 700 }], options),
});
const beatUse = makeScene($('[data-beat="use"]'), {
  before: () => beatToggle.classList.remove("is-on"),
  run: async (scene, options, signal) => {
    await scene.perform([{ tool: "ellipse", around: beatToggle, padX: 12, padY: 10, duration: 600, pause: 300 }], options);
    for (let click = 0; click < 3 && !signal.aborted; click++) {
      const r = scene.rectOf(beatToggle);
      if (options.cursor) await scene.moveGhost([r.x + r.w / 2 + 3, r.y + r.h / 2 + 3], 300);
      beatToggle.classList.toggle("is-on");
      await pause(650);
    }
  },
});
makeScene($('[data-beat="edit"]'), {
  run: async (scene, options, signal) => {
    const [arrow] = await scene.perform([
      { tool: "arrow", from: { el: $("#beat-a"), at: "right", dx: 8 }, to: { el: $("#beat-b"), at: "left", dx: -8 }, bend: 0, duration: 500, pause: 300 },
    ], { ...options, anchored: false });
    if (signal.aborted || !arrow) return;
    scene.select(arrow.id);
    const target = scene.rectOf($("#beat-c")), from = [arrow.x2, arrow.y2], to = [target.x - 8, target.y + target.h / 2];
    await pause(350);
    await tween(700, (k) => {
      arrow.x2 = lerp(from[0], to[0], k);
      arrow.y2 = lerp(from[1], to[1], k);
      arrow.cx = (arrow.x1 + arrow.x2) / 2;
      arrow.cy = (arrow.y1 + arrow.y2) / 2;
      scene.render(arrow);
    });
    await pause(300);
    const middle = [arrow.cx, arrow.cy];
    await tween(600, (k) => {
      arrow.cx = middle[0] - 30 * k;
      arrow.cy = middle[1] + 46 * k;
      scene.render(arrow);
    });
    await pause(300);
    arrow.color = "#2f7cf6";
    scene.render(arrow);
    await pause(600);
    scene.select(null);
  },
});

// 11:00 code review
makeScene($('[data-scene="review"]'), {
  color: "#ffd60a",
  run: (scene, options) => scene.perform([
    { tool: "rect", around: $("#rev-line"), padX: 8, padY: 1, duration: 700, pause: 150 },
    { tool: "text", at: { el: $("#rev-line"), at: "right", dx: 26, dy: -18 }, text: "twice?", pause: 320 },
    { tool: "blur", over: $("#rev-token"), pad: 4, duration: 650 },
  ], options),
});

// 13:15 support call
makeScene($('[data-scene="support"]'), {
  run: (scene, options) => scene.perform([
    { tool: "counter", at: { el: $("#sup-1"), at: "right", dx: -6 }, pause: 260 },
    { tool: "counter", at: { el: $("#sup-2"), at: "left", dx: -26 }, pause: 260 },
    { tool: "counter", at: { el: $("#sup-3"), at: "top right", dx: 2, dy: -4 }, pause: 200 },
    { tool: "arrow", from: { el: $("#sup-3"), at: "top left", dx: -110, dy: -50 }, to: { el: $("#sup-3"), at: "top", dx: -22, dy: -17 }, bend: -0.24, duration: 500 },
  ], options),
});

// 14:00 workshop: the same marks on the desktop, a whiteboard or a blackboard
const slide = $(".slide"), boards = $("#boards");
makeScene($('[data-scene="workshop"]'), {
  run: async (scene, options) => {
    setBoard("white");
    await pause(400);
    const line = (el, text) => [
      { tool: "counter", at: { el, at: "left", dx: -26 }, pause: 80 },
      { tool: "text", at: { el, at: "top left" }, text, speed: 24, pause: 180 },
    ];
    await scene.perform([
      { tool: "text", at: { el: $("#ws-title"), at: "top left" }, text: "How we ship", color: "#2f7cf6", speed: 26, pause: 220 },
      ...line($("#ws-1"), "Sketch the idea"), ...line($("#ws-2"), "Build the small one"), ...line($("#ws-3"), "Show it on Friday"),
      { tool: "highlight", over: $("#ws-3"), color: "#ffe14d", duration: 600 },
    ], options);
  },
});
// The board is the page's own, under the ink: the engine's board would cover its highlighter strokes.
function setBoard(name) {
  slide.dataset.board = name;
  $$("button", boards).forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.board === name)));
}
boards.addEventListener("click", (e) => { const button = e.target.closest("button"); if (button) setBoard(button.dataset.board); });

// 12:30 break timer ------------------------------------------------------------------------------
const timer = $("#timer"), timerCount = $("#timer-count");
let timerLeft = 600, timerTick = 0;
const showTimer = () => { timerCount.textContent = `${two(Math.floor(timerLeft / 60))}:${two(timerLeft % 60)}`; };
new IntersectionObserver(([entry]) => {
  clearInterval(timerTick);
  if (!entry.isIntersecting || reduce.matches) return;
  timerTick = setInterval(() => { timerLeft = timerLeft > 0 ? timerLeft - 1 : 600; showTimer(); }, 1000);
}, { threshold: 0.3 }).observe(timer);
$(".palettes", timer.parentElement).addEventListener("click", (e) => {
  const button = e.target.closest("button");
  if (!button) return;
  timer.dataset.palette = button.dataset.palette;
  $$("button", button.parentElement).forEach((b) => b.setAttribute("aria-pressed", String(b === button)));
});

// Clips recorded in the Mac app --------------------------------------------------------------------
$$(".player").forEach((player) => {
  const video = $("video", player), frame = $(".player-frame", player), playButton = $(".player-play", player);
  const ids = player.dataset.clips.split(","), tabs = $$("[data-clip]", player.closest(".win, .sticky"));
  let index = 0, seen = false, loaded = false;
  function load(i, play = !reduce.matches) {
    index = (i + ids.length) % ids.length;
    const base = `/public/demo/stage-${ids[index]}${tall.matches ? "-phone" : ""}`;
    frame.classList.toggle("is-tall", tall.matches);
    video.poster = `${base}.jpg`;
    video.src = `${base}.mp4`;
    video.loop = ids.length === 1 && !reduce.matches;
    loaded = true;
    tabs.forEach((tab) => tab.setAttribute("aria-pressed", String(tab.dataset.clip === ids[index])));
    playButton.hidden = play;
    if (play && seen) video.play().catch(() => {});
  }
  tabs.forEach((tab) => tab.addEventListener("click", () => load(ids.indexOf(tab.dataset.clip))));
  video.addEventListener("play", () => { playButton.hidden = true; });
  video.addEventListener("ended", () => { if (reduce.matches) playButton.hidden = false; else load(index + 1); });
  playButton.addEventListener("click", () => video.play().catch(() => {}));
  new IntersectionObserver(([entry]) => {
    seen = entry.isIntersecting;
    if (!seen) return video.pause();
    if (!loaded) load(0); else if (!reduce.matches) video.play().catch(() => {});
  }, { threshold: 0.25 }).observe(frame);
  if (reduce.matches) playButton.hidden = false;
});
$$(".thumb").forEach((button) => {
  const video = $("video", button), name = $("span", button).textContent;
  const start = () => {
    if (!video.getAttribute("src")) video.src = `/public/demo/stage-${button.dataset.thumb}.mp4`;
    video.play().catch(() => {});
  };
  button.setAttribute("aria-label", `Play the ${name} clip`);
  button.addEventListener("pointerenter", (e) => { if (e.pointerType === "mouse" && !reduce.matches) start(); });
  button.addEventListener("pointerleave", (e) => { if (e.pointerType === "mouse") video.pause(); });
  button.addEventListener("click", () => { if (video.paused) start(); else video.pause(); });
});

// 17:30, your turn: a second screen to draw on, with the real app playing in a window ----------------
const playground = $("#playground");
const playInk = createInk({
  surface: playground, toolbarParent: playground, toolbarPosition: "absolute", appearance: "dark", enabled: false,
  collapsed: phone.matches, useLabel: "Use apps", pillLabel: "Draw here",
});
const reel = $("#reel-video"), reelPlay = $("#reel-play"), reelSound = $("#reel-sound");
let reelWanted = !reduce.matches;
function syncReel() {
  reelPlay.classList.toggle("is-playing", !reel.paused);
  reelPlay.setAttribute("aria-label", reel.paused ? "Play the reel" : "Pause the reel");
}
reel.addEventListener("play", syncReel);
reel.addEventListener("pause", syncReel);
reel.addEventListener("timeupdate", () => {
  $("#reel-progress").style.setProperty("--p", reel.duration ? reel.currentTime / reel.duration : 0);
  $("#reel-time").textContent = `${Math.floor(reel.currentTime / 60)}:${two(Math.floor(reel.currentTime % 60))}`;
});
reelPlay.addEventListener("click", () => {
  reelWanted = reel.paused;
  if (reel.paused) reel.play().catch(() => {}); else reel.pause();
});
reelSound.addEventListener("click", () => {
  reel.muted = !reel.muted;
  reelSound.setAttribute("aria-pressed", String(!reel.muted));
});
new IntersectionObserver(([entry]) => {
  const here = entry.intersectionRatio >= 0.3;
  playInk.setEnabled(here);
  if (here && reelWanted) reel.play().catch(() => {}); else reel.pause();
}, { threshold: [0, 0.3] }).observe(playground);

// Keys: the real ones light the keycaps and play the first two beats ----------------------------------
const caps = (name) => $$(`kbd[data-key="${name}"]`);
const capFor = (e) => (e.key === "Control" ? "ctrl" : e.key === "Shift" ? "shift" : e.code === "Digit2" ? "2" : e.key === "Escape" ? "esc" : null);
let howSeen = false;
new IntersectionObserver(([entry]) => { howSeen = entry.isIntersecting; }, { threshold: 0.2 }).observe($("#how"));
addEventListener("keydown", (e) => {
  const name = capFor(e);
  if (name) caps(name).forEach((k) => k.classList.add("is-down"));
  if (!howSeen || e.repeat) return;
  if (e.ctrlKey && e.shiftKey && e.code === "Digit2") beatDraw.play();
  else if (e.key === "Escape") beatUse.play();
});
addEventListener("keyup", (e) => { const name = capFor(e); if (name) caps(name).forEach((k) => k.classList.remove("is-down")); });
addEventListener("blur", () => $$("kbd.is-down").forEach((k) => k.classList.remove("is-down")));
if (onWindows) $$("kbd[data-win-label]").forEach((k) => {
  k.textContent = k.dataset.winLabel;
  k.classList.remove("is-symbol", "is-combo");
  k.classList.add("is-word");
});

// Reveals, install switch, copy -------------------------------------------------------------------
const revealer = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) { entry.target.classList.add("is-in"); revealer.unobserve(entry.target); }
}, { threshold: 0.12, rootMargin: "0px 0px -6% 0px" });
$$(".reveal").forEach((el) => revealer.observe(el));

const osSwitch = $("#os-switch");
function chooseOS(os) {
  osSwitch.dataset.os = os;
  $$("button", osSwitch).forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.os === os)));
  $$(".os-panel").forEach((panel) => { panel.hidden = panel.dataset.panel !== os; });
}
osSwitch.addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) chooseOS(b.dataset.os); });
chooseOS(onWindows ? "windows" : "mac");

$$(".copy").forEach((button) => button.addEventListener("click", async () => {
  const text = $(`#${button.dataset.copy}`).textContent;
  try { await navigator.clipboard.writeText(text); }
  catch {
    const range = document.createRange();
    range.selectNodeContents($(`#${button.dataset.copy}`));
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    document.execCommand("copy");
  }
  button.textContent = "Copied";
  button.classList.add("is-done");
  setTimeout(() => { button.textContent = "Copy"; button.classList.remove("is-done"); }, 1600);
}));

// Start -----------------------------------------------------------------------------------------
let resizeFrame = 0;
addEventListener("resize", () => {
  cancelAnimationFrame(resizeFrame);
  resizeFrame = requestAnimationFrame(() => {
    arrange();
    measureDay();
    paintDay();
    aimTiny();
    if (coachEl.classList.contains("is-on")) placeCoach();
  });
});
new ResizeObserver(() => { measureDay(); paintSoon(); }).observe($("main"));
phone.addEventListener("change", () => location.reload());
(async () => {
  await Promise.race([document.fonts.ready, sleep(1200)]);
  arrange({ reset: true });
  measureDay();
  paintDay();
  desktop.classList.add("is-ready", "is-booting");
  setTimeout(() => desktop.classList.remove("is-booting"), 2000);
  if (phone.matches) return phoneIntro();
  await pause(1250);
  intro();
})();
