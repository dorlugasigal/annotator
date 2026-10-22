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

@MainActor
final class AppUpdateController {
    private var controller: SPUStandardUpdaterController?
    private var observation: NSKeyValueObservation?
    private let changed: (Bool, Bool, String) -> Void
    var canCheck: Bool { controller?.updater.canCheckForUpdates ?? false }
    var isConfigured: Bool { controller != nil }

    init(changed: @escaping (Bool, Bool, String) -> Void) { self.changed = changed }

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
        let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: nil, userDriverDelegate: nil)
        controller.updater.sendsSystemProfile = false
        try controller.updater.start()
        self.controller = controller
        observation = controller.updater.observe(\.canCheckForUpdates, options:[.initial,.new]) { [weak self] updater, _ in
            MainActor.assumeIsolated {
                self?.changed(updater.canCheckForUpdates,updater.automaticallyChecksForUpdates,
                              "Checks need an internet connection. Downloads are verified before installation.")
            }
        }
    }

    func check() { controller?.checkForUpdates(nil) }
    func setAutomaticChecks(_ enabled: Bool) {
        guard let controller else { return }
        controller.updater.automaticallyChecksForUpdates = enabled
        changed(controller.updater.canCheckForUpdates,enabled,
                enabled ? "Automatic checks are on. Drawing content isn't sent." :
                    "Automatic checks are off. You can still check here.")
    }
}
