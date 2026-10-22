import AppKit
import SwiftUI
import AnnotationCore
import MotionCore

enum BreakTimerError: LocalizedError, Equatable {
    case lockUnavailable
    case lockFailed(Int32)
    case lockNotConfirmed

    var errorDescription: String? {
        switch self {
        case .lockUnavailable:
            "Annotator can't lock this Mac, so the break didn't start. To use the timer without locking, turn off Lock when someone touches it in Settings > Break timer."
        case .lockFailed(let status):
            "macOS refused the lock (error \(status)), so nothing is locked. Press Control-Command-Q to lock your Mac. The lock screen hides the timer."
        case .lockNotConfirmed:
            "macOS didn't confirm the lock, so nothing is locked. Press Control-Command-Q to lock your Mac. The lock screen hides the timer."
        }
    }
}

/// Locks the Mac with the same system call as Control-Command-Q. It is a private login-framework function,
/// so it is looked up at run time, and a missing function is reported rather than hidden (spec 014, BRK-LOCK-04).
@MainActor
struct BreakScreenLock {
    var isAvailable: () -> Bool
    var lock: () throws -> Void
    var isLocked: () -> Bool

    static var system: BreakScreenLock {
        BreakScreenLock(
            isAvailable: { lockFunction() != nil },
            lock: {
                guard let function = lockFunction() else { throw BreakTimerError.lockUnavailable }
                let status = function()
                guard status == 0 else { throw BreakTimerError.lockFailed(status) }
            },
            isLocked: {
                guard let session = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
                return (session["CGSSessionScreenIsLocked"] as? Bool) ?? false
            })
    }

    private typealias LockFunction = @convention(c) () -> Int32

    private static func lockFunction() -> LockFunction? {
        guard let handle = dlopen("/System/Library/PrivateFrameworks/login.framework/Versions/Current/login", RTLD_LAZY),
              let symbol = dlsym(handle, "SACLockScreenImmediate") else { return nil }
        return unsafeBitCast(symbol, to: LockFunction.self)
    }
}

/// Everything the break timer reads from or does to the system, so tests never lock, beep or keep a Mac awake.
@MainActor
struct BreakTimerEnvironment {
    var now: () -> Date = { Date() }
    var displays: () -> [CGRect] = { NSScreen.screens.map(\.frame) }
    var pointer: () -> CGPoint = { NSEvent.mouseLocation }
    var lock = BreakScreenLock.system
    var chime: () -> Void = { NSSound(named: "Glass")?.play() }
    /// Keeps the Mac awake; `true` also keeps the display on. While the Mac is locked only the computer stays
    /// awake, so the countdown and its sound still run.
    var keepAwake: (_ display: Bool) -> NSObjectProtocol? = { display in
        ProcessInfo.processInfo.beginActivity(
            options: display ? [.idleDisplaySleepDisabled, .userInitiated] : [.userInitiated],
            reason: display ? "The break timer is on screen." : "The break timer counts down while the Mac is locked.")
    }
    var allowSleep: (NSObjectProtocol) -> Void = { ProcessInfo.processInfo.endActivity($0) }
    var isActive: () -> Bool = { NSApp.isActive }
    var voiceOverRunning: () -> Bool = { NSWorkspace.shared.isVoiceOverEnabled }
    /// Orders windows on screen, activates Annotator and hides the pointer. Tests keep this off.
    var presents = true
    var announces = true
}

final class BreakWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// Hosts one display's break screen. Keys and clicks are handled by the controller's event monitor;
/// this view only swallows what reaches it, so nothing beeps.
final class BreakHostView: NSView {
    private(set) var host: NSHostingView<BreakScreenRoot>?

    init(frame: CGRect, controller: BreakTimerController) {
        super.init(frame: frame)
        let host = NSHostingView(rootView: BreakScreenRoot(controller: controller))
        host.frame = bounds
        host.autoresizingMask = [.width, .height]
        addSubview(host)
        self.host = host
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
        setAccessibilityElement(false)
    }

    @available(*, unavailable) required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override var acceptsFirstResponder: Bool { true }
    override func keyDown(with event: NSEvent) {}
    override func keyUp(with event: NSEvent) {}
    override func mouseMoved(with event: NSEvent) {}
    override func mouseDown(with event: NSEvent) {}
    override func rightMouseDown(with event: NSEvent) {}
    override func scrollWheel(with event: NSEvent) {}
}

struct BreakScreenRoot: View {
    @ObservedObject var controller: BreakTimerController

    var body: some View {
        BreakScreenFace(state: controller.faceState, actions: BreakFaceActions(
            removeMinute: { controller.adjust(minutes: -1) },
            togglePause: { controller.togglePause() },
            addMinute: { controller.adjust(minutes: 1) },
            end: { controller.end() },
            hoverControls: { controller.controlsHovered = $0 }))
    }
}

/// The full-screen break countdown of specification 014.
@MainActor
final class BreakTimerController: ObservableObject {
    enum LockState: Equatable { case off, arming(Int), armed, locking }
    enum EndReason: Equatable { case ended, cancelled }
    struct Badge: Equatable { let id: Int; let text: String }

    static let armingSeconds = 3
    static let jitterPoints: CGFloat = 12
    static let controlsSeconds: TimeInterval = 3
    static let lockConfirmationSeconds: TimeInterval = 5
    static let awakeAfterTimeUp: TimeInterval = 30 * 60
    /// A time up noticed later than this, after sleep, is shown without the sound.
    static let lateChimeSeconds: TimeInterval = 10
    /// `NX_SUBTYPE_AUX_CONTROL_BUTTONS`: volume, brightness and media keys.
    static let mediaKeySubtype: Int16 = 8
    /// Carbon delivers registered global shortcuts, including the break's own, as these system events.
    static let hotKeySubtypes: Set<Int16> = [6, 9]

    @Published private(set) var countdown: BreakCountdown?
    @Published private(set) var settings = BreakTimerSettings()
    @Published private(set) var now = Date()
    @Published private(set) var lockState = LockState.off
    @Published private(set) var controlsVisible = false
    @Published private(set) var rippleGeneration = 0
    @Published private(set) var ringGeneration = 0
    @Published private(set) var badge: Badge?
    @Published private(set) var closing = false
    @Published private(set) var notice: String?
    /// The Mac is locked or another user is in front. The windows are closed; the countdown goes on.
    @Published private(set) var isAway = false
    @Published var controlsHovered = false {
        didSet { if !controlsHovered, controlsVisible { scheduleControlsHide() } }
    }

    private(set) var windows: [BreakWindow] = []
    private(set) var lastEndReason: EndReason?
    /// Keyboard focus is on the controls, so they stay until the pointer moves; Space then presses the focused one.
    private(set) var controlsPinned = false
    private weak var model: WorkspaceModel?
    private let environment: BreakTimerEnvironment
    private var tick: Timer?
    private var armTimer: Timer?
    private var controlsTimer: Timer?
    private var lockTimer: Timer?
    private var badgeTimer: Timer?
    private var closeTimer: Timer?
    private var monitor: Any?
    private var observers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var lockObservers: [NSObjectProtocol] = []
    private var awake: NSObjectProtocol?
    private var awakeKeepsDisplay = false
    private var timeUpAt: Date?
    private var announcedLastMinute = false
    private var armedPointer: CGPoint?
    private var previousApp: NSRunningApplication?
    private var badges = 0

    init(model: WorkspaceModel?, environment: BreakTimerEnvironment = BreakTimerEnvironment()) {
        self.model = model
        self.environment = environment
    }

    var isRunning: Bool { countdown != nil && !closing }
    var keepsDisplayAwake: Bool { awake != nil && awakeKeepsDisplay }
    var keepsComputerAwake: Bool { awake != nil }
    var monitorsInput: Bool { monitor != nil }

    // MARK: Start and end

    func start(minutes: Int? = nil, settings requested: BreakTimerSettings) throws {
        var settings = requested
        if let minutes { settings.minutes = minutes }
        try settings.validate()
        if settings.lockWhenTouched, !environment.lock.isAvailable() { throw BreakTimerError.lockUnavailable }
        let carried = countdown != nil ? previousApp : nil
        if countdown != nil { tearDown() }
        model?.prepareForBreak()
        let start = environment.now()
        countdown = try BreakCountdown(minutes: settings.minutes, start: start)
        self.settings = settings
        now = start
        closing = false
        notice = nil
        badge = nil
        timeUpAt = nil
        lastEndReason = nil
        announcedLastMinute = settings.minutes <= 1
        ringGeneration += 1
        // Annotator has no menu bar of its own, so the menu bar's owner is the app that was in front, even when
        // the break starts from Annotator's menu, which activates Annotator.
        let workspace = NSWorkspace.shared
        let front = environment.presents ? (workspace.menuBarOwningApplication ?? workspace.frontmostApplication) : nil
        previousApp = carried ?? (front?.processIdentifier == ProcessInfo.processInfo.processIdentifier ? nil : front)
        holdAwake(display: true)
        buildWindows(animated: true)
        installMonitoring()
        if settings.lockWhenTouched {
            lockState = .arming(Self.armingSeconds)
            scheduleArming()
        } else {
            lockState = .off
            revealControls()
        }
        scheduleTick()
        if environment.presents { NSCursor.setHiddenUntilMouseMoves(true) }
        announce("Break timer started. \(BreakCountdown.spoken(seconds: settings.minutes * 60)) left.")
    }

    /// Ends the break.
    func end(_ reason: EndReason = .ended) {
        guard countdown != nil, !closing else { return }
        lastEndReason = reason
        stopInputAndTimers()
        guard environment.presents, !windows.isEmpty else { tearDown(); returnFocus(reason); return }
        closing = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.32
            context.timingFunction = Motion.exit
            for window in windows { window.animator().alphaValue = 0 }
        }
        closeTimer = Timer.scheduledTimer(withTimeInterval: 0.34, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.tearDown()
                self?.returnFocus(reason)
            }
        }
    }

    /// Closes at once without animation or focus changes, as on quit.
    func close() {
        guard countdown != nil else { return }
        lastEndReason = .ended
        tearDown()
    }

    /// The break's global shortcut: ends a break that doesn't lock, cancels one that is arming and locks one that is armed.
    func shortcutPressed() {
        switch lockState {
        case .off: end()
        case .arming: end(.cancelled)
        case .armed: lockNow()
        case .locking: break
        }
    }

    // MARK: Changing a running break

    func adjust(minutes delta: Int) {
        guard var countdown, lockState == .off, !closing, delta != 0 else { return }
        let current = environment.now()
        guard delta > 0 || !countdown.isTimeUp(at: current) else { return }
        countdown.adjust(minutes: delta, at: current)
        self.countdown = countdown
        ringGeneration += 1
        if countdown.remaining(at: current) > 60 { announcedLastMinute = false }
        badges += 1
        let id = badges
        badge = Badge(id: id, text: delta > 0 ? "+\(delta) min" : "\u{2212}\(-delta) min")
        badgeTimer?.invalidate()
        badgeTimer = Timer.scheduledTimer(withTimeInterval: 0.9, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { if self?.badge?.id == id { self?.badge = nil } }
        }
        refresh()
        let left = max(Int(countdown.remaining(at: current).rounded(.up)), 0)
        announce(left > 0 ? "\(BreakCountdown.spoken(seconds: left)) left." : "Time's up.")
        if controlsVisible { scheduleControlsHide() }
    }

    func togglePause() {
        guard var countdown, lockState == .off, !closing else { return }
        let current = environment.now()
        guard !countdown.isTimeUp(at: current) else { return }
        countdown.togglePause(at: current)
        self.countdown = countdown
        ringGeneration += 1
        refresh()
        announce(countdown.isPaused ? "Paused." : "Resumed.")
    }

    // MARK: Input

    /// Decides what one event does. Returns `nil` when the break screen used or swallowed it.
    func handle(_ event: NSEvent) -> NSEvent? {
        guard countdown != nil, !closing, !isAway else { return event }
        // Let Annotator's global shortcuts through, so the break's shortcut can cancel, lock or end it.
        if event.type == .systemDefined, Self.hotKeySubtypes.contains(event.subtype.rawValue) { return event }
        switch lockState {
        case .locking:
            return nil
        case .armed:
            switch event.type {
            case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
                let point = environment.pointer()
                if let origin = armedPointer, hypot(point.x - origin.x, point.y - origin.y) > Self.jitterPoints { lockNow() }
            case .keyDown, .keyUp, .flagsChanged, .leftMouseDown, .rightMouseDown, .otherMouseDown, .scrollWheel,
                 .magnify, .rotate, .swipe, .smartMagnify:
                lockNow()
            case .systemDefined where event.subtype.rawValue == Self.mediaKeySubtype:
                lockNow()
            default:
                break
            }
            return nil
        case .arming:
            if event.type == .keyDown, event.keyCode == 53 { end(.cancelled) }
            return nil
        case .off:
            switch event.type {
            case .keyDown:
                if event.keyCode == 48 {
                    controlsPinned = true
                    revealControls()
                    return event
                }
                return handleKey(event) ? nil : event
            case .mouseMoved, .leftMouseDown, .rightMouseDown, .otherMouseDown, .scrollWheel:
                if event.type == .mouseMoved { controlsPinned = false }
                revealControls()
                return event
            default:
                return event
            }
        }
    }

    private func handleKey(_ event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option])
        guard modifiers.isEmpty else { return false }
        let character = event.charactersIgnoringModifiers ?? ""
        switch event.keyCode {
        case 36, 76, 49 where controlsPinned: return false
        case 53, 36, 76: end(); return true
        case 49: togglePause(); return true
        case 126, 69: adjust(minutes: 1); return true
        case 125, 78: adjust(minutes: -1); return true
        default: break
        }
        switch character {
        case "+", "=": adjust(minutes: 1); return true
        case "-", "_": adjust(minutes: -1); return true
        default: return false
        }
    }

    // MARK: What the screens show

    var faceState: BreakFaceState {
        let reference = now
        guard let countdown else { return BreakFaceState.preview(settings) }
        let left = countdown.remaining(at: reference)
        let timeUp = left <= 0
        let phase: BreakFaceState.Phase = timeUp ? .timeUp : countdown.isPaused ? .paused : left <= 60 ? .lastMinute : .running
        let label = countdown.label(at: reference, countOver: settings.countOver)
        let end = countdown.endDate(at: reference)
        let endTime = settings.showEndTime && !timeUp ? Self.endTimeText(end) : nil
        let spoken: String
        if timeUp {
            let over = Int((-left).rounded(.down))
            spoken = settings.countOver && over > 0 ? "Time's up. \(BreakCountdown.spoken(seconds: over)) over." : "Time's up."
        } else {
            let words = "\(BreakCountdown.spoken(seconds: Int(left.rounded(.up)))) left"
            spoken = (countdown.isPaused ? "Paused. " : "") + words + (endTime.map { ". \($0)." } ?? ".")
        }
        return BreakFaceState(
            settings: settings, label: label, fraction: timeUp ? 1 : countdown.fraction(at: reference),
            secondsToEmpty: timeUp || countdown.isPaused ? nil : left, ringGeneration: ringGeneration,
            phase: phase, endTime: endTime, lock: lockState, controlsVisible: controlsVisible,
            rippleGeneration: rippleGeneration, badge: badge, closing: closing, notice: notice, spokenValue: spoken)
    }

    nonisolated static func endTimeText(_ date: Date) -> String {
        "Back at " + date.formatted(date: .omitted, time: .shortened)
    }

    // MARK: Ticking

    private func scheduleTick() {
        tick?.invalidate()
        tick = nil
        // Once time is up behind the lock screen nothing changes until someone signs in.
        guard let countdown, !(isAway && timeUpAt != nil) else { return }
        let delay = countdown.secondsUntilLabelChanges(at: environment.now()) + 0.01
        tick = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        tick?.tolerance = 0.02
    }

    /// Reads the clock and moves the break on. The timer calls it on each second; tests call it directly.
    func refresh() {
        guard let countdown, !closing else { return }
        let current = environment.now()
        now = current
        let left = countdown.remaining(at: current)
        if left > 0 {
            if timeUpAt != nil { timeUpAt = nil; holdAwake(display: !isAway) }
            if left <= 60, !announcedLastMinute {
                announcedLastMinute = true
                announce("One minute left.")
            }
        } else if timeUpAt == nil {
            // The real end, not when it was noticed: after sleep these can be far apart.
            timeUpAt = countdown.endDate(at: current)
            rippleGeneration += 1
            ringGeneration += 1
            if settings.chime, -left < Self.lateChimeSeconds { environment.chime() }
            announce("Time's up.")
            if isAway { releaseAwake() }
        }
        if let timeUpAt, current.timeIntervalSince(timeUpAt) >= Self.awakeAfterTimeUp { releaseAwake() }
        scheduleTick()
    }

    // MARK: Locking

    private func scheduleArming() {
        armTimer?.invalidate()
        armTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.armStep() }
        }
    }

    /// One second of arming. The timer calls it; tests call it directly.
    func armStep() {
        guard case .arming(let seconds) = lockState else { armTimer?.invalidate(); armTimer = nil; return }
        if seconds > 1 { lockState = .arming(seconds - 1); return }
        armTimer?.invalidate(); armTimer = nil
        armedPointer = environment.pointer()
        lockState = .armed
        announce("This Mac now locks if anyone touches it.")
        if environment.presents, !environment.isActive() { lockNow() }
    }

    private func lockNow() {
        guard lockState == .armed else { return }
        lockState = .locking
        do {
            try environment.lock.lock()
        } catch {
            lockFailed(error)
            return
        }
        lockTimer = Timer.scheduledTimer(withTimeInterval: Self.lockConfirmationSeconds, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.confirmLock() }
        }
    }

    /// Called when macOS reports a lock, or after the waiting time. Tests call it directly.
    func confirmLock() {
        guard lockState == .locking else { return }
        if environment.lock.isLocked() { systemLocked() } else { lockFailed(BreakTimerError.lockNotConfirmed) }
    }

    private func lockFailed(_ error: Error) {
        lockTimer?.invalidate(); lockTimer = nil
        lockState = .off
        settings.lockWhenTouched = false
        notice = error.localizedDescription
        revealControls()
        model?.report(error)
    }

    /// The Mac locked or another user came to the front. The lock screen covers the break screen, so its windows
    /// close while the countdown goes on; the sound still plays at zero (spec 014, BRK-LOCK-05).
    func systemLocked() {
        guard countdown != nil, !closing, !isAway else { return }
        isAway = true
        for timer in [armTimer, controlsTimer, lockTimer, badgeTimer] { timer?.invalidate() }
        armTimer = nil; controlsTimer = nil; lockTimer = nil; badgeTimer = nil
        lockState = .off
        armedPointer = nil
        notice = nil
        badge = nil
        controlsVisible = false
        controlsHovered = false
        controlsPinned = false
        closeWindows()
        if timeUpAt == nil { holdAwake(display: false) } else { releaseAwake() }
        refresh()
    }

    /// Someone signed in again. Show the break as it is now, without locking: whoever signed in is at the Mac.
    func systemUnlocked() {
        guard countdown != nil, !closing, isAway else { return }
        isAway = false
        ringGeneration += 1
        refresh()
        if let timeUpAt, environment.now().timeIntervalSince(timeUpAt) >= Self.awakeAfterTimeUp { releaseAwake() }
        else { holdAwake(display: true) }
        buildWindows(animated: true)
        revealControls()
        announce("The break timer is back. " + faceState.spokenValue)
    }

    // MARK: Controls

    private func revealControls() {
        guard lockState == .off, countdown != nil, !closing else { return }
        if !controlsVisible { controlsVisible = true }
        scheduleControlsHide()
    }

    private func scheduleControlsHide() {
        controlsTimer?.invalidate()
        controlsTimer = Timer.scheduledTimer(withTimeInterval: Self.controlsSeconds, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.hideControls() }
        }
    }

    /// Hides the controls unless the pointer rests on them. Tests call it directly.
    func hideControls() {
        guard !controlsHovered, !controlsPinned, !environment.voiceOverRunning() else { scheduleControlsHide(); return }
        controlsVisible = false
        if environment.presents { NSCursor.setHiddenUntilMouseMoves(true) }
    }

    // MARK: Windows

    private func buildWindows(animated: Bool) {
        closeWindows()
        var frames = environment.displays().filter { $0.width > 0 && $0.height > 0 }
        if !settings.allDisplays {
            let pointer = environment.pointer()
            frames = [frames.first { $0.contains(pointer) } ?? frames.first].compactMap { $0 }
        }
        windows = frames.map(makeWindow)
        guard environment.presents else { return }
        NSApp.activate()
        for window in windows {
            window.alphaValue = animated ? 0 : 1
            window.orderFrontRegardless()
        }
        let pointer = environment.pointer()
        let key = windows.first { $0.frame.contains(pointer) } ?? windows.first
        key?.makeKeyAndOrderFront(nil)
        if let key, let content = key.contentView as? BreakHostView { key.makeFirstResponder(content.host ?? content) }
        guard animated else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.systemReducesMotion ? MotionTiming.reduced.duration : 0.45
            context.timingFunction = Motion.fade
            for window in windows { window.animator().alphaValue = 1 }
        }
    }

    private func makeWindow(_ frame: CGRect) -> BreakWindow {
        let screen = NSScreen.screens.first { $0.frame == frame }
        let window = BreakWindow(contentRect: RegionSharingGuide.contentRect(for: frame, on: screen),
                                 styleMask: [.borderless], backing: .buffered, defer: false, screen: screen)
        window.setFrame(frame, display: false)
        window.title = "Annotator break timer"
        window.isReleasedWhenClosed = false
        window.level = .screenSaver
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        let blur = settings.background == .blur
        window.isOpaque = !blur
        window.backgroundColor = blur ? .clear : settings.palette.colors.bottom
        window.hasShadow = false
        window.hidesOnDeactivate = false
        window.acceptsMouseMovedEvents = true
        window.animationBehavior = .none
        window.appearance = NSAppearance(named: .darkAqua)
        let content = BreakHostView(frame: CGRect(origin: .zero, size: frame.size), controller: self)
        window.contentView = content
        window.initialFirstResponder = content
        window.setAccessibilityLabel("Break timer")
        return window
    }

    private func displaysChanged() {
        guard countdown != nil, !closing, !isAway else { return }
        buildWindows(animated: false)
        ringGeneration += 1
    }

    // MARK: Monitoring and cleanup

    private func installMonitoring() {
        if environment.presents {
            monitor = NSEvent.addLocalMonitorForEvents(matching: [
                .keyDown, .keyUp, .flagsChanged, .leftMouseDown, .rightMouseDown, .otherMouseDown,
                .scrollWheel, .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged,
                .magnify, .rotate, .swipe, .smartMagnify, .systemDefined
            ]) { [weak self] event in
                let consumed = MainActor.assumeIsolated { self.map { $0.handle(event) == nil } ?? false }
                return consumed ? nil : event
            }
        }
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                            object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.displaysChanged() }
        })
        observers.append(center.addObserver(forName: NSApplication.didResignActiveNotification,
                                            object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { if self?.lockState == .armed { self?.lockNow() } }
        })
        workspaceObservers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.countdown != nil else { return }
                self.ringGeneration += 1
                self.refresh()
            }
        })
        workspaceObservers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.sessionDidResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemLocked() }
        })
        workspaceObservers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.sessionDidBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemUnlocked() }
        })
        lockObservers.append(DistributedNotificationCenter.default().addObserver(
            forName: NSNotification.Name("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemLocked() }
        })
        lockObservers.append(DistributedNotificationCenter.default().addObserver(
            forName: NSNotification.Name("com.apple.screenIsUnlocked"), object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.systemUnlocked() }
        })
    }

    private func stopInputAndTimers() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers.removeAll()
        workspaceObservers.forEach(NSWorkspace.shared.notificationCenter.removeObserver)
        workspaceObservers.removeAll()
        lockObservers.forEach { DistributedNotificationCenter.default().removeObserver($0) }
        lockObservers.removeAll()
        for timer in [tick, armTimer, controlsTimer, lockTimer, badgeTimer] { timer?.invalidate() }
        tick = nil; armTimer = nil; controlsTimer = nil; lockTimer = nil; badgeTimer = nil
        controlsVisible = false
        controlsHovered = false
        controlsPinned = false
    }

    private func holdAwake(display: Bool) {
        if awake != nil, awakeKeepsDisplay == display { return }
        releaseAwake()
        awake = environment.keepAwake(display)
        awakeKeepsDisplay = display
    }

    private func releaseAwake() {
        if let awake { environment.allowSleep(awake) }
        awake = nil
        awakeKeepsDisplay = false
    }

    private func closeWindows() {
        for window in windows {
            window.orderOut(nil)
            window.contentView = nil
            window.close()
        }
        windows.removeAll()
    }

    private func tearDown() {
        stopInputAndTimers()
        closeTimer?.invalidate(); closeTimer = nil
        closeWindows()
        releaseAwake()
        countdown = nil
        closing = false
        lockState = .off
        badge = nil
        notice = nil
        armedPointer = nil
        timeUpAt = nil
        isAway = false
    }

    private func returnFocus(_ reason: EndReason) {
        defer { previousApp = nil }
        guard environment.presents else { return }
        previousApp?.activate()
    }

    private func announce(_ text: String) {
        guard environment.announces else { return }
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [
            .announcement: text, .priority: NSAccessibilityPriorityLevel.high.rawValue
        ])
    }
}
