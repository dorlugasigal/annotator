import { createInk } from "/concepts/shared/ink.js";
// The app's own motion: its curves, its durations and the keyframes of its moves.
import { ease, toolbarMorph, poseKeyframes, magicFlight, travelFade, visibilityDuration, workspaceTiming, DURATIONS } from "/concepts/shared/app-motion.js";

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
// Calls step with 0 to 1 over a time, once a frame, until it is done or aborted.
const tween = (ms, step, signal) => new Promise((done) => {
  if (!motion || ms <= 0) { step(1); done(); return; }
  const from = performance.now();
  const tick = (now) => {
    if (signal?.aborted) { done(); return; }
    const t = clamp((now - from) / ms, 0, 1);
    step(t);
    if (t < 1) requestAnimationFrame(tick); else done();
  };
  requestAnimationFrame(tick);
});
// Plays one of the app's keyframe lists on an element, then lets go of it.
const play = (el, list, options = {}) => {
  const animation = el.animate(list, { duration: list.duration, easing: list.easing, ...options });
  return animation.finished.catch(() => {});
};
const CORAL = "#F04538", BLUE = "#2B73E0", TEAL = "#00969E", PURPLE = "#7D54B8";

// Where an element sits inside a drawing surface, from the layout and not from the screen. The engine measures
// with getBoundingClientRect, which a turning page distorts; these stand-ins answer in the surface's own space, so
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
    const o = ink.layer.getBoundingClientRect(), b = localBox(el, surface);
    return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h };
  },
});
const centre = (b) => [b.x + b.w / 2, b.y + b.h / 2];

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
  const here = sectionHere();
  let moving = false;
  for (const c of channels) {
    const gap = c.to - (c.value ?? c.to);
    c.value = c.value === null || Math.abs(gap) < 0.0008 ? c.to : c.value + gap * k;
    if (c.value !== c.to) moving = true;
    if (c.value !== c.shown) { c.shown = c.value; c.apply(c.value); }
  }
  markFolio(here);
  if (moving) request(); else lastFrame = 0;
}
function request() { if (!queued) { queued = true; requestAnimationFrame(frame); } }
addEventListener("scroll", request, { passive: true });
addEventListener("resize", request);

// The ticks down the left edge: the one whose section crosses the middle of the screen is marked.
const folio = $$(".folio a").map((link) => ({ link, section: $(link.getAttribute("href")) }));
let folioAt = -1;
function sectionHere() {
  const line = innerHeight * 0.45;
  let here = -1;
  folio.forEach(({ section }, i) => { const r = section.getBoundingClientRect(); if (r.top <= line && r.bottom > line) here = i; });
  return here;
}
function markFolio(here) {
  if (here === folioAt) return;
  folioAt = here;
  folio.forEach(({ link }, i) => { if (i === here) link.setAttribute("aria-current", "true"); else link.removeAttribute("aria-current"); });
}

// 1. The stage -------------------------------------------------------------------------------------
const stage = $("#try"), desk = $("#desk"), dock = $("#dock"), scene = $(".stage-scene"), apps = $("#apps");
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset): curved arrows, the
// roughest line, and the app's Tropic gradient as the color, which is this page's accent. It rests on the Arrow tool
// so its properties row shows that. The engine itself starts the row closed on a phone, which keeps the bar compact.
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, tool: "arrow", gradient: "tropic", moreHref: "#install" });

// Where something on the desk is, in the desk's own space, however the desk leans and wherever a window was dragged
// to. The lean is taken away for the moment of measuring; nothing is painted in between.
function planeBox(el) {
  const leaning = !desk.classList.contains("is-flat");
  if (leaning) desk.classList.add("is-flat");
  const o = ink.layer.getBoundingClientRect(), r = el.getBoundingClientRect();
  if (leaning) desk.classList.remove("is-flat");
  return { x: r.left - o.left, y: r.top - o.top, w: r.width, h: r.height };
}
const onDesk = (el) => ({
  getBoundingClientRect() {
    const o = ink.layer.getBoundingClientRect(), b = planeBox(el);
    return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h };
  },
});

// Theme --------------------------------------------------------------------------------------------
// Dark unless the visitor chose light. The choice is kept; the page's head applies it before the first paint.
const themeSwitch = $(".theme-switch"), themeColor = $('meta[name="theme-color"]');
const PAGE_COLOR = { dark: "#120e14", light: "#fbf6ee" };
function showTheme(theme) {
  root.dataset.theme = theme;
  themeSwitch.setAttribute("aria-checked", String(theme === "dark"));
  themeColor.content = PAGE_COLOR[theme];
  ink.setAppearance(theme);
}
// The page changes like window blinds. It is cut into horizontal bands; from the top down, a slat turns in each
// one: the new theme comes down from the band's top edge while the old one gives way below it, and between the two
// there is a thin gap where the gradient shows. Both themes are pictures the browser took of the page, each
// clipped to one polygon that holds every band, so the stagger is a matter of where each band's edges are.
const BLINDS = { turn: 440, total: 740 };
function blinds(transition) {
  const count = clamp(Math.round(innerHeight / 110), 6, 10), lag = (BLINDS.total - BLINDS.turn) / (count - 1);
  const shape = (edges) => {
    const points = [];
    for (let band = 0; band < count; band++) {
      // A band that is fully open laps a hair over the next one, so no seam shows between them.
      const [from, to] = edges(band), top = ((band + from) / count * 100).toFixed(3), bottom = ((band + to) / count * 100 + (to > from ? 0.02 : 0)).toFixed(3);
      points.push(`0% ${top}%`, `100% ${top}%`, `100% ${bottom}%`, `0% ${bottom}%`);
    }
    return `polygon(${points.join(", ")})`;
  };
  // How far the slat of a band has turned at a moment, on the app's curve for two-way changes, and how wide the gap is then.
  const turned = (band, at) => ease.standard(clamp((at - band * lag) / BLINDS.turn, 0, 1));
  const gap = (t) => Math.sin(Math.PI * t) * 0.12;
  const steps = 40, oldPage = [], newPage = [];
  for (let step = 0; step <= steps; step++) {
    const at = step / steps * BLINDS.total;
    newPage.push(shape((band) => { const t = turned(band, at); return [0, t * (1 - gap(t))]; }));
    oldPage.push(shape((band) => { const t = turned(band, at); return [Math.min(1, t * (1 - gap(t)) + gap(t)), 1]; }));
  }
  const running = [];
  transition.ready.then(() => {
    const timing = { duration: BLINDS.total, easing: "linear", fill: "both" };
    running.push(root.animate({ clipPath: oldPage }, { ...timing, pseudoElement: "::view-transition-old(root)" }));
    running.push(root.animate({ clipPath: newPage }, { ...timing, pseudoElement: "::view-transition-new(root)" }));
  }).catch(() => {});
  // The two animations hold their last frame until the pictures are gone, then they are let go.
  return () => running.forEach((animation) => animation.cancel());
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
  root.classList.add("theme-blinds");
  const transition = document.startViewTransition(() => showTheme(theme));
  const letGo = blinds(transition);
  const stopPassing = passThrough(transition);
  const done = () => { letGo(); stopPassing(); if (turn === themeTurn) root.classList.remove("theme-blinds"); };
  transition.finished.then(done, done);
}
// While the blinds turn, the browser shows a picture of the page and sends every press to the page's root, so
// nothing under the pointer can be pressed. A press ends the change at once and is handed to what is really there,
// so a visitor can pick a tool or start drawing the moment after they switch.
let handing = false;
function passThrough(transition) {
  let pressed = null;
  const down = (e) => {
    if (e.target !== root || !e.isTrusted) return;
    transition.skipTransition();
    const under = document.elementFromPoint(e.clientX, e.clientY);
    if (!under || under === root) return;
    pressed = under;
    handing = true;
    under.dispatchEvent(new PointerEvent("pointerdown", e));
    handing = false;
  };
  const click = (e) => {
    if (e.target !== root || !e.isTrusted || !pressed) return;
    const under = document.elementFromPoint(e.clientX, e.clientY), from = pressed;
    pressed = null;
    if (!under || !(under === from || from.contains(under) || under.contains(from))) return;
    handing = true;
    (under.closest("a, button") || under).click();
    handing = false;
  };
  addEventListener("pointerdown", down, true);
  addEventListener("click", click, true);
  // The click that belongs to a press arrives after the change has ended, so the listeners stay a moment longer.
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

// The lean. The desk lies like a page seen from one side at the top of the page, and faces you once the stage has
// come up the screen; a soft fold of light crosses it on the way. As soon as the visitor draws, it goes flat and
// stays flat: a pointer on a tilted plane would not draw where it points.
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

// The three windows start as one neat pile in the middle of the desk and are dealt to their places, top one first.
const wins = $$(".win", apps);
if (motion) {
  const pile = localBox(apps, desk);
  wins.forEach((win, i) => {
    const b = localBox(win, desk), under = wins.length - 1 - i;
    win.style.setProperty("--dx", `${(pile.x + pile.w / 2 - b.x - b.w / 2 + under * 7).toFixed(1)}px`);
    win.style.setProperty("--dy", `${(pile.y + pile.h / 2 - b.y - b.h / 2 + under * 7).toFixed(1)}px`);
    win.style.setProperty("--dn", under);
  });
}
apps.classList.add("is-dealt");
const dealt = Promise.all(apps.getAnimations({ subtree: true }).map((animation) => animation.finished.catch(() => {})));

// The sample apps ----------------------------------------------------------------------------------
// Three small apps with made-up numbers. In Use apps they answer to the visitor; in Draw they are out of reach.
// The first time one of them is used, the drawings stop following what they were drawn on: from then on they stay
// where they are on the screen, as in the app, whatever changes underneath.
let appsUsed = false;
function used() {
  if (appsUsed) return;
  appsUsed = true;
  for (const m of ink.marks()) m.anchor = null;
}

// The chart: two weeks of sign-ups.
const WEEKS = {
  this: { total: 318, note: "21% fewer than last week", days: [62, 58, 64, 55, 41, 22, 16] },
  last: { total: 402, note: "6% more than the week before", days: [58, 61, 57, 63, 66, 52, 45] },
};
const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const numberEl = $("#t-number"), lineEl = $("#t-line"), deltaEl = $("#chart-delta"), weekButtons = $$("[data-week]"), bars = $$(".bar");
function showWeek(name, quiet) {
  const week = WEEKS[name], changed = numberEl.textContent !== String(week.total);
  weekButtons.forEach((button) => button.setAttribute("aria-pressed", String(button.dataset.week === name)));
  numberEl.textContent = week.total;
  deltaEl.textContent = week.note;
  bars.forEach((bar, i) => {
    const value = week.days[i];
    bar.style.setProperty("--v", value);
    bar.classList.toggle("low", value < 45);
    bar.setAttribute("aria-label", `${DAYS[i]}: ${value} sign-ups`);
    $("b", bar).textContent = value;
  });
  if (quiet) return;
  // The number changes the way the app changes a hint's words: the old one out, the new one in.
  if (changed && motion) numberEl.animate([{ opacity: 0, transform: "translateY(.16em)" }, { opacity: 1, transform: "none" }], { duration: DURATIONS.hintTextCrossfade * 1000 + 120, easing: "cubic-bezier(.2, 0, 0, 1)" });
  used();
}
weekButtons.forEach((button) => button.addEventListener("click", () => showWeek(button.dataset.week)));

// The slide deck.
const slides = $$("[data-slide]"), deckCount = $("#deck-count"), deckPrev = $("#deck-prev"), deckNext = $("#deck-next");
let slideAt = 0;
function showSlide(index, quiet) {
  slideAt = (index + slides.length) % slides.length;
  slides.forEach((slide, i) => slide.classList.toggle("is-on", i === slideAt));
  deckCount.textContent = `${slideAt + 1} / ${slides.length}`;
  if (!quiet) used();
}
deckPrev.addEventListener("click", () => showSlide(slideAt - 1));
deckNext.addEventListener("click", () => showSlide(slideAt + 1));
$$('.todo input').forEach((box) => box.addEventListener("change", used));

// The message thread: a reply becomes a bubble. The first message stays; the thread holds the last five replies.
const thread = $("#thread"), reply = $("#reply"), replyInput = $("input", reply), sendButton = $("button", reply);
function send(text) {
  const words = text.trim();
  if (!words) return;
  const bubble = document.createElement("p");
  bubble.className = "bubble bubble-me";
  bubble.textContent = words;
  thread.append(bubble);
  while (thread.children.length > 6) thread.children[1].remove();
  // The thread keeps its size, as a chat does: the newest message is brought into view.
  thread.scrollTo({ top: thread.scrollHeight, behavior: motion ? "smooth" : "auto" });
  replyInput.value = "";
  // A reply arrives with the app's Pop.
  if (motion) play(bubble, poseKeyframes("pop", { appearing: true }));
  used();
}
reply.addEventListener("submit", (e) => { e.preventDefault(); send(replyInput.value); });
replyInput.addEventListener("input", used);

// Windows can be moved by their title bars, with a pointer or the arrow keys, and stay on the desk.
const moved = new Map(), roomy = matchMedia("(min-width: 600px)");
let raised = 1;
function raise(win) {
  wins.forEach((other) => other.classList.toggle("is-front", other === win));
  win.style.zIndex = ++raised;
}
function place(win, x, y) {
  const b = localBox(win, desk), edge = 6;
  const to = [clamp(x, edge - b.x, desk.clientWidth - edge - b.x - b.w), clamp(y, edge - b.y, desk.clientHeight - edge - b.y - b.h)];
  moved.set(win, to);
  win.style.setProperty("--wx", `${to[0].toFixed(1)}px`);
  win.style.setProperty("--wy", `${to[1].toFixed(1)}px`);
}
const NUDGE = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowDown: [0, 1], ArrowUp: [0, -1] };
for (const win of wins) {
  const grip = $(".win-grip", win);
  let from = null;
  grip.addEventListener("pointerdown", (e) => {
    if (!roomy.matches || e.button) return;
    const [x, y] = moved.get(win) || [0, 0];
    from = { px: e.clientX, py: e.clientY, x, y };
    grip.setPointerCapture(e.pointerId);
    raise(win);
    win.classList.remove("is-homing");
    win.classList.add("is-held");
  });
  grip.addEventListener("pointermove", (e) => {
    if (!from) return;
    place(win, from.x + e.clientX - from.px, from.y + e.clientY - from.py);
    used();
  });
  const drop = () => { from = null; win.classList.remove("is-held"); };
  grip.addEventListener("pointerup", drop);
  grip.addEventListener("pointercancel", drop);
  grip.addEventListener("keydown", (e) => {
    const step = NUDGE[e.key];
    if (!step || !roomy.matches) return;
    e.preventDefault();
    const [x, y] = moved.get(win) || [0, 0];
    raise(win);
    win.classList.remove("is-homing");
    place(win, x + step[0] * 16, y + step[1] * 16);
    used();
  });
}
function homeWindows() {
  for (const win of wins) {
    if (moved.has(win)) win.classList.add("is-homing");
    win.style.removeProperty("--wx");
    win.style.removeProperty("--wy");
    win.style.zIndex = "";
    win.classList.remove("is-front", "is-held");
  }
  moved.clear();
}
// A window that was moved stays on the desk when the desk changes size; stacked on a phone, the windows go home.
addEventListener("resize", () => {
  if (!roomy.matches) { homeWindows(); return; }
  for (const [win, [x, y]] of moved) place(win, x, y);
});
// Everything back as it was when the page opened.
function resetApps() {
  appsUsed = false;
  showWeek("this", true);
  showSlide(0, true);
  $$(".todo input").forEach((box) => { box.checked = false; });
  while (thread.children.length > 1) thread.lastElementChild.remove();
  thread.scrollTop = 0;
  replyInput.value = "";
  replyInput.classList.remove("is-typing");
  homeWindows();
}
showWeek("this", true);

// The mode -----------------------------------------------------------------------------------------
// The toolbar's Draw button is the switch. The chip on the stage, the line under it and the keys section all show
// what it says. In Draw the apps are out of reach, to a pointer and to the keyboard alike.
const keysSection = $("#keys"), done = $("#stage-done"), replay = $("#stage-replay");
let run = null;
function showMode(mode = ink.state.mode) {
  stage.dataset.mode = mode;
  keysSection.dataset.mode = mode;
  apps.inert = mode === "draw";
  done.hidden = mode !== "draw" || Boolean(run);
}
ink.on("mode", showMode);
showMode();
done.addEventListener("click", () => ink.setMode("use"));

// The invitation: "Your turn", with a small pointer drawing a squiggle. It comes after the first round and whenever
// the demo is idle, with the app's Pop, and goes for good once the visitor has drawn something.
const cue = $("#your-turn");
let drawn = false, invited = false;
function invite(strong) {
  if (drawn) return;
  if (!invited) {
    invited = true;
    cue.hidden = false;
    if (motion) play(cue, poseKeyframes("pop", { appearing: true }));
  }
  if (strong && motion) {
    // The edge of the stage lights up once.
    desk.classList.remove("is-cued");
    void desk.offsetWidth;
    desk.classList.add("is-cued");
  }
}
ink.on("mark", async () => {
  if (drawn) return;
  drawn = true;
  desk.classList.remove("is-cued");
  if (!invited) return;
  if (motion) await play(cue, poseKeyframes("pop", { appearing: false }), { fill: "forwards" });
  cue.hidden = true;
  cue.getAnimations().forEach((animation) => animation.cancel());
});

// The scripted demo ---------------------------------------------------------------------------------
// It tells the app's story with a pointer of its own: draw, switch to the apps and use them while the drawings stay
// put, switch back and draw again. It runs until the visitor does anything on the stage, and after that only when
// they ask for it with Replay.
const number = onDesk(numberEl);
const markSize = () => (desk.clientWidth < 600 ? { px: 12, py: 8, size: 3, small: true } : { px: 17, py: 11, size: 3.5, small: false });
function drawSteps() {
  const { px, py, size } = markSize();
  // The arrow runs from the line on the slide to the number: sideways when the windows sit side by side; when they
  // are stacked it comes down round the right and meets the ring's right side, clear of the week switcher.
  const ends = () => {
    const l = planeBox(lineEl), n = planeBox(numberEl);
    return n.x > l.x + l.w
      ? [[l.x + l.w + 10, l.y + l.h * 0.3], [n.x - px - 9, n.y + n.h * 0.42]]
      : [[l.x + l.w - 4, l.y + l.h + 9], [n.x + n.w + px + 9, n.y + n.h * 0.2]];
  };
  // The ring and the arrow name no color: they take the one selected in the toolbar. Both are drawn at the
  // toolbar's strongest roughness, and the arrow bows well clear of a straight line.
  return [
    { tool: "ellipse", around: number, padX: px, padY: py, size, rough: 6, duration: 820, pause: 240 },
    { tool: "arrow", from: () => ends()[0], to: () => ends()[1], bend: 0.42, size, rough: 6, duration: 760, pause: 260 },
  ];
}
function labelSteps() {
  const { px, small } = markSize();
  return [{ tool: "text", at: { el: number, at: "right", dx: px + 12, dy: small ? 2 : -18 }, text: "why the dip?", color: BLUE, pause: 200 }];
}

// The pointer's click, as the app shows it: a ring that swells and settles around the pointer.
const click = () => { if (motion) ink.clickGhost?.(); };
// What the scripted pointer can do to the stage: press something, type, drag a window. The pointer is placed in
// the desk's own space, so it lands on its target while the desk leans, and it never takes the keyboard's focus:
// a press is the element's own click, and typing fills the field without opening a phone's keyboard.
async function clickOn(el, signal) {
  if (signal.aborted) return;
  await ink.moveGhost(centre(planeBox(el)));
  if (signal.aborted) return;
  click();
  el.classList.add("is-pressed");
  await wait(170, signal);
  el.classList.remove("is-pressed");
  if (!signal.aborted) el.click();
  await wait(240, signal);
}
async function typeInto(input, text, signal) {
  if (signal.aborted) return;
  const b = planeBox(input);
  await ink.moveGhost([b.x + 16, b.y + b.h / 2]);
  if (signal.aborted) return;
  click();
  input.classList.add("is-typing");
  for (const letter of text) {
    if (signal.aborted) break;
    input.value += letter;
    used();
    await wait(48, signal);
  }
  await wait(220, signal);
  input.classList.remove("is-typing");
}
async function dragWindow(win, [dx, dy], signal) {
  if (signal.aborted) return;
  const from = centre(planeBox($(".win-grip", win))), [x0, y0] = moved.get(win) || [0, 0];
  await ink.moveGhost(from);
  if (signal.aborted) return;
  click();
  raise(win);
  win.classList.remove("is-homing");
  win.classList.add("is-held");
  await wait(180, signal);
  await tween(700, (t) => {
    const k = ease.standard(t);
    place(win, x0 + dx * k, y0 + dy * k);
    const [x, y] = moved.get(win);
    ink.placeGhost([from[0] + x - x0, from[1] + y - y0]);
  }, signal);
  win.classList.remove("is-held");
  used();
}
// The switch between Draw and Use apps is the toolbar's own button, pressed by the pointer.
async function switchTo(mode, signal) {
  if (signal.aborted || ink.state.mode === mode) return;
  const button = ink.toolbar.querySelector('[data-action="mode"]');
  if (button?.offsetParent && !ink.collapsed) await clickOn(button, signal);
  if (!signal.aborted && ink.state.mode !== mode) ink.setMode(mode);
  await wait(420, signal);
}
// The app's own moves, done by the engine: Shrink (the toolbar becomes its one button and opens again) and Hide
// drawings and tools, in the styles the app offers. Magic comes first.
const STYLES = ["magic", "pop", "slide", "drop", "corners", "focus"];
let shrunk = false;
async function shrink(on) {
  shrunk = on;
  await (on ? (ink.shrink ? ink.shrink() : ink.setCollapsed(true)) : (ink.expand ? ink.expand() : ink.setCollapsed(false)));
}
// While they are hidden the toolbar is gone too, as in the app; the demo brings everything back itself.
const hide = (hidden, style) => Promise.resolve(ink.setHidden(hidden, { style, pill: false }));
// Everything the demo hid or shrank is brought back at once.
function restore() {
  if (ink.state.hidden) ink.setHidden(false, { animate: false });
  if (shrunk) { shrunk = false; if (ink.collapsed) ink.setCollapsed(false, { animate: false }); }
}

// One round of the story.
async function round(index, signal) {
  const pause = (ms) => wait(ms, signal), style = STYLES[index % STYLES.length];
  resetApps();
  await pause(index ? 400 : 700);
  if (signal.aborted) return;
  // Draw: a ring around the number, and an arrow to it.
  ink.setMode("draw");
  await ink.perform(drawSteps(), { cursor: true, showTool: true, signal });
  // Use apps: the week changes and comes back, the deck moves on, a reply is sent, a window is moved. The ring and
  // the arrow stay where they are.
  await switchTo("use", signal);
  await clickOn(weekButtons[0], signal);
  await pause(1000);
  await clickOn(weekButtons[1], signal);
  await pause(500);
  await clickOn(deckNext, signal);
  await pause(600);
  await typeInto(replyInput, "Sure, Monday works.", signal);
  await clickOn(sendButton, signal);
  await pause(500);
  if (roomy.matches) { await dragWindow(wins[2], [16, 38], signal); await pause(400); }
  // Draw again: a note beside the ring.
  await switchTo("draw", signal);
  if (signal.aborted) return;
  await ink.perform(labelSteps(), { cursor: true, showTool: true, anchored: !appsUsed, signal });
  ink.hideGhost();
  keepChoice();
  await pause(1500);
  if (signal.aborted) return;
  // Hide drawings and tools, and back: the drawings leave and return the way the app moves them.
  await hide(true, style);
  await pause(500);
  if (signal.aborted) return;
  await hide(false, style);
  await pause(1200);
  if (signal.aborted) return;
  // A calm moment: the toolbar shrinks to its one button, and opens again.
  await shrink(true);
  await pause(1200);
  if (signal.aborted) return;
  await shrink(false);
  await pause(800);
  if (signal.aborted) return;
  // The round ends with the app's hide; the next one starts on a clean desk, brought back with the app's show.
  await hide(true, style);
  if (signal.aborted) return;
  ink.clear();
  resetApps();
  await pause(250);
  await hide(false, style);
}
// Puts the stage back in order after a round that was cut short because the stage left the screen.
function tidy() {
  ink.hideGhost();
  restore();
  ink.setMode("use");
  ink.clear();
  resetApps();
}

// The tool the visitor chose. A script that shows its tools in the toolbar puts back the tool it started with when
// it ends; if the visitor picked one meanwhile, theirs is put back instead.
let chosen = null;
ink.on("tool", (tool) => { chosen = tool; });
const keepChoice = () => { if (chosen && ink.state.tool !== chosen) ink.setTool(chosen, ink.state.mode !== "draw"); };

let deskSeen = false, seenWaiters = [], leaveView = null, settled = Promise.resolve();
const setRunning = (running) => {
  replay.dataset.state = running ? "playing" : "";
  $("b", replay).textContent = running ? "Pause" : "Replay";
  desk.classList.toggle("is-scripted", running);
  showMode();
};
const whenSeen = (signal) => new Promise((go) => {
  if (deskSeen || signal.aborted) { go(); return; }
  seenWaiters.push(go);
  signal.addEventListener("abort", go, { once: true });
});
async function loop(signal) {
  let index = 0;
  while (!signal.aborted) {
    await whenSeen(signal);
    if (signal.aborted) break;
    // A round also ends when the stage leaves the screen; it starts again from the top when the stage is back.
    const part = new AbortController(), cut = () => part.abort();
    signal.addEventListener("abort", cut, { once: true });
    leaveView = cut;
    await round(index, part.signal);
    leaveView = null;
    signal.removeEventListener("abort", cut);
    if (signal.aborted) break;
    if (part.signal.aborted) { tidy(); continue; }
    index++;
    invite();
    await wait(500, signal);
  }
}
function start() {
  if (run) return;
  const abort = new AbortController();
  const finished = loop(abort.signal).finally(() => { ink.hideGhost(); restore(); keepChoice(); if (run?.abort === abort) { run = null; setRunning(false); } });
  run = { abort, finished };
  setRunning(true);
}
function stop() {
  if (!run) return settled;
  const { abort, finished } = run;
  run = null;
  abort.abort();
  setRunning(false);
  invite();
  settled = finished;
  return settled;
}
async function restart() {
  await stop();
  restore();
  ink.setMode("use");
  if (ink.collapsed) ink.setCollapsed(false);
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
  chosen = null;
  userFlat = !motion;
  request();
  if (ink.marks().length) { await hide(true); ink.clear(); await hide(false); }
  resetApps();
  if (motion) start(); else still();
}
// Without motion the stage shows the end of the story, with nothing moving.
async function still() {
  await ink.perform([...drawSteps(), ...labelSteps()], { cursor: false });
  ink.reflow();
  invite();
}
replay.addEventListener("click", () => { if (run) { stop(); ink.setMode("use"); } else restart(); });

// The visitor takes over: the demo stops where it is, and never starts again by itself.
function takeOver() {
  touched = true;
  stop();
  if (!userFlat) { userFlat = true; request(); }
}
// A press is the visitor's when the browser says so, or when it was handed on from the middle of a theme change.
const theirs = (e) => e.isTrusted || handing;
ink.toolbar.addEventListener("pointerdown", (e) => { if (theirs(e)) takeOver(); }, true);
ink.toolbar.addEventListener("keydown", (e) => { if (theirs(e) && (e.key === "Enter" || e.key === " ")) takeOver(); }, true);
apps.addEventListener("pointerdown", (e) => { if (theirs(e)) takeOver(); }, true);
apps.addEventListener("keydown", (e) => { if (theirs(e) && e.key !== "Tab" && e.key !== "Shift") takeOver(); }, true);
// While the demo itself is drawing, a press on the stage goes through the drawing layer (see style.css), so a finger
// can still scroll the page. A click there means "let me": the demo stops and the stage is theirs, ready to draw.
desk.addEventListener("click", (e) => {
  if (!theirs(e) || !run || ink.state.mode !== "draw" || ink.toolbar.contains(e.target)) return;
  takeOver();
  invite(true);
});
// The keys that work the demo take it over too.
addEventListener("keydown", (e) => {
  if (!theirs(e) || !run || !deskSeen) return;
  const toggle = e.ctrlKey && e.shiftKey && e.code === "Digit2";
  const drawing = ink.state.mode === "draw" && !e.ctrlKey && !e.metaKey && !e.altKey && (e.key === "Escape" || (e.key.length === 1 && e.key !== " "));
  if (toggle || drawing) takeOver();
}, true);
addEventListener("resize", () => setTimeout(() => ink.reflow(), 260));

new IntersectionObserver(([entry]) => {
  deskSeen = entry.isIntersecting && entry.intersectionRatio >= 0.3;
  if (deskSeen) seenWaiters.splice(0).forEach((go) => go());
  // Off the screen, a round ends and drawing ends, so keys and touches go back to the page.
  if (!entry.isIntersecting) { leaveView?.(); ink.setMode("use"); }
}, { threshold: [0, 0.3] }).observe(scene);

const fontsReady = Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {});
let touched = false;
fontsReady.then(async () => {
  // The demo waits until the last window has been dealt.
  if (!motion) { still(); return; }
  await dealt;
  await wait(350);
  if (!touched) start();
});

// "Try it": from anywhere on the page, the stage comes into view with Draw on and the Pen in hand.
const header = $(".site-header");
function scrolled() {
  return new Promise((done) => {
    let last = scrollY, calm = 0;
    const started = performance.now();
    const check = () => {
      calm = scrollY === last ? calm + 1 : 0;
      last = scrollY;
      if (calm > 6 || performance.now() - started > 2200) done(); else requestAnimationFrame(check);
    };
    requestAnimationFrame(check);
  });
}
async function tryIt(e) {
  e.preventDefault();
  touched = true;
  takeOver();
  await settled;
  const r = scene.getBoundingClientRect(), top = header.offsetHeight + 14;
  if (r.top < top - 20 || r.top > innerHeight * 0.45) {
    scrollTo({ top: scrollY + r.top - top, behavior: motion ? "smooth" : "auto" });
    await scrolled();
  }
  if (ink.collapsed) ink.setCollapsed(false);
  ink.setTool("pen");
  invite(true);
  ink.toolbar.querySelector('[data-tool="pen"]')?.focus({ preventScroll: true });
}
$$("[data-try]").forEach((link) => link.addEventListener("click", tryIt));

// 2. What you can do -------------------------------------------------------------------------------
// Every picture is drawn live by the engine on a small sample screen.
const measure = document.createElement("canvas").getContext("2d");
const textSize = (shot) => Math.round(clamp(shot.clientWidth * 0.058, 15, 36));
const ghostOf = (layer) => $(".ink-ghost", layer.layer);
// Spotlight follows the visitor's pointer; in a scripted scene it follows the scripted one.
function light(layer, shot, on, at) {
  layer.setStyle("spotlight", { radius: Math.round(shot.clientWidth * 0.15) });
  layer.setSpotlight(on);
  layer.layer.classList.toggle("has-pointer", on);
  if (!on) return;
  layer.layer.style.setProperty("--px", `${at[0]}px`);
  layer.layer.style.setProperty("--py", `${at[1]}px`);
  if (motion) layer.placeGhost(at);
}
async function glide(layer, to) {
  const ghost = ghostOf(layer);
  let moving = true;
  const follow = () => {
    layer.layer.style.setProperty("--px", `${ghost.dataset.x}px`);
    layer.layer.style.setProperty("--py", `${ghost.dataset.y}px`);
    if (moving) requestAnimationFrame(follow);
  };
  requestAnimationFrame(follow);
  await layer.moveGhost(to, 900);
  moving = false;
  follow();
}
const SCENES = {
  point: (t, shot) => {
    const u = shot.clientWidth / 100;
    return [
      { tool: "ellipse", around: t("kpi"), padX: 2.6 * u, padY: 1.2 * u, size: 3, rough: 6, duration: 760, pause: 240 },
      { tool: "arrow", from: { el: t("kpi"), at: "bottom", dx: 2 * u, dy: 1.2 * u + 7 }, to: { el: t("dip"), at: "top", dx: -4, dy: -7 }, bend: 0.3, size: 3, rough: 6, duration: 720 },
    ];
  },
  // Smart Draw: a rough box, tidied into a neat one, and a note beside it.
  box: (t, shot, layer) => {
    const u = shot.clientWidth / 100, rough = { tool: "rect", around: t("pay"), pad: 1.8 * u, color: CORAL, size: 3, rough: 6, duration: 900, pause: 380 };
    return [
      rough,
      { run: () => {
        const mark = layer.marks().at(-1);
        if (!mark) return;
        const { x, y, w, h } = layer.box(mark);
        layer.remove(mark.id);
        layer.add({ type: "rect", x, y, w, h, color: CORAL, size: 3, rough: 0, round: 6, anchor: rough });
        const neat = $$(".ink-mark", layer.layer).at(-1);
        if (motion && neat) neat.animate([{ opacity: 0.2 }, { opacity: 1 }], { duration: DURATIONS.snapMorph * 1000, easing: "cubic-bezier(.2, 0, 0, 1)" });
      } },
      { wait: 320 },
      { tool: "text", at: { el: t("pay"), at: "right", dx: 3.6 * u, dy: -textSize(shot) * 0.6 }, text: "make this bigger", color: BLUE, speed: 44 },
    ];
  },
  blur: (t, shot) => {
    const pad = Math.max(3, shot.clientWidth / 110);
    return [
      { tool: "blur", over: t("email"), pad, amount: 13, duration: 700, pause: 380 },
      { tool: "blur", over: t("card"), pad, amount: 13, duration: 700 },
    ];
  },
  spotlight: (t, shot, layer) => {
    const at = (name) => centre(localBox($(`[data-t="${name}"]`, shot), shot));
    if (!motion) return [{ run: () => light(layer, shot, true, at("s2")) }];
    return [
      { run: () => light(layer, shot, true, at("s1")) }, { wait: 500 },
      { run: () => glide(layer, at("s2")) }, { wait: 420 },
      { run: () => glide(layer, at("s6")) }, { wait: 420 },
      { run: () => glide(layer, at("s4")) }, { wait: 700 },
    ];
  },
  steps: (t) => ["p1", "p2", "p3"].map((pin, i) => ({ tool: "counter", at: { el: t(pin) }, n: i + 1, color: BLUE, pause: 360 })),
  system: (t, shot) => {
    // A label sits in the middle of its shape, whatever size the hand-written text is at this width.
    const label = (pin, text, color) => ({ tool: "text", text, color, speed: 50, pause: 120, at: () => {
      const b = localBox($(`[data-t="${pin}"]`, shot), shot), size = textSize(shot);
      measure.font = `600 ${size}px "Caveat"`;
      return [b.x + b.w / 2 - measure.measureText(text).width / 2 - 4, b.y + b.h / 2 - size * 0.56];
    } });
    const link = (a, b) => ({ tool: "arrow", from: { el: t(a), at: "right", dx: 7 }, to: { el: t(b), at: "left", dx: -9 }, bend: 0, color: PURPLE, size: 3, duration: 380, pause: 100 });
    return [
      { tool: "rect", around: t("n1"), pad: 0, color: BLUE, size: 3, duration: 520 }, label("n1", "app", BLUE), link("n1", "n2"),
      { tool: "rect", around: t("n2"), pad: 0, color: TEAL, size: 3, duration: 520 }, label("n2", "API", TEAL), link("n2", "n3"),
      { tool: "ellipse", around: t("n3"), pad: 0, color: CORAL, size: 3, duration: 620 }, label("n3", "data", CORAL),
    ];
  },
};
// A spread's live picture: a small scene drawn by the pointer while the spread is open.
function sketch(card, shot) {
  const layer = createInk({ surface: shot, interactive: false, gradient: "tropic" });
  const target = (name) => standIn($(`[data-t="${name}"]`, shot), layer, shot);
  const fit = () => layer.layer.style.setProperty("--ink-text", `600 ${textSize(shot)}px/1.1 "Caveat", "Bradley Hand", cursive`);
  const wipe = () => { layer.clear(); if (layer.state.spotlight) light(layer, shot, false); };
  let busy = false, shown = false;
  async function draw() {
    if (busy) return;
    busy = true;
    wipe();
    shot.classList.remove("is-leaving");
    fit();
    await fontsReady;
    await wait(motion ? 260 : 0);
    await layer.perform(SCENES[shot.dataset.scene](target, shot, layer), { cursor: motion });
    await wait(500);
    layer.hideGhost();
    if (!layer.state.spotlight) layer.reflow();
    busy = false;
    if (!shown && motion) leave();
  }
  // A page that has been turned loses its drawing quietly, so it starts clean the next time.
  async function leave() {
    shot.classList.add("is-leaving");
    await wait(280);
    if (!shown && !busy) wipe();
  }
  addEventListener("resize", () => { fit(); setTimeout(() => { if (!busy) layer.reflow(); }, 260); });
  $(".ctl", card).addEventListener("click", draw);
  if (motion) $(".frame", card).addEventListener("pointerenter", (e) => { if (e.pointerType === "mouse" && shown) draw(); });
  return { show() { if (shown) return; shown = true; draw(); }, hide() { if (!shown) return; shown = false; if (motion && !busy) leave(); } };
}
const cards = $$(".book .card");
const visuals = cards.map((card) => sketch(card, $(".shot", card)));

// The book is pinned and its pages turn while there is motion and the window is tall enough to hold a spread.
const tall = matchMedia("(min-height: 560px)");
if (motion) {
  // Progress through the track turns into one number, 0 for the first spread to 5 for the last; each spread rests
  // open for a while around its own number, and a page turns between.
  // On a wide screen a spread is two pages, words on the left and the picture on the right, and a turn is one leaf
  // going round the spine: the picture page lifts and swings to the middle, and from there what you see is its
  // other side, the next spread's words, coming down on the left. The two halves of that leaf are separate
  // elements, each shown for its half of the turn. On a tall or narrow screen a spread is one page, and it lifts
  // from the bottom like a page of a notepad. Either way main.js writes how far each leaf has turned and where
  // the shade it throws falls; style.css does the rest.
  const track = $(".do-track"), tabs = $$(".do-tabs button"), rest = 0.2, last = cards.length - 1;
  const wide = matchMedia("(min-width: 900px) and (min-aspect-ratio: 1/1)");
  const leaves = cards.map((card) => ({ card, words: $(".page-text", card), figure: $(".page-fig", card) }));
  let front = -1, bookSeen = false, pinned = null;
  const face = (index) => {
    if (index === front) return;
    front = index;
    cards.forEach((card, i) => { card.inert = i !== index; (i === index ? visuals[i].show : visuals[i].hide)(); });
    tabs.forEach((tab, i) => tab.setAttribute("aria-current", String(i === index)));
  };
  cards.forEach((card, i) => card.style.setProperty("--i", i));
  const show = (el, visible) => { const value = visible ? "" : "hidden"; if (el.style.visibility !== value) el.style.visibility = value; };
  const book = channel(() => {
    const r = track.getBoundingClientRect();
    return clamp(-r.top / Math.max(1, r.height - innerHeight), 0, 1) * last;
  }, (at) => {
    if (!pinned) return;
    const open = Math.min(last, Math.floor(at)), spread = wide.matches;
    const turned = cards.map((_, i) => smooth((clamp(at - i, 0, 1) - rest) / (1 - 2 * rest)));
    leaves.forEach(({ card, words, figure }, i) => {
      const out = turned[i], arriving = i ? turned[i - 1] : 1, near = i === open || (i === open + 1 && turned[open] > 0);
      // How far round each leaf is, as an angle: a full half turn for a spread, a quarter and a bit for a notepad page.
      const mine = out * (spread ? Math.PI : Math.PI * 0.556), theirs = arriving * (spread ? Math.PI : Math.PI * 0.556);
      const reach = (angle) => Math.min(1, Math.abs(Math.cos(angle)) + 0.3 * Math.sin(angle));
      const set = (name, value) => card.style.setProperty(name, value.toFixed(4));
      set("--out", out);
      set("--in", arriving);
      // The shade on a page while it is in the air: on the picture as it lifts, on the words as they come down.
      set("--so", spread ? (out < 0.5 ? Math.sin(mine) : 0) : Math.sin(Math.min(mine, Math.PI / 2)));
      set("--si", spread && arriving > 0.5 ? Math.sin(theirs) : 0);
      // The shade a page in the air throws: on the picture under it as it lifts, on the words it comes down onto.
      const lifting = arriving > 0 && arriving < (spread ? 0.5 : 0.9);
      set("--uw", lifting ? reach(theirs) : 0);
      set("--uo", lifting ? (spread ? 1 : 1 - smooth((arriving - 0.55) / 0.35)) : 0);
      set("--cw", spread && out > 0.5 && out < 1 ? reach(mine) : 0);
      set("--co", spread && out > 0.5 ? 1 - smooth((out - 0.78) / 0.22) : 0);
      if (spread) {
        show(card, true);
        show(figure, near && out < 0.5);
        show(words, near && arriving > 0.5);
      } else {
        show(card, near && out < 0.9);
        show(figure, true);
        show(words, true);
      }
    });
    if (bookSeen) face(Math.round(at));
  });
  // In a window too short for a spread the book is a plain list, and every picture is drawn.
  const pin = () => {
    if (pinned === tall.matches) return;
    pinned = tall.matches;
    front = -1;
    if (pinned) { book.shown = null; request(); return; }
    for (const { card, words, figure } of leaves) { card.inert = false; for (const el of [card, words, figure]) el.style.visibility = ""; }
    tabs.forEach((tab) => tab.setAttribute("aria-current", "false"));
    visuals.forEach((visual) => visual.show());
  };
  tall.addEventListener("change", pin);
  wide.addEventListener("change", () => { book.shown = null; request(); });
  pin();
  new IntersectionObserver(([entry]) => {
    bookSeen = entry.isIntersecting;
    if (!pinned) return;
    if (bookSeen) face(Math.round(book.value ?? 0)); else { face(-1); cards.forEach((card) => { card.inert = false; }); }
  }, { threshold: 0.25 }).observe($(".book"));
  tabs.forEach((tab, i) => tab.addEventListener("click", () => {
    const r = track.getBoundingClientRect();
    scrollTo({ top: r.top + scrollY + (r.height - innerHeight) * (i / last) + 1, behavior: "smooth" });
  }));
} else {
  // Without motion the spreads are a plain list, and every picture shows its last frame.
  visuals.forEach((visual) => visual.show());
}

// 3. Two keys --------------------------------------------------------------------------------------
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
  touched = true;
  takeOver();
  for (const key of keys) { key.classList.add("is-down"); await wait(motion ? 70 : 0); }
  if (ink.collapsed) ink.setCollapsed(false);
  if (set.dataset.set === "draw") ink.setMode(ink.state.mode === "draw" ? "use" : "draw"); else ink.setMode("use");
  await wait(160);
  keys.forEach((key) => key.classList.remove("is-down"));
});

// 4. More in the app -------------------------------------------------------------------------------
// The app's Shrink, live: the bar's frame and corners follow the app's curve into the one button, the full face is
// gone by half way and the button's face comes in from 35%, in 0.30 s; back in 0.34 s.
const bar = $("#tb-morph"), barFull = $(".tb-full", bar), barOne = $(".tb-one", bar);
function morphBar(collapse) {
  const from = collapse ? barFull : barOne, to = collapse ? barOne : barFull;
  const a = { w: from.offsetWidth, h: from.offsetHeight }, b = { w: to.offsetWidth, h: to.offsetHeight };
  const shape = [], leaving = [], arriving = [];
  for (let step = 0; step <= 30; step++) {
    const t = step / 30, p = toolbarMorph.shape(t), mix = (x, y) => x + (y - x) * p;
    shape.push({ offset: t, width: `${mix(a.w, b.w).toFixed(2)}px`, height: `${mix(a.h, b.h).toFixed(2)}px`, borderRadius: `${(mix(a.h, b.h) * (collapse ? mix(0.26, 0.5) : mix(0.5, 0.26))).toFixed(2)}px` });
    leaving.push({ offset: t, opacity: toolbarMorph.outgoingOpacity(t) });
    arriving.push({ offset: t, opacity: toolbarMorph.incomingOpacity(t) });
  }
  const timing = { duration: (collapse ? toolbarMorph.collapseDuration : toolbarMorph.expandDuration) * 1000, fill: "both" };
  bar.animate(shape, timing);
  from.animate(leaving, timing);
  return to.animate(arriving, timing).finished.catch(() => {});
}
// Open again, the bar goes back to its own size, so it follows the plate when the window changes.
const restBar = () => [bar, barFull, barOne].forEach((el) => el.getAnimations().forEach((animation) => animation.cancel()));
if (motion) (async () => {
  const plate = bar.closest(".ill");
  addEventListener("resize", restBar);
  for (;;) {
    await wait(2200);
    if (!plate.classList.contains("is-live")) continue;
    await morphBar(true);
    await wait(1500);
    await morphBar(false);
    restBar();
  }
})();

// Arrivals -----------------------------------------------------------------------------------------
// Headings are split into words, each behind its own mask, so a line can slide up from under its baseline; the
// text stays the same for a screen reader. Which line a word is on is read when the heading arrives.
const words = new Map();
for (const heading of $$("[data-rise]")) {
  const list = [];
  const mask = (content) => {
    const word = document.createElement("span"), inner = document.createElement("span");
    word.className = "w";
    inner.className = "wi";
    inner.append(content);
    word.append(inner);
    list.push(word);
    return word;
  };
  for (const node of [...heading.childNodes]) {
    if (node.nodeType === Node.TEXT_NODE) {
      node.replaceWith(...node.textContent.split(/(\s+)/).filter(Boolean).map((part) => (/^\s+$/.test(part) ? document.createTextNode(" ") : mask(part))));
    } else {
      const hold = document.createComment("");
      node.replaceWith(hold);
      hold.replaceWith(mask(node));
    }
  }
  words.set(heading, list);
}
function setLines(heading) {
  let line = -1, top = null;
  for (const word of words.get(heading) || []) {
    if (word.offsetTop !== top) { top = word.offsetTop; line++; }
    word.style.setProperty("--l", line);
  }
}
// The headline's italic word is split once more, into letters that settle one after another. Each letter carries
// its own piece of the gradient (where it sits in the word, in ems), so the word still reads as one sweep of color.
const markWord = $(".mark-word b");
const letters = [...markWord.textContent].map((letter, i) => {
  const span = document.createElement("span");
  span.className = "ch";
  span.style.setProperty("--c", i);
  span.textContent = letter;
  return span;
});
markWord.replaceChildren(...letters);
function paintLetters() {
  const size = parseFloat(getComputedStyle(markWord).fontSize) || 1;
  markWord.style.setProperty("--ww", `${(markWord.offsetWidth / size).toFixed(4)}em`);
  for (const letter of letters) letter.style.setProperty("--lx", `${(letter.offsetLeft / size).toFixed(4)}em`);
}
paintLetters();
document.fonts?.ready.then(paintLetters);

// Things in a grid rise one column after another, left to right, and one row after another.
function setColumns(group) {
  const items = [...group.children], lefts = [...new Set(items.map((item) => item.offsetLeft))].sort((a, b) => a - b);
  const tops = [...new Set(items.map((item) => item.offsetTop))].sort((a, b) => a - b);
  for (const item of items) {
    item.style.setProperty("--c", lefts.indexOf(item.offsetLeft));
    item.style.setProperty("--r", tops.indexOf(item.offsetTop));
  }
}
// The extras arrive the way drawings come back in the app's Magic style: each on a swoop of its own, one after
// another, in the app's time and on its curve.
function flyIn(group) {
  const items = [...group.children], { objectStep } = workspaceTiming("magic", true, items.length);
  items.forEach((item, i) => {
    const flight = magicFlight({ x: -Math.min(150, item.offsetWidth * 0.4), y: 64, scale: 0.9 }, [0, 0], `extra ${i}`);
    const timing = { duration: visibilityDuration("magic", true) * 1000, delay: i * objectStep * 2 * 1000, easing: flight.easing, fill: "backwards" };
    item.animate(flight, timing);
    item.animate(travelFade(true, true), timing);
  });
}
const arrive = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) {
    const el = entry.target;
    if (el.matches("[data-rise]")) setLines(el);
    if (el.matches(".rise-group")) setColumns(el);
    if (el.matches("[data-flights]") && motion) flyIn(el);
    el.classList.add("is-in");
    arrive.unobserve(el);
  }
}, { rootMargin: "0px 0px -8% 0px", threshold: 0.12 });
$$("[data-rise], .rise-group, [data-flights], .rule, .coffee .cup, .try-line").forEach((el) => arrive.observe(el));

// Loops run only while they are on screen.
const live = new IntersectionObserver((entries) => {
  for (const entry of entries) entry.target.classList.toggle("is-live", entry.isIntersecting);
}, { threshold: 0.3 });
$$(".ill, .coffee").forEach((el) => live.observe(el));

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

// Install ------------------------------------------------------------------------------------------
// Copy a command. Where the clipboard is not allowed, the command is selected so it can be copied by hand.
const status = $("#status");
for (const button of $$("[data-copy]")) {
  let timer = 0;
  button.addEventListener("click", async () => {
    const command = button.closest(".command"), code = $("code", command);
    let copied = true;
    try { await navigator.clipboard.writeText(code.textContent.trim()); }
    catch {
      const range = document.createRange();
      range.selectNodeContents(code);
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      copied = document.execCommand("copy");
    }
    command.classList.toggle("is-copied", copied);
    status.textContent = copied ? "Copied. Paste it into Terminal and press Enter." : "It's selected. Copy it with your keyboard.";
    clearTimeout(timer);
    timer = setTimeout(() => { command.classList.remove("is-copied"); status.textContent = ""; }, 5000);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

matchMedia("(prefers-reduced-motion: reduce)").addEventListener("change", () => location.reload());
request();
