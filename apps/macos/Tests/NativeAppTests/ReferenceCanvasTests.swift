import AppKit
import ImageIO
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ReferenceCanvasTests: XCTestCase {
    private func image() throws -> CGImage {
        try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 240), background: .white)
    }
    private func mouse(_ type: NSEvent.EventType, _ point: NSPoint, in session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point, to: nil),
                                        modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber,
                                        context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }
    func testReferencePixelsSupportEffectsCountersAndRulerWithoutLiveCapture() throws {
        let suite = "native-snapshot-\(UUID())"
        let workspace = NativeCanvasFixture(image: try image(), defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { workspace.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let session = workspace.session
        XCTAssertTrue(workspace.model.hasReferenceImage)
        workspace.model.choose(.pixelate)
        session.canvas.mouseDown(with: try mouse(.leftMouseDown, NSPoint(x: 50, y: 50), in: session))
        session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, NSPoint(x: 140, y: 100), in: session))
        session.canvas.mouseUp(with: try mouse(.leftMouseUp, NSPoint(x: 140, y: 100), in: session))
        XCTAssertEqual(session.document.elements[0].tool, .pixelate)
        workspace.model.choose(.counter)
        for point in [NSPoint(x: 200, y: 100), NSPoint(x: 250, y: 100)] {
            session.canvas.mouseDown(with: try mouse(.leftMouseDown, point, in: session))
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, point, in: session))
        }
        XCTAssertEqual(session.document.elements.filter { $0.tool == .counter }.map(\.text), ["1", "2"])
        workspace.model.choose(.ruler)
        session.canvas.mouseDown(with: try mouse(.leftMouseDown, NSPoint(x: 50, y: 150), in: session))
        session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, NSPoint(x: 150, y: 150), in: session))
        session.canvas.mouseUp(with: try mouse(.leftMouseUp, NSPoint(x: 150, y: 150), in: session))
        XCTAssertEqual(session.document.elements.last?.text, "100 px")
        let data = try workspace.output()
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let output = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(output.width, 400)
        XCTAssertEqual(output.height, 240)
        workspace.setToolsVisible(false)
        XCTAssertFalse(workspace.toolsVisible)
        XCTAssertEqual(session.document.elements.count, 4)
        workspace.setToolsVisible(true)
        XCTAssertTrue(workspace.toolsVisible)
        XCTAssertEqual(session.document.elements.count, 4)
    }
}
