import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

extension ReleaseNotesCatalog.Entry {
    /// Fixed notes for the What's changed UX reference, so the reference doesn't change with every release.
    static let uxReference = ReleaseNotesCatalog.Entry(
        version: "0.1.0-beta.13", build: 31, date: "2026-10-24",
        changes: [
            "Added": ["After an update, What's changed shows the new version's notes once."],
            "Changed": ["Annotator checks for updates once a day. You can turn this off in Settings."],
            "Fixed": ["The update window waits until you finish drawing or sharing a region."]
        ],
        knownIssues: ["Effects do not follow moving windows and are not secure redaction."])
}

@MainActor
final class WhatsChangedTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!
    private var model: WorkspaceModel!
    private var controller: WhatsChangedController!

    override func setUp() async throws {
        suite = "whats-changed-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        model = WorkspaceModel(defaults: defaults)
        controller = WhatsChangedController(model: model)
    }

    override func tearDown() async throws {
        controller.close()
        controller = nil
        model.closeAll()
        model = nil
        defaults.removePersistentDomain(forName: suite)
    }

    private func notes(_ build: Int) -> () throws -> ReleaseNotesCatalog.Entry {
        { ReleaseNotesCatalog.Entry(version: "0.1.0-beta.\(build)", build: build, date: "2026-10-24",
                                    changes: ["Fixed": ["A fix."]], knownIssues: []) }
    }

    func testFirstInstallRecordsTheBuildWithoutShowing() {
        let tracker = WhatsChangedTracker(defaults: defaults)
        XCTAssertNil(tracker.due(build: 30, notes: notes(30)))
        XCTAssertEqual(defaults.integer(forKey: WhatsChangedTracker.lastBuildKey), 30)
        XCTAssertEqual(tracker.due(build: 31, notes: notes(31))?.build, 31)
    }

    func testAnUpdateFromBeforeWhatsChangedExistedStillShowsIt() {
        for key in WhatsChangedTracker.earlierUseKeys {
            defaults.removePersistentDomain(forName: suite)
            defaults.set(true, forKey: key)
            XCTAssertEqual(WhatsChangedTracker(defaults: defaults).due(build: 31, notes: notes(31))?.build, 31, key)
        }
    }

    func testOlderEqualAndNotelessBuildsNeverShow() {
        let tracker = WhatsChangedTracker(defaults: defaults)
        tracker.record(31)
        XCTAssertNil(tracker.due(build: 31, notes: notes(31)))
        XCTAssertNil(tracker.due(build: 30, notes: notes(30)))
        XCTAssertEqual(defaults.integer(forKey: WhatsChangedTracker.lastBuildKey), 31, "A downgrade keeps the newest build.")
        XCTAssertNil(tracker.due(build: 32, notes: { throw ReleaseNotesFailure.missingVersion }))
        XCTAssertEqual(defaults.integer(forKey: WhatsChangedTracker.lastBuildKey), 32)
        XCTAssertNil(tracker.due(build: 33, notes: notes(34)), "Notes for another build are not this build's notes.")
    }

    func testShowsOnceWithoutTakingFocusAndHidesTheCoffeeReminder() throws {
        WhatsChangedTracker(defaults: defaults).record(30)
        controller.prepare(build: 31, notes: notes(31))
        XCTAssertTrue(controller.isPending)
        model.beginDrawing()
        model.interact()
        XCTAssertTrue(model.canShowSupportReminder)
        let keyWindow = NSApp.keyWindow
        controller.start(isLoginItem: false)
        let window = try XCTUnwrap(controller.window)
        XCTAssertTrue(window.isVisible)
        XCTAssertEqual(window.title, "What's changed")
        XCTAssertFalse(window.isKeyWindow)
        XCTAssertTrue(NSApp.keyWindow === keyWindow)
        XCTAssertTrue(model.whatsChangedVisible)
        XCTAssertFalse(model.canShowSupportReminder)
        XCTAssertEqual(defaults.integer(forKey: WhatsChangedTracker.lastBuildKey), 31)
        controller.close()
        XCTAssertFalse(model.whatsChangedVisible)
        let again = WhatsChangedController(model: model)
        again.prepare(build: 31, notes: notes(31))
        XCTAssertFalse(again.isPending)
    }

    func testWaitsWhileDrawingAndUntilThePersonOpensAnnotatorAfterSignIn() async throws {
        WhatsChangedTracker(defaults: defaults).record(30)
        controller.prepare(build: 31, notes: notes(31))
        controller.start(isLoginItem: true)
        XCTAssertNil(controller.window)
        model.beginDrawing()
        model.active?.panel.ignoresMouseEvents = true
        controller.personOpenedAnnotator()
        XCTAssertNil(controller.window, "Drawing is never covered.")
        XCTAssertEqual(defaults.integer(forKey: WhatsChangedTracker.lastBuildKey), 30, "Not shown yet, so not recorded.")
        model.interact()
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertTrue(try XCTUnwrap(controller.window).isVisible)
        XCTAssertEqual(model.mode, .interact)
    }

    func testAnnouncementsWaitForBreaksSharingZoomAndTheIntroduction() {
        XCTAssertTrue(model.canShowAnnouncement)
        model.introductionVisible = true
        XCTAssertFalse(model.canShowAnnouncement)
        model.introductionVisible = false
        model.visibilityAnimating = true
        XCTAssertFalse(model.canShowAnnouncement)
        model.visibilityAnimating = false
        model.errorMessage = "Test error"
        XCTAssertFalse(model.canShowAnnouncement)
        model.errorMessage = nil
        XCTAssertTrue(model.canShowAnnouncement)
    }

    func testWindowShowsTheNotesKnownLimitsAndCoffeeLine() async throws {
        let host = NSHostingView(rootView: WhatsChangedView(entry: .uxReference, model: model, close: {}, animatesEntrance: false))
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: WhatsChangedView.width, height: WhatsChangedView.height),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let text = NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
        for expected in ["Annotator is updated", "You now have version 0.1.0-beta.13 (31).", "Added", "Changed", "Fixed",
                         "Known limits", "Enjoying Annotator?", "Buy me a coffee", "Close"] {
            XCTAssertTrue(text.contains(expected), "Missing \(expected)")
        }
        XCTAssertFalse(text.contains("secure redaction"), "Known limits start closed.")
        XCTAssertFalse(text.contains("Coffee is always welcome"), "What's changed shows only Enjoying Annotator? and the button.")
        XCTAssertNotNil(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "whats-changed-close" })
    }
}

@MainActor
final class GentleUpdateReminderTests: XCTestCase {
    func testAutomaticChecksAreOnDailyWithoutSilentInstallsOrProfiles() {
        XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: "SUEnableAutomaticChecks") as? Bool, true)
        XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: "SUScheduledCheckInterval") as? Int, 86400)
        XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: "SUAutomaticallyUpdate") as? Bool, false)
        XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: "SUSendProfileInfo") as? Bool, false)
    }

    func testScheduledUpdatesWaitWhileThePersonIsBusy() {
        var busy = true
        var reminders: [String?] = []
        let controller = AppUpdateController(changed: { _, _, _ in }, reminder: { reminders.append($0) },
                                             canInterrupt: { !busy })
        XCTAssertTrue(controller.supportsGentleScheduledUpdateReminders)
        XCTAssertFalse(controller.showsScheduledUpdateNow)
        busy = false
        XCTAssertTrue(controller.showsScheduledUpdateNow)
        controller.remind("0.1.0-beta.13")
        controller.remind(nil)
        XCTAssertEqual(reminders, ["0.1.0-beta.13", nil])
    }

    func testAWaitingUpdateChangesTheMenuAndStatus() {
        let model = WorkspaceModel(defaults: UserDefaults(suiteName: "gentle-\(UUID())")!)
        defer { model.closeAll() }
        model.updateStatus = AppUpdateController.status(automatic: true)
        XCTAssertEqual(model.updateStatusLine, "Annotator checks for updates once a day. Downloads are verified before installation.")
        model.updateReminderVersion = "0.1.0-beta.13"
        XCTAssertEqual(model.updateStatusLine, "Annotator 0.1.0-beta.13 is ready to install.")
    }
}
