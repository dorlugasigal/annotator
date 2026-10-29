import AppKit
import AVFoundation
import ScreenCaptureKit
import XCTest
import AnnotationCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class NativeDesktopRecordingTests: XCTestCase {
    func testRecordActualDesktopOverlay() async throws {
        let interview = ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_INTERVIEW"] == "1"
        let nativeQuality = ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_NATIVE_QUALITY"] == "1"
        let frameRate = nativeQuality ? 60.0 : 30.0
        let expanded = ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_AD"] == "1" || interview
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_DESKTOP"] == "1" || expanded,
                          "Recording the controlled live-desktop demo is opt-in.")
        guard #available(macOS 15.0, *) else { throw XCTSkip("This recording tool requires macOS 15; the app remains macOS 14+.") }
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked)
        let suite = "live-desktop-demo-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setToolbarDockEdge(.bottom)
        model.setPropertiesShown(true)
        model.setPropertiesShown(true)
        let app = AppDelegate(model: model)
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.resumeDrawing()
        let session = try XCTUnwrap(model.active)
        XCTAssertNil(session.sourceImage, "Marketing must use the actual desktop session, never an image-backed fixture.")
        let screen = try XCTUnwrap(session.panel.screen)
        let displayID = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        let controls = try XCTUnwrap(toolbar.contentView)
        toolbar.appearance = NSAppearance(named: .aqua)
        let backdrop = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        backdrop.isReleasedWhenClosed = false
        backdrop.title = "Controlled demo desktop"
        backdrop.backgroundColor = NSColor(calibratedRed: 0.91, green: 0.93, blue: 0.91, alpha: 1)
        backdrop.isOpaque = true
        backdrop.hasShadow = false
        backdrop.ignoresMouseEvents = true
        backdrop.level = .normal
        backdrop.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backdrop.orderFrontRegardless()
        let visible = screen.visibleFrame
        let notesWidth = min(1000, visible.width - 100)
        let notesFrame = CGRect(x: visible.midX - notesWidth/2, y: visible.minY + (interview ? 280 : 340),
                                width: notesWidth, height: min(interview ? 540 : 420, visible.height - (interview ? 320 : 380)))
        let notes = NSPanel(contentRect: notesFrame, styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        notes.isReleasedWhenClosed = false
        notes.title = "Demo notes"
        notes.appearance = NSAppearance(named: .aqua)
        notes.backgroundColor = .white
        notes.isOpaque = true
        notes.ignoresMouseEvents = true
        notes.level = NSWindow.Level(rawValue: NSWindow.Level.normal.rawValue + 1)
        let noteView = DesktopDemoNotes(frame: CGRect(origin: .zero, size: notesFrame.size))
        noteView.interview = interview
        noteView.showSampleKey = !nativeQuality
        notes.contentView = noteView
        notes.orderFrontRegardless()
        if interview {
            DisplayCapture.useControlledWindowsForTesting(Set(try [backdrop, notes].map {
                try XCTUnwrap(CGWindowID(exactly: $0.windowNumber))
            }))
        }
        defer { if interview { DisplayCapture.useControlledWindowsForTesting(nil) } }
        let pointerPanel = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        pointerPanel.isReleasedWhenClosed = false
        pointerPanel.title = "Demo pointer"
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
        func releasePhysicalInput() {
            session.panel.ignoresMouseEvents = true
            toolbar.ignoresMouseEvents = true
        }
        releasePhysicalInput()
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(250))
        if let pen = NativeAccessibilityNode.nodes(in: controls).first(where: { $0.accessibilityLabel() == "Pen" && $0.accessibilityRole() == .button }) {
            let frame = pen.accessibilityFrame()
            pointer.point = pointer.convert(pointerPanel.convertPoint(fromScreen: CGPoint(x: frame.midX, y: frame.midY)), from: nil)
            pointer.needsDisplay = true
        }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let display = try XCTUnwrap(content.displays.first { $0.displayID == displayID })
        let ownedIDs = Set([backdrop, notes, session.panel, toolbar, pointerPanel].map { CGWindowID($0.windowNumber) })
        let owned = content.windows.filter {
            ownedIDs.contains($0.windowID) && $0.owningApplication?.processID == ProcessInfo.processInfo.processIdentifier
        }
        XCTAssertEqual(owned.count, ownedIDs.count)
        guard owned.count == ownedIDs.count else { throw CaptureFailure.invalidFrame }
        var filter = SCContentFilter(display: display, including: owned)
        var filterIDs = ownedIDs
        let configuration = SCStreamConfiguration()
        configuration.width = nativeQuality ? Int((screen.frame.width * screen.backingScaleFactor / 2).rounded()) * 2 : 2880
        configuration.height = Int((screen.frame.height / screen.frame.width * Double(configuration.width) / 2).rounded()) * 2
        configuration.showsCursor = false
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: Int32(frameRate))
        let directory = RepositoryRoot.url.appendingPathComponent("build/native-desktop")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let output = directory.appendingPathComponent("desktop-\(UUID()).mp4")
        let movie = expanded ? nil : try NativeDemoMovieWriter(url: output, width: configuration.width, height: configuration.height)
        let realtime = expanded ? try RealtimeDesktopMovie(url:output,filter:filter,configuration:configuration,
                                                          codec:nativeQuality ? .hevc : .h264) : nil
        try await realtime?.start()
        var elapsed = 0.0
        func recordingTime() -> Double { realtime?.elapsed ?? elapsed }
        var chapters: [[String: Any]] = []
        var pointerSamples: [[String: Double]] = []
        var actions: [[String: Any]] = []
        var toolbarFrames: [[String: Any]] = []
        var previousToolbarFrame = CGRect.null
        let pixelScale = Double(configuration.width) / screen.frame.width
        func sourceRect(_ frame: CGRect) -> [String: Double] {
            ["x":(frame.minX-screen.frame.minX)*pixelScale,
             "y":(screen.frame.maxY-frame.maxY)*pixelScale,
             "width":frame.width*pixelScale,"height":frame.height*pixelScale]
        }
        var hand = DemoHandMotion(seed: 20260912)
        var recordedBounds = notes.frame.union(toolbar.frame)
        let recordingArea = recordedBounds.insetBy(dx: -160, dy: -80)
        var explainedControls = Set<String>()
        func pause(_ seconds: Double) async throws {
            releasePhysicalInput()
            if toolbar.frame != previousToolbarFrame {
                toolbarFrames.append(["time":recordingTime(),"rect":sourceRect(toolbar.frame)])
                previousToolbarFrame = toolbar.frame
            }
            for window in NSApp.windows where window.isVisible && window !== backdrop
                && window !== pointerPanel && window !== session.panel
                && window.frame.width < screen.frame.width * 0.95
                && window.frame.height < screen.frame.height * 0.9 && window.frame.intersects(recordingArea) {
                recordedBounds = recordedBounds.union(window.frame)
            }
            let visibleIDs = NSApp.windows.filter { $0.isVisible && $0.windowNumber > 0 }
                .compactMap { CGWindowID(exactly: $0.windowNumber) }
            let currentIDs = ownedIDs.union(visibleIDs)
            if currentIDs != filterIDs {
                let current = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
                let windows = current.windows.filter {
                    currentIDs.contains($0.windowID) && $0.owningApplication?.processID == ProcessInfo.processInfo.processIdentifier
                }
                filter = SCContentFilter(display: display, including: windows)
                filterIDs = currentIDs
                try await realtime?.update(filter:filter)
            }
            session.panel.displayIfNeeded()
            toolbar.displayIfNeeded()
            pointerPanel.displayIfNeeded()
            let frames = max(1, Int((seconds * frameRate).rounded()))
            if let realtime { try await realtime.wait(seconds) }
            else if let movie {
                try await Task.sleep(for: .milliseconds(35))
                let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
                try await movie.append(image, repeats: frames)
            }
            pointerSamples.append(["time":recordingTime(), "x":pointer.point.x * Double(configuration.width)/screen.frame.width,
                                   "y":pointer.point.y * Double(configuration.height)/screen.frame.height])
            elapsed += Double(frames)/frameRate
        }
        func point(_ x: Double, _ y: Double) -> Point {
            let scale = noteView.bounds.width/1000
            let global = notes.convertPoint(toScreen: noteView.convert(CGPoint(x:x*scale,y:y), to:nil))
            return Point(session.canvas.convert(session.panel.convertPoint(fromScreen:global),from:nil))
        }
        func pointerPoint(_ point: Point) -> CGPoint {
            let global = session.panel.convertPoint(toScreen: session.canvas.convert(point.cgPoint,to:nil))
            return pointer.convert(pointerPanel.convertPoint(fromScreen:global),from:nil)
        }
        func travel(_ target: CGPoint, seconds: Double = 0.32) async throws {
            let duration = interview ? hand.duration(seconds) : seconds
            let frames = max(2,Int(duration*frameRate))
            let path = hand.path(from: Point(pointer.point), to: Point(target), samples: frames, bend: interview ? 16 : 0)
            for location in path.dropFirst() {
                pointer.point=location.cgPoint
                pointer.pressed=false;pointer.needsDisplay=true
                try await pause(1.0/frameRate)
            }
        }
        func mouse(_ type: NSEvent.EventType, _ point: Point) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with:type,location:session.canvas.convert(point.cgPoint,to:nil),
                modifierFlags:[],timestamp:0,windowNumber:session.panel.windowNumber,context:nil,eventNumber:0,clickCount:1,pressure:1))
        }
        func click(_ label: String, native: NSButton? = nil) async throws {
            if interview { print("INTERVIEW ACTION \(String(format: "%.2f",recordingTime())): \(label)") }
            let nodes=NativeAccessibilityNode.nodes(in:controls) + NSApp.windows.filter {
                $0 !== toolbar && $0.isVisible
            }.compactMap(\.contentView).flatMap { NativeAccessibilityNode.nodes(in:$0) }
            let match=native.map { NativeAccessibilityNode(object:$0) } ??
                nodes.first {
                    ($0.accessibilityLabel()==label || $0.accessibilityLabel()?.hasPrefix(label + ", hex ") == true)
                        && $0.accessibilityRole() == .button
                }
            if match == nil { print("AVAILABLE CONTROLS: \(nodes.compactMap{$0.accessibilityLabel()}.joined(separator: " | "))") }
            let node=try XCTUnwrap(match,label)
            let frame=node.accessibilityFrame()
            let actionIndex = actions.count
            actions.append(["kind":"control","label":label,"time":recordingTime(),"rect":sourceRect(frame)])
            let global=CGPoint(x:frame.midX,y:frame.midY)
            try await travel(pointer.convert(pointerPanel.convertPoint(fromScreen:global),from:nil))
            pointer.pressed=true;pointer.needsDisplay=true
            if let native { native.performClick(nil) } else { XCTAssertTrue(node.accessibilityPerformPress(),label) }
            if label == "Tool library", let library = app.toolLibraryWindow {
                library.setContentSize(CGSize(width: 780, height: 500))
                let desired = CGRect(x: notes.frame.midX-390, y: toolbar.frame.maxY+16,
                                     width: library.frame.width, height: library.frame.height)
                var adjusted = desired
                adjusted.origin.y = min(adjusted.minY, screen.visibleFrame.maxY-adjusted.height)
                library.setFrame(adjusted, display: true)
                library.ignoresMouseEvents = true
            }
            actions[actionIndex]["pressTime"] = recordingTime()
            releasePhysicalInput()
            try await pause(0.12)
            pointer.pressed=false;pointer.needsDisplay=true
            let inspect = ["Tool library","Stroke color","Fill color","Start arrowhead","End arrowhead"].contains(label)
            let firstExplanation = inspect && explainedControls.insert(label).inserted
            try await pause(interview && firstExplanation ? 0.75 : 0.12)
        }
        func drag(_ start: Point, _ end: Point, seconds: Double = 0.8) async throws {
            try await travel(pointerPoint(start),seconds:0.35)
            session.canvas.mouseDown(with:try mouse(.leftMouseDown,start))
            let frames=max(2,Int((interview ? hand.duration(seconds) : seconds)*frameRate))
            for p in hand.path(from:start,to:end,samples:frames,bend:interview ? 2 : 0).dropFirst() {
                pointer.point=pointerPoint(p);pointer.pressed=true;pointer.needsDisplay=true
                session.canvas.mouseDragged(with:try mouse(.leftMouseDragged,p))
                try await pause(1.0/frameRate)
            }
            session.canvas.mouseUp(with:try mouse(.leftMouseUp,end))
            pointer.pressed=false;pointer.needsDisplay=true
            try await pause(0.35)
        }
        func label(_ value: String, at location: Point, size: Int = 28) async throws {
            try await click("Text")
            try await click("Font size \(size)")
            if nativeQuality {
                actions.append(["kind":"keyboard","label":"K · Black ink","time":recordingTime()])
                session.canvas.keyDown(with:try XCTUnwrap(NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],
                    timestamp:0,windowNumber:session.panel.windowNumber,context:nil,characters:"k",
                    charactersIgnoringModifiers:"k",isARepeat:false,keyCode:40)))
            }
            try await travel(pointerPoint(location))
            session.canvas.mouseDown(with:try mouse(.leftMouseDown,location))
            let editor=try XCTUnwrap(session.canvas.subviews.compactMap{$0 as? NSTextView}.first)
            for character in value {
                editor.insertText(String(character),replacementRange:NSRange(location:NSNotFound,length:0))
                try await pause(hand.duration(character == " " ? 0.05 : 0.027))
            }
            session.canvas.commitTextIfNeeded()
            try await pause(0.4)
        }
        func closePalette() async throws {
            let nodes = NSApp.windows.filter(\.isVisible).compactMap(\.contentView)
                .flatMap { NativeAccessibilityNode.nodes(in:$0) }
            if nodes.contains(where:{$0.accessibilityLabel()=="Done" && $0.accessibilityRole() == .button}) {
                try await click("Done")
            } else {
                XCTAssertFalse(nodes.contains(where:{$0.accessibilityLabel()?.contains("hex color and alpha") == true}),
                               "An open color palette must retain its Done control.")
            }
        }
        func color(_ name: String, fill: Bool = false) async throws {
            try await click(fill ? "Fill color" : "Stroke color")
            func paletteNodes() -> [NativeAccessibilityNode] {
                NSApp.windows.filter(\.isVisible).compactMap(\.contentView)
                    .flatMap { NativeAccessibilityNode.nodes(in:$0) }
            }
            var nodes = paletteNodes()
            if !nodes.contains(where: { $0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true }),
               !nodes.contains(where: { $0.accessibilityLabel()?.contains("hex color and alpha") == true }) {
                print("INTERVIEW: Palette closed during inspector resize; reopen the visible control.")
                try await pause(0.2)
                try await click(fill ? "Fill color" : "Stroke color")
                nodes = paletteNodes()
            }
            let label = try XCTUnwrap(nodes.first{$0.accessibilityLabel()?.hasPrefix(name + ", hex ") == true}?.accessibilityLabel())
            let expected = try XCTUnwrap(label.split(separator:"#").last).description
            try await click(name)
            XCTAssertEqual((fill ? model.style.fill : model.style.color).map(PaletteHex.format),expected)
            try await closePalette()
        }
        func sketch(_ points: [Point]) async throws {
            let first=try XCTUnwrap(points.first), last=try XCTUnwrap(points.last)
            try await travel(pointerPoint(first))
            session.canvas.mouseDown(with:try mouse(.leftMouseDown,first))
            for (start,end) in zip(points,points.dropFirst()) {
                let distance=hypot(end.x-start.x,end.y-start.y)
                let frames=max(4,Int(hand.duration(max(0.12,distance/340))*frameRate))
                for p in hand.path(from:start,to:end,samples:frames,bend:1.7).dropFirst() {
                    pointer.point=pointerPoint(p);pointer.pressed=true;pointer.needsDisplay=true
                    session.canvas.mouseDragged(with:try mouse(.leftMouseDragged,p))
                    try await pause(1.0/frameRate)
                }
            }
            session.canvas.mouseUp(with:try mouse(.leftMouseUp,last))
            pointer.pressed=false
            try await pause(0.45)
        }
        do {
            if interview {
                chapters.append(["name":"Set the problem","time":recordingTime()])
                try await label("Design a link service",at:point(40,20),size:44)
                try await color(nativeQuality ? "Blue" : "Dark ink")
                try await pause(1)
                chapters.append(["name":"Sketch the service","time":recordingTime()])
                try await click("Pen")
                if nativeQuality {
                    if !model.smartDraw { try await click("Smart Draw") }
                } else if model.smartDraw { try await click("Smart Draw") }
                if !nativeQuality || model.style.color != .blue { try await color("Blue") }
                try await sketch([point(360,165),point(420,162),point(535,166),point(541,245),
                                  point(470,248),point(360,243),point(357,204),point(360,165)])
                if nativeQuality {
                    for _ in 0..<15 {
                        if session.document.elements.last?.tool == .rectangle { break }
                        try await pause(0.1)
                    }
                }
                XCTAssertEqual(session.document.elements.last?.tool,nativeQuality ? .rectangle : .pen)
                if nativeQuality {
                    try await label("Link API",at:point(385,185))
                }
                try await click("Pen")
                if !model.smartDraw { try await click("Smart Draw") }
                try await sketch([point(375,335),point(452,332),point(545,337),point(549,395),
                                  point(462,398),point(375,394),point(375,335)])
                for _ in 0..<15 {
                    if session.document.elements.last?.tool == .rectangle { break }
                    try await pause(0.1)
                }
                XCTAssertEqual(session.document.elements.last?.tool,.rectangle)
                if nativeQuality { try await label("Cache",at:point(405,349)) }
                chapters.append(["name":"People and data","time":recordingTime()])
                try await click("Tool library");try await click("Person")
                try await drag(point(95,155),point(180,250),seconds:0.72)
                try await color("Teal")
                if nativeQuality { try await label("User",at:point(137.5,202.5),size:18) }
                try await click("Tool library");try await click("Database")
                try await drag(point(760,160),point(905,265),seconds:0.86)
                try await color("Purple")
                try await color("Purple",fill:true)
                try await color("Lighter shade of selected color",fill:true)
                try await click("Sketchy")
                try await pause(1)
                if nativeQuality { try await label("Links DB",at:point(774,201),size:18) }
                chapters.append(["name":"Connect the request","time":recordingTime()])
                try await click("Arrow")
                try await color("Dark ink")
                try await drag(point(185,210),point(354,210),seconds:0.65)
                try await click("End arrowhead");try await click("End: Filled triangle")
                try await click("Arrow")
                try await drag(point(550,215),point(752,215),seconds:0.73)
                try await click("End arrowhead");try await click("End: Arrow")
                try await click("Arrow")
                try await drag(point(465,255),point(465,325),seconds:0.54)
                try await click("Arrow")
                try await color("Coral")
                try await click("Dashed")
                for p in [point(555,365),point(690,365),point(820,280)] {
                    try await travel(pointerPoint(p),seconds:0.2)
                    session.canvas.mouseDown(with:try mouse(.leftMouseDown,p))
                    session.canvas.mouseUp(with:try mouse(.leftMouseUp,p))
                    try await pause(0.1)
                }
                session.canvas.keyDown(with:try XCTUnwrap(NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],
                    timestamp:0,windowNumber:session.panel.windowNumber,context:nil,characters:"\r",
                    charactersIgnoringModifiers:"\r",isARepeat:false,keyCode:36)))
                try await pause(0.2)
                try await click("Curved arrow")
                chapters.append(["name":"Name the parts","time":recordingTime()])
                if !nativeQuality {
                try await label("User",at:point(100,275))
                try await color("Dark ink")
                try await label("Link API",at:point(385,185))
                try await label("Links DB",at:point(774,201),size:18)
                try await label("Cache",at:point(405,349))
                }
                try await label("cache miss",at:point(625,385),size:18)
                try await color("Coral")
                chapters.append(["name":"Color and emphasis","time":recordingTime()])
                let api = try XCTUnwrap(session.document.elements.compactMap {
                    ShapeLabelLayout.textElement(for: $0) ?? ($0.tool == .text ? $0 : nil)
                }.first { $0.text == "Link API" })
                try await click("Highlighter")
                try await drag(Point(api.frame.origin.x - 3,api.frame.center.y),
                               Point(api.frame.maxX + 3,api.frame.center.y),seconds:0.55)
                let highlight = try XCTUnwrap(session.document.elements.last)
                XCTAssertEqual(highlight.points.first?.y ?? 0,api.frame.center.y,accuracy:0.5)
                try await click("Pen")
                if model.smartDraw { try await click("Smart Draw") }
                try await color("Teal")
                try await click("Clean")
                try await sketch([point(377,404),point(435,406),point(495,404),point(549,406)])
                try await label("Read from cache first",at:point(330,430))
                try await color("Dark ink")
                if !nativeQuality {
                chapters.append(["name":"Blur a sample secret","time":recordingTime()])
                try await click("Effects")
                model.setCoverMode(.blur)
                try await pause(0.3)
                try await drag(point(625,78),point(957,109),seconds:0.75)
                for _ in 0..<40 {
                    if session.effectsReady { break }
                    try await pause(0.1)
                }
                XCTAssertTrue(session.effectsReady,"Native blur must present a captured controlled-source frame.")
                XCTAssertTrue(session.document.elements.contains{$0.tool == .blur})
                try await pause(0.8)
                }
                chapters.append(["name":"Move and refine","time":recordingTime()])
                try await click("Select")
                try await drag(point(135,200),point(145,205),seconds:0.5)
                try await click("Undo")
                try await pause(0.5)
                XCTAssertTrue(session.document.elements.contains{$0.tool == .person})
                XCTAssertTrue(session.document.elements.contains{$0.tool == .databaseStack})
                XCTAssertGreaterThanOrEqual(session.document.elements.filter{$0.tool == .text || $0.label != nil}.count,7)
            } else {
            chapters.append(["name":"Smart Draw","time":recordingTime()])
            try await click("Pen")
            try await click("Smart Draw")
            XCTAssertTrue(model.smartDraw)
            let rough=[point(90,140),point(160,138),point(225,142),point(310,140),point(312,195),
                       point(308,263),point(225,266),point(165,262),point(90,265),point(87,200),point(90,140)]
            try await travel(pointerPoint(rough[0]))
            session.canvas.mouseDown(with:try mouse(.leftMouseDown,rough[0]))
            for (a,b) in zip(rough,rough.dropFirst()) {
                for i in 1...5 {
                    let p=a+(b-a)*(Double(i)/5)
                    pointer.point=pointerPoint(p);pointer.pressed=true;pointer.needsDisplay=true
                    session.canvas.mouseDragged(with:try mouse(.leftMouseDragged,p))
                    try await pause(1.0/30)
                }
            }
            session.canvas.mouseUp(with:try mouse(.leftMouseUp,rough.last!))
            pointer.pressed=false
            for _ in 0..<10 {
                if session.document.elements.first?.tool == .rectangle { break }
                try await Task.sleep(for:.milliseconds(60));try await pause(0.1)
            }
            XCTAssertEqual(session.document.elements.first?.tool,.rectangle)
            try await pause(0.35)
            chapters.append(["name":"Shapes","time":recordingTime()])
            try await click("Ellipse");try await drag(point(600,140),point(830,270))
            chapters.append(["name":"Arrows","time":recordingTime()])
            try await click("Arrow")
            for p in [point(312,205),point(450,110),point(600,205)] {
                try await travel(pointerPoint(p),seconds:0.28)
                pointer.pressed=true;pointer.needsDisplay=true
                session.canvas.mouseDown(with:try mouse(.leftMouseDown,p))
                session.canvas.mouseUp(with:try mouse(.leftMouseUp,p))
                try await pause(0.13);pointer.pressed=false
            }
            session.canvas.keyDown(with:try XCTUnwrap(NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],
                timestamp:0,windowNumber:session.panel.windowNumber,context:nil,characters:"\r",charactersIgnoringModifiers:"\r",isARepeat:false,keyCode:36)))
            try await pause(0.35)
            chapters.append(["name":"Arrow preferences","time":recordingTime()])
            try await click("Curved arrow")
            try await click("End arrowhead")
            try await click("End: Filled triangle");try await click("Width 7")
            XCTAssertTrue(model.style.curved);XCTAssertEqual(model.style.endHead,.filled);XCTAssertEqual(model.style.width,7)
            try await pause(0.35)
            chapters.append(["name":"Select and edit","time":recordingTime()])
            try await click("Select")
            try await drag(point(185,200),point(230,230))
            let rectangle=try XCTUnwrap(session.document.elements.first{$0.tool == .rectangle})
            let handle=Point(rectangle.frame.maxX,rectangle.frame.maxY)
            try await drag(handle,handle+Point(45,20),seconds:0.65)
            try await click("Undo")
            XCTAssertEqual(session.document.elements.first{$0.id==rectangle.id}?.frame,rectangle.frame)
            XCTAssertNil(session.sourceImage)
            if expanded {
                chapters.append(["name":"Text and color","time":recordingTime()])
                try await click("Text")
                let location=point(390,315)
                try await travel(pointerPoint(location))
                session.canvas.mouseDown(with:try mouse(.leftMouseDown,location))
                let text=try XCTUnwrap(session.canvas.subviews.compactMap{$0 as? NSTextView}.first)
                for character in "Look here." {
                    text.insertText(String(character),replacementRange:NSRange(location:NSNotFound,length:0))
                    try await pause(0.06)
                }
                session.canvas.commitTextIfNeeded()
                try await click("Mono font")
                try await click("Bold")
                let blue=try XCTUnwrap(NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],
                    timestamp:0,windowNumber:session.panel.windowNumber,context:nil,characters:"b",charactersIgnoringModifiers:"b",isARepeat:false,keyCode:11))
                session.canvas.keyDown(with:blue)
                try await pause(0.45)
                chapters.append(["name":"Highlighter","time":recordingTime()])
                try await click("Highlighter")
                try await drag(point(645,198),point(790,198),seconds:0.6)
                chapters.append(["name":"Hide and reveal","time":recordingTime()])
                model.setAnimationStyle(.slide)
                app.hideTools()
                for _ in 0..<12 { try await pause(1.0/30) }
                app.activateTools()
                for _ in 0..<18 { try await pause(1.0/30) }
                model.setVisibilityAnimation(false)
            }
            }
            let completed = session.document.elements
            try await click("Use apps")
            XCTAssertEqual(model.mode, .interact)
            XCTAssertEqual(session.document.elements, completed)
            try await pause(1.0)
            let duration: Double
            if let realtime { duration = try await realtime.finish() }
            else { duration = try await XCTUnwrap(movie).finish() }
            let shown = recordedBounds.insetBy(dx: -18, dy: -18).intersection(screen.frame)
            let scale = Double(configuration.width)/screen.frame.width
            let metadata:[String:Any]=[
                "path":output.path,"duration":duration,"width":configuration.width,"height":configuration.height,
                "chapters":chapters,"pointerSamples":pointerSamples,
                "actions":actions,"toolbarFrames":toolbarFrames,
                "notesRect":sourceRect(notes.frame),"captureFPS":frameRate,
                "backingScale":screen.backingScaleFactor,"nativeQuality":nativeQuality,
                "contentRect":["x":(shown.minX-screen.frame.minX)*scale,
                               "y":(screen.frame.maxY-shown.maxY)*scale,
                               "width":shown.width*scale,"height":shown.height*scale],
                "source":expanded ? "Real-time ScreenCaptureKit recording of scripted native live-desktop actions; only test-owned windows included." :
                    "Actual live desktop OverlaySession(screen:) and floating toolbar, captured through a display filter containing only test-owned windows. Scripted native actions and timed frames.",
                "privacy":"Only the opaque controlled desktop, sample notes window, live annotation overlay, toolbar and scripted pointer are included. Unrelated windows are excluded.",
                "imageWorkspace":false,
                "gestureSeed":interview ? 20260912 : 0,
                "captureSource":interview ? "Native live pixel-effect capture is restricted to the opaque test-owned desktop and notes windows; no unrelated desktop fallback." : "No native pixel effects requested."
            ]
            try JSONSerialization.data(withJSONObject:metadata,options:[.prettyPrinted,.sortedKeys])
                .write(to:directory.appendingPathComponent(nativeQuality ? "latest-interview-native.json" : interview ? "latest-interview.json" : expanded ? "latest-ad.json" : "latest.json"),options:.atomic)
            print("LIVE DESKTOP DEMO: \(output.path)")
        } catch {
            print("LIVE DESKTOP DEMO ERROR: \(error)")
            movie?.cancel()
            do { try await realtime?.cancel() } catch { XCTFail("Could not stop owned recording: \(error)") }
            throw error
        }
    }
}

@MainActor
private final class DesktopDemoNotes: NSView {
    var interview = false
    var showSampleKey = true
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.white.setFill();bounds.fill()
        let scale=bounds.width/1000
        func text(_ value:String,_ x:Double,_ y:Double,_ size:Double,_ weight:NSFont.Weight = .regular) {
            (value as NSString).draw(at:CGPoint(x:x*scale,y:y),withAttributes:[
                .font:NSFont.systemFont(ofSize:size,weight:weight),.foregroundColor:NSColor(calibratedWhite:0.18,alpha:1)])
        }
        if interview {
            text("SYSTEM DESIGN INTERVIEW",40,82,12,.medium)
            if showSampleKey { text("demo_key_not_a_real_secret",630,85,16) }
            text("GET /go/:code",205,125,14)
            text("POST /links",575,145,14)
            text("Sample data only",40,bounds.height-30,12)
            return
        }
        text("Release handoff",40,30,28,.semibold)
        text("Sample notes for a desktop annotation demo",40,70,14)
        text("Draft",155,180,24,.medium)
        text("Review",670,180,24,.medium)
        text("Explain the next step.",40,bounds.height-42,15)
    }
}

@MainActor
final class DesktopDemoPointer: NSView {
    var point=CGPoint(x:80,y:80)
    var pressed=false
    override var isFlipped: Bool { true }
    override func hitTest(_ point:NSPoint)->NSView? { nil }
    override func draw(_ dirtyRect:NSRect) {
        if pressed { NSColor.controlAccentColor.withAlphaComponent(0.22).setFill();NSBezierPath(ovalIn:CGRect(x:point.x-13,y:point.y-13,width:26,height:26)).fill() }
        let image=NSCursor.arrow.image
        image.draw(in:CGRect(origin:point,size:image.size),from:.zero,operation:.sourceOver,fraction:1,respectFlipped:true,hints:nil)
    }
}
