// A small web imitation of Annotator for the website concepts: the app's toolbar, and marks drawn over page content
// with the app's own rules. It is a demo, not the app.
// What a visitor draws is an element of the app's document (annotation-core.js, a port of AnnotationCore that
// test-core.mjs checks against reference/conformance), recognized by the app's Smart Draw (recognition.js) and
// painted as the app paints it (scene.js). Gestures, keys and undo follow apps/macos/Annotator/DrawingCanvas.swift.
// Marks that a page's script draws (perform) keep the hand-drawn look the concept pages were built with.
// The toolbar copies reference/ux-reference/macos/toolbar and apps/macos/Annotator/ToolbarView.swift; its icons are
// the Windows edition's (app-icons.js). What the web cannot do like the app opens "Want to see more?" instead.
//
// createInk(options) -> { layer, toolbar, state, styles, on, setMode, setTool, setColor, setCollapsed, select, undo, redo,
//   clear, perform, hideGhost, moveGhost, placeGhost, reflow, render, rectOf, box, add, remove, marks, setBoard,
//   setProperties, setAppearance, setHidden, setEnabled, setSpotlight, setHalo, setStyle, setGradient, more, fit, collapsed, enabled,
//   toolbarRect, shrink, expand, setVisibility, visibility, hidden, clickGhost, setRoom, room, shiftToolbar }
// Keys, as in the app: Control-Shift-2 is Draw (and shows everything first when it was hidden), Esc is Use apps
//   (after finishing text or cancelling an unfinished drawing), Control-2 hides the drawings and the toolbar, and
//   with everything hidden brings it back and starts drawing. Each emits `shortcut` ("draw", "use", "hide" or
//   "show", { key }). Control-2 and Control-Shift-2 do nothing while someone types in a field of the page.
// Options: surface, tool, color, palette, size, tools, collapsed, pillLabel, useLabel, toolbarParent, smart,
//   appearance ("dark" | "light" | "auto"), glass (true), toolbarPosition ("fixed" | "absolute"), interactive (true),
//   enabled (true), properties (whether the properties row starts open; open from 600 px wide, and remembered),
//   style (what drawing tools start with, for example { roughness: 2 }), moreHref ("#install"),
//   preset ("customized" | "factory": a toolbar someone has set up, or the app as installed), gradient (which of
//   the app's gradients is the stroke color at the start: "sunset", "lagoon", "ember", "spectrum", "dusk", "citrus",
//   "ocean", "orchid", "aurora", "cherry", "forest", "copper", "neon", "tropic" or "steel"), toolbarItems and
//   hiddenControls (the app's own names for the bar's arrangement, to override the preset's; setToolbarItems(ids)
//   changes the arrangement later, as Settings > Toolbar does, and `toolbarItems` reads it),
//   showToolbar (false: with interactive: false, still mount the toolbar, for a scripted picture of it),
//   visibility (how Hide drawings and tools moves: the app's "slide" by default, or "magic", "corners", "pop", "drop",
//   "focus", "fade", "none"), shrinkAfter (seconds with the pointer away before the toolbar shrinks to its one button,
//   the app's Shrink setting; never by default), toolbarMargin (room kept on each side of the toolbar, 8).
// Motion is the app's (app-motion.js): setCollapsed, shrink, expand and setHidden(hidden, { style, pill, animate })
//   return promises. perform(steps, { cursor, showTool, anchored, local, resting, signal }) also takes the steps
//   { mode }, { tool } or { pick }, { click }, { drag }, { type }, { hide } and { shrink } (see `story` below).
// Events: mark, edit, mode, tool, change, clear, smart, shortcut, collapse, board, properties, hide, enabled,
//   spotlight, halo, style, status, layout, and more (feature, event): event.preventDefault() keeps the page where it is.
// A mark is the app's element ({ id, tool, frame, points, text, style, rotation, ... }) and also answers to the names
// pages used before: type, x, y, w, h, x1, y1, x2, y2, cx, cy, head, color, size, n and anchor.
// Pages can read --ink-toolbar-height on the root element (the open toolbar's height) and set --ink-layer-z on
// .ink-layer (60 by default).
import { symbol, mark as brandMark, patternSample, roughnessSample, edgesSample, fillSample, routeSample, headSample, headPaths, pressureSample, APP_PALETTE, INK_KEYS } from "./toolbar-glyphs.js";
import { P, R, Tool, INK, Paint, GRADIENT_PRESETS, defaultStyle, makeElement, contains, segmentDistance, Stacking, DragConstraint, selectionGeometry, translate, resize, anchorLocations, anchorPoint, symbolFrame, CanvasDocument, LaserTrail, Motion } from "./annotation-core.js";
import { recognize as recognizeStroke, previewInput } from "./recognition.js";
import { drawOps, svgMarkup, laserOps, turn, snapOutline, align, resample } from "./scene.js";
import { css as CURVE, DURATIONS, toolbarMorph, DEFAULT_VISIBILITY, resolveVisibility, workspaceTiming, distanceOrder, gatheredPose, cornerRoute, usesPose, pose as visibilityPose,
  poseKeyframes, poseProgress, fadeKeyframes, travelFade, travelKeyframes, seedFor, returnSeed, magicFlight, magicTrail, toolbarPoof, absorbPulse, clickPulse } from "./app-motion.js";

const NS = "http://www.w3.org/2000/svg";
const reduceMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const f = (n) => Math.round(n * 10) / 10;
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const ease = (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
const sleep = (ms) => new Promise((done) => setTimeout(done, reduceMotion() ? 0 : ms));
const isMac = /mac|iphone|ipad/i.test(navigator.userAgentData?.platform || navigator.platform || "");
const LIBRARY_KEY = isMac ? "⌘L" : "Ctrl+L", RESUME_KEY = isMac ? "⌃⇧2" : "Ctrl+Shift+2", HIDE_KEY = isMac ? "⌃2" : "Ctrl+2";

function svgEl(name, attrs = {}) {
  const el = document.createElementNS(NS, name);
  for (const key in attrs) el.setAttribute(key, attrs[key]);
  return el;
}
function h(name, attrs = {}, html = "") {
  const el = document.createElement(name);
  for (const key in attrs) el.setAttribute(key, attrs[key]);
  if (html) el.innerHTML = html;
  return el;
}
// A seeded generator, so a sketchy shape keeps its wobble each time it is drawn again.
function seeded(seed) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
function tween(duration, step) {
  if (reduceMotion() || duration <= 0) { step(1); return Promise.resolve(); }
  return new Promise((done) => {
    const start = performance.now();
    const frame = (now) => {
      const t = clamp((now - start) / duration, 0, 1);
      step(t);
      if (t < 1) requestAnimationFrame(frame); else done();
    };
    requestAnimationFrame(frame);
  });
}

// Geometry ---------------------------------------------------------------------------------------
function spline(points) {
  if (points.length < 2) return "";
  let d = `M${f(points[0][0])} ${f(points[0][1])}`;
  for (let i = 0; i < points.length - 1; i++) {
    const p0 = points[i - 1] || points[i], p1 = points[i], p2 = points[i + 1], p3 = points[i + 2] || p2;
    d += `C${f(p1[0] + (p2[0] - p0[0]) / 6)} ${f(p1[1] + (p2[1] - p0[1]) / 6)} ${f(p2[0] - (p3[0] - p1[0]) / 6)} ${f(p2[1] - (p3[1] - p1[1]) / 6)} ${f(p2[0])} ${f(p2[1])}`;
  }
  return d;
}
function freehand(points) {
  if (points.length < 3) return spline(points);
  let d = `M${f(points[0][0])} ${f(points[0][1])}`;
  for (let i = 1; i < points.length - 1; i++) {
    const a = points[i], b = points[i + 1];
    d += `Q${f(a[0])} ${f(a[1])} ${f((a[0] + b[0]) / 2)} ${f((a[1] + b[1]) / 2)}`;
  }
  const last = points[points.length - 1];
  return `${d}L${f(last[0])} ${f(last[1])}`;
}
const jitter = (points, seed, amount) => {
  const r = seeded(seed), last = points.length - 1;
  return points.map(([x, y], i) => (i === 0 || i === last ? [x, y] : [x + (r() - 0.5) * amount, y + (r() - 0.5) * amount]));
};
// Hand-drawn outlines. `loose` is 1 for the look the demo always had; Wild draws them looser.
function ellipsePath({ x, y, w, h: height }, seed, loose = 1) {
  const r = seeded(seed), cx = x + w / 2, cy = y + height / 2, rx = w / 2, ry = height / 2;
  const start = -Math.PI * 0.8 + (r() - 0.5) * 0.5, turns = 1.07 + r() * 0.05, n = 28, points = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n, a = start + t * turns * Math.PI * 2, k = 1 + (t - 0.5) * 0.07 * loose + (r() - 0.5) * 0.02 * loose;
    points.push([cx + Math.cos(a) * rx * k, cy + Math.sin(a) * ry * k]);
  }
  return spline(points);
}
function loopPath(c, j) {
  let d = `M${f(c[0][0])} ${f(c[0][1])}`;
  for (let i = 0; i < c.length; i++) {
    const a = c[i], b = i === c.length - 1 ? [c[0][0] + j() * 0.6, c[0][1] - Math.abs(j()) - 1] : c[i + 1];
    d += `Q${f((a[0] + b[0]) / 2 + j() * 0.7)} ${f((a[1] + b[1]) / 2 + j() * 0.7)} ${f(b[0])} ${f(b[1])}`;
  }
  return d;
}
function rectPath({ x, y, w, h: height }, seed, loose = 1) {
  const r = seeded(seed), wob = Math.min(4, Math.min(w, height) * 0.05) * loose, j = () => (r() - 0.5) * 2 * wob;
  return loopPath([[x + j(), y + j()], [x + w + j(), y + j()], [x + w + j(), y + height + j()], [x + j(), y + height + j()]], j);
}
function diamondPath({ x, y, w, h: height }, seed, loose = 1) {
  const r = seeded(seed), wob = Math.min(4, Math.min(w, height) * 0.05) * loose, j = () => (r() - 0.5) * 2 * wob;
  return loopPath([[x + w / 2 + j(), y + j()], [x + w + j(), y + height / 2 + j()], [x + w / 2 + j(), y + height + j()], [x + j(), y + height / 2 + j()]], j);
}
// Clean outlines, as the app draws a shape with Roughness on Clean.
function cleanRect({ x, y, w, h: height }, round) {
  const r = round ? Math.min(14, w / 2, height / 2) : 0;
  if (!r) return `M${f(x)} ${f(y)}H${f(x + w)}V${f(y + height)}H${f(x)}Z`;
  return `M${f(x + r)} ${f(y)}H${f(x + w - r)}A${f(r)} ${f(r)} 0 0 1 ${f(x + w)} ${f(y + r)}V${f(y + height - r)}A${f(r)} ${f(r)} 0 0 1 ${f(x + w - r)} ${f(y + height)}H${f(x + r)}A${f(r)} ${f(r)} 0 0 1 ${f(x)} ${f(y + height - r)}V${f(y + r)}A${f(r)} ${f(r)} 0 0 1 ${f(x + r)} ${f(y)}Z`;
}
function cleanDiamond({ x, y, w, h: height }, round) {
  const c = [[x + w / 2, y], [x + w, y + height / 2], [x + w / 2, y + height], [x, y + height / 2]];
  if (!round) return `M${c.map((p) => `${f(p[0])} ${f(p[1])}`).join("L")}Z`;
  const side = Math.hypot(w, height) / 2, cut = Math.min(14, side / 2.5) / (side || 1);
  let d = "";
  for (let i = 0; i < 4; i++) {
    const before = c[(i + 3) % 4], at = c[i], after = c[(i + 1) % 4];
    const a = [at[0] + (before[0] - at[0]) * cut, at[1] + (before[1] - at[1]) * cut], b = [at[0] + (after[0] - at[0]) * cut, at[1] + (after[1] - at[1]) * cut];
    d += `${i ? "L" : "M"}${f(a[0])} ${f(a[1])}Q${f(at[0])} ${f(at[1])} ${f(b[0])} ${f(b[1])}`;
  }
  return `${d}Z`;
}
const cleanEllipse = ({ x, y, w, h: height }) =>
  `M${f(x)} ${f(y + height / 2)}A${f(w / 2)} ${f(height / 2)} 0 1 0 ${f(x + w)} ${f(y + height / 2)}A${f(w / 2)} ${f(height / 2)} 0 1 0 ${f(x)} ${f(y + height / 2)}Z`;

const HAND = { rect: rectPath, diamond: diamondPath, ellipse: ellipsePath };
const CLEAN = { rect: cleanRect, diamond: cleanDiamond, ellipse: cleanEllipse };
// A mark without a roughness of its own keeps the hand-drawn look scripted marks always had.
function outline(m) {
  if (m.rough === 0) return [CLEAN[m.type](m, m.round)];
  if (m.rough >= 4) return [HAND[m.type](m, m.seed, 2.2), HAND[m.type](m, m.seed + 7, 1.6)];
  return [HAND[m.type](m, m.seed)];
}
const curvePoint = (m, t) => [(1 - t) * (1 - t) * m.x1 + 2 * (1 - t) * t * m.cx + t * t * m.x2, (1 - t) * (1 - t) * m.y1 + 2 * (1 - t) * t * m.cy + t * t * m.y2];
function shaft(m) {
  if (!m.rough) return [`M${f(m.x1)} ${f(m.y1)}Q${f(m.cx)} ${f(m.cy)} ${f(m.x2)} ${f(m.y2)}`];
  const points = [0, 0.2, 0.4, 0.6, 0.8, 1].map((t) => curvePoint(m, t));
  const lines = [spline(jitter(points, m.seed, m.rough * 1.4))];
  if (m.rough >= 4) lines.push(spline(jitter(points, m.seed + 7, m.rough * 1.4)));
  return lines;
}
// The shaft and both ends. Marks made before arrowheads were a choice have an open head at the end.
function arrowParts(m) {
  const parts = shaft(m).map((d) => ({ d }));
  if (m.head === false) return parts;
  const total = Math.hypot(m.x2 - m.x1, m.y2 - m.y1), scale = (m.headScale ?? 0.75) / 0.75;
  const size = clamp(total * 0.2, 12, 14 + m.size * 2.4) * scale, end = m.end ?? "open", start = m.start ?? "none";
  const head = (kind, x, y, fromX, fromY) => {
    let dx = x - fromX, dy = y - fromY;
    const len = Math.hypot(dx, dy) || 1;
    dx /= len; dy /= len;
    if (kind !== "open") return headPaths(kind, x, y, dx, dy, size * 0.9);
    // The open head is drawn as it always was: two wings 0.5 rad off the line.
    const wing = (sign) => {
      const cos = Math.cos(0.5 * sign), sin = Math.sin(0.5 * sign);
      return [x - (dx * cos - dy * sin) * size, y - (dx * sin + dy * cos) * size];
    };
    const a = wing(1), b = wing(-1);
    return [{ d: `M${f(a[0])} ${f(a[1])}L${f(x)} ${f(y)}L${f(b[0])} ${f(b[1])}` }];
  };
  if (end !== "none") parts.push(...head(end, m.x2, m.y2, m.cx, m.cy));
  if (start !== "none") parts.push(...head(start, m.x1, m.y1, m.cx, m.cy));
  return parts;
}
// Ink pressure: one outline that is thin where the pen moved fast and at both ends, filled with the ink color.
function pressurePath(points, size) {
  const n = points.length, lengths = [0];
  for (let i = 1; i < n; i++) lengths.push(lengths[i - 1] + dist(points[i], points[i - 1]));
  const total = lengths[n - 1] || 1;
  let press = points.map((p, i) => clamp(1.5 - dist(points[Math.min(n - 1, i + 1)], points[Math.max(0, i - 1)]) / 22, 0.4, 1.45));
  press = press.map((v, i) => (v + (press[i - 1] ?? v) + (press[i + 1] ?? v) + (press[i - 2] ?? v) + (press[i + 2] ?? v)) / 5);
  const left = [], right = [];
  points.forEach((p, i) => {
    const a = points[Math.max(0, i - 1)], b = points[Math.min(n - 1, i + 1)], len = dist(a, b) || 1;
    const nx = -(b[1] - a[1]) / len, ny = (b[0] - a[0]) / len, t = lengths[i] / total;
    const half = Math.max(0.4, size * press[i] * (0.2 + 0.8 * Math.min(1, t * 7, (1 - t) * 7))) / 2;
    left.push([p[0] + nx * half, p[1] + ny * half]);
    right.push([p[0] - nx * half, p[1] - ny * half]);
  });
  return `${spline(left)}L${spline(right.reverse()).slice(1)}Z`;
}
function penParts(m) {
  if (m.type === "highlight") return [{ d: m.points.length === 2 ? spline(m.points) : freehand(m.points) }];
  if (m.pressure && m.points.length > 3) return [{ d: pressurePath(m.points, m.size * 1.5), fill: true, thin: true }];
  if (!m.rough) return [{ d: freehand(m.points) }];
  const parts = [{ d: freehand(jitter(m.points, m.seed, m.rough * 0.9)) }];
  if (m.rough >= 4) parts.push({ d: freehand(jitter(m.points, m.seed + 7, m.rough * 0.9)) });
  return parts;
}

// Dark or light text for a fill, for a scripted counter's number.
let colorProbe = null;
function textOn(color) {
  colorProbe ||= document.createElement("canvas").getContext("2d");
  colorProbe.fillStyle = "#000";
  colorProbe.fillStyle = color;
  const value = colorProbe.fillStyle;
  const rgb = value.startsWith("#") ? [1, 3, 5].map((i) => parseInt(value.slice(i, i + 2), 16)) : (value.match(/[\d.]+/g) || [0, 0, 0]).slice(0, 3).map(Number);
  return (rgb[0] * 299 + rgb[1] * 587 + rgb[2] * 114) / 1000 >= 150 ? "#10141c" : "#fff";
}

// Tools ------------------------------------------------------------------------------------------
// `size` is the icon's drawn size in points, chosen so its ink is as large as the Mac symbol's in the reference pictures.
const TOOL_LIST = [
  ["select", "Select", "1", "cursorarrow", 22.5, 0], ["rect", "Rectangle", "2", "rectangle", 24.5, 1], ["diamond", "Diamond", "3", "diamond", 24, 1],
  ["ellipse", "Ellipse", "4", "circle", 21, 1], ["arrow", "Arrow", "5", "arrow.up.right", 21.5, 1], ["line", "Line", "6", "line.diagonal", 23, 1],
  ["pen", "Pen", "7", "pencil", 17, 2], ["text", "Text", "8", "textformat", 24, 2], ["highlight", "Highlighter", "9", "highlighter", 21.5, 2],
  ["laser", "Laser pointer", "L", "laser.burst", 26, 2], ["eraser", "Eraser", "0", "eraser", 21, 3],
  ["counter", "Numbered counter", "N", "1.circle", 21, 5], ["blur", "Blur region", "U", "aqi.medium", 22, 4],
];
export const TOOLS = Object.fromEntries(TOOL_LIST.map(([id, label, key, name, size, family]) =>
  [id, { label, key, symbol: name, size, family, icon: symbol(name, 22) }]));
export const PALETTE = ["#ffb58c", "#ffe14d", "#35d0ba", "#5b8cff", "#ff5d5d", "#f4f6fb"];
// The bar as the app installs it: its tools in order, then Effects (WorkspacePreferences.toolbarItems).
const FACTORY_ITEMS = ["select", "rect", "diamond", "ellipse", "arrow", "line", "pen", "text", "highlight", "laser", "eraser", "effects"];
const DEFAULT_TOOLS = [...FACTORY_ITEMS.filter((id) => id !== "effects"), "blur", "counter"];
// Bar buttons that are not tools of the demo: Effects, which stands for the last effect, and Pixelate.
const BAR_EXTRA = {
  effects: { label: "Effects", key: "S", symbol: "camera.filters", size: 25, family: 4, title: "Effects (S) — Blur, Pixelate or Distort" },
  pixelate: { label: "Pixelate region", key: "X", symbol: "square.grid.3x3.fill", size: 22, family: 4, more: "effects" },
};
// ShortcutAction.toolbarChoices: the buttons people can add beside the tools, with the app's names, help and symbols.
// The last entry is what "Want to see more?" says for one the demo does not have.
const ACTIONS = {
  spotlight: ["Spotlight", "Turn the spotlight on or off.", "flashlight.on.fill", 19, "focus"],
  cursorHalo: ["Cursor halo", "Turn the ring around your pointer on or off.", "circle.dotted", 19, "focus"],
  pointerLens: ["Pointer lens", "Turn the zoom lens under your pointer on or off.", "plus.magnifyingglass", 19, "zoom"],
  smartDraw: ["Smart draw", "Turn shape recognition for the pen on or off.", "wand.and.stars", 19, null],
  shareRegion: ["Share region", "Choose part of a display and open a window to share in your call.", "rectangle.on.rectangle", 20, "share"],
  focusMenu: ["Spotlight menu", "Spotlight, cursor halo and cleanup, in one menu.", "light.beacon.max", 20, "focus"],
  zoomMenu: ["Zoom menu", "Pointer lens, screen zoom and zoom levels, in one menu.", "arrow.up.left.and.down.right.magnifyingglass", 19, "zoom"],
};
// ToolbarControl: the bar's own buttons, each of which Settings can turn off.
const CONTROLS = ["library", "board", "mode", "undo", "redo", "clearAll", "properties", "settings", "hide"];
// How the demo starts. "factory" is the app's plain defaults (Style.defaults), as in the reference captures.
// "customized" is a toolbar someone has used and set up: the owner asked for the demo to open that way, so these are
// deliberate differences from the app's factory defaults. Every one is a state the app itself can be left in, none
// is invented:
//   the stroke color of shapes, lines, arrows, the pen and text is Sunset, the gradient the app gives a brand-new
//   profile (ToolStyleDefaults.firstUseGradient); the counter, highlighter and laser keep their own colors, as there;
//   Arrow is set to Curved, and Roughness to Wild, its strongest choice, for shapes, lines and arrows. The pen
//   starts Clean: the owner asked for freehand strokes to follow the hand as drawn;
//   the pen, shapes, lines and arrows remember a width of 4 points;
//   Diamond and Line are taken off the bar, Numbered counter and the Smart draw button are added to it, and the
//   Redo button is turned off (Settings > Toolbar; compare reference/ux-reference/macos/toolbar/added-buttons and
//   hidden-buttons). Keys still choose the tools that left the bar, as in the app.
const PRESETS = {
  factory: { gradient: null, curved: false, roughness: 0, width: 3, items: FACTORY_ITEMS, hidden: [] },
  customized: { gradient: "sunset", curved: true, roughness: 6, width: 4, hidden: ["redo"],
    items: ["select", "rect", "ellipse", "arrow", "pen", "text", "highlight", "laser", "eraser", "effects", "counter", "action.smartDraw"] },
};
// The web demo's names for two tools differ from the app's.
const APP_TOOL = { rect: "rectangle", highlight: "highlighter" }, WEB_TOOL = { rectangle: "rect", highlighter: "highlight" };
const appTool = (id) => APP_TOOL[id] || id, webTool = (tool) => WEB_TOOL[tool] || tool;
const BOXED = ["rect", "diamond", "ellipse", "blur"];
const BOARDS = { desk: ["Desk", "Desktop", "desktopcomputer"], white: ["White", "Whiteboard", "rectangle"], black: ["Black", "Blackboard", "rectangle.fill"] };
const NOTE = {
  select: "Select an object to edit its properties.",
  blur: "Blurred details can still be readable. Remove private info before you share.",
  counter: "Click to add sequential numbered markers.",
  laser: "Drag to point. The trail fades on its own.",
};
// The name the properties give a selected mark.
const MARK_LABEL = { rect: "Rectangle", diamond: "Diamond", ellipse: "Ellipse", arrow: "Arrow", line: "Line", pen: "Pen", text: "Text", highlight: "Highlighter", counter: "Numbered counter", blur: "Blur region" };
// What the app does and this page does not: one plain line for each, shown under "Want to see more?".
const MORE = {
  app: "Annotator has more than 40 tools. This page shows a few.",
  library: "Tool library: more than 40 tools, shapes and diagram icons.",
  board: "Whiteboard and blackboard backgrounds.",
  settings: "Settings: change the pointer, the keys and the toolbar.",
  effects: "Pixelate and Distort need your real screen.",
  fill: "Fill colors, hatching and rounded corners.",
  heads: "Arrowheads at both ends, from dots to diamonds.",
  pressure: "Ink pressure: lines that get thinner and thicker as you draw.",
  text: "Fonts, sizes, bold and alignment for text.",
  label: "Text inside a shape: double-click a shape and type.",
  wheel: "A color wheel, for any color you like.",
  eyedropper: "Pick a color from anywhere on your screen.",
  focus: "Spotlight and a halo around your pointer.",
  zoom: "A zoom lens under your pointer, and screen zoom.",
  share: "Share one part of your screen in a call.",
};
const STORE = "annotator.demo.properties";
const ACCENT = "#0a84ff";
const svgCursor = (svg, side, fallback) => `url("data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="${side}" height="${side}" viewBox="0 0 ${side} ${side}">${svg}</svg>`)}") ${side / 2} ${side / 2}, ${fallback}`;
// ToolCursorFactory.rotationCursor: three quarters of a ring with an arrowhead, white under black.
const ROTATION_CURSOR = svgCursor([["#fff", 4], ["#000", 2]].map(([ink, width]) =>
  `<path d="M23.1 8.9A10 10 0 1 0 23.1 23.1M23 23L29 22L26 28" fill="none" stroke-linecap="round" stroke-linejoin="round" stroke="${ink}" stroke-width="${width}"/>`).join(""), 32, "grab");

export function createInk(options = {}) {
  const surface = options.surface || document.body;
  // A scripted-only instance has no toolbar, listens to no keys and never draws by hand.
  const interactive = options.interactive !== false;
  // A disabled instance keeps its marks but hears no keys, cannot draw and hides its toolbar (setEnabled).
  let enabled = options.enabled !== false;
  const palette = options.palette || PALETTE;
  const toolIds = options.tools || DEFAULT_TOOLS;
  const scriptSize = options.size || 5;
  const preset = PRESETS[options.preset] || PRESETS.customized;
  // The stroke color at the start: the gradient the page names, else a page's own solid color, else the preset's
  // gradient if it has one. A name the app does not have falls back to the first-use gradient, with one warning.
  const named = (name) => {
    const found = GRADIENT_PRESETS.find((choice) => choice.id === String(name).toLowerCase());
    if (!found) console.warn(`Annotator demo: there is no gradient called "${name}". Using "sunset". The app's gradients are ${GRADIENT_PRESETS.map((choice) => choice.id).join(", ")}.`);
    return (found || GRADIENT_PRESETS[0]).gradient;
  };
  const wanted = options.gradient ?? (options.color || options.palette ? null : preset.gradient);
  const gradient = wanted ? named(wanted) : null;
  const stroke = gradient ? gradient.colors[0] : options.color || (options.palette ? palette[0] : INK.coral);
  const moreHref = options.moreHref || "#install";
  // Each tool keeps its own style, as in the app (ToolStyleDefaults.swift), starting from the preset unless the page asks.
  const inked = { color: stroke, strokeGradient: gradient && { colors: [...gradient.colors], angle: gradient.angle } };
  const asked = options.style?.roughness ?? options.style?.rough;
  const drawn = { ...inked, width: options.size || preset.width, roughness: asked ?? preset.roughness, pattern: options.style?.pattern || "solid" };
  const own = (id, extra) => ({ ...defaultStyle(appTool(id)), ...extra });
  const styles = {
    rect: own("rect", drawn), diamond: own("diamond", drawn), ellipse: own("ellipse", drawn), line: own("line", drawn), pen: own("pen", { ...drawn, roughness: asked ?? 0 }),
    arrow: own("arrow", { ...drawn, curved: options.style?.curved ?? preset.curved }),
    text: own("text", inked), counter: own("counter", { color: options.color || (options.palette ? palette[0] : INK.coral) }), highlight: own("highlight"), laser: own("laser", { fade: LaserTrail.defaultLifetime }),
    eraser: own("eraser"), blur: own("blur", { effectAmount: 18 }), spotlight: { radius: 130 },
  };
  let remembered = null;
  // A scripted-only instance is a picture: it does not take up what a visitor chose on the page's own toolbar.
  if (options.interactive !== false) try { remembered = localStorage.getItem(STORE); } catch { /* storage is off */ }
  const state = {
    mode: "use", tool: options.tool || "pen", color: stroke, smart: options.smart !== false,
    board: "desk", spotlight: false, halo: false, hidden: false, properties: remembered ? remembered === "1" : options.properties ?? innerWidth >= 600,
  };
  const els = new Map(), inks = new Map(), anchors = new Map(), listeners = {};
  const uid = Math.random().toString(36).slice(2, 8);
  let seq = 1;

  const layer = h("div", { class: "ink-layer", "data-mode": "use", "data-tool": state.tool, "data-board": "desk" });
  const board = h("div", { class: "ink-board" }), under = h("div", { class: "ink-under" }), spot = h("div", { class: "ink-spot" }), over = h("div", { class: "ink-over" });
  const svg = svgEl("svg", { class: "ink-svg", "aria-hidden": "true" }), defs = svgEl("defs"), marksG = svgEl("g"), fx = svgEl("g"), laserG = svgEl("g");
  const ui = svgEl("svg", { class: "ink-svg ink-ui", "aria-hidden": "true" }), halo = h("div", { class: "ink-halo" });
  const ghost = h("div", { class: "ink-ghost", "aria-hidden": "true" }, '<svg viewBox="0 0 22 26" width="22" height="26"><path d="M2 1.5V21l5.2-4.7 3.7 8.2 3.6-1.6-3.6-8h6.9z" fill="#fff" stroke="#0b101a" stroke-width="1.5" stroke-linejoin="round"/></svg>');
  // Scripted highlighter strokes are drawn on a plane of their own so they can blend with the page (see ink.css).
  const plane = h("div", { class: "ink-layer ink-blend", "aria-hidden": "true" }), planeSvg = svgEl("svg", { class: "ink-svg" });
  plane.append(planeSvg);
  svg.append(defs, marksG, fx, laserG);
  layer.append(board, under, spot, svg, over, halo, ui, ghost);
  if (getComputedStyle(surface).position === "static") surface.style.position = "relative";
  surface.append(layer, plane);

  const emit = (name, ...details) => (listeners[name] || []).forEach((fn) => fn(...details));
  // True while a real key press or click of the visitor's is being handled: what a script does is never theirs.
  let byVisitor = false;
  const visitorActs = (event) => { if (event.isTrusted) { byVisitor = true; queueMicrotask(() => { byVisitor = false; }); } };
  // Where the surface's top left corner is on the screen: every drawing is measured from it. The layer covers the
  // surface, and with room (setRoom) reaches past it or stops short of it; the corner stays where it is, so nothing
  // that was drawn moves.
  let room = null;
  const origin = () => {
    const r = layer.getBoundingClientRect();
    return room ? { left: r.left + room.left, top: r.top + room.top, right: r.right - room.right, bottom: r.bottom - room.bottom, width: r.width - room.left - room.right, height: r.height - room.top - room.bottom } : r;
  };
  // How far the drawing layer reaches past its surface on each side, in pixels (negative stops short of the edge),
  // and how round its corners are. A page's growing stage sets it (stage.js); null or all zero is the surface itself.
  function setRoom(next) {
    const sides = next && { top: Number(next.top) || 0, right: Number(next.right) || 0, bottom: Number(next.bottom) || 0, left: Number(next.left) || 0 };
    room = sides && (sides.top || sides.right || sides.bottom || sides.left) ? sides : null;
    for (const el of [layer, plane]) {
      for (const side of ["top", "right", "bottom", "left"]) { if (room) el.style.setProperty(`--ink-room-${side}`, `${room[side]}px`); else el.style.removeProperty(`--ink-room-${side}`); }
      if (room && next.radius) el.style.setProperty("--ink-room-radius", typeof next.radius === "number" ? `${next.radius}px` : next.radius); else el.style.removeProperty("--ink-room-radius");
    }
  }
  const point = (e) => { const o = origin(); return [e.clientX - o.left, e.clientY - o.top]; };

  // Marks ----------------------------------------------------------------------------------------
  // A mark is one of the app's elements. Its prototype adds the names pages used before the demo had the app's model.
  const mid = (m) => m.points[Math.floor(m.points.length / 2)];
  const ends = (m) => [m.points[0], m.points[m.points.length - 1]];
  // The control point of the bow a scripted arrow is drawn with: its middle point lies on the curve.
  const control = (m) => { const [a, b] = ends(m); return m.points.length === 3 ? [2 * mid(m)[0] - (a[0] + b[0]) / 2, 2 * mid(m)[1] - (a[1] + b[1]) / 2] : P.mul(P.add(a, b), 0.5); };
  const setEnd = (m, index, axis, value) => { if (!m.points.length) return; m.points[index < 0 ? m.points.length - 1 : 0][axis] = value; m.frame = R.enclosing(m.points); };
  const setBow = (m, axis, value) => {
    const [a, b] = ends(m), on = 0.25 * a[axis] + 0.5 * value + 0.25 * b[axis];
    if (m.points.length === 2) m.points.splice(1, 0, P.mul(P.add(a, b), 0.5));
    mid(m)[axis] = on; m.frame = R.enclosing(m.points);
  };
  const Mark = {
    get type() { return this.tool === "line" ? "arrow" : webTool(this.tool); },
    get head() { return this.tool === "line" ? false : undefined; },
    get x() { return this.tool === "counter" && this.legacy ? this.frame.x + this.frame.width / 2 : this.frame.x; },
    set x(v) { this.frame.x = this.tool === "counter" && this.legacy ? v - this.frame.width / 2 : v; },
    get y() { return this.tool === "counter" && this.legacy ? this.frame.y + this.frame.height / 2 : this.frame.y; },
    set y(v) { this.frame.y = this.tool === "counter" && this.legacy ? v - this.frame.height / 2 : v; },
    get w() { return this.frame.width; }, set w(v) { this.frame.width = v; },
    get h() { return this.frame.height; }, set h(v) { this.frame.height = v; },
    get x1() { return this.points[0]?.[0]; }, set x1(v) { setEnd(this, 0, 0, v); },
    get y1() { return this.points[0]?.[1]; }, set y1(v) { setEnd(this, 0, 1, v); },
    get x2() { return this.points[this.points.length - 1]?.[0]; }, set x2(v) { setEnd(this, -1, 0, v); },
    get y2() { return this.points[this.points.length - 1]?.[1]; }, set y2(v) { setEnd(this, -1, 1, v); },
    get cx() { return this.points.length ? control(this)[0] : undefined; }, set cx(v) { setBow(this, 0, v); },
    get cy() { return this.points.length ? control(this)[1] : undefined; }, set cy(v) { setBow(this, 1, v); },
    get color() { return this.style.color; }, set color(v) { this.style.color = v; },
    get size() { return this.style.width; }, set size(v) { this.style.width = v; },
    // What a scripted mark follows is a DOM element or a function, which a history snapshot cannot hold. It is kept
    // by mark id; the mark only remembers whether it still follows it.
    get anchor() { return this.anchored ? anchors.get(this.id) || null : null; },
    set anchor(value) { if (value) { anchors.set(this.id, value); this.anchored = true; } else if (this.anchored) this.anchored = false; },
  };
  const copyAnchor = (a) => (a ? { elementID: a.elementID, relative: [a.relative[0], a.relative[1]] } : null);
  function cloneMark(m) {
    const copy = Object.assign(Object.create(Mark), m);
    return Object.assign(copy, { frame: { ...m.frame }, points: m.points.map((p) => [p[0], p[1]]), pressures: [...m.pressures], style: { ...m.style },
      startAnchor: copyAnchor(m.startAnchor), endAnchor: copyAnchor(m.endAnchor), legacy: m.legacy ? { ...m.legacy } : m.legacy });
  }
  const doc = new CanvasDocument({ clone: cloneMark, newID: () => seq++ });
  Object.defineProperties(state, {
    marks: { enumerable: true, get: () => doc.elements },
    selected: { enumerable: true, get: () => (doc.selection.size === 1 ? [...doc.selection][0] : null) },
  });
  const byId = (id) => doc.byID(id);
  const current = (m) => byId(m?.id ?? m);
  function newMark(tool, { frame, points, text, style }) {
    return Object.assign(Object.create(Mark), makeElement({ id: seq++, tool, frame, points, text, style, seed: (Math.random() * 0xffffffff) >>> 0 || 1 }));
  }
  // A mark in the words pages and scripts use ({ type: "rect", x, y, w, h, color, size, ... }), as an element that
  // keeps the hand-drawn look (`legacy` holds what that look needs).
  function place(m, g) {
    if (g.points) { m.points = g.points.map((p) => [p[0], p[1]]); if (g.size) m.style.width = g.size; }
    else if (g.x1 !== undefined) {
      const a = [g.x1, g.y1], b = [g.x2, g.y2], cx = g.cx ?? (a[0] + b[0]) / 2, cy = g.cy ?? (a[1] + b[1]) / 2;
      const bent = Math.hypot(cx - (a[0] + b[0]) / 2, cy - (a[1] + b[1]) / 2) > 2;
      // A scripted arrow has a middle point, so a visitor can bend it by dragging that point; a line has its two ends.
      m.points = bent || m.tool === "arrow" ? [a, [0.25 * a[0] + 0.5 * cx + 0.25 * b[0], 0.25 * a[1] + 0.5 * cy + 0.25 * b[1]], b] : [a, b];
    } else if (m.tool === "counter") m.frame = { x: g.x - 17, y: g.y - 17, width: 34, height: 34 };
    else if (m.tool === "text") { m.frame.x = g.x; m.frame.y = g.y; }
    else m.frame = { x: g.x, y: g.y, width: Math.max(0, g.w || 0), height: Math.max(0, g.h || 0) };
    if (m.points.length) m.frame = R.enclosing(m.points);
  }
  function scripted({ anchor, type, color, size, text = "", n, points, x, y, w, h: height, x1, y1, x2, y2, cx, cy, head, seed, pattern, id: _id, ...extras }) {
    const tool = type === "arrow" ? (head === false ? "line" : "arrow") : type === "pixelate" ? "blur" : appTool(type);
    // A step that names no color gets the selected one, gradient and all.
    const ink = color ? { color } : { color: state.color, strokeGradient: state.gradient && { colors: [...state.gradient.colors], angle: state.gradient.angle } };
    const m = newMark(tool, { text, style: { ...defaultStyle(tool), ...ink, width: size || scriptSize, pattern: pattern || "solid" } });
    m.legacy = { seed: seed ?? (Math.random() * 1e9) | 0, ...(type === "pixelate" ? { effect: "pixelate" } : {}), ...extras };
    if (n != null) m.n = n;
    place(m, { points, x, y, w, h: height, x1, y1, x2, y2, cx, cy });
    if (anchor) m.anchor = anchor;
    return m;
  }
  function legacyView(m) {
    const f = m.frame, v = { ...m.legacy, id: m.id, type: m.type, color: m.style.color, size: m.style.width, pattern: m.style.pattern, text: m.text, n: m.n, x: m.x, y: m.y, w: f.width, h: f.height, points: m.points };
    if (Tool.isConnector(m.tool)) Object.assign(v, { x1: m.x1, y1: m.y1, x2: m.x2, y2: m.y2, cx: m.cx, cy: m.cy, head: m.head });
    return v;
  }
  // Where a scripted highlighter stroke is drawn: the blending plane.
  function strokeHost(m) {
    if (!(m.legacy && m.tool === "highlighter")) return els.get(m.id);
    let g = inks.get(m.id);
    if (!g) {
      g = planeSvg.appendChild(svgEl("g", { class: "ink-mark ink-highlight", "data-id": m.id }));
      inks.set(m.id, g);
      plane.classList.add("has-ink");
    }
    return g;
  }
  function dropInk(id) {
    inks.get(id)?.remove();
    inks.delete(id);
    plane.classList.toggle("has-ink", inks.size > 0);
  }

  // Styles ---------------------------------------------------------------------------------------
  // What the properties edit (WorkspaceModel.effectiveTool): the selected marks' tool when they share one, "select"
  // for a mixed selection, else the current tool.
  const selectedMarks = () => doc.elements.filter((m) => doc.selection.has(m.id));
  function effectiveKey() {
    const chosen = selectedMarks(), editable = chosen.filter((m) => !m.locked), list = editable.length ? editable : chosen;
    if (!list.length) return state.tool;
    return list.every((m) => m.tool === list[0].tool) ? webTool(list[0].tool) : "select";
  }
  const SETTINGS = new Set(["fade", "radius"]);
  function read(prop) {
    const chosen = selectedMarks(), first = chosen.find((m) => !m.locked) || chosen[0];
    if (first && !SETTINGS.has(prop)) return first.style[prop] ?? defaultStyle(first.tool)[prop];
    const key = state.tool === "select" ? "pen" : state.tool;
    return (styles[key] || styles.pen)[prop];
  }
  // A scripted mark keeps its hand-drawn look while only its color, width or pattern change.
  const LEGACY_KEEPS = new Set(["color", "width", "pattern", "opacity"]);
  function modernize(m) {
    if (Tool.isConnector(m.tool) && m.points.length >= 3) m.style.curved = true;
    if (m.tool === "highlighter") m.style.width = Math.max(0.25, m.style.width * 0.88);
    m.legacy = null;
  }
  // WorkspaceModel.applyStyle: a change goes to the selected marks and becomes what the next mark of that tool gets.
  // A slider drag is one undo step (beginStyleEdit and finishStyleEdit).
  let styleEdit = false;
  const announce = () => { selectedMarks().forEach((m) => emit("edit", m)); emit("change", doc.elements); };
  // While the color picker is open everything it changes is one undo step, as in the app.
  function applyStyle(prop, mutate, live, supports = () => true) {
    const key = effectiveKey(), batch = live || popKind === "color";
    if (key !== "select" && styles[key]) mutate(styles[key]);
    if (doc.selection.size) {
      if (batch && !styleEdit && !doc.isEditing) { doc.begin(); styleEdit = true; }
      doc.editSelection((m) => {
        if (!supports(m) || (key === "select" && Tool.isProtection(m.tool))) return;
        if (m.legacy && !LEGACY_KEEPS.has(prop) && m.tool !== "text" && m.tool !== "counter") modernize(m);
        mutate(m.style);
      });
      paint();
      if (!batch) { if (styleEdit) finishStyleEdit(); else announce(); }
    }
    syncStyle();
    if (!live) refreshToolbar();
  }
  function write(prop, value, live) {
    const key = effectiveKey();
    if (SETTINGS.has(prop)) {
      if (styles[state.tool]) styles[state.tool][prop] = value;
      syncStyle();
      if (!live) refreshToolbar();
    } else applyStyle(prop, (style) => { style[prop] = value; }, live);
    emit("style", { tool: key, prop, value });
  }
  // The picker's choices (InspectorColorPalette): a gradient, a solid color, and the paint's opacity. Blur has no paint.
  const paintable = (m) => !Tool.isProtection(m.tool);
  function setGradient(gradient) {
    applyStyle("color", (style) => Paint.setGradient(style, gradient), false, paintable);
    emit("style", { tool: effectiveKey(), prop: "strokeGradient", value: gradient });
  }
  function setSolid(color, useColorAlpha = false) {
    applyStyle("color", (style) => Paint.setColor(style, color, useColorAlpha), false);
    emit("style", { tool: effectiveKey(), prop: "color", value: read("color") });
  }
  function finishStyleEdit() {
    if (!styleEdit) return;
    styleEdit = false;
    const before = doc.generation;
    if (doc.isEditing) doc.commit();
    if (doc.generation !== before) announce();
  }
  function syncStyle() {
    // The color a script's mark gets when its step names none: the selected mark's, else the tool's, else the pen's.
    const chosen = selectedMarks().find(paintable), from = chosen?.style || (["blur", "eraser", "select", "spotlight"].includes(state.tool) ? styles.pen : styles[state.tool] || styles.pen);
    state.color = from.color;
    state.gradient = from.strokeGradient || null;
    layer.style.setProperty("--ink-color", state.color);
    layer.style.setProperty("--ink-spot-size", `${styles.spotlight.radius}px`);
    cursorShown = state.mode === "draw" ? toolCursor() : "";
    layer.style.cursor = cursorShown;
  }
  // ToolCursorFactory: an arrow to select, an I-beam for text, the laser's dot, the footprint of a pen, highlighter or
  // eraser, and a crosshair for everything that is dragged out.
  function toolCursor() {
    const tool = appTool(state.tool), s = styles[state.tool] || styles.pen, width = s.width;
    if (tool === "select") return "default";
    if (tool === "text") return "text";
    if (tool === "laser") {
      const radius = Math.min(250, Math.max(2, width / 2)), side = Math.ceil(radius * 2 + 12), c = side / 2;
      return svgCursor(`<circle cx="${c}" cy="${c}" r="${radius + 5}" fill="${s.color}" fill-opacity=".28"/><circle cx="${c}" cy="${c}" r="${radius + 1.5}" fill="#fff"/><circle cx="${c}" cy="${c}" r="${radius}" fill="${s.color}"/>`, side, "crosshair");
    }
    if (!Tool.isFreehand(tool) && tool !== "eraser") return "crosshair";
    const footprint = Math.min(500, tool === "highlighter" ? width * 5 : tool === "eraser" ? width * 2 : width), side = Math.min(520, Math.max(24, Math.ceil(footprint) + 10));
    // Browsers ignore pointer pictures above 128 pixels and show the crosshair instead.
    const height = Math.max(4, footprint), nib = tool === "highlighter" ? Math.min(500, Math.max(4, width * 1.6)) : height, c = side / 2;
    const shape = (attrs) => (tool === "highlighter" ? `<rect x="${c - nib / 2}" y="${c - height / 2}" width="${nib}" height="${height}" ${attrs}/>` : `<ellipse cx="${c}" cy="${c}" rx="${nib / 2}" ry="${height / 2}" ${attrs}/>`);
    const cross = (attrs) => `<path d="M${c - 3} ${c}H${c + 3}M${c} ${c - 3}V${c + 3}" ${attrs}/>`;
    return svgCursor(shape('fill="none" stroke="#fff" stroke-width="3"') + shape(`fill="${tool === "eraser" ? "none" : "#000"}" fill-opacity=".12" stroke="#000" stroke-width="1"`)
      + (tool === "eraser" ? "" : cross('stroke="#fff" stroke-width="2"') + cross('stroke="#000" stroke-width=".7"')), side, "crosshair");
  }

  // Rendering ------------------------------------------------------------------------------------
  // Marks shown at 22% while the eraser has collected them or Smart Draw offers a neater shape.
  const eraseIDs = new Set();
  let smartPreview = null, releasePreview = null, morph = null, linear = null, editor = null, gesture = null, marquee = null, hints = [];
  function legacyParts(v) {
    if (v.type === "arrow") return arrowParts(v);
    if (HAND[v.type]) return outline(v).map((d, i) => ({ d, shape: i === 0 }));
    return penParts(v);
  }
  // A hatched fill is an SVG pattern of the mark's own, in its fill color.
  function hatch(v) {
    const id = `ink-hatch-${uid}-${v.id}`;
    let el = defs.querySelector(`[data-for="${v.id}"]`);
    if (!v.fill || !v.fillStyle || v.fillStyle === "solid") { el?.remove(); return null; }
    if (!el) el = defs.appendChild(svgEl("pattern", { "data-for": v.id, patternUnits: "userSpaceOnUse", width: 9, height: 9 }));
    el.id = id;
    el.innerHTML = `<path d="M-2 11L11 -2M-2 2L2 -2M7 11L11 7${v.fillStyle === "crosshatch" ? "M-2 -2L11 11M7 -2L11 2M-2 7L2 11" : ""}" stroke="${v.fill}" stroke-width="1.4" fill="none"/>`;
    return `url(#${id})`;
  }
  // GradientPainter: one gradient across the mark's frame, for its line, its dashes and its arrowheads alike. The
  // mark's <linearGradient> lives in the layer's defs; text and counters use the same gradient as a CSS background.
  function gradientPaint(m, frame = m.frame) {
    let el = defs.querySelector(`[data-grad="${m.id}"]`);
    const g = m.style.strokeGradient;
    if (!g) { el?.remove(); return null; }
    if (!el) el = defs.appendChild(svgEl("linearGradient", { "data-grad": m.id, id: `ink-grad-${uid}-${m.id}`, gradientUnits: "userSpaceOnUse" }));
    const { start, end } = Paint.line(g, frame), colors = g.colors.join();
    for (const [name, value] of [["x1", start[0]], ["y1", start[1]], ["x2", end[0]], ["y2", end[1]]]) el.setAttribute(name, f(value));
    if (el.dataset.colors !== colors) {
      el.dataset.colors = colors;
      el.innerHTML = g.colors.map((color, i) => `<stop offset="${i / (g.colors.length - 1)}" stop-color="${color}"/>`).join("");
    }
    return `url(#${el.id})`;
  }
  // A gradient as a CSS background, at the paint's opacity.
  const gradientCss = (style) => Paint.css({ ...style.strokeGradient, colors: style.strokeGradient.colors.map((color) => Paint.withAlpha(color, Paint.alpha(style.color))) });
  // Text and counters take their size from what is in them, as the app sizes text from its content.
  function measure(m, el) {
    const w = el.offsetWidth, height = el.offsetHeight;
    if (!w || !height || (m.frame.width === w && m.frame.height === height)) return;
    if (m.tool === "counter") { m.frame.x += (m.frame.width - w) / 2; m.frame.y += (m.frame.height - height) / 2; }
    m.frame.width = w; m.frame.height = height;
  }
  function renderLegacy(m, el) {
    const v = legacyView(m);
    if (v.type === "blur") {
      Object.assign(el.style, { left: `${v.x}px`, top: `${v.y}px`, width: `${v.w}px`, height: `${v.h}px` });
      el.classList.toggle("is-pixelate", v.effect === "pixelate");
      if (v.amount == null) { el.style.removeProperty("--ink-blur"); el.style.removeProperty("--ink-cell"); }
      else { el.style.setProperty("--ink-blur", `${v.amount / 2}px`); el.style.setProperty("--ink-cell", `${Math.max(4, v.amount * 0.7)}px`); }
    } else if (v.type === "counter") {
      Object.assign(el.style, { left: `${v.x}px`, top: `${v.y}px`, background: m.style.strokeGradient ? gradientCss(m.style) : v.color, opacity: v.opacity ?? (m.style.opacity < 1 ? m.style.opacity : "") });
      el.style.setProperty("--ink-counter-text", textOn(v.color));
      el.textContent = v.n;
      measure(m, el);
    } else if (v.type === "text") {
      Object.assign(el.style, {
        left: `${v.x}px`, top: `${v.y}px`, color: v.color, fontSize: v.fontSize ? `${v.fontSize}px` : "", minHeight: v.fontSize ? `${v.fontSize}px` : "",
        fontWeight: v.bold ? "800" : "", fontFamily: v.font === "mono" ? 'ui-monospace, "SF Mono", "Cascadia Mono", Menlo, Consolas, monospace' : "", textAlign: v.align && v.align !== "left" ? v.align : "",
        backgroundImage: m.style.strokeGradient ? gradientCss(m.style) : "",
      });
      if (editor?.el !== el && el.textContent !== v.text) el.textContent = v.text;
      measure(m, el);
    } else {
      const host = strokeHost(m), list = legacyParts(v), visible = [...host.querySelectorAll(".ink-stroke")], width = v.type === "highlight" ? v.size * 4.4 : v.size;
      const dash = v.pattern === "dashed" ? `${v.size * 5} ${v.size * 3}` : v.pattern === "dotted" ? `0.01 ${v.size * 3}` : "";
      const paint = v.fill ? hatch(v) || v.fill : (hatch(v), null), ink = gradientPaint(m) || v.color;
      list.forEach((part, i) => {
        const path = visible[i] || host.appendChild(svgEl("path", { class: "ink-stroke" }));
        path.setAttribute("d", part.d);
        path.setAttribute("stroke", ink);
        path.setAttribute("stroke-width", part.thin ? 0.6 : width);
        if (dash && !part.fill && !part.hollow) path.setAttribute("stroke-dasharray", dash); else path.removeAttribute("stroke-dasharray");
        path.style.fill = part.fill ? ink : part.shape && paint ? paint : "";
        path.style.fillOpacity = part.shape && paint && paint === v.fill ? 0.4 : "";
      });
      visible.slice(list.length).forEach((path) => path.remove());
      host.style.opacity = m.style.opacity < 1 ? m.style.opacity : "";
      if (host !== el) host.setAttribute("transform", turn(m));
    }
  }
  // SceneRenderer.draw for what a visitor draws. An arrow being placed point by point shows a line to the pointer.
  function renderApp(m, el) {
    const s = m.style;
    if (m.tool === "counter") {
      // A filled circle with its number in white: 56% of the circle's height, 12% down from its top.
      const f0 = m.frame;
      Object.assign(el.style, { left: `${f0.x}px`, top: `${f0.y}px`, width: `${f0.width}px`, height: `${f0.height}px`, fontSize: `${f0.height * 0.56}px`, paddingTop: `${f0.height * 0.12}px`,
        background: s.strokeGradient ? gradientCss(s) : s.color });
      if (el.textContent !== m.text) el.textContent = m.text;
    } else if (m.tool === "text") {
      Object.assign(el.style, { left: `${m.frame.x}px`, top: `${m.frame.y}px`, color: Paint.solid(s.color), fontSize: `${s.fontSize}px`, fontWeight: s.bold ? "600" : "400", textAlign: s.alignment,
        backgroundImage: s.strokeGradient ? gradientCss(s) : "" });
      // Text takes the size of what is in it, until its box is resized: then it wraps inside the box, as in the app.
      Object.assign(el.style, { width: m.sized ? `${m.frame.width}px` : "", height: m.sized ? `${m.frame.height}px` : "" });
      if (editor?.el !== el && el.textContent !== m.text) el.textContent = m.text;
      if (!m.sized) measure(m, el);
    } else if (Tool.isProtection(m.tool)) {
      const f = m.frame;
      Object.assign(el.style, { left: `${Math.floor(f.x)}px`, top: `${Math.floor(f.y)}px`, width: `${Math.ceil(f.x + f.width) - Math.floor(f.x)}px`, height: `${Math.ceil(f.y + f.height) - Math.floor(f.y)}px` });
      el.style.setProperty("--ink-blur", `${s.effectAmount ?? 18}px`);
    } else {
      let shown = m;
      if (linear?.id === m.id && linear.preview && !P.same(m.points[m.points.length - 1], linear.preview)) { const points = [...m.points, linear.preview]; shown = { ...m, points, frame: R.enclosing(points) }; }
      const ops = shown.points.length === 1 && Tool.isConnector(m.tool) ? [] : drawOps(shown, gradientPaint(m, shown.frame)), paths = [...el.children];
      ops.forEach((op, i) => {
        const path = paths[i] || el.appendChild(svgEl("path", { class: "ink-op" }));
        const attrs = op.fill ? { d: op.d, fill: op.color, "fill-opacity": op.opacity, stroke: "none" }
          : { d: op.d, fill: "none", stroke: op.color, "stroke-opacity": op.opacity, "stroke-width": op.width, "stroke-linecap": op.cap, "stroke-linejoin": op.join, "stroke-dasharray": op.dash || "none" };
        for (const key in attrs) path.setAttribute(key, attrs[key]);
      });
      paths.slice(ops.length).forEach((path) => path.remove());
    }
  }
  function render(m) {
    if (byId(m.id) !== m) return null;
    const html = m.tool === "text" || m.tool === "counter" || Tool.isProtection(m.tool), kind = m.legacy ? "legacy" : "app";
    let el = els.get(m.id);
    if (el && el.dataset.kind !== kind) { discard(m.id); el = null; }
    if (!el) {
      el = html ? h("div") : svgEl("g");
      el.dataset.id = m.id; el.dataset.kind = kind;
      (Tool.isProtection(m.tool) ? under : html ? over : marksG).append(el);
      els.set(m.id, el);
    }
    const editing = el.classList.contains("is-editing"), fresh = el.classList.contains("is-new");
    el.setAttribute("class", `ink-mark ink-${m.type}${m.legacy ? "" : " is-app"}${m.sized ? " is-sized" : ""}${m.tool === "text" && m.style.strokeGradient ? " is-gradient" : ""}${editing ? " is-editing" : ""}${fresh ? " is-new" : ""}`);
    // Whole-object opacity; text is also as see-through as its color.
    const strength = m.style.opacity * (m.tool === "text" && !m.legacy ? Paint.alpha(m.style.color) : 1);
    el.style.opacity = !m.legacy && html && strength < 1 ? strength : "";
    if (m.legacy) renderLegacy(m, el); else renderApp(m, el);
    if (html) el.style.transform = m.rotation ? `rotate(${m.rotation}rad)` : "";
    else if (m.rotation) el.setAttribute("transform", turn(m)); else el.removeAttribute("transform");
    // SceneRenderer draws a collected or about-to-be-replaced mark at 22%; a mark the morph is drawing waits for it.
    const faded = !Tool.isProtection(m.tool) && (eraseIDs.has(m.id) || (state.smart && (smartPreview?.id === m.id || releasePreview?.id === m.id)));
    if (morph?.id === m.id || faded) (m.legacy ? strokeHost(m) : el).style.opacity = morph?.id === m.id ? morph.reveal : 0.22;
    return el;
  }
  function discard(id) {
    els.get(id)?.remove();
    els.delete(id);
    dropInk(id);
    defs.querySelector(`[data-for="${id}"]`)?.remove();
    defs.querySelector(`[data-grad="${id}"]`)?.remove();
  }
  // Draws the document again: every mark, in order, then the selection.
  function paint() {
    const live = new Set(doc.elements.map((m) => m.id));
    for (const id of [...els.keys()]) if (!live.has(id)) discard(id);
    let created = false;
    for (const m of doc.elements) { created ||= !els.has(m.id); render(m); }
    // A mark that came back with Undo is drawn at its old place in the pile, not on top.
    if (created && !editor) doc.elements.forEach((m) => { const el = els.get(m.id); if (el.nextSibling) el.parentNode.append(el); });
    overlay();
  }
  // The enclosing box of a mark as drawn, turned marks included.
  function box(mark) {
    const m = current(mark) || mark, c = R.center(m.frame);
    const corners = (Tool.isConnector(m.tool) || Tool.isFreehand(m.tool)) && m.points.length ? m.points : R.corners(m.frame);
    const b = R.enclosing(m.rotation ? corners.map((p) => P.rotated(p, c, m.rotation)) : corners);
    return { x: b.x, y: b.y, w: b.width, h: b.height };
  }

  // SceneRenderer.drawSelection, the marquee, the eraser's boxes and the attach hints of DrawingCanvas.draw.
  function overlay() {
    const n = (v) => Math.round(v * 100) / 100, turned = (m, body) => `<g${m.rotation ? ` transform="${turn(m)}"` : ""}>${body}</g>`;
    const frame = (f, inset, extra = "") => `<rect x="${n(f.x - inset)}" y="${n(f.y - inset)}" width="${n(f.width + inset * 2)}" height="${n(f.height + inset * 2)}" fill="none" stroke="${ACCENT}" stroke-width="1" ${extra}/>`;
    const knob = (p) => `<circle cx="${n(p[0])}" cy="${n(p[1])}" r="4" fill="#fff" stroke="${ACCENT}" stroke-width="1"/>`;
    let out = "";
    if (state.mode === "draw" && !state.hidden) {
      const chosen = selectedMarks().filter((m) => !eraseIDs.has(m.id) && m.id !== editor?.id), group = selectionGeometry(chosen, new Set(chosen.map((m) => m.id)));
      const one = (m, handles) => {
        const f = m.frame, connector = Tool.isConnector(m.tool);
        let body = connector && !m.locked && handles ? "" : frame(f, 3, m.locked ? 'stroke-dasharray="3 3"' : "");
        if (!m.locked && handles) {
          body += (connector ? m.points : R.corners(f)).map(knob).join("");
          if (!Tool.isProtection(m.tool) && !connector) body += `<path d="M${n(f.x + f.width / 2)} ${n(f.y)}v-24" stroke="${ACCENT}" stroke-width="1"/><circle cx="${n(f.x + f.width / 2)}" cy="${n(f.y - 24)}" r="6" fill="#fff"/>`;
        }
        return turned(m, body);
      };
      for (const m of chosen) out += one(m, !group);
      if (group) out += one({ frame: group.bounds, tool: group.canRotate ? "rectangle" : "cover", points: [], rotation: 0, locked: false }, true);
    }
    if (marquee) out += frame(marquee, 0, 'stroke-dasharray="4 3"');
    for (const m of doc.elements) if (eraseIDs.has(m.id)) out += turned(m, frame(m.frame, 4, 'stroke-dasharray="3 3" stroke-opacity=".75"'));
    for (const id of new Set(hints.map((hint) => hint.elementID))) {
      const shape = byId(id);
      if (!shape) continue;
      const f = symbolFrame(shape);
      out += turned(shape, `<rect x="${n(f.x - 6)}" y="${n(f.y - 6)}" width="${n(f.width + 12)}" height="${n(f.height + 12)}" rx="6" fill="${ACCENT}" fill-opacity=".08" stroke="${ACCENT}" stroke-opacity=".9" stroke-width="2"/>`);
      for (const location of anchorLocations(shape)) {
        const p = anchorPoint(shape, location), picked = hints.some((hint) => hint.elementID === id && P.same(hint.relative, location));
        out += `<circle cx="${n(p[0])}" cy="${n(p[1])}" r="${picked ? 5.5 : 3.5}" fill="${picked ? ACCENT : "#fff"}" fill-opacity="${picked ? 1 : 0.9}" stroke="${picked ? "#fff" : ACCENT}" stroke-width="1.5"/>`;
      }
    }
    ui.innerHTML = out;
  }

  // History --------------------------------------------------------------------------------------
  // DrawingCanvas.hasActiveEdit
  const hasActiveEdit = () => Boolean(gesture || editor || linear);
  function settle() {
    paint();
    syncStyle();
    refreshToolbar();
    emit("change", doc.elements);
  }
  // WorkspaceModel.undo: an unfinished drawing is cancelled first.
  function undo() {
    finishStyleEdit();
    if (hasActiveEdit()) cancelGesture(); else doc.undo();
    settle();
  }
  function redo() {
    if (hasActiveEdit()) return;
    finishStyleEdit();
    doc.redo();
    settle();
  }
  // Adds a mark in the words pages use. Each one is a step Undo can take back.
  function add(mark, by = "user") {
    const m = scripted(mark);
    doc.add(m);
    render(m);
    if (by === "user") emit("mark", m);
    return m;
  }
  function remove(id) {
    if (!byId(id)) return;
    doc.elements = doc.elements.filter((m) => m.id !== id);
    doc.keepSelection();
    doc.resolveBindings();
    paint();
    refreshProperties();
  }
  function select(ids) {
    doc.select(ids == null ? [] : Array.isArray(ids) ? ids : [ids]);
    overlay();
    syncStyle();
    refreshProperties();
  }
  // WorkspaceModel.clearCanvas: everything goes at once, and Undo brings it back.
  function clear() {
    if (!doc.elements.length) return;
    commitText();
    cancelGesture();
    doc.clearAll();
    settle();
    emit("clear");
  }

  // Modes and tools ------------------------------------------------------------------------------
  function setMode(mode) {
    if (state.mode === mode || (mode === "draw" && !(interactive && enabled))) return;
    if (mode === "use") { finishStyleEdit(); commitText(); finishLinear(); cancelGesture(); layer.classList.remove("has-pointer"); closePop(); }
    state.mode = mode;
    layer.dataset.mode = mode;
    document.documentElement.classList.toggle("ink-drawing", mode === "draw");
    // While you draw, the app's overlay has the keys and the app under it gets none. When the visitor starts drawing,
    // a field of the page that was being typed in lets go, so the number and letter keys choose tools instead of
    // typing. A page's script never takes the keys from anyone.
    const typing = document.activeElement;
    if (mode === "draw" && byVisitor && typing && typing !== document.body && surface.contains(typing) && !layer.contains(typing) && !toolbar.contains(typing)) typing.blur();
    if (mode === "draw" && state.hidden) setHidden(false);
    syncStyle();
    overlay();
    refreshToolbar();
    emit("mode", mode);
  }
  // WorkspaceModel.choose: text is kept, an arrow being placed is finished, and nothing stays selected unless the
  // tool is Select.
  function setTool(tool, silent) {
    if (!TOOLS[tool]) return;
    finishStyleEdit();
    commitText();
    finishLinear();
    cancelGesture();
    closePop();
    state.tool = tool;
    layer.dataset.tool = tool;
    if (tool !== "select") doc.select([]);
    if (!silent) setMode("draw");
    syncStyle();
    overlay();
    refreshToolbar();
    emit("tool", tool);
  }
  // After a shape, line, arrow or blur is drawn the app selects it and goes back to Select.
  function useSelect() {
    if (state.tool === "select") return;
    state.tool = "select";
    layer.dataset.tool = "select";
    emit("tool", "select");
  }
  // The page's color for every drawing tool, and for the selected marks. Highlighter and laser keep their own.
  function setColor(color) {
    for (const key of ["rect", "diamond", "ellipse", "arrow", "line", "pen", "text", "counter"]) Paint.setColor(styles[key], color, true);
    if (doc.selection.size) { doc.editSelection((m) => { if (paintable(m)) Paint.setColor(m.style, color, true); }); paint(); emit("change", doc.elements); }
    syncStyle();
    refreshToolbar();
  }
  // Whiteboard, blackboard, Spotlight and Cursor halo are for a page's script; visitors are sent to the app for them.
  function setBoard(name) {
    if (!BOARDS[name] || state.board === name) return;
    state.board = name;
    layer.dataset.board = name;
    refreshToolbar();
    emit("board", name);
  }
  function setLight(name, on) {
    state[name] = on;
    layer.classList.toggle(`has-${name}`, on);
    if (!on && !state.spotlight && !state.halo) layer.classList.remove("has-pointer");
    refreshToolbar();
    emit(name, on);
  }
  function setProperties(shown) {
    state.properties = shown;
    try { localStorage.setItem(STORE, shown ? "1" : "0"); } catch { /* storage is off */ }
    refreshToolbar();
    emit("properties", shown);
  }

  // Text (DrawingCanvas.startText, installTextEditor and commitTextIfNeeded) ------------------------
  const onTextInput = () => { const m = editor && byId(editor.id); if (m) { if (!m.sized) measure(m, editor.el); overlay(); } };
  function startText(p) {
    const target = Stacking.frontmost(p, doc.elements);
    // In the app, text on a shape becomes the shape's label. The demo has no labels.
    if (target && Tool.isShape(target.tool)) { more("label", rail.querySelector('[data-tool="text"]') || propertiesButton); return; }
    doc.begin();
    const existing = [...doc.elements].reverse().find((m) => m.tool === "text" && !m.locked && contains(p, m));
    const m = existing || newMark("text", { frame: { x: p[0], y: p[1], width: 32, height: styles.text.fontSize * 1.5 }, style: styles.text });
    if (!existing) doc.add(m);
    doc.select([m.id]);
    paint();
    const el = els.get(m.id);
    editor = { id: m.id, el, initial: existing ? cloneMark(existing) : null };
    el.contentEditable = "plaintext-only";
    if (el.contentEditable !== "plaintext-only") el.contentEditable = "true";
    el.classList.add("is-editing");
    el.setAttribute("aria-label", "Annotation text. Escape or Command Return finishes editing.");
    el.addEventListener("input", onTextInput);
    const focus = () => {
      if (editor?.el !== el) return;
      el.focus({ preventScroll: true });
      getSelection().selectAllChildren(el);
      getSelection().collapseToEnd();
      el.addEventListener("blur", commitText, { once: true });
    };
    // The press that started the text is still being handled; the caret is placed once it is over.
    requestAnimationFrame(focus);
    overlay();
    syncStyle();
    refreshToolbar();
  }
  function commitText() {
    if (!editor) return;
    const { id, el, initial } = editor;
    editor = null;
    el.removeEventListener("input", onTextInput);
    el.removeEventListener("blur", commitText);
    el.contentEditable = "false";
    el.classList.remove("is-editing");
    el.removeAttribute("aria-label");
    const value = (el.innerText || "").replace(/\r/g, "").replace(/\n$/, ""), m = byId(id);
    const unchanged = Boolean(initial && m) && value === initial.text && JSON.stringify(m.style) === JSON.stringify(initial.style);
    if (!m || (!initial && !value.trim()) || unchanged) {
      // Empty new text, or text left as it was, is no step to undo.
      doc.cancel();
      if (unchanged) doc.select([id]);
      settle();
      return;
    }
    doc.update(id, (t) => { t.text = value; });
    doc.commit();
    paint();
    emit(initial ? "edit" : "mark", byId(id));
    settle();
  }

  // Gestures (DrawingCanvas.mouseDown, mouseDragged and mouseUp) -------------------------------------
  const now = () => performance.now() / 1000;
  const trail = new LaserTrail();
  let laserFrame = 0, previewFrame = 0, lastPress = null, cursorShown = "";
  const restore = (m, original) => Object.assign(m, cloneMark(original));
  const originalsOf = (marks) => new Map(marks.map((m) => [m.id, cloneMark(m)]));
  const setCursor = (cursor) => { if (cursor !== cursorShown) { cursorShown = cursor; layer.style.cursor = cursor; } };
  function status(words) {
    statusLine.textContent = words || "";
    statusLine.hidden = !words;
    emit("status", words || "");
  }
  function cancelGesture() {
    if (trail.isDrawing) { trail.release(null, now()); runLaser(); }
    const changed = hasActiveEdit() || marquee || eraseIDs.size || smartPreview || doc.isEditing;
    cancelSmartPreview();
    finishTransitions();
    styleEdit = false;
    doc.cancel();
    gesture = null; marquee = null; eraseIDs.clear();
    if (editor) { const { el } = editor; editor = null; el.removeEventListener("blur", commitText); el.contentEditable = "false"; el.classList.remove("is-editing"); }
    linear = null; hints = [];
    status("");
    if (changed) { paint(); syncStyle(); refreshToolbar(); }
  }
  // Return or a double-click ends an arrow that was placed point by point; it is selected, like any new shape.
  function finishLinear() {
    if (!linear) return;
    const id = linear.id, m = byId(id);
    if (!m || m.points.length < 2) { cancelGesture(); return; }
    const [first, last] = ends(m), start = doc.nearestAnchor(first, id), end = doc.nearestAnchor(last, id);
    doc.update(id, (el) => { el.startAnchor = start; el.endAnchor = end; });
    doc.commit();
    doc.select([id]);
    linear = null; hints = [];
    status("");
    useSelect();
    emit("mark", byId(id));
    settle();
  }
  function isOverRotationHandle(p) {
    if (state.mode !== "draw" || state.tool !== "select") return false;
    const group = selectionGeometry(doc.elements, doc.selection);
    if (group) return group.canRotate && P.dist(p, [group.bounds.x + group.bounds.width / 2, group.bounds.y - 24]) <= 18;
    const m = doc.selection.size === 1 ? selectedMarks()[0] : null;
    if (!m || m.locked || Tool.isProtection(m.tool) || Tool.isConnector(m.tool)) return false;
    const c = R.center(m.frame);
    return P.dist(P.rotated(p, c, -m.rotation), [c[0], m.frame.y - 24]) <= 18;
  }
  // The same rules as on release, so the highlight never promises a link that will not happen.
  function refreshAttachHints(p) {
    let next = [];
    if (state.mode === "draw") {
      if (gesture?.kind === "create") {
        const m = byId(gesture.id);
        if (m && Tool.isConnector(m.tool)) next = [doc.nearestAnchor(gesture.start, m.id), doc.nearestAnchor(m.points[m.points.length - 1] || p, m.id)];
      } else if (gesture?.kind === "vertex") {
        const m = byId(gesture.id), { index, original } = gesture;
        if (m && Tool.isConnector(original.tool) && (index === 0 || index === original.points.length - 1) && m.points[index]) next = [doc.nearestAnchor(m.points[index], m.id)];
      } else if (!gesture) {
        const m = linear && byId(linear.id);
        if (m) next = [m.points[0] ? doc.nearestAnchor(m.points[0], m.id) : null, doc.nearestAnchor(p, m.id)];
        else if (Tool.isConnector(appTool(state.tool))) next = [doc.nearestAnchor(p, null)];
      }
    }
    next = next.filter(Boolean);
    if (JSON.stringify(next) === JSON.stringify(hints)) return false;
    hints = next;
    return true;
  }
  // The eraser collects whole marks under it; they go when the button is let go.
  function collectErase(p) {
    const front = Stacking.frontmost(p, doc.elements);
    if (front && Tool.isProtection(front.tool)) { if (!front.locked) eraseIDs.add(front.id); return; }
    for (const m of doc.elements) if (!Tool.isProtection(m.tool) && !m.locked && contains(p, m, styles.eraser.width)) eraseIDs.add(m.id);
  }
  function straightHighlight(id, start, p) {
    const finish = DragConstraint.snappedLineEnd(start, p);
    doc.update(id, (m) => { m.points = P.same(finish, start) ? [start] : [start, finish]; m.pressures = []; m.frame = R.from(start, finish); });
  }
  const clicksAt = (e, p) => {
    const count = lastPress && e.timeStamp - lastPress.time < 500 && P.dist(p, lastPress.p) < 5 ? lastPress.count + 1 : 1;
    lastPress = { time: e.timeStamp, p, count };
    return count;
  };
  layer.addEventListener("pointerdown", (e) => {
    if (state.mode !== "draw" || e.button !== 0) return;
    if (editor && editor.el.contains(e.target)) return;
    e.preventDefault();
    closePop();
    finishTransitions();
    finishStyleEdit();
    commitText();
    // A press on the canvas takes the keys back from the toolbar, as the app makes its canvas first responder.
    if (toolbar.contains(document.activeElement)) document.activeElement.blur();
    layer.setPointerCapture(e.pointerId);
    const p = point(e), tool = appTool(state.tool), clicks = clicksAt(e, p);
    if (tool === "laser") {
      trail.lifetime = styles.laser.fade;
      trail.press(p, now(), styles.laser.color, styles.laser.width);
      runLaser();
      return;
    }
    const hit = Stacking.frontmost(p, doc.elements);
    if (tool === "counter") {
      // DrawingCanvas.mouseDown: a click places the next number, in a circle 36 points across.
      const next = doc.elements.reduce((most, m) => (m.tool === "counter" ? Math.max(most, parseInt(m.legacy ? m.n : m.text, 10) || 0) : most), 0) + 1;
      const m = newMark("counter", { frame: { x: p[0] - 18, y: p[1] - 18, width: 36, height: 36 }, text: String(next), style: styles.counter });
      doc.add(m);
      paint();
      emit("mark", m);
      settle();
      return;
    }
    if (linear) {
      if (clicks >= 2) { finishLinear(); return; }
      doc.update(linear.id, (m) => { if (!P.same(m.points[m.points.length - 1], p)) m.points.push(p); m.frame = R.enclosing(m.points); });
      paint();
      return;
    }
    if (clicks >= 2 && tool === "select" && hit && hit.tool === "arrow" && !hit.locked && doc.selection.has(hit.id)) {
      // A double-click on a selected arrow removes the point under it, or adds one where the line was pressed.
      const inner = hit.points.findIndex((q, i) => i > 0 && i < hit.points.length - 1 && P.dist(q, p) <= 8);
      // A scripted arrow's bow has one middle point; with more it becomes one of the app's curved arrows.
      const curve = (m) => { m.anchored = false; if (m.legacy && m.points.length > 3) modernize(m); };
      if (inner > 0) doc.update(hit.id, (m) => { m.points.splice(inner, 1); m.frame = R.enclosing(m.points); curve(m); });
      else if (hit.points.length >= 2) {
        let nearest = 0, least = Infinity;
        for (let i = 0; i < hit.points.length - 1; i++) { const d = segmentDistance(p, hit.points[i], hit.points[i + 1]); if (d < least) { least = d; nearest = i; } }
        doc.update(hit.id, (m) => { m.points.splice(nearest + 1, 0, p); m.frame = R.enclosing(m.points); curve(m); });
      }
      emit("edit", byId(hit.id));
      settle();
      return;
    }
    if (tool === "text" || (clicks === 2 && tool === "select" && (!hit || hit.tool === "text" || Tool.isShape(hit.tool)))) { startText(p); return; }
    if (tool === "select") {
      const group = selectionGeometry(doc.elements, doc.selection);
      if (group) {
        const originals = originalsOf(selectedMarks().filter((m) => !m.locked)), corner = group.corners.findIndex((q) => P.dist(q, p) <= 9);
        if (corner >= 0) { doc.begin(); gesture = { kind: "resizeSelection", anchor: group.corners[(corner + 2) % 4], corner: group.corners[corner], originals }; return; }
        if (isOverRotationHandle(p)) {
          const center = R.center(group.bounds);
          doc.begin(); gesture = { kind: "rotateSelection", center, angle: Math.atan2(p[1] - center[1], p[0] - center[0]), originals };
          setCursor(ROTATION_CURSOR);
          return;
        }
      }
      const one = doc.selection.size === 1 ? selectedMarks().find((m) => !m.locked) : null;
      if (one) {
        const vertex = Tool.isConnector(one.tool) ? one.points.findIndex((q) => P.dist(q, p) <= 8) : -1;
        if (vertex >= 0) { doc.begin(); gesture = { kind: "vertex", id: one.id, index: vertex, original: cloneMark(one) }; return; }
        const center = R.center(one.frame), local = P.rotated(p, center, -one.rotation), corners = R.corners(one.frame), corner = corners.findIndex((q) => P.dist(local, q) <= 9);
        if (corner >= 0) { doc.begin(); gesture = { kind: "resize", id: one.id, original: cloneMark(one), anchor: corners[(corner + 2) % 4] }; return; }
        if (!Tool.isProtection(one.tool) && !Tool.isConnector(one.tool) && P.dist(local, [center[0], one.frame.y - 24]) <= 18) {
          doc.begin(); gesture = { kind: "rotate", id: one.id, center, angle: Math.atan2(p[1] - center[1], p[0] - center[0]), rotation: one.rotation };
          setCursor(ROTATION_CURSOR);
          return;
        }
      }
      const keeps = !e.shiftKey && (hit ? doc.selection.has(hit.id) : Boolean(group && R.contains(group.bounds, p)));
      if (!keeps) doc.selectAt(p, e.shiftKey);
      doc.begin();
      gesture = doc.selection.size ? { kind: "move", origin: p, originals: originalsOf(selectedMarks()), moved: false } : { kind: "marquee", origin: p };
    } else if (tool === "eraser") {
      doc.begin(); gesture = { kind: "erase" }; collectErase(p);
    } else {
      doc.begin();
      const m = newMark(tool, { frame: R.from(p, p), points: Tool.isConnector(tool) ? [p, p] : [], style: styles[state.tool] });
      doc.add(m);
      if (Tool.isFreehand(tool)) doc.appendSample(m.id, p, 1);
      gesture = { kind: "create", id: m.id, start: p };
    }
    paint();
    syncStyle();
    refreshProperties();
  });
  function drag(e, p) {
    const g = gesture;
    if (g.kind === "create") {
      const m = byId(g.id);
      if (!m) return;
      if (m.tool === "highlighter" && e.shiftKey) straightHighlight(g.id, g.start, p);
      else if (Tool.isFreehand(m.tool)) {
        const events = e.getCoalescedEvents?.() || [];
        for (const event of events.length ? events : [e]) { const q = point(event); if (!P.same(q, m.points[m.points.length - 1])) doc.appendSample(g.id, q, 1); }
        requestSmartPreview(g.id);
      } else {
        const connector = Tool.isConnector(m.tool), finish = e.shiftKey ? (connector ? DragConstraint.snappedLineEnd(g.start, p) : DragConstraint.squareEnd(g.start, p)) : p;
        doc.update(g.id, (el) => { el.frame = R.from(g.start, finish); if (connector) el.points = [g.start, finish]; });
      }
    } else if (g.kind === "move") {
      g.moved ||= !P.same(p, g.origin);
      doc.editSelection((m) => { const original = g.originals.get(m.id); if (!original) return; restore(m, original); translate(m, P.sub(p, g.origin)); if (g.moved) m.anchored = false; });
    } else if (g.kind === "resize") {
      const o = g.original, center = R.center(o.frame);
      let local = P.rotated(p, center, -o.rotation);
      if (e.shiftKey && o.frame.width > 0 && o.frame.height > 0) {
        const delta = P.sub(local, g.anchor), scale = Math.max(Math.abs(delta[0]) / o.frame.width, Math.abs(delta[1]) / o.frame.height);
        const xSign = delta[0] === 0 ? center[0] - g.anchor[0] : delta[0], ySign = delta[1] === 0 ? center[1] - g.anchor[1] : delta[1];
        local = P.add(g.anchor, [(xSign < 0 ? -1 : 1) * o.frame.width * scale, (ySign < 0 ? -1 : 1) * o.frame.height * scale]);
      }
      const frame = R.from(g.anchor, local), fixed = P.rotated(g.anchor, center, o.rotation), moved = P.rotated(g.anchor, R.center(frame), o.rotation);
      doc.update(g.id, (m) => { restore(m, o); resize(m, frame); translate(m, P.sub(fixed, moved)); m.anchored = false; if (m.tool === "text") m.sized = true; });
    } else if (g.kind === "rotate") {
      const rotation = g.rotation + Math.atan2(p[1] - g.center[1], p[0] - g.center[0]) - g.angle;
      doc.update(g.id, (m) => { m.rotation = rotation; m.anchored = false; });
      setCursor(ROTATION_CURSOR);
    } else if (g.kind === "resizeSelection") {
      const diagonal = P.sub(g.corner, g.anchor), squared = P.dot(diagonal, diagonal);
      if (!(squared > 0)) return;
      const scale = Math.max(0.01, P.dot(P.sub(p, g.anchor), diagonal) / squared);
      doc.editSelection((m) => {
        const o = g.originals.get(m.id);
        if (!o) return;
        restore(m, o);
        const corner = P.add(g.anchor, P.mul(P.sub(R.origin(o.frame), g.anchor), scale));
        resize(m, { x: corner[0], y: corner[1], width: o.frame.width * scale, height: o.frame.height * scale });
        if (m.tool === "text") m.style.fontSize = Math.min(1024, Math.max(4, o.style.fontSize * scale));
        m.anchored = false;
      });
    } else if (g.kind === "rotateSelection") {
      const angle = Math.atan2(p[1] - g.center[1], p[0] - g.center[0]) - g.angle;
      doc.editSelection((m) => {
        const o = g.originals.get(m.id);
        if (!o) return;
        restore(m, o);
        const center = R.center(o.frame);
        if (Tool.isConnector(o.tool)) {
          m.points = o.points.map((q) => P.rotated(P.rotated(q, center, o.rotation), g.center, angle));
          m.rotation = 0;
          m.frame = R.enclosing(m.points);
        } else { translate(m, P.sub(P.rotated(center, g.center, angle), center)); m.rotation = o.rotation + angle; }
        m.anchored = false;
      });
      setCursor(ROTATION_CURSOR);
    } else if (g.kind === "vertex") {
      const o = g.original, last = o.points.length - 1, isEnd = g.index === 0 || g.index === last;
      let scaled = false;
      if (e.shiftKey && o.points.length >= 2 && isEnd) {
        // Shift on an end stretches the whole line from its other end, keeping its direction.
        const anchor = o.points[g.index === 0 ? last : 0], direction = P.sub(o.points[g.index], anchor), squared = P.dot(direction, direction);
        if (squared > 0) {
          const center = R.center(o.frame), scale = P.dot(P.sub(P.rotated(p, center, -o.rotation), anchor), direction) / squared;
          const points = o.points.map((q) => P.add(anchor, P.mul(P.sub(q, anchor), scale))), frame = R.enclosing(points);
          const shift = P.sub(P.rotated(anchor, center, o.rotation), P.rotated(anchor, R.center(frame), o.rotation));
          doc.update(g.id, (m) => { restore(m, o); m.points = points; m.frame = frame; translate(m, shift); if (g.index === 0) m.startAnchor = null; else m.endAnchor = null; m.anchored = false; });
          scaled = true;
        }
      }
      if (!scaled) doc.update(g.id, (m) => { m.points[g.index] = p; m.frame = R.enclosing(m.points); if (g.index === 0) m.startAnchor = null; if (g.index === m.points.length - 1) m.endAnchor = null; m.anchored = false; });
    } else if (g.kind === "marquee") marquee = R.from(g.origin, p);
    else if (g.kind === "erase") collectErase(p);
    refreshAttachHints(p);
    if (g.kind === "create" && Tool.isFreehand(byId(g.id)?.tool)) { render(byId(g.id)); overlay(); } else paint();
  }
  layer.addEventListener("pointermove", (e) => {
    if (state.mode !== "draw") return;
    const p = point(e);
    if (trail.isDrawing) {
      const events = e.getCoalescedEvents?.() || [];
      for (const event of events.length ? events : [e]) trail.move(point(event), now());
      runLaser();
      return;
    }
    if (gesture) { drag(e, p); return; }
    // DrawingCanvas.mouseMoved: the pointer picture, the line to the pointer of an arrow being placed, attach hints.
    if (!e.buttons) setCursor(isOverRotationHandle(p) ? ROTATION_CURSOR : toolCursor());
    let redraw = refreshAttachHints(p);
    if (linear) { linear.preview = p; const m = byId(linear.id); if (m) render(m); redraw = true; }
    if (redraw) overlay();
  });
  function release(e) {
    if (layer.hasPointerCapture?.(e.pointerId)) layer.releasePointerCapture(e.pointerId);
    if (trail.isDrawing) { trail.release(point(e), now()); runLaser(); return; }
    if (!gesture) return;
    const g = gesture, p = point(e), heldPreview = state.smart ? smartPreview : null;
    let erased = [], made = null;
    cancelSmartPreview();
    if (g.kind === "move") drag(e, p);
    else if (g.kind === "create") {
      const m = byId(g.id);
      if (m) {
        if (m.tool === "arrow" && P.dist(g.start, p) < 4) {
          // A click instead of a drag: the arrow is placed point by point.
          doc.update(g.id, (el) => { el.points = [g.start]; el.frame = R.from(g.start, g.start); });
          linear = { id: g.id, preview: p };
          gesture = null;
          status("Click to add arrow points. Return or double-click finishes; Escape cancels.");
          paint();
          refreshToolbar();
          return;
        }
        if (m.tool === "highlighter" && e.shiftKey) straightHighlight(g.id, g.start, p);
        else if (Tool.isFreehand(m.tool) && !P.same(m.points[m.points.length - 1], p)) doc.appendSample(g.id, p, 1);
        if (Tool.isConnector(m.tool)) {
          const [first, last] = ends(byId(g.id)), start = doc.nearestAnchor(first, g.id), end = doc.nearestAnchor(last, g.id);
          doc.update(g.id, (el) => { el.startAnchor = start; el.endAnchor = end; });
        }
        if (!Tool.isFreehand(m.tool)) { doc.select([g.id]); useSelect(); }
        made = g.id;
      }
    } else if (g.kind === "marquee") {
      if (marquee) doc.select(doc.elements.filter((m) => R.containsRect(marquee, m.frame)).map((m) => m.id));
    } else if (g.kind === "erase") {
      erased = doc.elements.filter((m) => eraseIDs.has(m.id) && !Tool.isProtection(m.tool)).map((m) => leaving(m));
      doc.select([...eraseIDs]);
      doc.deleteSelection(true);
    } else if (g.kind === "vertex") {
      const m = byId(g.id), last = m ? m.points.length - 1 : -1;
      if (m && Tool.isConnector(m.tool) && (g.index === 0 || g.index === last) && g.original.points[g.index] && !P.same(m.points[g.index], g.original.points[g.index])) {
        const anchor = doc.nearestAnchor(m.points[g.index], g.id);
        doc.update(g.id, (el) => { if (g.index === 0) el.startAnchor = anchor; else el.endAnchor = anchor; });
      }
    }
    hints = [];
    const before = doc.generation;
    doc.commit();
    const neat = made !== null ? scheduleRecognition(made, heldPreview) : null;
    gesture = null; marquee = null; eraseIDs.clear();
    paint();
    beginEraseFade(erased);
    if (neat) { emit("smart", neat); emit("mark", neat); }
    else if (made !== null && byId(made)) emit("mark", byId(made));
    else if (doc.generation !== before && g.kind !== "erase" && g.kind !== "marquee") selectedMarks().forEach((m) => emit("edit", m));
    settle();
  }
  layer.addEventListener("pointerup", release);
  layer.addEventListener("pointercancel", () => { if (trail.isDrawing || gesture) { cancelGesture(); settle(); } });
  layer.addEventListener("pointerleave", () => { if (!state.spotlight && !state.halo) layer.classList.remove("has-pointer"); });
  // Spotlight and Cursor halo, when a page's script turned them on, follow the pointer in both modes.
  if (interactive) addEventListener("pointermove", (e) => {
    if (!enabled || (!state.spotlight && !state.halo)) return;
    const o = layer.getBoundingClientRect();
    if (e.clientX < o.left || e.clientX > o.right || e.clientY < o.top || e.clientY > o.bottom) { layer.classList.remove("has-pointer"); return; }
    const p = point(e);
    layer.style.setProperty("--px", `${p[0]}px`);
    layer.style.setProperty("--py", `${p[1]}px`);
    layer.classList.add("has-pointer");
  }, { passive: true });

  // Smart Draw (DrawingCanvas.requestSmartPreview, scheduleRecognition and the snap morph) -------------
  // While a pen stroke is drawn, the shape it would become shows dashed and the stroke at 22%.
  function drawFx() {
    const candidate = state.smart ? smartPreview || releasePreview : null;
    let html = candidate ? svgMarkup({ ...candidate, style: { ...candidate.style, pattern: "dashed", opacity: 0.75 } }) : "";
    if (morph) html += morph.html;
    fx.innerHTML = html;
  }
  function cancelSmartPreview() {
    cancelAnimationFrame(previewFrame);
    previewFrame = 0;
    if (!smartPreview) return;
    const id = smartPreview.id;
    smartPreview = null;
    drawFx();
    const m = byId(id);
    if (m) render(m);
  }
  function requestSmartPreview(id) {
    if (!state.smart || previewFrame) return;
    previewFrame = requestAnimationFrame(() => {
      previewFrame = 0;
      const stroke = byId(id);
      if (!state.smart || gesture?.kind !== "create" || gesture.id !== id || !stroke || stroke.tool !== "pen") return;
      smartPreview = recognizeStroke(previewInput(stroke));
      drawFx();
      render(stroke);
    });
  }
  // On release the stroke becomes the shape without a history step of its own, so one Undo removes it.
  function scheduleRecognition(id, preview) {
    const source = byId(id);
    if (!state.smart || !source || source.tool !== "pen") return null;
    const recognized = recognizeStroke(source);
    if (!recognized) return null;
    const ink = cloneMark(source), shape = Object.assign(Object.create(Mark), recognized);
    if (!doc.applyRecognition(shape, doc.generation, source.geometryToken)) return null;
    beginMorph(ink, id, preview);
    return byId(id);
  }
  // 0.22 seconds: the stroke's line travels to the neat outline, then the real shape fades in over the last part.
  function beginMorph(ink, id, preview) {
    finishMorph();
    const shape = byId(id), from = resample(ink.points, 96);
    if (reduceMotion() || state.hidden || !shape || from.length !== 96) return;
    const outlinePoints = snapOutline(shape, 96);
    morph = { id, ink, from, to: outlinePoints ? align(outlinePoints, from, shape.tool !== "arrow") : null, preview, generation: doc.generation, selection: [...doc.selection].join(), tool: state.tool, start: null, reveal: 0, html: "" };
    requestAnimationFrame(stepMorph);
  }
  function stepMorph(time) {
    if (!morph) return;
    const shape = byId(morph.id);
    const valid = shape && !gesture && state.smart && !state.hidden && doc.generation === morph.generation && [...doc.selection].join() === morph.selection && state.tool === morph.tool;
    morph.start ??= time;
    const t = clamp((time - morph.start) / (Motion.snapMorphDuration * 1000), 0, 1);
    if (!valid || t >= 1) { finishMorph(); return; }
    const progress = Motion.progress(Motion.enter, t);
    let html = morph.preview ? svgMarkup({ ...morph.preview, style: { ...morph.preview.style, pattern: "dashed", opacity: 0.75 * (1 - progress) } }) : "";
    if (morph.to) {
      const reveal = Motion.smooth((t - 0.55) / 0.45), opacity = shape.style.opacity * (0.75 + 0.25 * progress) * (1 - reveal);
      const points = morph.from.map((q, i) => P.add(q, P.mul(P.sub(morph.to[i], q), progress)));
      if (opacity > 0.001) html += svgMarkup({ tool: "pen", points, frame: R.enclosing(points), rotation: 0, seed: 1, style: { ...shape.style, pattern: "solid", roughness: 0, pressure: false, fill: null, opacity } });
      morph.reveal = reveal;
    } else {
      html += `<g opacity="${1 - progress}">${svgMarkup(morph.ink)}</g>`;
      morph.reveal = progress;
    }
    morph.html = html;
    drawFx();
    render(shape);
    requestAnimationFrame(stepMorph);
  }
  function finishMorph() {
    if (!morph) return;
    const id = morph.id;
    morph = null;
    drawFx();
    const m = byId(id);
    if (m) render(m);
  }
  // DrawingCanvas.finishTransitions
  function finishTransitions() {
    if (releasePreview) { releasePreview = null; drawFx(); }
    finishMorph();
  }
  // An erased mark was already at 22%; it fades from there in 0.12 seconds, shrinking a little.
  function leaving(m) {
    const own = els.get(m.id), node = strokeHost(m) || own, c = R.center(m.frame);
    if (node !== own) own?.remove();
    els.delete(m.id); inks.delete(m.id);
    plane.classList.toggle("has-ink", inks.size > 0);
    return { node, center: c };
  }
  function beginEraseFade(items) {
    if (!items.length) return;
    if (state.hidden) { items.forEach(({ node }) => node?.remove()); return; }
    const scales = !reduceMotion(), start = performance.now();
    for (const item of items) {
      if (!(item.node instanceof SVGElement)) continue;
      // The shrink is about the mark's middle; a wrapper keeps it apart from the mark's own turn.
      item.wrap = svgEl("g");
      item.node.replaceWith(item.wrap);
      item.wrap.append(item.node);
    }
    const step = (time) => {
      const t = clamp((time - start) / (Motion.eraseFadeDuration * 1000), 0, 1), alpha = 0.22 * (1 - Motion.progress(Motion.fade, t)), scale = scales ? 1 - 0.03 * Motion.progress(Motion.exit, t) : 1;
      for (const { node, wrap, center } of items) {
        if (!node) continue;
        node.style.opacity = alpha;
        if (wrap) wrap.setAttribute("transform", `translate(${center[0]} ${center[1]}) scale(${scale}) translate(${-center[0]} ${-center[1]})`); else node.style.scale = scale;
      }
      if (t < 1) requestAnimationFrame(step); else items.forEach(({ node, wrap }) => (wrap || node)?.remove());
    };
    requestAnimationFrame(step);
  }

  // Laser pointer (LaserTrail and LaserTrailRenderer) ----------------------------------------------
  function stepLaser() {
    if (state.hidden) { clearLaser(); return false; }
    const time = now();
    trail.prune(time);
    laserG.innerHTML = laserOps(trail, time).map((op) => `<path d="${op.d}" fill="${op.color}" fill-opacity="${op.opacity}"/>`).join("");
    return !trail.isEmpty && !trail.isResting;
  }
  function tickLaser() { laserFrame = stepLaser() ? requestAnimationFrame(tickLaser) : 0; }
  function runLaser() {
    stepLaser();
    if (!laserFrame && !trail.isEmpty) laserFrame = requestAnimationFrame(tickLaser);
  }
  function clearLaser() {
    cancelAnimationFrame(laserFrame);
    laserFrame = 0;
    trail.strokes = [];
    laserG.innerHTML = "";
  }

  // Keys (DrawingCanvas.keyDown) --------------------------------------------------------------------
  const hasTool = (id) => toolIds.includes(id);
  const hasEffects = ["blur", "pixelate", "spotlight"].some(hasTool), hasLibrary = ["counter", "blur", "pixelate", "spotlight"].some(hasTool);
  // WorkspaceModel.perform: an action from a key or a button first settles whatever was in progress.
  function act(action) {
    finishStyleEdit();
    commitText();
    cancelGesture();
    action();
    settle();
  }
  // WorkspaceModel.deleteCurrent: an unfinished drawing is dropped, else the selection, else everything.
  function deleteCurrent() {
    if (hasActiveEdit()) { cancelGesture(); settle(); }
    else if (doc.selection.size) act(() => doc.deleteSelection(true));
    else clear();
  }
  const KEY_TOOLS = { 1: "select", 2: "rect", 3: "diamond", 4: "ellipse", 5: "arrow", 6: "line", 7: "pen", 8: "text", 9: "highlight", 0: "eraser", l: "laser", s: "blur", u: "blur", n: "counter" };
  const NUDGE = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowDown: [0, 1], ArrowUp: [0, -1] };
  if (interactive) document.addEventListener("keydown", (e) => {
    if (!enabled) return;
    visitorActs(e);
    // The app's two keys that work everywhere (AnnotatorApp.activateTools and resumeDrawing). They are the app's
    // global shortcuts; on a page they stay out of the way of someone typing in one of the page's own fields.
    const pageField = e.target.closest?.('input, textarea, select, [contenteditable=""], [contenteditable=true], [contenteditable=plaintext-only]') && !toolbar.contains(e.target) && !layer.contains(e.target);
    if (e.ctrlKey && !e.metaKey && !e.altKey && e.code === "Digit2") {
      if (pageField) return;
      e.preventDefault();
      if (e.shiftKey) {
        // Control-Shift-2 is Draw: it starts drawing, and shows everything first when it was hidden. It never stops drawing.
        if (state.mode !== "draw") setMode("draw");
        emit("shortcut", "draw", { key: RESUME_KEY });
      } else if (state.hidden) {
        // Control-2 with everything hidden opens drawing: the drawings and the toolbar come back and Draw is on.
        setMode("draw");
        emit("shortcut", "show", { key: HIDE_KEY });
      } else {
        // Control-2 otherwise does what Hide drawings and tools does, in Draw and in Use apps.
        setHidden(true);
        emit("shortcut", "hide", { key: HIDE_KEY });
      }
      return;
    }
    if (e.key === "Escape" && pop) { e.preventDefault(); closePop(true); return; }
    if (state.mode !== "draw") return;
    const command = e.metaKey || e.ctrlKey, key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    if (editor) {
      // Escape or Command-Return finishes text; Return is a new line.
      if (e.key === "Escape" || (e.key === "Enter" && command)) { e.preventDefault(); commitText(); }
      return;
    }
    // Escape while drawing keeps its meaning wherever the keys are: cancel what is unfinished, else Use apps.
    if (e.key === "Escape" && !toolbar.contains(e.target)) {
      e.preventDefault();
      if (hasActiveEdit()) { cancelGesture(); settle(); } else { setMode("use"); emit("shortcut", "use", { key: "Esc" }); }
      return;
    }
    if (e.target.closest?.("input, textarea, select, [contenteditable=true], [contenteditable=plaintext-only]")) return;
    if (e.key === "Escape") {
      e.preventDefault();
      if (hasActiveEdit()) { cancelGesture(); settle(); } else { setMode("use"); emit("shortcut", "use", { key: "Esc" }); }
      return;
    }
    if (command && key === "l" && !e.shiftKey && !e.altKey && hasLibrary) { e.preventDefault(); more("library", libraryButton); return; }
    if (NUDGE[e.key] && !hasActiveEdit() && doc.selection.size && !command && !e.altKey && !e.target.closest?.('[role="slider"]')) {
      e.preventDefault();
      act(() => doc.editSelection((m) => { translate(m, P.mul(NUDGE[e.key], e.shiftKey ? 10 : 1)); m.anchored = false; }));
      selectedMarks().forEach((m) => emit("edit", m));
      return;
    }
    if (e.key === "Enter" && linear && !e.target.closest?.("button, a")) { e.preventDefault(); finishLinear(); return; }
    if (command) {
      if (key === "z") { e.preventDefault(); if (e.shiftKey) redo(); else undo(); }
      else if (key === "y" && !isMac) { e.preventDefault(); redo(); }
      else if (key === "a") { e.preventDefault(); act(() => doc.select(doc.elements.map((m) => m.id))); }
      else if (key === "d") { e.preventDefault(); act(() => { doc.duplicateSelection(); selectedMarks().forEach((m) => { m.anchored = false; }); }); }
      return;
    }
    if (e.key === "Backspace" || e.key === "Delete") { e.preventDefault(); deleteCurrent(); return; }
    if (e.altKey) return;
    if (INK_KEYS[key]) {
      // Color keys are for drawing and text tools, not for blur.
      e.preventDefault();
      if (!Tool.isProtection(appTool(effectiveKey()))) setSolid(INK_KEYS[key][1]);
      return;
    }
    const id = KEY_TOOLS[key];
    if (id && !e.shiftKey && (id === "blur" ? hasEffects : hasTool(id))) { e.preventDefault(); setTool(id); }
  });

  // Toolbar --------------------------------------------------------------------------------------
  const toolbar = h("div", { class: "ink-toolbar", role: "toolbar", "aria-label": "Annotator web demo toolbar", "aria-orientation": "horizontal", "data-size": "expanded", "data-rows": "1" });
  const button = (attrs, html) => h("button", { type: "button", ...attrs }, html);
  const divider = (tall) => h("span", { class: tall ? "ink-div ink-div-tall" : "ink-div", "aria-hidden": "true" });
  const stacked = (glyph, key) => `<span class="ink-glyph">${glyph}</span><span class="ink-key">${key || "&nbsp;"}</span>`;
  const useWords = options.useLabel || "Use apps";

  const main = h("div", { class: "ink-main" }), rail = h("div", { class: "ink-rail" });
  // The bar's tools and added buttons in the order someone arranged them (WorkspacePreferences.toolbarItems), and the
  // bar's own buttons they turned off (hiddenToolbarControls). A page's `tools` list wins over the preset's tools.
  const isAction = (id) => id.startsWith("action.");
  const known = (id) => (isAction(id) ? Boolean(ACTIONS[id.slice(7)]) : Boolean(BAR_EXTRA[id] || TOOLS[id]));
  const listed = (id) => isAction(id) || (id === "effects" ? hasEffects : hasTool(id));
  const items = (options.toolbarItems || (options.tools ? [...FACTORY_ITEMS, ...preset.items.filter((id) => !FACTORY_ITEMS.includes(id))].filter(listed) : preset.items)).filter(known);
  const hiddenControls = new Set((options.hiddenControls || preset.hidden).filter((control) => CONTROLS.includes(control)));
  if (!hasLibrary) hiddenControls.add("library");
  const shows = (control) => !hiddenControls.has(control);
  const familyOf = (id) => (isAction(id) ? 6 : (BAR_EXTRA[id] || TOOLS[id]).family);
  let effectsButton = null;
  function itemButton(id) {
    if (isAction(id)) {
      // An added button is the size of a tool. It lights up while what it stands for is on.
      const name = id.slice(7), [label, detail, icon, size, feature] = ACTIONS[name], menu = name.endsWith("Menu");
      return button({ class: "ink-tool ink-toggle", "data-item": id, "data-id": `toolbar-action-${name}`, "aria-label": label, title: `${label}. ${detail}`, "aria-description": detail, "aria-pressed": "false",
        ...(feature ? { "data-more": feature, "aria-haspopup": "dialog" } : {}) },
      menu ? `<span class="ink-glyph">${symbol(icon, size, { line: 1.5 })}</span><span class="ink-key ink-menu-mark">${symbol("chevron.down", 9, { line: 1.4 })}</span>` : stacked(symbol(icon, size, { line: 1.5 }), ""));
    }
    const t = BAR_EXTRA[id] || TOOLS[id];
    const b = button({ class: "ink-tool", "data-tool": id, "aria-label": t.label, title: t.title || `${t.label} (${t.key})`, "aria-description": `Shortcut ${t.key}`, "aria-pressed": "false",
      ...(t.more ? { "data-more": t.more, "aria-haspopup": "dialog" } : {}) }, stacked(symbol(t.symbol, t.size, { line: 1.5 }), t.key));
    if (id === "effects") effectsButton = b;
    return b;
  }
  function fillRail() {
    rail.replaceChildren();
    items.forEach((id, i) => {
      if (i && familyOf(items[i - 1]) !== familyOf(id)) rail.append(divider());
      rail.append(itemButton(id));
    });
  }
  fillRail();
  // ToolbarToolLayout.identity: the bar button that stands for a tool. Effects stands for Blur unless Blur has its
  // own button, and Rectangle for a shape that is not on the bar.
  function barIdentity(tool) {
    if (tool === "blur" || tool === "pixelate") return items.includes(tool) ? tool : "effects";
    if (["diamond", "ellipse"].includes(tool) && !items.includes(tool) && items.includes("rect")) return "rect";
    return tool;
  }
  const libraryButton = button({ class: "ink-tool ink-library", "data-action": "library", "aria-label": "Tool library", title: `More shapes and tools (${LIBRARY_KEY})`, "aria-haspopup": "dialog" },
    stacked(symbol("square.grid.2x2", 21.5, { line: 1.5 }), LIBRARY_KEY));
  const boardButton = button({ class: "ink-board-button", "data-action": "board", "aria-label": "Board background", "aria-haspopup": "dialog",
    title: "Cycle Desktop, Whiteboard and Blackboard on this drawing display." });
  const modeButton = button({ class: "ink-mode", "data-action": "mode" },
    `<span class="ink-mode-icon">${symbol("pencil.tip", 19.5, { line: 1.4, className: "ink-when-draw" })}${symbol("cursorarrow", 20, { line: 1.2, className: "ink-when-use" })}</span><span class="ink-mode-row"><i class="ink-dot"></i><b></b></span>`);
  const action = (name, label, icon, size, extra = {}, line) => button({ class: "ink-action", "data-action": name, "aria-label": label, title: label, ...extra }, symbol(icon, size, { line }));
  const undoButton = action("undo", "Undo", "arrow.uturn.backward", 15), redoButton = action("redo", "Redo", "arrow.uturn.forward", 15);
  const clearButton = action("clear", "Clear all drawings — undoable", "trash", 16);
  const propertiesButton = action("properties", "Properties", "slider.horizontal.3", 15, { "data-id": "toolbar-properties" });
  const settingsButton = action("settings", "Settings and shortcuts", "gearshape", 16), hideButton = action("hide", "Hide drawings and tools", "xmark", 19, {}, 1.3);
  const actions = h("div", { class: "ink-actions" });
  const actionButtons = [["undo", undoButton], ["redo", redoButton], ["clearAll", clearButton], ["properties", propertiesButton], ["settings", settingsButton], ["hide", hideButton]].filter(([control]) => shows(control)).map(([, b]) => b);
  actions.append(...actionButtons);
  main.append(rail);
  if (shows("library")) main.append(libraryButton);
  main.append(h("span", { class: "ink-spacer" }));
  // The divider before Board and Draw goes when both of them do.
  if (shows("board") || shows("mode")) main.append(divider(true));
  if (shows("board")) main.append(boardButton);
  if (shows("mode")) main.append(modeButton);
  if (actionButtons.length) main.append(actions);
  const second = h("div", { class: "ink-second" });
  const strip = h("div", { class: "ink-strip", role: "group", "aria-label": "Active and pending modes" });
  const rule = h("span", { class: "ink-rule", "aria-hidden": "true" });
  const props = h("div", { class: "ink-props", role: "group", "aria-label": "Tool properties", "data-id": "contextual-inspector-viewport" });
  const pill = button({ class: "ink-pill", "aria-label": "Annotator toolbar" }, `${brandMark(20)}<span class="ink-status"><i class="ink-dot"></i><b></b></span>`);
  // What the app says while an arrow is placed point by point. The app shows it in its hint; here it sits above the bar.
  const statusLine = h("p", { class: "ink-status-line", role: "status", hidden: "" });
  toolbar.append(main, second, strip, rule, props, pill, statusLine);
  // Inside a parent, the toolbar is placed and dragged in that parent's coordinates.
  const inParent = options.toolbarPosition === "absolute";
  if (inParent) toolbar.dataset.position = "absolute";
  // `showToolbar` puts the toolbar on the page for a scripted-only instance too: a picture of the bar that a script
  // changes (scenes.js). It hears no keys and never draws; the page keeps it out of reach (inert).
  const mounted = interactive || Boolean(options.showToolbar);
  if (mounted) (options.toolbarParent || document.body).append(toolbar);
  if (options.collapsed) toolbar.classList.add("is-collapsed");
  toolbar.hidden = !enabled;
  // A page's own words on the button are its accessible name too.
  if (options.pillLabel) { pill.dataset.custom = "true"; pill.removeAttribute("aria-label"); }

  // Appearance: the app's Dark and Light, and its glass.
  const scheme = matchMedia("(prefers-color-scheme: light)");
  let appearance = options.appearance || "dark";
  function setAppearance(name) {
    appearance = name;
    toolbar.dataset.appearance = name === "auto" ? (scheme.matches ? "light" : "dark") : name === "light" ? "light" : "dark";
  }
  scheme.addEventListener?.("change", () => { if (appearance === "auto") setAppearance("auto"); });
  setAppearance(appearance);
  if (options.glass !== false) toolbar.dataset.glass = "true";

  // Properties -----------------------------------------------------------------------------------
  let propsKey = "", updaters = [];
  const text = (className, value) => { const el = h("span", { class: className }); el.textContent = value; return el; };
  function block(name, control, width) {
    const el = h("div", { class: "ink-block" });
    const label = text("ink-label", name);
    if (width) label.style.minWidth = `${width}px`;
    el.append(label, control);
    return el;
  }
  // One button for a few choices: it shows the current one and moves to the next on each click.
  function cycle(prop, name, choices, face, labelWidth) {
    const b = button({ class: "ink-control", "data-prop": prop, "aria-label": name });
    const update = () => {
      const value = read(prop), index = Math.max(0, choices.findIndex((c) => c[0] === value)), next = choices[(index + 1) % choices.length];
      b.innerHTML = `${face(choices[index][0])}<span class="ink-dots">${choices.map((c, i) => `<i${i === index ? ' class="is-on"' : ""}></i>`).join("")}</span>`;
      b.title = `${name}: ${choices[index][1]}. Click for ${next[1].toLowerCase()}.`;
      b.setAttribute("aria-description", `${choices[index][1]}. Changes to ${next[1].toLowerCase()}`);
      b.onclick = () => write(prop, next[0]);
    };
    updaters.push(update);
    return block(name, b, labelWidth);
  }
  function toggle(prop, blockName, name, face, onHelp, offHelp, labelWidth, get = () => Boolean(read(prop)), set = (on) => write(prop, on)) {
    const b = button({ class: "ink-control", "data-prop": prop, "aria-label": name }, face);
    b.onclick = () => set(!get());
    updaters.push(() => { const on = get(); b.setAttribute("aria-pressed", String(on)); b.title = on ? onHelp : offHelp; });
    return block(blockName, b, labelWidth);
  }
  // The app's small slider: a 4-point track, the accent color up to an 18 by 14 knob, and the value beside it.
  function slider(prop, name, { min, max, step = 1, width = 96, format, get = () => read(prop), set = (value, live) => write(prop, value, live) }) {
    const el = h("div", { class: "ink-slider", role: "slider", tabindex: "0", "aria-label": name, "aria-valuemin": min, "aria-valuemax": max, "data-prop": prop, style: `width:${width}px` },
      '<span class="ink-track"><span class="ink-fill"></span></span><span class="ink-knob"></span>');
    const readout = format ? text("ink-value", "") : null;
    let value = 0, sliding = false;
    const show = (v) => {
      value = v;
      const t = clamp((v - min) / (max - min), 0, 1);
      el.style.setProperty("--t", t);
      el.setAttribute("aria-valuenow", v);
      if (readout) { readout.textContent = format(v); el.setAttribute("aria-valuetext", format(v)); }
    };
    const snap = (v) => clamp(Math.round(v / step) * step, min, max);
    const from = (e) => { const r = el.getBoundingClientRect(), knob = (parseFloat(getComputedStyle(el).getPropertyValue("--knob")) || 18) * (r.width / el.offsetWidth); return snap(min + clamp((e.clientX - r.left - knob / 2) / (r.width - knob), 0, 1) * (max - min)); };
    el.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      e.preventDefault(); el.focus(); el.setPointerCapture(e.pointerId);
      sliding = true; el.classList.add("is-sliding");
      const v = from(e); show(v); set(v, true);
    });
    el.addEventListener("pointermove", (e) => { if (!sliding) return; const v = from(e); if (v !== value) { show(v); set(v, true); } });
    const end = () => { if (!sliding) return; sliding = false; el.classList.remove("is-sliding"); set(value, false); };
    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", end);
    el.addEventListener("keydown", (e) => {
      const move = { ArrowRight: step, ArrowUp: step, ArrowLeft: -step, ArrowDown: -step, PageUp: step * 5, PageDown: -step * 5 }[e.key];
      const to = e.key === "Home" ? min : e.key === "End" ? max : move ? snap(value + move) : null;
      if (to === null) return;
      e.preventDefault(); e.stopPropagation();
      show(to); set(to, false);
    });
    updaters.push(() => { if (!sliding) show(clamp(get(), min, max)); });
    return { el, readout, show };
  }
  function sliderBlock(prop, blockName, name, config, labelWidth) {
    const { el, readout } = slider(prop, name, config), row = h("div", { class: "ink-slider-row" });
    row.append(el, readout);
    return block(blockName, row, labelWidth);
  }
  // A slider with a caption before it and a number field after it, as for Opacity and Blur.
  function fieldRow(prop, name, { min, max, sliderMax = max, unit = "", get = () => read(prop), set = (value, live) => write(prop, value, live) }) {
    const row = h("div", { class: "ink-inline ink-field-row" });
    const input = h("input", { class: "ink-field", type: "text", inputmode: "numeric", "aria-label": `Precise ${name.toLowerCase()}`, title: `${name}: ${min}–${max} ${unit}`, placeholder: name });
    const part = slider(prop, name, { min, max: sliderMax, width: 90, get, set: (value, live) => { input.value = value; set(value, live); } });
    const apply = () => { const value = Number(input.value); if (input.value.trim() && Number.isFinite(value) && value >= min && value <= max) set(value, false); else input.value = get(); };
    input.addEventListener("change", apply);
    input.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") input.blur(); if (e.key === "Escape") { input.value = get(); input.blur(); } });
    updaters.push(() => { if (document.activeElement !== input) input.value = Math.round(get() * 10) / 10; });
    row.append(text("ink-caption", name), part.el, input);
    if (unit) row.append(text("ink-caption", unit));
    return row;
  }
  // Properties: what the demo does exactly as the app is live. The rest looks like the app's control and opens
  // "Want to see more?".
  const dots = (count) => `<span class="ink-dots">${Array.from({ length: count }, (_, i) => `<i${i ? "" : ' class="is-on"'}></i>`).join("")}</span>`;
  function inApp(feature, blockName, name, face, labelWidth, attrs = {}) {
    return block(blockName, button({ class: "ink-control", "data-more": feature, "aria-label": name, title: `${name}: in the app`, "aria-haspopup": "dialog", ...attrs }, face), labelWidth);
  }
  function well() {
    const b = button({ class: "ink-well", "data-prop": "color", "aria-label": "Stroke color", title: "Stroke color", "aria-haspopup": "dialog" }, `<span class="ink-paint"></span>${symbol("chevron.down", 10, { line: 1.2 })}`);
    b.onclick = () => openPop("color", b);
    updaters.push(() => {
      // PaintSwatch: the gradient when one is chosen, at the object's opacity.
      const value = read("color"), gradient = effectiveKey() === "laser" ? null : read("strokeGradient"), face = b.querySelector(".ink-paint");
      face.style.background = gradient ? Paint.css(gradient) : value || "none";
      face.style.opacity = (read("opacity") ?? 1) * (gradient ? Paint.alpha(value) : 1);
      b.setAttribute("aria-description", `#${Paint.hex8(value) || String(value || "").toUpperCase()}`);
    });
    return block("Stroke color", b, 65.5);
  }
  const fillWell = () => {
    const el = inApp("fill", "Fill", "Fill color", `<span class="ink-paint"></span>${symbol("chevron.down", 10, { line: 1.2 })}`, 15.5);
    const b = el.querySelector("button"), fill = read("fill");
    b.className = `ink-well${fill ? "" : " is-empty"}`;
    b.title = fill ? "Fill color" : "Transparent fill";
    if (fill) Object.assign(b.querySelector(".ink-paint").style, { background: fill, opacity: 0.4 });
    return el;
  };
  const headFace = (start) => {
    const value = read(start ? "startHead" : "endHead") || "none", el = inApp("heads", start ? "Start" : "End", `${start ? "Start" : "End"} arrowhead`, `${headSample(value, start)}${symbol("chevron.down", 12, { line: 1.1 })}`, start ? 27 : 20.5);
    el.querySelector("button").classList.add("ink-head");
    return el;
  };
  const note = (words) => text("ink-inline ink-note", words);
  const group = (...children) => { const el = h("div", { class: "ink-pair" }); el.append(...children); return el; };
  const widthBlock = (name = "Width", labelWidth = 32) => sliderBlock("width", name, name === "Width" ? "Width" : "Size", { min: 1, max: 40, format: (v) => `${v} pt` }, labelWidth);
  const patternBlock = () => cycle("pattern", "Pattern", [["solid", "Solid"], ["dashed", "Dashed"], ["dotted", "Dotted"]], patternSample, 39.5);
  const roughBlock = () => cycle("roughness", "Roughness", [[0, "Clean"], [2, "Sketchy"], [6, "Wild"]], roughnessSample, 59.5);
  function propertyControls(key, hasFill) {
    switch (key) {
      case "rect": case "diamond": case "ellipse": return [
        group(well(), fillWell()), widthBlock(), patternBlock(), roughBlock(),
        hasFill && inApp("fill", "Fill style", "Fill style", `${fillSample("solid")}${dots(3)}`, 44.5),
        key !== "ellipse" && inApp("fill", "Edges", "Rounded corners", edgesSample(true), 33.5, { "aria-pressed": "false" }),
      ];
      case "arrow": return [well(), widthBlock(), patternBlock(), roughBlock(), headFace(true), headFace(false),
        toggle("curved", "Curved", "Curved arrow", routeSample(true), "Curved arrow is on. Click for a straight arrow.", "Straight arrow. Click to curve it.", 39),
        sliderBlock("headScale", "Head size", "Arrowhead size", { min: 0.5, max: 2, step: 0.05, width: 86, format: (v) => `${Math.round(v * 100)}%`, get: () => read("headScale") ?? 1 }, 53)];
      case "line": return [well(), widthBlock(), patternBlock(), roughBlock()];
      case "pen": return [well(), widthBlock(), patternBlock(), roughBlock(),
        inApp("pressure", "Pressure", "Ink pressure", pressureSample(), 47.5, { "aria-pressed": "false" }),
        toggle("smart", "Smart Draw", "Smart Draw", symbol("wand.and.stars", 19, { line: 1.4 }), "Smart Draw is on. Click to turn it off.", "Turn on Smart Draw to turn pen strokes into rectangles and ellipses.", 63,
          () => state.smart, (on) => { state.smart = on; refreshToolbar(); })];
      case "text": {
        const size = h("span", { class: "ink-slider-row" }, `<span class="ink-slider" style="width:96px;--t:${(read("fontSize") - 8) / 112}"><span class="ink-track"><span class="ink-fill"></span></span><span class="ink-knob"></span></span><span class="ink-value">${Math.round(read("fontSize"))} pt</span>`);
        const sizeBlock = inApp("text", "Size", "Text size", "", 23);
        sizeBlock.querySelector("button").className = "ink-slider-button";
        sizeBlock.querySelector("button").append(size);
        return [well(), inApp("text", "Font", "Font", `<span class="ink-aa">Aa</span>${dots(2)}`, 24.5), sizeBlock,
          inApp("text", "Weight", "Bold", symbol("bold", 19), 38, { "aria-pressed": "false" }),
          inApp("text", "Alignment", "Alignment", `${symbol("text.alignleft", 17, { line: 1.3 })}${dots(3)}`, 55)];
      }
      case "highlight": return [well(), widthBlock()];
      case "laser": return [well(), widthBlock(), sliderBlock("fade", "Fade", "Fade", { min: 0.5, max: 5, step: 0.1, format: (v) => `${v.toFixed(1)} s`, get: () => styles.laser.fade }, 26), note(NOTE.laser)];
      case "eraser": return [widthBlock("Eraser size", 59)];
      case "counter": return [well(), fieldRow("opacity", "Opacity", { min: 0, max: 100, unit: "%", get: () => Math.round((read("opacity") ?? 1) * 100), set: (v, live) => write("opacity", v / 100, live) }), note(NOTE.counter)];
      case "blur": {
        // The app's menu offers Blur, Pixelate and Distort. The last two read the screen, which a web page cannot.
        const menu = button({ class: "ink-popup", "data-more": "effects", "aria-label": "Effect", "data-id": "cover-mode", "aria-haspopup": "dialog" }, "Blur");
        const wrap = h("span", { class: "ink-inline ink-popup-wrap" });
        wrap.append(menu, h("span", {}, symbol("chevron.up.chevron.down", 12, { line: 1.3 })));
        const cover = h("div", { class: "ink-cover" });
        cover.append(wrap, fieldRow("effectAmount", "Blur", { min: 0, max: 100, sliderMax: 20, get: () => read("effectAmount") ?? 18 }));
        return [cover, note(NOTE.blur)];
      }
      default: return doc.selection.size
        ? [well(), widthBlock(), fieldRow("opacity", "Opacity", { min: 0, max: 100, unit: "%", get: () => Math.round((read("opacity") ?? 1) * 100), set: (v, live) => write("opacity", v / 100, live) })]
        : [note(NOTE.select)];
    }
  }
  function refreshProperties(force) {
    if (!state.properties || toolbar.classList.contains("is-collapsed")) { propsKey = ""; return; }
    const key = effectiveKey(), count = doc.selection.size, hasFill = Boolean(read("fill"));
    const structure = `${key}|${[...doc.selection].join()}|${hasFill}`;
    if (force || structure !== propsKey) {
      const focused = props.contains(document.activeElement) ? document.activeElement.dataset.prop : null;
      propsKey = structure;
      updaters = [];
      const controls = propertyControls(key, hasFill).filter(Boolean);
      if (count) controls.unshift(h("span", { class: "ink-inline ink-editing", "data-id": "selected-object-context" }, `${symbol("square.dashed", 13, { line: 1.3 })}<span>Editing ${count === 1 ? MARK_LABEL[key] || "object" : `${count} selected objects`}</span>`));
      controls.push(button({ class: "ink-more-link", "data-more": "app", "aria-haspopup": "dialog", title: MORE.app }, "More in the app"));
      props.dataset.for = key || "select";
      props.replaceChildren(...controls);
      if (focused) props.querySelector(`[data-prop="${focused}"]`)?.focus();
      // A rebuilt row has new buttons; an open popover moves to the one that replaced its own.
      if (pop && !popAnchor.isConnected) {
        const again = props.querySelector(popAnchor.dataset.prop ? `[data-prop="${popAnchor.dataset.prop}"]` : `[data-more="${popAnchor.dataset.more}"]`);
        if (again) { popAnchor = again; again.setAttribute("aria-expanded", "true"); } else closePop();
      }
    }
    updaters.forEach((update) => update());
  }

  // Popovers: the color picker and "Want to see more?" -------------------------------------------
  let pop = null, popAnchor = null, popKind = "";
  function closePop(refocus) {
    if (!pop) return;
    const picker = popKind === "color";
    pop.remove();
    popAnchor?.setAttribute("aria-expanded", "false");
    if (refocus) popAnchor?.focus();
    pop = popAnchor = null; popKind = "";
    // What the color picker changed becomes one step for Undo.
    if (picker) { finishStyleEdit(); refreshToolbar(); }
  }
  function placePop() {
    if (!pop) return;
    const scrolled = pop.scrollTop;
    pop.style.maxHeight = "";
    const bar = toolbar.getBoundingClientRect(), a = popAnchor.getBoundingClientRect(), width = pop.offsetWidth, height = pop.offsetHeight;
    // The box the popover has to stay in: the window, cut down by any ancestor that clips what is inside it.
    const host = { left: 0, top: 0, right: document.documentElement.clientWidth, bottom: innerHeight };
    if (getComputedStyle(toolbar).position !== "fixed") {
      for (let el = toolbar.parentElement; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
        const style = getComputedStyle(el);
        if (style.overflowX === "visible" && style.overflowY === "visible") continue;
        const r = el.getBoundingClientRect();
        host.left = Math.max(host.left, r.left); host.top = Math.max(host.top, r.top); host.right = Math.min(host.right, r.right); host.bottom = Math.min(host.bottom, r.bottom);
      }
    }
    const left = clamp(a.left + a.width / 2 - width / 2, host.left + 8, Math.max(host.left + 8, host.right - width - 8));
    const above = bar.top - host.top >= height + 16 || bar.top - host.top > host.bottom - bar.bottom;
    pop.style.left = `${f(left - bar.left)}px`;
    // A popover taller than its room scrolls, as the app's picker does on a small screen.
    pop.style.maxHeight = `${Math.max(160, (above ? bar.top - host.top : host.bottom - bar.bottom) - 20)}px`;
    pop.style.top = above ? `${-pop.offsetHeight - 10}px` : `${f(bar.height + 10)}px`;
    pop.scrollTop = scrolled;
  }
  function openPop(kind, anchor) {
    const again = pop && popKind === kind && popAnchor === anchor;
    closePop();
    if (again) return;
    pop = h("div", { class: "ink-pop", "data-kind": kind, role: "dialog" });
    popKind = kind; popAnchor = anchor;
    anchor.setAttribute("aria-expanded", "true");
    // The picker opens on Gradient when the paint is one; the laser's color has no gradients.
    if (kind === "color") { showingGradients = effectiveKey() !== "laser" && Boolean(read("strokeGradient")); shadesOpen = false; }
    fillPop();
    toolbar.append(pop);
    placePop();
    pop.querySelector("[autofocus], .is-current, a, button:not(:disabled)")?.focus({ preventScroll: true });
  }
  let showingGradients = false, shadesOpen = false;
  const swatch = (name, color, selected) => button({ class: `ink-swatch${selected ? " is-current" : ""}`, "data-color": color, "aria-label": `${name}, hex #${Paint.hex8(color)}`, title: name, "aria-pressed": String(selected), style: `--swatch:${color}` });
  // "Want to see more?": what the app does and this page does not, with the way to get the app.
  function more(feature, anchor) {
    const name = MORE[feature] ? feature : "app", target = anchor?.isConnected ? anchor : propertiesButton;
    if (toolbar.classList.contains("is-collapsed") || !toolbar.isConnected) return;
    openPop(`more:${name}`, target);
  }
  function fillPop() {
    if (!pop) return;
    if (popKind.startsWith("more:")) {
      const feature = popKind.slice(5);
      pop.setAttribute("aria-label", "Want to see more?");
      pop.innerHTML = '<p class="ink-pop-title">Want to see more?</p><p class="ink-more-line"></p><a class="ink-more-go">Try it on your machine</a><p class="ink-pop-caption ink-more-free">Free for Mac and Windows</p>';
      pop.querySelector(".ink-more-line").textContent = MORE[feature];
      const go = pop.querySelector("a");
      go.href = moreHref;
      go.addEventListener("click", (e) => {
        // A page that handles the press itself calls event.preventDefault(); otherwise the link is followed and the
        // page gets its clicks back.
        const event = { feature, href: go.href, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
        emit("more", feature, event);
        closePop();
        if (event.defaultPrevented) e.preventDefault(); else setMode("use");
      });
      return;
    }
    // InspectorColorPalette: Gradient or Solid, fifteen swatches of either, shades of the chosen color, its hex
    // value, the paint's opacity and a color from the screen.
    const color = String(read("color") || ""), parts = Paint.parse(color), offers = effectiveKey() !== "laser", gradient = offers ? read("strokeGradient") : null;
    const isSelected = (value) => {
      const v = Paint.parse(value);
      return !gradient && Boolean(v && parts) && v.red === parts.red && v.green === parts.green && v.blue === parts.blue && (v.alpha === 0 ? parts.alpha === 0 : parts.alpha > 0);
    };
    const gradients = offers && showingGradients, percent = () => `${Math.round(Paint.alpha(read("color")) * 100)}%`;
    pop.setAttribute("aria-label", "Stroke color");
    pop.innerHTML = `<p class="ink-pop-title">Stroke color</p>
      ${offers ? `<div class="ink-seg" role="radiogroup" aria-label="Color options">${[["gradient", "Gradient"], ["solid", "Solid"]].map(([id, label]) =>
        `<button type="button" role="radio" data-seg="${id}" aria-checked="${(id === "gradient") === gradients}">${label}</button>`).join("")}</div>` : ""}
      <div class="ink-pick">
        <div class="ink-swatches" role="group" aria-label="${gradients ? "Gradients" : "Colors"}"></div>
        ${gradients ? '<p class="ink-pop-caption ink-blend-note">A blend across your whole shape.</p>'
          : parts ? `<div class="ink-shades"><button type="button" class="ink-disclosure" data-shades aria-expanded="${shadesOpen}">${symbol("chevron.right", 9, { line: 2.2 })}<b>Shades</b></button><div class="ink-shade-row"${shadesOpen ? "" : " hidden"}></div></div>` : ""}
        <button type="button" class="ink-disclosure ink-wheel" data-more="wheel" data-id="inline-color-wheel" aria-label="Color wheel" aria-haspopup="dialog">${symbol("chevron.right", 9, { line: 1.5 })}<span>Color wheel</span></button>
        ${gradients ? "" : `<p class="ink-pop-caption">Hex color</p>
        <label class="ink-hex" title="Six RGB or eight RGBA digits. Entering a hex color switches to solid paint."><span>#</span><input type="text" spellcheck="false" autocomplete="off" maxlength="9" placeholder="RRGGBB or RRGGBBAA" aria-label="Stroke hex color and alpha" value="${Paint.hex8(color)}"></label>
        <p class="ink-pop-error" hidden>Use six RGB or eight RGBA hex digits.</p>`}
      </div>
      <hr>
      <div class="ink-opacity"><span>Stroke opacity</span><span class="ink-opacity-slider"></span><span class="ink-value">${percent()}</span></div>
      <button type="button" class="ink-pop-button" data-pick aria-label="Pick from screen" title="Choose a color anywhere on the screen. Escape cancels.">${symbol("eyedropper", 15)} Pick from screen</button>
      <div class="ink-pop-foot"><button type="button" class="ink-pop-done" data-done>Done</button></div>`;
    const grid = pop.querySelector(".ink-swatches");
    if (gradients) {
      for (const choice of GRADIENT_PRESETS) {
        const selected = Paint.same(gradient, choice.gradient);
        grid.append(button({ class: `ink-swatch is-gradient${selected ? " is-current" : ""}`, "data-gradient": choice.id, "aria-label": `${choice.label} gradient`, title: choice.label, "aria-pressed": String(selected), style: `--swatch:${Paint.css(choice.gradient)}` }));
      }
    } else {
      grid.append(swatch("Transparent stroke", "#00000000", isSelected("#00000000")));
      APP_PALETTE.forEach(([name, value]) => grid.append(swatch(name, value, isSelected(value))));
      // Five shades of the chosen color: darker by half and a quarter, as it is, lighter by a quarter and a half.
      if (parts) [0.5, 0.75, 1, 1.25, 1.5].forEach((amount, i) => {
        const part = (value) => (amount <= 1 ? value * amount : value + (1 - value) * (amount - 1));
        const shade = Paint.format({ red: part(parts.red), green: part(parts.green), blue: part(parts.blue), alpha: parts.alpha });
        pop.querySelector(".ink-shade-row").append(swatch(`${["Darker", "Dark", "Current", "Light", "Lighter"][i]} shade of selected color`, shade, isSelected(shade)));
      });
    }
    // The paint's opacity is its color's alpha; the object's own opacity is a different thing.
    const shown = updaters.length, opacity = slider("paint-opacity", "Stroke opacity", { min: 0, max: 100, width: 126,
      get: () => Math.round(Paint.alpha(read("color")) * 100),
      set: (value, live) => {
        applyStyle("color", (style) => Paint.setOpacity(style, value / 100), live);
        pop.querySelector(".ink-opacity .ink-value").textContent = percent();
        const field = pop.querySelector(".ink-hex input");
        if (field) field.value = Paint.hex8(read("color"));
      } });
    updaters.length = shown;
    opacity.show(Math.round(Paint.alpha(color) * 100));
    pop.querySelector(".ink-opacity-slider").replaceWith(opacity.el);
    const redraw = (focus) => { fillPop(); placePop(); if (focus) pop.querySelector(focus)?.focus({ preventScroll: true }); };
    const hex = pop.querySelector(".ink-hex input");
    // PaletteHex.parse: six digits keep the paint's opacity, eight bring their own.
    const applyHex = () => {
      const value = hex.value.trim().replace(/^#/, "");
      if (!/^([0-9a-f]{6}|[0-9a-f]{8})$/i.test(value)) { hex.setAttribute("aria-invalid", "true"); pop.querySelector(".ink-pop-error").hidden = false; placePop(); hex.focus(); return false; }
      setSolid(`#${value.toUpperCase()}`, value.length === 8);
      return true;
    };
    pop.onclick = async (e) => {
      const b = e.target.closest("button");
      if (!b) return;
      if (b.dataset.seg) { showingGradients = b.dataset.seg === "gradient"; redraw(`[data-seg="${b.dataset.seg}"]`); }
      else if (b.dataset.gradient) { setGradient(GRADIENT_PRESETS.find((choice) => choice.id === b.dataset.gradient).gradient); redraw(`[data-gradient="${b.dataset.gradient}"]`); }
      else if (b.dataset.color) { setSolid(b.dataset.color, Paint.alpha(b.dataset.color) === 0); redraw(`[data-color="${b.dataset.color}"]`); }
      else if (b.hasAttribute("data-shades")) { shadesOpen = !shadesOpen; redraw("[data-shades]"); }
      else if (b.dataset.more) more(b.dataset.more, popAnchor);
      else if (b.hasAttribute("data-done")) { if (!hex || hex.value === Paint.hex8(read("color")) || applyHex()) closePop(true); }
      else if (b.hasAttribute("data-pick")) {
        if (!window.EyeDropper) { more("eyedropper", popAnchor); return; }
        try { setSolid((await new window.EyeDropper().open()).sRGBHex.toUpperCase()); if (pop && popKind === "color") redraw("[data-pick]"); } catch { /* Escape cancels */ }
      }
    };
    hex?.addEventListener("keydown", (e) => {
      e.stopPropagation();
      if (e.key === "Escape") closePop(true);
      if (e.key === "Enter" && applyHex()) redraw(".ink-hex input");
    });
  }
  if (interactive) document.addEventListener("pointerdown", (e) => {
    if (pop && !pop.contains(e.target) && !popAnchor.contains(e.target)) closePop();
  }, true);

  // Fitting: the app's expanded and minimal sizes, then a second row and scrolling tools on a narrow screen.
  // ToolbarToolLayout.width and ToolbarMetrics.regularReserve: the bar is exactly its tools and buttons on one row.
  const railWidth = (minimal) => items.reduce((sum, id, i) => sum + (i ? (familyOf(items[i - 1]) === familyOf(id) ? 2 : minimal ? 9 : 11) : 0) + (minimal ? 34 : 44), 0);
  const controlRoom = (control, minimal) => (control === "library" || control === "board" ? (minimal ? 34 : 44) : control === "mode" ? (minimal ? 46 : 60) : minimal ? 22 : 24) + (minimal ? 4 : 6);
  const hiddenRoom = (minimal) => [...hiddenControls].reduce((sum, control) => sum + controlRoom(control, minimal), 0) + (shows("board") || shows("mode") ? 0 : 1 + (minimal ? 4 : 6));
  // The box the toolbar is dragged in: the window, or its parent with toolbarPosition "absolute".
  const dragArea = () => (inParent && toolbar.offsetParent ? toolbar.offsetParent.getBoundingClientRect() : { left: 0, top: 0, width: innerWidth, height: innerHeight });
  // Tools that scroll sideways fade out at the edge where more of them wait.
  function fadeRail() {
    const more = rail.scrollWidth - rail.clientWidth;
    rail.dataset.fade = more > 1 ? `${rail.scrollLeft > 1 ? "start" : ""}${rail.scrollLeft < more - 1 ? "end" : ""}` : "";
  }
  rail.addEventListener("scroll", fadeRail, { passive: true });
  // On a narrow screen the chosen tool is scrolled into view when the tool or the toolbar's size changes.
  let revealed = "";
  function revealTool() {
    const b = rail.querySelector('[aria-pressed="true"]'), key = `${b?.dataset.tool}|${rail.clientWidth}`;
    if (!b || !rail.clientWidth || key === revealed) return;
    revealed = key;
    const left = b.offsetLeft - rail.offsetLeft, room = 22;
    if (left - room < rail.scrollLeft) rail.scrollLeft = left - room;
    else if (left + b.offsetWidth + room > rail.scrollLeft + rail.clientWidth) rail.scrollLeft = left + b.offsetWidth + room - rail.clientWidth;
  }
  let fitted = "", sideMargin = Number.isFinite(options.toolbarMargin) ? Math.max(0, options.toolbarMargin) : 8;
  // fit({ margin }) keeps at least that much room on each side of the toolbar inside the window or its parent, from then on.
  function fit({ margin } = {}) {
    if (Number.isFinite(margin)) sideMargin = Math.max(0, margin);
    const fixed = getComputedStyle(toolbar).position === "fixed", host = fixed ? document.documentElement : toolbar.offsetParent;
    if (!host) return;
    const available = host.clientWidth - sideMargin * 2;
    const natural = (minimal) => railWidth(minimal) + (minimal ? 307 : 383) - hiddenRoom(minimal);
    const minimal = natural(false) > available, rows = minimal && natural(true) > available && available < 560 ? 2 : 1;
    const next = `${minimal}|${rows}|${available}`;
    if (next !== fitted) {
      fitted = next;
      toolbar.dataset.size = minimal ? "minimal" : "expanded";
      toolbar.dataset.rows = rows;
      toolbar.style.setProperty("--ink-available", `${available}px`);
      if (actionButtons.length) (rows === 2 ? second : main).append(actions);
    }
    revealTool();
    fadeRail();
    // A toolbar that was moved stays inside the window, or its parent, when it grows.
    if ((fixed || inParent) && toolbar.classList.contains("is-placed")) {
      const r = toolbar.getBoundingClientRect(), area = dragArea();
      toolbar.style.left = `${clamp(r.left - area.left, 6, Math.max(6, area.width - r.width - 6))}px`;
      toolbar.style.top = `${clamp(r.top - area.top, 6, Math.max(6, area.height - r.height - 6))}px`;
    }
    placePop();
    const size = { width: toolbar.offsetWidth, height: toolbar.offsetHeight, size: toolbar.dataset.size, rows };
    if (fixed && size.height) document.documentElement.style.setProperty("--ink-toolbar-height", `${size.height}px`);
    emit("layout", size);
  }

  function chip(name, title, short, icon, filled) {
    const compact = toolbar.dataset.rows === "2";
    return button({ class: "ink-chip", "data-stop": name, "aria-label": `Stop ${title.toLowerCase()}`, title: `${title} is active. Click to stop.`, "data-id": `stop-active-${name}` },
      compact ? `<span>${symbol(icon, 14, { filled })}${symbol("xmark", 8, { line: 1.6 })}</span><b>${short} on</b>`
        : `${symbol(icon, 16, { filled })}<b>${title}</b>${symbol("xmark", 9, { line: 1.7 })}`);
  }
  let stripKey = "";
  function refreshToolbar() {
    const drawing = state.mode === "draw", shown = state.properties, [word, boardName, boardIcon] = BOARDS[state.board];
    toolbar.dataset.mode = state.mode;
    toolbar.dataset.board = state.board;
    toolbar.dataset.properties = shown;
    const chosen = barIdentity(state.tool);
    rail.querySelectorAll("[data-tool]").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.tool === chosen)));
    rail.querySelector('[data-item="action.smartDraw"]')?.setAttribute("aria-pressed", String(state.smart));
    if (boardButton.dataset.board !== state.board) {
      boardButton.dataset.board = state.board;
      boardButton.innerHTML = `<span class="ink-board-icon">${symbol(boardIcon, 21.5, { line: 1.5, filled: state.board === "black" })}</span><span class="ink-board-label">${word}</span>`;
    }
    boardButton.setAttribute("aria-description", boardName);
    boardButton.setAttribute("aria-pressed", String(state.board !== "desk"));
    const status = options.pillLabel || (drawing ? "Draw" : "Apps");
    modeButton.querySelector("b").textContent = drawing ? "Draw" : "Apps";
    modeButton.setAttribute("aria-label", drawing ? useWords : "Resume drawing");
    modeButton.setAttribute("aria-description", drawing ? "Drawing is active" : "Apps receive input");
    modeButton.title = drawing
      ? `Drawing is active. Click ${useWords} to pass clicks through. Escape finishes text or cancels an unfinished drawing first. ${RESUME_KEY} returns to drawing.`
      : `${useWords === "Use apps" ? "Your apps receive" : "The page receives"} input. Click Draw or press ${RESUME_KEY}.`;
    pill.querySelector("b").textContent = status;
    pill.title = drawing ? "Drawing is on. Click to show your tools." : state.hidden ? "Your drawings are hidden. Click to show them and your tools." : "Click to show your tools and draw.";
    pill.setAttribute("aria-description", drawing ? "Drawing is active" : "Apps receive input");
    undoButton.disabled = !doc.canUndo && !hasActiveEdit();
    redoButton.disabled = !doc.canRedo || hasActiveEdit();
    clearButton.disabled = !doc.elements.length;
    propertiesButton.setAttribute("aria-pressed", String(shown));
    propertiesButton.setAttribute("aria-description", shown ? "Shown" : "Hidden");
    propertiesButton.title = shown ? "Hide the properties below the tools." : "Show the properties below the tools.";
    const active = [state.spotlight && ["spotlight", "Spotlight", "Spot", "flashlight.on.fill", true], state.halo && ["halo", "Cursor halo", "Halo", "circle.dotted"],
      state.board === "white" && ["white", "Whiteboard", "White", "rectangle.fill", true], state.board === "black" && ["black", "Blackboard", "Black", "rectangle"]].filter(Boolean);
    const key = `${active.map((a) => a[0])}|${toolbar.dataset.rows}`;
    if (key !== stripKey) { stripKey = key; strip.replaceChildren(...active.map((a) => chip(...a))); }
    strip.hidden = !active.length;
    refreshProperties();
    fit();
  }
  // For a playground that is only live while it is on screen: off, the instance leaves draw mode, hears no keys and
  // hides its toolbar; its marks stay.
  function setEnabled(on) {
    if (enabled === Boolean(on)) return;
    if (!on) { setMode("use"); closePop(); layer.classList.remove("has-pointer"); }
    enabled = Boolean(on);
    toolbar.hidden = !enabled;
    if (!enabled) { hideRun?.finish(); morphRun?.finish(); }
    refreshToolbar();
    emit("enabled", enabled);
  }
  // Shrinking to one button and back ------------------------------------------------------------------
  // AnnotatorApp.beginToolbarMorph and runToolbarMorph, with MotionCore's ToolbarMorph: the background's frame and
  // corner radius go from one form to the other on the standard curve while both faces stay where they are, pinned to
  // the same point, and cross-fade. A toolbar that was dragged shrinks into its middle; one that sits where the page
  // put it keeps the edge the page docks it to. Reduce Motion changes the frame at once and keeps the cross-fade.
  const chrome = h("div", { class: "ink-chrome", "aria-hidden": "true" });
  toolbar.prepend(chrome);
  let collapsed = toolbar.classList.contains("is-collapsed"), morphRun = null, shrunk = null;
  const barShown = () => mounted && enabled && toolbar.isConnected && toolbar.getClientRects().length > 0 && !toolbar.classList.contains("is-away");
  // Puts the toolbar in its other form at once. Gives where it was, and for a dragged toolbar where it rests now and
  // where its full frame is.
  function reface(collapsing) {
    const area = dragArea(), was = toolbar.getBoundingClientRect();
    collapsed = collapsing;
    toolbar.classList.toggle("is-collapsed", collapsing);
    if (!collapsing) refreshToolbar();
    let rest = null, full = null;
    if (toolbar.classList.contains("is-placed") && was.width) {
      const from = { x: was.left - area.left, y: was.top - area.top, width: was.width, height: was.height }, size = { width: toolbar.offsetWidth, height: toolbar.offsetHeight };
      let frame = toolbarMorph.pinned(size, from, "center");
      // Opening the one button where it shrank goes back to the exact frame it shrank from.
      if (!collapsing && shrunk && Math.abs(shrunk.button.x - from.x) < 1 && Math.abs(shrunk.button.y - from.y) < 1 && shrunk.full.width === size.width && shrunk.full.height === size.height) frame = { ...shrunk.full };
      if (!collapsing) { frame.x = clamp(frame.x, 6, Math.max(6, area.width - size.width - 6)); frame.y = clamp(frame.y, 6, Math.max(6, area.height - size.height - 6)); }
      shrunk = collapsing ? { full: from, button: frame } : null;
      rest = frame;
      full = collapsing ? from : frame;
      toolbar.style.left = `${rest.x}px`;
      toolbar.style.top = `${rest.y}px`;
    }
    emit("collapse", collapsing);
    return { was, rest, full };
  }
  function reshape(collapsing, animate = true) {
    morphRun?.finish();
    closePop();
    const moves = animate && barShown() && !state.hidden;
    const { was, rest, full } = reface(collapsing);
    if (!moves) { refreshToolbar(); return Promise.resolve(); }
    const now = toolbar.getBoundingClientRect();
    // Both faces are needed while it changes, so the toolbar keeps its full layout until the end.
    const place = (at) => { if (at) { toolbar.style.left = `${at.x}px`; toolbar.style.top = `${at.y}px`; } };
    if (collapsing) { toolbar.classList.remove("is-collapsed"); place(full); }
    const look = getComputedStyle(toolbar), radius = parseFloat(look.borderTopLeftRadius) || 15;
    Object.assign(chrome.style, { background: look.background, boxShadow: look.boxShadow, backdropFilter: look.backdropFilter, webkitBackdropFilter: look.backdropFilter });
    // A page may show the toolbar smaller or larger than it is laid out (a transform on an ancestor): what is measured
    // on the screen is turned back into the toolbar's own pixels.
    const box = collapsing ? was : now, small = collapsing ? now : was, zoom = box.width / (toolbar.offsetWidth || box.width) || 1, round = Math.min(small.width, small.height) / 2 / zoom;
    const own = (r) => ({ x: (r.left - box.left) / zoom, y: (r.top - box.top) / zoom, width: r.width / zoom, height: r.height / zoom });
    const button = own(small), whole = { x: 0, y: 0, width: box.width / zoom, height: box.height / zoom };
    const [start, end] = collapsing ? [whole, button] : [button, whole], [fromRadius, toRadius] = collapsing ? [radius, round] : [round, radius];
    toolbar.classList.add("is-morphing");
    Object.assign(pill.style, { left: `${button.x}px`, top: `${button.y}px`, width: `${button.width}px` });
    const face = [main, second, strip, rule, props, statusLine].filter((el) => el.getClientRects().length);
    const rects = new Map([...face, pill].map((el) => [el, own(el.getBoundingClientRect())]));
    const [outgoing, incoming] = collapsing ? [face, [pill]] : [[pill], face];
    const reduced = reduceMotion(), duration = (reduced ? DURATIONS.reduced : collapsing ? toolbarMorph.collapseDuration : toolbarMorph.expandDuration) * 1000;
    const draw = (time) => {
      const frame = reduced ? end : toolbarMorph.frame(start, end, time), corner = fromRadius + (toRadius - fromRadius) * toolbarMorph.shape(time);
      Object.assign(chrome.style, { left: `${frame.x}px`, top: `${frame.y}px`, width: `${frame.width}px`, height: `${frame.height}px`, borderRadius: `${corner}px` });
      const show = (el, opacity) => {
        const r = rects.get(el);
        el.style.opacity = opacity;
        el.style.clipPath = `inset(${f(frame.y - r.y)}px ${f(r.x + r.width - frame.x - frame.width)}px ${f(r.y + r.height - frame.y - frame.height)}px ${f(frame.x - r.x)}px round ${f(corner)}px)`;
      };
      outgoing.forEach((el) => show(el, toolbarMorph.outgoingOpacity(time)));
      incoming.forEach((el) => show(el, toolbarMorph.incomingOpacity(time)));
    };
    return new Promise((done) => {
      const began = performance.now();
      let frameId = 0;
      const finish = () => {
        cancelAnimationFrame(frameId);
        morphRun = null;
        toolbar.classList.remove("is-morphing");
        for (const el of [...face, pill]) { el.style.opacity = ""; el.style.clipPath = ""; }
        Object.assign(pill.style, { left: "", top: "", width: "" });
        chrome.removeAttribute("style");
        toolbar.classList.toggle("is-collapsed", collapsing);
        place(rest);
        refreshToolbar();
        done();
      };
      const tick = (time) => {
        const t = clamp((time - began) / duration, 0, 1);
        draw(t);
        if (t >= 1) finish(); else frameId = requestAnimationFrame(tick);
      };
      morphRun = { finish };
      draw(0);
      frameId = requestAnimationFrame(tick);
    });
  }
  // The page's own switch: collapsed, the page gets the clicks again. Returns a promise for the end of the change.
  function setCollapsed(next, { animate = true } = {}) {
    next = Boolean(next);
    if (!next && state.hidden) return setHidden(false);
    if (collapsed === next) return Promise.resolve();
    if (next) setMode("use");
    return reshape(next, animate);
  }
  // The app's Shrink: the toolbar becomes its one button and keeps drawing or using apps as it was.
  const shrink = () => (collapsed || state.hidden ? Promise.resolve() : reshape(true));
  const expand = () => setCollapsed(false);
  // Settings > Toolbar > Shrink (ToolbarCollapseController): with `shrinkAfter` seconds the toolbar shrinks once the
  // pointer has been off it that long and opens as soon as the pointer is on it. A popover, a drag, typing or an
  // arrow being placed keeps it open.
  let shrinkTimer = 0, lastPointer = null;
  const shrinkDelay = Number(options.shrinkAfter) > 0 ? Number(options.shrinkAfter) : 0;
  // AnnotatorApp.toolbarMustStayOpen asks where the pointer is, not what the toolbar last heard of it.
  const pointerOnBar = () => { const r = toolbar.getBoundingClientRect(); return Boolean(lastPointer) && lastPointer[0] >= r.left && lastPointer[0] <= r.right && lastPointer[1] >= r.top && lastPointer[1] <= r.bottom; };
  function restartShrink() {
    clearTimeout(shrinkTimer);
    if (!shrinkDelay || collapsed) return;
    shrinkTimer = setTimeout(() => {
      if (pointerOnBar() || pop || moving || morphRun || hideRun || hasActiveEdit() || !barShown()) restartShrink(); else shrink();
    }, shrinkDelay * 1000);
  }
  if (shrinkDelay && interactive) {
    addEventListener("pointermove", (e) => { lastPointer = e.pointerType === "touch" ? null : [e.clientX, e.clientY]; }, { passive: true });
    toolbar.addEventListener("pointerenter", (e) => { if (collapsed && !state.hidden && e.pointerType !== "touch") expand().then(restartShrink); });
    toolbar.addEventListener("pointerleave", restartShrink);
    restartShrink();
  }

  // Show and hide (WorkspaceVisibilityAnimator, WorkspaceMotionTiming and WorkspaceLayerMotion) ------------------
  // Each drawing and the toolbar move in the style chosen in the app's settings; Slide is the app's own start.
  // Drawings leave nearest first and come back in the opposite order. The page keeps the toolbar's one button as the
  // way back (the app has its menu bar icon); `pill: false` hides that too, for a script that shows everything again.
  let visibility = resolveVisibility(options.visibility || DEFAULT_VISIBILITY), hideRun = null;
  const trails = svgEl("g", { class: "ink-trails" });
  svg.insertBefore(trails, marksG);
  function setHidden(hidden, { style = visibility, pill: keepsPill = true, animate = true } = {}) {
    hidden = Boolean(hidden);
    hideRun?.finish();
    morphRun?.finish();
    if (state.hidden === hidden) return Promise.resolve();
    const appearing = !hidden;
    if (hidden) { setMode("use"); clearLaser(); }
    state.hidden = hidden;
    overlay();
    if (appearing) {
      layer.classList.remove("is-hidden");
      plane.classList.remove("is-hidden");
      toolbar.classList.remove("is-away");
      if (collapsed) reface(false);
    }
    emit("hide", hidden);
    const marks = doc.elements.filter((m) => els.has(m.id)), bar = barShown();
    let kind = !animate ? "none" : reduceMotion() ? "fade" : resolveVisibility(style);
    // WorkspaceVisibilityAnimator.shouldAnimate: a crowded board shows and hides at once.
    if (kind !== "none" && marks.length > 64) { kind = "none"; status("This board has more than 64 shapes. It will show and hide without animation."); setTimeout(() => { if (!linear) status(""); }, 2400); }
    // The app falls back to a fade when its Dissolve particles cannot start; a web page cannot read its own pixels.
    if (kind === "dissolve") kind = "fade";
    const settle = () => {
      layer.classList.toggle("is-hidden", hidden);
      plane.classList.toggle("is-hidden", hidden);
      toolbar.classList.remove("is-busy");
      if (hidden) {
        if (!collapsed) reface(true);
        toolbar.classList.toggle("is-away", !keepsPill);
        if (keepsPill && bar && kind !== "none") toolbar.animate([{ opacity: 0 }, { opacity: 1 }], { duration: DURATIONS.fadeIn * 1000, easing: CURVE.easeOut });
      }
      refreshToolbar();
      restartShrink();
    };
    if (kind === "none" || (!bar && !marks.length)) { settle(); return Promise.resolve(); }

    const o = origin(), barRect = bar ? toolbar.getBoundingClientRect() : null, timing = workspaceTiming(kind, appearing, marks.length);
    // ToolbarSlideMotion.annotationTarget: the toolbar's middle, or a corner when there is no toolbar to go to.
    const target = barRect ? [barRect.left + barRect.width / 2 - o.left, barRect.top + barRect.height / 2 - o.top] : [24, 24];
    // Corners sends each drawing to the nearest corner of what is on screen of the layer.
    const full = layer.getBoundingClientRect(), view = { x: Math.max(full.left, 0) - o.left, y: Math.max(full.top, 0) - o.top, width: 0, height: 0 };
    view.width = Math.max(1, Math.min(full.right, innerWidth) - Math.max(full.left, 0));
    view.height = Math.max(1, Math.min(full.bottom, innerHeight) - Math.max(full.top, 0));
    const anims = [], temps = [], wraps = [];
    const run = (el, keyframes, delay, seconds, easing = keyframes.easing || "linear") => anims.push(el.animate(keyframes, { duration: seconds * 1000, delay: delay * 1000, easing, fill: "both" }));
    const flights = marks.map((m) => {
      const b = box(m), rect = { x: b.x, y: b.y, width: b.w, height: b.h }, center = [b.x + b.w / 2, b.y + b.h / 2];
      const route = kind === "corners" ? cornerRoute(m.id, center, view) : null;
      return { m, rect, center, route, target: route ? route.point : target };
    });
    const rank = new Map(distanceOrder(flights.map(({ m, center, target: to }) => ({ id: m.id, center, target: to }))).map((id, index) => [id, index]));
    layer.classList.add("is-flying");
    plane.classList.add("is-flying");
    for (const { m, rect, center, route, target: to } of flights) {
      const delay = timing.drawingDelay(rank.get(m.id)), gone = gatheredPose(rect, to), still = { x: 0, y: 0, scale: 1 }, [from, until] = appearing ? [gone, still] : [still, gone];
      let keys, fade = null;
      if (kind === "magic") {
        // MagicFlight: the way back uses another seed, so a drawing does not retrace its way out.
        const seed = appearing ? returnSeed(seedFor(m.id)) : seedFor(m.id), light = magicTrail((keys = magicFlight(from, until, seed)).points.map((p) => [p[0] + center[0], p[1] + center[1]]), seed), g = svgEl("g");
        fade = travelFade(appearing, true);
        trails.append(g);
        temps.push(g);
        for (const stroke of light.strokes) {
          const path = g.appendChild(svgEl("path", { class: "ink-trail", d: light.path, pathLength: 1, stroke: stroke.color, "stroke-width": stroke.width }));
          run(path, light.dash, delay, timing.duration);
          run(path, light.fade(stroke.peak), delay, timing.duration);
        }
        for (const spark of light.sparks) {
          const dot = g.appendChild(svgEl("circle", { class: "ink-spark", r: spark.size / 2, fill: spark.color }));
          run(dot, spark.keyframes, delay, timing.duration);
          run(dot, light.fade(spark.peak), delay, timing.duration);
        }
      } else if (kind === "slide" || kind === "corners") {
        keys = travelKeyframes(kind, from, until, { appearing, bendSign: route ? (appearing ? -route.bendSign : route.bendSign) : 1 });
        fade = travelFade(appearing);
      } else keys = usesPose(kind) ? poseKeyframes(kind, { appearing, annotation: true, seed: seedFor(m.id) }) : fadeKeyframes(appearing, timing.duration);
      for (const node of [els.get(m.id), inks.get(m.id)]) {
        if (!node) continue;
        let moved = node, own = 1;
        if (node instanceof SVGElement) {
          // A wrapper keeps the flight apart from the mark's own turn and opacity.
          moved = svgEl("g");
          moved.style.transformOrigin = `${center[0]}px ${center[1]}px`;
          node.replaceWith(moved);
          moved.append(node);
          wraps.push(moved);
        } else own = node.style.opacity === "" ? 1 : Number(node.style.opacity);
        const dim = (list) => (own === 1 ? list : Object.assign(list.map((k) => ({ ...k, opacity: Number(k.opacity) * own })), { easing: list.easing }));
        run(moved, fade || own === 1 ? keys : dim(keys), delay, timing.duration);
        if (fade) run(moved, dim(fade), delay, timing.duration);
      }
    }
    if (bar) {
      const delay = timing.toolbarDelay, seconds = timing.toolbarDuration;
      toolbar.classList.add("is-busy");
      if (kind === "magic") {
        // MagicToolbarEffect: the toolbar shrinks to a point of light that bursts into twelve sparks.
        const poof = toolbarPoof(appearing), holder = h("div", { class: "ink-poof", "aria-hidden": "true" }), area = dragArea();
        if (inParent) holder.dataset.position = "absolute";
        holder.style.left = `${barRect.left + barRect.width / 2 - area.left}px`;
        holder.style.top = `${barRect.top + barRect.height / 2 - area.top}px`;
        toolbar.after(holder);
        temps.push(holder);
        run(toolbar, poof.image, delay, seconds);
        const dot = (size, background, keyframes) => {
          const el = holder.appendChild(h("i"));
          Object.assign(el.style, { width: `${size}px`, height: `${size}px`, margin: `${-size / 2}px 0 0 ${-size / 2}px`, background });
          run(el, keyframes, delay, seconds);
        };
        dot(poof.glow.size, poof.glow.background, poof.glow.keyframes);
        for (const spark of poof.sparks) dot(spark.size, spark.color, spark.keyframes);
      } else if (kind === "slide" || kind === "corners") {
        // The toolbar's picture shrinks into its own middle, and swells a little as each gathered drawing lands in it.
        const small = Math.min(0.12, 24 / Math.max(1, barRect.width, barRect.height));
        const keys = travelKeyframes("slide", [0, 0, appearing ? small : 1], [0, 0, appearing ? 1 : small], { appearing });
        run(toolbar, keys.map(({ offset, scale }) => ({ offset, scale })), delay, seconds, keys.easing);
        run(toolbar, travelFade(appearing), delay, seconds);
        const pulse = kind === "slide" && !appearing && marks.length ? absorbPulse(flights.map(({ m }) => timing.drawingDelay(rank.get(m.id)) + timing.duration)) : null;
        if (pulse) anims.push(toolbar.animate(pulse.map((k) => ({ offset: k.offset, transform: `scale(${k.scale})` })), { duration: pulse.duration, delay: pulse.delay, fill: "both" }));
      } else if (usesPose(kind)) {
        // VisibilityPose for the toolbar: smaller moves than a drawing's, and no tilt.
        const keys = [];
        for (let step = 0; step <= 30; step++) {
          const gone = poseProgress(kind, step / 30, appearing), at = visibilityPose(kind, appearing ? 1 - gone : gone, { appearing, annotation: false, seed: 0 });
          keys.push({ offset: step / 30, scale: at.scale, opacity: at.opacity, transform: `translateY(${f(at.y / at.scale)}px)`, ...(kind === "focus" ? { filter: `blur(${f(at.blur)}px)` } : {}) });
        }
        run(toolbar, keys, delay, seconds);
      } else run(toolbar, fadeKeyframes(appearing, seconds), delay, seconds);
    }
    return new Promise((done) => {
      let timer = 0;
      const finish = () => {
        clearTimeout(timer);
        hideRun = null;
        anims.forEach((a) => a.cancel());
        temps.forEach((node) => node.remove());
        for (const g of wraps) { if (g.firstChild) g.replaceWith(g.firstChild); else g.remove(); }
        layer.classList.remove("is-flying");
        plane.classList.remove("is-flying");
        settle();
        done();
      };
      hideRun = { finish };
      timer = setTimeout(finish, timing.totalDuration * 1000);
    });
  }
  function setVisibility(style) { visibility = resolveVisibility(style); }
  toolbar.addEventListener("click", (e) => {
    if (toolbar.dataset.dragged) return;
    visitorActs(e);
    const b = e.target.closest("button");
    if (!b || pop?.contains(b)) return;
    const name = b.dataset.action;
    if (b === pill) { setCollapsed(false); setMode("draw"); restartShrink(); }
    else if (b.dataset.more) more(b.dataset.more, b);
    // WorkspaceModel.performShortcutAction: Smart draw turns shape recognition for the pen on or off.
    else if (b.dataset.item === "action.smartDraw") { state.smart = !state.smart; status(state.smart ? "Smart draw is on." : "Smart draw is off."); setTimeout(() => { if (!linear) status(""); }, 1600); refreshToolbar(); }
    // The app's Effects button chooses the last effect, Blur at first.
    else if (b === effectsButton) setTool("blur");
    else if (b.dataset.tool) setTool(b.dataset.tool);
    else if (b.dataset.stop) { if (b.dataset.stop === "white" || b.dataset.stop === "black") setBoard("desk"); else setLight(b.dataset.stop, false); }
    else if (name === "library" || name === "board" || name === "settings") more(name, b);
    else if (name === "mode") { const mode = state.mode === "draw" ? "use" : "draw"; setMode(mode); emit("shortcut", mode, byVisitor ? {} : { scripted: true }); }
    else if (name === "undo") undo();
    else if (name === "redo") redo();
    else if (name === "clear") clear();
    else if (name === "properties") setProperties(!state.properties);
    else if (name === "hide") setHidden(true);
  });
  // Like the app, press and drag the toolbar from any place on it. Sliders and fields keep their own press.
  let moving = null;
  toolbar.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || e.target.closest(".ink-slider, input, select, .ink-pop")) return;
    const r = toolbar.getBoundingClientRect(), scroller = e.pointerType === "touch" ? e.target.closest(".ink-rail, .ink-props") : null;
    moving = { x: e.clientX, y: e.clientY, left: r.left, top: r.top, active: false, id: e.pointerId, scrolls: scroller && scroller.scrollWidth > scroller.clientWidth + 1 };
  });
  if (interactive) addEventListener("pointermove", (e) => {
    if (!moving || e.pointerId !== moving.id) return;
    const dx = e.clientX - moving.x, dy = e.clientY - moving.y;
    if (!moving.active && (Math.hypot(dx, dy) < 6 || (moving.scrolls && Math.abs(dx) > Math.abs(dy)))) return;
    if (!moving.active) { moving.active = true; closePop(); toolbar.classList.add("is-moving"); toolbar.dataset.dragged = "1"; }
    const r = toolbar.getBoundingClientRect(), area = dragArea();
    toolbar.style.left = `${clamp(moving.left + dx - area.left, 6, area.width - r.width - 6)}px`;
    toolbar.style.top = `${clamp(moving.top + dy - area.top, 6, area.height - r.height - 6)}px`;
    toolbar.classList.add("is-placed");
  });
  const drop = () => {
    if (!moving) return;
    toolbar.classList.remove("is-moving");
    if (moving.active) setTimeout(() => delete toolbar.dataset.dragged, 0);
    moving = null;
  };
  if (interactive) { addEventListener("pointerup", drop); addEventListener("pointercancel", drop); }

  // Scripted marks -------------------------------------------------------------------------------
  // Where a target is on the layer. `local` measures it in the surface's own layout instead of on the screen, so
  // a mark lands on its target while the surface sits in a plane that is turned in 3D.
  function rectOf(target, local = false) {
    const el = typeof target === "string" ? document.querySelector(target) : target;
    if (local && el instanceof HTMLElement && surface.contains(el) && el !== surface) {
      let x = 0, y = 0;
      for (let node = el; node && node !== surface;) {
        const parent = node.offsetParent;
        x += node.offsetLeft; y += node.offsetTop;
        if (!parent || parent === surface) break;
        if (!surface.contains(parent)) { x -= surface.offsetLeft + surface.clientLeft; y -= surface.offsetTop + surface.clientTop; break; }
        x += parent.clientLeft; y += parent.clientTop;
        node = parent;
      }
      // Scrolled boxes on the way up move what is in them.
      for (let up = el.parentElement; up && up !== surface; up = up.parentElement) { x -= up.scrollLeft; y -= up.scrollTop; }
      return { x, y, w: el.offsetWidth, h: el.offsetHeight };
    }
    const r = el.getBoundingClientRect(), o = origin();
    return { x: r.left - o.left, y: r.top - o.top, w: r.width, h: r.height };
  }
  function resolve(spec, local) {
    if (Array.isArray(spec)) return spec;
    if (typeof spec === "function") return spec();
    const r = rectOf(spec.el, local), at = spec.at || "center";
    const x = at.includes("left") ? r.x : at.includes("right") ? r.x + r.w : r.x + r.w / 2;
    const y = at.includes("top") ? r.y : at.includes("bottom") ? r.y + r.h : r.y + r.h / 2;
    return [x + (spec.dx || 0), y + (spec.dy || 0)];
  }
  function geometry(step) {
    const pad = step.pad ?? 12, local = Boolean(step.local);
    if (BOXED.includes(step.tool) || step.tool === "pixelate") {
      const r = rectOf(step.around || step.over, local), px = step.padX ?? pad, py = step.padY ?? pad;
      return { x: r.x - px, y: r.y - py, w: r.w + px * 2, h: r.h + py * 2 };
    }
    if (step.tool === "arrow" || step.tool === "line") {
      const a = resolve(step.from, local), b = resolve(step.to, local), bend = step.bend ?? (step.tool === "line" ? 0 : 0.18), dx = b[0] - a[0], dy = b[1] - a[1];
      return { x1: a[0], y1: a[1], x2: b[0], y2: b[1], cx: a[0] + dx / 2 + dy * bend, cy: a[1] + dy / 2 - dx * bend };
    }
    if (step.tool === "highlight") {
      const r = rectOf(step.over, local), y = r.y + r.h * (step.line ?? 0.56);
      return { points: [[r.x - 4, y], [r.x + r.w + 4, y]], size: step.size ?? r.h / 4.4 };
    }
    if (step.tool === "underline") {
      const r = rectOf(step.under, local), y = r.y + r.h + (step.dy ?? 4), n = Math.max(4, Math.round(r.w / 26)), points = [];
      for (let i = 0; i <= n; i++) points.push([r.x + (r.w * i) / n, y + (i % 2 ? -3.5 : 3.5) * (step.wave ?? 1)]);
      return { points };
    }
    const p = resolve(step.at, local);
    return { x: p[0], y: p[1] };
  }
  const placeGhost = (p) => {
    ghost.style.transform = `translate(${f(p[0])}px, ${f(p[1])}px)`;
    ring.style.translate = `${f(p[0])}px ${f(p[1])}px`;
    ghost.dataset.x = p[0]; ghost.dataset.y = p[1];
  };
  async function moveGhost(to, duration = 520) {
    if (!ghost.classList.contains("is-on")) {
      placeGhost([to[0] + 90, to[1] + 70]);
      ghost.classList.add("is-on");
    }
    const from = [Number(ghost.dataset.x), Number(ghost.dataset.y)];
    await tween(clamp(dist(from, to) * 1.6, 180, duration), (t) => {
      const k = ease(t);
      placeGhost([from[0] + (to[0] - from[0]) * k, from[1] + (to[1] - from[1]) * k]);
    });
  }
  // The scripted pointer is drawn on the layer. To reach the toolbar, or a part of the page outside the layer, it
  // moves to a sheet over the whole window and keeps its place on the screen.
  const ring = h("div", { class: "ink-click", "aria-hidden": "true" }), reach = h("div", { class: "ink-reach", "aria-hidden": "true" });
  layer.append(ring);
  // A point of the layer's own layout, as it is on the screen, and back. A probe on the layer answers the first, so a
  // layer inside a plane that is turned in 3D comes out right; the way back is solved from it.
  const probe = h("i", { style: "position: absolute; width: 0; height: 0; pointer-events: none;", "aria-hidden": "true" });
  function toScreen(q) {
    if (probe.parentNode !== layer) layer.append(probe);
    probe.style.left = `calc(${q[0]}px + var(--ink-room-left, 0px))`;
    probe.style.top = `calc(${q[1]}px + var(--ink-room-top, 0px))`;
    const r = probe.getBoundingClientRect();
    return [r.left, r.top];
  }
  function toLayer(p) {
    const o = origin();
    let q = [p[0] - o.left, p[1] - o.top];
    for (let i = 0; i < 5; i++) {
      const s = toScreen(q), off = [p[0] - s[0], p[1] - s[1]];
      if (Math.hypot(off[0], off[1]) < 0.05) break;
      const sx = toScreen([q[0] + 1, q[1]]), sy = toScreen([q[0], q[1] + 1]), a = sx[0] - s[0], b = sy[0] - s[0], c = sx[1] - s[1], d = sy[1] - s[1], det = a * d - b * c;
      if (Math.abs(det) < 1e-6) break;
      q = [q[0] + (off[0] * d - off[1] * b) / det, q[1] + (off[1] * a - off[0] * c) / det];
    }
    return q;
  }
  function ghostSpace(space) {
    if ((space === "screen") === (ghost.parentNode === reach)) return;
    const at = [Number(ghost.dataset.x) || 0, Number(ghost.dataset.y) || 0], next = space === "screen" ? toScreen(at) : toLayer(at);
    if (space === "screen") { if (!reach.isConnected) document.body.append(reach); reach.append(ring, ghost); } else layer.append(ring, ghost);
    if (ghost.classList.contains("is-on")) placeGhost(next);
  }
  // Moves the pointer to an element, wherever it is: its middle, or `at` from its top left corner. Gives that point
  // on the screen, and on the layer when the pointer is there. With `local` the element is measured in the surface's
  // own layout, like a scripted mark, so the pointer lands on it in a plane that is turned in 3D.
  async function reachFor(el, cursor = true, { at = null, local = false } = {}) {
    const mine = el instanceof Element && surface.contains(el) && !toolbar.contains(el);
    if (local && mine) {
      const r = rectOf(el, true), q = at ? [r.x + at[0], r.y + at[1]] : [r.x + r.w / 2, r.y + r.h / 2];
      if (cursor) { ghostSpace("layer"); await moveGhost(q); }
      return { screen: toScreen(q), layer: q };
    }
    const r = el.getBoundingClientRect(), p = at ? [r.left + at[0], r.top + at[1]] : [r.left + r.width / 2, r.top + r.height / 2], o = origin();
    const inside = mine && p[0] >= o.left && p[0] <= o.right && p[1] >= o.top && p[1] <= o.bottom, q = inside ? [p[0] - o.left, p[1] - o.top] : null;
    if (cursor) { ghostSpace(inside ? "layer" : "screen"); await moveGhost(q || p); }
    return { screen: p, layer: q };
  }
  // The pointer's click (CursorHighlightRenderer.pulseScale): the app's ring, 36 points wide, swells by 18% and
  // settles in 0.45 s. In the app the ring is always there while the pointer halo is on; here it is there for the
  // click only, and then goes with the app's short fade-out.
  function clickGhost() {
    if (reduceMotion() || !ghost.classList.contains("is-on")) return Promise.resolve();
    const pulse = clickPulse(), gone = DURATIONS.fadeOut * 1000, total = pulse.duration + gone;
    ring.animate(pulse, { duration: pulse.duration });
    return ring.animate([{ opacity: 1 }, { opacity: 1, offset: pulse.duration / total, easing: CURVE.easeIn }, { opacity: 0 }], { duration: total }).finished.catch(() => {});
  }
  // Shows one of the toolbar's own buttons being pressed.
  async function press(control, cursor = true) {
    if (!control || !barShown() || !control.getClientRects().length) return;
    await reachFor(control, cursor);
    control.classList.add("is-pressed");
    if (cursor) clickGhost();
    await sleep(130);
    control.classList.remove("is-pressed");
  }

  // Using the page as a person would (perform's click, drag and type steps). The events are real DOM events on the
  // element under the pointer, in the order a mouse makes them; the page's own handlers and the browser's default
  // actions for a click run. Hover and press cannot be faked for CSS, so the element also gets `data-ink-hover` and
  // `data-ink-active` for a page that wants to style them like :hover and :active.
  const GHOST_POINTER = 9001;
  let hovered = null;
  const find = (target) => (typeof target === "string" ? document.querySelector(target) : target);
  function fire(el, type, p, more = {}) {
    const init = { bubbles: !/enter$|leave$/.test(type), cancelable: !/enter$|leave$/.test(type), composed: true, view: window, clientX: p[0], clientY: p[1], screenX: p[0] + (window.screenX || 0), screenY: p[1] + (window.screenY || 0), button: 0, buttons: 0, detail: /click|down|up/.test(type) ? 1 : 0, ...more };
    const event = type.startsWith("pointer") ? new PointerEvent(type, { pointerId: GHOST_POINTER, pointerType: "mouse", isPrimary: true, width: 1, height: 1, ...init }) : new MouseEvent(type, init);
    return el.dispatchEvent(event);
  }
  function hover(el, p) {
    if (hovered === el) { if (el) { fire(el, "pointermove", p); fire(el, "mousemove", p); } return; }
    if (hovered?.isConnected) {
      hovered.removeAttribute("data-ink-hover");
      for (const type of ["pointerout", "pointerleave", "mouseout", "mouseleave"]) fire(hovered, type, p, { relatedTarget: el });
    }
    const before = hovered;
    hovered = el;
    if (!el) return;
    el.setAttribute("data-ink-hover", "");
    for (const type of ["pointerover", "pointerenter", "mouseover", "mouseenter", "pointermove", "mousemove"]) fire(el, type, p, { relatedTarget: before });
  }
  // What is under the pointer: the element a real pointer would hit, if it belongs to the target.
  function hitOf(el, p) {
    const hit = document.elementFromPoint(p[0], p[1]);
    return hit && (el.contains(hit) || hit === el) ? hit : el;
  }
  // A script never takes the keys from the visitor: whatever is done inside `quiet` leaves the focus where it was,
  // also when the page's own handler moves it. `focus: true` on a step presses like a person and lets focus follow.
  function quiet(focus, run) {
    if (focus) return run();
    const had = document.activeElement, result = run(), now = document.activeElement;
    if (now !== had) { if (had && had !== document.body && had.isConnected) had.focus({ preventScroll: true }); else now?.blur?.(); }
    return result;
  }
  function pressDown(el, p, focus = false) {
    el.setAttribute("data-ink-active", "");
    fire(el, "pointerdown", p, { buttons: 1 });
    if (!fire(el, "mousedown", p, { buttons: 1 }) || !focus) return;
    // A real press gives the keys to what was pressed, or takes them from what had them.
    const focusable = el.closest('input, textarea, select, button, a[href], [tabindex], [contenteditable=""], [contenteditable="true"], [contenteditable="plaintext-only"]');
    if (focusable && !focusable.disabled) focusable.focus({ preventScroll: true });
    else if (document.activeElement && document.activeElement !== document.body && !toolbar.contains(document.activeElement)) document.activeElement.blur();
  }
  function pressUp(el, p) {
    el.removeAttribute("data-ink-active");
    fire(el, "pointerup", p);
    fire(el, "mouseup", p);
  }
  // A page that keeps the pointer for a drag asks the browser to capture it. The scripted pointer is not one the
  // browser knows, so for the length of a drag the capture is kept here and the moves go to whoever asked.
  async function captured(run) {
    const proto = Element.prototype, { setPointerCapture, releasePointerCapture, hasPointerCapture } = proto;
    let holder = null;
    proto.setPointerCapture = function (id) { if (id === GHOST_POINTER) holder = this; else setPointerCapture.call(this, id); };
    proto.releasePointerCapture = function (id) { if (id === GHOST_POINTER) { if (holder === this) holder = null; } else releasePointerCapture.call(this, id); };
    proto.hasPointerCapture = function (id) { return id === GHOST_POINTER ? holder === this : hasPointerCapture.call(this, id); };
    try { await run(() => holder); } finally { Object.assign(proto, { setPointerCapture, releasePointerCapture, hasPointerCapture }); }
  }
  // Types one character into a field as a key press would: keydown, beforeinput, the change itself, input, keyup.
  function typeKey(el, char) {
    const enter = char === "\n", back = char === "\b", key = enter ? "Enter" : back ? "Backspace" : char;
    const code = enter ? "Enter" : back ? "Backspace" : /^[a-z]$/i.test(char) ? `Key${char.toUpperCase()}` : /^\d$/.test(char) ? `Digit${char}` : char === " " ? "Space" : "";
    const keys = { key, code, bubbles: true, cancelable: true, composed: true, view: window };
    const field = el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement;
    if (el.dispatchEvent(new KeyboardEvent("keydown", keys))) {
      const inputType = back ? "deleteContentBackward" : enter ? "insertLineBreak" : "insertText", data = back || enter ? null : char;
      if (enter && el instanceof HTMLInputElement) el.form?.requestSubmit?.();
      else if (el.isContentEditable) {
        // The browser's own editing needs the caret in the element; without the keys, the text is changed directly.
        if (!(el.contains(document.activeElement) && document.execCommand(back ? "delete" : enter ? "insertLineBreak" : "insertText", false, data))) {
          if (back) el.textContent = el.textContent.slice(0, -1); else el.append(enter ? "\n" : data || "");
          el.dispatchEvent(new InputEvent("input", { inputType, data, bubbles: true, composed: true }));
        }
      } else if (field && el.dispatchEvent(new InputEvent("beforeinput", { inputType, data, bubbles: true, cancelable: true, composed: true }))) {
        const insert = enter ? "\n" : back ? "" : char;
        try {
          // A field that does not have the keys is written at its end.
          const mine = document.activeElement === el, length = el.value.length, start = mine ? el.selectionStart ?? length : length, end = mine ? el.selectionEnd ?? start : length;
          el.setRangeText(insert, back && start === end ? Math.max(0, start - 1) : start, end, "end");
        } catch {
          // Fields such as email and number have no text range; the value is set the way the browser sets it.
          const set = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), "value")?.set;
          set?.call(el, back ? el.value.slice(0, -1) : el.value + insert);
        }
        el.dispatchEvent(new InputEvent("input", { inputType, data, bubbles: true, composed: true }));
      }
    }
    el.dispatchEvent(new KeyboardEvent("keyup", keys));
  }
  // The toolbar button that stands for a tool: its own, Effects for blur, the Tool library for the rest.
  const barButton = (tool) => rail.querySelector(`[data-tool="${barIdentity(tool)}"]`) || effectsButton || libraryButton;
  const showTool = (tool) => { state.tool = tool; layer.dataset.tool = tool; syncStyle(); refreshToolbar(); };
  // Steps that are not marks: the story a page tells with the scripted pointer.
  //   { mode: "draw" | "use", via: "key" }   presses the toolbar's Draw button, or sends the key
  //   { tool: "arrow" } or { pick: "arrow" } picks a tool in the toolbar (and starts drawing, as the app does)
  //   { click: target }                      moves to an element of the page and clicks it; goes to Use apps first
  //   { drag: { el, by: [dx, dy] } }         presses it, moves and lets go (also `to: target`, `from: [x, y]`, `duration`)
  //   { type: { el, text, speed } }          presses a field and writes into it, one character at a time ("\n" is Return, "\b" deletes)
  //   { hide: true | false, style, pill }    Hide drawings and tools, and back, in one of the app's styles
  //   { shrink: true | false }               the toolbar shrinks to its one button, and opens again
  // click, drag and type leave the keyboard focus with the visitor; `focus: true` lets it follow the press. With
  // `local: true` (on the step or on perform) the pointer's place comes from the surface's own layout.
  const MARK_KEYS = ["around", "over", "under", "from", "to", "at", "text", "n"];
  async function useApps(cursor) {
    if (state.mode === "use") return;
    await press(modeButton, cursor);
    setMode("use");
    await sleep(160);
  }
  async function story(step, cursor) {
    if (step.mode) {
      if (step.mode === state.mode) return true;
      if (step.via === "key") emit("shortcut", step.mode, { key: step.mode === "draw" ? RESUME_KEY : "Esc", scripted: true });
      else await press(collapsed ? pill : modeButton, cursor);
      if (step.mode === "draw" && collapsed) await setCollapsed(false);
      setMode(step.mode);
      return true;
    }
    const picked = step.pick || (step.tool && !MARK_KEYS.some((key) => step[key] !== undefined) ? step.tool : null);
    if (picked) {
      if (!TOOLS[picked]) return true;
      if (collapsed) await setCollapsed(false);
      await press(barButton(picked), cursor);
      setTool(picked);
      return picked;
    }
    if (step.click) {
      const el = find(step.click);
      if (!el) return true;
      await useApps(cursor);
      const p = (await reachFor(el, cursor, { local: step.local })).screen, hit = hitOf(el, p), focus = Boolean(step.focus);
      quiet(focus, () => { hover(hit, p); pressDown(hit, p, focus); });
      if (cursor) clickGhost();
      await sleep(step.hold ?? 110);
      quiet(focus, () => { pressUp(hit, p); fire(hit, "click", p); });
      return true;
    }
    if (step.drag) {
      const spec = step.drag, el = find(spec.el), focus = Boolean(spec.focus ?? step.focus);
      if (!el) return true;
      await useApps(cursor);
      const start = await reachFor(el, cursor, { local: Boolean(spec.local ?? step.local), at: spec.from }), hit = hitOf(el, start.screen), flat = Boolean(spec.local ?? step.local) && start.layer;
      let by = spec.by || [0, 0];
      if (spec.to) {
        const goal = find(spec.to), r = flat ? rectOf(goal, true) : goal.getBoundingClientRect();
        by = flat ? [r.x + r.w / 2 - start.layer[0], r.y + r.h / 2 - start.layer[1]] : [r.left + r.width / 2 - start.screen[0], r.top + r.height / 2 - start.screen[1]];
      }
      // With `local` the path lies on the layer: `by` is in the surface's own pixels, and the page hears where each
      // point of it is on the screen.
      const at = (k) => (flat ? toScreen([start.layer[0] + by[0] * k, start.layer[1] + by[1] * k]) : [start.screen[0] + by[0] * k, start.screen[1] + by[1] * k]);
      const pointer = cursor ? [Number(ghost.dataset.x), Number(ghost.dataset.y)] : null;
      quiet(focus, () => hover(hit, start.screen));
      await captured(async (holder) => {
        quiet(focus, () => pressDown(hit, start.screen, focus));
        if (cursor) clickGhost();
        await sleep(90);
        const move = (k) => {
          const p = at(k), target = holder() || hit;
          quiet(focus, () => { fire(target, "pointermove", p, { buttons: 1 }); fire(target, "mousemove", p, { buttons: 1 }); });
          if (pointer) placeGhost([pointer[0] + by[0] * k, pointer[1] + by[1] * k]);
        };
        if (reduceMotion()) move(1); else await tween(spec.duration ?? clamp(Math.hypot(by[0], by[1]) * 3, 320, 1100), (t) => move(ease(t)));
        quiet(focus, () => pressUp(holder() || hit, at(1)));
      });
      return true;
    }
    if (step.type) {
      const spec = step.type, el = find(spec.el), focus = Boolean(spec.focus ?? step.focus);
      if (!el) return true;
      await useApps(cursor);
      // The pointer goes to the field and presses it. The keys stay with the visitor unless the step says `focus: true`:
      // the text is written into the field and the page hears `input` for each character.
      if (focus ? document.activeElement !== el : cursor) {
        const p = (await reachFor(el, cursor, { local: Boolean(spec.local ?? step.local) })).screen, hit = hitOf(el, p);
        quiet(focus, () => { hover(hit, p); pressDown(hit, p, focus); });
        if (cursor) clickGhost();
        await sleep(90);
        quiet(focus, () => { pressUp(hit, p); fire(hit, "click", p); });
        if (focus && document.activeElement !== el) el.focus?.({ preventScroll: true });
      }
      for (const char of String(spec.text ?? "")) { quiet(focus, () => typeKey(el, char)); await sleep(spec.speed ?? 60); }
      return true;
    }
    if (step.hide !== undefined) {
      if (Boolean(step.hide) === state.hidden) return true;
      if (step.hide) await press(shows("hide") ? hideButton : null, cursor);
      else if (!toolbar.classList.contains("is-away")) await press(pill, cursor);
      if (cursor && step.hide) hideGhost();
      await setHidden(Boolean(step.hide), { style: step.style, pill: step.pill });
      return true;
    }
    if (step.shrink !== undefined) {
      // The app shrinks once the pointer has left the toolbar, and opens when the pointer comes back to it.
      if (!step.shrink && collapsed && !state.hidden) await reachFor(pill, cursor && barShown());
      await (step.shrink ? shrink() : expand());
      return true;
    }
    return false;
  }
  async function perform(steps, { cursor = true, showTool: shows = false, anchored = true, local = false, resting, signal } = {}) {
    const made = [], toolBefore = state.tool;
    let picked = null;
    for (const given of steps) {
      if (signal?.aborted) break;
      const step = local && given.local === undefined && !given.wait && !given.run ? { ...given, local: true } : given;
      if (step.wait) { await sleep(step.wait); continue; }
      if (step.run) { await step.run(); continue; }
      const acted = await story(step, cursor);
      if (acted) { if (typeof acted === "string") picked = acted; if (step.pause) await sleep(step.pause); continue; }
      if (ghost.parentNode === reach) ghostSpace("layer");
      const tool = step.tool === "underline" ? "pen" : step.tool, shown = tool === "pixelate" ? "blur" : tool;
      if (shows && TOOLS[shown] && !hasActiveEdit()) { showTool(shown); barButton(tool).classList.add("is-demo"); }
      const g = geometry(step);
      const base = { type: tool === "line" ? "arrow" : tool, color: step.color, size: step.size || scriptSize, anchor: anchored ? step : null, ...(tool === "line" ? { head: false } : {}), ...g };
      for (const prop of ["rough", "pattern", "fill", "fillStyle", "round", "start", "end", "headScale", "amount", "effect"]) if (step[prop] !== undefined) base[prop] = step[prop];
      const duration = step.duration ?? 700;
      if (tool === "text") {
        if (cursor) await moveGhost([g.x, g.y + 14]);
        const m = add({ ...base, text: "" }, "script");
        made.push(m);
        for (const char of step.text) { const now = current(m); if (!now) break; now.text += char; render(now); await sleep(step.speed ?? 34); }
      } else if (tool === "counter") {
        if (cursor) await moveGhost([g.x, g.y]);
        const m = add({ ...base, n: step.n ?? made.filter((x) => x.tool === "counter").length + 1 }, "script");
        els.get(m.id).classList.add("is-new");
        made.push(m);
        await sleep(220);
      } else if (tool === "blur" || tool === "pixelate") {
        if (cursor) await moveGhost([g.x, g.y]);
        const m = add({ ...base, w: 0, h: 0 }, "script");
        made.push(m);
        await tween(duration, (t) => {
          const k = ease(t), now = current(m);
          if (now) { now.w = g.w * k; now.h = g.h * k; render(now); }
          if (cursor) placeGhost([g.x + g.w * k, g.y + g.h * k]);
        });
      } else {
        const m = add(base, "script");
        made.push(m);
        const paths = [...strokeHost(m).querySelectorAll(".ink-stroke")], lengths = paths.map((p) => p.getTotalLength());
        paths.forEach((p, i) => { p.style.strokeDasharray = `${lengths[i]} ${lengths[i]}`; p.style.strokeDashoffset = lengths[i]; });
        if (cursor && paths[0]) { const s = paths[0].getPointAtLength(0); await moveGhost([s.x, s.y]); }
        for (let i = 0; i < paths.length; i++) {
          await tween(i ? duration * 0.28 : duration, (t) => {
            const k = ease(t);
            paths[i].style.strokeDashoffset = lengths[i] * (1 - k);
            if (cursor) { const q = paths[i].getPointAtLength(lengths[i] * k); placeGhost([q.x, q.y]); }
          });
        }
        paths.forEach((p) => { p.style.strokeDasharray = ""; p.style.strokeDashoffset = ""; });
      }
      toolbar.querySelector(".is-demo")?.classList.remove("is-demo");
      if (step.pause) await sleep(step.pause);
    }
    // The toolbar goes back to the tool the visitor had before the script showed its own.
    // The toolbar goes back to the tool the visitor had before the script showed its own: or to the one the script
    // picked itself, or to `resting` when the page names one.
    const home = resting ?? picked ?? toolBefore;
    if ((shows || resting) && state.tool !== home && TOOLS[home] && !hasActiveEdit()) showTool(home);
    refreshToolbar();
    emit("change", doc.elements);
    return made;
  }
  function hideGhost() {
    ghost.classList.remove("is-on");
    hover(null, [Number(ghost.dataset.x) || 0, Number(ghost.dataset.y) || 0]);
    // Once it has faded, a pointer that went to the toolbar is put back on the layer, where pages look for it.
    if (ghost.parentNode === reach) setTimeout(() => { if (!ghost.classList.contains("is-on")) ghostSpace("layer"); }, 320);
  }
  // Scripted marks follow their targets when the page reflows, until someone moves them.
  function reflow() {
    for (const m of doc.elements) {
      if (!m.anchor || gesture) continue;
      try { place(m, geometry(m.anchor)); render(m); } catch { /* the target left the page */ }
    }
    overlay();
  }
  let reflowTimer = 0;
  const reflowSoon = () => { clearTimeout(reflowTimer); reflowTimer = setTimeout(reflow, 120); };
  addEventListener("resize", () => { fit(); reflowSoon(); });
  if (window.ResizeObserver) {
    // The page can grow or shrink without the window changing size, for example when a font or a picture arrives.
    new ResizeObserver(reflowSoon).observe(surface);
    if (toolbar.parentElement) new ResizeObserver(fit).observe(toolbar.parentElement);
  }
  document.fonts?.ready.then(() => { reflow(); fit(); });

  syncStyle();
  refreshToolbar();
  const STYLE_NAME = { size: "width", rough: "roughness", amount: "effectAmount" };
  return {
    layer, toolbar, state, styles,
    on(name, fn) { (listeners[name] ||= []).push(fn); return this; },
    setMode, setTool, setColor, setCollapsed, select, undo, redo, clear, perform, hideGhost, moveGhost, reflow, rectOf, box,
    setBoard, setProperties, setAppearance, setHidden, setEnabled, more, fit,
    // The app's motion: shrink() and expand() are its Shrink; setHidden(hidden, { style }) shows and hides in one of
    // its styles ("slide", "magic", "corners", "pop", "drop", "focus", "fade", "none"); each returns a promise.
    shrink, expand, setVisibility, clickGhost, setRoom,
    // Settings > Toolbar, from a script: the bar's tools and added buttons, in order (the app's names, as in the
    // `toolbarItems` option). The bar is laid out again at once; the caller animates the change if it wants to.
    setToolbarItems(next) {
      const wanted = next.filter(known);
      items.splice(0, items.length, ...wanted);
      fillRail();
      fitted = "";
      revealed = "";
      refreshToolbar();
      return [...items];
    },
    get toolbarItems() { return [...items]; },
    get room() { return room && { ...room }; },
    // Moves a toolbar the visitor dragged by this much, so it keeps its place on the screen when the corner of its
    // parent moves (a growing stage). A toolbar that sits where the page put it is left alone.
    shiftToolbar(dx, dy) {
      if (!toolbar.classList.contains("is-placed")) return;
      toolbar.style.left = `${(parseFloat(toolbar.style.left) || 0) + dx}px`;
      toolbar.style.top = `${(parseFloat(toolbar.style.top) || 0) + dy}px`;
    },
    get visibility() { return visibility; },
    get hidden() { return state.hidden; },
    get enabled() { return enabled; },
    get collapsed() { return collapsed; },
    toolbarRect: () => toolbar.getBoundingClientRect(),
    // Draws a mark again after its geometry or style was changed from outside.
    render(mark) {
      const m = current(mark);
      if (!m) return m;
      if (mark !== m && typeof mark === "object") Object.assign(m, { frame: { ...mark.frame }, points: mark.points.map((p) => [p[0], p[1]]), text: mark.text, style: { ...mark.style } });
      render(m);
      overlay();
      return m;
    },
    placeGhost(p) { placeGhost(p); ghost.classList.add("is-on"); },
    setSpotlight: (on) => setLight("spotlight", on), setHalo: (on) => setLight("halo", on),
    // Changes what the next mark of a tool looks like, for example setStyle("pen", { roughness: 2 }).
    // Makes one of the app's gradients the stroke color of shapes, lines, arrows, the pen and text, and of what is
    // selected: setGradient("ocean").
    setGradient(name) {
      const value = named(name);
      for (const key of ["rect", "diamond", "ellipse", "arrow", "line", "pen", "text"]) Paint.setGradient(styles[key], value);
      if (doc.selection.size) { doc.editSelection((m) => { if (paintable(m)) Paint.setGradient(m.style, value); }); paint(); emit("change", doc.elements); }
      syncStyle();
      refreshToolbar();
      if (pop && popKind === "color") { fillPop(); placePop(); }
    },
    // `gradient` takes one of the app's preset names ("sunset") or { colors, angle }; `color` is a solid color.
    setStyle(tool, patch) {
      const style = styles[tool];
      if (style) for (const key in patch) {
        if (key === "gradient") {
          const value = typeof patch[key] === "string" ? GRADIENT_PRESETS.find((choice) => choice.id === patch[key])?.gradient : patch[key];
          if (value) Paint.setGradient(style, value); else style.strokeGradient = null;
        } else if (key === "color") Paint.setColor(style, patch[key], true);
        else style[STYLE_NAME[key] || key] = patch[key];
      }
      syncStyle();
      refreshProperties(true);
    },
    add(mark) { const m = add(mark, "script"); refreshToolbar(); emit("change", doc.elements); return m; },
    remove(id) { remove(id); refreshToolbar(); emit("change", doc.elements); },
    marks: () => doc.elements,
  };
}
