// A JavaScript port of the parts of Annotator's core that the web demo uses, kept free of the DOM so Node can test it
// against the app's conformance vectors (test-core.mjs). Each section names the Swift source it follows, under
// apps/macos/Sources. Points are [x, y]; rectangles are { x, y, width, height }; angles are radians.
import { SYSTEM_SHAPE_PATHS } from "./system-shapes.js";

// Geometry.swift ---------------------------------------------------------------------------------
export const P = {
  add: (a, b) => [a[0] + b[0], a[1] + b[1]],
  sub: (a, b) => [a[0] - b[0], a[1] - b[1]],
  mul: (a, k) => [a[0] * k, a[1] * k],
  dot: (a, b) => a[0] * b[0] + a[1] * b[1],
  dist: (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]),
  same: (a, b) => a[0] === b[0] && a[1] === b[1],
  isFinite: (p) => Number.isFinite(p[0]) && Number.isFinite(p[1]) && Math.abs(p[0]) <= 10_000_000 && Math.abs(p[1]) <= 10_000_000,
  rotated(p, center, angle) {
    const x = p[0] - center[0], y = p[1] - center[1], cos = Math.cos(angle), sin = Math.sin(angle);
    return [center[0] + (x * cos - y * sin), center[1] + (x * sin + y * cos)];
  },
};
export const R = {
  zero: () => ({ x: 0, y: 0, width: 0, height: 0 }),
  from: (a, b) => ({ x: Math.min(a[0], b[0]), y: Math.min(a[1], b[1]), width: Math.abs(a[0] - b[0]), height: Math.abs(a[1] - b[1]) }),
  origin: (r) => [r.x, r.y],
  center: (r) => [r.x + r.width / 2, r.y + r.height / 2],
  maxX: (r) => r.x + r.width,
  maxY: (r) => r.y + r.height,
  corners: (r) => [[r.x, r.y], [r.x + r.width, r.y], [r.x + r.width, r.y + r.height], [r.x, r.y + r.height]],
  contains: (r, p, padding = 0) => p[0] >= r.x - padding && p[0] <= r.x + r.width + padding && p[1] >= r.y - padding && p[1] <= r.y + r.height + padding,
  containsRect: (r, other) => R.contains(r, [other.x, other.y]) && R.contains(r, [other.x + other.width, other.y + other.height]),
  translated: (r, offset) => ({ x: r.x + offset[0], y: r.y + offset[1], width: r.width, height: r.height }),
  union(a, b) {
    const x = Math.min(a.x, b.x), y = Math.min(a.y, b.y);
    return { x, y, width: Math.max(a.x + a.width, b.x + b.width) - x, height: Math.max(a.y + a.height, b.y + b.height) - y };
  },
  enclosing(points) {
    if (!points.length) return R.zero();
    return points.slice(1).reduce((box, p) => R.union(box, R.from(p, p)), R.from(points[0], points[0]));
  },
  isValid: (r) => [r.x, r.y, r.width, r.height].every(Number.isFinite) && r.width >= 0 && r.height >= 0
    && Math.abs(r.x) <= 10_000_000 && Math.abs(r.y) <= 10_000_000 && r.width <= 1_000_000 && r.height <= 1_000_000,
};

// Element.swift: tools ----------------------------------------------------------------------------
const SYSTEM_SHAPES = { databaseStack: 0.9, server: 1, messaging: 1.0002036, web: 1, mobile: 0.7, desktop: 1.1111111, laptop: 1.2515959, document: 0.80000003,
  configuration: 0.89726505, loadBalancer: 1, apiGateway: 1, envelope: 1.25, cart: 0.9985843, bell: 0.89998725 };
const SHAPES = new Set(["rectangle", "ellipse", "diamond", "database", "queue", "person", "grid", "axes", "triangle", "hexagon", "star", ...Object.keys(SYSTEM_SHAPES)]);
export const Tool = {
  isPixelEffect: (tool) => tool === "blur" || tool === "pixelate" || tool === "distort",
  isProtection: (tool) => tool === "cover" || Tool.isPixelEffect(tool),
  isFreehand: (tool) => tool === "pen" || tool === "highlighter",
  isConnector: (tool) => tool === "line" || tool === "arrow",
  isShape: (tool) => SHAPES.has(tool),
  polygonKind: (tool) => (tool === "triangle" || tool === "hexagon" || tool === "star" ? tool : null),
  aspectRatio: (tool) => SYSTEM_SHAPES[tool] ?? null,
};
// ToolStyleDefaults.swift and Style's defaults. Colors here are the app's RGBA values as CSS hex.
export const INK = { coral: "#F04538", highlighter: "#EDFF0D", laser: "#FA2933" };
export function defaultStyle(tool) {
  const style = { color: INK.coral, fill: null, width: 3, opacity: 1, pattern: "solid", fillPattern: "solid", roughness: 0, cornerRadius: 0,
    fontSize: 28, fontName: "system", bold: false, alignment: "left", curved: false, startHead: "none", endHead: "open", pressure: false,
    strokeGradient: null, fillGradient: null };
  if (tool === "arrow") style.headScale = 0.75;
  if (tool === "highlighter") { style.color = INK.highlighter; style.width = 4; }
  if (tool === "laser") { style.color = INK.laser; style.width = 6; }
  return style;
}

// ColorGradient.swift ------------------------------------------------------------------------------
// A gradient is { colors: [2 to 8 CSS hex colors], angle in degrees }. Colors here are #RRGGBB, or #RRGGBBAA when
// they are see-through; a paint's opacity is its color's alpha, as in the app.
const hex2 = (value) => Math.round(Math.min(1, Math.max(0, value)) * 255).toString(16).padStart(2, "0").toUpperCase();
const rgb = (red, green, blue) => `#${hex2(red)}${hex2(green)}${hex2(blue)}`;
const preset = (label, ...colors) => ({ id: label.toLowerCase(), label, gradient: { colors: colors.map((c) => rgb(...c)), angle: 25 } });
// GradientPreset: the app's fifteen gradients, in the picker's order.
export const GRADIENT_PRESETS = [
  preset("Sunset", [0.96, 0.12, 0.67], [1, 0.23, 0.39], [1, 0.48, 0.2]),
  preset("Lagoon", [0.13, 0.48, 0.96], [0.05, 0.73, 0.89], [0.05, 0.82, 0.66]),
  preset("Ember", [0.91, 0.19, 0.35], [1, 0.53, 0.16], [1, 0.85, 0.3]),
  preset("Spectrum", [0.93, 0.19, 0.49], [0.63, 0.27, 0.9], [0.16, 0.48, 0.95], [0.03, 0.72, 0.64], [0.98, 0.74, 0.2], [0.97, 0.31, 0.29]),
  preset("Dusk", [0.37, 0.28, 0.89], [0.67, 0.29, 0.8], [0.88, 0.32, 0.66]),
  preset("Citrus", [1, 0.55, 0.12], [0.97, 0.83, 0.2], [0.42, 0.76, 0.3]),
  preset("Ocean", [0.08, 0.79, 0.74], [0.18, 0.51, 0.94], [0.47, 0.3, 0.85]),
  preset("Orchid", [0.98, 0.43, 0.69], [0.7, 0.44, 0.91], [0.38, 0.63, 0.97]),
  preset("Aurora", [0.13, 0.72, 0.47], [0.25, 0.55, 0.95], [0.82, 0.33, 0.88]),
  preset("Cherry", [0.86, 0.1, 0.22], [0.95, 0.27, 0.47], [0.6, 0.17, 0.76]),
  preset("Forest", [0.07, 0.55, 0.33], [0.25, 0.7, 0.3], [0.62, 0.8, 0.18]),
  preset("Copper", [0.72, 0.33, 0.16], [0.88, 0.5, 0.25], [0.86, 0.66, 0.38]),
  preset("Neon", [0.95, 0.2, 0.75], [0.35, 0.45, 1], [0.1, 0.82, 0.78]),
  preset("Tropic", [0.98, 0.62, 0.12], [0.94, 0.3, 0.48], [0.42, 0.3, 0.9]),
  preset("Steel", [0.33, 0.4, 0.5], [0.52, 0.6, 0.7], [0.4, 0.5, 0.65]),
];
export const Paint = {
  // A hex color's parts, each 0 to 1, or null for a color written another way.
  parse(color) {
    const digits = /^#([0-9a-f]{6}|[0-9a-f]{8})$/i.exec(String(color || "").trim())?.[1];
    if (!digits) return null;
    const part = (i) => parseInt(digits.slice(i, i + 2), 16) / 255;
    return { red: part(0), green: part(2), blue: part(4), alpha: digits.length === 8 ? part(6) : 1 };
  },
  format: (c, alpha = c.alpha) => `${rgb(c.red, c.green, c.blue)}${alpha < 1 ? hex2(alpha) : ""}`,
  // PaletteHex.format: always eight digits.
  hex8: (color) => { const c = Paint.parse(color); return c ? `${rgb(c.red, c.green, c.blue).slice(1)}${hex2(c.alpha)}` : ""; },
  alpha: (color) => Paint.parse(color)?.alpha ?? 1,
  solid: (color) => { const c = Paint.parse(color); return c ? rgb(c.red, c.green, c.blue) : color; },
  withAlpha: (color, alpha) => { const c = Paint.parse(color); return c ? Paint.format(c, alpha) : color; },
  // Style.setPaintColor: a solid color keeps the paint's opacity unless it brings its own, and ends a gradient.
  setColor(style, color, useColorAlpha = false) {
    style.color = useColorAlpha ? color : Paint.withAlpha(color, Paint.alpha(style.color));
    style.strokeGradient = null;
  },
  // Style.setGradient: the first color stays behind as the plain color, with the paint's opacity.
  setGradient(style, gradient) {
    style.color = Paint.withAlpha(gradient.colors[0] ?? style.color, Paint.alpha(style.color));
    style.strokeGradient = { colors: [...gradient.colors], angle: gradient.angle };
  },
  setOpacity(style, alpha) { style.color = Paint.withAlpha(style.color, alpha); },
  same: (a, b) => Boolean(a && b) && a.angle === b.angle && a.colors.length === b.colors.length && a.colors.every((c, i) => c.toLowerCase() === b.colors[i].toLowerCase()),
  // GradientPainter: the gradient runs through the middle of the bounds at its angle, corner to corner.
  line(gradient, bounds) {
    const angle = gradient.angle * Math.PI / 180, dx = Math.cos(angle), dy = Math.sin(angle);
    const length = Math.max(1, Math.abs(dx) * bounds.width + Math.abs(dy) * bounds.height) / 2, c = R.center(bounds);
    return { start: [c[0] - dx * length, c[1] - dy * length], end: [c[0] + dx * length, c[1] + dy * length] };
  },
  // The same gradient as a CSS background: CSS measures its angle from "up", clockwise, and sizes the line the same way.
  css: (gradient) => `linear-gradient(${gradient.angle + 90}deg, ${gradient.colors.join(", ")})`,
};
let tokens = 0;
export const freshToken = () => ++tokens;
export function makeElement({ id, tool, frame = R.zero(), points = [], text = "", style = defaultStyle(tool), seed = 1 }) {
  return { id, tool, frame: points.length ? R.enclosing(points) : { ...frame }, points: points.map((p) => [p[0], p[1]]), pressures: [], text, style: { ...style },
    rotation: 0, groupID: null, locked: false, startAnchor: null, endAnchor: null, seed, revision: 0, geometryToken: freshToken() };
}
// SystemShapeCatalog.swift
export function symbolFrame(el) {
  const ratio = Tool.aspectRatio(el.tool), f = el.frame;
  if (ratio === null) return f;
  const width = Math.min(f.width, f.height * ratio), height = Math.min(f.height, f.width / ratio), c = R.center(f);
  return { x: c[0] - width / 2, y: c[1] - height / 2, width, height };
}
export function translate(el, offset) {
  el.frame = R.translated(el.frame, offset);
  el.points = el.points.map((p) => P.add(p, offset));
  el.revision++;
  el.geometryToken = freshToken();
}
export function resize(el, frame) {
  const prior = el.frame;
  el.points = el.points.map((p) => [frame.x + (p[0] - prior.x) * frame.width / Math.max(prior.width, 0.001), frame.y + (p[1] - prior.y) * frame.height / Math.max(prior.height, 0.001)]);
  el.frame = { ...frame };
  el.revision++;
  el.geometryToken = freshToken();
}
export function anchorLocations(el) {
  const kind = Tool.polygonKind(el.tool);
  if (!kind) return [[0.5, 0], [1, 0.5], [0.5, 1], [0, 0.5]];
  const vertices = polygonVertices(kind, { x: 0, y: 0, width: 1, height: 1 });
  return [...vertices, ...vertices.map((v, i) => P.mul(P.add(v, vertices[(i + 1) % vertices.length]), 0.5))];
}
export function anchorPoint(el, relative) {
  const f = symbolFrame(el);
  return P.rotated([f.x + relative[0] * f.width, f.y + relative[1] * f.height], R.center(el.frame), el.rotation);
}
// What a fill's alpha is: an RGBA object in the app's vectors, a CSS color with its own opacity on the web.
const fillAlpha = (style) => (style.fill && typeof style.fill === "object" ? style.fill.alpha : style.fillOpacity ?? 1);

// PolygonShape.swift and RoundedDiamond.swift -----------------------------------------------------
export function polygonVertices(kind, frame) {
  let normalized;
  if (kind === "triangle") normalized = [[0.5, 0], [1, 1], [0, 1]];
  else if (kind === "hexagon") normalized = [[0.25, 0], [0.75, 0], [1, 0.5], [0.75, 1], [0.25, 1], [0, 0.5]];
  else {
    const points = Array.from({ length: 10 }, (_, i) => {
      const angle = -Math.PI / 2 + i * Math.PI / 5, radius = i % 2 === 0 ? 0.5 : 0.22;
      return [0.5 + Math.cos(angle) * radius, 0.5 + Math.sin(angle) * radius];
    });
    const minX = points.reduce((m, p) => Math.min(m, p[0]), 1), maxX = points.reduce((m, p) => Math.max(m, p[0]), 0);
    const minY = points.reduce((m, p) => Math.min(m, p[1]), 1), maxY = points.reduce((m, p) => Math.max(m, p[1]), 0);
    normalized = points.map((p) => [(p[0] - minX) / (maxX - minX), (p[1] - minY) / (maxY - minY)]);
  }
  return normalized.map((p) => [frame.x + p[0] * frame.width, frame.y + p[1] * frame.height]);
}
function polygonContains(point, vertices, tolerance) {
  if (vertices.length < 3) return false;
  let inside = false;
  for (let i = 0; i < vertices.length; i++) {
    const a = vertices[i], b = vertices[(i + 1) % vertices.length];
    if (segmentDistance(point, a, b) <= tolerance) return true;
    if ((a[1] > point[1]) !== (b[1] > point[1]) && point[0] < (b[0] - a[0]) * (point[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
  }
  return inside;
}
export function roundedDiamondCorners(frame, radius) {
  const c = R.center(frame), vertices = [[c[0], frame.y], [R.maxX(frame), c[1]], [c[0], R.maxY(frame)], [frame.x, c[1]]];
  const length = P.dist(vertices[0], vertices[1]);
  if (!(length > 0)) return [];
  const fraction = Math.min(Math.max(0, radius) / length, 0.5);
  return vertices.map((point, i) => ({
    entry: P.add(point, P.mul(P.sub(vertices[(i + 3) % 4], point), fraction)), control: point,
    exit: P.add(point, P.mul(P.sub(vertices[(i + 1) % 4], point), fraction)),
  }));
}
function roundedDiamondContains(point, frame, radius, tolerance) {
  const outline = roundedDiamondCorners(frame, radius).flatMap((corner) => Array.from({ length: 17 }, (_, i) => {
    const t = i / 16, u = 1 - t;
    return P.add(P.add(P.mul(corner.entry, u * u), P.mul(corner.control, 2 * u * t)), P.mul(corner.exit, t * t));
  }));
  if (!outline.length) return false;
  let previous = outline[outline.length - 1], inside = false;
  for (const current of outline) {
    if (segmentDistance(point, previous, current) <= tolerance) return true;
    if ((current[1] > point[1]) !== (previous[1] > point[1])
      && point[0] < (previous[0] - current[0]) * (point[1] - current[1]) / (previous[1] - current[1]) + current[0]) inside = !inside;
    previous = current;
  }
  return inside;
}

// ElementInteraction.swift: connector curves and stacking ------------------------------------------
const curvePoint = (c, t) => {
  const u = 1 - t;
  return P.add(P.add(P.mul(c.start, u * u * u), P.mul(c.firstControl, 3 * u * u * t)), P.add(P.mul(c.secondControl, 3 * u * t * t), P.mul(c.end, t * t * t)));
};
function curveHits(c, point, tolerance, depth) {
  if (!R.contains(R.enclosing([c.start, c.firstControl, c.secondControl, c.end]), point, tolerance)) return false;
  const flatness = Math.max(segmentDistance(c.firstControl, c.start, c.end), segmentDistance(c.secondControl, c.start, c.end));
  if (flatness <= 0.5 || depth >= 10) return segmentDistance(point, c.start, c.end) <= tolerance;
  const half = (a, b) => P.mul(P.add(a, b), 0.5);
  const a = half(c.start, c.firstControl), b = half(c.firstControl, c.secondControl), cc = half(c.secondControl, c.end);
  const d = half(a, b), e = half(b, cc), middle = half(d, e);
  return curveHits({ start: c.start, firstControl: a, secondControl: d, end: middle }, point, tolerance, depth + 1)
    || curveHits({ start: middle, firstControl: e, secondControl: cc, end: c.end }, point, tolerance, depth + 1);
}
function connectorSegment(points, index) {
  const a = points[Math.max(0, index - 1)], b = points[index], c = points[index + 1], d = points[Math.min(points.length - 1, index + 2)];
  return { start: b, firstControl: P.add(b, P.mul(P.sub(c, a), 1 / 6)), secondControl: P.sub(c, P.mul(P.sub(d, b), 1 / 6)), end: c };
}
export const Connector = {
  curves: (points) => (points.length >= 3 ? Array.from({ length: points.length - 1 }, (_, i) => connectorSegment(points, i)) : []),
  endpointCurve: (points, atStart) => (points.length >= 3 ? connectorSegment(points, atStart ? 0 : points.length - 2) : null),
  point: curvePoint,
};
const isSeeThrough = (shape) => {
  if (!shape.style.fill || fillAlpha(shape.style) * shape.style.opacity < 0.5) return true;
  return shape.style.fillPattern !== "solid" && !shape.style.fillGradient;
};
export const Stacking = {
  ordered: (elements) => [...elements.filter((e) => !Tool.isProtection(e.tool)), ...elements.filter((e) => Tool.isPixelEffect(e.tool)), ...elements.filter((e) => e.tool === "cover")],
  frontmost(point, elements) {
    const stack = Stacking.ordered(elements).filter((e) => contains(point, e));
    const top = stack[stack.length - 1];
    if (!top) return null;
    if (!Tool.isShape(top.tool) || !isSeeThrough(top)) return top;
    // Text drawn inside an outline shape stays clickable even when the shape is newer.
    for (let i = stack.length - 1; i >= 0; i--) {
      if (stack[i].tool === "text") return stack[i];
      if (!Tool.isShape(stack[i].tool) || !isSeeThrough(stack[i])) break;
    }
    return top;
  },
};

// ArrowHeadGeometry.swift -------------------------------------------------------------------------
export function arrowHead(el, atStart) {
  const kind = atStart ? el.style.startHead : el.style.endHead, points = el.points;
  if (el.tool !== "arrow" || kind === "none" || !kind || points.length < 2) return null;
  const tip = atStart ? points[0] : points[points.length - 1];
  const others = atStart ? points.slice(1) : points.slice(0, -1).reverse();
  const neighbor = others.find((p) => P.dist(p, tip) > 0.001);
  if (!neighbor) return null;
  const length = Math.min(2 * Math.max(12, el.style.width * 4) * (el.style.headScale ?? 1), P.dist(tip, neighbor) * 0.7);
  let reference = neighbor;
  if (el.style.curved && points.length > 2) {
    const curve = Connector.endpointCurve(points, atStart);
    const reach = Math.min(length, P.dist(tip, neighbor) * 0.4);
    const tangent = atStart ? P.sub(curve.start, curve.firstControl) : P.sub(curve.end, curve.secondControl), tangentLength = Math.hypot(tangent[0], tangent[1]);
    for (let step = 1; step <= 24; step++) {
      const t = step / 48, sample = curvePoint(curve, atStart ? t : 1 - t), distance = P.dist(sample, tip);
      if (distance > 0.001) {
        const delta = P.sub(tip, sample);
        if (tangentLength > 0.001 && P.dot(delta, tangent) / (distance * tangentLength) < 0.866) break;
        reference = sample;
      }
      if (distance >= reach) break;
    }
  }
  const magnitude = P.dist(tip, reference);
  if (!(magnitude > 0.001)) return null;
  const direction = P.mul(P.sub(tip, reference), 1 / magnitude), side = [-direction[1], direction[0]];
  const base = kind === "bar" ? tip : P.sub(tip, P.mul(direction, length));
  const left = P.add(base, P.mul(side, length * 0.6)), right = P.sub(base, P.mul(side, length * 0.6));
  let polygon = [left, tip, right];
  if (kind === "square") {
    const along = P.mul(direction, length / 3), across = P.mul(side, length / 3);
    polygon = [P.add(P.add(tip, along), across), P.sub(P.add(tip, along), across), P.sub(P.sub(tip, along), across), P.add(P.sub(tip, along), across)];
  } else if (kind === "diamond" || kind === "diamondOutline") {
    const center = P.sub(tip, P.mul(direction, length * 0.5));
    polygon = [tip, P.add(center, P.mul(side, length * 0.36)), P.sub(tip, P.mul(direction, length)), P.sub(center, P.mul(side, length * 0.36))];
  }
  return { kind, tip, direction, length, side, base, left, right, polygon, hollow: ["triangleOutline", "circleOutline", "diamondOutline"].includes(kind) };
}
function headContains(head, point, tolerance) {
  const { kind, tip, direction, side, length, left, right, polygon } = head;
  if (kind === "circle") return P.dist(point, tip) <= length / 3 + tolerance;
  if (kind === "square") {
    const delta = P.sub(point, tip);
    return Math.abs(P.dot(delta, direction)) <= length / 3 + tolerance && Math.abs(P.dot(delta, side)) <= length / 3 + tolerance;
  }
  if (kind === "open") return segmentDistance(point, left, tip) <= tolerance || segmentDistance(point, tip, right) <= tolerance;
  if (kind === "bar") return segmentDistance(point, left, right) <= tolerance;
  if (kind === "filled") {
    const delta = P.sub(tip, point), along = P.dot(delta, direction), across = Math.abs(P.dot(delta, side));
    return along >= -tolerance && along <= length + tolerance && across <= Math.max(0, along) * 0.6 + tolerance;
  }
  if (kind === "circleOutline") return Math.abs(P.dist(point, tip) - length / 3) <= tolerance;
  if (kind === "triangleOutline" || kind === "diamondOutline" || kind === "diamond") {
    if (polygon.some((a, i) => segmentDistance(point, a, polygon[(i + 1) % polygon.length]) <= tolerance)) return true;
    if (kind !== "diamond") return false;
    const delta = P.sub(point, P.sub(tip, P.mul(direction, length * 0.5)));
    return Math.abs(P.dot(delta, direction)) / (length * 0.5) + Math.abs(P.dot(delta, side)) / (length * 0.36) <= 1;
  }
  return relationshipContains(head, point, tolerance);
}
// The relationship ends (crow's foot). The web demo never draws them; they are here so every hit-test vector runs.
function relationshipContains({ kind, tip, direction, side, length, base }, point, tolerance) {
  const bar = (offset) => { const center = P.sub(tip, P.mul(direction, length * offset)); return [P.add(center, P.mul(side, length * 0.4)), P.sub(center, P.mul(side, length * 0.4))]; };
  const fork = [[base, P.add(tip, P.mul(side, length * 0.5))], [base, tip], [base, P.sub(tip, P.mul(side, length * 0.5))]];
  const segments = { cardinalityOne: [bar(0.25)], cardinalityMany: fork, cardinalityOneOrMany: [...fork, bar(0.85)], cardinalityExactlyOne: [bar(0.2), bar(0.7)],
    cardinalityZeroOrOne: [bar(0.2)], cardinalityZeroOrMany: fork }[kind] || [];
  if (segments.some(([a, b]) => segmentDistance(point, a, b) <= tolerance)) return true;
  if (kind !== "cardinalityZeroOrOne" && kind !== "cardinalityZeroOrMany") return false;
  return Math.abs(P.dist(point, P.sub(tip, P.mul(direction, length * 1.25))) - length * 0.23) <= tolerance;
}

// Element.swift: HitTesting ------------------------------------------------------------------------
export function segmentDistance(p, a, b) {
  const d = P.sub(b, a), length = d[0] * d[0] + d[1] * d[1];
  if (!(length > 0)) return P.dist(p, a);
  const t = Math.min(1, Math.max(0, ((p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1]) / length));
  return P.dist(p, P.add(a, P.mul(d, t)));
}
export function contains(point, el, tolerance = 6) {
  const p = P.rotated(point, R.center(el.frame), -el.rotation), { tool, style, points, frame } = el;
  const strokeWidth = tool === "highlighter" ? style.width * 5 : style.width, hitRadius = tolerance + strokeWidth / 2;
  if (tool === "arrow") {
    for (const start of [true, false]) {
      const head = arrowHead(el, start);
      if (head && headContains(head, p, hitRadius)) return true;
    }
  }
  if (Tool.isConnector(tool) && style.curved && points.length >= 3) return Connector.curves(points).some((c) => curveHits(c, p, hitRadius, 0));
  if (tool === "ruler" && points.length) {
    const first = points[0], last = points[points.length - 1];
    const label = { x: (first[0] + last[0]) / 2 - 60, y: (first[1] + last[1]) / 2 - 25, width: 120, height: 22 };
    return R.contains(label, p, tolerance) || segmentDistance(p, first, last) <= hitRadius;
  }
  const hitFrame = symbolFrame(el);
  if (!R.contains(hitFrame, p, hitRadius)) return false;
  const kind = Tool.polygonKind(tool);
  if (kind) return polygonContains(p, polygonVertices(kind, frame), hitRadius);
  if (Tool.isFreehand(tool) || Tool.isConnector(tool)) {
    if (points.length === 1) return P.dist(p, points[0]) <= hitRadius;
    return points.some((a, i) => i < points.length - 1 && segmentDistance(p, a, points[i + 1]) <= hitRadius);
  }
  if ((tool === "ellipse" || tool === "counter" || tool === "web") && frame.width > 0 && frame.height > 0) {
    const c = R.center(hitFrame), dx = (p[0] - c[0]) / (hitFrame.width / 2 + tolerance), dy = (p[1] - c[1]) / (hitFrame.height / 2 + tolerance);
    return dx * dx + dy * dy <= 1;
  }
  if (tool === "diamond" && frame.width > 0 && frame.height > 0) {
    if (style.cornerRadius > 0) return roundedDiamondContains(p, frame, style.cornerRadius, hitRadius);
    const c = R.center(frame);
    return Math.abs(p[0] - c[0]) / (frame.width / 2 + tolerance) + Math.abs(p[1] - c[1]) / (frame.height / 2 + tolerance) <= 1;
  }
  return true;
}

// DragConstraint.swift ----------------------------------------------------------------------------
const roundAway = (n) => Math.sign(n) * Math.round(Math.abs(n));
export const DragConstraint = {
  snappedLineEnd(start, point) {
    const angle = roundAway(Math.atan2(point[1] - start[1], point[0] - start[0]) / (Math.PI / 4)) * (Math.PI / 4);
    return P.add(start, P.mul([Math.cos(angle), Math.sin(angle)], P.dist(start, point)));
  },
  squareEnd(start, point) {
    const length = Math.max(Math.abs(point[0] - start[0]), Math.abs(point[1] - start[1]));
    return P.add(start, [point[0] < start[0] ? -length : length, point[1] < start[1] ? -length : length]);
  },
  aspectEnd(start, point, ratio) {
    const height = Math.min(Math.abs(point[1] - start[1]), Math.abs(point[0] - start[0]) / ratio);
    return P.add(start, [point[0] < start[0] ? -height * ratio : height * ratio, point[1] < start[1] ? -height : height]);
  },
};

// Geometry.swift: SelectionGeometry ----------------------------------------------------------------
export function selectionGeometry(elements, selection) {
  const selected = elements.filter((e) => selection.has(e.id));
  if (selected.length <= 1) return null;
  const editable = selected.filter((e) => !e.locked);
  if (!editable.length) return null;
  const bounds = R.enclosing(editable.flatMap((e) => R.corners(e.frame).map((p) => P.rotated(p, R.center(e.frame), e.rotation))));
  return { bounds, canRotate: !editable.some((e) => Tool.isProtection(e.tool)), corners: R.corners(bounds) };
}

// SceneRenderer.swift, DiamondGeometry.swift and RoughGeometry.swift: the path of each shape and stroke --------
// A path is a list of ["move", [p]], ["line", [p]], ["quad", [c, p]], ["cubic", [c1, c2, p]] and ["close"], as in the vectors.
const KAPPA = 0.5522847498;
function ellipsePath(f) {
  const cx = f.x + f.width / 2, cy = f.y + f.height / 2, rx = f.width / 2, ry = f.height / 2, maxX = f.x + f.width, maxY = f.y + f.height;
  return [["move", [[maxX, cy]]],
    ["cubic", [[maxX, cy + ry * KAPPA], [cx + rx * KAPPA, maxY], [cx, maxY]]], ["cubic", [[cx - rx * KAPPA, maxY], [f.x, cy + ry * KAPPA], [f.x, cy]]],
    ["cubic", [[f.x, cy - ry * KAPPA], [cx - rx * KAPPA, f.y], [cx, f.y]]], ["cubic", [[cx + rx * KAPPA, f.y], [maxX, cy - ry * KAPPA], [maxX, cy]]], ["close"]];
}
function rectanglePath(f, cornerRadius) {
  const r = Math.min(cornerRadius, f.width / 2, f.height / 2), maxX = f.x + f.width, maxY = f.y + f.height;
  if (!(r > 0)) return [["move", [[f.x, f.y]]], ["line", [[maxX, f.y]]], ["line", [[maxX, maxY]]], ["line", [[f.x, maxY]]], ["close"]];
  const k = r * KAPPA;
  return [["move", [[maxX, f.y + f.height / 2]]],
    ["line", [[maxX, maxY - r]]], ["cubic", [[maxX, maxY - r + k], [maxX - r + k, maxY], [maxX - r, maxY]]],
    ["line", [[f.x + r, maxY]]], ["cubic", [[f.x + r - k, maxY], [f.x, maxY - r + k], [f.x, maxY - r]]],
    ["line", [[f.x, f.y + r]]], ["cubic", [[f.x, f.y + r - k], [f.x + r - k, f.y], [f.x + r, f.y]]],
    ["line", [[maxX - r, f.y]]], ["cubic", [[maxX - r + k, f.y], [maxX, f.y + r - k], [maxX, f.y + r]]], ["close"]];
}
function diamondPath(f, cornerRadius) {
  const c = R.center(f), vertices = [[c[0], f.y], [R.maxX(f), c[1]], [c[0], R.maxY(f)], [f.x, c[1]]];
  if (!(cornerRadius > 0 && f.width > 0 && f.height > 0)) return [["move", [vertices[0]]], ...vertices.slice(1).map((v) => ["line", [v]]), ["close"]];
  const path = [];
  roundedDiamondCorners(f, cornerRadius).forEach((corner, i) => { path.push([i ? "line" : "move", [corner.entry]], ["quad", [corner.control, corner.exit]]); });
  return [...path, ["close"]];
}
// SystemDiagramGeometry.path: a system-design shape's outline, fitted to its symbol frame.
function systemShapePath(el) {
  const { bounds: [x, y, width, height], path } = SYSTEM_SHAPE_PATHS[el.tool], b = symbolFrame(el), sx = b.width / width, sy = b.height / height;
  return path.map(([kind, points]) => (points ? [kind, points.map((p) => [b.x + (p[0] - x) * sx, b.y + (p[1] - y) * sy])] : [kind]));
}
export function canonicalShape(el) {
  if (SYSTEM_SHAPE_PATHS[el.tool]) return systemShapePath(el);
  if (el.tool === "ellipse") return ellipsePath(el.frame);
  if (el.tool === "diamond") return diamondPath(el.frame, el.style.cornerRadius);
  const kind = Tool.polygonKind(el.tool);
  if (kind) { const v = polygonVertices(kind, el.frame); return [["move", [v[0]]], ...v.slice(1).map((p) => ["line", [p]]), ["close"]]; }
  return rectanglePath(el.frame, el.style.cornerRadius);
}
// A pen or highlighter point moved by its stroke's seed (SceneRenderer.jitter). The seed is a 64-bit number.
const MASK = (1n << 64n) - 1n;
function jitter(point, index, el) {
  if (!(el.style.roughness > 0)) return point;
  let value = (BigInt(el.seed) + BigInt(index) * 0x9e3779b97f4a7c15n) & MASK;
  value = ((value ^ (value >> 30n)) * 0xbf58476d1ce4e5b9n) & MASK;
  value = ((value ^ (value >> 27n)) * 0x94d049bb133111ebn) & MASK;
  value ^= value >> 31n;
  const x = Number(value & 0xffffn) / 65535 - 0.5, y = Number((value >> 16n) & 0xffffn) / 65535 - 0.5;
  return [point[0] + x * el.style.roughness, point[1] + y * el.style.roughness];
}
// Rough.js's hand-drawn outline, as the app ported it: each line and curve drawn twice with seeded offsets.
function roughOutline(el, canonical) {
  let state = Number(BigInt(el.seed) & 0xffffffffn) || 1;
  const next = () => { state = Math.imul(state, 48271) >>> 0; return (state & 0x7fffffff) / 2147483648; };
  const roughness = el.style.roughness, out = [];
  const range = (low, high, gain = 1) => roughness * gain * (next() * (high - low) + low);
  const offset = (amount, gain = 1) => range(-amount, amount, gain);
  const line = (a, b, preserve) => {
    for (const overlay of [false, true]) {
      const length = P.dist(a, b), gain = length < 200 ? 1 : length > 500 ? 0.4 : -0.0016668 * length + 1.233334;
      const amount = Math.min(2, length / 10) * (overlay ? 0.5 : 1), divergence = 0.2 + next() * 0.2;
      const dx = offset(2 * (b[1] - a[1]) / 200, gain), dy = offset(2 * (a[0] - b[0]) / 200, gain);
      const sx = a[0] + (preserve ? 0 : offset(amount, gain)), sy = a[1] + (preserve ? 0 : offset(amount, gain));
      const fx = dx + a[0] + (b[0] - a[0]) * divergence + offset(amount, gain), fy = dy + a[1] + (b[1] - a[1]) * divergence + offset(amount, gain);
      const tx = dx + a[0] + 2 * (b[0] - a[0]) * divergence + offset(amount, gain), ty = dy + a[1] + 2 * (b[1] - a[1]) * divergence + offset(amount, gain);
      const ex = b[0] + (preserve ? 0 : offset(amount, gain)), ey = b[1] + (preserve ? 0 : offset(amount, gain));
      out.push(["move", [[sx, sy]]], ["cubic", [[fx, fy], [tx, ty], [ex, ey]]]);
    }
  };
  const bezier = (start, first, second, end, preserve) => {
    for (let pass = 0; pass < 2; pass++) {
      const amount = pass === 0 ? 2 : 2.3;
      let beginning = start;
      if (!(pass === 0 || preserve)) { const x = start[0] + offset(2); beginning = [x, start[1] + offset(2)]; }
      let finish = end;
      if (!preserve) { const x = end[0] + offset(amount); finish = [x, end[1] + offset(amount)]; }
      const ox = first[0] + offset(amount), oy = first[1] + offset(amount), tx = second[0] + offset(amount), ty = second[1] + offset(amount);
      out.push(["move", [beginning]], ["cubic", [[ox, oy], [tx, ty], finish]]);
    }
  };
  if (el.tool === "ellipse") {
    const f = el.frame, rx = f.width / 2, ry = f.height / 2, center = R.center(f);
    const estimate = Math.sqrt(Math.PI * 2 * Math.sqrt((rx * rx + ry * ry) / 2));
    const steps = Math.ceil(Math.max(9, 9 / Math.sqrt(200) * estimate)), increment = Math.PI * 2 / Math.max(steps, 4);
    offset(0); offset(0);
    const upper = range(0.4, 1), overlap = increment * range(0.1, upper);
    const position = (theta, factor = 1) => [center[0] + factor * rx * Math.cos(theta), center[1] + factor * ry * Math.sin(theta)];
    for (let pass = 0; pass < 2; pass++) {
      const amount = pass === 0 ? 1 : 1.5, extra = pass === 0 ? overlap : 0, angle = offset(0.5) - Math.PI / 2;
      const jittered = (point) => { const dx = offset(amount); return [point[0] + dx, point[1] + offset(amount)]; };
      const points = [jittered(position(angle - increment, 0.9))];
      let theta = angle;
      while (theta < Math.PI * 2 + angle - 0.01) { points.push(jittered(position(theta))); theta += increment; }
      const overlapStartAngle = angle + Math.PI * 2 + extra * 0.5, overlapEndAngle = angle + Math.PI * 2 + extra;
      const overlapStart = jittered(position(overlapStartAngle)), overlapEnd = jittered(position(angle + extra, 0.98));
      const gx = offset(amount), guide = [gx, offset(amount)];
      if (extra > increment) {
        const appendArc = (start, startAngle, startFactor, end, endAngle, endFactor) => {
          const segments = Math.max(1, Math.ceil((endAngle - startAngle) / increment));
          const startOffset = P.sub(start, position(startAngle, startFactor)), endOffset = P.sub(end, position(endAngle, endFactor));
          for (let i = 1; i < segments; i++) {
            const t = i / segments;
            points.push(P.add(P.add(position(startAngle + (endAngle - startAngle) * t, startFactor + (endFactor - startFactor) * t), P.mul(startOffset, 1 - t)), P.mul(endOffset, t)));
          }
          points.push(end);
        };
        appendArc(points[points.length - 1], theta - increment, 1, overlapStart, overlapStartAngle, 1);
        appendArc(overlapStart, overlapStartAngle, 1, overlapEnd, overlapEndAngle, 0.98);
        points.push(P.add(position(overlapEndAngle - Math.min(extra * 0.5, increment), 0.9), guide));
      } else points.push(overlapStart, overlapEnd, P.add(position(angle + extra * 0.5, 0.9), guide));
      if (points.length <= 3) continue;
      out.push(["move", [points[1]]]);
      for (let i = 1; i < points.length - 2; i++) {
        const a = points[i], b = points[i + 1];
        out.push(["cubic", [P.add(a, P.mul(P.sub(b, points[i - 1]), 1 / 6)), P.add(b, P.mul(P.sub(a, points[i + 2]), 1 / 6)), b]]);
      }
    }
    return out;
  }
  const preserve = el.style.cornerRadius > 0 || Tool.isConnector(el.tool);
  let current = [0, 0], first = [0, 0];
  for (const [kind, points] of canonical) {
    if (kind === "move") { current = points[0]; first = current; }
    else if (kind === "line") { line(current, points[0], preserve); current = points[0]; }
    else if (kind === "quad") {
      const [control, end] = points;
      bezier(current, P.add(current, P.mul(P.sub(control, current), 2 / 3)), P.add(end, P.mul(P.sub(control, end), 2 / 3)), end, true);
      current = end;
    } else if (kind === "cubic") { bezier(current, points[0], points[1], points[2], preserve); current = points[2]; }
    else if (kind === "close") { if (!P.same(current, first)) line(current, first, preserve); current = first; }
  }
  return out;
}
// SceneRenderer.path(for:)
export function pathFor(el) {
  if (Tool.isShape(el.tool)) {
    const canonical = canonicalShape(el);
    return el.style.roughness > 0 ? roughOutline(el, canonical) : canonical;
  }
  if (!el.points.length) return [];
  const connector = Tool.isConnector(el.tool);
  let path = [["move", [el.points[0]]]];
  if (el.style.curved && connector && el.points.length > 2) {
    for (const c of Connector.curves(el.points)) path.push(["cubic", [c.firstControl, c.secondControl, c.end]]);
  } else el.points.forEach((point, i) => { if (i) path.push(["line", [connector ? point : jitter(point, i, el)]]); });
  if (connector && el.style.roughness > 0) path = roughOutline(el, path);
  return path;
}
const n = (v) => Math.round(v * 1000) / 1000;
export const pathData = (path) => path.map(([kind, points]) => (kind === "close" ? "Z" : `${{ move: "M", line: "L", quad: "Q", cubic: "C" }[kind]}${points.map((p) => `${n(p[0])} ${n(p[1])}`).join(" ")}`)).join("");

// Geometry.swift: Viewport, and ZoomFollow.swift: screen zoom and its map ---------------------------
// A viewport is { output, source }: the part `source` of the display is shown over the whole of `output`.
const invalidGeometry = () => new Error("The drawing contains invalid coordinates or dimensions.");
const within = (value, low, high) => Math.min(high, Math.max(low, value));
const usable = (display) => R.isValid(display) && display.width > 0 && display.height > 0;
export const Viewport = {
  make(output, source) {
    if (!(R.isValid(output) && R.isValid(source) && output.width > 0 && output.height > 0 && source.width > 0 && source.height > 0)) throw invalidGeometry();
    return { output: { ...output }, source: { ...source } };
  },
  // The part around the pointer that keeps what is under the pointer under it.
  cursorAnchored(display, factor, cursor) {
    if (!(Number.isFinite(factor) && factor >= 1 && factor <= 16 && P.isFinite(cursor) && usable(display))) throw invalidGeometry();
    const x = within(cursor[0], display.x, R.maxX(display)), y = within(cursor[1], display.y, R.maxY(display));
    return Viewport.make(display, { x: display.x + (x - display.x) * (1 - 1 / factor), y: display.y + (y - display.y) * (1 - 1 / factor), width: display.width / factor, height: display.height / factor });
  },
  sourcePoint: ({ output, source }, p) => [source.x + (p[0] - output.x) * source.width / output.width, source.y + (p[1] - output.y) * source.height / output.height],
  outputPoint: ({ output, source }, p) => [output.x + (p[0] - source.x) * output.width / source.width, output.y + (p[1] - source.y) * output.height / source.height],
};
// Screen zoom holds still while the pointer moves inside it, and slides when the pointer comes near an edge.
export const ZoomFollow = {
  minimumSlidingFactor: 1.001, maximumStep: 0.1,
  edgeZone: (length) => Math.min(96, Math.max(24, length * 0.08)),
  // The zoom of `factor` from the part shown before: the point under the pointer stays under it.
  viewport(previous, display, factor, pointer) {
    if (!(previous && R.isValid(previous) && previous.width > 0 && previous.height > 0)) return Viewport.cursorAnchored(display, factor, pointer);
    if (!(Number.isFinite(factor) && factor >= 1 && factor <= 16 && P.isFinite(pointer) && usable(display))) throw invalidGeometry();
    const width = display.width / factor, height = display.height / factor;
    const px = within(pointer[0], display.x, R.maxX(display)), py = within(pointer[1], display.y, R.maxY(display));
    const x = previous.x + (px - display.x) * (previous.width - width) / display.width, y = previous.y + (py - display.y) * (previous.height - height) / display.height;
    return Viewport.make(display, { x: Math.min(Math.max(x, display.x), R.maxX(display) - width), y: Math.min(Math.max(y, display.y), R.maxY(display) - height), width, height });
  },
  // How fast the zoomed part slides, in display points per second on each axis.
  velocity(display, factor, pointer) {
    if (!(Number.isFinite(factor) && factor > ZoomFollow.minimumSlidingFactor && factor <= 16 && P.isFinite(pointer) && usable(display))) return [0, 0];
    const axis = (start, length, position) => {
      const zone = ZoomFollow.edgeZone(length), value = within(position, start, start + length);
      const low = within((start + zone - value) / zone, 0, 1), high = within((value - (start + length - zone)) / zone, 0, 1);
      return (high * high - low * low) * length * 0.75 / Math.sqrt(factor);
    };
    return [axis(display.x, display.width, pointer[0]), axis(display.y, display.height, pointer[1])];
  },
  // The zoomed part after sliding for `elapsed` seconds (at most maximumStep), kept inside the display.
  slide(viewport, pointer, elapsed) {
    const display = viewport.output, source = viewport.source, speed = ZoomFollow.velocity(display, display.width / source.width, pointer);
    const step = Number.isFinite(elapsed) ? within(elapsed, 0, ZoomFollow.maximumStep) : 0;
    if (!((speed[0] !== 0 || speed[1] !== 0) && step > 0)) return viewport;
    return Viewport.make(display, { x: Math.min(Math.max(source.x + speed[0] * step, display.x), R.maxX(display) - source.width),
      y: Math.min(Math.max(source.y + speed[1] * step, display.y), R.maxY(display) - source.height), width: source.width, height: source.height });
  },
  // The zoomed part after the pointer moved by `delta` to `pointer`: a move toward an edge inside that edge's zone
  // pushes it along as fast as the pointer moves on screen.
  push(viewport, pointer, delta) {
    const display = viewport.output, source = viewport.source, factor = display.width / source.width;
    if (!(Number.isFinite(factor) && factor > ZoomFollow.minimumSlidingFactor && P.isFinite(pointer) && P.isFinite(delta))) return viewport;
    const axis = (start, length, position, move) => {
      const zone = ZoomFollow.edgeZone(length), value = within(position, start, start + length);
      if (move > 0 && value > start + length - zone) return Math.min(move, value - (start + length - zone)) / factor;
      if (move < 0 && value < start + zone) return -Math.min(-move, start + zone - value) / factor;
      return 0;
    };
    const x = axis(display.x, display.width, pointer[0], delta[0]), y = axis(display.y, display.height, pointer[1], delta[1]);
    if (x === 0 && y === 0) return viewport;
    return Viewport.make(display, { x: Math.min(Math.max(source.x + x, display.x), R.maxX(display) - source.width),
      y: Math.min(Math.max(source.y + y, display.y), R.maxY(display) - source.height), width: source.width, height: source.height });
  },
  slides(viewport, pointer) {
    const next = ZoomFollow.slide(viewport, pointer, ZoomFollow.maximumStep);
    return next.source.x !== viewport.source.x || next.source.y !== viewport.source.y;
  },
};
// Screen zoom's map: a small picture of the whole display in a corner, with the zoomed part outlined.
export const ZoomMap = {
  inset: 16, cornerRadius: 10, borderWidth: 1, borderOpacity: 0.6, outlineWidth: 2, dimOpacity: 0.4, fadeDuration: 0.15, hoverOpacity: 0.2,
  positions: { bottomRight: "Bottom right", bottomLeft: "Bottom left", topRight: "Top right", topLeft: "Top left", off: "Off" },
  // The map's frame on a display, or null when the map is off or the display is too small for it.
  frame(display, position = "bottomRight") {
    if (position === "off" || !usable(display)) return null;
    let width = Math.min(260, Math.max(140, display.width * 0.16)), height = width * display.height / display.width;
    if (height > display.height * 0.3) { height = display.height * 0.3; width = height * display.width / display.height; }
    if (!(width >= 80 && width + ZoomMap.inset * 2 <= display.width && height + ZoomMap.inset * 2 <= display.height)) return null;
    const left = position === "bottomLeft" || position === "topLeft", top = position === "topLeft" || position === "topRight";
    return { x: left ? display.x + ZoomMap.inset : R.maxX(display) - ZoomMap.inset - width, y: top ? display.y + ZoomMap.inset : R.maxY(display) - ZoomMap.inset - height, width, height };
  },
  // The zoomed part of `viewport`, drawn in a map of `map`.
  zoomedArea({ output: display, source }, map) {
    const scaleX = map.width / display.width, scaleY = map.height / display.height;
    return { x: map.x + (source.x - display.x) * scaleX, y: map.y + (source.y - display.y) * scaleY, width: source.width * scaleX, height: source.height * scaleY };
  },
};

// MotionCore.swift --------------------------------------------------------------------------------
export const Motion = {
  enter: [0.2, 0, 0, 1], exit: [0.45, 0, 0.55, 0.4], standard: [0.3, 0, 0.2, 1], fade: [0.2, 0, 0.4, 1],
  snapMorphDuration: 0.22, eraseFadeDuration: 0.12,
  progress([x1, y1, x2, y2], x) {
    if (!(x > 0)) return 0;
    if (!(x < 1)) return 1;
    const sample = (t, a, b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
    let low = 0, high = 1;
    for (let i = 0; i < 40; i++) { const middle = (low + high) / 2; if (sample(middle, x1, x2) < x) low = middle; else high = middle; }
    return sample((low + high) / 2, y1, y2);
  },
  smooth(value) { const t = Math.min(1, Math.max(0, value)); return t * t * (3 - 2 * t); },
};

// LaserTrail.swift --------------------------------------------------------------------------------
const fraction = (remaining, lifetime) => (lifetime > 0 ? Math.min(1, Math.max(0, remaining / lifetime)) : 0);
export class LaserTrail {
  static defaultLifetime = 0.8;
  static minimumStep = 1;
  static tailTaper = 4;
  static restInterval = 0.05;
  static maximumSamples = 2048;
  // Four glows that get stronger towards the line, the line itself, then a thin white core.
  static passes = [{ scale: 2.8, extra: 3, alpha: 0.05 }, { scale: 2.2, extra: 2, alpha: 0.08 }, { scale: 1.7, extra: 1.5, alpha: 0.12 }, { scale: 1.35, extra: 1, alpha: 0.18 },
    { scale: 1, extra: 0, alpha: 1 }, { scale: 0.4, extra: 0, alpha: 0.85, white: true }];
  static radius = (pass, dot) => (dot.radius > 0 ? dot.radius * pass.scale + pass.extra : 0);

  constructor(lifetime = LaserTrail.defaultLifetime) { this.strokes = []; this.lifetime = lifetime; }
  get isEmpty() { return !this.strokes.length; }
  get isDrawing() { return this.strokes[this.strokes.length - 1]?.live === true; }
  get isResting() { return this.strokes.length === 1 && this.strokes[0].live && this.strokes[0].samples.length === 1; }
  press(point, time, color, width) {
    this.release(null, time);
    this.strokes.push({ samples: [{ point, time }], color, width, live: true });
  }
  move(point, time) {
    const stroke = this.strokes[this.strokes.length - 1];
    if (!stroke?.live) return;
    const samples = stroke.samples;
    if (samples.length >= 2 && P.dist(samples[samples.length - 2].point, point) < LaserTrail.minimumStep) { samples[samples.length - 1] = { point, time }; return; }
    this.noteRest(stroke, time);
    samples.push({ point, time });
    if (samples.length > LaserTrail.maximumSamples) samples.splice(0, samples.length - LaserTrail.maximumSamples);
  }
  release(point, time) {
    const stroke = this.strokes[this.strokes.length - 1];
    if (!stroke?.live) return;
    if (point) this.move(point, time);
    this.noteRest(stroke, time);
    stroke.live = false;
  }
  noteRest(stroke, time) {
    const newest = stroke.samples[stroke.samples.length - 1];
    if (newest && time - newest.time > LaserTrail.restInterval) stroke.samples.push({ point: newest.point, time });
  }
  prune(now) {
    const horizon = now - this.lifetime;
    for (const stroke of this.strokes) {
      let faded = 0;
      while (faded + 1 < stroke.samples.length && stroke.samples[faded + 1].time <= horizon) faded++;
      if (faded > 0) stroke.samples.splice(0, faded);
    }
    this.strokes = this.strokes.filter((s) => s.live || (s.samples[s.samples.length - 1]?.time ?? -Infinity) > horizon);
  }
  shapes(now) { return this.strokes.map((s) => LaserTrail.shape(s, now, this.lifetime)).filter(Boolean); }
  bounds(now) {
    let area = null;
    for (const shape of this.shapes(now)) {
      for (const dot of shape.dots) {
        const reach = Math.max(...LaserTrail.passes.map((pass) => LaserTrail.radius(pass, dot)));
        const rect = { x: dot.center[0] - reach, y: dot.center[1] - reach, width: reach * 2, height: reach * 2 };
        area = area ? R.union(area, rect) : rect;
      }
    }
    return area;
  }
  static shape(stroke, now, lifetime) {
    const horizon = now - lifetime, samples = stroke.samples, head = samples[samples.length - 1];
    if (!head || !(stroke.live || head.time > horizon)) return null;
    let visible = [];
    samples.forEach((sample, i) => {
      if (!(sample.time > horizon)) return;
      if (!visible.length && i > 0) {
        const previous = samples[i - 1], span = sample.time - previous.time;
        const share = span > 0 ? Math.min(1, Math.max(0, (horizon - previous.time) / span)) : 1;
        visible.push({ point: P.add(previous.point, P.mul(P.sub(sample.point, previous.point), share)), time: horizon });
      }
      visible.push(sample);
    });
    if (!visible.length) visible = [head];
    const radius = stroke.width / 2, taper = Math.max(stroke.width * LaserTrail.tailTaper, 1);
    let travelled = 0;
    const dots = visible.map((sample, i) => {
      if (i > 0) travelled += P.dist(visible[i - 1].point, sample.point);
      const isHead = i === visible.length - 1, life = isHead && stroke.live ? 1 : fraction(sample.time - horizon, lifetime);
      return { center: sample.point, radius: radius * (life * (2 - life)) * (isHead ? 1 : Math.min(1, travelled / taper)) };
    });
    return { dots, color: stroke.color, opacity: stroke.live ? 1 : fraction(head.time - horizon, lifetime) };
  }
  // The band that joins two neighboring dots in one layer, wound like the circles, or null when there is none.
  static band(start, end, pass) {
    const length = P.dist(start.center, end.center), first = LaserTrail.radius(pass, start), last = LaserTrail.radius(pass, end);
    if (!(length > 0) || !(first > 0 || last > 0)) return null;
    const normal = [-(end.center[1] - start.center[1]) / length, (end.center[0] - start.center[0]) / length];
    const corners = [P.sub(start.center, P.mul(normal, first)), P.sub(end.center, P.mul(normal, last)), P.add(end.center, P.mul(normal, last)), P.add(start.center, P.mul(normal, first))];
    const area = corners.reduce((sum, a, i) => { const b = corners[(i + 1) % 4]; return sum + a[0] * b[1] - b[0] * a[1]; }, 0) / 2;
    return area < 0 ? corners.reverse() : corners;
  }
}

// Document.swift: CanvasDocument ---------------------------------------------------------------------
const utf8Length = (text) => new TextEncoder().encode(text).length;
// Element.estimatedBytes: the app's history budget counts 528 bytes an element plus its points, pressures and text.
export const estimatedBytes = (el) => 528 + el.points.length * 16 + el.pressures.length * 8 + utf8Length(el.text || "")
  + ((el.style.strokeGradient?.colors.length ?? 0) + (el.style.fillGradient?.colors.length ?? 0)) * 32;
const plain = (value) => JSON.stringify(value, (key, v) => (typeof v === "bigint" ? `${v}n` : v));
export function validate(el) {
  const s = el.style, bad = (message) => { throw new Error(message); };
  if (Tool.isProtection(el.tool) && el.rotation !== 0) bad("The drawing could not be opened: Censor and pixel-effect regions must remain axis-aligned.");
  if (!R.isValid(el.frame) || !el.points.every(P.isFinite) || !Number.isFinite(el.rotation) || ["select", "eraser", "laser"].includes(el.tool)
    || !el.pressures.every((v) => Number.isFinite(v) && v >= 0 && v <= 1) || (el.pressures.length && el.pressures.length !== el.points.length)) {
    bad("The drawing contains invalid coordinates or dimensions.");
  }
  if (!(Number.isFinite(s.width) && s.width >= 0.25 && s.width <= 256 && s.opacity >= 0 && s.opacity <= 1 && s.roughness >= 0 && s.roughness <= 12
    && s.cornerRadius >= 0 && s.cornerRadius <= 1000 && s.fontSize >= 4 && s.fontSize <= 1024)
    || (s.headScale != null && !(s.headScale >= 0.5 && s.headScale <= 2)) || (s.effectAmount != null && !(s.effectAmount >= 0 && s.effectAmount <= 100))) {
    bad("The drawing contains an unsupported style value.");
  }
  for (const gradient of [s.strokeGradient, s.fillGradient]) {
    if (gradient && !(gradient.colors.length >= 2 && gradient.colors.length <= 8 && Number.isFinite(gradient.angle) && gradient.angle >= 0 && gradient.angle <= 360)) bad("The drawing contains an unsupported style value.");
  }
}
export class CanvasDocument {
  // `clone` copies an element for a history snapshot; `newID` names a copy.
  constructor({ elements = [], historyByteLimit = 64 * 1024 * 1024, historyCountLimit = 100, clone = (el) => structuredClone(el), newID } = {}) {
    Object.assign(this, { elements, selection: new Set(), generation: 0, presentation: { mode: "off", zoom: 2 }, past: [], future: [], pending: null,
      historyByteLimit, historyCountLimit, clone, newID });
    elements.forEach(validate);
    this.resolveBindings();
  }
  get canUndo() { return this.past.length > 0; }
  get canRedo() { return this.future.length > 0; }
  get isEditing() { return this.pending !== null; }
  get hasPendingChanges() { return this.pending !== null && (!this.sameElements(this.pending.elements) || plain(this.pending.presentation) !== plain(this.presentation)); }
  get historyBytes() { return [...this.past, ...this.future].reduce((sum, s) => sum + s.bytes, 0); }
  get snapshot() {
    const elements = this.elements.map(this.clone);
    return { elements, selection: new Set(this.selection), presentation: { ...this.presentation }, bytes: 64 + elements.reduce((sum, el) => sum + estimatedBytes(el), 0), operationID: null };
  }
  sameElements(other) { return other.length === this.elements.length && other.every((el, i) => plain(el) === plain(this.elements[i])); }
  byID(id) { return this.elements.find((el) => el.id === id); }
  begin() { if (!this.pending) this.pending = this.snapshot; }
  commit(operationID = null) {
    const original = this.pending;
    if (!original) throw new Error("There is no active edit to complete.");
    this.resolveBindings();
    this.pending = null;
    if (!this.sameElements(original.elements) || plain(original.presentation) !== plain(this.presentation) || operationID !== null) {
      this.generation++;
      original.operationID = operationID;
      this.past.push(original);
      this.future = [];
      this.trimHistory();
    }
  }
  cancel() {
    if (!this.pending) return;
    this.restore(this.pending);
    this.pending = null;
  }
  undo() {
    if (this.pending) { this.cancel(); return; }
    const previous = this.past.pop();
    if (!previous) return;
    const next = this.snapshot;
    next.operationID = previous.operationID;
    this.future.push(next);
    this.restore(previous);
    this.generation++;
    this.trimHistory();
  }
  redo() {
    this.cancel();
    const next = this.future.pop();
    if (!next) return;
    const previous = this.snapshot;
    previous.operationID = next.operationID;
    this.past.push(previous);
    this.restore(next);
    this.generation++;
    this.trimHistory();
  }
  select(ids, extending = false) {
    const valid = new Set([...ids].filter((id) => this.byID(id)));
    if (!extending) { this.selection = valid; return; }
    const result = new Set(this.selection);
    for (const id of valid) { if (result.has(id)) result.delete(id); else result.add(id); }
    this.selection = result;
  }
  selectAt(point, extending = false) {
    const hit = Stacking.frontmost(point, this.elements);
    if (!hit) { if (!extending) this.selection = new Set(); return; }
    this.select(hit.groupID ? this.elements.filter((el) => el.groupID === hit.groupID).map((el) => el.id) : [hit.id], extending);
  }
  add(el) {
    validate(el);
    if (this.byID(el.id)) throw new Error("The drawing could not be opened: Duplicate object identifier.");
    this.transaction(() => this.elements.push(el));
  }
  update(id, mutation) {
    const index = this.elements.findIndex((el) => el.id === id);
    if (index < 0) throw new Error("The selected object no longer exists.");
    if (this.elements[index].locked) return;
    const changed = this.clone(this.elements[index]);
    mutation(changed);
    if (changed.id !== id) throw new Error("The drawing could not be opened: Object identity cannot change.");
    validate(changed);
    changed.revision++;
    changed.geometryToken = freshToken();
    this.transaction(() => { this.elements[index] = changed; if (Tool.isShape(changed.tool)) this.resolveBindings(); });
  }
  appendSample(id, point, pressure = 1) {
    if (!P.isFinite(point) || !(pressure >= 0 && pressure <= 1)) throw new Error("The drawing contains invalid coordinates or dimensions.");
    const el = this.byID(id);
    if (!el) throw new Error("The selected object no longer exists.");
    if (!Tool.isFreehand(el.tool)) throw new Error("The drawing contains invalid coordinates or dimensions.");
    if (el.locked) return;
    this.transaction(() => {
      const hadPoints = el.points.length > 0;
      if (hadPoints && !el.pressures.length) el.pressures = el.points.map(() => 1);
      el.points.push(point);
      el.pressures.push(pressure);
      el.frame = hadPoints ? R.union(el.frame, R.from(point, point)) : R.from(point, point);
      el.revision++;
      el.geometryToken = freshToken();
    });
  }
  editSelection(mutation) {
    this.transaction(() => {
      this.elements.forEach((el, index) => {
        if (!this.selection.has(el.id) || el.locked) return;
        const changed = this.clone(el);
        mutation(changed);
        if (changed.id !== el.id) throw new Error("The drawing could not be opened: Object identity cannot change.");
        validate(changed);
        changed.revision++;
        changed.geometryToken = freshToken();
        this.elements[index] = changed;
      });
      this.resolveBindings();
    });
  }
  keepSelection() { this.selection = new Set([...this.selection].filter((id) => this.byID(id))); }
  deleteSelection(includingProtection = true) {
    this.transaction(() => {
      this.elements = this.elements.filter((el) => !(this.selection.has(el.id) && !el.locked && (includingProtection || !Tool.isProtection(el.tool))));
      this.keepSelection();
      this.resolveBindings();
    });
  }
  clearAll() { this.transaction(() => { this.elements = []; this.selection = new Set(); this.presentation.mode = "off"; }); }
  clearAnnotations() { this.transaction(() => { this.elements = this.elements.filter((el) => Tool.isProtection(el.tool) || el.locked); this.keepSelection(); this.resolveBindings(); }); }
  clearProtection() { this.transaction(() => { this.elements = this.elements.filter((el) => !Tool.isProtection(el.tool)); this.keepSelection(); }); }
  duplicateSelection() { this.insertCopies(this.elements.filter((el) => this.selection.has(el.id) && !el.locked), [18, 18]); }
  insertCopies(originals, offset) {
    if (!originals.length) return;
    const names = new Map(originals.map((el) => [el.id, this.newID()])), groups = new Map();
    this.transaction(() => {
      const copies = originals.map((original) => {
        const copy = this.clone(original);
        copy.id = names.get(original.id);
        copy.locked = false;
        if (original.groupID != null) { if (!groups.has(original.groupID)) groups.set(original.groupID, this.newID()); copy.groupID = groups.get(original.groupID); }
        translate(copy, offset);
        const remap = (anchor) => (anchor && names.has(anchor.elementID) ? { elementID: names.get(anchor.elementID), relative: anchor.relative } : null);
        copy.startAnchor = remap(original.startAnchor);
        copy.endAnchor = remap(original.endAnchor);
        return copy;
      });
      copies.forEach(validate);
      this.elements.push(...copies);
      this.selection = new Set(copies.map((el) => el.id));
      this.resolveBindings();
    });
  }
  groupSelection() { const group = this.newID(); this.editSelection((el) => { el.groupID = group; }); }
  ungroupSelection() { this.editSelection((el) => { el.groupID = null; }); }
  setSelectionLocked(locked) { this.transaction(() => { for (const el of this.elements) if (this.selection.has(el.id)) el.locked = locked; }); }
  arrangeSelection(toFront) {
    this.transaction(() => {
      const moved = this.elements.filter((el) => this.selection.has(el.id) && !el.locked), ids = new Set(moved.map((el) => el.id));
      const rest = this.elements.filter((el) => !ids.has(el.id));
      this.elements = toFront ? [...rest, ...moved] : [...moved, ...rest];
    });
  }
  nearestAnchor(point, excluding, threshold = 18) {
    let nearest = null;
    for (const el of this.elements) {
      if (el.id === excluding || !Tool.isShape(el.tool)) continue;
      for (const location of anchorLocations(el)) {
        const distance = P.dist(anchorPoint(el, location), point);
        if (distance <= threshold && distance < (nearest?.distance ?? Infinity)) nearest = { anchor: { elementID: el.id, relative: location }, distance };
      }
    }
    return nearest?.anchor ?? null;
  }
  // Smart Draw's shape takes the stroke's place without a history step of its own, so one Undo removes it.
  applyRecognition(replacement, expectedGeneration, expectedGeometryToken) {
    const index = this.elements.findIndex((el) => el.id === replacement.id), current = this.elements[index];
    if (this.pending || this.generation !== expectedGeneration || !current || current.geometryToken !== expectedGeometryToken || current.tool !== "pen" || current.locked
      || !(Tool.isShape(replacement.tool) || replacement.tool === "arrow")) return false;
    validate(replacement);
    replacement.geometryToken = freshToken();
    this.elements[index] = replacement;
    this.generation++;
    return true;
  }
  setPresentation(settings) { this.transaction(() => { this.presentation = { ...settings }; }); }
  stopPresentationWithoutHistory() {
    this.presentation.mode = "off";
    if (this.pending) this.pending.presentation.mode = "off";
    const last = this.past[this.past.length - 1];
    if (last && this.sameElements(last.elements) && plain(last.presentation) === plain(this.presentation)) this.past.pop();
  }
  transaction(mutation) {
    const owns = !this.pending;
    if (owns) this.begin();
    try {
      mutation();
      if (owns) this.commit();
    } catch (error) {
      this.cancel();
      throw error;
    }
  }
  // An arrow or line end that is attached to a shape follows that shape.
  resolveBindings() {
    const targets = new Map(this.elements.filter((el) => Tool.isShape(el.tool)).map((el) => [el.id, el]));
    for (const el of this.elements) {
      if (!Tool.isConnector(el.tool) || el.points.length < 2) continue;
      for (const isStart of [true, false]) {
        const key = isStart ? "startAnchor" : "endAnchor", anchor = el[key];
        if (!anchor) continue;
        const target = targets.get(anchor.elementID);
        if (!target) { el[key] = null; continue; }
        const index = isStart ? 0 : el.points.length - 1, resolved = anchorPoint(target, anchor.relative);
        if (!P.same(el.points[index], resolved)) {
          el.points[index] = resolved;
          el.frame = R.enclosing(el.points);
          el.revision++;
          el.geometryToken = freshToken();
        }
      }
    }
  }
  restore(snapshot) {
    this.elements = snapshot.elements.map(this.clone);
    this.selection = new Set([...snapshot.selection].filter((id) => this.byID(id)));
    this.presentation = { ...snapshot.presentation };
  }
  trimHistory() {
    while (this.past.length > this.historyCountLimit) this.past.shift();
    while (this.future.length > this.historyCountLimit) this.future.shift();
    while (this.historyBytes > this.historyByteLimit) {
      if (this.past.length) this.past.shift(); else if (this.future.length) this.future.shift(); else break;
    }
  }
}
