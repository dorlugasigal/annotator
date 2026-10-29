import AnnotationCore
import Foundation
import MotionCore
@testable import PresentationRendering
import QuartzCore

/// Motion curves and timelines from MotionCore, sampled every millisecond.
@MainActor
enum MotionExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("motion", "MotionCore curves, springs, fades and timelines sampled every millisecond, the per-style show and hide durations, and the pointer click pulse.")
        try curves(corpus)
        try springs(corpus)
        try timelines(corpus)
        try durations(corpus)
    }

    private static func samples(_ count: Int, _ value: (Int) -> Double) -> J { .array((0...count).map { .double(value($0)) }) }

    private static func curves(_ corpus: Corpus) throws {
        var records: [J] = []
        for (name, curve) in MotionCurves.named {
            let function = CAMediaTimingFunction(controlPoints: Float(curve.x1), Float(curve.y1), Float(curve.x2), Float(curve.y2))
            var first = [Float](repeating: 0, count: 2), second = [Float](repeating: 0, count: 2)
            function.getControlPoint(at: 1, values: &first)
            function.getControlPoint(at: 2, values: &second)
            let stored = [Double(first[0]), Double(first[1]), Double(second[0]), Double(second[1])]
            var largest = 0.0
            for step in 0...1_000 {
                let x = Double(step) / 1_000
                largest = max(largest, abs(curve.progress(at: x) - MotionCurves.cubic(x, stored[0], stored[1], stored[2], stored[3])))
            }
            records.append([
                "name": .string(name), "controlPoints": [.double(curve.x1), .double(curve.y1), .double(curve.x2), .double(curve.y2)],
                "coreAnimationControlPoints": .array(stored.map { .double($0) }),
                "largestDifferenceFromCoreAnimationControlPoints": .double(largest),
                "samples": samples(1_000) { curve.progress(at: Double($0) / 1_000) }
            ])
        }
        records.append(["name": "smoothstep", "samples": samples(1_000) { MotionCurves.smooth(Double($0) / 1_000) }])
        records.append(["name": "dropSettle", "samples": samples(1_000) { MotionCurves.dropSettle(Double($0) / 1_000) }])
        records.append(["name": "pointerClickEase", "controlPoints": [0.4, 0, 0.2, 1],
                        "samples": samples(1_000) { CursorHighlightRenderer.easeInOut(Double($0) / 1_000) }])
        try corpus.lines("motion/curves.jsonl", records)
    }

    private static func springs(_ corpus: Corpus) throws {
        var records: [J] = []
        for (name, spring) in MotionTiming.springs {
            records.append(["name": .string(name), "response": .double(spring.response), "dampingFraction": .double(spring.dampingFraction),
                            "settleTimeWithin0.001": .double(spring.settleTime()),
                            "samplesEveryMillisecondTo1500": samples(1_500) { spring.value(at: Double($0) / 1_000) }])
        }
        for (name, motion) in MotionTiming.timed {
            let milliseconds = Int((motion.duration * 1_000).rounded())
            records.append(["name": .string(name), "duration": .double(motion.duration),
                            "curve": [.double(motion.curve.x1), .double(motion.curve.y1), .double(motion.curve.x2), .double(motion.curve.y2)],
                            "samplesEveryMillisecond": samples(milliseconds) { motion.curve.progress(at: Double($0) / 1_000 / motion.duration) }])
        }
        try corpus.lines("motion/springs-and-fades.jsonl", records)
    }

    /// Styles that use VisibilityPose in the app (VisibilityPose.usesPose).
    private static let VisibilityPoseStyles: Set<VisibilityAnimationStyle> = [.pop, .drop, .focus]

    /// One eased value per millisecond from 0 to the duration. Time is clamped to 0...1 like the app's tickers.
    private static func timeline(_ name: String, duration: Double, source: String, _ value: (Double) -> Double) -> J {
        let milliseconds = Int((duration * 1_000).rounded())
        return ["name": .string(name), "duration": .double(duration), "source": .string(source),
                "samplesEveryMillisecond": samples(milliseconds) { duration > 0 ? value(min(1, Double($0) / 1_000 / duration)) : 1 }]
    }

    private static func timelines(_ corpus: Corpus) throws {
        var records: [J] = []
        // WorkspaceLayerMotion animates the toolbar and each shape. Pop, Drop and Focus sample VisibilityPose.hidden
        // into 31 keyframes (VisibilityPose.stepCount = 30). The other styles are described in durations.json notes.
        for style in VisibilityAnimationStyle.available where VisibilityPoseStyles.contains(style) {
            let show = MotionTiming.visibilityDuration(style, appearing: true)
            let hide = MotionTiming.visibilityDuration(style, appearing: false)
            if style == .drop {
                records.append(timeline("pose show, drop", duration: show, source: "VisibilityPose.hidden: dropSettle when appearing") {
                    MotionCurves.dropSettle($0) })
                records.append(timeline("pose hide, drop", duration: hide, source: "VisibilityPose.hidden: linear when hiding; the pose squares it") { $0 })
            } else if style == .pop || style == .focus {
                records.append(timeline("pose show, \(style.rawValue)", duration: show, source: "VisibilityPose.hidden: enter when appearing") {
                    MotionCurves.enter.progress(at: $0) })
                records.append(timeline("pose hide, \(style.rawValue)", duration: hide, source: "VisibilityPose.hidden: standard when hiding") {
                    MotionCurves.standard.progress(at: $0) })
            }
        }
        records.append(timeline("screen zoom in", duration: MotionTiming.zoomInDuration, source: "LivePresentation zoom ramp: enter") {
            MotionCurves.enter.progress(at: $0) })
        records.append(timeline("screen zoom out", duration: MotionTiming.zoomOutDuration, source: "LivePresentation zoom ramp: standard") {
            MotionCurves.standard.progress(at: $0) })
        records.append(timeline("spotlight dims in", duration: MotionTiming.spotlightFadeInDuration, source: "SelectionOverlay: fade, for a full change") {
            MotionCurves.fade.progress(at: $0) })
        records.append(timeline("spotlight dims out", duration: MotionTiming.spotlightFadeOutDuration, source: "SelectionOverlay: fade, for a full change") {
            MotionCurves.fade.progress(at: $0) })
        records.append(timeline("spotlight radius", duration: MotionTiming.spotlightResizeDuration, source: "SelectionOverlay: standard") {
            MotionCurves.standard.progress(at: $0) })
        records.append(timeline("smart draw morph", duration: MotionTiming.snapMorphDuration, source: "DrawingCanvas snap morph: enter") {
            MotionCurves.enter.progress(at: $0) })
        records.append(timeline("smart draw shape reveal", duration: MotionTiming.snapMorphDuration, source: "DrawingCanvas snap morph: smoothstep((t − 0.55) / 0.45)") {
            MotionCurves.smooth(($0 - 0.55) / 0.45) })
        records.append(timeline("eraser fade alpha factor", duration: MotionTiming.eraseFadeDuration, source: "DrawingCanvas erase fade: 0.22 × (1 − fade)") {
            0.22 * (1 - MotionCurves.fade.progress(at: $0)) })
        records.append(timeline("eraser fade scale", duration: MotionTiming.eraseFadeDuration, source: "DrawingCanvas erase fade: 1 − 0.03 × exit") {
            1 - 0.03 * MotionCurves.exit.progress(at: $0) })
        records.append(timeline("pointer click pulse scale", duration: CursorHighlightSettings.clickDuration,
                                source: "CursorHighlightRenderer.pulseScale") { CursorHighlightRenderer.pulseScale($0) })
        records.append(timeline("toolbar resize", duration: MotionTiming.toolbarResizeDuration,
                                source: "ToolbarFrameAnimator: standard") { ToolbarMorph.shape($0) })
        for (name, duration) in [("collapse", MotionTiming.toolbarCollapseDuration), ("expand", MotionTiming.toolbarExpandDuration)] {
            records.append(timeline("toolbar \(name) shape", duration: duration, source: "ToolbarMorph.shape: standard") {
                ToolbarMorph.shape($0) })
            records.append(timeline("toolbar \(name) outgoing face", duration: duration,
                                    source: "ToolbarMorph.outgoingOpacity: 1 − fade(t / 0.5)") { ToolbarMorph.outgoingOpacity($0) })
            records.append(timeline("toolbar \(name) incoming face", duration: duration,
                                    source: "ToolbarMorph.incomingOpacity: fade((t − 0.35) / 0.65)") { ToolbarMorph.incomingOpacity($0) })
        }
        try corpus.lines("motion/timelines.jsonl", records)
    }

    private static func durations(_ corpus: Corpus) throws {
        try corpus.json("motion/durations.json", [
            "visibility": .object(Dictionary(uniqueKeysWithValues: VisibilityAnimationStyle.allCases.map { style in
                (style.rawValue, J.object(["show": .double(MotionTiming.visibilityDuration(style, appearing: true)),
                                           "hide": .double(MotionTiming.visibilityDuration(style, appearing: false)),
                                           "resolvedStyle": .string(style.resolved.rawValue)]))
            })),
            "toolbarShowDuration": .double(MotionTiming.toolbarShowDuration),
            "toolbarHideDuration": .double(MotionTiming.toolbarHideDuration),
            "minimumReversalShare": .double(MotionTiming.minimumReversalShare),
            "zoomInDuration": .double(MotionTiming.zoomInDuration), "zoomOutDuration": .double(MotionTiming.zoomOutDuration),
            "spotlightFadeInDuration": .double(MotionTiming.spotlightFadeInDuration),
            "spotlightFadeOutDuration": .double(MotionTiming.spotlightFadeOutDuration),
            "spotlightResizeDuration": .double(MotionTiming.spotlightResizeDuration),
            "snapMorphDuration": .double(MotionTiming.snapMorphDuration), "eraseFadeDuration": .double(MotionTiming.eraseFadeDuration),
            "hintFadeInDuration": .double(MotionTiming.hintFadeInDuration), "hintFadeOutDuration": .double(MotionTiming.hintFadeOutDuration),
            "hintTextCrossfadeDuration": .double(MotionTiming.hintTextCrossfadeDuration),
            "hintDwellDuration": .double(MotionTiming.hintDwellDuration),
            "pointerClickDuration": .double(CursorHighlightSettings.clickDuration),
            "toolbarResizeDuration": .double(MotionTiming.toolbarResizeDuration),
            "toolbarCollapseDuration": .double(MotionTiming.toolbarCollapseDuration),
            "toolbarExpandDuration": .double(MotionTiming.toolbarExpandDuration),
            "toolbarMorphOutgoingEnd": .double(ToolbarMorph.outgoingEnd),
            "toolbarMorphIncomingStart": .double(ToolbarMorph.incomingStart),
            "toolbarHoldDelay": .double(ToolbarHold.delay),
            "toolbarHoldSlop": .double(ToolbarHold.slop),
            "laserTrailLifetime": .double(LaserTrail.defaultLifetime),
            "notes": .array([
                "Visibility durations apply to the toolbar and to each shape (WorkspaceLayerMotion in ToolbarSlideMotion.swift). Reversals take max(minimumReversalShare, distance left) of the duration and use the standard curve.",
                "Pop, Drop and Focus drive a pose from VisibilityPose.hidden (timelines.jsonl). The other styles move the layer with enter when appearing and exit when hiding (Slide and Corners along a bent keyframe path). Their opacity uses the fade curve when appearing and linear time when hiding; when the layer also moves, opacity reaches full at 20% of the time when appearing and holds until 72% when hiding (Magic: 15% and 82% with its own flight curve). Port these rules from WorkspaceLayerMotion.",
                "The spotlight scales its fade duration by the share of the change still to go, at least 0.001.",
                "The toolbar's window frame follows MotionCurves.standard when it grows or shrinks sideways and when it morphs into its one-button form and back. During a morph the background's corner radius follows the same curve, the outgoing face fades out by 50% of the time and the incoming face fades in from 35%. Reduce Motion changes the frame at once and keeps only the fades, over the reduced duration.",
                "Magic flights and Dissolve particles keep their own art-directed curves in the app and are not part of this export.",
                "Press and drag or press and hold anywhere on the toolbar: a left press that moves more than toolbarHoldSlop points, or stays within toolbarHoldSlop points for toolbarHoldDelay seconds, picks the toolbar up from where the press started, and the control under the pointer never gets the press. Letting go sooner gives the press to the control. Sliders and number fields get their press at once.",
                "The laser pointer's trail fades over laserTrailLifetime seconds unless the person chose another Fade (laser/constants.json has the range); laser/trails.jsonl has its exact shapes over time.",
                "SwiftUI springs use the published spring model, not SwiftUI's implementation; compare with a tolerance."
            ])
        ])
    }
}
