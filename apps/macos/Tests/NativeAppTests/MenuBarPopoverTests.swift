import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class MenuBarPopoverTests: XCTestCase {
    private func model() throws -> (WorkspaceModel, UserDefaults, String) {
        let suite = "menu-popover-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        return (WorkspaceModel(defaults: defaults), defaults, suite)
    }

    private func anchor() -> (NSPanel, NSButton) {
        let panel = NSPanel(contentRect: CGRect(x: 150, y: 450, width: 100, height: 50),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        let button = NSButton(frame: CGRect(x: 10, y: 10, width: 24, height: 24))
        panel.contentView?.addSubview(button)
        panel.orderFrontRegardless()
        return (panel, button)
    }

    func testBoardActionsAreGroupedAndSafetyActionsAreContextual() async throws {
        let (model, defaults, suite) = try model()
        let host = NSHostingView(rootView: MenuBarPopoverView(model: model, developerPreview: true, perform: { _ in },
            toolsVisible: { model.active != nil && !model.workspaceHidden }))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 320, height: 600),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer {
            panel.orderOut(nil); panel.contentView = nil; panel.close(); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(120))
        func ids() -> Set<String> {
            Set(NativeAccessibilityNode.nodes(in: host).compactMap { $0.accessibilityIdentifier() })
        }
        XCTAssertTrue(ids().isSuperset(of: ["menu-show-tools", "menu-board", "menu-settings", "menu-help",
                                          "menu-about", "menu-checkForUpdates", "menu-support", "menu-quit",
                                          "menu-toggleRegionSharing", "menu-shareRegion",
                                          "menu-shareLeftHalf", "menu-shareRightHalf"]))
        let region = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityIdentifier() == "menu-toggleRegionSharing"
        })
        XCTAssertEqual(region.accessibilityLabel(), "Start region sharing")
        XCTAssertEqual(region.accessibilityValue() as? String, "Off")
        for (id, title) in [("menu-shareLeftHalf", "Share left half"), ("menu-shareRightHalf", "Share right half")] {
            let quick = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == id })
            XCTAssertEqual(quick.accessibilityLabel(), title)
        }
        let primary = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "menu-show-tools" })
        XCTAssertEqual(primary.accessibilityLabel(), "Show drawings and tools")
        XCTAssertEqual(primary.accessibilityValue() as? String, model.preferences.activationShortcut.label)
        model.setActivationShortcut(ActivationShortcut(keyCode: 20, character: "3", control: true, option: true))
        try await Task.sleep(for: .milliseconds(80))
        let updated = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "menu-show-tools" })
        XCTAssertEqual(updated.accessibilityValue() as? String, model.preferences.activationShortcut.label)
        XCTAssertFalse(ids().contains("menu-useApps"))
        XCTAssertFalse(ids().contains("menu-stopZoom"))
        XCTAssertFalse(ids().contains("menu-openBoard"))
        let board = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "menu-board" })
        XCTAssertTrue(board.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertTrue(ids().isSuperset(of: ["menu-openBoard", "menu-saveBoard", "menu-exportPNG"]))
        model.beginDrawing()
        model.active?.panel.ignoresMouseEvents = true
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertTrue(ids().contains("menu-useApps"))
        XCTAssertTrue(ids().contains("menu-hide-tools"))
        XCTAssertFalse(ids().contains("menu-show-tools"))
        model.interact()
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertFalse(ids().contains("menu-useApps"))
        XCTAssertTrue(ids().contains("menu-draw"))
    }

    func testMenuVisibilityAndModeActionsAreIndependent() throws {
        let (model, defaults, suite) = try model()
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        app.finishLaunchPresentation(isLoginItem: false)
        XCTAssertTrue(app.drawingToolsArePresented)
        XCTAssertEqual(model.mode, .off)
        app.performMenuAction(.draw)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertTrue(app.drawingToolsArePresented)
        app.performMenuAction(.useApps)
        XCTAssertEqual(model.mode, .interact)
        XCTAssertTrue(app.drawingToolsArePresented)
        app.performMenuAction(.hideTools)
        XCTAssertTrue(model.workspaceHidden)
        XCTAssertFalse(app.drawingToolsArePresented)
        app.performMenuAction(.showTools)
        XCTAssertEqual(model.mode, .draw)
        XCTAssertFalse(model.workspaceHidden)
        XCTAssertTrue(app.drawingToolsArePresented)
    }

    func testUseAppsDuringRevealCancelsPendingDrawingInput() async throws {
        let (model, defaults, suite) = try model()
        let app = AppDelegate(model: model)
        defer {
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            defaults.removePersistentDomain(forName: suite)
        }
        model.setVisibilityAnimation(false)
        app.performMenuAction(.showTools)
        app.hideTools()
        model.setAnimationStyle(.fade)
        app.performMenuAction(.showTools)
        XCTAssertTrue(model.visibilityAnimating)
        XCTAssertEqual(model.mode, .draw)
        app.performMenuAction(.useApps)
        try await Task.sleep(for: .milliseconds(350))
        XCTAssertEqual(model.mode, .interact)
        XCTAssertTrue(try XCTUnwrap(model.active).panel.ignoresMouseEvents)
        XCTAssertTrue(app.drawingToolsArePresented)
    }

    func testActionsClosePopoverBeforeTheyRun() throws {
        let (model, defaults, suite) = try model()
        let (panel, button) = anchor()
        weak var observed: MenuBarPopoverController?
        var received: [MenuBarAction] = []
        let controller = MenuBarPopoverController(model: model, monitorsEnabled: false) {
            XCTAssertFalse(observed?.popover.isShown ?? true)
            received.append($0)
        }
        observed = controller
        defer {
            controller.close(); panel.orderOut(nil); panel.close(); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        controller.toggle(relativeTo: button)
        XCTAssertTrue(controller.popover.isShown)
        XCTAssertFalse(controller.hasEventMonitors)
        controller.perform(.settings)
        XCTAssertEqual(received, [.settings])
        XCTAssertFalse(controller.popover.isShown)
        controller.perform(.settings)
        XCTAssertEqual(received.count, 1)
    }

    /// Tester report: click 5 min, reopen the menu and click 10 min straight away, and nothing happens. While NSPopover
    /// animated in, its window let clicks through, and it resized once after it appeared, moving the buttons.
    func testMenuTakesAClickAsSoonAsItOpensAndButtonsStayPut() async throws {
        let (model, defaults, suite) = try model()
        var received: [MenuBarAction] = []
        let controller = MenuBarPopoverController(model: model, monitorsEnabled: false) { received.append($0) }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.title = "A"
        defer {
            controller.close(); NSStatusBar.system.removeStatusItem(item); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        let button = try XCTUnwrap(item.button)
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(200))
        func frame(_ identifier: String) throws -> CGRect {
            let content = try XCTUnwrap(controller.popover.contentViewController?.view)
            return try XCTUnwrap(NativeAccessibilityNode.nodes(in: content).first { $0.accessibilityIdentifier() == identifier },
                                 identifier).accessibilityFrame()
        }
        func click(_ identifier: String) throws {
            let window = try XCTUnwrap(controller.popover.contentViewController?.view.window)
            let target = try frame(identifier)
            let point = CGPoint(x: target.midX, y: target.midY)
            XCTAssertEqual(NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0), window.windowNumber,
                           "\(identifier) is not under the pointer yet")
            for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
                NSApp.sendEvent(try XCTUnwrap(NSEvent.mouseEvent(with: type, location: window.convertPoint(fromScreen: point),
                    modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                    context: nil, eventNumber: 0, clickCount: 1, pressure: 1)))
            }
        }
        for (identifier, expected) in [("menu-breakFive", MenuBarAction.breakFive), ("menu-breakTen", .breakTen),
                                       ("menu-show-tools", .showTools)] {
            controller.toggle(relativeTo: button)
            // Well inside the 100–150 ms the opening animation let clicks through.
            try await Task.sleep(for: .milliseconds(50))
            let opened = controller.popover.contentSize
            let window = try XCTUnwrap(controller.popover.contentViewController?.view.window)
            window.contentView?.layoutSubtreeIfNeeded()
            let tools = try frame("menu-show-tools")
            let five = try frame("menu-breakFive")
            try click(identifier)
            XCTAssertEqual(received.last, expected)
            XCTAssertFalse(controller.popover.isShown)

            // Open again and leave it: it doesn't resize or move once it's on screen.
            controller.toggle(relativeTo: button)
            try await Task.sleep(for: .milliseconds(250))
            XCTAssertEqual(controller.popover.contentSize, opened)
            XCTAssertEqual(try frame("menu-show-tools"), tools)
            XCTAssertEqual(try frame("menu-breakFive"), five)
            controller.close()
        }
        XCTAssertEqual(received, [.breakFive, .breakTen, .showTools])

        // Under the menu bar the menu grows and shrinks at the bottom, so the rows above a change stay put.
        controller.toggle(relativeTo: button)
        try await Task.sleep(for: .milliseconds(250))
        let tools = try frame("menu-show-tools")
        let five = try frame("menu-breakFive")
        let before = controller.popover.contentSize.height
        model.updateReminderVersion = "9.9.9"
        try await Task.sleep(for: .milliseconds(250))
        XCTAssertNotEqual(controller.popover.contentSize.height, before)
        XCTAssertEqual(try frame("menu-show-tools"), tools)
        XCTAssertEqual(try frame("menu-breakFive"), five)
    }

    func testPopoverExpandsForBoardsAndEscapeDismissesIt() async throws {
        let (model, defaults, suite) = try model()
        let (panel, button) = anchor()
        let controller = MenuBarPopoverController(model: model, monitorsEnabled: false, action: { _ in })
        defer {
            controller.close(); panel.orderOut(nil); panel.close(); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        controller.toggle(relativeTo: button)
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(150))
        let before = controller.popover.contentSize.height
        XCTAssertGreaterThan(before, 250)
        let content = try XCTUnwrap(controller.popover.contentViewController?.view)
        let board = try XCTUnwrap(NativeAccessibilityNode.nodes(in: content).first { $0.accessibilityIdentifier() == "menu-board" })
        XCTAssertTrue(board.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertGreaterThan(controller.popover.contentSize.height, before + 80,
                             "Measured \(controller.measuredContentSize), actual \(controller.popover.contentSize)")
        let escape = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
            timestamp: 0, windowNumber: content.window?.windowNumber ?? 0, context: nil,
            characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53))
        XCTAssertNil(controller.handleLocalEvent(escape))
        XCTAssertFalse(controller.popover.isShown)
        XCTAssertFalse(controller.hasEventMonitors)
    }

    func testOutsideDrawingClickIsConsumedAndEscapeDoesNotStopZoom() throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        let (panel, button) = anchor()
        let controller = MenuBarPopoverController(model: model, monitorsEnabled: false, action: { _ in })
        model.onDismissMenuBar = { [weak controller] in controller?.dismissIfShown() ?? false }
        defer {
            model.onDismissMenuBar = nil
            controller.close(); panel.orderOut(nil); panel.close(); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        controller.toggle(relativeTo: button)
        let click = try XCTUnwrap(NSEvent.mouseEvent(with: .leftMouseDown, location: CGPoint(x: 100, y: 100),
            modifierFlags: [], timestamp: 0, windowNumber: session.panel.windowNumber,
            context: nil, eventNumber: 0, clickCount: 1, pressure: 1))
        XCTAssertNil(controller.handleLocalEvent(click))
        XCTAssertFalse(controller.popover.isShown)
        XCTAssertTrue(session.document.elements.isEmpty)
        XCTAssertEqual(model.mode, .draw)
        controller.toggle(relativeTo: button)
        try session.document.setPresentation(PresentationSettings(mode: .screen, zoom: 2))
        model.handleZoomEscape()
        XCTAssertFalse(controller.popover.isShown)
        XCTAssertEqual(session.document.presentation.mode, .screen)
        XCTAssertFalse(controller.hasEventMonitors)
    }

    func testArrowNavigationAndReturnOpenBoardDisclosure() async throws {
        NativeAccessibilityNode.enable()
        let (model, defaults, suite) = try model()
        let (panel, button) = anchor()
        let controller = MenuBarPopoverController(model: model, monitorsEnabled: false, action: { _ in })
        defer {
            controller.close(); panel.orderOut(nil); panel.close(); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        controller.toggle(relativeTo: button)
        try await Task.sleep(for: .milliseconds(150))
        let window = try XCTUnwrap(controller.popover.contentViewController?.view.window)
        let initialHeight = controller.popover.contentSize.height
        let down = String(try XCTUnwrap(UnicodeScalar(NSDownArrowFunctionKey)))
        for (code, text) in [(UInt16(125), down), (UInt16(125), down), (UInt16(125), down),
                             (UInt16(125), down), (UInt16(125), down),
                             (UInt16(36), "\r")] {
            let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                timestamp: 0, windowNumber: window.windowNumber, context: nil, characters: text,
                charactersIgnoringModifiers: text, isARepeat: false, keyCode: code))
            window.sendEvent(event)
            try await Task.sleep(for: .milliseconds(80))
        }
        let board = try XCTUnwrap(NativeAccessibilityNode.nodes(in: controller.popover.contentViewController!.view).first {
            $0.accessibilityIdentifier() == "menu-board"
        })
        XCTAssertEqual(board.accessibilityValue() as? String, "Expanded")
        XCTAssertGreaterThanOrEqual(controller.popover.contentSize.height, initialHeight)
    }

    func testCommandShortcutsWorkWhileBoardIsCollapsed() throws {
        let (model, defaults, suite) = try model()
        model.beginDrawing()
        model.interact()
        let (panel, button) = anchor()
        var actions: [MenuBarAction] = []
        let controller = MenuBarPopoverController(model: model, monitorsEnabled: false) { actions.append($0) }
        defer {
            controller.close(); panel.orderOut(nil); panel.close(); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        for (character, key, expected) in [("s", UInt16(1), MenuBarAction.saveBoard),
                                            ("o", UInt16(31), .openBoard), (",", UInt16(43), .settings)] {
            controller.toggle(relativeTo: button)
            let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command,
                timestamp: 0, windowNumber: controller.popover.contentViewController?.view.window?.windowNumber ?? 0,
                context: nil, characters: character, charactersIgnoringModifiers: character, isARepeat: false, keyCode: key))
            XCTAssertNil(controller.handleLocalEvent(event))
            XCTAssertEqual(actions.last, expected)
        }
    }

    func testRenderPopoverInBothAppearances() async throws {
        let (model, defaults, suite) = try model()
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let root = RepositoryRoot.url
        let directory = root.appendingPathComponent("build/menu-popover-review")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for (name, dark, visible) in [("light", false, false), ("dark", true, false), ("visible", false, true)] {
            let host = NSHostingView(rootView: MenuBarPopoverView(model: model, developerPreview: true, perform: { _ in },
                toolsVisible: { visible })
                .environment(\.colorScheme, dark ? .dark : .light))
            host.sizingOptions = [.intrinsicContentSize]
            let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 320, height: 400),
                                styleMask: [.borderless], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            panel.contentView = host
            panel.orderFrontRegardless()
            defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
            try await Task.sleep(for: .milliseconds(150))
            panel.setContentSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
            host.cacheDisplay(in: host.bounds, to: bitmap)
            XCTAssertGreaterThan(host.bounds.height, 250)
            XCTAssertLessThan(host.bounds.height, 650)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: directory.appendingPathComponent("\(name).png"))
        }
    }
}
