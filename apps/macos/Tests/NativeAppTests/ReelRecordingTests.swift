import AppKit
import ObjectiveC
import ScreenCaptureKit
import XCTest
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

/// Owner-run creator recording for the website reel. Records art-directed takes of the live desktop overlay
/// on a HiDPI desktop: real toolbar controls, real canvas events, and the real macOS cursor moved along a
/// deterministic eased path so native pointer effects (Spotlight, cursor halo) follow it. Test-owned windows
/// only; the sample apps are fictional images. Normal test runs skip this.
@MainActor
final class ReelRecordingTests: XCTestCase {
    func testRecordReelTakes() async throws {
        let env = ProcessInfo.processInfo.environment
        guard env["ANNOTATOR_RECORD_REEL"] == "1" else {
            throw XCTSkip("The reel recording is an explicit owner-run task.")
        }
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        guard DisplayCapture.hasPermission else {
            throw CaptureFailure.streamStopped("The recorder needs its existing Screen Recording grant.")
        }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        // Record on a separate (virtual) display so the owner's main display and mouse stay untouched.
        let wanted = env["ANNOTATOR_REEL_SCREEN"] ?? "DeskPad"
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.localizedName.localizedCaseInsensitiveContains(wanted) },
                                   "Open the \(wanted) display before recording the reel.")
        guard screen.backingScaleFactor == 2 else {
            throw CaptureFailure.streamStopped("Switch the \(wanted) display to a HiDPI mode before recording.")
        }
        let root = RepositoryRoot.url
        let fixtures = root.appendingPathComponent("videos/annotator-reel/fixtures")
        let all = ["dashboard", "smart", "focus"]
        let takes = env["ANNOTATOR_REEL_TAKE"].map { [$0] } ?? all
        guard takes.allSatisfy({ all.contains($0) }) else { throw CocoaError(.coderInvalidValue) }
        ReelPointerHook.install()
        defer { ReelPointerHook.uninstall() }
        for take in takes { try await record(take, on: screen, root: root, fixtures: fixtures) }
    }

    @available(macOS 15.0, *)
    private func record(_ take: String, on screen: NSScreen, root: URL, fixtures: URL) async throws {
        let frame = screen.frame
        let fixtureName = take == "smart" ? "design" : take == "focus" ? "gallery" : "dashboard"
        let fixture = try ReelFixture.load(fixtures.appendingPathComponent("targets.json"), name: fixtureName)
        let image = try XCTUnwrap(NSImage(contentsOf: fixtures.appendingPathComponent("\(fixtureName)@2x.png")))
        image.size = CGSize(width: fixture.width, height: fixture.height)
        let windowTop: CGFloat = 24
        let windowRect = CGRect(x: frame.minX + (frame.width - fixture.width) / 2,
                                y: frame.maxY - windowTop - fixture.height, width: fixture.width, height: fixture.height)
        let toolbarTop = windowRect.minY - 14

        let suite = "reel-\(take)-\(UUID())"
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
            model.spotlight = false
            model.cursorHalo = false
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
            DisplayCapture.useControlledWindowsForTesting(nil)
        }
        try await Task.sleep(for: .milliseconds(200))
        let session = try XCTUnwrap(model.desktopSessions.first { $0.panel.screen == screen },
                                    "The app must open an overlay on the recording display.")
        if model.active !== session { XCTAssertTrue(model.activateDrawingSession(session)) }
        print("REEL DISPLAY pointer=\(NSEvent.mouseLocation) screen=\(screen.frame) sessions=\(model.desktopSessions.map { $0.panel.screen?.localizedName ?? "?" })")
        XCTAssertTrue(model.active === session)
        XCTAssertNil(session.sourceImage)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        toolbar.appearance = NSAppearance(named: .darkAqua)
        print("REEL TOOLBAR frame=\(toolbar.frame) screen=\(toolbar.screen?.localizedName ?? "?")")

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
        sample.title = "Reel sample app"
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
        let pointerView = ReelPointerView(frame: CGRect(origin: .zero, size: frame.size))
        pointerPanel.contentView = pointerView
        pointerPanel.orderFrontRegardless()
        defer {
            pointerPanel.orderOut(nil); pointerPanel.contentView = nil; pointerPanel.close()
            sample.orderOut(nil); sample.contentView = nil; sample.close()
            backdrop.orderOut(nil); backdrop.close()
        }
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
        var prepared: [String] = []
        func control(_ name: String, in roots: [NativeAccessibilityNode]) -> NativeAccessibilityNode? {
            roots.first {
                [.button, .radioButton, .checkBox].contains($0.accessibilityRole()) &&
                    ($0.accessibilityLabel() == name || $0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true)
            }
        }
        func prepare(_ name: String, optional: Bool = false) async throws {
            var found = control(name, in: nodes())
            for _ in 0..<(optional ? 6 : 30) where found == nil {
                try await Task.sleep(for: .milliseconds(50))
                try layout()
                found = control(name, in: nodes())
            }
            if optional && found == nil { return }
            let node = try XCTUnwrap(found, "Missing preparation control: \(name)")
            _ = node.accessibilityPerformPress()
            prepared.append(name)
            try await Task.sleep(for: .milliseconds(140))
            try layout()
        }

        // Unrecorded preparation: halo appearance (switched on only in the focus take, from the tool library),
        // style choices the take relies on, then leave the Select tool active.
        var halo = CursorHighlightSettings()
        halo.shape = .roundedSquare
        halo.visibility = .always
        halo.color = RGBA(0.08, 0.8, 0.84)
        halo.diameter = 84
        halo.borderWidth = 8
        halo.glowRadius = 16
        halo.showClicks = true
        halo.clickEffect = .ripple
        model.setShortcutHints(false)
        model.setCursorHighlight(halo)
        XCTAssertEqual(model.cursorHighlightSettings.resolvedClickEffect, .ripple)
        model.cursorHalo = false
        switch take {
        case "dashboard":
            for name in ["Arrow", "Curved arrow", "Ellipse", "Width 7", "Sketchy", "Stroke color", "Teal", "Done", "Select"] {
                try await prepare(name)
            }
        case "smart":
            // Styles are per tool: Smart Draw shapes use the Pen's ink, the label uses the Text tool's ink.
            try await prepare("Pen")
            try await prepare("Width 7", optional: true)
            for name in ["Stroke color", "Gradient", "Sunset gradient", "Done", "Text", "Stroke color", "Gradient",
                         "Sunset gradient", "Done", "Font size 28", "Select"] {
                try await prepare(name)
            }
            model.smartDraw = true
        default:
            model.spotlightRadius = 175
            try await prepare("Select")
            // Warm the tool library's Focus section before recording; its first render blocks for several seconds.
            try await prepare("Tool library")
            for _ in 0..<40 where libraryWindows().isEmpty { try await Task.sleep(for: .milliseconds(50)) }
            let warmLibrary = try XCTUnwrap(libraryWindows().first, "The tool library must open.")
            let focusTab = try XCTUnwrap(control("Focus", in: NativeAccessibilityNode.nodes(in: try XCTUnwrap(warmLibrary.contentView))))
            _ = focusTab.accessibilityPerformPress()
            try await Task.sleep(for: .milliseconds(400))
            warmLibrary.standardWindowButton(.closeButton)?.performClick(nil)
            try await Task.sleep(for: .milliseconds(300))
            XCTAssertTrue(libraryWindows().isEmpty, "Close the warmed library before recording.")
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
        let owned = shareable.windows.filter { $0.owningApplication?.processID == ownedPID && $0.isOnScreen &&
            screen.frame.intersects(CGRect(x: $0.frame.minX, y: NSScreen.screens[0].frame.maxY - $0.frame.maxY,
                                           width: $0.frame.width, height: $0.frame.height)) }
        print("REEL WINDOWS on \(screen.localizedName): \(owned.map { "\($0.title ?? "untitled") \($0.frame)" })")
        let ownApp = try XCTUnwrap(shareable.applications.first { $0.processID == ownedPID })
        func filter() async throws -> SCContentFilter {
            SCContentFilter(display: display, including: [ownApp], exceptingWindows: [])
        }
        let folder = root.appendingPathComponent("build/reel-takes")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let output = folder.appendingPathComponent("\(take)-\(UUID()).mp4")
        let movie = try RealtimeDesktopMovie(url: output, filter: try await filter(), configuration: config, codec: .hevc)
        var hand = DemoHandMotion(seed: take == "dashboard" ? 202609261 : take == "smart" ? 202609262 : 202609263)
        var actions: [[String: Any]] = []
        var samples: [[String: Double]] = []
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
        /// The cursor the app itself would show here: an arrow over its panels, the tool cursor over the canvas.
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
        }
        func pause(_ seconds: Double) async throws {
            try layout()
            toolbar.displayIfNeeded()
            session.panel.displayIfNeeded()
            pointerPanel.displayIfNeeded()
            try await movie.wait(seconds)
            samples.append(["time": movie.elapsed, "x": (cursor.x - frame.minX) * scale,
                            "y": (frame.maxY - cursor.y) * scale, "pressed": pressed ? 1 : 0])
        }
        /// Fixture coordinates (points, top-left origin of the sample window) to AppKit screen coordinates.
        func design(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: windowRect.minX + x, y: windowRect.maxY - y) }
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
        func travel(_ target: CGPoint, seconds: Double = 0.32, preview: Bool = false) async throws {
            let frames = max(8, Int(hand.duration(seconds) * 60))
            for step in hand.path(from: Point(cursor), to: Point(target), samples: frames, bend: 24).dropFirst() {
                warp(step.cgPoint)
                if preview && session.canvas.constructingArrow {
                    session.canvas.mouseMoved(with: try mouse(.mouseMoved, canvasPoint(step.cgPoint)))
                }
                try await pause(1.0 / 60)
            }
        }
        func click(_ name: String, inLibrary: Bool = false, hover: Double = 0, settle: Double = 0.08) async throws {
            let candidates = inLibrary ? libraryWindows().compactMap(\.contentView).flatMap {
                NativeAccessibilityNode.nodes(in: $0)
            } : nodes()
            let node = try XCTUnwrap(candidates.first {
                [.button, .radioButton, .checkBox].contains($0.accessibilityRole()) &&
                    ($0.accessibilityLabel() == name || $0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true)
            }, "Missing real native control: \(name)")
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
        func canvasDown(_ location: Point) async throws {
            NSApp.postEvent(try mouse(.leftMouseDown, location), atStart: false)
            try await pause(0.08)
            for _ in 0..<15 {
                if !nodes().contains(where: { $0.accessibilityLabel()?.contains("hex color and alpha") == true }) { break }
                try await pause(1.0 / 60)
            }
        }
        func drag(_ start: Point, _ end: Point, seconds: Double = 0.7, bend: Double = 1.2, label: String = "drag") async throws {
            try await travel(screenPoint(start))
            pressed = true
            model.notePointerEvent(.leftMouseDown)
            try await canvasDown(start)
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
        func arrowPoint(_ location: Point) async throws {
            try await travel(screenPoint(location), seconds: 0.3, preview: true)
            pressed = true
            model.notePointerEvent(.leftMouseDown)
            try await pause(0.06)
            try await canvasDown(location)
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, location))
            pressed = false
            actions.append(["gesture": "arrow-point", "time": movie.elapsed])
            try await pause(0.05)
        }
        /// A hand-drawn pen stroke through design-space points; Smart Draw may replace it with a clean shape.
        func penStroke(_ points: [CGPoint], seconds: Double, expect tool: Tool, label: String) async throws {
            let canvas = points.map { canvasPoint(design($0.x, $0.y)) }
            try await travel(screenPoint(canvas[0]))
            pressed = true
            model.notePointerEvent(.leftMouseDown)
            session.canvas.mouseDown(with: try mouse(.leftMouseDown, canvas[0]))
            let perSegment = max(3, Int(seconds * 60) / max(1, canvas.count - 1))
            for index in 1..<canvas.count {
                for step in 1...perSegment {
                    let t = Double(step) / Double(perSegment)
                    let point = canvas[index - 1] + (canvas[index] - canvas[index - 1]) * t
                    warp(screenPoint(point))
                    session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, point))
                    try await pause(1.0 / 60)
                }
            }
            for _ in 0..<40 {
                if session.canvas.smartPreview?.tool == tool { break }
                try await pause(1.0 / 60)
            }
            XCTAssertEqual(session.canvas.smartPreview?.tool, tool, "Smart Draw must preview a clean \(tool.label).")
            try await pause(0.12)
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, canvas[canvas.count - 1]))
            pressed = false
            for _ in 0..<40 {
                if session.document.elements.last?.tool == tool { break }
                try await pause(1.0 / 60)
            }
            XCTAssertEqual(session.document.elements.last?.tool, tool)
            actions.append(["gesture": label, "time": movie.elapsed, "tool": tool.rawValue])
            try await pause(0.2)
        }
        func typeLabel(_ text: String, at global: CGPoint) async throws {
            let location = canvasPoint(global)
            try await travel(global, seconds: 0.3)
            pressed = true
            model.notePointerEvent(.leftMouseDown)
            try await canvasDown(location)
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, location))
            pressed = false
            let editor = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
            try await travel(CGPoint(x: global.x + 40, y: global.y - 70), seconds: 0.2)
            for character in text {
                editor.insertText(String(character), replacementRange: NSRange(location: NSNotFound, length: 0))
                session.canvas.textDidChange(Notification(name: NSText.didChangeNotification, object: editor))
                actions.append(["gesture": "type-character", "text": String(character), "time": movie.elapsed])
                try await pause(0.045)
            }
            XCTAssertEqual(editor.string, text)
            try await pause(0.25)
            session.canvas.commitTextIfNeeded()
            XCTAssertEqual(session.document.elements.last?.tool, .text)
        }

        warp(take == "focus" ? design(180, 300) : take == "smart" ? design(470, 300) : design(760, 420))
        try await movie.start()
        do {
            try await pause(take == "dashboard" ? 1.2 : 0.3)
            if take == "dashboard" {
                try await click("Ellipse")
                // Encloses "41% ▼6%" and stays clear of the "Activation" label above it.
                let kpi = try fixture.rect("kpi").union(try fixture.rect("kpiChip"))
                let oval = CGRect(x: kpi.midX - 90, y: kpi.midY - 23, width: 180, height: 52)
                try await drag(canvasPoint(design(oval.minX, oval.minY)), canvasPoint(design(oval.maxX, oval.maxY)),
                               seconds: 0.72, label: "ellipse")
                let ellipse = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(ellipse.tool, .ellipse)
                XCTAssertEqual(ellipse.style.roughness, 2)
                try await click("Arrow")
                try await click("Stroke color", hover: 0.1, settle: 0.3)
                try await click("Blue", hover: 0.45)
                // A near-straight arrow from the circled number into the Thursday dip; its middle point is then
                // dragged out so the arrow bends into one clean arc inside the chart's V.
                let dip = try fixture.rect("dip")
                let tip = CGPoint(x: dip.minX - 3, y: dip.minY - 8)
                let points = [design(oval.midX - 43, oval.maxY + 10), design(606, 392), design(tip.x, tip.y)].map(canvasPoint)
                for point in points { try await arrowPoint(point) }
                session.canvas.keyDown(with: try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero,
                    modifierFlags: [], timestamp: movie.elapsed, windowNumber: session.panel.windowNumber,
                    context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36)))
                try await pause(0.35)
                let arrow = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(arrow.tool, .arrow)
                XCTAssertEqual(arrow.points.count, 3)
                XCTAssertTrue(model.style.curved)
                let adjusted = canvasPoint(design(566, 384))
                try await drag(arrow.points[1], adjusted, seconds: 0.55, bend: 0.4, label: "edit-arrow-point")
                let edited = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(edited.id, arrow.id)
                XCTAssertEqual(edited.points[1].x, adjusted.x, accuracy: 1)
                XCTAssertEqual(edited.points[1].y, adjusted.y, accuracy: 1)
                try await pause(0.25)
                try await click("Highlighter", hover: 0.35)
                let note = try fixture.rect("note")
                try await drag(canvasPoint(design(note.minX - 4, note.midY)), canvasPoint(design(note.maxX + 6, note.midY)),
                               seconds: 0.7, bend: 0.6, label: "highlight")
                XCTAssertEqual(session.document.elements.last?.tool, .highlighter)
                try await pause(0.6)
            } else if take == "smart" {
                try await click("Pen")
                XCTAssertEqual(model.effectiveTool, .pen)
                let pay = try fixture.rect("pay").insetBy(dx: -30, dy: -20)
                var jitter = DemoHandMotion(seed: 7_202_609)
                func wobble(_ p: CGPoint, _ amount: CGFloat) -> CGPoint {
                    CGPoint(x: p.x + CGFloat(jitter.next() * 2 - 1) * amount, y: p.y + CGFloat(jitter.next() * 2 - 1) * amount)
                }
                var loop: [CGPoint] = []
                let corners = [CGPoint(x: pay.minX + 6, y: pay.minY - 2), CGPoint(x: pay.maxX + 3, y: pay.minY + 3),
                               CGPoint(x: pay.maxX - 2, y: pay.maxY + 4), CGPoint(x: pay.minX - 3, y: pay.maxY - 1),
                               CGPoint(x: pay.minX + 2, y: pay.minY + 6), CGPoint(x: pay.minX + 22, y: pay.minY - 4)]
                for index in 0..<(corners.count - 1) {
                    for step in 0..<6 {
                        let t = CGFloat(step) / 6
                        let a = corners[index], b = corners[index + 1]
                        loop.append(wobble(CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t), 2.2))
                    }
                }
                loop.append(corners[corners.count - 1])
                try await penStroke(loop, seconds: 0.8, expect: .rectangle, label: "smart-rectangle")
                XCTAssertNotNil(session.document.elements.last?.style.strokeGradient, "The snapped box keeps the gradient.")
                let spot = try fixture.rect("labelSpot")
                let tail = CGPoint(x: spot.minX - 6, y: pay.midY)
                let tip = CGPoint(x: pay.maxX + 14, y: pay.midY)
                let arrowStroke = [tail, CGPoint(x: (tail.x + tip.x) / 2, y: pay.midY + 3), tip,
                                   CGPoint(x: tip.x + 34, y: tip.y - 30), tip, CGPoint(x: tip.x + 34, y: tip.y + 30)]
                try await penStroke(arrowStroke, seconds: 0.62, expect: .arrow, label: "smart-arrow")
                XCTAssertNotNil(session.document.elements.last?.style.strokeGradient, "The snapped arrow keeps the gradient.")
                try await click("Text")
                try await typeLabel("Make it bigger", at: design(spot.minX + 4, pay.midY - 18))
                XCTAssertNotNil(session.document.elements.last?.style.strokeGradient, "The label keeps the gradient.")
                try await pause(0.6)
            } else {
                try await click("Tool library", settle: 0.22)
                if control("Spotlight", in: libraryWindows().compactMap(\.contentView).flatMap { NativeAccessibilityNode.nodes(in: $0) }) == nil {
                    try await click("Focus", inLibrary: true, settle: 0.18)
                }
                try await click("Cursor halo", inLibrary: true, settle: 0.1)
                XCTAssertTrue(model.cursorHalo, "The native cursor halo must be on.")
                try await click("Spotlight", inLibrary: true, settle: 0.12)
                XCTAssertTrue(model.spotlight, "The native Spotlight must be on.")
                let library = try XCTUnwrap(libraryWindows().first, "The tool library must be open.")
                let close = try XCTUnwrap(library.standardWindowButton(.closeButton), "The library needs its close button.")
                let closeFrame = library.convertToScreen(close.convert(close.bounds, to: nil))
                try await travel(CGPoint(x: closeFrame.midX, y: closeFrame.midY), seconds: 0.28)
                pressed = true
                model.notePointerEvent(.leftMouseDown)
                try await pause(0.06)
                close.performClick(nil)
                actions.append(["control": "Close tool library", "time": movie.elapsed, "rect": sourceRect(closeFrame)])
                try await pause(0.07)
                pressed = false
                for _ in 0..<20 where !libraryWindows().isEmpty { try await pause(1.0 / 60) }
                XCTAssertTrue(libraryWindows().isEmpty, "The library must close before the Spotlight walk.")
                XCTAssertEqual(model.effectiveTool, .select, "Clicks during the walk must not draw.")
                let marks = session.document.elements.count
                for name in ["img0", "img1", "hero"] {
                    // A real click on each picture: the canvas gets an empty Select click and the halo its ripple.
                    let r = try fixture.rect(name)
                    let target = design(r.midX, r.midY)
                    try await travel(target, seconds: 0.45)
                    try await pause(0.12)
                    pressed = true
                    model.notePointerEvent(.leftMouseDown)
                    try await canvasDown(canvasPoint(target))
                    session.canvas.mouseUp(with: try mouse(.leftMouseUp, canvasPoint(target)))
                    model.notePointerEvent(.leftMouseUp)
                    pressed = false
                    actions.append(["gesture": "spotlight-click", "target": name, "time": movie.elapsed])
                    try await pause(name == "hero" ? 0.5 : 0.32)
                }
                XCTAssertEqual(session.document.elements.count, marks, "Clicking the pictures must not add marks.")
                XCTAssertTrue(model.spotlight, "The Spotlight stays on through the clicks.")
                actions.append(["gesture": "spotlight-walk", "time": movie.elapsed])
                try await click("Stop spotlight", settle: 0.15)
                XCTAssertFalse(model.spotlight)
                try await click("Effects")
                XCTAssertEqual(model.effectiveTool, .blur)
                let busy = try fixture.rect("busy").insetBy(dx: 8, dy: 8)
                try await drag(canvasPoint(design(busy.minX, busy.minY)), canvasPoint(design(busy.maxX, busy.maxY)),
                               seconds: 0.85, label: "blur")
                for _ in 0..<60 {
                    if session.effectsReady { break }
                    try await pause(0.1)
                }
                XCTAssertTrue(session.effectsReady, "The take must contain live native blur.")
                XCTAssertEqual(session.document.elements.last?.tool, .blur)
                try await pause(0.6)
            }
            let duration = try await movie.finish()
            let metadata: [String: Any] = [
                "take": take, "fixture": fixtureName, "path": output.path, "duration": duration,
                "width": config.width, "height": config.height, "scale": scale,
                "windowRect": sourceRect(windowRect), "toolbarRect": sourceRect(toolbar.frame),
                "actions": actions, "cursorSamples": samples, "preparedControls": prepared,
                "screen": screen.localizedName, "cursorScale": pointerView.scale, "toolbarMisses": toolbarMisses,
                "targets": fixture.targets.mapValues { r in
                    sourceRect(CGRect(x: windowRect.minX + r.minX, y: windowRect.maxY - r.maxY, width: r.width, height: r.height))
                },
                "cursor": "Scripted pointer drawn in a test-owned window with the cursor the app selects at each point; the app's own cursor halo, click ripples and Spotlight follow it.",
                "source": "Fresh real-time ScreenCaptureKit recording of the live native desktop overlay and real toolbar on a HiDPI desktop. Test-owned windows only; fictional sample-app images.",
                "input": "Timed native UI actions and canvas events with a scripted cursor path. Not a human or computer-use-provider recording."
            ]
            try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
                .write(to: folder.appendingPathComponent("latest-\(take).json"), options: .atomic)
            print("REEL TAKE \(take): \(output.path)")
        } catch {
            do { try await movie.cancel() } catch { XCTFail("Recording cleanup failed: \(error)") }
            throw error
        }
    }
}

private struct ReelFixture {
    let width: CGFloat
    let height: CGFloat
    let targets: [String: CGRect]

    static func load(_ url: URL, name: String) throws -> ReelFixture {
        let data = try Data(contentsOf: url)
        let root = try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let entry = try XCTUnwrap(root[name] as? [String: Any], "Missing fixture \(name)")
        let raw = try XCTUnwrap(entry["targets"] as? [String: [String: Double]])
        var rects: [String: CGRect] = [:]
        for (key, r) in raw {
            rects[key] = CGRect(x: r["x"] ?? 0, y: r["y"] ?? 0, width: r["width"] ?? 0, height: r["height"] ?? 0)
        }
        return ReelFixture(width: CGFloat(entry["width"] as? Double ?? 1580), height: CGFloat(entry["height"] as? Double ?? 690),
                           targets: rects)
    }

    func rect(_ key: String) throws -> CGRect { try XCTUnwrap(targets[key], "Missing fixture target \(key)") }
}

/// Test-only pointer instrumentation for creator recordings on a separate display. The app reads the scripted
/// location through NSEvent.mouseLocation, so native pointer effects follow it while the owner's real mouse stays
/// free. Global event monitors are suppressed so the owner's clicks elsewhere never reach the take.
@MainActor
enum ReelPointerHook {
    nonisolated(unsafe) static var location = CGPoint.zero
    private static var originals: [(Method, IMP)] = []

    static func install() {
        guard originals.isEmpty else { return }
        let type: AnyClass = NSEvent.self
        if let method = class_getClassMethod(type, #selector(getter: NSEvent.mouseLocation)) {
            let block: @convention(block) (AnyObject) -> NSPoint = { _ in ReelPointerHook.location }
            originals.append((method, method_setImplementation(method, imp_implementationWithBlock(block))))
        }
        if let method = class_getClassMethod(type, #selector(NSEvent.addGlobalMonitorForEvents(matching:handler:))) {
            let block: @convention(block) (AnyObject, UInt64, AnyObject?) -> AnyObject? = { _, _, _ in nil }
            originals.append((method, method_setImplementation(method, imp_implementationWithBlock(block))))
        }
    }

    static func uninstall() {
        for (method, implementation) in originals { method_setImplementation(method, implementation) }
        originals.removeAll()
    }
}

@MainActor
private final class ReelPointerView: NSView {
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
