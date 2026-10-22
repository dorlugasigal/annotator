import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class MultiDisplayEditingTests: XCTestCase {
    private func workspace() throws -> (WorkspaceModel, UserDefaults, String) {
        let suite = "multi-display-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let displays = [
            DesktopDisplay(id: 101, frame: CGRect(x: 0, y: 0, width: 640, height: 480), scale: 2),
            DesktopDisplay(id: 202, frame: CGRect(x: -800, y: 120, width: 800, height: 600), scale: 1)
        ]
        let model = WorkspaceModel(defaults: defaults, desktopDisplays: { displays })
        model.beginDrawing()
        for session in model.desktopSessions {
            session.presentation?.stop()
            session.presentation = nil
        }
        return (model, defaults, suite)
    }

    private func event(_ type: NSEvent.EventType, _ point: Point, in session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
            modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: 1, pressure: 1))
    }

    private func draw(in session: OverlaySession) throws {
        session.canvas.mouseDown(with: try event(.leftMouseDown, Point(60, 80), in: session))
        session.canvas.mouseDragged(with: try event(.leftMouseDragged, Point(180, 160), in: session))
        session.canvas.mouseUp(with: try event(.leftMouseUp, Point(180, 160), in: session))
    }

    private func key(_ character: String, code: UInt16, modifiers: NSEvent.ModifierFlags = [],
                     in session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
            timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
            characters: character, charactersIgnoringModifiers: character, isARepeat: false, keyCode: code))
    }

    private func localPoint(_ screenPoint: CGPoint, in session: OverlaySession) -> Point {
        Point(session.canvas.convert(session.panel.convertPoint(fromScreen: screenPoint), from: nil))
    }

    func testDragAcrossDisplaysPreviewsTransfersAndUndoesWithoutChangingScreenPosition() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let source = try XCTUnwrap(model.desktopSessions.first { $0.panel.frame.minX == 0 })
        let destination = try XCTUnwrap(model.desktopSessions.first { $0 !== source })
        var shape = Element(tool: .rectangle, frame: Rect(x: 60, y: 80, width: 120, height: 80))
        shape.style.color = .coral
        shape.rotation = 0.2
        let existing = Element(tool: .ellipse, frame: Rect(x: 500, y: 200, width: 80, height: 60))
        try source.document.add(shape)
        try destination.document.add(existing)
        model.choose(.select)
        let start = shape.frame.center
        let screenEnd = CGPoint(x: -300, y: 350)
        let finish = localPoint(screenEnd, in: source)
        source.canvas.mouseDown(with: try event(.leftMouseDown, start, in: source))
        source.canvas.mouseDragged(with: try event(.leftMouseDragged, finish, in: source))
        XCTAssertEqual(destination.document.elements.count, 2)
        XCTAssertTrue(destination.document.isEditing)
        XCTAssertTrue(model.active === source)
        let preview = try XCTUnwrap(destination.document.elements.first { $0.id == shape.id })
        XCTAssertEqual(preview.frame.center, localPoint(screenEnd, in: destination))
        source.canvas.mouseUp(with: try event(.leftMouseUp, finish, in: source))
        XCTAssertNil(model.errorMessage)
        XCTAssertTrue(source.document.elements.isEmpty)
        let transferred = try XCTUnwrap(destination.document.elements.first { $0.id == shape.id })
        XCTAssertEqual(transferred.frame, preview.frame)
        XCTAssertEqual(transferred.rotation, preview.rotation)
        XCTAssertEqual(transferred.style, preview.style)
        XCTAssertEqual(destination.document.elements, [existing, transferred])
        XCTAssertFalse(destination.document.isEditing)
        XCTAssertTrue(model.active === destination)
        XCTAssertEqual(destination.document.selection, [shape.id])
        XCTAssertEqual(preview.rotation, shape.rotation)
        XCTAssertEqual(preview.style, shape.style)
        model.undo()
        XCTAssertEqual(source.document.elements, [shape])
        XCTAssertEqual(destination.document.elements, [existing])
        model.redo()
        XCTAssertTrue(source.document.elements.isEmpty)
        XCTAssertEqual(destination.document.elements, [existing, transferred])
        let returnStart = transferred.frame.center
        let returnEnd = localPoint(CGPoint(x: 240, y: 240), in: destination)
        destination.canvas.mouseDown(with: try event(.leftMouseDown, returnStart, in: destination))
        destination.canvas.mouseUp(with: try event(.leftMouseUp, returnEnd, in: destination))
        XCTAssertTrue(model.active === source)
        XCTAssertEqual(destination.document.elements, [existing])
        XCTAssertEqual(source.document.elements.first?.frame.center, localPoint(CGPoint(x: 240, y: 240), in: source))
        model.undo()
        XCTAssertTrue(source.document.elements.isEmpty)
        XCTAssertEqual(destination.document.elements, [existing, transferred])
    }

    func testCrossDisplayDragCancellationAndOutsideDropRestoreBothDocuments() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let source = try XCTUnwrap(model.desktopSessions.first { $0.panel.frame.minX == 0 })
        let destination = try XCTUnwrap(model.desktopSessions.first { $0 !== source })
        let shape = Element(tool: .rectangle, frame: Rect(x: 60, y: 80, width: 120, height: 80))
        try source.document.add(shape)
        model.choose(.select)
        let end = localPoint(CGPoint(x: -300, y: 350), in: source)
        for outsideDrop in [false, true] {
            source.canvas.mouseDown(with: try event(.leftMouseDown, shape.frame.center, in: source))
            source.canvas.mouseDragged(with: try event(.leftMouseDragged, end, in: source))
            XCTAssertEqual(destination.document.elements.count, 1)
            if outsideDrop {
                source.canvas.mouseUp(with: try event(.leftMouseUp,
                    localPoint(CGPoint(x: 2000, y: 2000), in: source), in: source))
            } else { model.interact(); model.beginDrawing() }
            XCTAssertEqual(source.document.elements, [shape])
            XCTAssertTrue(destination.document.elements.isEmpty)
            XCTAssertFalse(destination.document.isEditing)
            XCTAssertFalse(source.document.isEditing)
        }
        model.undo()
        XCTAssertTrue(source.document.elements.isEmpty, "Cancelled moves must not add history.")
    }

    func testCrossDisplayTransferFailurePreservesObjectsAndHistory() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let source = try XCTUnwrap(model.desktopSessions.first { $0.panel.frame.minX == 0 })
        let destination = try XCTUnwrap(model.desktopSessions.first { $0 !== source })
        let shape = Element(tool: .rectangle, frame: Rect(x: 60, y: 80, width: 120, height: 80))
        let existing = Element(tool: .text, text: String(repeating: "Long text ", count: 100))
        try source.document.add(shape)
        destination.document = try CanvasDocument(elements: [existing], historyByteLimit: 100)
        model.choose(.select)
        source.canvas.mouseDown(with: try event(.leftMouseDown, shape.frame.center, in: source))
        source.canvas.mouseUp(with: try event(.leftMouseUp,
            localPoint(CGPoint(x: -300, y: 350), in: source), in: source))
        XCTAssertNotNil(model.errorMessage)
        XCTAssertEqual(source.document.elements, [shape])
        XCTAssertEqual(destination.document.elements, [existing])
        XCTAssertTrue(model.active === source)
        XCTAssertFalse(source.document.isEditing)
        XCTAssertFalse(destination.document.isEditing)
        model.undo()
        XCTAssertTrue(source.document.elements.isEmpty)
    }

    func testDisconnectDuringDragCancelsPreviewsAndResumesOnRemainingDisplay() throws {
        let suite = "disconnect-during-drag-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        var displays = [
            DesktopDisplay(id: 101, frame: CGRect(x: 0, y: 0, width: 640, height: 480), scale: 2),
            DesktopDisplay(id: 202, frame: CGRect(x: -800, y: 120, width: 800, height: 600), scale: 1)
        ]
        let model = WorkspaceModel(defaults: defaults, desktopDisplays: { displays })
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        for session in model.desktopSessions { session.presentation?.stop(); session.presentation = nil }
        let source = try XCTUnwrap(model.desktopSessions.first { $0.panel.frame.minX == 0 })
        let destination = try XCTUnwrap(model.desktopSessions.first { $0 !== source })
        let shape = Element(tool: .rectangle, frame: Rect(x: 60, y: 80, width: 120, height: 80))
        try source.document.add(shape)
        model.choose(.select)
        source.canvas.mouseDown(with: try event(.leftMouseDown, shape.frame.center, in: source))
        source.canvas.mouseDragged(with: try event(.leftMouseDragged,
            localPoint(CGPoint(x: -300, y: 350), in: source), in: source))
        XCTAssertEqual(destination.document.elements.count, 1)
        displays.removeLast()
        model.displaysChanged()
        XCTAssertEqual(model.desktopSessions.count, 1)
        XCTAssertEqual(source.document.elements, [shape])
        XCTAssertTrue(destination.document.elements.isEmpty)
        XCTAssertFalse(source.document.isEditing)
        XCTAssertTrue(source.panel.ignoresMouseEvents)
        model.beginDrawing()
        XCTAssertTrue(model.active === source)
        XCTAssertFalse(source.panel.ignoresMouseEvents)
    }

    func testCrossDisplayTransferKeepsGroupsAndInternalBindingsButLeavesLockedObjects() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let source = try XCTUnwrap(model.desktopSessions.first { $0.panel.frame.minX == 0 })
        let destination = try XCTUnwrap(model.desktopSessions.first { $0 !== source })
        let group = UUID()
        var shape = Element(tool: .rectangle, frame: Rect(x: 60, y: 80, width: 120, height: 80))
        shape.groupID = group
        var locked = Element(tool: .ellipse, frame: Rect(x: 300, y: 80, width: 100, height: 80))
        locked.locked = true
        var arrow = Element(tool: .arrow, frame: Rect(x: 180, y: 120, width: 120, height: 0))
        arrow.points = [Point(180, 120), Point(300, 120)]
        arrow.groupID = group
        arrow.startAnchor = Anchor(elementID: shape.id, relative: Point(1, 0.5))
        arrow.endAnchor = Anchor(elementID: locked.id, relative: Point(0, 0.5))
        for element in [shape, locked, arrow] { try source.document.add(element) }
        model.choose(.select)
        source.document.select([shape.id, arrow.id, locked.id])
        let start = Point(120, 120)
        source.canvas.mouseDown(with: try event(.leftMouseDown, start, in: source))
        source.canvas.mouseUp(with: try event(.leftMouseUp,
            localPoint(CGPoint(x: -300, y: 350), in: source), in: source))
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(source.document.elements, [locked])
        XCTAssertEqual(destination.document.elements.count, 2)
        let movedArrow = try XCTUnwrap(destination.document.elements.first { $0.id == arrow.id })
        XCTAssertEqual(movedArrow.startAnchor, arrow.startAnchor)
        XCTAssertNil(movedArrow.endAnchor)
        XCTAssertEqual(movedArrow.groupID, group)
        model.undo()
        XCTAssertEqual(source.document.elements.count, 3)
        XCTAssertTrue(destination.document.elements.isEmpty)
    }

    func testSelectAllFromEmptyDisplaySupportsGlobalStylesDeleteAndUndo() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let shape = Element(tool: .rectangle, frame: Rect(x: 30, y: 40, width: 100, height: 80))
        var locked = Element(tool: .ellipse, frame: Rect(x: 230, y: 40, width: 100, height: 80))
        locked.locked = true
        try second.document.add(shape)
        try second.document.add(locked)
        first.canvas.keyDown(with: try key("a", code: 0, modifiers: .command, in: first))
        XCTAssertTrue(model.selectionIsWorkspaceWide)
        XCTAssertEqual(model.selectionCount, 2)
        XCTAssertEqual(second.document.selection, [shape.id, locked.id])
        XCTAssertEqual(model.effectiveTool, .rectangle)
        model.beginStyleEdit()
        model.applyStyle { $0.fill = .blue }
        model.applyStyle { $0.fill = .coral }
        model.finishStyleEdit()
        XCTAssertEqual(second.document.elements[0].style.fill, .coral)
        XCTAssertEqual(second.document.elements[1], locked)
        model.undo()
        XCTAssertEqual(second.document.elements, [shape, locked])
        model.redo()
        XCTAssertEqual(second.document.elements[0].style.fill, .coral)
        first.canvas.keyDown(with: try key("\u{7f}", code: 51, in: first))
        XCTAssertEqual(second.document.elements, [locked])
        model.undo()
        XCTAssertEqual(second.document.elements.count, 2)
        XCTAssertEqual(model.selectionCount, 2)
        XCTAssertTrue(model.selectionIsWorkspaceWide)
    }

    func testWorkspaceSelectionNudgesAllDisplaysAndCanvasClickReturnsToLocalEditing() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let a = Element(tool: .rectangle, frame: Rect(x: 50, y: 50, width: 100, height: 80))
        let b = Element(tool: .ellipse, frame: Rect(x: 50, y: 50, width: 100, height: 80))
        try first.document.add(a)
        try second.document.add(b)
        model.selectAll()
        XCTAssertEqual(model.selectionCount, 2)
        first.canvas.keyDown(with: try key("\u{f703}", code: 124, in: first))
        XCTAssertEqual(first.document.elements[0].frame.x, a.frame.x + 1)
        XCTAssertEqual(second.document.elements[0].frame.x, b.frame.x + 1)
        model.undo()
        XCTAssertEqual(first.document.elements, [a])
        XCTAssertEqual(second.document.elements, [b])
        let menu = try XCTUnwrap(second.canvas.menu(for: event(.rightMouseDown, b.frame.center, in: second)))
        XCTAssertTrue(model.selectionIsWorkspaceWide)
        menu.performActionForItem(at: menu.indexOfItem(withTitle: "Delete selected"))
        XCTAssertTrue(first.document.elements.isEmpty)
        XCTAssertTrue(second.document.elements.isEmpty)
        model.undo()
        XCTAssertEqual(first.document.elements, [a])
        XCTAssertEqual(second.document.elements, [b])
        second.canvas.mouseDown(with: try event(.leftMouseDown, b.frame.center, in: second))
        second.canvas.mouseUp(with: try event(.leftMouseUp, b.frame.center, in: second))
        XCTAssertFalse(model.selectionIsWorkspaceWide)
        XCTAssertTrue(first.document.selection.isEmpty)
        XCTAssertEqual(second.document.selection, [b.id])
    }

    func testPointerCursorUsesDestinationDisplayWithoutChangingEditingTarget() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.choose(.highlighter)
        model.applyStyle { $0.width = 4 }
        let active = try XCTUnwrap(model.active)
        let footprint = ToolCursorFactory.cursor(tool: .highlighter, width: 4)
        for session in model.desktopSessions {
            let point = session.panel.convertPoint(toScreen: session.canvas.convert(CGPoint(x: 250, y: 250), to: nil))
            XCTAssertTrue(model.cursorForPointer(at: point, windowNumber: session.panel.windowNumber) === footprint)
            XCTAssertTrue(model.active === active)
        }
        XCTAssertTrue(model.cursorForPointer(at: .zero, windowNumber: -1) === NSCursor.arrow)
        model.interact()
        XCTAssertTrue(model.cursorForPointer(at: active.panel.frame.origin, windowNumber: active.panel.windowNumber) === NSCursor.arrow)
    }

    func testEveryDisplayReceivesDrawingAndKeepsItsOwnLocalDocument() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let sessions = model.desktopSessions.sorted { $0.panel.frame.minX < $1.panel.frame.minX }
        XCTAssertEqual(sessions.count, 2)
        XCTAssertEqual(sessions[0].panel.frame, CGRect(x: -800, y: 120, width: 800, height: 600))
        XCTAssertEqual(sessions[0].scale, 1)
        XCTAssertEqual(sessions[1].scale, 2)
        XCTAssertTrue(sessions.allSatisfy { !$0.panel.ignoresMouseEvents })
        XCTAssertTrue(try XCTUnwrap(model.active).panel.isKeyWindow)
        for session in sessions {
            model.choose(.rectangle)
            try draw(in: session)
            XCTAssertTrue(model.active === session)
            XCTAssertEqual(session.document.elements.count, 1)
            XCTAssertEqual(session.document.elements[0].frame, Rect(x: 60, y: 80, width: 120, height: 80))
        }
        model.undo()
        XCTAssertTrue(sessions[1].document.elements.isEmpty)
        XCTAssertEqual(sessions[0].document.elements.count, 1)
        model.interact()
        XCTAssertTrue(sessions.allSatisfy { $0.panel.ignoresMouseEvents })
        try model.activateCanvasForEditing(try XCTUnwrap(model.active))
        XCTAssertTrue(sessions.allSatisfy { !$0.panel.ignoresMouseEvents })
        model.revealWorkspaceSurfaces(drawing: true)
        XCTAssertTrue(sessions.allSatisfy { !$0.panel.ignoresMouseEvents })
        model.concealWorkspaceSurfaces()
        XCTAssertTrue(sessions.allSatisfy { $0.panel.ignoresMouseEvents && !$0.panel.isVisible })
        XCTAssertFalse(model.activateDrawingSession(sessions[0]))
    }

    func testClearFromAnEmptyDisplayClearsAndRestoresEveryDisplay() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let shape = Element(tool: .rectangle, frame: Rect(x: 20, y: 30, width: 100, height: 80))
        let cover = Element(tool: .cover, frame: Rect(x: 150, y: 30, width: 100, height: 80))
        try second.document.add(shape)
        try second.document.add(cover)
        model.synchronize()
        XCTAssertTrue(model.canClearCanvas)
        model.clearCanvas()
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.document.elements.isEmpty })
        XCTAssertTrue(model.canUndo)
        model.undo()
        XCTAssertTrue(first.document.elements.isEmpty)
        XCTAssertEqual(second.document.elements, [shape, cover])
        XCTAssertTrue(model.activateDrawingSession(second))
        model.redo()
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.document.elements.isEmpty })
        model.undo()
        XCTAssertEqual(second.document.elements, [shape, cover])
    }

    func testDrawingOnAnotherDisplayUsesToolStyleRatherThanItsOldSelection() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        var old = Element(tool: .rectangle, frame: Rect(x: 300, y: 300, width: 100, height: 80))
        old.style.color = .blue
        old.style.width = 11
        try second.document.add(old)
        second.document.select([old.id])
        model.choose(.pen)
        model.applyStyle { $0.color = .coral; $0.width = 3 }
        try draw(in: second)
        let ink = try XCTUnwrap(second.document.elements.last)
        XCTAssertEqual(ink.tool, .pen)
        XCTAssertEqual(ink.style.color, .coral)
        XCTAssertEqual(ink.style.width, 3)
        XCTAssertEqual(second.document.elements.first, old)
        XCTAssertTrue(second.document.selection.isEmpty)
    }

    func testGlobalClearRejectsConflictingHistoryWithoutPartialRestoration() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let a = Element(tool: .rectangle), b = Element(tool: .ellipse)
        try first.document.add(a)
        try second.document.add(b)
        model.clearCanvas()
        let newer = Element(tool: .diamond)
        try second.document.add(newer)
        model.undo()
        XCTAssertNotNil(model.errorMessage)
        XCTAssertTrue(first.document.elements.isEmpty)
        XCTAssertEqual(second.document.elements, [newer])
        XCTAssertTrue(model.activateDrawingSession(second))
        model.undo()
        XCTAssertTrue(second.document.elements.isEmpty)
        XCTAssertTrue(model.activateDrawingSession(first))
        model.undo()
        XCTAssertEqual(first.document.elements, [a])
        XCTAssertEqual(second.document.elements, [b])
    }

    func testClearPreservesAllDisplaysWhenOneCannotRetainUndo() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let a = Element(tool: .rectangle)
        let b = Element(tool: .text, text: String(repeating: "Long text ", count: 100))
        try first.document.add(a)
        second.document = try CanvasDocument(elements: [b], historyByteLimit: 100)
        model.clearCanvas()
        XCTAssertNotNil(model.errorMessage)
        XCTAssertEqual(first.document.elements, [a])
        XCTAssertEqual(second.document.elements, [b])
    }

    func testBoardBackgroundsBelongOnlyToTheLastUsedDisplay() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        model.setBoard(.light)
        XCTAssertEqual(first.board, .light)
        XCTAssertEqual(second.board, .transparent)
        model.choose(.rectangle)
        try draw(in: second)
        XCTAssertEqual(model.board, .transparent)
        XCTAssertEqual(first.board, .light)
        model.setBoard(.dark)
        XCTAssertEqual(second.board, .dark)
        func centerColor(_ session: OverlaySession) throws -> NSColor {
            let bitmap = try XCTUnwrap(session.canvas.bitmapImageRepForCachingDisplay(in: session.canvas.bounds))
            session.canvas.cacheDisplay(in: session.canvas.bounds, to: bitmap)
            return try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide / 2, y: bitmap.pixelsHigh / 2)?.usingColorSpace(.sRGB))
        }
        XCTAssertGreaterThan(try centerColor(first).redComponent, 0.95)
        XCTAssertLessThan(try centerColor(second).redComponent, 0.2)
        XCTAssertTrue(model.activateDrawingSession(first))
        XCTAssertEqual(model.board, .light)
        model.board = .transparent
        XCTAssertEqual(first.board, .transparent)
        XCTAssertEqual(second.board, .dark)
        XCTAssertLessThan(try centerColor(first).alphaComponent, 0.01)
        model.concealWorkspaceSurfaces()
        model.revealWorkspaceSurfaces(drawing: true)
        XCTAssertEqual(first.board, .transparent)
        XCTAssertEqual(second.board, .dark)
    }

    func testRightClickSelectsTheCorrectDisplayForActions() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let first = try XCTUnwrap(model.active)
        let second = try XCTUnwrap(model.desktopSessions.first { $0 !== first })
        let item = Element(tool: .rectangle, frame: Rect(x: 40, y: 40, width: 120, height: 100))
        try second.document.add(item)
        model.choose(.select)
        let menu = try XCTUnwrap(second.canvas.menu(for: event(.rightMouseDown, item.frame.center, in: second)))
        XCTAssertTrue(model.active === second)
        let index = menu.indexOfItem(withTitle: "Delete selected")
        XCTAssertGreaterThanOrEqual(index, 0)
        menu.performActionForItem(at: index)
        XCTAssertTrue(second.document.elements.isEmpty)
        model.undo()
        XCTAssertEqual(second.document.elements, [item])
        XCTAssertTrue(first.document.elements.isEmpty)
    }

    func testToolbarAlwaysExposesBoardsAndCompactModeWithoutLibraryHistoryButtons() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.setQuickPreferencesExpanded(false)
        for orientation in [ToolbarOrientation.horizontal, .vertical] {
            model.setToolbarOrientation(orientation)
            let host = NSHostingView(rootView: ToolbarView(model: model, availableSize: CGSize(width: 640, height: 800)))
            let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 640, height: 800),
                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.contentView = host
            panel.orderFrontRegardless()
            defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
            NativeAccessibilityNode.enable()
            host.layoutSubtreeIfNeeded()
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
            let nodes = NativeAccessibilityNode.nodes(in: host)
            XCTAssertTrue(nodes.contains { $0.accessibilityLabel() == "Board background" })
            let boardButton = try XCTUnwrap(nodes.first {
                $0.accessibilityLabel() == "Board background" && $0.accessibilityRole() == .button
            })
            for expected in [WorkspaceModel.Board.light, .dark, .transparent] {
                XCTAssertTrue(boardButton.accessibilityPerformPress())
                XCTAssertEqual(model.board, expected)
            }
            let mode = try XCTUnwrap(nodes.first { $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Use apps" })
            XCTAssertLessThanOrEqual(mode.accessibilityFrame().width, 64)
            for name in ["Undo", "Redo", "Clear all drawings — undoable"] {
                XCTAssertTrue(nodes.contains { $0.accessibilityLabel() == name })
            }
        }
        let library = NSHostingView(rootView: ToolLibraryPanel(model: model, section: .constant(.shapes), dismiss: {}))
        library.setFrameSize(library.fittingSize)
        library.layoutSubtreeIfNeeded()
        let names = NativeAccessibilityNode.nodes(in: library).compactMap { $0.accessibilityLabel() }
        XCTAssertFalse(names.contains("Undo"))
        XCTAssertFalse(names.contains("Redo"))
        XCTAssertFalse(names.contains("Clear canvas — undoable"))
    }
}
