// What every page does: the theme switch and its reveal, the header's line once the page scrolls, and headings and
// groups that arrive as they come into view. The homepage adds its demo in home.js.
const root = document.documentElement;
const motion = root.classList.contains("motion");
const THEME_KEY = "annotator.site.theme";
const PAGE_COLOR = { dark: "#0e0e11", light: "#f9f8fa" } as const;
type Theme = keyof typeof PAGE_COLOR;
type ViewTransition = { finished: Promise<void>; skipTransition(): void };
type TransitionDocument = Document & { startViewTransition?: (update: () => void) => ViewTransition };

// Theme ----------------------------------------------------------------------------------------------
// Dark unless the visitor chose light. The choice is kept for every page; the page's head applies it before the first
// paint. Other parts of the page (the homepage's drawings) follow the "site:theme" event, which fires while the new
// theme is being put in place, so they are part of the reveal.
const themeSwitch = document.querySelector<HTMLButtonElement>(".theme-switch");
const themeColor = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
const currentTheme = (): Theme => root.dataset.theme === "light" ? "light" : "dark";
function showTheme(theme: Theme) {
  root.dataset.theme = theme;
  themeSwitch?.setAttribute("aria-checked", String(theme === "dark"));
  if (themeColor) themeColor.content = PAGE_COLOR[theme];
  document.dispatchEvent(new CustomEvent("site:theme", { detail: { theme } }));
}
let themeTurn = 0;
function setTheme(theme: Theme) {
  try { localStorage.setItem(THEME_KEY, theme); } catch { /* storage is off: the choice lasts for this visit */ }
  const turn = ++themeTurn;
  const start = (document as TransitionDocument).startViewTransition?.bind(document);
  if (!motion) { showTheme(theme); return; }
  if (!start || !themeSwitch) {
    // No view transitions here: the colors cross-fade for a moment.
    root.classList.add("theme-fade");
    showTheme(theme);
    setTimeout(() => { if (turn === themeTurn) root.classList.remove("theme-fade"); }, 340);
    return;
  }
  // The new theme opens in a circle from the switch, far enough to reach the farthest corner.
  const r = themeSwitch.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
  root.style.setProperty("--vt-x", `${x}px`);
  root.style.setProperty("--vt-y", `${y}px`);
  root.style.setProperty("--vt-r", `${Math.ceil(Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y)))}px`);
  root.classList.add("theme-reveal");
  const transition = start(() => showTheme(theme));
  const stopPassing = passThrough(transition);
  const done = () => { stopPassing(); if (turn === themeTurn) root.classList.remove("theme-reveal"); };
  transition.finished.then(done, done);
}
// While the reveal runs, the browser shows a picture of the page and sends every press to the page's root, so nothing
// under the pointer can be pressed. A press ends the reveal at once and is handed to what is really there, so a visitor
// can pick a tool or follow a link the moment after they switch.
function passThrough(transition: ViewTransition) {
  let pressed: Element | null = null;
  const down = (e: PointerEvent) => {
    if (e.target !== root || !e.isTrusted) return;
    transition.skipTransition();
    const under = document.elementFromPoint(e.clientX, e.clientY);
    if (!under || under === root) return;
    pressed = under;
    under.dispatchEvent(new PointerEvent("pointerdown", e));
  };
  const click = (e: MouseEvent) => {
    if (e.target !== root || !e.isTrusted || !pressed) return;
    const under = document.elementFromPoint(e.clientX, e.clientY), from = pressed;
    pressed = null;
    if (under && (under === from || from.contains(under) || under.contains(from))) ((under.closest("a, button") || under) as HTMLElement).click();
  };
  addEventListener("pointerdown", down, true);
  addEventListener("click", click, true);
  // The click that belongs to a press arrives after the reveal has ended, so the listeners stay a moment longer.
  return () => setTimeout(() => { removeEventListener("pointerdown", down, true); removeEventListener("click", click, true); }, 700);
}
themeSwitch?.addEventListener("click", () => setTheme(currentTheme() === "dark" ? "light" : "dark"));
themeSwitch?.setAttribute("aria-checked", String(currentTheme() === "dark"));
// Another tab changed the theme: this one follows.
addEventListener("storage", (e) => { if (e.key === THEME_KEY && (e.newValue === "light" || e.newValue === "dark") && e.newValue !== currentTheme()) showTheme(e.newValue); });

// The header's line ------------------------------------------------------------------------------
let scrolled = false;
const markScrolled = () => {
  const now = scrollY > 8;
  if (now !== scrolled) { scrolled = now; root.classList.toggle("is-scrolled", now); }
};
addEventListener("scroll", markScrolled, { passive: true });
markScrolled();

// Arrivals ---------------------------------------------------------------------------------------
// Headings are split into words so each can rise on its own; the text stays the same for a screen reader.
for (const heading of document.querySelectorAll<HTMLElement>("[data-rise]")) {
  let i = 0;
  for (const node of [...heading.childNodes]) {
    if (node.nodeType === Node.TEXT_NODE) {
      const parts = (node.textContent ?? "").split(/(\s+)/).filter(Boolean).map((part) => {
        if (/^\s+$/.test(part)) return document.createTextNode(" ");
        const word = document.createElement("span");
        word.className = "w";
        word.style.setProperty("--i", String(i++));
        word.textContent = part;
        return word;
      });
      node.replaceWith(...parts);
    } else if (node instanceof HTMLElement) { node.classList.add("w"); node.style.setProperty("--i", String(i++)); }
  }
}
for (const group of document.querySelectorAll<HTMLElement>(".depth")) [...group.children].forEach((child, i) => (child as HTMLElement).style.setProperty("--i", String(i)));
root.classList.add("arrivals");
const arrive = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) { entry.target.classList.add("is-in"); arrive.unobserve(entry.target); }
}, { rootMargin: "0px 0px -10% 0px", threshold: 0.15 });
document.querySelectorAll("[data-rise], .depth").forEach((el) => arrive.observe(el));
// Printing shows everything as it ends up.
addEventListener("beforeprint", () => document.querySelectorAll("[data-rise], .depth").forEach((el) => el.classList.add("is-in")));

// A visitor who turns reduced motion on or off gets the page made for it.
matchMedia("(prefers-reduced-motion: reduce)").addEventListener("change", () => location.reload());

export {};
