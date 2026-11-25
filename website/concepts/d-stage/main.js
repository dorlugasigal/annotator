import { startShow } from "./show.js";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
const phone = matchMedia("(max-width: 640px)");
const no3d = new URLSearchParams(location.search).has("no3d");
const onScreen = (element, callback, options) => { const observer = new IntersectionObserver(([entry]) => callback(entry.isIntersecting, entry), options); observer.observe(element); return observer; };
const stamp = (seconds) => `00:${String(Math.floor(seconds)).padStart(2, "0")}.${Math.floor((seconds % 1) * 10)}`;
const play = (video) => { const started = video.play(); if (started) started.catch(() => {}); };
let inkModule = null;
const loadInk = () => (inkModule ||= import("/concepts/shared/ink.js"));

startShow({ reduced, no3d }).catch((error) => console.warn("Annotator: the opening show could not start.", error));

// Keycaps anywhere on the page go down with the real keys.
{
  const caps = $$(".cap[data-key]"), press = (key, down) => caps.filter((cap) => cap.dataset.key === key).forEach((cap) => cap.classList.toggle("is-held", down));
  const nameOf = (event) => (event.code === "Digit2" ? "2" : event.key);
  document.addEventListener("keydown", (event) => press(nameOf(event), true));
  document.addEventListener("keyup", (event) => press(nameOf(event), false));
  addEventListener("blur", () => caps.forEach((cap) => cap.classList.remove("is-held")));
}

// Moments: six questions, and one monitor that answers the chosen one with a real clip or a scripted scene ---
{
  const section = $("#moments"), monitor = $("#moment-monitor"), video = $("#moment-video"), items = $$(".moment"), scenes = $$(".scene");
  const playButton = $("#moment-play"), number = $("#moment-n"), kind = $("#moment-kind"), bar = $("#moment-progress"), status = $("#moment-status");
  const el = (id) => document.getElementById(id);
  // What the script draws in each scene. Marks follow these elements, so they stay in place at every size.
  const SCRIPTS = {
    code: { color: "#ffb58c", steps: () => [
      { tool: "rect", around: el("sc-loop"), padX: 9, padY: 2, duration: 900 },
      { tool: "text", at: phone.matches ? { el: el("sc-loop"), at: "bottom left", dx: 30, dy: 44 } : { el: el("sc-loop"), at: "right", dx: 18, dy: -18 }, text: "why five tries?" },
      { wait: 350 },
      { tool: "blur", over: el("sc-token"), pad: 3, duration: 600 },
    ] },
    board: { color: "#e5484d", steps: () => [
      { tool: "underline", under: el("wb-title"), duration: 600 },
      { tool: "counter", at: { el: el("wb-1") } }, { wait: 250 },
      { tool: "counter", at: { el: el("wb-2") } }, { wait: 250 },
      { tool: "counter", at: { el: el("wb-3") } }, { wait: 300 },
      { tool: "highlight", over: el("wb-key"), color: "#ffe14d", duration: 700 },
    ] },
    support: { color: "#e5484d", steps: () => [
      { tool: "counter", at: { el: el("st-nav"), at: phone.matches ? "top right" : "right", dx: phone.matches ? -4 : -16 } }, { wait: 250 },
      { tool: "counter", at: { el: el("st-toggle"), at: phone.matches ? "top left" : "left", dx: phone.matches ? -6 : -24, dy: phone.matches ? -8 : 0 } }, { wait: 250 },
      { tool: "counter", at: { el: el("st-save"), at: "bottom", dy: 26 } },
      { tool: "rect", around: el("st-save"), pad: 6, duration: 600 },
      { tool: "arrow", from: phone.matches ? { el: el("st-toggle"), at: "bottom", dx: -6, dy: 26 } : { el: el("st-toggle"), at: "bottom", dx: -70, dy: 30 }, to: { el: el("st-save"), at: "top", dx: -10, dy: -12 }, bend: phone.matches ? 0.2 : -0.22, duration: 600 },
    ] },
  };
  const inks = {};
  let active = 0, near = false, visible = false, playing = !reduced, picked = false, running = null, stop = null, token = 0, raf = 0, next = 0;
  const file = (clip, type) => `/public/demo/stage-${clip}${phone.matches ? "-phone" : ""}.${type}`;
  const progress = (value) => { bar.style.transform = `scaleX(${Math.min(1, value).toFixed(4)})`; };
  function tick() {
    raf = 0;
    if (items[active].dataset.kind !== "clip") return;
    progress((video.currentTime || 0) / (video.duration || 1));
    if (!video.paused) raf = requestAnimationFrame(tick);
  }
  // While nobody has chosen, the monitor moves on to the next moment by itself.
  function later(ms) {
    clearTimeout(next);
    if (!picked && !reduced) next = setTimeout(() => { if (!picked && visible && !document.hidden) show(active + 1); else later(1500); }, ms);
  }
  async function scene(name, mine) {
    const target = scenes.find((s) => s.dataset.scene === name), script = SCRIPTS[name];
    let createInk;
    try { ({ createInk } = await loadInk()); } catch (error) { console.warn("Annotator: the drawing demo could not load.", error); return; }
    // The last script finishes its current stroke before this one starts, so nothing is drawn twice.
    stop?.abort();
    await running;
    if (mine !== token) return;
    const ink = (inks[name] ||= createInk({ surface: target, interactive: false, color: script.color, size: 4 }));
    ink.clear();
    progress(0);
    const steps = script.steps(), control = new AbortController();
    stop = control;
    const counted = steps.flatMap((step, i) => [step, { run: () => { if (mine === token) progress((i + 1) / steps.length); } }]);
    running = ink.perform(counted, { cursor: !reduced, signal: control.signal }).catch(() => {}).then(() => { ink.hideGhost(); });
    await running;
    if (mine === token) later(3200);
  }
  function show(index, announce) {
    active = (index + items.length) % items.length;
    const item = items[active], mine = ++token, isClip = item.dataset.kind === "clip";
    clearTimeout(next);
    items.forEach((other, i) => { other.classList.toggle("is-active", i === active); $("button", other).setAttribute("aria-pressed", String(i === active)); });
    number.textContent = String(active + 1).padStart(2, "0");
    kind.textContent = isClip ? "Recording of the real app" : "Web demo, drawn by a script";
    video.hidden = !isClip;
    scenes.forEach((s) => { s.hidden = isClip || s.dataset.scene !== item.dataset.scene; });
    playButton.setAttribute("aria-pressed", String(isClip ? playing : true));
    playButton.setAttribute("aria-label", isClip ? (playing ? "Pause the clip" : "Play the clip") : "Draw it again");
    playButton.classList.toggle("is-replay", !isClip);
    if (announce) status.textContent = `Moment ${active + 1} of ${items.length}: ${$("button", item).textContent} ${isClip ? "A recording of the real app is playing." : "A scripted web demo is drawing."}`;
    if (!near) return;
    if (isClip) {
      stop?.abort();
      if (video.dataset.clip !== file(item.dataset.clip, "mp4")) {
        video.dataset.clip = file(item.dataset.clip, "mp4");
        video.poster = file(item.dataset.clip, "jpg");
        video.src = file(item.dataset.clip, "mp4");
        video.setAttribute("aria-label", item.dataset.label);
      }
      if (playing && visible && !document.hidden) { play(video); later(9000); } else video.pause();
      tick();
    } else {
      video.pause();
      if (visible) scene(item.dataset.scene, mine);
    }
  }
  items.forEach((item, i) => $("button", item).addEventListener("click", () => {
    picked = true;
    if (i !== active || items[i].dataset.kind === "scene") { if (items[i].dataset.kind === "clip") playing = true; show(i, true); }
    // On a narrow page the monitor is above the list: bring it into view.
    const box = monitor.getBoundingClientRect();
    if (box.top < 60 || box.bottom > innerHeight) monitor.scrollIntoView({ block: "center", behavior: reduced ? "auto" : "smooth" });
  }));
  playButton.addEventListener("click", () => {
    picked = true;
    if (items[active].dataset.kind === "clip") playing = !playing;
    show(active);
  });
  for (const type of ["playing", "pause", "seeked"]) video.addEventListener(type, () => { if (!raf) tick(); });
  show(0);
  onScreen(section, (shown) => { if (shown && !near) { near = true; show(active); } }, { rootMargin: "600px 0px" });
  onScreen(monitor, (shown) => { const was = visible; visible = shown; if (shown !== was) show(active); }, { threshold: 0.3 });
  document.addEventListener("visibilitychange", () => { if (items[active].dataset.kind === "clip") show(active); });
  phone.addEventListener("change", () => show(active));
}

// Intermission: a sketch of the break timer, counting down while you can see it -------------------------------
{
  const card = $("#break-card"), clock = $("#timer-clock"), palettes = $$("[data-palette]", $("#timer"));
  let left = 600, timer = 0;
  const draw = () => { clock.textContent = `${String(Math.floor(left / 60)).padStart(2, "0")}:${String(left % 60).padStart(2, "0")}`; };
  palettes.forEach((button) => button.addEventListener("click", () => {
    card.dataset.palette = button.dataset.palette;
    palettes.forEach((other) => other.setAttribute("aria-pressed", String(other === button)));
  }));
  onScreen(card, (shown) => {
    clearInterval(timer);
    if (shown && !reduced) timer = setInterval(() => { left = left > 0 ? left - 1 : 600; draw(); }, 1000);
  }, { threshold: 0.3 });
  draw();
}

// Try it: the shared web demo of the app, on a sample screen ----------------------------------------------
async function startPlayground() {
  const stage = $("#pg-stage"), playground = $("#playground"), go = $("#cue-go"), status = $("#cue-status"), items = $$("#cue-list li");
  let createInk;
  try { ({ createInk } = await loadInk()); } catch (error) { console.warn("Annotator: the drawing demo could not load.", error); go.hidden = true; return; }
  const ink = createInk({
    surface: playground, toolbarParent: stage, toolbarPosition: "absolute", appearance: "dark", moreHref: "#install",
    tool: "ellipse", color: "#ff7a45", size: 5, enabled: false,
    palette: ["#ff7a45", "#ffe14d", "#35d0ba", "#5b8cff", "#ff5d5d", "#14171f"],
  });
  // Room under the sample screen for the toolbar at its tallest, so it never covers the screen or moves the page.
  let room = 0;
  const reserve = () => { const height = ink.toolbar.offsetHeight; if (height + 64 > room) { room = height + 64; stage.style.setProperty("--bar-room", `${room}px`); } };
  ink.on("layout", reserve);

  const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
  const drop = $("#pg-drop"), dip = $("#pg-dip"), email = $("#pg-email");
  // A click with a shape tool leaves a zero-size mark, so every check also asks for a real size.
  const tasks = [
    { tool: "ellipse", name: "Ellipse", how: "then drag around 41%. A rectangle or a Pen ring works too.", done: (m) => {
      if (!["ellipse", "rect", "pen"].includes(m.type)) return false;
      const box = ink.box(m), target = ink.rectOf(drop);
      return box.w > 12 && box.h > 12 && overlap(box, target) > target.w * target.h * 0.6 && box.w < target.w * 3.2 && box.h < target.h * 6 && (m.type !== "pen" || box.h > target.h * 0.7);
    } },
    { tool: "arrow", name: "Arrow", how: "then drag it so it ends on the lowest point of the chart.", done: (m) => {
      if (m.type !== "arrow" || m.head === false) return false;
      const target = ink.rectOf(dip);
      return Math.hypot(m.x2 - m.x1, m.y2 - m.y1) > 30 && Math.hypot(m.x2 - target.x, m.y2 - target.y) < 80;
    } },
    { tool: "blur", name: "Blur", button: "Effects", how: "then drag over the email address.", done: (m) => {
      if (m.type !== "blur") return false;
      const box = ink.box(m), target = ink.rectOf(email);
      return box.w > 12 && box.h > 6 && overlap(box, target) > target.w * target.h * 0.55;
    } },
  ];
  let finished = tasks.map(() => false), engineHint = "";
  const current = () => finished.indexOf(false);
  // The tool goes back to Select after each mark, as in the app, so the line always says what to pick next.
  const say = () => {
    const now = current(), drawing = ink.state.mode === "draw";
    items.forEach((item, i) => item.classList.toggle("is-armed", drawing && ink.state.tool === tasks[i].tool));
    if (now < 0) status.textContent = drawing ? "All three done. Now press Esc." : "All three done. The ink stays, and the page under it works again.";
    else if (!drawing) status.textContent = "Click a cue to pick its tool, then draw on the screen below.";
    else if (engineHint) status.textContent = engineHint;
    else if (ink.state.tool === tasks[now].tool) status.textContent = `Cue ${now + 1}: ${tasks[now].name} is picked, ${tasks[now].how}`;
    else status.textContent = `Cue ${now + 1}: click the cue or pick ${tasks[now].button || tasks[now].name} in the toolbar, ${tasks[now].how}`;
  };
  // A cue is done while some mark on the screen does the job, so undo and the eraser untick it again.
  function check() {
    const marks = ink.marks();
    finished = tasks.map((task) => marks.some((m) => { try { return task.done(m); } catch { return false; } }));
    const now = current();
    items.forEach((item, i) => { item.classList.toggle("is-done", finished[i]); item.classList.toggle("is-current", i === now); });
    say();
  }
  for (const name of ["change", "mark", "edit", "clear"]) ink.on(name, check);
  ink.on("tool", say);
  ink.on("status", (words) => { engineHint = words; say(); });
  ink.on("mode", (mode) => {
    go.textContent = mode === "draw" ? "Use the page" : ink.marks().length ? "Keep drawing" : "Start drawing";
    say();
  });
  // Each cue picks its tool, as often as the visitor needs it.
  items.forEach((item, i) => $("button", item).addEventListener("click", () => ink.setTool(tasks[i].tool)));
  go.addEventListener("click", () => { if (ink.state.mode === "draw") ink.setMode("use"); else ink.setTool(tasks[Math.max(0, current())].tool); });
  $("#start-over").addEventListener("click", () => { ink.clear(); if (ink.state.mode === "draw") ink.setTool("ellipse"); check(); });
  check();
  // The demo is live only while its screen is in view, so its keys and toolbar stay out of the other sections.
  onScreen(stage, (shown) => {
    ink.setEnabled(shown);
    if (shown) { ink.fit(); reserve(); }
  }, { threshold: 0.12 });
}
startPlayground();

// The cast: tools with a play mark show their recording on the small monitor -------------------------------------
{
  const video = $("#take-video"), takes = $$(".take"), playButton = $("#take-play"), clock = $("#take-clock"), bar = $("#take-progress");
  const name = $("#take-name"), text = $("#take-text"), status = $("#take-status"), monitor = $("#cast-monitor");
  let active = takes.findIndex((take) => take.dataset.clip === "circle"), near = false, visible = false, playing = !reduced, raf = 0;
  const file = (take, type) => `/public/demo/stage-${take.dataset.clip}${phone.matches ? "-phone" : ""}.${type}`;
  function tick() {
    raf = 0;
    const total = video.duration || Number(takes[active].dataset.seconds), now = Math.min(video.currentTime || 0, total);
    clock.textContent = `${stamp(now)} / ${stamp(total)}`;
    bar.style.transform = `scaleX(${(now / total).toFixed(4)})`;
    if (!video.paused) raf = requestAnimationFrame(tick);
  }
  function sync() {
    const take = takes[active];
    if (near && video.dataset.clip !== file(take, "mp4")) {
      video.dataset.clip = file(take, "mp4");
      video.poster = file(take, "jpg");
      video.src = file(take, "mp4");
      video.setAttribute("aria-label", `Recording of ${take.textContent}`);
    }
    if (near && playing && visible && !document.hidden) play(video); else if (!video.paused) video.pause();
    playButton.setAttribute("aria-pressed", String(playing));
    playButton.setAttribute("aria-label", playing ? "Pause the clip" : "Play the clip");
    tick();
  }
  function pick(index, announce) {
    active = index;
    takes.forEach((take, i) => take.setAttribute("aria-pressed", String(i === active)));
    name.textContent = takes[active].textContent;
    text.textContent = takes[active].dataset.text;
    if (announce) { playing = true; status.textContent = `${name.textContent}: ${text.textContent}`; }
    sync();
  }
  takes.forEach((take, i) => take.addEventListener("click", () => {
    pick(i, true);
    const box = monitor.getBoundingClientRect();
    if (box.top < 60 || box.bottom > innerHeight) monitor.scrollIntoView({ block: "center", behavior: reduced ? "auto" : "smooth" });
  }));
  playButton.addEventListener("click", () => { playing = !playing; sync(); });
  for (const type of ["playing", "pause", "seeked", "loadedmetadata"]) video.addEventListener(type, () => { if (!raf) tick(); });
  pick(active);
  onScreen($("#tools"), (shown) => { if (shown) { near = true; sync(); } }, { rootMargin: "600px 0px" });
  onScreen(monitor, (shown) => { visible = shown; sync(); }, { threshold: 0.25 });
  document.addEventListener("visibilitychange", sync);
  phone.addEventListener("change", sync);
}

// Install ---------------------------------------------------------------------------------------------
{
  const tabs = $$(".switch [role=tab]");
  const pick = (os, focus) => tabs.forEach((tab) => {
    const on = tab.dataset.os === os;
    tab.setAttribute("aria-selected", String(on));
    tab.tabIndex = on ? 0 : -1;
    $(`#${tab.getAttribute("aria-controls")}`).hidden = !on;
    if (on && focus) tab.focus();
  });
  tabs.forEach((tab, i) => {
    tab.addEventListener("click", () => pick(tab.dataset.os));
    tab.addEventListener("keydown", (event) => {
      if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
      event.preventDefault();
      pick(tabs[(i + 1) % tabs.length].dataset.os, true);
    });
  });
  pick(document.documentElement.dataset.os);

  $$(".copy").forEach((button) => {
    const label = button.getAttribute("aria-label");
    let timer = 0;
    button.addEventListener("click", async () => {
      const code = $(`#${button.dataset.copy}`);
      let ok = true;
      try { await navigator.clipboard.writeText(code.textContent); }
      catch {
        const range = document.createRange();
        range.selectNodeContents(code);
        getSelection().removeAllRanges(); getSelection().addRange(range);
        try { ok = document.execCommand("copy"); } catch { ok = false; }
      }
      button.textContent = ok ? "Copied" : "Select it";
      button.setAttribute("aria-label", ok ? "Copied" : "Could not copy. The command is selected, press copy on your keyboard.");
      button.classList.toggle("is-done", ok);
      clearTimeout(timer);
      timer = setTimeout(() => { button.textContent = "Copy"; button.setAttribute("aria-label", label); button.classList.remove("is-done"); }, 1800);
    });
  });
}
