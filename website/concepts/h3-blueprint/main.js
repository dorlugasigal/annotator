import { createInk } from "/concepts/shared/ink.js";
import { growingStage } from "/concepts/shared/stage.js";
// The app's own motion, for this page's own pieces: its curves and its timings. The engine uses the same module.
import * as app from "/concepts/shared/app-motion.js";

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
const BLUE = token("--ink-blue");
const status = $("#status");

// Frames and marks ---------------------------------------------------------------------------------
// A frame is a line that can be drawn, so it is an SVG rectangle rather than a border. The main ones also get
// crop marks at their corners, and pictures get a registration mark on each side.
for (const host of $$("[data-frame]")) {
  host.insertAdjacentHTML("afterbegin", `<span class="frame" aria-hidden="true"><svg focusable="false"><rect pathLength="1" width="100%" height="100%" rx="6"/></svg>${host.dataset.frame === "ticks" ? "<i></i><i></i><i></i><i></i>" : ""}</span>`);
  host.classList.add("has-frame");
}
for (const host of $$("[data-reg]")) host.insertAdjacentHTML("beforeend", '<span class="reg" aria-hidden="true"><i></i><i></i></span>');

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
    // Stands in for an element where a step needs one ("around", "over"). It is measured from the surface's own
    // corner, which is where the engine measures from: the ink layer reaches past the surface while the frame is grown.
    box: (element) => ({
      getBoundingClientRect() { const o = surface.getBoundingClientRect(), b = rect(element); return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h }; },
    }),
    point: (element, ax = .5, ay = .5, dx = 0, dy = 0) => () => { const b = rect(element); return [b.x + b.w * ax + dx, b.y + b.h * ay + dy]; },
  };
}
const markElement = (ink, id) => ink.layer.querySelector(`[data-id="${id}"]`);
// Resolves when an element is on screen and the tab is in front.
function watcher(element, threshold = .35) {
  let seen = false, waiting = [];
  const check = () => { if (seen && !document.hidden) waiting.splice(0).forEach((done) => done()); };
  if (!window.IntersectionObserver) seen = true;
  else new IntersectionObserver(([entry]) => { seen = entry.isIntersecting; check(); }, { threshold }).observe(element);
  document.addEventListener("visibilitychange", check);
  // `leave` is for a caller that already knows the element has gone, before this observer has been told.
  return { get seen() { return seen; }, leave() { seen = false; }, ready: () => (seen && !document.hidden ? Promise.resolve() : new Promise((done) => waiting.push(done))) };
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

// The sample app -----------------------------------------------------------------------------------
// The window on the stage is a small working report: three weeks of made-up numbers, a week switcher and a chart
// whose days can be pointed at. Nothing leaves the page. It is drawn into every copy of the window (the real one
// and its line drawing), so the two always agree.
const stage = $("#stage"), screen = $("#screen"), board = $("#board"), dock = $("#dock"), sample = $("#report");
const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const WEEKS = [
  { n: 45, dates: "2 to 8 November", visitors: "11,765", orders: 90, deltas: [1, 2, 1], days: [54, 57, 55, 56, 55, 56, 57], notes: ["Nothing new shipped this week.", "The newsletter went out on Tuesday."] },
  { n: 46, dates: "9 to 15 November", visitors: "12,000", orders: 94, deltas: [2, 4, 3], days: [55, 58, 56, 60, 57, 57, 59], notes: ["The pricing page got a new headline.", "The newsletter went out on Tuesday."] },
  { n: 47, dates: "16 to 22 November", visitors: "12,480", orders: 96, deltas: [4, 2, -21], days: [48, 52, 49, 20, 42, 51, 56], notes: ["The newsletter went out on Tuesday.", "The new sign-up form shipped on Thursday."] },
];
// The dashed line is the week before; the first week's is not in the list.
const BEFORE_FIRST = [54, 56, 55, 55, 55, 55, 56];
const LATEST = WEEKS.length - 1;
const chartX = (i) => 24 + 62 * i, chartY = (value) => 140 - value * 1.84;
const curve = (days) => days.map((value, i) => (i ? `C${chartX(i - 1) + 21} ${chartY(days[i - 1]).toFixed(1)} ${chartX(i) - 21} ${chartY(value).toFixed(1)} ${chartX(i)} ${chartY(value).toFixed(1)}` : `M${chartX(0)} ${chartY(value).toFixed(1)}`)).join("");
const signed = (n) => `${n < 0 ? "\u2212" : "+"}${Math.abs(n)}%`;
DAYS.forEach(() => $("[data-plot]", sample).insertAdjacentHTML("beforeend", '<button type="button" class="pt"><span class="tip"></span></button>'));
const copies = [sample];
let week = LATEST;
function showWeek(index, fresh = motion) {
  week = clamp(index, 0, LATEST);
  const data = WEEKS[week], before = week ? WEEKS[week - 1].days : BEFORE_FIRST, total = data.days.reduce((sum, value) => sum + value, 0), low = data.days.indexOf(Math.min(...data.days));
  const fields = { title: `Week ${data.n}`, dates: data.dates, visitors: data.visitors, orders: data.orders, signups: total, "note-1": data.notes[0], "note-2": data.notes[1] };
  for (const copy of copies) {
    for (const name in fields) $(`[data-f="${name}"]`, copy).textContent = fields[name];
    ["visitors", "orders", "signups"].forEach((name, i) => {
      const delta = $(`[data-f="${name}-delta"]`, copy);
      delta.textContent = signed(data.deltas[i]);
      delta.className = `delta ${data.deltas[i] < 0 ? "down" : "up"}`;
    });
    $(".line", copy).setAttribute("d", curve(data.days));
    $(".area", copy).setAttribute("d", `${curve(data.days)}V136H24Z`);
    $(".last", copy).setAttribute("d", curve(before));
    $$(".pt", copy).forEach((point, i) => {
      point.style.setProperty("--x", `${(chartX(i) / 420 * 100).toFixed(2)}%`);
      point.style.setProperty("--y", `${(chartY(data.days[i]) / 160 * 100).toFixed(2)}%`);
      point.classList.toggle("is-low", i === low);
      point.setAttribute("aria-label", `${DAYS[i]}, ${data.days[i]} sign-ups`);
      point.firstElementChild.textContent = `${DAYS[i].slice(0, 3)} ${data.days[i]}`;
    });
    const [previous, next] = $$(".week-step", copy);
    previous.disabled = week === 0;
    next.disabled = week === LATEST;
    if (fresh) { const report = $(".report", copy); report.classList.remove("is-fresh"); void report.offsetWidth; report.classList.add("is-fresh"); }
  }
}
showWeek(LATEST, false);

// Hero stage --------------------------------------------------------------------------------------
const number = $("#ring-target"), plot = $("#plot"), dip = $("#dip"), secret = $("#private");
// The toolbar keeps the state the engine gives it (its properties row included), so no colors or sizes are set here.
// Its selected stroke color is the app's Lagoon gradient, the same one the page uses as its accent.
const ink = createInk({ surface: board, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme === "light" ? "light" : "dark", gradient: "lagoon", tool: "pen", moreHref: "#install", visibility: "magic", toolbarMargin: innerWidth < 600 ? 10 : 22 });
const at = anchorsFor(ink, board);
// The screen keeps room under the window for the toolbar. It remembers the tallest the toolbar has been at this
// size, so opening and closing the properties row moves nothing on the page.
const tallest = {};
// The frame that grows to fill the window (further down). While it is not small the toolbar has all the room it
// wants, so nothing is kept for it.
let growing = null;
ink.on("layout", ({ height, size, rows }) => {
  if (!height || ink.collapsed || (growing && growing.state !== "small")) return;
  const key = `${size}|${rows}`;
  if (height <= (tallest[key] || 0) && tallest.shown === key) return;
  tallest[key] = Math.max(height, tallest[key] || 0);
  tallest.shown = key;
  const room = tallest[key];
  requestAnimationFrame(() => { screen.style.setProperty("--tb-h", `${room}px`); schedule(); });
});
ink.fit();
// Where the toolbar is, in the screen's own layout.
function toolbarBox() {
  const bar = ink.toolbar, w = bar.offsetWidth;
  return { x: dock.offsetLeft + bar.offsetLeft - (bar.classList.contains("is-placed") ? 0 : w / 2), y: dock.offsetTop + bar.offsetTop, w, h: bar.offsetHeight };
}

// The stage starts as a line drawing of the screen, leaning in space, and becomes the real screen as it comes flat
// on scroll. It is also flat and real whenever the visitor is drawing on it, because a flat plane is where the
// pointer and the ink agree.
// `held` keeps it flat from the moment the visitor joins in until the grown frame is small again.
let engaged = false, held = false, lifted = !motion, tiltNow = motion ? 1 : 0, liftNow = 0;
let recross = () => {};
if (motion) {
  // The line drawing of the sample window is the window itself, copied and styled as outlines, so it always lines
  // up with the real one. It sits on the same sheet, under the ink, and takes no input.
  const wire = sample.cloneNode(true);
  wire.className = "wire-win";
  wire.removeAttribute("role");
  wire.removeAttribute("aria-label");
  wire.setAttribute("aria-hidden", "true");
  wire.inert = true;
  [wire, ...$$("[id]", wire)].forEach((node) => node.removeAttribute("id"));
  board.insertBefore(wire, sample);
  copies.push(wire);

  // Depth gauges sit under the window and under the toolbar, wherever those are.
  const gauges = [
    { element: $("#gauge-window"), box: () => localBox(sample, screen) },
    { element: $("#gauge-toolbar"), box: toolbarBox },
  ].map((gauge) => ({ ...gauge, placed: "" }));
  const placeGauges = () => {
    for (const gauge of gauges) {
      const b = gauge.box(), placed = `${Math.round(b.x)}|${Math.round(b.y + b.h)}|${Math.round(b.w)}`;
      if (placed === gauge.placed) continue;
      gauge.placed = placed;
      Object.assign(gauge.element.style, { left: `${Math.round(b.x)}px`, top: `${Math.round(b.y + b.h)}px`, width: `${Math.round(b.w)}px` });
    }
  };

  // The toolbar's gauge follows it when it changes size or shrinks to its one button.
  for (const name of ["layout", "collapse"]) ink.on(name, () => requestAnimationFrame(placeGauges));

  const lean = follow((k) => {
    tiltNow = k;
    stage.style.setProperty("--tilt", k.toFixed(4));
    stage.style.setProperty("--real", smooth(clamp((1 - k) / .82)).toFixed(4));
    stage.classList.toggle("is-live", k > 0);
  });
  const lift = follow((k) => {
    liftNow = k;
    stage.style.setProperty("--lift", k.toFixed(4));
    if (k > .01) placeGauges();
  }, 150);
  readers.push(({ y, h }) => {
    const top = stage.getBoundingClientRect().top + y, range = clamp(top - 90, 200, Math.max(200, h * .5));
    const k = engaged || held ? 0 : 1 - smooth(clamp(y / range));
    lean.target = k;
    lift.target = lifted ? k : 0;
  });

  // A crosshair follows the pointer over the stage. It has the frame's box, small or grown.
  const cross = $("#crosshair");
  if (hovers) {
    let pending = null, last = null;
    const apply = () => {
      const e = pending || last;
      pending = null;
      if (!e) return;
      last = e;
      const box = cross.getBoundingClientRect(), x = e.clientX - box.left, y = e.clientY - box.top;
      cross.style.setProperty("--x", `${x.toFixed(1)}px`);
      cross.style.setProperty("--y", `${y.toFixed(1)}px`);
      cross.classList.toggle("is-on", !e.target.closest?.(".ink-toolbar, .your-turn, .stage-shrink") && x >= 0 && y >= 0 && x <= box.width && y <= box.height);
    };
    screen.addEventListener("pointermove", (e) => {
      if (e.pointerType === "touch") return;
      if (!pending) requestAnimationFrame(apply);
      pending = e;
    });
    screen.addEventListener("pointerleave", () => { pending = null; last = null; cross.classList.remove("is-on"); });
    // The frame's corner moves while it grows and shrinks; the pointer does not, so the lines are set again.
    recross = apply;
  }
}
const engage = (on) => { if (engaged !== on) { engaged = on; schedule(); } };

// The mode, everywhere it shows ---------------------------------------------------------------------
// The toolbar's own Draw / Apps button is the switch. The chip on the stage and the line by the two keys say what
// it is set to, and the sample app only takes input while the stage is in Use apps.
const chip = $("#mode-chip"), modeWord = $("#mode-word"), chipKeys = { use: $('.chip-key[data-when="use"]'), draw: $('.chip-key[data-when="draw"]') };
const modeNote = $("#mode-note"), modeLine = $("#mode-line"), drawCombo = $('[data-combo="draw"]'), done = $("#done");
// The keycaps of the two keys section, by key.
const caps = Object.fromEntries($$("[data-key]").map((key) => [key.dataset.key, key]));
chip.hidden = false;
function showMode(mode) {
  chip.dataset.mode = mode;
  modeWord.textContent = mode === "draw" ? "Drawing" : "Using apps";
  modeNote.dataset.mode = mode;
  modeLine.textContent = mode === "draw" ? "Right now you\u2019re drawing." : "Right now you\u2019re using the app.";
  drawCombo.classList.toggle("is-on", mode === "draw");
  sample.inert = mode === "draw";
}
// The loop changes the mode too. While it does, `scripting` is above zero, and the change is not the visitor's.
let scripting = 0;
function scriptMode(mode) {
  scripting += 1;
  try { ink.setMode(mode); } finally { scripting -= 1; }
}

const small = () => screen.clientWidth < 620;
const stacked = () => at.rect(plot).y > at.rect(number).y + at.rect(number).h;
const LABEL = "Why the dip?";
let pen = null;
function labelWidth() {
  pen ||= document.createElement("canvas").getContext("2d");
  pen.font = getComputedStyle(ink.layer).getPropertyValue("--ink-text").trim().replace(/\/[\d.]+/, "");
  return pen.measureText(LABEL).width + 8;
}
// The ring, the arrow and the label name no color: they are drawn in whatever the toolbar has selected.
function ringAndArrow() {
  const size = small() ? 3 : 4, ring = { get x() { return small() ? 10 : 17; }, get y() { return small() ? 8 : 11; } };
  return [
    { tool: "ellipse", around: at.box(number), get padX() { return ring.x; }, get padY() { return ring.y; }, size, duration: 850, pause: 240 },
    {
      tool: "arrow", size, duration: 800, pause: 240,
      // Beside the chart the arrow leaves the ring's right side; above the chart it leaves the lower left, clear of the badge under the number.
      from: () => { const n = at.rect(number); return stacked() ? [n.x - ring.x * .4, n.y + n.h + ring.y * .7 + 4] : [n.x + n.w + ring.x + 8, n.y + n.h / 2 - 2]; },
      to: () => { const d = at.rect(dip); return stacked() ? [d.x + 8, d.y - 12] : [d.x - 7, d.y - 12]; },
      get bend() { return stacked() ? .16 : .24; },
    },
  ];
}
const labelAndBlur = () => [
  { tool: "text", text: LABEL, speed: 46, pause: 320, at: () => { const d = at.rect(dip); return [d.x - labelWidth() / 2, d.y + (small() ? 9 : 14)]; } },
  { tool: "blur", over: at.box(secret), padX: 6, padY: 3, duration: 650 },
];

// Whose doing is it? While one of the visitor's own presses or keys is being handled, `acting` is true. A change
// of mode, a hide or a shrink that arrives at any other time comes from the loop.
let acting = false;
for (const name of ["pointerdown", "pointerup", "keydown", "keyup", "click"]) addEventListener(name, (e) => {
  if (!e.isTrusted) return;
  acting = true;
  setTimeout(() => { acting = false; }, 0);
}, true);

// The loop ------------------------------------------------------------------------------------------
// It tells the product's story on the real stage, in the engine's own steps: draw, use the app under the
// drawings, draw again, then hide it all the way the app does. It stops for good the moment the visitor takes
// over, and only the Replay button starts it again.
const replay = $("#replay"), cue = $("#your-turn"), onStage = watcher(screen);
const demo = { run: 0, playing: false, ids: new Set(), busy: Promise.resolve(), controller: null, picked: null, rounds: 0, ready: false, drew: false, joined: false };
function showControl() {
  replay.hidden = false;
  replay.classList.toggle("is-off", !demo.playing);
  replay.lastElementChild.textContent = demo.playing ? "Pause" : "Replay";
}
// "Your turn" shows once the loop has been round once and whenever the loop is idle, until the visitor joins in.
let cueShown = false;
function updateCue() {
  const show = demo.ready && !demo.drew && !demo.joined && (demo.rounds > 0 || !demo.playing);
  if (show === cueShown) return;
  cueShown = show;
  cue.hidden = !show;
  stage.classList.toggle("has-turn", show);
  if (!show || !motion) return;
  // It grows out of a one-button square the way the app's toolbar grows back from its one button.
  const closed = cue.offsetWidth - 46, timing = { duration: app.toolbarMorph.expandDuration * 1000, easing: app.css.standard };
  cue.animate([{ clipPath: `inset(-2px ${closed}px -2px -2px round 4px)` }, { clipPath: "inset(-2px -2px -2px -2px round 4px)" }], timing);
  $(".turn-edge", cue).animate([{ transform: `translateX(${-closed}px)` }, { transform: "none" }], timing);
  cue.lastElementChild.previousElementSibling.animate([{ opacity: 0 }, { opacity: 0, offset: app.toolbarMorph.incomingStart }, { opacity: 1 }], { ...timing, easing: app.css.fade });
}
function forget() {
  for (const id of demo.ids) ink.remove(id);
  demo.ids.clear();
}
// What the loop did to the toolbar and the drawings is put back at once: nothing stays hidden or shrunk.
function restore() {
  if (ink.hidden) ink.setHidden(false, { animate: false });
  if (ink.collapsed) ink.setCollapsed(false, { animate: false });
}
// The keys that change the mode, shown pressed: on the stage's chip and on the two keys further down the page.
function showKeys(mode, down) {
  chipKeys[mode === "draw" ? "use" : "draw"].classList.toggle("is-down", down);
  for (const key of mode === "draw" ? [caps.control, caps.shift, caps[2]] : [caps.esc]) key.classList.toggle("is-down", down);
}
// `keepMode` is for the visitor who chose to draw: they stay in it. `keepHidden` is for the visitor who has just
// hidden or shown everything themselves: that stays as they left it.
function stopDemo(keepMode = false, keepHidden = false) {
  if (!demo.playing) return;
  demo.playing = false;
  demo.run += 1;
  demo.controller?.abort();
  ink.hideGhost();
  showKeys("draw", false);
  showKeys("use", false);
  $$(".is-shown", sample).forEach((element) => element.classList.remove("is-shown"));
  if (!keepHidden) restore();
  if (!keepMode && ink.state.mode === "draw") scriptMode("use");
  showControl();
  updateCue();
}
// A step of the loop that was already on its way when the visitor stopped it is taken back as soon as it lands.
// What the visitor hid themselves (the toolbar's Hide button, Control-2) stays hidden until they show it again.
let theirsHidden = false;
ink.on("hide", (hidden) => {
  if (acting) theirsHidden = hidden; else if (!hidden) theirsHidden = false;
  if (hidden && !theirsHidden && !demo.playing) setTimeout(() => { if (!demo.playing && !theirsHidden) restore(); }, 0);
});
ink.on("collapse", (collapsed) => { if (collapsed && !acting && !theirsHidden && !demo.playing) setTimeout(() => { if (!demo.playing && !theirsHidden) restore(); }, 0); });
async function playDemo() {
  const run = ++demo.run, live = () => run === demo.run;
  demo.playing = true;
  showControl();
  updateCue();
  await demo.busy;
  let release;
  demo.busy = new Promise((resolve) => { release = resolve; });
  // A round goes on while the loop is on and the stage is on screen.
  const on = () => live() && !demo.controller.signal.aborted;
  // A pause that ends at once when the round does.
  const rest = (ms) => new Promise((resolve) => {
    const signal = demo.controller.signal;
    if (!on()) { resolve(); return; }
    const timer = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { clearTimeout(timer); resolve(); }, { once: true });
  });
  const hold = (ms) => ({ run: () => rest(ms) });
  // The sample app is used with the engine's own click step: the pointer goes to the button, the app's ring shows
  // the click, and the button gets the events a mouse would give it. `local` finds the button in the plane's own
  // layout, so the pointer lands on it while the stage leans too.
  const click = (element) => ({ click: element, local: true });
  const reach = async (element, ms) => { const b = at.rect(element); await ink.moveGhost([b.x + b.w / 2, b.y + b.h / 2], ms); };
  // Esc, shown pressed for a moment before the engine's own step changes the mode with it.
  const esc = () => [
    { run: async () => { if (!on()) return; showKeys("use", true); await rest(230); showKeys("use", false); } },
    { mode: "use", via: "key", pause: 260 },
  ];
  function story() {
    const [previous, next] = $$(".week-step", sample), thursday = $$(".pt", sample)[3];
    return [
      // The next round comes in with the app's show: the toolbar is back from its one button.
      { hide: false, style: "magic", pause: 320 },
      // Draw: ring the number, point at the dip.
      { mode: "draw", pause: 220 },
      ...ringAndArrow(),
      // Use apps: the toolbar shrinks out of the way, and last week is one click off. The drawings stay where they are.
      ...esc(),
      { shrink: true, pause: 380 },
      click(previous), hold(1250),
      click(next), hold(420),
      { run: async () => { if (!on()) return; await reach(thursday, 520); thursday.classList.add("is-shown"); await rest(1050); thursday.classList.remove("is-shown"); } },
      // Draw again (the toolbar's one button opens it): say what it is, cover the address.
      { mode: "draw", pause: 220 },
      ...labelAndBlur(),
      ...esc(),
      { run: () => ink.hideGhost() }, hold(2300),
      // The round ends with the app's own hide, in its Magic style: the drawings fly to the toolbar and it goes too.
      { hide: true, style: "magic" },
      hold(650),
    ];
  }
  try {
    while (live()) {
      await onStage.ready();
      if (!live()) break;
      forget();
      demo.controller = new AbortController();
      demo.picked = null;
      if (week !== LATEST) showWeek(LATEST, false);
      const made = await ink.perform(story(), { cursor: true, showTool: true, signal: demo.controller.signal });
      made.forEach((mark) => demo.ids.add(mark.id));
      if (!live()) break;
      // A script puts the toolbar back on the tool it started with. If the visitor chose one meanwhile, theirs wins.
      if (demo.picked && ink.state.tool !== demo.picked) ink.setTool(demo.picked, true);
      // The round's drawings go while they are hidden. A round that ended early, because the stage went off screen,
      // leaves nothing behind either; the next one starts clean.
      forget();
      if (demo.controller.signal.aborted) { ink.hideGhost(); restore(); if (ink.state.mode === "draw") scriptMode("use"); continue; }
      demo.rounds += 1;
      updateCue();
    }
  } finally { release(); }
}
// Without motion nothing plays: the finished drawing is there, and the stage waits for the visitor.
async function showStill() {
  const made = await ink.perform([...ringAndArrow(), ...labelAndBlur()], { cursor: false });
  made.forEach((mark) => demo.ids.add(mark.id));
  ink.hideGhost();
}
// The visitor took the stage: the loop ends, and only the Replay button starts it again.
const takeOver = (keepMode = false, keepHidden = false) => stopDemo(keepMode, keepHidden);
// The app's keys, pressed for real: Control-Shift-2 draws, Esc goes back to the apps, Control-2 hides and shows.
// Drawing that the loop had on becomes the visitor's own the moment they ask for it: the stage takes their strokes.
function claimDraw() {
  if (ink.state.mode !== "draw") return;
  screen.classList.remove("is-scripted");
  done.hidden = false;
}
ink.on("shortcut", (name, how) => {
  if (how?.scripted) return;
  const draws = name === "draw" || name === "show";
  takeOver(draws, name === "hide" || name === "show");
  if (draws) claimDraw();
});
ink.on("tool", (tool) => { if (acting) demo.picked = tool; });
ink.on("mark", () => {
  demo.drew = true;
  takeOver(true);
  updateCue();
});
ink.on("mode", (mode) => {
  showMode(mode);
  const theirs = acting;
  done.hidden = !(mode === "draw" && theirs);
  // While the loop itself has drawing on, the stage takes no strokes: a press there ends the loop instead.
  screen.classList.toggle("is-scripted", mode === "draw" && !theirs);
  if (!theirs) {
    // A step of the loop that was already on its way when the visitor stopped it is taken back.
    if (mode === "draw" && !demo.playing && !scripting) setTimeout(() => { if (!demo.playing && ink.state.mode === "draw" && screen.classList.contains("is-scripted")) scriptMode("use"); }, 0);
    return;
  }
  takeOver(mode === "draw");
  // Drawing chosen from further down the page (the two keys) leaves the stage alone until it is on screen.
  if (mode === "draw" ? onStage.near : !onStage.near) engage(mode === "draw");
});
ink.toolbar.addEventListener("pointerdown", () => { takeOver(); engage(true); }, true);
ink.toolbar.addEventListener("focusin", () => engage(true));
done.addEventListener("click", () => ink.setMode("use"));
replay.addEventListener("click", () => {
  if (demo.playing) { stopDemo(); return; }
  scriptMode("use");
  engage(false);
  playDemo();
});
// A press anywhere on the stage ends the loop. The loop's own drawings keep following their targets when the page
// reflows, so a resized window leaves them where they belong. The loop's own clicks are not the visitor's (they are
// not trusted events).
board.addEventListener("pointerdown", (e) => { if (e.isTrusted) takeOver(); }, true);
for (const name of ["pointerdown", "keydown"]) sample.addEventListener(name, (e) => { if (e.isTrusted) takeOver(); }, true);
sample.addEventListener("click", (e) => {
  const button = e.target.closest(".week-step");
  if (!button || button.disabled) return;
  showWeek(week + Number(button.dataset.step));
  if (!e.isTrusted) return;
  status.textContent = `Week ${WEEKS[week].n}, ${WEEKS[week].dates}.`;
  // A button that has just switched itself off cannot keep the focus.
  if (button.disabled) $$(".week-step", sample).find((other) => !other.disabled)?.focus();
});
if (window.IntersectionObserver) new IntersectionObserver(([entry]) => {
  onStage.near = entry.isIntersecting;
  if (entry.isIntersecting) { if (ink.state.mode === "draw") engage(true); return; }
  // Out of view, the loop's round ends and drawing ends, so keys and touches go back to the page, and the stage
  // is free to lean again. The loop starts a new round when the stage is back. (Two observers watch the stage, and
  // the other one may hear about this later, so it is told now: the next round must wait.)
  onStage.leave();
  demo.controller?.abort();
  scriptMode("use");
  done.hidden = true;
  engage(false);
}).observe(screen);
showMode("use");

// The stage grows -----------------------------------------------------------------------------------
// Once the visitor joins in (a press on the stage or its toolbar, one of the app's keys, a "Try it" control), the
// screen's frame opens up to fill the window, and scrolling brings it back (shared/stage.js). The board with the
// sample window does not move. In index.html the desk, its line drawing, the dock, the crosshair and the outline
// that answers "Try it" carry data-stage-frame, and the chip and "Your turn" data-stage-corner.
growing = growingStage({ ink, surface: board, root: screen, tries: "[data-try]", motion: () => motion, isFlat: () => tiltNow < .004 && liftNow < .004 });
// Joining in ends the loop, retires "Your turn" and lays the stage flat: the frame grows only once it is.
growing.on("join", () => {
  takeOver(ink.state.mode === "draw" && !screen.classList.contains("is-scripted"));
  demo.joined = true;
  updateCue();
  held = true;
  schedule();
});
// Small again, the stage is free to lean with the scroll as before.
let wasOut = false;
growing.on("change", ({ state }) => {
  recross();
  if (state === "small" && wasOut) { held = false; schedule(); }
  wasOut = state !== "small";
});
// The outline that answers "Try it" is drawn as the frame opens, or at once when it is open already.
let inviting = false;
function invite() {
  inviting = false;
  stage.classList.remove("is-invited");
  void stage.offsetWidth;
  stage.classList.add("is-invited");
  clearTimeout(invite.timer);
  invite.timer = setTimeout(() => stage.classList.remove("is-invited"), 1600);
}
growing.on("grow", () => { if (inviting) invite(); });

Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {}).then(async () => {
  if (motion) await wait(1500);
  lifted = true;
  schedule();
  if (motion) await wait(700);
  demo.ready = true;
  if (motion) playDemo(); else { await showStill(); updateCue(); }
});

// Try it ----------------------------------------------------------------------------------------------
// Every "Try it" on the page does the same thing: ends the loop and turns on Draw with the pen. The growing stage
// hears the same click: it brings the stage into view and opens its frame, and the frame's outline is drawn once
// more, in the gradient, so it is plain where to draw.
function tryIt(e) {
  e?.preventDefault();
  takeOver();
  ink.setTool("pen");
  ink.setMode("draw");
  engage(true);
  if (growing.state === "grown") invite(); else inviting = true;
  status.textContent = "Drawing is on. Draw on the sample screen.";
}
for (const trigger of $$("[data-try]")) trigger.addEventListener("click", tryIt);

// Theme -------------------------------------------------------------------------------------------
// The script in the page's head has already set the theme. The switch changes it, remembers the choice and tells
// everything that keeps colors of its own: the toolbar and the browser's own chrome.
const THEME_KEY = "annotator.concept-h3.theme";
const themeSwitch = $("#theme-switch"), themeMeta = $('meta[name="theme-color"]');
let theme = root.dataset.theme === "light" ? "light" : "dark";
function showTheme(next) {
  theme = next;
  root.dataset.theme = next;
  themeSwitch.setAttribute("aria-checked", String(next === "light"));
  themeMeta.content = token("--page");
  ink.setAppearance(next);
}
// The colors change in one step, with nothing easing, so a view transition's new picture is the new theme whole.
function swapColors(next) {
  root.classList.add("theme-switching");
  showTheme(next);
  void root.offsetWidth;
  root.classList.remove("theme-switching");
}
// The new theme arrives in square tiles. Each tile opens from its middle line, and the tiles go in diagonals that
// start at the top right corner, where the switch is. A clip can be one polygon only, so every keyframe is one path
// that visits each tile along its row's middle line and comes back the same way: the way there and back has no
// area, so only the tiles themselves show. Every keyframe has the same points, so the browser can move between them.
function tileWave() {
  const w = innerWidth, h = innerHeight, size = clamp(Math.ceil(Math.max(w, h) / 9), 84, 170);
  const cols = Math.ceil(w / size), rows = Math.ceil(h / size), span = 3, steps = cols + rows - 1 + span;
  const px = (n) => `${Math.round(n * 10) / 10}px`;
  const frames = [];
  for (let step = 0; step <= steps; step++) {
    const points = [];
    for (let r = 0; r < rows; r++) {
      const mid = (r + .5) * size;
      points.push(`0px ${px(mid)}`);
      for (let c = 0; c < cols; c++) {
        // A tile turning over is as wide as ever and only as tall as it shows. A narrow gap at its sides, which
        // closes as it lands, keeps each one a square of its own.
        const open = smooth(clamp((step - (cols - 1 - c + r)) / span)), half = open * (size / 2 + .5), gap = (1 - open) * size * .1;
        const left = px(c * size + gap), right = px((c + 1) * size - gap + (open === 1 ? .5 : 0)), top = px(mid - half), bottom = px(mid + half);
        points.push(`${left} ${px(mid)}`, `${left} ${top}`, `${right} ${top}`, `${right} ${bottom}`, `${left} ${bottom}`, `${left} ${px(mid)}`);
      }
      points.push(`0px ${px(mid)}`);
    }
    points.push(`0px ${px(size / 2)}`);
    frames.push(`polygon(${points.join(",")})`);
  }
  return { frames, duration: clamp(380 + steps * 14, 460, 660) };
}
let fadeTimer = 0;
function crossFade(next) {
  root.classList.add("theme-fading");
  showTheme(next);
  clearTimeout(fadeTimer);
  fadeTimer = setTimeout(() => root.classList.remove("theme-fading"), 340);
}
function setTheme(next) {
  if (next === theme) return;
  try { localStorage.setItem(THEME_KEY, next); } catch { /* storage is off */ }
  if (!motion) { swapColors(next); return; }
  if (!document.startViewTransition || new URLSearchParams(location.search).has("fade")) { crossFade(next); return; }
  const transition = document.startViewTransition(() => swapColors(next));
  changing = transition;
  transition.finished.finally(() => { if (changing === transition) changing = null; });
  transition.ready.then(() => {
    const { frames, duration } = tileWave();
    root.animate({ clipPath: frames }, { duration, easing: "linear", fill: "both", pseudoElement: "::view-transition-new(root)" });
  }).catch(() => transition.skipTransition());
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
const what = $("#what"), deck = $("#deck"), tiles = $$(".tile", deck), deckSpace = $(".deck-space"), stepsBar = $("#deck-steps"), clipsToggle = $("#clips-toggle");
// A small sample screen that a pointer draws on, again and again while its sheet is the one on top. `build` gives
// the steps; a step that adds marks of its own hands their ids to `keep`, `reset` puts the sample back, and
// `leaving` names the app's hide style the drawings go with.
function createScene(surface, build, reset, leaving = "pop") {
  const scene = createInk({ surface, interactive: false, size: 3, gradient: "lagoon" }), anchors = anchorsFor(scene, surface);
  let active = false, running = false, ids = [], extra = [];
  const keep = (mark) => { extra.push(mark.id); return mark; };
  const clear = () => { [...ids, ...extra].forEach((id) => scene.remove(id)); ids = []; extra = []; reset?.(scene); };
  async function run() {
    if (running) return;
    running = true;
    while (active) {
      if (!ids.length && !extra.length) {
        ids = (await scene.perform(build(anchors, scene, keep, () => active), { cursor: motion })).map((mark) => mark.id);
        scene.hideGhost();
      }
      if (!motion) break;
      await wait(2600);
      if (!active) break;
      // The drawings go the way the app hides them, each sheet in a style of its own.
      await scene.setHidden(true, { style: leaving, pill: false });
      clear();
      scene.setHidden(false, { animate: false });
      await wait(600);
    }
    running = false;
  }
  // Without motion there is nothing to play: the finished drawing is simply there.
  if (!motion) scene.perform(build(anchors, scene, keep, () => true), { cursor: false });
  return { set(on) { if (!motion) return; active = on; if (on) run(); } };
}
// Moves a scene's pointer along a line while something is done with where it is.
function glide(scene, from, to, ms, each) {
  if (!motion) { each?.(to, 1); return Promise.resolve(); }
  return new Promise((resolve) => {
    const start = performance.now();
    const tick = (now) => {
      const t = clamp((now - start) / ms), k = app.ease.standard(t), p = [from[0] + (to[0] - from[0]) * k, from[1] + (to[1] - from[1]) * k];
      scene.placeGhost(p);
      each?.(p, t);
      if (t < 1) requestAnimationFrame(tick); else resolve();
    };
    requestAnimationFrame(tick);
  });
}
const sceneOf = (id) => $(`#${id}`);
const scenes = new Map([
  // Ring the number, then an arrow to the reason.
  ["scene-point", (a) => {
    const host = sceneOf("scene-point"), target = $("[data-ring]", host), low = $("[data-dip]", host);
    return [
      { wait: 350 },
      { tool: "ellipse", around: a.box(target), padX: 11, padY: 7, duration: 750, pause: 240 },
      { tool: "arrow", duration: 750, bend: -.3, from: () => { const r = a.rect(target); return [r.x + r.w + 15, r.y + r.h / 2 + 4]; }, to: () => { const d = a.rect(low); return [d.x - 5, d.y + 3]; } },
    ];
  }, null, "drop"],
  // A rough box that Smart Draw tidies up, then a note next to it.
  ["scene-smart", (a, scene, keep, active) => {
    const host = sceneOf("scene-smart"), target = $("[data-box]", host);
    const frame = () => { const r = a.rect(target); return { x: r.x - 8, y: r.y - 7, w: r.w + 16, h: r.h + 14 }; };
    return [
      { wait: 350 },
      { run: async () => {
        const f = frame(), x = f.x, y = f.y, w = f.w, h = f.h;
        // A box as a hand draws it: once round, not quite straight, not quite closed.
        const points = [[x + 2, y + 1], [x + w * .5, y - 2], [x + w + 1, y + 2], [x + w + 3, y + h * .5], [x + w - 1, y + h + 2], [x + w * .5, y + h - 1], [x + 1, y + h + 3], [x - 2, y + h * .5], [x + 3, y - 1]];
        const rough = keep(scene.add({ type: "pen", points, size: 3 })), stroke = $(".ink-stroke", markElement(scene, rough.id));
        if (motion && stroke) {
          const length = stroke.getTotalLength();
          stroke.style.strokeDasharray = `${length} ${length}`;
          stroke.style.strokeDashoffset = length;
          scene.placeGhost(points[0]);
          await new Promise((resolve) => {
            const start = performance.now();
            const tick = (now) => {
              const t = clamp((now - start) / 900), k = app.ease.standard(t), q = stroke.getPointAtLength(length * k);
              stroke.style.strokeDashoffset = length * (1 - k);
              scene.placeGhost([q.x, q.y]);
              if (t < 1) requestAnimationFrame(tick); else resolve();
            };
            requestAnimationFrame(tick);
          });
          await wait(260);
        }
        if (!active()) return;
        // Smart Draw's snap: the rough stroke gives way to the neat shape over the app's 0.22 s.
        const neat = keep(scene.add({ type: "rect", x: f.x, y: f.y, w: f.w, h: f.h, size: 3, rough: 0 }));
        const timing = { duration: motion ? app.DURATIONS.snapMorph * 1000 : 0, easing: app.css.standard, fill: "both" };
        markElement(scene, neat.id)?.animate([{ opacity: 0 }, { opacity: 1 }], timing);
        await markElement(scene, rough.id)?.animate([{ opacity: 1 }, { opacity: 0 }], timing).finished.catch(() => {});
        scene.remove(rough.id);
        await wait(motion ? 320 : 0);
      } },
      { tool: "arrow", duration: 600, bend: .12, pause: 160, from: () => { const f = frame(); return [f.x + f.w + f.h * 2.3, f.y + f.h / 2]; }, to: () => { const f = frame(); return [f.x + f.w + 9, f.y + f.h / 2]; } },
      { tool: "text", text: "Make it bigger", speed: 40, at: () => { const f = frame(); return [f.x + f.w + f.h * 2.3 + 8, f.y + f.h / 2 - 15]; } },
    ];
  }],
  ["scene-private", (a) => [{ wait: 350 }, ...$$("[data-blur]", sceneOf("scene-private")).map((el) => ({ tool: "blur", over: a.box(el), padX: 6, padY: 3, duration: 600, pause: 320 }))], null, "fade"],
  // Spotlight: everything dims but the place the pointer is.
  ["scene-spot", (a, scene) => {
    const rows = $$("[data-spot]", sceneOf("scene-spot")), middle = (row) => { const r = a.rect(row); return [r.x + r.w * .42, r.y + r.h / 2]; };
    const light = (p) => { scene.layer.style.setProperty("--px", `${p[0]}px`); scene.layer.style.setProperty("--py", `${p[1]}px`); };
    return [
      { wait: 350 },
      { run: async () => {
        const first = middle(rows[0]), second = middle(rows[1]), start = [first[0] + 70, first[1] - 50];
        scene.setStyle("spotlight", { radius: Math.max(40, a.rect(rows[0]).h * 2) });
        light(start);
        scene.placeGhost(start);
        scene.setSpotlight(true);
        scene.layer.classList.add("has-pointer");
        await glide(scene, start, first, 700, light);
        await wait(motion ? 1100 : 0);
        await glide(scene, first, second, 800, light);
        await wait(motion ? 1100 : 0);
      } },
    ];
  }, (scene) => { scene.setSpotlight(false); scene.layer.classList.remove("has-pointer"); }],
  ["scene-steps", (a) => [{ wait: 350 }, ...$$("[data-step]", sceneOf("scene-steps")).map((el) => ({ tool: "counter", at: a.point(el, 0, .5, -22), color: BLUE, pause: 300 }))], null, "focus"],
  // Two ready-made icons, a box between them, and arrows to join them up.
  ["scene-system", (a) => {
    const host = sceneOf("scene-system"), [server, database] = $$("[data-node] .icon", host), slot = $("[data-slot]", host);
    const right = (el, dx = 8) => () => { const r = a.rect(el); return [r.x + r.w + dx, r.y + r.h / 2]; };
    const left = (el, dx = -8) => () => { const r = a.rect(el); return [r.x + dx, r.y + r.h / 2]; };
    return [
      { run: async () => { for (const node of $$("[data-node]", host)) { node.classList.add("is-in"); await wait(motion ? 220 : 0); } } },
      { tool: "rect", around: a.box(slot), pad: 0, duration: 650, pause: 120 },
      { tool: "text", text: "Cache", speed: 50, pause: 160, at: () => { const r = a.rect(slot); return [r.x + r.w * .2, r.y + r.h / 2 - 14]; } },
      { tool: "arrow", duration: 520, bend: -.2, pause: 120, from: right(server), to: left(slot) },
      { tool: "arrow", duration: 520, bend: .2, from: right(slot), to: left(database) },
    ];
  }, () => { if (motion) $$("#scene-system [data-node]").forEach((node) => node.classList.remove("is-in")); }, "drop"],
].map(([id, build, reset, leaving]) => [sceneOf(id).closest(".tile"), createScene(sceneOf(id), build, reset, leaving)]));

let clipsPaused = !motion, front = -1;
const shown = new Set();
const refreshTiles = () => tiles.forEach((tile, i) => scenes.get(tile)?.set(!clipsPaused && (motion ? i === front : shown.has(tile))));
clipsToggle.hidden = !motion;
const labelClips = () => {
  clipsToggle.classList.toggle("is-off", clipsPaused);
  clipsToggle.lastElementChild.textContent = clipsPaused ? "Replay" : "Pause";
  clipsToggle.setAttribute("aria-label", clipsPaused ? "Replay the examples" : "Pause the examples");
};
labelClips();
clipsToggle.addEventListener("click", () => { clipsPaused = !clipsPaused; labelClips(); refreshTiles(); });

if (motion) {
  // The flat file. The section is five steps taller than the screen; each step turns the top sheet away.
  const last = tiles.length - 1, index = $("#deck-index"), roll = $(".roll", index), pin = $(".deck-pin"), head = $(".deck-head");
  stepsBar.hidden = false;
  index.hidden = false;
  const stepButtons = tiles.map((tile, i) => {
    const button = document.createElement("button");
    button.type = "button";
    button.setAttribute("aria-label", $("h3", tile).textContent);
    button.addEventListener("click", () => goTo(i));
    stepsBar.append(button);
    tile.style.setProperty("--z", tiles.length - i);
    return button;
  });
  const metrics = () => { const top = what.getBoundingClientRect().top + scrollY; return { top, step: (what.offsetHeight - innerHeight) / last }; };
  const goTo = (i) => { const { top, step } = metrics(); scrollTo({ top: Math.round(top + i * step) + 1, behavior: "smooth" }); };

  // The sheets are as tall as their words need. If that is more than the screen has room for, the picture gives
  // way: --trim takes the difference off the room the picture's size is worked out from.
  let fitting = false;
  const fitDeck = () => {
    if (fitting) return;
    fitting = true;
    let trim = 0;
    what.style.setProperty("--trim", "0px");
    for (let pass = 0; pass < 4; pass++) {
      const style = getComputedStyle(pin), room = pin.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom) - parseFloat(style.rowGap) - head.offsetHeight;
      const over = deckSpace.offsetHeight - room;
      if (over < 1) break;
      trim += Math.ceil(over);
      what.style.setProperty("--trim", `${trim}px`);
    }
    fitting = false;
  };
  fitDeck();
  addEventListener("resize", fitDeck);
  document.fonts?.ready.then(fitDeck);

  // The index ticks over like a counter: the old number leaves upward or downward, the new one follows it in.
  let counted = 0;
  const setIndex = (n) => {
    if (n === counted) return;
    const up = n > counted, old = roll.lastElementChild, next = document.createElement("b");
    counted = n;
    next.textContent = String(n + 1);
    roll.append(next);
    const timing = { duration: 260, easing: app.css.enter };
    next.animate([{ transform: `translateY(${up ? 100 : -100}%)` }, { transform: "none" }], timing);
    old.animate([{ transform: "none" }, { transform: `translateY(${up ? -100 : 100}%)` }], timing).finished.catch(() => {}).then(() => old.remove());
    while (roll.children.length > 2) roll.firstElementChild.remove();
  };

  let inDeck = false, place = 0;
  // The sheet on top is the one that plays. While a sheet is turning, and away from the section, none does.
  const updateFront = () => {
    const nearest = Math.round(place), now = inDeck && Math.abs(place - nearest) < .2 ? nearest : -1;
    if (now === front) return;
    front = now;
    tiles.forEach((tile, i) => tile.classList.toggle("is-front", i === front));
    refreshTiles();
  };
  const turn = follow((p) => {
    place = p;
    const nearest = Math.round(p);
    tiles.forEach((tile, i) => {
      // Above zero a sheet lies under others; below zero it has been turned away.
      const d = i - p, turned = clamp(-d), under = clamp(d, 0, 3);
      tile.style.setProperty("--turn", turned.toFixed(4));
      tile.style.setProperty("--under", under.toFixed(4));
      tile.style.setProperty("--o", (d >= 0 ? clamp(2.6 - d) : clamp(1 - (turned - .6) / .3)).toFixed(3));
      tile.style.setProperty("--shade-k", clamp(d).toFixed(3));
      tile.style.visibility = d <= -.9 || d >= 2.6 ? "hidden" : "";
      tile.style.pointerEvents = turned > .2 ? "none" : "";
      if (i === nearest) stepButtons[i].setAttribute("aria-current", "true"); else stepButtons[i].removeAttribute("aria-current");
    });
    stepsBar.style.setProperty("--p", p.toFixed(4));
    setIndex(nearest);
    updateFront();
  }, 110, .0004);
  const enter = follow((e) => deckSpace.style.setProperty("--enter", e.toFixed(4)));
  readers.push(({ y, h }) => {
    const { top, step } = metrics(), raw = clamp((y - top) / step, 0, last), whole = Math.floor(raw);
    // Each sheet rests for a moment before it starts to turn.
    turn.target = Math.min(last, whole + smooth(clamp((raw - whole - .14) / .72)));
    enter.target = smooth(clamp((y + h - top) / (h * .8)));
    inDeck = y + h * .55 > top && y < top + what.offsetHeight - h * .45;
    updateFront();
  });
} else if (window.IntersectionObserver) {
  const seen = new IntersectionObserver((entries) => {
    for (const entry of entries) { if (entry.isIntersecting) shown.add(entry.target); else shown.delete(entry.target); }
    refreshTiles();
  }, { threshold: .5 });
  tiles.forEach((tile) => seen.observe(tile));
}

// Two keys ------------------------------------------------------------------------------------------
const capFor = (e) => (e.key === "Control" ? caps.control : e.key === "Shift" ? caps.shift : e.code === "Digit2" ? caps[2] : e.key === "Escape" ? caps.esc : null);
addEventListener("keydown", (e) => capFor(e)?.classList.add("is-down"));
addEventListener("keyup", (e) => capFor(e)?.classList.remove("is-down"));
addEventListener("blur", () => Object.values(caps).forEach((key) => key.classList.remove("is-down")));
for (const combo of $$(".combo")) {
  combo.addEventListener("click", () => {
    const keys = $$(".key", combo);
    keys.forEach((key, i) => setTimeout(() => key.classList.add("is-down"), i * 45));
    setTimeout(() => keys.forEach((key) => key.classList.remove("is-down")), 190 + keys.length * 45);
    // What the real keys do: Control-Shift-2 only ever starts drawing, Esc goes back, and the stage joins in.
    ink.setMode(combo.dataset.combo === "draw" ? "draw" : "use");
    if (combo.dataset.combo === "draw") claimDraw();
    growing.join();
  });
}

// A toolbar that's yours ------------------------------------------------------------------------------
// The app's Shrink, on a drawing of the toolbar: the outline closes to one button over the app's 0.30 s and opens
// again over its 0.34 s, on its curve, and the two faces cross over as the app's do. The outline is three pieces
// (two ends and a middle), so only transforms move.
const barShape = $("#bar-shape");
if (motion && barShape && window.IntersectionObserver) {
  const [endA, middle, endB] = $$(".edge", barShape), full = $(".bar-face.full", barShape), pill = $(".bar-face.pill", barShape);
  const morph = app.toolbarMorph, PILL = 92, END = 14;
  let visible = false, looping = false;
  const shrink = (closing) => {
    const width = barShape.offsetWidth, shift = (width - PILL) / 2, scale = (PILL - 2 * END) / (width - 2 * END);
    const timing = { duration: (closing ? morph.collapseDuration : morph.expandDuration) * 1000, easing: app.css.standard, fill: "both" };
    const between = (a, b) => (closing ? [a, b] : [b, a]);
    endA.animate({ transform: between("none", `translateX(${shift}px)`) }, timing);
    endB.animate({ transform: between("none", `translateX(${-shift}px)`) }, timing);
    middle.animate({ transform: between("none", `scaleX(${scale})`) }, timing);
    const leaving = [{ opacity: 1 }, { opacity: 0, offset: morph.outgoingEnd }, { opacity: 0 }], arriving = [{ opacity: 0 }, { opacity: 0, offset: morph.incomingStart }, { opacity: 1 }];
    (closing ? full : pill).animate(leaving, { ...timing, easing: app.css.fade });
    return (closing ? pill : full).animate(arriving, { ...timing, easing: app.css.fade }).finished.catch(() => {});
  };
  const loop = async () => {
    if (looping) return;
    looping = true;
    await wait(1400);
    while (visible) {
      await shrink(true);
      await wait(1300);
      await shrink(false);
      await wait(2100);
    }
    looping = false;
  };
  new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; if (visible) loop(); }, { threshold: .6 }).observe(barShape);
}

// Arriving -------------------------------------------------------------------------------------------
if (motion) {
  // Each word of a heading becomes a piece that can come up on its own. An element in a heading counts as one word.
  for (const heading of $$("[data-words]")) {
    const pieces = [];
    for (const node of [...heading.childNodes]) {
      if (node.nodeType === Node.TEXT_NODE) pieces.push(...node.textContent.split(/\s+/).filter(Boolean));
      else if (node.nodeType === Node.ELEMENT_NODE) pieces.push(node);
    }
    heading.textContent = "";
    pieces.forEach((piece, i) => {
      const word = document.createElement("span"), inner = document.createElement("span");
      word.className = "word";
      word.style.setProperty("--w", i);
      inner.append(piece);
      word.append(inner);
      heading.append(word, i < pieces.length - 1 ? " " : "");
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
      if (element.classList.contains("extra")) element.classList.add("is-playing");
      if (element === coffee) setTimeout(() => element.classList.add("is-steaming"), 1100);
      // Once its words have come up, a heading's mask is taken away, so no letter can be cut by it.
      if (element.hasAttribute("data-words")) setTimeout(() => element.classList.add("is-done"), 1500);
      // Once it has landed, a cell answers the pointer without the delay it arrived with.
      setTimeout(() => element.classList.add("is-settled"), 1400 + Number(element.style.getPropertyValue("--i") || 0) * 80);
    }
  }, { rootMargin: "0px 0px -8% 0px", threshold: .12 });
  $$("[data-enter], [data-words], [data-rule], .has-frame").forEach((element) => arrive.observe(element));
  $$(".extra").forEach((extra) => extra.addEventListener("pointerenter", () => { if (extra.classList.contains("is-settled")) restart(extra, "is-playing"); }));
  coffee.addEventListener("pointerenter", () => { if (coffee.classList.contains("is-steaming")) restart(coffee, "is-steaming"); });
}

// Install -------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
for (const button of $$("[data-copy]")) {
  const command = button.closest(".command"), note = $(".copied", command);
  let timer = 0;
  button.addEventListener("click", async () => {
    const code = $("code", command);
    let copied = true;
    try { await navigator.clipboard.writeText(code.textContent.trim()); }
    catch {
      const range = document.createRange();
      range.selectNodeContents(code);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      copied = document.execCommand("copy");
    }
    note.hidden = !copied;
    status.textContent = copied ? note.textContent : "It is selected. Copy it with your keyboard.";
    clearTimeout(timer);
    timer = setTimeout(() => { note.hidden = true; status.textContent = ""; }, 6000);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

schedule();
