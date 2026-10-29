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
        // SwiftUI builds its accessibility tree only once accessibility is on, so switch it on before the toolbar exists.
        enableAccessibility()
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

    /// Switches accessibility on once: setting it again while a toolbar shows can make SwiftUI rebuild its tree, so a
    /// button found a moment before may no longer take a press.
    private static var accessibilityOn = false

    private func enableAccessibility() {
        guard !Self.accessibilityOn else { return }
        NativeAccessibilityNode.enable()
        Self.accessibilityOn = true
    }

    private func nodes(_ host: NSView) -> [NativeAccessibilityNode] {
        enableAccessibility()
        return NativeAccessibilityNode.nodes(in: host)
    }

    private func press(_ label: String, in host: NSView, file: StaticString = #filePath, line: UInt = #line) async throws {
        // The accessibility tree can trail a new layout, so wait for the button.
        var node = nodes(host).first { $0.accessibilityRole() == .button && $0.accessibilityLabel() == label }
        for _ in 0..<75 where node == nil {
            try await Task.sleep(for: .milliseconds(20))
            node = nodes(host).first { $0.accessibilityRole() == .button && $0.accessibilityLabel() == label }
        }
        let button = try XCTUnwrap(node, "No \(label) button", file: file, line: line)
        XCTAssertTrue(button.accessibilityPerformPress(), file: file, line: line)
        // A cycle button's new face slides in (Motion.snappy); let it finish before the next look at the tree.
        try await Task.sleep(for: .milliseconds(450))
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
        model.setPropertiesShown(true)
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
                let base = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400)
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
        model.setPropertiesShown(true)
        model.setToolbarSize(.minimal)
        model.configureToolbar([.select, .arrow, .pen])
        // Every choice side by side takes more room than the tools.
        for choice in [PropertyChoice.pattern, .roughness, .arrowType] { model.setShowsAllChoices(choice, true) }
        model.beginDrawing()
        let base = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400,
                                                     size: .minimal)
        model.tool = .arrow
        model.applyStyle { $0 = .defaults(for: .arrow) }
        let (panel, host) = try await show(model)
        defer { close(panel) }
        try save(host, name: "grow-arrow-minimal", dark: false)
        let wide = host.fittingSize.width
        XCTAssertGreaterThan(wide, base, "The arrow's properties need more room than the tools")
        let toolbarFrame = panel.convertToScreen(host.convert(host.bounds, to: nil)).insetBy(dx: -1, dy: -1)
        let row = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "contextual-inspector-viewport" })
        for control in NativeAccessibilityNode.nodes(in: row.object) where !control.accessibilityFrame().isEmpty {
            XCTAssertTrue(toolbarFrame.contains(control.accessibilityFrame()),
                          "\(control.accessibilityLabel() ?? "A control") ends outside the toolbar instead of fitting the row")
        }
        model.tool = .select
        try await settle(host)
        XCTAssertLessThan(host.fittingSize.width, wide, "Select needs less room, so the toolbar shrinks back")
        XCTAssertGreaterThanOrEqual(host.fittingSize.width, base - 1)
    }

    func testChoicesAreSingleButtonsThatCycle() async throws {
        let (model, cleanup) = try makeModel("toggles")
        defer { cleanup() }
        model.setPropertiesShown(true)
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

    /// Each property with a few choices can show all of them side by side instead of one button.
    func testPropertiesCanShowAllTheirChoices() async throws {
        let (model, cleanup) = try makeModel("all-choices")
        defer { cleanup() }
        model.setPropertiesShown(true)
        model.beginDrawing()
        typealias Case = (PropertyChoice, Tool, (inout Style) -> Void, String, [String], String, (Style) -> Bool)
        let cases: [Case] = [
            (.pattern, .rectangle, { _ in }, "Pattern", ["Solid", "Dashed", "Dotted"], "Dotted", { $0.pattern == .dotted }),
            (.roughness, .rectangle, { _ in }, "Roughness", ["Clean", "Sketchy", "Wild"], "Wild", { $0.roughness == 6 }),
            (.fillStyle, .rectangle, { $0.fill = .blue }, "Fill style", ["Solid", "Hatch", "Crosshatch"], "Hatch",
             { $0.fillPattern == .hatch }),
            (.edges, .rectangle, { $0.cornerRadius = 14 }, "Rounded corners", ["Sharp corners", "Rounded corners"],
             "Sharp corners", { $0.cornerRadius == 0 }),
            (.arrowType, .arrow, { $0.curved = false }, "Curved arrow", ["Straight arrow", "Curved arrow"], "Curved arrow",
             { $0.curved }),
            (.pressure, .pen, { $0.pressure = true }, "Ink pressure", ["Constant pressure", "Ink pressure"],
             "Constant pressure", { !$0.pressure }),
            (.font, .text, { _ in }, "Font", ["System font", "Mono font"], "Mono font", { $0.fontName == "monospace" }),
            (.alignment, .text, { _ in }, "Alignment", ["Align left", "Align center", "Align right"], "Align right",
             { $0.alignment == .right })
        ]
        for (choice, tool, prepare, one, all, pick, picked) in cases {
            model.tool = tool
            model.applyStyle { $0 = .defaults(for: tool); prepare(&$0) }
            let (onePanel, oneHost) = try await show(model)
            let oneLabels = nodes(oneHost).compactMap { $0.accessibilityLabel() }
            XCTAssertTrue(oneLabels.contains(one), "\(choice) is one button by default")
            XCTAssertFalse(Set(all).subtracting([one]).contains { oneLabels.contains($0) }, "\(choice) hides its other choices")
            let oneWidth = oneHost.fittingSize.width
            close(onePanel)

            model.setShowsAllChoices(choice, true)
            XCTAssertEqual(model.preferences.expandedPropertyChoices, [choice.rawValue])
            let (panel, host) = try await show(model)
            let labels = nodes(host).compactMap { $0.accessibilityLabel() }
            for label in all { XCTAssertTrue(labels.contains(label), "\(choice) shows \(label)") }
            if !all.contains(one) { XCTAssertFalse(labels.contains(one), "\(choice) has no one-button control as well") }
            XCTAssertGreaterThanOrEqual(host.fittingSize.width, oneWidth - 1)
            if choice == .pattern { try save(host, name: "all-choices-pattern", dark: false) }
            try await press(pick, in: host)
            XCTAssertTrue(picked(model.style), "\(pick) applies to \(choice)")
            close(panel)
            model.setShowsAllChoices(choice, false)
        }
        XCTAssertNil(model.preferences.expandedPropertyChoices)
    }

    func testSettingsPreviewChoosesHowPropertiesLook() async throws {
        let (model, cleanup) = try makeModel("choices-settings")
        defer { cleanup() }
        let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false, initialTab: .toolbar))
        let window = NSWindow(contentRect: NSRect(origin: CGPoint(x: 40, y: 40), size: SettingsSurfaceView.defaultContentSize),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.orderFrontRegardless()
        defer { window.orderOut(nil); window.contentView = nil; window.close() }
        var cards: [NativeAccessibilityNode] = []
        for _ in 0..<30 where cards.count < PropertyChoice.allCases.count {
            try await Task.sleep(for: .milliseconds(100))
            cards = nodes(host).filter { ($0.accessibilityIdentifier() ?? "").hasPrefix("property-choice-") &&
                !($0.accessibilityIdentifier() ?? "").hasSuffix("-one") && !($0.accessibilityIdentifier() ?? "").hasSuffix("-all") }
        }
        XCTAssertEqual(Set(cards.compactMap { $0.accessibilityIdentifier() }),
                       Set(PropertyChoice.allCases.map { "property-choice-\($0.rawValue)" }))
        for choice in PropertyChoice.allCases {
            let one = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "property-choice-\(choice.rawValue)-one" })
            XCTAssertEqual(one.accessibilityValue() as? String, "Selected", "\(choice) starts as one button")
        }
        let all = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "property-choice-roughness-all" })
        XCTAssertTrue(all.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertTrue(model.showsAllChoices(.roughness))
        XCTAssertEqual(try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "property-choice-roughness-all" })
            .accessibilityValue() as? String, "Selected")
        let reset = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == "reset-property-choices" })
        XCTAssertTrue(reset.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertFalse(model.showsAllChoices(.roughness))
        model.setShowsAllChoices(.pattern, true)
        try await Task.sleep(for: .milliseconds(150))
        try save(host, name: "settings-properties", dark: false)
        model.resetPropertyChoices()
    }

    // MARK: Removable buttons

    private static let controlLabels: [ToolbarControl: [String]] = [
        .library: ["Tool library"], .board: ["Board background"], .mode: ["Use apps", "Resume drawing"],
        .undo: ["Undo"], .redo: ["Redo"], .clearAll: ["Clear all drawings — undoable"], .properties: ["Properties"],
        .settings: ["Settings and shortcuts"], .hide: ["Hide drawings and tools"]
    ]

    func testEveryToolbarButtonCanBeTurnedOff() async throws {
        let (model, cleanup) = try makeModel("controls")
        defer { cleanup() }
        model.beginDrawing()
        model.tool = .pen
        for orientation in [ToolbarOrientation.horizontal, .vertical] {
            model.setToolbarOrientation(orientation)
            for shown in [false, true] {
                model.setPropertiesShown(shown)
                for control in ToolbarControl.allCases {
                    let labels = try XCTUnwrap(Self.controlLabels[control])
                    let (shownPanel, shownHost) = try await show(model)
                    XCTAssertTrue(nodes(shownHost).contains { labels.contains($0.accessibilityLabel() ?? "") },
                                  "\(control) is missing before it is turned off (\(orientation), properties \(shown))")
                    close(shownPanel)
                    model.setToolbarControl(control, shown: false)
                    let (panel, host) = try await show(model)
                    XCTAssertFalse(nodes(host).contains { labels.contains($0.accessibilityLabel() ?? "") },
                                   "\(control) stays after it is turned off (\(orientation), properties \(shown))")
                    close(panel)
                    model.setToolbarControl(control, shown: true)
                }
            }
        }
    }

    /// Spotlight and Zoom are menus the size of a tool. They wait in More tools until someone adds them.
    func testSpotlightAndZoomMenusAreToolSizedToolbarItems() async throws {
        let (model, cleanup) = try makeModel("menus")
        defer { cleanup() }
        model.beginDrawing()
        model.tool = .pen
        model.setPropertiesShown(true)
        let menuIDs = ["toolbar-action-focusMenu", "toolbar-action-zoomMenu"]
        let (plainPanel, plainHost) = try await show(model)
        XCTAssertFalse(nodes(plainHost).contains { menuIDs.contains($0.accessibilityIdentifier() ?? "") },
                       "The default toolbar has no Spotlight or Zoom menu")
        XCTAssertFalse(nodes(plainHost).contains { ["Zoom tools", "Focus and cleanup"].contains($0.accessibilityLabel() ?? "") },
                       "The properties row has no menus of its own")
        close(plainPanel)
        XCTAssertTrue(WorkspaceModel.toolbarCandidates.contains(.action(.focusMenu)))
        XCTAssertTrue(WorkspaceModel.toolbarCandidates.contains(.action(.zoomMenu)))
        model.configureToolbar(items: [.tool(.select), .tool(.rectangle), .tool(.ellipse), .action(.focusMenu), .action(.zoomMenu)])
        for size in ToolbarSize.allCases {
            for orientation in [ToolbarOrientation.horizontal, .vertical] {
                model.setToolbarSize(size)
                model.setToolbarOrientation(orientation)
                let (panel, host) = try await show(model)
                let all = nodes(host)
                let name = "\(size) \(orientation)"
                let rectangle = try XCTUnwrap(all.first { $0.accessibilityLabel() == "Rectangle" }, name).accessibilityFrame()
                let ellipse = try XCTUnwrap(all.first { $0.accessibilityLabel() == "Ellipse" }, name).accessibilityFrame()
                for (id, label) in zip(menuIDs, ["Spotlight menu", "Zoom menu"]) {
                    // VoiceOver finds one menu button per menu, with its name, as on Windows.
                    let elements = all.filter { $0.accessibilityIdentifier() == id }
                    XCTAssertEqual(elements.count, 1, "\(name): \(label) is one element")
                    XCTAssertEqual(elements.first?.accessibilityRole(), .menuButton, name)
                    XCTAssertEqual(elements.first?.accessibilityLabel(), label, name)
                    XCTAssertEqual(elements.first?.accessibilityValue() as? String, "Off", name)
                    XCTAssertFalse((elements.first?.accessibilityHelp() ?? "").isEmpty, "\(name): \(label) says what it holds")
                }
                let focus = try XCTUnwrap(all.first { $0.accessibilityIdentifier() == menuIDs[0] }, name).accessibilityFrame()
                let zoom = try XCTUnwrap(all.first { $0.accessibilityIdentifier() == menuIDs[1] }, name).accessibilityFrame()
                // The menu covers the whole cell, like a tool button.
                XCTAssertEqual(focus.width, ToolbarMetrics(size).buttonWidth, accuracy: 1, name)
                XCTAssertEqual(focus.height, ToolbarMetrics(size).buttonHeight, accuracy: 1, name)
                // Neighbouring menus sit one tool apart, like neighbouring tools, so each one takes a tool's room.
                if orientation == .horizontal {
                    XCTAssertEqual(zoom.midX - focus.midX, ellipse.midX - rectangle.midX, accuracy: 1, name)
                    XCTAssertEqual(rectangle.width, ToolbarMetrics(size).buttonWidth, accuracy: 1, name)
                } else {
                    XCTAssertEqual(abs(zoom.midY - focus.midY), abs(ellipse.midY - rectangle.midY), accuracy: 1, name)
                }
                if size == .expanded && orientation == .horizontal { try save(host, name: "presenting-menus", dark: false) }
                close(panel)
            }
        }
        model.setToolbarSize(.expanded)
        model.setToolbarOrientation(.horizontal)
        model.spotlight = true
        let (panel, host) = try await show(model)
        defer { close(panel) }
        try save(host, name: "presenting-menus-on", dark: true)
        let focus = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == menuIDs[0] })
        XCTAssertEqual(focus.accessibilityValue() as? String, "On", "The Spotlight menu lights up while the spotlight is on")
        let zoom = try XCTUnwrap(nodes(host).first { $0.accessibilityIdentifier() == menuIDs[1] })
        XCTAssertEqual(zoom.accessibilityValue() as? String, "Off")
        model.spotlight = false
    }

    func testTurningButtonsOffGivesTheirRoomBack() async throws {
        let (model, cleanup) = try makeModel("room")
        defer { cleanup() }
        model.setPropertiesShown(false)
        model.beginDrawing()
        let full = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400)
        let (panel, host) = try await show(model)
        XCTAssertEqual(host.fittingSize.width, full, accuracy: 1)
        close(panel)
        for control in ToolbarControl.allCases { model.setToolbarControl(control, shown: false) }
        let hidden = model.hiddenToolbarControls
        XCTAssertEqual(hidden, Set(ToolbarControl.allCases))
        let bare = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400,
                                                     hiding: hidden)
        XCTAssertLessThan(bare, full - 300)
        let (barePanel, bareHost) = try await show(model)
        defer { close(barePanel) }
        try save(bareHost, name: "all-buttons-off", dark: false)
        XCTAssertEqual(bareHost.fittingSize.width, bare, accuracy: 1)
        XCTAssertTrue(nodes(bareHost).contains { $0.accessibilityLabel() == "Select" }, "The tools stay")
        // Keys still work without their buttons.
        model.performShortcutAction(.properties)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .shown)
    }

    /// A toolbar with few tools hugs them on one row, with or without properties, so nothing leaves a gap.
    func testFewToolsKeepOneRowWithoutGaps() async throws {
        let (model, cleanup) = try makeModel("few")
        defer { cleanup() }
        model.beginDrawing()
        model.configureToolbar([.select, .pen, .eraser])
        model.choose(.pen)
        model.setPropertiesShown(false)
        let width = ToolbarToolLayout.horizontalWidth(items: model.toolbarItems, available: 1400)
        XCTAssertFalse(ToolbarToolLayout.usesTwoRows(items: model.toolbarItems, available: 1400))
        let (panel, host) = try await show(model)
        try save(host, name: "few-tools", dark: false)
        XCTAssertEqual(host.fittingSize.width, width, accuracy: 1, "The toolbar is exactly its tools and buttons")
        XCTAssertLessThanOrEqual(host.fittingSize.height, 72, "One row, never two")
        close(panel)
        model.setPropertiesShown(true)
        let (shownPanel, shownHost) = try await show(model)
        defer { close(shownPanel) }
        try save(shownHost, name: "few-tools-properties", dark: false)
        XCTAssertGreaterThanOrEqual(shownHost.fittingSize.width, width - 1)
        XCTAssertLessThanOrEqual(shownHost.fittingSize.height, 150, "The tools and the properties each take one row")
    }

    /// While the real toolbar grows or shrinks for a tool's properties, its tools stay at the left edge and its buttons
    /// at the right edge on every frame.
    func testToolsAndButtonsStayAtTheEdgesWhileTheToolbarResizes() async throws {
        let (model, cleanup) = try makeModel("resize")
        model.setVisibilityAnimation(false)
        model.configureToolbar([.select, .rectangle, .arrow, .pen, .text])
        model.setPropertiesShown(true)
        let app = AppDelegate(model: model)
        app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
        app.activateTools()
        model.choose(.pen)
        try await Task.sleep(for: .milliseconds(400))
        let panel = try XCTUnwrap(app.toolbarWindow)
        let content = try XCTUnwrap(panel.contentView)
        func edges() throws -> (left: CGFloat, right: CGFloat, width: CGFloat) {
            let all = nodes(content)
            let select = try XCTUnwrap(all.first { $0.accessibilityLabel() == "Select" }).accessibilityFrame()
            let hide = try XCTUnwrap(all.first { $0.accessibilityLabel() == "Hide drawings and tools" }).accessibilityFrame()
            return (select.minX - panel.frame.minX, panel.frame.maxX - hide.maxX, panel.frame.width)
        }
        let rest = try edges()
        // The Arrow's properties need more room than the Pen's.
        for tool in [Tool.arrow, .pen] {
            let start = panel.frame.width
            model.choose(tool)
            var moving = false
            for _ in 0..<30 {
                try await Task.sleep(for: .milliseconds(12))
                let sample = try edges()
                if app.toolbarIsAnimating && abs(sample.width - start) >= 1 { moving = true }
                XCTAssertEqual(sample.left, rest.left, accuracy: 1, "The tools leave the left edge at width \(sample.width)")
                XCTAssertEqual(sample.right, rest.right, accuracy: 1, "The buttons leave the right edge at width \(sample.width)")
            }
            XCTAssertNotEqual(panel.frame.width, start, accuracy: 1, "\(tool.label) changes the toolbar's width")
            if !Motion.systemReducesMotion { XCTAssertTrue(moving, "The toolbar animates to its new width") }
        }
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

    func testCollapseWaitsForTheDelayAndPointingOpens() async throws {
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
        XCTAssertEqual(expansions, 1, "Pointing at the button opens the toolbar at once, with no wait")
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

    /// The Shrink demo starts still, shrinks the toolbar once the pointer leaves, opens it the moment the pointer is
    /// back, and loops.
    func testShrinkDemoTimeline() {
        XCTAssertEqual(ShrinkDemoPose(at: 0), ShrinkDemoPose(at: ShrinkDemoPose.leave - 0.01), "The first second is still")
        XCTAssertEqual(ShrinkDemoPose(at: 0).shrink, 0)
        XCTAssertEqual(ShrinkDemoPose(at: 0).pointer, ShrinkDemoPose.onToolbar)
        XCTAssertEqual(ShrinkDemoPose(at: ShrinkDemoPose.collapse - 0.01), .open, "The pointer is away before it shrinks")
        let shrinking = ShrinkDemoPose(at: (ShrinkDemoPose.collapse + ShrinkDemoPose.rest) / 2)
        XCTAssertGreaterThan(shrinking.shrink, 0)
        XCTAssertLessThan(shrinking.shrink, 1)
        XCTAssertEqual(ShrinkDemoPose.rest - ShrinkDemoPose.collapse, MotionTiming.toolbarCollapseDuration, accuracy: 0.001)
        XCTAssertEqual(ShrinkDemoPose(at: ShrinkDemoPose.back - 0.01), .shrunk)
        let arrived = ShrinkDemoPose(at: ShrinkDemoPose.expand)
        XCTAssertEqual(arrived.pointer, ShrinkDemoPose.onToolbar)
        XCTAssertLessThan(ShrinkDemoPose(at: ShrinkDemoPose.expand + 0.05).shrink, 1, "It opens with no wait")
        XCTAssertEqual(ShrinkDemoPose.opened - ShrinkDemoPose.expand, MotionTiming.toolbarExpandDuration, accuracy: 0.001)
        XCTAssertEqual(ShrinkDemoPose(at: ShrinkDemoPose.opened + 0.01).shrink, 0)
        XCTAssertEqual(ShrinkDemoPose(at: ShrinkDemoPose.period + 0.3), ShrinkDemoPose(at: 0.3), "It loops")
    }

    func testSettingsShowTheShrinkDemo() async throws {
        let (model, cleanup) = try makeModel("shrink-demo")
        defer { cleanup() }
        let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false, initialTab: .toolbar))
        let window = NSWindow(contentRect: NSRect(origin: CGPoint(x: 40, y: 40), size: SettingsSurfaceView.defaultContentSize),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.orderFrontRegardless()
        defer { window.orderOut(nil); window.contentView = nil; window.close() }
        var found: NativeAccessibilityNode?
        for _ in 0..<30 where found == nil {
            try await Task.sleep(for: .milliseconds(100))
            found = nodes(host).first { $0.accessibilityIdentifier() == "toolbar-shrink-demo" }
        }
        let demo = try XCTUnwrap(found, "Settings shows what Shrink does")
        XCTAssertEqual(demo.accessibilityLabel(), "Shrink preview")
        XCTAssertEqual(demo.accessibilityRole(), .image)
        XCTAssertFalse((demo.accessibilityHelp() ?? "").isEmpty, "It says what it shows")
        XCTAssertGreaterThan(demo.accessibilityFrame().width, 200)
    }

    /// The morph covers the toolbar with a picture of its face while the window shrinks, so the tools fade out instead of
    /// blinking away.
    func testMorphStartsFromAPictureOfTheTools() async throws {
        let (model, cleanup) = try makeModel("picture")
        model.setVisibilityAnimation(false)
        model.setPropertiesShown(false)
        let app = AppDelegate(model: model)
        app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
        app.activateTools()
        try await Task.sleep(for: .milliseconds(300))
        let content = try XCTUnwrap(app.toolbarWindow?.contentView as? ToolbarContentView)
        let picture = try XCTUnwrap(content.snapshotHost(), "The morph has a picture of the tools")
        let bitmap = NSBitmapImageRep(cgImage: picture)
        var ink = 0
        for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) where (bitmap.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.5 {
                ink += 1
            }
        }
        XCTAssertGreaterThan(ink, 200, "The picture shows the tools, not an empty toolbar")
        XCTAssertEqual(content.subviews.count, 2)
        model.setToolbarCollapseDelay(3)
        app.collapse.collapse()
        XCTAssertTrue(app.toolbarIsAnimating, "The morph starts")
        XCTAssertEqual(content.subviews.count, 3, "The picture of the tools covers the toolbar while it morphs")
        XCTAssertFalse(content.subviews.last === content.host, "The picture stays above the new face and the background")
        for _ in 0..<40 where app.toolbarIsAnimating { try await Task.sleep(for: .milliseconds(25)) }
        XCTAssertTrue(model.toolbarCollapsed)
        XCTAssertEqual(content.subviews.count, 2, "The picture goes when the morph ends")
    }

    func testRealToolbarMorphsIntoOneButtonAndBack() async throws {
        let screen = try XCTUnwrap(NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main)
        let safe = screen.visibleFrame.insetBy(dx: 12, dy: 12)
        let (model, cleanup) = try makeModel("morph")
        model.setVisibilityAnimation(false)
        model.setPropertiesShown(false)
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

    /// Settings opens wide enough for 15 tools and every toolbar button on one line, like the toolbar itself.
    func testSettingsShowFifteenToolsAndEveryButtonOnOneLine() async throws {
        let (model, cleanup) = try makeModel("one-line")
        defer { cleanup() }
        model.configureToolbar(WorkspacePreferences.defaultToolbar + [.triangle, .hexagon, .star])
        XCTAssertEqual(model.toolbarTools.count, 15)
        let size = SettingsSurfaceView.defaultContentSize
        let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false, initialTab: .toolbar))
        let window = NSWindow(contentRect: NSRect(origin: CGPoint(x: 40, y: 40), size: size),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.orderFrontRegardless()
        defer { window.orderOut(nil); window.contentView = nil; window.close() }
        var found: NativeAccessibilityNode?
        for _ in 0..<30 where found == nil {
            try await Task.sleep(for: .milliseconds(100))
            found = nodes(host).first { $0.accessibilityIdentifier() == "toolbar-arrangement" }
        }
        let strip = try XCTUnwrap(found, "Settings shows your toolbar")
        let visible = strip.accessibilityFrame()
        let tiles = NativeAccessibilityNode.nodes(in: strip.object).filter {
            let id = $0.accessibilityIdentifier() ?? ""
            return id.hasPrefix("toolbar-tool-") || id.hasPrefix("toolbar-action-") || id.hasPrefix("toolbar-control-")
        }
        XCTAssertEqual(tiles.count, 15 + ToolbarControl.allCases.count)
        let middles = tiles.map { $0.accessibilityFrame().midY }
        XCTAssertLessThanOrEqual((middles.max() ?? 0) - (middles.min() ?? 0), 1, "Your toolbar stays on one line")
        for tile in tiles {
            XCTAssertTrue(visible.insetBy(dx: -1, dy: -1).contains(tile.accessibilityFrame()),
                          "\(tile.accessibilityIdentifier() ?? "A tile") needs scrolling at the default width: \(tile.accessibilityFrame()) outside \(visible)")
        }
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
