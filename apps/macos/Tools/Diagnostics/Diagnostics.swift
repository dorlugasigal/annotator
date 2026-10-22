import AppKit
import AnnotationCore
import NativePlatform
import PresentationRendering
import Foundation

@main
struct Diagnostics {
    @MainActor
    static func main() throws {
        var output: [String: Any] = [
            "operatingSystem": ProcessInfo.processInfo.operatingSystemVersionString,
            "physicalMemoryBytes": ProcessInfo.processInfo.physicalMemory,
            "screenRecordingGranted": DisplayCapture.hasPermission,
            "accessibilityGranted": AXIsProcessTrusted(),
            "displayCount": NSScreen.screens.count,
            "captureStarted": false,
            "releaseReady": false
        ]
        let count = 200_000
        var document = CanvasDocument.empty()
        let stroke = Element(tool: .pen)
        let start = ContinuousClock.now
        document.begin()
        try document.add(stroke)
        for index in 0..<count {
            try document.appendSample(to: stroke.id, point: Point(Double(index % 1_920), Double(index % 1_080)))
        }
        try document.commit()
        let duration = start.duration(to: .now)
        guard document.elements[0].points.count == count else { throw CanvasError.invalidDocument("Stress stroke was truncated.") }
        output["stressSamplesRetained"] = count
        output["strokeBuildDuration"] = "\(duration)"
        output["historyBytes"] = document.historyBytes
        output["benchmarkScope"] = "Headless model construction only; not input-to-display latency."
        let data = try JSONSerialization.data(withJSONObject: output, options: [.prettyPrinted, .sortedKeys])
        print(String(decoding: data, as: UTF8.self))
    }
}
