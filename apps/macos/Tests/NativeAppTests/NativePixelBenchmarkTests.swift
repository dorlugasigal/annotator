import AnnotationCore
import AppKit
import CoreVideo
import CoreImage
import CryptoKit
import PresentationRendering
import XCTest

@testable import AnnotatorApp

@MainActor
final class NativePixelBenchmarkTests: XCTestCase {
    func testMeasureSyntheticMetalPresentation() async throws {
        try XCTSkipUnless(
            ProcessInfo.processInfo.environment["ANNOTATOR_BENCHMARK_NATIVE"] == "1",
            "Native GPU timing is a separate opt-in benchmark.")
        let panel = NSPanel(
            contentRect: NSRect(x: 40, y: 40, width: 960, height: 540),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Annotator synthetic rendering benchmark"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        let view = try PixelEffectsView(renderingFrame: NSRect(x: 0, y: 0, width: 960, height: 540))
        panel.contentView = view
        panel.orderFrontRegardless()
        defer {
            view.clearFrame()
            panel.orderOut(nil)
            panel.contentView = nil
            panel.close()
        }
        view.autoResizeDrawable = false
        var latest: PixelFrameMetrics?
        var failure: Error?
        view.onFrameMetrics = { latest = $0 }
        view.onFailure = { failure = $0 }
        var generation: UInt64 = 0
        var results: [[String: Any]] = []
        for scale in [1, 2] {
            let factor = Double(scale)
            let buffer = try checkerboard(width: 1920 * scale, height: 1080 * scale)
            view.drawableSize = CGSize(width: 1920 * scale, height: 1080 * scale)
            let full = Rect(x: 0, y: 0, width: 960 * factor, height: 540 * factor)
            for scenario in ["plain", "zoom-2x", "toolbar", "zoom-2x-toolbar", "blur-4", "pixelate-4", "distort-4"] {
                let effect: Tool? =
                    scenario == "blur-4" ? .blur : scenario == "pixelate-4" ? .pixelate : scenario == "distort-4" ? .distort : nil
                let elements: [Element] =
                    effect.map { tool in
                        (0..<4).map { index in
                            Element(
                                tool: tool,
                                frame: Rect(
                                    x: (80 + Double(index % 2) * 420) * factor,
                                    y: (55 + Double(index / 2) * 250) * factor, width: 300 * factor, height: 170 * factor))
                        }
                    } ?? []
                let viewport = try Viewport(
                    output: full,
                    source: scenario.hasPrefix("zoom-2x") ? Rect(x: 240 * factor, y: 135 * factor, width: 480 * factor, height: 270 * factor) : full
                )
                let toolbar = scenario.contains("toolbar") ? PixelScreenOverlay(
                    image: CIImage(color: CIColor(red: 0.1, green: 0.2, blue: 0.8))
                        .cropped(to: CGRect(x: 0, y: 0, width: 360 * factor, height: 90 * factor)),
                    bounds: Rect(x: 60 * factor, y: 40 * factor, width: 360 * factor, height: 90 * factor),
                    region: full) : nil
                var samples: [PixelFrameMetrics] = []
                for index in 0..<80 {
                    generation += 1
                    latest = nil
                    view.present(
                        buffer, viewport: viewport, sourceSize: CGSize(width: full.width, height: full.height),
                        effects: elements, regionsOnly: effect != nil, generation: generation, screenOverlay: toolbar)
                    for _ in 0..<2000 {
                        if latest?.generation == generation || failure != nil { break }
                        try await Task.sleep(for: .milliseconds(1))
                    }
                    if let failure { throw failure }
                    let sample = try XCTUnwrap(latest, "The native Metal view did not present a requested frame.")
                    XCTAssertEqual(sample.generation, generation)
                    XCTAssertEqual(sample.textureWidth, 1920 * scale, "Measure the actual drawable, not only its requested size.")
                    XCTAssertEqual(sample.textureHeight, 1080 * scale)
                    guard sample.generation == generation, sample.textureWidth == 1920 * scale,
                        sample.textureHeight == 1080 * scale
                    else { return }
                    if index >= 20 { samples.append(sample) }
                }
                func percentile(_ values: [Double], _ p: Double) -> Double {
                    let sorted = values.sorted()
                    return sorted[Int(ceil(Double(sorted.count) * p)) - 1]
                }
                let gpu = samples.compactMap(\.gpuMilliseconds)
                var result: [String: Any] = [
                    "scenario": scenario, "warmup": 20, "samples": samples.count,
                    "drawableWidth": samples[0].textureWidth, "drawableHeight": samples[0].textureHeight,
                    "metalAllocatedBytesAfter": try XCTUnwrap(view.device).currentAllocatedSize,
                    "encodeP50MS": percentile(samples.map(\.encodeMilliseconds), 0.5),
                    "encodeP95MS": percentile(samples.map(\.encodeMilliseconds), 0.95),
                    "submitToCompletionP50MS": percentile(samples.map(\.submissionToCompletionMilliseconds), 0.5),
                    "submitToCompletionP95MS": percentile(samples.map(\.submissionToCompletionMilliseconds), 0.95),
                    "raw": samples.map { sample -> [String: Any] in
                        var row: [String: Any] = [
                            "encodeMS": sample.encodeMilliseconds,
                            "submitToCompletionMS": sample.submissionToCompletionMilliseconds,
                        ]
                        if let time = sample.gpuMilliseconds { row["gpuMS"] = time }
                        return row
                    },
                ]
                if !gpu.isEmpty {
                    result["gpuP50MS"] = percentile(gpu, 0.5)
                    result["gpuP95MS"] = percentile(gpu, 0.95)
                } else {
                    result["gpuTiming"] = "Not supplied by Metal."
                }
                results.append(result)
            }
        }
        let metadata: [String: Any] = [
            "device": try XCTUnwrap(view.device).name,
            "os": ProcessInfo.processInfo.operatingSystemVersionString,
            "configuration": "Signed Debug native test host; fixed 960x540-point window with 1080p and 4K drawables.",
            "scope": "Synthetic buffer -> production PixelEffectsView -> Metal completion. No screen capture.",
            "excluded": "WindowServer composition, physical input-to-photon, sustained FPS, battery, app-wide CPU percentage.",
            "sourceSHA256": try sourceFingerprint(),
            "results": results,
        ]
        let directory = RepositoryRoot.url.appendingPathComponent("build/performance")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("native-gpu.json"), options: .atomic)
    }

    private func sourceFingerprint() throws -> String {
        let root = RepositoryRoot.url
        var hash = SHA256()
        for name in ["PixelEffectsView.swift", "ViewportProjection.swift", "CaptureCompositor.swift"] {
            hash.update(data: Data(name.utf8))
            hash.update(data: try Data(contentsOf: root.appendingPathComponent("apps/macos/Sources/PresentationRendering/\(name)")))
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func checkerboard(width: Int, height: Int) throws -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        XCTAssertEqual(
            CVPixelBufferCreate(
                kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA,
                [kCVPixelBufferMetalCompatibilityKey: true, kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &buffer),
            kCVReturnSuccess)
        let pixels = try XCTUnwrap(buffer)
        CVPixelBufferLockBaseAddress(pixels, [])
        defer { CVPixelBufferUnlockBaseAddress(pixels, []) }
        let bytes = try XCTUnwrap(CVPixelBufferGetBaseAddress(pixels)).assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(pixels)
        for y in 0..<height {
            for x in 0..<width {
                let value: UInt8 = (x / 16 + y / 16) % 2 == 0 ? 230 : 45
                let offset = y * stride + x * 4
                bytes[offset] = value
                bytes[offset + 1] = value
                bytes[offset + 2] = value
                bytes[offset + 3] = 255
            }
        }
        return pixels
    }
}
