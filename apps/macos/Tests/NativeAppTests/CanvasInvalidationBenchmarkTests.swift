import AppKit
import CryptoKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class CanvasInvalidationBenchmarkTests: XCTestCase {
    func testMeasureToolSwitchInvalidation() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_BENCHMARK_INVALIDATION"] == "1",
                          "The large-scene invalidation measurement is opt-in.")
        let suite = "invalidation-benchmark-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        session.panel.orderOut(nil)
        session.canvas.appearance = NSAppearance(named: .aqua)
        session.canvas.setFrameSize(CGSize(width: 1920, height: 1080))
        session.presentation?.suspend()
        var style = Style()
        style.roughness = 6
        session.document = try CanvasDocument(elements: (0..<1000).map { index in
            var element = Element(tool: .ellipse, frame: Rect(x: Double(index % 40) * 45,
                y: Double(index / 40) * 38, width: 40, height: 30), style: style)
            element.seed = 0xA660 + UInt64(index)
            return element
        })
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1920, pixelsHigh: 1080,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 1920 * 4, bitsPerPixel: 32))
        bitmap.size = CGSize(width: 1920, height: 1080)
        func paintIfNeeded(force: Bool = false) {
            if force || session.canvas.contentNeedsRedraw {
                session.canvas.cacheDisplay(in: session.canvas.bounds, to: bitmap)
                session.canvas.needsDisplay = false
            }
        }
        model.synchronize()
        paintIfNeeded()
        func checksum() throws -> String {
            let bytes = try XCTUnwrap(bitmap.bitmapData)
            return SHA256.hash(data: Data(bytes: bytes, count: bitmap.bytesPerRow * bitmap.pixelsHigh))
                .map { String(format: "%02x", $0) }.joined()
        }
        let expected = try checksum()
        var rows: [[String: Any]] = []
        for forced in [true, false] {
            var samples: [Double] = []
            let before = session.canvas.renderPassCount
            for index in 0..<24 {
                let start = DispatchTime.now().uptimeNanoseconds
                model.choose(index.isMultiple(of: 2) ? .pen : .ellipse)
                paintIfNeeded(force: forced)
                let elapsed = Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000
                if index >= 3 { samples.append(elapsed) }
                XCTAssertEqual(try checksum(), expected)
            }
            let sorted = samples.sorted()
            rows.append([
                "mode": forced ? "forced-redraw-control" : "unchanged-scene",
                "samplesMS": samples, "p50MS": sorted[10], "p95MS": sorted[19],
                "paintCount": session.canvas.renderPassCount - before
            ])
        }
        let report: [String: Any] = [
            "scope": "Native tool selection plus offscreen DrawingCanvas paint when its production invalidation key changes. No user app or desktop pixels.",
            "control": "Force a repaint after every tool selection to model the prior invalidation behavior.",
            "configuration": "Signed Debug test host; 1920x1080 bitmap; 1000 rough ellipses.",
            "os": ProcessInfo.processInfo.operatingSystemVersionString,
            "warmup": 3, "measuredPerMode": 21, "checksum": expected, "results": rows,
            "excluded": "Toolbar layout, physical input/display latency, sustained FPS and customer workflows."
        ]
        let directory = RepositoryRoot.url.appendingPathComponent("build/performance")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("canvas-invalidation.json"), options: .atomic)
    }
}
