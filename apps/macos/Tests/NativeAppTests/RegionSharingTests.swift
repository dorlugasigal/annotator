import AppKit
import CoreVideo
import ScreenCaptureKit
import SwiftUI
import XCTest
import AnnotationCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class RegionSharingTests: XCTestCase {
    private func workspace(displays: [DesktopDisplay]? = nil) throws -> (WorkspaceModel, UserDefaults, String) {
        let suite = "region-sharing-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let candidates = displays ?? [
            DesktopDisplay(id: 101, frame: CGRect(x: 0, y: 0, width: 800, height: 600), scale: 2),
            DesktopDisplay(id: 202, frame: CGRect(x: -640, y: 120, width: 640, height: 480), scale: 1)
        ]
        let model = WorkspaceModel(defaults: defaults, desktopDisplays: { candidates })
        model.screenPointer = { CGPoint(x: 100, y: 100) }
        model.beginDrawing()
        return (model, defaults, suite)
    }

    private func mouse(_ type: NSEvent.EventType, at point: Point, view: NSView) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: view.convert(point.cgPoint, to: nil),
            modifierFlags: [], timestamp: 0, windowNumber: try XCTUnwrap(view.window).windowNumber,
            context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }

    private func buffer(red: UInt8 = 255, green: UInt8 = 0) throws -> CVPixelBuffer {
        var result: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(kCFAllocatorDefault, 64, 64, kCVPixelFormatType_32BGRA,
            [kCVPixelBufferMetalCompatibilityKey: true, kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary,
            &result), kCVReturnSuccess)
        let pixels = try XCTUnwrap(result)
        CVPixelBufferLockBaseAddress(pixels, [])
        defer { CVPixelBufferUnlockBaseAddress(pixels, []) }
        let base = try XCTUnwrap(CVPixelBufferGetBaseAddress(pixels)).assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(pixels)
        for y in 0..<64 {
            for x in 0..<64 {
                let index = y * stride + x * 4
                base[index] = 0; base[index + 1] = green; base[index + 2] = red; base[index + 3] = 255
            }
        }
        return pixels
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<400 {
            if condition() { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("The owned region view did not become ready within four seconds.")
    }

    func testPresetControlsAndOutlineDoNotStartCaptureOrChangeDrawings() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 80, y: 80, width: 120, height: 100))
        try session.document.add(shape)
        model.choose(.select)
        session.document.select([shape.id])
        model.synchronize()
        let before = session.document
        let sharing = model.regionSharing
        sharing.showControls()
        XCTAssertEqual(sharing.selectedDisplayID, 101)
        let controls = try XCTUnwrap(sharing.controls?.contentView)
        NativeAccessibilityNode.enable()
        controls.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        for preset in ShareRegionPreset.allCases {
            let node = try XCTUnwrap(NativeAccessibilityNode.nodes(in: controls).first {
                $0.accessibilityRole() == .button && $0.accessibilityLabel() == preset.title
            })
            XCTAssertTrue(node.accessibilityPerformPress())
            controls.layoutSubtreeIfNeeded()
            RunLoop.current.run(until: Date().addingTimeInterval(0.01))
            XCTAssertEqual(sharing.region, try SharingRegion(preset: preset, displayBounds: Rect(session.canvas.bounds)))
        }
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.presentation?.hasRequestedCapture == false })
        XCTAssertFalse(sharing.hasOutput)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertEqual(model.tool, .select)
        XCTAssertEqual(session.document.elements, before.elements)
        XCTAssertEqual(session.document.selection, before.selection)
        XCTAssertEqual(session.document.canUndo, before.canUndo)
        sharing.selectDisplay(202)
        let region = try XCTUnwrap(sharing.region)
        XCTAssertEqual(sharing.outlineFrame, RegionSharingGuide.screenFrame(for: region.bounds,
            on: CGRect(x: -640, y: 120, width: 640, height: 480)))
        sharing.controls?.close()
        XCTAssertNil(sharing.outlineFrame)
    }

    func testCustomFieldsRejectInvalidRequestsAndKeepTheLastRegion() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let sharing = model.regionSharing
        sharing.showControls()
        let original = sharing.region
        for invalid in [
            Rect(x: -1, y: 0, width: 100, height: 100),
            Rect(x: 700, y: 0, width: 200, height: 100),
            Rect(x: 0, y: 0, width: 31, height: 100),
            Rect(x: .nan, y: 0, width: 100, height: 100)
        ] {
            sharing.applyCustom(invalid)
            XCTAssertEqual(sharing.region, original)
            XCTAssertNotNil(sharing.validationMessage)
        }
        sharing.applyCustom(Rect(x: 20, y: 40, width: 320, height: 200))
        XCTAssertEqual(sharing.region?.bounds, Rect(x: 20, y: 40, width: 320, height: 200))
        XCTAssertNil(sharing.selectedPreset)
        XCTAssertNil(sharing.validationMessage)
        sharing.selectDisplay(202)
        XCTAssertNil(sharing.region, "A custom region is not silently replaced on another display.")
        XCTAssertNotNil(sharing.validationMessage)
        XCTAssertFalse(sharing.hasOutput)
    }

    func testStartingDuringWorkspaceMotionDoesNotRequestCapture() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.visibilityAnimating = false; model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let sharing = model.regionSharing
        sharing.showControls()
        model.visibilityAnimating = true
        sharing.start()
        XCTAssertFalse(sharing.state.isCapturing)
        XCTAssertFalse(sharing.hasOutput)
        XCTAssertTrue(sharing.state.message?.contains("workspace animation") == true)
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.presentation?.hasRequestedCapture == false })
    }

    func testChooserPreviewsWithHiddenToolbarAndFitsWithoutOptionalToggles() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.interact()
        model.concealWorkspaceSurfaces()
        model.openRegionSharing()
        let sharing = model.regionSharing
        sharing.choosePreset(.centerThird)
        let expected = try XCTUnwrap(sharing.region)
        let actual = try XCTUnwrap(sharing.outlineFrame)
        let expectedFrame = RegionSharingGuide.screenFrame(for: expected.bounds,
            on: CGRect(x: 0, y: 0, width: 800, height: 600))
        XCTAssertEqual(actual.minX, expectedFrame.minX, accuracy: 1)
        XCTAssertEqual(actual.minY, expectedFrame.minY, accuracy: 1)
        XCTAssertEqual(actual.width, expectedFrame.width, accuracy: 1)
        XCTAssertEqual(actual.height, expectedFrame.height, accuracy: 1)
        XCTAssertTrue(model.workspaceHidden)
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.panel.ignoresMouseEvents && !$0.panel.isVisible })
        let host = try XCTUnwrap(sharing.controls?.contentView)
        NativeAccessibilityNode.enable()
        host.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        let nodes = NativeAccessibilityNode.nodes(in: host)
        XCTAssertFalse(nodes.contains { $0.accessibilityLabel() == "Show the pointer" || $0.accessibilityLabel() == "Show the region outline" })
        XCTAssertFalse(nodes.contains { $0.accessibilityLabel() == "Pause region" || $0.accessibilityLabel() == "Show window" })
        XCTAssertFalse(nodes.contains { $0.accessibilityIdentifier() == "region-short-screen-scroll" })
        XCTAssertTrue(nodes.contains { $0.accessibilityIdentifier() == "region-start-stop" })
        XCTAssertEqual(RegionOutlineView.fillOpacity, 0.08)
        XCTAssertEqual(sharing.outlineFillOpacity, 0.08)
    }

    func testLiveOutlineClearsSelectionTintWithoutRemovingItsBorder() throws {
        let guide = RegionSharingGuide()
        defer { guide.close() }
        let region = try SharingRegion(bounds: Rect(x: 20, y: 30, width: 160, height: 100),
                                       displayBounds: Rect(x: 0, y: 0, width: 400, height: 300))
        for selecting in [true, false, true, false] {
            guide.showOutline(region: region, displayFrame: CGRect(x: 30, y: 30, width: 400, height: 300),
                              selectionFill: selecting)
            let view = try XCTUnwrap(guide.outlineWindow?.contentView as? RegionOutlineView)
            let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
            view.cacheDisplay(in: view.bounds, to: bitmap)
            let center = try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide / 2, y: bitmap.pixelsHigh / 2))
            XCTAssertEqual(center.alphaComponent, selecting ? 0.08 : 0, accuracy: 0.01)
            let border = try XCTUnwrap(bitmap.colorAt(x: 1, y: bitmap.pixelsHigh / 2))
            XCTAssertGreaterThan(border.alphaComponent, 0.6)
            XCTAssertEqual(guide.outlineFillOpacity, selecting ? 0.08 : 0)
            XCTAssertTrue(try XCTUnwrap(guide.outlineWindow).ignoresMouseEvents)
        }
    }

    func testCustomRectangleSupportsBothDirectionsAndEscapeReleasesItsWindow() throws {
        let guide = RegionSharingGuide()
        defer { guide.close() }
        let display = CGRect(x: 40, y: 40, width: 500, height: 400)
        let displays = [RegionSharingDisplay(id: 101, name: "Test display", frame: display, scale: 1)]
        var selected: Rect?
        for reversed in [false, true] {
            guide.choose(on: displays, preferredDisplayID: 101) {
                if case .selected(let id, let bounds) = $0 { XCTAssertEqual(id, 101); selected = bounds }
            }
            let panel = try XCTUnwrap(guide.selectionWindows[101])
            let view = try XCTUnwrap(panel.contentView as? RegionSelectionView)
            let start = reversed ? Point(350, 280) : Point(80, 60)
            let finish = reversed ? Point(80, 60) : Point(350, 280)
            view.mouseDown(with: try mouse(.leftMouseDown, at: start, view: view))
            view.mouseDragged(with: try mouse(.leftMouseDragged, at: finish, view: view))
            view.mouseUp(with: try mouse(.leftMouseUp, at: finish, view: view))
            XCTAssertEqual(selected, Rect(x: 80, y: 60, width: 270, height: 220))
            XCTAssertFalse(guide.isSelecting)
            XCTAssertTrue(panel.ignoresMouseEvents)
            XCTAssertFalse(panel.isVisible)
        }
        var cancelled = false
        guide.choose(on: displays, preferredDisplayID: 101) { cancelled = $0 == .cancelled }
        let panel = try XCTUnwrap(guide.selectionWindows[101])
        let view = try XCTUnwrap(panel.contentView as? RegionSelectionView)
        let escape = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero,
            modifierFlags: [], timestamp: 0, windowNumber: panel.windowNumber, context: nil,
            characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53))
        view.keyDown(with: escape)
        XCTAssertTrue(cancelled)
        XCTAssertFalse(guide.isSelecting)
        XCTAssertTrue(panel.ignoresMouseEvents)
        var lostFocus = false
        guide.choose(on: displays, preferredDisplayID: 101) { lostFocus = $0 == .focusLost }
        let focused = try XCTUnwrap(guide.selectionWindows[101])
        focused.resignKey()
        RunLoop.current.run(until: Date().addingTimeInterval(0.01))
        XCTAssertTrue(lostFocus)
        XCTAssertFalse(guide.isSelecting)
        XCTAssertFalse(focused.isVisible)
    }

    func testCustomSelectionUsesDragDisplayAndReleasesEveryPanel() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let sharing = model.regionSharing
        sharing.showControls()
        let previous = sharing.region
        let documents = model.desktopSessions.map(\.document)
        for targetID: CGDirectDisplayID in [202, 101] {
            sharing.drawCustomRegion()
            let panels = sharing.selectionWindows
            XCTAssertEqual(Set(panels.keys), [101, 202])
            for panel in panels.values {
                let point = CGPoint(x: panel.frame.midX, y: panel.frame.midY)
                XCTAssertTrue(model.cursorForPointer(at: point, windowNumber: panel.windowNumber) === NSCursor.crosshair)
                let view = try XCTUnwrap(panel.contentView as? RegionSelectionView)
                view.updateTrackingAreas()
                XCTAssertTrue(view.trackingAreas.contains { $0.options.contains(.activeAlways) && $0.options.contains(.cursorUpdate) })
                view.cursorUpdate(with: try mouse(.mouseMoved, at: Point(50, 50), view: view))
                XCTAssertTrue(NSCursor.current === NSCursor.crosshair)
            }
            let target = try XCTUnwrap(panels[targetID])
            target.makeKey()
            RunLoop.current.run(until: Date().addingTimeInterval(0.01))
            XCTAssertTrue(sharing.isSelectingRegion, "Focus handoff within the picker must not cancel it.")
            let view = try XCTUnwrap(target.contentView as? RegionSelectionView)
            XCTAssertTrue(view.acceptsFirstMouse(for: nil))
            view.mouseDown(with: try mouse(.leftMouseDown, at: Point(350, 280), view: view))
            view.mouseUp(with: try mouse(.leftMouseUp, at: Point(80, 60), view: view))
            XCTAssertEqual(sharing.selectedDisplayID, targetID)
            XCTAssertEqual(sharing.region?.bounds, Rect(x: 80, y: 60, width: 270, height: 220))
            XCTAssertEqual(sharing.outlineFrame, RegionSharingGuide.screenFrame(
                for: try XCTUnwrap(sharing.region).bounds, on: target.frame))
            XCTAssertFalse(sharing.isSelectingRegion)
            XCTAssertTrue(panels.values.allSatisfy { $0.ignoresMouseEvents && !$0.isVisible && $0.contentView == nil })
        }
        XCTAssertNotEqual(sharing.region, previous)
        XCTAssertNil(sharing.selectedPreset)
        XCTAssertFalse(sharing.hasOutput)
        for (session, document) in zip(model.desktopSessions, documents) {
            XCTAssertEqual(session.document.elements, document.elements)
            XCTAssertEqual(session.document.selection, document.selection)
            XCTAssertEqual(session.document.canUndo, document.canUndo)
        }
    }

    func testCustomSelectionCancelAndInvalidDragKeepPreviousDisplayAndRegion() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let sharing = model.regionSharing
        sharing.showControls()
        let previous = sharing.region
        for cancel in [true, false] {
            sharing.drawCustomRegion()
            let panels = sharing.selectionWindows
            let target = try XCTUnwrap(panels[202])
            target.makeKey()
            let view = try XCTUnwrap(target.contentView as? RegionSelectionView)
            if cancel {
                let escape = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero,
                    modifierFlags: [], timestamp: 0, windowNumber: target.windowNumber, context: nil,
                    characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53))
                view.keyDown(with: escape)
            } else {
                view.mouseDown(with: try mouse(.leftMouseDown, at: Point(80, 60), view: view))
                view.mouseUp(with: try mouse(.leftMouseUp, at: Point(90, 70), view: view))
                XCTAssertNotNil(sharing.validationMessage)
            }
            XCTAssertEqual(sharing.selectedDisplayID, 101)
            XCTAssertEqual(sharing.region, previous)
            XCTAssertFalse(sharing.isSelectingRegion)
            XCTAssertTrue(panels.values.allSatisfy { $0.ignoresMouseEvents && !$0.isVisible })
        }
    }

    func testOutlineRebindsToDisplayAndRendersAtEveryPresetSize() throws {
        let guide = RegionSharingGuide()
        defer { guide.close() }
        let frames = [
            CGRect(x: 0, y: 0, width: 800, height: 600),
            CGRect(x: -640, y: 120, width: 640, height: 480),
            CGRect(x: 100, y: 600, width: 960, height: 720)
        ]
        var previous: NSPanel?
        var previousFrame: CGRect?
        for frame in frames + frames.reversed() {
            for preset in ShareRegionPreset.allCases {
                let region = try SharingRegion(preset: preset,
                    displayBounds: Rect(x: 0, y: 0, width: frame.width, height: frame.height))
                guide.showOutline(region: region, displayFrame: frame)
                let panel = try XCTUnwrap(guide.outlineWindow)
                if previousFrame != nil && previousFrame != frame {
                    XCTAssertFalse(previous === panel, "A new display needs its own Space-bound outline.")
                }
                if let previous, previous !== panel { XCTAssertFalse(previous.isVisible) }
                XCTAssertTrue(panel.isVisible)
                XCTAssertTrue(panel.ignoresMouseEvents)
                let expected = RegionSharingGuide.screenFrame(for: region.bounds, on: frame)
                XCTAssertEqual(panel.frame.minX, expected.minX, accuracy: 1)
                XCTAssertEqual(panel.frame.minY, expected.minY, accuracy: 1)
                let view = try XCTUnwrap(panel.contentView)
                XCTAssertEqual(view.bounds.width, expected.width, accuracy: 1)
                XCTAssertEqual(view.bounds.height, expected.height, accuracy: 1)
                let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
                view.cacheDisplay(in: view.bounds, to: bitmap)
                XCTAssertGreaterThan(try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide - 2,
                    y: bitmap.pixelsHigh / 2)).alphaComponent, 0.6)
                previous = panel
                previousFrame = frame
            }
        }
    }

    func testOutlineUsesTheTargetNativeScreen() throws {
        let guide = RegionSharingGuide()
        defer { guide.close() }
        for screen in NSScreen.screens {
            let region = try SharingRegion(preset: .leftHalf,
                displayBounds: Rect(x: 0, y: 0, width: screen.frame.width, height: screen.frame.height))
            guide.showOutline(region: region, displayFrame: screen.frame)
            let panel = try XCTUnwrap(guide.outlineWindow)
            XCTAssertEqual(panel.screen, screen)
            XCTAssertEqual(panel.frame, RegionSharingGuide.screenFrame(for: region.bounds, on: screen.frame))
            XCTAssertTrue(panel.isVisible)
            XCTAssertFalse(panel.isKeyWindow)
            XCTAssertEqual(panel.sharingType, .readOnly)
        }
    }

    func testGuideWindowStartsOnItsDisplayBeforeAnyFrameCorrection() throws {
        for screen in NSScreen.screens {
            let frames = [screen.frame, CGRect(x: screen.frame.minX + 64, y: screen.frame.minY + 64,
                                               width: 320, height: 240)]
            for expected in frames {
                let panel = NSPanel(contentRect: RegionSharingGuide.contentRect(for: expected, on: screen),
                    styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false, screen: screen)
                panel.isReleasedWhenClosed = false
                defer { panel.close() }
                XCTAssertEqual(panel.frame, expected, "The initial frame must not apply the screen offset twice.")
                XCTAssertEqual(panel.screen, screen, "The window must start on its target screen, not off-screen.")
            }
        }
        let synthetic = CGRect(x: -700, y: 80, width: 320, height: 240)
        XCTAssertEqual(RegionSharingGuide.contentRect(for: synthetic, on: nil), synthetic)
    }

    func testRegionGuidesStayAboveAppWindowsOnEveryNativeDisplay() async throws {
        let guide = RegionSharingGuide()
        var occluders: [NSPanel] = []
        defer {
            guide.close()
            for panel in occluders { panel.orderOut(nil); panel.close() }
        }
        let displays = NSScreen.screens.enumerated().map { index, screen in
            RegionSharingDisplay(id: CGDirectDisplayID(index + 1), name: screen.localizedName,
                                 frame: screen.frame, scale: screen.backingScaleFactor)
        }
        XCTAssertFalse(displays.isEmpty)
        for display in displays {
            let panel = NSPanel(contentRect: display.frame.insetBy(dx: 80, dy: 80),
                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.backgroundColor = .gray
            panel.isOpaque = true
            panel.hidesOnDeactivate = false
            panel.level = .popUpMenu
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .canJoinAllApplications]
            panel.setFrame(display.frame.insetBy(dx: 80, dy: 80), display: true)
            occluders.append(panel)
        }
        func assertAboveApps(_ panel: NSPanel) throws {
            XCTAssertGreaterThan(panel.level.rawValue, NSWindow.Level.popUpMenu.rawValue)
            XCTAssertGreaterThan(panel.level.rawValue, PresentationWindowLevels.toolbar(screenZoom: true).rawValue)
            XCTAssertTrue(panel.collectionBehavior.contains(.canJoinAllApplications))
            XCTAssertTrue(panel.collectionBehavior.contains(.canJoinAllSpaces))
            XCTAssertTrue(panel.collectionBehavior.contains(.fullScreenAuxiliary))
            // A later orderFront must not put even an elevated app panel over the guide.
            for occluder in occluders { occluder.orderFrontRegardless() }
            let order = try XCTUnwrap(NSWindow.windowNumbers(options: [])).map(\.intValue)
            let guideIndex = try XCTUnwrap(order.firstIndex(of: panel.windowNumber))
            for occluder in occluders {
                XCTAssertLessThan(guideIndex, try XCTUnwrap(order.firstIndex(of: occluder.windowNumber)))
            }
        }
        for display in displays {
            let region = try SharingRegion(preset: .leftHalf, displayBounds: display.localBounds)
            guide.showOutline(region: region, displayFrame: display.frame)
            let outline = try XCTUnwrap(guide.outlineWindow)
            try assertAboveApps(outline)
            XCTAssertTrue(outline.ignoresMouseEvents)
            XCTAssertFalse(outline.isKeyWindow)
        }
        guide.hideOutline()
        guide.choose(on: displays, preferredDisplayID: try XCTUnwrap(displays.first).id) { _ in }
        let panels = guide.selectionWindows
        XCTAssertEqual(panels.count, displays.count)
        try await waitUntil {
            panels.values.allSatisfy { panel in
                NSWindow.windowNumber(at: CGPoint(x: panel.frame.midX, y: panel.frame.midY),
                                      belowWindowWithWindowNumber: 0) == panel.windowNumber
            }
        }
        for panel in panels.values {
            try assertAboveApps(panel)
            XCTAssertFalse(panel.ignoresMouseEvents)
            XCTAssertEqual(panel.sharingType, .readOnly)
            panel.displayIfNeeded()
            let center = CGPoint(x: panel.frame.midX, y: panel.frame.midY)
            let hit = NSWindow.windowNumber(at: center, belowWindowWithWindowNumber: 0)
            XCTAssertEqual(hit, panel.windowNumber, "The drag must reach the guide, not an app beneath it.")
            XCTAssertTrue(guide.cursor(at: center, windowNumber: hit) === NSCursor.crosshair)
            let view = try XCTUnwrap(panel.contentView as? RegionSelectionView)
            view.mouseDown(with: try mouse(.leftMouseDown, at: Point(100, 100), view: view))
            view.mouseDragged(with: try mouse(.leftMouseDragged, at: Point(300, 250), view: view))
            let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(
                in: CGRect(x: 90, y: 90, width: 220, height: 180)))
            view.cacheDisplay(in: CGRect(x: 90, y: 90, width: 220, height: 180), to: bitmap)
            let border = try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide * 10 / 220, y: bitmap.pixelsHigh / 2))
            let interior = try XCTUnwrap(bitmap.colorAt(x: bitmap.pixelsWide / 2, y: bitmap.pixelsHigh / 2))
            XCTAssertGreaterThan(border.alphaComponent, 0.8)
            XCTAssertLessThan(interior.alphaComponent, 0.01)
            XCTAssertTrue(guide.isSelecting)
        }
        guide.cancelSelection()
        XCTAssertTrue(panels.values.allSatisfy { !$0.isVisible && $0.ignoresMouseEvents })
    }

    func testCustomDragClampsToStartingDisplayAndFocusLossClosesAllPanels() throws {
        let guide = RegionSharingGuide()
        defer { guide.close() }
        let displays = [
            RegionSharingDisplay(id: 101, name: "Primary",
                frame: CGRect(x: 0, y: 0, width: 800, height: 600), scale: 2),
            RegionSharingDisplay(id: 202, name: "Secondary",
                frame: CGRect(x: -640, y: 120, width: 640, height: 480), scale: 1)
        ]
        var result: RegionSelectionResult?
        guide.choose(on: displays, preferredDisplayID: 101) { result = $0 }
        let view = try XCTUnwrap(guide.selectionWindows[202]?.contentView as? RegionSelectionView)
        view.mouseDown(with: try mouse(.leftMouseDown, at: Point(200, 150), view: view))
        view.mouseUp(with: try mouse(.leftMouseUp, at: Point(900, -200), view: view))
        XCTAssertEqual(result, .selected(displayID: 202, bounds: Rect(x: 200, y: 0, width: 440, height: 150)))
        XCTAssertFalse(guide.isSelecting)

        result = nil
        guide.choose(on: displays, preferredDisplayID: 101) { result = $0 }
        let panels = guide.selectionWindows
        panels[101]?.resignKey()
        RunLoop.current.run(until: Date().addingTimeInterval(0.01))
        XCTAssertEqual(result, .focusLost)
        XCTAssertFalse(guide.isSelecting)
        XCTAssertTrue(panels.values.allSatisfy { $0.ignoresMouseEvents && !$0.isVisible })
    }

    func testSystemTransitionCancelsRegionSelectionWithoutChangingHistory() throws {
        let (model, defaults, suite) = try workspace()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let session = try XCTUnwrap(model.active)
        let item = Element(tool: .text, frame: Rect(x: 60, y: 60, width: 100, height: 40), text: "Keep me")
        try session.document.add(item)
        let sharing = model.regionSharing
        sharing.showControls()
        let previous = sharing.region
        sharing.drawCustomRegion()
        XCTAssertTrue(sharing.isSelectingRegion)
        model.suspendPresentation()
        XCTAssertFalse(sharing.isSelectingRegion)
        XCTAssertEqual(sharing.region, previous)
        XCTAssertEqual(session.document.elements, [item])
        XCTAssertTrue(session.document.canUndo)
        XCTAssertTrue(model.desktopSessions.allSatisfy { $0.panel.ignoresMouseEvents })
    }

    func testMissingDisplayNeverFallsBackToAnotherDisplay() throws {
        let suite = "region-display-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        var displays = [
            DesktopDisplay(id: 101, frame: CGRect(x: 0, y: 0, width: 800, height: 600), scale: 1),
            DesktopDisplay(id: 202, frame: CGRect(x: -640, y: 0, width: 640, height: 480), scale: 1)
        ]
        let model = WorkspaceModel(defaults: defaults, desktopDisplays: { displays })
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let sharing = model.regionSharing
        sharing.showControls()
        sharing.selectDisplay(101)
        displays.removeFirst()
        model.displaysChanged()
        XCTAssertEqual(sharing.selectedDisplayID, 101)
        XCTAssertNil(sharing.region)
        XCTAssertNil(sharing.outlineFrame)
        XCTAssertNotNil(sharing.validationMessage)
        XCTAssertFalse(sharing.hasOutput)
    }

    func testSharedWindowStaysOpaqueUntilReadyAndRejectsStalePresentation() async throws {
        let region = try SharingRegion(preset: .leftHalf, displayBounds: Rect(x: 0, y: 0, width: 64, height: 64))
        let output = try SharedRegionWindow(region: region, displayFrame: try XCTUnwrap(NSScreen.main).visibleFrame)
        defer { output.close() }
        output.show()
        XCTAssertTrue(output.window.isOpaque)
        XCTAssertEqual(output.window.sharingType, .readOnly)
        XCTAssertEqual(output.window.title, SharedRegionWindow.title)
        XCTAssertTrue(output.content.isCovered)
        var rendered: UInt64?
        var failure: Error?
        output.content.onPresented = { rendered = $0 }
        output.content.onFailure = { failure = $0 }
        try output.content.present(buffer(), sourceBounds: region.bounds, sourceSize: CGSize(width: 64, height: 64),
            elements: [], background: nil, generation: 1)
        try await waitUntil { rendered == 1 || failure != nil }
        if let failure { throw failure }
        XCTAssertFalse(output.content.isCovered)
        try output.content.present(buffer(red: 0, green: 255), sourceBounds: region.bounds,
            sourceSize: CGSize(width: 64, height: 64), elements: [], background: nil, generation: 2)
        output.content.cover("Paused for privacy", generation: 3)
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(output.content.isCovered, "An earlier GPU callback cannot uncover a paused window.")
        XCTAssertNotEqual(rendered, 2)
        let identity = output.window.windowNumber
        let changed = try SharingRegion(preset: .rightHalf, displayBounds: region.displayBounds)
        output.content.configure(region: changed)
        try output.content.present(buffer(), sourceBounds: changed.bounds, sourceSize: CGSize(width: 64, height: 64),
            elements: [], background: nil, generation: 4)
        try await waitUntil { rendered == 4 || failure != nil }
        if let failure { throw failure }
        XCTAssertEqual(output.window.windowNumber, identity)
        XCTAssertFalse(output.content.isCovered)
    }

    func testOutputResizingLetterboxesAndDoesNotRebuildUnchangedDrawings() async throws {
        let region = try SharingRegion(preset: .leftHalf, displayBounds: Rect(x: 0, y: 0, width: 64, height: 64))
        let output = try SharedRegionWindow(region: region, displayFrame: try XCTUnwrap(NSScreen.main).visibleFrame)
        defer { output.close() }
        output.show()
        var rendered: UInt64?
        output.content.onPresented = { rendered = $0 }
        for (index, size) in [CGSize(width: 800, height: 400), CGSize(width: 400, height: 600)].enumerated() {
            output.window.setContentSize(size)
            output.content.layoutSubtreeIfNeeded()
            let generation = UInt64(index + 1)
            try output.content.present(buffer(red: index == 0 ? 255 : 0, green: index == 0 ? 0 : 255),
                sourceBounds: region.bounds, sourceSize: CGSize(width: 64, height: 64),
                elements: [], background: nil, generation: generation)
            try await waitUntil { rendered == generation }
            let frame = output.content.contentFrame
            XCTAssertEqual(frame.width / frame.height, 0.5, accuracy: 0.0001)
            XCTAssertTrue(output.content.bounds.contains(frame))
        }
        XCTAssertEqual(output.content.drawingRenderCount, 1, "Live source frames and window sizes reuse the drawing layer.")
    }

    func testOutputOpensAtTheRegionsOwnSizeBesideItAndRefitsForANewRegion() throws {
        let screen = try XCTUnwrap(NSScreen.main)
        let display = Rect(x: 0, y: 0, width: screen.frame.width, height: screen.frame.height)
        let visible = screen.visibleFrame
        let width = min(600, (visible.width / 3).rounded(.down)), height = min(400, (visible.height / 3).rounded(.down))
        let corner = try SharingRegion(bounds: Rect(x: 0, y: display.height - height, width: width, height: height),
                                       displayBounds: display)
        let output = try SharedRegionWindow(region: corner, displayFrame: screen.frame)
        defer { output.close() }
        output.show(inBackground: true)
        XCTAssertTrue(output.content.smoothDownscaling, "A window smaller than the region must not drop pixel rows.")
        var content = output.window.contentRect(forFrameRect: output.window.frame)
        XCTAssertEqual(content.size, CGSize(width: width, height: height), "Meeting apps get the region at its own size.")
        XCTAssertFalse(output.window.frame.intersects(RegionSharingGuide.screenFrame(for: corner.bounds, on: screen.frame)))
        XCTAssertTrue(visible.contains(output.window.frame))

        output.window.setContentSize(CGSize(width: 500, height: 320))
        let resized = output.window.frame
        try output.fit(region: corner, displayFrame: screen.frame)
        XCTAssertEqual(output.window.frame, resized, "The same region keeps the size people chose.")

        let half = try SharingRegion(preset: .leftHalf, displayBounds: display)
        try output.fit(region: half, displayFrame: screen.frame)
        content = output.window.contentRect(forFrameRect: output.window.frame)
        let available = Rect(x: visible.minX - screen.frame.minX, y: screen.frame.maxY - visible.maxY,
                             width: visible.width, height: visible.height)
        let expected = RegionSharingGuide.screenFrame(for: try half.outputContentFrame(available: available,
            frame: FrameInsets(top: output.window.frame.height - content.height)), on: screen.frame)
        for (actual, wanted) in [(content.minX, expected.minX), (content.maxY, expected.maxY),
                                 (content.width, expected.width), (content.height, expected.height)] {
            XCTAssertEqual(actual, wanted, accuracy: 1)
        }
        XCTAssertEqual(content.width / content.height, half.bounds.width / half.bounds.height, accuracy: 0.01)
        XCTAssertTrue(visible.contains(output.window.frame))
    }

    func testSharedTextIncludesTheNativeEditorBeforeCommit() throws {
        let suite = "shared-text-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 500, height: 400), background: .white)
        let fixture = NativeCanvasFixture(image: image, defaults: defaults)
        defer { fixture.close(); defaults.removePersistentDomain(forName: suite) }
        fixture.model.choose(.text)
        let canvas = fixture.session.canvas
        canvas.mouseDown(with: try mouse(.leftMouseDown, at: Point(80, 80), view: canvas))
        let editor = try XCTUnwrap(canvas.subviews.compactMap { $0 as? NSTextView }.first)
        editor.insertText("Live label", replacementRange: NSRange(location: NSNotFound, length: 0))
        canvas.textDidChange(Notification(name: NSText.didChangeNotification, object: editor))
        XCTAssertEqual(canvas.elementsForSharing.first?.text, "Live label")
        XCTAssertEqual(canvas.elementsForSharing.first?.frame, Rect(editor.frame))
        XCTAssertEqual(fixture.session.document.elements.first?.text, "")
        canvas.commitTextIfNeeded()
        XCTAssertEqual(canvas.elementsForSharing.first?.text, "Live label")
    }
}
