import AppKit
import AVFoundation
import ScreenCaptureKit

@available(macOS 15.0, *)
private final class DesktopMovieEvents: NSObject, SCRecordingOutputDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var started: Double?
    private var finished = false
    private var failure: Error?

    func state() throws -> (start: Double?, finished: Bool) {
        lock.lock()
        defer { lock.unlock() }
        if let failure { throw failure }
        return (started, finished)
    }
    func recordingOutputDidStartRecording(_ recordingOutput: SCRecordingOutput) {
        lock.lock(); started = ProcessInfo.processInfo.systemUptime; lock.unlock()
    }
    func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        lock.lock(); finished = true; lock.unlock()
    }
    func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        lock.lock(); failure = error; lock.unlock()
    }
}

@available(macOS 15.0, *)
private final class DesktopMovieFrameSink: NSObject, SCStreamOutput, @unchecked Sendable {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        // SCRecordingOutput encodes the movie; this sink drains the raw screen callbacks.
    }
}

@available(macOS 15.0, *)
@MainActor
final class RealtimeDesktopMovie {
    private let stream: SCStream
    private let events = DesktopMovieEvents()
    private let output: SCRecordingOutput
    private let frameSink = DesktopMovieFrameSink()
    private let url: URL
    private var startedAt = 0.0
    var elapsed: Double { max(0, ProcessInfo.processInfo.systemUptime - startedAt) }

    init(url: URL, filter: SCContentFilter, configuration: SCStreamConfiguration,
         codec: AVVideoCodecType = .h264) throws {
        self.url = url
        let settings = SCRecordingOutputConfiguration()
        settings.outputURL = url
        guard settings.availableVideoCodecTypes.contains(codec) else {
            throw CocoaError(.coderInvalidValue, userInfo: [NSLocalizedDescriptionKey: "The requested recording codec is unavailable."])
        }
        settings.videoCodecType = codec
        settings.outputFileType = .mp4
        output = SCRecordingOutput(configuration: settings, delegate: events)
        configuration.capturesAudio = false
        configuration.captureMicrophone = false
        stream = SCStream(filter: filter, configuration: configuration, delegate: nil)
        try stream.addStreamOutput(frameSink, type: .screen,
                                   sampleHandlerQueue: DispatchQueue(label: "annotator.recording.frames"))
        try stream.addRecordingOutput(output)
    }
    func start() async throws {
        try await stream.startCapture()
        let deadline = ProcessInfo.processInfo.systemUptime + 8
        while ProcessInfo.processInfo.systemUptime < deadline {
            if let start = try events.state().start { startedAt = start; return }
            try await Task.sleep(for: .milliseconds(20))
        }
        try await stream.stopCapture()
        throw CocoaError(.fileWriteUnknown, userInfo: [NSLocalizedDescriptionKey:"The owned desktop recording did not start."])
    }
    func update(filter: SCContentFilter) async throws { try await stream.updateContentFilter(filter) }
    func wait(_ seconds: Double) async throws {
        _ = try events.state()
        try await Task.sleep(for: .seconds(seconds))
        _ = try events.state()
    }
    func finish() async throws -> Double {
        try await stream.stopCapture()
        let deadline = ProcessInfo.processInfo.systemUptime + 8
        while ProcessInfo.processInfo.systemUptime < deadline {
            if try events.state().finished {
                return try await AVURLAsset(url:url).load(.duration).seconds
            }
            try await Task.sleep(for:.milliseconds(20))
        }
        throw CocoaError(.fileWriteUnknown, userInfo:[NSLocalizedDescriptionKey:"The owned desktop recording did not finish."])
    }
    func cancel() async throws { try await stream.stopCapture() }
}
