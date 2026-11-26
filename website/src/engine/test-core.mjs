// Checks annotation-core.js, the web demo's port of the app's core, against the app's vectors in reference/conformance:
// hit testing, the laser trail, shape and stroke paths, arrowheads, drag constraints, connectors, anchors, editing
// sessions with undo, and the motion curves. Vectors for things the web demo does not have (diagram shapes, the
// zoom lens, the pointer, fonts) are not run and are listed at the end.
// Run: node website/src/engine/test-core.mjs
import { P, Tool, contains, Stacking, LaserTrail, pathFor, canonicalShape, arrowHead, DragConstraint, Connector, anchorLocations, anchorPoint, symbolFrame,
  polygonVertices, roundedDiamondCorners, resize, translate, selectionGeometry, CanvasDocument, Motion, Viewport, ZoomFollow, ZoomMap } from "./annotation-core.js";
import { lines, json, element, differs, tally } from "./conformance.mjs";

const results = tally(), skipped = [];
const attempt = (run) => { try { return run(); } catch (error) { return `threw ${error.stack}`; } };

// hittest ----------------------------------------------------------------------------------------
const grid = (v, symbol) => Array.from({ length: v.rows }, (_, row) => Array.from({ length: v.columns }, (_, column) => symbol([v.origin[0] + column * v.step, v.origin[1] + row * v.step])).join(""));
const gridDiffers = (got, want) => { const row = want.findIndex((line, i) => line !== got[i]); return row < 0 ? null : `row ${row}:\n     ${got[row]}\n     ${want[row]}`; };
for (const v of lines("hittest/contains.jsonl")) {
  const el = element(v.element);
  results.check("hittest/contains.jsonl", v.name, attempt(() => gridDiffers(grid(v, (p) => (contains(p, el, v.tolerance) ? "#" : ".")), v.grid)));
}
for (const v of lines("hittest/frontmost.jsonl")) {
  const elements = v.elements.map(element), letters = new Map(Object.entries(v.legend).map(([letter, id]) => [id, letter]));
  results.check("hittest/frontmost.jsonl", v.scene, attempt(() => gridDiffers(grid(v, (p) => letters.get(Stacking.frontmost(p, elements)?.id) ?? "."), v.grid)));
}

// laser ------------------------------------------------------------------------------------------
function replay(v) {
  const trail = new LaserTrail(v.lifetime);
  for (const step of v.steps) {
    if (step.press) trail.press(step.press.point, step.press.time, step.press.color, step.press.width);
    else if (step.move) trail.move(step.move.point, step.move.time);
    else if (step.release) trail.release(step.release.point ?? null, step.release.time);
    else trail.prune(step.prune.time);
  }
  return trail;
}
for (const v of lines("laser/trails.jsonl")) {
  results.check("laser/trails.jsonl", v.name, attempt(() => {
    const trail = replay(v);
    const frames = v.frames.map((frame) => {
      const pruned = replay(v);
      pruned.prune(frame.time);
      return { time: frame.time, bounds: trail.bounds(frame.time),
        shapes: trail.shapes(frame.time).map((shape) => ({ color: shape.color, opacity: shape.opacity, dots: shape.dots,
          bands: LaserTrail.passes.map((pass) => shape.dots.slice(1).map((dot, i) => LaserTrail.band(shape.dots[i], dot, pass))) })),
        afterPrune: { strokeSampleCounts: pruned.strokes.map((s) => s.samples.length), isEmpty: pruned.isEmpty } };
    });
    return differs({ strokes: trail.strokes, isDrawing: trail.isDrawing, isResting: trail.isResting, frames }, { strokes: v.strokes, isDrawing: v.isDrawing, isResting: v.isResting, frames: v.frames }, 1e-9);
  }));
}
{
  const c = json("laser/constants.json");
  results.check("laser/constants.json", "constants", differs({ defaultLifetime: LaserTrail.defaultLifetime, minimumStep: LaserTrail.minimumStep, tailTaper: LaserTrail.tailTaper,
    restInterval: LaserTrail.restInterval, maximumSamples: LaserTrail.maximumSamples, passes: LaserTrail.passes.map((p) => ({ ...p, white: p.white === true })) },
  { defaultLifetime: c.lifetime, minimumStep: c.minimumStep, tailTaper: c.tailTaper, restInterval: c.restInterval, maximumSamples: c.maximumSamples, passes: c.passes }, 0));
}

// geometry ---------------------------------------------------------------------------------------
const PORTED_SHAPES = new Set(["rectangle", "ellipse", "diamond", "triangle", "hexagon", "star",
  "databaseStack", "server", "messaging", "web", "mobile", "desktop", "laptop", "document", "configuration", "loadBalancer", "apiGateway", "envelope", "cart", "bell"]);
let unported = 0;
for (const v of lines("geometry/shapes.jsonl")) {
  if (!PORTED_SHAPES.has(v.element.tool)) { unported++; continue; }
  results.check("geometry/shapes.jsonl", `${v.element.tool}, ${v.variant}`, attempt(() => differs(pathFor(element(v.element)), v.path.elements, 1e-6)));
}
skipped.push(`geometry/shapes.jsonl: ${unported} vectors for diagram shapes the web demo does not have (grid, axes, database, queue and person)`);
for (const v of lines("geometry/strokes.jsonl")) results.check("geometry/strokes.jsonl", v.variant, attempt(() => differs(pathFor(element(v.element)), v.path.elements, 1e-6)));
for (const v of lines("geometry/arrowheads.jsonl")) {
  const el = element(v.element), view = (head) => (head ? { kind: head.kind, tip: head.tip, direction: head.direction, length: head.length, side: head.side, base: head.base, left: head.left, right: head.right, polygon: head.polygon, hollow: head.hollow } : null);
  const want = (head) => (head ? view(head) : null);
  results.check("geometry/arrowheads.jsonl", `${v.kind}, ${v.config}`, attempt(() => differs({ start: view(arrowHead(el, true)), end: view(arrowHead(el, false)) }, { start: want(v.start), end: want(v.end) }, 1e-6)));
}
let ties = 0;
for (const v of lines("geometry/snapping.jsonl")) {
  const got = attempt(() => (v.constraint === "aspectEnd" ? DragConstraint.aspectEnd(v.start, v.point, v.ratio) : DragConstraint[v.constraint](v.start, v.point)));
  let problem = typeof got === "string" ? got : differs(got, v.result, 1e-6);
  if (problem && v.constraint === "snappedLineEnd") {
    // A drag exactly halfway between two 45-degree directions: atan2 differs in its last bit between this JavaScript
    // engine and the Mac, so either neighbor is right (the same rule the README gives for hit-test thresholds).
    const steps = Math.atan2(v.point[1] - v.start[1], v.point[0] - v.start[0]) / (Math.PI / 4), length = P.dist(v.start, v.point);
    if (Math.abs(Math.abs(steps - Math.floor(steps)) - 0.5) < 1e-9) {
      const neighbors = [Math.floor(steps), Math.ceil(steps)].map((k) => P.add(v.start, P.mul([Math.cos(k * Math.PI / 4), Math.sin(k * Math.PI / 4)], length)));
      if (neighbors.some((p) => !differs(p, v.result, 1e-6))) { problem = null; ties++; }
    }
  }
  results.check("geometry/snapping.jsonl", `${v.constraint} ${v.start} to ${v.point}`, problem);
}
if (ties) skipped.push(`geometry/snapping.jsonl: ${ties} vector on an exact 67.5-degree tie passes as either neighbor (last-bit atan2 difference between engines)`);
for (const v of lines("geometry/connectors.jsonl")) {
  const view = (c) => (c ? { start: c.start, firstControl: c.firstControl, secondControl: c.secondControl, end: c.end, samples: Array.from({ length: 9 }, (_, i) => Connector.point(c, i / 8)) } : null);
  const want = (c) => (c ? { start: c.start, firstControl: c.firstControl, secondControl: c.secondControl, end: c.end, samples: c.samples } : null);
  results.check("geometry/connectors.jsonl", `${v.points.length} points`, attempt(() => differs(
    { curves: Connector.curves(v.points).map(view), startCurve: view(Connector.endpointCurve(v.points, true)), endCurve: view(Connector.endpointCurve(v.points, false)) },
    { curves: v.curves.map(want), startCurve: want(v.startCurve), endCurve: want(v.endCurve) }, 1e-6)));
}
for (const v of lines("geometry/anchors.jsonl")) {
  const el = { tool: v.tool, frame: v.frame, rotation: v.rotation };
  results.check("geometry/anchors.jsonl", `${v.tool} turned ${v.rotation}`, attempt(() => differs(
    { anchorLocations: anchorLocations(el), anchorPoints: anchorLocations(el).map((location) => anchorPoint(el, location)), symbolFrame: symbolFrame(el) },
    { anchorLocations: v.anchorLocations, anchorPoints: v.anchorPoints, symbolFrame: v.symbolFrame }, 1e-6)));
}
for (const v of lines("geometry/polygons.jsonl")) {
  if (v.polygon) results.check("geometry/polygons.jsonl", `${v.polygon} in ${JSON.stringify(v.frame)}`, attempt(() => differs(polygonVertices(v.polygon, v.frame), v.vertices, 1e-6)));
  else results.check("geometry/polygons.jsonl", `rounded diamond ${v.roundedDiamond} in ${JSON.stringify(v.frame)}`, attempt(() => differs(
    { corners: roundedDiamondCorners(v.frame, v.roundedDiamond), path: canonicalShape({ tool: "diamond", frame: v.frame, style: { cornerRadius: v.roundedDiamond } }) }, { corners: v.corners, path: v.path }, 1e-6)));
}
for (const v of lines("geometry/transforms.jsonl")) {
  let problem;
  if (v.call === "resize") problem = attempt(() => { const el = element(v.element); resize(el, v.to); return differs({ frame: el.frame, points: el.points, revision: el.revision }, { frame: v.frame, points: v.points.map((p) => [p.x ?? p[0], p.y ?? p[1]]), revision: v.revision }, 1e-6); });
  else if (v.call === "translate") problem = attempt(() => { const el = element(v.element); translate(el, v.by); return differs({ frame: el.frame, points: el.points }, { frame: v.frame, points: v.points.map((p) => [p.x ?? p[0], p.y ?? p[1]]) }, 1e-6); });
  else if (v.call === "symbolFrame") problem = attempt(() => differs(symbolFrame({ tool: v.tool, frame: v.frame }), v.result, 1e-6));
  else problem = attempt(() => { const elements = v.elements.map(element); return differs(selectionGeometry(elements, new Set(elements.map((e) => e.id))), v.result, 1e-6); });
  results.check("geometry/transforms.jsonl", `${v.call} ${v.case ?? v.tool ?? v.element.tool}`, problem);
}
// Screen zoom and its map (ZoomFollow.swift). A result is the viewport, or the Mac's error.
{
  const rect = (r) => ({ x: r.x, y: r.y, width: r.width, height: r.height });
  // What the Mac records of a viewport: it, where two display points land in the source, and three source points on the display.
  const view = (v) => { const d = v.output; return { output: rect(v.output), source: rect(v.source),
    sourcePoints: [[d.x, d.y], [d.x + d.width / 2, d.y + d.height / 2], [d.x + d.width, d.y + d.height]].map((p) => Viewport.sourcePoint(v, p)),
    outputPoints: [[v.source.x, v.source.y], [v.source.x + v.source.width / 2, v.source.y + v.source.height / 2]].map((p) => Viewport.outputPoint(v, p)) }; };
  const outcome = (run, want, shape = view) => {
    let got;
    try { got = run(); } catch (error) { return want.error ? (error.message === want.error.message ? null : `threw "${error.message}", not "${want.error.message}"`) : `threw ${error.stack}`; }
    return want.error ? "did not throw" : differs(shape(got), want.ok, 1e-6);
  };
  const number = (value) => (value == null ? NaN : value), pair = (p) => [number(p[0]), number(p[1])];
  let checkedShape = false;
  for (const v of lines("geometry/viewports.jsonl")) {
    const display = v.display, factor = number(v.factor), name = `${v.call} ${JSON.stringify({ factor: v.factor, pointer: v.pointer ?? v.cursor, previous: v.previous, delta: v.delta, elapsed: v.elapsed, position: v.position, width: display.width })}`;
    let problem;
    if (v.call === "cursorAnchored") problem = outcome(() => Viewport.cursorAnchored(display, factor, pair(v.cursor)), v.result);
    else if (v.call === "zoomFollow") problem = outcome(() => ZoomFollow.viewport(v.previous, display, factor, pair(v.pointer)), v.result);
    else if (v.call === "zoomFollowPush") problem = outcome(() => ZoomFollow.push(ZoomFollow.viewport(null, display, factor, [display.x + display.width / 2, display.y + display.height / 2]), pair(v.pointer), pair(v.delta)), v.result);
    else if (v.call === "zoomFollowVelocity") problem = differs(ZoomFollow.velocity(display, factor, pair(v.pointer)), v.result, 1e-6);
    else if (v.call === "zoomFollowSlide") problem = outcome(() => {
      const start = ZoomFollow.viewport(null, display, factor, [display.x + display.width / 2, display.y + display.height / 2]);
      return { viewport: ZoomFollow.slide(start, pair(v.pointer), number(v.elapsed)), slides: ZoomFollow.slides(start, pair(v.pointer)) };
    }, v.result, (got) => ({ viewport: view(got.viewport), slides: got.slides }));
    else if (v.call === "zoomMap") {
      const frame = ZoomMap.frame(display, v.position), zoomed = Viewport.cursorAnchored(display, 2.5, [display.x + 300, display.y + 200]);
      problem = differs({ label: ZoomMap.positions[v.position], frame, zoomedArea: frame && v.zoomedArea ? ZoomMap.zoomedArea(zoomed, frame) : null }, { label: v.label, frame: v.frame, zoomedArea: v.zoomedArea }, 1e-6);
    } else continue;
    if (!checkedShape && v.result?.ok?.sourcePoints) { checkedShape = true; if (v.result.ok.sourcePoints.length !== 3 || v.result.ok.outputPoints.length !== 2) problem = "the vectors' viewport record has changed shape"; }
    results.check("geometry/viewports.jsonl", name, problem);
  }
}
skipped.push("geometry: axis-ticks, ink-pressure, pointer-shapes, text (macOS fonts) and the viewports of the zoom lens and region sharing are not part of the web demo");

// editing ----------------------------------------------------------------------------------------
// The Mac names the IDs it makes at random in the order they first appear; both sides are renamed the same way here.
function renamer(isRandom) {
  const names = new Map();
  const name = (id) => (id == null ? null : !isRandom(id) ? id : names.get(id) ?? names.set(id, `random-${names.size + 1}`).get(id));
  return { name, walk(elements) { for (const el of elements) { name(el.id); name(el.groupID); name(el.startAnchor?.elementID); name(el.endAnchor?.elementID); } } };
}
function session(file, document, script) {
  const records = lines(file), mine = renamer((id) => String(id).startsWith("new-")), theirs = renamer((id) => id.startsWith("FFFFFFFF-"));
  let index = 0, serial = 0;
  document.newID = () => `new-${++serial}`;
  const anchorView = (a, names) => (a ? { elementID: names.name(a.elementID), relative: a.relative } : null);
  const view = (el, names) => ({ id: names.name(el.id), tool: el.tool, frame: el.frame, points: el.points, pressures: el.pressures, text: el.text, rotation: el.rotation, locked: el.locked,
    groupID: names.name(el.groupID), startAnchor: anchorView(el.startAnchor, names), endAnchor: anchorView(el.endAnchor, names), revision: el.revision, style: el.style });
  const step = (name, body = () => null) => {
    const record = records[index++];
    if (!record || record.step !== name) { results.check(file, name, `the vector's step ${index} is "${record?.step}"`); return; }
    let result = null, error = null;
    try { result = body(document) ?? null; } catch (thrown) { error = thrown.message; }
    mine.walk(document.elements);
    const expected = record.elements.map(element);
    theirs.walk(expected);
    const last = (list) => list[list.length - 1]?.operationID ?? null;
    const got = { result: result && result.elementID ? anchorView(result, mine) : result, error,
      elements: document.elements.map((el) => view(el, mine)), selection: [...document.selection].map(mine.name).sort(), generation: document.generation,
      canUndo: document.canUndo, canRedo: document.canRedo, isEditing: document.isEditing, hasPendingChanges: document.hasPendingChanges, historyBytes: document.historyBytes,
      hasProtection: document.elements.some((el) => Tool.isProtection(el.tool)), presentation: document.presentation, undoOperationID: last(document.past), redoOperationID: last(document.future) };
    const want = { result: record.result && record.result.elementID ? anchorView(record.result, theirs) : record.result ?? null, error: record.error?.message ?? null,
      elements: expected.map((el, i) => ({ ...view(el, theirs), style: record.elements[i].style })), selection: record.selection.map(theirs.name).sort(), generation: record.generation,
      canUndo: record.canUndo, canRedo: record.canRedo, isEditing: record.isEditing, hasPendingChanges: record.hasPendingChanges, historyBytes: record.historyBytes,
      hasProtection: record.hasProtection, presentation: record.presentation, undoOperationID: record.undoOperationID, redoOperationID: record.redoOperationID };
    results.check(file, `step ${index}: ${name}`, differs(got, want, 1e-9) || (got.elements.length !== want.elements.length ? "a different number of elements" : null));
  };
  // The fixtures are the elements as the vectors first show them.
  const fixture = (stepName, id) => element(records.find((r) => r.step === stepName).elements.find((el) => el.id === id));
  script(step, fixture);
  if (index !== records.length) results.check(file, "every step ran", `${records.length - index} steps were not run`);
}
const id = (number) => `00000000-0000-4000-8000-${String(number).padStart(12, "0")}`;
const blue = { red: 0.17, green: 0.45, blue: 0.88, alpha: 1 };
session("editing/session.jsonl", new CanvasDocument(), (step, fixture) => {
  const rectangle = fixture("add a rectangle", id(1)), ellipse = fixture("add an ellipse", id(2)), arrow = fixture("add an arrow bound to both", id(3));
  const pen = fixture("add a pen stroke with one point", id(4)), cover = fixture("add a cover", id(5)), stroke = fixture("add a pen stroke to recognize", id(6));
  step("start with an empty document");
  step("add a rectangle", (d) => d.add(rectangle));
  step("add an ellipse", (d) => d.add(ellipse));
  step("add an arrow bound to both", (d) => d.add(arrow));
  step("move the rectangle by (10, 20); the arrow start follows", (d) => d.update(id(1), (el) => translate(el, [10, 20])));
  step("resize the ellipse; the arrow end follows", (d) => d.update(id(2), (el) => resize(el, { x: 320, y: 40, width: 140, height: 100 })));
  step("select the rectangle and the ellipse", (d) => d.select([id(1), id(2)]));
  step("group them", (d) => d.groupSelection());
  step("select nothing by clicking empty space", (d) => d.selectAt([700, 500]));
  step("click the ellipse, which selects its whole group", (d) => d.selectAt([390, 90]));
  step("shift-click the arrow to add it", (d) => d.selectAt([240, 95], true));
  step("duplicate the selection", (d) => d.duplicateSelection());
  step("send the copies to the back", (d) => d.arrangeSelection(false));
  step("lock the copies", (d) => d.setSelectionLocked(true));
  step("delete the selection; locked objects stay", (d) => d.deleteSelection());
  step("unlock and delete the copies", (d) => { d.setSelectionLocked(false); d.deleteSelection(); });
  step("undo", (d) => d.undo());
  step("undo", (d) => d.undo());
  step("redo", (d) => d.redo());
  step("ungroup the selection", (d) => d.ungroupSelection());
  step("begin an edit and move the arrow", (d) => { d.begin(); d.update(id(3), (el) => translate(el, [0, 50])); });
  step("cancel the edit", (d) => d.cancel());
  step("begin an edit, recolor the selection and commit with an operation ID", (d) => { d.begin(); d.editSelection((el) => { el.style.color = blue; }); d.commit(id(800)); });
  step("undo the operation", (d) => d.undo());
  step("add a pen stroke with one point", (d) => d.add(pen));
  step("append three pen samples with pressure", (d) => [[70, 210], [85, 205], [100, 220]].forEach((point, i) => d.appendSample(id(4), point, 0.25 * (i + 1))));
  step("undo the last sample", (d) => d.undo());
  step("append a sample after the undo", (d) => d.appendSample(id(4), [120, 230]));
  step("nearest anchor near the rectangle's right edge", (d) => d.nearestAnchor([168, 112], id(3)));
  step("nearest anchor just outside 18 points", (d) => d.nearestAnchor([170 + 18.5, 120], id(3)));
  step("nearest anchor between two candidates", (d) => d.nearestAnchor([330, 85], id(3), 40));
  step("add a cover", (d) => d.add(cover));
  step("select everything and delete without covers", (d) => { d.select(d.elements.map((el) => el.id)); d.deleteSelection(false); });
  step("undo", (d) => d.undo());
  step("clear annotations; the cover stays", (d) => d.clearAnnotations());
  step("undo", (d) => d.undo());
  step("clear covers and effects", (d) => d.clearProtection());
  step("show the lens at 3x", (d) => d.setPresentation({ mode: "lens", zoom: 3 }));
  step("stop the lens without a history step", (d) => d.stopPresentationWithoutHistory());
  step("copy the rectangle and the arrow 40 points right; the copy keeps only its binding to the copied rectangle", (d) => d.insertCopies(d.elements.filter((el) => [id(1), id(3)].includes(el.id)), [40, 0]));
  step("clear all", (d) => d.clearAll());
  step("undo", (d) => d.undo());
  // Smart Draw's replacement: refused when the document or the stroke changed since it was read.
  let generation, token;
  const shape = { ...element({ ...records6(stroke), tool: "rectangle", frame: { x: 500, y: 300, width: 100, height: 80 }, points: [] }), seed: stroke.seed };
  step("add a pen stroke to recognize", (d) => { d.add(stroke); generation = d.generation; token = d.byID(id(6)).geometryToken; });
  step("apply a recognized rectangle with a stale generation", (d) => d.applyRecognition({ ...shape }, generation + 1, token));
  step("apply a recognized rectangle with a stale geometry token", (d) => d.applyRecognition({ ...shape }, generation, "stale"));
  step("apply the recognized rectangle", (d) => d.applyRecognition({ ...shape }, generation, token));
  step("undo after recognition", (d) => d.undo());
});
function records6(stroke) {
  return { id: stroke.id, frame: stroke.frame, points: [], pressures: [], text: "", style: stroke.style, rotation: 0, locked: false, seed: String(stroke.seed), revision: 0, geometryToken: "shape" };
}
session("editing/history-limits.jsonl", new CanvasDocument({ historyByteLimit: 3000, historyCountLimit: 3 }), (step, fixture) => {
  for (let number = 1; number <= 6; number++) { const rectangle = fixture(`add rectangle ${number}`, id(number)); step(`add rectangle ${number}`, (d) => d.add(rectangle)); }
  for (let i = 0; i < 4; i++) step("undo", (d) => d.undo());
  for (let i = 0; i < 4; i++) step("redo", (d) => d.redo());
  const long = fixture("add a stroke with 200 points, larger than the history budget", id(7));
  step("add a stroke with 200 points, larger than the history budget", (d) => d.add(long));
  step("undo", (d) => d.undo());
});
skipped.push("editing/refusals.jsonl: the web demo never builds an invalid document, so the refusal messages are not checked");

// motion -----------------------------------------------------------------------------------------
for (const v of lines("motion/curves.jsonl")) {
  const curve = v.controlPoints ? (x) => Motion.progress(v.controlPoints, x) : v.name === "smoothstep" ? Motion.smooth : null;
  if (!curve) { skipped.push(`motion/curves.jsonl: ${v.name} (the toolbar's Drop animation, not used on the web)`); continue; }
  const worst = v.samples.reduce((most, want, i) => Math.max(most, Math.abs(curve(i / (v.samples.length - 1)) - want)), 0);
  results.check("motion/curves.jsonl", v.name, worst <= 1e-4 ? null : `off by ${worst}`);
}
{
  const d = json("motion/durations.json");
  results.check("motion/durations.json", "erase fade, laser lifetime and Smart Draw morph", differs(
    { eraseFadeDuration: Motion.eraseFadeDuration, laserTrailLifetime: LaserTrail.defaultLifetime, snapMorphDuration: Motion.snapMorphDuration },
    { eraseFadeDuration: d.eraseFadeDuration, laserTrailLifetime: d.laserTrailLifetime, snapMorphDuration: d.snapMorphDuration }, 0));
  for (const [name, curve] of Object.entries({ enter: Motion.enter, exit: Motion.exit, standard: Motion.standard, fade: Motion.fade })) {
    const want = lines("motion/curves.jsonl").find((v) => v.name === name).controlPoints;
    results.check("motion/curves.jsonl", `${name} control points`, differs(curve, want, 0));
  }
}
console.log(`Not run:\n  ${skipped.join("\n  ")}`);
results.finish();
