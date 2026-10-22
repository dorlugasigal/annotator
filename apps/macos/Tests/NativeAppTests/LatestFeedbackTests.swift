import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class LatestFeedbackTests: XCTestCase {
    private func workspace() throws -> (NativeCanvasFixture, UserDefaults, String) {
        let suite = "latest-feedback-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        return (NativeCanvasFixture(image: image, defaults: defaults), defaults, suite)
    }

    private func event(_ type: NSEvent.EventType, _ point: Point, _ session: OverlaySession,
                       flags: NSEvent.ModifierFlags = []) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
            modifierFlags: flags, timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: 1, pressure: 1))
    }

    func testTextGrowsPastOldBoxWithoutLosingCharacters() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.text)
        editor.model.applyStyle { $0.fontName = "monospace"; $0.bold = true }
        let session = editor.session
        session.canvas.mouseDown(with: try event(.leftMouseDown, Point(100, 100), session))
        let text = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
        XCTAssertTrue(try XCTUnwrap(text.font).isFixedPitch)
        let content = String(repeating: "Unwrapped text ", count: 12)
        text.insertText(content, replacementRange: NSRange(location: NSNotFound, length: 0))
        XCTAssertGreaterThan(text.frame.width, 600)
        let layout = try XCTUnwrap(text.layoutManager)
        layout.ensureLayout(for: try XCTUnwrap(text.textContainer))
        var lineCount = 0
        layout.enumerateLineFragments(forGlyphRange: NSRange(location: 0, length: layout.numberOfGlyphs)) { _, _, _, _, _ in
            lineCount += 1
        }
        XCTAssertEqual(lineCount, 1, "The old fixed-width container must not keep wrapping the growing editor.")
        let height = text.frame.height
        text.insertText("\nSecond line", replacementRange: NSRange(location: NSNotFound, length: 0))
        XCTAssertGreaterThan(text.frame.height, height)
        session.canvas.commitTextIfNeeded()
        XCTAssertEqual(session.document.elements.first?.text, content + "\nSecond line")
        XCTAssertGreaterThan(try XCTUnwrap(session.document.elements.first).frame.width, 600)
    }

    func testShiftResizePreservesAspectAndOppositeCornerAcrossTools() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let image = try PNGExporter.data(elements: [], size: CGSize(width: 16, height: 16), background: .blue)
        for tool in Tool.allCases where tool != .select && tool != .eraser && !tool.isConnector {
            var item = Element(tool: tool, frame: Rect(x: 200, y: 200, width: 120, height: 60),
                               text: tool == .counter ? "1" : "Text")
            if tool.isFreehand {
                item.points = [Point(200, 200), Point(260, 260), Point(320, 200)]
            }
            if tool == .ruler { item.points = [Point(200, 200), Point(320, 260)] }
            if tool == .image { item.imagePNG = image }
            item.rotation = tool.isProtection ? 0 : .pi / 6
            session.document = try CanvasDocument(elements: [item])
            session.document.select([item.id])
            editor.model.choose(.select)
            let handle = Point(item.frame.maxX, item.frame.maxY).rotated(around: item.frame.center, by: item.rotation)
            let target = Point(item.frame.maxX + 90, item.frame.maxY + 15).rotated(around: item.frame.center, by: item.rotation)
            let anchor = item.frame.origin.rotated(around: item.frame.center, by: item.rotation)
            session.canvas.mouseDown(with: try event(.leftMouseDown, handle, session))
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, target, session, flags: .shift))
            session.canvas.mouseUp(with: try event(.leftMouseUp, target, session, flags: .shift))
            let result = try XCTUnwrap(session.document.elements.first)
            XCTAssertEqual(result.frame.width / result.frame.height, 2, accuracy: 0.001, tool.label)
            XCTAssertGreaterThan(result.frame.width, 120, tool.label)
            let newAnchor = result.frame.origin.rotated(around: result.frame.center, by: result.rotation)
            XCTAssertEqual(newAnchor.x, anchor.x, accuracy: 0.001, tool.label)
            XCTAssertEqual(newAnchor.y, anchor.y, accuracy: 0.001, tool.label)
            editor.model.undo()
            XCTAssertEqual(session.document.elements.first?.frame, item.frame)
        }

    }

    func testShiftEndpointResizeScalesTheWholeConnector() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        for tool in [Tool.line, .arrow] {
            let original = Element(tool: tool, points: [Point(200, 200), Point(260, 260), Point(320, 240)])
            session.document = try CanvasDocument(elements: [original])
            session.document.select([original.id])
            editor.model.choose(.select)
            session.canvas.mouseDown(with: try event(.leftMouseDown, Point(320, 240), session))
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(450, 350), session, flags: .shift))
            session.canvas.mouseUp(with: try event(.leftMouseUp, Point(450, 350), session, flags: .shift))
            let result = try XCTUnwrap(session.document.elements.first)
            XCTAssertEqual(result.frame.width / result.frame.height, original.frame.width / original.frame.height, accuracy: 0.001)
            XCTAssertEqual(result.points.first, original.points.first)
            XCTAssertGreaterThan(result.frame.width, original.frame.width)
        }
    }

    func testCoverModeChangesSameRegionAndClearRestoresEverythingWithUndo() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let original = Element(tool: .cover, frame: Rect(x: 150, y: 100, width: 200, height: 140))
        try session.document.add(original)
        session.document.select([original.id])
        for mode in [Tool.blur, .pixelate, .distort] {
            editor.model.setCoverMode(mode)
            XCTAssertEqual(session.document.elements.count, 1)
            XCTAssertEqual(session.document.elements.first?.id, original.id)
            XCTAssertEqual(session.document.elements.first?.frame, original.frame)
            XCTAssertEqual(session.document.elements.first?.tool, mode)
            editor.model.undo()
            XCTAssertEqual(session.document.elements.first?.tool, .cover)
        }
        var locked = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 70, height: 40))
        locked.locked = true
        try session.document.add(locked)
        let before = session.document.elements
        editor.model.clearCanvas()
        XCTAssertTrue(session.document.elements.isEmpty)
        editor.model.undo()
        XCTAssertEqual(session.document.elements, before)
    }

    func testHighlighterDefaultsAndVerticalNib() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.highlighter)
        XCTAssertEqual(editor.model.style.color, RGBA(0.93, 1, 0.05))
        let cursor = ToolCursorFactory.cursor(tool: .highlighter, width: 4, style: .toolPreview)
        let bitmap = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(cursor.image.tiffRepresentation)))
        var minX = bitmap.pixelsWide, minY = bitmap.pixelsHigh, maxX = 0, maxY = 0
        for y in 0..<bitmap.pixelsHigh {
            for x in 0..<bitmap.pixelsWide where (bitmap.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.01 {
                minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
            }
        }
        XCTAssertGreaterThan(maxY - minY, maxX - minX)
        editor.model.applyStyle { $0.color = .blue }
        editor.model.choose(.pen); editor.model.choose(.highlighter)
        XCTAssertEqual(editor.model.style.color, .blue)
    }

    func testShiftHighlighterSnapsAndCommitsOneUndoStep() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.highlighter)
        let session = editor.session
        let start = Point(200, 200)
        for target in [Point(400, 220), Point(215, 400), Point(370, 350), Point(40, 180)] {
            session.canvas.mouseDown(with: try event(.leftMouseDown, start, session, flags: .shift))
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(240, 230), session, flags: .shift))
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, target, session, flags: .shift))
            let preview = try XCTUnwrap(session.document.elements.last)
            XCTAssertEqual(preview.points.count, 2)
            session.canvas.mouseUp(with: try event(.leftMouseUp, target, session, flags: .shift))
            let result = try XCTUnwrap(session.document.elements.last)
            XCTAssertEqual(result.tool, .highlighter)
            XCTAssertEqual(result.points, preview.points)
            XCTAssertEqual(result.points.first, start)
            let delta = try XCTUnwrap(result.points.last) - start
            let steps = atan2(delta.y, delta.x) / (.pi / 4)
            XCTAssertEqual(steps, steps.rounded(), accuracy: 0.0001)
            try result.validate()
            editor.model.undo()
            XCTAssertTrue(session.document.elements.isEmpty)
            editor.model.redo()
            XCTAssertEqual(session.document.elements.last?.points, result.points)
            editor.model.undo()
        }
    }

    func testHighlighterShiftTransitionsFinalEndpointAndCancellation() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.highlighter)
        let session = editor.session
        let start = Point(200, 200)
        session.canvas.mouseDown(with: try event(.leftMouseDown, start, session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(230, 260), session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(300, 210), session, flags: .shift))
        XCTAssertEqual(session.document.elements.last?.points.count, 2)
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(340, 260), session))
        XCTAssertEqual(session.document.elements.last?.points.count, 3)
        session.canvas.mouseUp(with: try event(.leftMouseUp, Point(400, 220), session, flags: .shift))
        let result = try XCTUnwrap(session.document.elements.last)
        XCTAssertEqual(result.points.count, 2)
        XCTAssertEqual(try XCTUnwrap(result.points.last).y, start.y, accuracy: 0.0001)
        XCTAssertGreaterThan(try XCTUnwrap(result.points.last).x, 400)
        editor.model.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
        session.canvas.mouseDown(with: try event(.leftMouseDown, start, session, flags: .shift))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(300, 210), session, flags: .shift))
        session.canvas.cancelGesture()
        XCTAssertTrue(session.document.elements.isEmpty)
        editor.model.redo()
        XCTAssertEqual(session.document.elements.last?.points, result.points)
    }

    func testDrawingFootprintIgnoresLegacyCursorStylesAndTracksFractionalWidth() throws {
        for tool in [Tool.pen, .highlighter, .eraser] {
            let footprint = ToolCursorFactory.cursor(tool: tool, width: 7, style: .toolPreview)
            for style in ToolCursorStyle.allCases {
                XCTAssertTrue(footprint === ToolCursorFactory.cursor(tool: tool, width: 7, style: style))
            }
        }
        let narrow = ToolCursorFactory.cursor(tool: .highlighter, width: 7.1, style: .toolPreview)
        let wide = ToolCursorFactory.cursor(tool: .highlighter, width: 7.4, style: .toolPreview)
        XCTAssertGreaterThan(wide.image.size.height, narrow.image.size.height)
        XCTAssertTrue(ToolCursorFactory.cursor(tool: .text, width: 7, style: .system) === NSCursor.iBeam)
        XCTAssertTrue(ToolCursorFactory.cursor(tool: .select, width: 7, style: .toolPreview) === NSCursor.arrow)
        XCTAssertTrue(ToolCursorFactory.cursor(tool: .rectangle, width: 7, style: .system) === NSCursor.crosshair)
    }

    func testCanvasFootprintTracksWidthZoomAndInputModeWithLegacyPreferences() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.highlighter)
        for style in ToolCursorStyle.allCases {
            editor.model.setCursorStyle(style)
            editor.model.applyStyle { $0.width = 7.4 }
            let canvas = editor.session.canvas
            canvas.refreshToolCursor()
            let normal = canvas.currentToolCursor
            XCTAssertTrue(normal === ToolCursorFactory.cursor(tool: .highlighter, width: 7.4))
            var presentation = editor.session.document.presentation
            presentation.mode = .screen
            presentation.zoom = 2
            try editor.session.document.setPresentation(presentation)
            canvas.refreshToolCursor()
            XCTAssertTrue(canvas.currentToolCursor === ToolCursorFactory.cursor(tool: .highlighter, width: 14.8))
            XCTAssertGreaterThan(canvas.currentToolCursor.image.size.height, normal.image.size.height)
            editor.session.document.stopPresentationWithoutHistory()
        }
        editor.model.interact()
        editor.session.canvas.refreshToolCursor()
        XCTAssertTrue(editor.session.canvas.currentToolCursor === NSCursor.arrow)
    }

    func testFootprintBeforeFirstClickAndArrowOverToolbar() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.highlighter)
        editor.show()
        let canvas = editor.session.canvas
        let toolbar = InspectorPanel(contentRect: CGRect(x: 100, y: 100, width: 800, height: 180),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        toolbar.isReleasedWhenClosed = false
        let host = MovableToolbarHost(rootView: ToolbarView(model: editor.model))
        toolbar.contentView = host
        toolbar.makeKeyAndOrderFront(nil)
        defer { toolbar.orderOut(nil); toolbar.contentView = nil; toolbar.close() }
        canvas.updateTrackingAreas()
        XCTAssertTrue(canvas.trackingAreas.contains { $0.options.contains(.activeAlways) })
        XCTAssertFalse(editor.session.panel.isKeyWindow)
        let hover = try event(.mouseMoved, Point(300, 300), editor.session)
        NSCursor.arrow.set()
        editor.session.panel.sendEvent(hover)
        XCTAssertTrue(NSCursor.current === canvas.currentToolCursor)
        XCTAssertFalse(NSCursor.current === NSCursor.arrow)
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        host.updateTrackingAreas()
        host.mouseEntered(with: hover)
        XCTAssertTrue(NSCursor.current === NSCursor.arrow)
        canvas.currentToolCursor.set()
        host.cursorUpdate(with: hover)
        XCTAssertTrue(NSCursor.current === NSCursor.arrow)
        editor.session.panel.sendEvent(hover)
        XCTAssertTrue(NSCursor.current === canvas.currentToolCursor)
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        canvas.setDrawingHUDVisible(true)
        XCTAssertTrue(canvas.hoverCursor(at: Point(30, 25)) === NSCursor.arrow)
    }

    func testChoosingHighlighterReturnsKeyFocusToCanvasBeforeDrawing() throws {
        let suite = "desktop-cursor-focus-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.choose(.pen)
        let session = try XCTUnwrap(model.active)
        let toolbar = InspectorPanel(contentRect: CGRect(x: 100, y: 100, width: 800, height: 180),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        toolbar.isReleasedWhenClosed = false
        toolbar.contentView = MovableToolbarHost(rootView: ToolbarView(model: model))
        toolbar.makeKeyAndOrderFront(nil)
        defer { toolbar.orderOut(nil); toolbar.contentView = nil; toolbar.close() }
        XCTAssertTrue(toolbar.isKeyWindow)
        model.choose(.highlighter)
        XCTAssertTrue(session.panel.isKeyWindow)
        XCTAssertTrue(session.panel.firstResponder === session.canvas)
        XCTAssertTrue(session.document.elements.isEmpty)
        XCTAssertTrue(session.canvas.currentToolCursor ===
            ToolCursorFactory.cursor(tool: .highlighter, width: model.style.width))
        toolbar.makeKey()
        model.applyStyle { $0.width = 8 }
        XCTAssertTrue(toolbar.isKeyWindow, "Editing properties must keep keyboard focus in the toolbar.")
        model.visibilityAnimating = true
        model.choose(.pen)
        XCTAssertTrue(toolbar.isKeyWindow, "Tool choice must not take focus before reveal completes.")
        model.visibilityAnimating = false
    }

    func testLegacySolidPreferenceChoosesBlurWithoutChangingSavedCovers() throws {
        let suite = "effects-legacy-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        var preferences = WorkspacePreferences()
        preferences.lastCoverMode = .cover
        defaults.set(try preferences.data(), forKey: "workspace-preferences-v1")
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let legacy = Element(tool: .cover, frame: Rect(x: 50, y: 50, width: 60, height: 40))
        editor.session.document = try CanvasDocument(elements: [legacy])
        editor.model.choose(.cover)
        XCTAssertEqual(editor.model.tool, .blur)
        XCTAssertEqual(editor.session.document.elements, [legacy])
        let start = Point(200, 200), end = Point(300, 260)
        editor.session.canvas.mouseDown(with: try event(.leftMouseDown, start, editor.session))
        editor.session.canvas.mouseDragged(with: try event(.leftMouseDragged, end, editor.session))
        editor.session.canvas.mouseUp(with: try event(.leftMouseUp, end, editor.session))
        XCTAssertEqual(editor.session.document.elements.last?.tool, .blur)
        editor.model.setCoverMode(.pixelate)
        editor.model.chooseCover()
        XCTAssertEqual(editor.model.tool, .pixelate)
        editor.model.setCoverMode(.cover)
        XCTAssertNotNil(editor.model.errorMessage)
        XCTAssertEqual(editor.session.document.elements.filter { $0.tool == .cover }, [legacy])
        let saved = try DrawingFile.read(DrawingFile(document: editor.session.document).data())
        XCTAssertEqual(saved.elements.first, legacy)
        XCTAssertEqual(SettingsToolLabel(tool: .cover).title, "Effects")
        XCTAssertEqual(Tool.cover.toolbarSymbol, "camera.filters")
    }

    func testArrowheadPalettesWorkIndependently() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.arrow)
        let host = NSHostingController(rootView: HStack {
            ArrowHeadPalette(model: editor.model, start: true, dismiss: {})
            ArrowHeadPalette(model: editor.model, start: false, dismiss: {})
        }.padding())
        let panel = NSPanel(contentRect: CGRect(x:80,y:80,width:470,height:420),
                            styleMask:[.borderless,.nonactivatingPanel],backing:.buffered,defer:false)
        panel.isReleasedWhenClosed = false
        panel.contentViewController = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentViewController = nil; panel.close() }
        NativeAccessibilityNode.enable()
        host.view.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        let buttons = NativeAccessibilityNode.nodes(in: host.view).filter {
            $0.accessibilityRole() == .button && ($0.accessibilityLabel()?.contains(": ") ?? false)
        }
        XCTAssertEqual(buttons.count, 32)
        for label in ["Start: Filled circle", "End: Square"] {
            let button = try XCTUnwrap(buttons.first { $0.accessibilityLabel() == label })
            XCTAssertTrue(button.accessibilityPerformPress())
        }
        XCTAssertEqual(editor.model.style.startHead, .circle)
        XCTAssertEqual(editor.model.style.endHead, .square)
    }
}
