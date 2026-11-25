import { createInk } from "/concepts/shared/ink.js";
import { setupPrints } from "./prints.js";

const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const root = document.documentElement;
const C = { red: "#ff4f2e", yellow: "#ffd92e", teal: "#0fa390", blue: "#2d5bff", ink: "#16181d", white: "#ffffff", chalk: "#fffdf7" };
const PALETTE = [C.red, C.yellow, C.teal, C.blue, C.ink, C.white];
const still = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const wait = (ms) => new Promise((done) => setTimeout(done, ms));
const pause = (ms) => wait(still() ? 0 : ms);
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const ease = (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
// A wait that ends early when its scene is no longer the current one.
const nap = (ms, alive) => new Promise((done) => {
  if (still()) return done();
  const end = performance.now() + ms, tick = () => (!alive() || performance.now() >= end ? done() : setTimeout(tick, 60));
  tick();
});
function tween(ms, step, alive = () => true) {
  if (still() || ms <= 0) { step(1); return Promise.resolve(); }
  return new Promise((done) => {
    const start = performance.now();
    const frame = (now) => {
      if (!alive()) return done();
      const t = clamp((now - start) / ms, 0, 1);
      step(t);
      if (t < 1) requestAnimationFrame(frame); else done();
    };
    requestAnimationFrame(frame);
  });
}
const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
const centre = (r) => [r.x + r.w / 2, r.y + r.h / 2];
const middleOf = (m) => [0.25 * m.x1 + 0.5 * m.cx + 0.25 * m.x2, 0.25 * m.y1 + 0.5 * m.cy + 0.25 * m.y2];

// Scripted scenes run one at a time. A new scene cancels the running one, waits for it to stop, resets the
// surface and then starts, and scenes that are already out of date by then are skipped. This is what keeps
// fast scrolling from stacking half-drawn marks.
function runner(reset) {
  let run = 0, chain = Promise.resolve(), abort = null;
  return (scene) => {
    const mine = ++run;
    abort?.abort();
    abort = new AbortController();
    const ctx = { signal: abort.signal, alive: () => mine === run };
    chain = chain.then(async () => {
      if (!ctx.alive()) return;
      await reset();
      if (scene && ctx.alive()) await scene(ctx);
    }).catch((error) => console.error(error));
    return chain;
  };
}
// A layer that only ever shows scripted marks: no toolbar, no keys, no drawing by hand.
const scripted = (surface, size) => createInk({ surface, interactive: false, color: C.red, size: size || (surface.clientWidth < 520 ? 3 : 5) });
async function clearMarks(layer) {
  const had = layer.marks().length;
  layer.select(null);
  layer.hideGhost();
  layer.clear();
  if (had) await pause(280);
}
// A pen stroke along given points, drawn by the ghost pointer like the engine's own scripted marks.
async function drawPen(layer, surface, points, { color, size, duration = 800, alive }) {
  const mark = layer.add({ type: "pen", points, color, size });
  const path = $(`[data-id="${mark.id}"] .ink-stroke`, surface);
  if (!path || still()) return mark;
  const length = path.getTotalLength();
  path.style.strokeDasharray = `${length} ${length}`;
  path.style.strokeDashoffset = length;
  await layer.moveGhost(points[0]);
  await tween(duration, (t) => {
    const k = ease(t), p = path.getPointAtLength(length * k);
    path.style.strokeDashoffset = length * (1 - k);
    layer.placeGhost([p.x, p.y]);
  }, alive);
  path.style.strokeDasharray = "";
  path.style.strokeDashoffset = "";
  return mark;
}

// The page layer: the one the visitor draws on, with the one toolbar -----------------------------
const ink = createInk({ surface: document.body, tool: "pen", color: C.red, palette: PALETTE, collapsed: true, appearance: "dark", properties: false, pillLabel: "Draw on this page", moreHref: "#install" });
const keyLive = $(".key-live");
ink.on("mode", (mode) => {
  root.toggleAttribute("data-draw", mode === "draw");
  if (mode === "draw" && ink.collapsed) ink.setCollapsed(false);
  keyLive.textContent = mode === "draw" ? "Draw mode is on. Scribble anywhere, then press Esc." : "Draw mode is off. Your drawings stay on the page.";
});
const startDrawing = (tool) => { if (ink.collapsed) ink.setCollapsed(false); if (tool) ink.setTool(tool); else ink.setMode("draw"); };
// Scripted marks on the page layer run one after another: there is one ghost pointer.
let pageQueue = Promise.resolve();
const queue = (job) => (pageQueue = pageQueue.then(job).catch((error) => console.error(error)));
const whenSeen = (el, threshold, job) => new IntersectionObserver(([entry], observer) => {
  if (!entry.isIntersecting) return;
  observer.disconnect();
  job();
}, { threshold }).observe(el);

// Hero -------------------------------------------------------------------------------------------
const title = $(".hero-title"), wigWord = $(".wiggler"), lostSpot = $('[data-hero="lost"]'), lost = $(".lost-cursor");
let lostWiggles = !still();
function wiggleLost() {
  const start = performance.now();
  const frame = (now) => {
    if (!lostWiggles) { lost.style.transition = "transform .35s ease"; lost.style.transform = ""; return; }
    const t = (now - start) / 1000;
    lost.style.transform = `translate(${(Math.sin(t * 21) * 7 + Math.sin(t * 6.3) * 5).toFixed(1)}px, ${(Math.cos(t * 17) * 4 + Math.sin(t * 4.7) * 4).toFixed(1)}px)`;
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}
async function heroMarks() {
  const fs = parseFloat(getComputedStyle(title).fontSize), small = matchMedia("(max-width: 700px)").matches;
  const word = ink.rectOf(wigWord), ringPad = clamp(fs * 0.055, 9, 15), thin = clamp(fs * 0.02, 3, 4.5);
  const steps = [
    { wait: 250 },
    // The marker lands on the word, and the word holds still.
    { run: async () => { await ink.moveGhost([word.x, word.y + word.h]); wigWord.classList.add("is-still"); lostWiggles = false; } },
    { tool: "underline", under: wigWord, color: C.red, size: clamp(fs * 0.032, 4, 7.5), dy: -fs * 0.025, wave: clamp(fs / 110, 0.8, 2), duration: 780, pause: 260 },
    { tool: "ellipse", around: lostSpot, pad: ringPad, color: C.red, size: thin + 0.5, duration: 600, pause: 180 },
    { tool: "text", at: { el: $('[data-hero="note"]'), at: "top left" }, text: small ? "that's\nyou" : "your cursor, on\ntheir screen", color: C.blue, pause: 140 },
  ];
  if (!small) steps.push({ tool: "arrow", from: { el: $('[data-hero="tail"]') }, to: { el: lostSpot, at: "right", dx: ringPad + 7, dy: 2 }, bend: -0.35, color: C.blue, size: thin, duration: 400, pause: 320 });
  const arrowWord = $('[data-hero="arrow"]');
  steps.push(
    { tool: "ellipse", around: $('[data-hero="ring"]'), padX: 5, padY: 2, color: C.red, size: thin, duration: 520, pause: 110 },
    // An arrow that underlines its own name.
    { tool: "arrow", from: { el: arrowWord, at: "bottom left", dx: -2, dy: 5 }, to: { el: arrowWord, at: "bottom right", dx: 10, dy: 3 }, bend: -0.08, color: C.red, size: thin, duration: 380, pause: 110 },
    { tool: "counter", at: { el: $('[data-hero="number"]'), at: "right", dx: 34, dy: -2 }, color: C.yellow },
  );
  await ink.perform(steps, { cursor: true });
  await pause(650);
  ink.hideGhost();
}
async function heroIntro() {
  await Promise.race([document.fonts.ready, wait(1800)]);
  root.classList.add("is-loaded");
  if (lostWiggles) wiggleLost();
  const last = $(".hero .line-3 .line-in");
  if (!still()) await Promise.race([new Promise((done) => last.addEventListener("animationend", done, { once: true })), wait(2400)]);
  title.classList.add("is-in");
  await pause(900);
  queue(heroMarks);
}
heroIntro();

$$("[data-try]").forEach((button) => button.addEventListener("click", () => {
  // On wide screens the jobs and the screen sit side by side, so centre the pair.
  $(".try-grid").scrollIntoView({ behavior: still() ? "auto" : "smooth", block: matchMedia("(max-width: 860px)").matches ? "start" : "center" });
  startDrawing("pen");
}));

// A marker tip that trails the real pointer in the hero. The real cursor is never hidden.
function heroNib() {
  if (still() || !matchMedia("(hover: hover) and (pointer: fine)").matches) return;
  const hero = $(".hero"), nib = $(".nib"), trail = $(".nib-trail path");
  let points = [], target = null, at = null, frame = 0;
  const draw = (now) => {
    points = points.filter((p) => now - p[2] < 380);
    trail.setAttribute("d", points.length > 1 ? `M${points.map((p) => `${p[0].toFixed(1)} ${p[1].toFixed(1)}`).join("L")}` : "");
    if (target) {
      at = at ? [at[0] + (target[0] - at[0]) * 0.22, at[1] + (target[1] - at[1]) * 0.22] : target;
      const lean = clamp((target[0] - at[0]) * 0.9, -28, 28);
      nib.style.transform = `translate(${(at[0] + 12).toFixed(1)}px, ${(at[1] - 50).toFixed(1)}px) rotate(${(24 + lean).toFixed(1)}deg)`;
    }
    frame = points.length || (target && Math.hypot(target[0] - at[0], target[1] - at[1]) > 0.5) ? requestAnimationFrame(draw) : 0;
  };
  hero.addEventListener("pointermove", (e) => {
    if (e.pointerType !== "mouse" || root.hasAttribute("data-draw")) return;
    const r = hero.getBoundingClientRect();
    target = [e.clientX - r.left, e.clientY - r.top];
    points.push([target[0], target[1], performance.now()]);
    nib.classList.add("is-on");
    if (!frame) frame = requestAnimationFrame(draw);
  });
  hero.addEventListener("pointerleave", () => { target = null; at = null; nib.classList.remove("is-on"); });
}
heroNib();

// The wiggle problem -----------------------------------------------------------------------------
function wiggleProblem() {
  const box = $(".wig-grid"), surface = $("[data-wig-surface]"), screen = $(".screen", surface), cursor = $(".wig-cursor", surface);
  const say = $("[data-say]"), hear = $("[data-hear]"), viewers = $(".viewers"), live = $(".wig-live"), buttons = $$("[data-wig]");
  const layer = scripted(surface);
  const W = (name) => $(`[data-w="${name}"]`, surface);
  const spot = (el) => { const r = el.getBoundingClientRect(), s = screen.getBoundingClientRect(); return [r.left + r.width / 2 - s.left - screen.clientLeft, r.top + r.height / 2 - s.top - screen.clientTop]; };
  const place = (p) => { cursor.style.transform = `translate(${p[0].toFixed(1)}px, ${p[1].toFixed(1)}px)`; };
  const play = runner(async () => {
    await clearMarks(layer);
    say.textContent = hear.textContent = "\u00a0";
    viewers.dataset.mood = "wait";
  });
  let touched = false;

  const scenes = {
    async without({ alive }) {
      const stops = ["a", "c", "b", "e", "d"].map((name) => spot(W(name)));
      place(stops[0]);
      cursor.classList.add("is-on");
      live.textContent = "Without Annotator: a small cursor shakes near the table while you say “this one, no, up a bit, the red one”. The people on the call ask: wait, where?";
      if (still()) { say.textContent = "“The red one.”"; viewers.dataset.mood = "lost"; hear.textContent = "“Wait, where?”"; return; }
      // The cursor drifts between cells that are near the right one, shaking all the way.
      const start = performance.now();
      const frame = (now) => {
        if (!alive()) return;
        const t = Math.max(0, now - start) / 1000, leg = t / 1.25, i = Math.floor(leg) % stops.length, k = ease(leg % 1), a = stops[i], b = stops[(i + 1) % stops.length];
        place([a[0] + (b[0] - a[0]) * k + Math.sin(t * 23) * 7, a[1] + (b[1] - a[1]) * k + Math.cos(t * 19) * 5]);
        requestAnimationFrame(frame);
      };
      requestAnimationFrame(frame);
      for (const line of ["“This one…”", "“No, up a bit…”", "“The red one.”"]) {
        await nap(1150, alive);
        if (!alive()) return;
        say.textContent = line;
      }
      await nap(1000, alive);
      if (!alive()) return;
      viewers.dataset.mood = "lost";
      hear.textContent = "“Wait, where?”";
    },
    async with({ signal, alive }) {
      const u = surface.clientWidth / 100;
      cursor.classList.remove("is-on");
      live.textContent = "With Annotator: a ring and an arrow mark the red cell while you say “this one”. The people on the call say: oh, that one.";
      say.textContent = "“This one.”";
      await layer.perform([
        { tool: "ellipse", around: W("cell"), padX: 1.7 * u, padY: 1.1 * u, color: C.red, duration: 700, pause: 160 },
        { tool: "text", at: { el: W("label"), at: "top left" }, text: "this one", color: C.blue, pause: 100 },
        { tool: "arrow", from: { el: W("tail") }, to: { el: W("cell"), at: "bottom left", dx: -0.4 * u, dy: 1.8 * u }, bend: 0.14, color: C.blue, size: Math.max(2.5, 0.42 * u), duration: 420 },
      ], { signal });
      if (!alive()) return;
      await pause(300);
      layer.hideGhost();
      viewers.dataset.mood = "clear";
      hear.textContent = "“Oh. That one.”";
    },
  };
  const show = (mode) => {
    box.dataset.mode = mode;
    buttons.forEach((button) => button.setAttribute("aria-pressed", String(button.dataset.wig === mode)));
    return play(scenes[mode]);
  };
  buttons.forEach((button) => button.addEventListener("click", () => { touched = true; show(button.dataset.wig); }));
  // First time on screen: the wiggle, then the answer. After that the switch is the visitor's.
  whenSeen(surface, 0.55, async () => {
    if (still()) { show("with"); return; }
    await show("without");
    await wait(2200);
    if (!touched) show("with");
  });
}
wiggleProblem();

// The rules --------------------------------------------------------------------------------------
const stageSurface = $("[data-stage]"), playSurface = $("[data-play]");
// The playground is a second copy of the first rule's screen.
playSurface.innerHTML = '<div class="screen"><div class="screen-zoom"></div></div>';
const playSlide = $('[data-slide="numbers"]', stageSurface).cloneNode(true);
playSlide.removeAttribute("data-slide");
$(".screen-zoom", playSurface).append(playSlide);

const stageInk = scripted(stageSurface);
const slides = $$("[data-slide]", stageSurface), clipSlide = $('[data-slide="clip"]', stageSurface), clipVideo = $("video", clipSlide), clipTag = $(".tag-real", clipSlide);
const steps = $$(".step"), ticks = $$(".ticks i"), nowN = $(".now-n"), nowName = $(".now-name"), stageFig = $("[data-stage-fig]");
const T = (name) => $(`[data-t="${name}"]`, stageSurface);
const unit = () => stageSurface.clientWidth / 100;
const showSlide = (name) => slides.forEach((slide) => slide.classList.toggle("is-on", slide.dataset.slide === name));
const hideGhostSoon = async (alive) => { await nap(500, alive); if (alive()) stageInk.hideGhost(); };
function cylinder({ x, y, w, h }) {
  const rx = w / 2, ry = h * 0.14, cx = x + rx, top = y + ry, bottom = y + h - ry, points = [];
  for (let i = 0; i <= 24; i++) { const a = Math.PI + (i / 24) * Math.PI * 2; points.push([cx + Math.cos(a) * rx, top + Math.sin(a) * ry]); }
  points.push([x, bottom]);
  for (let i = 1; i <= 12; i++) { const a = Math.PI - (i / 12) * Math.PI; points.push([cx + Math.cos(a) * rx, bottom + Math.sin(a) * ry]); }
  points.push([x + w, top]);
  return points;
}

const scenes = {
  async numbers({ signal, alive }) {
    const u = unit();
    const arrow = { tool: "arrow", from: { el: T("number"), at: "bottom right", dx: 4.5 * u, dy: 1.5 * u }, to: { el: T("dip"), at: "top", dx: 0.9 * u, dy: -1.5 * u }, bend: -0.1, color: C.red, duration: 650 };
    const made = await stageInk.perform([{ tool: "ellipse", around: T("number"), padX: 2.4 * u, padY: 1.5 * u, color: C.red, duration: 800, pause: 260 }, arrow], { signal });
    const mark = made[1];
    if (!mark || !alive() || still()) return;
    await nap(450, alive);
    if (!alive()) return;
    // The arrow is anchored to this step object, so changing its bend and reflowing redraws it.
    stageInk.select(mark.id);
    await stageInk.moveGhost(middleOf(mark));
    for (const [from, to] of [[-0.1, -0.6], [-0.6, 0.32]]) {
      await nap(240, alive);
      await tween(700, (t) => { arrow.bend = from + (to - from) * ease(t); stageInk.reflow(); stageInk.placeGhost(middleOf(mark)); }, alive);
      if (!alive()) return;
    }
    await nap(800, alive);
    if (alive()) { stageInk.select(null); stageInk.hideGhost(); }
  },
  async code({ signal, alive }) {
    const u = unit();
    await stageInk.perform([
      { tool: "rect", around: T("block"), padX: 1.2 * u, padY: 0.5 * u, color: C.red, duration: 800, pause: 200 },
      { tool: "text", at: { el: T("code-note"), at: "top left" }, text: "and the tax?", color: C.red, pause: 120 },
      { tool: "arrow", from: { el: T("code-tail") }, to: { el: T("block"), at: "bottom", dx: 5 * u, dy: 1.4 * u }, bend: 0.3, color: C.red, size: Math.max(2.5, 0.45 * u), duration: 400, pause: 320 },
      { tool: "blur", over: T("token"), padX: 0.8 * u, padY: 0.4 * u, duration: 700 },
    ], { signal });
    await hideGhostSoon(alive);
  },
  async board({ signal, alive }) {
    const u = unit(), lines = ["clone the project", "make a branch", "open a pull request"], colors = [C.red, C.yellow, C.teal];
    const script = [{ tool: "text", at: { el: T("b-title"), at: "top left" }, text: "Git, in three steps", color: C.ink, speed: 26, pause: 160 }];
    lines.forEach((text, i) => {
      const el = T(`b${i + 1}`);
      script.push({ tool: "counter", at: { el, at: "left", dx: -4.4 * u }, n: i + 1, color: colors[i] }, { tool: "text", at: { el, at: "top left", dy: 0.3 * u }, text, color: C.ink, speed: 24, pause: 90 });
    });
    const made = await stageInk.perform(script, { signal });
    // The highlighter runs over the line that was just written, whatever width the handwriting came out.
    const written = made[4] && $(`.ink-text[data-id="${made[4].id}"]`, stageSurface);
    if (!written || !alive()) return;
    await nap(200, alive);
    await stageInk.perform([{ tool: "highlight", over: written, color: C.yellow, line: 0.52, duration: 600 }], { signal });
    await hideGhostSoon(alive);
  },
  // Two recordings of the real app, one after the other.
  async clip({ alive }) {
    const clips = [["spotlight", "Spotlight"], ["zoom", "Screen zoom"]];
    for (let i = 0; alive(); i = (i + 1) % clips.length) {
      const [file, label] = clips[i];
      clipTag.textContent = `Recorded in the real app · ${label}`;
      clipVideo.poster = `/public/demo/stage-${file}.jpg`;
      clipVideo.src = `/public/demo/stage-${file}.mp4`;
      if (still()) return;
      try { await clipVideo.play(); } catch { return; }
      await new Promise((done) => {
        const end = () => { clearInterval(timer); clipVideo.removeEventListener("ended", end); done(); };
        const timer = setInterval(() => { if (!alive()) end(); }, 120);
        clipVideo.addEventListener("ended", end);
      });
    }
  },
  async system({ signal, alive }) {
    const u = unit(), thin = Math.max(2.5, 0.5 * u);
    const node = (name, tool) => ({ tool, around: T(name), pad: 0, color: C.chalk, size: thin, duration: 520, pause: 60 });
    const label = (name, text) => ({ tool: "text", at: { el: T(name), at: "top left" }, text, color: C.chalk, speed: 28, pause: 60 });
    const link = (from, to) => ({ tool: "arrow", from, to, bend: 0, color: C.yellow, size: thin, duration: 340, pause: 60 });
    await stageInk.perform([
      node("n-web", "rect"), label("t-web", "web"),
      link({ el: T("n-web"), at: "right", dx: 1.2 * u }, { el: T("n-api"), at: "left", dx: -1.4 * u }),
      node("n-api", "rect"), label("t-api", "api"),
      link({ el: T("n-api"), at: "right", dx: 1.2 * u }, { el: T("n-db"), at: "left", dx: -1.4 * u }),
    ], { signal });
    if (!alive()) return;
    await drawPen(stageInk, stageSurface, cylinder(stageInk.rectOf(T("n-db"))), { color: "#63dccb", size: thin, duration: 900, alive });
    if (!alive()) return;
    await stageInk.perform([
      label("t-db", "data"),
      link({ el: T("n-api"), at: "bottom", dy: 1.2 * u }, { el: T("n-queue"), at: "top", dy: -1.4 * u }),
      node("n-queue", "diamond"), label("t-queue", "queue"),
    ], { signal });
    await hideGhostSoon(alive);
  },
  async support({ signal, alive }) {
    const u = unit();
    await stageInk.perform([
      { tool: "counter", at: { el: T("s1"), at: "right", dx: 3.4 * u }, color: C.red, pause: 260 },
      { tool: "counter", at: { el: T("s2"), at: "right", dx: 3 * u, dy: -0.5 * u }, color: C.red, pause: 260 },
      { tool: "counter", at: { el: T("s3"), at: "left", dx: -3 * u }, color: C.red, pause: 260 },
      { tool: "text", at: { el: T("s-note"), at: "top left" }, text: "then this one", color: C.blue, pause: 100 },
      { tool: "arrow", from: { el: T("s-tail") }, to: { el: T("s3"), at: "bottom", dx: 0.6 * u, dy: 1.3 * u }, bend: 0.22, color: C.blue, size: Math.max(2.5, 0.45 * u), duration: 420 },
    ], { signal });
    await hideGhostSoon(alive);
  },
};

new ResizeObserver(([entry]) => $(".stage-col").style.setProperty("--stage-h", `${Math.round(entry.contentRect.height)}px`)).observe($(".stage"));
const playStage = runner(async () => { clipVideo.pause(); await clearMarks(stageInk); });
let active = -2;
function activate(index) {
  if (index === active) return;
  active = index;
  const scene = index < 0 ? "numbers" : steps[index].dataset.scene;
  steps.forEach((step, i) => { step.classList.toggle("is-active", i === index); if (i === index) step.setAttribute("aria-current", "step"); else step.removeAttribute("aria-current"); });
  ticks.forEach((tick, i) => tick.classList.toggle("is-on", i <= index));
  nowN.textContent = `Rule ${Math.max(0, index + 1)}/${steps.length}`;
  nowName.textContent = index < 0 ? "Scroll to start" : steps[index].dataset.name;
  stageFig.textContent = scene === "clip" ? "Fig. 3 — Recorded in the real app on a Mac, with sample content." : "Fig. 3 — Sample screens. The web demo draws the marks as you scroll.";
  playStage(async (ctx) => {
    showSlide(scene);
    if (index < 0) return;
    await nap(380, ctx.alive);
    if (ctx.alive()) await scenes[scene](ctx);
  });
}
function watchSteps() {
  const phone = matchMedia("(max-width: 860px)"), inView = new Set();
  let observer, stageInView = false;
  // Past the last step the stage keeps its last scene for as long as it is on screen.
  const update = () => {
    let index = steps.findLastIndex((step) => inView.has(step));
    if (index < 0 && steps.at(-1).getBoundingClientRect().bottom < innerHeight * (phone.matches ? 0.6 : 0.46)) index = steps.length - 1;
    activate(stageInView ? index : -1);
  };
  const start = () => {
    observer?.disconnect();
    inView.clear();
    // A thin line across the viewport: the step that crosses it is the active one.
    observer = new IntersectionObserver((entries) => {
      for (const entry of entries) { if (entry.isIntersecting) inView.add(entry.target); else inView.delete(entry.target); }
      update();
    }, { rootMargin: phone.matches ? "-60% 0px -40% 0px" : "-46% 0px -54% 0px" });
    steps.forEach((step) => observer.observe(step));
  };
  new IntersectionObserver(([entry]) => { stageInView = entry.isIntersecting; update(); }).observe($(".stage"));
  start();
  phone.addEventListener("change", start);
}
watchSteps();

// Three beats ------------------------------------------------------------------------------------
const capFor = (e) => (e.key === "Control" ? "Control" : e.key === "Shift" ? "Shift" : e.code === "Digit2" ? "2" : e.key === "Escape" ? "Escape" : null);
const press = (key, down) => $$(`.cap[data-key="${key}"]`).forEach((cap) => cap.classList.toggle("is-down", down));
addEventListener("keydown", (e) => { const key = capFor(e); if (key) press(key, true); });
addEventListener("keyup", (e) => { const key = capFor(e); if (key) press(key, false); });
addEventListener("blur", () => $$(".cap.is-down").forEach((cap) => cap.classList.remove("is-down")));
$$(".keygroup").forEach((group) => group.addEventListener("click", () => {
  if (group.dataset.keys === "draw") startDrawing(); else ink.setMode("use");
}));
// The third beat has no key. An arrow is drawn through an obstacle, then bent around it and recolored.
function editDemo() {
  const surface = $("[data-edit]"), layer = scripted(surface, 4), E = (name) => $(`[data-e="${name}"]`, surface);
  const play = runner(() => clearMarks(layer));
  const scene = async ({ signal, alive }) => {
    const arrow = { tool: "arrow", from: { el: E("a"), at: "bottom", dy: 7 }, to: { el: E("b"), at: "left", dx: -8 }, bend: 0, color: C.red, duration: 600 };
    while (alive()) {
      arrow.bend = 0;
      const [mark] = await layer.perform([arrow], { signal });
      if (!mark || !alive()) return;
      layer.select(mark.id);
      if (still()) { arrow.bend = -0.5; layer.reflow(); return; }
      await nap(500, alive);
      await layer.moveGhost(middleOf(mark));
      await tween(850, (t) => { arrow.bend = -0.5 * ease(t); layer.reflow(); layer.placeGhost(middleOf(mark)); }, alive);
      await nap(600, alive);
      if (!alive()) return;
      mark.color = C.blue;
      layer.render(mark);
      await nap(1300, alive);
      if (!alive()) return;
      layer.select(null);
      layer.hideGhost();
      await nap(2600, alive);
      if (alive()) await clearMarks(layer);
    }
  };
  new IntersectionObserver(([entry]) => play(entry.isIntersecting ? scene : null), { threshold: 0.5 }).observe(surface);
}
editDemo();

// Your turn --------------------------------------------------------------------------------------
const play = $(".play"), playBox = $(".play-box"), jobsPanel = $(".jobs"), jobLive = $(".job-live");
const P = (name) => $(`[data-t="${name}"]`, playSurface);
const jobs = { circle: false, arrow: false, blur: false };
const jobNames = { circle: "Circle the number that dropped", arrow: "Point an arrow at the dip", blur: "Blur the email" };
function finishJob(name) {
  if (jobs[name]) return;
  jobs[name] = true;
  $(`[data-job="${name}"]`).classList.add("is-done");
  const all = Object.values(jobs).every(Boolean);
  jobLive.textContent = all ? "All three done. That's the whole idea. Now do it on your real screen." : `Done: ${jobNames[name]}.`;
  if (all) { play.classList.add("is-done"); jobsPanel.classList.add("is-done"); }
}
function checkMark(m) {
  if (!m || !["ellipse", "rect", "pen", "arrow", "blur"].includes(m.type)) return;
  const box = ink.box(m);
  // A plain click leaves a mark with no size. It never counts.
  if (Math.max(box.w, box.h) < 12) return;
  if (m.type === "arrow") {
    const dip = centre(ink.rectOf(P("dip"))), reach = Math.max(34, playBox.clientWidth * 0.05);
    const long = Math.hypot(m.x2 - m.x1, m.y2 - m.y1) > 28;
    if (m.head !== false && long && Math.hypot(m.x2 - dip[0], m.y2 - dip[1]) < reach) finishJob("arrow");
  } else if (m.type === "blur") {
    const email = ink.rectOf(P("email"));
    if (overlap(box, email) / (email.w * email.h) > 0.7) finishJob("blur");
  } else {
    const number = ink.rectOf(P("number"));
    const closed = m.type !== "pen" || Math.hypot(m.points[0][0] - m.points.at(-1)[0], m.points[0][1] - m.points.at(-1)[1]) < Math.max(44, Math.hypot(box.w, box.h) * 0.4);
    // Around the dropped number only: a ring that also takes in its neighbours doesn't count.
    const others = $$(".kpi b", playSurface).filter((el) => el !== P("number")).map((el) => centre(ink.rectOf(el)));
    const lonely = others.every(([x, y]) => x < box.x || x > box.x + box.w || y < box.y || y > box.y + box.h);
    if (closed && lonely && overlap(box, number) / (number.w * number.h) > 0.85) finishJob("circle");
  }
}
ink.on("mark", checkMark);
ink.on("edit", checkMark);
// The app's own hint for the tool in hand (how to place arrow points, for example) takes the place of ours.
const jobHint = $(".finish-wait"), ownHint = jobHint.textContent;
ink.on("status", (words) => { jobHint.textContent = words || ownHint; });
$$("[data-pick]").forEach((button) => button.addEventListener("click", () => startDrawing(button.dataset.pick)));
$("[data-start]").addEventListener("click", () => startDrawing());
$("[data-reset]").addEventListener("click", () => {
  const area = ink.rectOf(playBox), near = { x: area.x - 40, y: area.y - 40, w: area.w + 80, h: area.h + 80 };
  ink.marks().filter((m) => overlap(ink.box(m), near) > 0).map((m) => m.id).forEach((id) => ink.remove(id));
  for (const name in jobs) jobs[name] = false;
  $$(".job").forEach((job) => job.classList.remove("is-done"));
  play.classList.remove("is-done");
  jobsPanel.classList.remove("is-done");
  jobLive.textContent = "Started over. Three jobs to do.";
});
// Open the toolbar while the playground is on screen. Close it again if the visitor never drew.
new IntersectionObserver(([entry]) => {
  if (entry.isIntersecting) { if (ink.collapsed) ink.setCollapsed(false); }
  else if (ink.state.mode === "use" && !ink.collapsed && !ink.marks().some((m) => !m.anchor)) ink.setCollapsed(true);
}, { threshold: 0.3 }).observe(playBox);

const os = root.dataset.os, handheld = /Android|iPhone|iPad|iPod/.test(navigator.userAgent);
$$("[data-download]").forEach((link) => {
  if (handheld) return;
  link.textContent = os === "windows" ? "Get it from the Microsoft Store" : "Download for Mac";
  link.href = os === "windows" ? "https://apps.microsoft.com/detail/9NMZ345ZJ6JK" : "/downloads/";
  if (os === "windows") { link.target = "_blank"; link.rel = "noopener"; }
});

// Things you don't expect ------------------------------------------------------------------------
setupPrints();
function extras() {
  // Story 3: a small copy of the break timer's default, counting down while it is on screen.
  const digits = $("[data-timer]");
  let left = 600, ticking = 0;
  new IntersectionObserver(([entry]) => {
    clearInterval(ticking);
    if (!entry.isIntersecting || still()) return;
    ticking = setInterval(() => {
      left = left > 0 ? left - 1 : 600;
      digits.textContent = `${String(Math.floor(left / 60)).padStart(2, "0")}:${String(left % 60).padStart(2, "0")}`;
    }, 1000);
  }).observe(digits);
  // Story 4: the demo toolbar shrinks to one button, like the app's.
  const shrink = $("[data-shrink]");
  const label = () => { shrink.textContent = ink.collapsed ? "Open the one on this page" : "Shrink the one on this page"; };
  shrink.addEventListener("click", () => ink.setCollapsed(!ink.collapsed));
  ink.on("collapse", label);
  label();
  // Story 6: a halo that follows the pointer on this page.
  const halo = $("[data-halo]");
  halo.addEventListener("click", () => {
    const on = !ink.state.halo;
    ink.setHalo(on);
    halo.setAttribute("aria-pressed", String(on));
    halo.textContent = on ? "Turn the halo off" : "Try a halo on this page";
  });
}
extras();

// Tool names: a second copy of each row makes the loop seamless.
$$(".band-track").forEach((track) => track.append(track.firstElementChild.cloneNode(true)));

// Straight answers -------------------------------------------------------------------------------
whenSeen($('[data-answer="blur"]'), 1, () => wait(still() ? 0 : 500).then(() => queue(async () => {
  await ink.perform([{ tool: "ellipse", around: $('[data-answer="blur"]'), padX: 14, padY: 6, color: C.red, size: clamp(innerWidth / 220, 4, 6), duration: 700 }], { cursor: true });
  await pause(450);
  ink.hideGhost();
})));
// The word widens along the font's width axis as it scrolls up the screen.
function freeWord() {
  const word = $(".free-word");
  if (still()) return;
  let near = false, frame = 0;
  const update = () => {
    frame = 0;
    const r = word.getBoundingClientRect(), p = clamp((innerHeight - r.top) / (innerHeight * 0.72), 0, 1);
    word.style.fontStretch = `${(62 + 63 * ease(p)).toFixed(1)}%`;
  };
  const request = () => { if (near && !frame) frame = requestAnimationFrame(update); };
  new IntersectionObserver(([entry]) => { near = entry.isIntersecting; request(); }, { rootMargin: "20% 0px" }).observe(word);
  addEventListener("scroll", request, { passive: true });
  addEventListener("resize", request);
  update();
}
freeWord();
// Each stamped fact gets a mark of its own, after the stamps have landed.
whenSeen($(".stamps"), 0.6, () => wait(still() ? 0 : 1300).then(() => queue(async () => {
  const facts = $$("[data-fact]"), size = clamp(innerWidth / 190, 4, 7);
  await ink.perform([
    { tool: "highlight", over: facts[0], color: C.yellow, line: 0.5, duration: 640, pause: 150 },
    { tool: "underline", under: facts[1], color: C.red, size, dy: 9, duration: 520, pause: 150 },
    { tool: "ellipse", around: facts[2], padX: 16, padY: 9, color: C.blue, size, duration: 720, pause: 150 },
    { tool: "underline", under: facts[3], color: C.teal, size, dy: 9, duration: 520 },
  ], { cursor: true });
  await pause(500);
  ink.hideGhost();
})));

// Install ----------------------------------------------------------------------------------------
function showInstall(which) {
  $$("[data-os-pick]").forEach((button) => button.setAttribute("aria-pressed", String(button.dataset.osPick === which)));
  $$("[data-panel]").forEach((panel) => { panel.inert = panel.dataset.panel !== which; });
}
$$("[data-os-pick]").forEach((button) => button.addEventListener("click", () => showInstall(button.dataset.osPick)));
showInstall(os);
const copyLive = $(".copy-live");
$$("[data-copy]").forEach((button) => button.addEventListener("click", async () => {
  let copied = true;
  try { await navigator.clipboard.writeText(button.dataset.copy); } catch { copied = false; }
  if (!copied) {
    const range = document.createRange();
    range.selectNodeContents(button.previousElementSibling);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
  }
  button.textContent = copied ? "Copied" : os === "windows" ? "Ctrl+C" : "⌘C";
  button.classList.toggle("is-copied", copied);
  copyLive.textContent = copied ? "Command copied." : "The command is selected. Copy it with your keyboard.";
  clearTimeout(button.timer);
  button.timer = setTimeout(() => { button.textContent = "Copy"; button.classList.remove("is-copied"); }, 1800);
}));

// Coffee: a handwritten pointer at the button, drawn once.
whenSeen($(".btn-coffee"), 0.9, () => wait(still() ? 0 : 700).then(() => queue(async () => {
  // There is no room beside the button on a phone.
  if (matchMedia("(max-width: 700px)").matches) return;
  const note = $('[data-coffee="note"]');
  await ink.perform([
    { tool: "text", at: { el: note, at: "top left" }, text: "coffee\ngoes here", color: C.blue, pause: 140 },
    { tool: "arrow", from: { el: note, at: "left", dx: -8, dy: 30 }, to: { el: $(".btn-coffee"), at: "right", dx: 16, dy: 4 }, bend: -0.24, color: C.blue, size: 4, duration: 480 },
  ], { cursor: true });
  await pause(500);
  ink.hideGhost();
})));

// Footer wordmark: each letter squeezes along the width axis under the pointer.
const footWord = $(".foot-word"), footDot = $("i", footWord);
footWord.replaceChildren(...[...footWord.firstChild.textContent].map((letter) => Object.assign(document.createElement("span"), { textContent: letter })), footDot);

// Scroll reveals ---------------------------------------------------------------------------------
const reveal = new IntersectionObserver((entries) => {
  for (const entry of entries) { if (entry.isIntersecting) { entry.target.classList.add("is-in"); reveal.unobserve(entry.target); } }
}, { threshold: 0.2, rootMargin: "0px 0px -6% 0px" });
$$("[data-reveal]").forEach((el) => reveal.observe(el));
