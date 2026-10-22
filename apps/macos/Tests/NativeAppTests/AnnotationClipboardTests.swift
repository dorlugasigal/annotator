import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class AnnotationClipboardTests: XCTestCase {
    private var clipboard: NSPasteboard!
    private var suite = ""
    private var defaults: UserDefaults!
    private var fixture: NativeCanvasFixture!

    override func setUp() async throws {
        clipboard = NSPasteboard.withUniqueName()
        suite = "annotation-clipboard-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 600, height: 400), background: .white)
        fixture = NativeCanvasFixture(image: image, defaults: defaults, pasteboard: clipboard)
        fixture.model.choose(.select)
        pointer(at: nil)
    }

    override func tearDown() async throws {
        fixture.close()
        defaults.removePersistentDomain(forName: suite)
        clipboard.releaseGlobally()
    }

    private var document: CanvasDocument { fixture.session.document }

    private func pointer(at point: Point?) {
        let canvas = fixture.session.canvas
        let panel = fixture.session.panel
        fixture.model.screenPointer = {
            guard let point else { return CGPoint(x: -50_000, y: -50_000) }
            return panel.convertPoint(toScreen: canvas.convert(point.cgPoint, to: nil))
        }
    }

    private func key(_ character: String, keyCode: UInt16, in window: NSWindow) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command, timestamp: 0,
            windowNumber: window.windowNumber, context: nil, characters: character,
            charactersIgnoringModifiers: character, isARepeat: false, keyCode: keyCode))
    }

    private func mouse(_ type: NSEvent.EventType, _ point: Point, count: Int = 1) throws -> NSEvent {
        let canvas = fixture.session.canvas
        return try XCTUnwrap(NSEvent.mouseEvent(with: type, location: canvas.convert(point.cgPoint, to: nil),
            modifierFlags: [], timestamp: 0, windowNumber: fixture.session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: count, pressure: 1))
    }

    func testCommandCAndCommandVCopyAnyObjectWithCascadingPlacement() throws {
        var box = Element(tool: .rectangle, frame: Rect(x: 40, y: 40, width: 120, height: 80))
        box.label = ShapeLabel(text: "Orders")
        let note = Element(tool: .text, frame: Rect(x: 300, y: 60, width: 80, height: 24), text: "Hello")
        fixture.session.document = try CanvasDocument(elements: [box, note])
        fixture.session.document.select([box.id])
        let canvas = fixture.session.canvas
        canvas.keyDown(with: try key("c", keyCode: 8, in: fixture.session.panel))
        XCTAssertNotNil(clipboard.data(forType: WorkspaceModel.annotationPasteboardType))
        canvas.keyDown(with: try key("v", keyCode: 9, in: fixture.session.panel))
        XCTAssertEqual(document.elements.count, 3)
        let first = try XCTUnwrap(document.elements.last)
        XCTAssertNotEqual(first.id, box.id)
        XCTAssertEqual(document.selection, [first.id])
        XCTAssertEqual(first.frame.origin, Point(58, 58))
        XCTAssertEqual(first.label?.text, "Orders")
        XCTAssertEqual(fixture.model.tool, .select)

        let toolbar = AnnotationPanel(contentRect: NSRect(x: 0, y: 0, width: 200, height: 80),
                                      styleMask: [.titled], backing: .buffered, defer: false)
        toolbar.imagePasteModel = fixture.model
        defer { toolbar.orderOut(nil) }
        XCTAssertTrue(toolbar.performKeyEquivalent(with: try key("v", keyCode: 9, in: toolbar)))
        XCTAssertEqual(document.elements.last?.frame.origin, Point(76, 76), "Repeated pastes step down and right.")

        pointer(at: Point(400, 250))
        fixture.model.paste()
        let placed = try XCTUnwrap(document.elements.last)
        XCTAssertEqual(placed.frame.center, Point(400, 250), "Paste lands at the pointer when it is away from the copy.")

        fixture.model.undo()
        XCTAssertEqual(document.elements.count, 4)

        fixture.session.document.select([note.id])
        XCTAssertTrue(toolbar.performKeyEquivalent(with: try key("x", keyCode: 7, in: toolbar)))
        XCTAssertFalse(document.elements.contains { $0.id == note.id })
        pointer(at: nil)
        fixture.model.paste()
        XCTAssertEqual(document.elements.last?.text, "Hello")
    }

    func testCopyWithoutSelectionFallsThroughAndImagesStillPaste() throws {
        fixture.session.document = try CanvasDocument(elements: [])
        XCTAssertFalse(fixture.model.copySelection())
        let image = try PNGExporter.data(elements: [], size: CGSize(width: 24, height: 16), background: .blue)
        clipboard.clearContents()
        XCTAssertTrue(clipboard.setData(image, forType: .png))
        fixture.model.paste()
        XCTAssertEqual(document.elements.map(\.tool), [.image])
    }

    func testLockedSelectionCopiesButDoesNotCut() throws {
        var box = Element(tool: .rectangle, frame: Rect(x: 40, y: 40, width: 120, height: 80))
        box.locked = true
        fixture.session.document = try CanvasDocument(elements: [box])
        fixture.session.document.select([box.id])
        fixture.model.synchronize()
        XCTAssertTrue(fixture.model.copySelection(cut: true))
        XCTAssertEqual(document.elements.count, 1)
        XCTAssertNil(clipboard.data(forType: WorkspaceModel.annotationPasteboardType))
        XCTAssertTrue(fixture.model.copySelection())
        fixture.model.paste()
        XCTAssertEqual(document.elements.count, 2)
        XCTAssertFalse(document.elements[1].locked, "Pasted copies are editable.")
    }

    func testDoubleClickOnTextInsideANewerShapeEditsThatText() throws {
        let text = Element(tool: .text, frame: Rect(x: 100, y: 100, width: 80, height: 30), text: "Label",
                           style: .defaults(for: .text))
        let box = Element(tool: .rectangle, frame: Rect(x: 60, y: 60, width: 220, height: 140))
        fixture.session.document = try CanvasDocument(elements: [text, box])
        let canvas = fixture.session.canvas
        canvas.mouseDown(with: try mouse(.leftMouseDown, Point(120, 110)))
        canvas.mouseUp(with: try mouse(.leftMouseUp, Point(120, 110)))
        XCTAssertEqual(document.selection, [text.id])
        XCTAssertEqual(fixture.model.effectiveTool, .text)
        canvas.mouseDown(with: try mouse(.leftMouseDown, Point(120, 110), count: 2))
        XCTAssertTrue(canvas.hasTextEditor)
        XCTAssertNil(canvas.editingShapeLabelID, "The text object is edited, not a new label on the box.")
        fixture.model.applyStyle { $0.fontSize = 48 }
        canvas.commitTextIfNeeded()
        XCTAssertEqual(document.elements.first { $0.id == text.id }?.style.fontSize, 48)
        XCTAssertNil(document.elements.first { $0.id == box.id }?.label)

        canvas.mouseDown(with: try mouse(.leftMouseDown, Point(250, 180), count: 2))
        XCTAssertEqual(canvas.editingShapeLabelID, box.id, "Empty box space still edits the box label.")
        canvas.cancelGesture()
    }

    func testArrowAttachHighlightMatchesTheAttachmentMade() throws {
        let left = Element(tool: .rectangle, frame: Rect(x: 40, y: 100, width: 100, height: 80))
        let right = Element(tool: .ellipse, frame: Rect(x: 360, y: 100, width: 100, height: 80))
        fixture.session.document = try CanvasDocument(elements: [left, right])
        fixture.model.choose(.arrow)
        let canvas = fixture.session.canvas
        canvas.mouseMoved(with: try mouse(.mouseMoved, Point(250, 250)))
        XCTAssertTrue(canvas.attachHints.isEmpty)
        canvas.mouseMoved(with: try mouse(.mouseMoved, Point(145, 141)))
        XCTAssertEqual(canvas.attachHints, [Anchor(elementID: left.id, relative: Point(1, 0.5))])

        canvas.mouseDown(with: try mouse(.leftMouseDown, Point(145, 141)))
        canvas.mouseDragged(with: try mouse(.leftMouseDragged, Point(250, 141)))
        XCTAssertEqual(canvas.attachHints.map(\.elementID), [left.id])
        canvas.mouseDragged(with: try mouse(.leftMouseDragged, Point(356, 139)))
        XCTAssertEqual(canvas.attachHints, [Anchor(elementID: left.id, relative: Point(1, 0.5)),
                                            Anchor(elementID: right.id, relative: Point(0, 0.5))])
        canvas.mouseUp(with: try mouse(.leftMouseUp, Point(356, 139)))
        XCTAssertTrue(canvas.attachHints.isEmpty)
        let arrow = try XCTUnwrap(document.elements.first { $0.tool == .arrow })
        XCTAssertEqual(arrow.startAnchor?.elementID, left.id)
        XCTAssertEqual(arrow.endAnchor?.elementID, right.id)

        canvas.mouseDown(with: try mouse(.leftMouseDown, Point(360, 140)))
        canvas.mouseDragged(with: try mouse(.leftMouseDragged, Point(300, 260)))
        XCTAssertTrue(canvas.attachHints.isEmpty)
        canvas.mouseDragged(with: try mouse(.leftMouseDragged, Point(412, 184)))
        XCTAssertEqual(canvas.attachHints, [Anchor(elementID: right.id, relative: Point(0.5, 1))])
        canvas.mouseUp(with: try mouse(.leftMouseUp, Point(412, 184)))
        let moved = try XCTUnwrap(document.elements.first { $0.tool == .arrow })
        XCTAssertEqual(moved.endAnchor, Anchor(elementID: right.id, relative: Point(0.5, 1)),
                       "Dropping a dragged arrow end on a highlighted point attaches it.")
        XCTAssertTrue(canvas.attachHints.isEmpty)
    }

    func testReviewEdgeCasesForCutLockedModeAndEndpointClicks() throws {
        let box = Element(tool: .rectangle, frame: Rect(x: 40, y: 40, width: 120, height: 80))
        var pinned = Element(tool: .ellipse, frame: Rect(x: 300, y: 40, width: 80, height: 80))
        pinned.locked = true
        fixture.session.document = try CanvasDocument(elements: [box, pinned])
        fixture.session.document.select([box.id, pinned.id])
        XCTAssertTrue(fixture.model.copySelection(cut: true))
        XCTAssertEqual(document.elements.count, 2, "A cut that includes a locked object does nothing.")
        XCTAssertNil(clipboard.data(forType: WorkspaceModel.annotationPasteboardType))

        fixture.model.interact()
        XCTAssertFalse(fixture.model.copySelection(), "Copy outside Draw mode leaves the key to other apps.")
        fixture.model.choose(.select)

        let arrow = Element(tool: .arrow, points: [Point(200, 250), Point(170, 82)])
        fixture.session.document = try CanvasDocument(elements: [box, arrow])
        fixture.session.document.select([arrow.id])
        let canvas = fixture.session.canvas
        canvas.mouseDown(with: try mouse(.leftMouseDown, Point(170, 82)))
        canvas.mouseUp(with: try mouse(.leftMouseUp, Point(170, 82)))
        XCTAssertFalse(document.canUndo, "Clicking an arrow end without dragging adds no undo step.")
        XCTAssertNil(document.elements[1].endAnchor)
    }

    func testCopyAcrossDisplaysKeepsTheirRelativePlacement() throws {
        let suite = "clipboard-displays-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let displays = [
            DesktopDisplay(id: 101, frame: CGRect(x: 0, y: 0, width: 640, height: 480), scale: 2),
            DesktopDisplay(id: 202, frame: CGRect(x: -800, y: 120, width: 800, height: 600), scale: 1)
        ]
        let model = WorkspaceModel(defaults: defaults, imagePasteboard: clipboard, desktopDisplays: { displays })
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        model.screenPointer = { CGPoint(x: -50_000, y: -50_000) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let here = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 50, height: 40))
        let there = Element(tool: .ellipse, frame: Rect(x: 30, y: 40, width: 50, height: 40))
        try first.document.add(here)
        try second.document.add(there)
        model.selectAll()
        XCTAssertTrue(model.selectionIsWorkspaceWide)
        XCTAssertTrue(model.copySelection())
        model.paste()
        let copies = first.document.elements.filter { first.document.selection.contains($0.id) }
        let a = try XCTUnwrap(copies.first { $0.tool == .rectangle })
        let b = try XCTUnwrap(copies.first { $0.tool == .ellipse })
        let offset = b.frame.origin - a.frame.origin
        let expectedX = second.panel.frame.minX - first.panel.frame.minX + 30 - 100
        let expectedY = first.panel.frame.maxY - second.panel.frame.maxY + 40 - 100
        XCTAssertEqual(offset.x, Double(expectedX), accuracy: 0.001)
        XCTAssertEqual(offset.y, Double(expectedY), accuracy: 0.001)
        XCTAssertEqual(second.document.elements.count, 1)
    }
}
