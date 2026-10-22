import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class RotationHandleTests: XCTestCase {
    func testRotationHandleHasLargerHitAreaDistinctCursorAndDoesNotJump() throws {
        let suite = "rotation-target-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let element = Element(tool: .rectangle, frame: Rect(x: 250, y: 200, width: 160, height: 100))
        try session.document.add(element); session.document.select([element.id]); editor.model.choose(.select)
        let grab = Point(element.frame.center.x + 14, element.frame.y - 24)
        XCTAssertTrue(session.canvas.isOverRotationHandle(grab))
        XCTAssertFalse(session.canvas.isOverRotationHandle(grab + Point(10, 0)))
        func event(_ type: NSEvent.EventType, _ point: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
                modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                eventNumber: 0, clickCount: 1, pressure: 1))
        }
        session.canvas.mouseMoved(with: try event(.mouseMoved, grab))
        XCTAssertTrue(NSCursor.current === ToolCursorFactory.rotationCursor)
        session.canvas.mouseDown(with: try event(.leftMouseDown, grab))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, grab))
        XCTAssertEqual(session.document.elements[0].rotation, 0, accuracy: 0.001)
        let finish = grab.rotated(around: element.frame.center, by: .pi / 3)
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, finish))
        session.canvas.mouseUp(with: try event(.leftMouseUp, finish))
        XCTAssertEqual(session.document.elements[0].rotation, .pi / 3, accuracy: 0.001)
        editor.model.undo()
        XCTAssertEqual(session.document.elements[0].rotation, 0, accuracy: 0.001)
    }
}
