// The page's light: a small canvas that paints aurora curtains in the app's Aurora gradient.
// Each ribbon is a curtain: a soft lower hem that waves from side to side, and rays that rise from it and fade.
// It is painted a few dozen pixels wide and stretched by the browser, which is what makes it soft and keeps it cheap.
// A field only moves while it is on screen and the tab is in front; without motion it paints one still frame.

const COLORS = [[33, 184, 120], [64, 140, 242], [209, 84, 224]];

// x and w: where a ribbon is across the field and how wide. hem: how far down its lower edge hangs. sway: how much
// the hem waves. reach: how far the rays rise. travel: how far it moves across as the page scrolls (setShift).
// A field fades out between the two heights in `fade`, and in from its top edge over `rise` if it has one. soft: how
// quickly the light ends under a hem.
// hem, sway and fade are parts of the field's floor: the height where the light must be gone, because body text
// starts there. The floor is the bottom of the field unless the page says otherwise (setFloor).
const FIELDS = {
  // Behind the hero: curtains hanging from the top of the page, down to the headline and no further.
  sky: {
    soft: .06, fade: [.8, 1.12], ribbons: [
      { color: 0, x: .14, w: .26, hem: .68, sway: .085, reach: .30, power: .62, travel: .50, phase: .0 },
      { color: 1, x: .50, w: .30, hem: .50, sway: .090, reach: .26, power: .56, travel: .04, phase: 2.1 },
      { color: 2, x: .86, w: .26, hem: .72, sway: .085, reach: .32, power: .62, travel: -.54, phase: 4.4 },
    ],
  },
  // Behind the carousel: light that stands behind the cards and stops above the words under them.
  arc: {
    soft: .05, rise: .2, fade: [.86, 1], ribbons: [
      { color: 2, x: .16, w: .26, hem: .78, sway: .040, reach: .34, power: .66, travel: .46, phase: 1.3 },
      { color: 1, x: .52, w: .34, hem: .72, sway: .050, reach: .38, power: .64, travel: -.04, phase: 3.7 },
      { color: 0, x: .86, w: .26, hem: .80, sway: .040, reach: .34, power: .66, travel: -.48, phase: 5.2 },
    ],
  },
  // Inside the coffee panel: one low arch behind the cup.
  cup: {
    soft: .1, fade: [.66, 1], ribbons: [
      { color: 0, x: .30, w: .22, hem: .66, sway: .045, reach: .56, power: .60, travel: .10, phase: .6 },
      { color: 1, x: .50, w: .24, hem: .52, sway: .055, reach: .60, power: .60, travel: 0, phase: 2.9 },
      { color: 2, x: .70, w: .22, hem: .66, sway: .045, reach: .56, power: .60, travel: -.10, phase: 4.8 },
    ],
  },
};
const TAU = Math.PI * 2;
const clamp = (n, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, n));
const smooth = (t) => t * t * (3 - 2 * t);

export function createAurora(canvas, { kind = "sky", animate = true, theme = "dark" } = {}) {
  const field = FIELDS[kind] || FIELDS.sky, ribbons = field.ribbons, count = ribbons.length;
  const context = canvas.getContext("2d");
  let width = 0, height = 0, image = null, hems = null, powers = null, reaches = null;
  let light = theme === "light", shift = 0, floor = 1, seen = false, frame = 0, last = 0, clock = 6.4, drawn = -1;

  function size() {
    const w = clamp(Math.round(canvas.clientWidth / 8), 56, 220), h = clamp(Math.round(canvas.clientHeight / 8), 36, 150);
    if (!canvas.clientWidth || (w === width && h === height)) return;
    width = w; height = h;
    canvas.width = w; canvas.height = h;
    image = context.createImageData(w, h);
    hems = new Float32Array(count * w); powers = new Float32Array(count * w); reaches = new Float32Array(count * w);
    drawn = -1;
  }

  function paint(t) {
    if (!image) return;
    const data = image.data, strength = light ? .92 : 1, fadeFrom = field.fade[0] * floor, fadeTo = field.fade[1] * floor;
    // A narrow field (a phone) shows fewer, wider curtains instead of the same ones squeezed.
    const zoom = clamp(canvas.clientWidth / 1100, .5, 1);
    for (let r = 0; r < count; r += 1) {
      const ribbon = ribbons[r], p = ribbon.phase, centre = ribbon.x + ribbon.travel * shift + .035 * Math.sin(t * .11 + p);
      const breath = .82 + .18 * Math.sin(t * .23 + p * 1.7);
      for (let x = 0; x < width; x += 1) {
        const u = x / (width - 1), wide = (.5 + (u - .5) * zoom), away = (u - centre) / (ribbon.w / zoom);
        const hem = ribbon.hem + ribbon.sway * Math.sin(TAU * (wide * 1.25 + t * .017) + p) + ribbon.sway * .55 * Math.sin(TAU * (wide * 2.9 - t * .026) + p * 2.3);
        // Rays: slow bands of brighter and longer light that slide along the curtain.
        const ray = .5 + .5 * Math.sin(TAU * (wide * 7.3 + t * .031) + p) * Math.sin(TAU * (wide * 3.1 - t * .019) + p * .7);
        const fine = .5 + .5 * Math.sin(TAU * (wide * 19 - t * .047) + p * 3.1);
        const i = r * width + x;
        hems[i] = hem * floor * height;
        powers[i] = ribbon.power * strength * breath * Math.exp(-away * away) * (.62 + .26 * ray + .12 * fine);
        reaches[i] = 1 / (ribbon.reach * height * (.55 + .7 * ray));
      }
    }
    const under = 1 / ((field.soft || .06) * height);
    for (let y = 0, at = 0; y < height; y += 1) {
      const v = y / (height - 1), rowFade = (1 - smooth(clamp((v - fadeFrom) / (fadeTo - fadeFrom)))) * (field.rise ? smooth(clamp(v / field.rise)) : 1);
      for (let x = 0; x < width; x += 1, at += 4) {
        let red = 0, green = 0, blue = 0, sum = 0, through = 1;
        for (let r = 0; r < count; r += 1) {
          const i = r * width + x, above = hems[i] - y;
          const a = powers[i] * (above >= 0 ? Math.exp(-above * reaches[i]) : Math.exp(above * under));
          if (a < .004) continue;
          const c = COLORS[ribbons[r].color];
          red += c[0] * a; green += c[1] * a; blue += c[2] * a; sum += a; through *= 1 - a;
        }
        if (!sum) { data[at + 3] = 0; continue; }
        data[at] = red / sum; data[at + 1] = green / sum; data[at + 2] = blue / sum;
        data[at + 3] = (1 - through) * rowFade * 255;
      }
    }
    context.putImageData(image, 0, 0);
  }

  function still() { size(); paint(clock); }
  function tick(now) {
    frame = 0;
    if (!seen || document.hidden) return;
    frame = requestAnimationFrame(tick);
    // About thirty pictures a second is plenty for light this slow.
    if (now - last < 30) return;
    clock += Math.min(.1, (now - (last || now)) / 1000);
    last = now;
    paint(clock);
  }
  function run() {
    if (!animate) return;
    if (seen && !document.hidden) { if (!frame) { last = 0; frame = requestAnimationFrame(tick); } }
    else if (frame) { cancelAnimationFrame(frame); frame = 0; }
  }

  size();
  paint(clock);
  if (window.IntersectionObserver) new IntersectionObserver(([entry]) => { seen = entry.isIntersecting; run(); }).observe(canvas);
  else { seen = true; run(); }
  document.addEventListener("visibilitychange", run);
  if (window.ResizeObserver) new ResizeObserver(still).observe(canvas); else addEventListener("resize", still);

  return {
    setTheme(name) { light = name === "light"; if (!frame) still(); },
    // 0 to 1 of the field's height: where the light has to end.
    setFloor(value) { const next = clamp(value, .1, 1); if (Math.abs(next - floor) < .004) return; floor = next; if (!frame) still(); },
    // 0 to 1: the ribbons trade places across the field.
    setShift(value) { if (Math.abs(value - drawn) < .002) return; shift = value; drawn = value; if (!frame) still(); },
  };
}
