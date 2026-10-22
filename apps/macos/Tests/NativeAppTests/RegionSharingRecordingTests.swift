import AppKit
import ScreenCaptureKit
import XCTest
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

@MainActor
final class RegionSharingRecordingTests: XCTestCase {
    func testRecordRegionChooser() async throws {
        guard ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_REGION_SHARING"] == "1" else {
            throw XCTSkip("Region sharing footage is an explicit owner-run recording.")
        }
        guard #available(macOS 15.0, *), DisplayCapture.hasPermission else {
            throw CaptureFailure.streamStopped("The recorder needs its existing Screen Recording grant.")
        }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        try await record()
    }

    @available(macOS 15.0, *)
    private func record() async throws {
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.visibleFrame.width >= 1040 && $0.visibleFrame.height >= 760 })
        let suite = "record-region-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.beginDrawing()
        XCTAssertNil(try XCTUnwrap(model.active).sourceImage)
        model.interact()
        model.concealWorkspaceSurfaces()
        let sharing = model.regionSharing
        sharing.showControls()
        let panel = try XCTUnwrap(sharing.controls)
        panel.appearance = NSAppearance(named: .darkAqua)
        panel.setContentSize(CGSize(width: 960, height: 660))
        panel.setFrameOrigin(CGPoint(x: screen.visibleFrame.midX - panel.frame.width / 2,
                                    y: screen.visibleFrame.midY - panel.frame.height / 2))
        panel.contentView?.layoutSubtreeIfNeeded()
        let stage = panel.frame.insetBy(dx: -24, dy: -24)
        XCTAssertTrue(screen.visibleFrame.contains(stage))
        let pointerPanel = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered, defer: false)
        pointerPanel.isReleasedWhenClosed = false
        pointerPanel.isOpaque = false
        pointerPanel.backgroundColor = .clear
        pointerPanel.hasShadow = false
        pointerPanel.ignoresMouseEvents = true
        pointerPanel.hidesOnDeactivate = false
        pointerPanel.level = NSWindow.Level(rawValue: panel.level.rawValue + 1)
        let pointer = DesktopDemoPointer(frame: CGRect(origin: .zero, size: screen.frame.size))
        pointerPanel.contentView = pointer
        pointerPanel.orderFrontRegardless()
        defer {
            pointerPanel.orderOut(nil); pointerPanel.contentView = nil; pointerPanel.close()
            model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(250))
        let available = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let displayID = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
        let display = try XCTUnwrap(available.displays.first { $0.displayID == displayID })
        let ids = Set([CGWindowID(panel.windowNumber), CGWindowID(pointerPanel.windowNumber)])
        let windows = available.windows.filter {
            ids.contains($0.windowID) && $0.owningApplication?.processID == ProcessInfo.processInfo.processIdentifier
        }
        XCTAssertEqual(windows.count, 2)
        guard windows.count == 2 else { throw CaptureFailure.invalidFrame }
        let scale = min(2, screen.backingScaleFactor)
        let configuration = SCStreamConfiguration()
        configuration.sourceRect = CGRect(x: stage.minX - screen.frame.minX, y: screen.frame.maxY - stage.maxY,
                                          width: stage.width, height: stage.height)
        configuration.width = Int(stage.width * scale / 2) * 2
        configuration.height = Int(stage.height * scale / 2) * 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        configuration.showsCursor = false
        let root = RepositoryRoot.url
        let folder = root.appendingPathComponent("build/region-sharing-recordings")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent("region-\(UUID()).mp4")
        let movie = try RealtimeDesktopMovie(url: file,
            filter: SCContentFilter(display: display, including: windows), configuration: configuration, codec: .hevc)
        var actions: [[String: Any]] = []
        var hand = DemoHandMotion(seed: 2026101201)
        pointer.point = pointer.convert(pointerPanel.convertPoint(fromScreen:
            CGPoint(x: panel.frame.midX + 130, y: panel.frame.maxY - 60)), from: nil)
        func sourceRect(_ frame: CGRect) -> [String: Double] {
            ["x": (frame.minX - stage.minX) * scale, "y": (stage.maxY - frame.maxY) * scale,
             "width": frame.width * scale, "height": frame.height * scale]
        }
        try await movie.start()
        do {
            try await movie.wait(0.8)
            for preset in [ShareRegionPreset.rightHalf, .centerThird, .topLeft, .leftHalf] {
                panel.contentView?.layoutSubtreeIfNeeded()
                let node = try XCTUnwrap(NativeAccessibilityNode.nodes(in: panel).first {
                    $0.accessibilityRole() == .button && $0.accessibilityLabel() == preset.title
                }, preset.title)
                let bounds = node.accessibilityFrame()
                XCTAssertTrue(panel.frame.contains(bounds), "Every used control must stay in the take.")
                let target = pointer.convert(pointerPanel.convertPoint(fromScreen: CGPoint(x: bounds.midX, y: bounds.midY)), from: nil)
                for point in hand.path(from: Point(pointer.point), to: Point(target), samples: 20, bend: 6).dropFirst() {
                    pointer.point = point.cgPoint
                    pointer.needsDisplay = true
                    pointerPanel.displayIfNeeded()
                    try await movie.wait(1.0 / 60)
                }
                pointer.pressed = true
                pointer.needsDisplay = true
                XCTAssertTrue(node.accessibilityPerformPress())
                actions.append(["control": preset.title, "time": movie.elapsed, "rect": sourceRect(bounds)])
                try await movie.wait(0.12)
                pointer.pressed = false
                pointer.needsDisplay = true
                try await movie.wait(1.05)
                XCTAssertEqual(sharing.selectedPreset, preset)
                XCTAssertEqual(sharing.region, try SharingRegion(preset: preset,
                    displayBounds: try XCTUnwrap(sharing.selectedDisplay).localBounds))
                XCTAssertTrue(model.workspaceHidden)
                XCTAssertFalse(sharing.state.isCapturing, "The chooser demo must not start desktop capture.")
            }
            try await movie.wait(0.6)
            let duration = try await movie.finish()
            let metadata: [String: Any] = [
                "path": file.path, "take": "region", "duration": duration,
                "width": configuration.width, "height": configuration.height,
                "contentRect": sourceRect(panel.frame), "recorded": "2026-10-12",
                "imageWorkspace": false, "toolbarAppearance": "dark", "ink": "native accent",
                "input": "Scripted native accessibility presses with an owned pointer; not human or computer-use acceptance.",
                "actions": actions, "recordingSource": "Real Annotator region chooser; only recorder-owned windows.",
                "preparedControls": ["Region sharing chooser", "Hidden drawing toolbar"],
                "speed": 1
            ]
            let data = try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
            try data.write(to: folder.appendingPathComponent("latest-region.json"), options: .atomic)
            print("REGION RECORDING: \(file.lastPathComponent); \(duration) seconds; no unrelated desktop or audio captured.")
        } catch {
            try await movie.cancel()
            throw error
        }
    }
}
