import Foundation
import AnnotationCore

/// A cubic Bézier timing curve from (0, 0) to (1, 1) with two control points, as Core Animation's
/// `CAMediaTimingFunction(controlPoints:)` and CSS `cubic-bezier()` define it.
public struct CubicBezier: Codable, Equatable, Hashable, Sendable {
    public let x1: Double
    public let y1: Double
    public let x2: Double
    public let y2: Double

    public init(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double) {
        self.x1 = x1; self.y1 = y1; self.x2 = x2; self.y2 = y2
    }

    /// Eased progress at linear time `x` in 0...1. Times outside 0...1 clamp to the endpoints.
    public func progress(at x: Double) -> Double {
        MotionCurves.cubic(x, x1, y1, x2, y2)
    }
}

/// Curves shared by the app's native motion. Magic flights and Dissolve particles keep their own
/// art-directed curves.
public enum MotionCurves {
    /// Entrances: quick start, long gentle settle that fills the whole duration.
    public static let enter = CubicBezier(0.2, 0, 0, 1)
    /// Exits: starts moving visibly early and still speeds up into the target, without a last-frame snap.
    public static let exit = CubicBezier(0.45, 0, 0.55, 0.4)
    /// Reversals and two-way changes: the object turns around without a hitch.
    public static let standard = CubicBezier(0.3, 0, 0.2, 1)
    /// Opacity-only changes. Perceived brightness follows this more evenly than the movement curves.
    public static let fade = CubicBezier(0.2, 0, 0.4, 1)

    /// Core Animation's named curves, which some timelines and SwiftUI's eased animations use.
    public static let linear = CubicBezier(0, 0, 1, 1)
    public static let easeIn = CubicBezier(0.42, 0, 1, 1)
    public static let easeOut = CubicBezier(0, 0, 0.58, 1)
    public static let easeInEaseOut = CubicBezier(0.42, 0, 0.58, 1)

    public static let named: [(name: String, curve: CubicBezier)] = [
        ("enter", enter), ("exit", exit), ("standard", standard), ("fade", fade),
        ("linear", linear), ("easeIn", easeIn), ("easeOut", easeOut), ("easeInEaseOut", easeInEaseOut)
    ]

    /// Solves the curve for `x` by 40 bisection steps on the parameter, then evaluates y.
    public static func cubic(_ x: Double, _ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double) -> Double {
        guard x > 0 else { return 0 }
        guard x < 1 else { return 1 }
        func sample(_ t: Double, _ a: Double, _ b: Double) -> Double {
            3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t
        }
        var low = 0.0, high = 1.0
        for _ in 0..<40 {
            let middle = (low + high) / 2
            if sample(middle, x1, x2) < x { low = middle } else { high = middle }
        }
        return sample((low + high) / 2, y1, y2)
    }

    /// Smoothstep: 3t² − 2t³ with t clamped to 0...1.
    public static func smooth(_ value: Double) -> Double {
        let t = min(1, max(0, value))
        return t * t * (3 - 2 * t)
    }

    /// The Drop style's entrance: lands with one small bounce, 1 − e^(−6t) cos(9.5t), clamped to 0 and 1
    /// outside 0...1. Drop's exit is linear in time; the pose squares it into constant acceleration.
    public static func dropSettle(_ time: Double) -> Double {
        time >= 1 ? 1 : (time <= 0 ? 0 : 1 - exp(-6 * time) * cos(9.5 * time))
    }
}

/// A spring described as SwiftUI's `spring(response:dampingFraction:)`.
public struct SpringMotion: Codable, Equatable, Sendable {
    public let response: Double
    public let dampingFraction: Double

    public init(response: Double, dampingFraction: Double) {
        self.response = response; self.dampingFraction = dampingFraction
    }

    /// Unit step response at `time` seconds for a spring with mass 1, stiffness (2π / response)²
    /// and damping 4π × dampingFraction / response, starting at rest at 0 and settling at 1.
    /// This is the published model of SwiftUI's spring, not SwiftUI's own implementation.
    public func value(at time: Double) -> Double {
        guard time > 0 else { return 0 }
        let omega = 2 * Double.pi / response
        let zeta = dampingFraction
        if zeta < 1 {
            let damped = omega * (1 - zeta * zeta).squareRoot()
            return 1 - exp(-zeta * omega * time) * (cos(damped * time) + zeta * omega / damped * sin(damped * time))
        }
        if zeta == 1 { return 1 - exp(-omega * time) * (1 + omega * time) }
        let root = omega * (zeta * zeta - 1).squareRoot()
        let fast = -zeta * omega - root, slow = -zeta * omega + root
        return 1 - (fast * exp(slow * time) - slow * exp(fast * time)) / (fast - slow)
    }

    /// The first whole millisecond after which the response stays within `tolerance` of 1.
    public func settleTime(tolerance: Double = 0.001, limit: Double = 5) -> Double {
        var settled = limit
        var millisecond = Int(limit * 1000)
        while millisecond >= 0 {
            if abs(1 - value(at: Double(millisecond) / 1000)) > tolerance { break }
            settled = Double(millisecond) / 1000
            millisecond -= 1
        }
        return settled
    }
}

/// An eased animation with a fixed duration.
public struct TimedMotion: Codable, Equatable, Sendable {
    public let duration: Double
    public let curve: CubicBezier

    public init(duration: Double, curve: CubicBezier) {
        self.duration = duration; self.curve = curve
    }
}

/// Durations and presets shared by the app's native motion.
public enum MotionTiming {
    /// Selection, toggles and small state changes.
    public static let snappy = SpringMotion(response: 0.28, dampingFraction: 0.86)
    /// Layout and panel size changes.
    public static let smooth = SpringMotion(response: 0.38, dampingFraction: 0.9)
    /// Confirmation bounces only, such as a newly assigned key.
    public static let playful = SpringMotion(response: 0.3, dampingFraction: 0.62)
    public static let fadeIn = TimedMotion(duration: 0.16, curve: MotionCurves.easeOut)
    public static let fadeOut = TimedMotion(duration: 0.12, curve: MotionCurves.easeIn)
    /// The replacement for movement when Reduce Motion is on.
    public static let reduced = TimedMotion(duration: 0.12, curve: MotionCurves.easeOut)

    public static let springs: [(name: String, spring: SpringMotion)] = [
        ("snappy", snappy), ("smooth", smooth), ("playful", playful)
    ]
    public static let timed: [(name: String, motion: TimedMotion)] = [
        ("fadeIn", fadeIn), ("fadeOut", fadeOut), ("reduced", reduced)
    ]

    /// The toolbar's show and hide durations for styles without their own.
    public static let toolbarShowDuration = 0.36
    public static let toolbarHideDuration = 0.28
    /// A show or hide that reverses mid-way takes this share of the full duration at least,
    /// otherwise the share of the distance left.
    public static let minimumReversalShare = 0.35

    /// Screen zoom ramps: zooming in uses `MotionCurves.enter`, zooming out to 1x uses `MotionCurves.standard`.
    public static let zoomInDuration = 0.26
    public static let zoomOutDuration = 0.2
    /// Spotlight dimming uses `MotionCurves.fade`; each fade duration is scaled by the share of the change
    /// still to go, at least 0.001. A radius change uses `MotionCurves.standard` over the full resize duration.
    public static let spotlightFadeInDuration = 0.18
    public static let spotlightFadeOutDuration = 0.14
    public static let spotlightResizeDuration = 0.12
    /// Smart Draw's morph from ink to shape uses `MotionCurves.enter`; the shape fades in with smoothstep
    /// from 55% of the time.
    public static let snapMorphDuration = 0.22
    /// An erased object fades with `MotionCurves.fade` and, when it scales, shrinks 3% with `MotionCurves.exit`.
    public static let eraseFadeDuration = 0.12
    /// Workspace hints fade in (Core Animation's `fade` curve) and out, and cross-fade their text.
    public static let hintFadeInDuration = 0.16
    public static let hintFadeOutDuration = 0.12
    public static let hintTextCrossfadeDuration = 0.14
    /// How long the on-screen tip stays after it wakes, before it fades out.
    public static let hintDwellDuration = 5.0
    /// The toolbar grows or shrinks sideways to fit a tool's properties, with `MotionCurves.standard`.
    public static let toolbarResizeDuration = 0.22
    /// The toolbar shrinks into one button, and grows back, with `MotionCurves.standard`. The outgoing face fades
    /// out and the incoming face fades in as `ToolbarMorph` describes.
    public static let toolbarCollapseDuration = 0.3
    public static let toolbarExpandDuration = 0.34
    /// Resting the pointer on the one-button toolbar this long opens it.
    public static let toolbarExpandHoverDelay = 0.25

    /// Seconds for one shape's or the toolbar's show (`appearing`) or hide in each workspace style.
    public static func visibilityDuration(_ style: VisibilityAnimationStyle, appearing: Bool) -> Double {
        switch style {
        case .none: 0
        case .quick: appearing ? 0.18 : 0.14
        case .fade: appearing ? 0.20 : 0.16
        case .dissolve: appearing ? 0.65 : 0.55
        case .magic: appearing ? 0.62 : 0.54
        case .corners: appearing ? 0.38 : 0.30
        case .pop: appearing ? 0.34 : 0.26
        case .drop: appearing ? 0.40 : 0.32
        case .focus: appearing ? 0.36 : 0.28
        default: appearing ? toolbarShowDuration : toolbarHideDuration
        }
    }
}

/// The toolbar's change between its full face and its one-button face: the background's frame and corner radius
/// follow `MotionCurves.standard`, while the faces cross-fade so the old one is gone before the new one settles.
public enum ToolbarMorph {
    /// The share of the morph after which the outgoing face is gone.
    public static let outgoingEnd = 0.5
    /// The share of the morph after which the incoming face starts to appear.
    public static let incomingStart = 0.35

    /// Progress of the background's frame and corner radius at linear time `t` in 0...1.
    public static func shape(_ t: Double) -> Double { MotionCurves.standard.progress(at: t) }

    /// Opacity of the face that is leaving at linear time `t` in 0...1.
    public static func outgoingOpacity(_ t: Double) -> Double {
        1 - MotionCurves.fade.progress(at: t / outgoingEnd)
    }

    /// Opacity of the face that is arriving at linear time `t` in 0...1.
    public static func incomingOpacity(_ t: Double) -> Double {
        MotionCurves.fade.progress(at: (t - incomingStart) / (1 - incomingStart))
    }
}
