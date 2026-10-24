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
/// gentle reminders: an update found while the person draws, zooms, shares a region or takes a break waits, and the
/// menu and Settings say it's ready until the person chooses Check for updates.
@MainActor
final class AppUpdateController: NSObject {
    private var controller: SPUStandardUpdaterController?
    private var observation: NSKeyValueObservation?
    private let changed: (Bool, Bool, String) -> Void
    private let reminder: (String?) -> Void
    private let canInterrupt: () -> Bool
    var canCheck: Bool { controller?.updater.canCheckForUpdates ?? false }
    var isConfigured: Bool { controller != nil }

    /// - Parameters:
    ///   - reminder: Called with the version of an update that waits for the person, and with nil once they see it.
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
        let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: nil, userDriverDelegate: self)
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

    /// Whether Sparkle may show a scheduled update now; otherwise it waits as a gentle reminder.
    var showsScheduledUpdateNow: Bool { canInterrupt() }

    /// Names the update that waits for the person, or clears it once they see it.
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
        if !handleShowingUpdate { remind(update.displayVersionString) }
    }

    func standardUserDriverDidReceiveUserAttention(forUpdate update: SUAppcastItem) { remind(nil) }

    func standardUserDriverWillFinishUpdateSession() { remind(nil) }
}
