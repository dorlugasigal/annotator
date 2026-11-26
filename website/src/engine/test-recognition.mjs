// Checks recognition.js, the web demo's Smart Draw, against the app's vectors in reference/conformance/recognition:
// every stroke must give the same tool (or none), and the same frame, rotation, points and style within 1e-6.
// Run: node website/src/engine/test-recognition.mjs
import { recognize, previewInput } from "./recognition.js";
import { lines, json, element, differs, tally } from "./conformance.mjs";

const results = tally();
for (const vector of lines("recognition/strokes.jsonl")) {
  const stroke = element(vector.stroke), got = recognize(stroke), want = vector.result;
  let problem = null;
  if (!want || !got) problem = (want ? want.tool : null) === (got ? got.tool : null) ? null : `recognized ${got ? got.tool : "nothing"}, the app ${want ? want.tool : "nothing"}`;
  else {
    const expected = element(want);
    problem = got.tool !== expected.tool ? `recognized ${got.tool}, the app ${expected.tool}`
      : differs({ frame: got.frame, rotation: got.rotation, points: got.points, style: got.style, id: got.id, locked: got.locked, revision: got.revision, seed: String(got.seed), text: got.text, pressures: got.pressures },
        { frame: expected.frame, rotation: expected.rotation, points: expected.points, style: want.style, id: expected.id, locked: expected.locked, revision: expected.revision, seed: String(expected.seed), text: expected.text, pressures: expected.pressures }, 1e-6);
  }
  results.check("recognition/strokes.jsonl", vector.name, problem);
}
// The stroke the live preview recognizes: at most 256 points of the one being drawn.
const preview = json("recognition/preview-input.json");
const long = { points: Array.from({ length: preview.strokePointCount }, (_, i) => [i * 0.5, 100 + 40 * Math.sin(i / 40)]) };
results.check("recognition/preview-input.json", "previewInput keeps at most 256 points", differs(previewInput(long).points, preview.previewPoints, 1e-6));
results.finish();
