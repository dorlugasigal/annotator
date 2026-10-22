import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class SmartDrawGestureTests: XCTestCase {
    func testDiamondAndArrowPreviewCommitAndUndoThroughPenEvents() async throws {
        let suite = "smart-draw-gestures-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 500), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.smartDraw = true
        editor.model.choose(.pen)
        editor.show()
        let cases: [(Tool, [Point])] = [
            (.diamond, [Point(180, 70), Point(290, 160), Point(175, 250), Point(70, 160), Point(180, 70)]),
            (.arrow, [Point(80, 170), Point(330, 170), Point(280, 130), Point(330, 170), Point(280, 210)])
        ]
        func event(_ type: NSEvent.EventType, _ point: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: editor.session.canvas.convert(point.cgPoint, to: nil),
                modifierFlags: [], timestamp: 0, windowNumber: editor.session.panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        }
        for (tool, vertices) in cases {
            editor.session.canvas.mouseDown(with: try event(.leftMouseDown, vertices[0]))
            for index in 1..<vertices.count {
                for step in 1...24 {
                    let t = Double(step) / 24
                    let point = vertices[index - 1] + (vertices[index] - vertices[index - 1]) * t
                    editor.session.canvas.mouseDragged(with: try event(.leftMouseDragged, point))
                }
            }
            for _ in 0..<100 {
                if editor.session.canvas.smartPreview?.tool == tool { break }
                try await Task.sleep(for: .milliseconds(5))
            }
            XCTAssertEqual(editor.session.canvas.smartPreview?.tool, tool)
            editor.session.canvas.mouseUp(with: try event(.leftMouseUp, try XCTUnwrap(vertices.last)))
            for _ in 0..<100 {
                if editor.session.document.elements.last?.tool == tool { break }
                try await Task.sleep(for: .milliseconds(5))
            }
            XCTAssertEqual(editor.session.document.elements.count, 1)
            XCTAssertEqual(editor.session.document.elements.last?.tool, tool)
            editor.model.undo()
            XCTAssertTrue(editor.session.document.elements.isEmpty)
            editor.model.redo()
            XCTAssertEqual(editor.session.document.elements.last?.tool, tool)
            editor.model.undo()
        }
    }

    private func drawEllipse(on editor: NativeCanvasFixture) async throws {
        let canvas = editor.session.canvas
        func event(_ type: NSEvent.EventType, _ point: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: canvas.convert(point.cgPoint, to: nil),
                modifierFlags: [], timestamp: 0, windowNumber: editor.session.panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        }
        canvas.mouseDown(with: try event(.leftMouseDown, Point(330, 200)))
        var last = Point(330, 200)
        for index in 1...120 {
            let angle = Double(index) / 120 * Double.pi * 2
            last = Point(250 + 80 * cos(angle), 200 + 60 * sin(angle))
            canvas.mouseDragged(with: try event(.leftMouseDragged, last))
        }
        for _ in 0..<100 where canvas.smartPreview == nil { try await Task.sleep(for: .milliseconds(5)) }
        XCTAssertEqual(canvas.smartPreview?.tool, .ellipse)
        canvas.mouseUp(with: try event(.leftMouseUp, last))
        for _ in 0..<200 where editor.session.document.elements.first?.tool != .ellipse {
            try await Task.sleep(for: .milliseconds(2))
        }
        XCTAssertEqual(editor.session.document.elements.first?.tool, .ellipse)
    }

    private func smartDrawFixture(_ name: String) throws -> (NativeCanvasFixture, UserDefaults, String) {
        let suite = "\(name)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 600, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        editor.model.smartDraw = true
        editor.model.choose(.pen)
        editor.show()
        return (editor, defaults, suite)
    }

    func testSnapMorphIsBoundedAndLandsOnTheRecognizedShapeInOneUndoStep() async throws {
        let (editor, defaults, suite) = try smartDrawFixture("snap-morph")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.reducesMotion = { false }
        try await drawEllipse(on: editor)
        XCTAssertTrue(canvas.isSnapMorphing, "The recognized shape morphs from the ink.")
        XCTAssertTrue(canvas.transitionTickersRunning)
        let recognized = editor.session.document.elements
        let started = Date()
        for _ in 0..<200 where canvas.isSnapMorphing { try await Task.sleep(for: .milliseconds(5)) }
        XCTAssertFalse(canvas.isSnapMorphing)
        XCTAssertLessThan(Date().timeIntervalSince(started), 0.8)
        XCTAssertFalse(canvas.transitionTickersRunning, "A finished morph leaves no display link running.")
        XCTAssertEqual(editor.session.document.elements, recognized)
        XCTAssertEqual(editor.session.document.elements.first?.tool, .ellipse)
        editor.model.undo()
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        XCTAssertFalse(editor.session.document.canUndo, "Ink and recognition stay one Undo step.")
    }

    func testSnapMorphJumpsToTheShapeOnANewGestureOrUndo() async throws {
        let (editor, defaults, suite) = try smartDrawFixture("snap-morph-cancel")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.reducesMotion = { false }
        try await drawEllipse(on: editor)
        XCTAssertTrue(canvas.isSnapMorphing)
        let recognized = editor.session.document.elements
        let down = try XCTUnwrap(NSEvent.mouseEvent(with: .leftMouseDown, location: canvas.convert(CGPoint(x: 520, y: 340), to: nil),
            modifierFlags: [], timestamp: 0, windowNumber: editor.session.panel.windowNumber,
            context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        canvas.mouseDown(with: down)
        XCTAssertFalse(canvas.isSnapMorphing)
        XCTAssertFalse(canvas.transitionTickersRunning)
        canvas.cancelGesture()
        XCTAssertEqual(editor.session.document.elements, recognized)

        editor.model.undo()
        try await drawEllipse(on: editor)
        XCTAssertTrue(canvas.isSnapMorphing)
        editor.model.undo()
        try await Task.sleep(for: .milliseconds(60))
        XCTAssertFalse(canvas.isSnapMorphing, "Undo ends the morph instead of animating a removed shape.")
        XCTAssertFalse(canvas.transitionTickersRunning)
        XCTAssertTrue(editor.session.document.elements.isEmpty)
    }

    func testReduceMotionShowsTheRecognizedShapeWithoutAMorph() async throws {
        let (editor, defaults, suite) = try smartDrawFixture("snap-morph-reduced")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.reducesMotion = { true }
        try await drawEllipse(on: editor)
        XCTAssertFalse(canvas.isSnapMorphing)
        XCTAssertFalse(canvas.transitionTickersRunning)
        XCTAssertEqual(editor.session.document.elements.first?.tool, .ellipse)
    }

    func testSnapOutlinesKeepClosedShapesClosedAndArrowsOpen() throws {
        let ink = (0...40).map { Point(100 + 50 * cos(Double($0) / 40 * 2 * .pi), 100 + 30 * sin(Double($0) / 40 * 2 * .pi)) }
        let from = DrawingCanvas.resample(ink, count: 96)
        XCTAssertEqual(from.count, 96)
        for tool in [Tool.rectangle, .diamond, .ellipse] {
            var shape = Element(tool: tool, frame: Rect(x: 50, y: 70, width: 100, height: 60))
            shape.rotation = 0.2
            let outline = try XCTUnwrap(DrawingCanvas.snapOutline(of: shape, count: 96))
            let aligned = DrawingCanvas.align(outline, to: from, closed: true)
            XCTAssertEqual(aligned.count, 96)
            XCTAssertEqual(try XCTUnwrap(aligned.first).distance(to: try XCTUnwrap(aligned.last)), 0, accuracy: 0.001, "\(tool)")
            let nearest = outline.map { $0.distance(to: from[0]) }.min() ?? 0
            XCTAssertEqual(aligned[0].distance(to: from[0]), nearest, accuracy: 0.001)
        }
        let arrow = Element(tool: .arrow, points: [Point(300, 100), Point(100, 100)])
        let line = DrawingCanvas.resample([Point(100, 100), Point(300, 100)], count: 96)
        let path = DrawingCanvas.align(try XCTUnwrap(DrawingCanvas.snapOutline(of: arrow, count: 96)), to: line, closed: false)
        XCTAssertEqual(path.first, Point(100, 100))
        XCTAssertEqual(path.last, Point(300, 100))
        XCTAssertNil(DrawingCanvas.snapOutline(of: Element(tool: .star, frame: Rect(x: 0, y: 0, width: 40, height: 40)), count: 96))
    }
}
