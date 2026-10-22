import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class StyleValueControlTests: XCTestCase {
    func testStartingDrawingClosesTheColorPickerWithoutDone() async throws {
        let suite = "palette-drawing-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        session.panel.ignoresMouseEvents = true
        model.choose(.ellipse)
        // Synthetic canvas events do not need a key desktop overlay.
        session.panel.orderOut(nil)
        let host = NSHostingView(rootView: InspectorColorPicker(model: model).padding(20))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 500, width: 200, height: 100),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let button = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Stroke color"
        })
        XCTAssertTrue(button.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(120))
        func paletteIsVisible() -> Bool {
            NSApp.windows.filter(\.isVisible).compactMap(\.contentView).flatMap {
                NativeAccessibilityNode.nodes(in: $0)
            }.contains { $0.accessibilityLabel() == "Stroke hex color and alpha" }
        }
        XCTAssertTrue(paletteIsVisible())
        func event(_ type: NSEvent.EventType, _ point: CGPoint) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point, to: nil),
                modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                windowNumber: session.panel.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        }
        session.canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 450, y: 300)))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 700, y: 450)))
        session.canvas.mouseUp(with: try event(.leftMouseUp, CGPoint(x: 700, y: 450)))
        try await Task.sleep(for: .milliseconds(300))
        XCTAssertFalse(paletteIsVisible())
        XCTAssertEqual(session.document.elements.count, 1)
        XCTAssertEqual(session.document.elements.first?.tool, .ellipse)
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    func testEffectSliderRangesAreToolSpecificWhilePreciseValuesRemainCompatible() {
        XCTAssertEqual(StyleParameter.effect.sliderRange(for: .blur), 0...20)
        XCTAssertEqual(StyleParameter.effect.sliderRange(for: .pixelate), 0...40)
        XCTAssertEqual(StyleParameter.effect.sliderRange(for: .distort), 2...100)
        XCTAssertEqual(StyleParameter.width.sliderRange(for: .blur), StyleParameter.width.range)
        XCTAssertEqual(StyleParameter.effect.parse("80.125"), 80.125)
    }

    func testPreciseStyleFieldsValidateRangesAndLocalizedDecimals() throws {
        for parameter in [StyleParameter.width, .textSize, .opacity, .effect] {
            XCTAssertEqual(parameter.parse(String(parameter.range.lowerBound)), parameter.range.lowerBound)
            XCTAssertEqual(parameter.parse(String(parameter.range.upperBound)), parameter.range.upperBound)
            XCTAssertNil(parameter.parse(String(parameter.range.lowerBound - 1)))
            XCTAssertNil(parameter.parse(String(parameter.range.upperBound + 1)))
            for invalid in ["", "oops", "nan", "inf", "-inf"] { XCTAssertNil(parameter.parse(invalid)) }
            XCTAssertEqual(parameter.parse(" 12,5 ", locale: Locale(identifier: "de_DE")), 12.5)
            var style = Style()
            parameter.apply(12.5, to: &style)
            XCTAssertEqual(parameter.value(in: style), 12.5, accuracy: 0.001)
            try style.validate()
        }
    }

    func testPreciseWidthCanCommitWithoutChangingOtherStyleProperties() throws {
        let suite = "precise-style-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let original = model.style
        let host = NSHostingController(rootView: StyleValueControl(model: model, parameter: .width).padding(12))
        let panel = NSPanel(contentRect: NSRect(x: 50, y: 50, width: 360, height: 80),
                            styleMask: [.titled], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentViewController = host
        defer { panel.contentViewController = nil; panel.close() }
        panel.makeKeyAndOrderFront(nil)
        settle()
        let field = try XCTUnwrap(textFields(in: host.view).first(where: { $0.isEditable }))
        XCTAssertTrue(panel.makeFirstResponder(field))
        settle()
        let editor = try XCTUnwrap(field.currentEditor() as? NSTextView)
        editor.selectAll(nil)
        editor.insertText("12.5", replacementRange: NSRange(location: NSNotFound, length: 0))
        settle()
        XCTAssertEqual(editor.string, "12.5")
        XCTAssertTrue(panel.makeFirstResponder(nil))
        settle()
        XCTAssertEqual(model.style.width, 12.5, accuracy: 0.001)
        XCTAssertEqual(model.style.color, original.color)
        XCTAssertEqual(model.style.opacity, original.opacity)
        XCTAssertTrue(panel.makeFirstResponder(field))
        settle()
        let invalidEditor = try XCTUnwrap(field.currentEditor() as? NSTextView)
        invalidEditor.selectAll(nil)
        invalidEditor.insertText("200", replacementRange: NSRange(location: NSNotFound, length: 0))
        settle()
        XCTAssertTrue(panel.makeFirstResponder(nil))
        settle()
        XCTAssertEqual(model.style.width, 12.5, accuracy: 0.001)
        XCTAssertEqual(field.stringValue, "200", "Invalid input should remain available for correction.")
    }

    func testDeletionAffordanceHonorsTheActualSelectedTarget() throws {
        let suite = "delete-affordance-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        var locked = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 80, height: 80))
        locked.locked = true
        let cover = Element(tool: .cover, frame: Rect(x: 200, y: 100, width: 80, height: 80))
        try session.document.add(locked)
        try session.document.add(cover)
        session.document.select([locked.id])
        model.synchronize()
        XCTAssertFalse(model.canDeleteCurrent)
        XCTAssertEqual(model.deleteActionLabel, "Unlock selection to delete")
        session.document.select([])
        model.synchronize()
        XCTAssertTrue(model.canDeleteCurrent)
        XCTAssertEqual(model.deleteActionLabel, "Clear canvas")
        model.deleteCurrent()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    private func settle() {
        RunLoop.current.run(until: Date().addingTimeInterval(0.06))
    }

    private func textFields(in view: NSView) -> [NSTextField] {
        (view as? NSTextField).map { [$0] } ?? view.subviews.flatMap { textFields(in: $0) }
    }
}
