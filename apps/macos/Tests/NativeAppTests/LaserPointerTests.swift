import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class LaserPointerTests: XCTestCase {
    private func fixture(_ name: String) throws -> (NativeCanvasFixture, UserDefaults, String) {
        let suite = "\(name)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 600, height: 400), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        editor.model.choose(.laser)
        editor.show()
        return (editor, defaults, suite)
    }

    private func event(_ type: NSEvent.EventType, _ point: Point, on editor: NativeCanvasFixture) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: editor.session.canvas.convert(point.cgPoint, to: nil),
            modifierFlags: [], timestamp: 0, windowNumber: editor.session.panel.windowNumber,
            context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }

    private func waitUntilGone(_ canvas: DrawingCanvas) async throws {
        let deadline = Date().addingTimeInterval(LaserTrail.defaultLifetime + 2)
        while !canvas.laserTrail.isEmpty && Date() < deadline { try await Task.sleep(for: .milliseconds(20)) }
    }

    func testLaserTrailFadesAwayAndNeverChangesTheDrawing() async throws {
        let (editor, defaults, suite) = try fixture("laser-trail")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        let generation = editor.session.document.generation
        canvas.mouseDown(with: try event(.leftMouseDown, Point(100, 100), on: editor))
        for index in 1...40 {
            canvas.mouseDragged(with: try event(.leftMouseDragged, Point(100 + Double(index) * 6, 100 + Double(index) * 2), on: editor))
        }
        XCTAssertTrue(canvas.laserTrail.isDrawing)
        XCTAssertEqual(canvas.laserTrail.strokes.first?.color, LaserTrail.defaultColor)
        XCTAssertTrue(canvas.isLaserFading, "The trail redraws while it fades behind the pointer.")
        canvas.mouseUp(with: try event(.leftMouseUp, Point(340, 180), on: editor))
        XCTAssertFalse(canvas.laserTrail.isDrawing)
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        XCTAssertEqual(editor.session.document.generation, generation)
        XCTAssertFalse(editor.model.canUndo, "The laser pointer leaves no Undo step.")
        XCTAssertEqual(editor.model.tool, .laser, "The laser pointer stays chosen after a stroke.")
        try await waitUntilGone(canvas)
        XCTAssertTrue(canvas.laserTrail.isEmpty)
        XCTAssertFalse(canvas.isLaserFading, "Nothing ticks once the trail is gone.")
    }

    func testHeldLaserRestsWithoutTickingAndEscapeLetsItFade() async throws {
        let (editor, defaults, suite) = try fixture("laser-rest")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.mouseDown(with: try event(.leftMouseDown, Point(200, 150), on: editor))
        XCTAssertTrue(canvas.laserTrail.isResting)
        XCTAssertFalse(canvas.isLaserFading, "A dot that doesn't change needs no frames.")
        canvas.mouseDragged(with: try event(.leftMouseDragged, Point(240, 150), on: editor))
        XCTAssertTrue(canvas.isLaserFading)
        canvas.cancelGesture()
        XCTAssertFalse(canvas.laserTrail.isDrawing)
        try await waitUntilGone(canvas)
        XCTAssertTrue(canvas.laserTrail.isEmpty)
    }

    /// Tester request: the trail disappears too fast, so Fade in the laser pointer's properties sets how long it stays.
    func testFadeInThePropertiesSetsHowLongTheNextTrailStays() throws {
        let (editor, defaults, suite) = try fixture("laser-fade")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        NativeAccessibilityNode.enable()
        let host = NSHostingView(rootView: ContextualInspector(model: editor.model))
        host.frame = CGRect(x: 0, y: 0, width: 700, height: 80)
        host.layoutSubtreeIfNeeded()
        let fade = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .slider && $0.accessibilityLabel() == "Fade"
        }, "The laser pointer's properties have a Fade slider.")
        XCTAssertEqual((fade.accessibilityValue() as? NSNumber)?.doubleValue ?? 0, LaserTrail.defaultLifetime, accuracy: 0.001)
        editor.model.setLaserFade(2.46)
        XCTAssertEqual(editor.model.laserFade, 2.5, "Fade keeps tenths of a second.")
        let canvas = editor.session.canvas
        canvas.mouseDown(with: try event(.leftMouseDown, Point(100, 100), on: editor))
        XCTAssertEqual(canvas.laserTrail.lifetime, 2.5, "The next stroke uses the new Fade.")
        canvas.mouseUp(with: try event(.leftMouseUp, Point(100, 100), on: editor))
        let saved = try WorkspacePreferences.read(try XCTUnwrap(editor.model.preferenceDefaults.data(forKey: "workspace-preferences-v1")))
        XCTAssertEqual(saved.laserFade, 2.5, "Fade is saved for the next time.")
        editor.model.setLaserFade(LaserTrail.defaultLifetime)
        XCTAssertNil(editor.model.preferences.laserFade, "The default saves nothing.")
    }

    func testEachPressKeepsItsOwnColorWhileEarlierTrailsFade() throws {
        let (editor, defaults, suite) = try fixture("laser-colors")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let canvas = editor.session.canvas
        canvas.mouseDown(with: try event(.leftMouseDown, Point(50, 50), on: editor))
        canvas.mouseDragged(with: try event(.leftMouseDragged, Point(120, 60), on: editor))
        canvas.mouseUp(with: try event(.leftMouseUp, Point(120, 60), on: editor))
        editor.model.applyStyle { $0.color = .blue; $0.width = 12 }
        canvas.mouseDown(with: try event(.leftMouseDown, Point(80, 200), on: editor))
        XCTAssertEqual(canvas.laserTrail.strokes.map(\.color), [LaserTrail.defaultColor, .blue])
        XCTAssertEqual(canvas.laserTrail.strokes.map(\.width), [LaserTrail.defaultWidth, 12])
        canvas.mouseUp(with: try event(.leftMouseUp, Point(80, 200), on: editor))
        XCTAssertTrue(editor.session.document.elements.isEmpty)
    }

    func testLaserCursorIsADotInTheLaserColor() throws {
        let (editor, defaults, suite) = try fixture("laser-cursor")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let cursor = ToolCursorFactory.cursor(tool: .laser, width: 6, color: LaserTrail.defaultColor)
        XCTAssertEqual(cursor.image.size, CGSize(width: 18, height: 18))
        XCTAssertEqual(cursor.hotSpot, NSPoint(x: 9, y: 9))
        XCTAssertTrue(cursor === ToolCursorFactory.cursor(tool: .laser, width: 6, color: LaserTrail.defaultColor))
        XCTAssertFalse(cursor === ToolCursorFactory.cursor(tool: .laser, width: 6, color: .blue))
        editor.session.canvas.refreshToolCursor()
        XCTAssertTrue(editor.session.canvas.currentToolCursor === ToolCursorFactory.cursor(
            tool: .laser, width: editor.model.style.width, color: editor.model.style.color))
    }

    func testLaserKeepsItsOwnRedWhenToolsShareAStyle() throws {
        let (editor, defaults, suite) = try fixture("laser-shared-style")
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.model.setShareToolStyles(true)
        editor.model.choose(.pen)
        editor.model.applyStyle { $0.color = .blue }
        editor.model.choose(.laser)
        XCTAssertEqual(editor.model.style.color, LaserTrail.defaultColor)
    }
}
