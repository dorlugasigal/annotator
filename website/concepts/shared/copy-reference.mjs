// Copies the toolbar pictures and layout data that toolbar-test.html compares against, from
// reference/ux-reference/macos, and the rendered scenes that render-test.html compares against, from
// reference/conformance/render (both generated on a Mac; never edit the copies). The concepts server only serves website/.
// Run: node website/concepts/shared/copy-reference.mjs
import { copyFileSync, mkdirSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const from = new URL("../../../reference/ux-reference/macos/toolbar/", import.meta.url);
const to = new URL("toolbar-reference/", import.meta.url);
const tools = ["select", "rectangle", "rectangle-filled", "diamond", "ellipse", "arrow", "line", "pen", "text", "highlighter", "laser", "eraser", "blur", "pixelate", "counter"];
const states = [
  "expanded-horizontal-tools-only", "expanded-horizontal-properties", "use-apps", ...tools.map((tool) => `full-properties-${tool}`),
  "selection-single", "board-whiteboard", "board-blackboard", "added-buttons", "hidden-buttons", "hidden-buttons-all", "draw-tools-only",
  "minimal-horizontal-tools-only", "minimal-horizontal-properties",
  "collapsed-expanded-horizontal-draw", "collapsed-expanded-horizontal-apps", "collapsed-minimal-horizontal-draw", "fit-narrow",
];
mkdirSync(to, { recursive: true });
for (const state of states) {
  for (const look of ["dark", "light"]) {
    for (const kind of ["png", "json"]) copyFileSync(new URL(`${state}-${look}.${kind}`, from), new URL(`${state}-${look}.${kind}`, to));
  }
}
writeFileSync(new URL("states.json", to), `${JSON.stringify(states, null, 1)}\n`);
// The color picker: solid colors, gradients, and the laser's, which has no gradients.
const pickers = ["color-stroke", "color-gradient", "color-laser"];
mkdirSync(new URL("pickers/", to), { recursive: true });
for (const picker of pickers) {
  for (const look of ["dark", "light"]) {
    for (const kind of ["png", "json"]) copyFileSync(new URL(`../pickers/${picker}-${look}.${kind}`, from), new URL(`pickers/${picker}-${look}.${kind}`, to));
  }
}
console.log(`${states.length} states -> ${fileURLToPath(to)}`);

// The app's rendered scenes with the elements the web demo draws: strokes, shapes, rough shapes, arrows, turned shapes
// and a gradient stroke.
const scenes = ["strokes", "shapes", "rough", "arrowheads", "opacity-rotation", "fills"];
const renderFrom = new URL("../../../reference/conformance/render/", import.meta.url), renderTo = new URL("render-reference/", import.meta.url);
mkdirSync(renderTo, { recursive: true });
for (const scene of scenes) {
  for (const file of [`${scene}.board.json`, `${scene}@1x.png`, `${scene}@2x.png`]) copyFileSync(new URL(file, renderFrom), new URL(file, renderTo));
}
writeFileSync(new URL("scenes.json", renderTo), `${JSON.stringify(scenes)}\n`);
console.log(`${scenes.length} scenes -> ${fileURLToPath(renderTo)}`);
