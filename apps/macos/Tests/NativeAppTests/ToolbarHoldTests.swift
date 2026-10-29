import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class ToolbarHoldTests: XCTestCase {
    private var cleanup: [() -> Void] = []

    override func tearDown() {
        cleanup.reversed().forEach { $0() }
        cleanup = []
        super.tearDown()
    }

    private func toolbar(properties: Bool = false) async throws -> (WorkspaceModel, InspectorPanel) {
        NativeAccessibilityNode.enable()
        let suite = "toolbar-hold-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setPropertiesShown(properties)
        let app = AppDelegate(model: model)
        cleanup.append {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(150))
        let panel = try XCTUnwrap(app.toolbarWindow as? InspectorPanel)
        panel.animationBehavior = .none
        panel.makeKeyAndOrderFront(nil)
        panel.contentView?.layoutSubtreeIfNeeded()
        return (model, panel)
    }

    /// The middle of a control, in the window's coordinates.
    private func center(of label: String, role: NSAccessibility.Role, in panel: InspectorPanel) throws -> CGPoint {
        let content = try XCTUnwrap(panel.contentView)
        content.layoutSubtreeIfNeeded()
        // Plain text reads its words as its value; controls read them as their label.
        let node = try XCTUnwrap(NativeAccessibilityNode.nodes(in: content).first {
            $0.accessibilityRole() == role && ($0.accessibilityLabel() == label || $0.accessibilityValue() as? String == label)
        }, "\(label) is on the toolbar")
        let frame = node.accessibilityFrame()
        return panel.convertPoint(fromScreen: CGPoint(x: frame.midX, y: frame.midY))
    }

    private func event(_ type: NSEvent.EventType, at point: CGPoint, in panel: InspectorPanel) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(with: type, location: point, modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
            context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
    }

    private func waitForHold() async throws {
        try await Task.sleep(for: .milliseconds(Int((ToolbarHold.delay + 0.2) * 1_000)))
    }

    func testHoldingAButtonMovesTheToolbarInsteadOfPressingIt() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        let start = try center(of: "Board background", role: .button, in: panel)
        let screenStart = panel.convertPoint(toScreen: start)
        let origin = panel.frame.origin
        panel.sendEvent(try event(.leftMouseDown, at: start, in: panel))
        XCTAssertTrue(panel.isHoldingPress, "The press waits to see whether it is a hold.")
        try await waitForHold()
        XCTAssertFalse(panel.isHoldingPress)
        XCTAssertTrue(panel.isTrackingToolbarDrag, "Holding still picks the toolbar up.")
        for step in 1...4 {
            let target = CGPoint(x: screenStart.x + Double(step) * 10, y: screenStart.y - Double(step) * 8)
            panel.sendEvent(try event(.leftMouseDragged, at: panel.convertPoint(fromScreen: target), in: panel))
        }
        let target = CGPoint(x: screenStart.x + 40, y: screenStart.y - 32)
        panel.sendEvent(try event(.leftMouseUp, at: panel.convertPoint(fromScreen: target), in: panel))
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        XCTAssertEqual(panel.frame.origin.x, origin.x + 40, accuracy: 0.5)
        XCTAssertEqual(panel.frame.origin.y, origin.y - 32, accuracy: 0.5)
        XCTAssertEqual(model.board, .transparent, "The held button never got its click.")
    }

    func testHoldingALabelMovesTheToolbar() async throws {
        let (model, panel) = try await toolbar(properties: true)
        model.choose(.pen)
        try await Task.sleep(for: .milliseconds(150))
        panel.leftButtonIsDown = { true }
        let label = try center(of: "Pattern", role: .staticText, in: panel)
        let origin = panel.frame.origin
        panel.sendEvent(try event(.leftMouseDown, at: label, in: panel))
        try await waitForHold()
        XCTAssertTrue(panel.isTrackingToolbarDrag)
        let target = panel.convertPoint(toScreen: label)
        panel.sendEvent(try event(.leftMouseUp, at: panel.convertPoint(fromScreen: CGPoint(x: target.x - 25, y: target.y - 15)),
                                  in: panel))
        XCTAssertEqual(panel.frame.origin.x, origin.x - 25, accuracy: 0.5)
        XCTAssertEqual(panel.frame.origin.y, origin.y - 15, accuracy: 0.5)
    }

    func testQuickAndSlowClicksStillPressTheButton() async throws {
        let (model, panel) = try await toolbar()
        let board = try center(of: "Board background", role: .button, in: panel)
        panel.sendEvent(try event(.leftMouseDown, at: board, in: panel))
        try await Task.sleep(for: .milliseconds(80))
        panel.sendEvent(try event(.leftMouseUp, at: board, in: panel))
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(model.board, .light)
        // The button was already up when the hold ended: the press goes to the button, then its release. The whiteboard
        // adds its own button to the toolbar, so the board button has moved.
        panel.leftButtonIsDown = { false }
        let moved = try center(of: "Board background", role: .button, in: panel)
        panel.sendEvent(try event(.leftMouseDown, at: moved, in: panel))
        try await waitForHold()
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        panel.sendEvent(try event(.leftMouseUp, at: moved, in: panel))
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(model.board, .dark)
    }

    /// Sends `type` at `offset` points from `start` on the screen, in the window's coordinates as they are now.
    private func send(_ type: NSEvent.EventType, _ start: CGPoint, _ dx: Double, _ dy: Double, to panel: InspectorPanel) throws {
        let point = panel.convertPoint(fromScreen: CGPoint(x: start.x + dx, y: start.y + dy))
        panel.sendEvent(try event(type, at: point, in: panel))
    }

    /// Presses at `local`, drags by (`dx`, `dy`) in a few steps and lets go, as a quick drag does.
    private func pressAndDrag(at local: CGPoint, by dx: Double, _ dy: Double, in panel: InspectorPanel) throws {
        let start = panel.convertPoint(toScreen: local)
        try send(.leftMouseDown, start, 0, 0, to: panel)
        for step in 1...4 {
            try send(.leftMouseDragged, start, dx * Double(step) / 4, dy * Double(step) / 4, to: panel)
        }
        try send(.leftMouseUp, start, dx, dy, to: panel)
    }

    func testMovingBeforeTheHoldEndsMovesTheToolbar() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        let start = panel.convertPoint(toScreen: try center(of: "Board background", role: .button, in: panel))
        let origin = panel.frame.origin
        var drags = 0
        let started = panel.onUserDragStarted
        panel.onUserDragStarted = { drags += 1; started?() }
        try send(.leftMouseDown, start, 0, 0, to: panel)
        try send(.leftMouseDragged, start, 1, 0, to: panel)
        XCTAssertTrue(panel.isHoldingPress, "A small wobble is still a press.")
        XCTAssertEqual(drags, 0)
        try send(.leftMouseDragged, start, 1, -6, to: panel)
        XCTAssertFalse(panel.isHoldingPress)
        XCTAssertTrue(panel.isTrackingToolbarDrag, "Moving on picks the toolbar up at once, from the press.")
        try send(.leftMouseDragged, start, 40, -32, to: panel)
        try send(.leftMouseUp, start, 40, -32, to: panel)
        try await waitForHold()
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        XCTAssertEqual(drags, 1)
        XCTAssertEqual(panel.frame.origin.x, origin.x + 40, accuracy: 0.5)
        XCTAssertEqual(panel.frame.origin.y, origin.y - 32, accuracy: 0.5)
        XCTAssertEqual(model.board, .transparent, "A press that moved the toolbar never reaches the button.")
    }

    /// Tester report: the first drag stops at once, the second works, the third stops, and so on.
    func testEveryDragInARowMovesTheToolbar() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        for attempt in 0..<6 {
            let direction: Double = attempt % 2 == 0 ? 1 : -1
            let origin = panel.frame.origin
            try pressAndDrag(at: try center(of: "Board background", role: .button, in: panel), by: 60 * direction,
                             -40 * direction, in: panel)
            XCTAssertFalse(panel.isTrackingToolbarDrag, "Drag \(attempt + 1) ended")
            XCTAssertEqual(panel.frame.origin.x, origin.x + 60 * direction, accuracy: 0.5, "Drag \(attempt + 1)")
            XCTAssertEqual(panel.frame.origin.y, origin.y - 40 * direction, accuracy: 0.5, "Drag \(attempt + 1)")
        }
        XCTAssertEqual(model.board, .transparent)
    }

    func testDraggingWorksFromLabelsPropertiesAVerticalToolbarAndTheOneButton() async throws {
        let (model, panel) = try await toolbar(properties: true)
        panel.leftButtonIsDown = { true }
        model.choose(.pen)
        try await Task.sleep(for: .milliseconds(300))
        func check(_ name: String, at local: CGPoint, dx: Double = 50, dy: Double = -30) throws {
            let origin = panel.frame.origin
            try pressAndDrag(at: local, by: dx, dy, in: panel)
            XCTAssertFalse(panel.isTrackingToolbarDrag, name)
            XCTAssertEqual(panel.frame.origin.x, origin.x + dx, accuracy: 0.5, name)
            XCTAssertEqual(panel.frame.origin.y, origin.y + dy, accuracy: 0.5, name)
        }
        try check("A property's name", at: try center(of: "Pattern", role: .staticText, in: panel))
        try check("A tool button", at: try center(of: "Pen", role: .button, in: panel), dx: -50, dy: 30)
        try check("The toolbar's edge", at: CGPoint(x: panel.frame.width - 4, y: 4))
        model.setPropertiesShown(false)
        model.setToolbarOrientation(.vertical)
        try await Task.sleep(for: .milliseconds(600))
        // A vertical toolbar docks at a side, so it moves away from that side.
        let inward: Double = panel.frame.midX > (panel.screen?.frame.midX ?? 0) ? -60 : 60
        try check("A vertical toolbar's button", at: try center(of: "Pen", role: .button, in: panel), dx: inward, dy: 0)
        model.setToolbarOrientation(.horizontal)
        try await Task.sleep(for: .milliseconds(600))
        model.setToolbarCollapsed(true)
        try await Task.sleep(for: .milliseconds(600))
        XCTAssertTrue(model.toolbarCollapsed)
        try check("The one button", at: CGPoint(x: panel.frame.width / 2, y: panel.frame.height / 2), dx: -40, dy: -20)
        XCTAssertEqual(model.tool, .pen, "No button under a drag was pressed.")
    }

    func testAPressAfterADragThatLostItsReleaseStartsAfresh() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        let board = try center(of: "Board background", role: .button, in: panel)
        panel.sendEvent(try event(.leftMouseDown, at: board, in: panel))
        try await waitForHold()
        XCTAssertTrue(panel.isTrackingToolbarDrag)
        // The release went somewhere else. The next press ends that drag and is a click as usual.
        let moved = try center(of: "Board background", role: .button, in: panel)
        panel.sendEvent(try event(.leftMouseDown, at: moved, in: panel))
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        panel.sendEvent(try event(.leftMouseUp, at: moved, in: panel))
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(model.board, .light)
    }

    /// Tester report: Properties turns on, but the toolbar doesn't grow, so only a sliver of the row shows.
    func testASizeAskedForDuringAHoldThatGoesNowhereStillApplies() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        let one = panel.frame.height
        let board = try center(of: "Board background", role: .button, in: panel)
        panel.sendEvent(try event(.leftMouseDown, at: board, in: panel))
        try await waitForHold()
        XCTAssertTrue(panel.isTrackingToolbarDrag)
        model.setPropertiesShown(true)
        model.choose(.pen)
        try await Task.sleep(for: .milliseconds(300))
        XCTAssertEqual(panel.frame.height, one, accuracy: 0.5, "The window keeps its size while it follows the pointer.")
        panel.sendEvent(try event(.leftMouseUp, at: board, in: panel))
        try await Task.sleep(for: .milliseconds(800))
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        let content = try XCTUnwrap(panel.contentView as? ToolbarContentView)
        XCTAssertGreaterThan(panel.frame.height, one + 30, "The properties row got its room once the hold ended.")
        XCTAssertEqual(content.host.frame.height, panel.frame.height, accuracy: 0.5)
    }

    func testHoldWithoutMovingKeepsTheToolbarDocked() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        let middle = panel.frame.midX
        let board = try center(of: "Board background", role: .button, in: panel)
        panel.sendEvent(try event(.leftMouseDown, at: board, in: panel))
        try await waitForHold()
        XCTAssertTrue(panel.isTrackingToolbarDrag)
        panel.sendEvent(try event(.leftMouseUp, at: board, in: panel))
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        XCTAssertEqual(model.board, .transparent)
        // A docked toolbar that grows for its properties stays centered on its edge.
        model.setPropertiesShown(true)
        model.choose(.pen)
        try await Task.sleep(for: .milliseconds(800))
        XCTAssertGreaterThan(panel.frame.width, 0)
        XCTAssertEqual(panel.frame.midX, middle, accuracy: 1, "Still docked after a hold that never moved")
    }

    /// Tester report, with a real mouse: drags that start on a button don't move the toolbar, and every other drag stops
    /// at once. Real pointer input, on Annotator's own toolbar only, while drawing and while another app is in front.
    func testRealDragsFromAnyPlaceMoveTheToolbarEveryTime() async throws {
        guard CGPreflightPostEventAccess() else { throw XCTSkip("Needs permission to post mouse events.") }
        let (model, panel) = try await toolbar()
        let top = NSScreen.screens[0].frame.maxY
        func post(_ type: CGEventType, _ point: CGPoint) {
            CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: CGPoint(x: point.x, y: top - point.y),
                    mouseButton: .left)?.post(tap: .cghidEventTap)
        }
        // The pointer goes back where it was, so it doesn't rest on the next test's toolbar.
        let original = NSEvent.mouseLocation
        defer { post(.mouseMoved, original) }
        func drag(_ name: String, from local: () throws -> CGPoint, holding hold: Int, by dx: Double, _ dy: Double) async throws {
            let start = panel.convertPoint(toScreen: try local())
            guard NSWindow.windowNumber(at: start, belowWindowWithWindowNumber: 0) == panel.windowNumber else {
                throw XCTSkip("Another window covers the toolbar at \(start).")
            }
            let origin = panel.frame.origin
            post(.mouseMoved, start)
            try await Task.sleep(for: .milliseconds(150))
            post(.leftMouseDown, start)
            try await Task.sleep(for: .milliseconds(hold))
            for step in 1...25 {
                post(.leftMouseDragged, CGPoint(x: start.x + dx * Double(step) / 25, y: start.y + dy * Double(step) / 25))
                try await Task.sleep(for: .milliseconds(8))
            }
            try await Task.sleep(for: .milliseconds(80))
            XCTAssertEqual(panel.frame.minX - origin.x, dx, accuracy: 1, name)
            XCTAssertEqual(panel.frame.minY - origin.y, dy, accuracy: 1, name)
            post(.leftMouseUp, CGPoint(x: start.x + dx, y: start.y + dy))
            try await Task.sleep(for: .milliseconds(400))
            XCTAssertFalse(panel.isTrackingToolbarDrag, name)
        }
        let board = { try self.center(of: "Board background", role: .button, in: panel) }
        let edge = { CGPoint(x: panel.frame.width / 2, y: panel.frame.height - 3) }
        for mode in ["Drawing", "Using apps"] {
            if mode == "Using apps" {
                model.interact()
                NSWorkspace.shared.runningApplications.first { $0.bundleIdentifier == "com.apple.finder" }?.activate()
                try await Task.sleep(for: .milliseconds(600))
            }
            for attempt in 0..<4 {
                let direction: Double = attempt % 2 == 0 ? 1 : -1
                try await drag("\(mode): drag \(attempt + 1) from a button", from: board, holding: 30,
                               by: 90 * direction, -70 * direction)
            }
            try await drag("\(mode): hold on a button, then drag", from: board, holding: 600, by: 90, -70)
            try await drag("\(mode): drag from the edge", from: edge, holding: 30, by: -90, 70)
        }
        XCTAssertEqual(model.board, .transparent, "No drag pressed the button.")
    }

    func testSlidersGetTheirPressAtOnce() async throws {
        let (model, panel) = try await toolbar(properties: true)
        model.choose(.pen)
        try await Task.sleep(for: .milliseconds(150))
        panel.leftButtonIsDown = { true }
        let slider = try center(of: "Width", role: .slider, in: panel)
        // A native slider follows the pointer in its own loop until the button goes up.
        NSApp.postEvent(try event(.leftMouseUp, at: slider, in: panel), atStart: false)
        panel.sendEvent(try event(.leftMouseDown, at: slider, in: panel))
        XCTAssertFalse(panel.isHoldingPress, "The slider took its press, and its own loop took the release.")
        if let up = NSApp.nextEvent(matching: .leftMouseUp, until: .distantPast, inMode: .default, dequeue: true) {
            panel.sendEvent(up)
        }
        XCTAssertFalse(panel.isHoldingPress)
        try await waitForHold()
        XCTAssertFalse(panel.isTrackingToolbarDrag)
    }
}
