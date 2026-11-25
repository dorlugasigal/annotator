// Annotator's own motion, for the web: the app's curves, durations and keyframes, with no DOM in it.
// Ported from apps/macos/Sources/MotionCore/MotionCore.swift, Annotator/ToolbarSlideMotion.swift (VisibilityPose,
// WorkspaceMotionTiming, WorkspaceLayerMotion), MagicFlight.swift, MagicToolbarEffect.swift, CornerRoutes.swift,
// PresentationRendering/CursorHighlightRenderer.swift and ParticleBurstView.swift; test-motion.mjs checks it against
// reference/conformance/motion. Durations are in seconds, as in the app; keyframe lists carry `duration` in
// milliseconds and `easing`, ready for element.animate(list, { duration: list.duration, easing: list.easing, fill: "both" }).
//   css.standard, ease.standard(t), DURATIONS.toolbarCollapse, toolbarMorph.shape(t), visibilityDuration("magic", false)
//   magicFlight([0, 0], [240, 180], "any id")   poseKeyframes("pop", { appearing: false })   toolbarPoof(false)
// Keyframes use the separate `translate`, `rotate` and `scale` properties, so an element keeps its own `transform`.
// Points are CSS pixels with y down; the app's y-up math is turned over inside.

const clamp01 = (v) => Math.min(1, Math.max(0, v));

// MotionCurves ----------------------------------------------------------------------------------------
export const CURVES = {
  enter: [0.2, 0, 0, 1], exit: [0.45, 0, 0.55, 0.4], standard: [0.3, 0, 0.2, 1], fade: [0.2, 0, 0.4, 1], linear: [0, 0, 1, 1],
  easeIn: [0.42, 0, 1, 1], easeOut: [0, 0, 0.58, 1], easeInEaseOut: [0.42, 0, 0.58, 1],
  // CursorHighlightRenderer's click ease and MagicFlight.timingFunction.
  pointerClick: [0.4, 0, 0.2, 1], magicFlight: [0.22, 0.06, 0.24, 1],
};
// MotionCurves.cubic: the eased progress of a cubic Bézier timing curve at linear time x.
export function cubic(x, x1, y1, x2, y2) {
  if (!(x > 0)) return 0;
  if (!(x < 1)) return 1;
  const sample = (t, a, b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
  let low = 0, high = 1;
  for (let i = 0; i < 40; i++) { const middle = (low + high) / 2; if (sample(middle, x1, x2) < x) low = middle; else high = middle; }
  return sample((low + high) / 2, y1, y2);
}
export const smooth = (value) => { const t = clamp01(value); return t * t * (3 - 2 * t); };
export const dropSettle = (time) => (time >= 1 ? 1 : time <= 0 ? 0 : 1 - Math.exp(-6 * time) * Math.cos(9.5 * time));
// The curves as functions of time 0...1, and as CSS timing functions.
export const ease = { smooth, dropSettle };
export const css = {};
for (const [name, c] of Object.entries(CURVES)) {
  ease[name] = (t) => cubic(t, c[0], c[1], c[2], c[3]);
  css[name] = name === "linear" ? "linear" : `cubic-bezier(${c.join(", ")})`;
}

// MotionTiming ----------------------------------------------------------------------------------------
export const DURATIONS = {
  toolbarShow: 0.36, toolbarHide: 0.28, minimumReversalShare: 0.35, toolbarResize: 0.22, toolbarCollapse: 0.3, toolbarExpand: 0.34,
  fadeIn: 0.16, fadeOut: 0.12, reduced: 0.12, snapMorph: 0.22, eraseFade: 0.12, pointerClick: 0.45,
  zoomIn: 0.26, zoomOut: 0.2, spotlightFadeIn: 0.18, spotlightFadeOut: 0.14, spotlightResize: 0.12,
  hintFadeIn: 0.16, hintFadeOut: 0.12, hintTextCrossfade: 0.14, hintDwell: 5,
};
export const FADES = {
  fadeIn: { duration: 0.16, curve: "easeOut" }, fadeOut: { duration: 0.12, curve: "easeIn" }, reduced: { duration: 0.12, curve: "easeOut" },
};
export const SPRINGS = {
  snappy: { response: 0.28, dampingFraction: 0.86 }, smooth: { response: 0.38, dampingFraction: 0.9 }, playful: { response: 0.3, dampingFraction: 0.62 },
};
// SpringMotion.value(at:): how far a spring has gone from 0 to 1 after `time` seconds.
export function spring({ response, dampingFraction: zeta }, time) {
  if (!(time > 0)) return 0;
  const omega = (2 * Math.PI) / response;
  if (zeta < 1) {
    const damped = omega * Math.sqrt(1 - zeta * zeta);
    return 1 - Math.exp(-zeta * omega * time) * (Math.cos(damped * time) + ((zeta * omega) / damped) * Math.sin(damped * time));
  }
  if (zeta === 1) return 1 - Math.exp(-omega * time) * (1 + omega * time);
  const root = omega * Math.sqrt(zeta * zeta - 1), fast = -zeta * omega - root, slow = -zeta * omega + root;
  return 1 - (fast * Math.exp(slow * time) - slow * Math.exp(fast * time)) / (fast - slow);
}
export function springSettleTime(motion, tolerance = 0.001, limit = 5) {
  let settled = limit;
  for (let ms = Math.round(limit * 1000); ms >= 0; ms--) {
    if (Math.abs(1 - spring(motion, ms / 1000)) > tolerance) break;
    settled = ms / 1000;
  }
  return settled;
}

// ToolbarMorph: the toolbar and its one-button form -------------------------------------------------------
// The background's frame and corner radius follow `shape`; the face that leaves is gone by half the time and the
// new face starts at 35%. With Reduce Motion the frame changes at once and the faces cross-fade over DURATIONS.reduced.
export const toolbarMorph = {
  collapseDuration: DURATIONS.toolbarCollapse, expandDuration: DURATIONS.toolbarExpand, outgoingEnd: 0.5, incomingStart: 0.35,
  shape: (t) => ease.standard(t),
  outgoingOpacity: (t) => 1 - ease.fade(t / 0.5),
  incomingOpacity: (t) => ease.fade((t - 0.35) / (1 - 0.35)),
  // ToolbarFrameAnimator.interpolate: whole-pixel frames between two { x, y, width, height } rects.
  frame(start, end, t) {
    const p = ease.standard(t), mix = (a, b) => a + (b - a) * p;
    const x = Math.floor(mix(start.x, end.x)), y = Math.floor(mix(start.y, end.y));
    return { x, y, width: Math.ceil(mix(start.x + start.width, end.x + end.width)) - x, height: Math.ceil(mix(start.y + start.height, end.y + end.height)) - y };
  },
  // ToolbarPin.frame(size:keeping:): a frame of `size` that keeps the pin's point of `frame` in place.
  pinned(size, frame, pin = "center") {
    const x = pin === "leading" ? frame.x : pin === "trailing" ? frame.x + frame.width - size.width : frame.x + frame.width / 2 - size.width / 2;
    const y = pin === "top" ? frame.y : pin === "bottom" ? frame.y + frame.height - size.height : frame.y + frame.height / 2 - size.height / 2;
    return { x: Math.floor(x), y: Math.floor(y), width: size.width, height: size.height };
  },
};

// Show and hide ---------------------------------------------------------------------------------------
// Preferences.swift: the styles the app offers, the one it starts with, and what the older names turn into.
export const VISIBILITY_STYLES = ["dissolve", "slide", "magic", "corners", "pop", "drop", "focus", "fade", "none"];
export const DEFAULT_VISIBILITY = "slide";
export const resolveVisibility = (style) => (style === "fluid" || style === "quick" ? "slide"
  : ["reveal", "curtain", "fold", "rise"].includes(style) ? "fade" : VISIBILITY_STYLES.includes(style) ? style : DEFAULT_VISIBILITY);
export function visibilityDuration(style, appearing) {
  switch (style) {
    case "none": return 0;
    case "quick": return appearing ? 0.18 : 0.14;
    case "fade": return appearing ? 0.2 : 0.16;
    case "dissolve": return appearing ? 0.65 : 0.55;
    case "magic": return appearing ? 0.62 : 0.54;
    case "corners": return appearing ? 0.38 : 0.3;
    case "pop": return appearing ? 0.34 : 0.26;
    case "drop": return appearing ? 0.4 : 0.32;
    case "focus": return appearing ? 0.36 : 0.28;
    default: return appearing ? DURATIONS.toolbarShow : DURATIONS.toolbarHide;
  }
}
// WorkspaceMotionTiming: when the toolbar and each drawing start. Drawings leave nearest first and come back in
// the opposite order (`rank` from distanceOrder).
export function workspaceTiming(style, appearing, objectCount = 0) {
  const hasDrawings = objectCount > 0, last = Math.max(0, objectCount - 1);
  const duration = visibilityDuration(style, appearing);
  const toolbarDuration = style === "magic" ? (appearing ? 0.4 : 0.36) : style === "slide" || style === "corners" ? (appearing ? 0.3 : 0.24) : duration;
  const lead = hasDrawings && style !== "fade" && style !== "none" ? (style === "dissolve" ? 0.12 : 0.07) : 0;
  const objectStep = lead > 0 && objectCount > 1 ? Math.min(0.065, 0.26 / (objectCount - 1)) : 0;
  let toolbarDelay, drawingsDelay;
  if ((style === "slide" || style === "magic" || style === "corners") && hasDrawings) {
    const lastArrival = duration + objectStep * last;
    toolbarDelay = appearing ? 0 : style === "magic" ? Math.max(0, lastArrival - toolbarDuration * 0.6) : lastArrival + 0.025;
    drawingsDelay = appearing ? toolbarDuration + 0.035 : 0;
  } else {
    toolbarDelay = appearing ? 0 : lead + objectStep * last;
    drawingsDelay = appearing ? lead : 0;
  }
  const drawingsEndDelay = drawingsDelay + objectStep * last;
  return {
    duration, toolbarDuration, toolbarDelay, drawingsDelay, objectStep, drawingsEndDelay,
    totalDuration: Math.max(toolbarDelay + toolbarDuration, hasDrawings ? drawingsEndDelay + duration : 0),
    drawingDelay: (rank) => drawingsDelay + objectStep * (appearing ? Math.max(0, last - rank) : rank),
  };
}
// ToolbarSlideMotion.distanceOrder: ids, nearest to their target first. Items are { id, center: [x, y], target: [x, y] }.
export function distanceOrder(items) {
  return items.map((item) => [item.id, Math.hypot(item.center[0] - item.target[0], item.center[1] - item.target[1])])
    .sort((a, b) => (a[1] !== b[1] ? a[1] - b[1] : a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0)).map((item) => item[0]);
}
// ToolbarSlideMotion.annotationTransform: where a drawing of this size ends when it is gathered into `target`.
export function gatheredPose(rect, target) {
  const scale = Math.min(0.12, 24 / Math.max(1, rect.width, rect.height));
  return { x: target[0] - (rect.x + rect.width / 2), y: target[1] - (rect.y + rect.height / 2), scale };
}
// CornerRoutes: the nearest corner of `area` for a drawing, and which way its path bends.
export function cornerRoute(id, center, area) {
  const inset = Math.min(24, Math.min(area.width, area.height) / 4);
  const corner = (index) => [index % 2 === 0 ? area.x + inset : area.x + area.width - inset, index < 2 ? area.y + inset : area.y + area.height - inset];
  let point = corner(0), distance = Infinity;
  for (let index = 0; index < 4; index++) {
    const target = corner(index), candidate = Math.hypot(target[0] - center[0], target[1] - center[1]);
    if (candidate < distance) { point = target; distance = candidate; }
  }
  let parity = 0;
  for (const byte of new TextEncoder().encode(String(id))) parity ^= byte;
  return { point, bendSign: parity % 2 === 0 ? 1 : -1 };
}

const frames = (list, duration, easing = "linear") => Object.defineProperties(list, {
  duration: { value: duration * 1000, writable: true }, easing: { value: easing, writable: true },
});
const place = (v) => (Array.isArray(v) ? { x: v[0], y: v[1], scale: v[2] ?? 1 } : { x: v.x ?? 0, y: v.y ?? 0, scale: v.scale ?? 1 });
const px = (n) => `${Math.round(n * 1000) / 1000}px`;
const num = (n) => String(Math.round(n * 100000) / 100000);

// VisibilityPose: Pop, Drop and Focus. `hidden` is 0 at rest and 1 when gone; `annotation` is true for a drawing and
// false for the toolbar; an even `seed` tips a dropped drawing one way and an odd one the other.
export function pose(style, hidden, { appearing = false, annotation = true, seed = 0 } = {}) {
  const p = Math.max(0, hidden);
  if (style === "pop") {
    const peak = annotation ? 0.06 : 0.03, low = annotation ? 0.18 : 0.6;
    const scale = p <= 0.22 ? 1 + peak * Math.sin(((p / 0.22) * Math.PI) / 2) : (1 + peak) * Math.exp(Math.log(low / (1 + peak)) * smooth((p - 0.22) / 0.78));
    return { x: 0, y: 0, scale, rotate: 0, opacity: 1 - smooth((p - 0.6) / 0.4), blur: 0 };
  }
  if (style === "drop") {
    const side = BigInt(asSeed(seed)) % 2n === 0n ? 1 : -1;
    // The app's y grows upward and its angles turn the other way.
    if (appearing) return { x: 0, y: -(annotation ? 56 : 20) * hidden, scale: 1, rotate: annotation ? -side * 0.08 * p : 0, opacity: 1 - smooth((hidden - 0.55) / 0.45), blur: 0 };
    return { x: 0, y: (annotation ? 110 : 28) * p * p, scale: 1 - 0.06 * p, rotate: annotation ? -side * 0.16 * p * p : 0, opacity: 1 - smooth((p - 0.3) / 0.7), blur: 0 };
  }
  if (style === "focus") return { x: 0, y: 0, scale: 1 + (annotation ? 0.06 : 0.03) * p, rotate: 0, opacity: 1 - smooth((p - 0.15) / 0.85), blur: (annotation ? 12 : 8) * p };
  return { x: 0, y: 0, scale: 1, rotate: 0, opacity: hidden >= 1 ? 0 : 1, blur: 0 };
}
// VisibilityPose.hidden's easing: how far a pose has gone at linear time 0...1.
export function poseProgress(style, time, appearing, reversing = false) {
  if (reversing) return ease.standard(time);
  if (style === "drop") return appearing ? dropSettle(time) : clamp01(time);
  return appearing ? ease.enter(time) : ease.standard(time);
}
export const usesPose = (style) => style === "pop" || style === "drop" || style === "focus";
// Keyframes for Pop, Drop or Focus on any element: 30 steps, as the app samples them.
export function poseKeyframes(style, { appearing = false, annotation = true, seed = 0 } = {}) {
  const list = [], from = appearing ? 1 : 0, to = appearing ? 0 : 1;
  for (let step = 0; step <= 30; step++) {
    const hidden = from + (to - from) * poseProgress(style, step / 30, appearing);
    const at = pose(style, hidden, { appearing, annotation, seed });
    const frame = { offset: step / 30, translate: `${px(at.x)} ${px(at.y)}`, rotate: `${num(at.rotate)}rad`, scale: num(at.scale), opacity: num(at.opacity) };
    if (style === "focus") frame.filter = `blur(${px(at.blur)})`;
    list.push(frame);
  }
  return frames(list, visibilityDuration(style, appearing));
}
// A plain fade (the Fade style, and what stands in when Reduce Motion is on): the fade curve in, linear out.
export function fadeKeyframes(appearing, duration = visibilityDuration("fade", appearing)) {
  return frames(appearing ? [{ opacity: 0 }, { opacity: 1 }] : [{ opacity: 1 }, { opacity: 0 }], duration, appearing ? css.fade : "linear");
}
// The opacity of something that also travels (Slide, Corners, Magic): full early when it appears, held late when it hides.
export function travelFade(appearing, magic = false) {
  const list = appearing ? [{ offset: 0, opacity: 0 }, { offset: magic ? 0.15 : 0.2, opacity: 1 }, { offset: 1, opacity: 1 }]
    : [{ offset: 0, opacity: 1 }, { offset: magic ? 0.82 : 0.72, opacity: 1 }, { offset: 1, opacity: 0 }];
  return frames(list, 0, magic ? css.magicFlight : appearing ? css.fade : "linear");
}
// WorkspaceLayerMotion's bent path for Slide and Corners: from one { x, y, scale } to another. Slide bends by up to
// 40 px, Corners by up to 28 px to the side `bendSign` names.
export function travelKeyframes(style, from, to, { appearing = false, bendSign = 1 } = {}) {
  const a = place(from), b = place(to), dx = b.x - a.x, dy = -(b.y - a.y), length = Math.max(1, Math.hypot(dx, dy));
  const bend = style === "corners" ? Math.min(28, length * 0.06) * bendSign : Math.min(40, length * 0.12);
  const startScale = Math.max(0.001, a.scale), endScale = Math.max(0.001, b.scale), list = [];
  for (let step = 0; step <= 24; step++) {
    const t = step / 24, arc = Math.sin(t * Math.PI) * bend;
    // Shapes keep their size early in a hide and grow early in a show; log space keeps the shrink even.
    const weight = appearing ? smooth(t / 0.8) : smooth((t - 0.2) / 0.8);
    const scale = weight <= 0 ? startScale : weight >= 1 ? endScale : Math.exp(Math.log(startScale) + (Math.log(endScale) - Math.log(startScale)) * weight);
    const x = a.x + dx * t - (dy / length) * arc, y = -a.y + dy * t + (dx / length) * arc;
    list.push({ offset: t, translate: `${px(x)} ${px(-y)}`, scale: num(scale) });
  }
  return frames(list, visibilityDuration(style, appearing), appearing ? css.enter : css.exit);
}

// MagicFlight -----------------------------------------------------------------------------------------
const MASK = (1n << 64n) - 1n;
// MagicFlight.seed(for:): FNV-1a over the id's UTF-8 bytes, as a 64-bit BigInt.
export function seedFor(id) {
  let hash = 14695981039346656037n;
  for (const byte of new TextEncoder().encode(String(id))) hash = ((hash ^ BigInt(byte)) * 1099511628211n) & MASK;
  return hash;
}
const asSeed = (seed) => (typeof seed === "bigint" ? seed & MASK : typeof seed === "number" ? BigInt(Math.abs(Math.trunc(seed))) : seedFor(seed));
// What the app mixes into a drawing's seed when it comes back, so the way back differs from the way out.
export const returnSeed = (seed) => asSeed(seed) ^ 0x9e3779b97f4a7c15n;
export const MAGIC = { steps: 96, maximumBend: 110, maximumBank: 0.15, envelopePeak: 0.4572 };
// The swoop of one drawing: 97 keyframes from one { x, y, scale } (or [x, y]) to another, offsets from where the
// element rests. `seed` is a number, a BigInt or any id text. The list also has `points`, the path's [x, y] offsets.
export function magicFlight(from, to, seed = 0) {
  const a = place(from), b = place(to), value = asSeed(seed);
  const fx = a.x, fy = -a.y, dx = b.x - a.x, dy = -(b.y - a.y), distance = Math.hypot(dx, dy);
  const direction = distance > 0.001 ? [dx / distance, dy / distance] : [1, 0], normal = [-direction[1], direction[0]];
  const amplitude = Math.min(MAGIC.maximumBend, Math.max(24, distance * 0.18));
  const cycles = 1 + Number(value % 2n) * 0.5, phase = (Number((value >> 8n) % 360n) * Math.PI) / 180;
  const point = (t) => {
    const envelope = (Math.pow(1 - t, 1.6) * Math.sin(Math.PI * t)) / MAGIC.envelopePeak;
    const side = amplitude * envelope * Math.sin(2 * Math.PI * cycles * t + phase), forward = amplitude * 0.12 * envelope * Math.sin(2 * Math.PI * t + phase);
    return [fx + dx * t + direction[0] * forward + normal[0] * side, fy + dy * t + direction[1] * forward + normal[1] * side];
  };
  const list = [], points = [];
  for (let step = 0; step <= MAGIC.steps; step++) {
    const t = step / MAGIC.steps;
    let at = [fx + dx * t, fy + dy * t], bank = 0, scale = step === 0 ? a.scale : b.scale;
    if (step > 0 && step < MAGIC.steps) {
      at = point(t);
      const before = point(Math.max(0, t - 0.003)), after = point(Math.min(1, t + 0.003)), vx = after[0] - before[0], vy = after[1] - before[1];
      const turn = Math.atan2(vx * normal[0] + vy * normal[1], vx * direction[0] + vy * direction[1]);
      bank = Math.min(MAGIC.maximumBank, Math.max(-MAGIC.maximumBank, turn * 0.18 * Math.sin(Math.PI * t)));
      scale = a.scale + (b.scale - a.scale) * t;
    }
    points.push([at[0], -at[1]]);
    list.push({ offset: t, translate: `${px(at[0])} ${px(-at[1])}`, rotate: `${num(-bank)}rad`, scale: num(scale) });
  }
  Object.defineProperty(list, "points", { value: points });
  return frames(list, 0, css.magicFlight);
}
// MagicTrail: the light a flying drawing leaves. `points` are a flight's points moved to the element's middle. It
// gives the trail's path (use pathLength="1"), three strokes with dash keyframes, and six sparks; every part fades
// with `fade(peak)` and runs on the flight's easing and duration.
export function magicTrail(points, seed = 0) {
  const value = asSeed(seed), count = points.length, lengths = [0];
  for (let i = 1; i < count; i++) lengths.push(lengths[i - 1] + Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]));
  const total = Math.max(1, lengths[count - 1]), tail = Math.min(0.28, 110 / total);
  const dash = frames(lengths.map((length, i) => {
    const end = length / total, start = Math.max(0, end - tail);
    return { offset: i / (count - 1), strokeDasharray: `${num(end - start)} 2`, strokeDashoffset: num(-start) };
  }), 0, css.magicFlight);
  const fade = (peak) => frames([{ offset: 0, opacity: 0 }, { offset: 0.12, opacity: peak }, { offset: 0.75, opacity: peak * 0.7 }, { offset: 1, opacity: 0 }], 0, css.magicFlight);
  const blue = "rgb(76.5 191.25 255)", white = "rgb(216.75 244.8 255)", gold = "rgb(255 193.8 76.5)";
  const sparks = [];
  for (let index = 0; index < 6; index++) {
    const phase = (Number(((value + BigInt(index) * 31n) & MASK) % 360n) * Math.PI) / 180;
    sparks.push({
      size: index % 2 === 0 ? 2 : 3, color: index % 3 === 0 ? gold : white, peak: 0.9,
      keyframes: frames(points.map((_, sample) => {
        const t = sample / (count - 1), at = points[Math.max(0, sample - 2 - index * 2)], spread = Math.sin(Math.PI * t) * (3 + index);
        return { offset: t, translate: `${px(at[0] + Math.sin(t * 12 + phase) * spread)} ${px(at[1] - Math.cos(t * 9 + phase) * spread)}` };
      }), 0, css.magicFlight),
    });
  }
  return {
    path: `M${points.map((p) => `${num(p[0])} ${num(p[1])}`).join("L")}`, dash, fade, sparks,
    strokes: [{ width: 9, color: blue, peak: 0.1 }, { width: 4, color: blue, peak: 0.3 }, { width: 1.25, color: white, peak: 0.85 }],
  };
}

// MagicToolbarEffect: the toolbar's poof -------------------------------------------------------------------
export function poofPose(progress) {
  const step = (lower, upper) => smooth((progress - lower) / (upper - lower));
  const burst = step(0.62, 1);
  return {
    scale: 1 - 0.985 * step(0, 0.62), imageOpacity: 1 - step(0.44, 0.64), glowOpacity: step(0.3, 0.6) * (1 - step(0.7, 0.95)),
    radius: 46 * burst, sparkOpacity: step(0.62, 0.72) * (1 - step(0.78, 1)),
  };
}
// Keyframes for the picture that shrinks to a point, the glow at its middle and the twelve sparks around it. Place
// the glow and the sparks at the picture's middle. Hiding takes 0.36 s and showing 0.40 s in the app.
export function toolbarPoof(appearing = false) {
  const from = appearing ? 1 : 0, to = appearing ? 0 : 1, duration = appearing ? 0.4 : 0.36;
  const poses = Array.from({ length: 61 }, (_, step) => poofPose(from + (to - from) * smooth(step / 60)));
  const keys = (make) => frames(poses.map((p, step) => ({ offset: step / 60, ...make(p) })), duration);
  const blue = "63.75 178.5 255";
  return {
    duration,
    image: keys((p) => ({ scale: num(p.scale), opacity: num(p.imageOpacity) })),
    glow: { size: 36, background: `radial-gradient(circle closest-side, #fff 0, rgb(${blue} / 0.8) 22%, rgb(${blue} / 0) 100%)`, keyframes: keys((p) => ({ opacity: num(p.glowOpacity) })) },
    sparks: Array.from({ length: 12 }, (_, index) => {
      const angle = (index * 2 * Math.PI) / 12, distance = 0.7 + (index % 4) * 0.1;
      return {
        size: 2.5, color: index % 3 === 0 ? "#fff" : `rgb(${blue})`,
        keyframes: keys((p) => ({ translate: `${px(Math.cos(angle) * p.radius * distance)} ${px(-Math.sin(angle) * p.radius * distance)}`, opacity: num(p.sparkOpacity) })),
      };
    }),
  };
}
// ToolbarSlideMotion.pulse: the small swell of the toolbar as gathered drawings land in it (Slide). `arrivals` are
// seconds; gives scale keyframes and when they start.
export function absorbPulse(arrivals, amplitude = 0.035, width = 0.13) {
  const times = [];
  for (const time of [...arrivals].sort((a, b) => a - b)) if (!times.length || time - times[times.length - 1] > 0.04) times.push(time);
  times.length = Math.min(4, times.length);
  if (!times.length) return null;
  const first = times[0], span = times[times.length - 1] + width - first, steps = Math.max(2, Math.floor(span * 120)), list = [];
  for (let step = 0; step <= steps; step++) {
    const time = first + (span * step) / steps;
    const height = times.reduce((most, arrival) => { const phase = (time - arrival) / width; return phase > 0 && phase < 1 ? Math.max(most, Math.sin(Math.PI * phase)) : most; }, 0);
    list.push({ offset: step / steps, scale: num(1 + amplitude * height) });
  }
  return Object.assign(frames(list, span), { delay: first * 1000 });
}

// The pointer's click (CursorHighlightRenderer.pulseScale) ------------------------------------------------
export const POINTER = { diameter: 36, border: 3, color: "rgb(255 209.1 0)", clickDuration: DURATIONS.pointerClick };
export function pulseScale(progress) {
  const p = clamp01(progress);
  if (p < 0.25) return 1 + 0.18 * (1 - Math.pow(1 - p / 0.25, 3));
  return 1 + 0.18 * (1 - ease.pointerClick((p - 0.25) / 0.75));
}
// Scale keyframes for the ring around the pointer when it clicks: 0.45 s.
export function clickPulse() {
  return frames(Array.from({ length: 46 }, (_, step) => ({ offset: step / 45, scale: num(pulseScale(step / 45)) })), DURATIONS.pointerClick);
}

// Dissolve (ParticleBurstView's shader) --------------------------------------------------------------------
// The picture fades in a sweep while its pixels fly off as particles. `progress` is 0 for the whole picture and 1
// when it has gone; `along` is 0 at the edge that faces the sweep and 1 at the far edge.
export const dissolve = {
  spread: 140,
  progress: (share) => smooth(share),
  local: (progress, along) => clamp01((progress - 0.25 * along) / 0.75),
  // uv in 0...1 with v down; `angle` is the sweep's direction, measured the app's way (y up).
  along: (u, v, angle) => clamp01(0.5 - ((u - 0.5) * Math.cos(angle) + (v - 0.5) * -Math.sin(angle))),
  pictureOpacity: (local) => 1 - smooth(local / 0.18),
  // How much of the middle of the picture still shows: what the toolbar's glass follows.
  middleOpacity: (progress) => 1 - smooth(clamp01((progress - 0.125) / 0.75) / 0.18),
  random(value) {
    let v = value >>> 0;
    v ^= v >>> 16; v = Math.imul(v, 0x7feb352d) >>> 0; v ^= v >>> 15; v = Math.imul(v, 0x846ca68b) >>> 0; v ^= v >>> 16;
    return (v & 0xffffff) / 16777216;
  },
  // ParticleBurstView.grid: columns and rows of particles for a picture of this size.
  grid(width, height, limit) {
    const spacing = Math.max(1.5, Math.sqrt((width * height) / limit)), columns = Math.max(1, Math.min(limit, Math.floor(width / spacing)));
    return [columns, Math.max(1, Math.min(Math.floor(limit / columns), Math.floor(height / spacing)))];
  },
  // One particle at local progress p: how far it is from its pixel (y down), its size and its opacity.
  particle(id, p) {
    const angle = dissolve.random(id + 1) * 6.2831853, dx = Math.cos(angle), dy = Math.sin(angle), speed = 0.25 + 0.75 * dissolve.random(id + 9209);
    const drag = (1 - Math.exp(-3.5 * p)) / (1 - Math.exp(-3.5)), swirl = Math.sin(p * 3.1415927) * 10;
    return {
      x: dx * 140 * speed * drag - dy * swirl, y: dy * 140 * speed * drag + dx * swirl + p * p * 12,
      size: (1.5 + 2 * dissolve.random(id + 7919)) * (1 - 0.65 * p), opacity: smooth(p / 0.18) * (1 - smooth((p - 0.35) / 0.65)),
    };
  },
};
// The picture's part of Dissolve for any element: a mask that fades it away in a sweep (down by default, as in the
// app). The particles need the picture's pixels, which a page can only read from a canvas or an image; draw them
// with dissolve.particle. Exact for a sweep along an axis.
export function dissolveKeyframes({ appearing = false, angle = -Math.PI / 2, steps = 36 } = {}) {
  const turn = (Math.atan2(-Math.cos(angle), -Math.sin(angle)) * 180) / Math.PI, list = [];
  for (let step = 0; step <= steps; step++) {
    const share = smooth(step / steps), progress = appearing ? 1 - share : share, stops = [];
    for (let i = 0; i <= 24; i++) stops.push(`rgb(0 0 0 / ${num(dissolve.pictureOpacity(dissolve.local(progress, i / 24)))}) ${num((i / 24) * 100)}%`);
    const mask = `linear-gradient(${num(turn)}deg, ${stops.join(", ")})`;
    list.push({ offset: step / steps, maskImage: mask, webkitMaskImage: mask });
  }
  return frames(list, visibilityDuration("dissolve", appearing));
}
