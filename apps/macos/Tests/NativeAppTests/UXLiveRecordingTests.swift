import AppKit
import AVFoundation
import ScreenCaptureKit
import XCTest
import AnnotationCore
import MotionCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

/// Owner-run live recordings for the macOS UX reference (WIN-PAR-06, plan task M-4): the workspace show and
/// hide animations, live zoom, live effects over an owned sample desktop and the live region-sharing output.
/// The real AppDelegate, toolbar and overlay windows run on the main display. ScreenCaptureKit records only
/// this process's windows, and the app's own capture reads only the owned backdrop and sample window. The
/// pointer is scripted, so this is not human or computer-use acceptance. Normal test runs skip it.
@MainActor
final class UXLiveRecordingTests: XCTestCase {
    func test01ShowAndHideStyles() async throws {
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        try await UXLiveStage.run("show-hide") { try await $0.recordShowAndHide() }
    }

    func test02LiveZoom() async throws {
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        try await UXLiveStage.run("zoom") { try await $0.recordZoom() }
    }

    func test03LiveEffects() async throws {
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        try await UXLiveStage.run("effects") { try await $0.recordEffects() }
    }

    func test04RegionSharingOutput() async throws {
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        try await UXLiveStage.run("sharing") { try await $0.recordSharing() }
    }
}

/// One recorded take: events and timed segments relative to the start of its movie.
@available(macOS 15.0, *)
@MainActor
final class UXLiveTake {
    let movie: RealtimeDesktopMovie
    private(set) var events: [[String: Any]] = []
    private(set) var segments: [[String: Any]] = []
    init(movie: RealtimeDesktopMovie) { self.movie = movie }
    var time: Double { movie.elapsed }
    func mark(_ label: String) { events.append(["time": time, "label": label]) }
    func wait(_ seconds: Double) async throws { try await movie.wait(seconds) }
    func segment(_ label: String, _ body: () async throws -> Void) async throws {
        let start = time
        try await body()
        segments.append(["label": label, "start": start, "end": time])
    }
}

/// The owned desktop: a plain backdrop, one fictional sample window, the real app and a scripted pointer.
@available(macOS 15.0, *)
@MainActor
final class UXLiveStage {
    static let output = RepositoryRoot.url.appendingPathComponent("build/ux-reference/raw-live")

    static func run(_ group: String, _ body: (UXLiveStage) async throws -> Void) async throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["ANNOTATOR_RECORD_UX_LIVE"] == "1" else {
            throw XCTSkip("Live UX reference recordings are an explicit owner-run task.")
        }
        if let groups = environment["ANNOTATOR_RECORD_UX_LIVE_GROUPS"],
           !groups.split(separator: ",").map(String.init).contains(group) {
            throw XCTSkip("The \(group) recordings were not requested.")
        }
        guard DisplayCapture.hasPermission else {
            throw CaptureFailure.streamStopped("The recorder needs its existing Screen Recording grant.")
        }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        let stage = try await UXLiveStage()
        do { try await body(stage) } catch {
            await stage.close()
            throw error
        }
        await stage.close()
    }

    let suite = "ux-live-\(UUID())"
    let defaults: UserDefaults
    let model: WorkspaceModel
    let app: AppDelegate
    let screen: NSScreen
    let session: OverlaySession
    let toolbar: NSPanel
    let backdrop: NSPanel
    let sample: NSPanel
    let pointerPanel: NSPanel
    let pointer: DesktopDemoPointer
    /// The recorded area in AppKit screen coordinates: the toolbar and the sample window.
    let stage: CGRect
    private(set) var cursor: CGPoint
    var hand = DemoHandMotion(seed: 2026101901)

    init() async throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setToolbarDockEdge(.bottom)
        let app = AppDelegate(model: model)
        let screen = try XCTUnwrap(NSScreen.main ?? NSScreen.screens.first)
        let visible = screen.visibleFrame
        let size = CGSize(width: min(1440, visible.width), height: min(900, visible.height))
        var area = CGRect(x: (visible.midX - size.width / 2).rounded(), y: visible.minY, width: size.width, height: size.height)
        let backdrop = Self.panel(screen.frame, title: "UX reference backdrop", level: .normal)
        backdrop.backgroundColor = NSColor(srgbRed: 0.80, green: 0.84, blue: 0.88, alpha: 1)
        let content = CGRect(x: area.minX + 130, y: area.minY + 176, width: area.width - 260, height: area.height - 236)
        let sample = Self.panel(content, title: "Sample content",
                                level: NSWindow.Level(rawValue: NSWindow.Level.normal.rawValue + 1), titled: true)
        sample.appearance = NSAppearance(named: .aqua)
        sample.contentView = UXLiveSampleView(frame: CGRect(origin: .zero, size: content.size))
        let pointerPanel = Self.panel(screen.frame, title: "Scripted pointer",
                                      level: NSWindow.Level(rawValue: NSWindow.Level.popUpMenu.rawValue + 1))
        pointerPanel.isOpaque = false
        pointerPanel.backgroundColor = .clear
        let pointer = DesktopDemoPointer(frame: CGRect(origin: .zero, size: screen.frame.size))
        pointerPanel.contentView = pointer
        backdrop.orderFrontRegardless()
        sample.orderFrontRegardless()
        DisplayCapture.useControlledWindowsForTesting(Set([backdrop, sample].map { CGWindowID($0.windowNumber) }))
        let cursor = CGPoint(x: content.midX, y: content.midY)
        ReelPointerHook.install()
        ReelPointerHook.location = cursor
        model.screenPointer = { ReelPointerHook.location }
        app.resumeDrawing()
        let session = try XCTUnwrap(model.desktopSessions.first { $0.panel.screen == screen } ?? model.active)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        try await Task.sleep(for: .milliseconds(500))
        area = area.union(toolbar.frame.insetBy(dx: -24, dy: -16)).intersection(screen.frame)
        self.defaults = defaults
        self.model = model
        self.app = app
        self.screen = screen
        self.backdrop = backdrop
        self.sample = sample
        self.pointerPanel = pointerPanel
        self.pointer = pointer
        self.cursor = cursor
        self.session = session
        self.toolbar = toolbar
        stage = CGRect(x: area.minX.rounded(.down), y: area.minY.rounded(.down),
                       width: (area.width / 2).rounded(.down) * 2, height: (area.height / 2).rounded(.down) * 2)
        pointerPanel.orderFrontRegardless()
        move(cursor)
        try await Task.sleep(for: .milliseconds(300))
    }

    func close() async {
        let sharing = model.regionSharing
        if sharing.state.isCapturing || sharing.output != nil { sharing.stop(closeOutput: true) }
        sharing.controls?.orderOut(nil)
        model.stopZoom()
        if model.magnifierEnabled { model.toggleMagnifier() }
        model.setVisibilityAnimation(false)
        app.hideTools()
        app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
        model.closeAll()
        ReelPointerHook.uninstall()
        DisplayCapture.useControlledWindowsForTesting(nil)
        for panel in [pointerPanel, sample, backdrop] {
            panel.orderOut(nil)
            panel.contentView = nil
            panel.close()
        }
        defaults.removePersistentDomain(forName: suite)
        try? await Task.sleep(for: .milliseconds(400))
    }

    static func panel(_ frame: CGRect, title: String, level: NSWindow.Level, titled: Bool = false) -> NSPanel {
        let panel = NSPanel(contentRect: frame, styleMask: titled ? [.titled, .nonactivatingPanel] : [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.title = title
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.hasShadow = titled
        panel.level = level
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        return panel
    }

    // MARK: Coordinates and pointer

    /// A point in the sample window's content, measured from its top-left corner, in screen coordinates.
    func design(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
        let frame = sample.convertToScreen(sample.contentView?.frame ?? .zero)
        return CGPoint(x: frame.minX + x, y: frame.maxY - y)
    }

    func canvasPoint(_ global: CGPoint) -> Point {
        Point(session.canvas.convert(session.panel.convertPoint(fromScreen: global), from: nil))
    }

    /// A rectangle in the sample window's content coordinates, as a canvas rectangle.
    func canvasRect(_ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat) -> Rect {
        Rect(from: canvasPoint(design(x, y)), to: canvasPoint(design(x + width, y + height)))
    }

    func move(_ global: CGPoint) {
        cursor = global
        ReelPointerHook.location = global
        pointer.point = pointer.convert(pointerPanel.convertPoint(fromScreen: global), from: nil)
        pointer.needsDisplay = true
        model.notePointerEvent(.mouseMoved)
    }

    func travel(to target: CGPoint, seconds: Double = 0.45, hover: Bool = false) async throws {
        let frames = max(6, Int(hand.duration(seconds) * 60))
        for step in hand.path(from: Point(cursor), to: Point(target), samples: frames, bend: 14).dropFirst() {
            move(step.cgPoint)
            if hover { session.canvas.mouseMoved(with: try event(.mouseMoved, step.cgPoint)) }
            try await Task.sleep(for: .milliseconds(16))
        }
    }

    func event(_ type: NSEvent.EventType, _ global: CGPoint) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.panel.convertPoint(fromScreen: global), modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: session.panel.windowNumber,
            context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }

    /// A real canvas drag along `points` (screen coordinates), evenly paced over `seconds`.
    func drag(_ points: [CGPoint], seconds: Double) async throws {
        guard let first = points.first, let last = points.last else { return }
        try await travel(to: first, seconds: 0.4)
        pointer.pressed = true
        pointer.needsDisplay = true
        session.canvas.mouseDown(with: try event(.leftMouseDown, first))
        let lengths = zip(points, points.dropFirst()).map { hypot($1.x - $0.x, $1.y - $0.y) }
        let total = max(1, lengths.reduce(0, +))
        let frames = max(2, Int(seconds * 60))
        for frame in 1...frames {
            var distance = total * CGFloat(frame) / CGFloat(frames)
            var location = last
            for (index, length) in lengths.enumerated() {
                if distance <= length || index == lengths.count - 1 {
                    let t = length > 0 ? min(1, distance / length) : 1
                    location = CGPoint(x: points[index].x + (points[index + 1].x - points[index].x) * t,
                                       y: points[index].y + (points[index + 1].y - points[index].y) * t)
                    break
                }
                distance -= length
            }
            move(location)
            session.canvas.mouseDragged(with: try event(.leftMouseDragged, location))
            try await Task.sleep(for: .milliseconds(16))
        }
        session.canvas.mouseUp(with: try event(.leftMouseUp, last))
        pointer.pressed = false
        pointer.needsDisplay = true
    }

    func waitUntil(_ take: UXLiveTake?, timeout: Double = 6, _ condition: () -> Bool) async throws {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while !condition() {
            guard ProcessInfo.processInfo.systemUptime < deadline else {
                throw CaptureFailure.streamStopped("A live state did not settle within \(timeout) seconds.")
            }
            if let take { try await take.wait(0.01) } else { try await Task.sleep(for: .milliseconds(10)) }
        }
    }

    // MARK: Recording

    func displayFilter() async throws -> SCContentFilter {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let id = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
        let display = try XCTUnwrap(content.displays.first { $0.displayID == id })
        let owned = try XCTUnwrap(content.applications.first { $0.processID == ProcessInfo.processInfo.processIdentifier })
        return SCContentFilter(display: display, including: [owned], exceptingWindows: [])
    }

    func streamConfiguration(for area: CGRect) -> SCStreamConfiguration {
        let scale = screen.backingScaleFactor
        let configuration = SCStreamConfiguration()
        configuration.sourceRect = CGRect(x: area.minX - screen.frame.minX, y: screen.frame.maxY - area.maxY,
                                          width: area.width, height: area.height)
        configuration.width = Int(area.width * scale / 2) * 2
        configuration.height = Int(area.height * scale / 2) * 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        configuration.showsCursor = false
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.colorSpaceName = CGColorSpace.sRGB
        configuration.captureResolution = .best
        return configuration
    }

    func record(_ name: String, title: String, notes: String, extra: [String: Any] = [:],
                _ body: (UXLiveTake) async throws -> Void) async throws {
        let folder = Self.output.appendingPathComponent(name)
        try? FileManager.default.removeItem(at: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let configuration = streamConfiguration(for: stage)
        let movie = try RealtimeDesktopMovie(url: folder.appendingPathComponent("recording.mp4"),
                                             filter: try await displayFilter(), configuration: configuration, codec: .h264)
        let take = UXLiveTake(movie: movie)
        try await movie.start()
        do {
            try await body(take)
            let duration = try await movie.finish()
            try write(folder, take: take, name: name, title: title, notes: notes, duration: duration,
                      pixels: [configuration.width, configuration.height], area: stage, extra: extra)
            print("UX LIVE: \(name), \(String(format: "%.2f", duration)) s")
        } catch {
            try? await movie.cancel()
            throw error
        }
    }

    func write(_ folder: URL, take: UXLiveTake, name: String, title: String, notes: String, duration: Double,
               pixels: [Int], area: CGRect, extra: [String: Any]) throws {
        let version = ProcessInfo.processInfo.operatingSystemVersion
        var metadata: [String: Any] = [
            "take": name, "title": title, "notes": notes, "video": "recording.mp4", "duration": duration,
            "fps": 60, "pixels": pixels, "scale": screen.backingScaleFactor,
            "areaPoints": ["x": area.minX - screen.frame.minX, "y": screen.frame.maxY - area.maxY,
                           "width": area.width, "height": area.height],
            "display": ["name": screen.localizedName, "points": [screen.frame.width, screen.frame.height],
                        "scale": screen.backingScaleFactor, "refreshRate": screen.maximumFramesPerSecond],
            "events": take.events, "segments": take.segments,
            "macOS": "\(version.majorVersion).\(version.minorVersion).\(version.patchVersion)",
            "appearance": NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? "dark" : "light",
            "recorded": String(ISO8601DateFormatter().string(from: Date()).prefix(10)),
            "input": "Scripted pointer, canvas events and model calls in the test host; not human or computer-use input.",
            "source": "Real-time ScreenCaptureKit recording of this process's windows only: the live toolbar, overlay, transient animation windows, an owned backdrop and an owned sample window."
        ]
        metadata.merge(extra) { $1 }
        let data = try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: folder.appendingPathComponent("meta.json"), options: .atomic)
    }

}

@available(macOS 15.0, *)
extension UXLiveStage {
    // MARK: Show and hide

    /// Drawings on the sample content, so shapes animate together with the toolbar.
    func addSampleDrawings() throws {
        let teal = RGBA(0.05, 0.58, 0.6), blue = RGBA(0.17, 0.45, 0.88), purple = RGBA(0.42, 0.33, 0.86)
        var ellipse = Element(tool: .ellipse, frame: canvasRect(338, 34, 128, 108))
        ellipse.style.color = teal; ellipse.style.width = 4; ellipse.style.roughness = 1.5
        var arrow = Element(tool: .arrow, points: [canvasPoint(design(740, 150)), canvasPoint(design(640, 96)),
                                                   canvasPoint(design(472, 92))])
        arrow.style.color = blue; arrow.style.width = 4; arrow.style.curved = true; arrow.style.headScale = 0.75
        var box = Element(tool: .rectangle, frame: canvasRect(560, 220, 324, 194))
        box.style.color = purple; box.style.width = 3; box.style.cornerRadius = 12
        let highlight = Element(tool: .highlighter, points: [canvasPoint(design(572, 108)), canvasPoint(design(930, 108))],
                                style: Style.defaults(for: .highlighter))
        var ink = Element(tool: .pen, points: (0..<16).map { canvasPoint(design(574 + CGFloat($0) * 15, 62 + 4 * sin(CGFloat($0) * 0.9))) })
        ink.style.color = blue; ink.style.width = 4
        var note = Element(tool: .text, frame: canvasRect(900, 178, 210, 40), text: "Book early!")
        note.style.color = blue; note.style.fontSize = 26; note.style.bold = true
        var counter = Element(tool: .counter, frame: canvasRect(500, 446, 32, 32), text: "1")
        counter.style.color = teal
        for element in [ellipse, arrow, box, highlight, ink, note, counter] { try session.document.add(element) }
        session.document.select([])
        model.synchronize()
    }

    func waitForVisibility(_ take: UXLiveTake) async throws {
        try await take.wait(0.05)
        try await waitUntil(take, timeout: 5) { !self.model.visibilityAnimating }
    }

    func recordShowAndHide() async throws {
        try addSampleDrawings()
        try await Task.sleep(for: .milliseconds(400))
        for style in VisibilityAnimationStyle.available {
            model.setAnimationStyle(style)
            try await Task.sleep(for: .milliseconds(300))
            let hide = MotionTiming.visibilityDuration(style, appearing: false)
            let show = MotionTiming.visibilityDuration(style, appearing: true)
            try await record("show-hide-\(style.rawValue)", title: "Show and hide: \(style.rawValue)",
                             notes: "Hide tools (Control-Option-2 path), then show them again (Control-Shift-2 path), with seven drawings on the canvas.",
                             extra: ["style": style.rawValue, "hideDuration": hide, "showDuration": show]) { take in
                try await take.wait(0.6)
                try await take.segment("hide") {
                    self.app.hideTools()
                    try await self.waitForVisibility(take)
                }
                XCTAssertTrue(model.workspaceHidden)
                try await take.wait(0.6)
                try await take.segment("show") {
                    self.app.resumeDrawing()
                    try await self.waitForVisibility(take)
                }
                XCTAssertFalse(model.workspaceHidden)
                try await take.wait(0.8)
            }
        }
        model.setAnimationStyle(.slide)
        try await Task.sleep(for: .milliseconds(300))
        let hide = MotionTiming.visibilityDuration(.slide, appearing: false)
        try await record("show-hide-reversal", title: "Show and hide: reversal",
                         notes: "Slide style. Showing again at 45% of the hide reverses from the current position.",
                         extra: ["style": "slide", "reverseAfter": hide * 0.45]) { take in
            try await take.wait(0.6)
            try await take.segment("hide, reversed mid-way") {
                self.app.hideTools()
                try await take.wait(hide * 0.45)
                take.mark("show while hiding")
                self.app.resumeDrawing()
                try await self.waitForVisibility(take)
            }
            XCTAssertFalse(model.workspaceHidden)
            try await take.wait(0.8)
        }
    }

    // MARK: Zoom

    func recordZoom() async throws {
        model.animatesScreenZoom = { true }
        let presentation = { self.session.presentation }
        try await record("zoom-screen", title: "Live zoom: screen",
                         notes: "Control-1 path from drawing: zoom ramps in around the pointer, follows it, zooms further, then ramps out.",
                         extra: ["zoomInDuration": MotionTiming.zoomInDuration, "zoomOutDuration": MotionTiming.zoomOutDuration]) { take in
            try await self.travel(to: self.design(400, 96), seconds: 0.6)
            try await take.wait(0.5)
            try await take.segment("zoom in") {
                self.model.toggleScreenZoomFromShortcut()
                try await self.waitUntil(take) {
                    self.model.zoomStartState == .active && presentation()?.isRampingScreenZoom == false
                }
            }
            take.mark("magnification \(String(format: "%.2f", self.model.magnification))")
            try await take.wait(0.8)
            try await take.segment("pointer moves while zoomed") {
                try await self.travel(to: self.design(820, 300), seconds: 1.3)
                try await self.travel(to: self.design(260, 470), seconds: 1.1)
            }
            try await take.wait(0.4)
            try await take.segment("zoom further") {
                for _ in 0..<8 {
                    self.model.scrollScreenZoom(6)
                    try await take.wait(0.06)
                }
                self.model.finishZoomGesture()
                try await take.wait(0.4)
            }
            take.mark("magnification \(String(format: "%.2f", self.model.magnification))")
            try await take.wait(0.8)
            try await take.segment("zoom out") {
                self.model.stopZoom()
                try await self.waitUntil(take) {
                    presentation()?.isExitingScreenZoom != true && presentation()?.isRampingScreenZoom != true
                }
            }
            try await take.wait(0.8)
        }
        try await record("zoom-lens", title: "Live zoom: pointer lens",
                         notes: "The pointer lens turns on, follows the pointer across fine detail and turns off.") { take in
            try await self.travel(to: self.design(180, 180), seconds: 0.5)
            try await take.wait(0.4)
            try await take.segment("lens on") {
                self.model.toggleMagnifier()
                try await self.waitUntil(take) { self.model.magnifierEnabled }
                try await take.wait(0.3)
            }
            try await take.segment("lens follows the pointer") {
                try await self.travel(to: self.design(700, 170), seconds: 1.4)
                try await self.travel(to: self.design(900, 520), seconds: 1.0)
                try await self.travel(to: self.design(300, 560), seconds: 1.0)
            }
            try await take.wait(0.4)
            try await take.segment("lens off") {
                self.model.toggleMagnifier()
                try await take.wait(0.4)
            }
            try await take.wait(0.6)
        }
    }

    // MARK: Effects

    func recordEffects() async throws {
        try await record("effects-live", title: "Live effects over the desktop",
                         notes: "Blur, pixelate and distort are dragged over owned sample content. Then the blur strength changes, a solid cover from an existing board overlaps the blur (the toolbar no longer creates solid covers), and Use apps keeps the effects on the desktop.") { take in
            try await take.wait(0.6)
            let regions: [(Tool, CGRect, String)] = [
                (.blur, CGRect(x: 40, y: 40, width: 488, height: 296), "blur"),
                (.pixelate, CGRect(x: 588, y: 446, width: 552, height: 180), "pixelate"),
                (.distort, CGRect(x: 580, y: 248, width: 290, height: 160), "distort")
            ]
            var created: [Tool: UUID] = [:]
            for (tool, rect, label) in regions {
                self.model.choose(tool)
                try await take.wait(0.3)
                try await take.segment(label) {
                    try await self.drag([self.design(rect.minX, rect.minY), self.design(rect.maxX, rect.maxY)], seconds: 0.9)
                    try await self.waitUntil(take) { self.session.effectsReady }
                }
                created[tool] = self.session.document.elements.last?.id
                try await take.wait(1.0)
            }
            let blur = try XCTUnwrap(created[.blur])
            self.model.choose(.select)
            self.session.document.select([blur])
            self.model.synchronize()
            try await take.wait(0.4)
            try await take.segment("blur strength 18 to 60 to 4") {
                for amount in stride(from: 18.0, through: 60, by: 3) {
                    self.model.applyStyle { $0.effectAmount = amount }
                    try await take.wait(0.05)
                }
                for amount in stride(from: 60.0, through: 4, by: -4) {
                    self.model.applyStyle { $0.effectAmount = amount }
                    try await take.wait(0.05)
                }
                try await self.waitUntil(take) { self.session.effectsReady }
            }
            try await take.wait(0.8)
            // The toolbar no longer creates solid covers, but covers in existing boards still render above effects.
            try await take.segment("existing solid cover over the blur") {
                var cover = Element(tool: .cover, frame: self.canvasRect(360, 210, 280, 120))
                cover.style.color = .ink
                try self.session.document.add(cover)
                self.session.document.select([])
                self.model.synchronize()
                try await take.wait(0.4)
            }
            try await take.wait(1.0)
            try await take.segment("use apps") {
                self.model.interact()
                try await take.wait(1.2)
            }
            XCTAssertTrue(self.session.document.elements.contains { $0.tool == .blur })
        }
    }

    // MARK: Region sharing

    func recordSharing() async throws {
        let sharing = model.regionSharing
        let regionTopLeft = design(12, 12), regionBottomRight = design(560, 652)
        let panelFrame = session.panel.frame
        let local = Rect(x: regionTopLeft.x - panelFrame.minX, y: panelFrame.maxY - regionTopLeft.y,
                         width: regionBottomRight.x - regionTopLeft.x, height: regionTopLeft.y - regionBottomRight.y)
        sharing.showControls()
        sharing.applyCustom(local)
        sharing.controls?.orderOut(nil)
        sharing.start()
        try await waitUntil(nil) { sharing.state == .live || self.model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        let output = try XCTUnwrap(sharing.output)
        output.window.orderFrontRegardless()
        try await Task.sleep(for: .milliseconds(600))
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let window = try XCTUnwrap(content.windows.first { $0.windowID == CGWindowID(output.window.windowNumber) })
        let folder = Self.output.appendingPathComponent("sharing-output")
        try? FileManager.default.removeItem(at: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let scale = screen.backingScaleFactor
        let configuration = SCStreamConfiguration()
        configuration.width = Int(output.window.frame.width * scale / 2) * 2
        configuration.height = Int(output.window.frame.height * scale / 2) * 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        configuration.showsCursor = false
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.colorSpaceName = CGColorSpace.sRGB
        let outputMovie = try RealtimeDesktopMovie(url: folder.appendingPathComponent("recording.mp4"),
            filter: SCContentFilter(desktopIndependentWindow: window), configuration: configuration, codec: .h264)
        let outputTake = UXLiveTake(movie: outputMovie)
        try await outputMovie.start()
        let started = ProcessInfo.processInfo.systemUptime
        var stageStarted = 0.0
        do {
            try await record("sharing-live", title: "Region sharing: the desktop while sharing",
                             notes: "The shared region outline on the desktop while the owner draws, adds a blur, hides and shows the workspace, then stops sharing. sharing-output records the output window at the same time.") { take in
                stageStarted = ProcessInfo.processInfo.systemUptime - take.time
                try await take.wait(1.0)
                try await take.segment("draw again") {
                    self.app.resumeDrawing()
                    try await take.wait(0.5)
                }
                self.model.choose(.pen)
                try await take.segment("pen stroke in the region") {
                    let points = (0...40).map { index -> CGPoint in
                        let angle = Double(index) / 40 * 2 * .pi
                        return self.design(400 + 62 * cos(angle), 96 + 52 * sin(angle))
                    }
                    try await self.drag(points, seconds: 1.2)
                }
                try await take.wait(0.8)
                self.model.choose(.arrow)
                try await take.segment("arrow in the region") {
                    try await self.drag([self.design(150, 520), self.design(330, 440)], seconds: 0.7)
                }
                try await take.wait(0.8)
                self.model.choose(.blur)
                try await take.segment("blur in the region") {
                    try await self.drag([self.design(36, 380), self.design(300, 470)], seconds: 0.8)
                    try await self.waitUntil(take) { self.session.effectsReady }
                }
                try await take.wait(1.0)
                self.model.setAnimationStyle(.magic)
                try await take.segment("hide") {
                    self.app.hideTools()
                    try await self.waitForVisibility(take)
                }
                try await take.wait(0.6)
                try await take.segment("show") {
                    self.app.resumeDrawing()
                    try await self.waitForVisibility(take)
                }
                try await take.wait(0.8)
                try await take.segment("stop sharing") {
                    sharing.stop()
                    try await take.wait(1.2)
                }
            }
            let duration = try await outputMovie.finish()
            try write(folder, take: outputTake, name: "sharing-output", title: "Region sharing: the output window",
                      notes: "The output window people share in a call, recorded with the sharing-live take. Its clock starts \(String(format: "%.3f", stageStarted - started)) s before sharing-live.",
                      duration: duration, pixels: [configuration.width, configuration.height], area: output.window.frame,
                      extra: ["startsBeforeSharingLive": stageStarted - started])
            print("UX LIVE: sharing-output, \(String(format: "%.2f", duration)) s")
        } catch {
            try? await outputMovie.cancel()
            throw error
        }
    }
}

/// Fictional sample content with fine color detail, so zoom and pixel effects visibly change it.
private final class UXLiveSampleView: NSView {
    override var isFlipped: Bool { true }

    private func color(_ red: CGFloat, _ green: CGFloat, _ blue: CGFloat) -> NSColor {
        NSColor(srgbRed: red, green: green, blue: blue, alpha: 1)
    }

    private func text(_ string: String, _ x: CGFloat, _ y: CGFloat, size: CGFloat, weight: NSFont.Weight = .regular,
                      color: NSColor = NSColor(white: 0.22, alpha: 1)) {
        (string as NSString).draw(at: CGPoint(x: x, y: y),
                                  withAttributes: [.font: NSFont.systemFont(ofSize: size, weight: weight), .foregroundColor: color])
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.white.setFill()
        bounds.fill()
        landscape(CGRect(x: 24, y: 24, width: 520, height: 330))
        text("Weekend trip ideas", 572, 24, size: 26, weight: .semibold, color: NSColor(white: 0.1, alpha: 1))
        for (index, line) in ["A quiet lake two hours north, with a short ridge walk",
                              "and a campsite that opens in May. Bring layers: the",
                              "evenings are cold and the weather changes fast.",
                              "Sample content for the Annotator UX reference."].enumerated() {
            text(line, 572, 70 + CGFloat(index) * 24, size: 16)
        }
        text("Visitors by month", 572, 222, size: 15, weight: .semibold, color: NSColor(white: 0.15, alpha: 1))
        chart(CGRect(x: 572, y: 248, width: 300, height: 160))
        chips(x: 900, y: 248)
        checklist(CGRect(x: 24, y: 376, width: 520, height: 270))
        mosaic(CGRect(x: 572, y: 432, width: 584, height: 214))
    }

    private func landscape(_ r: CGRect) {
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(roundedRect: r, xRadius: 10, yRadius: 10).addClip()
        let horizon = r.minY + r.height * 0.6
        NSGradient(starting: color(0.27, 0.55, 0.92), ending: color(0.99, 0.77, 0.52))?
            .draw(in: CGRect(x: r.minX, y: r.minY, width: r.width, height: horizon - r.minY), angle: 90)
        let sun = CGPoint(x: r.minX + 378, y: r.minY + 70)
        for step in stride(from: 5, through: 1, by: -1) {
            color(1, 0.86, 0.35).withAlphaComponent(0.12).setFill()
            let radius = 30 + CGFloat(step) * 9
            NSBezierPath(ovalIn: CGRect(x: sun.x - radius, y: sun.y - radius, width: radius * 2, height: radius * 2)).fill()
        }
        color(1, 0.85, 0.2).setFill()
        NSBezierPath(ovalIn: CGRect(x: sun.x - 30, y: sun.y - 30, width: 60, height: 60)).fill()
        for (x, y, width) in [(60.0, 50.0, 110.0), (200.0, 34.0, 80.0), (440.0, 120.0, 90.0)] {
            NSColor.white.withAlphaComponent(0.9).setFill()
            NSBezierPath(ovalIn: CGRect(x: r.minX + x, y: r.minY + y, width: width, height: 26)).fill()
            NSBezierPath(ovalIn: CGRect(x: r.minX + x + width * 0.2, y: r.minY + y - 12, width: width * 0.5, height: 30)).fill()
        }
        let ranges: [([CGFloat], NSColor)] = [
            ([0, 0.55, 0.12, 0.3, 0.27, 0.42, 0.43, 0.22, 0.6, 0.38, 0.78, 0.18, 1, 0.45], color(0.55, 0.58, 0.82)),
            ([0, 0.75, 0.18, 0.5, 0.36, 0.68, 0.55, 0.46, 0.72, 0.64, 0.9, 0.5, 1, 0.7], color(0.24, 0.55, 0.47)),
            ([0, 0.9, 0.1, 0.78, 0.25, 0.86, 0.42, 0.74, 0.62, 0.88, 0.8, 0.76, 1, 0.9], color(0.12, 0.38, 0.26))
        ]
        for (points, fill) in ranges {
            let path = NSBezierPath()
            path.move(to: CGPoint(x: r.minX, y: horizon))
            for index in stride(from: 0, to: points.count, by: 2) {
                path.line(to: CGPoint(x: r.minX + points[index] * r.width, y: r.minY + points[index + 1] * (horizon - r.minY)))
            }
            path.line(to: CGPoint(x: r.maxX, y: horizon))
            path.close()
            fill.setFill()
            path.fill()
        }
        NSGradient(starting: color(0.1, 0.45, 0.62), ending: color(0.04, 0.22, 0.38))?
            .draw(in: CGRect(x: r.minX, y: horizon, width: r.width, height: r.maxY - horizon), angle: 90)
        for line in 0..<14 {
            NSColor.white.withAlphaComponent(0.18 + 0.02 * CGFloat(line % 4)).setFill()
            let y = horizon + 8 + CGFloat(line) * 9
            let x = r.minX + 30 + CGFloat((line * 53) % 300)
            NSBezierPath(roundedRect: CGRect(x: x, y: y, width: 60 + CGFloat((line * 37) % 120), height: 2), xRadius: 1, yRadius: 1).fill()
        }
        for tree in 0..<16 {
            let x = r.minX + 12 + CGFloat(tree) * 33 + CGFloat((tree * 17) % 11)
            let height = 22 + CGFloat((tree * 29) % 18)
            let path = NSBezierPath()
            path.move(to: CGPoint(x: x, y: horizon))
            path.line(to: CGPoint(x: x + 9, y: horizon - height))
            path.line(to: CGPoint(x: x + 18, y: horizon))
            path.close()
            color(0.06, 0.26 + CGFloat(tree % 3) * 0.04, 0.16).setFill()
            path.fill()
        }
        NSGraphicsContext.restoreGraphicsState()
    }

    private func chart(_ r: CGRect) {
        NSColor(white: 0.965, alpha: 1).setFill()
        NSBezierPath(roundedRect: r, xRadius: 8, yRadius: 8).fill()
        let values: [CGFloat] = [0.32, 0.48, 0.81, 0.62, 0.95, 0.56]
        let colors = [color(0.05, 0.58, 0.6), color(0.17, 0.45, 0.88), color(0.42, 0.33, 0.86),
                      color(0.96, 0.55, 0.16), color(0.28, 0.69, 0.4), color(0.89, 0.26, 0.55)]
        for (index, value) in values.enumerated() {
            let height = value * (r.height - 46)
            colors[index].setFill()
            NSBezierPath(roundedRect: CGRect(x: r.minX + 20 + CGFloat(index) * 46, y: r.maxY - 26 - height, width: 30, height: height),
                         xRadius: 4, yRadius: 4).fill()
            text(["Jan", "Feb", "Mar", "Apr", "May", "Jun"][index], r.minX + 22 + CGFloat(index) * 46, r.maxY - 22, size: 11)
        }
    }

    private func chips(x: CGFloat, y: CGFloat) {
        let items = [("Lake", color(0.17, 0.45, 0.88)), ("Ridge walk", color(0.42, 0.33, 0.86)), ("Campsite", color(0.05, 0.58, 0.6)),
                     ("Food", color(0.96, 0.55, 0.16)), ("Gear", color(0.28, 0.69, 0.4))]
        for (index, item) in items.enumerated() {
            let frame = CGRect(x: x, y: y + CGFloat(index) * 34, width: 150, height: 26)
            item.1.withAlphaComponent(0.16).setFill()
            NSBezierPath(roundedRect: frame, xRadius: 13, yRadius: 13).fill()
            item.1.setFill()
            NSBezierPath(ovalIn: CGRect(x: frame.minX + 8, y: frame.minY + 8, width: 10, height: 10)).fill()
            text(item.0, frame.minX + 26, frame.minY + 4, size: 14, weight: .medium, color: item.1.blended(withFraction: 0.35, of: .black) ?? item.1)
        }
    }

    private func checklist(_ r: CGRect) {
        text("Packing list", r.minX, r.minY, size: 18, weight: .semibold, color: NSColor(white: 0.1, alpha: 1))
        let rows = ["Tent and ground sheet", "Two warm layers each", "Stove, pot and matches", "Water filter",
                    "Head torch and spare batteries", "Map of the ridge walk", "Snacks for the drive"]
        for (index, row) in rows.enumerated() {
            let y = r.minY + 34 + CGFloat(index) * 32
            if index.isMultiple(of: 2) {
                NSColor(white: 0.965, alpha: 1).setFill()
                NSRect(x: r.minX, y: y - 4, width: r.width, height: 30).fill()
            }
            let box = CGRect(x: r.minX + 10, y: y + 2, width: 18, height: 18)
            NSColor(white: 0.55, alpha: 1).setStroke()
            let outline = NSBezierPath(roundedRect: box, xRadius: 4, yRadius: 4)
            outline.lineWidth = 1.5
            outline.stroke()
            if index < 3 {
                color(0.17, 0.45, 0.88).setFill()
                NSBezierPath(roundedRect: box.insetBy(dx: 3, dy: 3), xRadius: 2, yRadius: 2).fill()
            }
            text(row, r.minX + 40, y + 1, size: 16)
        }
    }

    private func mosaic(_ r: CGRect) {
        text("Trail map (sample)", r.minX, r.minY - 2, size: 15, weight: .semibold, color: NSColor(white: 0.15, alpha: 1))
        let top = r.minY + 26, size: CGFloat = 23
        for row in 0..<Int((r.maxY - top) / size) {
            for column in 0..<Int(r.width / size) {
                let hue = CGFloat((row * 7 + column * 13) % 36) / 36
                let saturation = 0.45 + CGFloat((row + column * 3) % 5) * 0.1
                NSColor(calibratedHue: hue, saturation: saturation, brightness: 0.88, alpha: 1).setFill()
                NSRect(x: r.minX + CGFloat(column) * size, y: top + CGFloat(row) * size, width: size - 2, height: size - 2).fill()
            }
        }
    }
}
