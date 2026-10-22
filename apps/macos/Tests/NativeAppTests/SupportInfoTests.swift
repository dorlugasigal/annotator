import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class SupportInfoTests: XCTestCase {
    func testBrokenBoardErrorStaysVisibleWithoutOpeningSettingsOrChangingWork() async throws {
        let suite = "board-error-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let errors = WorkspaceErrorController(model: model)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer {
            errors.close()
            model.closeAll()
            defaults.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: folder)
        }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 100, height: 80))
        try session.document.add(shape)
        session.document.select([shape.id])
        model.synchronize()
        let generation = session.document.generation
        let broken = folder.appendingPathComponent("broken.annotator")
        try Data("{\"schema\":1,\"elements\":[".utf8).write(to: broken)
        var settingsOpened = false
        model.onOpenPreferences = { settingsOpened = true }
        model.openBoard(from: broken)
        let message = try XCTUnwrap(model.errorMessage)
        for _ in 0..<100 {
            if errors.window.isVisible { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(errors.window.isVisible)
        XCTAssertEqual(errors.displayedMessage, message)
        // Settings appearing or disappearing must not dismiss a different error host.
        model.settingsVisible = true
        model.settingsVisible = false
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertTrue(errors.window.isVisible)
        XCTAssertEqual(model.errorMessage, message)
        XCTAssertFalse(settingsOpened)
        XCTAssertFalse(model.settingsVisible)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertEqual(session.document.elements, [shape])
        XCTAssertEqual(session.document.selection, [shape.id])
        XCTAssertEqual(session.document.generation, generation)
        XCTAssertTrue(session.document.canUndo)
        errors.dismiss()
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(errors.window.isVisible)
        let valid = folder.appendingPathComponent("valid.annotator")
        let replacement = Element(tool: .ellipse, frame: Rect(x: 200, y: 200, width: 60, height: 60))
        try DrawingFile(document: CanvasDocument(elements: [replacement])).data().write(to: valid)
        model.openBoard(from: valid)
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(try XCTUnwrap(model.active).document.elements.map(\.id), [replacement.id])
    }

    func testErrorDismissalDoesNotClearANewerErrorAndWindowCloseAcknowledgesIt() async throws {
        let suite = "error-replacement-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let errors = WorkspaceErrorController(model: model)
        defer { errors.close(); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.report(CanvasError.invalidDocument("First error"))
        for _ in 0..<100 {
            if errors.window.isVisible { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        model.report(CanvasError.invalidDocument("Second error"))
        errors.dismiss()
        XCTAssertTrue(try XCTUnwrap(model.errorMessage).contains("Second error"))
        XCTAssertEqual(errors.displayedMessage, model.errorMessage)
        errors.window.performClose(nil)
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(errors.window.isVisible)
    }

    func testGlobalEscapeAcknowledgesErrorBeforeChangingDrawingMode() async throws {
        let suite = "error-escape-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.beginDrawing()
        model.report(CanvasError.invalidDocument("Choose another board."))
        for _ in 0..<100 {
            if app.errorWindow?.isVisible == true { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(app.errorWindow?.isVisible == true)
        model.handleZoomEscape()
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(app.errorWindow?.isVisible == true)
        XCTAssertEqual(model.mode, .draw)
    }

    func testSupportInfoContainsZoomDiagnosticsButNoDrawingContent() throws {
        let suite = "support-info-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let pasteboard = NSPasteboard.withUniqueName()
        defer {
            model.closeAll()
            pasteboard.releaseGlobally()
            defaults.removePersistentDomain(forName: suite)
        }
        model.beginDrawing()
        model.interact()
        try model.active?.document.add(Element(tool: .text, frame: Rect(x: 10, y: 10, width: 200, height: 50),
                                               text: "private-drawing-content-684"))
        model.copySupportInfo(to: pasteboard)
        let copied = try XCTUnwrap(pasteboard.string(forType: .string))
        XCTAssertTrue(copied.contains("Running build:"))
        XCTAssertTrue(copied.contains("Version: \(model.versionLabel)"))
        XCTAssertTrue(copied.contains("Scroll shortcut: Option only"))
        XCTAssertTrue(copied.contains("Global scroll monitor active:"))
        XCTAssertFalse(copied.contains("private-drawing-content-684"))
        XCTAssertNil(model.errorMessage)
    }
}
