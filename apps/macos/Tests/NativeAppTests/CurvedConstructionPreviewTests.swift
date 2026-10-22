import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class CurvedConstructionPreviewTests: XCTestCase {
    func testPendingPointUsesFullCurvedGeometryWithoutEnteringHistory() throws {
        let suite = "curve-construction-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.arrow)
        editor.model.applyStyle { $0.curved = true }
        let session = editor.session
        func event(_ type: NSEvent.EventType, _ point: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
                modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                eventNumber: 0, clickCount: 1, pressure: 1))
        }
        for point in [Point(150, 200), Point(350, 100)] {
            session.canvas.mouseDown(with: try event(.leftMouseDown, point))
            session.canvas.mouseUp(with: try event(.leftMouseUp, point))
        }
        let tentative = Point(450, 350)
        session.canvas.mouseMoved(with: try event(.mouseMoved, tentative))
        let draft = try XCTUnwrap(session.canvas.constructionPreview)
        XCTAssertEqual(draft.points.count, 3)
        XCTAssertTrue(draft.style.curved)
        XCTAssertEqual(ConnectorGeometry.curves(through: draft.points).count, 2)
        XCTAssertEqual(session.document.elements.first?.points.count, 2)
        XCTAssertTrue(session.document.isEditing)
        session.canvas.mouseDown(with: try event(.leftMouseDown, tentative))
        session.canvas.mouseUp(with: try event(.leftMouseUp, tentative))
        session.canvas.finishLinearPath()
        let committed = try XCTUnwrap(session.document.elements.first)
        XCTAssertEqual(committed.points, draft.points)
        XCTAssertEqual(committed.style.curved, draft.style.curved)
        let previewPixels = try PNGExporter.data(elements: [draft], size: CGSize(width: 800, height: 600))
        let finalPixels = try PNGExporter.data(elements: [committed], size: CGSize(width: 800, height: 600))
        XCTAssertEqual(previewPixels, finalPixels)
        editor.model.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }
}
