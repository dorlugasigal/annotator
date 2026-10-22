import AppKit
import ApplicationServices
import CoreMedia
import ScreenCaptureKit

public enum CaptureFailure: Error, LocalizedError, Sendable {
    case permissionRequired, desktopLocked, displayUnavailable, windowUnavailable, invalidFrame, streamStopped(String)
    public var errorDescription: String? {
        switch self {
        case .permissionRequired: "macOS Screen Recording permission is needed to read pixels for zoom, blur and capture; no video is recorded. Enable Annotator in System Settings, then relaunch it if macOS asks."
        case .desktopLocked: "Screen capture is paused while the Mac is locked. Unlock it, then resume live effects."
        case .displayUnavailable: "The selected display is no longer available for capture."
        case .windowUnavailable: "The toolbar is unavailable for capture. Show it and start region sharing again."
        case .invalidFrame: "Screen capture did not provide a usable image."
        case .streamStopped(let message): "Screen capture stopped: \(message)"
        }
    }
}

@MainActor
public final class OwnedWindowCapture {
    private var stream: SCStream?
    private var output: CaptureOutput?
    private var generation: UInt64 = 0
    public private(set) var isRunning = false

    public init() {}

    public func start(window: NSWindow) async throws -> AsyncThrowingStream<CaptureFrame, Error> {
        try await stop()
        let requested = generation
        guard DisplayCapture.hasPermission else { throw CaptureFailure.permissionRequired }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        guard NSApp.windows.contains(where: { $0 === window }), window.isVisible,
              let id = CGWindowID(exactly: window.windowNumber), id > 0 else {
            throw CaptureFailure.windowUnavailable
        }
        let available = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard generation == requested, !Task.isCancelled else { throw CancellationError() }
        guard let owned = available.windows.first(where: {
            $0.windowID == id && $0.owningApplication?.processID == ProcessInfo.processInfo.processIdentifier
        }), window.isVisible else { throw CaptureFailure.windowUnavailable }
        let width = window.frame.width * window.backingScaleFactor
        let height = window.frame.height * window.backingScaleFactor
        guard width.isFinite, height.isFinite, width >= 1, height >= 1,
              width <= 16_384, height <= 16_384, width * height <= 8_000_000 else {
            throw CaptureFailure.invalidFrame
        }
        let configuration = SCStreamConfiguration()
        configuration.width = Int(width.rounded())
        configuration.height = Int(height.rounded())
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 30)
        configuration.queueDepth = 3
        configuration.showsCursor = false
        configuration.capturesAudio = false
        configuration.ignoreShadowsSingleWindow = true
        configuration.colorSpaceName = CGColorSpace.sRGB
        if #available(macOS 14.2, *) { configuration.includeChildWindows = false }
        let pair = AsyncThrowingStream<CaptureFrame, Error>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let output = CaptureOutput(frames: pair.continuation)
        let stream = SCStream(filter: SCContentFilter(desktopIndependentWindow: owned),
                              configuration: configuration, delegate: output)
        try stream.addStreamOutput(output, type: .screen,
            sampleHandlerQueue: DispatchQueue(label: "com.dorlugasigal.annotator.toolbar-capture", qos: .userInteractive))
        self.stream = stream; self.output = output
        do {
            try await stream.startCapture()
            guard generation == requested, !Task.isCancelled else {
                try await stream.stopCapture()
                throw CancellationError()
            }
            isRunning = true
        } catch {
            if generation == requested { self.stream = nil; self.output = nil }
            pair.continuation.finish(throwing: error)
            throw error
        }
        return pair.stream
    }

    public func stop() async throws {
        generation &+= 1
        let previous = stream
        let running = isRunning
        stream = nil; isRunning = false
        output?.frames.finish(); output = nil
        guard let previous, running else { return }
        do { try await previous.stopCapture() }
        catch {
            let failure = error as NSError
            guard failure.domain == SCStreamErrorDomain,
                  failure.code == SCStreamError.Code.attemptToStopStreamState.rawValue else { throw error }
        }
    }
}

// ScreenCaptureKit owns these immutable sample buffers; retaining a frame
// extends its backing surface lifetime until the consumer finishes.
public struct CaptureFrame: @unchecked Sendable {
    public let pixelBuffer: CVPixelBuffer
    public let presentationTime: CMTime
}

private final class CaptureOutput: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    let frames: AsyncThrowingStream<CaptureFrame, Error>.Continuation
    init(frames: AsyncThrowingStream<CaptureFrame, Error>.Continuation) { self.frames = frames }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let status = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: status) == .complete,
              let image = sampleBuffer.imageBuffer else { return }
        frames.yield(CaptureFrame(pixelBuffer: image, presentationTime: sampleBuffer.presentationTimeStamp))
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        frames.finish(throwing: CaptureFailure.streamStopped(error.localizedDescription))
    }
}

@MainActor
public final class DisplayCapture {
    #if DEBUG
    private static var controlledWindows: [CGWindowID: pid_t]?

    public static func useControlledWindowsForTesting(
        _ windows: Set<CGWindowID>?, processID: pid_t = ProcessInfo.processInfo.processIdentifier
    ) {
        precondition(ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil)
        precondition(windows == nil || windows?.isEmpty == false)
        precondition(processID > 0)
        controlledWindows = windows.map { Dictionary(uniqueKeysWithValues: $0.map { ($0, processID) }) }
    }
    #endif
    private var stream: SCStream?
    private var output: CaptureOutput?
    private var generation: UInt64 = 0
    public private(set) var isRunning = false

    public init() {}
    public static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }
    public static var desktopIsLocked: Bool {
        (CGSessionCopyCurrentDictionary() as? [String: Any])?["CGSSessionScreenIsLocked"] as? Bool == true
    }
    public static func requestPermission() -> Bool { CGRequestScreenCaptureAccess() }

    public func start(displayID: CGDirectDisplayID, bundleIdentifier: String) async throws -> AsyncThrowingStream<CaptureFrame, Error> {
        #if DEBUG
        let requestedControlledWindows = Self.controlledWindows
        #endif
        try await stop()
        guard !Self.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        let requestedGeneration = generation
        guard Self.hasPermission else { throw CaptureFailure.permissionRequired }
        let available = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard generation == requestedGeneration, !Task.isCancelled else { throw CancellationError() }
        guard let display = available.displays.first(where: { $0.displayID == displayID }) else {
            throw CaptureFailure.displayUnavailable
        }
        let ownApps = available.applications.filter {
            $0.processID == ProcessInfo.processInfo.processIdentifier || $0.bundleIdentifier == bundleIdentifier
        }
        guard !ownApps.isEmpty else {
            throw CaptureFailure.streamStopped("This app could not be excluded from capture. No capture was started.")
        }
        var filter = SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])
        #if DEBUG
        if let ids = requestedControlledWindows {
            let windows = available.windows.filter {
                guard let owner = ids[$0.windowID] else { return false }
                return owner == $0.owningApplication?.processID
            }
            guard Set(windows.map(\.windowID)) == Set(ids.keys) else {
                throw CaptureFailure.streamStopped("A controlled test window is missing. No desktop fallback was used.")
            }
            filter = SCContentFilter(display: display, including: windows)
        }
        #endif
        let configuration = SCStreamConfiguration()
        guard let screen = NSScreen.screens.first(where: {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == displayID
        }) else { throw CaptureFailure.displayUnavailable }
        let width = screen.frame.width * screen.backingScaleFactor
        let height = screen.frame.height * screen.backingScaleFactor
        guard width.isFinite, height.isFinite, width > 0, height > 0,
              width * height <= 40_000_000 else {
            throw CaptureFailure.streamStopped("The display exceeds the supported screen-pixel budget.")
        }
        configuration.width = Int(width.rounded())
        configuration.height = Int(height.rounded())
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(60, screen.maximumFramesPerSecond)))
        configuration.queueDepth = 3
        configuration.showsCursor = false
        configuration.capturesAudio = false
        let pair = AsyncThrowingStream<CaptureFrame, Error>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let output = CaptureOutput(frames: pair.continuation)
        let stream = SCStream(filter: filter, configuration: configuration, delegate: output)
        try stream.addStreamOutput(output, type: .screen,
                                   sampleHandlerQueue: DispatchQueue(label: "com.dorlugasigal.annotator.capture", qos: .userInteractive))
        self.stream = stream; self.output = output
        do {
            try await stream.startCapture()
            guard generation == requestedGeneration, !Task.isCancelled else {
                try await stream.stopCapture()
                throw CancellationError()
            }
            isRunning = true
        }
        catch {
            if generation == requestedGeneration { self.stream = nil; self.output = nil }
            pair.continuation.finish(throwing: error)
            throw error
        }
        return pair.stream
    }

    public func stop() async throws {
        generation &+= 1
        let previous = stream
        let wasRunning = isRunning
        stream = nil; isRunning = false
        output?.frames.finish()
        output = nil
        // A start still in progress owns its eventual stop. ScreenCaptureKit
        // can also stop a running stream before this cleanup reaches it.
        guard let previous, wasRunning else { return }
        do {
            try await previous.stopCapture()
        } catch {
            let failure = error as NSError
            guard failure.domain == SCStreamErrorDomain,
                  failure.code == SCStreamError.Code.attemptToStopStreamState.rawValue else {
                throw error
            }
        }
    }
}
