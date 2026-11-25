// The stage: one view that stays put while the page scrolls through three moments.
// Everything the visitor reads is HTML and is laid out here; the glass is drawn over it (glass.js), or with CSS
// when there is no WebGL. Both read the same numbers from poses().
import { paintCopy, fontsReady } from "./copy.js";
import { APP_ICONS } from "/concepts/shared/app-icons.js";

const $ = (selector, root = document) => root.querySelector(selector);
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (a, b, x) => { const t = clamp((x - a) / (b - a)); return t * t * (3 - 2 * t); };
const SPAN = 2.9, SLAB_ASPECT = 11.2, PILL_ASPECT = 2.5, POWER = 1.12;
const MAGNIFY = 1 / (1 - (1 - 1 / 1.5) * POWER);

export async function startStage({ reduced, no3d }) {
  const header = $("#top"), stage = $("#stage"), view = $("#view"), layers = $("#layers");
  const heroCopy = $("#hero-copy"), h1 = $("#hero-title"), line1 = $("#line-1"), period = $("#period"), tinyCursor = $("#tiny-cursor"), reelEl = $("#reel"), video = $("#reel-video");
  const sceneLens = $("#scene-lens"), sceneBar = $("#scene-bar"), canvas = $("#glass"), flatEl = $("#flat");
  const grab = $("#bar-grab"), grabNote = $("#grab-note"), toggle = $("#bar-toggle");
  const params = new URLSearchParams(location.search);
  const hero = { id: "hero", el: heroCopy, twin: ".hero-copy", tx: 0, ty: 0, o: 1 };
  // The small pointer wiggles on its own inside the hero text, so it has its own copy.
  const wiggler = { id: "cursor", el: tinyCursor, twin: ".tiny-cursor", parent: hero, tx: 0, ty: 0, o: 1 };
  const capLens = { id: "lens", el: sceneLens, twin: ".scene:nth-of-type(2)", tx: 0, ty: 0, o: 0 }, capBar = { id: "bar", el: sceneBar, twin: ".scene:nth-of-type(3)", tx: 0, ty: 0, o: 0 };
  const groups = [hero, wiggler, capLens, capBar];
  const L = { vw: 1, vh: 1, top: 68, unit: 800, lensR: 100, slabW: 600, slabH: 54, ringR: 40, arrow: 100 };
  const pointer = { x: 0, y: 0, on: false };
  const lens = { x: 0, y: 0, vx: 0, vy: 0, placed: false };
  const bar = { dx: 0, dy: 0, vx: 0, held: null, pill: 0, pillTo: 0, tilt: 0 };
  let glass = null, flat = null, clock = 0, portrait = null, shown = true, raf = 0, last = 0, dirty = true, slow = 0, frames = 0;

  await fontsReady();

  // Layout -------------------------------------------------------------------------------------------
  const scratch = document.createElement("canvas").getContext("2d");
  function dotOf(el) {
    // The middle of the full stop's ink, from where the browser put the character.
    const range = document.createRange();
    range.selectNodeContents(el);
    const r = range.getClientRects()[0], s = getComputedStyle(el), o = view.getBoundingClientRect();
    scratch.font = `${s.fontStyle} ${s.fontWeight} ${s.fontSize} ${s.fontFamily}`;
    const m = scratch.measureText("."), share = m.fontBoundingBoxAscent / (m.fontBoundingBoxAscent + m.fontBoundingBoxDescent);
    return {
      x: r.left - o.left + (m.actualBoundingBoxRight - m.actualBoundingBoxLeft) / 2,
      y: r.top - o.top + r.height * share - (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 2,
      size: m.actualBoundingBoxAscent + m.actualBoundingBoxDescent,
    };
  }
  function measure() {
    const vw = view.clientWidth, vh = view.clientHeight, o = view.getBoundingClientRect();
    const pad = parseFloat(getComputedStyle($(".hero")).paddingLeft), top = header.offsetHeight;
    Object.assign(L, { vw, vh, top, pad, unit: Math.min(vh, 900) });
    stage.style.setProperty("--scroll", `${Math.round(L.unit * SPAN)}px`);
    const wide = vw / vh >= 0.85;
    if (portrait !== !wide) {
      portrait = !wide;
      const name = `/public/demo/annotator-reel${portrait ? "-phone" : ""}`;
      video.poster = `${name}.jpg`;
      video.dataset.src = `${name}.mp4`;
      if (video.getAttribute("src")) { video.src = video.dataset.src; video.load(); }
      glass?.setPoster(video.poster);
    }
    L.lensR = clamp(vw * 0.1, 76, 150);
    L.slabW = Math.min(clamp(vw * 0.6, 300, 860), vw - 28);
    L.slabH = L.slabW / SLAB_ASPECT;
    L.ringR = clamp(vw * 0.042, 24, 60);
    L.arrow = clamp(vw * 0.1, 62, 148);

    const shift = (g) => ({ x: g.tx + (g.parent?.tx || 0), y: g.ty + (g.parent?.ty || 0) });
    for (const g of groups) {
      const r = g.el.getBoundingClientRect(), s = shift(g);
      g.box = { x: r.left - o.left - s.x, y: r.top - o.top - s.y, w: r.width, h: r.height };
    }
    const hr = h1.getBoundingClientRect(), lr = line1.getBoundingClientRect();
    L.h1 = { x: hr.left - o.left - hero.tx, y: hr.top - o.top - hero.ty, w: hr.width, h: hr.height };
    const words = document.createRange();
    words.selectNodeContents(line1);
    const first = words.getBoundingClientRect();
    L.line1 = { right: first.right - o.left - hero.tx, y: lr.top - o.top - hero.ty + lr.height / 2 };
    const dot = dotOf(period);
    L.dot = { x: dot.x - hero.tx, y: dot.y - hero.ty, size: dot.size };
    L.grid = clamp(Math.round(vw / 36), 28, 40);
    view.style.setProperty("--grid", `${L.grid}px`);
    glass?.setGrid(pad, top, L.grid, "#15161a", 0.06);

    // The footage: as large as the view allows under its line of text, with room for the toolbar at its foot.
    const aspect = portrait ? 1080 / 1350 : 16 / 9, capH = Math.max(sceneLens.offsetHeight, sceneBar.offsetHeight);
    const room = L.slabH * (reduced ? 1.5 : 0.85) + (vh < 720 ? 22 : 34), gap = vh < 720 ? 14 : 24;
    const free = vh - top - 12 - capH - gap - room;
    const w = Math.min(vw - pad * 2, 1240, free * aspect), h = w / aspect;
    const lift = Math.max(0, free - h) * 0.42, capTop = top + 12 + lift;
    view.style.setProperty("--cap-top", `${Math.round(capTop)}px`);
    L.reel = { x: (vw - w) / 2, y: capTop + capH + gap, w, h, r: clamp(w * 0.022, 12, 26) };
    reelEl.style.setProperty("--reel-w", `${w}px`); reelEl.style.setProperty("--reel-h", `${h}px`); reelEl.style.setProperty("--reel-r", `${L.reel.r}px`);
    for (const g of [capLens, capBar]) g.box.y = capTop;

    // In the hero the slab rests low, and the top edge of the footage waits behind it.
    const heroBottom = hero.box.y + hero.box.h;
    // On a tall view it sits just under the text instead, so the footage fills what would be an empty half.
    const low = vh - L.slabH / 2 - Math.max(30, vh * 0.055), under = heroBottom + clamp(vh * 0.075, 34, 78) + L.slabH / 2;
    L.slab0 = Math.min(low, under);
    L.small = vw < 700;
    // Where the lens waits: beside the headline on a wide view, over the waiting footage on a narrow one.
    L.rest = L.small
      ? { x: vw - L.lensR - 12, y: Math.min(L.slab0 + L.lensR * 1.15, vh - L.lensR * 0.7) }
      : { x: Math.min(Math.max(L.line1.right + L.lensR * 0.95, vw - L.lensR - 14), vw - L.lensR - 8), y: Math.max(L.line1.y - L.lensR * 0.3, top + L.lensR * 0.7) };
    L.reel0 = L.slab0 - L.slabH * 0.08;
    L.slab2 = L.reel.y + L.reel.h + L.slabH * (reduced ? 0.95 : 0.3);
    dirty = true;
  }

  // Poses: where everything is for a scroll position and a moment in time ------------------------------
  function poses(dt) {
    const scrolled = clamp(-stage.getBoundingClientRect().top, 0, L.unit * SPAN);
    let t = scrolled / L.unit;
    if (reduced) t = t < 0.45 ? 0 : t < 1.95 ? 1.3 : SPAN;
    const time = reduced ? 0 : clock;
    const rise = 1 - Math.pow(1 - clamp(t / 0.96), 1.7), front = smooth(1.62, 2.3, t);
    const wH = 1 - smooth(0.2, 0.78, t), wB = smooth(1.75, 2.25, t), wL = 1 - wH - wB;
    const mix = (a, b, c) => a * wH + b * wL + c * wB;
    hero.o = 1 - smooth(0.1, 0.55, t); hero.ty = -clamp(t) * L.unit * 0.16;
    // The small pointer does what a pointer does on a call: it wiggles, vaguely.
    wiggler.tx = 13 * Math.sin(time * 5.3) + 7 * Math.sin(time * 8.9 + 1); wiggler.ty = 8 * Math.cos(time * 4.1) + 4 * Math.sin(time * 9.7);
    const inLens = smooth(0.6, 0.95, t), outLens = smooth(1.55, 1.86, t);
    capLens.o = inLens * (1 - outLens); capLens.ty = (1 - inLens) * 26 - outLens * 22;
    capBar.o = smooth(1.9, 2.26, t); capBar.ty = (1 - capBar.o) * 26;

    const scale = 1 - 0.13 * front, R = L.reel;
    const ry = lerp(L.reel0, R.y, rise) - R.h * 0.035 * front;
    const reel = { x: R.x + (R.w * (1 - scale)) / 2, y: ry + (R.h * (1 - scale)) / 2, w: R.w * scale, h: R.h * scale, r: R.r * scale, o: 1, dy: ry - R.y, scale };

    // Toolbar slab
    if (wB < 0.4 && !bar.held) { bar.dx *= 0.86; bar.dy *= 0.86; bar.pillTo = 0; }
    bar.pill += (bar.pillTo - bar.pill) * (reduced ? 1 : clamp(dt * 9));
    bar.tilt += (clamp(bar.vx * 0.0006, -0.22, 0.22) - bar.tilt) * clamp(dt * 8);
    bar.vx *= 0.8;
    const grow = 1 + 0.3 * front, sh = Math.min(L.slabH * grow, (L.vw - 20) / SLAB_ASPECT), full = sh * SLAB_ASPECT;
    const pill = bar.pill * bar.pill * (3 - 2 * bar.pill);
    const slab = {
      x: L.vw / 2 + bar.dx, y: lerp(lerp(L.slab0, L.slab2, rise), R.y + R.h * 0.6 - R.h * 0.035, front) + bar.dy + Math.sin(time * 0.8) * 3,
      w: lerp(full, sh * PILL_ASPECT, pill), h: sh, z: 46 + 70 * front, pill,
      rx: -0.16 - 0.1 * front + 0.035 * Math.sin(time * 0.53), ry: 0.05 * Math.sin(time * 0.37 + 1) + bar.tilt, rz: -0.012 + 0.006 * Math.sin(time * 0.29),
    };

    // Lens: follows the pointer on a spring, or drifts on its own
    const h = L.h1, r = L.lensR;
    // It rests beside the end of the first line, half on the paper, and wanders over the headline from there.
    const out = (1 - Math.cos(time * 0.17)) / 2, down = L.small ? out : (1 - Math.cos(time * 0.11)) / 2;
    const idle = {
      x: mix(lerp(L.rest.x, h.x + h.w * 0.3, out), reel.x + reel.w * (0.5 + 0.24 * Math.sin(time * 0.23)), slab.x + slab.w * 0.3),
      y: mix(lerp(L.rest.y, h.y + h.h * (L.small ? 0.5 : 0.7), down) + hero.ty, reel.y + reel.h * (0.5 + 0.16 * Math.sin(time * 0.31 + 2)), slab.y - slab.h * 0.05),
    };
    const follow = pointer.on && !bar.held && !reduced;
    const aim = follow ? { x: pointer.x, y: Math.max(pointer.y, L.top + r * 0.45) } : idle;
    if (!lens.placed || reduced) { lens.x = aim.x; lens.y = aim.y; lens.vx = lens.vy = 0; lens.placed = true; }
    else {
      // Two half steps keep the spring steady when a frame is late.
      const stiff = follow ? 150 : 26, damp = follow ? 17 : 9, step = dt / 2;
      for (let i = 0; i < 2; i++) {
        lens.vx += ((aim.x - lens.x) * stiff - lens.vx * damp) * step; lens.vy += ((aim.y - lens.y) * stiff - lens.vy * damp) * step;
        lens.x += lens.vx * step; lens.y += lens.vy * step;
      }
    }
    const lensPose = { x: lens.x, y: lens.y, r, rx: clamp(lens.vy * 0.00016, -0.2, 0.2), ry: clamp(lens.vx * 0.00016, -0.2, 0.2), power: POWER };

    // The two marks
    const rr = Math.max(L.ringR, L.dot.size * 1.05);
    const ring = {
      x: mix(L.dot.x, reel.x + reel.w - rr * 0.5, slab.x - full * 0.37), y: mix(L.dot.y + hero.ty, reel.y + rr * 0.6, slab.y - sh * 1.7 - rr),
      r: rr, z: 50, rx: 0.52 * Math.sin(time * 0.41), ry: 0.66 * Math.sin(time * 0.3 + 1.3),
    };
    // In the hero the arrow does the product's job: it points at the small pointer, so you can find it.
    const spot = { x: wiggler.box.x + wiggler.box.w / 2, y: wiggler.box.y + wiggler.box.h / 2 + hero.ty }, a = L.arrow * (wiggler.box.w ? 1 : 1 - wH);
    const from = {
      x: mix(spot.x - L.arrow * 0.78, Math.max(reel.x - a * 0.12, a * 0.5), Math.min(slab.x + full * 0.36, L.vw - a * 0.56)),
      y: mix(spot.y + L.arrow * 0.1, reel.y + reel.h * 0.8, slab.y - sh * 1.5 - a * 0.5),
    };
    const to = { x: mix(spot.x, reel.x + reel.w * 0.3, slab.x + full * 0.18), y: mix(spot.y, reel.y + reel.h * 0.62, slab.y) };
    const arrow = { ...from, size: a, z: 64, rz: Math.atan2(from.y - to.y, to.x - from.x), rx: 0.2 * Math.sin(time * 0.47 + 2), ry: 0.3 * Math.sin(time * 0.35) };
    return { t, front, wB, reel, slab, lens: lensPose, ring, arrow };
  }

  // The page follows the same numbers -----------------------------------------------------------------
  const wrote = new Map();
  const write = (el, key, value) => { const id = `${key}`; let seen = wrote.get(el); if (!seen) wrote.set(el, seen = {}); if (seen[id] !== value) { seen[id] = value; el.style[key] = value; } };
  function apply(p) {
    for (const g of groups) {
      write(g.el, "transform", `translate3d(${g.tx.toFixed(2)}px, ${g.ty.toFixed(2)}px, 0)`);
      if (g.parent) continue;
      write(g.el, "opacity", g.o.toFixed(3));
      g.el.classList.toggle("is-off", g.o < 0.5);
    }
    if (!bar.pillTo && toggle.getAttribute("aria-pressed") === "true") { toggle.setAttribute("aria-pressed", "false"); toggle.textContent = "Shrink it"; repaintSoon(capBar); }
    write(reelEl, "transform", `translate3d(${L.reel.x.toFixed(2)}px, ${(L.reel.y + p.reel.dy).toFixed(2)}px, 0) scale(${p.reel.scale.toFixed(4)})`);
    const live = p.wB > 0.7;
    grab.classList.toggle("is-on", live);
    grabNote.classList.toggle("is-on", live && !bar.moved && L.vw > 700);
    if (live) {
      write(grab, "width", `${p.slab.w.toFixed(1)}px`); write(grab, "height", `${(p.slab.h * 1.1).toFixed(1)}px`);
      write(grab, "transform", `translate3d(${(p.slab.x - p.slab.w / 2).toFixed(1)}px, ${(p.slab.y - p.slab.h * 0.55).toFixed(1)}px, 0)`);
      // The note sits past the right end of the slab, or above it when there is no room there.
      const beside = p.slab.x + p.slab.w / 2 + 132 < L.vw;
      write(grabNote, "transform", beside
        ? `translate3d(${(p.slab.x + p.slab.w / 2 + 6).toFixed(1)}px, ${(p.slab.y - p.slab.h * 0.5 - 40).toFixed(1)}px, 0)`
        : `translate3d(${(Math.min(p.slab.x + p.slab.w / 2, L.vw - 20) - 150).toFixed(1)}px, ${(p.slab.y - p.slab.h * 0.5 - 76).toFixed(1)}px, 0)`);
      grabNote.classList.toggle("is-above", !beside);
    }
    // The footage plays only while some of it can be seen.
    const playing = shown && !document.hidden && p.reel.y < L.vh - 4;
    if (!reduced) {
      if (playing && video.paused) { if (!video.getAttribute("src")) video.src = video.dataset.src; video.play().catch(() => {}); }
      else if (!playing && !video.paused) video.pause();
    }
  }

  // Copies of the page text for the glass to bend --------------------------------------------------------
  function paint(g) {
    if (!glass) return;
    if (!g.box.w || !g.box.h) { g.copy = null; return; }
    const sharp = Math.min(3, Math.max(2, (window.devicePixelRatio || 1) * 1.5));
    g.copy = paintCopy(g.el, sharp, g.copy?.canvas);
    glass.setPlane(g.id, g.copy.canvas);
    dirty = true;
  }
  let repaint = 0;
  const repaintSoon = (g) => { g.stale = true; if (!repaint) repaint = requestAnimationFrame(() => { repaint = 0; for (const each of groups) if (each.stale) { each.stale = false; paint(each); } wake(); }); };
  for (const g of groups) for (const type of ["pointerover", "pointerout", "focusin", "focusout"]) {
    g.el.addEventListener(type, (event) => { if (event.target.closest?.("a, button")) repaintSoon(g); });
  }

  // Without WebGL: a lens that shows a second, larger copy of the page, and CSS glass -------------------
  function buildFlat() {
    flatEl.hidden = false;
    const lensEl = $("#flat-lens"), lensView = $("#flat-lens-view"), barEl = $("#flat-bar"), barShadow = $(".flat-shadow-bar"), ringEl = $("#flat-ring"), arrowEl = $("#flat-arrow");
    const twin = layers.cloneNode(true);
    twin.removeAttribute("id"); twin.setAttribute("inert", "");
    twin.querySelectorAll("[id]").forEach((el) => el.removeAttribute("id"));
    const mirror = document.createElement("canvas"), film = $("video", twin);
    mirror.style.cssText = "width:100%;height:100%"; film.replaceWith(mirror);
    lensView.append(twin);
    const twins = groups.map((g) => $(g.twin, twin)), twinReel = $(".reel", twin), still = new Image();
    const draw = mirror.getContext("2d");
    $(".flat-glyphs").innerHTML = ["cursorarrow", "rectangle", "circle", "arrow.up.right", "pencil", "textformat", "highlighter", "eraser"].map((name) =>
      `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${APP_ICONS[name].map((part) => `<path d="${part.d}"${part.fill ? ' fill="currentColor"' : ""}/>`).join("")}</svg>`).join("");
    return (p) => {
      const { lens: l, slab: s, ring: g, arrow: a } = p, d = l.r * 2;
      write(lensEl, "width", `${d}px`); write(lensEl, "height", `${d}px`);
      write(lensEl, "transform", `translate3d(${(l.x - l.r).toFixed(1)}px, ${(l.y - l.r).toFixed(1)}px, 0)`);
      write(lensView, "width", `${L.vw}px`); write(lensView, "height", `${L.vh}px`);
      write(lensView, "transform", `translate3d(${(l.r - l.x * MAGNIFY).toFixed(1)}px, ${(l.r - l.y * MAGNIFY).toFixed(1)}px, 0) scale(${MAGNIFY.toFixed(3)})`);
      groups.forEach((group, i) => { write(twins[i], "transform", group.el.style.transform); write(twins[i], "opacity", group.el.style.opacity); });
      write(twinReel, "transform", reelEl.style.transform);
      if (mirror.width !== 960) { mirror.width = 960; mirror.height = Math.round(960 * L.reel.h / L.reel.w); }
      if (still.dataset.src !== video.poster) { still.dataset.src = still.src = video.poster; still.onload = () => { dirty = true; wake(); }; }
      const source = video.readyState >= 2 && (video.currentTime > 0 || !video.paused) ? video : still.complete && still.naturalWidth ? still : null;
      if (source) draw.drawImage(source, 0, 0, mirror.width, mirror.height);
      write(barEl, "width", `${s.w.toFixed(1)}px`); write(barEl, "height", `${s.h.toFixed(1)}px`);
      barEl.style.setProperty("--bar-h", `${s.h.toFixed(1)}px`);
      write(barEl, "transform", `translate3d(${(s.x - s.w / 2).toFixed(1)}px, ${(s.y - s.h / 2).toFixed(1)}px, 0) rotate(${(-s.rz).toFixed(4)}rad)`);
      write($(".flat-glyphs", barEl), "opacity", (1 - s.pill).toFixed(2));
      write(barShadow, "width", `${(s.w * 0.92).toFixed(1)}px`); write(barShadow, "height", `${(s.h * 0.7).toFixed(1)}px`);
      write(barShadow, "transform", `translate3d(${(s.x - s.w * 0.46 + 6).toFixed(1)}px, ${(s.y + s.h * 0.1).toFixed(1)}px, 0)`);
      const ringSize = g.r * 2.72, arrowSize = a.size * 1.15;
      write(ringEl, "width", `${ringSize}px`); write(ringEl, "height", `${ringSize}px`);
      write(ringEl, "transform", `translate3d(${(g.x - ringSize / 2).toFixed(1)}px, ${(g.y - ringSize / 2).toFixed(1)}px, 0) scale(${Math.cos(g.ry).toFixed(3)}, ${Math.cos(g.rx).toFixed(3)})`);
      write(arrowEl, "width", `${arrowSize}px`); write(arrowEl, "height", `${arrowSize}px`);
      write(arrowEl, "transform", `translate3d(${(a.x - arrowSize / 2).toFixed(1)}px, ${(a.y - arrowSize / 2).toFixed(1)}px, 0) rotate(${(-a.rz).toFixed(4)}rad) scale(${Math.cos(a.ry).toFixed(3)}, ${Math.cos(a.rx).toFixed(3)})`);
    };
  }

  // Frames ----------------------------------------------------------------------------------------------
  const maxRatio = Number(params.get("ratio")) || 2;
  let ratio = Math.min(window.devicePixelRatio || 1, maxRatio);
  function tick(now) {
    raf = 0;
    const dt = Math.min(0.05, Math.max(0.001, (now - last) / 1000));
    last = now;
    if (!reduced) clock += dt;
    const p = poses(dt);
    apply(p);
    if (glass) {
      glass.frame({
        planes: Object.fromEntries(groups.filter((g) => g.copy).map((g) => [g.id, {
          x: g.box.x + g.copy.x + g.tx + (g.parent?.tx || 0), y: g.box.y + g.copy.y + g.ty + (g.parent?.ty || 0), w: g.copy.w, h: g.copy.h, o: g.o * (g.parent?.o ?? 1),
        }])),
        reel: p.reel, lens: p.lens, slab: p.slab, ring: p.ring, arrow: p.arrow,
      });
      // A slow computer gets a lighter picture instead of a stutter.
      if (!reduced && !params.has("ratio")) {
        frames++; if (dt > 0.03) slow++;
        if (frames >= 50) { if (slow > 30 && ratio > 1) { ratio = Math.max(1, ratio - 0.5); glass.resize(L.vw, L.vh, ratio); } frames = slow = 0; }
      }
    } else flat?.(p);
    dirty = false;
    if (running()) raf = requestAnimationFrame(tick);
  }
  // With reduced motion the picture is still: it is drawn again only when something changed, or while the film runs.
  const running = () => shown && !document.hidden && (!reduced || dirty || !video.paused);
  function wake() { if (!raf && running()) { last = performance.now() - 16; raf = requestAnimationFrame(tick); } }
  const touch = () => { dirty = true; wake(); };

  function build() {
    if (!no3d) {
      return import("./glass.js").then(({ createGlass, webglAvailable }) => {
        if (!webglAvailable()) return false;
        glass = createGlass(canvas, { onLost: () => { glass = null; document.documentElement.classList.remove("has-glass"); flat = buildFlat(); touch(); } });
        const css = getComputedStyle(document.documentElement), color = (name) => css.getPropertyValue(name).trim();
        glass.setBackdrop(color("--paper"), color("--paper-2"), color("--warm"), 0.58);
        glass.setReel(video, video.poster);
        return true;
      }).catch((error) => { console.warn("Annotator: the glass could not start, showing the flat picture.", error); glass = null; return false; });
    }
    return Promise.resolve(false);
  }

  measure();
  const ok = await build();
  if (ok) { glass.resize(L.vw, L.vh, ratio); measure(); groups.forEach(paint); document.documentElement.classList.add("has-glass"); }
  else flat = buildFlat();
  if (reduced) video.controls = true;

  new IntersectionObserver(([entry]) => { shown = entry.isIntersecting; if (shown) touch(); else if (!video.paused) video.pause(); }).observe(view);
  document.addEventListener("visibilitychange", touch);
  addEventListener("scroll", touch, { passive: true });
  let resized = 0;
  new ResizeObserver(() => {
    measure(); glass?.resize(L.vw, L.vh, ratio); touch();
    clearTimeout(resized);
    resized = setTimeout(() => { measure(); groups.forEach(paint); touch(); }, 140);
  }).observe(view);
  for (const type of ["play", "pause", "seeked", "loadeddata"]) video.addEventListener(type, touch);

  view.addEventListener("pointermove", (event) => {
    if (event.pointerType === "touch") return;
    const o = view.getBoundingClientRect();
    pointer.x = event.clientX - o.left; pointer.y = event.clientY - o.top; pointer.on = true;
  }, { passive: true });
  view.addEventListener("pointerleave", () => { pointer.on = false; });
  // Someone tabbing back into the hidden first screen is taken to it.
  heroCopy.addEventListener("focusin", () => { if (hero.o < 0.5) stage.scrollIntoView({ block: "start" }); });
  sceneBar.addEventListener("focusin", () => { if (capBar.o < 0.5) scrollTo({ top: stage.offsetTop + L.unit * 2.5 }); });

  grab.addEventListener("pointerdown", (event) => {
    bar.held = { x: event.clientX - bar.dx, y: event.clientY - bar.dy, px: event.clientX };
    grab.setPointerCapture(event.pointerId); grab.classList.add("is-held");
  });
  grab.addEventListener("pointermove", (event) => {
    if (!bar.held) return;
    const reachX = L.vw / 2 - 30, reachY = L.vh * 0.36;
    bar.dx = clamp(event.clientX - bar.held.x, -reachX, reachX); bar.dy = clamp(event.clientY - bar.held.y, -reachY, reachY);
    bar.vx = (event.clientX - bar.held.px) * 60; bar.held.px = event.clientX; bar.moved = true;
    touch();
  });
  const release = () => { bar.held = null; grab.classList.remove("is-held"); };
  grab.addEventListener("pointerup", release); grab.addEventListener("pointercancel", release);
  toggle.addEventListener("click", () => {
    bar.pillTo = bar.pillTo ? 0 : 1;
    toggle.setAttribute("aria-pressed", String(Boolean(bar.pillTo)));
    toggle.textContent = bar.pillTo ? "Open it again" : "Shrink it";
    repaintSoon(capBar); touch();
  });

  touch();
  if (params.has("debug")) window.__stage = { L, groups, poses, lens, bar, get glass() { return glass; }, pointer };
}
