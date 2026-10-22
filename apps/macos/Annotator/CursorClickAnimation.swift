import AppKit
import AnnotationCore
import QuartzCore

/// One click effect, driven by the display and bounded to `CursorHighlightSettings.clickDuration`.
@MainActor
final class CursorClickAnimation {
    private let ticker = MotionTicker()
    private var staticEmphasis: Task<Void, Never>?
    private(set) var startedAt: Double?
    var isRunning: Bool { ticker.isRunning || staticEmphasis != nil }
    var progress: Double? {
        CursorHighlightSettings.clickProgress(now: ProcessInfo.processInfo.systemUptime, startedAt: startedAt)
    }

    /// Calls `update` on every display frame while the click animates and once more after it ends.
    /// When `animated` is false (Reduce Motion or no click effect), the static emphasis is cleared by one delayed update.
    func start(animated: Bool, view: NSView? = nil, update: @escaping @MainActor () -> Void) {
        stop()
        startedAt = ProcessInfo.processInfo.systemUptime
        guard animated else {
            update()
            staticEmphasis = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(CursorHighlightSettings.clickDuration)) } catch { return }
                guard let self, !Task.isCancelled else { return }
                staticEmphasis = nil
                startedAt = nil
                update()
            }
            return
        }
        let began = CACurrentMediaTime()
        ticker.start(view: view) { [weak self] time in
            guard let self else { return false }
            guard time - began < CursorHighlightSettings.clickDuration, progress != nil else {
                startedAt = nil
                update()
                return false
            }
            update()
            return true
        }
    }

    func stop() {
        ticker.stop()
        staticEmphasis?.cancel()
        staticEmphasis = nil
        startedAt = nil
    }
}
