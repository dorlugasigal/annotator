// The toolbar's pictures: the app's icons (app-icons.js, Lucide and Annotator's own drawings, never SF Symbols)
// and the small samples in the properties row, redrawn from the app's own drawing code
// (apps/macos/Annotator/NativeInspectorComponents.swift, apps/windows/src/Annotator.App/Toolbar/PropertiesControls.cs).
import { APP_ICONS } from "./app-icons.js";

const f = (n) => Math.round(n * 100) / 100;

// An icon from the app's set on its 24-unit grid. `line` is the stroke in CSS pixels, whatever the size,
// so every icon has the same weight; without it the stroke scales with the icon, as Lucide's does.
export function symbol(name, size, { line, filled = false, className = "" } = {}) {
  const parts = APP_ICONS[name];
  if (!parts) throw new Error(`No icon for ${name}`);
  const stroke = line ? `stroke-width="${line}"` : 'stroke-width="2"';
  const body = parts.map((part) =>
    `<path d="${part.d}"${part.fill || filled ? ' fill="currentColor"' : ""}${line ? ' vector-effect="non-scaling-stroke"' : ""}/>`).join("");
  return `<svg class="ink-symbol ${className}" viewBox="0 0 24 24" width="${size}" height="${size}" style="--s:${size}px" fill="none" stroke="currentColor" ${stroke} stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
}

// The Annotator mark, cut to its own outline so it can be sized by its ink (website/public/mark.svg).
export const mark = (width) =>
  `<svg class="ink-brand" viewBox="15.08 16.81 32.92 29.6" width="${width}" height="${f(width * 0.899)}" aria-hidden="true"><path fill="currentColor" d="M39 19c-17-10-32 17-19 26 6 4 13-1 17-9l-1 9h7l5-28h-8zm-4 7c-2 10-8 16-11 13-4-4 4-18 11-13z"/></svg>`;

// Samples sit on the app's 36-point square.
const sample = (body, extra = "") =>
  `<svg class="ink-sample" viewBox="0 0 36 36" width="36" height="36" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"${extra}>${body}</svg>`;

export const patternSample = (pattern) => sample(
  `<path d="M7 18H29"${pattern === "dashed" ? ' stroke-dasharray="7.5 4.5"' : pattern === "dotted" ? ' stroke-dasharray="0.01 4.5"' : ""}/>`);

// InspectorGlyph.roughness: one squiggle, the squiggle drawn twice, then two looser ones.
const SQUIGGLES = {
  0: ["M8 21.5C12 20 16.5 16 19.5 15.5C22 15 18 19.5 20 20C21.5 20.5 25 18.5 28 17.5"],
  2: ["M8 21.5C12 20 16.5 16 19.5 15.5C22 15 18 19.5 20 20C21.5 20.5 25 18.5 28 17.5",
    "M8.3 23C12.5 21 16.5 17.5 19.8 17C22 16.6 19 20.8 20.8 21.2C22.5 21.6 25.5 19.6 28.2 18.6"],
  6: ["M7.5 20C10 19 17 14.5 20.5 13.5C21.5 13.2 18.2 18.6 19 19C20 19.5 26 16.5 28.5 15.5",
    "M8.5 23.5C11 22.5 18 17.8 22 17.2C23 17 19.8 21.8 20.6 22.3C21.6 22.8 27 20 29.5 19"],
};
export const roughnessSample = (value) => sample((SQUIGGLES[value] || SQUIGGLES[0]).map((d) => `<path d="${d}"/>`).join(""));

// InspectorGlyph.edges: a dotted square with its top-left corner drawn solid, sharp or rounded.
export function edgesSample(rounded) {
  const dots = 'stroke-dasharray="1.5 2" stroke-dashoffset="1.5" stroke-linecap="butt"';
  if (!rounded) return sample(`<path d="M9 18V27H27V9H18" ${dots}/><path d="M9 18V9H18"/>`);
  return sample(`<path d="M9 18V21C9 24.3 11.7 27 15 27H21C24.3 27 27 24.3 27 21V15C27 11.7 24.3 9 21 9H18" ${dots}/><path d="M9 18V15C9 11.7 11.7 9 15 9H18"/>`);
}

export function fillSample(style) {
  const box = '<rect x="8" y="8" width="21" height="21"/>';
  if (style === "solid") return sample(`<rect x="8" y="8" width="21" height="21" fill="currentColor" fill-opacity=".5"/>`);
  const lines = [];
  for (let i = -3; i <= 3; i++) lines.push(`M${8 + Math.max(0, i * 5.25)} ${8 + Math.max(0, -i * 5.25)}L${29 + Math.min(0, i * 5.25)} ${29 + Math.min(0, -i * 5.25)}`);
  if (style === "crosshatch") for (let i = -3; i <= 3; i++) lines.push(`M${29 - Math.max(0, i * 5.25)} ${8 + Math.max(0, -i * 5.25)}L${8 - Math.min(0, i * 5.25)} ${29 + Math.min(0, -i * 5.25)}`);
  return sample(`${box}<path d="${lines.join("")}" stroke-width="1"/>`);
}

// InspectorSampleView's route sample: a straight or a curved arrow.
export const routeSample = (curved) => sample(curved
  ? '<path d="M7 28C7 8 15 8 28 8"/><path d="M23 3L28 8L23 13"/>'
  : '<path d="M7 28L28 8"/><path d="M21 8H28V15"/>', ' stroke-width="1.8"');

// One end of a line, drawn as the app draws an arrowhead. `x` is the tip and `dir` points along the line toward it.
export function headPaths(kind, x, y, dx, dy, size) {
  const px = -dy, py = dx, at = (along, across) => `${f(x - dx * along + px * across)} ${f(y - dy * along + py * across)}`;
  const half = size * 0.48;
  switch (kind) {
    case "open": return [{ d: `M${at(size, half)}L${at(0, 0)}L${at(size, -half)}` }];
    case "filled": return [{ d: `M${at(size, half)}L${at(0, 0)}L${at(size, -half)}Z`, fill: true }];
    case "triangleOutline": return [{ d: `M${at(size, half)}L${at(0, 0)}L${at(size, -half)}Z`, hollow: true }];
    case "circle": case "circleOutline": {
      const r = size * 0.42;
      return [{ d: `M${at(0, 0)}A${f(r)} ${f(r)} 0 1 0 ${at(r * 2, 0)}A${f(r)} ${f(r)} 0 1 0 ${at(0, 0)}Z`, fill: kind === "circle", hollow: kind !== "circle" }];
    }
    case "diamond": case "diamondOutline":
      return [{ d: `M${at(0, 0)}L${at(size * 0.6, half * 0.8)}L${at(size * 1.2, 0)}L${at(size * 0.6, -half * 0.8)}Z`, fill: kind === "diamond", hollow: kind !== "diamond" }];
    case "bar": return [{ d: `M${at(0, half)}L${at(0, -half)}` }];
    case "square": return [{ d: `M${at(0, half * 0.8)}L${at(half * 1.6, half * 0.8)}L${at(half * 1.6, -half * 0.8)}L${at(0, -half * 0.8)}Z`, fill: true }];
    default: return [];
  }
}
export function headSample(kind, start) {
  const parts = headPaths(kind, start ? 7 : 29, 18, start ? -1 : 1, 0, 7.5);
  const cut = kind === "none" || kind === "open" || kind === "bar" ? 0 : kind.startsWith("circle") ? 6.3 : kind.startsWith("diamond") ? 9 : kind === "square" ? 5.8 : 7.5;
  const line = start ? `M${7 + cut} 18H29` : `M7 18H${29 - cut}`;
  return sample(`<path d="${line}"/>${parts.map((p) => `<path d="${p.d}"${p.fill ? ' fill="currentColor"' : ""}/>`).join("")}`);
}

// The pressure sample: the app's five-point pen stroke, thin at both ends and heavy in the middle.
export const pressureSample = () => sample(
  '<path d="M6 22.2C8 20 9.6 17.8 11.2 16.2C13 14 16.3 12.3 18.2 13.2C20.2 14.2 21.2 16.3 23 17C25 17.5 27.2 17 29 16.9C27.2 17.5 25.2 18.3 23.2 18.6C21 19 19.6 18.3 17.5 18.2C15.2 18.2 13.3 18.8 11.6 19.6C9.6 20.5 7.8 21.5 6 22.2Z" fill="currentColor" stroke-width=".8"/>');

export const HEADS = [
  ["none", "None"], ["open", "Arrow"], ["filled", "Filled triangle"], ["triangleOutline", "Outlined triangle"],
  ["circle", "Filled circle"], ["circleOutline", "Outlined circle"], ["diamond", "Filled diamond"], ["diamondOutline", "Outlined diamond"],
  ["bar", "Bar"], ["square", "Square"],
];

// InspectorColorPalette's solid colors, in its order, with the names VoiceOver reads.
export const APP_PALETTE = [
  ["Dark ink", "#1F2629"], ["Slate gray", "#545E63"], ["White", "#FFFFFF"], ["Brown", "#87665C"],
  ["Teal", "#00969E"], ["Blue", "#2B73E0"], ["Purple", "#7D54B8"], ["Mauve", "#B052A8"], ["Rose", "#E34266"],
  ["Green", "#389E59"], ["Emerald", "#009E82"], ["Yellow", "#F5E338"], ["Orange", "#F29121"], ["Coral", "#F04538"],
];
// InkShortcuts.swift: a letter picks an ink color while you draw.
export const INK_KEYS = {
  r: ["Red", "#F04538"], g: ["Green", "#47B066"], b: ["Blue", "#2B73E0"], y: ["Yellow", "#F5E338"], o: ["Orange", "#FFA83B"],
  p: ["Pink", "#E34266"], c: ["Cyan", "#0099B3"], w: ["White", "#FFFFFF"], k: ["Black", "#000000"],
};
