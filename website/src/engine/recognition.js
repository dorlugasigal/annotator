// Smart Draw: a JavaScript port of apps/macos/Sources/AnnotationCore/Recognition.swift, with the same thresholds in the
// same order. A pen stroke becomes a rectangle, a diamond, an ellipse (each fitted, and turned when it was drawn turned)
// or a straight arrow with an open head; anything else stays a pen stroke. test-recognition.mjs runs it against
// reference/conformance/recognition.
import { P, R, makeElement, freshToken } from "./annotation-core.js";

const length = (points) => points.reduce((sum, p, i) => (i ? sum + P.dist(points[i - 1], p) : 0), 0);
function segmentDistance(point, start, end) {
  const vector = P.sub(end, start), squared = P.dot(vector, vector);
  if (!(squared > 0)) return P.dist(point, start);
  const t = Math.min(1, Math.max(0, P.dot(P.sub(point, start), vector) / squared));
  return P.dist(point, P.add(start, P.mul(vector, t)));
}
const pathDistance = (point, path) => path.reduce((least, p, i) => (i ? Math.min(least, segmentDistance(point, path[i - 1], p)) : least), Infinity);

export function resample(points, count) {
  if (!points.length) return [];
  const first = points[0], last = points[points.length - 1], distances = [0];
  for (let i = 1; i < points.length; i++) distances.push(distances[i - 1] + P.dist(points[i - 1], points[i]));
  const total = distances[distances.length - 1];
  if (!(total > 0)) return [first];
  let index = 1;
  const output = [first];
  for (let sample = 1; sample < count - 1; sample++) {
    const position = total * sample / (count - 1);
    while (index < points.length - 1 && distances[index] < position) index++;
    const segment = distances[index] - distances[index - 1], share = segment > 0 ? (position - distances[index - 1]) / segment : 0;
    output.push(P.add(points[index - 1], P.mul(P.sub(points[index], points[index - 1]), share)));
  }
  output.push(last);
  return output;
}

function outline(tool, box) {
  const c = R.center(box), maxX = R.maxX(box), maxY = R.maxY(box);
  if (tool === "rectangle") return [[box.x, box.y], [maxX, box.y], [maxX, maxY], [box.x, maxY], [box.x, box.y]];
  if (tool === "diamond") return [[c[0], box.y], [maxX, c[1]], [c[0], maxY], [box.x, c[1]], [c[0], box.y]];
  return Array.from({ length: 49 }, (_, i) => {
    const angle = i / 48 * 2 * Math.PI;
    return [c[0] + box.width / 2 * Math.cos(angle), c[1] + box.height / 2 * Math.sin(angle)];
  });
}

// The stroke as the preview sees it while it is drawn: at most 256 of its points, evenly picked.
export function previewInput(element) {
  const total = element.points.length, count = Math.min(256, total);
  const points = count < 2 ? element.points : Array.from({ length: count }, (_, i) => element.points[Math.floor(i * (total - 1) / (count - 1))]);
  return { ...element, points };
}

function preservingIdentity(result, source) {
  return { ...result, groupID: source.groupID, locked: source.locked, seed: source.seed, revision: source.revision + 1, geometryToken: freshToken() };
}

function recognizeLoop(samples, source) {
  const box = R.enclosing(samples), first = samples[0], last = samples[samples.length - 1];
  if (!(box.width >= 16 && box.height >= 16)) return null;
  const diagonal = Math.hypot(box.width, box.height);
  if (!(P.dist(first, last) <= diagonal * 0.28)) return null;
  const center = P.mul(samples.reduce((sum, p) => P.add(sum, p), [0, 0]), 1 / samples.length);
  const loop = [...samples, first];
  let winding = 0, turning = 0;
  for (let i = 1; i < loop.length; i++) {
    const a = P.sub(loop[i - 1], center), b = P.sub(loop[i], center);
    const angle = Math.atan2(a[0] * b[1] - a[1] * b[0], P.dot(a, b));
    winding += angle;
    turning += Math.abs(angle);
  }
  if (!(Math.abs(winding) >= Math.PI * 1.5 && Math.abs(winding) <= Math.PI * 2.6 && turning <= Math.abs(winding) * 1.45 + 0.4)) return null;

  let xx = 0, yy = 0, xy = 0;
  for (const p of samples) {
    const d = P.sub(p, center);
    xx += d[0] * d[0]; yy += d[1] * d[1]; xy += d[0] * d[1];
  }
  let principal = Math.atan2(2 * xy, xx - yy) / 2;
  while (principal > Math.PI / 4) principal -= Math.PI / 2;
  while (principal < -Math.PI / 4) principal += Math.PI / 2;
  let best = null;
  for (const angle of [-Math.PI / 6, -Math.PI / 12, 0, Math.PI / 12, Math.PI / 6, principal]) {
    const rotated = samples.map((p) => P.rotated(P.sub(p, center), [0, 0], -angle));
    const bounds = R.enclosing(rotated);
    if (!(bounds.width >= 16 && bounds.height >= 16)) continue;
    const closed = [...rotated, rotated[0]], size = Math.hypot(bounds.width, bounds.height);
    for (const tool of ["rectangle", "diamond", "ellipse"]) {
      const template = outline(tool, bounds), expectedLength = length(template), observedLength = length(closed);
      if (!(observedLength >= expectedLength * 0.70 && observedLength <= expectedLength * 1.45)) continue;
      // Both directions are checked, so a partial outline cannot pass because its few drawn edges match the template.
      const errors = [...rotated.map((p) => pathDistance(p, template)), ...resample(template, 48).map((p) => pathDistance(p, closed))];
      const mean = errors.reduce((sum, e) => sum + e, 0) / errors.length / size;
      const sorted = [...errors].sort((a, b) => a - b);
      if (!(mean <= 0.055 && sorted[Math.floor(sorted.length * 9 / 10)] / size <= 0.12)) continue;
      const score = mean + Math.abs(angle) / (Math.PI / 4) * 0.003;
      if (score < (best?.score ?? Infinity)) {
        const fitted = P.add(P.rotated(R.center(bounds), [0, 0], angle), center);
        best = { tool, angle, score, frame: { x: fitted[0] - bounds.width / 2, y: fitted[1] - bounds.height / 2, width: bounds.width, height: bounds.height } };
      }
    }
  }
  if (!best) return null;
  const result = makeElement({ id: source.id, tool: best.tool, frame: best.frame, style: source.style });
  result.rotation = best.angle;
  return preservingIdentity(result, source);
}

function recognizeArrow(samples, source) {
  for (const points of [samples, [...samples].reverse()]) {
    const tail = points[0], distances = points.map((p) => P.dist(tail, p)), farthest = Math.max(...distances);
    // Resampling can put a retraced tip slightly farther out than its first visit. The head's return trip stays out of the shaft.
    let tipIndex = -1;
    for (let i = 1; i < points.length - 1; i++) {
      if (distances[i] >= farthest * 0.97 && distances[i] >= distances[i - 1] && distances[i] >= distances[i + 1]) { tipIndex = i; break; }
    }
    if (tipIndex < 0 || !(tipIndex >= 4 && tipIndex < points.length - 6)) continue;
    const tip = points[tipIndex], span = P.dist(tail, tip);
    if (!(span >= 32)) continue;
    const axis = P.mul(P.sub(tip, tail), 1 / span), normal = [-axis[1], axis[0]];
    const shaft = points.slice(0, tipIndex + 1);
    const shaftError = shaft.reduce((sum, p) => sum + Math.abs(P.dot(P.sub(p, tail), normal)), 0) / shaft.length;
    if (!(shaftError / span <= 0.035 && length(shaft) <= span * 1.18)) continue;
    const head = points.slice(tipIndex);
    const inReach = head.every((p) => {
      const offset = P.sub(p, tip), along = P.dot(offset, axis);
      return along >= -span * 0.5 && along <= span * 0.08 && Math.abs(P.dot(offset, normal)) <= span * 0.4;
    });
    if (!inReach) continue;
    const across = (p) => P.dot(P.sub(p, tip), normal);
    // As Swift's max(by:) and min(by:): the first of equal elements wins.
    let left = head[0], right = head[0];
    for (const p of head) { if (across(left) < across(p)) left = p; if (across(p) < across(right)) right = p; }
    const leftWidth = across(left), rightWidth = -across(right);
    const leftBack = -P.dot(P.sub(left, tip), axis), rightBack = -P.dot(P.sub(right, tip), axis);
    if (!(Math.min(leftWidth, rightWidth) >= Math.max(5, span * 0.04) && Math.min(leftBack, rightBack) >= span * 0.035
      && Math.max(leftWidth, rightWidth) <= Math.min(leftWidth, rightWidth) * 3 && Math.max(leftBack, rightBack) <= Math.min(leftBack, rightBack) * 3)) continue;
    const headError = head.reduce((sum, p) => sum + Math.min(segmentDistance(p, tip, left), segmentDistance(p, tip, right)), 0) / head.length;
    if (!(headError / span <= 0.04 && length(head) <= (P.dist(tip, left) + P.dist(tip, right)) * 2.1)) continue;
    const style = { ...source.style, startHead: "none", endHead: "open", curved: false };
    style.headScale = Math.min(2, Math.max(0.5, (leftBack + rightBack) / 2 / (2 * Math.max(12, style.width * 4))));
    return preservingIdentity(makeElement({ id: source.id, tool: "arrow", points: [tail, tip], style }), source);
  }
  return null;
}

// The shape or arrow a pen stroke stands for, or null when it should stay as drawn.
export function recognize(element) {
  if (element.tool !== "pen" || element.points.length < 5) return null;
  const bounded = previewInput(element).points;
  if (!bounded.every(P.isFinite)) return null;
  const samples = resample(bounded, 96);
  if (samples.length < 6) return null;
  return recognizeArrow(samples, element) || recognizeLoop(samples, element);
}
