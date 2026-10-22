import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class SelectedContentEditingTests: XCTestCase {
    private func workspace() throws -> (NativeCanvasFixture, UserDefaults, String) {
        let suite = "selection-editing-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        return (NativeCanvasFixture(image: image, defaults: defaults), defaults, suite)
    }

    private func event(_ type: NSEvent.EventType, _ point: Point, in session: OverlaySession,
                       flags: NSEvent.ModifierFlags = []) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
            modifierFlags: flags, timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: 1, pressure: 1))
    }

    func testFillPaletteEditsFourSelectedRectanglesAndPreservesOtherStyles() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        var shapes = (0..<4).map { index in
            var shape = Element(tool: .rectangle, frame: Rect(x: Double(40 + index * 140), y: 100, width: 100, height: 80))
            shape.style.width = Double(index + 2)
            shape.style.color = index.isMultiple(of: 2) ? .coral : .ink
            return shape
        }
        for mixedAndLocked in [false, true] {
            if mixedAndLocked {
                shapes[1].tool = .ellipse
                shapes[3].locked = true
            }
            session.document = try CanvasDocument(elements: shapes)
            session.document.select(Set(shapes.map(\.id)))
            editor.model.choose(.select)
            XCTAssertTrue(editor.model.selectionSupportsFill)
            XCTAssertFalse(editor.model.selectionStyleIsLocked)
            let host = NSHostingView(rootView: InspectorColorPalette(model: editor.model, fill: true, done: {}))
            let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 330, height: 500),
                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.contentView = host
            panel.orderFrontRegardless()
            defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
            NativeAccessibilityNode.enable()
            host.layoutSubtreeIfNeeded()
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
            let button = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityRole() == .button && $0.accessibilityLabel()?.hasPrefix("Blue, hex") == true
            })
            editor.model.beginStyleEdit()
            XCTAssertTrue(button.accessibilityPerformPress())
            editor.model.finishStyleEdit()
            for (index, original) in shapes.enumerated() {
                let changed = session.document.elements[index]
                XCTAssertEqual(changed.style.fill, original.locked ? original.style.fill : RGBA(RGBA.blue.red, RGBA.blue.green, RGBA.blue.blue, 0.4))
                XCTAssertEqual(changed.style.width, original.style.width)
                XCTAssertEqual(changed.style.color, original.style.color)
                XCTAssertEqual(changed.frame, original.frame)
            }
            XCTAssertEqual(session.document.selection, Set(shapes.map(\.id)))
            editor.model.undo()
            XCTAssertEqual(session.document.elements.map(\.style), shapes.map(\.style))
            editor.model.redo()
            XCTAssertEqual(session.document.elements[0].style.fill, RGBA(RGBA.blue.red, RGBA.blue.green, RGBA.blue.blue, 0.4))
        }
    }

    func testMultiSelectionFillIsAvailableInCompactAndMixedFullProperties() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let shapes = [Element(tool: .rectangle, frame: Rect(x: 40, y: 100, width: 100, height: 80)),
                      Element(tool: .ellipse, frame: Rect(x: 200, y: 100, width: 100, height: 80))]
        editor.session.document = try CanvasDocument(elements: shapes)
        editor.session.document.select(Set(shapes.map(\.id)))
        editor.model.choose(.select)
        editor.model.setQuickPreferencesExpanded(true)
        editor.model.setInspectorPinned(false)
        editor.model.setToolbarOrientation(.vertical)
        for full in [false, true] {
            let content = full ? AnyView(ContextualInspector(model: editor.model)) :
                AnyView(ToolbarView(model: editor.model, availableSize: CGSize(width: 640, height: 800)))
            let host = NSHostingView(rootView: content)
            let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 640, height: 800),
                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.contentView = host
            panel.orderFrontRegardless()
            defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
            NativeAccessibilityNode.enable()
            host.layoutSubtreeIfNeeded()
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
            let fill = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Fill color"
            }, full ? "Full mixed-selection properties" : "Compact quick properties")
            if !full {
                XCTAssertTrue(fill.accessibilityPerformPress())
                RunLoop.current.run(until: Date().addingTimeInterval(0.05))
                let palette = try XCTUnwrap(NSApp.windows.filter(\.isVisible).compactMap(\.contentView).first { view in
                    NativeAccessibilityNode.nodes(in: view).contains {
                        $0.accessibilityRole() == .button && $0.accessibilityLabel()?.hasPrefix("Blue, hex") == true
                    }
                })
                let nodes = NativeAccessibilityNode.nodes(in: palette)
                let blue = try XCTUnwrap(nodes.first { $0.accessibilityRole() == .button && $0.accessibilityLabel()?.hasPrefix("Blue, hex") == true })
                XCTAssertTrue(blue.accessibilityPerformPress())
                XCTAssertTrue(editor.session.document.elements.allSatisfy {
                    $0.style.fill == RGBA(RGBA.blue.red, RGBA.blue.green, RGBA.blue.blue, 0.4)
                })
                let done = try XCTUnwrap(nodes.first { $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Done" })
                XCTAssertTrue(done.accessibilityPerformPress())
            }
        }
    }

    func testMixedStyleEditsDoNotChangeProtectiveCoversOrFillOpenStrokes() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let shape = Element(tool: .rectangle, frame: Rect(x: 40, y: 100, width: 100, height: 80))
        let line = Element(tool: .line, points: [Point(200, 100), Point(300, 100)])
        let cover = Element(tool: .cover, frame: Rect(x: 350, y: 100, width: 100, height: 80))
        editor.session.document = try CanvasDocument(elements: [shape, line, cover])
        editor.session.document.select([shape.id, line.id, cover.id])
        editor.model.choose(.select)
        editor.model.applyStyle(where: { $0.tool.supportsFill }) { $0.fill = .blue }
        XCTAssertEqual(editor.session.document.elements[0].style.fill, .blue)
        XCTAssertEqual(editor.session.document.elements[1].style, line.style)
        editor.model.applyStyle { $0.opacity = 0.25; $0.width = 8 }
        XCTAssertEqual(editor.session.document.elements[0].style.opacity, 0.25)
        XCTAssertEqual(editor.session.document.elements[1].style.width, 8)
        XCTAssertEqual(editor.session.document.elements[2].style, cover.style)
    }

    func testDraggingShiftSelectedShapesPreservesSelectionAndOneUndo() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let first = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 80, height: 60))
        let second = Element(tool: .ellipse, frame: Rect(x: 240, y: 100, width: 80, height: 60))
        session.document = try CanvasDocument(elements: [first, second])
        editor.model.choose(.select)
        for (point, flags) in [(Point(100, 130), NSEvent.ModifierFlags()), (Point(240, 130), .shift)] {
            session.canvas.mouseDown(with: try event(.leftMouseDown, point, in: session, flags: flags))
            session.canvas.mouseUp(with: try event(.leftMouseUp, point, in: session, flags: flags))
        }
        XCTAssertEqual(session.document.selection, [first.id, second.id])
        let start = Point(100, 130), finish = Point(130, 170)
        session.canvas.mouseDown(with: try event(.leftMouseDown, start, in: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, finish, in: session))
        session.canvas.mouseUp(with: try event(.leftMouseUp, finish, in: session))
        XCTAssertEqual(session.document.selection, [first.id, second.id])
        XCTAssertEqual(session.document.elements[0].frame, first.frame.translated(by: finish - start))
        XCTAssertEqual(session.document.elements[1].frame, second.frame.translated(by: finish - start))
        editor.model.undo()
        XCTAssertEqual(session.document.elements.map(\.frame), [first.frame, second.frame])
        editor.model.redo()
        XCTAssertEqual(session.document.elements[1].frame, second.frame.translated(by: finish - start))
    }

    func testSharedSelectionResizeRotationAndCancellationKeepLockedObjectsFixed() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        var first = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 80, height: 60))
        first.label = ShapeLabel(text: "Orders", style: Style())
        let second = Element(tool: .ellipse, frame: Rect(x: 240, y: 100, width: 80, height: 60))
        var locked = Element(tool: .rectangle, frame: Rect(x: 500, y: 400, width: 80, height: 60))
        locked.locked = true
        session.document = try CanvasDocument(elements: [first, second, locked])
        session.document.select([first.id, second.id, locked.id])
        editor.model.choose(.select)
        let group = try XCTUnwrap(SelectionGeometry(elements: session.document.elements, selection: session.document.selection))
        XCTAssertEqual(group.bounds, Rect(x: 100, y: 100, width: 220, height: 60))
        let corner = group.corners[2], target = group.corners[0] + (corner - group.corners[0]) * 2
        session.canvas.mouseDown(with: try event(.leftMouseDown, corner, in: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, target, in: session))
        session.canvas.mouseUp(with: try event(.leftMouseUp, target, in: session))
        XCTAssertEqual(session.document.elements[0].frame, Rect(x: 100, y: 100, width: 160, height: 120))
        XCTAssertEqual(session.document.elements[1].frame, Rect(x: 380, y: 100, width: 160, height: 120))
        XCTAssertEqual(session.document.elements[0].label?.style.fontSize, try XCTUnwrap(first.label).style.fontSize * 2)
        XCTAssertEqual(session.document.elements[2], locked)
        editor.model.undo()
        XCTAssertEqual(session.document.elements.map(\.frame), [first.frame, second.frame, locked.frame])
        let handle = Point(group.bounds.center.x, group.bounds.y - 24)
        let rotatedHandle = handle.rotated(around: group.bounds.center, by: .pi / 2)
        XCTAssertTrue(session.canvas.isOverRotationHandle(handle))
        session.canvas.mouseDown(with: try event(.leftMouseDown, handle, in: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, rotatedHandle, in: session))
        session.canvas.mouseUp(with: try event(.leftMouseUp, rotatedHandle, in: session))
        for (index, original) in [first, second].enumerated() {
            let changed = session.document.elements[index]
            let center = original.frame.center.rotated(around: group.bounds.center, by: .pi / 2)
            XCTAssertEqual(changed.frame.center.x, center.x, accuracy: 0.001)
            XCTAssertEqual(changed.frame.center.y, center.y, accuracy: 0.001)
            XCTAssertEqual(changed.rotation, .pi / 2, accuracy: 0.001)
            try changed.validate()
        }
        XCTAssertEqual(session.document.elements[2], locked)
        editor.model.undo()
        session.canvas.mouseDown(with: try event(.leftMouseDown, corner, in: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, target, in: session))
        session.canvas.cancelGesture()
        XCTAssertEqual(session.document.elements.map(\.frame), [first.frame, second.frame, locked.frame])
        editor.model.redo()
        XCTAssertEqual(session.document.elements[0].rotation, .pi / 2, accuracy: 0.001)
    }

    func testSelectionTransformPreservesBoundConnectorAndCoverRotationSafety() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let shape = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 80, height: 60))
        var arrow = Element(tool: .arrow, points: [Point(180, 130), Point(300, 130)])
        arrow.startAnchor = Anchor(elementID: shape.id, relative: Point(1, 0.5))
        session.document = try CanvasDocument(elements: [shape, arrow])
        session.document.select([shape.id, arrow.id])
        editor.model.choose(.select)
        let group = try XCTUnwrap(SelectionGeometry(elements: session.document.elements, selection: session.document.selection))
        let handle = Point(group.bounds.center.x, group.bounds.y - 24)
        let target = handle.rotated(around: group.bounds.center, by: .pi / 2)
        session.canvas.mouseDown(with: try event(.leftMouseDown, handle, in: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, target, in: session))
        session.canvas.mouseUp(with: try event(.leftMouseUp, target, in: session))
        let changed = session.document.elements[1]
        XCTAssertEqual(changed.startAnchor, arrow.startAnchor)
        XCTAssertEqual(changed.points[0], session.document.elements[0].anchorPoint(Point(1, 0.5)))
        XCTAssertEqual(changed.points[1].x, arrow.points[1].rotated(around: group.bounds.center, by: .pi / 2).x, accuracy: 0.001)
        XCTAssertEqual(changed.rotation, 0)
        editor.model.undo()
        let cover = Element(tool: .cover, frame: Rect(x: 400, y: 100, width: 60, height: 60))
        try session.document.add(cover)
        session.document.select([shape.id, cover.id])
        let coveredGroup = try XCTUnwrap(SelectionGeometry(elements: session.document.elements, selection: session.document.selection))
        XCTAssertFalse(coveredGroup.canRotate)
        XCTAssertFalse(session.canvas.isOverRotationHandle(Point(coveredGroup.bounds.center.x, coveredGroup.bounds.y - 24)))
    }

    func testBlankNewTextIsDiscardedAcrossCommitRoutesWithoutChangingHistory() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        for value in ["", " \t\n", "\u{00a0}\n"] {
            for route in ["escape", "commit", "tool", "elsewhere", "useApps"] {
                let original = Element(tool: .rectangle, frame: Rect(x: 10, y: 10, width: 40, height: 40))
                let undone = Element(tool: .ellipse, frame: Rect(x: 500, y: 400, width: 40, height: 40))
                session.document = try CanvasDocument(elements: [original])
                try session.document.add(undone)
                session.document.undo()
                editor.model.choose(.text)
                session.document.select([original.id])
                let bytes = session.document.historyBytes
                session.canvas.mouseDown(with: try event(.leftMouseDown, Point(150, 150), in: session))
                let text = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
                text.insertText(value, replacementRange: NSRange(location: NSNotFound, length: 0))
                switch route {
                case "escape":
                    XCTAssertTrue(session.canvas.textView(text, doCommandBy: #selector(NSResponder.cancelOperation(_:))))
                case "tool": editor.model.choose(.select)
                case "elsewhere":
                    let draftID = try XCTUnwrap(session.document.elements.last?.id)
                    session.canvas.mouseDown(with: try event(.leftMouseDown, Point(350, 300), in: session))
                    XCTAssertFalse(session.document.elements.contains { $0.id == draftID })
                    session.canvas.commitTextIfNeeded()
                case "useApps": editor.model.interact()
                default: session.canvas.commitTextIfNeeded()
                }
                XCTAssertFalse(session.canvas.hasTextEditor, route)
                XCTAssertFalse(session.document.isEditing, route)
                XCTAssertEqual(session.document.elements, [original], route)
                XCTAssertEqual(session.document.selection, [original.id], route)
                XCTAssertFalse(session.document.canUndo, route)
                XCTAssertTrue(session.document.canRedo, route)
                XCTAssertEqual(session.document.historyBytes, bytes, route)
                XCTAssertEqual(try DrawingFile.read(DrawingFile(document: session.document).data()).elements, [original])
                let saved = try SavedShape(name: "Original only", document: session.document)
                XCTAssertEqual(saved.drawing.elements.count, 1)
                session.document.redo()
                XCTAssertEqual(session.document.elements, [original, undone])
            }
        }
    }

    func testEnteringAndLeavingExistingTextDoesNotDeleteItOrAddHistory() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        for value in ["Existing text", "", " \n"] {
            let original = Element(tool: .text, frame: Rect(x: 100, y: 100, width: 200, height: 100), text: value)
            session.document = try CanvasDocument(elements: [original])
            editor.model.choose(.text)
            session.canvas.mouseDown(with: try event(.leftMouseDown, Point(130, 130), in: session))
            XCTAssertTrue(session.canvas.hasTextEditor)
            session.canvas.commitTextIfNeeded()
            XCTAssertEqual(session.document.elements, [original])
            XCTAssertEqual(session.document.selection, [original.id])
            XCTAssertFalse(session.document.canUndo)
        }
    }

    func testClearingExistingTextKeepsObjectAndCanBeUndone() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let original = Element(tool: .text, frame: Rect(x: 100, y: 100, width: 200, height: 100), text: "Before")
        session.document = try CanvasDocument(elements: [original])
        editor.model.choose(.text)
        session.canvas.mouseDown(with: try event(.leftMouseDown, Point(130, 130), in: session))
        let text = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
        text.insertText("", replacementRange: NSRange(location: 0, length: text.string.utf16.count))
        session.canvas.commitTextIfNeeded()
        XCTAssertEqual(session.document.elements.map(\.id), [original.id])
        XCTAssertEqual(session.document.elements[0].text, "")
        session.document.undo()
        XCTAssertEqual(session.document.elements, [original])
    }

    func testTypingThenClearingNewTextLeavesNoObjectOrUndoStep() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.choose(.text)
        editor.session.canvas.mouseDown(with: try event(.leftMouseDown, Point(150, 150), in: editor.session))
        let text = try XCTUnwrap(editor.session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
        text.insertText("Temporary", replacementRange: NSRange(location: NSNotFound, length: 0))
        text.insertText("", replacementRange: NSRange(location: 0, length: text.string.utf16.count))
        editor.session.canvas.commitTextIfNeeded()
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        XCTAssertFalse(editor.session.document.canUndo)
    }

    func testEscapeCommitsNewTextAndLabelWithOneUndo() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        for label in [false, true] {
            let shape = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 200, height: 120))
            session.document = try CanvasDocument(elements: label ? [shape] : [])
            editor.model.choose(.text)
            session.canvas.mouseDown(with: try event(.leftMouseDown, Point(150, 150), in: session))
            let text = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
            text.insertText("Keep this text", replacementRange: NSRange(location: NSNotFound, length: 0))
            XCTAssertTrue(session.canvas.textView(text, doCommandBy: #selector(NSResponder.cancelOperation(_:))))
            XCTAssertFalse(session.canvas.hasTextEditor)
            XCTAssertEqual(label ? session.document.elements[0].label?.text : session.document.elements[0].text, "Keep this text")
            XCTAssertEqual(editor.model.mode, .draw)
            editor.model.undo()
            if label { XCTAssertNil(session.document.elements[0].label) }
            else { XCTAssertTrue(session.document.elements.isEmpty) }
            editor.model.redo()
            XCTAssertEqual(label ? session.document.elements[0].label?.text : session.document.elements[0].text, "Keep this text")
        }
    }

    func testCanvasAndZoomEscapeCommitExistingTextInsteadOfDiscardingIt() throws {
        let (editor, defaults, suite) = try workspace()
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        for zoomRoute in [false, true] {
            let original = Element(tool: .text, frame: Rect(x: 100, y: 100, width: 200, height: 100), text: "Before")
            session.document = try CanvasDocument(elements: [original])
            editor.model.choose(.text)
            session.canvas.mouseDown(with: try event(.leftMouseDown, Point(130, 130), in: session))
            let text = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
            text.insertText("After", replacementRange: NSRange(location: 0, length: text.string.utf16.count))
            if zoomRoute { editor.model.handleZoomEscape() }
            else {
                let escape = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                    timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                    characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53))
                session.canvas.keyDown(with: escape)
            }
            XCTAssertFalse(session.canvas.hasTextEditor)
            XCTAssertEqual(session.document.elements[0].text, "After")
            XCTAssertEqual(editor.model.mode, .draw)
            editor.model.undo()
            XCTAssertEqual(session.document.elements[0].text, "Before")
        }
    }

    func testLiveTextEditingHasNoOpaqueBackgroundInEitherAppearance() throws {
        for appearance in [NSAppearance.Name.aqua, .darkAqua] {
            let suite = "transparent-text-\(UUID())"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
            let editor = NativeCanvasFixture(image: image, defaults: defaults)
            defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
            editor.session.panel.appearance = NSAppearance(named: appearance)
            editor.model.choose(.text)
            let event = try XCTUnwrap(NSEvent.mouseEvent(with: .leftMouseDown,
                location: editor.session.canvas.convert(CGPoint(x: 60, y: 70), to: nil), modifierFlags: [],
                timestamp: 0, windowNumber: editor.session.panel.windowNumber, context: nil,
                eventNumber: 0, clickCount: 1, pressure: 1))
            editor.session.canvas.mouseDown(with: event)
            let text = try XCTUnwrap(editor.session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
            XCTAssertFalse(text.drawsBackground)
            XCTAssertEqual(text.backgroundColor.alphaComponent, 0)
            XCTAssertEqual(text.insertionPointColor, text.textColor)
            XCTAssertTrue(editor.session.canvas.hoverCursor(at: Point(70, 80)) === NSCursor.iBeam)
            text.insertText("Link API", replacementRange: NSRange(location: NSNotFound, length: 0))
            editor.session.canvas.commitTextIfNeeded()
            XCTAssertEqual(editor.session.document.elements.last?.text, "Link API")
        }
    }

    func testCommittedTextRemeasuresWhenFontChangesAndUndoRestoresBounds() throws {
        let suite = "committed-font-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        var style = Style(); style.fontSize = 18
        let text = "This text grows after it has been committed."
        let size = NativeTextFont.contentSize(text, font: NativeTextFont.resolve(style))
        let element = Element(tool: .text, frame: Rect(x: 30, y: 30, width: size.width, height: size.height), text: text, style: style)
        try editor.session.document.add(element)
        editor.session.document.select([element.id])
        editor.model.synchronize()
        editor.model.applyStyle { $0.fontSize = 44; $0.bold = true }
        let edited = try XCTUnwrap(editor.session.document.elements.first)
        XCTAssertGreaterThan(edited.frame.width, element.frame.width * 2)
        XCTAssertGreaterThan(edited.frame.height, element.frame.height)
        XCTAssertEqual(edited.text, text)
        XCTAssertEqual(edited.frame.origin, element.frame.origin)
        let expected = NativeTextFont.contentSize(text, font: NativeTextFont.resolve(edited.style))
        XCTAssertEqual(edited.frame.width, expected.width)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements.first?.frame, element.frame)
    }

    func testResizedRulerRecalculatesItsDisplayedDistance() throws {
        let suite = "resize-ruler-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let session = editor.session
        let ruler = Element(tool: .ruler, frame: Rect(x: 100, y: 100, width: 100, height: 50),
                            points: [Point(100, 100), Point(200, 150)], text: "111 px")
        try session.document.add(ruler)
        session.document.select([ruler.id])
        editor.model.choose(.select)
        func event(_ type: NSEvent.EventType, _ point: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
                modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
                eventNumber: 0, clickCount: 1, pressure: 1))
        }
        session.canvas.mouseDown(with: try event(.leftMouseDown, Point(200, 150)))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(300, 200)))
        session.canvas.mouseUp(with: try event(.leftMouseUp, Point(300, 200)))
        XCTAssertEqual(session.document.elements.first?.text, "223 px")
        editor.model.undo()
        XCTAssertEqual(session.document.elements.first?.text, "111 px")
    }
}
