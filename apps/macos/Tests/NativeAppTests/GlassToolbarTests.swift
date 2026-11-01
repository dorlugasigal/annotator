import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

/// Glass toolbar (WIN-UI-09): the system's glass behind the real toolbar while the setting is on, the solid window color
/// while it is off or Reduce transparency is on, and solid previews in Settings.
@MainActor
final class GlassToolbarTests: XCTestCase {
    private func makeModel(_ name: String) throws -> (WorkspaceModel, () -> Void) {
        let suite = "glass-toolbar-\(name)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        return (model, { model.closeAll(); defaults.removePersistentDomain(forName: suite) })
    }

    private static func chromeViews(in view: NSView) -> [ToolbarChromeView] {
        view.subviews.flatMap { subview in
            (subview as? ToolbarChromeView).map { [$0] } ?? chromeViews(in: subview)
        }
    }

    func testBackgroundFollowsTheSettingAndReduceTransparency() {
        let chrome = ToolbarChromeView(cornerRadius: 15, glass: false)
        chrome.reducesTransparency = { false }
        chrome.frame = CGRect(x: 0, y: 0, width: 300, height: 60)
        XCTAssertFalse(chrome.showsGlass, "Off at first: the solid window color")
        chrome.glass = true
        XCTAssertTrue(chrome.showsGlass)
        var reduced = true
        chrome.reducesTransparency = { reduced }
        XCTAssertFalse(chrome.showsGlass, "Reduce transparency keeps the toolbar solid")
        reduced = false
        NSWorkspace.shared.notificationCenter.post(name: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
                                                   object: NSWorkspace.shared)
        XCTAssertTrue(chrome.showsGlass, "The glass comes back as soon as Reduce transparency is off")
        XCTAssertFalse(chrome.isAccessibilityElement())
        XCTAssertEqual(chrome.accessibilityChildren()?.count ?? 0, 0, "Screen readers don't see the glass")
        chrome.glass = false
        XCTAssertFalse(chrome.showsGlass)
        XCTAssertTrue(chrome.subviews.isEmpty)
    }

    func testGlassFollowsTheToolbarShape() throws {
        let chrome = ToolbarChromeView(cornerRadius: 15, glass: true)
        chrome.reducesTransparency = { false }
        chrome.frame = CGRect(x: 0, y: 0, width: 112, height: 44)
        let backdrop = try XCTUnwrap(chrome.subviews.first)
        XCTAssertEqual(backdrop.frame, chrome.bounds)
        if #available(macOS 26, *) {
            let glass = try XCTUnwrap(backdrop as? NSGlassEffectView, "Liquid Glass on macOS 26 and later")
            XCTAssertEqual(glass.cornerRadius, 15)
            chrome.cornerRadius = .greatestFiniteMagnitude
            XCTAssertEqual(glass.cornerRadius, 22, "The one-button toolbar is a capsule")
        } else {
            let blur = try XCTUnwrap(backdrop as? NSVisualEffectView, "A behind-window blur on macOS 14 and 15")
            XCTAssertEqual(blur.blendingMode, .behindWindow)
            XCTAssertEqual(blur.state, .active, "It stays clear while another app is in front")
            XCTAssertNotNil(blur.maskImage)
        }
    }

    func testRealToolbarUsesGlassOnlyWhenTurnedOn() async throws {
        let (model, cleanup) = try makeModel("real")
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(300))
        let content = try XCTUnwrap(app.toolbarWindow?.contentView as? ToolbarContentView)
        content.chrome.reducesTransparency = { false }
        XCTAssertFalse(content.chrome.showsGlass, "Glass toolbar is off at first")
        model.setGlass(true)
        XCTAssertTrue(content.chrome.showsGlass)
        XCTAssertEqual(content.chrome.subviews.first?.frame, content.chrome.bounds, "The glass fills the toolbar")

        model.setToolbarOrientation(.vertical)
        model.setPropertiesShown(true)
        try await Task.sleep(for: .milliseconds(300))
        let surfaces = Self.chromeViews(in: content.host)
        XCTAssertGreaterThanOrEqual(surfaces.count, 2, "The tool rail and the properties have their own surfaces")
        XCTAssertTrue(surfaces.allSatisfy(\.glass))

        model.setGlass(false)
        XCTAssertFalse(content.chrome.showsGlass)
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertTrue(Self.chromeViews(in: content.host).isEmpty, "Without glass, the vertical surfaces are solid again")
    }

    func testSettingsPreviewsStaySolid() throws {
        let (model, cleanup) = try makeModel("preview")
        defer { cleanup() }
        model.setToolbarOrientation(.vertical)
        model.setPropertiesShown(true)
        model.setGlass(true)
        func surfaces(drawsChrome: Bool) -> [ToolbarChromeView] {
            let panel = NSPanel(contentRect: CGRect(x: -10_000, y: -10_000, width: 600, height: 900),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            let host = NSHostingView(rootView: ToolbarView(model: model, availableSize: CGSize(width: 600, height: 900),
                                                           drawsChrome: drawsChrome))
            panel.contentView = host
            defer { panel.contentView = nil; panel.close() }
            host.layoutSubtreeIfNeeded()
            host.displayIfNeeded()
            return Self.chromeViews(in: host)
        }
        XCTAssertFalse(surfaces(drawsChrome: false).isEmpty, "The toolbar window's surfaces use glass")
        XCTAssertTrue(surfaces(drawsChrome: true).isEmpty, "Previews in Settings keep the solid look")
    }
}
