import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class PointerOverlayInvalidationTests: XCTestCase {
    func testPointerEffectsDoNotInvalidateTheUnchangedDrawingScene() throws {
        let suite = "pointer-invalidation-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        try session.document.add(Element(tool: .ellipse, frame: Rect(x: 120, y: 140, width: 400, height: 300)))
        let original = session.document.elements
        model.interact()
        model.spotlight = true
        XCTAssertNotNil(session.presentation?.selectionOverlayLevel)
        session.panel.displayIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        let baseline = session.canvas.renderPassCount
        XCTAssertGreaterThan(baseline, 0)
        let overlayBaseline = session.presentation?.pointerOverlayRenderPassCount ?? 0
        for _ in 0..<100 { model.refreshPointerEffectsOnly() }
        RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        XCTAssertEqual(session.canvas.renderPassCount, baseline, "Pointer motion should redraw the focus layer, not every static shape.")
        XCTAssertGreaterThan(session.presentation?.pointerOverlayRenderPassCount ?? 0, overlayBaseline)
        XCTAssertEqual(session.document.elements, original)
        model.spotlight = false
        XCTAssertNil(session.presentation?.selectionOverlayLevel)
    }

    func testSpotlightFadesAndResizesThenStopsTicking() throws {
        let suite = "spotlight-fade-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        model.interact()
        let fade = model.spotlightFade
        fade.reducesMotion = { false }
        func settle(_ seconds: Double = 0.4) { RunLoop.current.run(until: Date().addingTimeInterval(seconds)) }

        model.spotlight = true
        XCTAssertTrue(fade.isAnimating)
        XCTAssertLessThan(fade.intensity, 1, "Turning the spotlight on fades the dimming in.")
        settle()
        XCTAssertEqual(fade.intensity, 1)
        XCTAssertFalse(fade.isAnimating, "A resting spotlight keeps no animation running.")

        let radius = model.spotlightRadius
        model.spotlightRadius = radius + 60
        XCTAssertTrue(fade.isAnimating)
        XCTAssertLessThan(fade.radius(for: model.spotlightRadius), radius + 60)
        settle()
        XCTAssertEqual(fade.radius(for: model.spotlightRadius), radius + 60)
        XCTAssertFalse(fade.isAnimating)

        model.spotlight = false
        XCTAssertTrue(fade.isVisible, "The dimming fades out instead of disappearing.")
        settle()
        XCTAssertFalse(fade.isVisible)
        XCTAssertFalse(fade.isAnimating)
        session.panel.displayIfNeeded()
        let passes = session.canvas.renderPassCount
        settle(0.2)
        XCTAssertEqual(session.canvas.renderPassCount, passes, "Nothing redraws after the fade-out ends.")

        model.spotlight = true
        model.spotlight = false
        model.spotlight = true
        settle()
        XCTAssertEqual(fade.intensity, 1, "Rapid toggles settle on the latest state.")
        XCTAssertFalse(fade.isAnimating)

        fade.reducesMotion = { true }
        model.spotlightRadius = radius
        XCTAssertEqual(fade.radius(for: model.spotlightRadius), radius, "Reduce Motion resizes at once.")
        XCTAssertFalse(fade.isAnimating)
        model.spotlight = false
        settle()
        XCTAssertFalse(fade.isVisible)
    }
}
