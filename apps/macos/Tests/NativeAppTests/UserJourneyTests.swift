import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class UserJourneyTests: XCTestCase {
    private func mouse(_ type: NSEvent.EventType, at point: Point, session: OverlaySession) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(point.cgPoint, to: nil),
            modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber, context: nil,
            eventNumber: 0, clickCount: 1, pressure: 1))
    }

    private func draw(_ tool: Tool, from start: Point, to end: Point, in editor: NativeCanvasFixture) throws {
        editor.model.choose(tool)
        editor.session.canvas.mouseDown(with: try mouse(.leftMouseDown, at: start, session: editor.session))
        editor.session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, at: end, session: editor.session))
        editor.session.canvas.mouseUp(with: try mouse(.leftMouseUp, at: end, session: editor.session))
    }

    func testCreateReviseProtectSaveReopenExportAndRecoverJourney() throws {
        let suite = "full-image-journey-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("annotator-journey-\(UUID()).annotator")
        defer {
            editor.close(); defaults.removePersistentDomain(forName: suite)
            if FileManager.default.fileExists(atPath: file.path) {
                do { try FileManager.default.removeItem(at: file) }
                catch { XCTFail("Journey fixture cleanup failed: \(error)") }
            }
        }
        try draw(.rectangle, from: Point(30, 30), to: Point(150, 90), in: editor)
        let rectangle = try XCTUnwrap(editor.session.document.elements.first)
        try draw(.pen, from: Point(40, 350), to: Point(220, 390), in: editor)
        editor.model.choose(.text)
        editor.session.canvas.mouseDown(with: try mouse(.leftMouseDown, at: Point(330, 80), session: editor.session))
        let text = try XCTUnwrap(editor.session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
        text.insertText("An editable explanation\nwith a second line.", replacementRange: NSRange(location: NSNotFound, length: 0))
        editor.session.canvas.commitTextIfNeeded()
        let textID = try XCTUnwrap(editor.session.document.elements.last).id

        editor.session.document.select([rectangle.id, textID])
        editor.model.perform { try $0.groupSelection() }
        editor.model.perform { try $0.setSelectionLocked(true) }
        let protectedCount = editor.session.document.elements.count
        editor.model.deleteSelected()
        XCTAssertEqual(editor.session.document.elements.count, protectedCount)
        editor.model.perform { try $0.setSelectionLocked(false) }
        editor.model.deleteSelected()
        XCTAssertEqual(editor.session.document.elements.count, 1)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements.count, protectedCount)

        let legacyCover = Element(tool: .cover, frame: Rect(x: 200, y: 100, width: 100, height: 80))
        try editor.session.document.add(legacyCover)
        editor.session.document.select([legacyCover.id])
        editor.model.synchronize()
        let coverID = try XCTUnwrap(editor.session.document.elements.last).id
        editor.model.setCoverMode(.pixelate)
        XCTAssertEqual(editor.session.document.elements.last?.id, coverID)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements.last?.tool, .cover)
        try editor.model.insertImage(PNGExporter.data(elements: [], size: CGSize(width: 24, height: 24), background: .blue))
        let before = editor.session.document.elements
        try DrawingFile(document: editor.session.document).data().write(to: file, options: .atomic)
        let restored = try DrawingFile.read(Data(contentsOf: file))
        XCTAssertEqual(restored.elements.map(\.id), before.map(\.id))
        XCTAssertEqual(restored.elements.first(where: { $0.id == textID })?.text, "An editable explanation\nwith a second line.")
        XCTAssertNotNil(restored.elements.last?.imagePNG)

        let exported = try RasterAsset.decode(editor.output())
        XCTAssertEqual(exported.width, 800)
        XCTAssertEqual(exported.height, 600)
        let pixel = try XCTUnwrap(NSBitmapImageRep(cgImage: exported).colorAt(x: 250, y: 140)?.usingColorSpace(.sRGB))
        XCTAssertLessThan(pixel.redComponent, 0.01)
        XCTAssertLessThan(pixel.greenComponent, 0.01)
        XCTAssertLessThan(pixel.blueComponent, 0.01)

        editor.model.clearCanvas()
        XCTAssertTrue(editor.session.document.elements.isEmpty)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements, before)
        editor.session.document = try CanvasDocument(elements: restored.elements)
        editor.model.synchronize()
        XCTAssertEqual(editor.session.document.elements.map(\.id), before.map(\.id))
    }

    func testSharedPreferencesDoNotMixDocumentsAcrossImageWindows() throws {
        let suite = "two-image-journey-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let owner = WorkspaceModel(defaults: defaults)
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 300, height: 200), background: .white)
        let first = NativeCanvasFixture(image: image, settingsOwner: owner)
        let second = NativeCanvasFixture(image: image, settingsOwner: owner)
        defer { first.close(); second.close(); owner.closeAll(); defaults.removePersistentDomain(forName: suite) }
        try draw(.rectangle, from: Point(30, 30), to: Point(130, 100), in: first)
        first.model.choose(.pen)
        first.model.applyStyle { $0.width = 9 }
        first.model.setAnimationStyle(.slide)
        second.model.choose(.pen)
        XCTAssertEqual(second.model.style.width, 9)
        XCTAssertEqual(second.model.preferences.visibilityAnimationStyle, .slide)
        XCTAssertTrue(second.session.document.elements.isEmpty)
        second.model.clearCanvas()
        XCTAssertEqual(first.session.document.elements.count, 1)
    }
}
