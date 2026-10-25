import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ToolbarShortcutSettingsTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!
    private var model: WorkspaceModel!

    override func setUp() async throws {
        suite = "toolbar-shortcuts-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        model = WorkspaceModel(defaults: defaults)
    }

    override func tearDown() async throws {
        model.closeAll()
        model = nil
        defaults.removePersistentDomain(forName: suite)
    }

    // MARK: Settings

    func testClickingAKeyThenPressingOneAssignsShiftKeysAndUndoesMovedKeys() async throws {
        try await withSettings { panel, host in
            try await press("Star key", in: host)
            XCTAssertTrue(panel.firstResponder is KeyCapture.CaptureView, "The chip must listen for the next key.")
            XCTAssertTrue(text(in: host).contains("Waiting for a key"))
            XCTAssertFalse(text(in: host).contains("…"), "A waiting key says Press, not an ellipsis.")
            XCTAssertTrue(text(in: host).contains("Press a key for Star"))
            try await type("S", keyCode: 1, flags: .shift, in: panel)
            XCTAssertEqual(model.shortcut(for: .tool(.star)), LocalShortcut(key: "s", shift: true))
            XCTAssertEqual(model.preferences.toolShortcuts["s"], Tool.cover.rawValue, "Plain S keeps Effects.")
            XCTAssertFalse(panel.firstResponder is KeyCapture.CaptureView, "Recording stops after one key.")

            try await press("Spotlight key", in: host)
            try await type("2", keyCode: 19, in: panel)
            XCTAssertEqual(model.shortcut(for: .action(.spotlight))?.label, "2")
            XCTAssertNil(model.shortcut(for: .tool(.rectangle)))
            XCTAssertTrue(text(in: host).contains("Rectangle has no key now"))
            XCTAssertTrue(text(in: host).contains("Give 2 back to Rectangle"), "Undo says what it puts back.")
            try await pressIdentifier("undo-key-change", in: host)
            XCTAssertEqual(model.shortcut(for: .tool(.rectangle))?.label, "2")
            XCTAssertNil(model.shortcut(for: .action(.spotlight)))
        }
    }

    func testRejectedKeysExplainInPlaceAndDeleteClearsAKey() async throws {
        try await withSettings { panel, host in
            try await press("Pen key", in: host)
            try await type("r", keyCode: 15, in: panel)
            XCTAssertEqual(model.shortcut(for: .tool(.pen))?.label, "7")
            XCTAssertTrue(text(in: host).contains("changes the ink color"))
            XCTAssertTrue(panel.firstResponder is KeyCapture.CaptureView, "A rejected key keeps listening.")
            try await type("k", keyCode: 40, flags: .command, in: panel)
            XCTAssertTrue(text(in: host).contains("without Command"))
            try await type("\u{1b}", keyCode: 53, in: panel)
            XCTAssertFalse(panel.firstResponder is KeyCapture.CaptureView)
            XCTAssertEqual(model.shortcut(for: .tool(.pen))?.label, "7")

            try await press("Pen key", in: host)
            try await type("\u{7f}", keyCode: 51, in: panel)
            XCTAssertNil(model.shortcut(for: .tool(.pen)))
            XCTAssertTrue(text(in: host).contains("Pen has no key now"))
            XCTAssertTrue(text(in: host).contains("Give 7 back to Pen"))
            try await pressIdentifier("undo-key-change", in: host)
            XCTAssertEqual(model.shortcut(for: .tool(.pen))?.label, "7")

            // On AZERTY, Shift and the "&" key type "1"; that records the digit.
            try await press("Star key", in: host)
            try await type("1", keyCode: 18, flags: .shift, in: panel)
            XCTAssertEqual(model.shortcut(for: .tool(.star)), LocalShortcut(key: "1"))
            XCTAssertNil(model.shortcut(for: .tool(.select)))
        }
    }

    func testGlobalShortcutsRecordModifierKeysAndRejectClashes() async throws {
        try await withSettings { panel, host in
            let text = text(in: host)
            XCTAssertTrue(text.contains("Control + Shift + 2"))
            XCTAssertTrue(text.contains("Live zoom shortcut"))
            try await pressIdentifier("global-shortcut-liveZoom", in: host)
            try await type("2", keyCode: 19, flags: .control, in: panel)
            XCTAssertTrue(self.text(in: host).contains("already does Show / hide"))
            XCTAssertNil(model.preferences.zoomShortcut)
            try await type("9", keyCode: 25, flags: [.control, .option], in: panel)
            XCTAssertEqual(model.preferences.zoomShortcut?.label, "⌃⌥9")
            try await pressIdentifier("global-shortcut-draw", in: host)
            try await type("@", keyCode: 19, flags: [.control, .option, .shift], in: panel)
            XCTAssertEqual(model.preferences.resumeShortcut?.label, "⌃⌥⇧2", "Shift with a digit records the digit.")
        }
    }

    func testArrangerAccessibilityActionsAddMoveAndRemoveTools() async throws {
        try await withSettings { _, host in
            XCTAssertTrue(perform("Add to toolbar", on: try tool(.star, inToolbar: false, in: host)))
            try await Task.sleep(for: .milliseconds(150))
            XCTAssertEqual(model.toolbarTools.last, .star)
            XCTAssertTrue(perform("Move left", on: try tool(.star, inToolbar: true, in: host)))
            try await Task.sleep(for: .milliseconds(150))
            XCTAssertEqual(model.toolbarTools.suffix(2), [.star, .cover])
            XCTAssertTrue(perform("Remove from toolbar", on: try tool(.star, inToolbar: true, in: host)))
            try await Task.sleep(for: .milliseconds(150))
            XCTAssertFalse(model.toolbarTools.contains(.star))
            XCTAssertNoThrow(try tool(.star, inToolbar: false, in: host))
            XCTAssertTrue(perform("Remove from toolbar", on: try tool(.select, inToolbar: true, in: host)))
            try await Task.sleep(for: .milliseconds(150))
            XCTAssertEqual(model.toolbarTools.first, .select, "Select always stays.")
            XCTAssertTrue(text(in: host).contains("Select always stays in the toolbar"))
        }
    }

    func testPresentingButtonsAndSingleEffectsCanBePutInTheToolbar() async throws {
        try await withSettings { _, host in
            let fixed = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityIdentifier() == "toolbar-button-keys"
            })
            let fixedText = NativeAccessibilityNode.nodes(in: fixed.object).compactMap { $0.accessibilityLabel() }.joined(separator: "\n")
            XCTAssertTrue(fixedText.contains("Board key"))
            XCTAssertFalse(fixedText.contains("Spotlight key"), "Spotlight lives in More tools now.")
            for id in ["more-action-spotlight", "more-tool-blur"] {
                let node = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == id }, id)
                XCTAssertTrue(perform("Add to toolbar", on: node), id)
                try await Task.sleep(for: .milliseconds(150))
            }
            XCTAssertEqual(model.toolbarItems.suffix(2), [.action(.spotlight), .tool(.blur)])
            XCTAssertNotNil(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == "toolbar-action-spotlight" })
        }
        model.beginDrawing()
        let toolbar = NSHostingView(rootView: ToolbarView(model: model, availableSize: CGSize(width: 1400, height: 900)))
        let panel = NSPanel(contentRect: NSRect(x: 80, y: 80, width: 1300, height: 200), styleMask: [.borderless],
                            backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = toolbar
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        try await Task.sleep(for: .milliseconds(150))
        let spotlight = try XCTUnwrap(NativeAccessibilityNode.nodes(in: toolbar).first {
            $0.accessibilityIdentifier() == "toolbar-action-spotlight"
        })
        XCTAssertEqual(spotlight.accessibilityValue() as? String, "Off")
        XCTAssertTrue(spotlight.accessibilityPerformPress())
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(model.spotlight)
        XCTAssertNotNil(NativeAccessibilityNode.nodes(in: toolbar).first { $0.accessibilityLabel() == "Blur region" },
                        "A toolbar with added items grows instead of hiding them.")
        XCTAssertGreaterThan(toolbar.fittingSize.width, 1000)
        XCTAssertEqual(ToolbarToolLayout.identity(for: .blur, in: model.toolbarItems), .blur)
        XCTAssertEqual(ToolbarToolLayout.identity(for: .distort, in: model.toolbarItems), .cover)
        model.spotlight = false
    }

    func testSettingsTabsStayVisibleAtTheDefaultAndMinimumWidths() async throws {
        for width in [720.0, 840] {
            let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false, initialTab: .toolbar))
            let window = NSWindow(contentRect: NSRect(x: 120, y: 80, width: width, height: 700),
                                  styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.contentView = host
            window.orderFrontRegardless()
            defer { window.orderOut(nil); window.contentView = nil; window.close() }
            try await Task.sleep(for: .milliseconds(250))
            NativeAccessibilityNode.enable()
            let nodes = NativeAccessibilityNode.nodes(in: window)
            XCTAssertFalse(nodes.contains { $0.accessibilityLabel() == "more toolbar items" }, "Tabs are hidden behind » at \(width).")
            let labels = Set(nodes.compactMap { $0.accessibilityLabel() })
            for tab in ["General", "Toolbar & Shortcuts", "Pointer", "Permissions", "Help", "About"] {
                XCTAssertTrue(labels.contains(tab), "\(tab) tab is missing at \(width).")
            }
        }
    }

    func testRendersKeyMessagesForReview() async throws {
        try await withSettings { panel, host in
            @MainActor func snap(_ name: String) throws {
                host.layoutSubtreeIfNeeded()
                let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                host.cacheDisplay(in: host.bounds, to: bitmap)
                let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
                    .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
                try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
                try bitmap.representation(using: .png, properties: [:])?.write(to: output.appendingPathComponent("\(name).png"))
            }
            try await press("Spotlight key", in: host)
            try snap("key-message-recording")
            try await type("2", keyCode: 19, in: panel)
            try await Task.sleep(for: .milliseconds(400))
            try snap("key-message-moved")
            try await press("Pen key", in: host)
            try snap("key-message-recording-again")
        }
    }

    func testClickingToolsLeavesNoFocusRings() async throws {
        try await withSettings { panel, host in
            panel.makeKeyAndOrderFront(nil)
            for item in [Tool.diamond, .ellipse, .arrow, .line] {
                let frame = try tool(item, inToolbar: true, in: host).accessibilityFrame()
                let point = CGPoint(x: frame.midX, y: frame.maxY - 14)
                for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
                    panel.sendEvent(try XCTUnwrap(NSEvent.mouseEvent(with: type, location: panel.convertPoint(fromScreen: point),
                        modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                        context: nil, eventNumber: 0, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0)))
                }
                try await Task.sleep(for: .milliseconds(60))
            }
            let strip = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityIdentifier() == "toolbar-arrangement"
            }).accessibilityFrame()
            let local = host.convert(panel.convertFromScreen(strip), from: nil)
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: local))
            host.cacheDisplay(in: local, to: bitmap)
            let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
                .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            try bitmap.representation(using: .png, properties: [:])?.write(to: output.appendingPathComponent("toolbar-after-clicks.png"))
            var accent = 0
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 1) {
                for y in stride(from: 0, to: bitmap.pixelsHigh, by: 1) {
                    guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else { continue }
                    if color.blueComponent > 0.6 && color.blueComponent - color.redComponent > 0.35 { accent += 1 }
                }
            }
            XCTAssertLessThan(accent, 20, "Clicking tools must not leave focus rings behind (\(accent) accent pixels).")
        }
    }

    func testDraggingStillWorksRightAfterSettingAKey() async throws {
        try await withSettings { panel, host in
            panel.makeKeyAndOrderFront(nil)
            try await press("Pen key", in: host)
            try await type("j", keyCode: 38, in: panel)
            XCTAssertEqual(model.shortcut(for: .tool(.pen))?.label, "J")
            let strip = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityIdentifier() == "toolbar-arrangement"
            }).accessibilityFrame()
            let line = try tool(.line, inToolbar: true, in: host).accessibilityFrame()
            try await drag(from: CGPoint(x: line.midX, y: line.maxY - 18), to: CGPoint(x: line.midX + 40, y: strip.minY - 150), in: panel)
            XCTAssertFalse(model.toolbarTools.contains(.line), "A drag right after setting a key still works.")
        }
    }

    func testDraggingToolsBetweenTheToolbarAndMoreTools() async throws {
        try await withSettings { panel, host in
            let strip = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
                $0.accessibilityIdentifier() == "toolbar-arrangement"
            }).accessibilityFrame()
            let star = try tool(.star, inToolbar: false, in: host).accessibilityFrame()
            try await drag(from: CGPoint(x: star.midX, y: star.midY), to: CGPoint(x: strip.maxX - 20, y: strip.midY), in: panel)
            XCTAssertEqual(model.toolbarTools.last, .star, "Dropping at the end of the toolbar adds the tool there.")

            let pen = try tool(.pen, inToolbar: true, in: host).accessibilityFrame()
            let first = try tool(.select, inToolbar: true, in: host).accessibilityFrame()
            try await drag(from: CGPoint(x: pen.midX, y: pen.midY), to: CGPoint(x: first.minX + 4, y: first.midY), in: panel)
            XCTAssertEqual(model.toolbarTools.first, .pen, "Dropping on the left half of a tool puts it before that tool.")

            let moved = try tool(.star, inToolbar: true, in: host).accessibilityFrame()
            try await drag(from: CGPoint(x: moved.midX, y: moved.midY), to: CGPoint(x: strip.midX, y: strip.minY - 160), in: panel)
            XCTAssertFalse(model.toolbarTools.contains(.star), "Dragging a tool out of the toolbar removes it.")
            XCTAssertTrue(model.toolbarTools.contains(.select))
        }
    }

    // MARK: Canvas keys, sizes, shared style and tips

    func testCanvasKeysChooseShiftToolsAndPressToolbarButtons() throws {
        let source = try PNGExporter.image(elements: [], size: CGSize(width: 400, height: 300), background: .white)
        let editor = NativeCanvasFixture(image: source, defaults: defaults)
        defer { editor.close() }
        let canvasModel = editor.model
        XCTAssertEqual(canvasModel.bindShortcut(LocalShortcut(key: "d", shift: true), to: .tool(.triangle)), .assigned(displaced: nil))
        XCTAssertEqual(canvasModel.bindShortcut(LocalShortcut(key: "e"), to: .action(.spotlight)), .assigned(displaced: nil))
        XCTAssertEqual(canvasModel.bindShortcut(LocalShortcut(key: "j"), to: .action(.smartDraw)), .assigned(displaced: nil))
        func key(_ characters: String, _ code: UInt16, _ flags: NSEvent.ModifierFlags = []) throws {
            let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0,
                windowNumber: editor.session.panel.windowNumber, context: nil, characters: characters,
                charactersIgnoringModifiers: characters, isARepeat: false, keyCode: code))
            editor.session.canvas.keyDown(with: event)
        }
        try key("D", 2, .shift)
        XCTAssertEqual(canvasModel.tool, .triangle)
        try key("d", 2)
        XCTAssertEqual(canvasModel.tool, .database)
        try key("Q", 12, .shift)
        XCTAssertEqual(canvasModel.tool, .queue, "Shift without its own key keeps the plain key's meaning.")
        try key("e", 14)
        XCTAssertTrue(canvasModel.spotlight)
        try key("e", 14)
        XCTAssertFalse(canvasModel.spotlight)
        let smart = canvasModel.smartDraw
        try key("j", 38)
        XCTAssertEqual(canvasModel.smartDraw, !smart)
        try key("R", 15, .shift)
        XCTAssertEqual(canvasModel.tool, .queue, "Ink letters never choose tools.")
    }

    func testNewProfilesStartWithAGradientAndSavedProfilesKeepTheirColors() throws {
        XCTAssertNil(defaults.data(forKey: "workspace-preferences-v1"), "Nothing is saved before the first change.")
        model.choose(.rectangle)
        XCTAssertEqual(model.style.strokeGradient, GradientPreset.sunset.gradient)
        model.choose(.highlighter)
        XCTAssertEqual(model.style, .defaults(for: .highlighter))
        model.applyStyle { $0.width = 6 }
        let saved = try WorkspacePreferences.read(try XCTUnwrap(defaults.data(forKey: "workspace-preferences-v1")))
        XCTAssertEqual(saved.toolStyles[Tool.pen.rawValue]?.strokeGradient, GradientPreset.sunset.gradient,
                       "The first change saves the first-use gradients with it.")

        let returningSuite = "toolbar-shortcuts-returning-\(UUID())"
        let returningDefaults = try XCTUnwrap(UserDefaults(suiteName: returningSuite))
        defer { returningDefaults.removePersistentDomain(forName: returningSuite) }
        let existing = try WorkspacePreferences().data()
        returningDefaults.set(existing, forKey: "workspace-preferences-v1")
        let returning = WorkspaceModel(defaults: returningDefaults)
        defer { returning.closeAll() }
        returning.choose(.rectangle)
        XCTAssertEqual(returning.style, .defaults(for: .rectangle), "An existing profile keeps solid coral.")
        let kept = try WorkspacePreferences.read(try XCTUnwrap(returningDefaults.data(forKey: "workspace-preferences-v1")))
        XCTAssertTrue(kept.toolStyles.isEmpty, "Opening an existing profile adds no gradient.")
    }

    func testSharedStyleIsOffByDefaultAndCopiesOnlyChangesWhenOn() throws {
        // An existing profile with no tool styles, so every tool starts from its solid defaults.
        model.closeAll()
        defaults.set(try WorkspacePreferences().data(), forKey: "workspace-preferences-v1")
        model = WorkspaceModel(defaults: defaults)
        model.tool = .pen
        model.style = .defaults(for: .pen)
        model.applyStyle { $0.setPaintColor(.blue, fill: false) }
        XCTAssertNil(model.preferences.toolStyles[Tool.rectangle.rawValue], "Sharing is off by default.")
        XCTAssertFalse(model.sharesToolStyles)

        model.setShareToolStyles(true)
        model.applyStyle { $0.width = 9 }
        let rectangle = try XCTUnwrap(model.preferences.toolStyles[Tool.rectangle.rawValue])
        XCTAssertEqual(rectangle.width, 9)
        XCTAssertEqual(rectangle.color, Style.defaults(for: .rectangle).color, "Only what changed is shared.")
        model.applyStyle { $0.setPaintColor(RGBA(0.28, 0.69, 0.4), fill: false) }
        for tool in [Tool.rectangle, .highlighter, .text, .arrow, .star] {
            XCTAssertEqual(model.preferences.toolStyles[tool.rawValue]?.color, RGBA(0.28, 0.69, 0.4), tool.rawValue)
        }
        for tool in [Tool.blur, .eraser, .image, .cover, .select] {
            XCTAssertNil(model.preferences.toolStyles[tool.rawValue], "\(tool.rawValue) keeps its own settings.")
        }
        let reloaded = WorkspaceModel(defaults: defaults)
        defer { reloaded.closeAll() }
        XCTAssertTrue(reloaded.sharesToolStyles)
        XCTAssertEqual(reloaded.preferences.toolStyles[Tool.star.rawValue]?.width, 9)

        let child = WorkspaceModel(defaults: defaults)
        defer { child.closeAll() }
        child.connectSettingsOwner(model)
        child.tool = .rectangle
        child.style = model.preferences.toolStyles[Tool.rectangle.rawValue] ?? .defaults(for: .rectangle)
        child.applyStyle { $0.pattern = .dashed }
        XCTAssertEqual(model.preferences.toolStyles[Tool.pen.rawValue]?.pattern, .dashed,
                       "A second display's change reaches every tool in the shared settings.")

        model.setShareToolStyles(false)
        model.tool = .pen
        model.style = model.preferences.toolStyles[Tool.pen.rawValue] ?? .defaults(for: .pen)
        model.applyStyle { $0.width = 2 }
        XCTAssertEqual(model.preferences.toolStyles[Tool.rectangle.rawValue]?.width, 9)
    }

    func testMinimalToolbarIsSmallerInBothDirections() throws {
        func size(_ vertical: Bool) -> CGSize {
            model.setToolbarOrientation(vertical ? .vertical : .horizontal)
            let host = NSHostingController(rootView: ToolbarView(model: model, availableSize: CGSize(width: 1200, height: 900)))
            host.view.layoutSubtreeIfNeeded()
            return host.view.fittingSize
        }
        model.setQuickPreferencesExpanded(false)
        let expanded = size(false), expandedVertical = size(true)
        model.setToolbarSize(.minimal)
        XCTAssertEqual(model.preferences.toolbarSize, .minimal)
        let minimal = size(false), minimalVertical = size(true)
        XCTAssertEqual(minimal.width, ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools, available: 1200,
                                                                        propertiesVisible: false, size: .minimal), accuracy: 1)
        XCTAssertLessThan(minimal.width, expanded.width - 150)
        XCTAssertLessThanOrEqual(minimal.height, 56)
        XCTAssertLessThan(minimal.height, expanded.height)
        XCTAssertEqual(minimalVertical.width, 50, accuracy: 1)
        XCTAssertLessThan(minimalVertical.width, expandedVertical.width)
        model.setToolbarSize(.expanded)
        XCTAssertNil(model.preferences.toolbarSize, "Expanded is the default and is not stored.")
    }

    func testTipsSayHowToTurnThemOff() {
        let tips = WorkspaceHint.text(model: model)
        XCTAssertTrue(tips.hasSuffix(WorkspaceHint.footer))
        XCTAssertTrue(tips.contains("Settings > General"))
        let styled = WorkspaceHint.attributed(tips)
        let range = (tips as NSString).range(of: WorkspaceHint.footer)
        let color = styled.attribute(.foregroundColor, at: range.location, effectiveRange: nil) as? NSColor
        XCTAssertLessThan(color?.alphaComponent ?? 1, 1, "The footer is quieter than the tips.")
    }

    func testRendersTheWholeTabAndMinimalToolbarForReview() throws {
        let output = Bundle(for: Self.self).bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("UIReview")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        func render<V: View>(_ content: V, size: CGSize?, name: String, dark: Bool) throws {
            let controller = NSHostingController(rootView: content.environment(\.colorScheme, dark ? .dark : .light))
            let view = controller.view
            view.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            let panel = NSPanel(contentRect: NSRect(origin: .zero, size: size ?? CGSize(width: 1200, height: 800)),
                                styleMask: [.borderless], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.appearance = view.appearance
            panel.backgroundColor = dark ? .black : .white
            panel.contentViewController = controller
            panel.orderFrontRegardless()
            defer { panel.orderOut(nil); panel.contentViewController = nil; panel.close() }
            for _ in 0..<4 {
                view.setFrameSize(size ?? view.fittingSize)
                view.layoutSubtreeIfNeeded()
                RunLoop.current.run(until: Date().addingTimeInterval(0.05))
            }
            let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
            view.cacheDisplay(in: view.bounds, to: bitmap)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                .write(to: output.appendingPathComponent("\(name)-\(dark ? "dark" : "light").png"))
        }
        try model.bindShortcut(XCTUnwrap(LocalShortcut(key: "s", shift: true)), to: .tool(.star))
        try model.bindShortcut(XCTUnwrap(LocalShortcut(key: "l")), to: .action(.spotlight))
        model.configureToolbar(items: model.toolbarItems + [.tool(.star), .tool(.blur), .action(.spotlight), .action(.cursorHalo)])
        model.spotlight = true
        defer { model.spotlight = false }
        for dark in [false, true] {
            try render(ToolbarShortcutSettings(model: model).background(Color(nsColor: .windowBackgroundColor)),
                       size: CGSize(width: 800, height: 1720), name: "toolbar-shortcuts-full", dark: dark)
            model.setToolbarSize(.minimal)
            model.setToolbarOrientation(.horizontal)
            model.setInspectorPinned(false)
            model.setQuickPreferencesExpanded(true)
            try render(ToolbarView(model: model), size: nil, name: "toolbar-minimal-quick", dark: dark)
            model.setQuickPreferencesExpanded(false)
            try render(ToolbarView(model: model), size: nil, name: "toolbar-minimal", dark: dark)
            model.setToolbarOrientation(.vertical)
            try render(ToolbarView(model: model, availableSize: CGSize(width: 900, height: 900)), size: nil,
                       name: "toolbar-minimal-vertical", dark: dark)
            model.setToolbarOrientation(.horizontal)
            model.setToolbarSize(.expanded)
            try render(ToolbarView(model: model), size: nil, name: "toolbar-expanded", dark: dark)
        }
    }

    // MARK: Helpers

    private func withSettings(_ body: (NSPanel, NSView) async throws -> Void) async throws {
        let host = NSHostingView(rootView: CustomizationView(model: model, showDone: false, initialTab: .toolbar))
        let panel = NSPanel(contentRect: NSRect(x: 120, y: 80, width: 840, height: 760),
                            styleMask: [.titled, .resizable, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.makeKeyAndOrderFront(nil)
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        NativeAccessibilityNode.enable()
        try await Task.sleep(for: .milliseconds(200))
        host.layoutSubtreeIfNeeded()
        try await body(panel, host)
    }

    private func text(in host: NSView) -> String {
        NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
    }

    private func tool(_ tool: Tool, inToolbar: Bool, in host: NSView) throws -> NativeAccessibilityNode {
        let identifier = (inToolbar ? "toolbar-tool-" : "more-tool-") + tool.rawValue
        return try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == identifier }, identifier)
    }

    private func node(label: String, value: String?, in host: NSView) throws -> NativeAccessibilityNode {
        try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first {
            $0.accessibilityLabel() == label && (value == nil || $0.accessibilityValue() as? String == value)
        }, "\(label) \(value ?? "")")
    }

    private func press(_ label: String, in host: NSView) async throws {
        let target = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityLabel() == label }, label)
        XCTAssertTrue(target.accessibilityPerformPress(), label)
        try await Task.sleep(for: .milliseconds(150))
    }

    private func pressIdentifier(_ identifier: String, in host: NSView) async throws {
        let target = try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first { $0.accessibilityIdentifier() == identifier },
                                   identifier)
        XCTAssertTrue(target.accessibilityPerformPress(), identifier)
        try await Task.sleep(for: .milliseconds(150))
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

    private func type(_ characters: String, keyCode: UInt16, flags: NSEvent.ModifierFlags = [], in panel: NSPanel) async throws {
        let event = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags,
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber, context: nil,
            characters: characters, charactersIgnoringModifiers: characters, isARepeat: false, keyCode: keyCode))
        let responder = try XCTUnwrap(panel.firstResponder)
        responder.keyDown(with: event)
        try await Task.sleep(for: .milliseconds(150))
    }

    private func drag(from start: CGPoint, to end: CGPoint, in panel: NSPanel) async throws {
        // SwiftUI gestures need a key window; the nonactivating panel becomes key without activating the app.
        panel.makeKeyAndOrderFront(nil)
        func send(_ type: NSEvent.EventType, _ point: CGPoint) throws {
            let event = try XCTUnwrap(NSEvent.mouseEvent(with: type, location: panel.convertPoint(fromScreen: point),
                modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                context: nil, eventNumber: 0, clickCount: 1, pressure: type == .leftMouseUp ? 0 : 1))
            panel.sendEvent(event)
        }
        try send(.leftMouseDown, start)
        for step in 1...14 {
            let t = CGFloat(step) / 14
            try send(.leftMouseDragged, CGPoint(x: start.x + (end.x - start.x) * t, y: start.y + (end.y - start.y) * t))
            try await Task.sleep(for: .milliseconds(30))
        }
        try await Task.sleep(for: .milliseconds(80))
        try send(.leftMouseDragged, end)
        try await Task.sleep(for: .milliseconds(80))
        try send(.leftMouseUp, end)
        try await Task.sleep(for: .milliseconds(450))
    }
}
