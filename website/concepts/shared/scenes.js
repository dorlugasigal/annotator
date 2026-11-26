// Three small live scenes of the app, for the pages' "What you can do": the Break timer, Share region and the
// toolbar being made yours. Each fills the element the page gives it and draws its own small sample screen inside;
// the outer frame, radius and shadow are the page's. They use no words but the app's own, load nothing, and take
// no input.
//   import { breakTimerScene, shareRegionScene, toolbarScene } from "/concepts/shared/scenes.js";
//   const scene = breakTimerScene(container, { appearance: "dark", gradient: "sunset" });
//   await scene.play();   // runs once and resolves at the end (true, or false when it was stopped); the page may loop it
//   scene.stop();         // stops at once; play() again starts over
//   scene.reset();        // back to the first frame
//   scene.setAppearance("light");
//   scene.destroy();
// The container needs a size (about 16:10; anything from 4:3 to 2:1 works, down to 300 px wide). The page must load
// ink.css for toolbarScene. With Reduce Motion, play() shows the finished picture and resolves.
// Looks and motion are the app's: BreakScreenView.swift and reference/ux-reference/macos/break-timer (the break
// screen is always dark, in the app's palette nearest to the page's gradient), RegionSharingGuide.swift and
// SharedRegionWindow.swift (the outline and the "Annotator - Shared Region" window), and the engine's own toolbar.
import { createInk } from "./ink.js";
import { GRADIENT_PRESETS } from "./annotation-core.js";
import { css as curve, DURATIONS, SPRINGS, spring, ease, poseKeyframes } from "./app-motion.js";

const STYLE = `
.scene { position: relative; width: 100%; height: 100%; overflow: hidden; border-radius: inherit; container-type: size; font-family: inherit; -webkit-user-select: none; user-select: none; cursor: default; isolation: isolate;
  --s-desk: #101216; --s-dots: #ffffff14; --s-win: #1f2228; --s-bar: #272b33; --s-edge: #ffffff1c; --s-line: #ffffff24; --s-strong: #ffffff59; --s-text: #f2f4f8; --s-block: #ffffff2e; --s-shadow: 0 1.2cqmin 3cqmin -1.4cqmin #000000cc; --s-accent: #0a84ff; }
.scene[data-appearance="light"] { --s-desk: #ece9e2; --s-dots: #0000001a; --s-win: #ffffff; --s-bar: #f4f2ee; --s-edge: #0000001f; --s-line: #0000001f; --s-strong: #00000061; --s-text: #16171a; --s-block: #00000026; --s-shadow: 0 1.2cqmin 3cqmin -1.6cqmin #00000059; --s-accent: #007aff; }
.scene * { box-sizing: border-box; }
.scene-desk { position: absolute; inset: 0; background: radial-gradient(var(--s-dots) 1px, transparent 1.4px) 0 0 / 3.2cqmin 3.2cqmin, var(--s-desk); }
/* A scene that is a composition, not a screen, keeps 16:10 inside whatever the page gives it. */
.scene-fit { position: absolute; left: 50%; top: 50%; width: min(100cqw, 160cqh); aspect-ratio: 16 / 10; translate: -50% -50%; container-type: size; }
/* The sample windows: quiet, with made-up content and no words. */
.scene-win { position: absolute; overflow: hidden; border-radius: 1.6cqmin; background: var(--s-win); box-shadow: 0 0 0 1px var(--s-edge), var(--s-shadow); }
.scene-win-bar { display: flex; align-items: center; gap: .8cqmin; height: 3.6cqmin; padding: 0 1.4cqmin; background: var(--s-bar); border-bottom: 1px solid var(--s-edge); }
.scene-win-bar i { width: 1.1cqmin; height: 1.1cqmin; border-radius: 50%; background: var(--s-line); }
.scene-win-bar b { flex: 1; margin-right: 4.6cqmin; height: 1cqmin; max-width: 12cqmin; margin-left: auto; border-radius: 1cqmin; background: var(--s-line); }
.scene-body { position: absolute; inset: 3.6cqmin 0 0; padding: 2.2cqmin 2.4cqmin; display: flex; flex-direction: column; gap: 1.5cqmin; }
.scene-row { height: 1.3cqmin; border-radius: 1cqmin; background: var(--s-line); }
.scene-row.is-title { height: 2.2cqmin; background: var(--s-strong); }
.scene-num { font-size: 7.4cqmin; font-weight: 700; line-height: 1; color: var(--s-text); letter-spacing: -.02em; font-variant-numeric: tabular-nums; }
.scene-bars { flex: 1; min-height: 0; display: flex; align-items: flex-end; gap: 1.2cqmin; }
.scene-bars i { flex: 1; border-radius: .6cqmin .6cqmin 0 0; background: var(--s-block); }
.scene-pills { display: flex; gap: 1cqmin; }
.scene-pills i { width: 7cqmin; height: 2.6cqmin; border-radius: 2cqmin; background: var(--s-line); }
.scene-pills i:last-child { background: var(--s-block); }

/* Break timer (BreakScreenView.swift). Every size is a share of the shorter side, as in the app. */
.scene-break-face { position: absolute; inset: 0; overflow: hidden; opacity: 0; color: #fff; background: linear-gradient(var(--bk-top), var(--bk-bottom)); font-family: ui-rounded, "SF Pro Rounded", system-ui, -apple-system, "Segoe UI", sans-serif; }
.scene-break-face > i { position: absolute; width: 90cqmax; height: 90cqmax; margin: -45cqmax 0 0 -45cqmax; border-radius: 50%; }
.scene-break-face > i:nth-of-type(1) { left: 18%; top: 22%; background: radial-gradient(circle closest-side, color-mix(in srgb, var(--bk-a) 62%, transparent), color-mix(in srgb, var(--bk-a) 0%, transparent)); --dx: 20cqw; --dy: 14cqh; animation-duration: 22s, 23.2s; }
.scene-break-face > i:nth-of-type(2) { left: 82%; top: 72%; background: radial-gradient(circle closest-side, color-mix(in srgb, var(--bk-b) 62%, transparent), color-mix(in srgb, var(--bk-b) 0%, transparent)); --dx: -18cqw; --dy: -16cqh; animation-duration: 29s, 29.6s; }
.scene-break-face > i:nth-of-type(3) { left: 55%; top: 50%; background: radial-gradient(circle closest-side, color-mix(in srgb, var(--bk-c) 62%, transparent), color-mix(in srgb, var(--bk-c) 0%, transparent)); --dx: -12cqw; --dy: 10cqh; animation-duration: 37s, 17.6s; }
.scene-break-face.is-moving > i { animation-name: scene-drift, scene-swell; animation-timing-function: ease-in-out; animation-direction: alternate; animation-iteration-count: infinite; }
.scene-break-face::after { content: ""; position: absolute; inset: 0; background: radial-gradient(ellipse 65% 65% at center, rgb(0 0 0 / 0) 45%, rgb(0 0 0 / .42) 100%); }
@keyframes scene-drift { to { translate: var(--dx) var(--dy); } }
@keyframes scene-swell { to { scale: 1.16; } }
.scene-break-clock { position: absolute; inset: 0; z-index: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4cqmin; }
.scene-break-title { font-size: 5.2cqmin; font-weight: 600; line-height: 1.2; color: rgb(255 255 255 / .88); white-space: nowrap; }
.scene-break-ring { position: relative; width: 60cqmin; height: 60cqmin; display: grid; place-items: center; }
.scene-break-ring svg { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; rotate: -90deg; }
.scene-break-ring circle { fill: none; stroke-linecap: round; }
.scene-break-count { position: relative; display: flex; flex-direction: column; align-items: center; gap: 1.4cqmin; }
.scene-break-count b { font-size: 17cqmin; font-weight: 600; line-height: 1.19; font-variant-numeric: tabular-nums; }
.scene-break-count span { font-size: 3.2cqmin; font-weight: 500; line-height: 1.2; color: rgb(255 255 255 / .7); white-space: nowrap; }

/* Share region (RegionSharingGuide.swift, SharedRegionWindow.swift). */
.scene-share-screen { position: absolute; left: 4cqw; top: 6cqh; width: 92cqw; height: 58cqh; overflow: hidden; border-radius: 1.6cqmin; box-shadow: 0 0 0 1px var(--s-edge), var(--s-shadow); container-type: size; }
.scene-share-region { position: absolute; inset: 0 0 0 50%; container-type: size; }
.scene-share-outline { position: absolute; inset: 0 0 0 50%; border: max(1.5px, .45cqmin) solid color-mix(in srgb, var(--s-accent) 95%, transparent); background: color-mix(in srgb, var(--s-accent) 8%, transparent); opacity: 0; }
.scene-share-outline.is-live { background: none; }
.scene-share-window { position: absolute; left: 6cqw; top: 40cqh; width: 37cqw; border-radius: 1.5cqmin; overflow: hidden; background: #000; box-shadow: 0 0 0 1px var(--s-edge), 0 2.4cqmin 5cqmin -1.6cqmin #000000d9; opacity: 0; }
.scene-share-title { display: flex; align-items: center; gap: .9cqmin; height: 4.2cqmin; padding: 0 1.5cqmin; background: var(--s-bar); border-bottom: 1px solid var(--s-edge); color: var(--s-text); font: 600 2.3cqmin/1 -apple-system, "SF Pro Text", "Segoe UI", system-ui, sans-serif; white-space: nowrap; }
.scene-share-title i { flex: none; width: 1.3cqmin; height: 1.3cqmin; border-radius: 50%; background: var(--s-line); }
.scene-share-title span { flex: 1; margin-right: 5.7cqmin; text-align: center; overflow: hidden; text-overflow: ellipsis; opacity: .82; }
.scene-share-view { position: relative; aspect-ratio: var(--region); overflow: hidden; container-type: size; }
.scene-share-ink { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; }
.scene-share-ink path { fill: none; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }

/* Make it yours: the engine's own toolbar, drawn smaller. */
.scene-bar-screen { position: absolute; left: 14cqw; top: 7cqh; width: 72cqw; height: 46cqh; }
.scene-bar-holder { position: absolute; left: 50%; bottom: 7cqh; width: 1100px; height: 150px; transform-origin: 50% 100%; translate: -50% 0; }
.scene-bar-holder > .ink-toolbar { bottom: 0; }
@media (prefers-reduced-motion: reduce) { .scene-break-face.is-moving > i { animation: none; } }
`;

const el = (className, html = "") => Object.assign(document.createElement("div"), { className, innerHTML: html });
const win = (style, body) => `<div class="scene-win" style="${style}"><div class="scene-win-bar"><i></i><i></i><i></i><b></b></div><div class="scene-body">${body}</div></div>`;
const rows = (...widths) => widths.map((w) => `<div class="scene-row${w < 0 ? " is-title" : ""}" style="width:${Math.abs(w)}%"></div>`).join("");
const bars = (...heights) => `<div class="scene-bars">${heights.map((h) => `<i style="height:${h}%"></i>`).join("")}</div>`;
const reduced = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const gradientOf = (name) => (GRADIENT_PRESETS.find((choice) => choice.id === String(name || "").toLowerCase()) || GRADIENT_PRESETS[0]);
let serial = 0;
// A ring as a hand draws it: a little more than once around, and not quite round.
const loop = (cx, cy, rx, ry) => {
  const points = [];
  for (let i = 0; i <= 56; i++) {
    const angle = ((-205 + (i / 56) * 392) * Math.PI) / 180, wobble = 1 + 0.06 * Math.sin(angle * 2 + 0.8) + 0.03 * (i / 56);
    points.push(`${(cx + Math.cos(angle) * rx * wobble).toFixed(2)} ${(cy + Math.sin(angle) * ry * wobble).toFixed(2)}`);
  }
  return `M${points.join("L")}`;
};

// What every scene shares: its root, one run at a time, and the calls the page makes.
function scene(container, name, options, make) {
  if (!(container instanceof Element)) throw new Error("A scene needs the element to fill.");
  if (!document.querySelector("style[data-annotator-scenes]")) {
    const style = document.createElement("style");
    style.dataset.annotatorScenes = "";
    style.textContent = STYLE;
    document.head.prepend(style);
  }
  const root = el(`scene scene-${name}`);
  root.setAttribute("aria-hidden", "true");
  root.inert = true;
  root.dataset.appearance = options.appearance === "light" ? "light" : "dark";
  container.append(root);
  let run = 0, alive = true;
  const timers = new Set(), animations = new Set(), waits = new Set();
  // Whatever is waiting on a timer or an animation is let go with "false": the run it belonged to is over.
  const release = () => { timers.forEach(clearTimeout); timers.clear(); waits.forEach((done) => done(false)); waits.clear(); };
  const clear = () => {
    run++;
    release();
    animations.forEach((a) => a.cancel()); animations.clear();
  };
  const stage = {
    root,
    live: (id) => alive && id === run,
    // A wait that ends false when the scene was stopped or started over meanwhile.
    wait(ms, id) { return new Promise((done) => { waits.add(done); const t = setTimeout(() => { timers.delete(t); waits.delete(done); done(stage.live(id)); }, ms); timers.add(t); }); },
    // One of the app's animations on an element. When it ends its last keyframe stays, as plain styles.
    async move(node, keyframes, timing, id) {
      const a = node.animate(keyframes, { fill: "both", ...timing });
      animations.add(a);
      const ended = await Promise.race([a.finished.then(() => true, () => false), new Promise((done) => waits.add(done))]);
      if (!ended || !stage.live(id)) return false;
      const last = Array.isArray(keyframes) ? keyframes[keyframes.length - 1] : null;
      if (last) for (const key in last) if (!["offset", "easing", "composite"].includes(key)) node.style[key] = last[key];
      animations.delete(a);
      a.cancel();
      return true;
    },
    frames(step, ms, id) {
      return new Promise((done) => {
        const start = performance.now();
        const tick = (now) => {
          if (!stage.live(id)) { done(false); return; }
          const t = Math.min(1, (now - start) / ms);
          step(t);
          if (t < 1) requestAnimationFrame(tick); else done(true);
        };
        requestAnimationFrame(tick);
      });
    },
  };
  const parts = make(stage);
  parts.first();
  return {
    async play() {
      if (!alive) return false;
      clear();
      const id = run;
      if (reduced()) { await parts.last(); return true; }
      try { return Boolean(await parts.play(id)); } catch (error) { if (stage.live(id)) throw error; return false; }
    },
    stop() { run++; animations.forEach((a) => a.pause()); release(); parts.stop?.(); },
    reset() { clear(); parts.stop?.(); parts.first(); },
    setAppearance(appearance) { root.dataset.appearance = appearance === "light" ? "light" : "dark"; parts.appearance?.(root.dataset.appearance); },
    destroy() { if (!alive) return; clear(); alive = false; parts.destroy?.(); root.remove(); },
  };
}

// Break timer -----------------------------------------------------------------------------------------
// BreakPaletteColors: top, bottom, the three moving fields and the ring's accent.
const PALETTES = {
  forest: ["#16241C", "#0A110D", ["#2F6B4F", "#7FAF73", "#1F4D5C"], "#9BE3B4"],
  ocean: ["#0D1D33", "#06101D", ["#1F5FA8", "#3FB6C9", "#4B3F9E"], "#8FD3FF"],
  sunset: ["#2C1521", "#170A12", ["#D9604A", "#F0A458", "#8E3B7A"], "#FFC995"],
  berry: ["#24132F", "#12091B", ["#7D3FC4", "#C9558F", "#3D4FC0"], "#F0B0FF"],
  graphite: ["#1C1E21", "#0E0F11", ["#3C4450", "#5D6B7A", "#2B3138"], "#E8ECEF"],
};
// The app's palette nearest to a gradient: by hue, weighted by how strong each color is. Graphite is for grays.
export function breakPaletteFor(gradient) {
  const tone = (hex) => {
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255), max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    const hue = !d ? 0 : max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4, strength = d ? d / (1 - Math.abs(max + min - 1)) : 0;
    return [Math.cos((hue * Math.PI) / 3) * strength, Math.sin((hue * Math.PI) / 3) * strength];
  };
  const middle = (colors) => colors.map(tone).reduce((sum, p) => [sum[0] + p[0] / colors.length, sum[1] + p[1] / colors.length], [0, 0]);
  const colors = gradientOf(gradient).gradient.colors, at = middle(colors), strength = colors.map(tone).reduce((sum, p) => sum + Math.hypot(p[0], p[1]) / colors.length, 0);
  if (strength < 0.4) return "graphite";
  return ["forest", "ocean", "sunset", "berry"].map((name) => [name, Math.hypot(...middle(PALETTES[name][2]).map((v, i) => v - at[i]))]).sort((a, b) => a[1] - b[1])[0][0];
}
// BreakCountdown.clock
const clock = (seconds) => { const value = Math.max(Math.ceil(seconds), 0), minutes = Math.floor(value / 60), rest = value % 60; return `${minutes}:${String(rest).padStart(2, "0")}`; };

export function breakTimerScene(container, options = {}) {
  return scene(container, "break", options, (stage) => {
    const TOTAL = 600, LEFT = 462, palette = PALETTES[breakPaletteFor(options.gradient)];
    // The ring: a line of 1.6% of the short side on a circle of 60%, inset by 1.4 line widths (BreakRingLayerView.layout).
    const line = (1.6 / 60) * 100, radius = 50 - line * 1.4, circle = (extra) => `<circle cx="50" cy="50" r="${radius}" pathLength="1" ${extra}/>`;
    stage.root.innerHTML = `<div class="scene-desk">${win("left:7cqw;top:10cqh;width:50cqw;height:66cqh", `${rows(-46, 88, 74, 81, 52)}${bars(38, 52, 47, 70, 61, 84, 76)}`)}${win("left:61cqw;top:20cqh;width:32cqw;height:54cqh", `<div class="scene-num">318</div>${rows(64, 80, 42)}<div class="scene-pills"><i></i><i></i></div>`)}</div>
      <div class="scene-break-face" style="--bk-top:${palette[0]};--bk-bottom:${palette[1]};--bk-a:${palette[2][0]};--bk-b:${palette[2][1]};--bk-c:${palette[2][2]}"><i></i><i></i><i></i>
        <div class="scene-break-clock"><div class="scene-break-title">Coffee break</div>
          <div class="scene-break-ring"><svg viewBox="0 0 100 100">${circle(`stroke="rgb(255 255 255 / .1)" stroke-width="${line}"`)}${circle(`data-glow stroke="${palette[3]}" stroke-opacity=".22" stroke-width="${line * 2.6}"`)}${circle(`data-ring stroke="${palette[3]}" stroke-width="${line}"`)}</svg>
            <div class="scene-break-count"><b></b><span></span></div></div></div></div>`;
    const face = stage.root.querySelector(".scene-break-face"), group = stage.root.querySelector(".scene-break-clock"), label = face.querySelector("b"), back = face.querySelector("span");
    const arcs = [...face.querySelectorAll("[data-glow], [data-ring]")];
    const show = (left, fraction = left / TOTAL) => {
      label.textContent = clock(left);
      for (const arc of arcs) { arc.style.strokeDasharray = `${Math.max(fraction, 0.0001).toFixed(4)} 2`; arc.style.visibility = fraction > 0.0005 ? "" : "hidden"; }
    };
    // BreakTimerController.endTimeText: "Back at" and the time the break ends, in the reader's own clock format.
    const endTime = () => { back.textContent = `Back at ${new Date(Date.now() + TOTAL * 1000).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`; };
    const still = (shown, left) => { face.style.opacity = shown ? 1 : 0; group.style.opacity = 1; group.style.scale = ""; group.style.translate = ""; face.classList.toggle("is-moving", shown && !reduced()); endTime(); show(left); };
    return {
      first: () => still(false, TOTAL),
      last: () => still(true, LEFT),
      stop: () => face.classList.remove("is-moving"),
      async play(id) {
        // A page that loops the scene sees the break end as the app ends it, then the sample screen again.
        if (Number(face.style.opacity) > 0) {
          stage.move(group, [{ scale: 1, opacity: 1 }, { scale: 0.96, opacity: 0 }], { duration: 280, easing: curve.easeIn }, id);
          if (!(await stage.move(face, [{ opacity: 1 }, { opacity: 0 }], { duration: 320, easing: curve.exit }, id))) return false;
        }
        still(false, TOTAL);
        show(TOTAL, 0);
        if (!(await stage.wait(900, id))) return false;
        // BreakTimerController.present: the window fades in over 0.45 s; the clock settles with the app's smooth spring.
        face.classList.add("is-moving");
        const settle = Array.from({ length: 31 }, (_, i) => { const k = spring(SPRINGS.smooth, (i / 30) * 0.6); return { offset: i / 30, scale: 0.94 + 0.06 * k, translate: `0 ${(2.67 * (1 - k)).toFixed(3)}cqmin`, opacity: Math.min(1, Math.max(0, k)) }; });
        stage.move(face, [{ opacity: 0 }, { opacity: 1 }], { duration: 450, easing: curve.fade }, id);
        stage.move(group, settle, { duration: 600 }, id);
        // The ring fills in 0.6 s on the enter curve (BreakRingLayerView.restart), then drains with the time.
        if (!(await stage.frames((t) => show(TOTAL, ease.enter(t)), 600, id))) return false;
        if (!(await stage.wait(500, id))) return false;
        // A few quick seconds stand for the first minutes of the break; then the clock runs as it does.
        if (!(await stage.frames((t) => show(TOTAL - (TOTAL - LEFT - 3) * ease.easeInEaseOut(t)), 2600, id))) return false;
        if (!(await stage.frames((t) => show(LEFT + 3 - 3 * t), 3000, id))) return false;
        return stage.wait(600, id);
      },
    };
  });
}

// Share region ------------------------------------------------------------------------------------------
export function shareRegionScene(container, options = {}) {
  return scene(container, "share", options, (stage) => {
    const id = `scene-ink-${++serial}`, tones = gradientOf(options.gradient).gradient;
    // What is inside the region, drawn twice from the same markup: on the screen and in the shared window.
    const inside = `<div class="scene-desk"></div>${win("left:10cqw;top:12cqh;width:80cqw;height:76cqh", `<div class="scene-num">318</div>${rows(52)}${bars(46, 58, 52, 74, 40, 30, 24)}`)}`;
    const ink = (suffix) => `<svg class="scene-share-ink" viewBox="0 0 120 100" preserveAspectRatio="none"><defs><linearGradient id="${id}${suffix}" gradientUnits="userSpaceOnUse" x1="8" y1="30" x2="34" y2="14">${tones.colors.map((color, i) => `<stop offset="${i / (tones.colors.length - 1)}" stop-color="${color}"/>`).join("")}</linearGradient></defs>
      <path pathLength="1" stroke="url(#${id}${suffix})" d="${loop(21, 21.6, 12.6, 8.6)}"/></svg>`;
    stage.root.innerHTML = `<div class="scene-fit">
      <div class="scene-share-screen"><div class="scene-desk"></div>${win("left:4cqw;top:14cqh;width:40cqw;height:72cqh", `${rows(-40, 86, 70, 78, 60, 82, 48)}<div class="scene-pills"><i></i><i></i></div>`)}
        <div class="scene-share-region">${inside}${ink("a")}</div><div class="scene-share-outline"></div></div>
      <div class="scene-share-window" style="--region: 1"><div class="scene-share-title"><i></i><i></i><i></i><span>Annotator - Shared Region</span></div><div class="scene-share-view">${inside}${ink("b")}</div></div></div>`;
    const screen = stage.root.querySelector(".scene-share-screen"), outline = stage.root.querySelector(".scene-share-outline"), shared = stage.root.querySelector(".scene-share-window"), paths = [...stage.root.querySelectorAll(".scene-share-ink path")];
    // The window shows the region at the region's own shape.
    const shape = () => { const r = screen.getBoundingClientRect(); if (r.width && r.height) shared.style.setProperty("--region", `${(r.width / 2).toFixed(2)} / ${r.height.toFixed(2)}`); };
    const sized = window.ResizeObserver ? new ResizeObserver(shape) : null;
    sized?.observe(screen);
    shape();
    const draw = (share) => paths.forEach((path) => { path.style.strokeDasharray = `${share.toFixed(4)} 2`; path.style.visibility = share > 0.001 ? "" : "hidden"; });
    const still = (done) => {
      outline.style.opacity = done ? 1 : 0; outline.classList.toggle("is-live", done);
      shared.style.opacity = done ? 1 : 0; shared.style.scale = "";
      draw(done ? 1 : 0);
    };
    return {
      first: () => still(false),
      last: () => still(true),
      destroy: () => sized?.disconnect(),
      async play(run) {
        still(false);
        if (!(await stage.wait(700, run))) return false;
        // Choosing: the app's outline with its faint fill, over the right half of the wide screen.
        if (!(await stage.move(outline, [{ opacity: 0 }, { opacity: 1 }], { duration: DURATIONS.fadeIn * 1000, easing: curve.easeOut }, run))) return false;
        if (!(await stage.wait(900, run))) return false;
        // Sharing starts: the fill goes, and the region is a window of its own (it arrives with the app's Pop).
        outline.classList.add("is-live");
        const pop = poseKeyframes("pop", { appearing: true, annotation: false });
        if (!(await stage.move(shared, pop.map(({ offset, scale, opacity }) => ({ offset, scale, opacity })), { duration: pop.duration, easing: pop.easing }, run))) return false;
        if (!(await stage.wait(600, run))) return false;
        // What is drawn in the region is in the window too.
        if (!(await stage.frames((t) => draw(ease.easeInEaseOut(t)), 1100, run))) return false;
        return stage.wait(1400, run);
      },
    };
  });
}

// Make it yours -----------------------------------------------------------------------------------------
// Settings > Toolbar on the engine's own toolbar: a tool goes, another comes, the color becomes another of the
// app's gradients, and the bar shrinks to its one button and opens again (the app's Shrink).
export function toolbarScene(container, options = {}) {
  return scene(container, "bar", options, (stage) => {
    const START = ["select", "rect", "diamond", "ellipse", "arrow", "line", "pen", "text", "highlight", "laser", "eraser", "effects"];
    const WITHOUT = START.filter((item) => item !== "line"), YOURS = [...WITHOUT, "counter"];
    // The second color is one of the app's gradients that is clearly another: Ocean after a warm one, else Sunset.
    const first = gradientOf(options.gradient).id, other = breakPaletteFor(first) === "sunset" ? "ocean" : "sunset";
    stage.root.innerHTML = `<div class="scene-fit"><div class="scene-bar-screen"><div class="scene-desk" style="border-radius:1.6cqmin;box-shadow:0 0 0 1px var(--s-edge), var(--s-shadow)"></div>${win("left:8%;top:14%;width:52%;height:72%", `${rows(-44, 84, 70)}${bars(40, 56, 48, 72, 62, 80)}`)}${win("left:64%;top:26%;width:28%;height:52%", `<div class="scene-num" data-number>318</div>${rows(70, 46)}`)}</div><div class="scene-bar-holder"></div></div>`;
    const fit = stage.root.querySelector(".scene-fit"), screen = stage.root.querySelector(".scene-bar-screen"), holder = stage.root.querySelector(".scene-bar-holder"), number = stage.root.querySelector("[data-number]");
    const ink = createInk({ surface: screen, interactive: false, showToolbar: true, toolbarParent: holder, toolbarPosition: "absolute", preset: "factory", toolbarItems: START, tool: "pen", properties: true, gradient: first, appearance: stage.root.dataset.appearance, glass: false });
    // The bar is laid out at its real size and shown as wide as the scene: its pixels are the app's points.
    const scale = () => { const w = fit.clientWidth, bar = ink.toolbar.offsetWidth || 969; if (w) holder.style.scale = Math.min(1, (w * 0.94) / Math.max(bar, 969)).toFixed(4); };
    const sized = window.ResizeObserver ? new ResizeObserver(scale) : null;
    sized?.observe(fit);
    scale();
    const size = () => Math.max(2, fit.clientWidth / 150);
    const underline = () => ink.perform([{ tool: "underline", under: number, size: size(), dy: 2 }], { cursor: false });
    const ring = (duration) => ink.perform([{ tool: "ellipse", around: number, pad: Math.max(4, fit.clientWidth / 60), size: size(), duration }], { cursor: false });
    const wipe = () => { for (const mark of [...ink.marks()]) ink.remove(mark.id); };
    const still = async (done) => {
      await ink.setCollapsed(false, { animate: false });
      ink.setToolbarItems(done ? YOURS : START);
      ink.setGradient(done ? other : first);
      wipe();
      if (done) { ink.setGradient(first); await underline(); ink.setGradient(other); await ring(0); } else await underline();
      scale();
    };
    const button = (item) => ink.toolbar.querySelector(`[data-tool="${item}"]`);
    // The bar's width follows the app's toolbar resize: 0.22 s on the standard curve.
    const resize = async (items, run) => {
      const from = ink.toolbar.offsetWidth;
      ink.setToolbarItems(items);
      const to = ink.toolbar.offsetWidth;
      ink.toolbar.style.overflow = "hidden";
      const ok = await stage.move(ink.toolbar, [{ width: `${from}px` }, { width: `${to}px` }], { duration: DURATIONS.toolbarResize * 1000, easing: curve.standard }, run);
      ink.toolbar.style.overflow = ""; ink.toolbar.style.width = "";
      return ok;
    };
    return {
      first: () => still(false),
      last: () => still(true),
      appearance: (appearance) => ink.setAppearance(appearance),
      destroy: () => { sized?.disconnect(); ink.toolbar.remove(); },
      async play(run) {
        await still(false);
        if (!(await stage.wait(900, run))) return false;
        // Settings > Toolbar: Line is turned off, and its room goes.
        if (!(await stage.move(button("line"), [{ opacity: 1 }, { opacity: 0 }], { duration: DURATIONS.fadeOut * 1000, easing: curve.easeIn }, run))) return false;
        if (!(await resize(WITHOUT, run))) return false;
        if (!(await stage.wait(450, run))) return false;
        // Numbered counter is added to the bar.
        const growing = resize(YOURS, run);
        stage.move(button("counter"), [{ opacity: 0 }, { opacity: 1 }], { duration: DURATIONS.fadeIn * 1000, delay: 80, easing: curve.easeOut }, run);
        if (!(await growing)) return false;
        if (!(await stage.wait(700, run))) return false;
        // Another of the app's gradients becomes the color, and the next mark is drawn with it.
        ink.setGradient(other);
        if (!(await stage.wait(250, run))) return false;
        await ring(800);
        if (!stage.live(run) || !(await stage.wait(700, run))) return false;
        // The app's Shrink: the bar becomes its one button, and opens again.
        await ink.shrink();
        if (!stage.live(run) || !(await stage.wait(900, run))) return false;
        await ink.expand();
        if (!stage.live(run)) return false;
        return stage.wait(900, run);
      },
    };
  });
}
