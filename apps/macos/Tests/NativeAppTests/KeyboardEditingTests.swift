import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class KeyboardEditingTests: XCTestCase {
    func testNudgingSelectionHonorsStepSizeLocksAndUndo() throws {
        let suite = "keyboard-nudge-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 350), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let rectangle = Element(tool: .rectangle, frame: Rect(x: 80, y: 70, width: 120, height: 60))
        var locked = Element(tool: .cover, frame: Rect(x: 250, y: 70, width: 50, height: 50))
        locked.locked = true
        try session.document.add(rectangle); try session.document.add(locked)
        session.document.select([rectangle.id, locked.id])
        editor.model.synchronize()
        func key(_ code: UInt16, _ flags: NSEvent.ModifierFlags = []) throws {
            let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero,
                modifierFlags: flags, timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: code))
            session.canvas.keyDown(with: event)
        }
        try key(124)
        XCTAssertEqual(session.document.elements[0].frame.x, 81)
        try key(125, .shift)
        XCTAssertEqual(session.document.elements[0].frame.y, 80)
        XCTAssertEqual(session.document.elements[1].frame, locked.frame)
        editor.model.undo()
        XCTAssertEqual(session.document.elements[0].frame.y, 70)
        editor.model.undo()
        XCTAssertEqual(session.document.elements[0].frame, rectangle.frame)
        session.document.select([locked.id]); editor.model.synchronize()
        try key(123)
        XCTAssertEqual(session.document.elements[1].frame, locked.frame)
        XCTAssertTrue(editor.model.status.contains("locked"))
    }

    func testCanvasExposesItsStateAndFocusToolsAction() throws {
        let suite = "canvas-accessibility-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 200, height: 150), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        var focused = false
        editor.model.onFocusToolbar = { focused = true }
        try editor.session.document.add(Element(tool: .ellipse, frame: Rect(x: 10, y: 10, width: 50, height: 50)))
        editor.model.synchronize()
        editor.session.canvas.refreshAccessibilitySummary()
        XCTAssertEqual(editor.session.canvas.accessibilityLabel(), "Annotation canvas")
        XCTAssertTrue((editor.session.canvas.accessibilityValue() as? String)?.contains("1 object") == true)
        let action = try XCTUnwrap(editor.session.canvas.accessibilityCustomActions()?.first)
        XCTAssertEqual(action.name, "Focus drawing tools")
        XCTAssertTrue(try XCTUnwrap(action.handler)())
        XCTAssertTrue(focused)
    }
}
