import AppKit
import ScreenCaptureKit
import XCTest
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

/// Owner-run creator recordings for the website's Zoom and tool-library demos. Like the reel takes, they record
/// the live desktop overlay on a HiDPI virtual display: the real toolbar, tool library and screen zoom, with a
/// scripted pointer. Test-owned windows only; the sample apps are fictional images. Normal test runs skip this.
@MainActor
final class WebsiteDemoRecordingTests: XCTestCase {
    func testRecordWebsiteDemoTakes() async throws {
        let env = ProcessInfo.processInfo.environment
        guard env["ANNOTATOR_RECORD_WEBSITE_DEMO"] == "1" else {
            throw XCTSkip("The website demo recording is an explicit owner-run task.")
        }
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        guard DisplayCapture.hasPermission else {
            throw CaptureFailure.streamStopped("The recorder needs its existing Screen Recording grant.")
        }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        // Record on a separate (virtual) display so the owner's main display and mouse stay untouched.
        let wanted = env["ANNOTATOR_WEBSITE_DEMO_SCREEN"] ?? "DeskPad"
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.localizedName.localizedCaseInsensitiveContains(wanted) },
                                   "Open the \(wanted) display before recording.")
        guard screen.backingScaleFactor == 2 else {
            throw CaptureFailure.streamStopped("Switch the \(wanted) display to a HiDPI mode before recording.")
        }
        let root = RepositoryRoot.url
        let all = ["zoom", "library"]
        let takes = env["ANNOTATOR_WEBSITE_DEMO_TAKE"].map { [$0] } ?? all
        guard takes.allSatisfy({ all.contains($0) }) else { throw CocoaError(.coderInvalidValue) }
        ReelPointerHook.install()
        defer { ReelPointerHook.uninstall() }
        for take in takes { try await record(take, on: screen, root: root) }
    }

    @available(macOS 15.0, *)
    private func record(_ take: String, on screen: NSScreen, root: URL) async throws {
        let frame = screen.frame
        let fixtures = root.appendingPathComponent("videos/annotator-reel/fixtures")
        let fixtureName = take == "library" ? "design" : "dashboard"
        let fixture = try DemoFixture.load(fixtures.appendingPathComponent("targets.json"), name: fixtureName)
        let image = try XCTUnwrap(NSImage(contentsOf: fixtures.appendingPathComponent("\(fixtureName)@2x.png")))
        image.size = CGSize(width: fixture.width, height: fixture.height)
        let windowTop: CGFloat = 24
        let windowRect = CGRect(x: frame.minX + (frame.width - fixture.width) / 2,
                                y: frame.maxY - windowTop - fixture.height, width: fixture.width, height: fixture.height)
        let toolbarTop = windowRect.minY - 14

        let suite = "website-\(take)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setToolbarDockEdge(.bottom)
        model.setPropertiesShown(true)
        model.setPropertiesShown(true)
        let previousAppearance = NSApp.appearance
        NSApp.appearance = NSAppearance(named: .darkAqua)
        defer { NSApp.appearance = previousAppearance }
        ReelPointerHook.location = CGPoint(x: windowRect.midX, y: windowRect.midY)
        let app = AppDelegate(model: model)
        app.resumeDrawing()
        defer {
            model.stopZoom()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
            DisplayCapture.useControlledWindowsForTesting(nil)
        }
        try await Task.sleep(for: .milliseconds(200))
        let session = try XCTUnwrap(model.desktopSessions.first { $0.panel.screen == screen },
                                    "The app must open an overlay on the recording display.")
        if model.active !== session { XCTAssertTrue(model.activateDrawingSession(session)) }
        XCTAssertTrue(model.active === session)
        XCTAssertNil(session.sourceImage)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        toolbar.appearance = NSAppearance(named: .darkAqua)

        let backdrop = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        backdrop.isReleasedWhenClosed = false
        backdrop.isOpaque = true
        backdrop.backgroundColor = NSColor(srgbRed: 11 / 255, green: 16 / 255, blue: 26 / 255, alpha: 1)
        backdrop.hasShadow = false
        backdrop.ignoresMouseEvents = true
        backdrop.level = .normal
        backdrop.orderFrontRegardless()
        let sample = NSPanel(contentRect: windowRect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        sample.isReleasedWhenClosed = false
        sample.title = "Website demo sample app"
        sample.isOpaque = false
        sample.backgroundColor = .clear
        sample.hasShadow = true
        sample.ignoresMouseEvents = true
        sample.level = NSWindow.Level(rawValue: NSWindow.Level.normal.rawValue + 1)
        let imageView = NSImageView(frame: CGRect(origin: .zero, size: windowRect.size))
        imageView.image = image
        imageView.imageScaling = .scaleAxesIndependently
        imageView.wantsLayer = true
        imageView.layer?.cornerRadius = 12
        imageView.layer?.masksToBounds = true
        sample.contentView = imageView
        sample.orderFrontRegardless()
        let pointerPanel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        pointerPanel.isReleasedWhenClosed = false
        pointerPanel.isOpaque = false
        pointerPanel.backgroundColor = .clear
        pointerPanel.hasShadow = false
        pointerPanel.ignoresMouseEvents = true
        pointerPanel.level = .screenSaver
        let pointerView = DemoPointerView(frame: CGRect(origin: .zero, size: frame.size))
        pointerPanel.contentView = pointerView
        pointerPanel.orderFrontRegardless()
        defer {
            pointerPanel.orderOut(nil); pointerPanel.contentView = nil; pointerPanel.close()
            sample.orderOut(nil); sample.contentView = nil; sample.close()
            backdrop.orderOut(nil); backdrop.close()
        }
        // Screen zoom magnifies only the sample app and its backdrop, never the owner's other windows.
        let captureIDs = Set(try [backdrop, sample].map { try XCTUnwrap(CGWindowID(exactly: $0.windowNumber)) })
        DisplayCapture.useControlledWindowsForTesting(captureIDs)
        NativeAccessibilityNode.enable()

        var toolbarMisses: [[String: Double]] = []
        func layout() throws {
            session.panel.ignoresMouseEvents = true
            // Only the recorder's own events may move previews; the owner's real mouse stays out of the take.
            session.panel.acceptsMouseMovedEvents = false
            session.canvas.setDrawingHUDVisible(false)
            for other in model.desktopSessions where other !== session {
                other.panel.ignoresMouseEvents = true
                if other.panel.isVisible { other.panel.orderOut(nil) }
            }
            toolbar.ignoresMouseEvents = true
            toolbar.setFrameOrigin(CGPoint(x: frame.midX - toolbar.frame.width / 2, y: toolbarTop - toolbar.frame.height))
            if !frame.insetBy(dx: 1, dy: 1).contains(toolbar.frame) {
                toolbarMisses.append(["x": toolbar.frame.minX, "y": toolbar.frame.minY,
                                      "width": toolbar.frame.width, "height": toolbar.frame.height])
            }
            for window in NSApp.windows where window.isVisible && window !== toolbar && window !== sample &&
                window !== backdrop && window !== session.panel && window !== pointerPanel &&
                window.frame.width < frame.width && window.frame.height < frame.height {
                let origin = CGPoint(
                    x: min(max(window.frame.minX, frame.minX + 16), frame.maxX - window.frame.width - 16),
                    y: min(max(window.frame.minY, toolbar.frame.maxY + 10), frame.maxY - window.frame.height - 16))
                window.setFrameOrigin(origin)
                window.ignoresMouseEvents = true
                window.appearance = NSAppearance(named: .darkAqua)
            }
        }
        func nodes() -> [NativeAccessibilityNode] {
            NSApp.windows.filter(\.isVisible).compactMap(\.contentView).flatMap { NativeAccessibilityNode.nodes(in: $0) }
        }
        func libraryWindows() -> [NSWindow] {
            NSApp.windows.filter { window in
                window.isVisible && window !== toolbar && window.contentView.map {
                    NativeAccessibilityNode.nodes(in: $0).contains { $0.accessibilityIdentifier() == "tool-library-scroll" }
                } == true
            }
        }
        func control(_ name: String, in roots: [NativeAccessibilityNode]) -> NativeAccessibilityNode? {
            roots.first {
                [.button, .radioButton, .checkBox].contains($0.accessibilityRole()) &&
                    ($0.accessibilityLabel() == name || $0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true)
            }
        }
        var prepared: [String] = []
        func prepare(_ name: String) async throws {
            var found = control(name, in: nodes())
            for _ in 0..<30 where found == nil {
                try await Task.sleep(for: .milliseconds(50))
                try layout()
                found = control(name, in: nodes())
            }
            let node = try XCTUnwrap(found, "Missing preparation control: \(name)")
            _ = node.accessibilityPerformPress()
            prepared.append(name)
            try await Task.sleep(for: .milliseconds(140))
            try layout()
        }

        // Unrecorded preparation: leave Select active and warm the library's Focus section (its first render
        // blocks for seconds). Screen zoom then starts at the app's default magnification.
        model.setShortcutHints(false)
        model.spotlight = false
        model.cursorHalo = false
        try await prepare("Select")
        try await prepare("Tool library")
        for _ in 0..<40 where libraryWindows().isEmpty { try await Task.sleep(for: .milliseconds(50)) }
        let warmLibrary = try XCTUnwrap(libraryWindows().first, "The tool library must open.")
        let focusTab = try XCTUnwrap(control("Focus", in: NativeAccessibilityNode.nodes(in: try XCTUnwrap(warmLibrary.contentView))))
        _ = focusTab.accessibilityPerformPress()
        try await Task.sleep(for: .milliseconds(400))
        warmLibrary.standardWindowButton(.closeButton)?.performClick(nil)
        try await Task.sleep(for: .milliseconds(300))
        XCTAssertTrue(libraryWindows().isEmpty, "Close the warmed library before recording.")
        if take == "library" {
            // Styles are per tool: give the Database shape blue ink before the take, then return to Select.
            try await prepare("Tool library")
            for _ in 0..<40 where libraryWindows().isEmpty { try await Task.sleep(for: .milliseconds(50)) }
            for name in ["Shapes", "Database"] { try await prepare(name) }
            for _ in 0..<40 where !libraryWindows().isEmpty { try await Task.sleep(for: .milliseconds(50)) }
            XCTAssertEqual(model.effectiveTool, .databaseStack)
            for name in ["Stroke color", "Blue", "Done", "Select"] { try await prepare(name) }
        }
        try layout()
        try await Task.sleep(for: .milliseconds(250))

        let shareable = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let displayID = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
        let display = try XCTUnwrap(shareable.displays.first { $0.displayID == displayID })
        let config = SCStreamConfiguration()
        config.width = Int(frame.width * screen.backingScaleFactor / 2) * 2
        config.height = Int(frame.height * screen.backingScaleFactor / 2) * 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.showsCursor = false
        config.captureResolution = .best
        config.colorSpaceName = CGColorSpace.sRGB
        let ownedPID = ProcessInfo.processInfo.processIdentifier
        let ownApp = try XCTUnwrap(shareable.applications.first { $0.processID == ownedPID })
        let filter = SCContentFilter(display: display, including: [ownApp], exceptingWindows: [])
        let folder = root.appendingPathComponent("build/website-demo-takes")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let output = folder.appendingPathComponent("\(take)-\(UUID()).mp4")
        let movie = try RealtimeDesktopMovie(url: output, filter: filter, configuration: config, codec: .hevc)
        var hand = DemoHandMotion(seed: take == "zoom" ? 202609271 : 202609272)
        var actions: [[String: Any]] = []
        var samples: [[String: Double]] = []
        var zoomSamples: [[String: Double]] = []
        let scale = Double(config.width) / frame.width
        func sourceRect(_ rect: CGRect) -> [String: Double] {
            ["x": (rect.minX - frame.minX) * scale, "y": (frame.maxY - rect.maxY) * scale,
             "width": rect.width * scale, "height": rect.height * scale]
        }
        var cursor = CGPoint(x: windowRect.midX, y: windowRect.midY)
        var pressed: Bool {
            get { pointerView.pressed }
            set { pointerView.pressed = newValue; pointerView.needsDisplay = true }
        }
        func appCursor(at global: CGPoint) -> NSCursor {
            let overUI = NSApp.windows.contains { window in
                window.isVisible && window !== sample && window !== backdrop && window !== pointerPanel &&
                    window !== session.panel && window.frame.width < frame.width - 1 && window.frame.contains(global)
            }
            guard !overUI, model.mode == .draw, session.panel.frame.contains(global) else { return .arrow }
            let local = session.canvas.convert(session.panel.convertPoint(fromScreen: global), from: nil)
            return session.canvas.hoverCursor(at: Point(local))
        }
        func warp(_ point: CGPoint) {
            cursor = point
            ReelPointerHook.location = point
            pointerView.point = pointerView.convert(pointerPanel.convertPoint(fromScreen: point), from: nil)
            pointerView.cursor = appCursor(at: point)
            pointerView.needsDisplay = true
            model.notePointerEvent(.mouseMoved)
            // The zoomed view follows the pointer: the recorder's pointer moves arrive without real mouse events,
            // so ask the live presentation to render again, as a mouse-moved event would.
            if model.screenZoomEnabled { session.presentation?.refresh() }
        }
        func pause(_ seconds: Double) async throws {
            try layout()
            toolbar.displayIfNeeded()
            session.panel.displayIfNeeded()
            pointerPanel.displayIfNeeded()
            try await movie.wait(seconds)
            samples.append(["time": movie.elapsed, "x": (cursor.x - frame.minX) * scale,
                            "y": (frame.maxY - cursor.y) * scale, "pressed": pressed ? 1 : 0])
            if model.screenZoomEnabled { zoomSamples.append(["time": movie.elapsed, "zoom": model.magnification]) }
        }
        func design(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: windowRect.minX + x, y: windowRect.maxY - y) }
        func travel(_ target: CGPoint, seconds: Double = 0.32, bend: Double = 24) async throws {
            let frames = max(8, Int(hand.duration(seconds) * 60))
            for step in hand.path(from: Point(cursor), to: Point(target), samples: frames, bend: bend).dropFirst() {
                warp(step.cgPoint)
                try await pause(1.0 / 60)
            }
        }
        func click(_ name: String, inLibrary: Bool = false, hover: Double = 0, settle: Double = 0.08) async throws {
            let candidates = inLibrary ? libraryWindows().compactMap(\.contentView).flatMap {
                NativeAccessibilityNode.nodes(in: $0)
            } : nodes()
            let node = try XCTUnwrap(control(name, in: candidates), "Missing real native control: \(name)")
            let bounds = node.accessibilityFrame()
            XCTAssertTrue(frame.contains(bounds), "Control \(name) must be on screen.")
            try await travel(CGPoint(x: bounds.midX, y: bounds.midY), seconds: 0.3)
            if hover > 0 { try await pause(hover) }
            pressed = true
            model.notePointerEvent(.leftMouseDown)
            try await pause(0.06)
            let done = node.accessibilityPerformPress()
            if node.accessibilityRole() == .button { XCTAssertTrue(done, "\(name) must respond to its press.") }
            actions.append(["control": name, "time": movie.elapsed, "rect": sourceRect(bounds)])
            try await pause(0.07)
            pressed = false
            try await pause(settle)
        }

        func canvasPoint(_ global: CGPoint) -> Point {
            Point(session.canvas.convert(session.panel.convertPoint(fromScreen: global), from: nil))
        }
        func screenPoint(_ value: Point) -> CGPoint {
            session.panel.convertPoint(toScreen: session.canvas.convert(value.cgPoint, to: nil))
        }
        func mouse(_ type: NSEvent.EventType, _ location: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(location.cgPoint, to: nil),
                modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: session.panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        }
        func drag(_ start: Point, _ end: Point, seconds: Double = 0.7, bend: Double = 1.2, label: String = "drag") async throws {
            try await travel(screenPoint(start))
            pressed = true
            model.notePointerEvent(.leftMouseDown)
            NSApp.postEvent(try mouse(.leftMouseDown, start), atStart: false)
            try await pause(0.08)
            let frames = max(12, Int(hand.duration(seconds) * 60))
            for step in hand.path(from: start, to: end, samples: frames, bend: bend).dropFirst() {
                warp(screenPoint(step))
                session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, step))
                try await pause(1.0 / 60)
            }
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, end))
            pressed = false
            actions.append(["gesture": label, "time": movie.elapsed, "seconds": Double(frames) / 60])
            try await pause(0.2)
        }
        func scrollableList(in window: NSWindow) throws -> NSScrollView {
            func descendants(_ view: NSView) -> [NSScrollView] {
                ((view as? NSScrollView).map { [$0] } ?? []) + view.subviews.flatMap(descendants)
            }
            return try XCTUnwrap(descendants(try XCTUnwrap(window.contentView)).first {
                !$0.isHiddenOrHasHiddenAncestor && ($0.documentView?.frame.height ?? 0) > $0.contentSize.height + 20
            }, "The real scrollable tool list must be available.")
        }
        /// Real wheel events on the library's own scroll view, with the pointer resting over the list.
        func scroll(_ view: NSScrollView, pixels: Int32) async throws {
            let window = try XCTUnwrap(view.window)
            let middle = window.convertPoint(toScreen: view.convert(CGPoint(x: view.bounds.midX, y: view.bounds.midY), to: nil))
            try await travel(middle, seconds: 0.25, bend: 8)
            let before = view.contentView.bounds.origin
            for _ in 0..<10 {
                let event = try XCTUnwrap(CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1,
                                                 wheel1: pixels / 10, wheel2: 0, wheel3: 0))
                view.scrollWheel(with: try XCTUnwrap(NSEvent(cgEvent: event)))
                view.window?.displayIfNeeded()
                try await pause(1.0 / 30)
            }
            XCTAssertNotEqual(view.contentView.bounds.origin, before, "Native wheel events must actually scroll the tools.")
            actions.append(["gesture": "scroll-tools", "time": movie.elapsed, "pixels": Double(pixels)])
            try await pause(0.12)
        }

        // The pointer starts on the sample app, well clear of the toolbar.
        warp(take == "zoom" ? design(760, 420) : design(1180, 300))
        try await movie.start()
        do {
            try await pause(0.4)
            if take == "library" {
                try await click("Tool library", settle: 0.2)
                let library = try XCTUnwrap(libraryWindows().first, "The tool library must open.")
                try await click("Shapes", inLibrary: true, settle: 0.25)
                try await scroll(try scrollableList(in: library), pixels: -420)
                for category in ["Focus", "Extras"] {
                    try await click(category, inLibrary: true, hover: 0.05, settle: 0.45)
                }
                try await click("Shapes", inLibrary: true, settle: 0.2)
                try await scroll(try scrollableList(in: library), pixels: -420)
                try await click("Database", inLibrary: true, hover: 0.2, settle: 0.02)
                for _ in 0..<40 where !libraryWindows().isEmpty { try await pause(1.0 / 60) }
                XCTAssertTrue(libraryWindows().isEmpty, "Choosing a shape closes the tool library.")
                XCTAssertEqual(model.effectiveTool, .databaseStack)
                let spot = try fixture.rect("labelSpot")
                let start = canvasPoint(design(spot.minX + 120, spot.minY - 170))
                let end = canvasPoint(design(spot.minX + 300, spot.minY - 20))
                try await drag(start, end, seconds: 0.6, bend: 0.8, label: "draw-shape")
                let shape = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(shape.tool, .databaseStack)
                XCTAssertTrue(prepared.contains("Blue"), "The Database shape uses the prepared blue ink.")
                try await pause(0.9)
            } else {
                try await click("Tool library", settle: 0.22)
                if control("Screen zoom", in: libraryWindows().compactMap(\.contentView).flatMap { NativeAccessibilityNode.nodes(in: $0) }) == nil {
                    try await click("Focus", inLibrary: true, settle: 0.18)
                }
                try await click("Screen zoom", inLibrary: true, settle: 0.02)
                for _ in 0..<40 where !libraryWindows().isEmpty { try await pause(1.0 / 60) }
                XCTAssertTrue(libraryWindows().isEmpty, "Choosing Screen zoom closes the tool library.")
                // Wait for the first zoomed frame; the app shows its own cover until then.
                for _ in 0..<600 {
                    if model.zoomStartState == .active { break }
                    if case .failed(let message) = model.zoomStartState { XCTFail("Screen zoom failed: \(message)"); break }
                    try await pause(1.0 / 60)
                }
                XCTAssertEqual(model.zoomStartState, .active, "Screen zoom must start on the recording display.")
                XCTAssertTrue(model.screenZoomEnabled)
                actions.append(["gesture": "zoom-started", "time": movie.elapsed, "zoom": model.magnification])
                // Glide from the toolbar up to the circled number, then along the chart to the Thursday dip.
                let kpi = try fixture.rect("kpi")
                try await travel(design(kpi.midX, kpi.midY + 10), seconds: 1.0, bend: 60)
                actions.append(["gesture": "zoom-follow", "time": movie.elapsed, "target": "kpi"])
                try await pause(0.45)
                let dip = try fixture.rect("dip")
                try await travel(design(dip.midX, dip.midY - 40), seconds: 1.1, bend: 80)
                actions.append(["gesture": "zoom-follow", "time": movie.elapsed, "target": "dip"])
                try await pause(0.35)
                // Zoom in further with the toolbar's own control, then read the note.
                try await click("Increase live zoom", hover: 0.1, settle: 0.25)
                let deeper = model.magnification
                XCTAssertGreaterThan(deeper, 2)
                try await click("Increase live zoom", settle: 0.2)
                XCTAssertGreaterThan(model.magnification, deeper)
                let note = try fixture.rect("note")
                try await travel(design(note.minX + 40, note.midY), seconds: 1.1, bend: 70)
                actions.append(["gesture": "zoom-follow", "time": movie.elapsed, "target": "note"])
                try await travel(design(note.maxX - 30, note.midY + 4), seconds: 1.1, bend: 10)
                try await pause(0.4)
                try await click("Stop screen zoom", hover: 0.1, settle: 0.1)
                for _ in 0..<60 where model.screenZoomEnabled { try await pause(1.0 / 60) }
                XCTAssertFalse(model.screenZoomEnabled, "Stop screen zoom must end the zoom.")
                XCTAssertEqual(session.document.elements.count, 0, "The zoom demo must not add marks.")
                try await pause(0.7)
            }
            let duration = try await movie.finish()
            let metadata: [String: Any] = [
                "take": take, "fixture": fixtureName, "path": output.path, "duration": duration,
                "width": config.width, "height": config.height, "scale": scale,
                "windowRect": sourceRect(windowRect), "toolbarRect": sourceRect(toolbar.frame),
                "actions": actions, "cursorSamples": samples, "zoomSamples": zoomSamples, "preparedControls": prepared,
                "screen": screen.localizedName, "cursorScale": pointerView.scale, "toolbarMisses": toolbarMisses,
                "targets": fixture.targets.mapValues { r in
                    sourceRect(CGRect(x: windowRect.minX + r.minX, y: windowRect.maxY - r.maxY, width: r.width, height: r.height))
                },
                "cursor": "Scripted pointer drawn in a test-owned window with the cursor the app selects at each point; the app's screen zoom follows it.",
                "source": "Fresh real-time ScreenCaptureKit recording of the live native desktop overlay, real toolbar, tool library and screen zoom on a HiDPI desktop. Test-owned windows only; fictional sample-app images.",
                "input": "Timed native UI actions with a scripted cursor path. Not a human or computer-use-provider recording."
            ]
            try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
                .write(to: folder.appendingPathComponent("latest-\(take).json"), options: .atomic)
            print("WEBSITE DEMO TAKE \(take): \(output.path)")
        } catch {
            do { try await movie.cancel() } catch { XCTFail("Recording cleanup failed: \(error)") }
            throw error
        }
    }
}

private struct DemoFixture {
    let width: CGFloat
    let height: CGFloat
    let targets: [String: CGRect]

    static func load(_ url: URL, name: String) throws -> DemoFixture {
        let data = try Data(contentsOf: url)
        let root = try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let entry = try XCTUnwrap(root[name] as? [String: Any], "Missing fixture \(name)")
        let raw = try XCTUnwrap(entry["targets"] as? [String: [String: Double]])
        var rects: [String: CGRect] = [:]
        for (key, r) in raw {
            rects[key] = CGRect(x: r["x"] ?? 0, y: r["y"] ?? 0, width: r["width"] ?? 0, height: r["height"] ?? 0)
        }
        return DemoFixture(width: CGFloat(entry["width"] as? Double ?? 1580), height: CGFloat(entry["height"] as? Double ?? 690),
                           targets: rects)
    }

    func rect(_ key: String) throws -> CGRect { try XCTUnwrap(targets[key], "Missing fixture target \(key)") }
}

/// Draws the scripted pointer with the cursor the app would show, above everything else on the display.
@MainActor
private final class DemoPointerView: NSView {
    var point = CGPoint.zero
    var cursor: NSCursor = .arrow
    var pressed = false
    let scale: CGFloat = 1.35
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func draw(_ dirtyRect: NSRect) {
        let image = cursor.image, hotSpot = cursor.hotSpot
        let size = scale * (pressed ? 0.9 : 1)
        let rect = CGRect(x: point.x - hotSpot.x * size, y: point.y - hotSpot.y * size,
                          width: image.size.width * size, height: image.size.height * size)
        image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true,
                   hints: [.interpolation: NSImageInterpolation.high])
    }
}
