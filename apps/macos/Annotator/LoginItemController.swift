import AppKit
import Combine
import ServiceManagement

enum LoginItemState: String {
    case off, enabled, requiresApproval, notFound, unavailable

    init(_ status: SMAppService.Status) {
        switch status {
        case .notRegistered: self = .off
        case .enabled: self = .enabled
        case .requiresApproval: self = .requiresApproval
        case .notFound: self = .notFound
        @unknown default: self = .unavailable
        }
    }

    var isRequested: Bool { self == .enabled || self == .requiresApproval }
    var canChange: Bool { self != .unavailable }
    var message: String {
        switch self {
        case .off: "Annotator will not start automatically."
        case .enabled: "Annotator will start quietly when you log in. Use the menu-bar icon or your drawing shortcut."
        case .requiresApproval: "Approval is needed. Allow Annotator in macOS Login Items to finish enabling it."
        case .notFound: "macOS could not find a login item for this app. Turn this on to try setting it up."
        case .unavailable: "macOS returned an unsupported login-item status. Check Login Items in System Settings."
        }
    }
}

@MainActor
struct LoginItemService {
    let status: () -> SMAppService.Status
    let register: () throws -> Void
    let unregister: () async throws -> Void
    let openSettings: () -> Void

    static var mainApp: Self {
        Self(status: { SMAppService.mainApp.status },
             register: { try SMAppService.mainApp.register() },
             unregister: { try await SMAppService.mainApp.unregister() },
             openSettings: { SMAppService.openSystemSettingsLoginItems() })
    }
}

private enum LoginItemFailure: Error, LocalizedError {
    case unavailable, unchanged

    var errorDescription: String? {
        switch self {
        case .unavailable: "macOS cannot manage launch at login for this app. Check Login Items in System Settings."
        case .unchanged: "macOS did not apply the requested login-item change. Check Login Items and try again."
        }
    }
}

@MainActor
final class LoginItemController: ObservableObject {
    @Published private(set) var state: LoginItemState
    @Published private(set) var isChanging = false
    @Published private(set) var errorMessage: String?
    private let service: LoginItemService
    private let report: (Error) -> Void

    init(service: LoginItemService = .mainApp, report: @escaping (Error) -> Void) {
        self.service = service
        self.report = report
        state = LoginItemState(service.status())
    }

    func refresh() {
        guard !isChanging else { return }
        let updated = LoginItemState(service.status())
        if updated != state { errorMessage = nil }
        state = updated
    }

    func setEnabled(_ enabled: Bool) async {
        guard !isChanging else { return }
        isChanging = true
        errorMessage = nil
        defer { isChanging = false }
        state = LoginItemState(service.status())
        do {
            guard state.canChange else { throw LoginItemFailure.unavailable }
            if (enabled && state.isRequested) || (!enabled && !state.isRequested) { return }
            if enabled { try service.register() }
            else { try await service.unregister() }
            state = LoginItemState(service.status())
            guard (enabled ? state.isRequested : state == .off) else { throw LoginItemFailure.unchanged }
        } catch {
            state = LoginItemState(service.status())
            errorMessage = "Could not change launch at login: \(error.localizedDescription) Open Login Items to check its status."
            report(error)
        }
    }

    func openSystemSettings() { service.openSettings() }
}

enum AppLaunchContext {
    static func isLoginItem(_ event: NSAppleEventDescriptor?) -> Bool {
        guard event?.eventID == AEEventID(kAEOpenApplication) else { return false }
        return event?.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?.enumCodeValue
            == OSType(keyAELaunchedAsLogInItem)
    }
}
