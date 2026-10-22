// Script-driven animations read the same motion tokens as the stylesheet (motion.css).
const reduced = matchMedia("(prefers-reduced-motion: reduce)");

export function motionAllowed(): boolean {
  return !reduced.matches && typeof Element.prototype.animate === "function";
}

function read(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** A duration token in milliseconds, or an easing token as a string. */
export function token(name: string): number;
export function token(name: string, kind: "string"): string;
export function token(name: string, kind: "ms" | "string" = "ms"): number | string {
  const value = read(name);
  if (kind === "string") return value || "ease-out";
  const ms = value.endsWith("ms") ? parseFloat(value) : value.endsWith("s") ? parseFloat(value) * 1000 : NaN;
  // Without the stylesheet, fall back to an instant change rather than guessing a duration.
  return Number.isFinite(ms) ? ms : 0;
}
