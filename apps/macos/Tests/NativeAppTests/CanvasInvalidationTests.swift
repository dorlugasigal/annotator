import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class CanvasInvalidationTests: XCTestCase {
    private func workspace() throws -> (WorkspaceModel, OverlaySession, UserDefaults, String) {
        let suite = "canvas-invalidation-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        session.canvas.setFrameSize(CGSize(width: 400, height: 300))
        session.presentation?.suspend()
        return (model, session, defaults, suite)
    }

    private func display(_ model: WorkspaceModel, _ session: OverlaySession) {
        model.synchronize()
        paintIfNeeded(session)
    }

    private func paintIfNeeded(_ session: OverlaySession) {
        session.panel.displayIfNeeded()
        session.canvas.displayIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.04))
    }

    func testToolAndDefaultStyleChangesDoNotRepaintUnchangedDrawing() throws {
        let (model, session, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let shape = Element(tool: .ellipse, frame: Rect(x: 40, y: 40, width: 140, height: 100))
        session.document = try CanvasDocument(elements: [shape])
        display(model, session)
        let baseline = session.canvas.renderPassCount
        XCTAssertGreaterThan(baseline, 0)
        for tool in [Tool.pen, .ellipse, .rectangle, .diamond, .arrow] {
            model.choose(tool)
            paintIfNeeded(session)
        }
        model.applyStyle { $0.width = 7 }
        paintIfNeeded(session)
        XCTAssertEqual(session.canvas.renderPassCount, baseline)
        XCTAssertEqual(session.document.elements, [shape])
    }

    func testChangesCancellationAndDocumentReplacementStillRepaint() throws {
        let (model, session, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let shape = Element(tool: .rectangle, frame: Rect(x: 40, y: 40, width: 140, height: 100))
        session.document = try CanvasDocument(elements: [shape])
        display(model, session)
        func expectPaint(_ change: () throws -> Void) throws {
            let before = session.canvas.renderPassCount
            try change()
            display(model, session)
            XCTAssertGreaterThan(session.canvas.renderPassCount, before)
        }
        try expectPaint { session.document.select([shape.id]) }
        try expectPaint {
            session.document.begin()
            try session.document.update(shape.id) { $0.frame.x += 30 }
        }
        try expectPaint { session.document.cancel() }
        try expectPaint { try session.document.setSelectionLocked(true) }
        try expectPaint { session.document.undo() }
        try expectPaint { model.board = .light }
        try expectPaint { session.canvas.outputRegion = Rect(x: 20, y: 20, width: 60, height: 60) }
        try expectPaint {
            var changed = shape
            changed.frame.x = 200
            session.document = try CanvasDocument(elements: [changed])
            session.canvas.renderer.reset()
        }
        try expectPaint {
            let dark = session.canvas.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            session.canvas.appearance = NSAppearance(named: dark ? .aqua : .darkAqua)
        }
    }

    func testPixelEffectsAlwaysKeepTheirExistingInvalidationPath() throws {
        let (model, session, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        session.document = try CanvasDocument(elements: [
            Element(tool: .blur, frame: Rect(x: 40, y: 40, width: 140, height: 100))
        ])
        display(model, session)
        let baseline = session.canvas.renderPassCount
        display(model, session)
        XCTAssertGreaterThan(session.canvas.renderPassCount, baseline)
    }
}
