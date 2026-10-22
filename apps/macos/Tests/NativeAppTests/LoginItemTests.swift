import AppKit
import ServiceManagement
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class LoginItemTests: XCTestCase {
    @MainActor private final class FakeService {
        var state: SMAppService.Status = .notRegistered
        var nextRegistrationState: SMAppService.Status = .enabled
        var registrationError: Error?
        var unregisterError: Error?
        var registerCalls = 0
        var unregisterCalls = 0
        var openSettingsCalls = 0
        var gate: CheckedContinuation<Void, Error>?
        var suspendUnregister = false

        var service: LoginItemService {
            LoginItemService(status: { self.state }, register: {
                self.registerCalls += 1
                if let error = self.registrationError { throw error }
                self.state = self.nextRegistrationState
            }, unregister: {
                self.unregisterCalls += 1
                if self.suspendUnregister {
                    try await withCheckedThrowingContinuation { self.gate = $0 }
                }
                if let error = self.unregisterError { throw error }
                self.state = .notRegistered
            }, openSettings: { self.openSettingsCalls += 1 })
        }
    }

    func testReadingOrRefreshingStatusNeverRegistersAnything() {
        let fake = FakeService()
        let controller = LoginItemController(service: fake.service, report: { _ in XCTFail("Unexpected error") })
        XCTAssertEqual(controller.state, .off)
        XCTAssertFalse(controller.state.isRequested)
        for (status, expected) in [(SMAppService.Status.enabled, LoginItemState.enabled),
                                   (.requiresApproval, .requiresApproval),
                                   (.notFound, .notFound), (.notRegistered, .off)] {
            fake.state = status
            controller.refresh()
            XCTAssertEqual(controller.state, expected)
        }
        XCTAssertEqual(fake.registerCalls, 0)
        XCTAssertEqual(fake.unregisterCalls, 0)
        XCTAssertEqual(fake.openSettingsCalls, 0)
    }

    func testUserCanEnableDisableAndCancelPendingApproval() async {
        let fake = FakeService()
        let controller = LoginItemController(service: fake.service, report: { _ in XCTFail("Unexpected error") })
        await controller.setEnabled(true)
        XCTAssertEqual(controller.state, .enabled)
        await controller.setEnabled(true)
        XCTAssertEqual(fake.registerCalls, 1)
        await controller.setEnabled(false)
        XCTAssertEqual(controller.state, .off)
        XCTAssertEqual(fake.unregisterCalls, 1)
        fake.nextRegistrationState = .requiresApproval
        await controller.setEnabled(true)
        XCTAssertEqual(controller.state, .requiresApproval)
        XCTAssertTrue(controller.state.isRequested)
        XCTAssertTrue(controller.state.message.contains("Approval"))
        XCTAssertEqual(fake.openSettingsCalls, 0, "Only the explicit Settings button opens macOS settings.")
        controller.openSystemSettings()
        XCTAssertEqual(fake.openSettingsCalls, 1)
        await controller.setEnabled(false)
        XCTAssertEqual(controller.state, .off)
        XCTAssertEqual(fake.unregisterCalls, 2)
    }

    func testRegistrationAndRemovalFailuresKeepActualSystemStateAndReportErrors() async {
        let fake = FakeService()
        var errors: [Error] = []
        let controller = LoginItemController(service: fake.service, report: { errors.append($0) })
        fake.registrationError = NSError(domain: "LoginItemTest", code: 1,
                                        userInfo: [NSLocalizedDescriptionKey: "Registration denied"])
        await controller.setEnabled(true)
        XCTAssertEqual(controller.state, .off)
        XCTAssertTrue(controller.errorMessage?.contains("Registration denied") == true)
        XCTAssertEqual(errors.count, 1)
        XCTAssertFalse(controller.isChanging)
        fake.state = .enabled
        fake.unregisterError = NSError(domain: "LoginItemTest", code: 2,
                                      userInfo: [NSLocalizedDescriptionKey: "Removal denied"])
        await controller.setEnabled(false)
        XCTAssertEqual(controller.state, .enabled)
        XCTAssertTrue(controller.errorMessage?.contains("Removal denied") == true)
        XCTAssertEqual(errors.count, 2)
        fake.state = .requiresApproval
        controller.refresh()
        XCTAssertEqual(controller.state, .requiresApproval)
        XCTAssertNil(controller.errorMessage)
    }

    func testNotFoundAllowsExplicitRegistrationWithoutAutomaticOptIn() async {
        let fake = FakeService()
        fake.state = .notFound
        let controller = LoginItemController(service: fake.service, report: { _ in XCTFail("Unexpected error") })
        XCTAssertTrue(controller.state.canChange)
        XCTAssertFalse(controller.state.isRequested)
        controller.refresh()
        XCTAssertEqual(fake.registerCalls, 0)
        await controller.setEnabled(false)
        XCTAssertEqual(fake.unregisterCalls, 0)
        await controller.setEnabled(true)
        XCTAssertEqual(fake.registerCalls, 1)
        XCTAssertEqual(controller.state, .enabled)
        XCTAssertFalse(LoginItemState.unavailable.canChange)
    }

    func testNotFoundFailuresAndUnchangedRegistrationCannotLookSuccessful() async {
        let fake = FakeService()
        fake.state = .notFound
        var errors = 0
        let controller = LoginItemController(service: fake.service, report: { _ in errors += 1 })
        fake.registrationError = NSError(domain: "LoginItemTest", code: 3,
            userInfo: [NSLocalizedDescriptionKey: "Registration was blocked"])
        await controller.setEnabled(true)
        XCTAssertEqual(fake.registerCalls, 1)
        XCTAssertEqual(controller.state, .notFound)
        XCTAssertFalse(controller.state.isRequested)
        XCTAssertTrue(controller.errorMessage?.contains("Registration was blocked") == true)
        fake.registrationError = nil
        fake.nextRegistrationState = .notFound
        await controller.setEnabled(true)
        XCTAssertEqual(controller.state, .notFound)
        XCTAssertNotNil(controller.errorMessage)
        fake.state = .notRegistered
        fake.nextRegistrationState = .notRegistered
        await controller.setEnabled(true)
        XCTAssertEqual(controller.state, .off)
        XCTAssertNotNil(controller.errorMessage)
        XCTAssertEqual(errors, 3)
    }

    func testInFlightUnregisterDoesNotRaceAnotherToggle() async throws {
        let fake = FakeService()
        fake.state = .enabled
        fake.suspendUnregister = true
        let controller = LoginItemController(service: fake.service, report: { _ in XCTFail("Unexpected error") })
        let task = Task { await controller.setEnabled(false) }
        for _ in 0..<50 {
            if fake.gate != nil { break }
            try await Task.sleep(for: .milliseconds(1))
        }
        let gate = try XCTUnwrap(fake.gate)
        XCTAssertTrue(controller.isChanging)
        await controller.setEnabled(true)
        XCTAssertEqual(fake.registerCalls, 0)
        gate.resume()
        await task.value
        XCTAssertEqual(controller.state, .off)
        XCTAssertFalse(controller.isChanging)
    }

    func testLoginLaunchIsQuietButOpeningTheAppShowsTheToolbar() throws {
        let suite = "quiet-login-launch-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.finishLaunchPresentation(isLoginItem: true)
        XCTAssertNil(app.toolbarWindow)
        XCTAssertNil(model.active)
        XCTAssertEqual(model.mode, .off)
        XCTAssertTrue(app.applicationShouldHandleReopen(NSApp, hasVisibleWindows: false))
        XCTAssertTrue(app.toolbarIsVisible)
        XCTAssertNil(model.active)
        XCTAssertEqual(model.mode, .off)
    }

    func testLaunchEventDetectionDistinguishesLoginFromOrdinaryOpen() {
        let event = NSAppleEventDescriptor(eventClass: AEEventClass(kCoreEventClass),
            eventID: AEEventID(kAEOpenApplication), targetDescriptor: nil,
            returnID: AEReturnID(kAutoGenerateReturnID), transactionID: AETransactionID(kAnyTransactionID))
        XCTAssertFalse(AppLaunchContext.isLoginItem(nil))
        XCTAssertFalse(AppLaunchContext.isLoginItem(event))
        event.setParam(NSAppleEventDescriptor(enumCode: OSType(keyAELaunchedAsLogInItem)),
                       forKeyword: AEKeyword(keyAEPropData))
        XCTAssertTrue(AppLaunchContext.isLoginItem(event))
        event.setParam(NSAppleEventDescriptor(enumCode: OSType(keyAELaunchedAsServiceItem)),
                       forKeyword: AEKeyword(keyAEPropData))
        XCTAssertFalse(AppLaunchContext.isLoginItem(event))
    }

    func testSettingsToggleUsesControllerAndRefreshesExternalChanges() async throws {
        let fake = FakeService()
        fake.state = .notFound
        let controller = LoginItemController(service: fake.service, report: { _ in XCTFail("Unexpected error") })
        let host = NSHostingController(rootView: LoginItemSettings(controller: controller).padding(20).frame(width: 500))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 500, height: 200),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.contentViewController = host
        defer { panel.orderOut(nil); panel.contentViewController = nil; panel.close() }
        panel.setContentSize(host.view.fittingSize)
        panel.orderFrontRegardless()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let toggle = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host.view).first {
            $0.accessibilityIdentifier() == "launch-at-login"
        })
        XCTAssertTrue(toggle.accessibilityPerformPress())
        for _ in 0..<50 {
            if controller.state == .enabled { break }
            try await Task.sleep(for: .milliseconds(2))
        }
        XCTAssertEqual(controller.state, .enabled)
        XCTAssertEqual(fake.registerCalls, 1)
        fake.state = .requiresApproval
        controller.refresh()
        try await Task.sleep(for: .milliseconds(30))
        XCTAssertEqual(controller.state, .requiresApproval)
        XCTAssertTrue(controller.state.isRequested)
    }

    func testLoginSettingStaysOutOfProfilesAndSupportInfoUsesSystemState() throws {
        let suite = "login-item-profile-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let fake = FakeService()
        model.loginItemController = LoginItemController(service: fake.service, report: { _ in XCTFail("Unexpected error") })
        let before = try WorkspacePreferences.read(model.preferences.data())
        fake.state = .enabled
        XCTAssertTrue(model.supportInfo.contains("Launch at login: enabled"))
        XCTAssertEqual(try WorkspacePreferences.read(model.preferences.data()), before)
        XCTAssertEqual(fake.registerCalls, 0)
        XCTAssertEqual(fake.unregisterCalls, 0)
    }
}
