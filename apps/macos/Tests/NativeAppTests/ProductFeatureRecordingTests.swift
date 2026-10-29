import AppKit
import ScreenCaptureKit
import XCTest
import SwiftUI
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

@MainActor
final class ProductFeatureRecordingTests: XCTestCase {
    func testRecordFreshProductFeatures() async throws {
        guard ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_PRODUCT_FEATURES"] == "1" else {
            throw XCTSkip("Fresh product footage is an explicit owner-run recording.")
        }
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        guard DisplayCapture.hasPermission else {
            throw CaptureFailure.streamStopped("The recording app needs its existing Screen Recording grant.")
        }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        let takes = ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_PRODUCT_TAKE"].map { [$0] } ?? ["draw", "arrow", "blur"]
        guard takes.allSatisfy({ ["draw", "arrow", "blur"].contains($0) }) else {
            throw CocoaError(.coderInvalidValue)
        }
        for take in takes { try await record(take) }
    }

    func testRecordWebsiteShowcase() async throws {
        guard ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_WEBSITE_SHOWCASE"] == "1" else {
            throw XCTSkip("Website showcase recording is an explicit owner-run task.")
        }
        guard #available(macOS 15.0, *), DisplayCapture.hasPermission else {
            throw CaptureFailure.streamStopped("The recorder needs its existing Screen Recording grant.")
        }
        guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
        let takes = ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_PRODUCT_TAKE"].map { [$0] } ??
            ["draw", "arrow", "blur", "style", "tools", "pointer", "shortcuts"]
        guard takes.allSatisfy({ ["draw", "arrow", "blur", "style", "tools", "pointer", "shortcuts"].contains($0) }) else { throw CocoaError(.coderInvalidValue) }
        for take in takes {
            if take == "pointer" || take == "shortcuts" { try await recordSettingsShowcase(take) }
            else { try await record(take, showcase: true) }
        }
    }

    @available(macOS 15.0, *)
    private func record(_ take: String, showcase: Bool = false) async throws {
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.frame.width >= 1280 && $0.frame.height >= 800 })
        let visible = screen.visibleFrame
        let width = min(showcase ? 1100 : 1500, visible.width - 80)
        let height = min(showcase ? 1000 : 1040, visible.height - 70)
        let stage = CGRect(x: visible.midX - width / 2, y: visible.midY - height / 2, width: width, height: height)
        let suite = "fresh-product-\(take)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setToolbarDockEdge(.bottom)
        model.setPropertiesShown(true)
        model.setPropertiesShown(true)
        let previousAppearance = NSApp.appearance
        NSApp.appearance = NSAppearance(named: .darkAqua)
        defer { NSApp.appearance = previousAppearance }
        let app = AppDelegate(model: model)
        app.resumeDrawing()
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
            DisplayCapture.useControlledWindowsForTesting(nil)
        }
        let session = try XCTUnwrap(model.active)
        XCTAssertNil(session.sourceImage)
        XCTAssertEqual(session.panel.screen, screen)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        toolbar.appearance = NSAppearance(named: .darkAqua)
        let backdrop = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                               backing: .buffered, defer: false)
        backdrop.isReleasedWhenClosed = false
        backdrop.isOpaque = true
        backdrop.backgroundColor = NSColor(calibratedRed: 0.075, green: 0.095, blue: 0.14, alpha: 1)
        backdrop.hasShadow = false
        backdrop.ignoresMouseEvents = true
        backdrop.level = .normal
        backdrop.orderFrontRegardless()
        let contentBottom: CGFloat = showcase ? 460 : 280
        let contentFrame = CGRect(x: stage.minX + 28, y: stage.minY + contentBottom,
                                  width: stage.width - 56, height: stage.height - contentBottom - 28)
        let notes = NSPanel(contentRect: contentFrame, styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        notes.isReleasedWhenClosed = false
        notes.title = "Fresh product sample"
        notes.isOpaque = true
        notes.hasShadow = false
        notes.ignoresMouseEvents = true
        notes.level = NSWindow.Level(rawValue: NSWindow.Level.normal.rawValue + 1)
        let content = ProductFeatureNotes(frame: CGRect(origin: .zero, size: contentFrame.size), take: take)
        notes.contentView = content
        notes.orderFrontRegardless()
        let pointerPanel = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                                   backing: .buffered, defer: false)
        pointerPanel.isReleasedWhenClosed = false
        pointerPanel.isOpaque = false
        pointerPanel.backgroundColor = .clear
        pointerPanel.hasShadow = false
        pointerPanel.ignoresMouseEvents = true
        pointerPanel.level = NSWindow.Level(rawValue: toolbar.level.rawValue + 1)
        let pointer = DesktopDemoPointer(frame: CGRect(origin: .zero, size: screen.frame.size))
        pointerPanel.contentView = pointer
        pointerPanel.orderFrontRegardless()
        defer {
            pointerPanel.orderOut(nil); pointerPanel.contentView = nil; pointerPanel.close()
            notes.orderOut(nil); notes.contentView = nil; notes.close()
            backdrop.orderOut(nil); backdrop.close()
        }
        let captureIDs = Set(try [backdrop, notes].map { try XCTUnwrap(CGWindowID(exactly: $0.windowNumber)) })
        DisplayCapture.useControlledWindowsForTesting(captureIDs)
        NativeAccessibilityNode.enable()
        var stableToolbarFrame: CGRect?
        func layout() throws {
            session.panel.ignoresMouseEvents = true
            toolbar.ignoresMouseEvents = true
            if let stableToolbarFrame {
                XCTAssertEqual(toolbar.frame.minX, stableToolbarFrame.minX, accuracy: 1)
                XCTAssertEqual(toolbar.frame.maxY, stableToolbarFrame.maxY, accuracy: 1)
                XCTAssertEqual(toolbar.frame.width, stableToolbarFrame.width, accuracy: 1)
                XCTAssertLessThanOrEqual(toolbar.frame.height, 340,
                                         "Expanded native properties must remain within the reserved toolbar area.")
                XCTAssertGreaterThanOrEqual(toolbar.frame.minY-stage.minY, 80)
            } else {
                toolbar.setFrameOrigin(CGPoint(x: stage.midX - toolbar.frame.width / 2,
                                               y: showcase ? stage.minY + 420 - toolbar.frame.height : stage.minY + 22))
            }
            XCTAssertTrue(stage.insetBy(dx: 4, dy: 4).contains(toolbar.frame), "The complete toolbar must stay in frame.")
            for window in NSApp.windows where window.isVisible && window !== toolbar && window !== notes &&
                window !== backdrop && window !== pointerPanel && window !== session.panel &&
                window.frame.width < stage.width && window.frame.height < stage.height {
                let origin = CGPoint(
                    x: min(max(window.frame.minX, stage.minX + 16), stage.maxX - window.frame.width - 16),
                    y: min(max(window.frame.minY, toolbar.frame.maxY + 10), stage.maxY - window.frame.height - 16))
                window.setFrameOrigin(origin)
                window.ignoresMouseEvents = true
                window.appearance = NSAppearance(named: .darkAqua)
            }
        }
        try layout()
        try await Task.sleep(for: .milliseconds(200))
        let shareable = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let displayID = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
        let display = try XCTUnwrap(shareable.displays.first { $0.displayID == displayID })
        let config = SCStreamConfiguration()
        config.width = Int(screen.frame.width * screen.backingScaleFactor / 2) * 2
        config.height = Int(screen.frame.height * screen.backingScaleFactor / 2) * 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.showsCursor = false
        let ownedPID = ProcessInfo.processInfo.processIdentifier
        func filter() async throws -> SCContentFilter {
            let ids = Set(NSApp.windows.filter { $0.isVisible && $0.windowNumber > 0 }.map { CGWindowID($0.windowNumber) })
            let current = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            let windows = current.windows.filter { ids.contains($0.windowID) && $0.owningApplication?.processID == ownedPID }
            guard windows.contains(where: { $0.windowID == CGWindowID(toolbar.windowNumber) }),
                  windows.contains(where: { $0.windowID == CGWindowID(notes.windowNumber) }) else {
                throw CaptureFailure.invalidFrame
            }
            return SCContentFilter(display: display, including: windows)
        }
        let root = RepositoryRoot.url
        let folder = root.appendingPathComponent(showcase ? "build/website-showcase" : "build/fresh-product")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let output = folder.appendingPathComponent("\(take)-\(UUID()).mp4")
        let movie = try RealtimeDesktopMovie(url: output, filter: try await filter(), configuration: config, codec: .hevc)
        var capturedWindows = Set(NSApp.windows.filter { $0.isVisible && $0.windowNumber > 0 }.map(\.windowNumber))
        var hand = DemoHandMotion(seed: take == "draw" ? 202609241 : take == "arrow" ? 202609242 : 202609243)
        var actions: [[String: Any]] = []
        var samples: [[String: Double]] = []
        var toolbarSamples: [[String: Any]] = []
        var minimumToolbarInset = Double.infinity
        let scale = Double(config.width) / screen.frame.width
        func sourceRect(_ rect: CGRect) -> [String: Double] {
            ["x": (rect.minX - screen.frame.minX) * scale,
             "y": (screen.frame.maxY - rect.maxY) * scale,
             "width": rect.width * scale, "height": rect.height * scale]
        }
        func pause(_ seconds: Double) async throws {
            try layout()
            let currentWindows = Set(NSApp.windows.filter { $0.isVisible && $0.windowNumber > 0 }.map(\.windowNumber))
            if currentWindows != capturedWindows {
                try await movie.update(filter: filter())
                capturedWindows = currentWindows
            }
            pointer.needsDisplay = true
            pointerPanel.displayIfNeeded()
            toolbar.displayIfNeeded()
            session.panel.displayIfNeeded()
            try await movie.wait(seconds)
            samples.append(["time": movie.elapsed, "x": pointer.point.x * scale, "y": pointer.point.y * scale])
            if showcase {
                toolbarSamples.append(["time": movie.elapsed, "rect": sourceRect(toolbar.frame)])
                minimumToolbarInset = min(minimumToolbarInset, (toolbar.frame.minY-stage.minY) * scale)
            }
        }
        func globalPointer(_ value: CGPoint) -> CGPoint {
            pointer.convert(pointerPanel.convertPoint(fromScreen: value), from: nil)
        }
        func point(_ x: Double, _ y: Double) -> Point {
            let local = CGPoint(x: x / 1000 * content.bounds.width, y: y / 600 * content.bounds.height)
            return canvasPoint(local)
        }
        func canvasPoint(_ local: CGPoint) -> Point {
            let global = notes.convertPoint(toScreen: content.convert(local, to: nil))
            return Point(session.canvas.convert(session.panel.convertPoint(fromScreen: global), from: nil))
        }
        func pointerPoint(_ value: Point) -> CGPoint {
            globalPointer(session.panel.convertPoint(toScreen: session.canvas.convert(value.cgPoint, to: nil)))
        }
        func travel(_ target: CGPoint, seconds: Double = 0.55, preview: Bool = false) async throws {
            let frames = max(8, Int(hand.duration(seconds * (showcase ? 0.65 : 1)) * 60))
            for step in hand.path(from: Point(pointer.point), to: Point(target), samples: frames, bend: 22).dropFirst() {
                pointer.point = step.cgPoint
                pointer.pressed = false
                if preview && session.canvas.constructingArrow {
                    let global = pointerPanel.convertPoint(toScreen: pointer.convert(step.cgPoint, to: nil))
                    let local = session.canvas.convert(session.panel.convertPoint(fromScreen: global), from: nil)
                    session.canvas.mouseMoved(with: try mouse(.mouseMoved, Point(local)))
                }
                try await pause(1.0 / 60)
            }
        }
        func mouse(_ type: NSEvent.EventType, _ location: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: session.canvas.convert(location.cgPoint, to: nil),
                modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: session.panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
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
        func click(_ name: String, inLibrary: Bool = false) async throws {
            let candidates = inLibrary ? libraryWindows().compactMap(\.contentView).flatMap {
                NativeAccessibilityNode.nodes(in: $0)
            } : nodes()
            let node = try XCTUnwrap(candidates.first {
                [.button, .radioButton].contains($0.accessibilityRole()) &&
                    ($0.accessibilityLabel() == name || $0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true)
            }, "Missing real native control: \(name)")
            let bounds = node.accessibilityFrame()
            XCTAssertTrue(stage.contains(bounds), "Control \(name) must be inside the take.")
            try await travel(globalPointer(CGPoint(x: bounds.midX, y: bounds.midY)))
            pointer.pressed = true
            try await pause(0.1)
            let pressed = node.accessibilityPerformPress()
            if node.accessibilityRole() == .button { XCTAssertTrue(pressed) }
            actions.append(["control": name, "time": movie.elapsed, "rect": sourceRect(bounds)])
            try await pause(0.12)
            try await movie.update(filter: filter())
            pointer.pressed = false
            try await pause(["Stroke color", "Fill color", "End arrowhead"].contains(name) ? 0.45 : 0.18)
        }
        func color(_ name: String) async throws {
            try await click("Stroke color")
            try await click(name)
        }
        func canvasDown(_ location: Point) async throws {
            NSApp.postEvent(try mouse(.leftMouseDown, location), atStart: false)
            try await pause(0.08)
            for _ in 0..<15 {
                if !nodes().contains(where: { $0.accessibilityLabel()?.contains("hex color and alpha") == true }) { break }
                try await pause(1.0 / 60)
            }
            XCTAssertFalse(nodes().contains { $0.accessibilityLabel()?.contains("hex color and alpha") == true },
                           "Starting a native drawing should dismiss the color picker without Done.")
        }
        func drag(_ start: Point, _ end: Point, seconds: Double = 1.1) async throws {
            if showcase {
                XCTAssertTrue(libraryWindows().isEmpty, "Close the native tool library before drawing.")
            }
            try await travel(pointerPoint(start))
            pointer.pressed = true
            try await canvasDown(start)
            let frames = max(12, Int(hand.duration(seconds) * 60))
            for step in hand.path(from: start, to: end, samples: frames, bend: 1.2).dropFirst() {
                pointer.point = pointerPoint(step)
                session.canvas.mouseDragged(with: try mouse(.leftMouseDragged, step))
                try await pause(1.0 / 60)
            }
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, end))
            pointer.pressed = false
            actions.append(["gesture": "drag", "time": movie.elapsed, "seconds": Double(frames) / 60])
            try await pause(showcase ? 0.2 : 0.5)
        }
        func arrowPoint(_ location: Point) async throws {
            try await travel(pointerPoint(location), seconds: 0.65, preview: true)
            pointer.pressed = true
            try await pause(0.09)
            try await canvasDown(location)
            session.canvas.mouseUp(with: try mouse(.leftMouseUp, location))
            pointer.pressed = false
            actions.append(["gesture": "arrow-point", "time": movie.elapsed])
            try await pause(showcase ? 0.08 : 0.18)
        }
        var preparedControls: [String] = []
        if showcase {
            func prepareControl(_ name: String) async throws {
                let node = try XCTUnwrap(nodes().first {
                    ($0.accessibilityRole() == .button || $0.accessibilityRole() == .checkBox) &&
                        ($0.accessibilityLabel() == name || $0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true)
                }, "Missing preparation control: \(name)")
                XCTAssertTrue(node.accessibilityPerformPress())
                preparedControls.append(name)
                try await Task.sleep(for: .milliseconds(120))
                try layout()
            }
            if take == "style" {
                try await prepareControl("Text")
                try await prepareControl("Font size 44")
            }
            if take == "tools" {
                try await prepareControl("Tool library")
                let shapes = try XCTUnwrap(nodes().first { $0.accessibilityLabel() == "Shapes" })
                _ = shapes.accessibilityPerformPress()
                try await Task.sleep(for: .milliseconds(100))
                let scroll = try visibleScroll(in: NSApp.windows.filter(\.isVisible).compactMap(\.contentView))
                for _ in 0..<10 { try sendScroll(-60, to: scroll); try await Task.sleep(for: .milliseconds(25)) }
                try await prepareControl("Grid")
                XCTAssertEqual(model.effectiveTool, .grid)
                try await prepareControl("Stroke color")
                try await prepareControl("Blue")
                try await prepareControl("Done")
            } else if take == "blur" {
                try await prepareControl("Effects")
            } else {
                try await prepareControl(take == "draw" ? "Ellipse" : take == "style" ? "Rectangle" : "Arrow")
                try await prepareControl("Width 7")
                if take != "style" { try await prepareControl("Sketchy") }
                if take == "arrow" { try await prepareControl("Curved arrow") }
                try await prepareControl("Stroke color")
                if take == "style" {
                    let gradient = try XCTUnwrap(nodes().first { $0.accessibilityLabel() == "Gradient" })
                    _ = gradient.accessibilityPerformPress()
                    try await Task.sleep(for: .milliseconds(100))
                    try await prepareControl("Lagoon gradient")
                    try await prepareControl("Done")
                    try await prepareControl("Fill color")
                    try await prepareControl("Fill shape")
                    try await prepareControl("Done")
                    XCTAssertEqual(model.style.strokeGradient, GradientPreset.lagoon.gradient)
                    XCTAssertEqual(model.style.fill?.alpha, 0.4)
                } else {
                    try await prepareControl(take == "draw" ? "Teal" : "Blue")
                    try await prepareControl("Done")
                }
            }
            XCTAssertEqual(ToolbarPropertiesMode(model: model), .shown)
            let floating = try XCTUnwrap(toolbar as? InspectorPanel)
            let start = CGPoint(x: toolbar.frame.midX, y: toolbar.frame.midY)
            let destination = CGPoint(x: stage.midX, y: stage.minY + 420 - toolbar.frame.height / 2)
            floating.beginToolbarDrag(at: start)
            floating.updateToolbarDrag(to: destination)
            floating.endToolbarDrag(at: destination)
            try await Task.sleep(for: .milliseconds(150))
            try layout()
            stableToolbarFrame = toolbar.frame
            try await movie.update(filter: filter())
        }
        pointer.point = showcase ? pointerPoint(point(200, 220)) :
            globalPointer(CGPoint(x: toolbar.frame.minX + 40, y: toolbar.frame.midY))
        try await movie.start()
        do {
            try await pause(showcase ? 0.15 : 0.7)
            if take == "draw" {
                if !showcase {
                try await click("Ellipse")
                try await click("Width 7")
                try await click("Sketchy")
                try await color("Teal")
                }
                let target = content.textBounds("Friday, 10:00", x: 75, y: 245, size: 39, weight: .semibold)
                let oval = target.insetBy(dx: -target.width * 0.13, dy: -target.height * 0.48)
                try await drag(canvasPoint(oval.origin), canvasPoint(CGPoint(x: oval.maxX, y: oval.maxY)), seconds: 1.35)
                let ellipse = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(ellipse.tool, .ellipse)
                XCTAssertEqual(ellipse.style.roughness, 2)
                XCTAssertEqual(ellipse.frame.center.x, canvasPoint(CGPoint(x: target.midX, y: target.midY)).x, accuracy: 1)
                XCTAssertEqual(ellipse.frame.center.y, canvasPoint(CGPoint(x: target.midX, y: target.midY)).y, accuracy: 1)
                try await click("Highlighter")
                let line = content.textBounds("Make the first step obvious.", x: 592, y: 275, size: 20)
                try await drag(canvasPoint(CGPoint(x: line.minX - 4, y: line.midY)),
                               canvasPoint(CGPoint(x: line.maxX + 4, y: line.midY)), seconds: 1.2)
                XCTAssertEqual(session.document.elements.last?.tool, .highlighter)
            } else if take == "arrow" {
                if !showcase {
                try await click("Arrow")
                try await click("Width 7")
                try await click("Sketchy")
                try await click("Curved arrow")
                try await color("Blue")
                }
                let originalHead = model.style.endHead
                for p in [point(283, 303), point(415, 185), point(558, 380), point(725, 300)] {
                    try await arrowPoint(p)
                }
                session.canvas.keyDown(with: try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero,
                    modifierFlags: [], timestamp: movie.elapsed, windowNumber: session.panel.windowNumber,
                    context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36)))
                try await pause(0.4)
                XCTAssertTrue(model.style.curved)
                XCTAssertEqual(session.document.elements.last?.points.count, 4)
                XCTAssertEqual(model.style.roughness, 2)
                try await pause(0.7)
                let arrow = try XCTUnwrap(session.document.elements.last)
                let adjusted = point(466, 141)
                try await drag(arrow.points[1], adjusted, seconds: 1.2)
                let edited = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(edited.id, arrow.id)
                XCTAssertEqual(edited.points[1].x, adjusted.x, accuracy: 1)
                XCTAssertEqual(edited.points[1].y, adjusted.y, accuracy: 1)
                XCTAssertEqual(edited.style.endHead, originalHead)
                actions.append(["gesture": "edit-arrow-point", "time": movie.elapsed])
            } else if take == "style" {
                try await drag(point(170, 210), point(830, 400), seconds: 1)
                let rectangle = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(rectangle.style.strokeGradient, GradientPreset.lagoon.gradient)
                try await click("Text")
                let center = rectangle.frame.center
                try await travel(pointerPoint(center))
                try await canvasDown(center)
                session.canvas.mouseUp(with: try mouse(.leftMouseUp, center))
                let editor = try XCTUnwrap(session.canvas.subviews.compactMap { $0 as? NSTextView }.first)
                try await travel(pointerPoint(point(900, 470)), seconds: 0.3)
                for character in "A useful idea." {
                    editor.insertText(String(character), replacementRange: NSRange(location: NSNotFound, length: 0))
                    session.canvas.textDidChange(Notification(name: NSText.didChangeNotification, object: editor))
                    actions.append(["gesture": "type-character", "text": String(character), "time": movie.elapsed])
                    try await pause(0.075)
                }
                XCTAssertEqual(editor.string, "A useful idea.")
                try await pause(0.7)
                session.canvas.commitTextIfNeeded()
                XCTAssertEqual(session.document.elements.last?.label?.followsStroke, true)
                actions.append(["gesture": "matching-gradient-label", "time": movie.elapsed])
            } else if take == "tools" {
                try await click("Tool library")
                try await click("Shapes", inLibrary: true)
                let scroll = try visibleScroll(in: NSApp.windows.filter(\.isVisible).compactMap(\.contentView))
                try await scrollRecording(scroll, pixels: 480, movie: movie, pointer: pointer, pointerPanel: pointerPanel)
                try await scrollRecording(scroll, pixels: -480, movie: movie, pointer: pointer, pointerPanel: pointerPanel)
                actions.append(["gesture": "scroll-tools", "time": movie.elapsed])
                for category in ["Extras", "Focus", "Draw", "Shapes"] {
                    try await click(category, inLibrary: true)
                    try await pause(0.25)
                }
                let shapesScroll = try visibleScroll(in: libraryWindows().compactMap(\.contentView))
                try await scrollRecording(shapesScroll, pixels: -480, movie: movie, pointer: pointer, pointerPanel: pointerPanel)
                try await click("Grid", inLibrary: true)
                XCTAssertEqual(model.effectiveTool, .grid)
                for _ in 0..<20 {
                    if libraryWindows().isEmpty { break }
                    try await pause(0.05)
                }
                XCTAssertTrue(libraryWindows().isEmpty, "Choosing Grid must dismiss the library before the drawing.")
                try await drag(point(170, 210), point(820, 420), seconds: 0.9)
                XCTAssertEqual(session.document.elements.last?.tool, .grid)
            } else {
                if !showcase { try await click("Effects") }
                XCTAssertEqual(model.effectiveTool, .blur)
                try await drag(point(57, 192), point(553, 433), seconds: 1.5)
                for _ in 0..<60 {
                    if session.effectsReady { break }
                    try await pause(0.1)
                }
                XCTAssertTrue(session.effectsReady, "The fresh take must contain live native blur.")
                XCTAssertEqual(session.document.elements.last?.tool, .blur)
            }
            try await pause(showcase ? 0.6 : 1.3)
            try await click("Use apps")
            XCTAssertEqual(model.mode, .interact)
            try await travel(globalPointer(CGPoint(x: stage.maxX - 32, y: stage.minY + 12)), seconds: 0.6)
            try await pause(showcase ? 0.7 : 1.2)
            let duration = try await movie.finish()
            let metadata: [String: Any] = [
                "take": take, "path": output.path, "duration": duration,
                "width": config.width, "height": config.height, "contentRect": sourceRect(stage),
                "toolbarRect": sourceRect(toolbar.frame), "actions": actions, "pointerSamples": samples,
                "source": "Fresh real-time ScreenCaptureKit recording of the live native desktop overlay and real toolbar. Isolated test-owned windows only.",
                "input": "Timed native UI actions and canvas events, with a deterministic scripted pointer. Not a human or computer-use-provider recording.",
                "imageWorkspace": false, "sourceRevision": showcase ? "website-showcase-working-tree" : "working-tree",
                "toolbarAppearance": "dark", "ink": take == "draw" ? "teal" : take == "arrow" ? "blue" :
                    take == "style" ? "lagoon gradient" : take == "tools" ? "blue grid" : "native blur",
                "preparedControls": preparedControls, "toolbarSamples": toolbarSamples,
                "toolbarAnchorStable": showcase, "toolbarPropertiesMode": showcase ? "full" : "legacy",
                "toolbarBottomInset": showcase ? minimumToolbarInset : 22
            ]
            try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
                .write(to: folder.appendingPathComponent("latest-\(take).json"), options: .atomic)
            print("FRESH TAKE \(take): \(output.path)")
        } catch {
            do { try await movie.cancel() } catch { XCTFail("Recording cleanup failed: \(error)") }
            throw error
        }
    }

    private func descendants<T: NSView>(_ type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { self.descendants(type, in: $0) }
    }

    private func visibleScroll(in roots: [NSView]) throws -> NSScrollView {
        try XCTUnwrap(roots.flatMap { self.descendants(NSScrollView.self, in: $0) }.first {
            !$0.isHiddenOrHasHiddenAncestor && ($0.documentView?.frame.height ?? 0) > $0.contentSize.height + 20
        }, "The real scrollable tool list must be available.")
    }

    private func sendScroll(_ pixels: Int32, to view: NSScrollView) throws {
        let event = try XCTUnwrap(CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1,
                                         wheel1: pixels, wheel2: 0, wheel3: 0))
        view.scrollWheel(with: try XCTUnwrap(NSEvent(cgEvent: event)))
    }

    @available(macOS 15.0, *)
    private func scrollRecording(_ view: NSScrollView, pixels: Int32, movie: RealtimeDesktopMovie,
                                 pointer: DesktopDemoPointer, pointerPanel: NSPanel) async throws {
        let window = try XCTUnwrap(view.window)
        let global = window.convertPoint(toScreen: view.convert(CGPoint(x: view.bounds.midX, y: view.bounds.midY), to: nil))
        let target = pointer.convert(pointerPanel.convertPoint(fromScreen: global), from: nil)
        var hand = DemoHandMotion(seed: 202609246)
        for step in hand.path(from: Point(pointer.point), to: Point(target), samples: 12, bend: 5).dropFirst() {
            pointer.point = step.cgPoint; pointer.needsDisplay = true
            try await movie.wait(1.0/60)
        }
        let before = view.contentView.bounds.origin
        for _ in 0..<8 {
            try sendScroll(pixels/8, to: view)
            view.window?.displayIfNeeded()
            try await movie.wait(0.05)
        }
        XCTAssertNotEqual(view.contentView.bounds.origin, before, "Native wheel events must actually scroll the content.")
        try await movie.wait(0.15)
    }

    @available(macOS 15.0, *)
    private func recordSettingsShowcase(_ take: String) async throws {
            let screen = try XCTUnwrap(NSScreen.main)
            let suite = "website-pointer-\(UUID())"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            let model = WorkspaceModel(defaults: defaults)
            defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
            let panel = NSPanel(contentRect: CGRect(x: screen.visibleFrame.midX-420, y: screen.visibleFrame.midY-350,
                                                   width: 840, height: 700),
                                styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.title = "Annotator Settings"
            panel.appearance = NSAppearance(named: .darkAqua)
            let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false,
                                                                 initialTab: take == "shortcuts" ? .toolbar : .pointer))
            panel.contentView = host
            panel.orderFrontRegardless()
            let pointerPanel = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                                       backing: .buffered, defer: false)
            pointerPanel.isReleasedWhenClosed = false; pointerPanel.isOpaque = false
            pointerPanel.backgroundColor = .clear; pointerPanel.hasShadow = false
            pointerPanel.ignoresMouseEvents = true; pointerPanel.level = .floating
            let pointer = DesktopDemoPointer(frame: CGRect(origin: .zero, size: screen.frame.size))
            pointerPanel.contentView = pointer; pointerPanel.orderFrontRegardless()
            defer {
                pointerPanel.orderOut(nil); pointerPanel.contentView = nil; pointerPanel.close()
                panel.orderOut(nil); panel.contentView = nil; panel.close()
            }
            NativeAccessibilityNode.enable()
            try await Task.sleep(for: .milliseconds(250))
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            let displayID = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
            let display = try XCTUnwrap(content.displays.first { $0.displayID == displayID })
            let ids = Set([CGWindowID(panel.windowNumber), CGWindowID(pointerPanel.windowNumber)])
            let windows = content.windows.filter { ids.contains($0.windowID) }
            XCTAssertEqual(windows.count, 2)
            let config = SCStreamConfiguration()
            config.width = Int(screen.frame.width * screen.backingScaleFactor)
            config.height = Int(screen.frame.height * screen.backingScaleFactor)
            config.minimumFrameInterval = CMTime(value: 1, timescale: 60); config.showsCursor = false
            let root = RepositoryRoot.url
            let folder = root.appendingPathComponent("build/website-showcase")
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let output = folder.appendingPathComponent("\(take)-\(UUID()).mp4")
            let movie = try RealtimeDesktopMovie(url: output,
                filter: SCContentFilter(display: display, including: windows), configuration: config, codec: .hevc)
            let scale = Double(config.width) / screen.frame.width
            func rect(_ value: CGRect) -> [String: Double] {
                ["x": (value.minX-screen.frame.minX)*scale, "y": (screen.frame.maxY-value.maxY)*scale,
                 "width": value.width*scale, "height": value.height*scale]
            }
            func node(_ label: String) throws -> NativeAccessibilityNode {
                try XCTUnwrap(NativeAccessibilityNode.nodes(in: panel).first { node in
                    let title = node.object.responds(to: NSSelectorFromString("accessibilityTitle"))
                        ? node.object.value(forKey: "accessibilityTitle") as? String : nil
                    return node.accessibilityLabel() == label || title == label || node.accessibilityValue() as? String == label
                }, label)
            }
            var actions: [[String: Any]] = []
            var hand = DemoHandMotion(seed: 202609245)
            func travelTo(_ bounds: CGRect) async throws {
                XCTAssertTrue(panel.frame.contains(bounds))
                let target = pointer.convert(pointerPanel.convertPoint(fromScreen: CGPoint(x: bounds.midX, y: bounds.midY)), from: nil)
                for step in hand.path(from: Point(pointer.point), to: Point(target), samples: 16, bend: 8).dropFirst() {
                    pointer.point = step.cgPoint; pointer.needsDisplay = true
                    try await movie.wait(1.0/60)
                }
            }
            func click(_ label: String) async throws {
                let target = try node(label), bounds = target.accessibilityFrame()
                try await travelTo(bounds)
                pointer.pressed = true; pointer.needsDisplay = true
                _ = target.accessibilityPerformPress()
                actions.append(["control": label, "time": movie.elapsed, "rect": rect(bounds)])
                try await movie.wait(0.12)
                pointer.pressed = false; pointer.needsDisplay = true
                try await movie.wait(0.22)
            }
            if take == "pointer" {
                let sizeControl = try node("Cursor size")
                for _ in 0..<8 { _ = sizeControl.accessibilityPerformIncrement() }
                XCTAssertGreaterThan(model.cursorHighlightSettings.diameter, 80)
            }
            pointer.point = CGPoint(x: panel.frame.midX, y: screen.frame.maxY-panel.frame.midY)
            try await movie.start()
            do {
                if take == "shortcuts" {
                    // SwiftUI drags need a key window; this nonactivating panel becomes key without activating the app.
                    panel.makeKeyAndOrderFront(nil)
                    func identified(_ id: String) throws -> CGRect {
                        try XCTUnwrap(NativeAccessibilityNode.nodes(in: panel).first { $0.accessibilityIdentifier() == id }, id)
                            .accessibilityFrame()
                    }
                    func send(_ type: NSEvent.EventType, _ point: CGPoint) throws {
                        panel.sendEvent(try XCTUnwrap(NSEvent.mouseEvent(with: type, location: panel.convertPoint(fromScreen: point),
                            modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                            context: nil, eventNumber: 0, clickCount: 1, pressure: type == .leftMouseUp ? 0 : 1)))
                    }
                    func drag(from bounds: CGRect, to end: CGPoint) async throws {
                        // Grab the icon, above the key under it.
                        let start = CGPoint(x: bounds.midX, y: bounds.maxY - 18)
                        try await travelTo(CGRect(origin: start, size: .zero))
                        // Another app may have taken key status since the last step; drags start only in the key window.
                        panel.makeKeyAndOrderFront(nil)
                        XCTAssertTrue(panel.isKeyWindow)
                        pointer.pressed = true; pointer.needsDisplay = true
                        try send(.leftMouseDown, start)
                        try await movie.wait(0.1)
                        for step in hand.path(from: Point(start), to: Point(end), samples: 42, bend: 14).dropFirst() {
                            try send(.leftMouseDragged, step.cgPoint)
                            pointer.point = pointer.convert(pointerPanel.convertPoint(fromScreen: step.cgPoint), from: nil)
                            pointer.needsDisplay = true
                            try await movie.wait(1.0/60)
                        }
                        try await movie.wait(0.12)
                        try send(.leftMouseDragged, end)
                        try send(.leftMouseUp, end)
                        pointer.pressed = false; pointer.needsDisplay = true
                        try await movie.wait(0.45)
                    }
                    try await movie.wait(0.4)
                    let strip = try identified("toolbar-arrangement")
                    try await drag(from: try identified("more-action-spotlight"), to: CGPoint(x: strip.maxX - 26, y: strip.midY))
                    XCTAssertEqual(model.toolbarItems.last, .action(.spotlight))
                    actions.append(["gesture": "drag-into-toolbar", "item": "Spotlight", "time": movie.elapsed])
                    try await click("Spotlight key")
                    try await movie.wait(0.35)
                    let key = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                        timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber, context: nil,
                        characters: "j", charactersIgnoringModifiers: "j", isARepeat: false, keyCode: 38))
                    panel.firstResponder?.keyDown(with: key)
                    try await movie.wait(0.7)
                    // L belongs to the laser pointer, so Spotlight gets a free key.
                    XCTAssertEqual(model.shortcut(for: .action(.spotlight)), LocalShortcut(key: "j"))
                    actions.append(["gesture": "type-shortcut", "text": "J", "time": movie.elapsed])
                    let line = try identified("toolbar-tool-line")
                    try await drag(from: line, to: CGPoint(x: line.midX + 40, y: strip.minY - 150))
                    XCTAssertFalse(model.toolbarTools.contains(.line))
                    actions.append(["gesture": "drag-out-of-toolbar", "item": "Line", "time": movie.elapsed])
                    try await movie.wait(1.1)
                } else {
                try await click("Diamond")
                XCTAssertEqual(model.cursorHighlightSettings.shape, .diamond)
                try await click("Cyan")
                let glow = try node("Cursor glow")
                try await travelTo(glow.accessibilityFrame())
                pointer.pressed = true
                for _ in 0..<8 {
                    _ = glow.accessibilityPerformIncrement()
                    let bounds = glow.accessibilityFrame()
                    let position = CGPoint(x: bounds.minX + bounds.width * model.cursorHighlightSettings.resolvedGlow / 24,
                                           y: bounds.midY)
                    pointer.point = pointer.convert(pointerPanel.convertPoint(fromScreen: position), from: nil)
                    pointer.needsDisplay = true
                    try await movie.wait(0.06)
                }
                pointer.pressed = false
                XCTAssertGreaterThan(model.cursorHighlightSettings.resolvedGlow, 0)
                actions.append(["control": "Cursor glow", "time": movie.elapsed, "rect": rect(glow.accessibilityFrame())])
                try await click("Preview click animation")
                try await movie.wait(0.55)
                try await click("Squircle")
                try await click("Ripple")
                XCTAssertEqual(model.cursorHighlightSettings.resolvedClickEffect, .ripple)
                try await click("Preview click animation")
                try await movie.wait(0.7)
                try await click("Custom color")
                let scroll = try visibleScroll(in: [host])
                try await scrollRecording(scroll, pixels: -240, movie: movie, pointer: pointer, pointerPanel: pointerPanel)
                actions.append(["gesture": "scroll-color-controls", "time": movie.elapsed])
                let hue = try node("Color hue")
                try await travelTo(hue.accessibilityFrame())
                let previousColor = model.cursorHighlightSettings.color
                for _ in 0..<3 { _ = hue.accessibilityPerformIncrement(); try await movie.wait(0.06) }
                XCTAssertNotEqual(model.cursorHighlightSettings.color, previousColor)
                try await scrollRecording(scroll, pixels: 240, movie: movie, pointer: pointer, pointerPanel: pointerPanel)
                try await click("Custom color")
                try await click("Preview click animation")
                try await movie.wait(0.6)
                }
                let duration = try await movie.finish()
                let metadata: [String: Any] = [
                    "take": take, "path": output.path, "duration": duration,
                    "width": config.width, "height": config.height, "contentRect": rect(panel.frame),
                    "toolbarRect": rect(panel.frame), "actions": actions,
                    "preparedControls": take == "pointer" ? ["Cursor size"] : [],
                    "imageWorkspace": false, "toolbarAppearance": "dark", "ink": take == "pointer" ? "cyan halo" : "native shortcuts",
                    "input": take == "shortcuts" ?
                        "Real native Settings drags and typing, with native mouse and key events and a scripted pointer. Test-owned windows only." :
                        "Real native Settings navigation, scrolling and edits, with timed accessibility actions and a scripted pointer. Test-owned windows only."
                ]
                try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
                    .write(to: folder.appendingPathComponent("latest-\(take).json"), options: .atomic)
            } catch { try await movie.cancel(); throw error }
    }
}

@MainActor
private final class ProductFeatureNotes: NSView {
    let take: String
    init(frame: CGRect, take: String) { self.take = take; super.init(frame: frame) }
    required init?(coder: NSCoder) { nil }
    override var isFlipped: Bool { true }
    func textBounds(_ value: String, x: Double, y: Double, size: Double,
                    weight: NSFont.Weight = .regular) -> CGRect {
        let sx = bounds.width / 1000, sy = bounds.height / 600
        let font = NSFont.systemFont(ofSize: size * sx, weight: weight)
        return CGRect(origin: CGPoint(x: x * sx, y: y * sy),
                      size: (value as NSString).size(withAttributes: [.font: font]))
    }
    override func draw(_ dirtyRect: NSRect) {
        (take == "style" ? NSColor(calibratedRed: 0.09, green: 0.12, blue: 0.17, alpha: 1) :
            NSColor(calibratedRed: 0.97, green: 0.97, blue: 0.94, alpha: 1)).setFill()
        bounds.fill()
        let sx = bounds.width / 1000, sy = bounds.height / 600
        func text(_ value: String, _ x: Double, _ y: Double, _ size: Double, _ weight: NSFont.Weight = .regular) {
            (value as NSString).draw(at: CGPoint(x: x * sx, y: y * sy),
                withAttributes: [.font: NSFont.systemFont(ofSize: size * sx, weight: weight),
                                 .foregroundColor: take == "style" ? NSColor.white :
                                    NSColor(calibratedRed: 0.13, green: 0.17, blue: 0.22, alpha: 1)])
        }
        text("Studio notes", 52, 30, 14, .medium)
        text("Sample content", 842, 30, 12)
        if take == "draw" {
            text("Let's check the plan.", 52, 83, 46, .semibold)
            text("TEAM REVIEW", 68, 191, 12, .medium)
            text("Friday, 10:00", 75, 245, 39, .semibold)
            text("Walk through it together.", 74, 365, 21)
            text("Before the review", 592, 204, 21, .semibold)
            text("Make the first step obvious.", 592, 275, 20)
            text("Keep the important bit in view.", 592, 336, 20)
            text("Leave room for questions.", 592, 397, 20)
        } else if take == "arrow" {
            text("Follow the request.", 52, 83, 46, .semibold)
            text("01", 74, 230, 12, .medium)
            text("Your app", 74, 263, 33, .semibold)
            text("One incoming link.", 74, 333, 18)
            text("02", 742, 235, 12, .medium)
            text("The API", 742, 279, 33, .semibold)
            text("A clear destination.", 742, 354, 18)
        } else if take == "tools" {
            text("Room for the whole idea.", 52, 83, 42, .semibold)
            text("Shapes, diagrams and a little structure.", 180, 448, 22)
        } else if take == "style" {
            text("Make the idea stand out.", 52, 83, 42, .semibold)
            text("Change the color. Keep the idea.", 180, 448, 22)
        } else {
            text("Keep the focus here.", 52, 83, 46, .semibold)
            func rect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) -> CGRect {
                CGRect(x: x * sx, y: y * sy, width: w * sx, height: h * sy)
            }
            let landscape = rect(55, 190, 500, 245)
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(roundedRect: landscape, xRadius: 16, yRadius: 16).addClip()
            NSGradient(starting: NSColor(calibratedRed: 0.25, green: 0.55, blue: 0.76, alpha: 1),
                       ending: NSColor(calibratedRed: 0.75, green: 0.85, blue: 0.57, alpha: 1))?.draw(in: landscape, angle: 90)
            NSColor(calibratedRed: 0.96, green: 0.85, blue: 0.34, alpha: 1).setFill()
            NSBezierPath(ovalIn: rect(429, 219, 56, 56)).fill()
            for (baseline, color) in [
                (343.0, NSColor(calibratedRed: 0.25, green: 0.50, blue: 0.47, alpha: 1)),
                (397.0, NSColor(calibratedRed: 0.12, green: 0.32, blue: 0.35, alpha: 1))
            ] {
                let hill = NSBezierPath()
                hill.move(to: CGPoint(x: 55 * sx, y: baseline * sy))
                hill.curve(to: CGPoint(x: 555 * sx, y: (baseline - 48) * sy),
                           controlPoint1: CGPoint(x: 210 * sx, y: (baseline - 148) * sy),
                           controlPoint2: CGPoint(x: 344 * sx, y: (baseline + 94) * sy))
                hill.line(to: CGPoint(x: 555 * sx, y: 435 * sy))
                hill.line(to: CGPoint(x: 55 * sx, y: 435 * sy))
                hill.close(); color.setFill(); hill.fill()
            }
            NSGraphicsContext.restoreGraphicsState()
            text("A little room to explore.", 72, 454, 23, .semibold)
            text("Moodboard / early direction", 74, 497, 14)
            NSColor(calibratedRed: 0.77, green: 0.79, blue: 0.94, alpha: 1).setFill()
            NSBezierPath(roundedRect: rect(609, 175, 343, 300), xRadius: 16, yRadius: 16).fill()
            NSColor(calibratedRed: 0.34, green: 0.36, blue: 0.65, alpha: 1).setFill()
            NSBezierPath(ovalIn: rect(635, 199, 50, 50)).fill()
            text("SAMPLE CONTACT", 704, 209, 12, .medium)
            text("Demo partner", 637, 265, 25, .semibold)
            text("hello@example.invalid", 638, 327, 18)
            text("+1 202 555 0142", 638, 371, 18)
            text("Workshop / Thursday", 638, 422, 15)
        }
        text("Fictional content for this recording. No private data.", 52, 552, 12)
    }
}
