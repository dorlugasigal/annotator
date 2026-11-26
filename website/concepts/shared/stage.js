// The growing stage: a demo's frame opens up to fill the window once the visitor joins in, and scrolling brings it
// back. The sample windows never move: the frame opens around them, and every drawing keeps its place.
//
// How to wire this into a page
//   import { growingStage } from "/concepts/shared/stage.js";
//   const stage = growingStage({ ink, surface, tries: "[data-try]" });
//   stage.on("join", () => { /* stop the scripted loop; lay a tilted stage flat */ });
// 1. `surface` is the element createInk got: the positioned box the sample windows sit in. It is the small frame.
//    The module never moves or resizes it. It sets, on `root` (the surface by default; pass an ancestor when the
//    toolbar's parent or the background is a sibling of the surface): --stage-top, --stage-right, --stage-bottom,
//    --stage-left (px the frame reaches past the surface; 0 while small, negative where the window is smaller),
//    --stage-grow (0 to 1) and data-stage="small" | "between" | "grown".
// 2. In the HTML, mark what belongs to the frame: data-stage-frame on everything that has the frame's box (the
//    background panel, the toolbar's parent; each `inset: 0` on the surface's box while small), and
//    data-stage-corner="top left" (any two sides) on labels that sit in a corner, like the mode chip.
// 3. In the CSS, the page may set --stage-radius (the frame's corners), --stage-scrim (the color behind a grown frame;
//    the page's background by default), --stage-z (1100), and restyle .stage-shrink, the "Make the demo smaller"
//    button the module adds (place it with --stage-shrink-top and --stage-shrink-right, not top and right).
// Options: margin (16), phoneMargin (8, under 600 px), motion (() => false: no animation; Reduce Motion by default),
//   isFlat (() => true once a tilted stage lies flat; "the surface has no transform" by default),
//   invite ("Draw here": a pill with these words that follows a mouse or pen over the stage, and rests near the
//   stage's middle on touch screens, until the visitor joins in. It takes no input and is aria-hidden: the page's
//   own heading says the words. Restyle it as .stage-invite, or set --stage-invite-bg and --stage-invite-fg).
// Events: join (at once, when the visitor joins in), grow, shrink, change ({ progress, state }, every step).
// Calls: stage.join() (for a control the module cannot see), stage.shrink(), stage.progress, stage.state, stage.refresh(),
//   stage.room ({ top, right, bottom, left }: px the frame reaches past the surface now; keep draggable windows inside
//   the surface's box widened by it, and keep them in again on "change" as the frame shrinks).
// Mistakes to avoid: an ancestor of `root` with perspective, transform, opacity, filter or z-index keeps the stage
//   under the header (take it off while data-stage is not "small"); `overflow: hidden` on the surface cuts the frame;
//   sizing the surface from --stage-* moves the windows; calling join() from the scripted loop grows it for nobody.
//   The module warns in the console when it sees the first two.
import { ease, toolbarMorph, DURATIONS, clickPulse } from "./app-motion.js";

const STYLE = `
[data-stage]:not([data-stage="small"]) { z-index: var(--stage-z, 1100); }
[data-stage-frame][data-stage-frame] { position: absolute; inset: calc(var(--stage-top, 0px) * -1) calc(var(--stage-right, 0px) * -1) calc(var(--stage-bottom, 0px) * -1) calc(var(--stage-left, 0px) * -1); }
[data-stage-corner~="top"][data-stage-corner~="left"] { translate: calc(var(--stage-left, 0px) * -1) calc(var(--stage-top, 0px) * -1); }
[data-stage-corner~="top"][data-stage-corner~="right"] { translate: var(--stage-right, 0px) calc(var(--stage-top, 0px) * -1); }
[data-stage-corner~="bottom"][data-stage-corner~="left"] { translate: calc(var(--stage-left, 0px) * -1) var(--stage-bottom, 0px); }
[data-stage-corner~="bottom"][data-stage-corner~="right"] { translate: var(--stage-right, 0px) var(--stage-bottom, 0px); }
.stage-scrim { position: fixed; inset: 0; z-index: calc(var(--stage-z, 1100) - 1); background: var(--stage-scrim, Canvas); opacity: 0; visibility: hidden; pointer-events: none; }
.stage-scrim[data-on] { visibility: visible; pointer-events: auto; }
.stage-shrink { position: absolute; top: calc(var(--stage-shrink-top, 10px) - var(--stage-top, 0px)); right: calc(var(--stage-shrink-right, 10px) - var(--stage-right, 0px)); z-index: 120; display: grid; place-items: center; width: 36px; height: 36px; padding: 0; border: 0; border-radius: 50%; background: color-mix(in srgb, CanvasText 12%, Canvas); color: CanvasText; cursor: pointer; }
.stage-shrink svg { fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
[data-stage="small"] .stage-shrink { display: none; }
.stage-invite { position: absolute; left: 0; top: 0; z-index: calc(var(--stage-z, 1100) - 200); margin: 0; padding: 8px 14px; border-radius: 999px; background: var(--stage-invite-bg, CanvasText); color: var(--stage-invite-fg, Canvas); font-family: inherit; font-size: 14px; font-weight: 600; line-height: 1; letter-spacing: 0; white-space: nowrap; box-shadow: 0 8px 20px -10px rgb(0 0 0 / .55); opacity: 0; pointer-events: none; -webkit-user-select: none; user-select: none; transition: opacity 120ms cubic-bezier(0.42, 0, 1, 1); }
.stage-invite[data-on] { opacity: 1; transition: opacity 160ms cubic-bezier(0, 0, 0.58, 1); }
`;
// Lucide's "minimize-2" (ISC), the icon family the demo toolbar already uses.
const ICON = '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7"/></svg>';
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const frame = () => new Promise((done) => requestAnimationFrame(done));

export function growingStage({ ink, surface, root = surface, tries = null, margin = 16, phoneMargin = 8, motion = null, isFlat = null, invite = "" } = {}) {
  if (!(surface instanceof Element)) throw new Error("growingStage needs the stage's surface element.");
  const warn = (words, ...more) => console.warn(`Annotator demo, growing stage: ${words}`, ...more);
  if (!document.querySelector("style[data-growing-stage]")) {
    const style = document.createElement("style");
    style.dataset.growingStage = "";
    style.textContent = STYLE;
    // First in the head, so a page's own rules of the same weight (a restyled .stage-shrink) win.
    document.head.prepend(style);
  }
  const scrim = document.body.appendChild(Object.assign(document.createElement("div"), { className: "stage-scrim" }));
  scrim.setAttribute("aria-hidden", "true");
  const button = Object.assign(document.createElement("button"), { type: "button", className: "stage-shrink", innerHTML: ICON });
  button.setAttribute("aria-label", "Make the demo smaller");
  surface.prepend(button);

  const listeners = {}, emit = (name, ...details) => (listeners[name] || []).forEach((fn) => fn(...details));
  const moves = () => (motion ? Boolean(motion()) : !matchMedia("(prefers-reduced-motion: reduce)").matches);
  const flat = () => {
    if (isFlat) return Boolean(isFlat());
    const transform = getComputedStyle(surface).transform;
    return transform === "none" || transform === "matrix(1, 0, 0, 1, 0, 0)";
  };
  const view = () => ({ width: document.documentElement.clientWidth, height: window.innerHeight });
  // 0 is the small frame, 1 the frame that fills the window. `anchor` is where the page was scrolled when it grew.
  let room = { top: 0, right: 0, bottom: 0, left: 0 }, progress = 0, anchor = null, tween = null, ticking = false, wants = false, settling = false, lastScroll = -1e9, lastOut = null, checked = false, padding = null;
  const pressed = new Set(), joining = new Set();

  // How much of the grown frame is left after scrolling away from where it grew: all of it at first, none after
  // 40% of the window's height. Without motion the first scroll switches it back.
  function scrollShare() {
    if (anchor === null) return 0;
    const away = Math.abs(window.scrollY - anchor);
    return moves() ? 1 - clamp(away / (view().height * 0.4), 0, 1) : away > 4 ? 0 : 1;
  }
  // Publishes the frame for the current progress. The surface is measured every time, so the frame follows the
  // page while it scrolls and the window while it is resized.
  function apply() {
    const s = surface.getBoundingClientRect(), v = view(), m = v.width < 600 ? phoneMargin : margin, g = progress;
    const out = { top: g * (s.top - m), right: g * (v.width - m - s.right), bottom: g * (v.height - m - s.bottom), left: g * (s.left - m) };
    const state = g <= 0 ? "small" : g >= 1 ? "grown" : "between";
    room = out;
    // A toolbar the visitor dragged keeps its place on the screen while its parent's corner moves.
    const parent = ink?.toolbar?.offsetParent, before = parent?.getBoundingClientRect();
    for (const side of ["top", "right", "bottom", "left"]) root.style.setProperty(`--stage-${side}`, `${out[side].toFixed(2)}px`);
    root.style.setProperty("--stage-grow", g.toFixed(4));
    if (root.dataset.stage !== state) {
      // A page keeps room for its sticky header when the browser scrolls something into view. The grown frame covers
      // the header, so that room goes for as long: without this, focusing a control near the frame's top would scroll the page.
      const html = document.documentElement;
      if (state !== "small" && (root.dataset.stage || "small") === "small") { padding = html.style.scrollPadding; html.style.scrollPadding = "0px"; }
      else if (state === "small" && padding !== null) { html.style.scrollPadding = padding; padding = null; }
      root.dataset.stage = state;
    }
    const radius = getComputedStyle(root).getPropertyValue("--stage-radius").trim() || "0px";
    // Where the window is smaller than the surface, the frame crops what is in the surface: windows, toolbar, drawings.
    const crops = g > 0 && (out.top < -0.5 || out.right < -0.5 || out.bottom < -0.5 || out.left < -0.5);
    surface.style.clipPath = crops ? `inset(${(-out.top).toFixed(2)}px ${(-out.right).toFixed(2)}px ${(-out.bottom).toFixed(2)}px ${(-out.left).toFixed(2)}px round ${radius})` : "";
    ink?.setRoom?.(g > 0 ? { ...out, radius } : null);
    if (before && ink?.shiftToolbar) {
      const after = parent.getBoundingClientRect();
      if (after.left !== before.left || after.top !== before.top) ink.shiftToolbar(before.left - after.left, before.top - after.top);
    }
    scrim.style.opacity = g.toFixed(4);
    scrim.toggleAttribute("data-on", g > 0);
    if (!lastOut || state !== lastOut.state || g !== lastOut.g) emit("change", { progress: g, state });
    lastOut = { state, g };
  }
  function tick(now) {
    ticking = false;
    if (tween) {
      const t = tween.duration > 0 ? clamp((now - tween.start) / tween.duration, 0, 1) : 1, to = tween.to === "scroll" ? scrollShare() : 0;
      progress = tween.from + (to - tween.from) * ease.standard(t);
      if (t >= 1) { progress = to; tween = null; }
    } else if (anchor !== null && !pressed.size) progress = scrollShare();
    if (progress <= 0.0005 && !tween) { progress = 0; anchor = null; }
    apply();
    if (tween) request();
  }
  function request() { if (!ticking) { ticking = true; requestAnimationFrame(tick); } }
  const startTween = (to, seconds) => { tween = { from: progress, to, start: performance.now(), duration: moves() ? seconds * 1000 : 0 }; request(); };

  // How far the page has to scroll so the stage is in place. A stage that is wholly in the window stays where it is;
  // one that is partly out comes to the middle; one taller than the window moves just enough to cover it.
  function placement() {
    const s = surface.getBoundingClientRect(), v = view(), m = v.width < 600 ? phoneMargin : margin, top = m, bottom = v.height - m;
    if (s.height > bottom - top) return s.top > top ? s.top - top : s.bottom < bottom ? s.bottom - bottom : 0;
    return s.top >= top && s.bottom <= bottom ? 0 : s.top - (v.height - s.height) / 2;
  }
  // The color behind a grown frame, when the page names none: the page's own background.
  const pageColor = () => [document.body, document.documentElement].map((el) => getComputedStyle(el).backgroundColor).find((c) => c && c !== "transparent" && c !== "rgba(0, 0, 0, 0)");
  function tint() {
    if (getComputedStyle(scrim).getPropertyValue("--stage-scrim").trim()) { scrim.style.background = ""; return; }
    const color = pageColor();
    if (color) scrim.style.background = color;
  }
  // "Draw here": a small pill that asks the visitor in, until they join. With a mouse or a pen it follows the pointer
  // over the stage, just below and to the right of it. On a touch screen, and until a pointer has been over the
  // stage, it rests near the stage's middle and swells now and then with the app's click pulse. It takes no input,
  // lives outside the stage (so it changes nothing in it) and steps aside while the scripted pointer draws under it.
  function inviting(words) {
    const pill = document.body.appendChild(Object.assign(document.createElement("div"), { className: "stage-invite", textContent: words }));
    pill.setAttribute("aria-hidden", "true");
    let at = null, to = null, over = false, shown = false, covered = 0, following = 0, pulsing = 0, resting = 0, back = 0, pause = 0;
    // The page's own colors, unless it names the pill's: its text color behind, its background for the words.
    const colors = () => {
      const named = getComputedStyle(pill);
      pill.style.background = named.getPropertyValue("--stage-invite-bg").trim() ? "" : getComputedStyle(document.body).color;
      pill.style.color = named.getPropertyValue("--stage-invite-fg").trim() ? "" : pageColor() || "";
    };
    // Page coordinates, kept inside the window's width so the pill never makes the page scroll sideways.
    const put = (p) => {
      const width = pill.offsetWidth, left = window.scrollX + 8, right = window.scrollX + view().width - width - 8;
      at = [clamp(p[0], left, Math.max(left, right)), p[1]];
      pill.style.translate = `${at[0].toFixed(1)}px ${at[1].toFixed(1)}px`;
    };
    const middle = () => {
      const r = surface.getBoundingClientRect();
      return [r.left + r.width / 2 - pill.offsetWidth / 2 + window.scrollX, r.top + Math.min(r.height / 2, window.innerHeight * 0.45) - pill.offsetHeight / 2 + window.scrollY];
    };
    const show = () => {
      const on = !covered && (over || !back) && !document.hidden;
      if (on !== shown) { shown = on; pill.toggleAttribute("data-on", on); }
    };
    // With motion the pill eases after the pointer; without, it is put near the pointer once the pointer rests.
    const follow = () => {
      following = 0;
      if (!over || !to || !at) return;
      const next = [at[0] + (to[0] - at[0]) * 0.32, at[1] + (to[1] - at[1]) * 0.32];
      put(Math.hypot(to[0] - next[0], to[1] - next[1]) < 0.4 ? to : next);
      if (at[0] !== to[0] || at[1] !== to[1]) following = requestAnimationFrame(follow);
    };
    const rest = () => { if (!over) { put(middle()); show(); } };
    const move = (e) => {
      if (e.pointerType === "touch") return;
      const inside = e.target instanceof Element && (surface.contains(e.target) || root.contains(e.target)) && !ink?.toolbar?.contains(e.target) && !button.contains(e.target);
      if (inside) {
        to = [e.pageX + 14, e.pageY + 18];
        clearTimeout(back); back = 0;
        if (!over) { over = true; put(to); show(); }
        else if (moves()) { if (!following) following = requestAnimationFrame(follow); }
        else { clearTimeout(pause); pause = setTimeout(() => { if (over) put(to); }, 220); }
      } else if (over) leave();
    };
    // Off the stage the pill goes, and a moment later it waits near the middle again.
    const leave = () => {
      if (!over) return;
      over = false;
      cancelAnimationFrame(following); following = 0;
      back = setTimeout(() => { back = 0; rest(); }, 1400);
      show();
    };
    // The scripted pointer, when it is on: the pill stays out from under it, and a little longer.
    const watch = () => {
      const ghost = document.querySelector(".ink-reach .ink-ghost.is-on") || ink?.layer?.querySelector(".ink-ghost.is-on");
      if (ghost && (shown || covered)) {
        const g = ghost.getBoundingClientRect(), p = pill.getBoundingClientRect(), pad = 28;
        if (g.left < p.right + pad && g.right > p.left - pad && g.top < p.bottom + pad && g.bottom > p.top - pad) covered = 4;
        else if (covered) covered--;
      } else if (covered) covered--;
      show();
    };
    const pulse = () => { if (shown && !over && moves()) { const keys = clickPulse(); pill.animate(keys, { duration: keys.duration }); } };
    addEventListener("pointermove", move, { passive: true });
    document.documentElement.addEventListener("pointerleave", leave);
    addEventListener("blur", leave);
    addEventListener("scroll", rest, { passive: true });
    addEventListener("resize", rest);
    document.addEventListener("visibilitychange", show);
    const sized = window.ResizeObserver ? new ResizeObserver(rest) : null;
    sized?.observe(surface);
    resting = setInterval(watch, 140);
    pulsing = setInterval(pulse, 3200);
    colors();
    rest();
    // The stage may still be arriving (an entrance animation, fonts): the resting place is looked up again.
    const late = [setTimeout(rest, 700), setTimeout(rest, 2000)];
    return {
      pill, colors,
      end() {
        late.forEach(clearTimeout);
        removeEventListener("pointermove", move);
        document.documentElement.removeEventListener("pointerleave", leave);
        removeEventListener("blur", leave);
        removeEventListener("scroll", rest);
        removeEventListener("resize", rest);
        document.removeEventListener("visibilitychange", show);
        sized?.disconnect();
        clearInterval(resting); clearInterval(pulsing); clearTimeout(back); clearTimeout(pause); cancelAnimationFrame(following);
        pill.remove();
      },
    };
  }
  let cue = typeof invite === "string" && invite.trim() ? inviting(invite.trim()) : null;
  // Waits until the page has stopped scrolling and the surface has stopped moving, at most a second and a half.
  async function still() {
    const until = performance.now() + 1500;
    let last = null, same = 0;
    await frame(); await frame();
    while (performance.now() < until) {
      const r = surface.getBoundingClientRect(), key = `${r.left.toFixed(1)},${r.top.toFixed(1)},${r.width.toFixed(1)},${r.height.toFixed(1)}`;
      same = key === last ? same + 1 : 0;
      last = key;
      if (same >= 2 && performance.now() - lastScroll > 110 && flat()) return true;
      await frame();
    }
    return flat();
  }
  // An entrance animation that has run its course still keeps its element a layer of its own in the browser, and a
  // stage inside it cannot rise above the header. Such an animation has nothing left to do, so it is switched off.
  function rest() {
    for (let el = root.parentElement; el && el !== document.documentElement; el = el.parentElement) {
      if (getComputedStyle(el).animationName === "none") continue;
      const own = el.getAnimations().filter((a) => a.effect?.target === el);
      if (own.every((a) => (a.playState === "finished" || a.playState === "idle") && ["none", "backwards"].includes(a.effect.getComputedTiming().fill))) el.style.animation = "none";
    }
  }
  // The visitor joined in. The frame grows once no pointer is pressed, the page is still and the stage is in place.
  async function settle() {
    if (!wants || settling || pressed.size) return;
    settling = true;
    try {
      if (!(await still())) { wants = false; warn("the surface is still tilted or moving, so the frame stays small. Flatten it in the `join` event.", surface); return; }
      if (pressed.size) return;
      rest();
      const move = placement();
      if (Math.abs(move) > 1) {
        window.scrollBy({ top: move, behavior: moves() ? "smooth" : "instant" });
        await still();
        if (pressed.size) return;
      }
      wants = false;
      anchor = window.scrollY;
      away = 0;
      tint();
      emit("grow");
      startTween("scroll", toolbarMorph.expandDuration);
      if (!checked) { checked = true; setTimeout(check, toolbarMorph.expandDuration * 1000 + 400); }
    } finally {
      settling = false;
      if (wants && !pressed.size) settle();
    }
  }
  function join() {
    // The invitation has been taken up: it goes for good.
    if (cue) { cue.end(); cue = null; }
    if (progress >= 1 && anchor !== null && !tween) return;
    emit("join");
    wants = true;
    settle();
  }
  // The button, or the page: the frame goes back on the app's own curve, and stays small until the next join.
  function shrink({ animate = true } = {}) {
    wants = false;
    if (progress <= 0) return;
    emit("shrink");
    anchor = null;
    if (button.contains(document.activeElement)) button.blur();
    startTween(0, animate ? toolbarMorph.collapseDuration : 0);
  }
  // Looks, once, for the wiring mistakes that are easy to make. Only words in the console.
  function check() {
    if (progress < 1) { checked = false; return; }
    for (const el of root.querySelectorAll("[data-stage-frame]")) {
      const r = el.getBoundingClientRect(), v = view(), m = v.width < 600 ? phoneMargin : margin;
      if (Math.abs(r.left - m) > 1.5 || Math.abs(r.top - m) > 1.5 || Math.abs(r.right - (v.width - m)) > 1.5 || Math.abs(r.bottom - (v.height - m)) > 1.5) warn("this [data-stage-frame] element does not have the frame's box. It must be `inset: 0` on the surface's own box while small.", el);
    }
    const v = view(), hit = document.elementFromPoint(v.width / 2, (v.width < 600 ? phoneMargin : margin) + 3);
    if (hit && !root.contains(hit) && hit !== scrim) warn("something sits above the grown stage. Raise --stage-z, or take the stage out of a stacking context that is under it.", hit);
    if (hit === scrim) {
      // The usual reason: an ancestor that the browser stacks as one layer.
      const layers = [];
      for (let el = root.parentElement; el && el !== document.documentElement; el = el.parentElement) {
        const c = getComputedStyle(el);
        if (c.transform !== "none" || c.perspective !== "none" || c.opacity !== "1" || c.filter !== "none" || c.isolation === "isolate" || c.animationName !== "none" || c.willChange !== "auto" || (c.position !== "static" && c.zIndex !== "auto") || /paint|strict|content/.test(c.contain)) layers.push(el);
      }
      warn("the grown stage is under its own backdrop, so an ancestor keeps it in a layer of its own (a transform, perspective, opacity, filter, isolation, z-index, will-change or a running animation). Remove that while the stage is grown.", ...layers);
    }
    if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) warn("the page scrolls sideways while the stage is grown.");
  }
  if (getComputedStyle(root).position === "static") warn("`root` must be positioned (position: relative), so it can sit above the page while it is grown.", root);

  // What counts as joining in: a real press on the stage or its toolbar that ends with the pointer lifting (a touch
  // that turns into a scroll is cancelled, and does not count), a real key of the demo's, a "Try it here" control.
  const onStage = (e) => { const path = e.composedPath(); return (path.includes(root) || path.includes(surface) || (ink?.toolbar && path.includes(ink.toolbar))) && !path.includes(button) && !path.some((el) => el instanceof Element && el.hasAttribute("data-stage-ignore")); };
  addEventListener("pointerdown", (e) => {
    if (!e.isTrusted) return;
    pressed.add(e.pointerId);
    if (e.button === 0 && onStage(e)) joining.add(e.pointerId);
  }, true);
  const lift = (e) => {
    if (!e.isTrusted) return;
    pressed.delete(e.pointerId);
    const joins = joining.delete(e.pointerId) && e.type === "pointerup";
    if (joins) join(); else if (!pressed.size) settle();
    // What scrolled by while a pointer was down is caught up now, on the app's short resize.
    if (!pressed.size && anchor !== null && !tween && Math.abs(progress - scrollShare()) > 0.001) startTween("scroll", DURATIONS.toolbarResize);
  };
  addEventListener("pointerup", lift, true);
  addEventListener("pointercancel", lift, true);
  // Esc goes back to using apps: it is a way out, so it never opens the frame.
  ink?.on?.("shortcut", (name, how) => { if (!how?.scripted && name !== "use") join(); });
  const controls = typeof tries === "string" ? [...document.querySelectorAll(tries)] : tries ? [...tries] : [];
  for (const control of controls) control.addEventListener("click", (e) => { if (e.isTrusted) join(); });
  button.addEventListener("click", () => shrink());
  // A window resize reflows the page, which moves the scroll position without the visitor scrolling (and before the
  // resize event is heard). While that goes on the frame keeps the share it had after the visitor's last own scroll,
  // and a fully grown stage is put back in place once the window rests.
  let resizing = 0, held = 0, placing = 0, away = 0;
  const hold = () => { anchor = window.scrollY - held; };
  function replace() {
    if (anchor === null || held !== 0 || pressed.size || tween) return;
    const move = placement();
    if (Math.abs(move) > 1) window.scrollBy({ top: move, behavior: "instant" });
    anchor = window.scrollY;
    away = 0;
    request();
  }
  addEventListener("scroll", () => {
    lastScroll = performance.now();
    if (anchor !== null) { if (lastScroll < resizing) hold(); else away = window.scrollY - anchor; }
    if (anchor !== null || progress > 0) request();
  }, { passive: true });
  addEventListener("resize", () => {
    if (anchor !== null) {
      const now = performance.now();
      if (now > resizing) held = away;
      resizing = now + 300;
      hold();
      clearTimeout(placing);
      placing = setTimeout(replace, 340);
    }
    if (progress > 0) request();
  });
  // A pointer that lifted outside the window is not pressed any more.
  addEventListener("blur", () => { pressed.clear(); joining.clear(); });
  // The keyboard leaving the stage for the page behind it makes the frame small, so the page is there to see.
  document.addEventListener("focusin", (e) => { if (progress > 0 && e.target instanceof Element && !root.contains(e.target) && !surface.contains(e.target) && !ink?.toolbar?.contains(e.target)) shrink(); });
  // The page's colors can change while the frame is grown (a theme switch).
  new MutationObserver(() => { if (progress > 0) tint(); cue?.colors(); }).observe(document.documentElement, { attributes: true, attributeFilter: ["class", "data-theme", "style"] });
  apply();

  return {
    on(name, fn) { (listeners[name] ||= []).push(fn); return this; },
    join, shrink, button, scrim,
    // The "Draw here" pill while it is still asking, for a page that wants to look at it; null once the visitor joined.
    get invite() { return cue ? cue.pill : null; },
    refresh: () => { if (progress > 0) request(); else apply(); },
    // How far the frame reaches past the surface on each side right now, in px (0 while small): for a page that keeps
    // its windows inside the frame.
    get room() { return { ...room }; },
    get progress() { return progress; },
    get state() { return progress <= 0 ? "small" : progress >= 1 ? "grown" : "between"; },
  };
}
