import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class MyShapesTests: XCTestCase {
    private func selectedDocument() throws -> CanvasDocument {
        var style = Style()
        style.fill = .coral
        let element = Element(tool: .rectangle, frame: Rect(x: 20, y: 30, width: 80, height: 60), style: style)
        var document = try CanvasDocument(elements: [element])
        document.select([element.id])
        return document
    }

    func testWorkspaceInsertionSelectsEditableCopyAtVisibleCenterAndUndoesOnce() throws {
        let suite = "my-shapes-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 800, height: 600), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        editor.session.document = try selectedDocument()
        editor.model.synchronize()
        let original = editor.session.document
        let shape = try SavedShape(name: "Box", document: original)
        let expectedCenter = Point(editor.session.canvas.visibleRect.midX, editor.session.canvas.visibleRect.midY)
        try editor.model.insertSavedShape(shape)
        XCTAssertEqual(editor.session.document.elements.count, 2)
        let inserted = try XCTUnwrap(editor.session.document.elements.last)
        XCTAssertEqual(inserted.frame.center, expectedCenter)
        XCTAssertNotEqual(inserted.id, original.elements[0].id)
        XCTAssertEqual(editor.model.tool, .select)
        XCTAssertEqual(editor.session.document.selection, [inserted.id])
        XCTAssertEqual(editor.model.selectionCount, 1)
        XCTAssertTrue(editor.model.canUndo)
        editor.model.undo()
        XCTAssertEqual(editor.session.document.elements, original.elements)
        XCTAssertEqual(editor.session.document.selection, original.selection)
        XCTAssertFalse(editor.session.document.canUndo)
        editor.model.redo()
        XCTAssertEqual(editor.session.document.elements.last, inserted)
    }

    func testDesktopInsertionFromUseAppsEnablesEditingWithoutSwitchingCanvas() throws {
        let suite = "my-shapes-desktop-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        model.interact()
        let target = try XCTUnwrap(model.active)
        target.document = try selectedDocument()
        model.synchronize()
        let original = target.document
        let shape = try SavedShape(name: "Reusable box", document: original)
        XCTAssertTrue(target.panel.ignoresMouseEvents)
        try model.insertSavedShape(shape)
        XCTAssertTrue(model.active === target)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertFalse(target.panel.ignoresMouseEvents)
        XCTAssertEqual(target.document.selection.count, 1)
        XCTAssertFalse(target.document.selection.contains(original.elements[0].id))
        model.undo()
        XCTAssertEqual(target.document.elements, original.elements)
        XCTAssertEqual(target.document.selection, original.selection)
        XCTAssertFalse(target.document.canUndo)
        model.concealWorkspaceSurfaces()
        XCTAssertThrowsError(try model.insertSavedShape(shape))
        XCTAssertEqual(target.document.elements, original.elements)
        XCTAssertEqual(target.document.selection, original.selection)
    }

    func testLibraryModelSaveReopenDeleteAndReadableFailure() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("my-shapes-ui-\(UUID())")
        defer { try? FileManager.default.removeItem(at: directory) }
        let library = ShapeLibraryModel(directory: directory)
        await library.reload()
        XCTAssertTrue(library.isLoaded)
        let document = try selectedDocument()
        let saved = await library.save(name: "My box", document: document)
        XCTAssertTrue(saved)
        let entry = try XCTUnwrap(library.shapes.first)
        let reopened = ShapeLibraryModel(directory: directory)
        await reopened.reload()
        XCTAssertEqual(reopened.shapes.map(\.id), [entry.id])
        XCTAssertEqual(reopened.shapes.first?.name, "My box")
        let failed = await reopened.save(name: "", document: document)
        XCTAssertFalse(failed)
        XCTAssertTrue(try XCTUnwrap(reopened.errorMessage).contains("Enter a name"))
        XCTAssertEqual(reopened.shapes.count, 1)
        await reopened.delete(entry)
        XCTAssertNil(reopened.errorMessage)
        await library.reload()
        XCTAssertTrue(library.shapes.isEmpty)
    }

    func testThumbnailUsesDrawingRendererWithoutCapture() throws {
        let shape = try SavedShape(name: "Box", document: selectedDocument())
        let view = ShapeThumbnailView(shape: shape)
        view.frame = NSRect(x: 0, y: 0, width: 160, height: 80)
        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        let center = try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide / 2, y: bitmap.pixelsHigh / 2)?
            .usingColorSpace(.sRGB))
        XCTAssertGreaterThan(center.alphaComponent, 0.9)
        XCTAssertGreaterThan(center.redComponent, center.blueComponent)
        XCTAssertFalse(view.isAccessibilityElement())
    }

    func testProductionLibraryInitializationDoesNotResolveOrLoadStorage() {
        let library = ShapeLibraryModel(defaults: .standard)
        XCTAssertNil(library.store)
        XCTAssertFalse(library.isLoaded)
        XCTAssertFalse(library.isBusy)
        XCTAssertTrue(library.shapes.isEmpty)
        XCTAssertNil(library.errorMessage)
    }

    func testLegacyIsolatedDefaultsAutomaticallyUsePrivateInMemoryLibrary() async throws {
        let firstSuite = "my-shapes-scope-a-\(UUID())"
        let secondSuite = "my-shapes-scope-b-\(UUID())"
        let firstDefaults = try XCTUnwrap(UserDefaults(suiteName: firstSuite))
        let secondDefaults = try XCTUnwrap(UserDefaults(suiteName: secondSuite))
        let firstModel = WorkspaceModel(defaults: firstDefaults)
        let secondModel = WorkspaceModel(defaults: secondDefaults)
        defer {
            firstModel.closeAll()
            secondModel.closeAll()
            firstDefaults.removePersistentDomain(forName: firstSuite)
            secondDefaults.removePersistentDomain(forName: secondSuite)
        }
        let first = MyShapesLibrary(model: firstModel).library
        let second = MyShapesLibrary(model: secondModel).library
        XCTAssertTrue(first === MyShapesLibrary(model: firstModel).library)
        XCTAssertFalse(first === second)
        let firstStore = try XCTUnwrap(first.store)
        let secondStore = try XCTUnwrap(second.store)
        XCTAssertNil(firstStore.directory)
        XCTAssertNil(secondStore.directory)
        await first.reload()
        await second.reload()
        XCTAssertTrue(first.shapes.isEmpty)
        XCTAssertTrue(second.shapes.isEmpty)
        let saved = await first.save(name: "Synthetic fixture", document: try selectedDocument())
        XCTAssertTrue(saved)
        XCTAssertEqual(MyShapesLibrary(model: firstModel).library.shapes.count, 1)
        await second.reload()
        XCTAssertTrue(second.shapes.isEmpty)
        XCTAssertNil(firstStore.directory)
    }
}
