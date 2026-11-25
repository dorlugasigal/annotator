// The app's moves, on this page's elements. The numbers (curves, durations, keyframes) come from the shared port of
// the app's motion, /concepts/shared/app-motion.js; this file only puts them on elements.
import {
  css, DURATIONS, toolbarMorph, visibilityDuration, gatheredPose, cornerRoute, poseKeyframes, travelFade, travelKeyframes,
  magicFlight, magicTrail, toolbarPoof, dissolve, returnSeed, seedFor, smooth,
} from "/concepts/shared/app-motion.js";

const NS = "http://www.w3.org/2000/svg";
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const svgEl = (name, attrs) => { const el = document.createElementNS(NS, name); for (const key in attrs) el.setAttribute(key, attrs[key]); return el; };
const div = (name, style) => { const el = document.createElement("div"); el.className = name; Object.assign(el.style, style); return el; };

export function play(el, keyframes, options = {}) {
  const animation = el.animate(keyframes, { duration: keyframes.duration, easing: keyframes.easing, fill: "both", ...options });
  animation.finished.catch(() => {});
  return animation;
}

// One move: its animations, the nodes it adds and what it changed. cancel() takes all of it off again, so what was
// moved is back at rest; a hide therefore keeps its group until the thing is shown again.
export function group() {
  const animations = [], nodes = [], undo = [], tasks = [];
  return {
    add(...list) { animations.push(...list); return list[0]; },
    node(node) { nodes.push(node); return node; },
    undo(fn) { undo.push(fn); },
    task(promise, stop) { tasks.push(promise); undo.push(stop); },
    done() { return Promise.all([...animations.map((a) => a.finished), ...tasks]).then(() => true, () => false); },
    speed(rate) { animations.forEach((a) => a.updatePlaybackRate(rate)); },
    cancel() {
      animations.splice(0).forEach((a) => a.cancel());
      nodes.splice(0).forEach((node) => node.remove());
      undo.splice(0).forEach((fn) => fn());
      tasks.length = 0;
    },
  };
}
// A drawing in SVG turns and grows about its own middle, like a picture of itself.
function centered(g, el) {
  if (!(el instanceof SVGElement)) return;
  el.style.transformBox = "fill-box";
  el.style.transformOrigin = "center";
  g.undo(() => { el.style.transformBox = ""; el.style.transformOrigin = ""; });
}
const middle = (box) => [box.x + box.width / 2, box.y + box.height / 2];

// Magic: a drawing flies between its place and `target` on one swoop, and leaves a light trail in `trail` (an svg in
// the same coordinates as `box`).
export function magic(g, el, { box, target, appearing, id, delay = 0, trail }) {
  const hidden = gatheredPose(box, target), seed = appearing ? returnSeed(id) : seedFor(id), rest = [0, 0, 1];
  const flight = appearing ? magicFlight(hidden, rest, seed) : magicFlight(rest, hidden, seed);
  const timing = { duration: visibilityDuration("magic", appearing) * 1000, delay: delay * 1000, easing: flight.easing };
  centered(g, el);
  g.add(play(el, flight, timing), play(el, travelFade(appearing, true), timing));
  if (!trail) return;
  const c = middle(box), light = magicTrail(flight.points.map((p) => [c[0] + p[0], c[1] + p[1]]), seed);
  light.strokes.forEach((stroke, i) => {
    const path = trail.appendChild(g.node(svgEl("path", { d: light.path, pathLength: 1, class: `fx-trail${i === 2 ? " is-core" : ""}`, "stroke-width": stroke.width })));
    g.add(play(path, light.dash, timing), play(path, light.fade(stroke.peak), timing));
  });
  light.sparks.forEach((spark, i) => {
    const dot = trail.appendChild(g.node(svgEl("circle", { r: spark.size / 2, class: `fx-spark${i % 3 === 0 ? " is-gold" : ""}` })));
    g.add(play(dot, spark.keyframes, timing), play(dot, light.fade(spark.peak), timing));
  });
}

// The toolbar's poof: it shrinks to a point (or grows from one) while a glow and twelve sparks show at `center`, a
// point in `host`. `origin` is the same point as a transform-origin for `el`.
export function poof(g, el, { appearing, host, center, origin, delay = 0 }) {
  const effect = toolbarPoof(appearing), timing = { delay: delay * 1000 };
  if (origin) { el.style.transformOrigin = origin; g.undo(() => { el.style.transformOrigin = ""; }); }
  g.add(play(el, effect.image, timing));
  const at = (size) => ({ left: `${center[0] - size / 2}px`, top: `${center[1] - size / 2}px`, width: `${size}px`, height: `${size}px` });
  g.add(play(host.appendChild(g.node(div("fx-glow", at(effect.glow.size)))), effect.glow.keyframes, timing));
  effect.sparks.forEach((spark, i) => g.add(play(host.appendChild(g.node(div(`fx-dot${i % 3 === 0 ? " is-core" : ""}`, at(spark.size)))), spark.keyframes, timing)));
  return effect.duration;
}

// Pop, Drop and Focus. `annotation` is true for something that moves like a drawing, false for something as calm
// as the toolbar.
export function posed(g, el, style, { appearing, annotation = true, id = "", delay = 0 }) {
  centered(g, el);
  const frames = poseKeyframes(style, { appearing, annotation, seed: seedFor(id) });
  g.add(play(el, frames, { delay: delay * 1000 }));
  return frames.duration / 1000;
}

// Corners: a drawing travels to the nearest corner of `area` on a bent path.
export function corners(g, el, { box, area, appearing, id, delay = 0 }) {
  const route = cornerRoute(id, middle(box), area), hidden = gatheredPose(box, route.point), rest = [0, 0, 1];
  const options = { appearing, bendSign: appearing ? -route.bendSign : route.bendSign };
  const frames = appearing ? travelKeyframes("corners", hidden, rest, options) : travelKeyframes("corners", rest, hidden, options);
  centered(g, el);
  g.add(play(el, frames, { delay: delay * 1000 }), play(el, travelFade(appearing), { duration: frames.duration, delay: delay * 1000 }));
}
// With Corners the toolbar slides off its own edge.
export function slide(g, el, { appearing, by, delay = 0 }) {
  const duration = (appearing ? 0.3 : 0.24) * 1000, away = { translate: `0 ${by}px`, opacity: 0 }, home = { translate: "0 0", opacity: 1 };
  g.add(play(el, appearing ? [away, home] : [home, away], { duration, delay: delay * 1000, easing: appearing ? css.enter : css.exit }));
}
export function fade(g, el, { appearing, delay = 0, duration }) {
  const time = duration ?? visibilityDuration("fade", appearing);
  g.add(play(el, appearing ? [{ opacity: 0 }, { opacity: 1 }] : [{ opacity: 1 }, { opacity: 0 }], { duration: time * 1000, delay: delay * 1000, easing: appearing ? css.fade : "linear" }));
}

// Dissolve. The app breaks a picture into a grid of particles while the picture fades in a sweep; each particle
// bursts out, slows like a puff, curls a little and sinks. A page cannot read its own pixels, so the particles take
// `colors` (a list, or a function of a particle's place that gives an index into `palette`).
// `box` is the picture's place in `host`. `sweep` is how much of the time the sweep takes (a quarter in the app).
export function burst(g, host, box, { appearing = false, duration = visibilityDuration("dissolve", appearing), delay = 0, palette, pick, limit = 900, spread = dissolve.spread, sweep = 0.25, along, fixed = false }) {
  const pad = fixed ? 0 : Math.ceil(spread) + 12, ratio = fixed ? 1 : Math.min(2, window.devicePixelRatio || 1);
  const width = box.width + pad * 2, height = box.height + pad * 2;
  const canvas = g.node(document.createElement("canvas"));
  canvas.className = "fx-burst";
  canvas.width = Math.ceil(width * ratio); canvas.height = Math.ceil(height * ratio);
  Object.assign(canvas.style, { left: `${box.x - pad}px`, top: `${box.y - pad}px`, width: `${width}px`, height: `${height}px` });
  host.append(canvas);
  const context = canvas.getContext("2d"), [columns, rows] = dissolve.grid(box.width, box.height, limit), count = columns * rows, scale = spread / dissolve.spread;
  // What never changes for a particle: where it starts, when the sweep reaches it, its direction, speed, size, color.
  const data = new Float32Array(count * 7), order = [];
  for (let id = 0; id < count; id++) {
    const u = ((id % columns) + 0.5) / columns, v = (Math.floor(id / columns) + 0.5) / rows, angle = dissolve.random(id + 1) * 6.2831853;
    data.set([pad + u * box.width, pad + v * box.height, along ? along(u, v) : dissolve.along(u, v, -Math.PI / 2), Math.cos(angle), Math.sin(angle),
      0.25 + 0.75 * dissolve.random(id + 9209), 1.5 + 2 * dissolve.random(id + 7919)], id * 7);
    order.push([pick ? pick(u, v, id) : Math.floor(dissolve.random(id + 4001) * palette.length), id]);
  }
  // Particles are drawn color by color, and within a color by how see-through they are, so the canvas changes
  // its paint a few dozen times a frame instead of thousands.
  order.sort((a, b) => a[0] - b[0]);
  const LEVELS = 6, buckets = Array.from({ length: LEVELS }, () => []);
  function draw(progress) {
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    let at = 0;
    while (at < count) {
      const color = order[at][0];
      for (; at < count && order[at][0] === color; at++) {
        const o = order[at][1] * 7, p = clamp((progress - sweep * data[o + 2]) / (1 - sweep));
        if (p <= 0 || p >= 1) continue;
        const opacity = smooth(p / 0.18) * (1 - smooth((p - 0.35) / 0.65));
        if (opacity < 0.04) continue;
        const drag = (1 - Math.exp(-3.5 * p)) / (1 - Math.exp(-3.5)), swirl = Math.sin(p * Math.PI) * 10, reach = dissolve.spread * data[o + 5] * drag;
        const size = data[o + 6] * (1 - 0.65 * p);
        buckets[Math.min(LEVELS - 1, Math.floor(opacity * LEVELS))].push(
          data[o] + (data[o + 3] * reach - data[o + 4] * swirl) * scale - size / 2, data[o + 1] + (data[o + 4] * reach + data[o + 3] * swirl + p * p * 12) * scale - size / 2, size);
      }
      context.fillStyle = palette[color];
      buckets.forEach((bucket, level) => {
        if (!bucket.length) return;
        context.globalAlpha = (level + 0.5) / LEVELS;
        context.beginPath();
        for (let i = 0; i < bucket.length; i += 3) context.rect(bucket[i], bucket[i + 1], bucket[i + 2], bucket[i + 2]);
        context.fill();
        bucket.length = 0;
      });
    }
  }
  let frame = 0, start = null, stopped = false;
  g.task(new Promise((resolve) => {
    const tick = (now) => {
      if (stopped) { resolve(false); return; }
      start ??= now + delay * 1000;
      const share = clamp((now - start) / (duration * 1000));
      if (now >= start) draw(appearing ? 1 - smooth(share) : smooth(share));
      if (share < 1) frame = requestAnimationFrame(tick); else { context.clearRect(0, 0, width, height); resolve(true); }
    };
    frame = requestAnimationFrame(tick);
  }), () => { stopped = true; cancelAnimationFrame(frame); });
  return canvas;
}
// Dissolve on an element. An HTML element goes behind the same soft sweep the app's picture does (a mask three
// times its height, moved so its edge is where the particles leave from); a drawing in SVG fades the way the
// middle of the picture does.
export function dissolved(g, el, { host, box, appearing, palette, delay = 0, limit, spread, masked = true }) {
  const duration = visibilityDuration("dissolve", appearing), frames = [], html = masked && el instanceof HTMLElement;
  for (let step = 0; step <= 30; step++) {
    const share = step / 30, progress = appearing ? 1 - smooth(share) : smooth(share);
    frames.push(html ? { offset: share, maskPosition: `0% ${clamp(progress * 2) * 100}%`, webkitMaskPosition: `0% ${clamp(progress * 2) * 100}%` } : { offset: share, opacity: dissolve.middleOpacity(progress) });
  }
  if (html) {
    const mask = "linear-gradient(to top, transparent 0 48.7%, #000 66.7%) 0% 0% / 100% 300% no-repeat";
    el.style.webkitMask = mask; el.style.mask = mask;
    g.undo(() => { el.style.webkitMask = ""; el.style.mask = ""; });
  }
  g.add(play(el, frames, { duration: duration * 1000, delay: delay * 1000 }));
  burst(g, host, box, { appearing, duration, delay, palette, limit: limit ?? Math.round(clamp((box.width * box.height) / 110, 200, 5000)), spread });
}

// The toolbar's Shrink: the old face is gone by half way, the new one starts at 35%, and the frame follows the
// standard curve. `shape` lists [element, from, to] keyframes for whatever stands for the frame.
export function morph(g, { outgoing, incoming, expanding, shape = [] }) {
  const duration = (expanding ? toolbarMorph.expandDuration : toolbarMorph.collapseDuration) * 1000;
  g.add(play(outgoing, [{ opacity: 1 }, { opacity: 0 }], { duration: duration * toolbarMorph.outgoingEnd, easing: css.fade }));
  g.add(play(incoming, [{ opacity: 0 }, { opacity: 1 }], { delay: duration * toolbarMorph.incomingStart, duration: duration * (1 - toolbarMorph.incomingStart), easing: css.fade }));
  for (const [el, from, to] of shape) g.add(play(el, [from, to], { duration, easing: css.standard }));
  return duration;
}

// Something arrives the way the toolbar's own things do: a short rise on the enter curve.
export function rise(el, { delay = 0, by = 12, duration = DURATIONS.toolbarShow } = {}) {
  return play(el, [{ opacity: 0, translate: `0 ${by}px` }, { opacity: 1, translate: "0 0" }], { duration: duration * 1000, delay: delay * 1000, easing: css.enter, fill: "backwards" });
}
