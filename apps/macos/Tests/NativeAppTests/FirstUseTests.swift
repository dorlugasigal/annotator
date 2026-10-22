import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class FirstUseTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!
    private var model: WorkspaceModel!
    private var introduction: IntroductionController!

    override func setUp() async throws {
        suite = "first-use-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        model = WorkspaceModel(defaults: defaults, zoomInputPermissions: ZoomInputPermissions(
            isGranted: { false }, request: { XCTFail("Introduction must not request Accessibility."); return false }))
        introduction = IntroductionController(model: model)
    }

    override func tearDown() async throws {
        introduction.close()
        introduction = nil
        model.closeAll()
        model = nil
        defaults.removePersistentDomain(forName: suite)
    }

    func testOrdinaryLaunchShowsWithoutTakingKeyFocusOrChangingMode() throws {
        let keyWindow = NSApp.keyWindow
        introduction.showAutomatically(isLoginItem: false)
        XCTAssertTrue(try XCTUnwrap(introduction.window).isVisible)
        XCTAssertFalse(try XCTUnwrap(introduction.window).isKeyWindow)
        XCTAssertTrue(NSApp.keyWindow === keyWindow)
        XCTAssertEqual(model.mode, .off)
        XCTAssertEqual(model.zoomStartState, .idle)
        XCTAssertTrue(model.desktopSessions.isEmpty)
        introduction.close()
        introduction.showAutomatically(isLoginItem: false)
        XCTAssertFalse(try XCTUnwrap(introduction.window).isVisible)
    }

    func testSuppressionPersistsButManualReopeningStillWorks() throws {
        introduction.preferences.setSuppressed(true)
        introduction = IntroductionController(model: model)
        XCTAssertTrue(introduction.preferences.suppressed)
        introduction.showAutomatically(isLoginItem: false)
        XCTAssertNil(introduction.window)
        introduction.showManually()
        XCTAssertTrue(try XCTUnwrap(introduction.window).isVisible)
        introduction.close()
        XCTAssertTrue(introduction.preferences.suppressed)
        introduction.preferences.setSuppressed(false)
        XCTAssertFalse(IntroductionPreferences(defaults: defaults).suppressed)
    }

    func testLoginLaunchDoesNotShowOrPersistSuppression() {
        introduction.showAutomatically(isLoginItem: true)
        XCTAssertNil(introduction.window)
        XCTAssertFalse(introduction.preferences.suppressed)
    }

    func testBusyStatesPreventPresentationWithoutChangingInteraction() {
        model.visibilityAnimating = true
        XCTAssertFalse(introduction.isEligible)
        introduction.showManually()
        XCTAssertNil(introduction.window)
        XCTAssertTrue(model.visibilityAnimating)
        model.visibilityAnimating = false
        model.errorMessage = "Test error"
        XCTAssertFalse(introduction.isEligible)
        model.errorMessage = nil
        model.reportCaptureFailure(NSError(domain: "FirstUseTests", code: 1))
        XCTAssertFalse(introduction.isEligible)
        model.noteCaptureFrame()
        model.beginDrawing()
        model.active?.panel.ignoresMouseEvents = true
        XCTAssertFalse(introduction.isEligible)
        introduction.showManually()
        XCTAssertNil(introduction.window)
        XCTAssertEqual(model.mode, .draw)
    }

    func testStartingDrawingClosesIntroductionAndKeepsDrawingActive() async throws {
        introduction.showAutomatically(isLoginItem: false)
        model.beginDrawing()
        model.active?.panel.ignoresMouseEvents = true
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertFalse(try XCTUnwrap(introduction.window).isVisible)
        XCTAssertFalse(model.introductionVisible)
        XCTAssertEqual(model.mode, .draw)
    }

    func testIntroductionDoesNotOverlapSupportReminder() {
        model.beginDrawing()
        model.interact()
        XCTAssertTrue(model.canShowSupportReminder)
        introduction.showManually()
        XCTAssertFalse(model.canShowSupportReminder)
        introduction.close()
        XCTAssertTrue(model.canShowSupportReminder)
    }

    func testSettingsIncludesHelpAboutAndProminentSupportControls() async throws {
        let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false))
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 840, height: 700),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        for (tab, expected) in [
            (CustomizationTab.general, ["Buy me a coffee", "Check for updates...", "Show introduction", "Startup", "Appearance"]),
            (.help, ["Quick start", "Screen-sharing limits", "Report a problem...", "Use an AI assistant"]),
            (.about, ["Version", "Check for updates...", "Copy support info", "Open Downloads page..."])
        ] {
            model.settingsTab = tab
            try await Task.sleep(for: .milliseconds(100))
            let text = NativeAccessibilityNode.nodes(in: host).flatMap {
                [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
            }.joined(separator: "\n")
            for label in expected { XCTAssertTrue(text.contains(label), "Missing \(label) in \(tab)") }
            XCTAssertFalse(NativeAccessibilityNode.nodes(in: host).contains {
                $0.accessibilityRole() == .checkBox && $0.accessibilityLabel()?.contains("Coffee") == true
            })
            if tab == .help {
                try await expand("help-ai", in: host)
                let expanded = accessibilityText(in: host)
                XCTAssertTrue(expanded.contains("model provider"))
                XCTAssertTrue(expanded.contains("Save AI skill..."))
                try await expand("help-sharing", in: host)
                XCTAssertTrue(accessibilityText(in: host).contains("Open Permissions"))
            }
            if tab == .about {
                let icon = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                    $0.accessibilityIdentifier() == "about-app-icon"
                })
                XCTAssertGreaterThanOrEqual(icon.accessibilityFrame().width, 90)
                XCTAssertTrue(text.contains("Local build"))
            }
        }
    }

    func testHelpAboutAndUpdateRoutesUseSettingsAndExistingCallbacks() {
        let app = AppDelegate(model: model)
        defer { app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification)) }
        var opened: [CustomizationTab] = []
        model.onOpenPreferences = { opened.append(self.model.settingsTab) }
        app.performMenuAction(.help)
        app.performMenuAction(.about)
        app.performMenuAction(.settings)
        XCTAssertEqual(opened, [.help, .about, .general])
        XCTAssertFalse(model.showHelp)
        var checks = 0
        model.onCheckForUpdates = { checks += 1 }
        model.canCheckForUpdates = false
        app.performMenuAction(.checkForUpdates)
        XCTAssertEqual(checks, 0)
        XCTAssertEqual(model.status, model.updateStatus)
        model.canCheckForUpdates = true
        app.performMenuAction(.checkForUpdates)
        XCTAssertEqual(checks, 1)
        app.introduction?.preferences.setSuppressed(true)
        model.onShowIntroduction?()
        XCTAssertTrue(app.introduction?.window?.isVisible == true)
    }

    func testSupportRouteUsesConfiguredURLAndKeepsReminderPreference() throws {
        let app = AppDelegate(model: model)
        defer { app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification)) }
        model.supportReminder.setEnabled(false)
        var urls: [URL] = []
        model.openExternalURL = { urls.append($0); return true }
        app.performMenuAction(.support)
        XCTAssertEqual(urls, [try XCTUnwrap(model.optionalSupportURL)])
        XCTAssertEqual(urls.first?.absoluteString, "https://buymeacoffee.com/dorlugasigal")
        XCTAssertFalse(model.supportReminder.enabled)
        XCTAssertNotNil(defaults.object(forKey: SupportReminder.lastShownKey))
    }

    func testSupportFailureIsReportedWithoutSnoozing() {
        model.openExternalURL = { _ in false }
        XCTAssertFalse(model.openOptionalSupport())
        XCTAssertNotNil(model.errorMessage)
        XCTAssertNil(defaults.object(forKey: SupportReminder.lastShownKey))
    }

    func testIntroductionUsesConfiguredShortcuts() async throws {
        model.setActivationShortcut(ActivationShortcut(keyCode: 20, character: "3", control: true, option: true))
        introduction.showManually()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let host = try XCTUnwrap(introduction.window?.contentView)
        let labels = NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
        XCTAssertTrue(labels.contains("Control + Option + 3"))
        XCTAssertTrue(labels.contains("Control + Shift + 2"))
        XCTAssertTrue(labels.contains("Escape"))
        XCTAssertTrue(labels.contains("Show on launch"))
        XCTAssertTrue(labels.contains("Covers hide too"))
        XCTAssertFalse(labels.contains("development preview"))
        let visibleText = Set(NativeAccessibilityNode.nodes(in: host).filter {
            $0.accessibilityRole() == .staticText
        }.compactMap { $0.accessibilityValue() as? String ?? $0.accessibilityLabel() })
        XCTAssertLessThanOrEqual(visibleText.joined(separator: " ").split(whereSeparator: \.isWhitespace).count, 120)
        try await expand("introduction-sharing", in: host)
        XCTAssertTrue(accessibilityText(in: host).contains("App or window sharing may leave out covers"))
        let close = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Got it"
        })
        XCTAssertTrue(close.accessibilityPerformPress())
        XCTAssertFalse(try XCTUnwrap(introduction.window).isVisible)
        XCTAssertFalse(introduction.preferences.suppressed, "Closing must not change launch suppression.")
    }

    func testTourPagesRemainSkippableWithoutChangingDrawingOrRequestingPermissions() async throws {
        introduction.showManually()
        NativeAccessibilityNode.enable()
        let host = try XCTUnwrap(introduction.window?.contentView)
        for (index, title) in ["Make a point", "Make it yours", "Lead the way", "A little setup"].enumerated() {
            try await Task.sleep(for: .milliseconds(80))
            let step = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityRole() == .button && $0.accessibilityLabel() == title
            })
            XCTAssertTrue(step.accessibilityPerformPress())
            try await Task.sleep(for: .milliseconds(100))
            XCTAssertEqual(model.mode, .off)
            XCTAssertTrue(model.desktopSessions.isEmpty)
            XCTAssertEqual(model.zoomStartState, .idle)
            let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
                .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: output.appendingPathComponent("tour-\(index + 1).png"))
        }
        let text = accessibilityText(in: host)
        XCTAssertTrue(text.contains("optional"))
        XCTAssertTrue(text.contains("Screen Recording"))
        XCTAssertTrue(text.contains("Accessibility"))
        let close = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Got it"
        })
        XCTAssertTrue(close.accessibilityPerformPress())
        XCTAssertFalse(try XCTUnwrap(introduction.window).isVisible)
    }

    func testProductHelpDestinationsAndBrowserFailuresAreExplicit() {
        var opened: [String] = []
        model.openExternalURL = { opened.append($0.absoluteString); return true }
        for page in ProductPage.allCases { model.openProductPage(page) }
        XCTAssertEqual(opened, [
            "https://dorlugasigal.github.io/annotator/docs/",
            "https://dorlugasigal.github.io/annotator/downloads/",
            "https://dorlugasigal.github.io/annotator/ai/",
            "https://dorlugasigal.github.io/annotator/privacy/"
        ])
        XCTAssertNil(model.errorMessage)
        model.openExternalURL = { _ in false }
        model.openProductPage(.help)
        XCTAssertTrue(model.errorMessage?.contains("The page could not open") == true)
    }

    func testPermissionRecoveryUsesConfiguredZoomShortcut() async throws {
        let shortcut = ActivationShortcut(keyCode: 21, character: "4", control: true, option: true)
        model.setZoomShortcut(shortcut)
        let host = NSHostingView(rootView: CustomizationView(model: model, initialTab: .permissions))
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 840, height: 700),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        try await expand("permission-recovery", in: host)
        let text = NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
        XCTAssertTrue(text.contains("Control + Option + 4"))
        XCTAssertTrue(text.contains("quitting removes covers"))
        XCTAssertTrue(text.contains("No video is recorded"))
        XCTAssertTrue(text.contains("Zoom buttons don't need this"))
        XCTAssertFalse(text.contains("then press Control-1"))
    }

    func testSettingsWindowCanBeResizedButNotBelowItsLayout() async throws {
        let window = NSWindow(contentRect: NSRect(x: 100, y: 100, width: 840, height: 700),
                              styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: CustomizationView(model: model, showDone: false)
            .background(SettingsWindowSurface()))
        window.orderFrontRegardless()
        defer { window.orderOut(nil); window.contentView = nil; window.close() }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(window.styleMask.contains(.resizable))
        XCTAssertEqual(window.contentMinSize, SettingsSurfaceView.minimumContentSize)
        window.setContentSize(NSSize(width: 1180, height: 900))
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(window.contentLayoutRect.width, 1180, accuracy: 1)
        XCTAssertEqual(window.contentLayoutRect.height, 900, accuracy: 1)
    }

    func testShortcutsKeepKeycapsAndRoomForToolKeys() async throws {
        let host = NSHostingView(rootView: CustomizationView(model: model, initialTab: .toolbar))
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 840, height: 700),
                            styleMask: [.titled, .resizable, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let text = accessibilityText(in: host)
        XCTAssertTrue(text.contains("Control + Shift + 2"))
        XCTAssertTrue(text.contains("Draw shortcut"))
        XCTAssertTrue(text.contains("Pen key"))
        XCTAssertFalse(text.contains("Shortcut symbols:"))
        let tools = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityIdentifier() == "tool-shortcuts-list"
        })
        XCTAssertGreaterThanOrEqual(tools.accessibilityFrame().height, 200)
        XCTAssertGreaterThanOrEqual(host.fittingSize.width, 720)
    }

    func testUsageConsentIsOffAndAppearsInBothFirstUseAndGeneral() async throws {
        XCTAssertFalse(model.usageMetrics.enabled)
        introduction.showManually()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let welcome = try XCTUnwrap(introduction.window?.contentView)
        let consent = try XCTUnwrap(NativeAccessibilityNode.nodes(in: welcome).first {
            $0.accessibilityRole() == .checkBox && $0.accessibilityLabel() == "Share usage counts"
        })
        XCTAssertTrue(consent.accessibilityPerformPress())
        XCTAssertTrue(model.usageMetrics.enabled)
        XCTAssertNotNil(defaults.string(forKey: UsageMetrics.installationKey))
        model.usageMetrics.setEnabled(false)
        XCTAssertNil(defaults.string(forKey: UsageMetrics.installationKey))
        introduction.close()
        let host = NSHostingView(rootView: CustomizationView(model: model, initialTab: .general))
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 840, height: 700),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(accessibilityText(in: host).contains("Share usage counts"))
        XCTAssertFalse(model.usageMetrics.enabled)
    }

    private func accessibilityText(in host: NSView) -> String {
        NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
    }

    private func expand(_ id: String, in host: NSView) async throws {
        let deadline = Date().addingTimeInterval(2)
        var group: NativeAccessibilityNode?
        while group == nil && Date() < deadline {
            host.layoutSubtreeIfNeeded()
            group = NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == id }
            if group == nil { try await Task.sleep(for: .milliseconds(10)) }
        }
        let target = try XCTUnwrap(group)
        let control = try XCTUnwrap(NativeAccessibilityNode.nodes(in: target.object).first {
            $0.accessibilityRole() == .disclosureTriangle || $0.accessibilityRole() == .button
        })
        XCTAssertTrue(control.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(80))
    }

    func testBuiltIconContainsCatalogAndICNSResource() throws {
        XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: "CFBundleIconName") as? String, "AppIcon")
        let iconName = try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleIconFile") as? String)
        let stem = (iconName as NSString).deletingPathExtension
        let iconURL = try XCTUnwrap(Bundle.main.url(forResource: stem, withExtension: "icns"))
        let icon = try XCTUnwrap(NSImage(contentsOf: iconURL))
        XCTAssertGreaterThanOrEqual(icon.representations.map(\.pixelsWide).max() ?? 0, 256)
        let catalogIcon = try XCTUnwrap(NSImage(named: "AppIcon"))
        let large = try XCTUnwrap(catalogIcon.bestRepresentation(
            for: NSRect(x: 0, y: 0, width: 1024, height: 1024), context: nil, hints: nil))
        XCTAssertGreaterThanOrEqual(large.pixelsWide, 1024)
        XCTAssertNotNil(Bundle.main.url(forResource: "Assets", withExtension: "car"))
        let root = RepositoryRoot.url
        let catalog = root.appendingPathComponent("apps/macos/Annotator/Assets.xcassets/AppIcon.appiconset")
        for size in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let data = try Data(contentsOf: catalog.appendingPathComponent("icon_\(size)x\(size)@\(scale)x.png"))
                let bitmap = try XCTUnwrap(NSBitmapImageRep(data: data))
                XCTAssertEqual(bitmap.pixelsWide, size * scale)
                XCTAssertEqual(bitmap.pixelsHigh, size * scale)
            }
        }
    }
}
