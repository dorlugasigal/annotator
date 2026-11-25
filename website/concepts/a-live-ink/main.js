import { createInk } from "/concepts/shared/ink.js";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;
const wait = (ms) => new Promise((done) => setTimeout(done, reduce ? 0 : ms));
const root = document.documentElement;
root.classList.add("js");
root.dataset.os = /win/i.test(navigator.userAgentData?.platform || navigator.platform || "") ? "windows" : "mac";

// Apricot, yellow and teal read on the night-blue page; the red is the app's own first color, for light sample screens.
const APRICOT = "#ffb58c", YELLOW = "#ffe14d", TEAL = "#35d0ba", RED = "#f04538", CHALK = "#f4f6fb";
const ink = createInk({ surface: document.body, collapsed: true, pillLabel: "Draw on this page", useLabel: "Use page", tool: "pen" });
const phone = () => innerWidth <= 600;
const onScreen = (element) => { const box = element.getBoundingClientRect(); return box.bottom > 0 && box.top < innerHeight; };

// Scripted marks -----------------------------------------------------------------------------------
// They run one after another, so the ghost pointer is never in two places. A script whose section has
// already left the window lands at once instead of holding up the ones the visitor is looking at.
const scripted = new Set();
let queue = Promise.resolve(), waiting = 0;
const hurried = (steps) => steps.filter((step) => !step.wait).map((step) => (step.run ? step : { ...step, duration: 0, speed: 0, pause: 0 }));
function perform(steps, { near, cursor = true, signal } = {}) {
  waiting += 1;
  const job = queue.then(async () => {
    if (signal?.aborted) return [];
    const live = !near || onScreen(near);
    const list = typeof steps === "function" ? steps(live) : steps;
    const made = await ink.perform(live ? list : hurried(list), { cursor: cursor && live, signal });
    made.forEach((mark) => scripted.add(mark.id));
    return made;
  }).catch((error) => { console.error(error); return []; });
  queue = job.then(() => { waiting -= 1; if (!waiting) ink.hideGhost(); });
  return job;
}
const spot = (at) => {
  if (typeof at === "function") return at();
  const box = ink.rectOf(at.el), where = at.at || "center";
  const x = where.includes("left") ? box.x : where.includes("right") ? box.x + box.w : box.x + box.w / 2;
  const y = where.includes("top") ? box.y : where.includes("bottom") ? box.y + box.h : box.y + box.h / 2;
  return [x + (at.dx || 0), y + (at.dy || 0)];
};
// A text step can't choose its size, so large handwriting is added as a mark and typed here.
const write = (text, at, { color = APRICOT, fontSize = 34, speed = 70 } = {}) => ({
  run: async () => {
    const [x, y] = spot(at);
    let mark;
    try { mark = ink.add({ type: "text", x, y, text: "", color, fontSize, anchor: { tool: "text", at } }); } catch { mark = null; }
    if (!mark) { (await ink.perform([{ tool: "text", at, text, color }], { cursor: false })).forEach((made) => scripted.add(made.id)); return; }
    scripted.add(mark.id);
    for (const letter of text) { mark.text += letter; ink.render(mark); await wait(speed); }
  },
});

// A short line above the toolbar -----------------------------------------------------------------
const coach = document.body.appendChild(Object.assign(document.createElement("p"), { className: "coach" }));
coach.setAttribute("role", "status");
let coachTimer = 0;
function say(text, ms = 4200) {
  coach.textContent = text;
  coach.classList.add("is-on");
  clearTimeout(coachTimer);
  coachTimer = setTimeout(() => coach.classList.remove("is-on"), ms);
}
function startDrawing(tool, color) {
  ink.setCollapsed(false);
  if (color) ink.setColor(color);
  if (tool) ink.setTool(tool); else ink.setMode("draw");
}
$$("[data-start-drawing]").forEach((button) => button.addEventListener("click", () => startDrawing()));
let greeted = false;
ink.on("mode", (mode) => {
  if (mode === "draw" && !greeted) { greeted = true; say("You’re drawing on the page. Press Esc to click and scroll as usual."); }
  else if (mode === "use" && ink.marks().length && !ink.state.hidden) say("Back to the page. Your drawings stay.", 2600);
});
ink.on("smart", () => say("Smart Draw made it neat.", 2200));
// The toolbar shows its own hint in the same place, for example while an arrow is placed point by point.
ink.on("status", (words) => { if (words) coach.classList.remove("is-on"); });

// Hero: the question, then the answer -----------------------------------------------------------------
const hero = $(".hero"), lost = $("#lost");
hero.addEventListener("pointermove", (event) => {
  const box = $(".hero-dots").getBoundingClientRect();
  hero.style.setProperty("--mx", `${event.clientX - box.left}px`);
  hero.style.setProperty("--my", `${event.clientY - box.top}px`);
});
async function heroIntro() {
  await document.fonts.ready;
  await wait(1900);
  lost.classList.add("is-found");
  const small = innerWidth <= 900, size = small ? 54 : 84;
  const here = () => {
    const box = ink.rectOf(lost);
    return phone() ? [box.x - 150, box.y - 30] : small ? [box.x - 58, box.y + 44] : [box.x + 62, box.y - size * 0.52];
  };
  await perform([
    { tool: "ellipse", around: lost, pad: small ? 20 : 27, color: APRICOT, size: small ? 4 : 6, duration: 950 },
    { wait: 180 },
    write("Here.", here, { fontSize: size, speed: 90 }),
  ], { near: hero });
}
heroIntro();

// Which one? A call without and with Annotator -------------------------------------------------------
const call = $(".call"), callBody = $(".call-body"), callTarget = $("#call-target"), wiggler = $(".wiggler");
const callSays = $("[data-call-says]"), callBubble = $("[data-call-bubble]"), callPicks = $$("[data-call-pick]");
const vague = ["“this one…”", "“no, up…”", "“the red one.”", "“the other red one.”"];
let callMarks = [], callAbort = null, sayTimer = 0, callTouched = false;
function placeWiggler() {
  const body = callBody.getBoundingClientRect(), target = callTarget.getBoundingClientRect();
  wiggler.style.setProperty("--wx", `${target.left + target.width / 2 - body.left}px`);
  wiggler.style.setProperty("--wy", `${target.top + target.height / 2 - body.top}px`);
}
const callSteps = () => {
  const label = phone() ? { el: callTarget, at: "bottom left", dx: -118, dy: 30 } : { el: callTarget, at: "top right", dx: 26, dy: -86 };
  return [
    { tool: "ellipse", around: callTarget, padX: 16, padY: 9, color: RED, size: 4, duration: 650 },
    { tool: "text", at: label, text: "this one", color: RED, speed: 45 },
    phone()
      ? { tool: "arrow", from: { el: callTarget, at: "bottom left", dx: -40, dy: 30 }, to: { el: callTarget, at: "bottom left", dx: -6, dy: 12 }, bend: 0.3, color: RED, size: 4, duration: 350 }
      : { tool: "arrow", from: { el: callTarget, at: "top right", dx: 44, dy: -50 }, to: { el: callTarget, at: "top right", dx: 14, dy: -9 }, bend: -0.3, color: RED, size: 4, duration: 350 },
  ];
};
function showCall(state) {
  call.dataset.call = state;
  callPicks.forEach((pick) => pick.setAttribute("aria-pressed", String(pick.dataset.callPick === state)));
  clearInterval(sayTimer);
  callAbort?.abort();
  callMarks.forEach((id) => ink.remove(id));
  callMarks = [];
  if (state === "without") {
    let index = 0;
    callSays.textContent = reduce ? "“this one… no, up… the red one.”" : vague[0];
    callBubble.textContent = "which one??";
    if (!reduce) sayTimer = setInterval(() => { index = (index + 1) % vague.length; callSays.textContent = vague[index]; }, 1500);
    return;
  }
  callSays.textContent = "“This one.”";
  const abort = (callAbort = new AbortController());
  perform(callSteps, { near: call, signal: abort.signal }).then((made) => {
    const ids = made.map((mark) => mark.id);
    if (abort.signal.aborted) ids.forEach((id) => ink.remove(id));
    else { callMarks = ids; callBubble.textContent = "oh, THAT one."; }
  });
}
callPicks.forEach((pick) => pick.addEventListener("click", () => { callTouched = true; if (call.dataset.call !== pick.dataset.callPick) showCall(pick.dataset.callPick); }));
placeWiggler();
addEventListener("resize", placeWiggler);
document.fonts.ready.then(placeWiggler);
showCall("without");
// It switches by itself once, after the visitor has watched the cursor wiggle for a moment.
new IntersectionObserver(([entry], observer) => {
  if (!entry.isIntersecting) return;
  observer.disconnect();
  wait(4600).then(() => { if (!callTouched) showCall("with"); });
}, { threshold: 0.6 }).observe(callBody);

// Reel -------------------------------------------------------------------------------------------
const reel = $("#reel-video"), frame = $("[data-scroll-tilt]");
const playButton = $("[data-reel-play]"), soundButton = $("[data-reel-sound]");
let reelWanted = !reduce;
const setPlayLabel = () => {
  const playing = !reel.paused;
  $("[data-label]", playButton).textContent = playing ? "Pause" : "Play";
  playButton.setAttribute("aria-label", playing ? "Pause the reel" : "Play the reel");
};
reel.addEventListener("play", setPlayLabel);
reel.addEventListener("pause", setPlayLabel);
playButton.addEventListener("click", () => { reelWanted = reel.paused; if (reel.paused) reel.play(); else reel.pause(); });
soundButton.addEventListener("click", () => {
  reel.muted = !reel.muted;
  soundButton.setAttribute("aria-pressed", String(!reel.muted));
  soundButton.textContent = reel.muted ? "Sound off" : "Sound on";
});
new IntersectionObserver(([entry]) => {
  if (entry.isIntersecting && reelWanted) reel.play().catch(() => {}); else reel.pause();
}, { threshold: 0.35 }).observe(reel);
setPlayLabel();
let ticking = false;
function tilt() {
  ticking = false;
  const box = frame.getBoundingClientRect();
  frame.style.setProperty("--p", Math.min(1, Math.max(0, (innerHeight - box.top) / (innerHeight * 0.8))).toFixed(3));
}
if (!reduce) {
  addEventListener("scroll", () => { if (!ticking) { ticking = true; requestAnimationFrame(tilt); } }, { passive: true });
  tilt();
}

// Clips of the real app: they play while they are on screen ----------------------------------------------
const clipSuffix = () => (phone() ? "-phone" : "");
const clipWatcher = new IntersectionObserver((entries) => {
  for (const { target, isIntersecting } of entries) {
    target.dataset.seen = isIntersecting ? "1" : "";
    if (isIntersecting && !reduce) target.play().catch(() => {}); else target.pause();
  }
}, { threshold: 0.3 });
$$("video[data-autoplay], video[data-clip-video]").forEach((video) => {
  if (reduce) video.controls = true;
  clipWatcher.observe(video);
});
$$("[data-clips]").forEach((shot) => {
  const video = $("[data-clip-video]", shot), screen = $(".shot-screen", shot), buttons = $$("[data-clip]", shot);
  let timer = 0;
  const load = (button) => {
    const name = `/public/demo/stage-${button.dataset.clip}${clipSuffix()}`;
    video.poster = `${name}.jpg`;
    video.src = `${name}.mp4`;
    video.setAttribute("aria-label", button.dataset.clipLabel);
    if (video.dataset.seen && !reduce) video.play().catch(() => {});
    screen.classList.remove("is-swapping");
  };
  buttons.forEach((button) => button.addEventListener("click", () => {
    if (button.getAttribute("aria-pressed") === "true") return;
    buttons.forEach((other) => other.setAttribute("aria-pressed", String(other === button)));
    clearTimeout(timer);
    if (reduce) return load(button);
    screen.classList.add("is-swapping");
    timer = setTimeout(() => load(button), 220);
  }));
  load(buttons[0]);
});

// Moments the page draws itself --------------------------------------------------------------------------
const scripts = {
  code: () => {
    const line = $("#m-line"), narrow = phone();
    return [
      { tool: "blur", over: $("#m-key"), pad: 3, duration: 500 },
      { wait: 250 },
      { tool: "rect", around: line, padX: 9, padY: 1, color: RED, size: 4, duration: 700 },
      { tool: "text", at: narrow ? { el: line, at: "bottom left", dx: 30, dy: 56 } : { el: line, at: "right", dx: 92, dy: -20 }, text: "never stops?", color: RED, speed: 45 },
      narrow
        ? { tool: "arrow", from: { el: line, at: "bottom left", dx: 62, dy: 56 }, to: { el: line, at: "bottom left", dx: 44, dy: 10 }, bend: -0.25, color: RED, size: 4, duration: 350 }
        : { tool: "arrow", from: { el: line, at: "right", dx: 88, dy: 0 }, to: { el: line, at: "right", dx: 22, dy: 0 }, bend: 0.14, color: RED, size: 4, duration: 350 },
    ];
  },
  steps: () => [
    { tool: "counter", at: { el: $("#m-step-1"), at: "left", dx: -36 }, n: 1, color: RED },
    { tool: "counter", at: { el: $("#m-step-2"), at: "left", dx: -36 }, n: 2, color: RED },
    { tool: "counter", at: { el: $("#m-step-3"), at: "left", dx: -36 }, n: 3, color: RED },
    { wait: 200 },
    { tool: "highlight", over: $("#m-one"), color: "#ffd21f", duration: 600 },
  ],
  system: () => {
    const app = $("#n-app"), api = $("#n-api"), jobs = $("#n-queue"), db = $("#n-db");
    const box = (el) => ({ tool: "rect", around: el, padX: 12, padY: 9, color: CHALK, size: 3, duration: 480 });
    const link = (from, to, bend) => ({ tool: "arrow", from, to, bend, color: APRICOT, size: 3, duration: 380 });
    return [
      box(app), box(api),
      link({ el: app, at: "right", dx: 20 }, { el: api, at: "left", dx: -22 }, 0),
      box(jobs),
      link({ el: api, at: "top right", dx: 10, dy: -16 }, { el: jobs, at: "left", dx: -22 }, 0.22),
      box(db),
      link({ el: api, at: "bottom right", dx: 10, dy: 16 }, { el: db, at: "left", dx: -22 }, -0.22),
    ];
  },
  support: () => {
    const button = $("#m-button"), narrow = phone();
    return [
      { tool: "ellipse", around: button, padX: 13, padY: 9, color: RED, size: 4, duration: 700 },
      narrow
        ? { tool: "text", at: { el: button, at: "left", dx: -164, dy: -20 }, text: "press this", color: RED, speed: 45 }
        : { tool: "text", at: { el: button, at: "top left", dx: -170, dy: -78 }, text: "press this", color: RED, speed: 45 },
      narrow
        ? { tool: "arrow", from: { el: button, at: "left", dx: -44, dy: 2 }, to: { el: button, at: "left", dx: -16, dy: 2 }, bend: 0, color: RED, size: 4, duration: 300 }
        : { tool: "arrow", from: { el: button, at: "top left", dx: -58, dy: -58 }, to: { el: button, at: "top left", dx: 34, dy: -12 }, bend: 0.38, color: RED, size: 4, duration: 380 },
    ];
  },
};

// Reveals, with a drawn mark for some of them ------------------------------------------------------
const underline = (color, extra) => (el) => [{ tool: "underline", under: el, color, size: 5, dy: -3, duration: 500, ...extra }];
const ring = (color, extra) => (el) => [{ tool: "ellipse", around: el, padX: 14, padY: 4, color, size: 5, duration: 750, ...extra }];
const marksOnReveal = new Map([
  ["real-app", underline(APRICOT)],
  ["not-expect", underline(YELLOW)],
  ["forty", ring(YELLOW, { padX: 7, padY: 3 })],
  ["qa-2", underline(APRICOT, { dy: -6 })],
  ["qa-4", ring(TEAL, { padX: 12, padY: 2 })],
  ["qa-6", underline(YELLOW, { dy: -6 })],
  ["latest", ring(APRICOT, { padX: 6, padY: 2 })],
  ["in-the-open", underline(TEAL)],
  ["coffee-word", underline("#2b1a10", { size: 6, dy: -2 })],
  ["get-it", ring(APRICOT, { padX: 9, padY: -4, size: 6 })],
]);
const revealer = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    const element = entry.target;
    revealer.unobserve(element);
    element.classList.add("is-in");
    // Marks wait for the entrance to settle, so they land where the words end up.
    for (const [id, steps] of marksOnReveal) {
      const target = element.id === id ? element : $(`#${id}`, element);
      if (!target) continue;
      marksOnReveal.delete(id);
      wait(950).then(() => perform(() => steps(target), { near: target }));
    }
    if (element.dataset.script) wait(950).then(() => perform(scripts[element.dataset.script], { near: element }));
    if (element.classList.contains("beats")) wait(950).then(drawBendArrow);
  }
}, { threshold: 0.3, rootMargin: "0px 0px -8% 0px" });
$$("[data-reveal]").forEach((element) => revealer.observe(element));

// Keys -------------------------------------------------------------------------------------------
const keyState = $("[data-key-state]");
const keyFor = (event) => (event.code === "Digit2" ? "2" : event.key);
const press = (event, down) => $$(`kbd[data-key="${CSS.escape(keyFor(event))}"]`).forEach((key) => key.classList.toggle("is-down", down));
addEventListener("keydown", (event) => press(event, true));
addEventListener("keyup", (event) => press(event, false));
addEventListener("blur", () => $$("kbd.is-down").forEach((key) => key.classList.remove("is-down")));
function showMode(mode) {
  $('[data-keys="draw"]').classList.toggle("is-on", mode === "draw");
  $('[data-keys="use"]').classList.toggle("is-on", mode === "use");
  keyState.textContent = mode === "draw" ? "You’re drawing. Esc takes you back." : ink.marks().some((mark) => !scripted.has(mark.id)) ? "You’re using the page. Your drawings are still here." : "You’re using the page.";
}
ink.on("mode", showMode);
ink.on("shortcut", () => ink.setCollapsed(false));
showMode("use");

// Every mark stays editable: the visitor bends an arrow the page drew.
const bendButton = $("[data-bend]"), bendState = $("[data-bend-state]"), bendStage = $(".beat-edit");
let bendId = 0;
const bendSteps = () => [{ tool: "arrow", from: { el: $("#edit-from") }, to: { el: $("#edit-target"), dx: -11, dy: -11 }, bend: 0, color: RED, size: 4, duration: 600 }];
const hasBendArrow = () => ink.marks().some((mark) => mark.id === bendId);
async function drawBendArrow() {
  if (hasBendArrow()) return;
  const made = await perform(bendSteps, { near: bendStage });
  bendId = made[0]?.id || 0;
}
bendButton.addEventListener("click", async () => {
  await drawBendArrow();
  startDrawing("select");
  ink.select?.(bendId);
  say("Drag the dot in the middle of the arrow.", 6000);
});
ink.on("edit", (mark) => { if (mark.id === bendId) bendState.textContent = "Still an arrow. Bend it again."; });

// What else: small sketches of the idea ------------------------------------------------------------------
function whileSeen(element, start, stop) {
  new IntersectionObserver(([entry]) => (entry.isIntersecting ? start() : stop()), { threshold: 0.2 }).observe(element);
}
const breakCard = $(".v-break"), breakTime = $("[data-break-time]"), palettes = $$("[data-palette-pick]");
let breakLeft = 600, breakTimer = 0;
const showBreak = () => { breakTime.textContent = `${String(Math.floor(breakLeft / 60)).padStart(2, "0")}:${String(breakLeft % 60).padStart(2, "0")}`; };
if (!reduce) whileSeen(breakCard, () => {
  clearInterval(breakTimer);
  breakTimer = setInterval(() => { breakLeft = breakLeft > 1 ? breakLeft - 1 : 600; showBreak(); }, 1000);
}, () => clearInterval(breakTimer));
palettes.forEach((pick) => pick.addEventListener("click", () => {
  breakCard.dataset.palette = pick.dataset.palettePick;
  palettes.forEach((other) => other.setAttribute("aria-pressed", String(other === pick)));
}));

const boards = $(".v-boards"), boardPicks = $$("[data-board-pick]");
let boardTimer = 0;
function showBoard(name) {
  boards.dataset.board = name;
  boardPicks.forEach((pick) => pick.setAttribute("aria-pressed", String(pick.dataset.boardPick === name)));
}
const stopBoards = () => clearInterval(boardTimer);
if (!reduce) whileSeen(boards, () => {
  stopBoards();
  if (boards.dataset.touched) return;
  boardTimer = setInterval(() => showBoard({ desk: "white", white: "black", black: "desk" }[boards.dataset.board]), 2400);
}, stopBoards);
boardPicks.forEach((pick) => pick.addEventListener("click", () => { boards.dataset.touched = "1"; stopBoards(); showBoard(pick.dataset.boardPick); }));

const pad = $("[data-pointer-pad]"), halo = $(".halo", pad);
function moveHalo(event) {
  const box = pad.getBoundingClientRect();
  pad.style.setProperty("--hx", `${event.clientX - box.left}px`);
  pad.style.setProperty("--hy", `${event.clientY - box.top}px`);
  pad.classList.add("is-live");
}
pad.addEventListener("pointermove", moveHalo);
pad.addEventListener("pointerleave", () => pad.classList.remove("is-live"));
pad.addEventListener("pointerdown", (event) => {
  moveHalo(event);
  halo.classList.remove("is-click");
  void halo.offsetWidth;
  halo.classList.add("is-click");
});

// Playground -------------------------------------------------------------------------------------
const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
// An arrow's tip, whether the engine keeps two ends or a list of points.
const tip = (mark) => (mark.x2 != null ? [mark.x2, mark.y2] : mark.points?.[mark.points.length - 1] || [NaN, NaN]);
const scenes = {
  demo: {
    title: "Weekly numbers — Sample data", task: "Circle the number that dropped.", go: "Pick up the Ellipse", tool: "ellipse", color: RED,
    hint: "Drag around the number.",
    praise: "Circled. Nobody asks “where?” now.",
    done: (mark, target) => {
      if (!["ellipse", "rect", "diamond", "pen"].includes(mark.type)) return false;
      const b = ink.box(mark), cx = target.x + target.w / 2, cy = target.y + target.h / 2;
      return cx > b.x && cx < b.x + b.w && cy > b.y && cy < b.y + b.h && b.w < target.w * 5 && b.h < target.h * 5 && b.w > 30;
    },
  },
  review: {
    title: "deploy.ts — Sample code", task: "Blur the token before anyone sees it.", go: "Pick up Blur", tool: "blur",
    hint: "Drag across the token.",
    praise: "Covered. For real secrets, take them off the screen.",
    done: (mark, target) => mark.type === "blur" && overlap(ink.box(mark), target) / (target.w * target.h) > 0.6,
  },
  lesson: {
    title: "Lesson 4 — Sample slide", task: "Point an arrow at the longest side.", go: "Pick up the Arrow", tool: "arrow", color: TEAL,
    hint: "Drag for a straight arrow. Or click a few points, then press Return.",
    praise: "Pointed. Double-click the arrow to add a point you can drag.",
    done: (mark, target) => {
      const [x, y] = tip(mark), b = ink.box(mark);
      return mark.type === "arrow" && mark.head !== false && Math.hypot(b.w, b.h) > 30 && Math.hypot(x - (target.x + target.w / 2), y - (target.y + target.h / 2)) < 80;
    },
  },
};
const order = Object.keys(scenes), finished = new Set();
const tabs = $$("[data-scene]"), task = $("[data-task]"), taskText = $("[data-task-text]"), taskGo = $("[data-task-go]");
const progress = $("[data-progress]"), finale = $("[data-finale]"), windowElement = $("[data-playground-window]");
let scene = "demo";
function refreshTask() {
  const current = scenes[scene], done = finished.has(scene), next = order.find((name) => !finished.has(name));
  task.classList.toggle("is-done", done);
  taskText.textContent = done ? current.praise : current.task;
  taskGo.hidden = done && !next;
  taskGo.textContent = done && next ? `Next: ${$(`[data-scene="${next}"]`).textContent.toLowerCase()}` : current.go;
  progress.textContent = `${finished.size} of ${order.length} done`;
  tabs.forEach((tab) => tab.classList.toggle("is-done", finished.has(tab.dataset.scene)));
}
function showScene(name) {
  if (name === scene) return;
  // Marks sit on the page, so the ones over the window would cover the next screen. A plain click leaves a
  // mark with no size, which still has to go.
  const area = ink.rectOf(windowElement);
  const inside = (b) => b.x + b.w >= area.x && b.x <= area.x + area.w && b.y + b.h >= area.y && b.y <= area.y + area.h;
  ink.marks().filter((mark) => inside(ink.box(mark))).forEach((mark) => ink.remove(mark.id));
  scene = name;
  tabs.forEach((tab) => {
    const selected = tab.dataset.scene === name;
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    $(`#scene-${tab.dataset.scene}`).hidden = !selected;
  });
  $("[data-window-title]").textContent = scenes[name].title;
  refreshTask();
}
tabs.forEach((tab, index) => {
  tab.addEventListener("click", () => showScene(tab.dataset.scene));
  tab.addEventListener("keydown", (event) => {
    const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
    if (!step) return;
    const next = tabs[(index + step + tabs.length) % tabs.length];
    next.focus(); showScene(next.dataset.scene);
  });
});
taskGo.addEventListener("click", () => {
  if (finished.has(scene)) {
    const next = order.find((name) => !finished.has(name));
    if (next) showScene(next);
  }
  const current = scenes[scene], first = !greeted;
  startDrawing(current.tool, current.color);
  say(first ? `${current.hint} Esc takes you back to the page.` : current.hint, 6000);
});
function judge(mark) {
  if (finished.has(scene) || scripted.has(mark.id)) return;
  const target = $(`[data-target="${scene}"]`);
  if (!target || !scenes[scene].done(mark, ink.rectOf(target))) return;
  finished.add(scene);
  refreshTask();
  if (finished.size === order.length) {
    finale.hidden = false;
    wait(900).then(() => perform(() => [{ tool: "underline", under: $("em", finale), color: APRICOT, size: 6, dy: -4, duration: 600 }], { near: finale, cursor: false }));
  }
}
ink.on("mark", judge);
ink.on("edit", judge);
// The page's own marks stay: only what the visitor drew goes.
$("[data-clear]").addEventListener("click", () => ink.marks().filter((mark) => !scripted.has(mark.id)).forEach((mark) => ink.remove(mark.id)));
refreshTask();

// Install ----------------------------------------------------------------------------------------
const card = $(".install-card"), picks = $$("[data-platform-pick]"), copyStatus = $("[data-copy-status]");
function showPlatform(platform) {
  card.dataset.platform = platform;
  picks.forEach((pick) => pick.setAttribute("aria-pressed", String(pick.dataset.platformPick === platform)));
  copyStatus.textContent = "";
  ink.reflow();
}
picks.forEach((pick) => pick.addEventListener("click", () => showPlatform(pick.dataset.platformPick)));
showPlatform(root.dataset.os);
$$("[data-copy]").forEach((command) => {
  $("[data-copy-button]", command).addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText($("code", command).textContent);
      copyStatus.textContent = "Copied. Paste into Terminal and press Enter.";
    } catch {
      copyStatus.textContent = "Couldn’t copy. Select the command and copy it yourself.";
    }
  });
});
