import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class IncrementalInkTests: XCTestCase {
    private func workspace() throws -> (WorkspaceModel, OverlaySession, String) {
        let suite = "incremental-ink-\(UUID())"
        let model = WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        model.smartDraw = false
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        session.canvas.setFrameSize(CGSize(width: 400, height: 300))
        session.presentation?.suspend()
        return (model, session, suite)
    }

    private func bitmap(antialias: Bool = true) throws -> CGContext {
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: 400, height: 300,
            bitsPerComponent: 8, bytesPerRow: 1600, space: space,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.translateBy(x: 0, y: 300)
        context.scaleBy(x: 1, y: -1)
        context.setShouldAntialias(antialias)
        return context
    }

    private func paint(_ canvas: DrawingCanvas, into context: CGContext, rect: CGRect) {
        context.saveGState()
        context.clip(to: rect)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        canvas.draw(rect)
        NSGraphicsContext.restoreGraphicsState()
        context.restoreGState()
        context.flush()
    }

    func testPartialInkRepaintPreservesCanvasWithinAntialiasRounding() throws {
        let (model, session, suite) = try workspace()
        defer { model.closeAll(); UserDefaults().removePersistentDomain(forName: suite) }
        for tool in [Tool.pen, .highlighter] {
            for pressure in [false, true] {
                for antialias in [false, true] {
                    var stroke = Element(tool: tool, points: [Point(30, 40), Point(80, 110), Point(140, 70)])
                    stroke.style.width = 4
                    stroke.style.roughness = 0
                    stroke.style.opacity = 0.6
                    stroke.style.pressure = pressure
                    stroke.pressures = [0.2, 1, 0.4]
                    let cover = Element(tool: .cover, frame: Rect(x: 160, y: 50, width: 45, height: 130))
                    session.document = try CanvasDocument(elements: [stroke, cover])
                    session.document.begin()
                    let partial = try bitmap(antialias: antialias)
                    paint(session.canvas, into: partial, rect: session.canvas.bounds)
                    let next = Point(180, 90)
                    let rect = try XCTUnwrap(session.canvas.incrementalInkRect(for: stroke, to: next))
                    XCTAssertLessThan(rect.width * rect.height, 12_000)
                    try session.document.appendSample(to: stroke.id, point: next, pressure: 0.8)
                    paint(session.canvas, into: partial, rect: rect)
                    let full = try bitmap(antialias: antialias)
                    session.canvas.renderer.reset()
                    paint(session.canvas, into: full, rect: session.canvas.bounds)
                    let count = partial.bytesPerRow * partial.height
                    let actual = Data(bytes: try XCTUnwrap(partial.data), count: count)
                    let expected = Data(bytes: try XCTUnwrap(full.data), count: count)
                    // Clipped translucent edge rasterization can differ by two 8-bit steps.
                    // Require exact geometry with antialiasing off, and exact opaque covers in both modes.
                    let differences = actual.indices.map { abs(Int(actual[$0]) - Int(expected[$0])) }
                    XCTAssertLessThanOrEqual(differences.max() ?? 0, antialias ? 2 : 0, "\(tool), pressure \(pressure)")
                    XCTAssertLessThan(differences.filter { $0 != 0 }.count, 480)
                    let protectedPixel = (80 * 400 + 170) * 4
                    XCTAssertEqual(Array(actual[protectedPixel..<(protectedPixel + 4)]), [0, 0, 0, 255])
                    XCTAssertTrue(actual.contains { $0 != 0 })
                }
            }
        }
    }

    func testMouseDragActuallyPaintsOnlyTheChangedRegion() throws {
        let (model, session, suite) = try workspace()
        defer { model.closeAll(); UserDefaults().removePersistentDomain(forName: suite) }
        model.choose(.pen)
        func event(_ type: NSEvent.EventType, _ point: CGPoint) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type,
                location: session.canvas.convert(point, to: nil), modifierFlags: [], timestamp: 0,
                windowNumber: session.panel.windowNumber, context: nil, eventNumber: 0,
                clickCount: 1, pressure: 1))
        }
        func flush() {
            session.panel.displayIfNeeded()
            session.canvas.displayIfNeeded()
            RunLoop.current.run(until: Date().addingTimeInterval(0.04))
        }
        session.canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 30, y: 40)))
        flush()
        let before = session.canvas.renderPassCount
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 35, y: 44)))
        flush()
        XCTAssertGreaterThan(session.canvas.renderPassCount, before)
        let rect = session.canvas.lastDrawnRect
        XCTAssertFalse(rect.isNull)
        XCTAssertLessThan(rect.width * rect.height, session.canvas.bounds.width * session.canvas.bounds.height / 10)
        session.canvas.mouseUp(with: try event(.leftMouseUp, CGPoint(x: 35, y: 44)))
    }

    func testSelectionSmartDrawAndEffectsKeepFullInvalidation() throws {
        let (model, session, suite) = try workspace()
        defer { model.closeAll(); UserDefaults().removePersistentDomain(forName: suite) }
        let stroke = Element(tool: .pen, points: [Point(30, 40), Point(80, 110)])
        session.document = try CanvasDocument(elements: [stroke])
        let next = Point(100, 120)
        XCTAssertNotNil(session.canvas.incrementalInkRect(for: stroke, to: next))
        var rough = stroke
        rough.style.roughness = 4
        XCTAssertNil(session.canvas.incrementalInkRect(for: rough, to: next))
        model.smartDraw = true
        XCTAssertNil(session.canvas.incrementalInkRect(for: stroke, to: next))
        model.smartDraw = false
        session.document.select([stroke.id])
        XCTAssertNil(session.canvas.incrementalInkRect(for: stroke, to: next))
        session.document.select([])
        try session.document.add(Element(tool: .blur, frame: Rect(x: 10, y: 10, width: 20, height: 20)))
        XCTAssertNil(session.canvas.incrementalInkRect(for: stroke, to: next))
    }

    func testStraighteningHighlightRepaintsThePreviousFreehandStroke() throws {
        let (model, session, suite) = try workspace()
        defer { model.closeAll(); UserDefaults().removePersistentDomain(forName: suite) }
        model.choose(.highlighter)
        func event(_ type: NSEvent.EventType, _ point: CGPoint, flags: NSEvent.ModifierFlags = []) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type,
                location: session.canvas.convert(point, to: nil), modifierFlags: flags, timestamp: 0,
                windowNumber: session.panel.windowNumber, context: nil, eventNumber: 0,
                clickCount: 1, pressure: 1))
        }
        session.canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 30, y: 100)))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 80, y: 200)))
        session.panel.displayIfNeeded()
        session.canvas.displayIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.04))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 200, y: 100), flags: .shift))
        session.panel.displayIfNeeded()
        session.canvas.displayIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.04))
        XCTAssertEqual(session.canvas.lastDrawnRect, session.canvas.bounds)
        XCTAssertEqual(session.document.elements.last?.points.count, 2)
        session.canvas.cancelGesture()
    }
}
