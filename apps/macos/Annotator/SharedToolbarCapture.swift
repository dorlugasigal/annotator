import AppKit
import CoreVideo
import NativePlatform

@MainActor
final class SharedToolbarCapture {
    struct Frame {
        let pixels: CVPixelBuffer
        let screenBounds: CGRect
        let opacity: Double
    }
    private struct Key: Equatable {
        let id: Int
        let size: CGSize
        let scale: CGFloat
    }
    private weak var window: NSWindow?
    private var observers: [NSObjectProtocol] = []
    private var capture: OwnedWindowCapture?
    private var reader: Task<Void, Never>?
    private var epoch: UInt64 = 0
    private var key: Key?
    private var displayFrame = CGRect.zero
    private var region = CGRect.zero
    private var enabled = false
    private(set) var frame: Frame?
    var isWaitingForFrame: Bool { key != nil && frame == nil }
    var isCapturing: Bool { key != nil }
    var onChanged: (() -> Void)?
    var onFailure: ((Error) -> Void)?

    func update(window: NSWindow?, displayFrame: CGRect, region: CGRect, enabled: Bool) {
        self.displayFrame = displayFrame
        self.region = region
        self.enabled = enabled
        if self.window !== window {
            clearObservers()
            self.window = window
            if let window {
                for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification,
                             NSWindow.didChangeOcclusionStateNotification, NSWindow.didChangeBackingPropertiesNotification,
                             NSWindow.willCloseNotification] {
                    observers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) {
                        [weak self] notification in
                        let closing = notification.name == NSWindow.willCloseNotification
                        MainActor.assumeIsolated {
                            if closing {
                                self?.enabled = false
                                self?.refresh()
                                // willClose fires before the window becomes invisible.
                                Task { @MainActor [weak self] in self?.onChanged?() }
                                return
                            }
                            self?.refresh()
                            self?.onChanged?()
                        }
                    })
                }
            }
        }
        refresh()
    }

    private func refresh() {
        guard enabled, let window, window.isVisible, window.alphaValue > 0,
              window.frame.intersects(displayFrame), window.frame.intersects(region) else {
            stopStream()
            return
        }
        let desired = Key(id: window.windowNumber, size: window.frame.size, scale: window.backingScaleFactor)
        if key == desired {
            if let frame {
                self.frame = Frame(pixels: frame.pixels, screenBounds: window.frame, opacity: window.alphaValue)
            }
            return
        }
        stopStream()
        key = desired
        let requested = epoch
        let capture = OwnedWindowCapture()
        self.capture = capture
        reader = Task { [weak self, weak window, capture] in
            guard let window else { return }
            do {
                let frames = try await capture.start(window: window)
                for try await pixels in frames {
                    guard let self, epoch == requested, key == desired, !Task.isCancelled else { return }
                    frame = Frame(pixels: pixels.pixelBuffer, screenBounds: window.frame, opacity: window.alphaValue)
                    onChanged?()
                }
                if let self, epoch == requested, key == desired, !Task.isCancelled {
                    onFailure?(CaptureFailure.invalidFrame)
                }
            } catch {
                if let self, epoch == requested, key == desired, !Task.isCancelled { onFailure?(error) }
            }
        }
    }

    private func stopStream() {
        guard key != nil || capture != nil || frame != nil else { return }
        epoch &+= 1
        reader?.cancel(); reader = nil
        key = nil; frame = nil
        if let capture {
            self.capture = nil
            let failure = onFailure
            Task { [capture, failure] in
                do { try await capture.stop() } catch { failure?(error) }
            }
        }
    }

    func stop() {
        enabled = false
        stopStream()
        clearObservers()
        window = nil
    }

    private func clearObservers() {
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
    }
}
