import { installEditableDemo } from "./editable-demo";
import { ToolSequence, arrive, trace } from "./tool-sequence";

type DemoMode = "draw" | "focus" | "cover" | "zoom";
type CoverMode = "solid" | "blur" | "pixelate" | "distort";

function playgroundRoot(): HTMLElement {
  const element = document.querySelector<HTMLElement>("[data-playground]");
  if (!element) throw new Error("The annotation example is missing its root.");
  return element;
}
const root = playgroundRoot();
const within = <T extends Element>(selector: string): T => {
  const element = root.querySelector<T>(selector);
  if (!element) throw new Error(`The annotation example is missing ${selector}.`);
  return element;
};
const scene = within<HTMLElement>("[data-demo-scene]");
const controls = within<HTMLElement>("[data-demo-controls]");
const title = within<HTMLElement>("[data-demo-title]");
const description = within<HTMLElement>("[data-demo-description]");
const note = within<HTMLElement>("[data-scene-note]");
const motionButton = within<HTMLButtonElement>("[data-depth-toggle]");
const motionLabel = within<HTMLElement>("[data-depth-label]");
const motionNote = within<HTMLElement>("[data-depth-note]");
const zoomInput = within<HTMLInputElement>("[data-zoom-input]");
const zoomOutput = within<HTMLOutputElement>("[data-zoom-output]");
const focusInput = within<HTMLInputElement>("[data-focus-input]");
const focusOutput = within<HTMLOutputElement>("[data-focus-output]");
const coverWarning = within<HTMLElement>("[data-cover-warning]");
const outline = within<SVGPathElement>("[data-morph-outline]");
const replayReveal = within<HTMLButtonElement>("[data-replay-reveal]");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const buttons = [...root.querySelectorAll<HTMLButtonElement>("[data-demo-tool]")];
const copy: Record<DemoMode, [string, string, string]> = {
  draw: ["Point to the changed date.", "Try moving the frame around Friday. In the Mac app, you can also draw your own shapes, arrows, text and ink, then edit them.", "Move the frame to explain the change."],
  focus: ["Point out a detail.", "The spotlight has a soft edge. Move it around this example with the slider or your pointer.", "Keep the detail in view."],
  cover: ["Choose what to cover.", "The Mac app uses one Cover tool with four modes. Change the mode of a selected region without placing another object.", "Change the mode of a selected cover."],
  zoom: ["Read the small print.", "Try the zoom slider. In the Mac app, Screen Recording allows screen zoom; Accessibility adds Option-scroll over other apps.", "Adjust with Option-scroll."],
};
let mode: DemoMode = "draw";
let motion = false;
let motionPreference: boolean | null = null;
let pointerFrame = 0;
let inView = false;
let entrancePlayed = false;
const sequence = new ToolSequence(root);

function stopMotion() {
  sequence.stop();
}
function stopPointerFrame() {
  if (pointerFrame) cancelAnimationFrame(pointerFrame);
  pointerFrame = 0;
}
function playMotion(entrance = false) {
  stopMotion();
  if (!motion || document.hidden || !inView) return;
  const arrow = within<SVGPathElement>("[data-pointing-arrow]");
  const underline = within<SVGPathElement>("[data-demo-underline]");
  const cues = [];
  if (entrance) {
    cues.push(arrive(within("[data-demo-dock-surface]"), 0, 280, 24));
    buttons.forEach((button, index) => {
      cues.push(arrive(within(`[data-demo-tool="${button.dataset.demoTool}"] .tool-button-content`), 170 + index * 65, 210, 8));
      const icon = button.querySelector<SVGPathElement>("svg path");
      if (icon) cues.push(trace(icon, 230 + index * 65, 270));
    });
    cues.push(arrive(within("[data-demo-reset] .tool-button-content"), 450, 180, 6));
  } else {
    const selected = within<SVGPathElement>(`[data-demo-tool="${mode}"] svg path`);
    cues.push(trace(selected, 0, 200));
  }
  if (mode === "draw") {
    const start = entrance ? 640 : 90;
    cues.push(trace(outline, start, 440), trace(underline, start + 340, 250), trace(arrow, start + 520, 350));
  } else if (mode === "cover") {
    cues.push({ element: within(".cover-patch"), start: 0, duration: 240,
      keyframes: [{ clipPath: "inset(0 100% 0 0)" }, { clipPath: "inset(0 0% 0 0)" }] });
  } else if (mode === "focus") {
    cues.push({ element: within(".focus-mask"), start: 0, duration: 220,
      keyframes: [{ opacity: 0 }, { opacity: 1 }] });
  }
  sequence.play(cues);
}
function setMotion(enabled: boolean) {
  motion = enabled && !reducedMotion.matches;
  scene.dataset.depth = motion ? "on" : "off";
  motionButton.setAttribute("aria-pressed", String(motion));
  motionLabel.textContent = motion ? "Motion on" : "Motion off";
  motionButton.disabled = reducedMotion.matches;
  replayReveal.disabled = !motion;
  document.dispatchEvent(new CustomEvent("annotator-motion-change", { detail: { enabled: motion } }));
  motionNote.textContent = reducedMotion.matches ? "Motion is off because Reduce Motion is enabled." : "Use any tool during the reveal. Replay any time.";
  if (!motion) stopMotion();
}
function setMode(next: DemoMode, entrance = false) {
  mode = next;
  scene.dataset.mode = next;
  for (const button of buttons) button.setAttribute("aria-pressed", String(button.dataset.demoTool === next));
  for (const options of root.querySelectorAll<HTMLElement>("[data-options]")) options.hidden = options.dataset.options !== next;
  [title.textContent, description.textContent, note.textContent] = copy[next];
  scene.setAttribute("aria-label", `${copy[next][0]} Annotation example. ${copy[next][1]} Browser illustration, not the Mac app.`);
  stopPointerFrame();
  editing.modeChanged(next === "draw");
  paintPixelation();
  playMotion(entrance);
}
function paintPixelation() {
  if (mode !== "cover" || scene.dataset.cover !== "pixelate") return;
  const canvas = within<HTMLCanvasElement>("[data-pixelated-cover]");
  const patch = canvas.parentElement!;
  const width = Math.round(patch.clientWidth), height = Math.round(patch.clientHeight);
  if (!width || !height) return;
  const source = document.createElement("canvas");
  source.width = Math.min(2048, width); source.height = Math.min(256, height);
  const context = source.getContext("2d"), pixels = canvas.getContext("2d");
  if (!context || !pixels) {
    scene.dataset.cover = "solid";
    within<HTMLInputElement>('input[name="demo-cover"][value="solid"]').checked = true;
    coverWarning.textContent = "Pixelation could not load. Solid is covering the sample instead.";
    console.error("The cover example could not create its pixelation canvas.");
    return;
  }
  const text = within<HTMLElement>(".token-wrap code");
  const style = getComputedStyle(text);
  context.fillStyle = "#072732"; context.fillRect(0, 0, source.width, source.height);
  context.fillStyle = style.color; context.font = style.font;
  context.textAlign = "center"; context.textBaseline = "middle";
  context.fillText(text.textContent ?? "", source.width / 2, source.height / 2);
  canvas.width = Math.max(1, Math.ceil(width / 9));
  canvas.height = Math.max(1, Math.ceil(height / 9));
  pixels.drawImage(source, 0, 0, canvas.width, canvas.height);
}
function setCover(value: string) {
  if (!["solid", "blur", "pixelate", "distort"].includes(value)) throw new Error(`Unknown cover mode: ${value}`);
  scene.dataset.cover = value as CoverMode;
  coverWarning.textContent = value === "solid"
    ? "Solid conceals the sample value. Always verify the receiver’s view."
    : "Visual effect, not irreversible redaction. Use sanitized content for sensitive information.";
  paintPixelation();
}
function updateZoom() {
  stopMotion();
  const value = Number(zoomInput.value);
  if (!Number.isFinite(value) || value < 1 || value > 3) throw new Error("Invalid demo magnification.");
  scene.style.setProperty("--demo-zoom", String(value));
  zoomOutput.value = `${value.toFixed(2).replace(/0$/, "")}×`;
}
function updateFocus() {
  stopMotion();
  scene.style.setProperty("--focus-x", `${focusInput.value}%`);
  scene.style.setProperty("--focus-y", "53%");
  focusOutput.value = `${focusInput.value}%`;
}
const editing = installEditableDemo(root, stopMotion);
replayReveal.addEventListener("click", () => setMode("draw", true));
root.addEventListener("pointerdown", stopMotion, { capture: true });
root.addEventListener("keydown", stopMotion, { capture: true });

for (const button of buttons) button.addEventListener("click", () => {
  const value = button.dataset.demoTool;
  if (value !== "draw" && value !== "focus" && value !== "cover" && value !== "zoom") throw new Error(`Unknown tool: ${value}`);
  setMode(value);
});
for (const radio of root.querySelectorAll<HTMLInputElement>('input[name="demo-cover"]')) radio.addEventListener("change", () => setCover(radio.value));
motionButton.addEventListener("click", () => {
  motionPreference = !motion;
  setMotion(motionPreference);
  if (motion) playMotion();
});
zoomInput.addEventListener("input", updateZoom);
focusInput.addEventListener("input", updateFocus);
within<HTMLButtonElement>("[data-demo-reset]").addEventListener("click", () => {
  zoomInput.value = "1.5";
  focusInput.value = "60";
  within<HTMLInputElement>('input[name="demo-cover"][value="solid"]').checked = true;
  editing.reset();
  updateZoom(); updateFocus(); setCover("solid"); setMode("draw");
});
scene.addEventListener("pointermove", (event) => {
  if (event.pointerType === "touch" || mode !== "focus") return;
  stopPointerFrame();
  pointerFrame = requestAnimationFrame(() => {
    pointerFrame = 0;
    const bounds = scene.getBoundingClientRect();
    const x = Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width));
    const y = Math.min(1, Math.max(0, (event.clientY - bounds.top) / bounds.height));
    const position = Math.round(15 + x * 70);
    focusInput.value = String(position);
    focusOutput.value = `${position}%`;
    scene.style.setProperty("--focus-x", `${position}%`);
    scene.style.setProperty("--focus-y", `${25 + y * 50}%`);
  });
});
scene.addEventListener("pointerleave", stopPointerFrame);
const observation = new IntersectionObserver(entries => {
  inView = entries.some(entry => entry.isIntersecting);
  if (!inView) { stopPointerFrame(); stopMotion(); }
  else if (!entrancePlayed) { entrancePlayed = true; playMotion(true); }
}, { threshold: .1 });
const observeStage = () => observation.observe(within(".demo-stage"));
const pixelSize = new ResizeObserver(paintPixelation);
const observePixels = () => pixelSize.observe(within(".cover-patch"));
document.addEventListener("visibilitychange", () => { if (document.hidden) { stopPointerFrame(); stopMotion(); } });
reducedMotion.addEventListener("change", () => setMotion(motionPreference ?? true));
window.addEventListener("pagehide", () => { stopPointerFrame(); stopMotion(); observation.disconnect(); pixelSize.disconnect(); });
window.addEventListener("pageshow", event => { if (event.persisted) { observeStage(); observePixels(); } });
updateZoom(); updateFocus(); setCover("solid"); setMotion(true); setMode("draw");
controls.hidden = false;
motionButton.hidden = false;
replayReveal.hidden = false;
within<HTMLElement>("[data-static-note]").hidden = true;
root.dataset.enhanced = "true";
observeStage();
observePixels();
