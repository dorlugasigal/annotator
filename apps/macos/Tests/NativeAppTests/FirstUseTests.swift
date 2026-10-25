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
    private var tutorial: TutorialController!

    override func setUp() async throws {
        suite = "first-use-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        model = WorkspaceModel(defaults: defaults, zoomInputPermissions: ZoomInputPermissions(
            isGranted: { false }, request: { XCTFail("The tutorial must not request Accessibility."); return false }))
        tutorial = TutorialController(model: model, startDrawing: { [weak self] in self?.draw() })
        tutorial.reducesMotion = { true }
    }

    override func tearDown() async throws {
        tutorial.shutdown()
        tutorial = nil
        model.closeAll()
        model = nil
        defaults.removePersistentDomain(forName: suite)
    }

    func testOrdinaryLaunchShowsWithoutTakingKeyFocusOrChangingMode() throws {
        let keyWindow = NSApp.keyWindow
        tutorial.showAutomatically(isLoginItem: false)
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible)
        XCTAssertFalse(try XCTUnwrap(tutorial.window).isKeyWindow)
        XCTAssertTrue(NSApp.keyWindow === keyWindow)
        XCTAssertEqual(tutorial.surface, .welcome)
        XCTAssertTrue(model.introductionVisible)
        XCTAssertEqual(model.mode, .off)
        XCTAssertEqual(model.zoomStartState, .idle)
        XCTAssertTrue(model.desktopSessions.isEmpty)
        tutorial.close()
        XCTAssertFalse(model.introductionVisible)
        tutorial.showAutomatically(isLoginItem: false)
        XCTAssertFalse(try XCTUnwrap(tutorial.window).isVisible, "The welcome card shows once per launch.")
    }

    func testSuppressionPersistsButManualReopeningStillWorks() throws {
        tutorial.preferences.setSuppressed(true)
        XCTAssertTrue(defaults.bool(forKey: "introduction-suppressed"), "Show on launch keeps the old tour's key.")
        tutorial.shutdown()
        tutorial = TutorialController(model: model)
        XCTAssertTrue(tutorial.preferences.suppressed)
        tutorial.showAutomatically(isLoginItem: false)
        XCTAssertNil(tutorial.window)
        tutorial.showManually()
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible)
        tutorial.close()
        XCTAssertTrue(tutorial.preferences.suppressed)
        tutorial.preferences.setSuppressed(false)
        XCTAssertFalse(TutorialPreferences(defaults: defaults).suppressed)
    }

    func testFinishedTutorialNoLongerShowsOnLaunchButStaysInSettings() throws {
        XCTAssertFalse(tutorial.preferences.completed)
        tutorial.preferences.setCompleted(true)
        XCTAssertTrue(defaults.bool(forKey: "tutorial-completed"))
        tutorial.shutdown()
        tutorial = TutorialController(model: model)
        tutorial.showAutomatically(isLoginItem: false)
        XCTAssertNil(tutorial.window)
        tutorial.showManually()
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible)
        XCTAssertEqual(tutorial.surface, .welcome)
    }

    func testLoginLaunchDoesNotShowOrPersistSuppression() {
        tutorial.showAutomatically(isLoginItem: true)
        XCTAssertNil(tutorial.window)
        XCTAssertFalse(tutorial.preferences.suppressed)
        XCTAssertFalse(tutorial.preferences.completed)
    }

    func testBusyStatesPreventPresentationWithoutChangingInteraction() {
        model.visibilityAnimating = true
        XCTAssertFalse(tutorial.isEligible)
        tutorial.showManually()
        XCTAssertNil(tutorial.window)
        XCTAssertEqual(model.status, "Finish drawing and stop zoom, then open the tutorial from Settings > Help.")
        XCTAssertTrue(model.visibilityAnimating)
        model.visibilityAnimating = false
        model.errorMessage = "Test error"
        XCTAssertFalse(tutorial.isEligible)
        model.errorMessage = nil
        model.reportCaptureFailure(NSError(domain: "FirstUseTests", code: 1))
        XCTAssertFalse(tutorial.isEligible)
        model.noteCaptureFrame()
        model.beginDrawing()
        model.active?.panel.ignoresMouseEvents = true
        XCTAssertFalse(tutorial.isEligible)
        tutorial.showManually()
        XCTAssertNil(tutorial.window)
        XCTAssertEqual(model.mode, .draw)
    }

    func testStartTutorialTurnsDrawingOnAndTheCoachStaysOpen() async throws {
        tutorial.showAutomatically(isLoginItem: false)
        XCTAssertEqual(model.mode, .off)
        tutorial.start()
        XCTAssertEqual(model.mode, .draw, "Start tutorial turns drawing on, so the first step's key reaches the canvas.")
        try await waitUntil { self.tutorial.surface == .coach }
        XCTAssertEqual(tutorial.shownStep, .rectangle)
        XCTAssertEqual(tutorial.progress?.step, .rectangle)
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible)
        XCTAssertFalse(try XCTUnwrap(tutorial.window).isKeyWindow, "The coach card never takes key focus.")
        XCTAssertTrue(model.introductionVisible)
        model.choose(.rectangle)
        try await waitUntil { self.tutorial.progress?.step == .shape }
        XCTAssertTrue(try XCTUnwrap(tutorial.window).isVisible)
        XCTAssertEqual(model.mode, .draw)
    }

    func testStepWordsUseInlineKeysAndOneLineOrGoalRows() {
        let preferences = model.preferences
        for step in TutorialStep.allCases {
            let text = TutorialText.step(step, preferences: preferences)
            if step.goals.count == 1 {
                XCTAssertNotNil(text.detail, "\(step) has one goal, so it has a line.")
                XCTAssertTrue(text.goals.isEmpty)
            } else {
                XCTAssertNil(text.detail, "\(step) has goal rows instead of a line.")
                XCTAssertEqual(text.goals.count, step.goals.count)
            }
        }
        XCTAssertEqual(TutorialText.step(.rectangle, preferences: preferences).detail, "Press {2}, or click it in the toolbar.")
        XCTAssertEqual(TutorialText.plain("Press {⌘Z}, or click Undo."), "Press ⌘Z, or click Undo.")
        let segments = TutorialText.segments("Press {⌘Z}, or click Undo.")
        XCTAssertEqual(segments.map { $0.text }, ["Press ", "", ", or click Undo."])
        XCTAssertEqual(segments.map { $0.keys }, [[], ["⌘Z"], []])
        let tokens = TutorialText.tokens("Press {2}, or click it.")
        XCTAssertEqual(tokens.map(\.text), ["Press", "2", ",", "or", "click", "it."])
        XCTAssertEqual(tokens.map(\.isKey), [false, true, false, false, false, false])
        XCTAssertEqual(tokens.map(\.spaceBefore), [false, true, false, true, true, true], "The comma stays with its key.")
        XCTAssertEqual(TutorialText.spoken(["⌃⇧2"]), "Control + Shift + 2")
        XCTAssertEqual(TutorialText.spoken(["esc"]), "Escape")
        XCTAssertEqual(TutorialText.keys(for: .drawing, preferences: preferences), ["⌃⇧2"])
        XCTAssertTrue(TutorialText.showsKeyboard(.drawing, preferences: preferences), "A shortcut with a modifier lights the keyboard.")
        XCTAssertTrue(TutorialText.showsKeyboard(.usingApps, preferences: preferences))
        XCTAssertFalse(TutorialText.showsKeyboard(.rectangleTool, preferences: preferences), "A single key shows only in the line.")
        XCTAssertFalse(TutorialText.showsKeyboard(.curvedArrowDrawn, preferences: preferences))
        let rows = TutorialText.finishRows(preferences: preferences)
        XCTAssertEqual(rows.map { $0.label }, ["Draw", "Use apps", "Show or hide", "Undo", "Zoom", "Break timer"])
        XCTAssertEqual(rows[4].keys, [ActivationShortcut.liveZoom.label])
        XCTAssertEqual(rows[5].keys, [ActivationShortcut.breakTimer.label])
        var custom = preferences
        custom.zoomShortcut = ActivationShortcut(keyCode: 21, character: "4", control: true)
        custom.breakShortcut = ActivationShortcut(keyCode: 23, character: "5", control: true)
        let customRows = TutorialText.finishRows(preferences: custom)
        XCTAssertEqual(customRows[4].keys, ["⌃4"], "The finish card follows the person's zoom shortcut.")
        XCTAssertEqual(customRows[5].keys, ["⌃5"], "The finish card follows the person's break shortcut.")
    }

    func testTutorialDoesNotOverlapSupportReminder() {
        model.beginDrawing()
        model.interact()
        XCTAssertTrue(model.canShowSupportReminder)
        tutorial.showManually()
        XCTAssertFalse(model.canShowSupportReminder)
        tutorial.close()
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
            (CustomizationTab.general, ["Buy me a coffee", "Check for updates...", "Show tutorial", "Startup", "Appearance"]),
            (.help, ["Tutorial", "Screen-sharing limits", "Report a problem...", "Use an AI assistant"]),
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
        app.tutorial?.preferences.setSuppressed(true)
        model.onShowIntroduction?()
        XCTAssertTrue(app.tutorial?.window?.isVisible == true)
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

    func testTutorialUsesConfiguredShortcuts() async throws {
        model.setActivationShortcut(ActivationShortcut(keyCode: 20, character: "3", control: true, option: true))
        tutorial.showManually()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let host = try XCTUnwrap(tutorial.window?.contentView)
        var text = accessibilityText(in: host)
        XCTAssertTrue(text.contains("Start tutorial"))
        XCTAssertTrue(text.contains("Show on launch"))
        XCTAssertFalse(text.contains("development preview"))
        tutorial.start()
        try await waitUntil { self.tutorial.surface == .coach }
        try await Task.sleep(for: .milliseconds(150))
        text = accessibilityText(in: host)
        XCTAssertTrue(text.contains("Press 2, or click it in the toolbar."), "Keys read as plain words.")
        XCTAssertFalse(text.contains("Keyboard:"), "A single key shows only in the line.")
        XCTAssertEqual(tutorial.lastAnnouncement, "Step 1 of 10. Grab the Rectangle. Press 2, or click it in the toolbar.")
        for _ in 0..<20 where tutorial.shownStep != .drawAgain { tutorial.skipStep() }
        XCTAssertEqual(tutorial.shownStep, .drawAgain)
        try await Task.sleep(for: .milliseconds(150))
        text = accessibilityText(in: host)
        XCTAssertTrue(text.contains("Press ⌃⇧2 from any app, or click a tool."))
        XCTAssertTrue(text.contains("Keyboard: Control + Shift + 2"), "The Draw shortcut lights the keyboard.")
        tutorial.skipStep()
        XCTAssertEqual(tutorial.shownStep, .hide)
        try await Task.sleep(for: .milliseconds(150))
        text = accessibilityText(in: host)
        XCTAssertTrue(text.contains("Hide it all: press ⌃⌥3"))
        XCTAssertTrue(text.contains("Bring it back: press ⌃⌥3 again"))
        XCTAssertTrue(text.contains("Control + Option + 3"))
        XCTAssertEqual(tutorial.lastAnnouncement,
                       "Step 10 of 10. Now you see it…. Hide it all: press ⌃⌥3. Bring it back: press ⌃⌥3 again")
        tutorial.skipStep()
        try await waitUntil { self.tutorial.surface == .finish }
        try await Task.sleep(for: .milliseconds(150))
        text = accessibilityText(in: host)
        for keys in ["Control + Shift + 2", "Escape", "Control + Option + 3", "Command + Z"] {
            XCTAssertTrue(text.contains(keys), "Missing \(keys) on the finish card.")
        }
        try await expand("tutorial-sharing", in: host)
        XCTAssertTrue(accessibilityText(in: host).contains("App or window sharing may leave out covers"))
        let done = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Done"
        })
        XCTAssertTrue(done.accessibilityPerformPress())
        try await waitUntil { self.tutorial.window?.isVisible != true }
        XCTAssertFalse(tutorial.preferences.suppressed, "Closing must not change Show on launch.")
        XCTAssertTrue(tutorial.preferences.completed)
    }

    func testSkippingEveryStepNeverDrawsOrRequestsPermissions() async throws {
        tutorial.showManually()
        NativeAccessibilityNode.enable()
        let host = try XCTUnwrap(tutorial.window?.contentView)
        try await Task.sleep(for: .milliseconds(100))
        let start = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Start tutorial"
        })
        XCTAssertTrue(start.accessibilityPerformPress())
        try await waitUntil { self.tutorial.surface == .coach }
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        for step in TutorialStep.allCases {
            try await Task.sleep(for: .milliseconds(120))
            XCTAssertEqual(tutorial.shownStep, step)
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: output.appendingPathComponent("tutorial-\(step.index + 1)-\(step.rawValue).png"))
            let skip = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Skip step"
            })
            XCTAssertTrue(skip.accessibilityPerformPress())
            XCTAssertEqual(model.mode, .draw, "Start tutorial turned drawing on; skipping changes nothing.")
            XCTAssertTrue(model.desktopSessions.allSatisfy { $0.document.elements.isEmpty })
            XCTAssertEqual(model.zoomStartState, .idle)
        }
        try await waitUntil { self.tutorial.surface == .finish }
        XCTAssertTrue(tutorial.preferences.completed)
        XCTAssertEqual(tutorial.overlay.celebrations, 0, "Reduce Motion leaves out the confetti.")
        XCTAssertEqual(tutorial.progress?.completed, [TutorialStep.drawAgain],
                       "Start tutorial turned drawing on, so Draw again counts as soon as it shows.")
        XCTAssertEqual(tutorial.progress?.skipped, TutorialStep.allCases.filter { $0 != .drawAgain })
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
        tutorial.showManually()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let welcome = try XCTUnwrap(tutorial.window?.contentView)
        let consent = try XCTUnwrap(NativeAccessibilityNode.nodes(in: welcome).first {
            $0.accessibilityRole() == .checkBox && $0.accessibilityLabel() == "Share usage counts"
        })
        XCTAssertTrue(consent.accessibilityPerformPress())
        XCTAssertTrue(model.usageMetrics.enabled)
        XCTAssertNotNil(defaults.string(forKey: UsageMetrics.installationKey))
        model.usageMetrics.setEnabled(false)
        XCTAssertNil(defaults.string(forKey: UsageMetrics.installationKey))
        tutorial.close()
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

    /// Starts drawing with click-through canvases, so the test never covers the desktop's input.
    private func draw() {
        model.beginDrawing()
        for session in model.desktopSessions { session.panel.ignoresMouseEvents = true }
    }

    private func accessibilityText(in host: NSView) -> String {
        NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
    }

    private func waitUntil(timeout: Duration = .seconds(5), _ condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now + timeout
        while !condition() {
            guard ContinuousClock.now < deadline else { return XCTFail("Timed out waiting for the tutorial.") }
            try await Task.sleep(for: .milliseconds(20))
        }
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
