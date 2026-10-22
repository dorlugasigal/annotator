import Foundation
import SwiftUI

@MainActor
final class SupportReminder: ObservableObject {
    static let useDaysKey = "optional-support-use-days"
    static let lastUseKey = "optional-support-last-use"
    static let lastShownKey = "optional-support-last-shown"
    static let disabledKey = "optional-support-disabled"
    private let defaults: UserDefaults
    private let now: () -> Date
    private let calendar: Calendar
    private var trackingSession = false
    private var presentedThisSession = false
    @Published private(set) var available = false

    init(defaults: UserDefaults, now: @escaping () -> Date = Date.init, calendar: Calendar = .current) {
        self.defaults = defaults
        self.now = now
        self.calendar = calendar
    }

    var enabled: Bool { !defaults.bool(forKey: Self.disabledKey) }

    func recordLaunch() { trackingSession = true }

    func recordUse() {
        guard trackingSession, enabled else { return }
        let today = now()
        var days = min(10, max(0, defaults.integer(forKey: Self.useDaysKey)))
        let previousUse = defaults.object(forKey: Self.lastUseKey) as? Date
        if previousUse.map({ calendar.isDate($0, inSameDayAs: today) }) != true {
            defaults.set(today, forKey: Self.lastUseKey)
            days = min(10, days + 1)
            defaults.set(days, forKey: Self.useDaysKey)
        }
        let previous = defaults.object(forKey: Self.lastShownKey) as? Date
        let cooldownPassed = previous.map { today.timeIntervalSince($0) >= 30 * 24 * 60 * 60 } ?? true
        available = days >= (previous == nil ? 5 : 10) && cooldownPassed
    }

    func markPresented() {
        guard !presentedThisSession else { return }
        presentedThisSession = true
        defaults.set(now(), forKey: Self.lastShownKey)
        defaults.set(0, forKey: Self.useDaysKey)
    }

    func snooze() {
        defaults.set(now(), forKey: Self.lastShownKey)
        defaults.set(0, forKey: Self.useDaysKey)
        available = false
    }

    func setEnabled(_ enabled: Bool) {
        objectWillChange.send()
        defaults.set(!enabled, forKey: Self.disabledKey)
        if !enabled { available = false }
    }
}

struct SupportReminderView: View {
    @ObservedObject var model: WorkspaceModel
    @ObservedObject var reminder: SupportReminder

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Enjoying Annotator?").font(.headline)
            Text("Buy me a coffee if you'd like. All tools stay free.")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Button("Buy me a coffee") {
                    if model.openOptionalSupport() { reminder.snooze() }
                }
                .buttonStyle(.borderedProminent).tint(CoffeeSupportView.accent)
                Button("Not now") { reminder.snooze() }
                    .help("Wait at least 30 days and more active use before asking again.")
            }.controlSize(.small)
        }
        .padding(16).frame(width: 300)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("optional-support-reminder")
        .onAppear { reminder.markPresented() }
    }
}

struct CoffeeSupportView: View {
    static let accent = Color(red: 0.58, green: 0.29, blue: 0.10)
    @ObservedObject var model: WorkspaceModel

    var body: some View {
        HStack(spacing: 16) {
            Image(systemName: "cup.and.saucer.fill").font(.largeTitle).foregroundStyle(Self.accent)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 5) {
                Text("Enjoying Annotator?").font(.headline)
                Text("Coffee is always welcome. All tools stay free.").font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Buy me a coffee") { model.openOptionalSupport() }
                .buttonStyle(.borderedProminent).tint(Self.accent)
                .disabled(model.optionalSupportURL == nil)
        }
        .padding(18)
        .background(Color.orange.opacity(0.10), in: RoundedRectangle(cornerRadius: 12))
    }
}
