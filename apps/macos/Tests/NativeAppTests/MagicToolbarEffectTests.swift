import AppKit
import QuartzCore
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class MagicToolbarEffectTests: XCTestCase {
    func testMagicToolbarVanishesWithTheLastShapeInsteadOfWaitingAfterward() {
        for count in [1, 4, 64] {
            let timing = WorkspaceMotionTiming(style: .magic, appearing: false, hasDrawings: true, objectCount: count)
            let lastArrival = timing.drawingsEndDelay + timing.duration
            XCTAssertLessThan(timing.toolbarDelay, lastArrival)
            let fraction = (lastArrival - timing.toolbarDelay) / timing.toolbarDuration
            let progress = fraction * fraction * (3 - 2 * fraction)
            XCTAssertEqual(MagicToolbarEffect.pose(at: progress).imageOpacity, 0)
            XCTAssertLessThan(timing.totalDuration - lastArrival, timing.toolbarDuration / 2)
        }
        for style in [VisibilityAnimationStyle.slide, .corners] {
            let timing = WorkspaceMotionTiming(style: style, appearing: false, hasDrawings: true, objectCount: 4)
            XCTAssertGreaterThan(timing.toolbarDelay, timing.drawingsEndDelay + timing.duration)
        }
    }

    func testCenteredShrinkPointAndPoofHaveClearEndpoints() {
        let visible = MagicToolbarEffect.pose(at: 0)
        XCTAssertEqual(visible.scale, 1)
        XCTAssertEqual(visible.imageOpacity, 1)
        XCTAssertEqual(visible.glowOpacity, 0)
        XCTAssertEqual(visible.sparkOpacity, 0)
        let point = MagicToolbarEffect.pose(at: 0.62)
        XCTAssertLessThan(point.scale, 0.02)
        XCTAssertGreaterThan(point.glowOpacity, 0.9)
        let burst = MagicToolbarEffect.pose(at: 0.8)
        XCTAssertEqual(burst.imageOpacity, 0)
        XCTAssertGreaterThan(burst.radius, 0)
        XCTAssertGreaterThan(burst.sparkOpacity, 0)
        let hidden = MagicToolbarEffect.pose(at: 1)
        XCTAssertEqual(hidden.imageOpacity, 0)
        XCTAssertEqual(hidden.glowOpacity, 0)
        XCTAssertEqual(hidden.sparkOpacity, 0)
    }

    func testReversalInEveryPhaseKeepsTheCenterAndCleansAllEffects() throws {
        let parent = CALayer()
        let image = CALayer()
        image.frame = CGRect(x: 170, y: 170, width: 800, height: 150)
        parent.addSublayer(image)
        let center = image.position
        for phase in [0.2, 0.60, 0.75, 0.95] {
            for appearing in [true, false] {
                let started = CACurrentMediaTime() + 1
                let effect = try MagicToolbarEffect(imageLayer: image, appearing: appearing,
                    started: started, duration: 0.5, progress: phase)
                XCTAssertEqual(effect.progress(at: started), phase)
                XCTAssertEqual(effect.progress(at: started + 0.5), appearing ? 0 : 1)
                XCTAssertEqual(effect.layerCount, MagicToolbarEffect.sparkCount + 1)
                XCTAssertEqual(image.position, center)
                let group = try XCTUnwrap(image.animation(forKey: MagicToolbarEffect.animationKey) as? CAAnimationGroup)
                let scale = try XCTUnwrap(group.animations?.first as? CAKeyframeAnimation)
                let values = try XCTUnwrap(scale.values as? [NSValue]).map(\.caTransform3DValue)
                XCTAssertTrue(values.allSatisfy { $0.m41 == 0 && $0.m42 == 0 && $0.m11 == $0.m22 })
                XCTAssertEqual(values[0].m11, MagicToolbarEffect.pose(at: phase).scale)
                effect.cancel()
                XCTAssertEqual(effect.layerCount, 0)
                XCTAssertEqual(parent.sublayers?.count, 1)
                XCTAssertNil(image.animation(forKey: MagicToolbarEffect.animationKey))
            }
        }
    }
}
