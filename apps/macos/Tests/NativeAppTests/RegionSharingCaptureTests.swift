import AppKit
import ScreenCaptureKit
import SwiftUI
import XCTest
import AnnotationCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class RegionSharingCaptureTests: XCTestCase {
    private func permitControlledCapture() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1",
            "Controlled screen/window capture is opt-in; ordinary tests do not read the desktop.")
        try XCTSkipUnless(DisplayCapture.hasPermission, "The development app needs Screen Recording permission.")
        try XCTSkipIf(DisplayCapture.desktopIsLocked, "Unlock the Mac for the owned-window capture check.")
    }

    private func ready(_ condition: () -> Bool) async throws {
        for _ in 0..<600 {
            if condition() { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("The controlled native sharing view did not become ready within six seconds.")
    }

    private func fixture(_ color: NSColor, region: Rect, source: CGRect) -> NSPanel {
        let panel = NSPanel(contentRect: RegionSharingGuide.screenFrame(for: region, on: source),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Annotator controlled region source"
        panel.backgroundColor = color
        panel.isOpaque = true
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.sharingType = .readOnly
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.orderFrontRegardless()
        return panel
    }

    private func colors(in window: NSWindow) async throws -> (red: Int, green: Int, blue: Int, magenta: Int, total: Int) {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let target = try XCTUnwrap(content.windows.first { $0.windowID == CGWindowID(window.windowNumber) },
            "The output must be visible as one shareable native window.")
        let configuration = SCStreamConfiguration()
        configuration.width = 640
        let frame = window.frame
        guard frame.width.isFinite, frame.height.isFinite, frame.width > 0, frame.height > 0 else {
            throw RenderFailure.invalidSize
        }
        configuration.height = Int((frame.height / frame.width * 640).rounded())
        configuration.showsCursor = false
        configuration.ignoreShadowsSingleWindow = true
        configuration.colorSpaceName = CGColorSpace.sRGB
        let image = try await SCScreenshotManager.captureImage(
            contentFilter: SCContentFilter(desktopIndependentWindow: target), configuration: configuration)
        let attachment = XCTAttachment(image: NSImage(cgImage: image, size: .zero))
        attachment.name = "Controlled shared output"
        attachment.lifetime = .keepAlways
        add(attachment)
        print("CONTROLLED OUTPUT: native=\(frame.size), ScreenCaptureKit=\(target.frame.size), snapshot=\(image.width)x\(image.height)")
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: image.width, height: image.height,
            bitsPerComponent: 8, bytesPerRow: image.width * 4, space: space,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        bitmap.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        let pixels = try XCTUnwrap(bitmap.data).assumingMemoryBound(to: UInt8.self)
        var red = 0, green = 0, blue = 0, magenta = 0
        for index in stride(from: 0, to: image.width * image.height * 4, by: 4) {
            if pixels[index] > 204 && pixels[index + 1] < 51 && pixels[index + 2] < 51 { red += 1 }
            if pixels[index + 1] > 204 && pixels[index] < 51 && pixels[index + 2] < 51 { green += 1 }
            if pixels[index + 2] > 204 && pixels[index] < 51 && pixels[index + 1] < 51 { blue += 1 }
            if pixels[index] > 204 && pixels[index + 2] > 204 && pixels[index + 1] < 51 { magenta += 1 }
        }
        return (red, green, blue, magenta, image.width * image.height)
    }

    func testOutlineIsRenderedByWindowServerOnEveryDisplay() async throws {
        try permitControlledCapture()
        let suite = "region-outline-displays-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let sharing = model.regionSharing
        sharing.showControls()
        let controlsFrame = try XCTUnwrap(sharing.controls).frame
        for screen in NSScreen.screens {
            let id = try XCTUnwrap(screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber).uint32Value
            sharing.selectDisplay(id)
            for preset in [ShareRegionPreset.leftHalf, .rightHalf] {
                sharing.choosePreset(preset)
                let region = try XCTUnwrap(sharing.region)
                let panel = try XCTUnwrap(NSApp.windows.first {
                    $0.title == "Annotator region outline" && $0.isVisible
                })
                XCTAssertEqual(panel.frame, RegionSharingGuide.screenFrame(for: region.bounds, on: screen.frame))
                XCTAssertEqual(panel.screen, screen)
                XCTAssertEqual(sharing.controls?.frame, controlsFrame)
                XCTAssertEqual(panel.sharingType, .readOnly)
                try await Task.sleep(for: .milliseconds(150))
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
                let target = try XCTUnwrap(content.windows.first { $0.windowID == CGWindowID(panel.windowNumber) })
                let display = try XCTUnwrap(content.displays.first { $0.displayID == id })
                let configuration = SCStreamConfiguration()
                configuration.width = 320
                configuration.height = 240
                configuration.sourceRect = CGRect(x: region.bounds.x, y: region.bounds.center.y - 120,
                                                   width: 320, height: 240)
                configuration.showsCursor = false
                let filter = SCContentFilter(display: display, including: [target])
                let deadline = ContinuousClock.now.advanced(by: .seconds(4))
                var borderContrast = 0
                repeat {
                    let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
                    let bitmap = try XCTUnwrap(CGContext(data: nil, width: image.width, height: image.height,
                        bitsPerComponent: 8, bytesPerRow: image.width * 4,
                        space: try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
                    bitmap.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
                    let pixels = try XCTUnwrap(bitmap.data).assumingMemoryBound(to: UInt8.self)
                    // Allow the two-point border's rasterization fringe, not the faint interior fill.
                    let edgeWidth = min(image.width, max(1, Int(ceil(Double(image.width) / 320 * 4))))
                    borderContrast = (0..<edgeWidth).map { x in
                        let index = (image.height / 2 * image.width + x) * 4
                        let channels = (0..<3).map { Int(pixels[index + $0]) }
                        return (channels.max() ?? 0) - (channels.min() ?? 0)
                    }.max() ?? 0
                    if borderContrast > 50 { break }
                    try await Task.sleep(for: .milliseconds(50))
                } while ContinuousClock.now < deadline
                print("DISPLAY GUIDE: \(screen.localizedName), \(preset), frame=\(panel.frame), borderContrast=\(borderContrast)")
                XCTAssertGreaterThan(borderContrast, 50, "The selected display must render its accent border within four seconds.")
            }
        }
    }

    func testWorkspaceAnimationsAppearInSharedOutputWithoutRevealingProtectedOrOutsidePixels() async throws {
        try permitControlledCapture()
        let suite = "shared-workspace-motion-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let primary = try XCTUnwrap(NSScreen.screens.first)
        model.screenPointer = { CGPoint(x: primary.frame.midX, y: primary.frame.midY) }
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        app.finishLaunchPresentation(isLoginItem: false)
        model.beginDrawing()
        model.interact()
        let source = try XCTUnwrap(model.active)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        let area = Rect(x: 40, y: 40,
            width: min(source.panel.frame.width - 80, max(1012, toolbar.frame.width + 80)),
            height: min(source.panel.frame.height - 80, 500))
        let screenArea = RegionSharingGuide.screenFrame(for: area, on: source.panel.frame)
        toolbar.setFrameOrigin(CGPoint(x: screenArea.minX + 20, y: screenArea.maxY - toolbar.frame.height - 20))
        let background = fixture(NSColor(srgbRed: 0, green: 1, blue: 0, alpha: 1),
                                 region: area, source: source.panel.frame)
        var blocker: NSPanel?
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            DisplayCapture.useControlledWindowsForTesting(nil)
            background.orderOut(nil); background.close()
            blocker?.orderOut(nil); blocker?.close()
            defaults.removePersistentDomain(forName: suite)
        }
        DisplayCapture.useControlledWindowsForTesting([CGWindowID(background.windowNumber)])
        var publicShape = Element(tool: .rectangle, frame: Rect(x: area.x + 80, y: area.y + 200, width: 220, height: 140))
        publicShape.style.color = RGBA(1, 0, 0)
        publicShape.style.fill = RGBA(1, 0, 0)
        var secret = Element(tool: .rectangle, frame: Rect(x: area.x + 510, y: area.y + 260, width: 60, height: 60))
        secret.style.color = RGBA(1, 0, 1)
        secret.style.fill = RGBA(1, 0, 1)
        var cover = Element(tool: .cover, frame: Rect(x: area.x + 490, y: area.y + 240, width: 100, height: 100))
        cover.style.color = .ink
        let outside = Element(tool: .rectangle, frame: Rect(x: area.maxX + 20, y: area.y + 200, width: 80, height: 80),
                              style: secret.style)
        for element in [publicShape, secret, cover, outside] { try source.document.add(element) }
        source.document.select([])
        model.applyStyle { $0.color = .ink }
        source.document.select([publicShape.id])
        model.synchronize()
        let history = source.document
        let sharing = model.regionSharing
        sharing.showControls()
        sharing.applyCustom(area)
        sharing.controls?.orderOut(nil)
        sharing.start()
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        let output = try XCTUnwrap(sharing.output)
        let identity = output.window.windowNumber
        let initial = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(initial.red) / Double(initial.total), 0.02)
        XCTAssertLessThan(Double(initial.magenta) / Double(initial.total), 0.002)

        for style: VisibilityAnimationStyle in [.magic, .dissolve] {
            model.setAnimationStyle(style)
            if style == .dissolve {
                let panel = NSPanel(contentRect: output.window.frame,
                    styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
                panel.isReleasedWhenClosed = false
                panel.isOpaque = true
                panel.backgroundColor = .yellow
                panel.level = .floating
                panel.orderFrontRegardless()
                blocker = panel
            }
            for showing in [false, true] {
                if showing { app.activateTools() } else { app.hideTools() }
                let transition = try XCTUnwrap(output.content.workspaceTransition)
                XCTAssertLessThanOrEqual(transition.animatedImageCount, 2,
                    "Fully protected and outside objects must not create sprites or motion trails.")
                if showing {
                    let sprite = try XCTUnwrap(transition.subviews.first as? VisibilityImageView)
                    let contents = try XCTUnwrap(sprite.imageLayer.contents)
                    guard CFGetTypeID(contents as CFTypeRef) == CGImage.typeID else {
                        return XCTFail("The animation sprite must contain a CGImage.")
                    }
                    let image = contents as! CGImage
                    XCTAssertEqual(image.bitsPerPixel, 32)
                    let data = try XCTUnwrap(image.dataProvider?.data)
                    let bytes = try XCTUnwrap(CFDataGetBytePtr(data))
                    let hasSelectionBlue = (0..<image.height).contains { y in
                        (0..<image.width).contains { x in
                            let index = y * image.bytesPerRow + x * 4
                            return bytes[index + 2] > 200 && bytes[index] < 50 && bytes[index + 3] > 128
                        }
                    }
                    XCTAssertFalse(hasSelectionBlue, "The moving sprite must omit desktop selection handles.")
                }
                var sawIntermediateFrame = false
                let deadline = ContinuousClock.now.advanced(by: .seconds(4))
                while model.visibilityAnimating && ContinuousClock.now < deadline {
                    try await Task.sleep(for: .milliseconds(80))
                    let sample = try await colors(in: output.window)
                    print("SHARED MOTION: \(style), showing=\(showing), red=\(sample.red), initial=\(initial.red)")
                    XCTAssertLessThan(Double(sample.magenta) / Double(sample.total), 0.002,
                        "Protected or originally outside content must not move into the share.")
                    if sample.red > initial.red / 20 && sample.red < initial.red * 19 / 20 {
                        sawIntermediateFrame = true
                    }
                }
                XCTAssertTrue(sawIntermediateFrame, "\(style) must render an intermediate shared frame, not just toggle visibility.")
                try await ready {
                    (!model.visibilityAnimating && output.content.workspaceTransition == nil && sharing.state == .live) ||
                        model.errorMessage != nil
                }
                XCTAssertNil(model.errorMessage)
                model.desktopSessions.forEach { $0.panel.ignoresMouseEvents = true }
                XCTAssertTrue(sharing.state.isCapturing)
                XCTAssertEqual(output.window.windowNumber, identity)
                XCTAssertEqual(source.document.elements, history.elements)
                XCTAssertEqual(source.document.selection, history.selection)
                XCTAssertEqual(source.document.canUndo, history.canUndo)
                let final = try await colors(in: output.window)
                if showing { XCTAssertGreaterThan(final.red, initial.red / 2) }
                else { XCTAssertLessThan(Double(final.red) / Double(final.total), 0.002) }
            }
        }
        model.setAnimationStyle(.magic)
        app.hideTools()
        try await Task.sleep(for: .milliseconds(100))
        app.activateTools()
        XCTAssertNotNil(output.content.workspaceTransition, "Reversing must retain a shared animation.")
        try await Task.sleep(for: .milliseconds(100))
        let interrupted = output.content.workspaceTransition
        try source.document.add(Element(tool: .cover, frame: publicShape.frame))
        model.synchronize()
        XCTAssertNil(output.content.workspaceTransition, "A protection edit must discard the old moving sprites.")
        XCTAssertFalse(interrupted?.hasFramePump == true)
        sharing.stop()
        XCTAssertNil(output.content.workspaceTransition)
        XCTAssertTrue(output.content.isCovered)
        XCTAssertFalse(sharing.state.isCapturing)
    }

    func testControlledMultipleWindowsCropVisibilityAndSharedCaptureDemand() async throws {
        try permitControlledCapture()
        let suite = "region-live-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.beginDrawing()
        let source = try XCTUnwrap(model.active)
        let fixtures = [
            fixture(NSColor(srgbRed: 1, green: 0, blue: 0, alpha: 1),
                    region: Rect(x: 80, y: 80, width: 100, height: 80), source: source.panel.frame),
            fixture(NSColor(srgbRed: 0, green: 1, blue: 0, alpha: 1),
                    region: Rect(x: 210, y: 90, width: 100, height: 80), source: source.panel.frame),
            fixture(NSColor(srgbRed: 0, green: 0, blue: 1, alpha: 1),
                    region: Rect(x: 400, y: 90, width: 100, height: 80), source: source.panel.frame)
        ]
        defer {
            model.closeAll()
            DisplayCapture.useControlledWindowsForTesting(nil)
            for panel in fixtures { panel.orderOut(nil); panel.close() }
            defaults.removePersistentDomain(forName: suite)
        }
        let ids = Set(fixtures.map { CGWindowID($0.windowNumber) })
        var fixturesReady = false
        for _ in 0..<200 {
            let available = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            fixturesReady = ids.isSubset(of: Set(available.windows.map(\.windowID)))
            if fixturesReady { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(fixturesReady)
        DisplayCapture.useControlledWindowsForTesting(ids)
        let sharing = model.regionSharing
        sharing.showControls()
        sharing.applyCustom(Rect(x: 64, y: 64, width: 280, height: 160))
        XCTAssertEqual(sharing.outlineFillOpacity, 0.08)
        var blur = Element(tool: .blur, frame: Rect(x: 70, y: 70, width: 40, height: 40))
        blur.style.effectAmount = 2
        try source.document.add(blur)
        model.synchronize()
        try await ready { source.effectsReady || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertTrue(source.effectsReady)
        XCTAssertEqual(source.presentation?.captureAttemptCount, 1)
        sharing.start()
        XCTAssertEqual(sharing.outlineFillOpacity, 0, "The selection tint disappears before the first live frame.")
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(sharing.state, .live)
        XCTAssertEqual(source.presentation?.captureAttemptCount, 1, "Zoom/effects and the region must reuse one display stream.")
        let output = try XCTUnwrap(sharing.output)
        let identity = output.window.windowNumber
        let initial = try await colors(in: output.window)
        print("CONTROLLED COLORS: red=\(initial.red), green=\(initial.green), blue=\(initial.blue), total=\(initial.total)")
        XCTAssertGreaterThan(Double(initial.red) / Double(initial.total), 0.03)
        XCTAssertGreaterThan(Double(initial.green) / Double(initial.total), 0.03)
        XCTAssertLessThan(Double(initial.blue) / Double(initial.total), 0.005,
            "The blue source window outside the chosen region must not appear.")
        sharing.stop()
        XCTAssertEqual(sharing.outlineFillOpacity, 0.08)
        XCTAssertTrue(output.content.isCovered)
        XCTAssertTrue(source.presentation?.hasRequestedCapture == true,
            "Stopping a region cannot stop an effect that still needs pixels.")
        let stopped = try await colors(in: output.window)
        XCTAssertLessThan(Double(stopped.red) / Double(stopped.total), 0.005)
        XCTAssertLessThan(Double(stopped.green) / Double(stopped.total), 0.005)
        sharing.start()
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(output.window.windowNumber, identity)
        sharing.choosePreset(.rightHalf)
        XCTAssertFalse(sharing.state.isCapturing)
        XCTAssertTrue(output.content.isCovered)
        XCTAssertEqual(output.window.windowNumber, identity)
        sharing.applyCustom(Rect(x: 64, y: 64, width: 280, height: 160))
        sharing.start()
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        var drawing = Element(tool: .rectangle, frame: Rect(x: 230, y: 100, width: 60, height: 50))
        drawing.style.color = RGBA(0, 0, 1)
        drawing.style.fill = RGBA(0, 0, 1)
        let cover = Element(tool: .cover, frame: Rect(x: 80, y: 80, width: 80, height: 80))
        try source.document.add(drawing)
        try source.document.add(cover)
        model.synchronize()
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        let drawn = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(drawn.blue) / Double(drawn.total), 0.03)
        let history = source.document
        model.concealWorkspaceSurfaces()
        XCTAssertTrue(sharing.state.isCapturing, "Hiding drawings and tools must leave the region on.")
        XCTAssertTrue(output.content.isCovered, "Hide immediately covers the old drawings until the new GPU frame is ready.")
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertTrue(source.panel.ignoresMouseEvents)
        let hidden = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(hidden.red) / Double(hidden.total), 0.03)
        XCTAssertGreaterThan(Double(hidden.green) / Double(hidden.total), 0.03)
        XCTAssertLessThan(Double(hidden.blue) / Double(hidden.total), 0.005, "Hidden drawings must not remain shared.")
        XCTAssertGreaterThan(hidden.red, drawn.red + hidden.total / 20, "Hiding drawings also removes opaque covers.")
        XCTAssertEqual(source.document.elements, history.elements)
        XCTAssertEqual(source.document.canUndo, history.canUndo)
        XCTAssertEqual(source.presentation?.captureAttemptCount, 1)
        model.revealWorkspaceSurfaces()
        XCTAssertTrue(sharing.state.isCapturing)
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        let restored = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(restored.blue) / Double(restored.total), 0.03)
        model.setBoard(.light)
        try await Task.sleep(for: .milliseconds(100))
        let board = try await colors(in: output.window)
        XCTAssertLessThan(Double(board.green) / Double(board.total), 0.005)
        model.concealWorkspaceSurfaces()
        try await ready { sharing.state == .live || model.errorMessage != nil }
        let hiddenBoard = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(hiddenBoard.green) / Double(hiddenBoard.total), 0.03,
            "Hiding drawings must remove the board background from the shared output too.")
        model.revealWorkspaceSurfaces()
        try await ready { sharing.state == .live || model.errorMessage != nil }
        model.suspendPresentation()
        XCTAssertFalse(sharing.state.isCapturing, "System transitions still stop region sharing.")
        XCTAssertTrue(output.content.isCovered)
        sharing.stop()
        XCTAssertTrue(sharing.output === output, "Start/Stop keeps the same meeting source window.")
        try source.document.clearAll()
        model.synchronize()
        XCTAssertFalse(source.presentation?.hasRequestedCapture == true)
        if initial.red > 0 && initial.green > 0 {
            print("CONTROLLED REGION: owned sources cropped; outside source excluded; selection-only tint; Stop opaque; drawings, covers and board hide while capture stays live; history preserved; one display stream. No meeting or unrelated desktop captured.")
        }
    }

    func testActualToolbarQuickHalvesAndFullyOccludedOutputKeepUpdating() async throws {
        try permitControlledCapture()
        let suite = "region-toolbar-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        app.finishLaunchPresentation(isLoginItem: false)
        model.beginDrawing()
        model.interact()
        try await Task.sleep(for: .milliseconds(150))
        let source = try XCTUnwrap(model.active)
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        let area = Rect(x: 40, y: 40,
            width: min(source.panel.frame.width - 80, max(600, toolbar.frame.width + 80)),
            height: min(source.panel.frame.height - 80, max(320, toolbar.frame.height + 80)))
        let screenArea = RegionSharingGuide.screenFrame(for: area, on: source.panel.frame)
        toolbar.setFrameOrigin(CGPoint(x: screenArea.minX + 20, y: screenArea.maxY - toolbar.frame.height - 20))
        let host = try XCTUnwrap(toolbar.contentView)
        let marker = NSView(frame: CGRect(x: host.bounds.midX - 28, y: host.bounds.midY - 18, width: 56, height: 36))
        marker.wantsLayer = true
        marker.layer?.backgroundColor = NSColor(srgbRed: 0, green: 0, blue: 1, alpha: 1).cgColor
        host.addSubview(marker)
        let background = fixture(NSColor(srgbRed: 0, green: 1, blue: 0, alpha: 1),
                                 region: area, source: source.panel.frame)
        toolbar.orderFrontRegardless()
        var blocker: NSPanel?
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            DisplayCapture.useControlledWindowsForTesting(nil)
            marker.removeFromSuperview()
            background.orderOut(nil); background.close()
            blocker?.orderOut(nil); blocker?.close()
            defaults.removePersistentDomain(forName: suite)
        }
        DisplayCapture.useControlledWindowsForTesting([CGWindowID(background.windowNumber)])
        let sharing = model.regionSharing
        app.performMenuAction(.shareRegion)
        sharing.applyCustom(area)
        sharing.controls?.orderOut(nil)
        let nativeToolbar = try await colors(in: toolbar)
        print("CONTROLLED TOOLBAR INPUT: blue=\(nativeToolbar.blue), total=\(nativeToolbar.total), frame=\(toolbar.frame), area=\(area)")
        XCTAssertGreaterThan(Double(nativeToolbar.blue) / Double(nativeToolbar.total), 0.004,
            "The toolbar source must contain the test marker before output inclusion is asserted.")
        app.performMenuAction(.toggleRegionSharing)
        XCTAssertEqual(sharing.outlineFillOpacity, 0)
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertTrue(sharing.isCapturingToolbar)
        let output = try XCTUnwrap(sharing.output)
        XCTAssertFalse(output.window.isKeyWindow, "Starting sharing must not focus the output window.")
        let identity = output.window.windowNumber
        let sharedFraction = output.content.contentFrame.width * output.content.contentFrame.height /
            (output.window.frame.width * output.window.frame.height)
        let visible = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(visible.blue) / Double(visible.total), 0.002,
            "Pixels from the actual native toolbar must be included after desktop projection.")
        let menu = NSHostingView(rootView: MenuBarPopoverView(model: model, developerPreview: true, perform: { _ in }))
        let menuPanel = NSPanel(contentRect: CGRect(x: 60, y: 60, width: 320, height: 640),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        menuPanel.isReleasedWhenClosed = false
        menuPanel.contentView = menu
        menuPanel.orderFrontRegardless()
        defer { menuPanel.orderOut(nil); menuPanel.contentView = nil; menuPanel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(100))
        let stopRow = try XCTUnwrap(NativeAccessibilityNode.nodes(in: menu).first {
            $0.accessibilityIdentifier() == "menu-toggleRegionSharing"
        })
        XCTAssertEqual(stopRow.accessibilityLabel(), "Stop region sharing")
        XCTAssertEqual(stopRow.accessibilityValue() as? String, "On")
        let menuBitmap = try XCTUnwrap(menu.bitmapImageRepForCachingDisplay(in: menu.bounds))
        menu.cacheDisplay(in: menu.bounds, to: menuBitmap)
        var redPixels = 0
        for y in 0..<menuBitmap.pixelsHigh {
            for x in 0..<menuBitmap.pixelsWide {
                let color = try XCTUnwrap(menuBitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
                if color.redComponent > color.greenComponent + 0.3 && color.redComponent > color.blueComponent + 0.3 {
                    redPixels += 1
                }
            }
        }
        let menuScale = Double(menuBitmap.pixelsWide) / 320
        XCTAssertGreaterThan(Double(redPixels), 80 * menuScale * menuScale,
            "The active Stop row must render its red accent, not inherit primary text color.")
        menuPanel.orderOut(nil)
        app.performMenuAction(.hideTools)
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(sharing.isCapturingToolbar)
        let hidden = try await colors(in: output.window)
        XCTAssertLessThan(Double(hidden.blue) / Double(hidden.total), 0.001)
        XCTAssertGreaterThan(Double(hidden.green) / Double(hidden.total), sharedFraction * 0.95,
            "At least 95% of the shared content, excluding letterboxing and the title bar, must show the source.")
        app.performMenuAction(.showTools)
        try await ready { sharing.isCapturingToolbar && sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        let shown = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(shown.blue) / Double(shown.total), 0.002)
        toolbar.setFrameOrigin(CGPoint(x: screenArea.maxX + 20, y: screenArea.minY))
        try await ready { !sharing.isCapturingToolbar || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        let outside = try await colors(in: output.window)
        XCTAssertLessThan(Double(outside.blue) / Double(outside.total), 0.001)
        toolbar.setFrameOrigin(CGPoint(x: screenArea.minX + 20, y: screenArea.maxY - toolbar.frame.height - 20))
        try await ready { sharing.isCapturingToolbar && sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        toolbar.close()
        try await ready { !sharing.isCapturingToolbar || model.errorMessage != nil }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(sharing.state, .live, "Closing the toolbar must release only its stream, not the region.")

        blocker = fixture(NSColor(srgbRed: 1, green: 0, blue: 1, alpha: 1),
                          region: Rect(x: 0, y: 0, width: 64, height: 64), source: source.panel.frame)
        blocker?.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 20)
        blocker?.setFrame(output.window.frame, display: true)
        blocker?.orderFrontRegardless()
        let cover = try XCTUnwrap(blocker)
        XCTAssertTrue(cover.isOpaque)
        XCTAssertEqual(cover.alphaValue, 1)
        XCTAssertTrue(cover.frame.contains(output.window.frame))
        try await Task.sleep(for: .milliseconds(100))
        let windows = try XCTUnwrap(CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]])
        let ownedOrder = windows.compactMap { info -> CGWindowID? in
            guard (info[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == ProcessInfo.processInfo.processIdentifier else {
                return nil
            }
            return (info[kCGWindowNumber as String] as? NSNumber)?.uint32Value
        }
        print("CONTROLLED OUTPUT VISIBILITY: activeSpace=\(output.window.isOnActiveSpace), visible=\(output.window.isVisible), occlusion=\(output.window.occlusionState.rawValue), outputListed=\(ownedOrder.contains(CGWindowID(output.window.windowNumber))), blockerListed=\(ownedOrder.contains(CGWindowID(cover.windowNumber)))")
        XCTAssertLessThan(try XCTUnwrap(ownedOrder.firstIndex(of: CGWindowID(cover.windowNumber))),
                          try XCTUnwrap(ownedOrder.firstIndex(of: CGWindowID(output.window.windowNumber))),
                          "The opaque blocker must cover the entire output and be above it.")
        XCTAssertTrue(output.window.isVisible, "The output stays registered even when another window covers it.")
        var presentations = 0
        output.content.onFrameMetrics = { _ in presentations += 1 }
        let previous = presentations
        background.backgroundColor = NSColor(srgbRed: 1, green: 0, blue: 0, alpha: 1)
        background.displayIfNeeded()
        try await ready { presentations > previous || model.errorMessage != nil }
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertNil(model.errorMessage)
        let occluded = try await colors(in: output.window)
        XCTAssertGreaterThan(Double(occluded.red) / Double(occluded.total), sharedFraction * 0.95,
            "The fully covered output must contain the updated source, not a frozen preview.")
        XCTAssertLessThan(Double(occluded.magenta) / Double(occluded.total), 0.001,
            "Other Annotator windows and the output's occluder must not leak into its source.")

        app.performMenuAction(.shareLeftHalf)
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(sharing.selectedPreset, .leftHalf)
        XCTAssertEqual(sharing.region, try SharingRegion(preset: .leftHalf, displayBounds: Rect(source.canvas.bounds)))
        XCTAssertEqual(output.window.windowNumber, identity)
        app.performMenuAction(.shareRightHalf)
        try await ready { sharing.state == .live || model.errorMessage != nil }
        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(sharing.selectedPreset, .rightHalf)
        XCTAssertEqual(output.window.windowNumber, identity)
        app.performMenuAction(.toggleRegionSharing)
        XCTAssertFalse(sharing.state.isCapturing)
        XCTAssertFalse(sharing.isCapturingToolbar)
        XCTAssertTrue(output.content.isCovered)
        XCTAssertFalse(source.presentation?.hasRequestedCapture == true)
        app.performMenuAction(.shareRegion)
        XCTAssertTrue(try XCTUnwrap(sharing.controls).isVisible)
    }
}
