import AppKit
import XCTest
import AnnotationCore
import NativePlatform
@testable import AnnotatorApp

@MainActor
final class LiveZoomStartTests: XCTestCase {
    func testControlOneStartsZoomFromOffDrawingAndUsingApps() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1")
        try XCTSkipUnless(DisplayCapture.hasPermission)
        try XCTSkipIf(DisplayCapture.desktopIsLocked)
        let screen = try XCTUnwrap(NSScreen.main)
        let backdrop = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                               backing: .buffered, defer: false)
        backdrop.isReleasedWhenClosed = false
        backdrop.isOpaque = true
        backdrop.backgroundColor = .lightGray
        backdrop.ignoresMouseEvents = true
        backdrop.orderFrontRegardless()
        DisplayCapture.useControlledWindowsForTesting([try XCTUnwrap(CGWindowID(exactly: backdrop.windowNumber))])
        defer {
            DisplayCapture.useControlledWindowsForTesting(nil)
            backdrop.orderOut(nil); backdrop.close()
        }
        for mode in [WorkspaceModel.Mode.off, .draw, .interact] {
            let suite = "control-one-route-\(UUID())"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            let model = WorkspaceModel(defaults: defaults)
            defer { model.stopZoom(); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
            if mode != .off { model.beginDrawing() }
            if mode == .interact { model.interact() }
            model.active?.panel.ignoresMouseEvents = true
            let panel = AnnotationPanel(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
            panel.imagePasteModel = model
            defer { panel.orderOut(nil) }
            let key = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .control,
                timestamp: 0, windowNumber: panel.windowNumber, context: nil, characters: "1",
                charactersIgnoringModifiers: "1", isARepeat: false, keyCode: 18))
            XCTAssertTrue(panel.performKeyEquivalent(with: key))
            for _ in 0..<900 {
                model.active?.panel.ignoresMouseEvents = true
                if model.zoomStartState == .active { break }
                if case .failed = model.zoomStartState { break }
                try await Task.sleep(for: .milliseconds(10))
            }
            XCTAssertEqual(model.zoomStartState, .active)
            XCTAssertTrue(model.screenZoomEnabled)
            XCTAssertGreaterThan(model.magnification, 1)
            for drawing in [true, false] {
                if drawing { model.beginDrawing() } else { model.interact() }
                model.active?.panel.ignoresMouseEvents = true
                let before = model.magnification
                let event = try XCTUnwrap(CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1,
                                                 wheel1: 8, wheel2: 0, wheel3: 0))
                event.flags = .maskAlternate
                XCTAssertTrue(model.handleLocalZoomScroll(try XCTUnwrap(NSEvent(cgEvent: event))))
                model.finishZoomGesture()
                XCTAssertGreaterThan(model.magnification, before)
                XCTAssertTrue(model.screenZoomEnabled)
            }
            XCTAssertTrue(panel.performKeyEquivalent(with: key))
            XCTAssertFalse(model.screenZoomEnabled)
        }
    }

    func testGrantedZoomRequestPresentsAdjustsStopsAndRestarts() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1",
                          "Real zoom rendering is explicitly opted in.")
        try XCTSkipUnless(DisplayCapture.hasPermission, "Screen-pixel permission is needed for live zoom acceptance.")
        try XCTSkipIf(DisplayCapture.desktopIsLocked)
        let suite = "real-zoom-start-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.stopZoom(); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        model.magnification = 1
        model.toggleScreenZoom()
        for _ in 0..<900 {
            if model.zoomStartState == .active { break }
            if case .failed = model.zoomStartState { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(model.zoomStartState, .active)
        XCTAssertTrue(model.screenZoomEnabled)
        XCTAssertGreaterThan(model.magnification, 1, "Starting at 1x must not look like a no-op.")
        let before = model.magnification
        model.scrollScreenZoom(15)
        model.finishZoomGesture()
        XCTAssertGreaterThan(model.magnification, before)
        model.stopZoom()
        XCTAssertFalse(model.screenZoomEnabled)
        XCTAssertEqual(model.zoomStartState, .idle)
        model.toggleScreenZoom()
        for _ in 0..<900 {
            if model.zoomStartState == .active { break }
            if case .failed = model.zoomStartState { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(model.zoomStartState, .active)
        model.handleZoomEscape()
        XCTAssertFalse(model.screenZoomEnabled)
        XCTAssertEqual(model.zoomStartState, .idle)
    }

    func testScreenZoomRampsInAndOutOnTheSameViewportAndStopReverses() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_LIVE_CAPTURE_TEST"] == "1",
                          "Real zoom rendering is explicitly opted in.")
        try XCTSkipUnless(DisplayCapture.hasPermission, "Screen-pixel permission is needed for live zoom acceptance.")
        try XCTSkipIf(DisplayCapture.desktopIsLocked)
        let suite = "real-zoom-ramp-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.stopZoom(); model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.animatesScreenZoom = { true }
        var levels: [Bool] = []
        model.onScreenZoomChanged = { levels.append($0) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let presentation = try XCTUnwrap(session.presentation)

        func checkViewport() throws {
            guard let viewport = presentation.screenViewport else { return }
            XCTAssertEqual(viewport.source.width, session.canvas.bounds.width / presentation.magnification, accuracy: 0.0001)
            let pointer = NSEvent.mouseLocation
            let local = Point(pointer.x - session.panel.frame.minX, session.panel.frame.maxY - pointer.y)
            let shown = viewport.outputPoint(for: viewport.sourcePoint(for: local))
            XCTAssertEqual(shown.x, local.x, accuracy: 0.001)
            XCTAssertEqual(shown.y, local.y, accuracy: 0.001)
        }
        func sample(while condition: () -> Bool) async throws -> [Double] {
            var values: [Double] = []
            for _ in 0..<300 where condition() {
                values.append(presentation.magnification)
                try checkViewport()
                try await Task.sleep(for: .milliseconds(5))
            }
            return values
        }

        model.toggleScreenZoomFromShortcut()
        XCTAssertEqual(model.magnification, 1.5, "The target is stored at once.")
        XCTAssertTrue(model.screenZoomEnabled)
        let rampIn = try await sample { presentation.isRampingScreenZoom }
        XCTAssertEqual(model.zoomStartState, .active)
        XCTAssertEqual(presentation.magnification, 1.5, accuracy: 0.000001)
        XCTAssertTrue(rampIn.contains { $0 > 1.01 && $0 < 1.49 }, "Zoom must pass through intermediate factors.")
        XCTAssertEqual(rampIn, rampIn.sorted())

        model.stopZoom()
        XCTAssertFalse(model.screenZoomEnabled)
        XCTAssertEqual(session.document.presentation.mode, .off)
        XCTAssertTrue(presentation.isExitingScreenZoom)
        XCTAssertEqual(presentation.presentationMode, .screen, "Pixels stay zoomed while the display returns to 1x.")
        XCTAssertEqual(levels.last, true, "Window levels wait for the zoom-out ramp.")
        let rampOut = try await sample { presentation.isExitingScreenZoom }
        XCTAssertEqual(rampOut, rampOut.sorted(by: >))
        XCTAssertTrue(rampOut.contains { $0 > 1.01 && $0 < 1.49 })
        XCTAssertEqual(presentation.presentationMode, .off)
        XCTAssertEqual(levels.last, false)
        XCTAssertTrue(model.canUndo)
        model.undo()
        XCTAssertTrue(model.screenZoomEnabled, "Stopping is one undoable step.")
        model.stopZoom()
        _ = try await sample { presentation.isRampingScreenZoom }

        model.magnification = 4
        model.toggleScreenZoom()
        for _ in 0..<3000 {
            if presentation.isRampingScreenZoom && presentation.magnification > 1.3 { break }
            try await Task.sleep(for: .milliseconds(1))
        }
        XCTAssertTrue(presentation.isRampingScreenZoom)
        let reversed = presentation.magnification
        model.stopZoom()
        XCTAssertTrue(presentation.isExitingScreenZoom)
        XCTAssertEqual(presentation.magnification, reversed, accuracy: 0.000001, "Stop reverses from the displayed factor.")
        let reverse = try await sample { presentation.isExitingScreenZoom }
        XCTAssertTrue(reverse.allSatisfy { $0 <= reversed + 0.000001 })
        XCTAssertEqual(presentation.presentationMode, .off)

        model.animatesScreenZoom = { false }
        model.toggleScreenZoomFromShortcut()
        for _ in 0..<900 where model.zoomStartState != .active { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertFalse(presentation.isRampingScreenZoom, "Reduce Motion starts zoom immediately.")
        XCTAssertEqual(presentation.magnification, 1.5)
        model.stopZoom()
        XCTAssertFalse(presentation.isRampingScreenZoom)
        XCTAssertEqual(presentation.presentationMode, .off, "Reduce Motion stops zoom immediately.")
    }
}
