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

    func testMovingBeforeTheHoldEndsGivesThePressBack() async throws {
        let (model, panel) = try await toolbar()
        panel.leftButtonIsDown = { true }
        let board = try center(of: "Board background", role: .button, in: panel)
        var drags = 0
        let started = panel.onUserDragStarted
        panel.onUserDragStarted = { drags += 1; started?() }
        panel.sendEvent(try event(.leftMouseDown, at: board, in: panel))
        panel.sendEvent(try event(.leftMouseDragged, at: CGPoint(x: board.x + 1, y: board.y), in: panel))
        XCTAssertTrue(panel.isHoldingPress, "A small wobble is still a hold.")
        panel.sendEvent(try event(.leftMouseDragged, at: CGPoint(x: board.x + 1, y: board.y + 6), in: panel))
        XCTAssertFalse(panel.isHoldingPress)
        panel.sendEvent(try event(.leftMouseUp, at: CGPoint(x: board.x + 1, y: board.y + 6), in: panel))
        try await waitForHold()
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        XCTAssertEqual(drags, 0, "A press that moves before the hold ends belongs to the button.")
        XCTAssertEqual(model.board, .light, "Released over the button, the press is a click.")
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
        if let up = NSApp.nextEvent(matching: .leftMouseUp, until: .distantPast, inMode: .default, dequeue: true) {
            panel.sendEvent(up)
        }
        XCTAssertFalse(panel.isHoldingPress)
        try await waitForHold()
        XCTAssertFalse(panel.isTrackingToolbarDrag)
    }
}
