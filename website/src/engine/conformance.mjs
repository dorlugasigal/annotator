// Shared by the Node checks: reads the app's conformance vectors (reference/conformance, written on a Mac) and turns
// them into the shapes annotation-core.js works with.
import { readFileSync } from "node:fs";
import { defaultStyle } from "./annotation-core.js";

const root = new URL("../../../reference/conformance/", import.meta.url);
// Seeds are 64-bit, beyond what a JavaScript number holds, so they are read as text.
const parse = (text) => JSON.parse(text.replace(/"seed":\s*(\d+)/g, '"seed":"$1"'));
export const lines = (path) => readFileSync(new URL(path, root), "utf8").split("\n").filter(Boolean).map(parse);
export const json = (path) => parse(readFileSync(new URL(path, root), "utf8"));

const point = (p) => (Array.isArray(p) ? p : [p.x, p.y]);
const anchor = (a) => (a ? { elementID: a.elementID, relative: point(a.relative) } : null);
// An element as the Mac wrote it, as annotation-core.js reads it. The Mac leaves out style values that are unset.
export function element(v) {
  return {
    id: v.id, tool: v.tool, frame: { ...v.frame }, points: v.points.map(point), pressures: [...v.pressures], text: v.text,
    style: { ...defaultStyle("pen"), width: 3, headScale: undefined, ...v.style }, rotation: v.rotation, groupID: v.groupID ?? null, locked: v.locked,
    startAnchor: anchor(v.startAnchor), endAnchor: anchor(v.endAnchor), seed: BigInt(v.seed), revision: v.revision, geometryToken: v.geometryToken,
  };
}

// Whether two values are the same, with numbers compared within `tolerance`. Returns where they differ, or null.
export function differs(actual, expected, tolerance, path = "") {
  if (typeof expected === "number" && typeof actual === "number") return Math.abs(actual - expected) <= tolerance ? null : `${path}: ${actual} is not ${expected}`;
  if (Array.isArray(expected)) {
    if (!Array.isArray(actual) || actual.length !== expected.length) return `${path}: ${Array.isArray(actual) ? `${actual.length} items, not ${expected.length}` : "not a list"}`;
    for (let i = 0; i < expected.length; i++) { const d = differs(actual[i], expected[i], tolerance, `${path}[${i}]`); if (d) return d; }
    return null;
  }
  if (expected && typeof expected === "object") {
    if (!actual || typeof actual !== "object") return `${path}: ${JSON.stringify(actual)} is not an object`;
    for (const key of Object.keys(expected)) { const d = differs(actual[key], expected[key], tolerance, `${path}.${key}`); if (d) return d; }
    return null;
  }
  return actual === expected || (actual == null && expected == null) ? null : `${path}: ${JSON.stringify(actual)} is not ${JSON.stringify(expected)}`;
}

// Counts vectors by folder and prints the first few that fail.
export function tally() {
  const folders = new Map();
  let failed = 0;
  return {
    check(folder, name, problem) {
      const entry = folders.get(folder) || folders.set(folder, { passed: 0, failed: 0 }).get(folder);
      if (problem) { entry.failed++; failed++; if (failed <= 12) console.log(`FAIL ${folder}: ${name}\n     ${String(problem).slice(0, 300)}`); } else entry.passed++;
    },
    finish() {
      let total = 0;
      for (const [folder, { passed, failed: bad }] of folders) { total += passed + bad; console.log(`${bad ? "FAIL" : "ok  "} ${folder}: ${passed} of ${passed + bad} vectors pass`); }
      console.log(failed ? `${failed} of ${total} vectors FAILED` : `All ${total} vectors pass.`);
      process.exit(failed ? 1 : 0);
    },
  };
}
