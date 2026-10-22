import AppKit
import Combine
import XCTest
import AnnotationCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class LocalZoomScrollTests: XCTestCase {
    func testContinuousZoomPublishesOncePerEventAndKeepsOneUndoStep() throws {
        let suite = "zoom-scroll-updates-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        editor.model.synchronize()
        editor.model.scrollScreenZoom(0.25)
        var notifications = 0
        let observation = editor.model.objectWillChange.sink { notifications += 1 }
        defer { observation.cancel() }
        for _ in 0..<120 {
            editor.model.scrollScreenZoom(0.25)
        }
        XCTAssertEqual(notifications, 120, "Scroll must not republish unrelated workspace fields.")
        XCTAssertEqual(editor.model.magnification, 2 * exp(121 * 0.25 * 0.012), accuracy: 0.000001)
        XCTAssertTrue(editor.model.canUndo)
        XCTAssertFalse(editor.model.canRedo)
        editor.model.finishZoomGesture()
        editor.model.undo()
        XCTAssertEqual(editor.model.magnification, 2, accuracy: 0.000001)
        XCTAssertEqual(editor.session.document.presentation.mode, .screen)
    }

    private func scroll(_ delta: Int32, modifiers: CGEventFlags = .maskAlternate) throws -> NSEvent {
        let event = try XCTUnwrap(CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1,
                                         wheel1: delta, wheel2: 0, wheel3: 0))
        event.flags = modifiers
        return try XCTUnwrap(NSEvent(cgEvent: event))
    }

    func testScreenZoomRampKeepsDisplayedFactorSeparateAndLandsWhenInterrupted() throws {
        let suite = "zoom-ramp-state-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let presentation = try XCTUnwrap(session.presentation)
        presentation.suspend()
        defer { presentation.stop() }

        XCTAssertTrue(presentation.apply(PresentationSettings(mode: .screen, zoom: 2), enteringFrom: 1))
        XCTAssertTrue(presentation.isRampingScreenZoom, "The entrance waits for the first zoomed frame.")
        XCTAssertEqual(presentation.magnification, 1)
        XCTAssertEqual(presentation.rampingScreenZoomFactor, 1)
        XCTAssertFalse(presentation.apply(PresentationSettings(mode: .screen, zoom: 2)))
        XCTAssertTrue(presentation.isRampingScreenZoom, "Synchronizing the same target keeps the ramp.")
        let viewport = try XCTUnwrap(presentation.screenViewport)
        XCTAssertEqual(viewport.source.width, session.canvas.bounds.width / presentation.magnification, accuracy: 0.0001,
                       "Drawing and hit-testing use the displayed factor, not the target.")

        XCTAssertTrue(presentation.retargetZoomRamp(to: 2.4))
        XCTAssertTrue(presentation.isRampingScreenZoom, "Scrolling during an entrance changes its target.")
        XCTAssertEqual(presentation.magnification, 1, "The display keeps easing from what is on screen.")

        presentation.magnification = 2.5
        XCTAssertFalse(presentation.isRampingScreenZoom, "A direct zoom change cancels the ramp.")
        XCTAssertEqual(presentation.magnification, 2.5)
        XCTAssertFalse(presentation.retargetZoomRamp(to: 3), "Without a ramp, scrolling sets the zoom directly.")

        presentation.apply(PresentationSettings(mode: .screen, zoom: 3), enteringFrom: 2.5)
        XCTAssertEqual(presentation.magnification, 2.5)
        presentation.stop()
        XCTAssertFalse(presentation.isRampingScreenZoom)
        XCTAssertEqual(presentation.magnification, 3, "Stopping lands an unfinished entrance on its target.")

        XCTAssertTrue(presentation.apply(PresentationSettings(mode: .off, zoom: 3), animatesExit: true))
        XCTAssertEqual(presentation.presentationMode, .off, "Without a visible zoomed frame, stopping is immediate.")
        XCTAssertFalse(presentation.isExitingScreenZoom)

        presentation.apply(PresentationSettings(mode: .screen, zoom: 2), enteringFrom: 1)
        presentation.presentationMode = .off
        XCTAssertFalse(presentation.isRampingScreenZoom)
        XCTAssertEqual(presentation.presentationMode, .off)
    }

    func testContinuousZeroPointScrollDoesNotBecomeAWheelNotch() throws {
        let event = try XCTUnwrap(try scroll(1).cgEvent)
        event.setIntegerValueField(.scrollWheelEventIsContinuous, value: 1)
        event.setDoubleValueField(.scrollWheelEventDeltaAxis1, value: 1)
        event.setDoubleValueField(.scrollWheelEventPointDeltaAxis1, value: 0)
        XCTAssertNil(ZoomGestureMonitor.zoomScrollDelta(event))
        event.setDoubleValueField(.scrollWheelEventPointDeltaAxis1, value: 2)
        XCTAssertEqual(ZoomGestureMonitor.zoomScrollDelta(event), 2)
        event.setDoubleValueField(.scrollWheelEventPointDeltaAxis1, value: 0)
        event.setIntegerValueField(.scrollWheelEventIsContinuous, value: 0)
        XCTAssertEqual(ZoomGestureMonitor.zoomScrollDelta(event), 12)
        event.setDoubleValueField(.scrollWheelEventDeltaAxis1, value: -1)
        event.setDoubleValueField(.scrollWheelEventPointDeltaAxis1, value: 0)
        XCTAssertEqual(ZoomGestureMonitor.zoomScrollDelta(event), -12)
    }

    func testOptionScrollDeliveredToAppChangesZoomWithoutGlobalAccess() throws {
        let suite = "local-zoom-scroll-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        editor.model.synchronize()
        editor.model.noteZoomGestures(available: false)
        let original = editor.session.document.elements
        editor.session.panel.sendEvent(try scroll(12))
        XCTAssertGreaterThan(editor.model.magnification, 2)
        let increased = editor.model.magnification
        editor.session.panel.sendEvent(try scroll(-12))
        XCTAssertLessThan(editor.model.magnification, increased)
        XCTAssertEqual(editor.model.magnification, 2, accuracy: 0.000001)
        XCTAssertEqual(editor.session.document.elements, original)
        editor.model.finishZoomGesture()
    }

    func testDeliveredLocalScrollWorksEvenWhenGlobalAccessIsReportedAvailable() throws {
        let suite = "local-zoom-filter-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
        let editor = NativeCanvasFixture(image: image, defaults: defaults)
        defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        editor.model.synchronize()
        XCTAssertFalse(editor.model.handleLocalZoomScroll(try scroll(12, modifiers: [])))
        XCTAssertFalse(editor.model.handleLocalZoomScroll(try scroll(12, modifiers: .maskControl)))
        XCTAssertFalse(editor.model.handleLocalZoomScroll(try scroll(12, modifiers: [.maskControl, .maskAlternate])))
        XCTAssertFalse(editor.model.handleLocalZoomScroll(try scroll(0)))
        editor.model.noteZoomGestures(available: true)
        XCTAssertTrue(editor.model.handleLocalZoomScroll(try scroll(12)))
        XCTAssertGreaterThan(editor.model.magnification, 2)
        editor.model.noteZoomGestures(available: false)
        editor.model.stopZoom()
        XCTAssertFalse(editor.model.handleLocalZoomScroll(try scroll(12)))
    }

    func testZoomScrollAndPendingZoomSurviveDrawingModeChanges() throws {
        let suite = "zoom-mode-change-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        let image = try PNGExporter.image(elements:[],size:CGSize(width:400,height:300),background:.white)
        let editor = NativeCanvasFixture(image:image,defaults:defaults)
        defer { editor.close();defaults.removePersistentDomain(forName:suite) }
        try editor.session.document.setPresentation(PresentationSettings(mode:.screen,zoom:2))
        editor.model.synchronize()
        for available in [false,true] {
            editor.model.noteZoomGestures(available:available)
            editor.model.beginDrawing()
            let before = editor.model.magnification
            XCTAssertTrue(editor.model.handleLocalZoomScroll(try scroll(8)))
            let after = editor.model.magnification
            XCTAssertGreaterThan(after,before)
            editor.model.interact()
            XCTAssertEqual(editor.model.magnification,after,accuracy:0.0001)
            XCTAssertTrue(WorkspaceHint.text(model:editor.model).contains("Using apps"))
            XCTAssertTrue(WorkspaceHint.text(model:editor.model).contains("Option-scroll"))
            XCTAssertTrue(editor.model.handleLocalZoomScroll(try scroll(8)))
            let appsZoom = editor.model.magnification
            editor.model.beginDrawing()
            XCTAssertEqual(editor.model.magnification,appsZoom,accuracy:0.0001)
            XCTAssertTrue(WorkspaceHint.text(model:editor.model).contains("Drawing"))
        }
    }

    func testGlobalAndLocalParserLeaveSystemZoomModifiersAlone() throws {
        let passthrough: [CGEventFlags] = [[], .maskControl, .maskCommand, .maskShift,
                                         [.maskControl, .maskAlternate], [.maskCommand, .maskAlternate],
                                         [.maskShift, .maskAlternate]]
        for flags in passthrough {
            let event = try XCTUnwrap(try scroll(12, modifiers: flags).cgEvent)
            XCTAssertNil(ZoomGestureMonitor.zoomScrollDelta(event))
        }
        let event = try XCTUnwrap(try scroll(12).cgEvent)
        XCTAssertGreaterThan(try XCTUnwrap(ZoomGestureMonitor.zoomScrollDelta(event)), 0)
    }

    func testIdlePresentationCannotClearAnotherDisplaysScrollState() throws {
        let suite = "zoom-scroll-owner-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        model.interact()
        let session = try XCTUnwrap(model.active)
        let idle = LivePresentation(session: session, model: model)
        model.noteZoomGestures(available: true)
        idle.stop()
        XCTAssertTrue(model.zoomGesturesAvailable)
        model.noteZoomGestures(available: false, error: ZoomInputFailure.unavailable)
        let message = model.zoomGestureMessage
        idle.stop()
        XCTAssertEqual(model.zoomGestureMessage, message)
    }

    func testNativeTestsDoNotTakeOverTheUsersGlobalScroll() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil)
        try XCTSkipIf(ProcessInfo.processInfo.environment["ANNOTATOR_ENABLE_GLOBAL_INPUT_TEST"] == "1")
        let monitor = ZoomGestureMonitor(scroll: { _ in XCTFail("No global input should be received.") },
                                         exit: { XCTFail("No global input should be received.") },
                                         failure: { _ in })
        XCTAssertThrowsError(try monitor.start()) { error in
            guard case ZoomInputFailure.testInputDisabled = error else {
                return XCTFail("Expected the test-process safety guard.")
            }
        }
        XCTAssertFalse(ZoomGestureMonitor.requestPermission(), "Automated tests must not prompt for real Accessibility access.")
    }

    func testGlobalHandlerConsumesOptionScrollInBothModes() throws {
            let suite = "global-zoom-mode-\(UUID())"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            let image = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
            let editor = NativeCanvasFixture(image: image, defaults: defaults)
            defer { editor.close(); defaults.removePersistentDomain(forName: suite) }
            try editor.session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
            editor.model.synchronize()
            let monitor = ZoomGestureMonitor(scroll: { editor.model.scrollScreenZoom($0) },
                                             exit: { editor.model.handleZoomEscape() },
                                             failure: { _ in XCTFail("No live event tap is installed in this test.") })
            for drawing in [true, false, true] {
                if drawing { editor.model.beginDrawing() } else { editor.model.interact() }
                let before = editor.model.magnification
                let event = try XCTUnwrap(try scroll(8).cgEvent)
                XCTAssertTrue(monitor.handle(type: .scrollWheel, event: event),
                              "Consumed global scroll must not reach the app behind the overlay.")
                XCTAssertGreaterThan(editor.model.magnification, before)
                editor.model.finishZoomGesture()
            }
            for flags: CGEventFlags in [[], .maskControl, [.maskAlternate, .maskControl]] {
                XCTAssertFalse(monitor.handle(type: .scrollWheel, event: try XCTUnwrap(try scroll(8, modifiers: flags).cgEvent)))
            }
        }

    func testActivationHandlerConsumesOptionScrollButLeavesEscapeAlone() throws {
        var receivedDelta: Double?
        var exited = false
        let monitor = ZoomGestureMonitor(
            scroll: { receivedDelta = $0 },
            exit: { exited = true },
            failure: { _ in XCTFail("No live event tap is installed in this test.") },
            capturesEscape: false
        )
        let scrollEvent = try XCTUnwrap(try scroll(8).cgEvent)
        XCTAssertTrue(monitor.handle(type: .scrollWheel, event: scrollEvent))
        XCTAssertEqual(receivedDelta, 8)

        let escape = try XCTUnwrap(CGEvent(keyboardEventSource: nil, virtualKey: 53, keyDown: true))
        XCTAssertFalse(monitor.handle(type: .keyDown, event: escape))
        XCTAssertFalse(exited)
    }

    func testToolbarOptionScrollActivationRunsOnlyWhileVisibleAndIdle() throws {
        XCTAssertTrue(ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: true, workspaceHidden: false, visibilityAnimating: false,
            screenZoomEnabled: false, permissionGranted: true
        ))
        XCTAssertFalse(ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: false, workspaceHidden: false, visibilityAnimating: false,
            screenZoomEnabled: false, permissionGranted: true
        ))
        XCTAssertFalse(ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: true, workspaceHidden: true, visibilityAnimating: false,
            screenZoomEnabled: false, permissionGranted: true
        ))
        XCTAssertFalse(ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: true, workspaceHidden: false, visibilityAnimating: true,
            screenZoomEnabled: false, permissionGranted: true
        ))
        XCTAssertFalse(ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: true, workspaceHidden: false, visibilityAnimating: false,
            screenZoomEnabled: true, permissionGranted: true
        ))
        XCTAssertFalse(ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: true, workspaceHidden: false, visibilityAnimating: false,
            screenZoomEnabled: false, permissionGranted: false
        ))
        XCTAssertTrue(ToolbarZoomActivation.shouldHandleScroll(try XCTUnwrap(try scroll(8).cgEvent)))
        XCTAssertFalse(ToolbarZoomActivation.shouldHandleScroll(try XCTUnwrap(try scroll(-8).cgEvent)))
    }

    func testDisplayRoutingUsesTheScrollEventsPosition() throws {
            let event = try XCTUnwrap(try scroll(8).cgEvent)
            let leftDisplay = CGRect(x: -1440, y: -200, width: 1440, height: 900)
            event.location = CGPoint(x: -600, y: 700)
            XCTAssertTrue(ZoomGestureMonitor.eventIsOnDisplay(event, frame: leftDisplay, primaryScreenTop: 1080))
            event.location = CGPoint(x: 800, y: 400)
            XCTAssertFalse(ZoomGestureMonitor.eventIsOnDisplay(event, frame: leftDisplay, primaryScreenTop: 1080))
            let monitor = ZoomGestureMonitor(scroll: { _ in XCTFail("Wrong display must not change zoom.") },
                                             exit: {}, failure: { _ in },
                                             shouldHandleScroll: {
                ZoomGestureMonitor.eventIsOnDisplay($0, frame: leftDisplay, primaryScreenTop: 1080)
            })
            XCTAssertFalse(monitor.handle(type: .scrollWheel, event: event))
        }

    func testMissingScrollPermissionPromptsOncePerZoomRequestAndExplainsWhy() throws {
            let suite = "zoom-permission-request-\(UUID())"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
            var granted = false
            var requests = 0
            let model = WorkspaceModel(defaults: defaults, zoomInputPermissions: ZoomInputPermissions(
                isGranted: { granted }, request: { requests += 1; return granted }))
            defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
            XCTAssertEqual(requests, 0)
            model.prepareZoomScrollAccess()
            model.prepareZoomScrollAccess()
            XCTAssertEqual(requests, 1)
            XCTAssertFalse(model.zoomGesturesAvailable)
            XCTAssertTrue(model.zoomScrollStatus.contains("Accessibility"))
            model.stopZoom()
            model.prepareZoomScrollAccess()
            XCTAssertEqual(requests, 2)
            granted = true
            model.prepareZoomScrollAccess()
            XCTAssertEqual(requests, 2)
            model.noteZoomGestures(available: true)
            XCTAssertTrue(model.zoomScrollStatus.contains("Drawing and Using apps"))
        }
}
