import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class AreaEscapeCancellationTests: XCTestCase {
    func testEscapeCancelsPendingRegionBeforeReleasingDrawingInput() throws {
        let suite = "area-escape-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        editor.session.panel.styleMask.insert(.nonactivatingPanel)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let completed = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 60, height: 40))
        try session.document.add(completed)
        editor.model.choose(.pen)
        var selectedArea: Rect?
        session.canvas.selectRegion { selectedArea = $0 }
        XCTAssertTrue(session.canvas.hasActiveEdit)
        let escape = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
            timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
            characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53))

        session.canvas.keyDown(with: escape)

        XCTAssertFalse(session.canvas.hasActiveEdit)
        XCTAssertEqual(editor.model.mode, .draw, "The first Escape cancels Area, not drawing input.")
        XCTAssertEqual(session.document.elements, [completed])
        XCTAssertNil(selectedArea)
        XCTAssertTrue(session.document.canUndo)
        XCTAssertFalse(session.document.isEditing)

        func mouse(_ type: NSEvent.EventType, at point: CGPoint) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point, to: nil),
                modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                eventNumber: 0, clickCount: 1, pressure: 1))
        }
        session.canvas.mouseDown(with: try mouse(.leftMouseDown, at: CGPoint(x: 120, y: 120)))
        session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, at: CGPoint(x: 180, y: 160)))
        session.canvas.mouseUp(with: try mouse(.leftMouseUp, at: CGPoint(x: 180, y: 160)))
        XCTAssertEqual(session.document.elements.count, 2, "Drawing resumes without another tool activation.")
        XCTAssertEqual(session.document.elements.last?.tool, .pen)
        XCTAssertNil(selectedArea, "The cancelled region callback must not consume the next drag.")
        editor.model.undo()
        XCTAssertEqual(session.document.elements, [completed])

        session.canvas.keyDown(with: escape)
        XCTAssertEqual(editor.model.mode, .interact, "A second Escape releases drawing input.")
        XCTAssertEqual(session.document.elements, [completed])
        XCTAssertNil(selectedArea)
    }
}
