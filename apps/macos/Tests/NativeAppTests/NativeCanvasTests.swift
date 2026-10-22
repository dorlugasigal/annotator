import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class NativeCanvasTests: XCTestCase {
    private func makeModel() throws -> (WorkspaceModel, String) {
        let suite = "com.dorlugasigal.annotator.native-test.\(UUID().uuidString)"
        return (WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite))), suite)
    }
    private func event(_ type: NSEvent.EventType, at point: NSPoint, session: OverlaySession) throws -> NSEvent {
        let native = session.canvas.convert(point, to: nil)
        return try XCTUnwrap(NSEvent.mouseEvent(with: type, location: native, modifierFlags: [],
                                               timestamp: ProcessInfo.processInfo.systemUptime,
                                               windowNumber: session.panel.windowNumber, context: nil,
                                               eventNumber: 1, clickCount: 1, pressure: 1))
    }

    func testNativeDrawingAndInputRelease() throws {
        let (model, suite) = try makeModel()
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertFalse(session.panel.ignoresMouseEvents)
        model.tool = .rectangle
        session.canvas.mouseDown(with: try event(.leftMouseDown, at: NSPoint(x: 220, y: 220), session: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, at: NSPoint(x: 420, y: 340), session: session))
        session.canvas.mouseUp(with: try event(.leftMouseUp, at: NSPoint(x: 420, y: 340), session: session))
        XCTAssertEqual(session.document.elements.count, 1)
        XCTAssertEqual(session.document.elements[0].frame, Rect(x: 220, y: 220, width: 200, height: 120))
        XCTAssertEqual(model.tool, .select)
        model.interact()
        XCTAssertTrue(session.panel.ignoresMouseEvents)
        XCTAssertEqual(model.mode, .interact)
        XCTAssertEqual(session.document.elements.count, 1)
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    func testNativeTextCommitsAndOpaqueCoverSurvivesClear() throws {
        let (model, suite) = try makeModel()
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        model.tool = .text
        session.canvas.mouseDown(with: try event(.leftMouseDown, at: NSPoint(x: 260, y: 300), session: session))
        let editor = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
        editor.string = "Native\nmultiline text"
        session.canvas.commitTextIfNeeded()
        XCTAssertEqual(session.document.elements.first?.text, "Native\nmultiline text")
        try session.document.add(Element(tool: .cover, frame: Rect(x: 50, y: 50, width: 100, height: 40)))
        model.perform { try $0.clearAnnotations() }
        XCTAssertEqual(session.document.elements.count, 1)
        XCTAssertTrue(model.hasProtection)
        model.interact()
        XCTAssertTrue(model.hasProtection)
    }
}
