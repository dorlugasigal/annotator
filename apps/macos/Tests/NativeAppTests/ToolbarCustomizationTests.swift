import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import MotionCore
@testable import AnnotatorApp

/// Removable toolbar buttons, one-row properties with toggles, and the toolbar that shrinks to one button.
@MainActor
final class ToolbarCustomizationTests: XCTestCase {
    private var output: URL {
        Bundle(for: Self.self).bundleURL
            .deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("UIReview/toolbar-customization")
    }

    private func makeModel(_ name: String) throws -> (WorkspaceModel, () -> Void) {
        let suite = "toolbar-customization-\(name)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        return (model, { model.closeAll(); defaults.removePersistentDomain(forName: suite) })
    }

    /// Shows the toolbar in a panel, lets it settle and returns the panel and its hosting view.
    private func show(_ model: WorkspaceModel, available: CGSize = CGSize(width: 1400, height: 900),
                      dark: Bool = false) async throws -> (NSPanel, NSHostingView<ToolbarView>) {
        let host = NSHostingView(rootView: ToolbarView(model: model, availableSize: available))
        host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let panel = NSPanel(contentRect: CGRect(x: 80, y: 260, width: 1400, height: 400),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.appearance = host.appearance
        panel.level = PresentationWindowLevels.toolbar(screenZoom: false)
        panel.contentView = host
        panel.orderFrontRegardless()
        try await settle(host)
        return (panel, host)
    }

    private func settle(_ host: NSView) async throws {
        for _ in 0..<4 {
            host.setFrameSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(25))
        }
        host.setFrameSize(host.fittingSize)
        host.layoutSubtreeIfNeeded()
    }

    private func close(_ panel: NSPanel) {
        panel.orderOut(nil)
        panel.contentView = nil
        panel.close()
    }

    private func nodes(_ host: NSView) -> [NativeAccessibilityNode] {
        NativeAccessibilityNode.enable()
        return NativeAccessibilityNode.nodes(in: host)
    }

    private func press(_ label: String, in host: NSView, file: StaticString = #filePath, line: UInt = #line) async throws {
        let node = try XCTUnwrap(nodes(host).first { $0.accessibilityRole() == .button && $0.accessibilityLabel() == label },
                                 "No \(label) button", file: file, line: line)
        XCTAssertTrue(node.accessibilityPerformPress(), file: file, line: line)
        try await Task.sleep(for: .milliseconds(40))
    }

    private func save(_ host: NSView, name: String, dark: Bool) throws {
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: output.appendingPathComponent("\(name)-\(dark ? "dark" : "light").png"))
    }

    // MARK: One row of properties

    func testFullPropertiesStayOnOneRowForEveryTool() async throws {
        let (model, cleanup) = try makeModel("one-row")
        defer { cleanup() }
        model.setInspectorPinned(true)
        model.beginDrawing()
        let cases: [(String, Tool, (inout Style) -> Void)] = [
            ("select", .select, { _ in }), ("rectangle", .rectangle, { _ in }),
            ("rectangle-filled", .rectangle, { $0.fill = .blue }), ("diamond", .diamond, { _ in }),
            ("ellipse", .ellipse, { _ in }), ("arrow", .arrow, { _ in }), ("arrow-curved", .arrow, { $0.curved = true }),
            ("line", .line, { _ in }), ("pen", .pen, { _ in }), ("text", .text, { _ in }),
            ("highlighter", .highlighter, { _ in }), ("eraser", .eraser, { _ in }), ("blur", .blur, { _ in }),
            ("pixelate", .pixelate, { _ in }), ("counter", .counter, { _ in }), ("ruler", .ruler, { _ in }),
            ("grid", .grid, { _ in }), ("axes", .axes, { _ in }), ("database", .database, { _ in }), ("image", .image, { _ in })
        ]
        for dark in [false, true] {
            for (name, tool, change) in cases {
                model.tool = tool
                model.applyStyle { $0 = .defaults(for: tool); change(&$0) }
                let (panel, host) = try await show(model, dark: dark)
                defer { close(panel) }
                try save(host, name: "full-\(name)", dark: dark)
                let size = host.fittingSize
                let base = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400, propertiesVisible: true)
                XCTAssertGreaterThanOrEqual(size.width, base - 1, name)
                XCTAssertLessThanOrEqual(size.height, 152, "\(name) properties took more than one row (\(size.height) points)")
                let row = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "contextual-inspector-viewport" }, name)
                let controls = NativeAccessibilityNode.nodes(in: row.object).filter {
                    [.button, .slider, .popUpButton, .checkBox, .menuButton].contains($0.accessibilityRole())
                }
                let middles = controls.map { $0.accessibilityFrame().midY }
                if let top = middles.max(), let bottom = middles.min() {
                    XCTAssertLessThanOrEqual(top - bottom, 24, "\(name) has controls on two rows")
                }
                XCTAssertFalse(nodes(host).contains { $0.accessibilityLabel() == "Precise width" },
                               "\(name): Width is just the slider")
                XCTAssertFalse(NativeAccessibilityNode.nodes(in: host).contains { $0.object is NSScroller },
                               "\(name): the properties have no scroll bar")
            }
        }
    }

    func testToolbarGrowsSidewaysWhenPropertiesNeedMoreRoom() async throws {
        let (model, cleanup) = try makeModel("grow")
        defer { cleanup() }
        model.setInspectorPinned(true)
        model.setToolbarSize(.minimal)
        model.configureToolbar([.select, .arrow, .pen])
        model.beginDrawing()
        let base = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400, propertiesVisible: true,
                                                     size: .minimal)
        model.tool = .arrow
        model.applyStyle { $0 = .defaults(for: .arrow) }
        let (panel, host) = try await show(model)
        defer { close(panel) }
        try save(host, name: "grow-arrow-minimal", dark: false)
        let wide = host.fittingSize.width
        XCTAssertGreaterThan(wide, base, "The arrow's properties need more room than the tools")
        let zoom = try XCTUnwrap(nodes(host).first { $0.accessibilityLabel() == "Zoom tools" })
        let toolbarFrame = panel.convertToScreen(host.convert(host.bounds, to: nil))
        XCTAssertTrue(toolbarFrame.insetBy(dx: -1, dy: -1).contains(zoom.accessibilityFrame()),
                      "The row's menus end inside the toolbar instead of on another line")
        model.tool = .select
        try await settle(host)
        XCTAssertLessThan(host.fittingSize.width, wide, "Select needs less room, so the toolbar shrinks back")
        XCTAssertGreaterThanOrEqual(host.fittingSize.width, base - 1)
    }

    func testChoicesAreSingleButtonsThatCycle() async throws {
        let (model, cleanup) = try makeModel("toggles")
        defer { cleanup() }
        model.setInspectorPinned(true)
        model.beginDrawing()
        model.tool = .rectangle
        model.applyStyle { $0 = .defaults(for: .rectangle) }
        let (panel, host) = try await show(model)
        defer { close(panel) }
        XCTAssertEqual(model.style.pattern, .solid)
        for expected in [StrokePattern.dashed, .dotted, .solid] {
            try await press("Pattern", in: host)
            XCTAssertEqual(model.style.pattern, expected)
        }
        XCTAssertEqual(model.style.roughness, 0)
        for expected in [2.0, 6, 0] {
            try await press("Roughness", in: host)
            XCTAssertEqual(model.style.roughness, expected)
        }
        let rounded = model.style.cornerRadius > 0
        try await press("Rounded corners", in: host)
        XCTAssertEqual(model.style.cornerRadius > 0, !rounded)
        XCTAssertEqual(nodes(host).filter { $0.accessibilityLabel() == "Pattern" }.count, 1, "One Pattern button, not three")

        model.tool = .arrow
        model.applyStyle { $0 = .defaults(for: .arrow) }
        try await settle(host)
        let curved = model.style.curved
        try await press("Curved arrow", in: host)
        XCTAssertEqual(model.style.curved, !curved)
        let toggle = try XCTUnwrap(nodes(host).first { $0.accessibilityLabel() == "Curved arrow" })
        XCTAssertEqual(toggle.accessibilityValue() as? String, model.style.curved ? "On" : "Off")

        model.tool = .pen
        model.applyStyle { $0 = .defaults(for: .pen) }
        try await settle(host)
        let pressure = model.style.pressure
        try await press("Ink pressure", in: host)
        XCTAssertEqual(model.style.pressure, !pressure)

        model.tool = .text
        model.applyStyle { $0 = .defaults(for: .text) }
        try await settle(host)
        let font = model.style.fontName
        try await press("Font", in: host)
        XCTAssertNotEqual(model.style.fontName, font)
        XCTAssertEqual(model.style.alignment, .left)
        try await press("Alignment", in: host)
        XCTAssertEqual(model.style.alignment, .center)
        XCTAssertFalse(nodes(host).contains { $0.accessibilityLabel() == "Font size 28" }, "Text size is just the slider")
    }

    // MARK: Removable buttons

    private static let controlLabels: [ToolbarControl: [String]] = [
        .library: ["Tool library"], .board: ["Board background"], .mode: ["Use apps", "Resume drawing"],
        .undo: ["Undo"], .redo: ["Redo"], .clearAll: ["Clear all drawings — undoable"], .properties: ["Properties"],
        .settings: ["Settings and shortcuts"], .hide: ["Hide drawings and tools"], .focusMenu: ["Focus and cleanup"],
        .zoomMenu: ["Zoom tools"]
    ]

    func testEveryToolbarButtonCanBeTurnedOff() async throws {
        let (model, cleanup) = try makeModel("controls")
        defer { cleanup() }
        model.beginDrawing()
        model.tool = .pen
        for orientation in [ToolbarOrientation.horizontal, .vertical] {
            model.setToolbarOrientation(orientation)
            for pinned in [false, true] {
                model.setInspectorPinned(pinned)
                if !pinned { model.setQuickPreferencesExpanded(true) }
                for control in ToolbarControl.allCases {
                    // The vertical rail has no Spotlight menu; the horizontal quick row and full properties do.
                    if orientation == .vertical && control == .focusMenu { continue }
                    let labels = try XCTUnwrap(Self.controlLabels[control])
                    let (shownPanel, shownHost) = try await show(model)
                    XCTAssertTrue(nodes(shownHost).contains { labels.contains($0.accessibilityLabel() ?? "") },
                                  "\(control) is missing before it is turned off (\(orientation), pinned \(pinned))")
                    close(shownPanel)
                    model.setToolbarControl(control, shown: false)
                    let (panel, host) = try await show(model)
                    XCTAssertFalse(nodes(host).contains { labels.contains($0.accessibilityLabel() ?? "") },
                                   "\(control) stays after it is turned off (\(orientation), pinned \(pinned))")
                    close(panel)
                    model.setToolbarControl(control, shown: true)
                }
            }
        }
    }

    func testTurningButtonsOffGivesTheirRoomBack() async throws {
        let (model, cleanup) = try makeModel("room")
        defer { cleanup() }
        model.setInspectorPinned(false)
        model.setQuickPreferencesExpanded(false)
        model.beginDrawing()
        let full = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400, propertiesVisible: false)
        let (panel, host) = try await show(model)
        XCTAssertEqual(host.fittingSize.width, full, accuracy: 1)
        close(panel)
        for control in ToolbarControl.allCases { model.setToolbarControl(control, shown: false) }
        let hidden = model.hiddenToolbarControls
        XCTAssertEqual(hidden, Set(ToolbarControl.allCases))
        let bare = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400, propertiesVisible: false,
                                                     hiding: hidden)
        XCTAssertLessThan(bare, full - 300)
        let (barePanel, bareHost) = try await show(model)
        defer { close(barePanel) }
        try save(bareHost, name: "all-buttons-off", dark: false)
        XCTAssertEqual(bareHost.fittingSize.width, bare, accuracy: 1)
        XCTAssertTrue(nodes(bareHost).contains { $0.accessibilityLabel() == "Select" }, "The tools stay")
        // Keys still work without their buttons.
        model.performShortcutAction(.properties)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .quick)
    }

    // MARK: One-button toolbar

    func testCollapsedToolbarShowsTheMarkAndModeAndOpensOnClick() async throws {
        let (model, cleanup) = try makeModel("pill")
        defer { cleanup() }
        model.beginDrawing()
        model.setToolbarCollapsed(true)
        for vertical in [false, true] {
            model.setToolbarOrientation(vertical ? .vertical : .horizontal)
            for dark in [false, true] {
                let (panel, host) = try await show(model, dark: dark)
                defer { close(panel) }
                try save(host, name: "collapsed-\(vertical ? "vertical" : "horizontal")-draw", dark: dark)
                XCTAssertEqual(host.fittingSize, CollapsedToolbarButton.size(vertical: vertical, toolbarSize: .expanded))
                let pill = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "collapsed-toolbar" })
                XCTAssertEqual(pill.accessibilityValue() as? String, "Drawing is active")
                XCTAssertFalse(nodes(host).contains { $0.accessibilityLabel() == "Select" }, "Only one button shows")
                model.interact()
                try await settle(host)
                try save(host, name: "collapsed-\(vertical ? "vertical" : "horizontal")-apps", dark: dark)
                XCTAssertEqual(try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "collapsed-toolbar" })
                    .accessibilityValue() as? String, "Apps receive input")
                model.resumeDrawing()
            }
        }
        model.setToolbarOrientation(.horizontal)
        var requests = 0
        model.onExpandToolbarRequested = { requests += 1 }
        let (panel, host) = try await show(model)
        defer { close(panel) }
        let pill = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "collapsed-toolbar" })
        XCTAssertTrue(pill.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(40))
        XCTAssertEqual(requests, 1)
        model.onExpandToolbarRequested = nil
        model.requestToolbarExpansion()
        XCTAssertFalse(model.toolbarCollapsed, "Without the app's animation the toolbar opens at once")
    }

    func testCollapseWaitsForTheDelayAndHoverOpens() async throws {
        let (model, cleanup) = try makeModel("controller")
        defer { cleanup() }
        let controller = ToolbarCollapseController(model: model)
        controller.wait = { _ in try await Task.sleep(for: .milliseconds(20)) }
        var held = false
        var collapses = 0
        var expansions = 0
        controller.isHeldOpen = { held }
        controller.collapse = { collapses += 1; model.setToolbarCollapsed(true) }
        controller.expand = { expansions += 1; model.setToolbarCollapsed(false) }
        controller.restart()
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(collapses, 0, "Never shrinks without the setting")

        model.setToolbarCollapseDelay(3)
        held = true
        controller.restart()
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(collapses, 0, "A menu, drag or the tutorial holds the toolbar open")
        XCTAssertTrue(controller.isWaitingToCollapse, "It tries again later")
        held = false
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(collapses, 1)
        XCTAssertTrue(model.toolbarCollapsed)

        controller.pointerChanged(inside: true)
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(expansions, 1, "Resting the pointer on the button opens the toolbar")
        XCTAssertFalse(model.toolbarCollapsed)
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(collapses, 1, "It stays open while the pointer is on it")
        controller.pointerChanged(inside: false)
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertEqual(collapses, 2, "It shrinks again after the pointer leaves")

        model.setToolbarCollapseDelay(nil)
        XCTAssertFalse(model.toolbarCollapsed, "Turning the setting off opens the toolbar")
        controller.cancel()
    }

    func testRealToolbarMorphsIntoOneButtonAndBack() async throws {
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main)
        let safe = screen.visibleFrame.insetBy(dx: 12, dy: 12)
        let (model, cleanup) = try makeModel("morph")
        model.setVisibilityAnimation(false)
        model.setInspectorPinned(false)
        model.setQuickPreferencesExpanded(false)
        model.setToolbarCollapseDelay(3)
        let app = AppDelegate(model: model)
        app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
        app.collapse.wait = { _ in try await Task.sleep(for: .seconds(30)) }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
        app.activateTools()
        model.interact()
        try await Task.sleep(for: .milliseconds(250))
        XCTAssertFalse(model.toolbarCollapsed, "The tools appear open")
        let open = try XCTUnwrap(app.toolbarFrame)
        XCTAssertEqual(open.midX, safe.midX, accuracy: 1)
        // The timer shrinks it once the pointer has been away for the delay.
        app.collapse.wait = { _ in try await Task.sleep(for: .milliseconds(60)) }
        app.collapse.restart()
        for _ in 0..<40 where !(model.toolbarCollapsed && !app.toolbarIsAnimating) {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertTrue(model.toolbarCollapsed)
        let pill = try XCTUnwrap(app.toolbarFrame)
        XCTAssertEqual(pill.size, CollapsedToolbarButton.size(vertical: false, toolbarSize: .expanded))
        XCTAssertEqual(pill.midX, open.midX, accuracy: 1, "The button stays where the toolbar's middle was")
        XCTAssertEqual(pill.maxY, open.maxY, accuracy: 1)

        app.collapse.wait = { _ in try await Task.sleep(for: .seconds(30)) }
        model.requestToolbarExpansion()
        try await Task.sleep(for: .milliseconds(80))
        let midway = try XCTUnwrap(app.toolbarFrame)
        XCTAssertTrue(app.toolbarIsAnimating || midway == open, "The button grows back into the toolbar")
        for _ in 0..<20 where app.toolbarIsAnimating { try await Task.sleep(for: .milliseconds(50)) }
        XCTAssertFalse(model.toolbarCollapsed)
        let reopened = try XCTUnwrap(app.toolbarFrame)
        XCTAssertEqual(reopened.width, open.width, accuracy: 1)
        XCTAssertEqual(reopened.midX, open.midX, accuracy: 1)
        XCTAssertEqual(reopened.maxY, open.maxY, accuracy: 1)
    }

    // MARK: Settings

    func testSettingsPreviewTakesButtonsOffAndPutsThemBack() async throws {
        let (model, cleanup) = try makeModel("settings")
        defer { cleanup() }
        let host = NSHostingView(rootView: ToolbarShortcutSettings(model: model).frame(width: 840, height: 1600))
        let panel = NSPanel(contentRect: CGRect(x: 60, y: 60, width: 840, height: 900),
                            styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { close(panel) }
        try await Task.sleep(for: .milliseconds(150))
        let strip = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "toolbar-arrangement" })
        let shown = NativeAccessibilityNode.nodes(in: strip.object).compactMap { $0.accessibilityIdentifier() }
        for control in ToolbarControl.allCases {
            XCTAssertTrue(shown.contains("toolbar-control-\(control.rawValue)"), "The preview has no \(control) tile")
        }
        let undo = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "toolbar-control-undo" })
        XCTAssertTrue(perform("Remove from toolbar", on: undo))
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertFalse(model.showsToolbarControl(.undo))
        let tray = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "more-control-undo" },
                                 "Undo waits in More tools")
        XCTAssertTrue(perform("Add to toolbar", on: tray))
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertTrue(model.showsToolbarControl(.undo))
        XCTAssertTrue(nodes(host).contains { $0.accessibilityIdentifier() == "toolbar-collapse-delay" })
        model.setToolbarCollapseDelay(10)
        XCTAssertEqual(model.preferences.toolbarCollapseDelay, 10)
        try await Task.sleep(for: .milliseconds(100))
        try save(host, name: "settings-arranger", dark: false)
    }

    private func perform(_ name: String, on node: NativeAccessibilityNode) -> Bool {
        guard let actions = node.object.value(forKey: "accessibilityCustomActions") as? [NSAccessibilityCustomAction],
              let action = actions.first(where: { $0.name == name }) else { return false }
        if let handler = action.handler { return handler() }
        if let target = action.target as? NSObject, let selector = action.selector {
            target.perform(selector, with: action)
            return true
        }
        return false
    }
}
