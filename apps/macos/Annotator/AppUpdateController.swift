import AppKit
import Sparkle

enum AppUpdateConfigurationError: Error, LocalizedError {
    case invalidFeed, missingKey
    var errorDescription: String? {
        switch self {
        case .invalidFeed: "The update address is invalid. Report this in Settings > Help."
        case .missingKey: "The update signing key is missing or invalid. Updates are off. Report this in Settings > Help."
        }
    }
}

/// Sparkle updates. Automatic checks are on by default, once a day. Annotator is a menu bar app, so it uses Sparkle's
/// gentle reminders: an update found while the person draws, zooms, shares a region or takes a break waits. Once any check
/// finds an update, the menu and Settings say it's ready until the person installs or skips it. Opening the menu checks
/// quietly when the last check is a few hours old, so the menu can say so.
@MainActor
final class AppUpdateController: NSObject {
    /// Opening the menu checks quietly when the last check is at least this old.
    nonisolated static let menuCheckInterval: TimeInterval = 6 * 60 * 60

    private var controller: SPUStandardUpdaterController?
    private var observation: NSKeyValueObservation?
    private let changed: (Bool, Bool, String) -> Void
    private let reminder: (String?) -> Void
    private let canInterrupt: () -> Bool
    var canCheck: Bool { controller?.updater.canCheckForUpdates ?? false }
    var isConfigured: Bool { controller != nil }

    /// - Parameters:
    ///   - reminder: Called with the version of an update that a check found, and with nil once it's installed or skipped,
    ///     or a check finds none.
    ///   - canInterrupt: Whether Sparkle's update window may appear now.
    init(changed: @escaping (Bool, Bool, String) -> Void, reminder: @escaping (String?) -> Void = { _ in },
         canInterrupt: @escaping () -> Bool = { true }) {
        self.changed = changed
        self.reminder = reminder
        self.canInterrupt = canInterrupt
        super.init()
    }

    static func validate(feed: String?, publicKey: String?) throws -> Bool {
        let feed = feed?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let key = publicKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if (feed.isEmpty || feed.contains("$(")) && (key.isEmpty || key.contains("$(")) { return false }
        guard let url = URLComponents(string:feed), url.scheme == "https",
              url.host?.isEmpty == false, url.user == nil, url.password == nil else {
            throw AppUpdateConfigurationError.invalidFeed
        }
        guard Data(base64Encoded:key)?.count == 32 else { throw AppUpdateConfigurationError.missingKey }
        return true
    }

    func configure(bundle: Bundle = .main) throws {
        guard try Self.validate(feed:bundle.object(forInfoDictionaryKey:"SUFeedURL") as? String,
                                publicKey:bundle.object(forInfoDictionaryKey:"SUPublicEDKey") as? String) else {
            changed(false,false,"Updates aren't set up in this build. Find new versions in Settings > About.")
            return
        }
        let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
        controller.updater.sendsSystemProfile = false
        try controller.updater.start()
        self.controller = controller
        observation = controller.updater.observe(\.canCheckForUpdates, options:[.initial,.new]) { [weak self] updater, _ in
            MainActor.assumeIsolated {
                self?.changed(updater.canCheckForUpdates,updater.automaticallyChecksForUpdates,
                              Self.status(automatic: updater.automaticallyChecksForUpdates))
            }
        }
    }

    static func status(automatic: Bool) -> String {
        automatic ? "Annotator checks for updates once a day. Downloads are verified before installation."
            : "Automatic checks are off. You can still check here."
    }

    func check() { controller?.checkForUpdates(nil) }

    /// The menu opened: when automatic checks are on and the last check is old enough, asks the feed without showing
    /// anything. A found update turns the menu's row into Update available….
    func checkWhenMenuOpens(now: Date = Date()) {
        guard let updater = controller?.updater, updater.automaticallyChecksForUpdates, updater.canCheckForUpdates,
              !updater.sessionInProgress, Self.menuCheckIsDue(lastCheck: updater.lastUpdateCheckDate, now: now) else { return }
        updater.checkForUpdateInformation()
    }

    /// Whether a quiet menu check is due: no check yet, the last one is at least `menuCheckInterval` old, or the clock
    /// was set back since.
    nonisolated static func menuCheckIsDue(lastCheck: Date?, now: Date) -> Bool {
        guard let lastCheck else { return true }
        let age = now.timeIntervalSince(lastCheck)
        return age < 0 || age >= menuCheckInterval
    }

    /// Whether Sparkle may show a scheduled update now; otherwise it waits as a gentle reminder.
    var showsScheduledUpdateNow: Bool { canInterrupt() }

    /// Names the update that a check found, or clears it once it's installed, skipped or gone.
    func remind(_ version: String?) { reminder(version) }

    func setAutomaticChecks(_ enabled: Bool) {
        guard let controller else { return }
        controller.updater.automaticallyChecksForUpdates = enabled
        changed(controller.updater.canCheckForUpdates,enabled,
                enabled ? "Automatic checks are on. Drawing content isn't sent." :
                    "Automatic checks are off. You can still check here.")
    }
}

extension AppUpdateController: @preconcurrency SPUStandardUserDriverDelegate {
    var supportsGentleScheduledUpdateReminders: Bool { true }

    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                              andInImmediateFocus immediateFocus: Bool) -> Bool {
        showsScheduledUpdateNow
    }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
                                                   state: SPUUserUpdateState) {
        remind(update.displayVersionString)
    }
}

extension AppUpdateController: @preconcurrency SPUUpdaterDelegate {
    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) { remind(item.displayVersionString) }

    func updaterDidNotFindUpdate(_ updater: SPUUpdater) { remind(nil) }

    /// Remind me later keeps the menu's Update available…; installing or skipping the version clears it.
    func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice, forUpdate updateItem: SUAppcastItem,
                 state: SPUUserUpdateState) {
        remind(choice == .dismiss ? updateItem.displayVersionString : nil)
    }
}
