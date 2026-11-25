import { startStage } from "./stage.js";
import { $, $$, reduced, no3d, onScreen, startHeader, startInstall } from "./page.js";

startHeader();
startStage({ reduced, no3d }).catch((error) => console.warn("Annotator: the opening scene could not start.", error));

const loadInk = () => import("/concepts/shared/ink.js").then((module) => module.createInk);
const RED = "#f04538";

// The problem: pull the layer across a busy screen -----------------------------------------------------
{
  const root = $("#compare"), screen = $("#compare-screen"), range = $("#compare-range"), cursor = $("#compare-cursor");
  const target = $("#cmp-target"), note = $("#cmp-note"), line = $("#says-line");
  const phrases = ["“This one…”", "“No, up…”", "“The red one.”"];
  let cut = 96, shown = false, touched = false, raf = 0, phrase = 0, phraseAt = 0, glide = null, started = false;
  const at = () => {
    const s = screen.getBoundingClientRect(), t = target.getBoundingClientRect();
    return { x: t.right - s.left + 10, y: t.bottom - s.top + 2, mid: (t.left + t.width / 2 - s.left) / s.width * 100 };
  };
  function place(now) {
    const p = at(), after = p.mid > cut, wave = !after && !reduced;
    root.dataset.side = after ? "after" : "before";
    const dx = wave ? 16 * Math.sin(now / 190) + 8 * Math.sin(now / 83) : 0, dy = wave ? 10 * Math.cos(now / 240) + 5 * Math.sin(now / 97) : 0;
    cursor.style.transform = `translate3d(${(p.x + dx).toFixed(1)}px, ${(p.y + dy).toFixed(1)}px, 0)`;
    if (wave && now - phraseAt > 1300) { phraseAt = now; phrase = (phrase + 1) % phrases.length; line.textContent = phrases[phrase]; }
  }
  const setCut = (value) => { cut = Math.min(96, Math.max(4, value)); screen.style.setProperty("--cut", `${cut}%`); range.value = Math.round(cut); };
  function tick(now) {
    raf = 0;
    if (glide) {
      const t = Math.min(1, (now - glide.start) / glide.time), k = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
      setCut(glide.from + (glide.to - glide.from) * k);
      if (t >= 1) glide = null;
    }
    place(now);
    if (shown && (!reduced || glide)) raf = requestAnimationFrame(tick);
  }
  const wake = () => { if (!raf) raf = requestAnimationFrame(tick); };
  range.addEventListener("input", () => { touched = true; glide = null; setCut(Number(range.value)); wake(); });
  if (reduced) { line.textContent = "“This one… no, up… the red one.”"; setCut(26); }
  onScreen(screen, (visible) => {
    shown = visible;
    if (!visible) return;
    wake();
    // The first time it comes into view the layer slides in by itself, once.
    if (!started && !touched && !reduced) { started = true; glide = { from: 96, to: 26, start: performance.now() + 900, time: 1900 }; }
  }, { threshold: 0.55 });
  addEventListener("resize", wake);

  // The marks on the layer are the shared engine's, drawn once; the layer is clipped to where the sheet is.
  loadInk().then((createInk) => {
    const ink = createInk({ surface: screen, interactive: false, color: RED, size: 4 });
    return ink.perform([
      { tool: "ellipse", around: target, padX: 16, padY: 10, duration: 1 },
      { tool: "text", at: { el: note }, text: "refunds doubled here", speed: 0 },
      { tool: "arrow", from: { el: note, dx: 96, dy: -8 }, to: { el: target, at: "bottom left", dx: -18, dy: 12 }, bend: 0.22, duration: 1 },
    ], { cursor: false });
  }).catch((error) => console.warn("Annotator: the marks for the comparison could not load.", error));
}

// Moments: one screen, six situations -------------------------------------------------------------------
{
  const tabs = $$("#moment-tabs [role=tab]"), panels = tabs.map((tab) => $(`#${tab.getAttribute("aria-controls")}`));
  const key = (panel, name) => $(`[data-k="${name}"]`, panel);
  const scripts = {
    code: (p) => [
      { tool: "ellipse", around: key(p, "line"), padX: 14, padY: 5, duration: 900 },
      { tool: "text", at: { el: key(p, "line"), at: "right", dx: 30, dy: -20 }, text: "why inside the loop?" },
      { wait: 350 },
      { tool: "blur", over: key(p, "token"), pad: 3, duration: 700 },
    ],
    class: (p) => [
      { run: () => $(".sample-board", p).classList.add("is-board") },
      { wait: 500 },
      ...[1, 2, 3].flatMap((n) => [
        { tool: "counter", at: { el: key(p, `n${n}`) }, color: "#2f6bff" },
        { tool: "text", at: { el: key(p, `s${n}`), at: "top left", dx: -4 }, text: key(p, `s${n}`).textContent, color: "#15161a", speed: 26 },
      ]),
      { wait: 250 },
      { tool: "highlight", over: key(p, "s3"), color: "#ffe14d" },
      { tool: "text", at: { el: key(p, "note") }, text: "nobody does this one", color: RED },
    ],
    support: (p) => [
      { tool: "counter", at: { el: key(p, "menu"), at: "right", dx: 24 } },
      { tool: "counter", at: { el: key(p, "toggle"), at: "left", dx: -26 } },
      { tool: "text", at: { el: key(p, "note"), dy: -18 }, text: "then save" },
      { tool: "arrow", from: { el: key(p, "note"), dx: 112, dy: 14 }, to: { el: key(p, "save"), at: "bottom", dy: 10 }, bend: -0.34 },
    ],
  };
  const inks = new Map();
  let active = 0, visible = false, loaded = false;
  // The moments always use the wide cut of a clip: on a phone it sits beside the scripted screens at the same shape.
  const file = (panel, type) => `/public/demo/stage-${panel.dataset.clip}.${type}`;

  async function play(panel, again) {
    const name = panel.dataset.scene;
    let state = inks.get(panel);
    if (!state) {
      const createInk = await loadInk();
      state = { ink: createInk({ surface: $("[data-surface]", panel), interactive: false, color: RED, size: 4 }), stop: null, done: false };
      inks.set(panel, state);
    }
    if (state.done && !again) return;
    state.stop?.abort();
    state.ink.clear(); state.ink.hideGhost();
    $(".sample-board", panel)?.classList.remove("is-board");
    const stop = state.stop = new AbortController();
    state.done = true;
    await new Promise((next) => setTimeout(next, again ? 150 : 450));
    if (stop.signal.aborted) return;
    await state.ink.perform(scripts[name](panel), { cursor: !reduced, signal: stop.signal });
    state.ink.hideGhost();
  }
  function media() {
    panels.forEach((panel, i) => {
      const video = $("video", panel), on = i === active;
      if (video) {
        if (loaded && video.dataset.poster !== file(panel, "jpg")) { video.dataset.poster = video.poster = file(panel, "jpg"); video.removeAttribute("src"); video.load(); }
        if (on && loaded) {
          if (!video.getAttribute("src")) video.src = file(panel, "mp4");
          video.controls = reduced;
          if (visible && !reduced) video.play().catch(() => {}); else if (!reduced) video.pause();
        } else if (!video.paused) video.pause();
      } else if (on && visible) play(panel).catch((error) => console.warn("Annotator: this scene could not play.", error));
    });
  }
  function pick(i, focus) {
    active = (i + tabs.length) % tabs.length;
    tabs.forEach((tab, n) => {
      const on = n === active;
      tab.setAttribute("aria-selected", String(on)); tab.tabIndex = on ? 0 : -1;
      panels[n].hidden = !on;
      if (on && focus) tab.focus();
      if (on) tab.scrollIntoView?.({ block: "nearest", inline: "nearest" });
    });
    media();
  }
  tabs.forEach((tab, i) => {
    tab.addEventListener("click", () => pick(i));
    tab.addEventListener("keydown", (event) => {
      const step = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[event.key];
      if (!step) return;
      event.preventDefault(); pick(i + step, true);
    });
  });
  panels.forEach((panel) => $(".replay", panel)?.addEventListener("click", () => play(panel, true)));
  const stage = $(".moment-stage");
  onScreen(stage, (near) => { if (near && !loaded) { loaded = true; media(); } }, { rootMargin: "800px 0px" });
  onScreen(stage, (shown) => { visible = shown; media(); }, { threshold: 0.35 });
}

// How it works: the keys are live ---------------------------------------------------------------------
const keysState = $("#keys-state");
const lamp = (mode) => {
  keysState.dataset.mode = mode;
  $("span", keysState).textContent = mode === "draw" ? "You are drawing. Every click and drag makes a mark." : "You are using your apps. Drawings stay visible.";
};
let ink = null, tryVisible = false;
{
  const caps = $$(".cap"), press = (key, down) => caps.filter((cap) => cap.dataset.key === key).forEach((cap) => cap.classList.toggle("is-down", down));
  const name = (event) => (event.code === "Digit2" ? "2" : event.key);
  const toggle = (mode) => {
    if (ink && tryVisible) ink.setMode(mode === "toggle" ? (ink.state.mode === "draw" ? "use" : "draw") : mode);
    else lamp(mode === "toggle" ? (keysState.dataset.mode === "draw" ? "use" : "draw") : mode);
  };
  document.addEventListener("keydown", (event) => {
    press(name(event), true);
    if (event.repeat) return;
    const combo = event.ctrlKey && event.shiftKey && event.code === "Digit2";
    if (combo && !(ink && tryVisible)) { event.preventDefault(); toggle("toggle"); }
    if (event.key === "Escape" && !(ink && tryVisible)) toggle("use");
  });
  document.addEventListener("keyup", (event) => press(name(event), false));
  addEventListener("blur", () => caps.forEach((cap) => cap.classList.remove("is-down")));
  $$(".combo").forEach((combo) => combo.addEventListener("click", () => toggle(combo.dataset.combo === "draw" ? "toggle" : "use")));
}

// The break timer in the stories counts down while you look at it ------------------------------------
{
  const face = $("#timer-face");
  let left = 600, timer = 0;
  onScreen(face, (shown) => {
    clearInterval(timer);
    if (!shown || reduced) return;
    timer = setInterval(() => {
      left = left > 1 ? left - 1 : 600;
      face.textContent = `${String(Math.floor(left / 60)).padStart(2, "0")}:${String(left % 60).padStart(2, "0")}`;
    }, 1000);
  });
}

// Try it: the shared web demo of the app, on a sample screen -----------------------------------------
async function startPlayground() {
  const playground = $("#playground"), startButton = $("#prompt-go"), status = $("#task-status");
  let createInk;
  try { createInk = await loadInk(); } catch (error) { console.warn("Annotator: the drawing demo could not load.", error); startButton.hidden = true; return; }
  ink = createInk({
    surface: playground, toolbarParent: playground, toolbarPosition: "absolute", appearance: "light", moreHref: "#install",
    tool: "ellipse", color: "#ff8a4c", size: 5, enabled: false,
    palette: ["#ff8a4c", "#ffe14d", "#35d0ba", "#5b8cff", "#ff5d5d", "#15161a"],
  });

  const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
  const drop = $("#pg-drop"), dip = $("#pg-dip"), email = $("#pg-email");
  // Where an arrow points: its last point, however the engine stores it.
  const tip = (m) => {
    const last = Array.isArray(m.points) && m.points.length ? m.points[m.points.length - 1] : null;
    if (last) return Array.isArray(last) ? { x: last[0], y: last[1] } : last;
    return { x: m.x2, y: m.y2 };
  };
  const tasks = [
    { text: "Circle the number that dropped", tool: "ellipse", name: "Ellipse", done: (m) => {
      if (!["ellipse", "rect", "pen"].includes(m.type)) return false;
      const box = ink.box(m), target = ink.rectOf(drop);
      return overlap(box, target) > target.w * target.h * 0.6 && box.w < target.w * 3.2 && box.h < target.h * 6 && (m.type !== "pen" || box.h > target.h * 0.7);
    } },
    { text: "Point an arrow at the dip", tool: "arrow", name: "Arrow", done: (m) => {
      if (m.type !== "arrow" || m.head === false) return false;
      // A click that never became an arrow has no length and points at nothing.
      const target = ink.rectOf(dip), end = tip(m), box = ink.box(m);
      return Math.hypot(box.w, box.h) > 36 && Math.hypot(end.x - target.x, end.y - target.y) < 78;
    } },
    { text: "Blur the email", tool: "blur", name: "Blur", done: (m) => {
      if (m.type !== "blur" && m.type !== "pixelate") return false;
      const target = ink.rectOf(email);
      return overlap(ink.box(m), target) > target.w * target.h * 0.55;
    } },
  ];
  const ticks = $$("#ticks li"), text = $("#prompt-text");
  const finale = () => (ink.state.mode === "draw" ? "All done. Now press Esc." : "All done. The ink stays, and the page works again.");
  let finished = [false, false, false];
  const current = () => finished.indexOf(false);
  // As in the app, the tool goes back to Select after each shape. So the button always offers the tool the
  // current task needs, and steps aside only while that tool is already in hand.
  function offer() {
    const now = current(), drawing = ink.state.mode === "draw", task = tasks[Math.max(0, now)];
    startButton.textContent = drawing ? `Pick ${task.name}` : "Start drawing";
    startButton.classList.toggle("is-idle", now < 0 || (drawing && ink.state.tool === task.tool));
  }
  // A task is done while some mark on the screen does the job, so undo and the eraser untick it again.
  function check() {
    const marks = ink.marks(), before = finished, was = before.indexOf(false);
    finished = tasks.map((task) => marks.some((m) => { try { return task.done(m); } catch { return false; } }));
    const now = current();
    ticks.forEach((tick, i) => { tick.classList.toggle("is-done", finished[i]); tick.classList.toggle("is-current", i === now); });
    text.textContent = now < 0 ? finale() : tasks[now].text;
    if (now !== was) {
      const gained = finished.some((done, i) => done && !before[i]);
      status.textContent = now < 0 ? finale() : `${gained ? "Done. Next" : "Step"} ${now + 1} of 3: ${tasks[now].text}.`;
      if (gained && now >= 0 && ink.state.mode === "draw") ink.setTool(tasks[now].tool);
    }
    offer();
  }
  ink.on("change", check);
  ink.on("tool", offer);
  ink.on("mode", (mode) => {
    if (current() < 0) text.textContent = status.textContent = finale();
    offer();
    if (tryVisible) lamp(mode);
  });
  startButton.addEventListener("click", () => { ink.setMode("draw"); ink.setTool(tasks[Math.max(0, current())].tool); });
  $("#start-over").addEventListener("click", () => {
    ink.clear();
    ink.setTool("ellipse");
    check();
    status.textContent = `Cleared. ${tasks[0].text}.`;
  });
  check();
  // The demo is live only while the sample screen is in view: away from it the keys belong to the page again.
  onScreen(playground, (shown) => {
    tryVisible = shown;
    ink.setEnabled(shown);
    if (!shown) lamp("use");
  }, { threshold: 0.25 });
}
startPlayground();

startInstall();
