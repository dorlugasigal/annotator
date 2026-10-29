import AppKit
import AVFoundation
import ScreenCaptureKit
import XCTest
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

@MainActor
final class NativeBrowserBlurRecordingTests: XCTestCase {
    private struct BrowserInfo: Decodable {
        struct Box: Decodable { let x: Double, y: Double, width: Double, height: Double }
        struct Geometry: Decodable {
            let left: Double, top: Double, outerWidth: Double, outerHeight: Double, innerWidth: Double, innerHeight: Double
        }
        let title: String
        let pid: Int32
        let target: Box
        let geometry: Geometry
    }

    func testRecordBlurOverAnOwnedBrowserWindow() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_RECORD_BROWSER_BLUR"] == "1",
                          "Controlled browser recording is opt-in.")
        guard #available(macOS 15.0, *) else { throw XCTSkip("Recording requires macOS 15.") }
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked)
        let root = RepositoryRoot.url
        let directory = root.appendingPathComponent("build/browser-blur")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let infoURL = directory.appendingPathComponent("browser-\(UUID()).json")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/opt/homebrew/bin/node")
        process.arguments = [root.appendingPathComponent("tools/demo/recording/browser-fixture.mjs").path, infoURL.path]
        process.currentDirectoryURL = root
        try process.run()
        defer {
            if process.isRunning { process.terminate(); process.waitUntilExit() }
        }
        for _ in 0..<150 {
            if FileManager.default.fileExists(atPath: infoURL.path) { break }
            if !process.isRunning { throw CocoaError(.executableLoad, userInfo:[NSLocalizedDescriptionKey:"Controlled browser exited before capture."]) }
            try await Task.sleep(for:.milliseconds(100))
        }
        let browser = try JSONDecoder().decode(BrowserInfo.self, from:Data(contentsOf:infoURL))
        let content = try await SCShareableContent.excludingDesktopWindows(false,onScreenWindowsOnly:false)
        let source = try XCTUnwrap(content.windows.first {
            $0.owningApplication?.processID == browser.pid && $0.title?.contains(browser.title) == true
        })
        let display = try XCTUnwrap(content.displays.first { $0.displayID == CGMainDisplayID() })
        DisplayCapture.useControlledWindowsForTesting([source.windowID],processID:browser.pid)
        defer { DisplayCapture.useControlledWindowsForTesting(nil) }
        let suite = "browser-blur-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        let model = WorkspaceModel(defaults:defaults)
        model.setVisibilityAnimation(false)
        model.setToolbarDockEdge(.bottom)
        model.setPropertiesShown(true)
        model.setPropertiesShown(true)
        let app = AppDelegate(model:model)
        app.resumeDrawing()
        defer {
            app.applicationWillTerminate(Notification(name:NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName:suite)
        }
        let session = try XCTUnwrap(model.active)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        let screen = try XCTUnwrap(session.panel.screen)
        XCTAssertNil(session.sourceImage)
        XCTAssertEqual((screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value,display.displayID)
        let displayBounds = CGDisplayBounds(display.displayID)
        let scale = source.frame.width / browser.geometry.outerWidth
        let side = (browser.geometry.outerWidth-browser.geometry.innerWidth)/2
        let top = browser.geometry.outerHeight-browser.geometry.innerHeight-side
        let target = CGRect(x:source.frame.minX+(browser.target.x+side)*scale-displayBounds.minX,
                            y:source.frame.minY+(browser.target.y+top)*scale-displayBounds.minY,
                            width:browser.target.width*scale,height:browser.target.height*scale)
        XCTAssertTrue(session.canvas.bounds.contains(target),"Browser geometry must map inside the captured display.")
        let pointerPanel = NSPanel(contentRect:screen.frame,styleMask:[.borderless,.nonactivatingPanel],backing:.buffered,defer:false)
        pointerPanel.isReleasedWhenClosed=false;pointerPanel.isOpaque=false;pointerPanel.backgroundColor = .clear
        pointerPanel.hasShadow=false;pointerPanel.ignoresMouseEvents=true
        pointerPanel.level=NSWindow.Level(rawValue:toolbar.level.rawValue+1)
        let pointer=DesktopDemoPointer(frame:CGRect(origin:.zero,size:screen.frame.size))
        pointerPanel.contentView=pointer;pointerPanel.orderFrontRegardless()
        defer { pointerPanel.orderOut(nil);pointerPanel.close() }
        session.panel.ignoresMouseEvents=true;toolbar.ignoresMouseEvents=true
        let config=SCStreamConfiguration()
        config.width=2880;config.height=Int(screen.frame.height/screen.frame.width*2880/2)*2
        config.showsCursor=false;config.minimumFrameInterval=CMTime(value:1,timescale:30)
        func filter() async throws -> SCContentFilter {
            let ids=Set(NSApp.windows.filter(\.isVisible).compactMap{CGWindowID(exactly:$0.windowNumber)})
            let available=try await SCShareableContent.excludingDesktopWindows(false,onScreenWindowsOnly:false)
            let windows=available.windows.filter {
                ($0.windowID==source.windowID && $0.owningApplication?.processID==browser.pid)
                || (ids.contains($0.windowID) && $0.owningApplication?.processID==ProcessInfo.processInfo.processIdentifier)
            }
            guard windows.contains(where:{$0.windowID==source.windowID}) else { throw CaptureFailure.invalidFrame }
            return SCContentFilter(display:display,including:windows)
        }
        let url=directory.appendingPathComponent("browser-blur-\(UUID()).mp4")
        let movie=try RealtimeDesktopMovie(url:url,filter:try await filter(),configuration:config)
        try await movie.start()
        do {
            try await movie.wait(1)
            model.chooseCover();model.setCoverMode(.blur)
            model.applyStyle{$0.effectAmount=5}
            try await movie.wait(0.5)
            let start=Point(target.origin),end=Point(target.maxX,target.maxY)
            func event(_ type:NSEvent.EventType,_ point:Point)throws->NSEvent {
                try XCTUnwrap(NSEvent.mouseEvent(with:type,location:session.canvas.convert(point.cgPoint,to:nil),
                    modifierFlags:[],timestamp:0,windowNumber:session.panel.windowNumber,context:nil,eventNumber:0,clickCount:1,pressure:1))
            }
            var hand=DemoHandMotion(seed:7183)
            pointer.point=start.cgPoint;pointer.pressed=true;pointer.needsDisplay=true
            session.canvas.mouseDown(with:try event(.leftMouseDown,start))
            for point in hand.path(from:start,to:end,samples:24,bend:1).dropFirst() {
                pointer.point=point.cgPoint;pointer.needsDisplay=true
                session.canvas.mouseDragged(with:try event(.leftMouseDragged,point))
                try await movie.wait(1.0/30)
            }
            session.canvas.mouseUp(with:try event(.leftMouseUp,end))
            pointer.pressed=false;pointer.needsDisplay=true
            for _ in 0..<50 {
                try await movie.update(filter:try await filter())
                if session.effectsReady { break }
                try await movie.wait(0.1)
            }
            XCTAssertTrue(session.effectsReady)
            try await movie.wait(1.1)
            model.applyStyle{$0.effectAmount=16}
            try await movie.wait(1.2)
            model.applyStyle{$0.effectAmount=28}
            try await movie.wait(1.3)
            let duration=try await movie.finish()
            let factor=Double(config.width)/screen.frame.width
            let meta:[String:Any]=["path":url.path,"duration":duration,"width":config.width,"height":config.height,
                "browserRect":["x":(source.frame.minX-displayBounds.minX)*factor,"y":(source.frame.minY-displayBounds.minY)*factor,
                               "width":source.frame.width*factor,"height":source.frame.height*factor],
                "targetRect":["x":target.minX*factor,"y":target.minY*factor,"width":target.width*factor,"height":target.height*factor],
                "source":"Actual Chromium window showing a localhost-only fictional workspace. Native Annotator Blur, scripted canvas events and native commands. Only verified browser PID/window ID and test-owned overlays included.",
                "imageWorkspace":false]
            try JSONSerialization.data(withJSONObject:meta,options:[.prettyPrinted,.sortedKeys])
                .write(to:directory.appendingPathComponent("latest.json"),options:.atomic)
            print("BROWSER BLUR: \(url.path)")
        } catch {
            do { try await movie.cancel() } catch { XCTFail("Owned movie cleanup failed: \(error)") }
            throw error
        }
    }
}
