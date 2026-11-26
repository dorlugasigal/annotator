// Small live scenes of the app, for the pages' "What you can do": Zoom in, Shapes and icons, Share just one part and
// Take a break (and the toolbar being made yours, which no page uses now). Each fills the element the page gives it
// and draws its own small sample screen inside; the outer frame, radius and shadow are the page's. They use no words
// but the app's own, load nothing, and take no input.
//   import { zoomScene, shapesScene, shareRegionScene, breakTimerScene } from "./scenes.js";
//   const scene = zoomScene(container, { appearance: "dark", gradient: "dusk" });
//   await scene.play();   // runs once and resolves at the end (true, or false when it was stopped); the page may loop it
//   scene.stop();         // stops at once and holds the picture; play() again starts over
//   scene.reset();        // back to the opening picture
//   scene.setAppearance("light");
//   scene.setGradient("ocean");   // another of the app's fifteen gradients, where the scene is: nothing starts over
//   scene.destroy();
// The container needs a size (about 16:10; anything from 4:3 to 2:1 works, down to 300 px wide). The page must load
// ink.css for shapesScene and toolbarScene. With Reduce Motion, play() shows the finished picture and resolves.
// The four are short films of 8 seconds or so, each drawn from one clock: every picture is a function of the time
// since the piece began, so stop, reset and play are exact. A piece opens on its sample screen, and a round that
// follows another starts with a short fade. A camera opens a little close and settles, goes in on what the piece
// is about, drifts while it holds and comes back; it moves the planes (desk, windows, the app's interface) a little
// against each other. Only transforms and opacity change, but for the strokes of ink and of the break ring.
// What the app shows is the app's: BreakScreenView.swift and reference/ux-reference/macos/break-timer (the break
// screen is always dark, in the app's palette nearest to the page's gradient; its digits roll as the app's do),
// RegionSharingGuide.swift and SharedRegionWindow.swift, ZoomFollow.swift and LivePresentation.swift (the zoom and
// its map), ToolLibrary.swift and reference/ux-reference/macos/tools-window (the tools window), and the engine's own
// marks and toolbar.
import { createInk } from "./ink.js";
import { GRADIENT_PRESETS, Viewport, ZoomFollow, ZoomMap, canonicalShape, pathData } from "./annotation-core.js";
import { css as curve, DURATIONS, SPRINGS, spring, ease, pose, poseProgress, visibilityDuration, pulseScale } from "./app-motion.js";
import { APP_ICONS } from "./app-icons.js";

const STYLE = `
.scene { position: relative; width: 100%; height: 100%; overflow: hidden; border-radius: inherit; container-type: size; font-family: inherit; -webkit-user-select: none; user-select: none; cursor: default; isolation: isolate;
  --s-desk: #101216; --s-dots: #ffffff14; --s-win: #1f2228; --s-bar: #272b33; --s-edge: #ffffff1c; --s-line: #ffffff24; --s-strong: #ffffff59; --s-text: #f2f4f8; --s-block: #ffffff2e; --s-shadow: 0 1.2cqmin 3cqmin -1.4cqmin #000000cc; --s-lift: 0 3.4cqmin 7cqmin -2cqmin #000000e6; --s-accent: #0a84ff;
  --scene-light: linear-gradient(90deg, transparent, color-mix(in srgb, var(--g1) 42%, transparent) 30%, color-mix(in srgb, var(--g2) 58%, transparent) 50%, color-mix(in srgb, var(--g3) 42%, transparent) 70%, transparent); }
.scene[data-appearance="light"] { --s-desk: #ece9e2; --s-dots: #0000001a; --s-win: #ffffff; --s-bar: #f4f2ee; --s-edge: #0000001f; --s-line: #0000001f; --s-strong: #00000061; --s-text: #16171a; --s-block: #00000026; --s-shadow: 0 1.2cqmin 3cqmin -1.6cqmin #00000059; --s-lift: 0 3.4cqmin 7cqmin -2.2cqmin #00000080; --s-accent: #007aff; }
.scene * { box-sizing: border-box; }
.scene-desk { position: absolute; inset: 0; background: radial-gradient(var(--s-dots) 1px, transparent 1.4px) 0 0 / 3.2cqmin 3.2cqmin, var(--s-desk); }
/* A scene that is a composition, not a screen, keeps 16:10 inside whatever the page gives it. */
.scene-fit { position: absolute; left: 50%; top: 50%; width: min(100cqw, 160cqh); aspect-ratio: 16 / 10; translate: -50% -50%; container-type: size; }
/* The planes the camera moves, and what a new round fades. The far plane reaches past the frame, so a camera move
   never shows its edge. */
.scene-plane, .scene-act { position: absolute; inset: 0; }
.scene-plane { will-change: transform; }
.scene-far { inset: -9%; }
/* The sample windows: quiet, with made-up content and no words. */
.scene-win { position: absolute; overflow: hidden; border-radius: 1.6cqmin; background: var(--s-win); box-shadow: 0 0 0 1px var(--s-edge), var(--s-shadow); }
.scene-win-bar { display: flex; align-items: center; gap: .8cqmin; height: 3.6cqmin; padding: 0 1.4cqmin; background: var(--s-bar); border-bottom: 1px solid var(--s-edge); }
.scene-win-bar i { width: 1.1cqmin; height: 1.1cqmin; border-radius: 50%; background: var(--s-line); }
.scene-win-bar b { flex: 1; margin-right: 4.6cqmin; height: 1cqmin; max-width: 12cqmin; margin-left: auto; border-radius: 1cqmin; background: var(--s-line); }
.scene-body { position: absolute; inset: 3.6cqmin 0 0; padding: 2.2cqmin 2.4cqmin; display: flex; flex-direction: column; gap: 1.5cqmin; }
.scene-row { height: 1.3cqmin; border-radius: 1cqmin; background: var(--s-line); transform-origin: 0 50%; }
.scene-row.is-title { height: 2.2cqmin; background: var(--s-strong); }
.scene-num { font-size: 7.4cqmin; font-weight: 700; line-height: 1; color: var(--s-text); letter-spacing: -.02em; font-variant-numeric: tabular-nums; }
.scene-bars { flex: 1; min-height: 0; display: flex; align-items: flex-end; gap: 1.2cqmin; }
.scene-bars i { flex: 1; border-radius: .6cqmin .6cqmin 0 0; background: var(--s-block); transform-origin: 50% 100%; }
.scene-pills { display: flex; gap: 1cqmin; }
.scene-pills i { width: 7cqmin; height: 2.6cqmin; border-radius: 2cqmin; background: var(--s-line); }
.scene-pills i:last-child { background: var(--s-block); }
/* The scripted pointer and the app's click ring (36 points across, 3 points thick). */
.scene-pointer { position: absolute; left: 0; top: 0; width: 5.2cqmin; margin: -.36cqmin 0 0 -.48cqmin; opacity: 0; transform-origin: 9% 6%; filter: drop-shadow(0 .5cqmin .8cqmin #0007); will-change: transform, opacity; }
.scene-pointer svg { display: block; width: 100%; height: auto; }
.scene-click { position: absolute; left: 0; top: 0; width: 36px; height: 36px; margin: -18px 0 0 -18px; border: 3px solid rgb(255 209.1 0); border-radius: 50%; opacity: 0; will-change: transform, opacity; }
/* One pass of light, in the page's gradient, over what the piece is about. */
.scene-sweep { position: absolute; inset: 0; overflow: hidden; border-radius: inherit; }
.scene-sweep i { position: absolute; top: -10%; bottom: -10%; left: 0; width: 46%; background: var(--scene-light); opacity: 0; transform: skewX(-16deg); will-change: translate, opacity; }
.scene-glow { position: absolute; border-radius: 50%; background: radial-gradient(closest-side, color-mix(in srgb, var(--g2) 60%, transparent), color-mix(in srgb, var(--g1) 26%, transparent) 55%, transparent); opacity: 0; will-change: transform, opacity; }
/* While the camera is close, what is around the piece's subject falls back toward the desk's own tone. */
.scene-veil { position: absolute; inset: -12%; background: radial-gradient(ellipse var(--veil-w, 30%) var(--veil-h, 40%) at var(--veil-x, 50%) var(--veil-y, 50%), transparent 55%, color-mix(in srgb, var(--s-desk) 58%, transparent) 100%); opacity: 0; will-change: transform, opacity; }

/* Break timer (BreakScreenView.swift). Every size is a share of the shorter side, as in the app. */
.scene-soft { filter: blur(1.1cqmin); opacity: 0; will-change: transform, opacity; }
.scene-dim { position: absolute; inset: 0; background: #000; opacity: 0; will-change: opacity; }
.scene-break-face { position: absolute; inset: 0; overflow: hidden; opacity: 0; color: #fff; background: linear-gradient(var(--bk-top), var(--bk-bottom)); font-family: ui-rounded, "SF Pro Rounded", system-ui, -apple-system, "Segoe UI", sans-serif; will-change: opacity; }
.scene-break-fields > i { position: absolute; width: 90cqmax; height: 90cqmax; margin: -45cqmax 0 0 -45cqmax; border-radius: 50%; will-change: transform; }
.scene-break-fields > i:nth-of-type(1) { left: 18%; top: 22%; background: radial-gradient(circle closest-side, color-mix(in srgb, var(--bk-a) 62%, transparent), color-mix(in srgb, var(--bk-a) 0%, transparent)); }
.scene-break-fields > i:nth-of-type(2) { left: 82%; top: 72%; background: radial-gradient(circle closest-side, color-mix(in srgb, var(--bk-b) 62%, transparent), color-mix(in srgb, var(--bk-b) 0%, transparent)); }
.scene-break-fields > i:nth-of-type(3) { left: 55%; top: 50%; background: radial-gradient(circle closest-side, color-mix(in srgb, var(--bk-c) 62%, transparent), color-mix(in srgb, var(--bk-c) 0%, transparent)); }
.scene-break-shade { position: absolute; inset: 0; background: radial-gradient(ellipse 65% 65% at center, rgb(0 0 0 / 0) 45%, rgb(0 0 0 / .42) 100%); }
.scene-break-glow { left: 50%; top: 50%; width: 96cqmin; height: 96cqmin; margin: -48cqmin 0 0 -48cqmin; background: radial-gradient(closest-side, color-mix(in srgb, var(--bk-accent) 34%, transparent), color-mix(in srgb, var(--bk-accent) 10%, transparent) 60%, transparent); }
.scene-break-clock { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4cqmin; will-change: transform, opacity; }
.scene-break-title { font-size: 5.2cqmin; font-weight: 600; line-height: 1.2; color: rgb(255 255 255 / .88); white-space: nowrap; }
.scene-break-ring { position: relative; width: 60cqmin; height: 60cqmin; display: grid; place-items: center; }
.scene-break-ring svg { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; rotate: -90deg; }
.scene-break-ring circle { fill: none; stroke-linecap: round; }
.scene-break-count { position: relative; display: flex; flex-direction: column; align-items: center; gap: 1.4cqmin; }
.scene-break-count b { display: flex; font-size: 17cqmin; font-weight: 600; line-height: 1.19; font-variant-numeric: tabular-nums; will-change: transform; }
.scene-break-count b span { position: relative; display: block; white-space: pre; }
.scene-break-count b i { display: block; font-style: normal; will-change: transform, opacity; }
.scene-break-count b i + i { position: absolute; right: 0; top: 0; }
.scene-break-count > span { font-size: 3.2cqmin; font-weight: 500; line-height: 1.2; color: rgb(255 255 255 / .7); white-space: nowrap; opacity: 0; will-change: opacity; }

/* Share region (RegionSharingGuide.swift, SharedRegionWindow.swift). */
.scene-share-screen { position: absolute; left: 4cqw; top: 6cqh; width: 92cqw; height: 58cqh; overflow: hidden; border-radius: 1.6cqmin; box-shadow: 0 0 0 1px var(--s-edge), var(--s-shadow); container-type: size; }
.scene-share-region { position: absolute; inset: 0 0 0 50%; container-type: size; }
/* The chooser's outline, 2 points wide, drawn side by side; its faint fill is there while a region is being chosen. */
.scene-share-outline { position: absolute; inset: 0 0 0 50%; --edge: max(1.5px, .45cqmin); }
.scene-share-outline b { position: absolute; inset: 0; background: color-mix(in srgb, var(--s-accent) 8%, transparent); opacity: 0; will-change: opacity; }
.scene-share-outline i { position: absolute; background: color-mix(in srgb, var(--s-accent) 95%, transparent); will-change: transform; }
.scene-share-outline i:nth-of-type(1) { left: 0; right: 0; top: 0; height: var(--edge); transform-origin: 0 50%; }
.scene-share-outline i:nth-of-type(2) { top: 0; bottom: 0; right: 0; width: var(--edge); transform-origin: 50% 0; }
.scene-share-outline i:nth-of-type(3) { left: 0; right: 0; bottom: 0; height: var(--edge); transform-origin: 100% 50%; }
.scene-share-outline i:nth-of-type(4) { top: 0; bottom: 0; left: 0; width: var(--edge); transform-origin: 50% 100%; }
/* The shared window. Its box is the picture of the region; the title bar sits on top of it, so the window can start
   as the region itself. Two shadows: one around the picture alone as it lifts, one around the whole window. */
.scene-share-window { position: absolute; left: 6cqw; top: calc(40cqh + 4.2cqmin); width: 37cqw; --r: 1.5cqmin; opacity: 0; transform-origin: 0 0; will-change: transform, opacity; }
.scene-share-window > u { position: absolute; inset: 0; border-radius: 0 0 var(--r) var(--r); box-shadow: 0 0 0 1px var(--s-edge), var(--s-lift); opacity: 0; will-change: opacity; }
.scene-share-window > u + u { top: -4.2cqmin; border-radius: var(--r); }
.scene-share-clip { position: relative; overflow: hidden; border-radius: 0 0 var(--r) var(--r); }
.scene-share-title { position: absolute; left: 0; right: 0; bottom: 100%; display: flex; align-items: center; gap: .9cqmin; height: 4.2cqmin; padding: 0 1.5cqmin; border-radius: var(--r) var(--r) 0 0; background: var(--s-bar); border-bottom: 1px solid var(--s-edge); color: var(--s-text); font: 600 2.3cqmin/1 -apple-system, "SF Pro Text", "Segoe UI", system-ui, sans-serif; white-space: nowrap; opacity: 0; will-change: opacity; }
.scene-share-title i { flex: none; width: 1.3cqmin; height: 1.3cqmin; border-radius: 50%; background: var(--s-line); }
.scene-share-title span { flex: 1; margin-right: 5.7cqmin; text-align: center; overflow: hidden; text-overflow: ellipsis; opacity: .82; }
.scene-share-view { position: relative; aspect-ratio: var(--region); overflow: hidden; container-type: size; }
.scene-share-ink { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; }
.scene-share-ink path { fill: none; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }

/* Make it yours: the engine's own toolbar, drawn smaller. */
.scene-bar-screen { position: absolute; left: 14cqw; top: 7cqh; width: 72cqw; height: 46cqh; }
.scene-bar-holder { position: absolute; left: 50%; bottom: 7cqh; width: 1100px; height: 150px; transform-origin: 50% 100%; translate: -50% 0; }
.scene-bar-holder > .ink-toolbar { bottom: 0; }

/* Screen zoom (LivePresentation.swift, ZoomFollow.swift). The display is 900 points high: 1 point is 0.111cqh. */
.scene-zoom-screen { position: absolute; inset: 0; transform-origin: 0 0; }
.scene-zoom-screen > .scene-desk { inset: -6%; }
.scene-zoom-app { left: 5cqw; top: 7cqh; width: 90cqw; height: 86cqh; }
.scene-zoom-side { position: absolute; inset: 3.6cqmin auto 0 0; width: 19%; padding: 2.4cqmin 1.8cqmin; display: flex; flex-direction: column; gap: 1.7cqmin; border-right: 1px solid var(--s-edge); }
.scene-zoom-side .scene-row:nth-child(3) { background: var(--s-strong); }
.scene-zoom-table { position: absolute; inset: 3.6cqmin 0 0 19%; padding: .6cqmin 2.6cqmin 1cqmin; display: grid; grid-template-columns: 1.7fr repeat(4, 1fr); grid-auto-rows: 1fr; }
.scene-zoom-table > div { display: flex; align-items: center; justify-content: flex-end; min-width: 0; border-bottom: 1px solid var(--s-edge); color: var(--s-text); font-size: 1.9cqmin; font-weight: 500; line-height: 1; font-variant-numeric: tabular-nums; white-space: nowrap; opacity: .86; }
.scene-zoom-table > div:nth-of-type(5n + 1) { justify-content: flex-start; }
.scene-zoom-table > div:nth-last-of-type(-n + 5) { border-bottom: 0; }
.scene-zoom-table > div > i { display: block; height: 1.15cqmin; border-radius: 1cqmin; background: var(--s-line); }
.scene-zoom-table > div:nth-of-type(-n + 5) > i { background: var(--s-strong); }
.scene-zoom-table > .scene-sweep { display: block; inset: auto 0 auto 0; border-radius: 0; }
.scene-zoom-map { position: absolute; overflow: hidden; border-radius: 1.111cqh; opacity: 0; transform-origin: 100% 100%; will-change: transform, opacity; }
.scene-zoom-map::after { content: ""; position: absolute; inset: 0; border-radius: inherit; border: max(1px, .111cqh) solid rgb(255 255 255 / .6); }
.scene-zoom-mini { position: absolute; inset: 0; container-type: size; }
/* The map is a smoothed picture of the whole display: at its size the desk's dots are one tone. */
.scene-zoom-mini .scene-desk { background: var(--s-desk); }
.scene-zoom-area { position: absolute; inset: 0; --line: max(1.5px, .222cqh); }
/* The zoomed part: white, 2 points wide, with the rest of the map darkened. Each side rides on a carrier as large as
   the map, so it moves and stretches by transforms alone and keeps its width while the part changes size. */
.scene-zoom-area i { position: absolute; inset: 0; will-change: transform; }
.scene-zoom-area b { position: absolute; left: 0; top: 0; background: #fff; transform-origin: 0 0; will-change: transform; }
.scene-zoom-area i:nth-of-type(odd) b { width: 100%; height: var(--line); }
.scene-zoom-area i:nth-of-type(even) b { width: var(--line); height: 100%; }
.scene-zoom-area i:nth-of-type(2) b { margin-left: calc(var(--line) * -1); }
.scene-zoom-area i:nth-of-type(3) b { margin-top: calc(var(--line) * -1); }
.scene-zoom-shade { position: absolute; inset: 0; overflow: hidden; border-radius: inherit; }
.scene-zoom-shade i { position: absolute; left: 0; top: 0; width: 100%; height: 100%; box-shadow: 0 0 0 100cqmax rgb(0 0 0 / .4); transform-origin: 0 0; will-change: transform; }

/* Shapes and icons: the tools window (ToolLibrary.swift, reference/ux-reference/macos/tools-window) at its real size
   in points on a stage of 704 by 440, which is shown as large as the scene. */
.scene-shapes-stage { position: absolute; left: 50%; top: 50%; width: 704px; height: 440px; margin: -220px 0 0 -352px; container-type: size; --ink-layer-z: 4; }
.scene-shapes-stage .ink-layer { overflow: visible; will-change: transform; }
.scene-shapes-glow { left: 414px; top: 96px; width: 260px; height: 300px; }
.scene-shapes-shadow { position: absolute; left: 0; top: 0; overflow: visible; filter: blur(5px); opacity: 0; will-change: transform, opacity; }
.scene[data-appearance="light"] .scene-shapes-shadow path { stroke-opacity: .5; }
.scene-tools { position: absolute; left: 14px; top: 17px; width: 368px; height: 405px; border-radius: 12px; background: var(--t-window); color: var(--t-text); box-shadow: 0 0 0 1px var(--t-edge), 0 22px 44px -14px rgb(0 0 0 / .6); font: 13px/16px -apple-system, "SF Pro Text", "Segoe UI", system-ui, sans-serif; letter-spacing: 0; transform-origin: 50% 100%; opacity: 0; will-change: transform, opacity;
  --t-window: #1e1e1e; --t-text: rgb(255 255 255 / .86); --t-quiet: rgb(255 255 255 / .55); --t-off: #808080; --t-edge: rgb(255 255 255 / .16); --t-tile: #272727; --t-chosen: #192d42; --t-track: #303030; --t-on: #4a4a4a; --t-split: #646464; --t-rule: #343434; }
.scene[data-appearance="light"] .scene-tools { --t-window: #ffffff; --t-text: rgb(0 0 0 / .87); --t-quiet: rgb(0 0 0 / .5); --t-off: #8f8f8f; --t-edge: rgb(0 0 0 / .14); --t-tile: #f6f6f6; --t-chosen: #d6eaff; --t-track: #ebebeb; --t-on: #cdcdcd; --t-split: #b4b4b4; --t-rule: #e6e6e6; box-shadow: 0 0 0 1px var(--t-edge), 0 22px 44px -16px rgb(0 0 0 / .32); }
.scene-tools > * { position: absolute; }
.scene-tools-title { left: 16px; top: 18px; font-weight: 700; }
.scene-tools-paste { left: 243.5px; top: 16px; width: 108.5px; height: 20px; display: flex; align-items: center; justify-content: center; gap: 7px; border-radius: 5px; background: var(--t-track); font-size: 11px; line-height: 20px; }
.scene-tools-tabs { left: 15px; top: 48px; width: 338px; height: 24px; display: flex; border-radius: 6px; background: var(--t-track); }
.scene-tools-tabs span { position: relative; display: grid; place-items: center; border-radius: 6px; }
.scene-tools-tabs span.is-on { background: var(--t-on); }
.scene-tools-tabs span.is-split::before { content: ""; position: absolute; left: -.5px; top: 6px; width: 1px; height: 12px; background: var(--t-split); }
.scene-tools-tile { width: 70px; height: 76px; padding-top: 2.5px; display: flex; flex-direction: column; align-items: center; gap: 4px; border-radius: 9px; background: var(--t-tile); }
.scene-tools-tile.is-selected { background: var(--t-chosen); }
.scene-tools-tile b { height: 26px; display: grid; place-items: center; font-weight: 400; }
.scene-tools-tile b svg { display: block; overflow: visible; }
.scene-tools-tile span { height: 26px; display: grid; place-items: center; font-size: 10px; font-weight: 500; line-height: 13px; text-align: center; white-space: nowrap; }
.scene-tools-tile small { height: 11px; font-size: 9px; line-height: 11px; color: var(--t-quiet); }
.scene-tools-rule { left: 16px; top: 360px; width: 336px; height: 1px; background: var(--t-rule); }
.scene-tools-menu { top: 373px; height: 16px; display: flex; align-items: center; justify-content: center; gap: 4px; white-space: nowrap; }
.scene-tools-menu.is-off { color: var(--t-off); }
.scene-tools-menu svg { margin-top: 1px; }
`;

const el = (className, html = "") => Object.assign(document.createElement("div"), { className, innerHTML: html });
const win = (style, body, extra = "") => `<div class="scene-win" ${extra} style="${style}"><div class="scene-win-bar"><i></i><i></i><i></i><b></b></div><div class="scene-body">${body}</div></div>`;
const rows = (...widths) => widths.map((w) => `<div class="scene-row${w < 0 ? " is-title" : ""}" style="width:${Math.abs(w)}%"></div>`).join("");
const bars = (...heights) => `<div class="scene-bars">${heights.map((h) => `<i style="height:${h}%"></i>`).join("")}</div>`;
const reduced = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const gradientOf = (name) => (GRADIENT_PRESETS.find((choice) => choice.id === String(name || "").toLowerCase()) || GRADIENT_PRESETS[0]);
// The pointer of the engine's scripts.
const POINTER = '<div class="scene-pointer"><svg viewBox="0 0 22 26"><path d="M2 1.5V21l5.2-4.7 3.7 8.2 3.6-1.6-3.6-8h6.9z" fill="#fff" stroke="#0b101a" stroke-width="1.5" stroke-linejoin="round"/></svg></div>';
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

// The page's gradient on a scene: its first, middle and last colors, for the light and the glow.
function tint(root, name) {
  const tones = gradientOf(name).gradient.colors;
  [tones[0], tones[Math.floor(tones.length / 2)], tones[tones.length - 1]].forEach((tone, i) => root.style.setProperty(`--g${i + 1}`, tone));
}
// The root every scene draws in.
function mount(container, name, options) {
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
  tint(root, options.gradient);
  container.append(root);
  return root;
}

// The pieces ------------------------------------------------------------------------------------------
// Each piece is a function of its clock: render(t) sets every transform and opacity for the time t in seconds.
const clamp01 = (value) => Math.min(1, Math.max(0, value));
const mix = (a, b, k) => a + (b - a) * k;
// How far a move that starts at `start` and lasts `length` seconds has gone at time t. Moves start and settle gently.
const go = (t, start, length, shape = ease.easeInEaseOut) => shape(clamp01((t - start) / length));
// A style is written only when it changes.
const written = new WeakMap();
const put = (node, name, value) => {
  if (!node) return;
  let last = written.get(node);
  if (!last) written.set(node, (last = {}));
  if (last[name] === value) return;
  last[name] = value;
  if (name === "text") node.textContent = value; else node.style[name] = value;
};
const fixed = (value, digits = 3) => Number(value.toFixed(digits));
// Something arrives: a short rise and a fade.
const arrive = (node, t, start, rise = "1.8cqmin", length = 0.45) => {
  const k = go(t, start, length, ease.easeOut);
  put(node, "opacity", String(fixed(k)));
  put(node, "translate", k < 1 ? `0 calc(${rise} * ${fixed(1 - k)})` : "none");
};
// What is in a sample window comes after the window, piece by piece: its lines are drawn from the left, its bars
// grow from the ground, its number fades in. `content` is what contents() found in the window.
const contents = (box) => ({ rows: [...box.querySelectorAll(".scene-row, .scene-pills")], bars: [...box.querySelectorAll(".scene-bars i")], number: box.querySelector(".scene-num") });
const furnish = (content, t, start) => {
  content.rows.forEach((row, i) => { const k = fixed(go(t, start + i * 0.035, 0.4, ease.easeOut)); put(row, "transform", k < 1 ? `scaleX(${k})` : "none"); put(row, "opacity", String(Math.min(1, k * 3))); });
  content.bars.forEach((bar, i) => { const k = fixed(go(t, start + 0.08 + i * 0.025, 0.42, ease.easeOut)); put(bar, "transform", k < 1 ? `scaleY(${k})` : "none"); });
  put(content.number, "opacity", String(fixed(go(t, start + 0.05, 0.35, ease.easeOut))));
};
// A point on a curved path from a to b; `bend` is how far the path leaves the straight line, as a share of its length.
const arc = (a, b, k, bend = 0.16) => {
  const c = [(a[0] + b[0]) / 2 + (b[1] - a[1]) * bend, (a[1] + b[1]) / 2 - (b[0] - a[0]) * bend];
  return [(1 - k) * (1 - k) * a[0] + 2 * (1 - k) * k * c[0] + k * k * b[0], (1 - k) * (1 - k) * a[1] + 2 * (1 - k) * k * c[1] + k * k * b[1]];
};
// Where the pointer is at time t: it rests at `home`, then takes each leg ({ at, length, to, bend }) in turn.
const pointerAt = (home, legs, t) => {
  let from = home;
  for (const leg of legs) {
    if (t <= leg.at) return from;
    if (t < leg.at + leg.length) return arc(from, leg.to, go(t, leg.at, leg.length), leg.bend);
    from = leg.to;
  }
  return from;
};
// The app's click: the ring swells by 18% and settles in 0.45 s (CursorHighlightRenderer), then goes with the app's
// short fade. The pointer dips a little as it presses.
const click = (ring, t, at) => {
  const k = (t - at) / DURATIONS.pointerClick, gone = (t - at - DURATIONS.pointerClick) / DURATIONS.fadeOut;
  put(ring, "opacity", k < 0 || gone >= 1 ? "0" : String(fixed(1 - ease.easeIn(clamp01(gone)))));
  put(ring, "scale", String(fixed(pulseScale(clamp01(k)))));
};
const press = (t, clicks) => 1 - 0.1 * clicks.reduce((most, at) => Math.max(most, Math.sin(Math.PI * clamp01((t - at + 0.07) / 0.22))), 0);
// One pass of the light across its holder.
const sweep = (node, t, start, length) => {
  const k = clamp01((t - start) / length);
  put(node, "opacity", String(fixed(0.8 * Math.sin(Math.PI * k))));
  put(node, "translate", `${fixed(mix(-115, 235, ease.easeInEaseOut(k)), 1)}% 0`);
};
// The camera: a pan (shares of the scene) and a scale about the middle. Each plane takes it by its depth, so nearer
// planes move a little more than far ones. `base` is the depth of a plane's own parent plane.
function camera() {
  const planes = [];
  const cam = {
    x: 0, y: 0, s: 1, still: false,
    plane(node, depth = 1, base = 0) { planes.push([node, depth - base]); return node; },
    set(x, y, s) {
      if (cam.still) { x = 0; y = 0; s = 1; }
      Object.assign(cam, { x, y, s });
      for (const [node, depth] of planes) put(node, "transform", x || y || s !== 1 ? `translate(${fixed(x * depth * 100)}%, ${fixed(y * depth * 100)}%) scale(${fixed(1 + (s - 1) * depth, 4)})` : "none");
    },
    // A piece's camera work, the same way in each: it opens a little close and settles as the scene arrives
    // (`opening` seconds), goes in on what the piece is about (`to`: pan and scale, from `at.in` over `at.push`
    // seconds), keeps drifting closer while it holds, and comes back (from `at.out` over `at.pull`).
    film(t, opening, to, at) {
      const arriving = 1 - go(t, 0, opening, ease.easeOut), back = go(t, at.out, at.pull), near = go(t, at.in, at.push) * (1 - back);
      const drifting = ease.smooth((t - at.in - at.push * 0.6) / (at.out - at.in - at.push * 0.6)) * (1 - back);
      cam.set(to.x * near + (to.dx || 0) * drifting, to.y * near + (to.dy || 0) * drifting + START.y * arriving, 1 + (to.s - 1) * near + (to.ds ?? 0.022) * drifting + (START.s - 1) * arriving);
      return near;
    },
  };
  return cam;
}
// Where the camera is as a piece begins.
const START = { x: 0, y: 0.012, s: 1.04 };
// A new round fades what the last one left, while the camera comes home.
const LEAD = 0.26;
function piece(container, name, options, build) {
  const root = mount(container, name, options), cam = camera(), parts = build({ root, cam });
  let alive = true, frame = 0, settle = null, state = "opening", shown = parts.opening;
  const show = (t) => { shown = t; parts.render(t); };
  const fade = (amount) => { for (const node of parts.acts) put(node, "opacity", amount >= 1 ? "" : String(fixed(amount))); };
  const halt = () => {
    cancelAnimationFrame(frame);
    frame = 0;
    if (settle) { const done = settle; settle = null; done(false); }
  };
  const still = (t, kind) => { cam.still = kind === "finished"; fade(1); show(t); state = kind; };
  const sized = window.ResizeObserver ? new ResizeObserver(() => { parts.layout?.(); if (!frame) show(shown); }) : null;
  parts.layout?.();
  still(parts.opening, "opening");
  sized?.observe(root);
  parts.ready?.then(() => { if (alive && !frame) show(shown); });
  return {
    play() {
      if (!alive) return Promise.resolve(false);
      halt();
      if (reduced()) { still(parts.finished, "finished"); return Promise.resolve(true); }
      // From its opening picture the piece simply goes on. From anywhere else, what is on screen fades first and the
      // piece starts from its empty stage.
      const from = state === "opening" ? parts.opening : -LEAD, home = { x: cam.x, y: cam.y, s: cam.s };
      cam.still = false;
      state = "playing";
      return new Promise((done) => {
        let began = 0;
        settle = done;
        const step = (now) => {
          began ||= now;
          const t = from + (now - began) / 1000;
          if (t < 0) {
            // What the last round left fades, and the camera goes to where the piece begins.
            const k = ease.easeInEaseOut(1 + t / LEAD);
            fade(1 - k);
            cam.set(mix(home.x, START.x, k), mix(home.y, START.y, k), mix(home.s, START.s, k));
          } else {
            fade(1);
            show(Math.min(t, parts.duration));
          }
          if (t >= parts.duration) { frame = 0; settle = null; state = "end"; done(true); return; }
          frame = requestAnimationFrame(step);
        };
        frame = requestAnimationFrame(step);
      });
    },
    stop() { if (frame) state = "held"; halt(); },
    reset() { halt(); still(parts.opening, "opening"); },
    setAppearance(appearance) { root.dataset.appearance = appearance === "light" ? "light" : "dark"; parts.appearance?.(root.dataset.appearance); },
    // Another of the app's gradients, at once and where the piece is: nothing starts over.
    setGradient(name) { tint(root, name); parts.gradient?.(gradientOf(name)); if (!frame) show(shown); },
    destroy() { if (!alive) return; halt(); alive = false; sized?.disconnect(); parts.destroy?.(); root.remove(); },
  };
}

// Take a break ------------------------------------------------------------------------------------------
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
// Moving colors (BreakBackdropView): each field drifts by a share of the screen over its period and back, and swells
// to 1.16 over another, both eased in and out.
const FIELDS = [[20, 14, 22, 29 * 0.8], [-18, -16, 29, 37 * 0.8], [-12, 10, 37, 22 * 0.8]];
const sway = (time, period) => { const u = (time / period) % 2; return ease.easeInEaseOut(u < 1 ? u : 2 - u); };

// The sample screen dims and softens as the break screen rises over it; the ring sweeps in, the seconds roll, and
// the camera moves in a little on the clock.
export function breakTimerScene(container, options = {}) {
  return piece(container, "break", options, ({ root, cam }) => {
    const TOTAL = 600, RISE = 1.15, shades = (name) => { const [top, bottom, [a, b, c], accent] = PALETTES[breakPaletteFor(name)]; return { top, bottom, a, b, c, accent }; };
    // The ring: a line of 1.6% of the short side on a circle of 60%, inset by 1.4 line widths (BreakRingLayerView.layout).
    const line = (1.6 / 60) * 100, radius = 50 - line * 1.4, circle = (extra) => `<circle cx="50" cy="50" r="${radius}" pathLength="1" ${extra}/>`;
    const windows = `${win("left:7cqw;top:10cqh;width:50cqw;height:66cqh", `${rows(-46, 88, 74, 81, 52)}${bars(38, 52, 47, 70, 61, 84, 76)}`)}${win("left:61cqw;top:20cqh;width:32cqw;height:54cqh", `<div class="scene-num">318</div>${rows(64, 80, 42)}<div class="scene-pills"><i></i><i></i></div>`)}`;
    root.innerHTML = `<div class="scene-plane scene-far"><div class="scene-desk"></div></div>
      <div class="scene-act"><div class="scene-plane" data-sharp>${windows}</div><div class="scene-plane scene-soft">${windows}</div><div class="scene-dim"></div></div>
      <div class="scene-act"><div class="scene-break-face">
        <div class="scene-plane scene-break-fields"><i></i><i></i><i></i></div><div class="scene-break-shade"></div>
        <div class="scene-plane" data-near><div class="scene-glow scene-break-glow"></div><div class="scene-break-clock"><div class="scene-break-title">Coffee break</div>
          <div class="scene-break-ring"><svg viewBox="0 0 100 100">${circle(`stroke="rgb(255 255 255 / .1)" stroke-width="${line}"`)}${circle(`data-arc stroke-opacity=".22" stroke-width="${line * 2.6}"`)}${circle(`data-arc stroke-width="${line}"`)}</svg>
            <div class="scene-break-count"><b>${"<span><i></i><i></i></span>".repeat(5)}</b><span></span></div></div></div></div></div></div>`;
    const $ = (selector) => root.querySelector(selector), $$ = (selector) => [...root.querySelectorAll(selector)];
    const sharp = $$("[data-sharp] .scene-win"), inside = sharp.map(contents), soft = $(".scene-soft"), dim = $(".scene-dim"), face = $(".scene-break-face"), fields = $$(".scene-break-fields > i"), glow = $(".scene-break-glow");
    const group = $(".scene-break-clock"), arcs = $$("[data-arc]"), digits = $(".scene-break-count b"), slots = $$(".scene-break-count b span").map((slot) => [...slot.children]), back = $(".scene-break-count > span");
    cam.plane($(".scene-far"), 0.9); cam.plane($("[data-sharp]"), 1); cam.plane(soft, 1); cam.plane($(".scene-break-fields"), 0.82); cam.plane($("[data-near]"), 1.14);
    // The break screen's colors: the app's palette nearest to the page's gradient.
    const color = (name) => {
      const shade = shades(name);
      for (const key in shade) face.style.setProperty(`--bk-${key}`, shade[key]);
      for (const arc of arcs) arc.setAttribute("stroke", shade.accent);
    };
    color(options.gradient);
    // BreakTimerController.endTimeText: "Back at" and the time the break ends, in the reader's own clock format.
    back.textContent = `Back at ${new Date(Date.now() + TOTAL * 1000).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`;
    let digit = 0;
    return {
      duration: 8.6, opening: 0.92, finished: 5.9, acts: $$(".scene-act"),
      layout() { digit = 0; },
      gradient: (preset) => color(preset.id),
      render(t) {
        const b = t - RISE;
        sharp.forEach((node, i) => { arrive(node, t, 0.04 + i * 0.055); furnish(inside[i], t, 0.13 + i * 0.055); });
        // The break screen fades in over 0.45 s on the app's fade curve; what is under it goes dark and soft first.
        put(soft, "opacity", String(fixed(go(t, RISE - 0.12, 0.42, ease.easeOut))));
        put(dim, "opacity", String(fixed(0.42 * go(t, RISE - 0.12, 0.5, ease.easeOut))));
        put(face, "opacity", String(fixed(go(t, RISE, 0.45, ease.fade))));
        fields.forEach((node, i) => {
          const [dx, dy, period, swell] = FIELDS[i], k = sway(Math.max(b, 0), period);
          put(node, "transform", `translate(${fixed(dx * k, 2)}cqw, ${fixed(dy * k, 2)}cqh) scale(${fixed(1 + 0.16 * sway(Math.max(b, 0), swell), 4)})`);
        });
        // The clock settles with the app's smooth spring: from 94% and 24 points lower.
        const settle = b > 0 ? spring(SPRINGS.smooth, b) : 0;
        put(group, "opacity", String(fixed(clamp01(settle))));
        put(group, "transform", `translate(0, ${fixed(2.67 * (1 - settle))}cqmin) scale(${fixed(0.94 + 0.06 * settle, 4)})`);
        // The ring fills in 0.6 s on the enter curve (BreakRingLayerView.restart), then drains with the time.
        const left = TOTAL - Math.max(b, 0), share = go(t, RISE + 0.2, 0.6, ease.enter) * (left / TOTAL);
        for (const arc of arcs) { put(arc, "strokeDasharray", `${fixed(Math.max(share, 0.0001), 4)} 2`); put(arc, "visibility", share > 0.0005 ? "" : "hidden"); }
        // The seconds: a digit that changes rolls down as the next comes from above (numericText, counting down), on
        // the app's spring.
        const passed = b > 1 ? Math.ceil(b) - 1 : 0, now = clock(TOTAL - passed).padStart(5), before = passed ? clock(TOTAL - passed + 1).padStart(5) : now;
        const roll = passed ? clamp01(spring({ response: 0.42, dampingFraction: 0.86 }, b - passed)) : 1, moving = roll < 0.999;
        slots.forEach(([front, old], i) => {
          const next = now[i].trim(), last = before[i].trim(), changes = moving && next !== last;
          put(front, "text", next);
          put(front, "opacity", changes ? String(fixed(roll)) : "1");
          put(front, "translate", changes ? `0 ${fixed(-0.34 * (1 - roll))}em` : "none");
          put(old, "text", changes ? last : "");
          put(old, "opacity", changes ? String(fixed(1 - roll)) : "0");
          put(old, "translate", changes ? `0 ${fixed(0.34 * roll)}em` : "none");
        });
        // A place that empties makes the number narrower: what is left slides to its new middle.
        digit ||= slots[4][0].offsetWidth;
        put(digits, "translate", moving && now.trim().length < before.trim().length ? `${fixed((digit / 2) * (1 - roll), 2)}px 0` : "none");
        put(back, "opacity", String(fixed(go(t, RISE + 0.7, 0.45, ease.easeOut))));
        // One swell of light behind the ring; the camera goes in on the clock, drifts closer while it counts, and
        // comes back.
        const lit = clamp01((b - 0.3) / 1.5);
        put(glow, "opacity", String(fixed(0.9 * Math.sin(Math.PI * lit))));
        put(glow, "scale", String(fixed(mix(0.7, 1.2, ease.easeOut(lit)))));
        cam.film(t, 0.85, { x: 0, y: 0.004, s: 1.085, ds: 0.03 }, { in: RISE + 0.3, push: 2.2, out: 6.7, pull: 1.6 });
      },
    };
  });
}

// Share just one part -----------------------------------------------------------------------------------
// The chooser's outline is drawn around half of a wide screen; that part lifts and travels into a window of its own,
// and ink drawn in the part shows in the window at the same moment.
export function shareRegionScene(container, options = {}) {
  return piece(container, "share", options, ({ root, cam }) => {
    const id = `scene-ink-${++serial}`, tones = gradientOf(options.gradient).gradient;
    // What is inside the region, drawn twice from the same markup: on the screen and in the shared window.
    const inside = (extra) => `<div class="scene-desk"></div>${win("left:10cqw;top:12cqh;width:80cqw;height:76cqh", `<div class="scene-num">318</div>${rows(52)}${bars(46, 58, 52, 74, 40, 30, 24)}`, extra)}`;
    const stops = (colors) => colors.map((color, i) => `<stop offset="${i / (colors.length - 1)}" stop-color="${color}"/>`).join("");
    const ink = (suffix) => `<svg class="scene-share-ink" viewBox="0 0 120 100" preserveAspectRatio="none"><defs><linearGradient id="${id}${suffix}" gradientUnits="userSpaceOnUse" x1="8" y1="30" x2="34" y2="14">${stops(tones.colors)}</linearGradient></defs>
      <path pathLength="1" stroke="url(#${id}${suffix})" d="${loop(21, 21.6, 12.6, 8.6)}"/></svg>`;
    root.innerHTML = `<div class="scene-fit"><div class="scene-act">
      <div class="scene-plane" data-far><div class="scene-share-screen"><div class="scene-desk"></div>${win("left:4cqw;top:14cqh;width:40cqw;height:72cqh", `${rows(-40, 86, 70, 78, 60, 82, 48)}<div class="scene-pills"><i></i><i></i></div>`, "data-in")}
        <div class="scene-share-region">${inside("data-in")}${ink("a")}</div><div class="scene-veil" style="--veil-x:62%;--veil-y:34%;--veil-w:32%;--veil-h:66%"></div><div class="scene-share-outline"><b></b><i></i><i></i><i></i><i></i></div></div></div>
      <div class="scene-plane" data-near><div class="scene-share-window" style="--region: 1"><u></u><u></u><div class="scene-share-title"><i></i><i></i><i></i><span>Annotator - Shared Region</span></div><div class="scene-share-clip"><div class="scene-share-view">${inside("")}${ink("b")}</div><div class="scene-sweep"><i></i></div></div></div></div></div></div>`;
    const $ = (selector) => root.querySelector(selector), $$ = (selector) => [...root.querySelectorAll(selector)];
    const screen = $(".scene-share-screen"), entering = $$("[data-in]"), within = entering.map(contents), fill = $(".scene-share-outline b"), edges = $$(".scene-share-outline i"), shared = $(".scene-share-window"), [lift, whole] = $$(".scene-share-window > u");
    const title = $(".scene-share-title"), view = $(".scene-share-view"), light = $(".scene-sweep i"), paths = $$(".scene-share-ink path"), veil = $(".scene-veil");
    cam.plane($("[data-far]"), 0.9); cam.plane($("[data-near]"), 1.12);
    // Where the region is and where its window rests, in the composition's own pixels. Layout sizes, not boxes on
    // screen: a page may turn the scene in 3D, which changes the box but not the shape.
    let region = null, home = null;
    const draw = (share) => paths.forEach((path) => { put(path, "strokeDasharray", `${fixed(share, 4)} 2`); put(path, "visibility", share > 0.001 ? "" : "hidden"); });
    return {
      duration: 8.4, opening: 0.95, finished: 6.4, acts: $$(".scene-act"),
      // The ink takes the new gradient where it is.
      gradient: (preset) => { for (const paint of $$(".scene-share-ink linearGradient")) paint.innerHTML = stops(preset.gradient.colors); },
      layout() {
        const w = screen.offsetWidth, h = screen.offsetHeight;
        if (!w || !h) return;
        shared.style.setProperty("--region", `${(w / 2).toFixed(2)} / ${h.toFixed(2)}`);
        region = { x: screen.offsetLeft + w / 2, y: screen.offsetTop, w: w / 2, h };
        home = { x: shared.offsetLeft, y: shared.offsetTop, w: shared.offsetWidth, h: view.offsetHeight };
      },
      render(t) {
        arrive(screen, t, 0, "2.4cqmin");
        entering.forEach((node, i) => { arrive(node, t, 0.12 + i * 0.06); furnish(within[i], t, 0.2 + i * 0.06); });
        // Choosing: the outline is drawn once around, top side first, and its faint fill comes with it.
        const around = go(t, 1.08, 0.86), sides = region ? [region.w, region.h, region.w, region.h] : [1, 1, 1, 1], length = sides.reduce((sum, side) => sum + side, 0);
        let done = 0;
        edges.forEach((edge, i) => {
          const k = fixed(clamp01((around * length - done) / sides[i]), 4);
          put(edge, "transform", i % 2 ? `scaleY(${k})` : `scaleX(${k})`);
          done += sides[i];
        });
        // Sharing starts: the fill goes (RegionSharingGuide), and the part lifts off the screen.
        const lifted = go(t, 2.25, 0.42, ease.easeOut), travel = go(t, 2.6, 1.0), titled = go(t, 3.0, 0.45, ease.easeOut);
        put(fill, "opacity", String(fixed(go(t, 1.15, 0.6, ease.easeOut) * (1 - go(t, 2.2, DURATIONS.fadeIn, ease.easeOut)))));
        if (region && home) {
          // The window starts as the part itself, exactly over it; it comes a little closer, then travels on an arc
          // to its place and takes its title bar on the way.
          const start = region.w / home.w, from = [region.x + region.w / 2, region.y + region.h / 2], to = [home.x + home.w / 2, home.y + home.h / 2];
          const at = arc(from, to, travel, -0.2), size = mix(start * (1 + 0.05 * lifted), 1, travel) * (1 + 0.035 * Math.sin(Math.PI * travel));
          put(shared, "transform", `translate(${fixed(at[0] - home.x - size * (home.w / 2), 2)}px, ${fixed(at[1] - home.y - size * (home.h / 2), 2)}px) scale(${fixed(size, 4)})`);
        }
        put(shared, "opacity", t >= 2.25 ? "1" : "0");
        put(lift, "opacity", String(fixed(lifted * (1 - titled))));
        put(whole, "opacity", String(fixed(titled)));
        put(title, "opacity", String(fixed(titled)));
        // What is drawn in the part is in the window too, stroke for stroke.
        draw(go(t, 4.2, 1.05));
        sweep(light, t, 5.3, 0.85);
        // The camera goes in between the ink on the screen and the ink in the window, drifts, and comes back; while
        // it is close, the rest of the screen falls back.
        const near = cam.film(t, 0.9, { x: 0.13, y: 0.115, s: 1.24, dx: -0.006 }, { in: 3.7, push: 1.5, out: 6.8, pull: 1.3 });
        put(veil, "opacity", String(fixed(near)));
      },
    };
  });
}

// Zoom in -----------------------------------------------------------------------------------------------
// Screen zoom over a sample screen with small print. The zoom is the app's: it grows around the pointer, as it does
// when someone scrolls into it, so what is under the pointer stays under it; the view then holds still while the
// pointer moves inside it and moves along when the pointer nears an edge (ZoomFollow); and the map of the whole
// display sits bottom right with the zoomed part outlined (ZoomMap). The app shows nothing else of its own on a
// zoomed screen but the toolbar, which this scene leaves out.
const FIGURES = [
  ["1,284", "4.2", "96", "12:40"], ["907", "3.1", "142", "09:15"], ["2,416", "5.8", "88", "14:05"], ["655", "2.4", "203", "08:30"],
  ["1,730", "4.9", "67", "16:20"], ["318", "1.7", "54", "11:45"], ["1,092", "3.6", "171", "13:10"], ["846", "2.9", "65", "10:55"],
];
export function zoomScene(container, options = {}) {
  return piece(container, "zoom", options, ({ root, cam }) => {
    const IN = 1.85, PUSH = 1.25, WANDER = 3.95, OUT = 6.35, PULL = 0.9, DEEP = 3, STEP = 1 / 120;
    const bar = (width) => `<div><i style="width:${width}%"></i></div>`, names = [62, 48, 70, 55, 66, 44, 58, 51];
    const table = `${[34, 46, 40, 34, 44].map(bar).join("")}${FIGURES.map((row, r) => `${bar(names[r])}${row.map((value, c) => `<div${r === 5 && c === 0 ? " data-read" : ""}>${value}</div>`).join("")}`).join("")}`;
    const app = (live) => `<div class="scene-win scene-zoom-app"><div class="scene-win-bar"><i></i><i></i><i></i><b></b></div><div class="scene-zoom-side">${rows(-70, 84, 62, 76, 54, 70)}</div><div class="scene-zoom-table">${table}${live ? '<span class="scene-sweep"><i></i></span>' : ""}</div></div>`;
    root.innerHTML = `<div class="scene-plane" data-far><div class="scene-zoom-screen"><div class="scene-desk"></div></div></div>
      <div class="scene-act"><div class="scene-plane" data-view><div class="scene-zoom-screen">${app(true)}</div>${POINTER}</div>
        <div class="scene-plane" data-near><div class="scene-zoom-map"><div class="scene-zoom-mini"><div class="scene-desk"></div>${app(false)}</div><div class="scene-zoom-shade"><i></i></div><div class="scene-zoom-area">${"<i><b></b></i>".repeat(4)}</div></div></div></div>`;
    const $ = (selector) => root.querySelector(selector), $$ = (selector) => [...root.querySelectorAll(selector)];
    const screens = $$(".scene-zoom-screen"), sample = $("[data-view] .scene-zoom-app"), cell = $("[data-view] [data-read]"), band = $(".scene-zoom-table > .scene-sweep"), light = $(".scene-zoom-table .scene-sweep i");
    const cells = $$("[data-view] .scene-zoom-table > div"), aside = { rows: $$("[data-view] .scene-zoom-side .scene-row"), bars: [], number: null };
    const map = $(".scene-zoom-map"), shade = $(".scene-zoom-shade i"), sides = $$(".scene-zoom-area i").map((carrier) => [carrier, carrier.firstChild]), pointer = $(".scene-pointer");
    cam.plane($("[data-far]"), 0.9); cam.plane($("[data-view]"), 0.97); cam.plane($("[data-near]"), 1.12);
    // The display in points: 900 high and as wide as the scene's shape makes it.
    let display = { x: 0, y: 0, width: 1440, height: 900 }, frame = null, read = [0.55, 0.7], legs = [], slid = [], rested = null;
    const home = [0.5, 0.9], points = (p) => [p[0] * display.width, p[1] * display.height];
    const share = (value) => `${fixed(value * 100)}%`;
    return {
      duration: 8.3, opening: 0.9, finished: 3.9, acts: $$(".scene-act"),
      layout() {
        const w = root.clientWidth, h = root.clientHeight;
        if (!w || !h) return;
        display = { x: 0, y: 0, width: (900 * w) / h, height: 900 };
        // The map's place: bottom right, 16 points in (ZoomMap.frame). A display too small for it has none.
        frame = ZoomMap.frame(display);
        if (frame) Object.assign(map.style, { left: share(frame.x / display.width), top: share(frame.y / display.height), width: share(frame.width / display.width), height: share(frame.height / display.height) });
        // Just past the end of the number, where the pointer rests to read it.
        let x = cell.offsetWidth + h * 0.004, y = cell.offsetHeight * 0.6;
        for (let node = cell; node && node !== root; node = node.offsetParent) { x += node.offsetLeft; y += node.offsetTop; }
        read = [x / w, y / h];
        Object.assign(band.style, { top: `${cell.offsetTop}px`, height: `${cell.offsetHeight}px` });
        legs = [{ at: 0.9, length: 0.8, to: read, bend: -0.22 }, { at: WANDER, length: 0.7, to: [0.972, read[1] - 0.07], bend: 0.12 }, { at: 5.35, length: 0.55, to: [0.76, read[1] - 0.05], bend: -0.1 }, { at: 7.25, length: 0.8, to: [0.62, 0.86], bend: 0.18 }];
        // While zoomed, the pointer's own move in an edge zone pushes the view along, and a pointer that rests there
        // slides it. That depends on every step before, so it is worked out once for the whole stretch.
        let view = Viewport.cursorAnchored(display, DEEP, points(read)), last = points(read);
        slid = [];
        for (let time = WANDER; time <= OUT + STEP; time += STEP) {
          const now = points(pointerAt(home, legs, time));
          view = ZoomFollow.slide(ZoomFollow.push(view, now, [now[0] - last[0], now[1] - last[1]]), now, STEP);
          slid.push(view.source);
          last = now;
        }
        rested = { source: slid[slid.length - 1], pointer: last };
      },
      render(t) {
        const at = pointerAt(home, legs, t);
        // The window comes first; its side list is drawn in, and the table fills row after row.
        arrive(sample, t, 0.04, "2.2cqmin");
        furnish(aside, t, 0.16);
        cells.forEach((each, i) => {
          const k = fixed(go(t, 0.14 + Math.floor(i / 5) * 0.04, 0.36, ease.easeOut));
          put(each, "opacity", String(fixed(0.86 * k)));
          put(each, "translate", k < 1 ? `0 ${fixed(0.9 * (1 - k))}cqmin` : "none");
        });
        put(pointer, "opacity", String(fixed(go(t, 0.6, DURATIONS.fadeIn, ease.easeOut))));
        put(pointer, "transform", `translate(${fixed(at[0] * 100)}cqw, ${fixed(at[1] * 100)}cqh)`);
        // The part of the display that fills the screen now.
        let source = display;
        if (t > IN && t <= WANDER) {
          const factor = mix(1, DEEP, go(t, IN, PUSH));
          if (factor > 1.0005) source = Viewport.cursorAnchored(display, factor, points(read)).source;
        } else if (t > WANDER && t <= OUT && slid.length) {
          const place = Math.min((t - WANDER) / STEP, slid.length - 1), a = slid[Math.floor(place)], b = slid[Math.ceil(place)], k = place - Math.floor(place);
          source = { x: mix(a.x, b.x, k), y: mix(a.y, b.y, k), width: a.width, height: a.height };
        } else if (t > OUT && rested) {
          const factor = mix(DEEP, 1, go(t, OUT, PULL));
          if (factor > 1.0005) source = ZoomFollow.viewport(rested.source, display, factor, rested.pointer).source;
        }
        const factor = display.width / source.width, zoomed = factor > 1.0005;
        for (const screen of screens) put(screen, "transform", zoomed ? `translate(${share((-source.x * factor) / display.width)}, ${share((-source.y * factor) / display.height)}) scale(${fixed(factor, 4)})` : "none");
        // The map: in its corner from the first zoomed picture (it fades in over 0.15 s, here with a short slide), with
        // the zoomed part outlined in white and the rest darkened.
        const part = ZoomMap.zoomedArea({ output: display, source }, { x: 0, y: 0, width: 1, height: 1 });
        put(shade, "transform", `translate(${share(part.x)}, ${share(part.y)}) scale(${fixed(part.width, 4)}, ${fixed(part.height, 4)})`);
        // Top, right, bottom and left of the outline, each as long as its side of the part.
        [[part.x, part.y], [part.x + part.width, part.y], [part.x, part.y + part.height], [part.x, part.y]].forEach(([x, y], i) => {
          put(sides[i][0], "transform", `translate(${share(x)}, ${share(y)})`);
          put(sides[i][1], "transform", i % 2 ? `scaleY(${fixed(part.height, 4)})` : `scaleX(${fixed(part.width, 4)})`);
        });
        const there = go(t, IN + 0.08, ZoomMap.fadeDuration, ease.fade) * (1 - go(t, OUT + PULL - 0.16, 0.14, ease.easeIn)), come = go(t, IN + 0.08, 0.42, ease.easeOut);
        put(map, "opacity", frame ? String(fixed(there)) : "0");
        put(map, "transform", come < 1 ? `translate(0, ${fixed(1.8 * (1 - come))}cqh) scale(${fixed(0.9 + 0.1 * come, 4)})` : "none");
        sweep(light, t, 3.05, 0.8);
        // The camera goes in with the zoom, toward the pointer, drifts closer while the screen is read, and comes
        // back with the zoom.
        cam.film(t, 0.85, { x: (0.5 - read[0]) * 0.085, y: (0.5 - read[1]) * 0.085, s: 1.085, ds: 0.02 }, { in: IN, push: 1.5, out: OUT - 0.15, pull: 1.3 });
      },
    };
  });
}

// Shapes and icons ------------------------------------------------------------------------------------
// The Shapes section of the tools window as far as it shows without scrolling (ToolLibrarySection.shapes): each
// tool's name and key, and for the ones the Mac draws with SF Symbols the Windows edition's icon. The system-design
// shapes are drawn from the app's own outlines, as ToolGlyph draws them.
const SHAPE_TILES = [
  ["rectangle", "Rectangle", "2", "rectangle", 24.5], ["ellipse", "Ellipse", "4", "circle", 21], ["diamond", "Diamond", "3", "diamond", 24], ["triangle", "Triangle", "", "triangle", 22.5],
  ["hexagon", "Hexagon", "", "hexagon", 23], ["star", "Star", "", "star", 23.5], ["person", "Person", "H", "person", 22], ["databaseStack", "Database", ""],
  ["server", "Server", ""], ["messaging", "Messaging", ""], ["web", "Web", ""], ["mobile", "Mobile phone", ""],
];
const icon = (name, size, weight = 1.6) => `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="${((weight * 24) / size).toFixed(2)}" stroke-linecap="round" stroke-linejoin="round">${
  APP_ICONS[name].map((part) => `<path d="${part.d}"${part.fill ? ' fill="currentColor"' : ""}/>`).join("")}</svg>`;
const outlineOf = (tool, frame) => pathData(canonicalShape({ tool, frame, style: {} }));
// ToolGlyph: the outline in a 21-point square, inset and stroked by a twelfth of it.
const glyph = (tool) => `<svg viewBox="0 0 21 21" width="21" height="21"><path d="${outlineOf(tool, { x: 1.75, y: 1.75, width: 17.5, height: 17.5 })}" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
// ToolLibraryPanel at its default width of 368 points: every frame is the one in tools-window/section-shapes.
const toolsWindow = () => `<div class="scene-tools"><div class="scene-tools-title">Tools</div><div class="scene-tools-paste">${icon("photo.on.rectangle", 14, 1.4)}Paste image</div>
  <div class="scene-tools-tabs">${[["Draw", 57], ["Shapes", 69], ["Focus", 60.5], ["Extras", 62], ["My shapes", 89.5]].map(([name, width], i) => `<span class="${i === 1 ? "is-on" : i > 2 ? "is-split" : ""}" style="width:${width}px">${name}</span>`).join("")}</div>
  ${SHAPE_TILES.map(([tool, name, key, symbol, size], i) => `<div class="scene-tools-tile" data-tool="${tool}" style="left:${[16, 94, 171.5, 249.5][i % 4]}px;top:${84 + Math.floor(i / 4) * 84}px"><b>${symbol ? icon(symbol, size) : glyph(tool)}</b><span>${name}</span><small>${key}</small></div>`).join("")}
  <div class="scene-tools-rule"></div>${[["Selection", 74, 76, " is-off"], ["File", 158, 41, ""], ["Workspace", 207, 87, ""]].map(([name, left, width, off]) => `<div class="scene-tools-menu${off}" style="left:${left}px;width:${width}px">${name}${icon("chevron.down", 9, 1.7)}</div>`).join("")}</div>`;

// The tools window opens over a sample screen; the pointer picks Database and Server, and each lifts out of the
// window and travels to its place; an arrow then joins them. The three are the engine's own marks, as the app makes
// them. In the app the window closes when a tool is picked; here it stays, so both picks are in one picture.
export function shapesScene(container, options = {}) {
  return piece(container, "shapes", options, ({ root, cam }) => {
    // Where each shape rests on the stage, the middle of its tile's glyph in the window, and when it is picked.
    const SHAPES = [
      { tool: "databaseStack", from: [496, 92], to: [592, 199], tile: [298.5, 200.5], at: 1.62 },
      { tool: "server", from: [496, 298], to: [592, 394], tile: [65, 284.5], at: 2.7 },
    ];
    const LINK = { tool: "arrow", from: [544, 208], to: [544, 288] }, FLIGHT = 0.9, GLYPH = 17.5, LINE = 3;
    for (const shape of SHAPES) shape.box = { x: shape.from[0], y: shape.from[1], width: shape.to[0] - shape.from[0], height: (shape.to[0] - shape.from[0]) / (shape.tool === "server" ? 1 : 0.9) };
    const shadow = ({ tool, box }) => `<svg class="scene-shapes-shadow" width="704" height="440" viewBox="0 0 704 440"><path d="${outlineOf(tool, box)}" fill="none" stroke="#000" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
    root.innerHTML = `<div class="scene-plane scene-far"><div class="scene-desk"></div></div><div class="scene-fit"><div class="scene-shapes-stage"><div class="scene-act">
      <div class="scene-plane" data-mid>${win("left:398px;top:17px;width:292px;height:406px", rows(-38, 62))}<div class="scene-glow scene-shapes-glow"></div>${SHAPES.map(({ box }) => `<div class="scene-glow" data-landing style="left:${box.x - 34}px;top:${box.y - 30}px;width:${box.width + 68}px;height:${box.height + 60}px"></div>`).join("")}</div>
      <div class="scene-plane" data-near style="z-index:2">${toolsWindow()}</div>
      <div class="scene-plane" data-veil style="z-index:2"><div class="scene-veil" style="--veil-x:74%;--veil-y:54%;--veil-w:23%;--veil-h:46%"></div></div>
      <div class="scene-plane" data-cast style="z-index:3">${SHAPES.map(shadow).join("")}</div>
      <div class="scene-plane" data-point style="z-index:5"><div class="scene-click"></div>${POINTER}</div></div></div></div>`;
    const $ = (selector) => root.querySelector(selector), $$ = (selector) => [...root.querySelectorAll(selector)];
    const fit = $(".scene-fit"), board = $(".scene-shapes-stage"), sample = $("[data-mid] .scene-win"), within = contents(sample), glow = $(".scene-shapes-glow"), landings = $$("[data-landing]"), veil = $(".scene-veil");
    const tools = $(".scene-tools"), casts = $$(".scene-shapes-shadow"), ring = $(".scene-click"), pointer = $(".scene-pointer");
    const tiles = SHAPES.map(({ tool }) => tools.querySelector(`[data-tool="${tool}"]`));
    const ink = createInk({ surface: board, interactive: false, gradient: gradientOf(options.gradient).id, appearance: root.dataset.appearance, glass: false });
    cam.plane($(".scene-far"), 0.88); cam.plane($("[data-mid]"), 1); cam.plane($("[data-veil]"), 1); cam.plane($("[data-cast]"), 1); cam.plane(ink.layer, 1); cam.plane($("[data-near]"), 1.07); cam.plane($("[data-point]"), 1.07);
    // The marks are made once, as the app makes them from a drag; the clock then moves and draws them.
    let marks = null, tone = gradientOf(options.gradient);
    const node = (mark) => ink.layer.querySelector(`[data-id="${mark.id}"]`);
    // The marks in the page's gradient: the engine's selected color, and the color of what is drawn already.
    const color = () => {
      ink.setGradient(tone.id);
      for (const { mark } of marks || []) { mark.style.strokeGradient = { colors: [...tone.gradient.colors], angle: tone.gradient.angle }; ink.render(mark); }
    };
    const ready = ink.perform([...SHAPES, LINK].map((step) => ({ tool: step.tool, from: step.from, to: step.to, app: true, duration: 0 })), { cursor: false }).then((made) => {
      marks = made.map((mark) => ({ mark, node: node(mark) }));
      for (const { node: drawn } of marks.slice(0, 2)) drawn.style.transformBox = "view-box";
      color();
    });
    const home = [436, 404], clicks = SHAPES.map((shape) => shape.at);
    const legs = [{ at: 0.82, length: 0.62, to: [SHAPES[0].tile[0] + 4, SHAPES[0].tile[1] + 22], bend: 0.2 }, { at: 1.95, length: 0.58, to: [SHAPES[1].tile[0] + 4, SHAPES[1].tile[1] + 22], bend: -0.16 }, { at: 3.0, length: 0.7, to: [236, 416], bend: 0.22 }];
    const pop = visibilityDuration("pop", true);
    let width = "";
    return {
      duration: 8.5, opening: 0.78, finished: 6.1, ready,
      get acts() { return [$(".scene-act"), ink.layer]; },
      // The stage is laid out in the app's points and shown as large as the scene.
      layout() { const w = fit.clientWidth; if (w) board.style.scale = (w / 704).toFixed(4); },
      appearance: (appearance) => ink.setAppearance(appearance),
      gradient: (preset) => { tone = preset; color(); },
      render(t) {
        arrive(sample, t, 0.04, "8px");
        furnish(within, t, 0.14);
        // The tools window opens with the app's Pop.
        const opened = pose("pop", 1 - poseProgress("pop", clamp01((t - 0.3) / pop), true), { appearing: true, annotation: false });
        put(tools, "opacity", t < 0.3 ? "0" : String(fixed(opened.opacity)));
        put(tools, "scale", String(fixed(opened.scale, 4)));
        // The pointer comes on a curve, rests a moment, and clicks.
        const at = pointerAt(home, legs, t), last = clicks.reduce((found, time) => (t >= time ? time : found), -1);
        put(pointer, "opacity", String(fixed(go(t, 0.55, DURATIONS.fadeIn, ease.easeOut) * (1 - go(t, 3.45, 0.3, ease.easeIn)))));
        put(pointer, "transform", `translate(${fixed(at[0], 2)}px, ${fixed(at[1], 2)}px) scale(${fixed(press(t, clicks))})`);
        put(ring, "translate", `${fixed(at[0], 2)}px ${fixed(at[1], 2)}px`);
        click(ring, t, last < 0 ? 1e9 : last);
        tiles.forEach((tile, i) => { const chosen = t >= clicks[i] && (i === clicks.length - 1 || t < clicks[i + 1]); if (tile.classList.contains("is-selected") !== chosen) tile.classList.toggle("is-selected", chosen); });
        SHAPES.forEach((shape, i) => {
          // The picked shape lifts out of its tile, travels on an arc and settles; its shadow is farthest in the
          // middle of the way and eases down as it lands.
          const { box, tile } = shape, begun = t - (shape.at + 0.08), k = go(begun, 0, FLIGHT), lifted = Math.sin(Math.PI * k), rest = [box.x + box.width / 2, box.y + box.height / 2];
          const middle = arc(tile, rest, k, 0.3), size = mix(GLYPH / box.height, 1, ease.easeOut(k)) * (1 + 0.1 * lifted), cast = casts[i], settled = go(begun, FLIGHT, 0.4, ease.easeOut);
          const move = (dx, dy, scale) => `translate(${fixed(middle[0] - rest[0] + dx, 2)}px, ${fixed(middle[1] - rest[1] + dy, 2)}px) scale(${fixed(scale, 4)})`;
          put(cast, "transformOrigin", `${rest[0]}px ${rest[1]}px`);
          put(cast, "transform", move(0, 5 + 22 * lifted, size * (1 + 0.05 * lifted)));
          put(cast, "opacity", begun <= 0 ? "0" : String(fixed(0.5 * Math.min(1, k * 8) * (1 - 0.4 * lifted) * (1 - settled))));
          // Where it lands, the screen lights up for a moment.
          const landed = clamp01((begun - FLIGHT + 0.12) / 0.8);
          put(landings[i], "opacity", String(fixed(0.55 * Math.sin(Math.PI * landed))));
          put(landings[i], "scale", String(fixed(mix(0.8, 1.12, ease.easeOut(landed)))));
          if (!marks) return;
          const { mark, node: drawn } = marks[i], line = fixed(mix(1.75, LINE, k) / size, 3);
          // (The engine writes its marks' opacity itself, so whether one shows is said another way.)
          put(drawn, "visibility", begun <= 0 ? "hidden" : "visible");
          put(drawn, "transformOrigin", `${rest[0]}px ${rest[1]}px`);
          put(drawn, "transform", k < 1 ? move(0, 0, size) : "none");
          // Its line is the glyph's at first and the mark's own at the end, whatever its size on the way.
          if (mark.style.width !== line) { mark.style.width = line; ink.render(mark); }
        });
        if (marks) {
          // The arrow is drawn from the Database to the Server: its far end runs ahead as the engine draws it.
          const { mark, node: drawn } = marks[2], k = fixed(go(t, 3.8, 0.7), 4), end = [mix(LINK.from[0], LINK.to[0], Math.max(k, 0.04)), mix(LINK.from[1], LINK.to[1], Math.max(k, 0.04))];
          put(drawn, "visibility", k > 0.03 ? "visible" : "hidden");
          if (width !== k) {
            width = k;
            mark.points = [LINK.from, end];
            mark.frame = { x: Math.min(LINK.from[0], end[0]), y: Math.min(LINK.from[1], end[1]), width: Math.abs(end[0] - LINK.from[0]), height: Math.abs(end[1] - LINK.from[1]) };
            ink.render(mark);
          }
        }
        // One swell of light behind the finished diagram. The camera goes in on it, drifts closer, and comes back;
        // while it is close, the tools window and the rest fall back.
        const lit = clamp01((t - 4.35) / 1.5);
        put(glow, "opacity", String(fixed(0.8 * Math.sin(Math.PI * lit))));
        put(glow, "scale", String(fixed(mix(0.75, 1.15, ease.easeOut(lit)))));
        const near = cam.film(t, 0.72, { x: -0.19, y: -0.046, s: 1.26, dx: -0.008, ds: 0.02 }, { in: 3.45, push: 1.6, out: 6.8, pull: 1.35 });
        put(veil, "opacity", String(fixed(near)));
      },
    };
  });
}

// Make it yours -----------------------------------------------------------------------------------------
// What every scene of the older kind shares: its root, one run at a time, and the calls the page makes.
function scene(container, name, options, make) {
  const root = mount(container, name, options);
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
    setGradient(name) { tint(root, name); parts.gradient?.(gradientOf(name)); },
    destroy() { if (!alive) return; clear(); alive = false; parts.destroy?.(); root.remove(); },
  };
}
// Settings > Toolbar on the engine's own toolbar: a tool goes, another comes, the color becomes another of the
// app's gradients, and the bar shrinks to its one button and opens again (the app's Shrink).
export function toolbarScene(container, options = {}) {
  return scene(container, "bar", options, (stage) => {
    const START = ["select", "rect", "diamond", "ellipse", "arrow", "line", "pen", "text", "highlight", "laser", "eraser", "effects"];
    const WITHOUT = START.filter((item) => item !== "line"), YOURS = [...WITHOUT, "counter"];
    // The second color is one of the app's gradients that is clearly another: Ocean after a warm one, else Sunset.
    const another = (name) => (breakPaletteFor(name) === "sunset" ? "ocean" : "sunset");
    let first = gradientOf(options.gradient).id, other = another(first);
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
      // The bar's first color from now on; a round that is running takes it at its next step.
      gradient: (preset) => { first = preset.id; other = another(first); ink.setGradient(first); },
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
