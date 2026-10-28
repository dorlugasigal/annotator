import AppKit
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class ToolbarWindowFitTests: XCTestCase {
    func testHiddenToolbarReturnsFromDisconnectedDisplayAndKeepsValidPlacement() throws {
        let suite = "toolbar-display-recovery-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setInspectorPinned(false)
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        let panel = try XCTUnwrap(app.toolbarWindow)
        app.hideTools()
        let visible = CGRect(x: 0, y: 40, width: 1000, height: 700)
        let missing = CGRect(x: -1800, y: 250, width: 800, height: 130)
        panel.setFrame(missing, display: false)
        app.recoverToolbarPlacement(visibleFrames: [visible])
        XCTAssertTrue(visible.contains(panel.frame))
        XCTAssertFalse(panel.isVisible)
        XCTAssertTrue(model.workspaceHidden)
        let recovered = panel.frame
        app.recoverToolbarPlacement(visibleFrames: [visible])
        XCTAssertEqual(panel.frame, recovered)
        app.activateTools()
        let connected = NSScreen.screens.map(\.visibleFrame)
        XCTAssertTrue(connected.contains { $0.contains(panel.frame) })
        XCTAssertTrue(panel.isVisible)
        XCTAssertFalse(model.workspaceHidden)
        let shown = panel.frame
        model.displaysChanged()
        XCTAssertEqual(panel.frame, shown)
        XCTAssertNotEqual(model.mode, .draw)
    }

    func testToolbarRecoveryFitsSmallerDisplayAndPartialOffscreenFrame() throws {
        let suite = "toolbar-small-display-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setInspectorPinned(false)
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        let panel = try XCTUnwrap(app.toolbarWindow)
        let visible = CGRect(x: -500, y: 100, width: 500, height: 400)
        for frame in [CGRect(x: -1600, y: 800, width: 1200, height: 600),
                      CGRect(x: -50, y: 450, width: 300, height: 100)] {
            panel.setFrame(frame, display: false)
            app.recoverToolbarPlacement(visibleFrames: [visible])
            XCTAssertTrue(visible.contains(panel.frame))
        }
    }

    func testPhysicalMouseButtonRouteDoesNotStartToolbarDragging() async throws {
        let accessibility = NSAccessibility.Attribute(rawValue: "AXEnhancedUserInterface")
        NSApp.accessibilitySetValue(false, forAttribute: accessibility)
        defer { NativeAccessibilityNode.enable() }
        let suite = "toolbar-click-routing-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setQuickPreferencesExpanded(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(100))
        let panel = try XCTUnwrap(app.toolbarWindow as? InspectorPanel)
        let content = try XCTUnwrap(panel.contentView as? ToolbarContentView)
        panel.animationBehavior = .none
        panel.makeKeyAndOrderFront(nil)
        var backgroundDrags = 0
        let dragStart = panel.onUserDragStarted
        panel.onUserDragStarted = { backgroundDrags += 1; dragStart?() }
        func click(_ label: String) throws {
            NativeAccessibilityNode.enable()
            content.layoutSubtreeIfNeeded()
            let node = try XCTUnwrap(NativeAccessibilityNode.nodes(in: content.host).first {
                $0.accessibilityRole() == .button && $0.accessibilityLabel() == label
            })
            let frame = node.accessibilityFrame()
            let location = panel.convertPoint(fromScreen: CGPoint(x: frame.midX, y: frame.midY))
            NSApp.accessibilitySetValue(false, forAttribute: accessibility)
            content.layoutSubtreeIfNeeded()
            func event(_ type: NSEvent.EventType) throws -> NSEvent {
                try XCTUnwrap(NSEvent.mouseEvent(with: type, location: location, modifierFlags: [],
                    timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                    context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
            }
            // Native button tracking can synchronously wait for its mouse-up.
            let up = try event(.leftMouseUp)
            NSApp.postEvent(up, atStart: false)
            panel.sendEvent(try event(.leftMouseDown))
            if let pending = NSApp.nextEvent(matching: .leftMouseUp, until: .distantPast,
                                             inMode: .default, dequeue: true) {
                panel.sendEvent(pending)
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.15))
            XCTAssertFalse(panel.isTrackingToolbarDrag)
        }
        try click("Board background")
        XCTAssertEqual(model.board, .light)
        try click("Use apps")
        XCTAssertEqual(model.mode, .interact)
        try click("Board background")
        XCTAssertEqual(model.board, .dark)
        try click("Resume drawing")
        XCTAssertEqual(model.mode, .draw)
        try click("Properties")
        XCTAssertNotEqual(ToolbarPropertiesMode(model: model), .toolsOnly)
        XCTAssertEqual(backgroundDrags, 0, "Button presses must not invoke background dragging.")
    }

    func testBlankToolbarPaddingDragsWithoutStealingControlClicks() async throws {
        let suite = "toolbar-background-drag-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setQuickPreferencesExpanded(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(100))
        let panel = try XCTUnwrap(app.toolbarWindow as? InspectorPanel)
        var changes = 0
        let moved = panel.onUserDragMoved
        panel.onUserDragMoved = { point in changes += 1; moved?(point) }
        func event(_ type: NSEvent.EventType, _ point: CGPoint) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: point, modifierFlags: [],
                timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        }
        for _ in 0..<2 {
            let before = changes
            let start = CGPoint(x: panel.frame.width / 2, y: 5)
            for offset in [5.0, 15.0, 25.0] {
                NSApp.postEvent(try event(.leftMouseDragged, CGPoint(x: start.x + offset, y: start.y)), atStart: false)
            }
            NSApp.postEvent(try event(.leftMouseUp, CGPoint(x: start.x + 25, y: start.y)), atStart: false)
            panel.sendEvent(try event(.leftMouseDown, start))
            while let pending = NSApp.nextEvent(matching: [.leftMouseDragged, .leftMouseUp], until: .distantPast,
                                                inMode: .default, dequeue: true) {
                panel.sendEvent(pending)
            }
            try await Task.sleep(for: .milliseconds(30))
            XCTAssertGreaterThan(changes, before)
            XCTAssertFalse(panel.isTrackingToolbarDrag)
        }
        NativeAccessibilityNode.enable()
        let content = try XCTUnwrap(panel.contentView)
        panel.makeKeyAndOrderFront(nil)
        content.layoutSubtreeIfNeeded()
        let board = try XCTUnwrap(NativeAccessibilityNode.nodes(in: content).first {
            $0.accessibilityLabel() == "Board background" && $0.accessibilityRole() == .button
        })
        let frame = board.accessibilityFrame()
        let point = panel.convertPoint(fromScreen: CGPoint(x: frame.midX, y: frame.midY))
        NSApp.postEvent(try event(.leftMouseUp, point), atStart: false)
        panel.sendEvent(try event(.leftMouseDown, point))
        if let up = NSApp.nextEvent(matching: .leftMouseUp, until: .distantPast, inMode: .default, dequeue: true) {
            panel.sendEvent(up)
        }
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(model.board, .light)
        XCTAssertFalse(panel.isTrackingToolbarDrag)
    }

    func testEmptyFullInspectorSpaceDragsToolbarInBothOrientations() async throws {
        let suite = "inspector-background-drag-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setInspectorPinned(true)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        model.choose(.arrow)
        let panel = try XCTUnwrap(app.toolbarWindow as? InspectorPanel)
        func scrollView(in view: NSView) -> NSScrollView? {
            if let scroll = view as? NSScrollView,
               scroll.identifier?.rawValue == "contextual-inspector-viewport" { return scroll }
            return view.subviews.lazy.compactMap { scrollView(in: $0) }.first
        }
        var moves = 0
        let callback = panel.onUserDragMoved
        panel.onUserDragMoved = { point in moves += 1; callback?(point) }
        NativeAccessibilityNode.enable()
        for orientation in [ToolbarOrientation.horizontal, .vertical] {
            model.setToolbarOrientation(orientation)
            try await Task.sleep(for: .milliseconds(300))
            panel.contentView?.layoutSubtreeIfNeeded()
            let content = try XCTUnwrap(panel.contentView)
            // Horizontal properties are one row: its top-right corner, above the menus, is empty. Vertical properties
            // scroll in a column without a scroll bar.
            let scroll = scrollView(in: content)
            let start: CGPoint
            if orientation == .horizontal {
                XCTAssertNil(scroll, "One row of properties needs no scroll view")
                let row = try XCTUnwrap(NativeAccessibilityNode.nodes(in: content).first {
                    $0.accessibilityIdentifier() == "contextual-inspector-viewport"
                }).accessibilityFrame()
                start = panel.convertPoint(fromScreen: CGPoint(x: row.maxX - 2, y: row.maxY - 4))
            } else {
                let document = try XCTUnwrap(try XCTUnwrap(scroll).documentView)
                start = document.convert(CGPoint(x: document.bounds.maxX - 2, y: 8), to: nil)
            }
            func event(_ type: NSEvent.EventType, _ offset: CGFloat) throws -> NSEvent {
                try XCTUnwrap(NSEvent.mouseEvent(with: type,
                    location: CGPoint(x: start.x + offset, y: start.y), modifierFlags: [],
                    timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                    context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
            }
            let before = moves
            let scrollOffset = scroll?.contentView.bounds.origin
            for offset in [5.0, 10.0, 15.0] {
                NSApp.postEvent(try event(.leftMouseDragged, offset), atStart: false)
            }
            NSApp.postEvent(try event(.leftMouseUp, 15), atStart: false)
            panel.sendEvent(try event(.leftMouseDown, 0))
            while let pending = NSApp.nextEvent(matching: [.leftMouseDragged, .leftMouseUp],
                                                until: .distantPast, inMode: .default, dequeue: true) {
                panel.sendEvent(pending)
            }
            try await Task.sleep(for: .milliseconds(30))
            XCTAssertGreaterThan(moves, before, "\(orientation)")
            XCTAssertFalse(panel.isTrackingToolbarDrag)
            XCTAssertEqual(scroll?.contentView.bounds.origin, scrollOffset)
        }
    }

    func testWorkspaceActionsRemainAvailableWithoutDedicatedSidePanel() async throws {
        NativeAccessibilityNode.enable()
        let suite = "workspace-control-panel-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setQuickPreferencesExpanded(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(100))
        let panel = try XCTUnwrap(app.toolbarWindow)
        let content = try XCTUnwrap(panel.contentView as? ToolbarContentView)
        let nodes = NativeAccessibilityNode.nodes(in: content.host)
        XCTAssertFalse(nodes.contains { $0.accessibilityIdentifier() == "workspace-control-panel" })
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .toolsOnly)
        for label in ["Board background", "Use apps", "Undo", "Redo", "Clear all drawings — undoable",
                      "Properties", "Settings and shortcuts", "Hide drawings and tools"] {
            let matches = nodes.filter { $0.accessibilityRole() == .button && $0.accessibilityLabel() == label }
            XCTAssertEqual(matches.count, 1, label)
            let control = try XCTUnwrap(matches.first)
            XCTAssertTrue(panel.frame.insetBy(dx: -1, dy: -1).contains(control.accessibilityFrame()), label)
        }
    }

    func testEdgeDropsCenterAndReorientOnTheDestinationDisplay() async throws {
        NativeAccessibilityNode.enable()
        let suite = "toolbar-edge-drops-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setQuickPreferencesExpanded(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        XCTAssertFalse(try XCTUnwrap(app.toolbarWindow).isMovable)
        XCTAssertFalse(try XCTUnwrap(app.toolbarWindow).isMovableByWindowBackground)
        // Docking uses scripted points, not live desktop clicks.
        for session in model.desktopSessions { session.panel.ignoresMouseEvents = true }
        let session = try XCTUnwrap(model.active)
        let shape = Element(tool: .rectangle, frame: Rect(x: 40, y: 60, width: 100, height: 80))
        try session.document.add(shape)
        model.synchronize()
        for screen in NSScreen.screens {
            let bounds = screen.frame
            let safe = screen.visibleFrame
            let cases: [(ToolbarDockEdge, CGPoint)] = [
                (.top, CGPoint(x: bounds.midX, y: bounds.maxY - 1)),
                (.left, CGPoint(x: bounds.minX + 1, y: bounds.midY)),
                (.bottom, CGPoint(x: bounds.midX, y: bounds.minY + 1)),
                (.right, CGPoint(x: bounds.maxX - 1, y: bounds.midY))
            ]
            for (edge, point) in cases {
                let oldOrientation = model.preferences.toolbarOrientation
                app.updateDockPreview(at: point)
                let preview = try XCTUnwrap(app.dockPreviewFrame)
                XCTAssertEqual(model.preferences.toolbarOrientation, oldOrientation)
                app.updateDockPreview(at: CGPoint(x: bounds.midX, y: bounds.midY))
                XCTAssertNil(app.dockPreviewFrame)
                app.updateDockPreview(at: point)
                app.finishToolbarDrag(at: point)
                try await Task.sleep(for: .milliseconds(150))
                let frame = try XCTUnwrap(app.toolbarFrame)
                XCTAssertNil(app.dockPreviewFrame)
                XCTAssertEqual(frame, preview)
                XCTAssertTrue(safe.insetBy(dx: -1, dy: -1).contains(frame), "\(edge): \(frame) outside \(safe)")
                XCTAssertEqual(model.preferences.toolbarDockEdge, edge)
                if edge == .left || edge == .right {
                    XCTAssertEqual(model.preferences.toolbarOrientation, .vertical)
                    XCTAssertEqual(frame.midY, safe.midY, accuracy: 1)
                    XCTAssertEqual(edge == .left ? frame.minX : frame.maxX, edge == .left ? safe.minX : safe.maxX, accuracy: 1)
                } else {
                    XCTAssertEqual(model.preferences.toolbarOrientation, .horizontal)
                    XCTAssertEqual(frame.midX, safe.midX, accuracy: 1)
                    XCTAssertEqual(edge == .top ? frame.maxY : frame.minY, edge == .top ? safe.maxY : safe.minY, accuracy: 1)
                }
                XCTAssertEqual(session.document.elements, [shape])
                XCTAssertTrue(model.active === session)
                XCTAssertEqual(model.mode, .draw)
            }
        }
        let window = try XCTUnwrap(app.toolbarWindow)
        let screen = try XCTUnwrap(window.screen)
        let drop = CGPoint(x: screen.frame.minX + screen.frame.width * 0.75, y: screen.frame.maxY - 1)
        app.updateDockPreview(at: drop)
        let preview = try XCTUnwrap(app.dockPreviewFrame)
        app.finishToolbarDrag(at: drop)
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertEqual(window.frame, preview)
        XCTAssertEqual(window.frame.midX, screen.visibleFrame.midX, accuracy: 1)
        model.setInspectorPinned(true)
        for edge in [ToolbarDockEdge.right, .left] {
            let point = CGPoint(x: edge == .right ? screen.frame.maxX - 1 : screen.frame.minX + 1, y: screen.frame.midY)
            app.updateDockPreview(at: point)
            let target = try XCTUnwrap(app.dockPreviewFrame)
            app.finishToolbarDrag(at: point)
            try await Task.sleep(for: .milliseconds(200))
            XCTAssertEqual(window.frame, target)
            let content = try XCTUnwrap(window.contentView)
            XCTAssertEqual(content.frame.size.width, window.frame.width, accuracy: 1)
            XCTAssertEqual(content.frame.size.height, window.frame.height, accuracy: 1)
            let container = try XCTUnwrap(content as? ToolbarContentView)
            XCTAssertEqual(container.host.frame, container.bounds)
            XCTAssertTrue(container.host.sizingOptions.isEmpty)
            let nodes = NativeAccessibilityNode.nodes(in: container.host)
            for name in ["Tool library", "Board background", "Use apps", "Hide drawings and tools"] {
                let node = try XCTUnwrap(nodes.first { $0.accessibilityLabel() == name }, name)
                XCTAssertTrue(window.frame.insetBy(dx: -1, dy: -1).contains(node.accessibilityFrame()), name)
            }
        }
        window.setFrameOrigin(CGPoint(x: screen.visibleFrame.midX - window.frame.width / 2,
                                      y: screen.visibleFrame.midY - window.frame.height / 2))
        let free = window.frame
        app.finishToolbarDrag(at: CGPoint(x: screen.visibleFrame.midX, y: screen.visibleFrame.midY))
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(window.frame, free)
        let nearTop = CGPoint(x: screen.frame.midX + 50, y: screen.frame.maxY - 3)
        app.updateDockPreview(at: nearTop)
        XCTAssertNil(app.dockPreviewFrame)
        app.finishToolbarDrag(at: nearTop)
        XCTAssertEqual(window.frame, free)
    }

    func testDragTrackingStopsOnReleaseAndHide() async throws {
        let panel = InspectorPanel(contentRect: CGRect(x: 100, y: 100, width: 100, height: 50),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.ignoresMouseEvents = true
        panel.orderFrontRegardless()
        defer { panel.close() }
        var completed = 0
        var dropPoint: CGPoint?
        panel.onUserDragEnded = { point in completed += 1; dropPoint = point }
        panel.beginToolbarDrag(at: CGPoint(x: 110, y: 115))
        panel.updateToolbarDrag(to: CGPoint(x: 150, y: 155))
        XCTAssertTrue(panel.isTrackingToolbarDrag)
        XCTAssertEqual(completed, 0)
        panel.endToolbarDrag(at: CGPoint(x: 150, y: 155))
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        XCTAssertEqual(completed, 1)
        XCTAssertEqual(dropPoint, CGPoint(x: 150, y: 155))
        panel.beginToolbarDrag(at: CGPoint(x: 150, y: 155))
        panel.orderOut(nil)
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        try await Task.sleep(for: .milliseconds(70))
        XCTAssertEqual(completed, 1)
    }

    func testAppOwnedDragEventsCommitExactlyTheGhostWithoutServerDragging() async throws {
        let suite = "owned-toolbar-drag-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setInspectorPinned(false)
        model.setVisibilityAnimation(false)
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(100))
        let panel = try XCTUnwrap(app.toolbarWindow as? InspectorPanel)
        let screen = try XCTUnwrap(panel.screen)
        func event(_ type: NSEvent.EventType, at screenPoint: CGPoint) throws -> NSEvent {
            try XCTUnwrap(NSEvent.mouseEvent(with: type, location: panel.convertPoint(fromScreen: screenPoint),
                modifierFlags: [], timestamp: 0, windowNumber: panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        }
        let start = CGPoint(x: panel.frame.minX + 10, y: panel.frame.midY)
        let edge = CGPoint(x: screen.frame.minX, y: screen.frame.midY)
        panel.performDrag(with: try event(.leftMouseDown, at: start))
        panel.sendEvent(try event(.leftMouseDragged, at: edge))
        let preview = try XCTUnwrap(app.dockPreviewFrame)
        panel.sendEvent(try event(.leftMouseUp, at: edge))
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertFalse(panel.isTrackingToolbarDrag)
        XCTAssertNil(app.dockPreviewFrame)
        XCTAssertEqual(panel.frame, preview)
        XCTAssertEqual(panel.frame.minX, screen.visibleFrame.minX, accuracy: 1)
        let nextStart = CGPoint(x: panel.frame.midX, y: panel.frame.midY)
        let free = CGPoint(x: nextStart.x + 80, y: nextStart.y - 30)
        let original = panel.frame
        panel.performDrag(with: try event(.leftMouseDown, at: nextStart))
        panel.sendEvent(try event(.leftMouseDragged, at: free))
        XCTAssertNil(app.dockPreviewFrame)
        panel.sendEvent(try event(.leftMouseUp, at: free))
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertEqual(panel.frame.origin, CGPoint(x: original.minX + 80, y: original.minY - 30))
    }

    func testDockDetectionUsesNearestEdgeAndLeavesInteriorFree() {
        let bounds = CGRect(x: -1400, y: -900, width: 1400, height: 900)
        XCTAssertEqual(ToolbarPlacement.dockEdge(at: CGPoint(x: -1399, y: -450), visibleFrame: bounds), .left)
        XCTAssertEqual(ToolbarPlacement.dockEdge(at: CGPoint(x: -1, y: -450), visibleFrame: bounds), .right)
        XCTAssertEqual(ToolbarPlacement.dockEdge(at: CGPoint(x: -700, y: -1), visibleFrame: bounds), .top)
        XCTAssertEqual(ToolbarPlacement.dockEdge(at: CGPoint(x: -700, y: -899), visibleFrame: bounds), .bottom)
        XCTAssertNil(ToolbarPlacement.dockEdge(at: CGPoint(x: -700, y: -10), visibleFrame: bounds))
        XCTAssertNil(ToolbarPlacement.dockEdge(at: CGPoint(x: -700, y: -2), visibleFrame: bounds))
        XCTAssertNil(ToolbarPlacement.dockEdge(at: CGPoint(x: -700, y: -450), visibleFrame: bounds))
        XCTAssertNil(ToolbarPlacement.dockEdge(at: CGPoint(x: 500, y: 500), visibleFrame: bounds))
    }

    func testDraggingCannotCoverMenuBarButPointerStillTriggersDockPreview() throws {
        let screen = try XCTUnwrap(NSScreen.main)
        let safe = screen.visibleFrame
        let panel = InspectorPanel(contentRect: CGRect(x: safe.midX - 100, y: safe.midY - 40, width: 200, height: 80),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        defer { panel.close() }
        var hover: CGPoint?
        panel.onUserDragEnded = { _ in }
        panel.onUserDragMoved = { hover = $0 }
        let start = CGPoint(x: panel.frame.midX, y: panel.frame.midY)
        let edge = CGPoint(x: screen.frame.midX, y: screen.frame.maxY - 0.5)
        panel.beginToolbarDrag(at: start)
        panel.updateToolbarDrag(to: edge)
        XCTAssertLessThanOrEqual(panel.frame.maxY, safe.maxY)
        XCTAssertEqual(hover, edge, "Clamp the toolbar, not the pointer used for snap detection.")
        XCTAssertEqual(ToolbarPlacement.dockEdge(at: try XCTUnwrap(hover), visibleFrame: screen.frame), .top)
        panel.updateToolbarDrag(to: start)
        XCTAssertEqual(panel.frame.midX, start.x, accuracy: 1)
        XCTAssertEqual(panel.frame.midY, start.y, accuracy: 1)
        panel.endToolbarDrag(at: start)
        let negativeScreen = CGRect(x: -1920, y: -600, width: 1920, height: 1050)
        let constrained = ToolbarPlacement.constrainedDragFrame(
            CGRect(x: -2000, y: 440, width: 400, height: 100), visibleFrame: negativeScreen)
        XCTAssertTrue(negativeScreen.contains(constrained))
    }

    func testStartupAndOrientationChangesStayDockedAndOnScreen() async throws {
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main)
        let safe = screen.visibleFrame.insetBy(dx: 12, dy: 12)
        let suite = "toolbar-window-fit-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.setVisibilityAnimation(false)
        model.setInspectorPinned(true)
        model.setToolbarOrientation(.vertical)
        model.setToolbarDockEdge(.left)
        let app = AppDelegate(model: model)
        let resize = model.onToolbarSizeChanged
        var measuredSizes: [CGSize] = []
        model.onToolbarSizeChanged = { size in
            measuredSizes.append(size)
            resize?(size)
        }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        app.activateTools()
        model.interact()
        try await Task.sleep(for: .milliseconds(250))
        let vertical = try XCTUnwrap(app.toolbarFrame)
        XCTAssertTrue(measuredSizes.contains(vertical.size), "The real content size must reach the window controller.")
        XCTAssertTrue(safe.insetBy(dx: -1, dy: -1).contains(vertical), "\(vertical) escaped \(safe)")
        XCTAssertEqual(vertical.midY, safe.midY, accuracy: 1)
        XCTAssertEqual(vertical.minX, safe.minX, accuracy: 1)

        model.setToolbarOrientation(.horizontal)
        try await Task.sleep(for: .milliseconds(250))
        let horizontal = try XCTUnwrap(app.toolbarFrame)
        XCTAssertTrue(safe.insetBy(dx: -1, dy: -1).contains(horizontal))
        XCTAssertEqual(horizontal.midX, safe.midX, accuracy: 1)

        model.setInspectorPinned(false)
        model.setQuickPreferencesExpanded(false)
        try await Task.sleep(for: .milliseconds(150))
        let collapsed = try XCTUnwrap(app.toolbarFrame)
        XCTAssertEqual(collapsed.midX, safe.midX, accuracy: 1)
        model.setInspectorPinned(true)
        try await Task.sleep(for: .milliseconds(150))
        let expanded = try XCTUnwrap(app.toolbarFrame)
        XCTAssertTrue(safe.insetBy(dx: -1, dy: -1).contains(expanded))
        XCTAssertEqual(expanded.midX, safe.midX, accuracy: 1)

        model.setQuickPreferencesExpanded(false)
        try await Task.sleep(for: .milliseconds(100))
        let originalWidth = try XCTUnwrap(app.toolbarFrame).width
        model.configureToolbar([.select, .pen])
        // The toolbar grows and shrinks sideways in a short animation.
        try await Task.sleep(for: .milliseconds(150))
        for _ in 0..<20 where app.toolbarIsAnimating { try await Task.sleep(for: .milliseconds(25)) }
        let fewerTools = try XCTUnwrap(app.toolbarFrame)
        XCTAssertLessThan(fewerTools.width, originalWidth)
        XCTAssertEqual(fewerTools.midX, safe.midX, accuracy: 1)
        model.configureToolbar(WorkspacePreferences.defaultToolbar)
        try await Task.sleep(for: .milliseconds(150))
        for _ in 0..<20 where app.toolbarIsAnimating { try await Task.sleep(for: .milliseconds(25)) }
        XCTAssertEqual(try XCTUnwrap(app.toolbarFrame).width, originalWidth, accuracy: 1)
    }
}
