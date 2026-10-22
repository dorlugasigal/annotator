import AppKit
import QuartzCore
import SwiftUI
import MotionCore

/// Native forms of the shared motion values. The curves, durations and springs themselves live in
/// MotionCore, which is platform-neutral and feeds the Windows conformance corpus. Magic flights and
/// Dissolve particles keep their own art-directed curves.
enum Motion {
    // MARK: SwiftUI

    /// Selection, toggles and small state changes.
    static let snappy = spring(MotionTiming.snappy)
    /// Layout and panel size changes.
    static let smooth = spring(MotionTiming.smooth)
    /// Confirmation bounces only, such as a newly assigned key.
    static let playful = spring(MotionTiming.playful)
    static let fadeIn = Animation.easeOut(duration: MotionTiming.fadeIn.duration)
    static let fadeOut = Animation.easeIn(duration: MotionTiming.fadeOut.duration)
    /// The replacement for movement when macOS Reduce Motion is on.
    static let reduced = Animation.easeOut(duration: MotionTiming.reduced.duration)

    static func resolve(_ animation: Animation, reduceMotion: Bool) -> Animation {
        reduceMotion ? reduced : animation
    }

    static var systemReducesMotion: Bool { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }

    private static func spring(_ spring: SpringMotion) -> Animation {
        .spring(response: spring.response, dampingFraction: spring.dampingFraction)
    }

    // MARK: Core Animation

    /// Entrances: quick start, long gentle settle that fills the whole duration.
    static var enter: CAMediaTimingFunction { timing(MotionCurves.enter) }
    /// Exits: starts moving visibly early and still speeds up into the target, without a last-frame snap.
    static var exit: CAMediaTimingFunction { timing(MotionCurves.exit) }
    /// Reversals and two-way changes: the object turns around without a hitch.
    static var standard: CAMediaTimingFunction { timing(MotionCurves.standard) }
    /// Opacity-only changes. Perceived brightness follows this more evenly than the movement curves.
    static var fade: CAMediaTimingFunction { timing(MotionCurves.fade) }

    static func timing(_ curve: CubicBezier) -> CAMediaTimingFunction {
        CAMediaTimingFunction(controlPoints: Float(curve.x1), Float(curve.y1), Float(curve.x2), Float(curve.y2))
    }

    /// Eased progress of `function` at linear time `x` in 0...1.
    static func progress(_ function: CAMediaTimingFunction, at x: Double) -> Double {
        var first = [Float](repeating: 0, count: 2), second = [Float](repeating: 0, count: 2)
        function.getControlPoint(at: 1, values: &first)
        function.getControlPoint(at: 2, values: &second)
        return cubic(x, Double(first[0]), Double(first[1]), Double(second[0]), Double(second[1]))
    }

    static func cubic(_ x: Double, _ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double) -> Double {
        MotionCurves.cubic(x, x1, y1, x2, y2)
    }

    static func smooth(_ value: Double) -> Double { MotionCurves.smooth(value) }
}

/// A display-synchronized callback for short, owned animations. It never idles:
/// the callback returns `false` when its animation has finished, which stops the link.
@MainActor
final class MotionTicker: NSObject {
    private var link: CADisplayLink?
    private var tick: (@MainActor (CFTimeInterval) -> Bool)?
    var isRunning: Bool { link != nil }

    /// Starts calling `tick` once per display frame. Without a display the callback runs once
    /// with a time far in the future so the animation lands on its final state.
    func start(view: NSView?, _ tick: @escaping @MainActor (CFTimeInterval) -> Bool) {
        stop()
        self.tick = tick
        let link = view?.window != nil ? view?.displayLink(target: self, selector: #selector(step(_:))) :
            NSScreen.main?.displayLink(target: self, selector: #selector(step(_:)))
        guard let link else {
            _ = tick(CACurrentMediaTime() + 3600)
            self.tick = nil
            return
        }
        self.link = link
        link.add(to: .main, forMode: .common)
        if !tick(CACurrentMediaTime()) { stop() }
    }

    func stop() {
        link?.invalidate()
        link = nil
        tick = nil
    }

    @objc private func step(_ link: CADisplayLink) {
        guard let tick else { stop(); return }
        if !tick(CACurrentMediaTime()) { stop() }
    }
}
