// The show: six shots that the page scrolls through. This file directs (scroll, clips, pointer, ink and laser);
// stage.js draws the room in 3D, and a flat stage below draws the same show without WebGL.
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (t) => { const x = clamp(t); return x * x * (3 - 2 * x); };

const APRICOT = "#ffb58c", CORE = "#fff1e6";
const BACK_ROW = 1, KEYS = 2, SPOT = 3, LASER = 4, ZOOM = 5;
// Which recording each shot plays. The back-row shot holds one frame, so there is something still to point at.
const SHOTS = [{ clip: "reel" }, { clip: "circle", freeze: 0.5 }, { clip: "smart" }, { clip: "blur" }, { clip: "highlight" }, { clip: "zoom" }];
const CLIPS = {
  reel: { file: "annotator-reel", w: 1920, h: 1080 }, circle: { file: "stage-circle", w: 1728, h: 1080 }, smart: { file: "stage-smart", w: 1728, h: 1080 },
  blur: { file: "stage-blur", w: 1728, h: 1080 }, highlight: { file: "stage-highlight", w: 1728, h: 1080 }, zoom: { file: "stage-zoom", w: 1728, h: 1080 },
};
// Where things are on the held frame (u, v from the top left of the picture): the number people can't find,
// and the ring, arrow and note that answer it. Then the ring the "one key" shot draws by itself.
const PLAN = {
  wide: { wiggle: [0.535, 0.2], ring: [0.474, 0.132, 0.086, 0.078], arrow: [[0.705, 0.35], [0.67, 0.165], [0.578, 0.152]], note: [0.635, 0.455], size: 0.074, demo: [0.56, 0.47, 0.15, 0.2] },
  tall: { wiggle: [0.64, 0.21], ring: [0.578, 0.133, 0.17, 0.052], arrow: [[0.84, 0.37], [0.86, 0.2], [0.775, 0.143]], note: [0.5, 0.45], size: 0.1, demo: [0.5, 0.46, 0.3, 0.16] },
};
const loop = ([cx, cy, rx, ry], steps = 70) => Array.from({ length: steps + 1 }, (_, i) => {
  const k = i / steps, a = -2.5 + k * Math.PI * 2.12, grow = 0.95 + k * 0.08 + 0.02 * Math.sin(a * 2 + 0.7);
  return { u: cx + Math.cos(a) * rx * grow, v: cy + Math.sin(a) * ry * grow };
});

export async function startShow({ reduced, no3d }) {
  const show = $("#show"), view = $("#view"), header = $("#top"), hud = $("#hud"), playButton = $("#play"), timecode = $("#timecode"), marks = $$(".shot-list a");
  const pad = $("#draw-pad"), crowd = $("#crowd"), lamp = $("#stage-mode"), undoButton = $("#stage-undo");
  const shots = $$(".shot", show).map((el, i) => ({ el, pin: $(".shot-pin", el), copy: $(".shot-copy", el), ...SHOTS[i], right: 0, hold: 1 }));
  const stacked = matchMedia("(max-width: 860px), (max-aspect-ratio: 1/1)"), phone = matchMedia("(max-width: 640px)");

  // Ink, the pointer and the laser are painted on one canvas that lies on the screen.
  const overlay = document.createElement("canvas"), ink = overlay.getContext("2d");
  let overlayVersion = 0, ready = false, raf = 0;

  let stage = null;
  if (!no3d) {
    try {
      const { createStage, webglAvailable } = await import("./stage.js");
      if (webglAvailable()) stage = createStage($("#scene"), { reduced });
    } catch (error) { console.warn("Annotator: the 3D stage could not start, showing the flat one.", error); }
  }
  if (!stage) stage = createFlat(overlay);
  document.documentElement.classList.add(stage.flat ? "is-flat" : "has-3d");
  $("#flat").hidden = !stage.flat;
  stage.lost?.(() => location.reload());

  // Clips ------------------------------------------------------------------------------------------
  const holder = stage.flat ? $("#flat-clips") : $("#clips"), clips = new Map();
  function clipOf(name) {
    const tall = phone.matches, key = `${name}${tall ? "-phone" : ""}`;
    if (clips.has(key)) return clips.get(key);
    const info = CLIPS[name], base = `/public/demo/${info.file}${tall ? "-phone" : ""}`;
    const image = new Image(), video = document.createElement("video");
    const clip = { key, name, image, video, src: `${base}.mp4`, aspect: tall ? 0.8 : info.w / info.h, width: tall ? 1080 : info.w, live: false };
    image.onload = wake;
    image.src = `${base}.jpg`;
    video.muted = video.defaultMuted = video.playsInline = video.loop = true;
    video.setAttribute("muted", ""); video.setAttribute("playsinline", "");
    video.preload = "none"; video.poster = `${base}.jpg`; video.tabIndex = -1;
    video.addEventListener("playing", () => { clip.live = true; wake(); });
    for (const type of ["loadeddata", "seeked"]) video.addEventListener(type, () => { stage.touch(clip); wake(); });
    holder.append(video);
    clips.set(key, clip);
    return clip;
  }
  const load = (clip) => { if (!clip.video.getAttribute("src")) { clip.video.preload = "auto"; clip.video.src = clip.src; } };
  let current = 0, front = clipOf(SHOTS[0].clip), back = null, mix = 0;
  // Autoplay is off for people who asked for less motion; the button plays the recording.
  let playing = !reduced, visible = true;
  function syncPlayback() {
    const active = back || front, hold = shots[current].freeze, held = hold !== undefined, run = playing && visible && !document.hidden;
    for (const clip of clips.values()) {
      const video = clip.video;
      if (clip !== active) { if (!video.paused) video.pause(); continue; }
      load(clip);
      if (held) {
        clip.live = true;
        if (!video.paused) video.pause();
        if (Math.abs(video.currentTime - hold) > 0.02) video.currentTime = hold;
      } else if (run) { const started = video.play(); if (started) started.catch(() => {}); } else if (!video.paused) video.pause();
    }
    playButton.hidden = held;
    playButton.setAttribute("aria-pressed", String(playing));
    playButton.setAttribute("aria-label", playing ? "Pause the recording" : "Play the recording");
    hud.dataset.state = held ? "held" : playing ? "playing" : "paused";
  }
  function cut(index, first) {
    if (index === current && !first) return;
    current = index;
    const next = clipOf(shots[index].clip);
    if (back) { front = mix > 0.5 ? back : front; back = null; mix = 0; }
    if (next !== front) { back = next; mix = reduced ? 1 : 0; }
    [index - 1, index + 1].forEach((n) => { if (shots[n]) load(clipOf(shots[n].clip)); });
    marks.forEach((mark, i) => { if (i === index) mark.setAttribute("aria-current", "true"); else mark.removeAttribute("aria-current"); });
    syncPlayback();
  }
  playButton.addEventListener("click", () => { playing = !playing; syncPlayback(); wake(); });

  // Layout and scroll --------------------------------------------------------------------------------
  const aim = { focus: 0, holds: shots.map(() => 0), stacked: false };
  const size = { w: 1, h: 1, top: 0, left: 0 };
  const plan = () => PLAN[phone.matches ? "tall" : "wide"];
  function layout() {
    size.w = view.clientWidth; size.h = view.clientHeight;
    stage.resize(size.w, size.h);
    const tall = phone.matches, aspect = tall ? 0.8 : 1.6;
    if (overlay.dataset.aspect !== String(aspect)) {
      overlay.dataset.aspect = aspect;
      overlay.width = tall ? 1080 : 1600; overlay.height = tall ? 1350 : 1000;
      // A phone gets the upright recordings.
      const active = clipOf(shots[current].clip);
      if (active !== front) { front = active; back = null; mix = 0; syncPlayback(); }
      demo.loop = loop(plan().demo);
      if (demo.stroke) demo.stroke.points = demo.loop;
      painted = "";
    }
    shots.forEach((shot) => { shot.right = shot.copy.getBoundingClientRect().right; shot.hold = Math.max(1, shot.el.offsetHeight - size.h); });
    scroll();
  }
  function scroll() {
    const rect = view.getBoundingClientRect(), vh = size.h, stack = stacked.matches;
    size.top = rect.top; size.left = rect.left;
    const tops = shots.map((shot) => shot.el.getBoundingClientRect().top);
    aim.focus = tops.slice(1).reduce((sum, top) => sum + smooth(1 - top / (vh * 0.6)), 0);
    aim.holds = tops.map((top, i) => clamp(-top / shots[i].hold));
    aim.stacked = stack;
    shots.forEach((shot) => {
      // Text fades as its pinned block arrives and leaves, faster on a narrow page where it passes the screen.
      const c = shot.pin.getBoundingClientRect().top / vh;
      const o = c >= 0 ? 1 - smooth((c - 0.06) / (stack ? 0.3 : 0.36)) : 1 - smooth((-c - 0.02) / (stack ? 0.12 : 0.34));
      shot.copy.style.setProperty("--o", o.toFixed(3));
      shot.copy.style.visibility = o < 0.01 ? "hidden" : "";
    });
    header.classList.toggle("is-stuck", scrollY > 16);
    wake();
  }
  function region(focus) {
    const top = header.offsetHeight;
    if (aim.stacked) {
      const edge = Math.max(14, size.w * 0.05), y = top + 6, bottom = size.h * (size.w >= 700 ? 0.6 : 0.505);
      return { x: edge, y, w: size.w - edge * 2, h: bottom - y };
    }
    const i = Math.min(shots.length - 2, Math.floor(focus)), right = lerp(shots[i].right, shots[i + 1].right, smooth(focus - i));
    const x = right + Math.max(40, size.w * 0.04), edge = Math.max(36, size.w * 0.035), y = top + size.h * 0.035;
    return { x, y, w: Math.max(120, size.w - edge - x), h: size.h * 0.73 - y };
  }

  // Pointer ------------------------------------------------------------------------------------------
  const pointer = { x: 0, y: 0, at: -1e9, seen: false };
  addEventListener("pointermove", (event) => {
    if (event.pointerType === "touch" || reduced) return;
    pointer.x = event.clientX; pointer.y = event.clientY; pointer.at = performance.now(); pointer.seen = true;
    if (visible) wake();
  }, { passive: true });

  // State, eased every frame ---------------------------------------------------------------------------
  const state = {
    time: 0, focus: 0, hold: aim.holds, zoom: 0.3, region: { x: 0, y: 0, w: 1, h: 1 }, stacked: false, parallax: { x: 0, y: 0 }, aspect: 16 / 9,
    a: front, b: null, mix: 0, dim: 1, glow: [0.5, 0.55, 0.7], overlay, overlayVersion: 0, inkOn: false,
    spot: { u: 0.6, v: 0.46, r: 0.25, on: 0 }, laser: { u: 0.58, v: 0.52, on: 0 },
  };
  let auto = 1, drawn = 0, trail = [], painted = "", glowAt = 0, last = 0, started = false, placed = false;
  const glowTarget = [0.5, 0.55, 0.7], sampler = document.createElement("canvas").getContext("2d", { willReadFrequently: true });
  sampler.canvas.width = 8; sampler.canvas.height = 5;
  const weight = (i) => smooth(1 - Math.abs(state.focus - i) * 1.7);

  // The back row: without Annotator (a small pointer that wiggles) or with it (a ring, an arrow, a note).
  const compare = shots[BACK_ROW].el, compareButtons = $$("[data-answer]", compare);
  let answer = -1, chosen = false;
  function setAnswer(value) {
    if (value === answer) return;
    answer = value;
    compare.dataset.answer = value;
    compareButtons.forEach((button) => button.setAttribute("aria-pressed", String(Number(button.dataset.answer) === value)));
    $$(".when-before", compare).forEach((el) => { el.hidden = value === 1; });
    $$(".when-after", compare).forEach((el) => { el.hidden = value === 0; });
    shots[BACK_ROW].right = shots[BACK_ROW].copy.getBoundingClientRect().right;
    wake();
  }
  compareButtons.forEach((button) => button.addEventListener("click", () => { chosen = true; setAnswer(Number(button.dataset.answer)); }));
  setAnswer(0);

  // One key: Control-Shift-2 draws on the big screen, Esc goes back, and what you drew stays.
  const strokes = [], demo = { at: -1, step: 0, done: false, stroke: null, loop: loop(plan().demo) };
  let mode = "use", strokeVersion = 0, pen = null, taken = false;
  const keysLive = () => Math.abs(aim.focus - KEYS) < 0.5 && visible;
  function setMode(next) {
    if (mode === next) return;
    mode = next;
    shots[KEYS].el.dataset.mode = next;
    pad.classList.toggle("is-drawing", next === "draw");
    lamp.dataset.mode = next;
    $("span", lamp).textContent = next === "draw"
      ? (matchMedia("(hover: hover)").matches ? "You are drawing. Drag on the big screen." : "You are drawing. Drag a finger on the screen above.")
      : strokes.length ? "You are back in your app. The drawing stays." : "You are using your apps.";
    wake();
  }
  const press = (kind) => {
    const caps = $$(`[data-do="${kind}"] .cap`, shots[KEYS].el);
    caps.forEach((cap) => cap.classList.add("is-down"));
    setTimeout(() => caps.forEach((cap) => cap.classList.remove("is-down")), 340);
  };
  // Anything the visitor does ends the short demonstration and leaves its ring whole.
  function take() {
    taken = true;
    if (demo.stroke && !demo.done) { demo.stroke.points = demo.loop; strokeVersion++; }
    demo.done = true;
  }
  const refreshUndo = () => { undoButton.disabled = !strokes.length; };
  $$("[data-do]", shots[KEYS].el).forEach((button) => button.addEventListener("click", () => {
    take();
    setMode(button.dataset.do === "draw" ? (mode === "draw" ? "use" : "draw") : "use");
  }));
  undoButton.addEventListener("click", () => { take(); strokes.pop(); strokeVersion++; refreshUndo(); setMode(mode); $("span", lamp).textContent = "Undone. Every mark can be moved, restyled or removed."; wake(); });
  document.addEventListener("keydown", (event) => {
    if (!keysLive() || event.repeat) return;
    if (event.ctrlKey && event.shiftKey && event.code === "Digit2") { event.preventDefault(); take(); setMode(mode === "draw" ? "use" : "draw"); }
    else if (event.key === "Escape" && mode === "draw") { take(); setMode("use"); }
  });
  const onScreen = (event) => {
    const hit = stage.pick(event.clientX - size.left, event.clientY - size.top);
    return hit && hit.u > -0.01 && hit.u < 1.01 && hit.v > -0.01 && hit.v < 1.01 ? { u: clamp(hit.u, 0.004, 0.996), v: clamp(hit.v, 0.006, 0.994) } : null;
  };
  pad.addEventListener("pointerdown", (event) => {
    if (mode !== "draw" || event.button > 0) return;
    const hit = onScreen(event);
    if (!hit) return;
    event.preventDefault();
    take();
    pad.setPointerCapture(event.pointerId);
    pen = { points: [hit, hit] };
    if (strokes.length > 60) strokes.shift();
    strokes.push(pen);
    strokeVersion++; refreshUndo(); wake();
  });
  pad.addEventListener("pointermove", (event) => {
    if (!pen) return;
    const hit = onScreen(event), tip = pen.points[pen.points.length - 1];
    if (!hit || Math.hypot(hit.u - tip.u, hit.v - tip.v) < 0.004) return;
    pen.points.push(hit);
    strokeVersion++; wake();
  });
  for (const type of ["pointerup", "pointercancel"]) pad.addEventListener(type, () => { pen = null; });
  refreshUndo();

  function frame(now) {
    raf = 0;
    const dt = started ? Math.min(0.05, (now - last) / 1000) : 0;
    last = now; started = true;
    const ease = (rate) => (reduced ? 1 : 1 - Math.exp(-dt * rate));
    if (!reduced) state.time += dt;

    // People who asked for less motion get one still picture per shot: the camera cuts instead of moving.
    const focus = reduced ? Math.round(aim.focus) : aim.focus;
    state.focus += (focus - state.focus) * ease(5.5);
    if (Math.abs(aim.focus - current) > 0.55) cut(Math.round(aim.focus));
    state.hold = reduced ? aim.holds.map(() => 0.5) : aim.holds;
    state.stacked = aim.stacked;
    const goal = region(state.focus), settle = placed ? ease(7) : 1;
    for (const key of ["x", "y", "w", "h"]) state.region[key] += (goal[key] - state.region[key]) * settle;
    placed = true;
    state.zoom += ((reduced ? 0.8 : 0.3 + 0.7 * smooth(aim.holds[ZOOM] * 1.15)) - state.zoom) * ease(4);

    const manual = !reduced && pointer.seen && now - pointer.at < 4500;
    auto += ((manual ? 0 : 1) - auto) * ease(3);
    const tilt = manual && !aim.stacked && !pen ? [clamp((pointer.x / innerWidth - 0.5) * 2, -1, 1), clamp((pointer.y / innerHeight - 0.5) * 2, -1, 1)] : [0, 0];
    state.parallax.x += (tilt[0] - state.parallax.x) * ease(2.2);
    state.parallax.y += (tilt[1] - state.parallax.y) * ease(2.2);
    const hit = manual ? stage.pick(pointer.x - size.left, pointer.y - size.top) : null;
    const t = state.time, aspect = Number(overlay.dataset.aspect) || 1.6, where = plan();
    // The reel is 16:9 and the tool clips are 16:10: the screen changes shape while the camera moves to shot two.
    state.aspect = aspect < 1 ? aspect : lerp(16 / 9, 1.6, smooth((state.focus - 0.3) / 0.4));

    // The back row: scrolling through the shot goes from the wiggle to the ring, unless a button chose.
    const backOn = weight(BACK_ROW);
    if (backOn < 0.03) chosen = false;
    if (!chosen) setAnswer(aim.holds[BACK_ROW] > 0.46 ? 1 : 0);
    if (reduced) drawn = answer;
    else if (answer === 1 && backOn > 0.5) drawn = Math.min(1, drawn + dt / 2.3);
    else if (answer === 0 || backOn < 0.1) drawn = Math.max(0, drawn - dt / 0.35);
    const wiggle = backOn * (1 - smooth(drawn * 6));
    const lost = { u: where.wiggle[0] + (reduced ? 0 : 0.03 * Math.sin(t * 6.4) + 0.024 * Math.sin(t * 2.2 + 1)), v: where.wiggle[1] + (reduced ? 0 : 0.034 * Math.cos(t * 5.2) + 0.02 * Math.sin(t * 1.6)) };
    crowd.style.opacity = (aim.stacked ? 0 : backOn * 0.96).toFixed(3);

    // One key: a short demonstration the first time, then the visitor's own marks.
    const keysOn = weight(KEYS);
    if (keysOn > 0.8 && !demo.done && !taken) {
      if (demo.at < 0) demo.at = t;
      const elapsed = t - demo.at;
      if (!demo.stroke) { demo.stroke = { points: [] }; strokes.push(demo.stroke); refreshUndo(); }
      if (reduced) { demo.stroke.points = demo.loop; demo.done = true; strokeVersion++; setMode("draw"); setMode("use"); }
      else {
        if (elapsed > 0.8 && demo.step === 0) { demo.step = 1; press("draw"); setMode("draw"); }
        if (elapsed > 1.5 && demo.step === 1) {
          const k = smooth((elapsed - 1.5) / 1.3);
          demo.stroke.points = demo.loop.slice(0, Math.max(2, Math.round(k * demo.loop.length)));
          strokeVersion++;
          if (k >= 1) demo.step = 2;
        }
        if (elapsed > 3.7 && demo.step === 2) { demo.done = true; press("use"); setMode("use"); }
      }
    }
    if (keysOn < 0.02 && mode === "draw") setMode("use");
    pad.classList.toggle("is-live", keysOn > 0.5);

    // Spotlight: follows the pointer, or sweeps slowly on its own.
    const spotOn = weight(SPOT), r = state.spot.r = aspect < 1 ? 0.17 : 0.25, mx = (r * 0.55) / aspect, my = r * 0.55;
    let su = reduced ? 0.6 : 0.5 + 0.3 * Math.sin(t * 0.52), sv = reduced ? 0.46 : 0.5 + 0.2 * Math.sin(t * 0.81 + 1.1);
    if (hit) { su = lerp(clamp(hit.u, mx, 1 - mx), su, auto); sv = lerp(clamp(hit.v, my, 1 - my), sv, auto); }
    state.spot.u += (su - state.spot.u) * ease(9); state.spot.v += (sv - state.spot.v) * ease(9);
    state.spot.on = spotOn;

    // Laser: the dot follows the pointer and leaves a trail that fades in under a second.
    const laserOn = weight(LASER);
    let lu = reduced ? 0.6 : 0.52 + 0.27 * Math.sin(t * 1.25), lv = reduced ? 0.5 : 0.52 + 0.16 * Math.sin(t * 2.05 + 0.4) * Math.cos(t * 0.63);
    if (hit) { lu = lerp(clamp(hit.u, 0.02, 0.98), lu, auto); lv = lerp(clamp(hit.v, 0.03, 0.97), lv, auto); }
    state.laser.u += (lu - state.laser.u) * ease(24); state.laser.v += (lv - state.laser.v) * ease(24);
    state.laser.on = laserOn;
    if (reduced) trail = laserOn > 0.5 ? Array.from({ length: 22 }, (_, i) => ({ u: 0.6 - (21 - i) * 0.012, v: 0.5 + Math.sin((21 - i) * 0.3) * 0.05 * ((21 - i) / 21), at: now - (21 - i) * 38 })) : [];
    else if (laserOn > 0.02) {
      const tip = trail[trail.length - 1];
      if (!tip || Math.hypot((tip.u - state.laser.u) * aspect, tip.v - state.laser.v) > 0.003) trail.push({ u: state.laser.u, v: state.laser.v, at: now });
      while (trail.length && now - trail[0].at > 850) trail.shift();
    } else trail.length = 0;

    state.dim += (1 - 0.4 * backOn * smooth(drawn * 3) - 0.22 * laserOn - (mode === "draw" ? 0.16 * keysOn : 0) - state.dim) * ease(6);

    const key = [
      wiggle > 0.004 ? `${lost.u.toFixed(4)},${lost.v.toFixed(4)},${wiggle.toFixed(2)}` : "",
      drawn > 0 && backOn > 0.004 ? `${drawn.toFixed(4)},${backOn.toFixed(2)}` : "",
      strokes.length && keysOn > 0.004 ? `${strokeVersion},${keysOn.toFixed(2)}` : "",
      laserOn > 0.004 ? `${state.laser.u.toFixed(4)},${state.laser.v.toFixed(4)},${trail.length && !reduced ? now | 0 : 0},${laserOn.toFixed(2)}` : "",
    ].join("|");
    if (key !== painted) {
      painted = key;
      ink.clearRect(0, 0, overlay.width, overlay.height);
      if (drawn > 0 && backOn > 0.004) paintAnswer(ink, overlay, where, drawn, backOn);
      if (wiggle > 0.004) paintPointer(ink, overlay, lost, wiggle);
      if (strokes.length && keysOn > 0.004) paintStrokes(ink, overlay, strokes, keysOn);
      if (laserOn > 0.004) paintLaser(ink, overlay, trail, state.laser, laserOn, now);
      state.overlayVersion = ++overlayVersion;
    }
    state.inkOn = key !== "|||";

    if (back) {
      mix = reduced ? 1 : Math.min(1, mix + dt / 0.5);
      if (mix >= 1) { front = back; back = null; mix = 0; }
    }
    state.a = front; state.b = back; state.mix = mix;

    // The room takes its color from the picture on the screen.
    if (now - glowAt > 320) {
      glowAt = now;
      const active = back || front, source = active.live && active.video.readyState >= 2 ? active.video : active.image.complete && active.image.naturalWidth ? active.image : null;
      if (source) {
        try {
          sampler.drawImage(source, 0, 0, 8, 5);
          const data = sampler.getImageData(0, 0, 8, 5).data, sum = [0, 0, 0];
          for (let i = 0; i < data.length; i += 4) { sum[0] += data[i]; sum[1] += data[i + 1]; sum[2] += data[i + 2]; }
          const mean = sum.map((value) => value / 40 / 255), level = (mean[0] + mean[1] + mean[2]) / 3;
          mean.forEach((value, i) => { glowTarget[i] = clamp(0.07 + lerp(value, [0.9, 0.98, 1.2][i] * level, 0.3)); });
        } catch { /* keep the last color */ }
      }
    }
    state.glow.forEach((value, i) => { state.glow[i] = value + (glowTarget[i] - value) * ease(2.5); });

    stage.render(state);
    place();
    const busy = !reduced || (playing && shots[current].freeze === undefined);
    if (visible && !document.hidden && busy) wake();
  }
  function wake() { if (ready && !raf) raf = requestAnimationFrame(frame); }

  // The play button and the clock sit under the screen's corner and move with it; the drawing pad lies on the screen.
  const pad2 = (n) => String(Math.floor(n)).padStart(2, "0");
  let clock = "";
  function place() {
    const box = stage.box(), width = hud.offsetWidth, height = hud.offsetHeight;
    const floor = aim.stacked ? Math.min(size.h - height - 14, state.region.y + state.region.h + 6) : size.h - height - 14;
    const x = clamp(box.left, 14, size.w - width - 14), y = clamp(box.bottom + 12, header.offsetHeight + 8, floor);
    hud.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px)`;
    hud.classList.add("is-placed");
    pad.style.transform = `translate(${box.left.toFixed(1)}px, ${box.top.toFixed(1)}px)`;
    pad.style.width = `${(box.right - box.left).toFixed(1)}px`; pad.style.height = `${(box.bottom - box.top).toFixed(1)}px`;
    const video = (back || front).video, held = shots[current].freeze !== undefined;
    const text = held ? "Paused on a frame" : `${pad2(video.currentTime / 60)}:${pad2(video.currentTime % 60)} / ${pad2((video.duration || 0) / 60)}:${pad2((video.duration || 0) % 60)}`;
    if (text !== clock) { clock = text; timecode.textContent = text; }
  }

  new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; syncPlayback(); wake(); }).observe(show);
  document.addEventListener("visibilitychange", () => { syncPlayback(); wake(); });
  addEventListener("scroll", scroll, { passive: true });
  new ResizeObserver(layout).observe(view);
  stacked.addEventListener("change", layout);
  document.fonts?.ready.then(layout);
  await document.fonts?.load('600 60px "Caveat"').catch(() => {});
  ready = true;
  layout();
  cut(Math.round(aim.focus), true);
  state.focus = aim.focus;
  [0, 1].forEach((n) => load(clipOf(shots[n].clip)));
  syncPlayback();
  wake();
}

// Ink, pointer and laser ---------------------------------------------------------------------------------
const trace = (context, points) => { context.beginPath(); points.forEach(([x, y], i) => (i ? context.lineTo(x, y) : context.moveTo(x, y))); };
// A glowing line: two soft halos under a bright thin core.
function glowStroke(context, points, width, alpha) {
  if (points.length < 2) return;
  context.save();
  context.lineCap = context.lineJoin = "round";
  context.strokeStyle = APRICOT; context.shadowColor = APRICOT;
  context.globalAlpha = alpha * 0.55; context.lineWidth = width * 1.5; context.shadowBlur = width * 7;
  trace(context, points); context.stroke();
  context.globalAlpha = alpha; context.lineWidth = width; context.shadowBlur = width * 2.2;
  trace(context, points); context.stroke();
  context.shadowBlur = 0; context.globalAlpha = alpha * 0.9; context.strokeStyle = CORE; context.lineWidth = width * 0.36;
  trace(context, points); context.stroke();
  context.restore();
}
const part = (progress, from, to) => smooth((progress - from) / (to - from));
// The answer to "wait, where?": a ring by hand, an arrow and a note.
function paintAnswer(context, canvas, plan, progress, alpha) {
  const W = canvas.width, H = canvas.height, width = W * 0.0068, ring = loop(plan.ring);
  glowStroke(context, ring.slice(0, Math.round(ring.length * part(progress, 0, 0.42))).map((p) => [p.u * W, p.v * H]), width, alpha);
  const [p0, p1, p2] = plan.arrow, shaft = [], reach = part(progress, 0.46, 0.78);
  for (let i = 0; i <= 40 * reach; i++) {
    const k = i / 40, m = 1 - k;
    shaft.push([(m * m * p0[0] + 2 * m * k * p1[0] + k * k * p2[0]) * W, (m * m * p0[1] + 2 * m * k * p1[1] + k * k * p2[1]) * H]);
  }
  glowStroke(context, shaft, width, alpha);
  const head = part(progress, 0.76, 0.86);
  if (head > 0) {
    const tip = [p2[0] * W, p2[1] * H], from = Math.atan2((p1[1] - p2[1]) * H, (p1[0] - p2[0]) * W), length = W * 0.032 * head;
    for (const side of [-1, 1]) glowStroke(context, [tip, [tip[0] + Math.cos(from + side * 0.5) * length, tip[1] + Math.sin(from + side * 0.5) * length]], width, alpha);
  }
  const said = part(progress, 0.84, 1);
  if (said > 0) {
    const text = "this one.", x = plan.note[0] * W, y = plan.note[1] * H, px = W * plan.size;
    context.save();
    context.font = `600 ${Math.round(px)}px "Caveat", "Bradley Hand", cursive`;
    context.beginPath(); context.rect(x - 20, y - px * 1.2, (context.measureText(text).width + 40) * said, px * 1.8); context.clip();
    context.shadowColor = APRICOT; context.shadowBlur = W * 0.02;
    context.globalAlpha = alpha; context.fillStyle = "#ffc9a8";
    context.fillText(text, x, y);
    context.shadowBlur = 0; context.fillText(text, x, y);
    context.restore();
  }
}
// The system pointer at its real size on a shared screen: small.
function paintPointer(context, canvas, at, alpha) {
  const s = (canvas.width / 1600) * 1.45, x = at.u * canvas.width, y = at.v * canvas.height;
  context.save();
  context.globalAlpha = alpha; context.translate(x, y); context.scale(s, s);
  context.beginPath();
  [[0, 0], [0, 17], [4.4, 13], [7.6, 20], [10.4, 18.8], [7.2, 12], [12.6, 12]].forEach(([px, py], i) => (i ? context.lineTo(px, py) : context.moveTo(px, py)));
  context.closePath();
  context.lineJoin = "round"; context.lineWidth = 2.2; context.strokeStyle = "#fff"; context.stroke();
  context.fillStyle = "#0d0f14"; context.fill();
  context.restore();
}
function paintStrokes(context, canvas, strokes, alpha) {
  const W = canvas.width, H = canvas.height;
  for (const stroke of strokes) glowStroke(context, stroke.points.map((p) => [p.u * W, p.v * H]), W * 0.0062, alpha);
}
function paintLaser(context, canvas, trail, dot, alpha, now) {
  const W = canvas.width, H = canvas.height, unit = W / 1600;
  context.lineCap = "round";
  for (let i = 1; i < trail.length; i++) {
    const a = trail[i - 1], b = trail[i], life = clamp(1 - (now - b.at) / 850), fade = life * life * alpha;
    for (const [scale, strength, color] of [[5.5, 0.1, "#ff1f33"], [2.4, 0.3, "#ff2a3c"], [1, 0.95, "#ff5a64"]]) {
      context.globalAlpha = fade * strength; context.strokeStyle = color; context.lineWidth = unit * (2 + 7 * life) * scale;
      context.beginPath(); context.moveTo(a.u * W, a.v * H); context.lineTo(b.u * W, b.v * H); context.stroke();
    }
  }
  const x = dot.u * W, y = dot.v * H, glow = context.createRadialGradient(x, y, 0, x, y, unit * 74);
  glow.addColorStop(0, "rgba(255, 60, 70, 0.75)"); glow.addColorStop(0.22, "rgba(255, 30, 48, 0.34)"); glow.addColorStop(1, "rgba(255, 20, 40, 0)");
  context.globalAlpha = alpha; context.fillStyle = glow;
  context.beginPath(); context.arc(x, y, unit * 74, 0, Math.PI * 2); context.fill();
  context.fillStyle = "#ff3344"; context.beginPath(); context.arc(x, y, unit * 9.5, 0, Math.PI * 2); context.fill();
  context.fillStyle = "#ffe9ea"; context.beginPath(); context.arc(x, y, unit * 4.6, 0, Math.PI * 2); context.fill();
  context.globalAlpha = 1;
}

// The same show without WebGL: the recording in a dark frame, lit with CSS.
function createFlat(overlay) {
  const root = $("#flat"), screen = $("#flat-screen"), inner = $("#flat-zoom"), dim = $("#flat-dim");
  const box = { left: 0, top: 0, right: 1, bottom: 1 };
  overlay.className = "flat-ink";
  inner.append(overlay);
  return {
    flat: true,
    resize() {},
    touch() {},
    box: () => box,
    pick: (x, y) => ({ u: (x - box.left) / (box.right - box.left), v: (y - box.top) / (box.bottom - box.top) }),
    render(state) {
      const { region, aspect } = state, far = smooth(1 - Math.abs(state.focus - BACK_ROW) * 1.7);
      const fill = (state.stacked ? 1 : 0.96) * lerp(1, 0.62, far);
      const w = Math.min(region.w, region.h * aspect) * fill, h = w / aspect;
      box.left = region.x + (region.w - w) / 2; box.top = region.y + (region.h - h) / 2; box.right = box.left + w; box.bottom = box.top + h;
      screen.style.width = `${w.toFixed(1)}px`; screen.style.height = `${h.toFixed(1)}px`;
      screen.style.transform = `translate(${box.left.toFixed(1)}px, ${box.top.toFixed(1)}px)`;
      root.style.setProperty("--glow", state.glow.map((value) => Math.round(value * 255)).join(" "));
      root.style.setProperty("--light", (state.dim * lerp(1, 0.4, state.spot.on)).toFixed(3));
      root.style.setProperty("--floor", `${box.bottom.toFixed(1)}px`);
      root.style.setProperty("--middle", `${((box.left + box.right) / 2).toFixed(1)}px`);
      for (const video of inner.querySelectorAll("video")) {
        const on = video === state.b?.video ? state.mix : video === state.a?.video ? 1 : 0;
        video.style.opacity = on; video.style.zIndex = video === state.b?.video ? 2 : 1;
      }
      const r = state.spot.r * h, shade = (0.9 * state.spot.on).toFixed(3);
      dim.style.background = `radial-gradient(circle at ${(state.spot.u * 100).toFixed(2)}% ${(state.spot.v * 100).toFixed(2)}%, rgb(3 5 9 / 0) ${(r * 0.84).toFixed(1)}px, rgb(3 5 9 / ${shade}) ${(r * 1.12).toFixed(1)}px), rgb(3 5 9 / ${(1 - state.dim).toFixed(3)})`;
      const push = smooth(1 - Math.abs(state.focus - ZOOM) * 1.7) * state.zoom;
      inner.style.transform = `scale(${(1 + push * 0.9).toFixed(4)})`;
      overlay.style.opacity = state.inkOn ? 1 : 0;
    },
  };
}
