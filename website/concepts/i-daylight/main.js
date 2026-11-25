import { createInk } from "/concepts/shared/ink.js";

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
const CORAL = "#F04538", BLUE = "#2B73E0", TEAL = "#00969E", YELLOW = "#F5E338", PURPLE = "#7D54B8";

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
    const o = ink.layer.getBoundingClientRect(), b = localBox(el, surface);
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
const desk = $("#desk"), dock = $("#dock"), scene = $(".stage-scene");
// The toolbar is the engine's own, as someone who uses it has set it up (its "customized" preset): a gradient as the
// color, curved arrows, the roughest line. It rests on the Arrow tool so its properties row shows that. The engine
// itself starts the row closed on a phone, which keeps the bar compact there.
const ink = createInk({ surface: desk, toolbarParent: dock, toolbarPosition: "absolute", appearance: root.dataset.theme, tool: "arrow", moreHref: "#install" });

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
const tilt = channel(() => {
  if (userFlat) return 0;
  const top = scene.getBoundingClientRect().top + scrollY;
  return 1 - smooth(scrollY / clamp(top - innerHeight * 0.1, 200, innerHeight * 0.7));
}, (v) => {
  desk.style.setProperty("--tilt", v.toFixed(4));
  desk.style.setProperty("--lift", (4 * v * (1 - v)).toFixed(4));
  desk.classList.toggle("is-flat", v === 0);
});

const numberEl = $("#t-number"), lineEl = $("#t-line"), emailEl = $("#t-email");
const on = (el) => standIn(el, ink, desk);
const number = on(numberEl), line = on(lineEl), email = on(emailEl);
// A blur region softens what is behind it, which a browser cannot do across planes in 3D. While the stage leans,
// the address under a blur region is softened itself; flat, the region does it (see .is-covered in style.css).
function markCovered() {
  const e = localBox(emailEl, desk);
  emailEl.classList.toggle("is-covered", ink.marks().some((m) => {
    if (m.type !== "blur") return false;
    const b = ink.box(m);
    return b.x <= e.x + 2 && b.y <= e.y + 2 && b.x + b.w >= e.x + e.w - 2 && b.y + b.h >= e.y + e.h - 2;
  }));
}
ink.on("change", markCovered).on("clear", markCovered);
function demoSteps() {
  const small = desk.clientWidth < 600, px = small ? 12 : 17, py = small ? 8 : 11, size = small ? 3 : 3.5;
  // The arrow runs from the line on the slide to the number: sideways when the windows sit side by side, down when
  // they are stacked.
  const ends = () => {
    const l = localBox(lineEl, desk), n = localBox(numberEl, desk);
    return n.x > l.x + l.w
      ? [[l.x + l.w + 10, l.y + l.h * 0.3], [n.x - px - 9, n.y + n.h * 0.42]]
      : [[l.x + l.w - 4, l.y + l.h + 9], [n.x + n.w * 0.8, n.y - py - 14]];
  };
  // The ring and the arrow name no color: they take the one selected in the toolbar. Both are drawn at the
  // toolbar's strongest roughness, and the arrow bows well clear of a straight line.
  return [
    { tool: "ellipse", around: number, padX: px, padY: py, size, rough: 6, duration: 820, pause: 240 },
    { tool: "arrow", from: () => ends()[0], to: () => ends()[1], bend: 0.42, size, rough: 6, duration: 760, pause: 220 },
    { tool: "text", at: { el: number, at: "right", dx: px + 12, dy: small ? -15 : -18 }, text: "why the dip?", color: BLUE, pause: 320 },
    { tool: "highlight", over: line, color: YELLOW, duration: 620, pause: 320 },
    { tool: "blur", over: email, pad: 5, amount: 12, duration: 660 },
    { run: markCovered },
    { wait: 160 },
  ];
}

// The tool the visitor chose. A script that shows its tools in the toolbar puts back the tool it started with when
// it ends; if the visitor picked one meanwhile, theirs is put back instead.
let chosen = null;
ink.on("tool", (tool) => { chosen = tool; });
const keepChoice = () => { if (chosen && ink.state.tool !== chosen) ink.setTool(chosen, ink.state.mode !== "draw"); };

const replay = $("#stage-replay"), done = $("#stage-done"), note = $("#stage-note");
let run = null, deskSeen = false, seenWaiters = [];
const setRunning = (running) => {
  replay.dataset.state = running ? "playing" : "";
  $("b", replay).textContent = running ? "Pause" : "Replay";
};
const whenSeen = (signal) => new Promise((go) => {
  if (deskSeen || signal.aborted) { go(); return; }
  seenWaiters.push(go);
  signal.addEventListener("abort", go, { once: true });
});
async function wipe(signal) {
  if (!ink.marks().length) return;
  desk.classList.add("is-wiping");
  await wait(340, signal);
  if (!signal?.aborted) ink.clear();
  desk.classList.remove("is-wiping");
}
async function loop(signal) {
  while (!signal.aborted) {
    await whenSeen(signal);
    await wait(700, signal);
    if (signal.aborted) break;
    await ink.perform(demoSteps(), { cursor: true, showTool: true, signal });
    ink.hideGhost();
    keepChoice();
    ink.reflow();
    await wait(3400, signal);
    if (signal.aborted) break;
    await wipe(signal);
    await wait(500, signal);
  }
}
function start() {
  if (run) return;
  const abort = new AbortController();
  const finished = loop(abort.signal).finally(() => { ink.hideGhost(); keepChoice(); if (run?.abort === abort) { run = null; setRunning(false); } });
  run = { abort, finished };
  setRunning(true);
}
async function stop() {
  if (!run) return;
  const { abort, finished } = run;
  abort.abort();
  setRunning(false);
  await finished;
}
async function restart() {
  await stop();
  ink.setMode("use");
  if (ink.state.tool !== "arrow") ink.setTool("arrow", true);
  chosen = null;
  userFlat = !motion;
  request();
  await wipe();
  if (motion) start();
  else { await ink.perform(demoSteps(), { cursor: false }); ink.reflow(); }
}
replay.addEventListener("click", () => { if (run) stop(); else restart(); });
done.addEventListener("click", () => ink.setMode("use"));

// The loop stops for good when the visitor takes over: a press on the toolbar, or draw mode by any route.
const takeOver = () => { stop(); if (!userFlat) { userFlat = true; request(); } };
ink.toolbar.addEventListener("pointerdown", takeOver, true);
ink.on("mode", (mode) => {
  if (mode === "draw") takeOver();
  done.hidden = mode !== "draw";
  note.textContent = mode === "draw"
    ? "You are drawing on the demo. Press Esc to use the page again."
    : "This is a web demo of the app. Pick a tool and draw on it.";
  setKeysMode(mode);
});
addEventListener("resize", () => setTimeout(() => ink.reflow(), 260));

new IntersectionObserver(([entry]) => {
  deskSeen = entry.isIntersecting && entry.intersectionRatio >= 0.3;
  if (deskSeen) seenWaiters.splice(0).forEach((go) => go());
  // Drawing ends when the stage is out of view, so keys and touches go back to the page.
  if (!entry.isIntersecting) ink.setMode("use");
}, { threshold: [0, 0.3] }).observe(scene);

const fontsReady = Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {});
fontsReady.then(async () => {
  if (motion) { await wait(1500); start(); }
  else { await ink.perform(demoSteps(), { cursor: false }); ink.reflow(); }
});

// 2. What you can do -------------------------------------------------------------------------------
const measure = document.createElement("canvas").getContext("2d");
const SCENES = {
  blur: (t) => [
    { tool: "blur", over: t("email"), pad: 5, amount: 13, duration: 700, pause: 380 },
    { tool: "blur", over: t("card"), pad: 5, amount: 13, duration: 700 },
  ],
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
const textSize = (shot) => Math.round(clamp(shot.clientWidth * 0.06, 21, 38));
// A card's live visual: a recording that plays while it is shown, or a small scene drawn by the pointer.
function clip(card, video) {
  const button = $(".ctl", card);
  let wanted = motion, shown = false;
  const sync = () => {
    const playing = !video.paused;
    button.dataset.state = playing ? "playing" : "";
    button.setAttribute("aria-label", playing ? "Pause this recording" : "Play this recording");
  };
  const source = () => {
    const name = `${video.dataset.clip}${matchMedia("(max-width: 599px)").matches ? "-phone" : ""}`;
    if (video.dataset.loaded === name) return;
    video.dataset.loaded = name;
    video.poster = `/public/demo/${name}.jpg`;
    video.src = `/public/demo/${name}.mp4`;
  };
  source();
  addEventListener("resize", source);
  for (const name of ["play", "pause"]) video.addEventListener(name, sync);
  button.addEventListener("click", () => { wanted = video.paused; if (wanted) video.play().catch(() => {}); else video.pause(); });
  sync();
  return {
    show() { if (shown) return; shown = true; if (wanted) video.play().catch(() => {}); },
    hide() { shown = false; video.pause(); },
  };
}
function sketch(card, shot) {
  const layer = createInk({ surface: shot, interactive: false });
  const steps = SCENES[shot.dataset.scene]((name) => standIn($(`[data-t="${name}"]`, shot), layer, shot), shot);
  const fit = () => layer.layer.style.setProperty("--ink-text", `600 ${textSize(shot)}px/1.1 "Caveat", "Bradley Hand", cursive`);
  let busy = false, shown = false;
  async function play() {
    if (busy) return;
    busy = true;
    layer.clear();
    shot.classList.remove("is-leaving");
    fit();
    await fontsReady;
    await wait(motion ? 260 : 0);
    await layer.perform(steps, { cursor: motion });
    await wait(500);
    layer.hideGhost();
    layer.reflow();
    busy = false;
    if (!shown && motion) leave();
  }
  // A card that no longer faces front loses its drawing quietly, so it starts clean the next time.
  async function leave() {
    shot.classList.add("is-leaving");
    await wait(280);
    if (!shown && !busy) layer.clear();
  }
  addEventListener("resize", () => { fit(); setTimeout(() => layer.reflow(), 260); });
  $(".ctl", card).addEventListener("click", play);
  if (motion) $(".frame", card).addEventListener("pointerenter", (e) => { if (e.pointerType === "mouse" && shown) play(); });
  return { show() { if (shown) return; shown = true; play(); }, hide() { if (!shown) return; shown = false; if (motion && !busy) leave(); } };
}
const cards = $$(".do-deck .card");
const visuals = cards.map((card) => {
  const video = $("video", card);
  return video ? clip(card, video) : sketch(card, $(".shot", card));
});

if (motion) {
  // The pinned deck. Progress through the track turns into one number, 0 for the first card to 5 for the last; each
  // card rests flat for a while around its own number, and eases between.
  const track = $(".do-track"), tabs = $$(".do-tabs button"), rest = 0.2;
  let front = -1, deckSeen = false;
  const face = (index) => {
    if (index === front) return;
    front = index;
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
  // Without motion the cards are a plain grid. Recordings wait for their play buttons; scenes show their last frame.
  visuals.forEach((visual) => visual.show());
}

// 3. The two keys ----------------------------------------------------------------------------------
const keysSection = $("#keys"), modeWord = $("[data-mode-word]");
function setKeysMode(mode) {
  keysSection.dataset.mode = mode;
  modeWord.textContent = mode === "draw" ? "Draw" : "Apps";
}
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
  const keys = $$(".key", set);
  for (const key of keys) { key.classList.add("is-down"); await wait(motion ? 70 : 0); }
  if (set.dataset.set === "draw") ink.setMode(ink.state.mode === "draw" ? "use" : "draw"); else ink.setMode("use");
  await wait(160);
  keys.forEach((key) => key.classList.remove("is-down"));
});

// 4. The recording ---------------------------------------------------------------------------------
const reel = $("#reel"), play = $("#reel-play"), sound = $("#reel-sound");
if (matchMedia("(max-width: 599px)").matches) {
  reel.poster = "reel-poster-phone.jpg";
  reel.src = "/public/demo/annotator-reel-phone.mp4";
}
reel.controls = false;
// The recording opens with a title card; the page starts it, and starts it again, where the app comes on screen.
const REEL_START = 1.8;
reel.loop = false;
reel.addEventListener("loadedmetadata", () => { if (reel.currentTime < REEL_START) reel.currentTime = REEL_START; });
reel.addEventListener("ended", () => { reel.currentTime = REEL_START; if (reelWanted) reel.play().catch(() => {}); });
$(".video-controls").hidden = false;
let reelWanted = motion;
const reelLabels = () => {
  play.dataset.state = reel.paused ? "" : "playing";
  $("b", play).textContent = reel.paused ? "Play" : "Pause";
  sound.dataset.sound = reel.muted ? "" : "on";
  $("b", sound).textContent = reel.muted ? "Sound on" : "Sound off";
};
const reelToggle = () => { reelWanted = reel.paused; if (reelWanted) reel.play().catch(() => {}); else reel.pause(); };
play.addEventListener("click", reelToggle);
reel.addEventListener("click", reelToggle);
sound.addEventListener("click", () => { reel.muted = !reel.muted; });
for (const name of ["play", "pause", "volumechange"]) reel.addEventListener(name, reelLabels);
reelLabels();
new IntersectionObserver(([entry]) => {
  if (entry.isIntersecting && reelWanted) reel.play().catch(() => {}); else if (!entry.isIntersecting) reel.pause();
}, { threshold: 0.55 }).observe($(".reel-scene"));

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
  const reelScene = $(".reel-scene");
  channel(entering(reelScene), write(reelScene, "--enter"));
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
    button.textContent = copied ? "Copied" : "Selected";
    status.textContent = copied ? "Command copied." : "Command selected. Copy it with your keyboard.";
    setTimeout(() => { button.textContent = "Copy"; status.textContent = ""; }, 2000);
  });
}
// The visitor's platform comes first in the page's order too, not only on screen.
if (root.dataset.platform === "windows") $(".platforms").prepend($('.platform[data-os="windows"]'));

matchMedia("(prefers-reduced-motion: reduce)").addEventListener("change", () => location.reload());
request();
