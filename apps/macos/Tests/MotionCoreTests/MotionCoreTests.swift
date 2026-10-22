import Foundation
import Testing
import AnnotationCore
@testable import MotionCore

/// An independent solver: 200 bisection steps, far beyond the app's 40.
private func reference(_ curve: CubicBezier, _ x: Double) -> Double {
    guard x > 0 else { return 0 }
    guard x < 1 else { return 1 }
    func bezier(_ t: Double, _ a: Double, _ b: Double) -> Double {
        let u = 1 - t
        return 3 * u * u * t * a + 3 * u * t * t * b + t * t * t
    }
    var low = 0.0, high = 1.0
    for _ in 0..<200 {
        let middle = (low + high) / 2
        if bezier(middle, curve.x1, curve.x2) < x { low = middle } else { high = middle }
    }
    return bezier((low + high) / 2, curve.y1, curve.y2)
}

@Test func appCurvesKeepTheirControlPoints() {
    #expect(MotionCurves.enter == CubicBezier(0.2, 0, 0, 1))
    #expect(MotionCurves.exit == CubicBezier(0.45, 0, 0.55, 0.4))
    #expect(MotionCurves.standard == CubicBezier(0.3, 0, 0.2, 1))
    #expect(MotionCurves.fade == CubicBezier(0.2, 0, 0.4, 1))
}

@Test func curvesStartAndEndExactlyAndNeverGoBackwards() {
    for (name, curve) in MotionCurves.named {
        #expect(curve.progress(at: 0) == 0, "\(name)")
        #expect(curve.progress(at: 1) == 1, "\(name)")
        #expect(curve.progress(at: -0.5) == 0, "\(name)")
        #expect(curve.progress(at: 1.5) == 1, "\(name)")
        var previous = 0.0
        for millisecond in 0...1000 {
            let value = curve.progress(at: Double(millisecond) / 1000)
            #expect(value >= previous - 1e-12, "\(name) at \(millisecond) ms")
            previous = value
        }
    }
}

@Test func solverMatchesAnIndependentSolverClosely() {
    for (name, curve) in MotionCurves.named {
        for step in 1..<1000 {
            let x = Double(step) / 1000
            #expect(abs(curve.progress(at: x) - reference(curve, x)) < 1e-9, "\(name) at \(x)")
        }
    }
    #expect(abs(MotionCurves.linear.progress(at: 0.25) - 0.25) < 1e-9)
}

@Test func smoothstepIsSymmetricAndClamped() {
    #expect(MotionCurves.smooth(-1) == 0)
    #expect(MotionCurves.smooth(0) == 0)
    #expect(MotionCurves.smooth(0.5) == 0.5)
    #expect(MotionCurves.smooth(1) == 1)
    #expect(MotionCurves.smooth(2) == 1)
    #expect(abs(MotionCurves.smooth(0.25) + MotionCurves.smooth(0.75) - 1) < 1e-15)
}

@Test func springsStartAtRestAndSettleWithTheExpectedCharacter() {
    for (name, spring) in MotionTiming.springs {
        #expect(spring.value(at: 0) == 0, "\(name)")
        #expect(abs(spring.value(at: 3) - 1) < 1e-6, "\(name)")
        #expect(spring.settleTime() > 0.1 && spring.settleTime() < 1.5, "\(name)")
    }
    let peak = (0...1000).map { MotionTiming.playful.value(at: Double($0) / 1000) }.max() ?? 0
    #expect(peak > 1.05, "Playful bounces past its target.")
    let snappyPeak = (0...1000).map { MotionTiming.snappy.value(at: Double($0) / 1000) }.max() ?? 0
    #expect(snappyPeak < 1.01, "Snappy barely overshoots.")
    #expect(SpringMotion(response: 0.3, dampingFraction: 1).value(at: 0.15) > 0)
    #expect(SpringMotion(response: 0.3, dampingFraction: 1.4).value(at: 2) > 0.999)
}

@Test func visibilityDurationsMatchTheMotionSpecification() {
    let expected: [(VisibilityAnimationStyle, Double, Double)] = [
        (.none, 0, 0), (.fade, 0.16, 0.20), (.dissolve, 0.55, 0.65), (.magic, 0.54, 0.62),
        (.corners, 0.30, 0.38), (.pop, 0.26, 0.34), (.drop, 0.32, 0.40), (.focus, 0.28, 0.36),
        (.slide, 0.28, 0.36), (.quick, 0.14, 0.18)
    ]
    for (style, hide, show) in expected {
        #expect(MotionTiming.visibilityDuration(style, appearing: false) == hide, "\(style) hide")
        #expect(MotionTiming.visibilityDuration(style, appearing: true) == show, "\(style) show")
    }
    #expect(MotionTiming.toolbarShowDuration == 0.36 && MotionTiming.toolbarHideDuration == 0.28)
}

@Test func dropSettleLandsWithOneSmallBounce() {
    #expect(MotionCurves.dropSettle(0) == 0 && MotionCurves.dropSettle(-1) == 0)
    #expect(MotionCurves.dropSettle(1) == 1 && MotionCurves.dropSettle(2) == 1)
    let samples = (0...1000).map { MotionCurves.dropSettle(Double($0) / 1000) }
    let peak = samples.max() ?? 0
    #expect(peak > 1.16 && peak < 1.17, "The entrance overshoots by about 17% once, at 27% of the time.")
    #expect(abs(samples[999] - 1) < 0.01, "It has settled before the last frame.")
}
