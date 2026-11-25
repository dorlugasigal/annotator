// How the web demo paints what the app paints: a port of SceneRenderer.draw, LaserTrailRenderer and the Smart Draw
// morph in DrawingCanvas.swift, kept free of the DOM. It returns SVG path data and stroke settings; ink.js makes
// the nodes and render-test.html compares the result with the app's pictures in reference/conformance/render.
import { P, R, Tool, pathFor, pathData, arrowHead, LaserTrail } from "./annotation-core.js";

const n = (v) => Math.round(v * 1000) / 1000;
const line = (points, close) => `M${points.map((p) => `${n(p[0])} ${n(p[1])}`).join("L")}${close ? "Z" : ""}`;
const circle = (c, r) => `M${n(c[0] + r)} ${n(c[1])}A${n(r)} ${n(r)} 0 1 1 ${n(c[0] - r)} ${n(c[1])}A${n(r)} ${n(r)} 0 1 1 ${n(c[0] + r)} ${n(c[1])}Z`;

// The turn every element is drawn with, around the middle of its frame.
export function turn(el) {
  if (!el.rotation) return "";
  const c = R.center(el.frame);
  return `rotate(${n(el.rotation * 180 / Math.PI)} ${n(c[0])} ${n(c[1])})`;
}
export function dashes(style) {
  if (style.pattern === "dashed") return `${n(style.width * 5)} ${n(style.width * 3)}`;
  return style.pattern === "dotted" ? `0.01 ${n(style.width * 3)}` : "";
}
function headOps(head, base) {
  const { kind, tip, left, right, polygon, length } = head, stroke = { ...base, dash: "" }, fill = { fill: true, color: base.color, opacity: base.opacity };
  if (kind === "circle" || kind === "circleOutline") return [{ ...(head.hollow ? stroke : fill), d: circle(tip, length / 3) }];
  if (["square", "diamond", "diamondOutline", "triangleOutline"].includes(kind)) return [{ ...(head.hollow ? stroke : fill), d: line(polygon, true) }];
  if (kind === "bar") return [{ ...stroke, d: line([left, right]) }];
  if (kind === "filled") return [{ ...fill, d: line([left, tip, right], true) }];
  return [{ ...stroke, d: line([left, tip, right]) }];
}
// The strokes and fills of one shape, line, arrow, pen or highlighter stroke, in drawing order. Each is
// { d, color, opacity } with `fill: true`, or with width, cap, join and dash for a stroke.
export function drawOps(el) {
  const { tool, style, points } = el, marker = tool === "highlighter";
  const opacity = style.opacity * (marker ? 0.35 : 1);
  const base = { color: style.color, opacity, width: marker ? style.width * 5 : style.width, cap: marker ? "butt" : "round", join: marker ? "bevel" : "round", dash: dashes(style) };
  const ops = [];
  if (Tool.isFreehand(tool) && points.length === 1) {
    const [x, y] = points[0], w = style.width;
    ops.push({ fill: true, color: style.color, opacity, d: marker ? `M${n(x - w * 0.8)} ${n(y - w * 2.5)}h${n(w * 1.6)}v${n(w * 5)}h${n(-w * 1.6)}Z` : circle([x, y], w / 2) });
  } else ops.push({ ...base, d: pathData(pathFor(el)) });
  if (tool === "arrow" && points.length >= 2) {
    for (const start of [true, false]) { const head = arrowHead(el, start); if (head) ops.push(...headOps(head, base)); }
  }
  return ops;
}
// The same, as SVG markup: for render-test.html and for anything that wants a picture without the engine.
export function svgMarkup(el) {
  const body = drawOps(el).map((op) => (op.fill
    ? `<path d="${op.d}" fill="${op.color}" fill-opacity="${op.opacity}"/>`
    : `<path d="${op.d}" fill="none" stroke="${op.color}" stroke-opacity="${op.opacity}" stroke-width="${op.width}" stroke-linecap="${op.cap}" stroke-linejoin="${op.join}"${op.dash ? ` stroke-dasharray="${op.dash}"` : ""}/>`)).join("");
  return `<g${el.rotation ? ` transform="${turn(el)}"` : ""}>${body}</g>`;
}

// LaserTrailRenderer: each of the six layers is one filled outline of circles and the bands between them.
export function laserOps(trail, now) {
  const ops = [];
  for (const shape of trail.shapes(now)) {
    if (!(shape.opacity > 0)) continue;
    for (const pass of LaserTrail.passes) {
      let d = "";
      shape.dots.forEach((dot, i) => {
        const radius = LaserTrail.radius(pass, dot);
        if (radius > 0) d += circle(dot.center, radius);
        const band = i > 0 ? LaserTrail.band(shape.dots[i - 1], dot, pass) : null;
        if (band) d += line(band, true);
      });
      if (d) ops.push({ d, color: pass.white ? "#fff" : shape.color, opacity: pass.alpha * shape.opacity });
    }
  }
  return ops;
}

// DrawingCanvas.swift: the Smart Draw morph. The stroke's 96 points travel to 96 points on the neat outline.
export function resample(points, count) {
  if (points.length < 2 || count < 2) return [];
  const distances = [0];
  for (let i = 1; i < points.length; i++) distances.push(distances[i - 1] + P.dist(points[i - 1], points[i]));
  const total = distances[distances.length - 1];
  if (!(total > 0) || !Number.isFinite(total)) return [];
  const output = [points[0]];
  let index = 1;
  for (let sample = 1; sample < count - 1; sample++) {
    const position = total * sample / (count - 1);
    while (index < points.length - 1 && distances[index] < position) index++;
    const segment = distances[index] - distances[index - 1], share = segment > 0 ? (position - distances[index - 1]) / segment : 0;
    output.push(P.add(points[index - 1], P.mul(P.sub(points[index], points[index - 1]), share)));
  }
  output.push(points[points.length - 1]);
  return output;
}
export function snapOutline(el, count) {
  const box = el.frame, c = R.center(box), maxX = R.maxX(box), maxY = R.maxY(box);
  let points;
  if (el.tool === "rectangle") points = [[box.x, box.y], [maxX, box.y], [maxX, maxY], [box.x, maxY], [box.x, box.y]];
  else if (el.tool === "diamond") points = [[c[0], box.y], [maxX, c[1]], [c[0], maxY], [box.x, c[1]], [c[0], box.y]];
  else if (el.tool === "ellipse") points = Array.from({ length: 65 }, (_, i) => { const a = i / 64 * 2 * Math.PI; return [c[0] + box.width / 2 * Math.cos(a), c[1] + box.height / 2 * Math.sin(a)]; });
  else if (el.tool === "arrow" && el.points.length >= 2) points = el.points;
  else return null;
  if (el.rotation) points = points.map((p) => P.rotated(p, c, el.rotation));
  const result = resample(points, count);
  return result.length === count ? result : null;
}
// A closed outline starts at the point nearest the stroke's first sample and runs the same way round.
export function align(outline, ink, closed) {
  const first = ink[0], last = ink[ink.length - 1];
  if (!first || outline.length < 3) return outline;
  if (!closed) {
    const end = outline[outline.length - 1];
    return P.dist(first, outline[0]) + P.dist(last, end) <= P.dist(first, end) + P.dist(last, outline[0]) ? outline : [...outline].reverse();
  }
  const area = (points) => points.reduce((sum, a, i) => { const b = points[(i + 1) % points.length]; return sum + a[0] * b[1] - b[0] * a[1]; }, 0);
  let ring = outline.slice(0, -1);
  if ((area(ring) < 0) !== (area(ink) < 0)) ring.reverse();
  let start = 0;
  ring.forEach((p, i) => { if (P.dist(p, first) < P.dist(ring[start], first)) start = i; });
  ring = [...ring.slice(start), ...ring.slice(0, start)];
  return [...ring, ring[0]];
}
