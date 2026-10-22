import AppKit
import QuartzCore
import ScreenCaptureKit
import SwiftUI
import XCTest
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

@MainActor
final class MotionReviewFramesTests: XCTestCase {
    func testCaptureIntermediateNativeMotionFrames() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_CAPTURE_MOTION_REVIEW"] == "1",
                          "Intermediate motion capture is a separate visual check.")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked)
        let suite = "motion-review-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setQuickPreferencesExpanded(false)
        let host = NSHostingView(rootView: ToolbarView(model: model).environment(\.colorScheme, .dark))
        let panel = NSPanel(contentRect: CGRect(x: 80, y: 80, width: 924, height: 72),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Annotator owned motion review"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.isOpaque = true
        panel.backgroundColor = NSColor(calibratedWhite: 0.12, alpha: 1)
        panel.sharingType = .readOnly
        panel.contentView = host
        panel.setContentSize(host.fittingSize)
        panel.orderFrontRegardless()
        defer {
            host.layer?.removeAllAnimations()
            panel.orderOut(nil)
            panel.contentView = nil
            panel.close()
            model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        try await Task.sleep(for: .milliseconds(120))
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let source = try XCTUnwrap(content.windows.first {
            $0.windowID == CGWindowID(panel.windowNumber)
                && $0.owningApplication?.processID == ProcessInfo.processInfo.processIdentifier
        })
        let directory = RepositoryRoot.url.appendingPathComponent("build/motion-review")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let configuration = SCStreamConfiguration()
        configuration.width = Int(panel.frame.width * 2)
        configuration.height = Int(panel.frame.height * 2)
        configuration.showsCursor = false
        host.wantsLayer = true
        let layer = try XCTUnwrap(host.layer)
        for style in [VisibilityAnimationStyle.fold, .rise] {
            for appearing in [true, false] {
                let duration = WorkspaceLayerMotion.duration(style, appearing: appearing) * 4
                let motion = WorkspaceLayerMotion(layer: layer, style: style, appearing: appearing,
                                                  flipped: host.isFlipped, duration: duration)
                CATransaction.flush()
                let started = ContinuousClock.now
                for progress in [0.1, 0.35, 0.65, 1.0] {
                    try await Task.sleep(until: started + .seconds(duration * progress), clock: .continuous)
                    let image = try await SCScreenshotManager.captureImage(
                        contentFilter: SCContentFilter(desktopIndependentWindow: source), configuration: configuration)
                    let bitmap = NSBitmapImageRep(cgImage: image)
                    let name = "\(style.rawValue)-\(appearing ? "show" : "hide")-\(Int(progress * 100)).png"
                    try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                        .write(to: directory.appendingPathComponent(name))
                }
                motion.cancel()
            }
        }
        try Data("Toolbar layer motion sampled at 4x duration. Window movement is covered by ToolbarSlideMotionTests; desktop-independent window capture cannot prove that movement.\n".utf8)
            .write(to: directory.appendingPathComponent("capture-scope.txt"))
    }
}
