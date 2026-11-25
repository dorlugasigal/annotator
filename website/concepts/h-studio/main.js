import { createInk } from "/concepts/shared/ink.js";

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
const OCEAN = token("--ocean-2"), BLUE = token("--ink-blue");

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
    // Stands in for an element where a step needs one ("around", "over").
    box: (element) => ({
      getBoundingClientRect() { const o = ink.layer.getBoundingClientRect(), b = rect(element); return { left: o.left + b.x, top: o.top + b.y, width: b.w, height: b.h }; },
    }),
    point: (element, ax = .5, ay = .5, dx = 0, dy = 0) => () => { const b = rect(element); return [b.x + b.w * ax + dx, b.y + b.h * ay + dy]; },
  };
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

const header = $(".site-header"), ambient = $(".ambient");
readers.push(({ y }) => {
  header.classList.toggle("is-stuck", y > 8);
  if (motion) ambient.style.setProperty("--scroll", Math.round(y));
});

// Hero stage --------------------------------------------------------------------------------------
const stage = $("#stage"), screen = $("#screen"), board = $("#board"), number = $("#ring-target"), plot = $("#plot"), dip = $("#dip"), secret = $("#private");
// The toolbar keeps the state the engine gives it (its properties row included), so no colors or sizes are set here.
// Its selected stroke color is the app's Ocean gradient, the same one the page uses as its accent.
const ink = createInk({ surface: board, toolbarParent: $("#dock"), toolbarPosition: "absolute", appearance: root.dataset.theme === "light" ? "light" : "dark", gradient: "ocean", tool: "pen", moreHref: "#install" });
const at = anchorsFor(ink, board);
// The screen keeps room under the window for the toolbar. It remembers the tallest the toolbar has been at this
// size, so opening and closing the properties row moves nothing on the page.
const tallest = {};
ink.on("layout", ({ height, size, rows }) => {
  if (!height || ink.collapsed) return;
  const key = `${size}|${rows}`;
  if (height <= (tallest[key] || 0) && tallest.shown === key) return;
  tallest[key] = Math.max(height, tallest[key] || 0);
  tallest.shown = key;
  const room = tallest[key];
  requestAnimationFrame(() => { screen.style.setProperty("--tb-h", `${room}px`); schedule(); });
});
ink.fit();

// The stage leans back at the top of the page and comes flat as it is scrolled up. It is also flat whenever
// the visitor is using it, because a flat plane is where the pointer and the ink agree.
let engaged = false, lifted = !motion;
if (motion) {
  const lean = follow((k) => {
    stage.style.setProperty("--tilt", (k * (innerWidth < 600 ? 13 : 19)).toFixed(3));
    stage.style.setProperty("--scale", (1 - k * .055).toFixed(4));
    stage.classList.toggle("is-live", k > 0);
  });
  const lift = follow((k) => stage.style.setProperty("--lift", k.toFixed(4)), 150);
  readers.push(({ y, h }) => {
    const top = stage.getBoundingClientRect().top + y, range = clamp(top - 90, 200, Math.max(200, h * .5));
    const k = engaged ? 0 : 1 - smooth(clamp(y / range));
    lean.target = k;
    lift.target = lifted ? k : 0;
  });
}
const engage = (on) => { if (engaged !== on) { engaged = on; schedule(); } };

const small = () => screen.clientWidth < 620;
const stacked = () => at.rect(plot).y > at.rect(number).y + at.rect(number).h;
const LABEL = "why the dip?";
let pen = null;
function labelWidth() {
  pen ||= document.createElement("canvas").getContext("2d");
  pen.font = getComputedStyle(ink.layer).getPropertyValue("--ink-text").trim().replace(/\/[\d.]+/, "");
  return pen.measureText(LABEL).width + 8;
}
function heroSteps() {
  const size = small() ? 3 : 4, ring = { get x() { return small() ? 10 : 17; }, get y() { return small() ? 8 : 11; } };
  // The ring and the arrow name no color: they are drawn in whatever the toolbar has selected.
  return [
    { tool: "ellipse", around: at.box(number), get padX() { return ring.x; }, get padY() { return ring.y; }, size, duration: 850, pause: 260 },
    {
      tool: "arrow", size, duration: 800, pause: 260,
      // Beside the chart the arrow leaves the ring's right side; above the chart it leaves the lower left, clear of the badge under the number.
      from: () => { const n = at.rect(number); return stacked() ? [n.x - ring.x * .4, n.y + n.h + ring.y * .7 + 4] : [n.x + n.w + ring.x + 8, n.y + n.h / 2 - 2]; },
      to: () => { const d = at.rect(dip); return stacked() ? [d.x + 8, d.y - 10] : [d.x - 6, d.y - 11]; },
      get bend() { return stacked() ? .16 : .24; },
    },
    { tool: "text", color: OCEAN, text: LABEL, speed: 46, pause: 360, at: () => { const d = at.rect(dip); return [d.x - labelWidth() / 2, d.y + (small() ? 7 : 12)]; } },
    { tool: "blur", over: at.box(secret), padX: 6, padY: 3, duration: 650 },
  ];
}

const replay = $("#replay"), onStage = watcher(screen);
const demo = { run: 0, playing: false, ids: new Set(), busy: Promise.resolve(), controller: null, picked: null };
function showControl() {
  replay.hidden = false;
  replay.classList.toggle("is-off", !demo.playing);
  replay.lastElementChild.textContent = demo.playing ? "Pause demo" : "Replay";
}
function forget() {
  for (const id of demo.ids) ink.remove(id);
  demo.ids.clear();
}
function stopDemo() {
  if (!demo.playing) return;
  demo.playing = false;
  demo.run += 1;
  demo.controller?.abort();
  ink.hideGhost();
  $$(".is-leaving", ink.layer).forEach((mark) => mark.classList.remove("is-leaving"));
  showControl();
}
async function playDemo() {
  const run = ++demo.run, live = () => run === demo.run;
  demo.playing = true;
  showControl();
  await demo.busy;
  let release;
  demo.busy = new Promise((done) => { release = done; });
  try {
    while (live()) {
      await onStage.ready();
      if (!live()) break;
      forget();
      demo.controller = new AbortController();
      demo.picked = null;
      const made = await ink.perform(heroSteps(), { cursor: motion, showTool: motion, signal: demo.controller.signal });
      made.forEach((mark) => demo.ids.add(mark.id));
      ink.hideGhost();
      // A script puts the toolbar back on the tool it started with. If the visitor chose one meanwhile, theirs wins.
      if (demo.picked && ink.state.tool !== demo.picked) ink.setTool(demo.picked, true);
      if (!live()) break;
      if (!motion) { demo.playing = false; demo.run += 1; replay.hidden = true; break; }
      await wait(3400);
      if (!live()) break;
      await leave(ink, [...demo.ids]);
      if (!live()) break;
      forget();
      await wait(700);
    }
  } finally { release(); }
}
ink.on("tool", (tool) => { demo.picked = tool; });
ink.on("mark", stopDemo);
ink.on("mode", (mode) => {
  if (mode === "draw") stopDemo();
  // Drawing chosen from further down the page (the two keys) leaves the stage alone until it is on screen.
  if (mode === "draw" ? onStage.near : !onStage.near) engage(mode === "draw");
});
ink.toolbar.addEventListener("pointerdown", () => { stopDemo(); engage(true); }, true);
ink.toolbar.addEventListener("focusin", () => engage(true));
replay.addEventListener("click", () => {
  if (demo.playing) { stopDemo(); return; }
  ink.setMode("use");
  playDemo();
});
if (window.IntersectionObserver) new IntersectionObserver(([entry]) => {
  onStage.near = entry.isIntersecting;
  // Out of view, drawing ends, so keys and touches go back to the page, and the stage is free to lean again.
  if (!entry.isIntersecting) { ink.setMode("use"); engage(false); } else if (ink.state.mode === "draw") engage(true);
}).observe(screen);

Promise.all([document.fonts?.ready, document.fonts?.load('600 30px "Caveat"')]).catch(() => {}).then(async () => {
  if (motion) await wait(1250);
  lifted = true;
  schedule();
  if (motion) await wait(650);
  playDemo();
});

// Theme -------------------------------------------------------------------------------------------
// The script in the page's head has already set the theme. The switch changes it, remembers the choice and tells
// everything that keeps colors of its own: the toolbar and the browser's own chrome.
const THEME_KEY = "annotator.concept-h.theme";
const themeSwitch = $("#theme-switch"), coin = $(".coin", themeSwitch), coinLift = $(".coin-lift", themeSwitch), themeMeta = $('meta[name="theme-color"]');
let theme = root.dataset.theme === "light" ? "light" : "dark", turns = theme === "light" ? 1 : 0;
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
  if (under && (target.contains(under) || under.contains(target))) under.closest("a, button, video")?.click();
}, true);
showTheme(theme);

// What you can do ---------------------------------------------------------------------------------
const what = $("#what"), deck = $("#deck"), tiles = $$(".tile", deck), deckSpace = $(".deck-space"), stepsBar = $("#deck-steps"), clipsToggle = $("#clips-toggle");
// A small sample screen that a pointer draws on, again and again while its card is the one in front.
function createScene(surface, build) {
  const scene = createInk({ surface, interactive: false, size: 3 }), anchors = anchorsFor(scene, surface);
  let active = false, running = false, ids = [];
  async function run() {
    if (running) return;
    running = true;
    while (active) {
      if (!ids.length) {
        ids = (await scene.perform(build(anchors), { cursor: motion })).map((mark) => mark.id);
        scene.hideGhost();
      }
      if (!motion) break;
      await wait(2800);
      if (!active) break;
      await leave(scene, ids);
      ids.forEach((id) => scene.remove(id));
      ids = [];
      await wait(600);
    }
    running = false;
  }
  // Without motion there is nothing to play: the finished drawing is simply there.
  if (!motion) scene.perform(build(anchors), { cursor: false });
  return { set(on) { if (!motion) return; active = on; if (on) run(); } };
}
const scenes = new Map([
  [$("#scene-private"), (a) => [{ wait: 350 }, ...$$("[data-blur]", $("#scene-private")).map((el) => ({ tool: "blur", over: a.box(el), padX: 6, padY: 3, duration: 600, pause: 320 }))]],
  [$("#scene-steps"), (a) => [{ wait: 350 }, ...$$("[data-step]", $("#scene-steps")).map((el) => ({ tool: "counter", at: a.point(el, 0, .5, -22), color: BLUE, pause: 300 }))]],
].map(([surface, build]) => [surface.closest(".tile"), createScene(surface, build)]));

let clipsPaused = !motion, front = -1;
const shown = new Set();
function playTile(tile, on) {
  const video = $("video", tile);
  if (video) { if (on) video.play().catch(() => {}); else video.pause(); }
  scenes.get(tile)?.set(on);
}
const refreshTiles = () => tiles.forEach((tile, i) => playTile(tile, !clipsPaused && (motion ? i === front : shown.has(tile))));
clipsToggle.hidden = false;
const labelClips = () => {
  clipsToggle.classList.toggle("is-off", clipsPaused);
  clipsToggle.lastElementChild.textContent = clipsPaused ? "Play clips" : "Pause clips";
};
labelClips();
clipsToggle.addEventListener("click", () => { clipsPaused = !clipsPaused; labelClips(); refreshTiles(); });

if (motion) {
  // The deck. The section is five steps taller than the screen; each step brings the next card to the front.
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
  const turn = follow((p) => {
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
// Two keys ------------------------------------------------------------------------------------------
const modePill = $("#mode-pill"), drawCombo = $('[data-combo="draw"]');
const caps = Object.fromEntries($$("[data-key]").map((key) => [key.dataset.key, key]));
const capFor = (e) => (e.key === "Control" ? caps.control : e.key === "Shift" ? caps.shift : e.code === "Digit2" ? caps[2] : e.key === "Escape" ? caps.esc : null);
addEventListener("keydown", (e) => capFor(e)?.classList.add("is-down"));
addEventListener("keyup", (e) => capFor(e)?.classList.remove("is-down"));
addEventListener("blur", () => Object.values(caps).forEach((key) => key.classList.remove("is-down")));
function showMode(mode) {
  modePill.dataset.mode = mode;
  modePill.lastElementChild.textContent = mode === "draw" ? "Draw" : "Use apps";
  drawCombo.classList.toggle("is-on", mode === "draw");
}
ink.on("mode", showMode);
for (const combo of $$(".combo")) {
  combo.addEventListener("click", () => {
    const keys = $$(".key", combo);
    keys.forEach((key, i) => setTimeout(() => key.classList.add("is-down"), i * 45));
    setTimeout(() => keys.forEach((key) => key.classList.remove("is-down")), 190 + keys.length * 45);
    if (combo.dataset.combo === "draw") ink.setMode(ink.state.mode === "draw" ? "use" : "draw"); else ink.setMode("use");
  });
}

// Planes that follow the scroll: the keys' plates, the recording, the cup ---------------------------------
if (motion) {
  for (const element of $$("[data-scroll]")) {
    const through = follow((p) => element.style.setProperty("--p", p.toFixed(4)));
    const arrive = follow((e) => element.style.setProperty("--e", e.toFixed(4)));
    readers.push(({ h }) => {
      const box = element.getBoundingClientRect();
      if (box.top > h * 1.6 || box.bottom < -h * .6) return;
      through.target = clamp((h - box.top) / (h + box.height));
      arrive.target = smooth(clamp((h - box.top) / (h * .74)));
    });
  }
}

// The recording: the poster first, playing only while it is in view, with two plain buttons.
const reel = $("#reel"), play = $("#reel-play"), sound = $("#reel-sound");
if (matchMedia("(max-width: 599px)").matches) {
  reel.poster = "reel-poster-phone.jpg";
  reel.src = "/public/demo/annotator-reel-phone.mp4";
}
reel.controls = false;
$(".video-controls").hidden = false;
let wanted = motion;
const labelReel = () => {
  play.textContent = reel.paused ? "Play" : "Pause";
  sound.textContent = reel.muted ? "Turn sound on" : "Turn sound off";
};
const toggleReel = () => {
  wanted = reel.paused;
  if (wanted) reel.play().catch(() => {}); else reel.pause();
};
play.addEventListener("click", toggleReel);
reel.addEventListener("click", toggleReel);
sound.addEventListener("click", () => { reel.muted = !reel.muted; });
for (const name of ["play", "pause", "volumechange"]) reel.addEventListener(name, labelReel);
if (window.IntersectionObserver) new IntersectionObserver(([entry]) => {
  if (entry.isIntersecting && wanted) reel.play().catch(() => {}); else if (!entry.isIntersecting) reel.pause();
}, { threshold: .5 }).observe($(".reel-space"));

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
      if (element.classList.contains("extra")) element.classList.add("is-playing");
      if (element === coffee) element.classList.add("is-steaming");
      // Once it has landed, a card answers the pointer without the delay it arrived with.
      setTimeout(() => element.classList.add("is-settled"), 1000 + Number(element.style.getPropertyValue("--i") || 0) * 70);
    }
  }, { rootMargin: "0px 0px -10% 0px", threshold: .15 });
  $$("[data-enter], [data-words]").forEach((element) => arrive.observe(element));
  $$(".extra").forEach((extra) => extra.addEventListener("pointerenter", () => { if (extra.classList.contains("is-settled")) restart(extra, "is-playing"); }));
  coffee.addEventListener("pointerenter", () => restart(coffee, "is-steaming"));
}
// A drawn underline for the header's links.
for (const link of $$(".nav-link")) link.insertAdjacentHTML("beforeend", '<svg class="scribble" viewBox="0 0 100 8" preserveAspectRatio="none" aria-hidden="true"><path stroke="url(#ocean)" d="M2 5C16 1 30 7 46 4S78 2 98 5"/></svg>');

// Install -------------------------------------------------------------------------------------------
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

schedule();
