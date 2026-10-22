import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

/// Specification 014. The environment is fake: no window is shown, nothing takes focus, nothing beeps,
/// the Mac is never kept awake and the lock is a probe, so these tests never lock the test Mac.
@MainActor
final class BreakTimerTests: XCTestCase {
    private final class Probe {
        var now = Date(timeIntervalSinceReferenceDate: 800_000_000)
        var displays = [CGRect(x: 0, y: 0, width: 1440, height: 900), CGRect(x: 1440, y: 0, width: 1920, height: 1080)]
        var pointer = CGPoint(x: 1700, y: 400)
        var lockAvailable = true
        var lockError: Error?
        var locks = 0
        var locked = false
        var chimes = 0
        /// One entry per request: `true` keeps the display on, `false` only the computer.
        var awakeRequests: [Bool] = []
        var awakeReleases = 0
    }

    private final class Token: NSObject {}

    private var cleanups: [() -> Void] = []

    override func tearDown() async throws {
        for cleanup in cleanups.reversed() { cleanup() }
        cleanups = []
    }

    private func environment(_ probe: Probe) -> BreakTimerEnvironment {
        var environment = BreakTimerEnvironment()
        environment.now = { probe.now }
        environment.displays = { probe.displays }
        environment.pointer = { probe.pointer }
        environment.lock = BreakScreenLock(
            isAvailable: { probe.lockAvailable },
            lock: {
                probe.locks += 1
                if let error = probe.lockError { throw error }
            },
            isLocked: { probe.locked })
        environment.chime = { probe.chimes += 1 }
        environment.keepAwake = { display in probe.awakeRequests.append(display); return Token() }
        environment.allowSleep = { _ in probe.awakeReleases += 1 }
        environment.isActive = { true }
        environment.voiceOverRunning = { false }
        environment.presents = false
        environment.announces = false
        return environment
    }

    private func model(_ probe: Probe, settings: BreakTimerSettings = BreakTimerSettings()) throws -> WorkspaceModel {
        let suite = "break-timer-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.breakTimerEnvironment = environment(probe)
        model.setBreakTimerSettings(settings)
        cleanups.append { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        return model
    }

    private func unlocked() -> BreakTimerSettings {
        var settings = BreakTimerSettings()
        settings.lockWhenTouched = false
        return settings
    }

    private func key(_ code: UInt16, _ characters: String, modifiers: NSEvent.ModifierFlags = []) throws -> NSEvent {
        try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers, timestamp: 0,
                                       windowNumber: 0, context: nil, characters: characters,
                                       charactersIgnoringModifiers: characters, isARepeat: false, keyCode: code))
    }

    private func mouse(_ type: NSEvent.EventType) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0,
                                         context: nil, eventNumber: 0, clickCount: 1, pressure: 0))
    }

    private func arm(_ timer: BreakTimerController) {
        for _ in 0..<BreakTimerController.armingSeconds { timer.armStep() }
    }

    // MARK: Windows

    func testBreakCoversEveryDisplayAboveEverythingAndKeepsItAwake() throws {
        let probe = Probe()
        let model = try model(probe)
        XCTAssertTrue(model.startBreak())
        let timer = model.breakTimer
        XCTAssertTrue(timer.isRunning)
        XCTAssertEqual(timer.windows.map(\.frame), probe.displays)
        for window in timer.windows {
            XCTAssertEqual(window.level, .screenSaver)
            XCTAssertTrue(window.collectionBehavior.contains(.canJoinAllSpaces))
            XCTAssertTrue(window.collectionBehavior.contains(.fullScreenAuxiliary))
            XCTAssertNotEqual(window.sharingType, .none, "Meeting apps sharing a whole display must show the timer.")
            XCTAssertTrue(window.canBecomeKey)
            XCTAssertTrue(window.isOpaque)
        }
        XCTAssertEqual(probe.awakeRequests, [true])
        XCTAssertEqual(timer.faceState.label, "10:00")
        XCTAssertEqual(timer.faceState.title, "Coffee break")
        XCTAssertEqual(timer.faceState.endTime, BreakTimerController.endTimeText(probe.now.addingTimeInterval(600)))
        XCTAssertEqual(timer.lockState, .arming(3))
        timer.end()
        XCTAssertFalse(timer.isRunning)
        XCTAssertTrue(timer.windows.isEmpty)
        XCTAssertEqual(probe.awakeReleases, 1)
        XCTAssertFalse(timer.monitorsInput)
    }

    func testOneDisplayUsesTheDisplayWithThePointer() throws {
        let probe = Probe()
        var settings = unlocked()
        settings.allDisplays = false
        settings.background = .blur
        let model = try model(probe, settings: settings)
        XCTAssertTrue(model.startBreak(minutes: 5))
        XCTAssertEqual(model.breakTimer.windows.map(\.frame), [probe.displays[1]])
        XCTAssertFalse(model.breakTimer.windows[0].isOpaque, "Blurred desktop needs a see-through window.")
        XCTAssertEqual(model.breakTimer.faceState.label, "5:00")
    }

    func testDisplayChangesRebuildTheWindowsAndKeepTheTime() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak()
        probe.now = probe.now.addingTimeInterval(90)
        probe.displays = [CGRect(x: 0, y: 0, width: 1512, height: 982)]
        NotificationCenter.default.post(name: NSApplication.didChangeScreenParametersNotification, object: nil)
        XCTAssertEqual(model.breakTimer.windows.map(\.frame), probe.displays)
        model.breakTimer.refresh()
        XCTAssertEqual(model.breakTimer.faceState.label, "8:30")
    }

    // MARK: Locking

    func testArmedBreakLocksOnAKeyAndEndsWhenMacOSConfirms() throws {
        let probe = Probe()
        let model = try model(probe)
        model.startBreak()
        let timer = model.breakTimer
        XCTAssertNil(timer.handle(try key(0, "a")), "Input during arming is swallowed.")
        XCTAssertEqual(probe.locks, 0)
        timer.armStep()
        XCTAssertEqual(timer.lockState, .arming(2))
        timer.armStep(); timer.armStep()
        XCTAssertEqual(timer.lockState, .armed)
        XCTAssertNil(timer.handle(try key(0, "a")))
        XCTAssertEqual(probe.locks, 1)
        XCTAssertEqual(timer.lockState, .locking)
        XCTAssertNil(timer.handle(try key(0, "a")))
        XCTAssertEqual(probe.locks, 1, "One lock request only.")
        probe.locked = true
        timer.confirmLock()
        XCTAssertTrue(timer.isRunning, "The break keeps counting behind the lock screen.")
        XCTAssertTrue(timer.isAway)
        XCTAssertNil(timer.lastEndReason)
        XCTAssertTrue(timer.windows.isEmpty)
        XCTAssertEqual(probe.awakeRequests, [true, false], "Only the computer stays awake while locked.")
        XCTAssertEqual(probe.awakeReleases, 1)
        XCTAssertNotNil(timer.handle(try key(0, "a")), "Nothing is handled while the Mac is locked.")
        probe.now = probe.now.addingTimeInterval(120)
        timer.systemUnlocked()
        XCTAssertFalse(timer.isAway)
        XCTAssertEqual(timer.windows.map(\.frame), probe.displays, "The break screen comes back after sign-in.")
        XCTAssertEqual(timer.lockState, .off, "Whoever signed in is at the Mac, so it doesn't lock again.")
        XCTAssertTrue(timer.controlsVisible)
        XCTAssertEqual(timer.faceState.label, "8:00")
        XCTAssertEqual(probe.awakeRequests, [true, false, true])
        XCTAssertEqual(probe.locks, 1)
        XCTAssertNil(timer.handle(try key(53, "\u{1b}")))
        XCTAssertFalse(timer.isRunning)
    }

    func testEveryKindOfTouchLocksButJitterDoesNot() throws {
        let mediaKey = try XCTUnwrap(NSEvent.otherEvent(with: .systemDefined, location: .zero, modifierFlags: [],
            timestamp: 0, windowNumber: 0, context: nil, subtype: BreakTimerController.mediaKeySubtype, data1: 0, data2: 0))
        for event in [try key(56, ""), try mouse(.leftMouseDown), try mouse(.rightMouseDown), mediaKey, try mouse(.mouseMoved)] {
            let probe = Probe()
            let model = try model(probe)
            model.startBreak()
            arm(model.breakTimer)
            if event.type == .mouseMoved {
                probe.pointer.x += 8
                _ = model.breakTimer.handle(event)
                XCTAssertEqual(probe.locks, 0, "Small jitter must not lock.")
                probe.pointer.x += 10
            }
            _ = model.breakTimer.handle(event)
            XCTAssertEqual(probe.locks, 1, "\(event.type) should lock.")
        }
    }

    func testTheShortcutCancelsWhileArmingAndLocksOnceArmed() throws {
        let probe = Probe()
        let model = try model(probe)
        model.toggleBreakFromShortcut()
        XCTAssertTrue(model.breakTimerRunning)
        model.toggleBreakFromShortcut()
        XCTAssertFalse(model.breakTimerRunning)
        XCTAssertEqual(model.breakTimer.lastEndReason, .cancelled)
        XCTAssertEqual(probe.locks, 0)
        model.toggleBreakFromShortcut()
        arm(model.breakTimer)
        model.toggleBreakFromShortcut()
        XCTAssertEqual(probe.locks, 1)
    }

    func testGlobalShortcutEventsPassTheBreakScreen() throws {
        let probe = Probe()
        let model = try model(probe)
        model.startBreak()
        for subtype: Int16 in [6, 9] {
            let hotKey = try XCTUnwrap(NSEvent.otherEvent(with: .systemDefined, location: .zero, modifierFlags: [],
                timestamp: 0, windowNumber: 0, context: nil, subtype: subtype, data1: 0, data2: 0))
            XCTAssertNotNil(model.breakTimer.handle(hotKey), "Carbon must still see the break's shortcut.")
        }
        XCTAssertEqual(probe.locks, 0)
    }

    func testEscapeCancelsWhileArming() throws {
        let probe = Probe()
        let model = try model(probe)
        model.startBreak()
        XCTAssertNil(model.breakTimer.handle(try key(53, "\u{1b}")))
        XCTAssertFalse(model.breakTimerRunning)
        XCTAssertEqual(model.breakTimer.lastEndReason, .cancelled)
        XCTAssertEqual(probe.locks, 0)
    }

    func testAFailedLockKeepsTheTimerOnScreenAndSaysNothingIsLocked() throws {
        let probe = Probe()
        probe.lockError = BreakTimerError.lockFailed(-1)
        let model = try model(probe)
        model.startBreak()
        let timer = model.breakTimer
        arm(timer)
        _ = timer.handle(try mouse(.leftMouseDown))
        XCTAssertTrue(timer.isRunning)
        XCTAssertEqual(timer.lockState, .off)
        XCTAssertEqual(timer.faceState.notice, BreakTimerError.lockFailed(-1).errorDescription)
        XCTAssertTrue(timer.faceState.notice?.contains("so nothing is locked") == true)
        XCTAssertTrue(timer.controlsVisible)
        XCTAssertEqual(model.errorMessage, BreakTimerError.lockFailed(-1).errorDescription)
        XCTAssertNotNil(timer.handle(try key(0, "a")), "Without a lock, other keys pass on.")
        XCTAssertTrue(timer.isRunning)
    }

    func testALockMacOSDoesNotConfirmIsNotShownAsALock() throws {
        let probe = Probe()
        let model = try model(probe)
        model.startBreak()
        arm(model.breakTimer)
        _ = model.breakTimer.handle(try mouse(.leftMouseDown))
        model.breakTimer.confirmLock()
        XCTAssertTrue(model.breakTimerRunning)
        XCTAssertEqual(model.breakTimer.lockState, .off)
        XCTAssertEqual(model.breakTimer.faceState.notice, BreakTimerError.lockNotConfirmed.errorDescription)
    }

    func testABreakThatCannotLockDoesNotStart() throws {
        let probe = Probe()
        probe.lockAvailable = false
        let model = try model(probe)
        XCTAssertFalse(model.startBreak())
        XCTAssertFalse(model.breakTimerRunning)
        XCTAssertTrue(model.breakTimer.windows.isEmpty)
        XCTAssertEqual(model.errorMessage, BreakTimerError.lockUnavailable.errorDescription)
        XCTAssertTrue(probe.awakeRequests.isEmpty)
        model.setBreakTimerSettings(unlocked())
        XCTAssertTrue(model.startBreak())
    }

    func testTheBreakCountsAndChimesWhileLockedAndReturnsAfterSignIn() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak(minutes: 1)
        let timer = model.breakTimer
        // The app observes com.apple.screenIsLocked; posting it here would reach every app on this Mac.
        timer.systemLocked()
        XCTAssertTrue(model.breakTimerRunning)
        XCTAssertTrue(timer.windows.isEmpty)
        XCTAssertTrue(timer.keepsComputerAwake)
        XCTAssertFalse(timer.keepsDisplayAwake, "The display may sleep behind the lock screen.")
        timer.systemLocked()
        XCTAssertEqual(probe.awakeRequests, [true, false], "A second lock notice changes nothing.")
        probe.now = probe.now.addingTimeInterval(60.5)
        timer.refresh()
        XCTAssertEqual(probe.chimes, 1, "The sound plays at zero even while the Mac is locked.")
        XCTAssertFalse(timer.keepsComputerAwake, "After the sound nothing needs the Mac awake.")
        probe.now = probe.now.addingTimeInterval(90)
        timer.systemUnlocked()
        XCTAssertEqual(timer.faceState.phase, .timeUp)
        XCTAssertEqual(timer.faceState.label, "+1:30")
        XCTAssertEqual(timer.windows.count, probe.displays.count)
        XCTAssertTrue(timer.keepsDisplayAwake)
        XCTAssertEqual(probe.chimes, 1)
        timer.systemUnlocked()
        XCTAssertEqual(timer.windows.count, probe.displays.count, "A second unlock notice changes nothing.")
    }

    func testATimeUpMissedDuringSleepIsShownWithoutTheSound() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak(minutes: 1)
        let timer = model.breakTimer
        timer.systemLocked()
        probe.now = probe.now.addingTimeInterval(BreakTimerController.awakeAfterTimeUp + 120)
        timer.systemUnlocked()
        XCTAssertEqual(probe.chimes, 0, "A late time up doesn't play the sound at sign-in.")
        XCTAssertEqual(timer.faceState.phase, .timeUp)
        XCTAssertFalse(timer.keepsComputerAwake, "Long after time up the display may sleep.")
        XCTAssertFalse(timer.windows.isEmpty)
    }

    func testLockingDuringArmingStopsArmingAndTheBreakReturnsWithoutLocking() throws {
        let probe = Probe()
        let model = try model(probe)
        model.startBreak()
        let timer = model.breakTimer
        timer.systemLocked()
        timer.armStep(); timer.armStep(); timer.armStep()
        XCTAssertEqual(timer.lockState, .off)
        timer.systemUnlocked()
        XCTAssertEqual(timer.lockState, .off)
        _ = timer.handle(try mouse(.leftMouseDown))
        XCTAssertEqual(probe.locks, 0)
    }

    // MARK: Without locking

    func testKeysPauseChangeTimeAndEndWithoutLocking() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak()
        let timer = model.breakTimer
        XCTAssertEqual(timer.lockState, .off)
        XCTAssertTrue(timer.controlsVisible, "The controls show at the start so people see them.")
        probe.now = probe.now.addingTimeInterval(30)
        XCTAssertNil(timer.handle(try key(49, " ")))
        XCTAssertEqual(timer.faceState.phase, .paused)
        XCTAssertNil(timer.faceState.secondsToEmpty, "A paused ring stands still.")
        probe.now = probe.now.addingTimeInterval(120)
        timer.refresh()
        XCTAssertEqual(timer.faceState.label, "9:30")
        XCTAssertNil(timer.handle(try key(49, " ")))
        XCTAssertEqual(timer.faceState.phase, .running)
        XCTAssertNil(timer.handle(try key(126, "")))
        XCTAssertEqual(timer.faceState.label, "10:30")
        XCTAssertEqual(timer.faceState.badge?.text, "+1 min")
        XCTAssertNil(timer.handle(try key(27, "-")))
        XCTAssertNil(timer.handle(try key(125, "")))
        XCTAssertEqual(timer.faceState.label, "8:30")
        XCTAssertNotNil(timer.handle(try key(12, "q", modifiers: .command)), "Command keys stay with the app.")
        timer.hideControls()
        XCTAssertNotNil(timer.handle(try key(48, "\t")), "Tab moves between the controls.")
        XCTAssertTrue(timer.controlsVisible, "Tab shows the controls.")
        timer.hideControls()
        XCTAssertTrue(timer.controlsVisible, "Controls with keyboard focus stay.")
        XCTAssertNotNil(timer.handle(try key(49, " ")), "Space presses the focused control.")
        XCTAssertNotNil(timer.handle(try key(36, "\r")), "Return presses the focused control.")
        XCTAssertTrue(timer.isRunning)
        XCTAssertEqual(timer.faceState.phase, .running)
        _ = timer.handle(try mouse(.mouseMoved))
        timer.hideControls()
        XCTAssertFalse(timer.controlsVisible)
        XCTAssertNil(timer.handle(try key(53, "\u{1b}")))
        XCTAssertFalse(timer.isRunning)
        XCTAssertEqual(timer.lastEndReason, .ended)
        XCTAssertEqual(probe.locks, 0)
    }

    func testControlsHideUnlessThePointerRestsOnThem() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak()
        let timer = model.breakTimer
        timer.hideControls()
        XCTAssertFalse(timer.controlsVisible)
        _ = timer.handle(try mouse(.mouseMoved))
        XCTAssertTrue(timer.controlsVisible)
        timer.controlsHovered = true
        timer.hideControls()
        XCTAssertTrue(timer.controlsVisible)
        timer.controlsHovered = false
        timer.hideControls()
        XCTAssertFalse(timer.controlsVisible)
    }

    func testTimeUpChimesOnceCountsOverAndLetsTheDisplaySleepLater() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak(minutes: 1)
        let timer = model.breakTimer
        let ripples = timer.rippleGeneration
        probe.now = probe.now.addingTimeInterval(30)
        timer.refresh()
        XCTAssertEqual(timer.faceState.phase, .lastMinute)
        probe.now = probe.now.addingTimeInterval(35)
        timer.refresh()
        timer.refresh()
        XCTAssertEqual(probe.chimes, 1)
        XCTAssertEqual(timer.rippleGeneration, ripples + 1)
        XCTAssertEqual(timer.faceState.phase, .timeUp)
        XCTAssertEqual(timer.faceState.title, "Time's up")
        XCTAssertEqual(timer.faceState.label, "+0:05")
        XCTAssertEqual(timer.faceState.fraction, 1, "The ring fills again at time up.")
        XCTAssertNil(timer.faceState.endTime)
        XCTAssertTrue(timer.faceState.spokenValue.hasPrefix("Time's up."))
        XCTAssertNil(timer.handle(try key(49, " ")))
        XCTAssertEqual(timer.faceState.phase, .timeUp, "A finished break can't pause.")
        XCTAssertNil(timer.handle(try key(125, "")))
        XCTAssertEqual(timer.faceState.label, "+0:05", "Removing time after time up keeps the time over.")
        XCTAssertNil(timer.faceState.badge)
        XCTAssertTrue(timer.keepsDisplayAwake)
        probe.now = probe.now.addingTimeInterval(BreakTimerController.awakeAfterTimeUp)
        timer.refresh()
        XCTAssertFalse(timer.keepsDisplayAwake)
        XCTAssertEqual(probe.awakeReleases, 1)
        XCTAssertTrue(timer.isRunning, "The timer stays until someone ends it.")
        _ = timer.handle(try key(126, ""))
        XCTAssertEqual(timer.faceState.label, "1:00")
        XCTAssertEqual(timer.faceState.phase, .lastMinute)
        XCTAssertTrue(timer.keepsDisplayAwake, "More time keeps the display awake again.")
    }

    func testSilentBreakWithoutCountingOver() throws {
        let probe = Probe()
        var settings = unlocked()
        settings.chime = false
        settings.countOver = false
        settings.showEndTime = false
        settings.title = ""
        let model = try model(probe, settings: settings)
        model.startBreak(minutes: 2)
        XCTAssertNil(model.breakTimer.faceState.endTime)
        XCTAssertEqual(model.breakTimer.faceState.title, "")
        probe.now = probe.now.addingTimeInterval(200)
        model.breakTimer.refresh()
        XCTAssertEqual(probe.chimes, 0)
        XCTAssertEqual(model.breakTimer.faceState.label, "0:00")
    }

    // MARK: Other features and settings

    func testStartingABreakReleasesDrawingInputAndKeepsDrawings() throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        let session = OverlaySession(referenceImage: try UXReferenceCaptureTests.backdrop(CGSize(width: 400, height: 300)),
                                     model: model, frame: CGRect(x: -40_000, y: -40_000, width: 400, height: 300))
        session.panel.alphaValue = 0
        session.panel.ignoresMouseEvents = true
        model.adoptReferenceSession(session)
        model.tool = .rectangle
        XCTAssertEqual(model.mode, .draw)
        let count = session.document.elements.count
        model.startBreak()
        XCTAssertEqual(model.mode, .interact)
        XCTAssertEqual(session.document.elements.count, count)
    }

    func testSettingsPersistAndReachTheNextBreak() throws {
        let probe = Probe()
        let suite = "break-settings-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let first = WorkspaceModel(defaults: defaults)
        var settings = unlocked()
        settings.minutes = 25
        settings.title = "Lunch"
        settings.palette = .berry
        first.setBreakTimerSettings(settings)
        first.closeAll()
        let second = WorkspaceModel(defaults: defaults)
        second.breakTimerEnvironment = environment(probe)
        defer { second.closeAll() }
        XCTAssertEqual(second.breakTimerSettings, settings)
        second.startBreak()
        XCTAssertEqual(second.breakTimer.faceState.label, "25:00")
        XCTAssertEqual(second.breakTimer.faceState.settings.palette, .berry)
        var invalid = settings
        invalid.minutes = 0
        second.setBreakTimerSettings(invalid)
        XCTAssertEqual(second.breakTimerSettings, settings)
        XCTAssertEqual(second.errorMessage, CanvasError.invalidDocument("A break lasts 1 to 180 minutes.").errorDescription)
    }

    func testBreakShortcutIsAGlobalKeyWithItsOwnKeys() throws {
        let probe = Probe()
        let model = try model(probe)
        XCTAssertTrue(GlobalShortcutKind.allCases.contains(.breakTimer))
        XCTAssertEqual(GlobalShortcutKind.breakTimer.title, "Break timer")
        XCTAssertEqual(GlobalShortcutKind.breakTimer.value(in: model).label, "⌃3")
        var registered: [ActivationShortcut] = []
        model.onBreakShortcutChanged = { registered.append($0) }
        model.setBreakShortcut(model.preferences.zoomShortcut ?? .liveZoom)
        XCTAssertEqual(model.errorMessage, CanvasError.invalidDocument(
            "Break timer needs its own shortcut. Choose keys that no other global shortcut uses.").errorDescription)
        XCTAssertTrue(registered.isEmpty)
        let keys = ActivationShortcut(keyCode: 11, character: "b", control: true, option: true)
        GlobalShortcutKind.breakTimer.set(keys, in: model)
        XCTAssertEqual(registered, [keys])
        XCTAssertEqual(model.preferences.breakShortcut, keys)
        model.setBreakShortcut(ActivationShortcut(keyCode: 37, character: "l", command: true))
        XCTAssertNil(model.toolLibraryShortcut, "Command-L leaves the Tools window when the break timer uses it.")
    }

    func testMenuOffersTheBreakTimerAndSaysWhetherItLocks() async throws {
        let probe = Probe()
        let model = try model(probe)
        var performed: [MenuBarAction] = []
        let host = NSHostingView(rootView: MenuBarPopoverView(model: model, developerPreview: false,
                                                              perform: { performed.append($0) }))
        let panel = NSPanel(contentRect: CGRect(x: -40_000, y: -40_000, width: 320, height: 900),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.alphaValue = 0
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(150))
        func node(_ id: String) -> NativeAccessibilityNode? {
            NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == id }
        }
        let start = try XCTUnwrap(node("menu-startBreak"))
        XCTAssertEqual(start.accessibilityLabel(), "Start break timer")
        XCTAssertEqual(start.accessibilityValue() as? String, "10 minutes")
        for (action, minutes) in zip(MenuBarAction.quickBreaks, BreakTimerSettings.quickMinutes) {
            XCTAssertEqual(action.breakMinutes, minutes)
            XCTAssertEqual(node("menu-\(action.rawValue)")?.accessibilityLabel(), "Start a \(minutes)-minute break")
        }
        func caption() -> String? {
            node("menu-break-caption").flatMap { $0.accessibilityLabel() ?? ($0.accessibilityValue() as? String) }
        }
        XCTAssertEqual(caption(), "Your Mac locks if someone touches it.")
        XCTAssertTrue(try XCTUnwrap(node("menu-breakFifteen")).accessibilityPerformPress())
        XCTAssertEqual(performed, [.breakFifteen])
        model.setBreakTimerSettings(unlocked())
        try await Task.sleep(for: .milliseconds(120))
        XCTAssertEqual(caption(), "Press Esc to end the break.")
    }

    func testSettingsTabShowsPreviewStartAndLockingLimits() async throws {
        let probe = Probe()
        let model = try model(probe)
        model.settingsTab = .breakTimer
        let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false, initialTab: .breakTimer))
        let panel = NSPanel(contentRect: CGRect(x: -40_000, y: -40_000, width: 840, height: 1400),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.alphaValue = 0
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        for _ in 0..<40 where !NativeAccessibilityNode.nodes(in: host).contains(where: { $0.accessibilityIdentifier() == "break-lock" }) {
            try await Task.sleep(for: .milliseconds(50))
        }
        let ids = Set(NativeAccessibilityNode.nodes(in: host).compactMap { $0.accessibilityIdentifier() })
        XCTAssertTrue(ids.isSuperset(of: ["break-preview", "settings-start-break", "break-length", "break-title",
                                          "break-background", "break-lock"] +
                                         BreakTimerSettings.Palette.allCases.map { "break-palette-\($0.rawValue)" }), "\(ids)")
        let labels = NativeAccessibilityNode.nodes(in: host).flatMap { [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 } }
        XCTAssertTrue(labels.contains { $0.contains("macOS doesn't let apps show anything on the lock screen") }, "\(labels)")
        let berry = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "break-palette-berry" })
        XCTAssertTrue(berry.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(model.breakTimerSettings.palette, .berry)
        let start = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "settings-start-break" })
        XCTAssertEqual(start.accessibilityLabel(), "Start a 10-minute break")
        XCTAssertTrue(start.accessibilityPerformPress())
        XCTAssertTrue(model.breakTimerRunning)
    }

    // MARK: Drawing

    func testBreakScreenDrawsTheCountdownAndTheRingDrainsOnItsOwn() async throws {
        let probe = Probe()
        let model = try model(probe, settings: unlocked())
        model.startBreak(minutes: 10)
        let timer = model.breakTimer
        let window = try XCTUnwrap(timer.windows.first)
        window.setFrame(CGRect(x: -40_000, y: -40_000, width: 1440, height: 900), display: false)
        window.alphaValue = 0
        window.ignoresMouseEvents = true
        window.orderFrontRegardless()
        try await Task.sleep(for: .milliseconds(900))
        let content = try XCTUnwrap(window.contentView)
        func find<T: NSView>(_ type: T.Type, in view: NSView) -> T? {
            if let match = view as? T { return match }
            for child in view.subviews { if let match = find(type, in: child) { return match } }
            return nil
        }
        let ring = try XCTUnwrap(find(BreakRingLayerView.self, in: content))
        let backdrop = try XCTUnwrap(find(BreakBackdropView.self, in: content))
        XCTAssertTrue(ring.isDraining, "A running ring drains with one Core Animation animation.")
        XCTAssertEqual(backdrop.drifting, !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
        let image = try UXReferenceRecorder.render(content)
        XCTAssertFalse(UXReferenceRecorder.isBlank(image))
        if let folder = ProcessInfo.processInfo.environment["ANNOTATOR_BREAK_SNAPSHOT_DIR"] {
            try UXReferenceRecorder.writePNG(image, to: URL(fileURLWithPath: folder).appendingPathComponent("break-running.png"))
        }
        timer.togglePause()
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertFalse(ring.isDraining, "A paused ring stands still.")
        window.orderOut(nil)
    }
}
