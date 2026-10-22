import AppKit
import QuartzCore
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class MagicFlightTests: XCTestCase {
    func testFlightsSwoopCalmlyWithExactEndpointsInBothDirections() throws {
        var dock = CATransform3DMakeScale(0.04, 0.04, 1)
        dock.m41 = 700
        for seed: UInt64 in [11, 22, 33, UInt64.max] {
            for (from, to) in [(CATransform3DIdentity, dock), (dock, CATransform3DIdentity)] {
                let frames = MagicFlight.transforms(from: from, to: to, seed: seed)
                XCTAssertEqual(frames.count, MagicFlight.steps + 1)
                XCTAssertEqual(NSValue(caTransform3D: try XCTUnwrap(frames.first)), NSValue(caTransform3D: from))
                XCTAssertEqual(NSValue(caTransform3D: try XCTUnwrap(frames.last)), NSValue(caTransform3D: to))
                let signs = frames.compactMap { abs($0.m42) < 0.01 ? nil : ($0.m42 > 0 ? 1 : -1) }
                let bends = zip(signs, signs.dropFirst()).filter { $0 != $1 }.count
                XCTAssertGreaterThanOrEqual(bends, 1, "Magic must curve, not fly straight.")
                XCTAssertLessThanOrEqual(bends, 3, "Magic swoops once or twice; it must not wobble.")
                let lastQuarter = frames[(MagicFlight.steps * 3 / 4)...]
                let early = frames[..<(MagicFlight.steps / 2)].map { abs($0.m42) }.max() ?? 0
                XCTAssertLessThan(lastQuarter.map { abs($0.m42) }.max() ?? 0, early,
                                  "The swoop is strongest early and settles before landing.")
                for frame in frames {
                    XCTAssertTrue(frame.m41.isFinite && frame.m42.isFinite)
                    XCTAssertLessThanOrEqual(abs(frame.m42), MagicFlight.maximumBend + 0.001)
                    XCTAssertLessThanOrEqual(abs(atan2(frame.m12, frame.m11)), MagicFlight.maximumBank + 0.001)
                }
            }
        }
    }

    func testMagicIsFasterThanTheOriginalFlight() {
        for appearing in [true, false] {
            XCTAssertLessThanOrEqual(WorkspaceLayerMotion.duration(.magic, appearing: appearing), 0.62)
            let timing = WorkspaceMotionTiming(style: .magic, appearing: appearing, hasDrawings: true)
            XCTAssertLessThanOrEqual(timing.toolbarDuration, 0.4)
        }
    }

    func testRoutesAreSeededAndInterruptedFlightsStartAtTheirCurrentTransform() {
        var dock = CATransform3DMakeScale(0.05, 0.05, 1)
        dock.m41 = -600; dock.m42 = 300
        let seed = MagicFlight.seed(for: "shape-a")
        let first = MagicFlight.transforms(from: CATransform3DIdentity, to: dock, seed: seed)
        let repeated = MagicFlight.transforms(from: CATransform3DIdentity, to: dock, seed: seed)
        let other = MagicFlight.transforms(from: CATransform3DIdentity, to: dock, seed: MagicFlight.seed(for: "shape-b"))
        XCTAssertEqual(first.map { NSValue(caTransform3D: $0) }, repeated.map { NSValue(caTransform3D: $0) })
        XCTAssertNotEqual(first.map { NSValue(caTransform3D: $0) }, other.map { NSValue(caTransform3D: $0) })
        let current = first[37]
        let reversed = MagicFlight.transforms(from: current, to: CATransform3DIdentity, seed: seed)
        XCTAssertEqual(NSValue(caTransform3D: reversed[0]), NSValue(caTransform3D: current))
        XCTAssertTrue(CATransform3DIsIdentity(reversed[MagicFlight.steps]))
    }

    func testTrailsShareTheFlightTimelineAndLeaveNoLayersBehind() throws {
        let parent = CALayer()
        parent.bounds = CGRect(x: 0, y: 0, width: 1200, height: 800)
        let image = CALayer()
        image.frame = CGRect(x: 200, y: 200, width: 100, height: 80)
        parent.addSublayer(image)
        var dock = CATransform3DMakeScale(0.05, 0.05, 1)
        dock.m41 = 500; dock.m42 = 100
        let started = CACurrentMediaTime() + 0.1
        let motion = WorkspaceLayerMotion(layer: image, style: .magic, appearing: false, flipped: false,
            duration: 0.75, started: started, hiddenTransform: dock, magicSeed: 42)
        let trail = try MagicTrail(imageLayer: image, transforms: motion.flightTransforms, seed: 42,
                                   started: started, duration: 0.75)
        XCTAssertEqual(trail.layerCount, 9)
        XCTAssertEqual(parent.sublayers?.count, 10)
        let flight = try XCTUnwrap(image.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
        for layer in parent.sublayers ?? [] where layer !== image {
            XCTAssertEqual(layer.opacity, 0)
            let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
            XCTAssertEqual(group.beginTime, flight.beginTime, accuracy: 0.0001)
            XCTAssertEqual(group.duration, flight.duration)
            let fade = try XCTUnwrap(group.animations?.last as? CAKeyframeAnimation)
            XCTAssertEqual((fade.values?.last as? NSNumber)?.doubleValue, 0)
            XCTAssertEqual(group.repeatCount, 0)
        }
        trail.cancel()
        motion.cancel()
        XCTAssertEqual(parent.sublayers?.count, 1)
        XCTAssertEqual(trail.layerCount, 0)
    }

    func testMagicLifecycleCleansUpEffectsAndPreservesTheDrawing() async throws {
        let suite = "magic-lifecycle-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .ellipse, frame: Rect(x: 200, y: 250, width: 120, height: 80))
        try session.document.add(shape)
        let generation = session.document.generation
        let toolbar = NSPanel(contentRect: CGRect(x: 150, y: 150, width: 600, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        var reduced = false
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { reduced })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        model.setAnimationStyle(.magic)
        animator.hide(toolbar: toolbar, model: model) {}
        XCTAssertEqual(animator.magicEffectLayerCount, 9 + MagicToolbarEffect.sparkCount + 1)
        animator.show(toolbar: toolbar, model: model) {}
        try await Task.sleep(for: .seconds(0.9))
        XCTAssertFalse(animator.hasActiveTimeline)
        XCTAssertEqual(animator.magicEffectLayerCount, 0)
        XCTAssertEqual(animator.transientPanelCount, 0)
        XCTAssertEqual(session.document.elements, [shape])
        XCTAssertEqual(session.document.generation, generation)
        XCTAssertEqual(model.mode, .interact)
        reduced = true
        animator.hide(toolbar: toolbar, model: model) {}
        XCTAssertEqual(animator.magicEffectLayerCount, 0, "Reduce Motion replaces the flight with a short fade.")
        try await Task.sleep(for: .seconds(0.4))
        XCTAssertFalse(animator.hasActiveTimeline)
        XCTAssertEqual(animator.transientPanelCount, 0)
    }
}
