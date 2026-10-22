import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ShapeLabelInteractionTests: XCTestCase {
    private func fixture() throws -> (NativeCanvasFixture, UserDefaults, String) {
        let suite = "shape-label-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
        return (NativeCanvasFixture(image: image, defaults: defaults), defaults, suite)
    }

    private func click(_ point: Point, count: Int, in fixture: NativeCanvasFixture) throws {
        let canvas = fixture.session.canvas
        let event = try XCTUnwrap(NSEvent.mouseEvent(with: .leftMouseDown,
            location: canvas.convert(point.cgPoint, to: nil), modifierFlags: [], timestamp: 0,
            windowNumber: fixture.session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: count, pressure: 1))
        canvas.mouseDown(with: event)
    }

    private func editor(in fixture: NativeCanvasFixture) throws -> NSTextView {
        try XCTUnwrap(fixture.session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
    }

    func testEmptySpaceDoubleClickUsesSavedTextSettingsAndUpdatesProperties() throws {
        let (fixture, defaults, suite) = try fixture()
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        fixture.model.choose(.text)
        fixture.model.applyStyle {
            $0.fontSize = 48
            $0.fontName = "monospace"
            $0.bold = true
            $0.alignment = .right
            $0.color = .blue
            $0.opacity = 0.6
        }
        let expected = fixture.model.style
        fixture.model.choose(.rectangle)
        fixture.model.applyStyle { $0.color = .coral; $0.fontSize = 16 }
        fixture.model.choose(.select)
        XCTAssertNotEqual(fixture.model.style, expected)
        try click(Point(100, 100), count: 2, in: fixture)
        let text = try editor(in: fixture)
        XCTAssertEqual(fixture.session.document.elements.first?.style, expected)
        XCTAssertEqual(fixture.model.style, expected)
        XCTAssertEqual(fixture.model.effectiveTool, .text)
        XCTAssertEqual(fixture.model.selectionCount, 1)
        XCTAssertEqual(text.font, NativeTextFont.resolve(expected))
        XCTAssertEqual(text.alignment, .right)
        XCTAssertEqual(text.textColor, expected.color.nsColor.withAlphaComponent(expected.color.alpha * expected.opacity))
        text.insertText("Saved text style", replacementRange: NSRange(location: NSNotFound, length: 0))
        fixture.session.canvas.commitTextIfNeeded()
        XCTAssertEqual(fixture.session.document.elements.first?.style, expected)
        fixture.model.undo()
        XCTAssertTrue(fixture.session.document.elements.isEmpty)
        fixture.model.redo()
        XCTAssertEqual(fixture.session.document.elements.first?.style, expected)
    }

    func testDoubleClickStyleChangesAreSharedWithTextToolAndPersisted() throws {
        let (fixture, defaults, suite) = try fixture()
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        fixture.model.choose(.select)
        try click(Point(100, 100), count: 2, in: fixture)
        _ = try editor(in: fixture)
        fixture.model.applyStyle { $0.fontSize = 52; $0.bold = true; $0.color = .blue }
        let expected = fixture.model.style
        fixture.session.canvas.commitTextIfNeeded()
        XCTAssertTrue(fixture.session.document.elements.isEmpty)
        fixture.model.choose(.text)
        XCTAssertEqual(fixture.model.style, expected)
        try click(Point(200, 100), count: 1, in: fixture)
        XCTAssertEqual(fixture.session.document.elements.first?.style, expected)
        let text = try editor(in: fixture)
        text.insertText("Same text tool", replacementRange: NSRange(location: NSNotFound, length: 0))
        fixture.session.canvas.commitTextIfNeeded()
        fixture.model.choose(.select)
        try click(Point(100, 250), count: 2, in: fixture)
        XCTAssertEqual(fixture.session.document.elements.last?.style, expected)
        fixture.session.canvas.commitTextIfNeeded()
        let data = try XCTUnwrap(defaults.data(forKey: "workspace-preferences-v1"))
        XCTAssertEqual(try WorkspacePreferences.read(data).toolStyles[Tool.text.rawValue], expected)
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
        let restored = NativeCanvasFixture(image: image, defaults: defaults)
        defer { restored.close() }
        restored.model.choose(.select)
        try click(Point(100, 100), count: 2, in: restored)
        XCTAssertEqual(restored.session.document.elements.first?.style, expected)
        XCTAssertEqual(try editor(in: restored).font, NativeTextFont.resolve(expected))
        restored.session.canvas.commitTextIfNeeded()
    }

    func testNewTextDoesNotInheritPreviousSelectionAndExistingTextKeepsItsStyle() throws {
        let (fixture, defaults, suite) = try fixture()
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        fixture.model.choose(.text)
        fixture.model.applyStyle { $0.fontSize = 42; $0.color = .blue }
        let expected = fixture.model.style
        var original = Element(tool: .text, frame: Rect(x: 50, y: 50, width: 180, height: 50), text: "Original")
        original.style.fontSize = 20
        original.style.color = .coral
        fixture.session.document = try CanvasDocument(elements: [original])
        fixture.model.choose(.select)
        fixture.session.document.select([original.id])
        fixture.model.synchronize()
        XCTAssertEqual(fixture.model.style, original.style)
        try click(Point(300, 250), count: 2, in: fixture)
        XCTAssertEqual(fixture.session.document.elements.last?.style, expected)
        fixture.session.canvas.commitTextIfNeeded()
        try click(Point(80, 70), count: 2, in: fixture)
        let text = try editor(in: fixture)
        XCTAssertEqual(text.font, NativeTextFont.resolve(original.style))
        XCTAssertEqual(fixture.model.style, original.style)
        fixture.session.canvas.commitTextIfNeeded()
        XCTAssertEqual(fixture.session.document.elements, [original])
        XCTAssertEqual(fixture.model.preferences.toolStyles[Tool.text.rawValue], expected)
    }

    func testDoubleClickInsideFilledAndEmptyShapesCreatesOneCenteredOwnerLabel() throws {
        let (fixture, defaults, suite) = try fixture()
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        for tool in [Tool.rectangle, .ellipse, .database, .databaseStack] {
            for filled in [false, true] {
                var shape = Element(tool: tool, frame: Rect(x: 80, y: 60, width: 220, height: 140))
                if filled { shape.style.fill = .blue }
                fixture.session.document = try CanvasDocument(elements: [shape])
                fixture.model.choose(.select)
                try click(Point(140, 110), count: 2, in: fixture)
                let text = try editor(in: fixture)
                XCTAssertEqual(fixture.session.canvas.editingShapeLabelID, shape.id)
                XCTAssertEqual(fixture.model.effectiveTool, .text)
                XCTAssertEqual(fixture.session.document.elements.count, 1)
                XCTAssertFalse(text.drawsBackground)
                text.insertText("Orders", replacementRange: NSRange(location: NSNotFound, length: 0))
                fixture.session.canvas.textDidChange(Notification(name: NSText.didChangeNotification, object: text))
                let center = text.convert(CGPoint(x: text.bounds.midX, y: text.bounds.midY), to: fixture.session.canvas)
                XCTAssertEqual(center.x, shape.frame.center.x, accuracy: 0.01)
                XCTAssertEqual(center.y, shape.frame.center.y, accuracy: 0.01)
                fixture.session.canvas.commitTextIfNeeded()
                XCTAssertEqual(fixture.session.document.elements.count, 1)
                XCTAssertEqual(fixture.session.document.elements[0].label?.text, "Orders")
                XCTAssertEqual(fixture.session.document.elements[0].frame, shape.frame)
                XCTAssertEqual(fixture.session.document.elements[0].style, shape.style)
                fixture.model.undo()
                XCTAssertNil(fixture.session.document.elements[0].label)
                fixture.model.redo()
                XCTAssertEqual(fixture.session.document.elements[0].label?.text, "Orders")
            }
        }
    }

    func testTextToolBindsToShapeAndTextStyleDoesNotChangeItsStroke() throws {
        let (fixture, defaults, suite) = try fixture()
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        let shape = Element(tool: .database, frame: Rect(x: 80, y: 60, width: 220, height: 140))
        fixture.session.document = try CanvasDocument(elements: [shape])
        fixture.model.choose(.text)
        try click(Point(140, 110), count: 1, in: fixture)
        let text = try editor(in: fixture)
        text.insertText("Customer records", replacementRange: NSRange(location: NSNotFound, length: 0))
        fixture.model.applyStyle { $0.fontSize = 44; $0.color = .blue; $0.bold = true }
        fixture.session.canvas.commitTextIfNeeded()
        let owner = fixture.session.document.elements[0]
        XCTAssertEqual(owner.style, shape.style)
        XCTAssertEqual(owner.label?.style.fontSize, 44)
        XCTAssertEqual(owner.label?.style.color, .blue)
        XCTAssertEqual(owner.frame.center, shape.frame.center)
        XCTAssertGreaterThan(owner.frame.width, shape.frame.width)
        try fixture.session.document.editSelection { $0.translate(by: Point(25, 40)) }
        let moved = fixture.session.document.elements[0]
        XCTAssertEqual(ShapeLabelLayout.textElement(for: moved)?.frame.center, moved.frame.center)
        try fixture.session.document.editSelection { $0.resize(to: Rect(x: moved.frame.x, y: moved.frame.y, width: moved.frame.width*1.5, height: moved.frame.height*1.5)) }
        let resized = fixture.session.document.elements[0]
        XCTAssertEqual(ShapeLabelLayout.textElement(for: resized)?.frame.center, resized.frame.center)
        XCTAssertEqual(resized.label?.style.fontSize, 66)
    }

    func testCancelLockedObjectsAndRotatedLabelCenters() throws {
        let (fixture, defaults, suite) = try fixture()
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        var shape = Element(tool: .database, frame: Rect(x: 80, y: 60, width: 220, height: 140))
        shape.rotation = 0.4
        fixture.session.document = try CanvasDocument(elements: [shape])
        fixture.model.choose(.select)
        try click(shape.frame.center, count: 2, in: fixture)
        let text = try editor(in: fixture)
        text.insertText("Orders", replacementRange: NSRange(location: NSNotFound, length: 0))
        fixture.session.canvas.textDidChange(Notification(name: NSText.didChangeNotification, object: text))
        let center = text.convert(CGPoint(x: text.bounds.midX, y: text.bounds.midY), to: fixture.session.canvas)
        XCTAssertEqual(center.x, shape.frame.center.x, accuracy: 0.01)
        XCTAssertEqual(center.y, shape.frame.center.y, accuracy: 0.01)
        fixture.session.canvas.cancelGesture()
        XCTAssertEqual(fixture.session.document.elements, [shape])
        shape.locked = true
        fixture.session.document = try CanvasDocument(elements: [shape])
        try click(shape.frame.center, count: 2, in: fixture)
        XCTAssertFalse(fixture.session.canvas.hasTextEditor)
        XCTAssertNil(fixture.session.document.elements[0].label)
    }
}
