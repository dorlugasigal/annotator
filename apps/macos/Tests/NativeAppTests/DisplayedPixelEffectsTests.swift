import AppKit
import CoreVideo
import ScreenCaptureKit
import XCTest
import AnnotationCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class DisplayedPixelEffectsTests: XCTestCase {
    func testDrawableTextureTracksRepeatedResolutionChanges() async throws {
        let panel = NSPanel(contentRect: NSRect(x: 40, y: 40, width: 128, height: 128),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        let view = try PixelEffectsView(renderingFrame: NSRect(x: 0, y: 0, width: 128, height: 128))
        panel.contentView = view
        panel.orderFrontRegardless()
        defer { view.clearFrame(); panel.orderOut(nil); panel.contentView = nil; panel.close() }
        view.autoResizeDrawable = false
        let pixels = try fixture()
        let viewport = try Viewport(output: Rect(x: 0, y: 0, width: 128, height: 128),
                                    source: Rect(x: 0, y: 0, width: 8, height: 8))
        var latest: PixelFrameMetrics?
        var failure: Error?
        view.onFrameMetrics = { latest = $0 }
        view.onFailure = { failure = $0 }
        for (index, size) in [256, 512, 128, 256].enumerated() {
            let generation = UInt64(index + 1)
            view.drawableSize = CGSize(width: size, height: size)
            view.present(pixels, viewport: viewport, sourceSize: CGSize(width: 8, height: 8),
                         generation: generation)
            for _ in 0..<200 {
                if latest?.generation == generation || failure != nil { break }
                try await Task.sleep(for: .milliseconds(10))
            }
            if let failure { throw failure }
            let sample = try XCTUnwrap(latest)
            XCTAssertEqual(sample.generation, generation)
            XCTAssertEqual(sample.textureWidth, size)
            XCTAssertEqual(sample.textureHeight, size)
        }
    }

    func testActualDisplayedMetalSurfaceIsUprightAndMagnifiesCorrectCorner() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1",
                          "Window-capture acceptance is opt-in.")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked, "Unlock the Mac for actual displayed-pixel verification.")
        let panel = NSPanel(contentRect: NSRect(x: 100, y: 100, width: 128, height: 128),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.hasShadow = false
        panel.title = "Annotator Synthetic Metal Fixture"
        panel.hidesOnDeactivate = false
        panel.sharingType = .readOnly
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.ignoresMouseEvents = true
        panel.level = .floating
        let view = try PixelEffectsView(renderingFrame: NSRect(x: 0, y: 0, width: 128, height: 128))
        panel.contentView = view
        panel.orderFrontRegardless()
        defer { view.clearFrame(); panel.orderOut(nil); panel.contentView = nil; panel.close() }
        let pixels = try fixture()
        var presented: UInt64?
        var failure: Error?
        view.onPresented = { presented = $0 }
        view.onFailure = { failure = $0 }
        for (index, crop) in [Rect(x: 0, y: 0, width: 8, height: 8), Rect(x: 0, y: 0, width: 3, height: 3)].enumerated() {
            let generation = UInt64(index + 1)
            view.present(pixels, viewport: try Viewport(output: Rect(x: 0, y: 0, width: 128, height: 128), source: crop),
                         sourceSize: CGSize(width: 8, height: 8), generation: generation)
            for _ in 0..<200 {
                if presented == generation || failure != nil { break }
                try await Task.sleep(for: .milliseconds(10))
            }
            if let failure { throw failure }
            XCTAssertEqual(presented, generation, "A single static frame must reach the display without waiting for mouse movement.")
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            let window = try XCTUnwrap(content.windows.first { $0.windowID == CGWindowID(panel.windowNumber) })
            let configuration = SCStreamConfiguration()
            configuration.width = 128; configuration.height = 128; configuration.showsCursor = false
            let image = try await SCScreenshotManager.captureImage(
                contentFilter: SCContentFilter(desktopIndependentWindow: window), configuration: configuration)
            let bitmap = NSBitmapImageRep(cgImage: image)
            let topLeft = try XCTUnwrap(bitmap.colorAt(x: 20, y: 20)?.usingColorSpace(.sRGB))
            let bottomLeft = try XCTUnwrap(bitmap.colorAt(x: 20, y: 108)?.usingColorSpace(.sRGB))
            XCTAssertGreaterThan(topLeft.redComponent - topLeft.blueComponent, 0.7,
                                 "Displayed top-left must be red, not vertically inverted.")
            if index == 0 {
                XCTAssertGreaterThan(bottomLeft.blueComponent - bottomLeft.redComponent, 0.7)
            } else {
                XCTAssertGreaterThan(bottomLeft.redComponent - bottomLeft.blueComponent, 0.7,
                                     "Magnified top-left crop must stay red throughout.")
            }
        }
    }

    func testLiveBlurKeepsReadyPixelsWhileChoosingAndAddingSolidCover() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked, "Unlock the Mac for live blur verification.")
        let suite = "live-blur-regression-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        try session.document.add(Element(tool: .blur, frame: Rect(x: 100, y: 100, width: 160, height: 100)))
        model.synchronize()
        for _ in 0..<400 {
            if session.effectsReady || model.errorMessage != nil { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertNil(model.errorMessage)
        XCTAssertTrue(session.effectsReady)
        XCTAssertGreaterThan(try XCTUnwrap(session.presentation?.effectsOverlayLevel).rawValue, session.panel.level.rawValue)
        model.choose(.select)
        XCTAssertTrue(session.effectsReady)
        try session.document.add(Element(tool: .cover, frame: Rect(x: 130, y: 130, width: 40, height: 40)))
        model.synchronize()
        XCTAssertTrue(session.effectsReady, "Adding a cover must not black out an unchanged blur region.")
        XCTAssertGreaterThan(try XCTUnwrap(session.presentation?.selectionOverlayLevel).rawValue,
                             try XCTUnwrap(session.presentation?.effectsOverlayLevel).rawValue)
    }

    private func fixture() throws -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(kCFAllocatorDefault, 8, 8, kCVPixelFormatType_32BGRA,
            [kCVPixelBufferMetalCompatibilityKey: true, kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &buffer), kCVReturnSuccess)
        let pixels = try XCTUnwrap(buffer)
        CVPixelBufferLockBaseAddress(pixels, [])
        defer { CVPixelBufferUnlockBaseAddress(pixels, []) }
        let data = try XCTUnwrap(CVPixelBufferGetBaseAddress(pixels)).assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(pixels)
        for y in 0..<8 {
            for x in 0..<8 {
                let color: [UInt8] = y < 4 ? (x < 4 ? [0, 0, 255, 255] : [0, 255, 0, 255])
                    : (x < 4 ? [255, 0, 0, 255] : [255, 255, 255, 255])
                for channel in 0..<4 { data[y * stride + x * 4 + channel] = color[channel] }
            }
        }
        return pixels
    }
}
