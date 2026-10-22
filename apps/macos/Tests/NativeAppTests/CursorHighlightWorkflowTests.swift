import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class CursorHighlightWorkflowTests: XCTestCase {
    func testPointerOverlayDoesNotAddAWindowShadowAroundHaloPixels() throws {
        let suite = "halo-shadow-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        var settings = model.cursorHighlightSettings
        settings.shape = .diamond
        settings.glowRadius = 20
        model.setCursorHighlight(settings)
        model.cursorHalo = true
        let session = try XCTUnwrap(model.active)
        let overlay = try XCTUnwrap(NSApp.windows.first {
            guard let view = $0.contentView as? SelectionOverlayView else { return false }
            return view.session === session
        })
        XCTAssertTrue(overlay.isVisible)
        XCTAssertFalse(overlay.isOpaque)
        XCTAssertTrue(overlay.ignoresMouseEvents)
        XCTAssertFalse(overlay.hasShadow, "The window server must not outline the halo and glow with a black shadow.")
        settings.glowRadius = 0
        model.setCursorHighlight(settings)
        XCTAssertFalse(overlay.hasShadow)
    }

    func testClickAnimationExpiresAndStopsWithoutAnIdleLoop() async throws {
        let animation = CursorClickAnimation()
        var updates = 0
        animation.start(animated: true) { updates += 1 }
        XCTAssertTrue(animation.isRunning)
        try await Task.sleep(for: .milliseconds(120))
        XCTAssertGreaterThan(updates, 1)
        XCTAssertNotNil(animation.progress)
        try await Task.sleep(for: .milliseconds(450))
        XCTAssertFalse(animation.isRunning)
        XCTAssertNil(animation.progress)
        let finished = updates
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(updates, finished)
        animation.start(animated: false) { updates += 1 }
        let started = updates
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(updates, started)
        animation.stop()
        try await Task.sleep(for: .milliseconds(450))
        XCTAssertEqual(updates, started)
    }

    func testClickFramesFollowTheDisplayAndStopAfterOneClick() async throws {
        let animation = CursorClickAnimation()
        XCTAssertFalse(animation.isRunning, "An idle highlight must not own a display link.")
        var updates = 0
        let began = ProcessInfo.processInfo.systemUptime
        var lastUpdate = began
        animation.start(animated: true) { updates += 1; lastUpdate = ProcessInfo.processInfo.systemUptime }
        XCTAssertTrue(animation.isRunning)
        while animation.isRunning && ProcessInfo.processInfo.systemUptime - began < 2 {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertFalse(animation.isRunning)
        XCTAssertNil(animation.progress)
        XCTAssertGreaterThan(updates, 5)
        XCTAssertGreaterThanOrEqual(lastUpdate - began, CursorHighlightSettings.clickDuration - 0.02)
        XCTAssertLessThan(lastUpdate - began, CursorHighlightSettings.clickDuration + 0.25)
        let finished = updates
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(updates, finished)

        animation.start(animated: true) { updates += 1 }
        XCTAssertTrue(animation.isRunning)
        animation.stop()
        XCTAssertFalse(animation.isRunning)
        XCTAssertNil(animation.startedAt)
        let stopped = updates
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(updates, stopped, "Stopping a click must cancel its display link.")
    }

    func testPreviewClicksAnimateWithoutEnablingDesktopEffectsAndColorStaysInline() async throws {
        let suite = "halo-preview-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        var settings = model.cursorHighlightSettings
        settings.shape = .diamond; settings.glowRadius = 10; settings.clickEffect = .ripple
        model.setCursorHighlight(settings)
        let host = NSHostingView(rootView: CursorHighlightControls(model: model).frame(width: 780, height: 650))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 780, height: 650),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host; panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(150))
        let preview = try XCTUnwrap(findPreview(host))
        XCTAssertFalse(preview.animation.isRunning)
        XCTAssertTrue(preview.accessibilityPerformPress())
        XCTAssertTrue(preview.animation.isRunning)
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertNotNil(preview.animation.progress)
        XCTAssertFalse(model.cursorHalo)
        XCTAssertTrue(model.desktopSessions.isEmpty)
        try await Task.sleep(for: .milliseconds(450))
        XCTAssertFalse(preview.animation.isRunning)
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        for effect in [CursorHighlightSettings.ClickEffect.pulse, .ripple, .turn] {
            settings.clickEffect = effect
            preview.update(settings)
            preview.previewClick()
            try await Task.sleep(for: .milliseconds(100))
            let bitmap = try XCTUnwrap(preview.bitmapImageRepForCachingDisplay(in: preview.bounds))
            preview.cacheDisplay(in: preview.bounds, to: bitmap)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: output.appendingPathComponent("halo-\(effect.rawValue).png"))
            try await Task.sleep(for: .milliseconds(400))
        }
        let colors = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityLabel() == "Custom color"
        })
        XCTAssertTrue(colors.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(NativeAccessibilityNode.nodes(in: host).contains { $0.accessibilityLabel() == "Color brightness" })
        XCTAssertFalse(NSApp.windows.contains { $0 is NSColorPanel && $0.isVisible })
        preview.previewClick()
        preview.isHidden = true
        XCTAssertFalse(preview.animation.isRunning)
        preview.previewClick()
        XCTAssertFalse(preview.animation.isRunning)
    }

    func testDisablingTheRealHaloStopsClickFrames() async throws {
        let suite = "halo-frames-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.cursorHalo = true
        model.notePointerEvent(.leftMouseDown)
        XCTAssertTrue(model.pointerClickAnimation.isRunning)
        model.cursorHalo = false
        XCTAssertFalse(model.pointerClickAnimation.isRunning)
        model.cursorHalo = true
        model.notePointerEvent(.rightMouseDown)
        model.concealWorkspaceSurfaces()
        XCTAssertFalse(model.pointerClickAnimation.isRunning)
    }

    private func findPreview(_ view: NSView) -> CursorPreviewCanvas? {
        if let preview = view as? CursorPreviewCanvas { return preview }
        return view.subviews.lazy.compactMap { self.findPreview($0) }.first
    }
}
