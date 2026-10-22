import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class AdvancedEditingTests: XCTestCase {
    func testSwitchingToolsDoesNotRestyleExistingSelection() throws {
        let suite = "native-selection-\(UUID())"
        let model = WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 200, y: 200, width: 100, height: 100))
        try session.document.add(shape)
        session.document.select([shape.id])
        model.choose(.pen)
        model.applyStyle { $0.width = 16 }
        XCTAssertTrue(session.document.selection.isEmpty)
        XCTAssertEqual(session.document.elements[0].style.width, 3)
        XCTAssertEqual(model.style.width, 16)
    }

    func testNativeSmartDrawCompletesWithoutSecondUndo() async throws {
        let suite = "native-recognition-\(UUID())"
        let model = WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing(); model.choose(.pen); model.smartDraw = true
        let session = try XCTUnwrap(model.active)
        let first = NSPoint(x: 450, y: 300)
        session.canvas.mouseDown(with: try mouse(.leftMouseDown, point: first, session: session))
        for index in 1...100 {
            let angle = Double(index) / 100 * Double.pi * 2
            let point = NSPoint(x: 350 + 100 * cos(angle), y: 300 + 80 * sin(angle))
            session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, point: point, session: session))
        }
        session.canvas.mouseUp(with: try mouse(.leftMouseUp, point: first, session: session))
        for _ in 0..<100 {
            if session.document.elements.first?.tool == .ellipse { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(session.document.elements.first?.tool, .ellipse)
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    private func mouse(_ type: NSEvent.EventType, point: NSPoint, session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point, to: nil), modifierFlags: [],
                                        timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                                        eventNumber: 0, clickCount: 1, pressure: 1))
    }

    func testClickBuiltArrowFinishesWithOneUndo() throws {
        let suite = "native-arrows-\(UUID())"
        let model = WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        model.choose(.arrow)
        model.applyStyle { $0.curved = true }
        let session = try XCTUnwrap(model.active)
        for point in [NSPoint(x: 240, y: 230), NSPoint(x: 350, y: 170), NSPoint(x: 480, y: 290)] {
            session.canvas.mouseDown(with: try mouse(.leftMouseDown, point: point, session: session))
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, point: point, session: session))
        }
        XCTAssertTrue(session.document.isEditing)
        session.canvas.finishLinearPath()
        XCTAssertFalse(session.document.isEditing)
        XCTAssertEqual(session.document.elements.count, 1)
        XCTAssertEqual(session.document.elements[0].points.count, 3)
        XCTAssertTrue(session.document.elements[0].style.curved)
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    func testContinuousStyleChangesAreOneUndoAndPersistDefaults() throws {
        let suite = "native-style-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 200, y: 200, width: 100, height: 100))
        try session.document.add(shape)
        session.document.select([shape.id])
        model.beginStyleEdit()
        for width in [6.0, 8.0, 10.0] { model.applyStyle { $0.width = width } }
        model.finishStyleEdit()
        XCTAssertEqual(session.document.elements[0].style.width, 10)
        session.document.undo()
        XCTAssertEqual(session.document.elements[0].style.width, 3)
        let restored = WorkspaceModel(defaults: defaults)
        defer { restored.closeAll() }
        XCTAssertEqual(restored.preferences.toolStyles["rectangle"]?.width, 10)
    }
}
