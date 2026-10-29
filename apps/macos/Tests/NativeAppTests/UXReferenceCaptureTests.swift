import AppKit
import ServiceManagement
import SwiftUI
import XCTest
import AnnotationCore
import MotionCore
import NativePlatform
import PresentationRendering
@testable import AnnotatorApp

/// Writes the macOS UX reference that the Windows edition copies (specification 013, WIN-PAR-06).
///
/// Run on a Mac with:
/// `TEST_RUNNER_ANNOTATOR_CAPTURE_UX_REFERENCE=1 bash tools/macos/build-signed-dev.sh test -only-testing:NativeAppTests/UXReferenceCaptureTests`
/// then `python3 tools/macos/ux_reference.py pack`. Rendering is offscreen: no window appears, nothing takes
/// focus, no desktop overlay is created and no screen pixels are read.
@MainActor
final class UXReferenceCaptureTests: XCTestCase {
    private var cleanups: [() -> Void] = []

    override func setUp() async throws {
        try XCTSkipUnless(UXReferenceRecorder.isEnabled, "The UX reference is an explicit capture run.")
    }

    override func tearDown() async throws {
        for cleanup in cleanups.reversed() { cleanup() }
        cleanups = []
    }

    // MARK: Models

    /// A model with no drawing session: the toolbar and menu show the Off state, with the properties shown as by default.
    private func plainModel(_ name: String) throws -> WorkspaceModel {
        let suite = "ux-reference-\(name)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        cleanups.append { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        return model
    }

    /// The canvas size used for canvas captures. The backdrop is drawn at this size, so nothing is stretched.
    static let canvasSize = CGSize(width: 900, height: 568)

    /// A model in Draw mode through a test-only reference canvas with a fictional backdrop, for canvas
    /// captures. Its window stays transparent and off screen, so no input surface ever covers the desktop.
    private func drawingModel(_ name: String) throws -> (WorkspaceModel, OverlaySession) {
        let model = try plainModel(name)
        let session = OverlaySession(referenceImage: try Self.backdrop(Self.canvasSize), model: model,
                                     frame: CGRect(x: -40_000, y: -40_000, width: 900, height: 600))
        session.panel.preservesDesktopFrame = true
        session.panel.alphaValue = 0
        session.panel.ignoresMouseEvents = true
        session.panel.setFrame(CGRect(x: -40_000, y: -40_000, width: 900, height: 600), display: false)
        session.panel.setContentSize(Self.canvasSize)
        model.adoptReferenceSession(session)
        return (model, session)
    }

    /// A model in Draw mode with a desktop-style session and no reference image, so the toolbar, menu and
    /// Tools window show what the live app shows. The session's window is transparent, click-through and
    /// 40,000 points off screen, and the session gets no effect, spotlight, halo or zoom, so it never shows,
    /// takes input, opens another panel or starts screen capture.
    private func liveModel(_ name: String) throws -> (WorkspaceModel, OverlaySession) {
        let model = try plainModel(name)
        let session = OverlaySession(desktopFrame: CGRect(x: -40_000, y: -40_000, width: 1440, height: 900),
                                     scale: 2, model: model)
        session.panel.alphaValue = 0
        session.panel.ignoresMouseEvents = true
        model.adoptReferenceSession(session)
        XCTAssertFalse(model.hasReferenceImage)
        return (model, session)
    }

    private func use(_ model: WorkspaceModel, tool: Tool) {
        model.tool = tool
        model.applyStyle { $0 = .defaults(for: tool) }
        model.synchronize()
    }

    /// A plain, fictional backdrop: a light desk, one card with text bars and a colorful block.
    static func backdrop(_ size: CGSize) throws -> CGImage {
        let width = Int(size.width), height = Int(size.height)
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: 0, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.translateBy(x: 0, y: size.height)
        context.scaleBy(x: 1, y: -1)
        context.setFillColor(CGColor(srgbRed: 0.91, green: 0.93, blue: 0.96, alpha: 1))
        context.fill(CGRect(origin: .zero, size: size))
        let card = CGRect(x: 40, y: 36, width: size.width - 80, height: size.height - 72)
        context.setFillColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 1))
        context.addPath(CGPath(roundedRect: card, cornerWidth: 12, cornerHeight: 12, transform: nil))
        context.fillPath()
        context.setFillColor(CGColor(srgbRed: 0.80, green: 0.83, blue: 0.88, alpha: 1))
        for (index, length) in [0.42, 0.66, 0.55, 0.61, 0.38].enumerated() {
            context.fill(CGRect(x: card.minX + 32, y: card.minY + 40 + CGFloat(index) * 30,
                                width: (card.width * 0.5 - 64) * length * 1.6, height: 10))
        }
        let colors = [CGColor(srgbRed: 0.98, green: 0.45, blue: 0.35, alpha: 1),
                      CGColor(srgbRed: 0.98, green: 0.78, blue: 0.25, alpha: 1),
                      CGColor(srgbRed: 0.32, green: 0.72, blue: 0.55, alpha: 1),
                      CGColor(srgbRed: 0.32, green: 0.52, blue: 0.95, alpha: 1)]
        for (index, color) in colors.enumerated() {
            context.setFillColor(color)
            context.fill(CGRect(x: card.maxX - 300 + CGFloat(index) * 64, y: card.minY + 40, width: 52, height: 150))
        }
        return try XCTUnwrap(context.makeImage())
    }

    // MARK: Toolbar

    func test01Toolbar() async throws {
        let recorder = try UXReferenceRecorder("toolbar")
        let spec = ["002 design.md", "009", "ADR 0012", "WIN-UI-02"]
        for dark in [false, true] {
            let off = try plainModel("toolbar-off")
            use(off, tool: .pen)
            try await recorder.view(ToolbarView(model: off), state: "off", dark: dark,
                                    notes: "Before drawing starts. The mode control offers Draw.", spec: spec)

            let (model, session) = try liveModel("toolbar")
            use(model, tool: .pen)
            model.setPropertiesShown(false)
            try await recorder.view(ToolbarView(model: model), state: "draw-tools-only", dark: dark,
                                    notes: "Properties hidden: only the tools and buttons.", spec: spec)
            model.setPropertiesShown(true)
            for tool in [Tool.select, .rectangle, .diamond, .ellipse, .arrow, .line, .pen, .text, .highlighter,
                         .eraser, .blur, .pixelate, .distort, .ruler, .counter, .image, .databaseStack] {
                use(model, tool: tool)
                try await recorder.view(ToolbarView(model: model), state: "full-properties-\(tool.rawValue)", dark: dark,
                                        notes: "Properties for \(tool.label) on one row below the tools. The toolbar is as wide as its tools or the row.",
                                        spec: spec)
            }
            use(model, tool: .rectangle)
            model.applyStyle { $0.setFillEnabled(true) }
            try await recorder.view(ToolbarView(model: model), state: "full-properties-rectangle-filled", dark: dark,
                                    notes: "Rectangle with Fill on: the fill pattern control appears.", spec: spec)

            use(model, tool: .pen)
            model.interact()
            try await recorder.view(ToolbarView(model: model), state: "use-apps", dark: dark,
                                    notes: "Use apps: other apps get the clicks; the remembered tool keeps a neutral outline.",
                                    spec: spec + ["WIN-OVR-02"])
            model.adoptReferenceSession(session)

            for size in [ToolbarSize.expanded, .minimal] {
                model.setToolbarSize(size)
                for orientation in [ToolbarOrientation.horizontal, .vertical] {
                    model.setToolbarOrientation(orientation)
                    for expanded in [false, true] {
                        model.setPropertiesShown(expanded)
                        let available = orientation == .vertical ? CGSize(width: 880, height: 620) : CGSize(width: 1000, height: 900)
                        try await recorder.view(ToolbarView(model: model, availableSize: available),
                            state: "\(size.rawValue)-\(orientation.rawValue)-\(expanded ? "properties" : "tools-only")", dark: dark,
                            notes: "\(size.rawValue.capitalized) toolbar, \(orientation.rawValue), \(expanded ? "properties shown" : "properties hidden"). Pen selected.",
                            spec: spec)
                    }
                }
            }
            model.setToolbarSize(.expanded)
            model.setToolbarOrientation(.horizontal)
            model.setPropertiesShown(true)
            model.setToolbarOrientation(.vertical)
            use(model, tool: .arrow)
            try await recorder.view(ToolbarView(model: model, availableSize: CGSize(width: 880, height: 620)),
                                    state: "full-properties-vertical-arrow", dark: dark,
                                    notes: "Vertical rail with full properties beside it.", spec: spec)
            model.setToolbarOrientation(.horizontal)
            model.setToolbarSize(.minimal)
            try await recorder.view(ToolbarView(model: model), state: "minimal-full-properties-arrow", dark: dark,
                                    notes: "Minimal toolbar: full properties keep the expanded layout, drawn at the minimal button size, on one row.",
                                    spec: spec)
            model.setToolbarSize(.expanded)
            for (tool, choices) in [(Tool.rectangle, [PropertyChoice.pattern, .roughness, .fillStyle, .edges]),
                                    (.arrow, [.pattern, .roughness, .arrowType]), (.text, [.font, .alignment]), (.pen, [.pressure])] {
                use(model, tool: tool)
                if tool == .rectangle { model.applyStyle { $0.setFillEnabled(true) } }
                for choice in choices { model.setShowsAllChoices(choice, true) }
                try await recorder.view(ToolbarView(model: model, availableSize: CGSize(width: 1600, height: 900)),
                                        state: "full-properties-all-choices-\(tool.rawValue)", dark: dark,
                                        notes: "Full properties with every choice side by side (Settings > Your properties). The toolbar grows sideways to keep one row.",
                                        spec: spec)
                model.resetPropertyChoices()
            }

            use(model, tool: .pen)
            for control in [ToolbarControl.undo, .redo, .settings, .hide] { model.setToolbarControl(control, shown: false) }
            try await recorder.view(ToolbarView(model: model), state: "hidden-buttons", dark: dark,
                                    notes: "Undo, Redo, Settings and Hide turned off in Settings; their room goes back to the tools.",
                                    spec: spec + ["005 Toolbar & Shortcuts"])
            for control in ToolbarControl.allCases { model.setToolbarControl(control, shown: false) }
            try await recorder.view(ToolbarView(model: model), state: "hidden-buttons-all", dark: dark,
                                    notes: "Every toolbar button turned off: only the tools and their properties stay.",
                                    spec: spec + ["005 Toolbar & Shortcuts"])
            model.showAllToolbarControls()

            model.setToolbarCollapseDelay(5)
            model.setToolbarCollapsed(true)
            for size in [ToolbarSize.expanded, .minimal] {
                model.setToolbarSize(size)
                for orientation in [ToolbarOrientation.horizontal, .vertical] {
                    model.setToolbarOrientation(orientation)
                    for drawing in [true, false] {
                        if drawing { model.adoptReferenceSession(session) } else { model.interact() }
                        try await recorder.view(ToolbarView(model: model, availableSize: CGSize(width: 1000, height: 900)),
                            state: "collapsed-\(size.rawValue)-\(orientation.rawValue)-\(drawing ? "draw" : "apps")", dark: dark,
                            notes: "The toolbar shrunk to one button after the Shrink delay: the Annotator mark and \(drawing ? "Draw" : "Apps").",
                            spec: spec)
                    }
                }
            }
            model.adoptReferenceSession(session)
            model.setToolbarCollapsed(false)
            model.setToolbarCollapseDelay(nil)
            model.setToolbarSize(.expanded)
            model.setToolbarOrientation(.horizontal)

            use(model, tool: .arrow)
            for (name, available) in [("narrow", CGSize(width: 400, height: 600)), ("short", CGSize(width: 648, height: 230))] {
                try await recorder.view(ToolbarView(model: model, availableSize: available), state: "fit-\(name)", dark: dark,
                    notes: "Toolbar fitted into \(Int(available.width)) x \(Int(available.height)) points.", spec: spec)
            }

            let defaultItems = model.toolbarItems
            model.configureToolbar(items: defaultItems + ShortcutAction.toolbarChoices.map(ShortcutCommand.action)
                + [Tool.blur, .pixelate].map(ShortcutCommand.tool))
            use(model, tool: .pen)
            try await recorder.view(ToolbarView(model: model, availableSize: CGSize(width: 1600, height: 900)),
                                    state: "added-buttons", dark: dark,
                                    notes: "Spotlight, Cursor halo, Pointer lens, Smart draw, Share region, the Spotlight and Zoom menus, Blur and Pixelate added.",
                                    spec: spec + ["005 Toolbar & Shortcuts"])
            model.configureToolbar(items: defaultItems)

            for board in [WorkspaceModel.Board.light, .dark] {
                model.setBoard(board)
                try await recorder.view(ToolbarView(model: model), state: "board-\(board == .light ? "whiteboard" : "blackboard")",
                                        dark: dark, notes: "Board button after choosing \(board.rawValue).", spec: spec)
            }
            model.setBoard(.transparent)

            model.setShareToolStyles(true)
            use(model, tool: .rectangle)
            try await recorder.view(ToolbarView(model: model), state: "shared-style", dark: dark,
                                    notes: "Share style between tools is on: the link mark ends the properties row.", spec: spec)
            model.setShareToolStyles(false)

            use(model, tool: .blur)
            model.reportCaptureFailure(CaptureFailure.streamStopped("Synthetic reference fixture"))
            try await recorder.view(ToolbarView(model: model), state: "capture-failure", dark: dark,
                                    notes: "A capture failure warning in the toolbar; effect regions stay opaque.",
                                    spec: spec + ["WIN-CAP-03"])
            model.noteCaptureFrame()

            try addSampleScene(to: session)
            use(model, tool: .select)
            let shapes = session.document.elements.filter { $0.tool == .rectangle || $0.tool == .ellipse }
            session.document.select(Set(shapes.prefix(1).map(\.id)))
            model.synchronize()
            try await recorder.view(ToolbarView(model: model), state: "selection-single", dark: dark,
                                    notes: "One shape selected with Select.", spec: spec)
            session.document.select(Set(shapes.map(\.id)))
            model.synchronize()
            try await recorder.view(ToolbarView(model: model), state: "selection-multiple", dark: dark,
                                    notes: "Two shapes selected: shared style controls.", spec: spec)
            if let locked = session.document.elements.first(where: \.locked) {
                session.document.select([locked.id])
                model.synchronize()
                try await recorder.view(ToolbarView(model: model), state: "selection-locked", dark: dark,
                                        notes: "A locked object selected: Unlock is offered.", spec: spec)
            }
        }
    }

    // MARK: Properties and pickers

    func test02PropertiesAndPickers() async throws {
        let recorder = try UXReferenceRecorder("properties")
        let spec = ["002 design.md", "001 color and gradient paint", "WIN-UI-02"]
        for dark in [false, true] {
            let (model, _) = try liveModel("properties")
            for tool in [Tool.rectangle, .ellipse, .diamond, .arrow, .line, .pen, .highlighter, .text,
                         .blur, .pixelate, .distort, .eraser, .counter, .ruler] {
                use(model, tool: tool)
                try await recorder.view(ContextualInspector(model: model).padding(16), state: tool.rawValue, dark: dark,
                                        background: .window, width: 648,
                                        notes: "Full properties for \(tool.label), 648 points wide.", spec: spec)
            }
            use(model, tool: .rectangle)
            model.applyStyle { $0.setFillEnabled(true) }
            try await recorder.view(ContextualInspector(model: model).padding(16), state: "rectangle-filled", dark: dark,
                                    background: .window, width: 648, notes: "Fill on: fill pattern and opacity appear.", spec: spec)
            use(model, tool: .arrow)
            model.applyStyle { $0.curved = true }
            try await recorder.view(ContextualInspector(model: model).padding(16), state: "arrow-curved", dark: dark,
                                    background: .window, width: 648, notes: "Curved arrow.", spec: spec)
            use(model, tool: .pen)
            model.smartDraw = true
            try await recorder.view(ContextualInspector(model: model).padding(16), state: "pen-smart-draw", dark: dark,
                                    background: .window, width: 648, notes: "Pen with Smart draw on.", spec: spec)
            model.smartDraw = false
            use(model, tool: .diamond)
            try await recorder.view(ContextualInspector(model: model).padding(16), state: "diamond-wide", dark: dark,
                                    background: .window, width: 932, notes: "Wide layout: one row.", spec: spec)

            let pickers = try UXReferenceRecorder("pickers")
            use(model, tool: .rectangle)
            try await pickers.view(InspectorColorPalette(model: model, fill: false, done: {}), state: "color-stroke", dark: dark,
                                   background: .popover, notes: "Stroke color popover, 284 points wide. Shown in a system popover.",
                                   spec: spec)
            model.applyStyle { $0.setFillEnabled(true) }
            try await pickers.view(InspectorColorPalette(model: model, fill: true, done: {}), state: "color-fill", dark: dark,
                                   background: .popover, notes: "Fill color popover.", spec: spec)
            model.applyStyle { $0.setGradient(GradientPreset.spectrum.gradient, fill: false) }
            try await pickers.view(InspectorColorPalette(model: model, fill: false, done: {}), state: "color-gradient", dark: dark,
                                   background: .popover, notes: "Stroke with the spectrum gradient preset.", spec: spec)
            use(model, tool: .arrow)
            for start in [true, false] {
                try await pickers.view(ArrowHeadPalette(model: model, start: start, dismiss: {}),
                                       state: "arrowhead-\(start ? "start" : "end")", dark: dark, background: .popover,
                                       notes: "Arrowhead choices for the \(start ? "start" : "end").", spec: spec)
            }
            try await pickers.view(LiveZoomControls(model: model, compact: true).padding(12), state: "zoom-compact", dark: dark,
                                   background: .window, notes: "Compact zoom controls from the toolbar.", spec: spec + ["009"])
            try await pickers.view(LiveZoomControls(model: model, compact: false).padding(12), state: "zoom-full", dark: dark,
                                   background: .window, notes: "Zoom controls in full properties.", spec: spec + ["009"])
        }
    }

    // MARK: Menu bar

    func test03MenuBar() async throws {
        let recorder = try UXReferenceRecorder("menu")
        let spec = ["000 product spec (menu-bar popover)", "WIN-UI-01"]
        // The app caps the menu at the screen's height less 60 points; a usual screen shows all of it without scrolling.
        let menuHeight: CGFloat = 900
        for dark in [false, true] {
            let off = try plainModel("menu-off")
            off.canCheckForUpdates = true
            try await recorder.view(MenuBarPopoverView(model: off, developerPreview: false, perform: { _ in }, maximumHeight: menuHeight, toolsVisible: { false }),
                                    state: "tools-hidden", dark: dark, background: .popover,
                                    notes: "Release build, drawings and tools hidden.", spec: spec)
            try await recorder.view(MenuBarPopoverView(model: off, developerPreview: true, perform: { _ in }, maximumHeight: menuHeight, toolsVisible: { false }),
                                    state: "developer-preview", dark: dark, background: .popover,
                                    notes: "Development build: the Developer preview badge.", spec: spec)
            off.canCheckForUpdates = false
            try await recorder.view(MenuBarPopoverView(model: off, developerPreview: true, perform: { _ in }, maximumHeight: menuHeight, toolsVisible: { false }),
                                    state: "updates-unavailable", dark: dark, background: .popover,
                                    notes: "Update checks unavailable: the reason shows under the button.", spec: spec)
            off.canCheckForUpdates = true
            off.updateStatus = AppUpdateController.status(automatic: true)
            off.updateReminderVersion = "0.1.0-beta.13"
            try await recorder.view(MenuBarPopoverView(model: off, developerPreview: false, perform: { _ in }, maximumHeight: menuHeight, toolsVisible: { false }),
                                    state: "update-available", dark: dark, background: .popover,
                                    notes: "An automatic check found an update while the person was busy: Update available and the waiting version.",
                                    spec: spec + ["WIN-SYS-03"])
            off.updateReminderVersion = nil
            let (model, _) = try liveModel("menu-draw")
            model.canCheckForUpdates = true
            try await recorder.view(MenuBarPopoverView(model: model, developerPreview: false, perform: { _ in }, maximumHeight: menuHeight, toolsVisible: { true }),
                                    state: "drawing", dark: dark, background: .popover,
                                    notes: "Tools visible, drawing.", spec: spec)
            model.interact()
            try await recorder.view(MenuBarPopoverView(model: model, developerPreview: false, perform: { _ in }, maximumHeight: menuHeight, toolsVisible: { true }),
                                    state: "using-apps", dark: dark, background: .popover,
                                    notes: "Tools visible, Use apps.", spec: spec)

            for sharing in [false, true] {
                // The status item draws a template image: black on a light menu bar, white on a dark one.
                let size = CGSize(width: 24, height: 22)
                let icon = MenuBarIcon.makeImage(color: dark ? .white : .black)
                let image = try UXReferenceRecorder.draw(width: size.width, height: size.height) { _ in
                    icon.draw(in: CGRect(x: (size.width - icon.size.width) / 2, y: (size.height - icon.size.height) / 2,
                                         width: icon.size.width, height: icon.size.height),
                              from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
                    if sharing {
                        // Same frame as AppDelegate.configureMenuBarButton in the flipped status button:
                        // 6 points, 1 point from the right and bottom edges.
                        let dot = SharingStatusDot(frame: CGRect(x: size.width - 7, y: size.height - 7, width: 6, height: 6))
                        NSGraphicsContext.current?.cgContext.saveGState()
                        NSGraphicsContext.current?.cgContext.translateBy(x: dot.frame.minX, y: dot.frame.minY)
                        dot.draw(dot.bounds)
                        NSGraphicsContext.current?.cgContext.restoreGState()
                    }
                }
                let button = NSStatusBarButton(frame: CGRect(origin: .zero, size: size))
                AppDelegate.configureMenuBarButton(button, sharingActive: sharing)
                try recorder.image(image, state: "status-item\(sharing ? "-sharing" : "")", dark: dark,
                                   notes: "Menu-bar icon. The template glyph is black on a light menu bar and white on a dark one\(sharing ? "; the red dot shows while region sharing is on" : ""). The accessibility tree is the app's configured status button.",
                                   spec: spec + ["012"], accessibility: button)
            }
        }
    }

    // MARK: Settings

    func test04Settings() async throws {
        let recorder = try UXReferenceRecorder("settings")
        let spec = ["005", "ADR 0012", "WIN-UI-03"]
        for dark in [false, true] {
            let model = try plainModel("settings")
            model.loginItemController = LoginItemController(service: LoginItemService(
                status: { .notRegistered }, register: {}, unregister: {}, openSettings: {}), report: { _ in })
            for tab in CustomizationTab.allCases {
                // The Pointer tab's preview plays one click when it appears; let it finish before capturing.
                let settle: Duration = tab == .pointer ? .seconds(1.5) : .milliseconds(180)
                try await recorder.view(CustomizationView(model: model, showDone: false, initialTab: tab),
                                        state: "tab-\(tab.rawValue)", dark: dark, background: .window,
                                        notes: "Settings, \(tab.rawValue) tab, at its default size. Content below the fold scrolls.",
                                        spec: spec, settle: settle)
                // Toolbar & Shortcuts is the longest tab.
                let full: CGFloat = tab == .toolbar ? 2400 : 1500
                try await recorder.view(CustomizationView(model: model, showDone: false, initialTab: tab),
                                        state: "tab-\(tab.rawValue)-full", dark: dark, background: .window, height: full,
                                        notes: "Settings, \(tab.rawValue) tab, stretched to \(Int(full)) points so the whole scrolling content shows.",
                                        spec: spec, settle: settle)
            }
            let states: [(String, SMAppService.Status)] = [("off", .notRegistered), ("enabled", .enabled),
                                                           ("requires-approval", .requiresApproval), ("not-found", .notFound)]
            for (name, status) in states {
                let controller = LoginItemController(service: LoginItemService(
                    status: { status }, register: {}, unregister: {}, openSettings: {}), report: { _ in })
                try await recorder.view(LoginItemSettings(controller: controller).padding(16), state: "launch-at-login-\(name)",
                                        dark: dark, background: .window, width: 620,
                                        notes: "Launch at login, \(name). Windows maps these to the startup task states (WIN-SYS-01).",
                                        spec: spec + ["005 launch-at-login.md", "WIN-SYS-01"])
            }
        }
    }

    // MARK: Tutorial

    func test05Tutorial() async throws {
        let recorder = try UXReferenceRecorder("tutorial")
        let spec = ["005 guided first use", "WIN-UI-04"]
        for dark in [false, true] {
            let model = try plainModel("tutorial")
            let tutorial = TutorialController(model: model)
            tutorial.reducesMotion = { true }
            cleanups.append { tutorial.shutdown() }
            let card = TutorialCardView(tutorial: tutorial, model: model, preferences: tutorial.preferences,
                                        metrics: model.usageMetrics)
            tutorial.showReview(.welcome)
            try await recorder.view(card, state: "welcome", dark: dark,
                                    notes: "Welcome card. It shows on launch until the tutorial is finished or Show on launch is off.",
                                    spec: spec)
            for step in TutorialStep.allCases {
                tutorial.showReview(.coach, step: step)
                try await recorder.view(card, state: "coach-\(step.rawValue)", dark: dark,
                                        notes: "Coach card, step \(step.index + 1) of \(TutorialStep.allCases.count): the first goal is current and nothing is done.",
                                        spec: spec)
            }
            tutorial.showReview(.coach, step: .arrow, goalsDone: 1)
            try await recorder.view(card, state: "coach-arrow-curved", dark: dark,
                                    notes: "Arrow step: the Arrow is picked; Turn on Curved is the current goal.", spec: spec)
            tutorial.showReview(.coach, step: .shape, stepDone: true)
            try await recorder.view(card, state: "coach-shape-done", dark: dark,
                                    notes: "Shape step done: the title's check shows during the \(TutorialMotion.doneBeat) s pause before the next step.",
                                    spec: spec)
            tutorial.showReview(.finish)
            try await recorder.view(card, state: "finish", dark: dark,
                                    notes: "Finish card with the default shortcuts. Confetti plays on its display; it is not part of the card.",
                                    spec: spec)
            let times = (0...36).map { Double($0) / 60 }
            let frames = try await stepChangeFrames(tutorial, model: model, dark: dark, times: times)
            try recorder.sequence(frames, name: "step-change", dark: dark, times: times,
                                  notes: "From the finished shape step to the color step. Words leave upward over \(TutorialMotion.stepOutDuration) s; the next words rise \(TutorialMotion.stepInRise) pt over \(TutorialMotion.stepInDuration) s after \(TutorialMotion.stepInDelay) s; the track's leading edge moves over \(TutorialMotion.trackLeadDuration) s and the trailing edge follows over \(TutorialMotion.trackTrailDuration) s after \(TutorialMotion.trackTrailDelay) s.",
                                  spec: spec)
        }
    }

    /// The change from the shape step's finished card to the color step, drawn still at each time.
    private func stepChangeFrames(_ tutorial: TutorialController, model: WorkspaceModel, dark: Bool,
                                  times: [Double]) async throws -> [CGImage] {
        let panel = UXReferenceRecorder.panel(size: CGSize(width: TutorialLayout.width, height: 400), dark: dark)
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        let host = NSHostingView(rootView: TutorialCardView(tutorial: tutorial, model: model,
                                                            preferences: tutorial.preferences, metrics: model.usageMetrics)
            .frame(maxHeight: .infinity, alignment: .top)
            .environment(\.colorScheme, dark ? .dark : .light)
            .environment(\.controlActiveState, .key))
        host.appearance = panel.appearance
        panel.contentView = host
        panel.orderFrontRegardless()
        // Every frame has the size of the taller card, so the motion doesn't jump.
        var height: CGFloat = 0
        for step in [TutorialStep.shape, .color] {
            tutorial.showReview(.coach, step: step, stepDone: step == .shape)
            try await Task.sleep(for: .milliseconds(80))
            host.layoutSubtreeIfNeeded()
            height = max(height, host.fittingSize.height)
        }
        let size = CGSize(width: TutorialLayout.width, height: ceil(height))
        panel.setContentSize(size)
        host.setFrameSize(size)
        // Let the switch to the still review frames finish before recording.
        tutorial.showStepChangeReview(from: .shape, time: 0)
        try await Task.sleep(for: .milliseconds(400))
        var frames: [CGImage] = []
        for time in times {
            tutorial.showStepChangeReview(from: .shape, time: time)
            try await Task.sleep(for: .milliseconds(30))
            host.layoutSubtreeIfNeeded()
            frames.append(try UXReferenceRecorder.render(host))
        }
        return frames
    }

    // MARK: Windows and dialogs

    func test06WindowsAndDialogs() async throws {
        let library = try UXReferenceRecorder("tools-window")
        let dialogs = try UXReferenceRecorder("dialogs")
        let region = try UXReferenceRecorder("region-sharing")
        for dark in [false, true] {
            let (model, _) = try liveModel("windows")
            for section in ToolLibrarySection.allCases {
                model.tool = section.tools.first ?? .pen
                try await library.view(ToolLibraryPanel(model: model, section: .constant(section), dismiss: {}),
                                       state: "section-\(section.rawValue.lowercased().replacingOccurrences(of: " ", with: "-"))",
                                       dark: dark, background: .window,
                                       notes: "Tools window, \(section.rawValue), default 368-point width.",
                                       spec: ["005 shape-labels-and-library.md", "WIN-UI-05"])
            }
            try await library.view(ToolLibraryPanel(model: model, section: .constant(.shapes), dismiss: {},
                                                    maximumHeight: 570, width: 780),
                                   state: "shapes-resized", dark: dark, background: .window,
                                   notes: "Tools window resized wider: more columns.", spec: ["005 shape-labels-and-library.md"])

            for kind in FeedbackKind.allCases {
                try await dialogs.view(FeedbackView(model: model, kind: kind),
                                       state: kind == .issue ? "report-a-problem" : "share-feedback", dark: dark,
                                       background: .window, notes: "\(kind.rawValue) window.", spec: ["005 words review", "WIN-UI-05"])
            }
            try await dialogs.view(ReleaseNotesView().padding(20), state: "whats-changed", dark: dark, background: .window,
                                   width: 560, height: 640, notes: "What's changed, bundled with the app.", spec: ["WIN-UI-05"])
            try await dialogs.view(WhatsChangedView(entry: .uxReference, model: model, close: {}, animatesEntrance: false),
                                   state: "whats-changed-update", dark: dark, background: .window,
                                   width: WhatsChangedView.width, height: WhatsChangedView.height,
                                   notes: "What's changed after an update: fixed notes, Known limits closed, Enjoying Annotator? with Buy me a coffee, and Close.",
                                   spec: ["005", "WIN-UI-05"])
            try await dialogs.view(SupportReminderView(model: model, reminder: model.supportReminder).padding(16),
                                   state: "coffee-reminder", dark: dark, background: .window, width: 460,
                                   notes: "Coffee reminder.", spec: ["005"])
            for (name, shortcut) in [("show-hide", ActivationShortcut.standard), ("hide-only", .hideTools),
                                     ("live-zoom", .liveZoom), ("resume-drawing", .resumeDrawing)] {
                try await dialogs.view(ShortcutKeycaps(shortcut: shortcut).padding(8), state: "keycaps-\(name)", dark: dark,
                                       background: .window, notes: "Keycaps for the default \(name) shortcut. Windows shows Ctrl, Alt, Shift and Win as text.",
                                       spec: ["005", "WIN-INP-05"])
            }
            let errors = WorkspaceErrorController(model: model)
            if let content = errors.window.contentView,
               let label = Self.subviews(of: content).compactMap({ $0 as? NSTextField }).first(where: { $0.isSelectable }) {
                label.stringValue = CanvasError.unsupportedSchema(9).localizedDescription
                errors.window.setContentSize(CGSize(width: 460, height: 160))
                content.frame = CGRect(x: 0, y: 0, width: 460, height: 160)
                try await dialogs.nsView(content, state: "error-window", dark: dark, background: .window,
                                         notes: "Import error window, titled Annotator.", spec: ["005", "WIN-UI-05"])
            }
            let hint = WorkspaceHintView(frame: CGRect(x: 0, y: 0, width: 540, height: 130))
            hint.text = WorkspaceHint.text(model: model)
            hint.closable = true
            try await dialogs.nsView(hint, state: "canvas-hint", dark: dark,
                                     notes: "On-screen tip in the canvas corner while drawing, with the ✕ that turns tips off.", spec: ["003", "WIN-OVR-03"])

            let sharing = RegionSharingController(model: model)
            sharing.refreshDisplays()
            if let display = sharing.displays.first {
                sharing.selectDisplay(display.id)
                for preset in [ShareRegionPreset.leftHalf, .topRight] {
                    sharing.choosePreset(preset)
                    try await region.view(RegionSharingControls(sharing: sharing), state: "chooser-\(preset.rawValue)", dark: dark,
                                          background: .window, width: 960, height: 660,
                                          notes: "Share a region window at its default 960 x 660 size. The display list shows this Mac's display.",
                                          spec: ["012", "WIN-CAP-08"])
                }
                sharing.applyCustom(Rect(x: 0, y: 0, width: 12, height: 12))
                try await region.view(RegionSharingControls(sharing: sharing), state: "chooser-invalid", dark: dark,
                                      background: .window, width: 960, height: 660,
                                      notes: "A custom region smaller than 32 points: inline error.", spec: ["012"])
            }
            for fill in [true, false] {
                let outline = RegionOutlineView(frame: CGRect(x: 0, y: 0, width: 480, height: 270))
                outline.showsSelectionFill = fill
                try await region.nsView(outline, state: "outline-\(fill ? "choosing" : "live")", dark: dark,
                                        notes: fill ? "Outline while choosing: faint accent fill." : "Outline while sharing: no fill.",
                                        spec: ["012"])
            }
            let output = try SharedRegionView(region: SharingRegion(preset: .leftHalf, displayBounds: Rect(x: 0, y: 0, width: 1440, height: 900)))
            output.frame = CGRect(x: 0, y: 0, width: 720, height: 440)
            output.cover("Starting the region...", generation: 1)
            try await region.nsView(output, state: "output-covered", dark: dark,
                                    notes: "Annotator - Shared Region output while it starts: opaque until the first frame.",
                                    spec: ["012", "WIN-CAP-08"])
        }
    }

    // MARK: Canvas chrome

    /// Shapes, a connector, text, a pen stroke and a locked shape. No effect regions: in a live-style session
    /// they would start screen capture, and the conformance corpus covers effect rendering.
    private func addSampleScene(to session: OverlaySession) throws {
        var box = Element(tool: .rectangle, frame: Rect(x: 120, y: 110, width: 220, height: 130), style: .defaults(for: .rectangle))
        box.label = ShapeLabel(text: "API", followsStroke: true)
        var oval = Element(tool: .ellipse, frame: Rect(x: 420, y: 120, width: 190, height: 110), style: .defaults(for: .ellipse))
        oval.style.color = RGBA(0.20, 0.55, 0.95)
        let arrow = Element(tool: .arrow, points: [Point(345, 175), Point(415, 175)], style: .defaults(for: .arrow))
        let text = Element(tool: .text, frame: Rect(x: 120, y: 300, width: 220, height: 40), text: "Checkout flow",
                           style: .defaults(for: .text))
        let pen = Element(tool: .pen, points: stride(from: 0.0, through: 1.0, by: 0.05).map {
            Point(130 + $0 * 240, 400 + sin($0 * .pi * 2) * 22)
        }, style: .defaults(for: .pen))
        var locked = Element(tool: .diamond, frame: Rect(x: 650, y: 300, width: 120, height: 110), style: .defaults(for: .diamond))
        locked.locked = true
        for element in [box, oval, arrow, text, pen, locked] { try session.document.add(element) }
        session.document.select([])
    }

    func test07CanvasChrome() async throws {
        let recorder = try UXReferenceRecorder("canvas")
        let spec = ["001", "002", "009", "WIN-OVR"]
        for dark in [false, true] {
            let (model, session) = try drawingModel("canvas")
            try addSampleScene(to: session)
            let canvas = session.canvas
            canvas.frame = CGRect(origin: .zero, size: Self.canvasSize)
            session.panel.contentView = canvas
            XCTAssertEqual(canvas.bounds.size, Self.canvasSize)
            session.panel.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            let elements = session.document.elements
            let box = try XCTUnwrap(elements.first { $0.tool == .rectangle })
            let oval = try XCTUnwrap(elements.first { $0.tool == .ellipse })
            let arrow = try XCTUnwrap(elements.first { $0.tool == .arrow })
            let locked = try XCTUnwrap(elements.first(where: \.locked))

            func capture(_ state: String, _ notes: String) async throws {
                let notes = notes + " The Use apps button in the corner uses a system material that offscreen rendering leaves out."
                // The live overlay draws drawings as vectors. Outside a gesture this test canvas would draw them
                // from its 1x backdrop snapshot instead, so hold an empty edit open while rendering.
                let opened = !session.document.isEditing
                if opened { session.document.begin() }
                defer { if opened { session.document.cancel() } }
                canvas.needsDisplay = true
                try await Task.sleep(for: .milliseconds(120))
                let summary = session.document.elements.map { element -> [String: Any] in
                    ["tool": element.tool.rawValue, "frame": [element.frame.x, element.frame.y, element.frame.width, element.frame.height],
                     "rotation": element.rotation, "locked": element.locked,
                     "selected": session.document.selection.contains(element.id)]
                }
                canvas.refreshAccessibilitySummary()
                try recorder.image(try UXReferenceRecorder.render(canvas), state: state, dark: dark, notes: notes, spec: spec,
                                   extra: ["tool": model.tool.rawValue, "elements": summary,
                                           "backdrop": "Fictional test backdrop drawn by the capture test, not a real desktop."],
                                   accessibility: canvas)
            }
            func event(_ type: NSEvent.EventType, _ point: CGPoint, clicks: Int = 1) throws -> NSEvent {
                try XCTUnwrap(NSEvent.mouseEvent(with: type, location: canvas.convert(point, to: nil), modifierFlags: [],
                    timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: session.panel.windowNumber, context: nil,
                    eventNumber: 1, clickCount: clicks, pressure: 1))
            }

            use(model, tool: .select)
            try await capture("scene", "Draw mode with Select and nothing selected.")
            session.document.select([box.id]); model.synchronize()
            try await capture("select-shape", "One labeled rectangle selected: corner and edge handles plus the rotation handle.")
            session.document.select([box.id, oval.id]); model.synchronize()
            try await capture("select-multiple", "Two shapes selected: shared corner handles and one rotation handle.")
            session.document.select([arrow.id]); model.synchronize()
            try await capture("select-arrow", "A lone arrow selected: point handles only, no bounding box.")
            session.document.select([locked.id]); model.synchronize()
            try await capture("select-locked", "A locked shape selected: outline only.")
            session.document.select([]); model.synchronize()

            use(model, tool: .arrow)
            canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 560, y: 470)))
            canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 480, y: 300)))
            canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 424, y: 178)))
            try await capture("attach-hint", "Drawing an arrow toward the ellipse: accent halo, its anchor points and the filled dot where the end will attach.")
            canvas.mouseUp(with: try event(.leftMouseUp, CGPoint(x: 424, y: 178)))
            model.undo()

            use(model, tool: .select)
            try session.document.update(oval.id) { $0.rotation = 0.35 }
            session.document.select([oval.id]); model.synchronize()
            try await capture("select-rotated", "A rotated ellipse selected.")
            session.document.select([]); model.synchronize()

            canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 90, y: 270)))
            canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 260, y: 360)))
            canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 380, y: 450)))
            try await capture("marquee", "Dragging on empty space with Select: dashed selection rectangle.")
            canvas.mouseUp(with: try event(.leftMouseUp, CGPoint(x: 380, y: 450)))
            session.document.select([]); model.synchronize()

            use(model, tool: .eraser)
            // The path crosses the pen wave at (250, 400), where the wave meets its center line.
            canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 150, y: 350)))
            canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 250, y: 400)))
            canvas.mouseDragged(with: try event(.leftMouseDragged, CGPoint(x: 350, y: 450)))
            try await capture("eraser-preview", "Erasing across the pen stroke: it fades and shows a dashed outline until release.")
            canvas.cancelGesture()

            use(model, tool: .text)
            canvas.mouseDown(with: try event(.leftMouseDown, CGPoint(x: 480, y: 330)))
            canvas.mouseUp(with: try event(.leftMouseUp, CGPoint(x: 480, y: 330)))
            if let editor = Self.subviews(of: canvas).compactMap({ $0 as? NSTextView }).first {
                editor.insertText("New note", replacementRange: NSRange(location: NSNotFound, length: 0))
            }
            try await capture("text-editing", "Typing new text with the saved Text settings.")
            canvas.commitTextIfNeeded()

            for board in [WorkspaceModel.Board.light, .dark] {
                model.setBoard(board)
                use(model, tool: .select)
                session.document.select([box.id]); model.synchronize()
                try await capture("board-\(board == .light ? "whiteboard" : "blackboard")",
                                  "\(board.rawValue) board under the drawings, with one selection.")
            }
            model.setBoard(.transparent)
            session.panel.contentView = nil
        }
    }

    // MARK: Pointer, cursors and icons

    func test08PointerCursorsAndIcons() async throws {
        let pointer = try UXReferenceRecorder("pointer")
        let spec = ["003", "005 pointer controls", "WIN-CAP-09"]
        let backdrop = try Self.backdrop(CGSize(width: 900, height: 560))
        let spotlight = try UXReferenceRecorder.draw(width: 900, height: 560) { context in
            context.draw(backdrop, in: CGRect(x: 0, y: 0, width: 900, height: 560))
            try SpotlightRenderer.draw(in: context, bounds: CGRect(x: 0, y: 0, width: 900, height: 560),
                                       center: CGPoint(x: 600, y: 200), radius: 130, opacity: 1)
        }
        try pointer.image(spotlight, state: "spotlight", dark: false,
                          notes: "Spotlight at the default 130-point radius with its radial feather.", spec: spec)

        for dark in [false, true] {
            let shades: CGFloat = dark ? 0.16 : 0.92
            for pattern in StrokePattern.allCases {
                for glow in [0.0, 12.0] {
                    let image = try UXReferenceRecorder.draw(width: 7 * 90, height: 90) { context in
                        context.setFillColor(gray: shades, alpha: 1)
                        context.fill(CGRect(x: 0, y: 0, width: 7 * 90, height: 90))
                        for (index, shape) in CursorHighlightSettings.Shape.allCases.enumerated() {
                            var settings = CursorHighlightSettings()
                            settings.shape = shape
                            settings.borderStyle = pattern
                            settings.glowRadius = glow
                            CursorHighlightRenderer.draw(settings, center: CGPoint(x: 45 + Double(index) * 90, y: 45),
                                                         clickProgress: nil, in: context)
                        }
                    }
                    try pointer.image(image, state: "halo-\(pattern.rawValue)\(glow > 0 ? "-glow" : "")", dark: dark,
                        notes: "Cursor halo shapes left to right: \(CursorHighlightSettings.Shape.allCases.map(\.label).joined(separator: ", ")). Default size 36, border 3, yellow.",
                        spec: spec)
                }
            }
            for effect in [CursorHighlightSettings.ClickEffect.pulse, .ripple, .turn] {
                let count = Int((CursorHighlightSettings.clickDuration * 60).rounded()) + 1
                let times = (0..<count).map { Double($0) / 60 }
                let frames = try times.map { time in
                    try UXReferenceRecorder.draw(width: 140, height: 140) { context in
                        context.setFillColor(gray: shades, alpha: 1)
                        context.fill(CGRect(x: 0, y: 0, width: 140, height: 140))
                        var settings = CursorHighlightSettings()
                        settings.clickEffect = effect
                        CursorHighlightRenderer.draw(settings, center: CGPoint(x: 70, y: 70),
                                                     clickProgress: time / CursorHighlightSettings.clickDuration,
                                                     clickCenter: CGPoint(x: 70, y: 70), in: context)
                    }
                }
                try pointer.sequence(frames, name: "click-\(effect.rawValue)", dark: dark, times: times,
                                     notes: "\(effect.label) click effect over \(CursorHighlightSettings.clickDuration) s at 60 frames per second.",
                                     spec: spec)
            }
        }

        let cursors = try UXReferenceRecorder("cursors")
        var entries: [[String: Any]] = []
        func record(_ cursor: NSCursor, name: String) throws {
            let size = cursor.image.size
            let image = try UXReferenceRecorder.draw(width: size.width, height: size.height) { _ in
                cursor.image.draw(in: CGRect(origin: .zero, size: size), from: .zero, operation: .sourceOver, fraction: 1,
                                  respectFlipped: true, hints: nil)
            }
            try cursors.image(image, state: name, dark: false, notes: "Cursor image with its hot spot in points.",
                              spec: ["009 drawing cursors", "WIN-OVR-11"],
                              extra: ["hotSpot": [cursor.hotSpot.x, cursor.hotSpot.y]])
            entries.append(["name": name, "size": [size.width, size.height], "hotSpot": [cursor.hotSpot.x, cursor.hotSpot.y]])
        }
        for tool in [Tool.pen, .highlighter, .eraser, .line, .arrow, .rectangle, .ellipse, .diamond, .text, .blur] {
            for width in [2.0, 8.0, 24.0] {
                try record(ToolCursorFactory.cursor(tool: tool, width: width), name: "\(tool.rawValue)-\(Int(width))")
            }
        }
        try record(ToolCursorFactory.rotationCursor, name: "rotate")
        try UXReferenceRecorder.writeJSON(entries, to: UXReferenceRecorder.root.appendingPathComponent("cursors/cursors.json"))
    }

    // MARK: Break timer

    func test10BreakTimer() async throws {
        let recorder = try UXReferenceRecorder("break-timer")
        let spec = ["014", "WIN-PAR-03"]
        let end = Date(timeIntervalSinceReferenceDate: 800_000_000)
        func state(_ label: String, fraction: Double, phase: BreakFaceState.Phase = .running,
                   lock: BreakTimerController.LockState = .armed, controls: Bool = false,
                   notice: String? = nil, settings: BreakTimerSettings = BreakTimerSettings()) -> BreakFaceState {
            var value = BreakFaceState.preview(settings)
            value.label = label
            value.fraction = fraction
            value.phase = phase
            value.lock = lock
            value.controlsVisible = controls
            value.notice = notice
            value.endTime = settings.showEndTime && phase != .timeUp ? BreakTimerController.endTimeText(end) : nil
            return value
        }
        var unlocked = BreakTimerSettings()
        unlocked.lockWhenTouched = false
        let states: [(String, BreakFaceState, String)] = [
            ("arming", state("10:00", fraction: 1, lock: .arming(3)),
             "Just started, with locking on: the lock arms after 3 seconds; Escape cancels."),
            ("armed", state("7:42", fraction: 0.77),
             "Locking on and armed: the first key, click, scroll or move of more than 12 points locks the Mac."),
            ("controls", state("7:42", fraction: 0.77, lock: .off, controls: true, settings: unlocked),
             "Locking off: moving the pointer shows the controls for 3 seconds."),
            ("paused", state("7:42", fraction: 0.77, phase: .paused, lock: .off, controls: true, settings: unlocked),
             "Paused: the countdown dims and the ring stands still."),
            ("last-minute", state("0:42", fraction: 0.07, phase: .lastMinute),
             "The last minute: the ring and accent turn amber and breathe."),
            ("time-up", state("+1:23", fraction: 1, phase: .timeUp, lock: .off, settings: unlocked),
             "Time is up and counting over. The title reads Time's up and the ring is full and amber."),
            ("lock-failed", state("7:42", fraction: 0.77, lock: .off, controls: true,
                                  notice: BreakTimerError.lockNotConfirmed.errorDescription, settings: unlocked),
             "macOS did not confirm the lock: the timer stays, says nothing is locked and shows the controls.")
        ]
        for (name, value, notes) in states {
            try await recorder.view(BreakScreenFace(state: value), state: name, dark: true, width: 480, height: 300,
                                    notes: notes + " Shown full screen on each display; always dark. Every size scales with the display's shorter side, so this 480 by 300 capture keeps the layout of a full screen.", spec: spec,
                                    settle: .seconds(1.2))
        }
        for palette in BreakTimerSettings.Palette.allCases {
            var settings = BreakTimerSettings()
            settings.palette = palette
            try await recorder.view(BreakScreenFace(state: state("10:00", fraction: 1, settings: settings)),
                                    state: "palette-\(palette.rawValue)", dark: true, width: 320, height: 200,
                                    notes: "Moving colors, \(palette.label). The color fields drift on 22 to 37 second loops.",
                                    spec: spec, settle: .seconds(1.2))
        }
        var plain = BreakTimerSettings()
        plain.background = .plain
        plain.title = ""
        plain.showEndTime = false
        try await recorder.view(BreakScreenFace(state: state("25:00", fraction: 1, settings: plain)),
                                state: "plain-no-title", dark: true, width: 320, height: 200,
                                notes: "Plain background with no title and no end time. Blurred desktop uses the system window blur, which offscreen capture can't show.",
                                spec: spec, settle: .seconds(1.2))
    }

    // MARK: Design values

    func test09DesignValues() throws {
        let colors: [(String, NSColor)] = [
            ("windowBackgroundColor", .windowBackgroundColor), ("controlBackgroundColor", .controlBackgroundColor),
            ("underPageBackgroundColor", .underPageBackgroundColor), ("textBackgroundColor", .textBackgroundColor),
            ("labelColor", .labelColor), ("secondaryLabelColor", .secondaryLabelColor),
            ("tertiaryLabelColor", .tertiaryLabelColor), ("quaternaryLabelColor", .quaternaryLabelColor),
            ("separatorColor", .separatorColor), ("gridColor", .gridColor), ("controlColor", .controlColor),
            ("controlTextColor", .controlTextColor), ("disabledControlTextColor", .disabledControlTextColor),
            ("controlAccentColor", .controlAccentColor), ("selectedContentBackgroundColor", .selectedContentBackgroundColor),
            ("unemphasizedSelectedContentBackgroundColor", .unemphasizedSelectedContentBackgroundColor),
            ("keyboardFocusIndicatorColor", .keyboardFocusIndicatorColor), ("linkColor", .linkColor),
            ("systemRed", .systemRed), ("systemOrange", .systemOrange), ("systemYellow", .systemYellow),
            ("systemGreen", .systemGreen), ("systemBlue", .systemBlue), ("systemPink", .systemPink),
            ("systemPurple", .systemPurple), ("systemTeal", .systemTeal), ("systemGray", .systemGray)
        ]
        var resolved: [String: Any] = [:]
        for (name, appearance) in [("light", NSAppearance.Name.aqua), ("dark", .darkAqua)] {
            var table: [String: String] = [:]
            NSAppearance(named: appearance)?.performAsCurrentDrawingAppearance {
                for (key, color) in colors {
                    guard let rgb = color.usingColorSpace(.sRGB) else { continue }
                    table[key] = String(format: "#%02X%02X%02X%02X", Int((rgb.redComponent * 255).rounded()),
                                        Int((rgb.greenComponent * 255).rounded()), Int((rgb.blueComponent * 255).rounded()),
                                        Int((rgb.alphaComponent * 255).rounded()))
                }
            }
            resolved[name] = table
        }
        let styles: [(String, NSFont.TextStyle)] = [("largeTitle", .largeTitle), ("title1", .title1), ("title2", .title2),
            ("title3", .title3), ("headline", .headline), ("subheadline", .subheadline), ("body", .body),
            ("callout", .callout), ("footnote", .footnote), ("caption1", .caption1), ("caption2", .caption2)]
        let fonts = styles.map { name, style -> [String: Any] in
            let font = NSFont.preferredFont(forTextStyle: style)
            let traits = font.fontDescriptor.object(forKey: .traits) as? [NSFontDescriptor.TraitKey: Any]
            return ["style": name, "size": font.pointSize, "weight": (traits?[.weight] as? NSNumber)?.doubleValue ?? 0,
                    "family": font.familyName ?? ""]
        }
        let values: [String: Any] = [
            "note": "Resolved on this Mac. controlAccentColor follows the Mac's accent setting. One macOS point is one Windows DIP. SwiftUI .caption is caption1; .title is title1.",
            "colors": resolved, "textStyles": fonts,
            "systemFontSize": NSFont.systemFontSize, "smallSystemFontSize": NSFont.smallSystemFontSize,
            "labelFontSize": NSFont.labelFontSize,
            "accessibility": ["reduceMotion": NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                              "increaseContrast": NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast]
        ]
        try FileManager.default.createDirectory(at: UXReferenceRecorder.root, withIntermediateDirectories: true)
        try UXReferenceRecorder.writeJSON(values, to: UXReferenceRecorder.root.appendingPathComponent("design-values.json"))
    }

    static func subviews(of view: NSView) -> [NSView] {
        view.subviews + view.subviews.flatMap { subviews(of: $0) }
    }
}
