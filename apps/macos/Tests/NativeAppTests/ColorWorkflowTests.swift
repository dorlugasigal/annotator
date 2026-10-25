import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ColorWorkflowTests: XCTestCase {
    func testPaletteFitsItsContentAndOnlyScrollsOnShortScreens() async throws {
        let suite = "palette-height-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.tool = .rectangle
        // Solid paint, so the palette shows the solid swatches and the hex field.
        model.applyStyle { $0.setPaintColor(.coral, fill: false); $0.setFillEnabled(true) }
        NativeAccessibilityNode.enable()
        for height in [CGFloat(900), 300] {
            let host = NSHostingView(rootView: InspectorColorPalette(
                model: model, fill: true, done: {}, availableHeight: height))
            let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 284, height: height),
                                styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.contentView = host
            panel.orderFrontRegardless()
            defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
            try await Task.sleep(for: .milliseconds(150))
            panel.setContentSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
            let nodes = NativeAccessibilityNode.nodes(in: host)
            XCTAssertLessThanOrEqual(host.fittingSize.height, height)
            let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
                .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(
                to: output.appendingPathComponent(height == 900 ? "filled-palette-natural.png" : "filled-palette-short.png"))
            if height == 900 {
                XCTAssertLessThan(host.fittingSize.height, 650)
                XCTAssertFalse(nodes.contains { $0.accessibilityRole() == .scrollArea })
                let bounds = panel.convertToScreen(host.convert(host.bounds, to: nil)).insetBy(dx: -1, dy: -1)
                for label in ["Color wheel", "Pick from screen", "Done", "Fill hex color and alpha"] {
                    let node: NativeAccessibilityNode
                    if label == "Color wheel" {
                        let group = try XCTUnwrap(nodes.first { $0.accessibilityIdentifier() == "inline-color-wheel" })
                        node = try XCTUnwrap(NativeAccessibilityNode.nodes(in: group.object).first {
                            $0.accessibilityRole() == .disclosureTriangle || $0.accessibilityRole() == .button
                        })
                    } else {
                        node = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == label })
                    }
                    XCTAssertTrue(bounds.contains(node.accessibilityFrame()),
                                  "\(label) must not be clipped: \(node.accessibilityFrame()) in \(bounds)")
                }
            } else {
                XCTAssertTrue(nodes.contains { $0.accessibilityRole() == .scrollArea })
            }
        }
    }

    func testRealInspectorKeepsWheelInlineAndOpenAfterFirstFillChanges() async throws {
        let suite = "wheel-handoff-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        session.panel.orderOut(nil)
        let shape = Element(tool: .rectangle, frame: Rect(x: 50, y: 50, width: 180, height: 90))
        session.document = try CanvasDocument(elements: [shape])
        session.document.select([shape.id])
        model.setInspectorPinned(true)
        model.synchronize()
        let host = NSHostingView(rootView: ToolbarView(model: model, availableSize: CGSize(width: 900, height: 900)))
        let toolbar = NSPanel(contentRect: CGRect(x: 100, y: 300, width: 900, height: 400),
                              styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        toolbar.isReleasedWhenClosed = false
        toolbar.level = PresentationWindowLevels.toolbar(screenZoom: false)
        toolbar.contentView = host
        toolbar.orderFrontRegardless()
        defer { toolbar.orderOut(nil); toolbar.contentView = nil; toolbar.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(150))
        let fill = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Fill color"
        })
        XCTAssertTrue(fill.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(150))
        let palette = try XCTUnwrap(NSApp.windows.filter(\.isVisible).compactMap(\.contentView).first {
            NativeAccessibilityNode.nodes(in: $0).contains { $0.accessibilityIdentifier() == "inline-color-wheel" }
        })
        let paletteWindow = try XCTUnwrap(palette.window)
        let group = try XCTUnwrap(NativeAccessibilityNode.nodes(in: palette).first {
            $0.accessibilityIdentifier() == "inline-color-wheel"
        })
        let disclosure = try XCTUnwrap(NativeAccessibilityNode.nodes(in: group.object).first {
            $0.accessibilityRole() == .disclosureTriangle || $0.accessibilityRole() == .button
        })
        XCTAssertTrue(disclosure.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertNil(model.style.fill, "Expanding the wheel must not change the drawing.")
        let wheel = try XCTUnwrap(NativeAccessibilityNode.nodes(in: palette).first {
            $0.accessibilityLabel() == "Hue and saturation wheel"
        })
        let frame = wheel.accessibilityFrame()
        XCTAssertTrue(paletteWindow.frame.contains(frame))
        let hueControl = try XCTUnwrap(NativeAccessibilityNode.nodes(in: palette).first {
            $0.accessibilityRole() == .slider && $0.accessibilityLabel() == "Color hue"
        })
        _ = hueControl.accessibilityPerformIncrement()
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(paletteWindow.isVisible, "A first fill must not rebuild the host and dismiss its palette.")
        XCTAssertTrue(palette.window === paletteWindow)
        XCTAssertFalse(NSApp.windows.contains { $0 is NSColorPanel && $0.isVisible })
        XCTAssertEqual(model.style.fill?.alpha, 0.4)
        XCTAssertEqual(model.style.color, shape.style.color)
        XCTAssertNotEqual(model.style.fill?.green, shape.style.color.green)
        XCTAssertTrue(session.document.isEditing)
        XCTAssertTrue(NativeAccessibilityNode.nodes(in: palette).contains { $0.accessibilityLabel() == "Color brightness" })
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let bitmap = try XCTUnwrap(palette.bitmapImageRepForCachingDisplay(in: palette.bounds))
        palette.cacheDisplay(in: palette.bounds, to: bitmap)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: output.appendingPathComponent("inline-color-wheel.png"))
        let done = try XCTUnwrap(NativeAccessibilityNode.nodes(in: palette).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Done"
        })
        XCTAssertTrue(done.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertFalse(paletteWindow.isVisible)
        model.undo()
        XCTAssertEqual(session.document.elements, [shape])
        XCTAssertFalse(session.document.canUndo)
    }

    func testGradientPaletteChangesOnlyTheSelectedPaintAndUndoesAsOneEdit() async throws {
        let suite = "gradient-palette-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        session.panel.ignoresMouseEvents = true
        let shape = Element(tool: .rectangle, frame: Rect(x: 50, y: 50, width: 180, height: 90))
        session.document = try CanvasDocument(elements: [shape])
        session.document.select([shape.id]); model.synchronize()
        let host = NSHostingView(rootView: InspectorColorPalette(model: model, fill: true, done: {}))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 284, height: 500),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let solid = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel()?.hasPrefix("Blue, hex") == true
        })
        let solidSize = solid.accessibilityFrame().size
        let tab = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityLabel() == "Gradient" })
        // AppKit's segmented picker can change selection while returning false.
        _ = tab.accessibilityPerformPress()
        try await Task.sleep(for: .milliseconds(100))
        let preset = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityLabel() == "Spectrum gradient" })
        XCTAssertEqual(preset.accessibilityFrame().width, solidSize.width, accuracy: 1)
        XCTAssertEqual(preset.accessibilityFrame().height, solidSize.height, accuracy: 1)
        XCTAssertFalse(NativeAccessibilityNode.nodes(in: host).contains {
            $0.accessibilityRole() == .staticText && $0.accessibilityLabel() == "Spectrum"
        })
        model.beginStyleEdit()
        XCTAssertTrue(preset.accessibilityPerformPress())
        model.applyStyle { $0.opacity = 0.5 }
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(model.style.opacity, 0.5)
        let reset = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityLabel() == "Reset object opacity"
        })
        XCTAssertTrue(reset.accessibilityPerformPress())
        XCTAssertEqual(model.style.opacity, 1)
        model.finishStyleEdit()
        XCTAssertEqual(model.style.fillGradient, GradientPreset.spectrum.gradient)
        XCTAssertEqual(model.style.fill?.alpha, 0.4)
        XCTAssertEqual(model.style.color, shape.style.color)
        model.undo()
        XCTAssertEqual(session.document.elements, [shape])
    }

    func testGradientLabelUsesOwnerPaintWhileTypingAndKeepsExplicitOverrides() throws {
        let suite = "gradient-label-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let background = try PNGExporter.image(elements: [], size: CGSize(width: 640, height: 300), background: .ink)
        let fixture = NativeCanvasFixture(image: background, defaults: defaults)
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        var shape = Element(tool: .rectangle, frame: Rect(x: 60, y: 60, width: 440, height: 110))
        shape.style.setGradient(GradientPreset.sunset.gradient, fill: false)
        shape.style.setGradient(GradientPreset.sunset.gradient, fill: true)
        fixture.session.document = try CanvasDocument(elements: [shape])
        fixture.model.choose(.text)
        let canvas = fixture.session.canvas
        let event = try XCTUnwrap(NSEvent.mouseEvent(with: .leftMouseDown,
            location: canvas.convert(shape.frame.center.cgPoint, to: nil), modifierFlags: [],
            timestamp: 0, windowNumber: fixture.session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: 1, pressure: 1))
        canvas.mouseDown(with: event)
        let editor = try XCTUnwrap(canvas.subviews.compactMap { $0 as? NSTextView }.first)
        editor.insertText("Make your point", replacementRange: NSRange(location: NSNotFound, length: 0))
        canvas.textDidChange(Notification(name: NSText.didChangeNotification, object: editor))
        let layout = try XCTUnwrap(editor.layoutManager as? PaintTextLayoutManager)
        XCTAssertEqual(layout.gradient, shape.style.strokeGradient)
        XCTAssertEqual(layout.paintBounds.width, shape.frame.width, accuracy: 0.01)
        XCTAssertEqual(fixture.session.document.elements[0].label?.followsStroke, true)
        canvas.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(canvas.bitmapImageRepForCachingDisplay(in: canvas.bounds))
        canvas.cacheDisplay(in: canvas.bounds, to: bitmap)
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: output.appendingPathComponent("gradient-editing.png"))
        fixture.model.applyStyle { $0.bold = true }
        XCTAssertEqual(fixture.session.document.elements[0].label?.followsStroke, true)
        fixture.model.applyStyle { $0.setPaintColor(.blue, fill: false) }
        XCTAssertNil(layout.gradient)
        XCTAssertEqual(fixture.session.document.elements[0].label?.followsStroke, false)
        fixture.model.matchLabelToStroke()
        XCTAssertEqual(layout.gradient, shape.style.strokeGradient)
        XCTAssertEqual(fixture.session.document.elements[0].label?.followsStroke, true)
        fixture.model.applyStyle { $0.setPaintColor(.blue, fill: false) }
        canvas.commitTextIfNeeded()
        XCTAssertEqual(fixture.session.document.elements[0].style, shape.style)
        XCTAssertEqual(fixture.session.document.elements[0].label?.style.color, .blue)
        fixture.model.undo()
        XCTAssertNil(fixture.session.document.elements[0].label)
    }

    func testInlineWheelColorConversionAndPointerMapping() {
        for color in [RGBA.ink, .coral, .blue, .white, RGBA(0, 0, 0), RGBA(0.1, 0.75, 0.35)] {
            let restored = WheelColor(color).color
            XCTAssertEqual(restored.red, color.red, accuracy: 0.00001)
            XCTAssertEqual(restored.green, color.green, accuracy: 0.00001)
            XCTAssertEqual(restored.blue, color.blue, accuracy: 0.00001)
        }
        var value = WheelColor(.white)
        value.move(to: CGPoint(x: 176, y: 88), size: CGSize(width: 176, height: 176))
        XCTAssertEqual(value.color, RGBA(1, 0, 0))
        value.move(to: CGPoint(x: 0, y: 88), size: CGSize(width: 176, height: 176))
        XCTAssertEqual(value.color, RGBA(0, 1, 1))
        value.move(to: CGPoint(x: 88, y: 88), size: CGSize(width: 176, height: 176))
        XCTAssertEqual(value.color, .white)
    }

    func testPointerSettingsPersistAndRejectInvalidSizes() throws {
        let suite = "pointer-settings-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        var settings = model.cursorHighlightSettings
        settings.shape = .roundedSquare
        settings.diameter = 76
        settings.color.alpha = 0.5
        settings.visibility = .clicks
        model.setCursorHighlight(settings)
        let restored = WorkspaceModel(defaults: defaults)
        defer { restored.closeAll() }
        XCTAssertEqual(restored.cursorHighlightSettings, settings)
        var invalid = settings; invalid.diameter = .infinity
        model.setCursorHighlight(invalid)
        XCTAssertEqual(model.cursorHighlightSettings, settings)
        XCTAssertNotNil(model.errorMessage)
    }

    func testFillToggleCopiesTheStrokeGradientWithoutChangingStrokeOpacity() async throws {
        let suite = "matching-fill-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.choose(.rectangle)
        model.active?.panel.orderOut(nil)
        model.applyStyle { $0.setGradient(GradientPreset.ocean.gradient, fill: false); $0.color.alpha = 0.8 }
        let host = NSHostingView(rootView: InspectorColorPalette(model: model, fill: true, done: {}))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 284, height: 500),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host; panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertNil(model.style.fill, "Opening the palette must not add a fill.")
        let toggle = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .checkBox && $0.accessibilityLabel() == "Fill shape"
        })
        XCTAssertTrue(toggle.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(model.style.fillGradient, GradientPreset.ocean.gradient)
        XCTAssertEqual(model.style.fill?.alpha, 0.4)
        XCTAssertEqual(model.style.color.alpha, 0.8)
        XCTAssertTrue(NativeAccessibilityNode.nodes(in: host).contains {
            $0.accessibilityLabel() == "Ocean gradient"
        })
    }
}
