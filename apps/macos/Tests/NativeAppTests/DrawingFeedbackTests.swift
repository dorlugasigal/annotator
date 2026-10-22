import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class DrawingFeedbackTests: XCTestCase {
    private func mouse(_ type: NSEvent.EventType, at point: Point, session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
                                        modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber,
                                        context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }

    func testSmartDrawPreviewsBeforeReleaseAndCancelsWithoutCommitting() async throws {
        let suite = "smart-preview-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 600, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        editor.model.smartDraw = true
        editor.model.choose(.pen)
        let session = editor.session
        session.canvas.mouseDown(with: try mouse(.leftMouseDown, at: Point(330, 200), session: session))
        for index in 1...120 {
            let angle = Double(index) / 120 * Double.pi * 2
            session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, at: Point(250 + 80 * cos(angle), 200 + 60 * sin(angle)), session: session))
        }
        for _ in 0..<100 {
            if session.canvas.smartPreview != nil { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(session.canvas.smartPreview?.tool, .ellipse)
        XCTAssertEqual(session.document.elements.first?.tool, .pen)
        XCTAssertTrue(session.document.isEditing)
        session.canvas.cancelGesture()
        try await Task.sleep(for: .milliseconds(20))
        XCTAssertNil(session.canvas.smartPreview)
        XCTAssertTrue(session.document.elements.isEmpty)
        XCTAssertFalse(session.document.canUndo)
    }

    func testEraserTargetsAreDeferredAndCancellationRestoresThem() throws {
        let suite = "eraser-preview-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 600, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let shape = Element(tool: .rectangle, frame: Rect(x: 200, y: 100, width: 100, height: 100))
        try editor.session.document.add(shape)
        editor.model.choose(.eraser)
        editor.session.canvas.mouseDown(with: try mouse(.leftMouseDown, at: shape.frame.center, session: editor.session))
        XCTAssertEqual(editor.session.canvas.eraserPreviewIDs, [shape.id])
        XCTAssertEqual(editor.session.document.elements.count, 1)
        editor.session.canvas.cancelGesture()
        XCTAssertTrue(editor.session.canvas.eraserPreviewIDs.isEmpty)
        XCTAssertEqual(editor.session.document.elements.count, 1)
    }

    private func eraserFixture(_ name: String) throws -> (NativeCanvasFixture, String) {
        let suite = "\(name)-\(UUID())"
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 600, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        editor.show()
        return (editor, suite)
    }

    private func erase(_ shape: Element, in editor: NativeCanvasFixture) throws {
        editor.model.choose(.eraser)
        editor.session.canvas.mouseDown(with: try mouse(.leftMouseDown, at: shape.frame.center, session: editor.session))
        editor.session.canvas.mouseUp(with: try mouse(.leftMouseUp, at: shape.frame.center, session: editor.session))
    }

    func testEraserFadeDeletesOnReleaseAndStopsItsTicker() async throws {
        let (editor, suite) = try eraserFixture("eraser-fade")
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.reducesMotion = { false }
        let shape = Element(tool: .rectangle, frame: Rect(x: 200, y: 100, width: 100, height: 100))
        try editor.session.document.add(shape)
        try erase(shape, in: editor)
        XCTAssertTrue(editor.session.document.elements.isEmpty, "Deletion is applied before the fade starts.")
        XCTAssertTrue(canvas.isEraseFading)
        XCTAssertTrue(canvas.transitionTickersRunning)
        for _ in 0..<200 where canvas.isEraseFading { try await Task.sleep(for: .milliseconds(5)) }
        XCTAssertFalse(canvas.isEraseFading)
        XCTAssertFalse(canvas.transitionTickersRunning)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements.map(\.id), [shape.id])
    }

    func testUndoEndsTheEraserFadeAndRestoresTheObject() async throws {
        let (editor, suite) = try eraserFixture("eraser-fade-undo")
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.reducesMotion = { true }
        let shape = Element(tool: .ellipse, frame: Rect(x: 200, y: 100, width: 100, height: 100))
        try editor.session.document.add(shape)
        try erase(shape, in: editor)
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        XCTAssertTrue(canvas.isEraseFading, "Reduce Motion keeps an opacity-only fade.")
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements.map(\.id), [shape.id])
        try await Task.sleep(for: .milliseconds(60))
        XCTAssertFalse(canvas.isEraseFading)
        XCTAssertFalse(canvas.transitionTickersRunning)
    }

    func testDrawingHUDSettlesOnTheLatestRequestAndIgnoresClicksWhileFadingOut() throws {
        let (editor, suite) = try eraserFixture("hud-fade")
        defer { editor.close(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.reducesMotion = { false }
        let button = try XCTUnwrap(canvas.subviews.compactMap { $0 as? NSButton }.first { $0.title.contains("Use apps") })
        let inside = CGPoint(x: button.frame.midX, y: button.frame.midY)
        func settle() { RunLoop.current.run(until: Date().addingTimeInterval(0.35)) }
        canvas.setDrawingHUDVisible(true)
        settle()
        XCTAssertFalse(button.isHidden)
        XCTAssertNotNil(button.hitTest(inside))

        canvas.setDrawingHUDVisible(false)
        XCTAssertFalse(canvas.drawingHUDVisible)
        XCTAssertNil(button.hitTest(inside), "A fading button never takes a click.")
        settle()
        XCTAssertTrue(button.isHidden)

        canvas.setDrawingHUDVisible(true)
        XCTAssertFalse(button.isHidden)
        XCTAssertNotNil(button.layer?.animation(forKey: "hud.rise"))
        canvas.setDrawingHUDVisible(false)
        canvas.setDrawingHUDVisible(true)
        settle()
        XCTAssertTrue(canvas.drawingHUDVisible)
        XCTAssertFalse(button.isHidden, "An older fade-out must not hide the button after a newer show.")
        XCTAssertNotNil(button.hitTest(inside))

        canvas.setDrawingHUDVisible(false)
        canvas.setDrawingHUDVisible(true)
        canvas.setDrawingHUDVisible(false)
        settle()
        XCTAssertTrue(button.isHidden)
        XCTAssertFalse(canvas.drawingHUDVisible)

        canvas.reducesMotion = { true }
        canvas.setDrawingHUDVisible(true)
        XCTAssertNil(button.layer?.animation(forKey: "hud.rise"), "Reduce Motion fades without movement.")
        XCTAssertNotNil(button.layer?.animation(forKey: "hud.opacity"))
        settle()
        XCTAssertFalse(button.isHidden)
    }

    func testHintTextCrossfadesOnlyForSettledChanges() throws {
        let window = NSWindow(contentRect: CGRect(x: 200, y: 200, width: 600, height: 200), styleMask: [.borderless],
                              backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let hint = WorkspaceHintView(frame: CGRect(x: 16, y: 14, width: 540, height: 130))
        window.contentView?.addSubview(hint)
        window.orderFrontRegardless()
        defer { window.orderOut(nil); window.close() }
        hint.text = "Drawing"
        RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        hint.layer?.removeAllAnimations()
        hint.text = "Using apps"
        XCTAssertNotNil(hint.layer?.animation(forKey: kCATransition))
        hint.layer?.removeAllAnimations()
        hint.text = "Zoom 1.1×"
        XCTAssertNil(hint.layer?.animation(forKey: kCATransition), "Rapid value updates change in place.")
        XCTAssertEqual(hint.accessibilityValue() as? String, "Zoom 1.1×")
    }
}
