import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ParityRegressionTests: XCTestCase {
    func testCaptureSettingsUseTheSharedGlobalPreferences() throws {
        let (root, suite) = try makeModel()
        root.hotkeyRegistered = true
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 80, height: 60), background: .white)
        let editor = NativeCanvasFixture(image: image, settingsOwner: root)
        defer { editor.close(); root.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let shortcut = ActivationShortcut(keyCode: 20, character: "3", control: true)
        editor.model.setActivationShortcut(shortcut)
        editor.model.configureToolbar([.select, .pen, .blur])
        editor.model.setMenuBarVisible(false)
        editor.model.choose(.pen)
        editor.model.applyStyle { $0.width = 9 }
        XCTAssertEqual(root.preferences.activationShortcut, shortcut)
        XCTAssertEqual(root.toolbarTools, [.select, .pen, .cover])
        XCTAssertEqual(root.preferences.showMenuBarIcon, false)
        XCTAssertEqual(root.preferences.toolStyles["pen"]?.width, 9)
        let saved = try WorkspacePreferences.read(try XCTUnwrap(root.preferenceDefaults.data(forKey: "workspace-preferences-v1")))
        XCTAssertEqual(saved.activationShortcut, shortcut)
        XCTAssertEqual(saved.showMenuBarIcon, false)
    }

    func testMenuBarVisibilityRequiresAWorkingRecoveryShortcut() throws {
        let (model, suite) = try makeModel()
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        var changes: [Bool] = []
        model.onMenuBarVisibilityChanged = { changes.append($0) }
        model.setMenuBarVisible(false)
        XCTAssertTrue(changes.isEmpty)
        model.hotkeyRegistered = true
        model.setMenuBarVisible(false)
        model.setMenuBarVisible(true)
        XCTAssertEqual(changes, [false, true])
        XCTAssertEqual(model.preferences.showMenuBarIcon, true)
    }

    func testActivationActionAndDedicatedHideCloseTheActualToolbar() throws {
        let (model, suite) = try makeModel()
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        defer { app.hideTools(); model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        app.activateTools()
        XCTAssertTrue(app.toolbarIsVisible)
        XCTAssertEqual(model.mode, .draw)
        app.activateTools()
        XCTAssertFalse(app.toolbarIsVisible)
        XCTAssertEqual(model.mode, .interact)
        app.activateTools()
        XCTAssertTrue(app.toolbarIsVisible)
        app.hideTools()
        XCTAssertFalse(app.toolbarIsVisible)
        XCTAssertTrue(try XCTUnwrap(model.active).panel.ignoresMouseEvents)
    }

    private func makeModel() throws -> (WorkspaceModel, String) {
        let suite = "native-parity-\(UUID())"
        return (WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite))), suite)
    }

    func testSelectedSolidCensorCanBeDeletedByKeyboard() throws {
        let (model, suite) = try makeModel()
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let censor = Element(tool: .cover, frame: Rect(x: 200, y: 200, width: 100, height: 60))
        try session.document.add(censor)
        session.document.select([censor.id])
        let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                                  windowNumber: session.panel.windowNumber, context: nil,
                                                  characters: "\u{7f}", charactersIgnoringModifiers: "\u{7f}",
                                                  isARepeat: false, keyCode: 51))
        session.canvas.keyDown(with: event)
        XCTAssertFalse(session.document.hasProtection)
        session.document.undo()
        XCTAssertTrue(session.document.hasProtection)
    }

    func testPastedImageIsEditablePersistentAndUndoable() throws {
        let (model, suite) = try makeModel()
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        let data = try PNGExporter.data(elements: [], size: CGSize(width: 40, height: 30), background: .coral)
        try model.insertImage(data)
        let session = try XCTUnwrap(model.active)
        XCTAssertEqual(session.document.elements.first?.tool, .image)
        let embedded = try XCTUnwrap(session.document.elements.first?.imagePNG)
        XCTAssertEqual(try RasterAsset.decode(embedded).width, 40)
        let restored = try DrawingFile.read(DrawingFile(document: session.document).data())
        XCTAssertEqual(restored.elements.first?.imagePNG, embedded)
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    func testCursorFootprintsAndToolbarPreferences() throws {
        let pen = ToolCursorFactory.cursor(tool: .pen, width: 7, style: .toolPreview)
        let eraser = ToolCursorFactory.cursor(tool: .eraser, width: 28, style: .toolPreview)
        XCTAssertGreaterThan(eraser.image.size.width, pen.image.size.width)
        XCTAssertTrue(pen === ToolCursorFactory.cursor(tool: .pen, width: 7, style: .toolPreview))
        let (model, suite) = try makeModel()
        defer { model.closeAll(); UserDefaults.standard.removePersistentDomain(forName: suite) }
        model.configureToolbar([.select, .blur, .pen])
        model.setToolbarOrientation(.vertical)
        let controller = NSHostingController(rootView: ToolbarView(model: model))
        let size = controller.sizeThatFits(in: CGSize(width: 648, height: 900))
        XCTAssertEqual(size.width, 376, accuracy: 1)
        XCTAssertLessThan(size.height, 600)
        let short = NSHostingController(rootView: ToolbarView(model: model, availableSize: CGSize(width: 648, height: 400)))
        XCTAssertLessThanOrEqual(short.sizeThatFits(in: CGSize(width: 648, height: 400)).height, 400)
        let restored = WorkspaceModel(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        defer { restored.closeAll() }
        XCTAssertEqual(restored.toolbarTools, [.select, .cover, .pen])
        XCTAssertEqual(restored.preferences.toolbarOrientation, .vertical)
    }
}
