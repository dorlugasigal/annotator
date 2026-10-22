import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class LocalShortcutRecoveryTests: XCTestCase {
    func testLocalControlOneStopsAnActiveZoomInsteadOfRequiringGlobalDispatch() throws {
        let suite = "local-control-one-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 200, height: 150), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        editor.model.synchronize()
        XCTAssertTrue(editor.session.panel.performKeyEquivalent(with: try key(18, "1", flags: .control, window: editor.session.panel)))
        XCTAssertFalse(editor.model.screenZoomEnabled)
        XCTAssertEqual(editor.model.zoomStartState, .idle)
    }

    func testLocalEscapeStopsZoomWithoutAGlobalEventTap() throws {
        let suite = "local-zoom-escape-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 200, height: 150), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        let model = editor.model
        try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        model.synchronize()
        XCTAssertFalse(model.zoomGesturesAvailable)
        let escape = try key(53, "", flags: [], window: editor.session.panel)
        editor.session.canvas.keyDown(with: escape)
        XCTAssertFalse(model.screenZoomEnabled)
        try editor.session.document.setPresentation(PresentationSettings(mode: .lens, zoom: 2))
        model.synchronize()
        XCTAssertTrue(editor.session.panel.performKeyEquivalent(with: escape))
        XCTAssertFalse(model.magnifierEnabled)
    }

    func testToolbarReceivesConfiguredShortcutsThroughNormalKeyRouting() throws {
        let suite = "local-toolbar-shortcuts-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let panel = AnnotationPanel(contentRect: NSRect(x: 0, y: 0, width: 200, height: 100),
                                    styleMask: [.borderless], backing: .buffered, defer: false)
        panel.imagePasteModel = model
        defer { panel.orderOut(nil); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        var toggles = 0, hides = 0
        model.onActivationRequested = { toggles += 1 }
        model.onHideToolbar = { hides += 1 }
        XCTAssertTrue(panel.performKeyEquivalent(with: try key(19, "2", flags: .control, window: panel)))
        XCTAssertEqual(toggles, 1)
        XCTAssertTrue(panel.performKeyEquivalent(with: try key(19, "2", flags: [.control, .option], window: panel)))
        XCTAssertEqual(hides, 1)
        _ = panel.performKeyEquivalent(with: try key(19, "2", flags: [], window: panel))
        XCTAssertEqual(toggles, 1)
    }

    private func key(_ code: UInt16, _ text: String, flags: NSEvent.ModifierFlags, window: NSWindow) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0,
            windowNumber: window.windowNumber, context: nil, characters: text, charactersIgnoringModifiers: text,
            isARepeat: false, keyCode: code))
    }
}
