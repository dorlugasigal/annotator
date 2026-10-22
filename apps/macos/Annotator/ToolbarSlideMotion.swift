import AppKit
import CoreImage
import QuartzCore
import AnnotationCore
import MotionCore

enum ToolbarSlideMotion {
    static let showDuration = MotionTiming.toolbarShowDuration
    static let hideDuration = MotionTiming.toolbarHideDuration

    static func resolvedEdge(frame: CGRect, screen: CGRect, orientation: ToolbarOrientation,
                             edge: ToolbarDockEdge) -> ToolbarDockEdge {
        if orientation == .vertical {
            if edge == .left || edge == .right { return edge }
            return frame.midX < screen.midX ? .left : .right
        }
        if edge == .top || edge == .bottom { return edge }
        return frame.midY < screen.midY ? .bottom : .top
    }

    static func offset(edge: ToolbarDockEdge, flipped: Bool) -> CGPoint {
        switch edge {
        case .left: CGPoint(x: -12, y: 0)
        case .right: CGPoint(x: 12, y: 0)
        case .top: CGPoint(x: 0, y: flipped ? -12 : 12)
        default: CGPoint(x: 0, y: flipped ? 12 : -12)
        }
    }

    static func annotationTarget(source: CGRect, toolbar: CGRect, screen: CGRect) -> CGPoint {
        if screen.intersects(toolbar) { return CGPoint(x: toolbar.midX, y: toolbar.midY) }
        return CGPoint(x: screen.minX + 24, y: screen.maxY - 24)
    }

    static func annotationTransform(source: CGRect, target: CGPoint) -> CATransform3D {
        let scale = min(0.12, 24 / max(1, max(source.width, source.height)))
        var transform = CATransform3DMakeScale(scale, scale, 1)
        transform.m41 = target.x - source.midX
        transform.m42 = target.y - source.midY
        return transform
    }

    /// Nearest shapes leave first, so a hide rolls toward its destination. Showing replays the order in reverse.
    static func distanceOrder(_ items: [(id: String, center: CGPoint, target: CGPoint)]) -> [String] {
        items.map { item in (item.id, hypot(item.center.x - item.target.x, item.center.y - item.target.y)) }
            .sorted { $0.1 != $1.1 ? $0.1 < $1.1 : $0.0 < $1.0 }
            .map(\.0)
    }

    /// Arrivals closer than 40 ms share one pulse, and a burst of shapes gets at most four.
    static func pulseTimes(_ arrivals: [CFTimeInterval]) -> [CFTimeInterval] {
        var result: [CFTimeInterval] = []
        for time in arrivals.sorted() where result.last.map({ time - $0 > 0.04 }) ?? true { result.append(time) }
        return Array(result.prefix(4))
    }

    /// 0...1 bump height at `time`; overlapping bumps never stack above one.
    static func pulse(at time: CFTimeInterval, arrivals: [CFTimeInterval], width: Double) -> CGFloat {
        arrivals.reduce(CGFloat(0)) { height, arrival in
            let phase = (time - arrival) / width
            return phase > 0 && phase < 1 ? max(height, sin(.pi * phase)) : height
        }
    }
}

/// Pop, Drop and Soft focus describe a snapshot by how hidden it is (0 at rest, 1 hidden).
enum VisibilityPose {
    struct Pose {
        var transform: CATransform3D
        var opacity: Float
        var blur: CGFloat
    }

    static let stepCount = 30
    static let blurPadding: CGFloat = 36

    static func usesPose(_ style: VisibilityAnimationStyle) -> Bool {
        style == .pop || style == .drop || style == .focus
    }

    /// Extra room around a snapshot so its pose never clips at the transient window edge.
    static func padding(_ style: VisibilityAnimationStyle, size: CGSize) -> CGFloat {
        let longest = max(size.width, size.height)
        switch style {
        case .pop: return 48 + longest * 0.04
        case .drop: return 64 + longest * 0.1
        case .focus: return 48 + longest * 0.04 + blurPadding
        default: return 48
        }
    }

    /// Drop falls below its frame when hiding and arrives from above when showing.
    static func travel(_ style: VisibilityAnimationStyle, frame: CGRect) -> CGRect {
        guard style == .drop else { return frame }
        return frame.union(frame.offsetBy(dx: 0, dy: -122)).union(frame.offsetBy(dx: 0, dy: 60))
    }

    static func pose(_ style: VisibilityAnimationStyle, hidden: CGFloat, appearing: Bool,
                     annotation: Bool, seed: UInt64) -> Pose {
        switch style {
        case .pop:
            let p = max(0, hidden)
            let peak: CGFloat = annotation ? 0.06 : 0.03, low: CGFloat = annotation ? 0.18 : 0.6
            let scale = p <= 0.22 ? 1 + peak * sin(p / 0.22 * .pi / 2) :
                (1 + peak) * exp(log(low / (1 + peak)) * CGFloat(Motion.smooth(Double((p - 0.22) / 0.78))))
            return Pose(transform: CATransform3DMakeScale(scale, scale, 1),
                        opacity: Float(1 - Motion.smooth(Double((p - 0.6) / 0.4))), blur: 0)
        case .drop:
            let side: CGFloat = seed % 2 == 0 ? 1 : -1
            var transform: CATransform3D
            let opacity: Double
            if appearing {
                transform = CATransform3DMakeRotation(annotation ? side * 0.08 * max(0, hidden) : 0, 0, 0, 1)
                transform.m42 = (annotation ? 56 : 20) * hidden
                opacity = 1 - Motion.smooth(Double((hidden - 0.55) / 0.45))
            } else {
                let p = max(0, hidden)
                transform = CATransform3DMakeRotation(annotation ? side * 0.16 * p * p : 0, 0, 0, 1)
                transform = CATransform3DScale(transform, 1 - 0.06 * p, 1 - 0.06 * p, 1)
                transform.m42 = -(annotation ? 110 : 28) * p * p
                opacity = 1 - Motion.smooth(Double((p - 0.3) / 0.7))
            }
            return Pose(transform: transform, opacity: Float(opacity), blur: 0)
        case .focus:
            let p = max(0, hidden)
            let scale = 1 + (annotation ? 0.06 : 0.03) * p
            return Pose(transform: CATransform3DMakeScale(scale, scale, 1),
                        opacity: Float(1 - Motion.smooth(Double((p - 0.15) / 0.85))),
                        blur: (annotation ? 12 : 8) * p)
        default:
            return Pose(transform: CATransform3DIdentity, opacity: hidden >= 1 ? 0 : 1, blur: 0)
        }
    }

    /// Linear time to hidden-progress. Drop's exit is constant acceleration (the pose squares it)
    /// and its entrance lands with one small bounce.
    static func hidden(_ style: VisibilityAnimationStyle, from: CGFloat, to: CGFloat, time: Double,
                       appearing: Bool, reversing: Bool) -> CGFloat {
        let eased: Double
        if reversing { eased = Motion.progress(Motion.standard, at: time) }
        else if style == .drop { eased = appearing ? settle(time) : min(1, max(0, time)) }
        else { eased = Motion.progress(appearing ? Motion.enter : Motion.standard, at: time) }
        return from + (to - from) * CGFloat(eased)
    }

    static func settle(_ time: Double) -> Double { MotionCurves.dropSettle(time) }

    static func mix(_ a: CATransform3D, _ b: CATransform3D, _ t: CGFloat) -> CATransform3D {
        func m(_ x: CGFloat, _ y: CGFloat) -> CGFloat { x + (y - x) * t }
        return CATransform3D(m11: m(a.m11, b.m11), m12: m(a.m12, b.m12), m13: m(a.m13, b.m13), m14: m(a.m14, b.m14),
                             m21: m(a.m21, b.m21), m22: m(a.m22, b.m22), m23: m(a.m23, b.m23), m24: m(a.m24, b.m24),
                             m31: m(a.m31, b.m31), m32: m(a.m32, b.m32), m33: m(a.m33, b.m33), m34: m(a.m34, b.m34),
                             m41: m(a.m41, b.m41), m42: m(a.m42, b.m42), m43: m(a.m43, b.m43), m44: m(a.m44, b.m44))
    }
}

@MainActor
struct WorkspaceMotionTiming {
    let duration: Double
    let toolbarDuration: Double
    let toolbarDelay: Double
    let drawingsDelay: Double
    private let objectCount: Int
    private let appearing: Bool
    private let hasDrawings: Bool
    let objectStep: Double
    var drawingsEndDelay: Double { drawingsDelay + objectStep * Double(max(0, objectCount - 1)) }
    var totalDuration: Double {
        max(toolbarDelay + toolbarDuration, hasDrawings ? drawingsEndDelay + duration : 0)
    }

    init(style: VisibilityAnimationStyle, appearing: Bool, hasDrawings: Bool, objectCount: Int = 1) {
        self.objectCount = objectCount
        self.appearing = appearing
        self.hasDrawings = hasDrawings
        duration = WorkspaceLayerMotion.duration(style, appearing: appearing)
        toolbarDuration = style == .magic ? (appearing ? 0.40 : 0.36) :
            (style == .slide || style == .corners ? (appearing ? 0.30 : 0.24) : duration)
        let lead = hasDrawings && style != .fade && style != .none ? (style == .dissolve ? 0.12 : 0.07) : 0
        objectStep = lead > 0 && objectCount > 1 ? min(0.065, 0.26 / Double(objectCount - 1)) : 0
        if (style == .slide || style == .magic || style == .corners) && hasDrawings {
            let lastArrival = duration + objectStep * Double(max(0, objectCount - 1))
            toolbarDelay = appearing ? 0 : (style == .magic ?
                max(0, lastArrival - toolbarDuration * 0.6) : lastArrival + 0.025)
            drawingsDelay = appearing ? toolbarDuration + 0.035 : 0
        } else {
            toolbarDelay = appearing ? 0 : lead + objectStep * Double(max(0, objectCount - 1))
            drawingsDelay = appearing ? lead : 0
        }
    }

    func drawingDelay(rank: Int) -> Double {
        drawingsDelay + objectStep * Double(appearing ? max(0, objectCount - 1 - rank) : rank)
    }
}

@MainActor
final class WorkspaceLayerMotion {
    struct State {
        var transform: CATransform3D
        var opacity: Float
        var maskLocations: [NSNumber]? = nil
        /// How hidden a pose-driven snapshot is (0 at rest, 1 hidden).
        var progress: Double? = nil
    }

    private struct PoseTimeline {
        let style: VisibilityAnimationStyle
        let from: CGFloat
        let to: CGFloat
        let appearing: Bool
        let reversing: Bool
    }

    static let animationKey = "workspace-visibility"
    static let minimumReversalShare = MotionTiming.minimumReversalShare
    let layer: CALayer
    /// Reversals that have less distance left finish proportionally sooner.
    let effectiveDuration: Double
    let started: CFTimeInterval
    var endTime: CFTimeInterval { started + effectiveDuration }
    private let initialState: State
    private let previousMask: CALayer?
    private var motionMask: CAGradientLayer?
    private weak var blurLayer: CALayer?
    private var poseTimeline: PoseTimeline?
    private(set) var flightTransforms: [CATransform3D] = []

    var currentState: State {
        guard let presented = layer.presentation() else { return initialState }
        var state = State(transform: presented.transform, opacity: presented.opacity,
                          maskLocations: (motionMask?.presentation() as? CAGradientLayer)?.locations ?? initialState.maskLocations)
        if let timeline = poseTimeline {
            let time = effectiveDuration > 0 ? min(1, max(0, (CACurrentMediaTime() - started) / effectiveDuration)) : 1
            state.progress = Double(VisibilityPose.hidden(timeline.style, from: timeline.from, to: timeline.to,
                time: time, appearing: timeline.appearing, reversing: timeline.reversing))
        }
        return state
    }

    static func duration(_ style: VisibilityAnimationStyle, appearing: Bool) -> Double {
        MotionTiming.visibilityDuration(style, appearing: appearing)
    }

    static func maskLocations(_ style: VisibilityAnimationStyle, visible: Bool) -> [NSNumber]? {
        switch style {
        case .reveal: visible ? [0, 1, 1, 1] : [0, 0, 0, 1]
        case .curtain: visible ? [0, 0, 0, 1, 1, 1] : [0, 0.5, 0.5, 0.5, 0.5, 1]
        default: nil
        }
    }

    /// Share of the full travel still left between `current` and `end`, from movement, scale or opacity.
    static func remainingFraction(current: State, end: CATransform3D, endOpacity: Float,
                                  hidden: CATransform3D) -> Double {
        var parts = [Double(abs(endOpacity - current.opacity))]
        let span = hypot(hidden.m41, hidden.m42)
        if span > 1 {
            parts.append(Double(hypot(end.m41 - current.transform.m41, end.m42 - current.transform.m42) / span))
        }
        let scaleSpan = abs(1 - hypot(hidden.m11, hidden.m12))
        if scaleSpan > 0.01 {
            parts.append(Double(abs(hypot(end.m11, end.m12) - hypot(current.transform.m11, current.transform.m12)) / scaleSpan))
        }
        return min(1, parts.max() ?? 1)
    }

    static func contentTransform(style: VisibilityAnimationStyle, bounds: CGRect, progress: CGFloat,
                                 flipped: Bool, anchor: CGPoint, edge: ToolbarDockEdge = .bottom,
                                 annotation: Bool = false) -> CATransform3D {
        let remaining = 1 - min(1, max(0, progress))
        guard remaining > 0, bounds.width > 0, bounds.height > 0 else { return CATransform3DIdentity }
        switch style {
        case .slide, .fluid, .quick, .magic:
            let offset = ToolbarSlideMotion.offset(edge: edge, flipped: flipped)
            let distance = remaining * (style == .quick ? 0.5 : 1)
            return CATransform3DMakeTranslation(offset.x * distance, offset.y * distance, 0)
        case .reveal:
            return CATransform3DIdentity
        case .curtain:
            return CATransform3DIdentity
        case .fold:
            let hinge = (flipped ? 0 : bounds.height) - bounds.height * anchor.y
            var transform = CATransform3DIdentity
            transform.m34 = -1 / max(1200, bounds.height * 4)
            transform = CATransform3DTranslate(transform, 0, hinge, 0)
            let angle: CGFloat = annotation ? .pi / 3 : .pi / 36
            transform = CATransform3DRotate(transform, (flipped ? -1 : 1) * angle * remaining, 1, 0, 0)
            return CATransform3DTranslate(transform, 0, -hinge, 0)
        case .rise, .dissolve:
            return CATransform3DMakeTranslation(0, (flipped ? 1 : -1) * (annotation ? 40 : 10) * remaining, 0)
        case .fade, .corners, .pop, .drop, .focus, .none:
            return CATransform3DIdentity
        }
    }

    init(layer: CALayer, style: VisibilityAnimationStyle, appearing: Bool, flipped: Bool,
         duration: Double, initial: State? = nil, started: CFTimeInterval = CACurrentMediaTime(),
         edge: ToolbarDockEdge = .bottom, hiddenTransform: CATransform3D? = nil, annotation: Bool = false,
         magicSeed: UInt64? = nil, curveSign: CGFloat = 1, poseSeed: UInt64? = nil) {
        self.layer = layer
        self.started = started
        previousMask = layer.mask
        let reversing = initial != nil
        let restingOpacity = layer.opacity
        if let poseSeed, VisibilityPose.usesPose(style) {
            let from = initial.map { state in CGFloat(state.progress ?? Double(1 - state.opacity)) } ?? (appearing ? 1 : 0)
            let to: CGFloat = appearing ? 0 : 1
            let timeline = PoseTimeline(style: style, from: from, to: to, appearing: appearing, reversing: reversing)
            let start = VisibilityPose.pose(style, hidden: from, appearing: appearing, annotation: annotation, seed: poseSeed)
            initialState = initial ?? State(transform: start.transform, opacity: start.opacity * restingOpacity,
                                            progress: Double(from))
            effectiveDuration = reversing ? duration * max(Self.minimumReversalShare, Double(abs(to - from))) : duration
            poseTimeline = timeline
            var transforms: [NSValue] = [], opacities: [NSNumber] = [], blurs: [NSNumber] = []
            let steps = VisibilityPose.stepCount
            for step in 0...steps {
                let time = Double(step) / Double(steps)
                let hidden = step == steps ? to : VisibilityPose.hidden(style, from: from, to: to, time: time,
                    appearing: appearing, reversing: reversing)
                var pose = VisibilityPose.pose(style, hidden: hidden, appearing: appearing, annotation: annotation, seed: poseSeed)
                pose.opacity *= restingOpacity
                if let initial, time < 0.35 {
                    // Blend out of the interrupted pose so a reversal never jumps.
                    let weight = CGFloat(Motion.smooth(time / 0.35))
                    pose.transform = VisibilityPose.mix(initial.transform, pose.transform, weight)
                    pose.opacity = initial.opacity + (pose.opacity - initial.opacity) * Float(weight)
                }
                transforms.append(NSValue(caTransform3D: pose.transform))
                opacities.append(NSNumber(value: pose.opacity))
                blurs.append(NSNumber(value: Double(pose.blur)))
            }
            let keyTimes = (0...steps).map { NSNumber(value: Double($0) / Double(steps)) }
            let movement = CAKeyframeAnimation(keyPath: "transform")
            movement.values = transforms
            movement.keyTimes = keyTimes
            let fade = CAKeyframeAnimation(keyPath: "opacity")
            fade.values = opacities
            fade.keyTimes = keyTimes
            Self.install([movement, fade], on: layer, duration: effectiveDuration, started: started)
            if style == .focus, let container = layer.superlayer,
               container.filters?.contains(where: { ($0 as? CIFilter)?.name == VisibilityImageView.blurFilterName }) == true {
                let blur = CAKeyframeAnimation(keyPath: "filters.\(VisibilityImageView.blurFilterName).inputRadius")
                blur.values = blurs
                blur.keyTimes = keyTimes
                blur.duration = effectiveDuration
                blur.beginTime = container.convertTime(started, from: nil)
                blur.fillMode = .both
                blur.isRemovedOnCompletion = false
                container.add(blur, forKey: Self.animationKey)
                blurLayer = container
            }
            return
        }
        let hidden = hiddenTransform ?? Self.contentTransform(style: style, bounds: layer.bounds, progress: 0,
            flipped: flipped, anchor: layer.anchorPoint, edge: edge, annotation: annotation)
        initialState = initial ?? State(transform: appearing ? hidden : CATransform3DIdentity,
            opacity: appearing ? 0 : layer.opacity, maskLocations: Self.maskLocations(style, visible: !appearing))
        let end = appearing ? CATransform3DIdentity : hidden
        let endOpacity = appearing ? layer.opacity : Float(0)
        effectiveDuration = reversing ? duration * max(Self.minimumReversalShare,
            Self.remainingFraction(current: initialState, end: end, endOpacity: endOpacity, hidden: hidden)) : duration
        let curve = reversing ? Motion.standard : (appearing ? Motion.enter : Motion.exit)
        let movement: CAPropertyAnimation
        if let magicSeed {
            flightTransforms = MagicFlight.transforms(from: initialState.transform, to: end, seed: magicSeed)
            let flight = CAKeyframeAnimation(keyPath: "transform")
            flight.values = flightTransforms.map { NSValue(caTransform3D: $0) }
            flight.keyTimes = flightTransforms.indices.map { NSNumber(value: Double($0) / Double(flightTransforms.count - 1)) }
            flight.timingFunction = MagicFlight.timingFunction
            movement = flight
        } else if (style == .slide || style == .corners), hiddenTransform != nil {
            let path = CAKeyframeAnimation(keyPath: "transform")
            let from = initialState.transform
            let dx = end.m41 - from.m41, dy = end.m42 - from.m42
            let length = max(1, hypot(dx, dy))
            let bend = style == .corners ? min(28, length * 0.06) * curveSign : min(40, length * 0.12)
            let startScale = max(0.001, from.m11), endScale = max(0.001, end.m11)
            path.values = (0...24).map { step in
                let t = CGFloat(step) / 24
                let arc = sin(t * .pi) * bend
                // Shapes keep their size early in a hide and grow early in a show; log space keeps the shrink even.
                let weight = appearing ? Motion.smooth(Double(t) / 0.8) : Motion.smooth((Double(t) - 0.2) / 0.8)
                let scale = weight <= 0 ? startScale : (weight >= 1 ? endScale :
                    exp(log(startScale) + (log(endScale) - log(startScale)) * CGFloat(weight)))
                var value = CATransform3DMakeScale(scale, scale, 1)
                value.m41 = from.m41 + dx * t - dy / length * arc
                value.m42 = from.m42 + dy * t + dx / length * arc
                return NSValue(caTransform3D: value)
            }
            path.timingFunction = curve
            movement = path
        } else {
            let straight = CABasicAnimation(keyPath: "transform")
            straight.fromValue = NSValue(caTransform3D: initialState.transform)
            straight.toValue = NSValue(caTransform3D: end)
            straight.timingFunction = curve
            movement = straight
        }
        let opacity: CAPropertyAnimation
        if hiddenTransform != nil {
            let fade = CAKeyframeAnimation(keyPath: "opacity")
            fade.values = appearing ? [initialState.opacity, layer.opacity, layer.opacity] :
                [initialState.opacity, initialState.opacity, Float(0)]
            if magicSeed != nil {
                fade.keyTimes = appearing ? [0, 0.15, 1] : [0, 0.82, 1]
                fade.timingFunction = MagicFlight.timingFunction
            } else {
                fade.keyTimes = appearing ? [0, 0.2, 1] : [0, 0.72, 1]
                fade.timingFunction = appearing ? Motion.fade : CAMediaTimingFunction(name: .linear)
            }
            opacity = fade
        } else {
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = initialState.opacity
            fade.toValue = endOpacity
            fade.timingFunction = appearing ? Motion.fade : CAMediaTimingFunction(name: .linear)
            opacity = fade
        }
        Self.install([movement, opacity], on: layer, duration: effectiveDuration, started: started)
        if style == .reveal || style == .curtain {
            let mask = CAGradientLayer()
            mask.frame = layer.bounds
            mask.startPoint = CGPoint(x: 0, y: 0.5)
            mask.endPoint = CGPoint(x: 1, y: 0.5)
            let clear = NSColor.clear.cgColor, solid = NSColor.black.cgColor
            mask.colors = style == .reveal ? [solid, solid, clear, clear] :
                [clear, clear, solid, solid, clear, clear]
            let hiddenLocations: [NSNumber] = style == .reveal ? [0, 0, 0, 1] : [0, 0.5, 0.5, 0.5, 0.5, 1]
            let visibleLocations: [NSNumber] = style == .reveal ? [0, 1, 1, 1] : [0, 0, 0, 1, 1, 1]
            mask.locations = visibleLocations
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            layer.mask = mask
            CATransaction.commit()
            motionMask = mask
            let wipe = CAKeyframeAnimation(keyPath: "locations")
            let start: [NSNumber]
            if let locations = initial?.maskLocations, locations.count == hiddenLocations.count {
                start = locations
            } else {
                start = appearing ? hiddenLocations : visibleLocations
            }
            let finish = appearing ? visibleLocations : hiddenLocations
            var middle = zip(start, finish).map { ($0.doubleValue + $1.doubleValue) / 2 }
            middle[1] = max(0, middle[1] - 0.06)
            middle[2] = min(1, middle[2] + 0.06)
            if style == .curtain {
                middle[3] = max(middle[2], middle[3] - 0.06)
                middle[4] = min(1, middle[4] + 0.06)
            }
            wipe.values = [start, middle.map { NSNumber(value: $0) }, finish]
            wipe.keyTimes = [0, 0.5, 1]
            wipe.duration = effectiveDuration
            wipe.beginTime = layer.convertTime(started, from: nil)
            wipe.timingFunction = curve
            wipe.fillMode = .both
            wipe.isRemovedOnCompletion = false
            mask.add(wipe, forKey: Self.animationKey)
        }
    }

    private static func install(_ animations: [CAPropertyAnimation], on layer: CALayer,
                                duration: Double, started: CFTimeInterval) {
        let group = CAAnimationGroup()
        for animation in animations { animation.duration = duration }
        group.animations = animations
        group.duration = duration
        group.beginTime = layer.convertTime(started, from: nil)
        // Each movement and opacity animation carries its own curve.
        group.timingFunction = CAMediaTimingFunction(name: .linear)
        group.fillMode = .both
        group.isRemovedOnCompletion = false
        layer.add(group, forKey: animationKey)
    }

    func cancel() {
        layer.removeAnimation(forKey: Self.animationKey)
        blurLayer?.removeAnimation(forKey: Self.animationKey)
        blurLayer = nil
        if let motionMask, layer.mask === motionMask {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            layer.mask = previousMask
            CATransaction.commit()
        }
        motionMask = nil
    }
}
