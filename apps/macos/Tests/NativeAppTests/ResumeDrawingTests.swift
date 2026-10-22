import AppKit
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ResumeDrawingTests: XCTestCase {
    func testDrawingToolbarSnapshotIsReadyBeforeItsEntranceBegins() async throws {
        let suite = "drawing-toolbar-snapshot-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        app.finishLaunchPresentation(isLoginItem: false)
        try await Task.sleep(for: .milliseconds(100))
        app.hideTools()
        XCTAssertTrue(model.prepareDrawingForReveal())
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        let before = try animator.snapshotToolbar(toolbar, pixelBudget: 4_000_000)
        try await Task.sleep(for: .milliseconds(100))
        let after = try animator.snapshotToolbar(toolbar, pixelBudget: 4_000_000)
        XCTAssertEqual(before.width, after.width)
        XCTAssertEqual(before.height, after.height)
        XCTAssertEqual(try XCTUnwrap(before.dataProvider?.data) as Data,
                       try XCTUnwrap(after.dataProvider?.data) as Data,
                       "Drawing controls must be in the snapshot, not change after landing.")
    }

    func testControl2RevealPreparesDrawingModeAndPreservesSelectionAndHistory() async throws {
        let suite = "show-without-selecting-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        defer {
            model.setVisibilityAnimation(false); app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        model.resumeDrawing()
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 100, y: 100, width: 100, height: 80))
        try session.document.add(shape)
        session.document.select([shape.id])
        let generation = session.document.generation
        let hud = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSButton }
            .first { $0.title.contains("Use apps") })
        for style in [VisibilityAnimationStyle.none, .fade] {
            model.setAnimationStyle(style)
            app.hideTools()
            app.activateTools()
            XCTAssertEqual(model.mode, .draw, "Drawing state is prepared before the toolbar enters.")
            if style != .none { XCTAssertTrue(session.panel.ignoresMouseEvents) }
            try await Task.sleep(for: .milliseconds(350))
            XCTAssertEqual(model.mode, .draw)
            XCTAssertFalse(session.panel.ignoresMouseEvents)
            XCTAssertFalse(hud.isHidden)
            XCTAssertEqual(session.document.selection, [shape.id])
            XCTAssertEqual(session.document.generation, generation)
            model.resumeDrawing()
            XCTAssertEqual(model.mode, .draw)
            XCTAssertFalse(hud.isHidden)
            XCTAssertFalse(session.panel.ignoresMouseEvents)
        }
    }

    func testControl2RevealCreatesADrawingSessionAfterAnEmptyStartup() throws {
        let suite = "control2-empty-startup-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        app.finishLaunchPresentation(isLoginItem: false)
        XCTAssertNil(model.active)
        app.activateTools()
        XCTAssertTrue(model.workspaceHidden)
        app.activateTools()
        XCTAssertFalse(model.workspaceHidden)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertFalse(try XCTUnwrap(model.active).panel.ignoresMouseEvents)
    }

    func testDesktopResumePreservesSelectionAndHistoryAndNeverHides() throws {
        let suite = "resume-desktop-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        defer {
            model.setVisibilityAnimation(false)
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        model.resumeDrawing()
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 40, y: 50, width: 80, height: 60))
        try session.document.add(shape)
        session.document.select([shape.id])
        let generation = session.document.generation
        for hide in [false, true] {
            if hide { app.hideTools() } else { model.interact() }
            for _ in 0..<3 { model.resumeDrawing() }
            XCTAssertEqual(model.mode, .draw)
            XCTAssertFalse(model.workspaceHidden)
            XCTAssertTrue(app.toolbarIsVisible)
            XCTAssertFalse(session.panel.ignoresMouseEvents)
            XCTAssertEqual(session.document.elements, [shape])
            XCTAssertEqual(session.document.selection, [shape.id])
            XCTAssertEqual(session.document.generation, generation)
        }
        session.document.undo()
        XCTAssertTrue(session.document.elements.isEmpty)
    }

    func testResumeReversesHideAndRepeatedPressDoesNotRestartShow() async throws {
        let suite = "resume-animation-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        let app = AppDelegate(model: model, visibility: animator)
        defer {
            model.setVisibilityAnimation(false)
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        model.resumeDrawing()
        model.setAnimationStyle(.slide)
        app.hideTools()
        XCTAssertTrue(model.visibilityAnimating)
        XCTAssertFalse(animator.targetVisible)
        model.resumeDrawing()
        XCTAssertTrue(animator.targetVisible)
        // Continue pressing past the original deadline: a restarted show never finishes here.
        for _ in 0..<8 {
            model.resumeDrawing()
            try await Task.sleep(for: .milliseconds(60))
        }
        XCTAssertFalse(model.visibilityAnimating)
        XCTAssertFalse(model.workspaceHidden)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertEqual(animator.transientPanelCount, 0)
    }

    func testImageEditorLocalShortcutResumesWithoutOpeningDesktopOrChangingWork() throws {
        let suite = "resume-image-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let owner = WorkspaceModel(defaults: defaults)
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 240, height: 180), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        editor.model.connectSettingsOwner(owner)
        defer { editor.close(); owner.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let shape = Element(tool: .ellipse, frame: Rect(x: 30, y: 30, width: 40, height: 40))
        try editor.session.document.add(shape)
        editor.session.document.select([shape.id])
        let generation = editor.session.document.generation
        for hide in [false, true] {
            if hide { editor.setToolsVisible(false) } else { editor.model.interact() }
            for _ in 0..<3 {
                XCTAssertTrue(editor.session.panel.performKeyEquivalent(with:
                    try key(19, "2", flags: [.control, .shift], panel: editor.session.panel)))
            }
            XCTAssertTrue(editor.toolsVisible)
            XCTAssertEqual(editor.model.mode, .draw)
            XCTAssertEqual(editor.session.document.elements, [shape])
            XCTAssertEqual(editor.session.document.selection, [shape.id])
            XCTAssertEqual(editor.session.document.generation, generation)
            XCTAssertNil(owner.active)
        }
    }

    func testCustomLocalShortcutAndUnmodifiedTyping() throws {
        let suite = "resume-local-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let panel = AnnotationPanel(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
        panel.imagePasteModel = model
        defer { panel.orderOut(nil); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        var resumes = 0
        model.onResumeDrawingRequested = { resumes += 1 }
        model.setResumeShortcut(ActivationShortcut(keyCode: 15, character: "r", control: true, shift: true))
        XCTAssertTrue(panel.performKeyEquivalent(with: try key(15, "R", flags: [.control, .shift], panel: panel)))
        _ = panel.performKeyEquivalent(with: try key(15, "r", flags: [], panel: panel))
        _ = panel.performKeyEquivalent(with: try key(19, "2", flags: [.control, .shift], panel: panel))
        XCTAssertEqual(resumes, 1)
    }

    func testResumePreferenceSharedPersistedAndRegistrationFailurePreservesBinding() throws {
        let suite = "resume-settings-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let owner = WorkspaceModel(defaults: defaults)
        let child = WorkspaceModel(defaults: defaults)
        child.connectSettingsOwner(owner)
        defer { child.closeAll(); owner.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let shortcut = ActivationShortcut(keyCode: 15, character: "r", control: true, shift: true)
        var registered: [ActivationShortcut] = []
        owner.onResumeShortcutChanged = { registered.append($0) }
        child.setResumeShortcut(shortcut)
        XCTAssertEqual(registered, [shortcut])
        XCTAssertEqual(child.preferences.resumeShortcut, shortcut)
        let data = try XCTUnwrap(defaults.data(forKey: "workspace-preferences-v1"))
        XCTAssertEqual(try WorkspacePreferences.read(data).resumeShortcut, shortcut)
        owner.onResumeShortcutChanged = { _ in throw CanvasError.invalidDocument("Shortcut already used.") }
        child.setResumeShortcut(.resumeDrawing)
        XCTAssertNotNil(child.errorMessage)
        XCTAssertEqual(owner.preferences.resumeShortcut, shortcut)
        XCTAssertEqual(defaults.data(forKey: "workspace-preferences-v1"), data)
        child.setResumeShortcut(.standard)
        XCTAssertEqual(owner.preferences.resumeShortcut, shortcut)
    }

    func testFailedProfileRestoresAllFourShortcuts() throws {
        let suite = "resume-profile-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let before = model.preferences
        var incoming = before
        incoming.resumeShortcut = ActivationShortcut(keyCode: 15, character: "r", control: true, shift: true)
        var activations: [ActivationShortcut] = [], hides: [ActivationShortcut] = []
        var zooms: [ActivationShortcut] = [], resumes: [ActivationShortcut] = []
        model.onActivationShortcutChanged = { activations.append($0) }
        model.onHideShortcutChanged = { hides.append($0) }
        model.onZoomShortcutChanged = { zooms.append($0) }
        model.onResumeShortcutChanged = {
            resumes.append($0)
            if $0 != .resumeDrawing { throw CanvasError.invalidDocument("Shortcut already used.") }
        }
        XCTAssertThrowsError(try model.registerProfileShortcuts(incoming))
        XCTAssertEqual(activations, [.standard, .standard])
        XCTAssertEqual(hides, [.hideTools, .hideTools])
        XCTAssertEqual(zooms, [.liveZoom, .liveZoom])
        XCTAssertEqual(resumes, [try XCTUnwrap(incoming.resumeShortcut), .resumeDrawing])
        XCTAssertEqual(model.preferences, before)
    }

    private func key(_ code: UInt16, _ text: String, flags: NSEvent.ModifierFlags, panel: NSWindow) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0,
            windowNumber: panel.windowNumber, context: nil, characters: text, charactersIgnoringModifiers: text,
            isARepeat: false, keyCode: code))
    }
}
