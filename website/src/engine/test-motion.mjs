// Checks app-motion.js against the app's motion vectors in reference/conformance/motion: every curve, every
// timeline (one sample per millisecond), the springs and fades, and every duration. Durations must be exact and
// samples within 1e-4, as reference/conformance/README.md asks. Run: node website/src/engine/test-motion.mjs
import { lines, json, differs, tally } from "./conformance.mjs";
import { CURVES, DURATIONS, FADES, SPRINGS, ease, cubic, smooth, dropSettle, spring, springSettleTime, toolbarMorph, poseProgress, pulseScale,
  visibilityDuration, resolveVisibility, workspaceTiming, seedFor, magicFlight, toolbarPoof, poofPose, poseKeyframes, pose } from "./app-motion.js";

const results = tally(), TOLERANCE = 1e-4;
const worst = (samples, value) => samples.reduce((most, want, i) => Math.max(most, Math.abs(value(i) - want)), 0);
const within = (off) => (off <= TOLERANCE ? null : `off by ${off}`);

// curves.jsonl: 1001 samples of each curve.
const curveNames = { smoothstep: smooth, dropSettle, pointerClickEase: ease.pointerClick };
for (const v of lines("motion/curves.jsonl")) {
  const fn = curveNames[v.name] || ease[v.name];
  if (!fn) { results.check("motion/curves.jsonl", v.name, "app-motion.js has no such curve"); continue; }
  const points = v.name === "pointerClickEase" ? CURVES.pointerClick : CURVES[v.name];
  const control = v.controlPoints ? differs(points, v.controlPoints, 0, "controlPoints") : null;
  results.check("motion/curves.jsonl", v.name, control || within(worst(v.samples, (i) => fn(i / (v.samples.length - 1)))));
}

// timelines.jsonl: what each animation's value is at every millisecond.
const timeline = {
  "pose show, pop": (t) => poseProgress("pop", t, true), "pose hide, pop": (t) => poseProgress("pop", t, false),
  "pose show, drop": (t) => poseProgress("drop", t, true), "pose hide, drop": (t) => poseProgress("drop", t, false),
  "pose show, focus": (t) => poseProgress("focus", t, true), "pose hide, focus": (t) => poseProgress("focus", t, false),
  "screen zoom in": ease.enter, "screen zoom out": ease.standard,
  "spotlight dims in": ease.fade, "spotlight dims out": ease.fade, "spotlight radius": ease.standard,
  "smart draw morph": ease.enter, "smart draw shape reveal": (t) => smooth((t - 0.55) / 0.45),
  "eraser fade alpha factor": (t) => 0.22 * (1 - ease.fade(t)), "eraser fade scale": (t) => 1 - 0.03 * ease.exit(t),
  "pointer click pulse scale": pulseScale, "toolbar resize": ease.standard,
  "toolbar collapse shape": toolbarMorph.shape, "toolbar collapse outgoing face": toolbarMorph.outgoingOpacity, "toolbar collapse incoming face": toolbarMorph.incomingOpacity,
  "toolbar expand shape": toolbarMorph.shape, "toolbar expand outgoing face": toolbarMorph.outgoingOpacity, "toolbar expand incoming face": toolbarMorph.incomingOpacity,
};
const timelineDuration = {
  "pose show, pop": visibilityDuration("pop", true), "pose hide, pop": visibilityDuration("pop", false),
  "pose show, drop": visibilityDuration("drop", true), "pose hide, drop": visibilityDuration("drop", false),
  "pose show, focus": visibilityDuration("focus", true), "pose hide, focus": visibilityDuration("focus", false),
  "screen zoom in": DURATIONS.zoomIn, "screen zoom out": DURATIONS.zoomOut, "spotlight dims in": DURATIONS.spotlightFadeIn,
  "spotlight dims out": DURATIONS.spotlightFadeOut, "spotlight radius": DURATIONS.spotlightResize, "smart draw morph": DURATIONS.snapMorph,
  "smart draw shape reveal": DURATIONS.snapMorph, "eraser fade alpha factor": DURATIONS.eraseFade, "eraser fade scale": DURATIONS.eraseFade,
  "pointer click pulse scale": DURATIONS.pointerClick, "toolbar resize": DURATIONS.toolbarResize,
  "toolbar collapse shape": toolbarMorph.collapseDuration, "toolbar collapse outgoing face": toolbarMorph.collapseDuration, "toolbar collapse incoming face": toolbarMorph.collapseDuration,
  "toolbar expand shape": toolbarMorph.expandDuration, "toolbar expand outgoing face": toolbarMorph.expandDuration, "toolbar expand incoming face": toolbarMorph.expandDuration,
};
for (const v of lines("motion/timelines.jsonl")) {
  const fn = timeline[v.name];
  if (!fn) { results.check("motion/timelines.jsonl", v.name, "app-motion.js has no such timeline"); continue; }
  const samples = v.samplesEveryMillisecond, duration = timelineDuration[v.name];
  const problem = duration !== v.duration ? `duration ${duration} is not ${v.duration}`
    : samples.length !== Math.round(v.duration * 1000) + 1 ? `${samples.length} samples for ${v.duration} s` : within(worst(samples, (i) => fn(i / 1000 / v.duration)));
  results.check("motion/timelines.jsonl", v.name, problem);
}

// springs-and-fades.jsonl
for (const v of lines("motion/springs-and-fades.jsonl")) {
  if (v.response !== undefined) {
    const motion = SPRINGS[v.name];
    const problem = !motion ? "no such spring" : differs(motion, { response: v.response, dampingFraction: v.dampingFraction }, 0)
      || within(worst(v.samplesEveryMillisecondTo1500, (i) => spring(motion, i / 1000)))
      || (springSettleTime(motion) === v["settleTimeWithin0.001"] ? null : `settles at ${springSettleTime(motion)}, not ${v["settleTimeWithin0.001"]}`);
    results.check("motion/springs-and-fades.jsonl", v.name, problem);
  } else {
    const motion = FADES[v.name], curve = motion && CURVES[motion.curve];
    const problem = !motion ? "no such fade" : motion.duration !== v.duration ? `duration ${motion.duration} is not ${v.duration}` : differs(curve, v.curve, 0, "curve")
      || within(worst(v.samplesEveryMillisecond, (i) => cubic(i / 1000 / v.duration, ...curve)));
    results.check("motion/springs-and-fades.jsonl", v.name, problem);
  }
}

// durations.json: every number in it that the web port has.
{
  const d = json("motion/durations.json");
  const mine = {
    eraseFadeDuration: DURATIONS.eraseFade, hintDwellDuration: DURATIONS.hintDwell, hintFadeInDuration: DURATIONS.hintFadeIn, hintFadeOutDuration: DURATIONS.hintFadeOut,
    hintTextCrossfadeDuration: DURATIONS.hintTextCrossfade, minimumReversalShare: DURATIONS.minimumReversalShare, pointerClickDuration: DURATIONS.pointerClick,
    snapMorphDuration: DURATIONS.snapMorph, spotlightFadeInDuration: DURATIONS.spotlightFadeIn, spotlightFadeOutDuration: DURATIONS.spotlightFadeOut,
    spotlightResizeDuration: DURATIONS.spotlightResize, toolbarCollapseDuration: DURATIONS.toolbarCollapse, toolbarExpandDuration: DURATIONS.toolbarExpand,
    toolbarHideDuration: DURATIONS.toolbarHide, toolbarMorphIncomingStart: toolbarMorph.incomingStart, toolbarMorphOutgoingEnd: toolbarMorph.outgoingEnd,
    toolbarResizeDuration: DURATIONS.toolbarResize, toolbarShowDuration: DURATIONS.toolbarShow, zoomInDuration: DURATIONS.zoomIn, zoomOutDuration: DURATIONS.zoomOut,
  };
  for (const [name, value] of Object.entries(mine)) results.check("motion/durations.json", name, value === d[name] ? null : `${value} is not ${d[name]}`);
  for (const [style, want] of Object.entries(d.visibility)) {
    const got = { show: visibilityDuration(style, true), hide: visibilityDuration(style, false), resolvedStyle: resolveVisibility(style) };
    results.check("motion/durations.json", `visibility ${style}`, differs(got, want, 0));
  }
}

// What has no vector (the app's notes say Magic flights and Dissolve keep their own curves): the port's own rules,
// worked by hand from MagicFlight.swift, MagicToolbarEffect.swift and WorkspaceMotionTiming.
{
  const check = (name, ok, detail = "") => results.check("port rules (no vectors)", name, ok ? null : `wrong ${detail}`);
  check("FNV-1a seed of an empty id", seedFor("") === 14695981039346656037n);
  check("FNV-1a seed of \"a\"", seedFor("a") === 0xaf63dc4c8601ec8cn, String(seedFor("a")));
  const flight = magicFlight([0, 0], { x: 300, y: -400, scale: 0.12 }, "shape-1");
  check("a flight has 97 keyframes and ends where it should", flight.length === 97 && flight[0].translate === "0px 0px" && flight[96].translate === "300px -400px" && flight[96].scale === "0.12");
  const amplitude = Math.min(110, Math.max(24, 500 * 0.18));
  check("a flight never leaves its bend", flight.points.every((p, i) => { const t = i / 96, off = Math.hypot(p[0] - 300 * t, p[1] + 400 * t); return off <= amplitude * 1.13 + 1e-6; }));
  check("a flight banks by at most 0.15 rad", flight.every((k) => Math.abs(parseFloat(k.rotate)) <= 0.15 + 1e-9));
  const hide = poofPose(1), rest = poofPose(0), half = poofPose(0.62);
  check("the poof starts whole and ends as sparks 46 px out", rest.scale === 1 && rest.imageOpacity === 1 && rest.glowOpacity === 0 && Math.abs(hide.scale - 0.015) < 1e-12 && hide.radius === 46 && hide.sparkOpacity === 0 && half.radius === 0);
  check("the poof has 61 keyframes, a glow and 12 sparks", toolbarPoof(false).image.length === 61 && toolbarPoof(true).sparks.length === 12 && toolbarPoof(false).duration === 0.36 && toolbarPoof(true).duration === 0.4);
  const seven = workspaceTiming("magic", false, 7), back = workspaceTiming("magic", true, 7), pop = workspaceTiming("pop", false, 3), none = workspaceTiming("slide", false, 0);
  check("magic hide of seven drawings", Math.abs(seven.objectStep - 0.26 / 6) < 1e-12 && Math.abs(seven.toolbarDelay - (0.54 + 0.26 - 0.36 * 0.6)) < 1e-12 && seven.drawingDelay(0) === 0 && Math.abs(seven.totalDuration - (seven.toolbarDelay + 0.36)) < 1e-12);
  check("magic show of seven drawings", back.toolbarDelay === 0 && Math.abs(back.drawingsDelay - 0.435) < 1e-12 && Math.abs(back.drawingDelay(6) - 0.435) < 1e-12 && Math.abs(back.totalDuration - (0.435 + 0.26 + 0.62)) < 1e-12);
  check("pop hide of three drawings", pop.objectStep === 0.065 && Math.abs(pop.toolbarDelay - 0.2) < 1e-12 && pop.toolbarDuration === 0.26);
  check("slide with no drawings", none.toolbarDelay === 0 && none.toolbarDuration === 0.24 && none.totalDuration === 0.24);
  const popped = poseKeyframes("pop", { appearing: false }), dropped = pose("drop", 1, { appearing: false, annotation: true, seed: 3n });
  check("pop ends at 18% and clear", popped.length === 31 && popped.duration === 260 && popped[30].scale === "0.18" && popped[30].opacity === "0");
  check("drop falls 110 px and tips", dropped.y === 110 && Math.abs(dropped.rotate - 0.16) < 1e-12 && Math.abs(dropped.scale - 0.94) < 1e-12 && dropped.opacity === 0);
}
results.finish();
