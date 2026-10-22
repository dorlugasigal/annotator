import XCTest
@testable import AnnotatorApp

@MainActor
final class ZoomStartControllerTests: XCTestCase {
    func testScreenPermissionCanStartZoomWithoutGestureAccess() {
        var requests = 0, starts = 0
        let controller = ZoomStartController(screenGranted: { true }, gesturesGranted: { false },
            requestScreen: { requests += 1 }, requestGestures: { requests += 1 },
            automaticChecks: false, requiresGesturesForStart: false,
            start: { starts += 1 }, changed: { _ in })
        controller.request()
        XCTAssertEqual(starts, 1)
        XCTAssertEqual(requests, 0)
        controller.presented()
        XCTAssertEqual(controller.state, .active)
    }

    func testGrantingBothPermissionsResumesTheOriginalRequestOnce() {
        var screen = false, gestures = false, screenRequests = 0, gestureRequests = 0, starts = 0
        var states: [ZoomStartState] = []
        let controller = ZoomStartController(screenGranted: { screen }, gesturesGranted: { gestures },
            requestScreen: { screenRequests += 1 }, requestGestures: { gestureRequests += 1 },
            automaticChecks: false, start: { starts += 1 }, changed: { states.append($0) })
        controller.request()
        XCTAssertEqual(controller.state, .screenPermission)
        controller.refresh(); controller.request()
        XCTAssertEqual(screenRequests, 1)
        screen = true
        controller.refresh()
        XCTAssertEqual(controller.state, .gesturePermission)
        XCTAssertEqual(gestureRequests, 1)
        controller.refresh()
        XCTAssertEqual(gestureRequests, 1)
        gestures = true
        controller.refresh()
        XCTAssertEqual(controller.state, .starting)
        XCTAssertEqual(starts, 1)
        controller.request(); controller.refresh()
        XCTAssertEqual(starts, 1)
        controller.presented()
        XCTAssertEqual(controller.state, .active)
        XCTAssertEqual(states, [.screenPermission, .gesturePermission, .starting, .active])
    }

    func testExistingGrantsDoNotRequestAgainAndFailureCanRetry() {
        var requests = 0, starts = 0
        let controller = ZoomStartController(screenGranted: { true }, gesturesGranted: { true },
            requestScreen: { requests += 1 }, requestGestures: { requests += 1 },
            automaticChecks: false, start: { starts += 1 }, changed: { _ in })
        controller.request()
        XCTAssertEqual(requests, 0)
        XCTAssertEqual(starts, 1)
        controller.fail("Capture unavailable")
        XCTAssertEqual(controller.state, .failed("Capture unavailable"))
        controller.request()
        XCTAssertEqual(starts, 2)
        controller.cancel()
        controller.presented()
        XCTAssertEqual(controller.state, .idle)
    }

    func testCancellationDoesNotStartLaterWhenPermissionChanges() {
        var granted = false, starts = 0
        let controller = ZoomStartController(screenGranted: { granted }, gesturesGranted: { granted },
            requestScreen: {}, requestGestures: {}, automaticChecks: false, start: { starts += 1 }, changed: { _ in })
        controller.request()
        controller.cancel()
        granted = true
        controller.refresh()
        XCTAssertEqual(starts, 0)
        XCTAssertEqual(controller.state, .idle)
    }
}
