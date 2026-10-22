import AppKit
import ApplicationServices

public enum ZoomInputFailure: Error, LocalizedError {
    case permissionRequired, unavailable, interrupted, testInputDisabled
    public var errorDescription: String? {
        switch self {
        case .permissionRequired: "To use Option-scroll over other apps, allow Annotator in macOS Settings > Privacy & Security > Accessibility."
        case .unavailable: "Option-scroll could not start. Use the zoom buttons, or try starting zoom again."
        case .interrupted: "macOS stopped listening for zoom shortcuts, so screen zoom has stopped."
        case .testInputDisabled: "Global input is disabled in automated tests unless explicitly enabled."
        }
    }
}

@MainActor
public final class ZoomGestureMonitor {
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private let scroll: (Double) -> Void
    private let exit: () -> Void
    private let failure: (Error) -> Void
    private let shouldHandleScroll: (CGEvent) -> Bool
    private let capturesEscape: Bool

    public static var hasPermission: Bool { AXIsProcessTrusted() }
    private static var inputAllowedInThisProcess: Bool {
        let environment = ProcessInfo.processInfo.environment
        return environment["XCTestConfigurationFilePath"] == nil ||
            environment["ANNOTATOR_ENABLE_GLOBAL_INPUT_TEST"] == "1"
    }
    public static func requestPermission() -> Bool {
        guard inputAllowedInThisProcess else { return false }
        return AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
    }
    public static func zoomScrollDelta(_ event: CGEvent) -> Double? {
        let modifiers = event.flags.intersection([.maskControl, .maskCommand, .maskAlternate, .maskShift])
        guard event.type == .scrollWheel, modifiers == .maskAlternate else { return nil }
        let points = event.getDoubleValueField(.scrollWheelEventPointDeltaAxis1)
        let continuous = event.getIntegerValueField(.scrollWheelEventIsContinuous) != 0
        // A trackpad event can round to zero points while retaining a line
        // delta. Treating that as a wheel notch creates a sudden zoom jump.
        let delta = points == 0 && !continuous ? event.getDoubleValueField(.scrollWheelEventDeltaAxis1) * 12 : points
        return delta.isFinite && delta != 0 ? delta : nil
    }
    public init(scroll: @escaping (Double) -> Void, exit: @escaping () -> Void, failure: @escaping (Error) -> Void,
                capturesEscape: Bool = true,
                shouldHandleScroll: @escaping (CGEvent) -> Bool = { _ in true }) {
        self.scroll = scroll; self.exit = exit; self.failure = failure
        self.capturesEscape = capturesEscape
        self.shouldHandleScroll = shouldHandleScroll
    }
    public func start() throws {
        stop()
        guard Self.inputAllowedInThisProcess else {
            throw ZoomInputFailure.testInputDisabled
        }
        guard Self.hasPermission else { throw ZoomInputFailure.permissionRequired }
        var mask = CGEventMask(1) << CGEventType.scrollWheel.rawValue
        if capturesEscape { mask |= CGEventMask(1) << CGEventType.keyDown.rawValue }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
                                         options: .defaultTap, eventsOfInterest: mask,
                                         callback: { _, type, event, context in
            guard let context else { return Unmanaged.passUnretained(event) }
            let monitor = Unmanaged<ZoomGestureMonitor>.fromOpaque(context).takeUnretainedValue()
            let consumed = MainActor.assumeIsolated { monitor.handle(type: type, event: event) }
            return consumed ? nil : Unmanaged.passUnretained(event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque()) else { throw ZoomInputFailure.unavailable }
        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            CFMachPortInvalidate(tap); throw ZoomInputFailure.unavailable
        }
        self.tap = tap; self.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
    }

    public func handle(type: CGEventType, event: CGEvent) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            stop(); failure(ZoomInputFailure.interrupted)
            return false
        }
        if type == .scrollWheel, let delta = Self.zoomScrollDelta(event), shouldHandleScroll(event) {
            scroll(delta)
            return true
        }
        if capturesEscape && type == .keyDown && event.getIntegerValueField(.keyboardEventKeycode) == 53
            && event.flags.intersection([.maskCommand, .maskControl, .maskAlternate, .maskShift]).isEmpty {
            exit()
            return true
        }
        return false
    }

    public static func eventIsOnDisplay(_ event: CGEvent, frame: CGRect, primaryScreenTop: CGFloat) -> Bool {
        let display = CGRect(x: frame.minX, y: primaryScreenTop-frame.maxY,
                             width: frame.width, height: frame.height)
        return display.contains(event.location)
    }

    public func stop() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CFMachPortInvalidate(tap) }
        source = nil; tap = nil
    }
}
