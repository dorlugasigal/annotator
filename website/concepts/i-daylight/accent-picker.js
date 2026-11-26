// TEMPORARY. A floating picker so the owner can try the page in each of the app's fifteen gradients and choose the
// accent himself. It is not part of the design. To remove it: delete this file and the lines marked "accent picker"
// in main.js; the page then stays in its own accent (Dusk, in style.css).
//   accentPicker({ pages: { dark: "#0e0e11", light: "#f9f8fa" }, change(preset) { … } })
// It sets the accent's custom properties on the page (the names are in style.css, under "The accent's names"),
// repaints the SVG gradient "accent-ink", and calls change(preset) for what styles cannot reach (the demo's ink and
// the live scenes). The choice is kept in localStorage as "annotator.concepts.accent". ?accent=<name> sets one, and
// ?picker=0 hides the panel (a saved or named accent still applies).
import { GRADIENT_PRESETS } from "/concepts/shared/annotation-core.js";

const KEY = "annotator.concepts.accent", OPEN_KEY = "annotator.concepts.accent.open";
const WHITE = [255, 255, 255], BLACK = [0, 0, 0], DARK_TEXT = [20, 20, 26];

// Color arithmetic, in sRGB bytes.
const parse = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const hex = (c) => `#${c.map((v) => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, "0")).join("")}`;
const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
const channel = (v) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
const luminance = (c) => 0.2126 * channel(c[0]) + 0.7152 * channel(c[1]) + 0.0722 * channel(c[2]);
// WCAG contrast of two colors.
const contrast = (a, b) => { const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05); };

// The gradient with words on it. Each stop moves toward black (for white words) or toward white (for dark words)
// only as far as it must for 4.6 to 1 all the way along, and the page takes whichever of the two moves the colors
// least. So Dusk keeps its violet and deepens only its pink, and a pale gradient like Citrus gets dark words.
function wordSafe(stops) {
  const fit = (words, toward) => {
    const out = stops.map((c) => [...c]);
    const worst = () => {
      // The stops and nine places between each pair.
      let low = { ratio: Infinity, at: 0 };
      for (let i = 0; i < out.length - 1; i++) for (let k = 0; k <= 10; k++) {
        const ratio = contrast(mix(out[i], out[i + 1], k / 10), words);
        if (ratio < low.ratio) low = { ratio, at: i + (k > 5 ? 1 : 0), between: k > 0 && k < 10, i };
      }
      return low;
    };
    for (let step = 0; step < 400; step++) {
      const low = worst();
      if (low.ratio >= 4.6) break;
      // Move the nearer stop; a dip in the middle of a pair moves both.
      const move = low.between ? [low.i, low.i + 1] : [low.at];
      for (const i of move) out[i] = mix(out[i], toward, 0.02);
    }
    const moved = out.reduce((sum, c, i) => sum + Math.hypot(c[0] - stops[i][0], c[1] - stops[i][1], c[2] - stops[i][2]), 0);
    return { stops: out, words, moved };
  };
  const white = fit(WHITE, BLACK), dark = fit(DARK_TEXT, WHITE);
  return white.moved <= dark.moved * 1.15 ? white : dark;
}
// One flat color from the gradient's middle that shows on a page: 3.2 to 1 or more, for rings, lines and focus.
function flatOn(color, page) {
  const toward = luminance(page) > 0.5 ? BLACK : WHITE;
  let out = [...color];
  for (let step = 0; step < 100 && contrast(out, page) < 3.2; step++) out = mix(out, toward, 0.03);
  return out;
}
const spread = (colors) => colors.map((c, i) => `${hex(c)} ${Math.round((i / (colors.length - 1)) * 100)}%`).join(", ");

// The custom properties for one of the app's gradients.
function tokens(preset, pages) {
  const stops = preset.gradient.colors.map(parse), middle = stops[Math.floor((stops.length - 1) / 2)], fill = wordSafe(stops);
  return {
    "--accent-stops": spread(stops),
    "--accent-fill-stops": spread(fill.stops),
    "--on-accent": hex(fill.words),
    "--accent-shadow": hex(fill.stops[Math.floor((fill.stops.length - 1) / 2)]),
    "--accent-on-dark": hex(flatOn(middle, parse(pages.dark))),
    "--accent-on-light": hex(flatOn(middle, parse(pages.light))),
  };
}

const STYLE = `
.accent-picker { position: fixed; left: 12px; bottom: 12px; z-index: 1250; display: grid; justify-items: start; gap: 8px; font: 500 12px/1.2 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif; color: var(--text, #111); letter-spacing: 0; }
.accent-picker * { box-sizing: border-box; }
.accent-picker button { font: inherit; color: inherit; cursor: pointer; -webkit-tap-highlight-color: transparent; }
.ap-panel { width: min(336px, calc(100vw - 24px)); padding: 10px; border: 1.5px dashed var(--faint, #888); border-radius: 14px; background: var(--surface-1, #fff); box-shadow: 0 18px 40px -18px #000000b3; }
.accent-picker:not([data-open]) .ap-panel { display: none; }
.ap-head { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; margin: 0 2px 8px; }
.ap-head b { font-weight: 650; font-size: 13px; }
.ap-head span { color: var(--muted, #666); }
.ap-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 6px; }
.ap-grid button { display: flex; align-items: center; gap: 7px; min-width: 0; height: 32px; padding: 0 8px 0 5px; border: 1px solid var(--line-strong, #8884); border-radius: 9px; background: none; text-align: left; white-space: nowrap; }
.ap-grid button:hover { background: var(--wash, #8882); }
.ap-grid button[aria-pressed="true"] { border-color: var(--text, #111); box-shadow: inset 0 0 0 1px var(--text, #111); font-weight: 650; }
.ap-grid i, .ap-toggle i { flex: none; display: block; border-radius: 50%; box-shadow: inset 0 0 0 1px #00000026; }
.ap-grid i { width: 20px; height: 20px; }
.ap-grid span { overflow: hidden; text-overflow: ellipsis; }
.ap-toggle { display: inline-flex; align-items: center; gap: 8px; height: 40px; padding: 0 12px 0 6px; border: 1.5px dashed var(--faint, #888); border-radius: 999px; background: var(--surface-1, #fff); box-shadow: 0 10px 24px -12px #000000b3; }
.ap-toggle i { width: 26px; height: 26px; }
.ap-toggle small { font-size: 10.5px; color: var(--muted, #666); }
.accent-picker button:focus-visible { outline: 2px solid var(--text, #111); outline-offset: 2px; border-radius: 9px; }
.ap-toggle:focus-visible { border-radius: 999px; }
/* On a phone the grown demo's toolbar is at the bottom of the screen: the picker steps aside. */
@media (max-width: 759px) { :root:has([data-stage]:not([data-stage="small"])) .accent-picker { display: none; } }
@media print { .accent-picker { display: none; } }
`;

export function accentPicker({ pages, change = () => {}, fallback = "dusk" } = {}) {
  const root = document.documentElement, query = new URLSearchParams(location.search);
  const find = (name) => GRADIENT_PRESETS.find((preset) => preset.id === String(name || "").toLowerCase());
  const read = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
  const write = (key, value) => { try { localStorage.setItem(key, value); } catch { /* storage is off: the choice lasts for this visit */ } };

  let current = null;
  const buttons = new Map();
  let swatch = null, label = null;
  function apply(preset, save = true) {
    current = preset;
    for (const [name, value] of Object.entries(tokens(preset, pages))) root.style.setProperty(name, value);
    // The SVG gradient that drawn shapes are stroked and filled with.
    const svg = document.getElementById("accent-ink"), colors = preset.gradient.colors;
    if (svg) svg.replaceChildren(...colors.map((color, i) => {
      const stop = document.createElementNS("http://www.w3.org/2000/svg", "stop");
      stop.setAttribute("offset", String(i / (colors.length - 1)));
      stop.setAttribute("stop-color", color);
      return stop;
    }));
    root.dataset.accent = preset.id;
    if (save) write(KEY, preset.id);
    for (const [id, button] of buttons) button.setAttribute("aria-pressed", String(id === preset.id));
    if (swatch) swatch.style.background = `linear-gradient(25deg, ${spread(colors.map(parse))})`;
    if (label) label.textContent = preset.label;
    change(preset);
  }

  const named = find(query.get("accent")), first = named || find(read(KEY)) || find(fallback);
  // The page's own accent needs nothing set: its styles are already that.
  if (first.id !== fallback || named) apply(first, Boolean(named) || first.id !== fallback); else { current = first; root.dataset.accent = first.id; }

  if (query.get("picker") === "0") return { get name() { return current.id; }, set: (name) => { const preset = find(name); if (preset) apply(preset); } };

  const style = document.createElement("style");
  style.dataset.accentPicker = "";
  style.textContent = STYLE;
  document.head.append(style);

  const panel = document.createElement("aside");
  panel.className = "accent-picker";
  panel.setAttribute("aria-label", "Accent color. A temporary tool for choosing, not part of the page.");
  panel.innerHTML = `
    <div class="ap-panel" id="accent-picker-panel">
      <p class="ap-head"><b>Accent</b><span>temporary picker</span></p>
      <div class="ap-grid" role="group" aria-label="The app's gradients"></div>
    </div>
    <button type="button" class="ap-toggle" aria-controls="accent-picker-panel"><i></i><span></span><small>temporary</small></button>`;
  const grid = panel.querySelector(".ap-grid"), toggle = panel.querySelector(".ap-toggle");
  swatch = toggle.querySelector("i");
  label = toggle.querySelector("span");
  for (const preset of GRADIENT_PRESETS) {
    const button = document.createElement("button");
    button.type = "button";
    button.innerHTML = `<i style="background: linear-gradient(25deg, ${spread(preset.gradient.colors.map(parse))})"></i><span>${preset.label}</span>`;
    button.addEventListener("click", () => apply(preset));
    buttons.set(preset.id, button);
    grid.append(button);
  }
  const open = (on, remember = true) => {
    panel.toggleAttribute("data-open", on);
    toggle.setAttribute("aria-expanded", String(on));
    toggle.setAttribute("aria-label", `Accent color: ${current.label}. ${on ? "Close" : "Open"} the temporary picker.`);
    if (remember) write(OPEN_KEY, on ? "1" : "0");
  };
  toggle.addEventListener("click", () => open(!panel.hasAttribute("data-open")));
  panel.addEventListener("keydown", (e) => {
    if (e.key !== "Escape" || !panel.hasAttribute("data-open")) return;
    e.stopPropagation();
    open(false);
    toggle.focus();
  });
  document.body.append(panel);
  // The marks of the current choice, without applying it again.
  for (const [id, button] of buttons) button.setAttribute("aria-pressed", String(id === current.id));
  swatch.style.background = `linear-gradient(25deg, ${spread(current.gradient.colors.map(parse))})`;
  label.textContent = current.label;
  // It starts as one swatch button, so it never covers the demo, and remembers being opened.
  open(read(OPEN_KEY) === "1", false);
  const refresh = () => toggle.setAttribute("aria-label", `Accent color: ${current.label}. ${panel.hasAttribute("data-open") ? "Close" : "Open"} the temporary picker.`);
  grid.addEventListener("click", refresh);

  return { get name() { return current.id; }, set: (name) => { const preset = find(name); if (preset) apply(preset); } };
}
