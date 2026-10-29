import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class FeedbackSupportTests: XCTestCase {
    func testOversizedFeedbackKeepsDraftAndOffersOnlyAnExplicitPlainForm() async throws {
        let suite = "feedback-form-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        var opened: [URL] = []
        model.openExternalURL = { opened.append($0); return true }
        let host = NSHostingView(rootView: FeedbackView(model: model, kind: .feedback))
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 500, height: 760),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer {
            panel.orderOut(nil); panel.contentView = nil; panel.close()
            model.closeAll(); defaults.removePersistentDomain(forName: suite)
        }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        func nodes() -> [NativeAccessibilityNode] { NativeAccessibilityNode.nodes(in: host) }
        func descendants<T: NSView>(_ view: NSView, of type: T.Type) -> [T] {
            (view as? T).map { [$0] } ?? view.subviews.flatMap { descendants($0, of: type) }
        }
        let labels = nodes().flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }
        XCTAssertTrue(labels.contains("Title"))
        XCTAssertTrue(labels.contains("Details"))
        XCTAssertFalse(nodes().contains { $0.accessibilityIdentifier() == "feedback-plain-form" })
        let title = try XCTUnwrap(descendants(host, of: NSTextField.self).first { $0.isEditable })
        XCTAssertTrue(panel.makeFirstResponder(title))
        let titleEditor = try XCTUnwrap(title.currentEditor() as? NSTextView)
        titleEditor.insertText("Private sample title", replacementRange: NSRange(location: 0, length: 0))
        let body = try XCTUnwrap(descendants(host, of: NSTextView.self).first { !$0.isFieldEditor })
        XCTAssertTrue(panel.makeFirstResponder(body))
        let longDetails = String(repeating: "sample-only ", count: 1_000)
        body.insertText(longDetails, replacementRange: NSRange(location: 0, length: 0))
        try await Task.sleep(for: .milliseconds(100))
        let openDraft = try XCTUnwrap(nodes().first { $0.accessibilityLabel() == "Open GitHub draft" })
        XCTAssertTrue(openDraft.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(opened.isEmpty, "An oversized draft must never open a browser automatically.")
        XCTAssertEqual(body.string, longDetails)
        let recovery = try XCTUnwrap(nodes().first { $0.accessibilityIdentifier() == "feedback-plain-form" })
        XCTAssertLessThanOrEqual(host.fittingSize.height, 680, "Long feedback errors must scroll, not grow beyond the screen.")
        XCTAssertTrue(recovery.accessibilityPerformPress())
        let url = try XCTUnwrap(opened.first)
        let parts = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false))
        XCTAssertEqual(parts.path, "/dorlugasigal/annotator/issues/new")
        XCTAssertEqual(parts.queryItems?.first { $0.name == "template" }?.value, "feedback.yml")
        XCTAssertFalse(url.absoluteString.contains("Private"))
        XCTAssertNil(parts.queryItems?.first { $0.name == "experience" })
        XCTAssertEqual(opened.count, 1)
        body.insertText("Short report", replacementRange: NSRange(location: 0, length: body.string.utf16.count))
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertFalse(nodes().contains { $0.accessibilityIdentifier() == "feedback-plain-form" })
    }

    func testFeedbackCopiesOnlyAuthoredTextAndVersion() throws {
        let draft = FeedbackDraft(kind: .issue, title: "Toolbar click", details: "Expected the button to open. It did not.")
        let pasteboard = NSPasteboard.withUniqueName()
        defer { pasteboard.releaseGlobally() }
        try draft.copy(version: "0.2.0", to: pasteboard)
        let text = try XCTUnwrap(pasteboard.string(forType: .string))
        XCTAssertTrue(text.contains("Toolbar click"))
        XCTAssertTrue(text.contains("Version: 0.2.0\n"))
        XCTAssertFalse(text.contains("Screen Recording"))
        XCTAssertThrowsError(try FeedbackDraft(kind: .feedback).report(version: "test"))
    }

    func testBothFeedbackKindsPrefillPublicIssuesWithoutSubmitting() throws {
        for kind in FeedbackKind.allCases {
            let draft = FeedbackDraft(kind: kind, title: "A & B #1", details: "A line\nAnother line & symbols?")
            let url = try draft.githubURL(base: "https://github.com/dorlugasigal/annotator/issues/new", version: "test")
            let parts = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false))
            XCTAssertEqual(parts.path, "/dorlugasigal/annotator/issues/new")
            let items = try XCTUnwrap(parts.queryItems)
            XCTAssertEqual(items.first { $0.name == "template" }?.value,
                           kind == .issue ? "bug-report.yml" : "feedback.yml")
            XCTAssertEqual(items.first { $0.name == (kind == .issue ? "steps" : "experience") }?.value, draft.details)
            XCTAssertEqual(items.first { $0.name == "title" }?.value, "[test] A & B #1")
            XCTAssertNil(items.first { $0.name == "submit" })
        }
        let large = FeedbackDraft(kind: .feedback, title: "Long", details: String(repeating: "x", count: 10_000))
        XCTAssertThrowsError(try large.githubURL(base: "https://github.com/owner/repo/issues/new", version: "test"))
        XCTAssertTrue(try large.report(version: "test").contains(large.details), "Copy must not truncate long feedback.")
    }

    func testReminderRequiresActiveDaysAndRepeatsOnlyAfterCooldownAndContinuedUse() throws {
        let suite = "support-reminder-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        var day = Date(timeIntervalSince1970: 1_700_000_000)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 0))
        let reminder = SupportReminder(defaults: defaults, now: { day }, calendar: calendar)
        reminder.recordUse()
        XCTAssertEqual(defaults.integer(forKey: SupportReminder.useDaysKey), 0)
        reminder.recordLaunch()
        for index in 0..<5 {
            reminder.recordUse()
            reminder.recordUse()
            XCTAssertEqual(defaults.integer(forKey: SupportReminder.useDaysKey), index + 1)
            XCTAssertEqual(reminder.available, index == 4)
            if index < 4 { day.addTimeInterval(86_400) }
        }
        reminder.markPresented()
        reminder.snooze()
        XCTAssertFalse(reminder.available)
        let lastShown = day
        for _ in 0..<10 {
            day.addTimeInterval(86_400)
            reminder.recordUse()
        }
        XCTAssertFalse(reminder.available, "Ten used days cannot bypass the 30-day cooldown.")
        day = lastShown.addingTimeInterval(30 * 86_400)
        let relaunched = SupportReminder(defaults: defaults, now: { day }, calendar: calendar)
        relaunched.recordLaunch()
        relaunched.recordUse()
        XCTAssertTrue(relaunched.available, "Not now must snooze, not disable reminders forever.")
        relaunched.snooze()
        day.addTimeInterval(60 * 86_400)
        relaunched.recordUse()
        XCTAssertFalse(relaunched.available, "Elapsed time alone is not continued usage.")
        relaunched.setEnabled(false)
        XCTAssertFalse(relaunched.enabled)
        let disabled = SupportReminder(defaults: defaults, now: { day }, calendar: calendar)
        disabled.recordLaunch()
        disabled.recordUse()
        XCTAssertFalse(disabled.available)
    }

    func testReminderNeverInterruptsDrawingZoomSettingsOrErrors() throws {
        let suite = "support-reminder-mode-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        XCTAssertNotNil(model.optionalSupportURL)
        model.beginDrawing()
        XCTAssertFalse(model.canShowSupportReminder)
        model.interact()
        XCTAssertTrue(model.canShowSupportReminder)
        model.showHelp = true
        XCTAssertFalse(model.canShowSupportReminder)
        model.showHelp = false
        model.settingsVisible = true
        XCTAssertFalse(model.canShowSupportReminder)
        model.settingsVisible = false
        model.errorMessage = "Test error"
        XCTAssertFalse(model.canShowSupportReminder)
        model.errorMessage = nil
        model.concealWorkspaceSurfaces()
        XCTAssertFalse(model.canShowSupportReminder)
    }
}
