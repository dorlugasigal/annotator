import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class UniformEditingTests: XCTestCase {
    private func assertFrame(_ actual: Rect, matches expected: Rect, message: String = "",
                             file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(actual.x, expected.x, accuracy: 0.000001, message, file: file, line: line)
        XCTAssertEqual(actual.y, expected.y, accuracy: 0.000001, message, file: file, line: line)
        XCTAssertEqual(actual.width, expected.width, accuracy: 0.000001, message, file: file, line: line)
        XCTAssertEqual(actual.height, expected.height, accuracy: 0.000001, message, file: file, line: line)
    }
    func testUndoCancelsConstructionBeforeRemovingAnEarlierObject() throws {
        let suite = "construction-undo-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let existing = Element(tool: .rectangle, frame: Rect(x: 50, y: 50, width: 100, height: 100))
        try editor.session.document.add(existing)
        editor.model.choose(.arrow)
        let point = Point(250, 200)
        editor.session.canvas.mouseDown(with: try event(.leftMouseDown, at: point, session: editor.session))
        editor.session.canvas.mouseUp(with: try event(.leftMouseUp, at: point, session: editor.session))
        XCTAssertTrue(editor.model.canUndo)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements.map(\.id), [existing.id])
        XCTAssertFalse(editor.session.canvas.hasActiveEdit)
        editor.model.undo()
        XCTAssertTrue(editor.session.document.elements.isEmpty)
    }

    func testScreenZoomScrollIsOneUndoableGesture() throws {
        let suite = "scroll-undo-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 100, height: 100), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        editor.model.scrollScreenZoom(10)
        editor.model.scrollScreenZoom(10)
        editor.model.finishZoomGesture()
        XCTAssertGreaterThan(editor.session.document.presentation.zoom, 2)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.presentation.zoom, 2)
        XCTAssertEqual(editor.session.document.presentation.mode, .screen)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.presentation.mode, .off)
    }

    private func event(_ type: NSEvent.EventType, at point: Point, session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
                                        modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber,
                                        context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }
    private func deleteKey(_ session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                      windowNumber: session.panel.windowNumber, context: nil, characters: "\u{7f}",
                                      charactersIgnoringModifiers: "\u{7f}", isARepeat: false, keyCode: 51))
    }

    func testEveryPlacedToolCanBeSelectedMovedDeletedAndUndone() throws {
        let suite = "uniform-editing-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let png = try PNGExporter.encode(image)
        for tool in Tool.allCases where tool != .select && tool != .eraser {
            var item = Element(tool: tool, frame: Rect(x: 200, y: 200, width: 120, height: 100), text: tool == .counter ? "1" : "Editable")
            var hit = item.frame.center
            if tool.isFreehand || tool.isConnector || tool == .ruler {
                item = Element(tool: tool, points: [Point(200, 250), Point(260, 210), Point(320, 250)])
                if tool == .ruler { item.points = [Point(200, 250), Point(320, 250)] }
                item.frame = Rect.enclosing(item.points)
                hit = tool == .ruler ? Point(260, 250) : Point(260, 210)
                if tool == .arrow {
                    item.style.curved = true
                    hit = try XCTUnwrap(ConnectorGeometry.curves(through: item.points).first).point(at: 0.5)
                }
            }
            if tool == .image { item.imagePNG = png }
            let session = editor.session
            session.document = try CanvasDocument(elements: [item])
            editor.model.choose(.select)
            session.canvas.mouseDown(with: try event(.leftMouseDown, at: hit, session: session))
            XCTAssertEqual(session.document.selection, [item.id], "Selection failed for \(tool)")
            let displacement = Point(30, 20)
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, at: hit + displacement, session: session))
            session.canvas.mouseUp(with: try event(.leftMouseUp, at: hit + displacement, session: session))
            assertFrame(session.document.elements[0].frame, matches: item.frame.translated(by: displacement), message: "Move failed for \(tool)")
            session.canvas.keyDown(with: try deleteKey(session))
            XCTAssertTrue(session.document.elements.isEmpty, "Delete failed for \(tool)")
            session.document.undo()
            assertFrame(session.document.elements[0].frame, matches: item.frame.translated(by: displacement))
            session.document.undo()
            XCTAssertEqual(session.document.elements[0].frame, item.frame)
        }
    }

    func testSelectedBlurStrengthChangesOnlyThatRegionAndIsUndoable() throws {
        let suite = "blur-editing-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        var first = Element(tool: .blur, frame: Rect(x: 100, y: 100, width: 100, height: 80))
        first.style.effectAmount = 8
        var second = Element(tool: .blur, frame: Rect(x: 300, y: 100, width: 100, height: 80))
        second.style.effectAmount = 22
        editor.session.document = try CanvasDocument(elements: [first, second])
        editor.model.choose(.select)
        editor.session.canvas.mouseDown(with: try event(.leftMouseDown, at: first.frame.center, session: editor.session))
        editor.session.canvas.mouseUp(with: try event(.leftMouseUp, at: first.frame.center, session: editor.session))
        XCTAssertEqual(editor.model.effectiveTool, .blur)
        editor.model.beginStyleEdit()
        editor.model.applyStyle { $0.effectAmount = 45 }
        editor.model.finishStyleEdit()
        XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 45)
        XCTAssertEqual(editor.session.document.elements[1].style.effectAmount, 22)
        editor.model.perform { $0.undo() }
        XCTAssertEqual(editor.session.document.elements[0].style.effectAmount, 8)
    }

    func testDeleteAndUndoIncludeMagnifierState() throws {
        let suite = "zoom-history-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 100, height: 100), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode: .lens, zoom: 3))
        editor.model.synchronize()
        XCTAssertTrue(editor.model.canUndo)
        XCTAssertTrue(editor.model.canDeleteCurrent)
        editor.model.deleteCurrent()
        XCTAssertEqual(editor.session.document.presentation.mode, .off)
        editor.model.perform { $0.undo() }
        XCTAssertEqual(editor.session.document.presentation.mode, .lens)
        editor.model.perform { $0.undo() }
        XCTAssertEqual(editor.session.document.presentation.mode, .off)
    }

    func testEffectSelectionControlsAreAboveLivePixelsWithoutConsumingInput() throws {
        let suite = "effect-selection-\(UUID())"
        let model = WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let region = Element(tool: .blur, frame: Rect(x: 100, y: 100, width: 200, height: 150))
        try session.document.add(region)
        session.document.select([region.id])
        let presentation = try XCTUnwrap(session.presentation)
        presentation.refreshSelectionOverlay()
        XCTAssertGreaterThan(try XCTUnwrap(presentation.selectionOverlayLevel).rawValue, NSWindow.Level.floating.rawValue)
        XCTAssertTrue(presentation.selectionOverlayIgnoresMouseEvents)
        session.document.select([])
        presentation.refreshSelectionOverlay()
        XCTAssertNil(presentation.selectionOverlayLevel)
    }

    func testRoughnessAndEdgesIconsAreHandDrawnStrokes() {
        XCTAssertEqual(InspectorGlyph.roughness(0).count, 1)
        XCTAssertEqual(InspectorGlyph.roughness(2).count, 2)
        XCTAssertEqual(InspectorGlyph.roughness(2).first, InspectorGlyph.roughness(0).first)
        let wild = InspectorGlyph.roughness(6)
        XCTAssertEqual(wild.count, 2)
        XCTAssertNotEqual(wild, InspectorGlyph.roughness(2))
        XCTAssertTrue(wild.allSatisfy { $0.segments.count == 2 })
        XCTAssertTrue(InspectorGlyph.roughness(6).allSatisfy { !$0.dotted })
        for rounded in [false, true] {
            let edges = InspectorGlyph.edges(rounded: rounded)
            XCTAssertEqual(edges.map(\.dotted), [true, false])
            let corner = edges[1].segments
            XCTAssertEqual(corner.contains { if case .curve = $0 { return true }; return false }, rounded)
        }
        XCTAssertEqual(Style().roughness, 0)
    }
}
