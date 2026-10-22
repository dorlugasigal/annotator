import Foundation

enum ZoomStartState: Equatable {
    case idle, screenPermission, gesturePermission, starting, active, failed(String)
}

@MainActor
final class ZoomStartController {
    private(set) var state = ZoomStartState.idle
    private let screenGranted: () -> Bool
    private let gesturesGranted: () -> Bool
    private let requestScreen: () -> Void
    private let requestGestures: () -> Void
    private let start: () -> Void
    private let changed: (ZoomStartState) -> Void
    private var waiting: Task<Void, Never>?
    private var startupDeadline: Task<Void, Never>?
    private let automaticChecks: Bool
    private let requiresGesturesForStart: Bool

    init(screenGranted: @escaping () -> Bool, gesturesGranted: @escaping () -> Bool,
         requestScreen: @escaping () -> Void, requestGestures: @escaping () -> Void,
         automaticChecks: Bool = true, requiresGesturesForStart: Bool = true, start: @escaping () -> Void,
         changed: @escaping (ZoomStartState) -> Void) {
        self.screenGranted = screenGranted; self.gesturesGranted = gesturesGranted
        self.requestScreen = requestScreen; self.requestGestures = requestGestures
        self.start = start; self.changed = changed; self.automaticChecks = automaticChecks
        self.requiresGesturesForStart = requiresGesturesForStart
    }

    func request() {
        if state == .starting || state == .active { return }
        advance()
        guard automaticChecks, isWaiting, waiting == nil else { return }
        waiting = Task { [weak self] in
            for _ in 0..<120 {
                do { try await Task.sleep(for: .milliseconds(750)) } catch { return }
                guard let self, self.isWaiting else { return }
                self.advance()
            }
            guard let self, self.isWaiting else { return }
            self.fail("Zoom is waiting for permission. Open Settings > Permissions to check access for this copy of Annotator. Then try your Live zoom shortcut from Settings > Toolbar & Shortcuts.")
        }
    }

    var isWaiting: Bool { state == .screenPermission || state == .gesturePermission }

    func refresh() { if isWaiting { advance() } }

    private func advance() {
        if !screenGranted() {
            if state != .screenPermission { set(.screenPermission); requestScreen() }
        } else if requiresGesturesForStart && !gesturesGranted() {
            if state != .gesturePermission { set(.gesturePermission); requestGestures() }
        } else {
            waiting?.cancel(); waiting = nil
            set(.starting)
            if automaticChecks {
                startupDeadline?.cancel()
                startupDeadline = Task { [weak self] in
                    do { try await Task.sleep(for: .seconds(10)) } catch { return }
                    guard let self, self.state == .starting else { return }
                    self.fail("Screen zoom did not appear. Stop zoom and try again. If you just allowed screen access, you may need to reopen Annotator. Stop screen sharing and save your board first: quitting removes covers.")
                }
            }
            start()
        }
    }

    func presented() {
        if state == .starting { startupDeadline?.cancel(); startupDeadline = nil; set(.active) }
    }
    func fail(_ message: String) {
        waiting?.cancel(); waiting = nil
        startupDeadline?.cancel(); startupDeadline = nil
        set(.failed(message))
    }
    func cancel() {
        waiting?.cancel(); waiting = nil
        startupDeadline?.cancel(); startupDeadline = nil
        set(.idle)
    }
    private func set(_ state: ZoomStartState) { self.state = state; changed(state) }
}
