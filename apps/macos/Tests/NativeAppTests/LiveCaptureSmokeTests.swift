import AppKit
import CoreVideo
import XCTest
import NativePlatform

@MainActor
final class LiveCaptureSmokeTests: XCTestCase {
    func testAuthorizedAppCanCaptureAndStop() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1",
                          "Live capture acceptance is opt-in; normal CI never captures an unrelated desktop.")
        try XCTSkipUnless(DisplayCapture.hasPermission, "The app does not have Screen Recording permission.")
        try XCTSkipIf(DisplayCapture.desktopIsLocked, "Unlock the Mac to verify the actual capture stream.")
        let screen = try XCTUnwrap(NSScreen.main)
        let number = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)
        let identifier = try XCTUnwrap(Bundle.main.bundleIdentifier)
        let capture = DisplayCapture()
        let fixture = NSPanel(contentRect: NSRect(x: screen.frame.minX + 20, y: screen.frame.maxY - 70, width: 220, height: 30),
                              styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        fixture.ignoresMouseEvents = true
        fixture.hidesOnDeactivate = false
        fixture.title = "Annotator Capture Readiness Fixture"
        fixture.orderFrontRegardless()
        defer { fixture.orderOut(nil) }
        do {
            let frames = try await capture.start(displayID: number.uint32Value, bundleIdentifier: identifier)
            let dimensions = try await withThrowingTaskGroup(of: (Int, Int).self) { group in
                group.addTask {
                    var iterator = frames.makeAsyncIterator()
                    guard let frame = try await iterator.next() else { throw CaptureFailure.invalidFrame }
                    return (CVPixelBufferGetWidth(frame.pixelBuffer), CVPixelBufferGetHeight(frame.pixelBuffer))
                }
                group.addTask {
                    try await Task.sleep(for: .seconds(8))
                    throw CaptureFailure.invalidFrame
                }
                defer { group.cancelAll() }
                guard let dimensions = try await group.next() else { throw CaptureFailure.invalidFrame }
                return dimensions
            }
            try await capture.stop()
            XCTAssertGreaterThan(dimensions.0, 0)
            XCTAssertGreaterThan(dimensions.1, 0)
            XCTAssertFalse(capture.isRunning)
            print("LIVE CAPTURE: one frame received and released; no screen pixels saved.")
        } catch {
            try await capture.stop()
            throw error
        }
    }
}
