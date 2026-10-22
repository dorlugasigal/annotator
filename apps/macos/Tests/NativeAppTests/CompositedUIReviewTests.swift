import AppKit
import ScreenCaptureKit
import SwiftUI
import XCTest
import AnnotationCore
import NativePlatform
import PresentationRendering
import ServiceManagement
@testable import AnnotatorApp

@MainActor
final class CompositedUIReviewTests: XCTestCase {
    func testSettingsPropertiesAndLibraryAsActuallyCompositedWindows() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1",
                          "Native window-compositor review is opt-in.")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked, "Unlock the Mac for composited UI review.")
        let suite = "composited-ui-review-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        for dark in [false, true] {
            for tab in CustomizationTab.allCases {
                try await capture(CustomizationView(model: model, showDone: false, initialTab: tab),
                                  name: "settings-\(tab.rawValue)", dark: dark)
            }
            for tool in [Tool.rectangle, .diamond, .arrow, .pen, .highlighter, .text, .blur] {
                model.tool = tool
                model.applyStyle { $0 = .defaults(for: tool) }
                try await capture(ContextualInspector(model: model).padding(16).frame(width: 648),
                                  name: "properties-\(tool.rawValue)", dark: dark)
            }
            for section in ToolLibrarySection.allCases {
                model.tool = section.tools.first ?? .pen
                try await capture(ToolLibraryPanel(model: model, section: .constant(section), dismiss: {}),
                                  name: "library-\(section.rawValue.lowercased())", dark: dark)
            }
            model.setToolbarOrientation(.vertical)
            model.tool = .blur
            model.reportCaptureFailure(CaptureFailure.streamStopped("Synthetic review fixture"))
            try await capture(ToolbarView(model: model, availableSize: CGSize(width: 880, height: 620)),
                              name: "vertical-recovery", dark: dark)
            model.noteCaptureFrame()
            model.setToolbarOrientation(.horizontal)
        }
    }

    func testAdaptivePropertiesAndCompactZoomAsCompositedWindows() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        let suite = "adaptive-composited-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.tool = .diamond
        for dark in [false, true] {
            try await capture(ContextualInspector(model: model).padding(16).frame(width: 932),
                              name: "diamond-single-row", dark: dark)
            try await capture(LiveZoomControls(model: model, compact: true).padding(12),
                              name: "zoom-scroll-readable", dark: dark)
        }
    }

    func testDesktopOnlyToolLibraryCapture() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        let suite = "desktop-only-library-capture-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        try await capture(ToolLibraryPanel(model: model, section: .constant(.shapes), dismiss: {}),
                          name: "desktop-only-library", dark: false)
    }

    func testFeedbackInspectorCaptures() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        let suite = "feedback-inspector-capture-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        let model = WorkspaceModel(defaults:defaults)
        defer { model.closeAll();defaults.removePersistentDomain(forName:suite) }
        for dark in [false,true] {
            for tool in [Tool.pen,.arrow,.text] {
                model.choose(tool)
                model.active?.panel.ignoresMouseEvents = true
                try await capture(ContextualInspector(model:model).padding(16).frame(width:932),
                                  name:"feedback-\(tool.rawValue)",dark:dark)
            }
            try await capture(ArrowHeadPalette(model:model,start:false,dismiss:{}),
                              name:"feedback-arrowhead-picker",dark:dark)
        }
    }

    func testSystemShapeLibraryScrollCaptures() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        let suite = "system-library-review-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        for dark in [false, true] {
            for bottom in [false, true] {
                try await capture(ToolLibraryPanel(model: model, section: .constant(.shapes), dismiss: {}, maximumHeight: 310),
                                  name: bottom ? "system-library-bottom" : "system-library-top", dark: dark,
                                  scrollToBottom: bottom)
            }
        }
    }

    func testExpandedSystemLibraryCapture() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        let suite = "expanded-library-review-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        let model = WorkspaceModel(defaults:defaults)
        defer { model.closeAll();defaults.removePersistentDomain(forName:suite) }
        try await capture(ToolLibraryPanel(model:model,section:.constant(.shapes),dismiss:{},
                                          maximumHeight:570,width:780),
                          name:"system-library-expanded",dark:false)
    }

    func testLaunchAtLoginSettingsCapture() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        let suite = "login-settings-review-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.loginItemController = LoginItemController(
            service: LoginItemService(status: { .requiresApproval },
                                      register: { XCTFail("Read-only UI capture must not register login items.") },
                                      unregister: { XCTFail("Read-only UI capture must not unregister login items.") },
                                      openSettings: { XCTFail("Read-only UI capture must not open system settings.") }),
            report: { _ in XCTFail("Unexpected error") })
        for dark in [false, true] {
            try await capture(CustomizationView(model: model, showDone: false, initialTab: .general),
                              name: "launch-at-login-settings", dark: dark)
        }
    }

    private func capture<V: View>(_ content: V, name: String, dark: Bool, scrollToBottom: Bool = false) async throws {
        let appearance = try XCTUnwrap(NSAppearance(named: dark ? .darkAqua : .aqua))
        var resolvedBackground: NSColor?
        appearance.performAsCurrentDrawingAppearance {
            resolvedBackground = NSColor.windowBackgroundColor.usingColorSpace(.sRGB)
        }
        let background = try XCTUnwrap(resolvedBackground)
        let host = NSHostingController(rootView: content
            .environment(\.colorScheme, dark ? .dark : .light)
            .background(Color(nsColor: background)))
        host.view.appearance = appearance
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 680, height: 640),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.title = "Annotator Composited Review \(name)"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.sharingType = .readOnly
        panel.isOpaque = true
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.backgroundColor = background
        panel.appearance = appearance
        panel.level = .floating
        panel.contentViewController = host
        defer { panel.orderOut(nil); panel.contentViewController = nil; panel.close() }
        for _ in 0..<3 {
            let size = host.view.fittingSize
            panel.setContentSize(size)
            host.view.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(30))
        }
        panel.orderFrontRegardless()
        try await Task.sleep(for: .milliseconds(120))
        if scrollToBottom {
            let scroll = try XCTUnwrap(ScrollableShapeLibraryTests.scrollView(in: host.view))
            ScrollableShapeLibraryTests.scrollToBottom(scroll)
            try await Task.sleep(for: .milliseconds(150))
        }
        let contentList = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let window = try XCTUnwrap(contentList.windows.first { $0.windowID == CGWindowID(panel.windowNumber) })
        let configuration = SCStreamConfiguration()
        configuration.width = Int(panel.frame.width * 2)
        configuration.height = Int(panel.frame.height * 2)
        configuration.showsCursor = false
        let image = try await SCScreenshotManager.captureImage(
            contentFilter: SCContentFilter(desktopIndependentWindow: window), configuration: configuration)
        let bitmap = NSBitmapImageRep(cgImage: image)
        XCTAssertGreaterThan(try XCTUnwrap(bitmap.colorAt(x: 8, y: 8)).alphaComponent, 0.99,
                             "Review images must include the native background, not transparent cacheDisplay fragments.")
        let directory = Bundle(for: Self.self).bundleURL
            .deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("UIReview/Composited")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: directory.appendingPathComponent("\(name)-\(dark ? "dark" : "light").png"), options: .atomic)
    }
}
