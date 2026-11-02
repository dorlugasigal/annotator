import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

/// Glass toolbar (WIN-UI-09): the system's glass behind the real toolbar while the setting is on, the solid window color
/// while it is off or Reduce transparency is on, and solid previews in Settings.
@MainActor
final class GlassToolbarTests: XCTestCase {
    private func makeModel(_ name: String) throws -> (WorkspaceModel, () -> Void) {
        let suite = "glass-toolbar-\(name)-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        return (model, { model.closeAll(); defaults.removePersistentDomain(forName: suite) })
    }

    private static func chromeViews(in view: NSView) -> [ToolbarChromeView] {
        view.subviews.flatMap { subview in
            (subview as? ToolbarChromeView).map { [$0] } ?? chromeViews(in: subview)
        }
    }

    func testBackgroundFollowsTheSettingAndReduceTransparency() {
        let chrome = ToolbarChromeView(cornerRadius: 15, glass: false)
        chrome.reducesTransparency = { false }
        chrome.frame = CGRect(x: 0, y: 0, width: 300, height: 60)
        XCTAssertFalse(chrome.showsGlass, "Off at first: the solid window color")
        chrome.glass = true
        XCTAssertTrue(chrome.showsGlass)
        var reduced = true
        chrome.reducesTransparency = { reduced }
        XCTAssertFalse(chrome.showsGlass, "Reduce transparency keeps the toolbar solid")
        reduced = false
        NSWorkspace.shared.notificationCenter.post(name: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
                                                   object: NSWorkspace.shared)
        XCTAssertTrue(chrome.showsGlass, "The glass comes back as soon as Reduce transparency is off")
        XCTAssertFalse(chrome.isAccessibilityElement())
        XCTAssertEqual(chrome.accessibilityChildren()?.count ?? 0, 0, "Screen readers don't see the glass")
        chrome.glass = false
        XCTAssertFalse(chrome.showsGlass)
        XCTAssertTrue(chrome.subviews.isEmpty)
    }

    func testGlassFollowsTheToolbarShape() throws {
        let chrome = ToolbarChromeView(cornerRadius: 15, glass: true)
        chrome.reducesTransparency = { false }
        chrome.frame = CGRect(x: 0, y: 0, width: 112, height: 44)
        let backdrop = try XCTUnwrap(chrome.subviews.first)
        XCTAssertEqual(backdrop.frame, chrome.bounds, "The glass keeps the toolbar's size")
        if #available(macOS 26, *) {
            let glass = try XCTUnwrap(backdrop.subviews.first as? NSGlassEffectView, "Liquid Glass on macOS 26 and later")
            XCTAssertEqual(glass.style, .regular)
            XCTAssertEqual(glass.frame, backdrop.bounds)
            XCTAssertEqual(glass.cornerRadius, 15)
            XCTAssertEqual(backdrop.layer?.cornerRadius, 15)
            XCTAssertEqual(backdrop.layer?.masksToBounds, true, "The glass's edge light stays inside the toolbar")
            chrome.cornerRadius = .greatestFiniteMagnitude
            XCTAssertEqual(glass.cornerRadius, 22, "The one-button toolbar is a capsule")
            XCTAssertEqual(backdrop.layer?.cornerRadius, 22)

            chrome.appearance = NSAppearance(named: .aqua)
            let light = try XCTUnwrap(glass.contentView?.layer?.backgroundColor, "A light body keeps labels readable")
            XCTAssertEqual(light.alpha, ToolbarGlass.bodyOpacity, accuracy: 0.001)
            chrome.appearance = NSAppearance(named: .darkAqua)
            let dark = try XCTUnwrap(glass.contentView?.layer?.backgroundColor)
            XCTAssertNotEqual(light, dark, "The body follows Light and Dark")
        } else {
            let blur = try XCTUnwrap(backdrop as? NSVisualEffectView, "A behind-window blur on macOS 14 and 15")
            XCTAssertEqual(blur.blendingMode, .behindWindow)
            XCTAssertEqual(blur.state, .active, "It stays clear while another app is in front")
            XCTAssertNotNil(blur.maskImage)
        }
    }

    func testRealToolbarUsesGlassOnlyWhenTurnedOn() async throws {
        let (model, cleanup) = try makeModel("real")
        model.setVisibilityAnimation(false)
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
        content.chrome.reducesTransparency = { false }
        XCTAssertFalse(content.chrome.showsGlass, "Glass toolbar is off at first")
        let panel = try XCTUnwrap(app.toolbarWindow as? InspectorPanel)
        XCTAssertTrue(panel.hasShadow, "The solid toolbar keeps its shadow")
        XCTAssertFalse(panel.isKeyWindow)
        XCTAssertFalse(panel._hasActiveAppearanceIgnoringKeyFocus(), "The solid toolbar follows the window's own state")
        model.setGlass(true)
        XCTAssertTrue(content.chrome.showsGlass)
        XCTAssertEqual(content.chrome.subviews.first?.frame, content.chrome.bounds, "The glass fills the toolbar")
        XCTAssertFalse(panel.hasShadow, "The window shadow would draw a dark line around the glass")
        content.chrome.reducesTransparency = { true }
        XCTAssertTrue(panel.hasShadow, "Reduce transparency brings the solid toolbar and its shadow back")
        content.chrome.reducesTransparency = { false }
        XCTAssertFalse(panel.hasShadow)
        if #available(macOS 26, *) {
            XCTAssertTrue(panel._hasActiveAppearanceIgnoringKeyFocus(), "The glass keeps its active look without the keyboard")
            XCTAssertFalse(panel.isKeyWindow, "The toolbar doesn't take the keyboard for it")
        }

        model.setToolbarOrientation(.vertical)
        model.setPropertiesShown(true)
        try await Task.sleep(for: .milliseconds(300))
        let surfaces = Self.chromeViews(in: content.host)
        XCTAssertGreaterThanOrEqual(surfaces.count, 2, "The tool rail and the properties have their own surfaces")
        XCTAssertTrue(surfaces.allSatisfy(\.glass))

        model.setGlass(false)
        XCTAssertFalse(content.chrome.showsGlass)
        XCTAssertFalse(panel._hasActiveAppearanceIgnoringKeyFocus())
        XCTAssertTrue(panel.hasShadow, "The shadow comes back with the solid toolbar")
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertTrue(Self.chromeViews(in: content.host).isEmpty, "Without glass, the vertical surfaces are solid again")
    }

    /// A toolbar in the toolbar window (no chrome of its own) with a chosen tool and buttons that are on.
    private func showOnStates(_ model: WorkspaceModel, reduceTransparency: Bool = false) throws -> (NSPanel, NSView) {
        model.configureToolbar(items: WorkspacePreferences.defaultToolbar.map(ShortcutCommand.tool)
                               + [.action(.smartDraw), .action(.focusMenu)])
        model.setPropertiesShown(true)
        model.beginDrawing()
        model.tool = .pen
        model.smartDraw = true
        model.cursorHalo = true
        let panel = NSPanel(contentRect: CGRect(x: -10_000, y: -10_000, width: 1300, height: 400),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.appearance = NSAppearance(named: .aqua)
        let toolbar = ToolbarView(model: model, availableSize: CGSize(width: 1300, height: 900), drawsChrome: false)
        let host = NSHostingView(rootView: AnyView(toolbar.environment(\._accessibilityReduceTransparency, reduceTransparency)))
        panel.contentView = host
        host.layoutSubtreeIfNeeded()
        host.displayIfNeeded()
        return (panel, host)
    }

    /// How opaque the toolbar's own drawing is 3 points inside a control's leading edge, where only its background is.
    private func backgroundAlpha(of node: NativeAccessibilityNode, in host: NSView) throws -> CGFloat {
        let window = try XCTUnwrap(host.window)
        let frame = host.convert(window.convertFromScreen(node.accessibilityFrame()), from: nil)
        let point = CGPoint(x: frame.minX + 3, y: frame.midY)
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        let fromTop = host.isFlipped ? point.y : host.bounds.height - point.y
        let x = Int((point.x / host.bounds.width * CGFloat(bitmap.pixelsWide)).rounded(.down))
        let y = Int((fromTop / host.bounds.height * CGFloat(bitmap.pixelsHigh)).rounded(.down))
        return try XCTUnwrap(bitmap.colorAt(x: x, y: y)).alphaComponent
    }

    private func control(_ host: NSView, where match: (NativeAccessibilityNode) -> Bool) throws -> NativeAccessibilityNode {
        try XCTUnwrap(NativeAccessibilityNode.nodes(in: host).first(where: match))
    }

    /// On glass, the chosen tool and anything that's on sit on raised lenses. The solid toolbar and Reduce transparency
    /// keep today's light, flat tints.
    func testChosenAndOnButtonsSitOnLensesOnlyOnGlass() throws {
        NativeAccessibilityNode.enable()
        for (glass, reduce) in [(true, false), (false, false), (true, true)] {
            let (model, cleanup) = try makeModel("lenses")
            defer { cleanup() }
            model.setGlass(glass)
            let (panel, host) = try showOnStates(model, reduceTransparency: reduce)
            defer { panel.contentView = nil; panel.close() }
            let lenses = glass && !reduce
            let smartDraw = try control(host) { $0.accessibilityIdentifier() == "toolbar-action-smartDraw" }
            let halo = try control(host) { $0.accessibilityIdentifier() == "stop-active-cursorHalo" }
            let pen = try control(host) { $0.accessibilityRole() == .button && $0.accessibilityLabel() == "Pen" }
            let state = "glass \(glass), Reduce transparency \(reduce)"
            if lenses {
                XCTAssertGreaterThan(try backgroundAlpha(of: smartDraw, in: host), 0.5, "Smart draw is on a lens: \(state)")
                XCTAssertGreaterThan(try backgroundAlpha(of: halo, in: host), 0.5, "The Cursor halo chip is on a lens: \(state)")
                XCTAssertGreaterThan(try backgroundAlpha(of: pen, in: host), 0.5, "The chosen tool is on a lens: \(state)")
            } else {
                XCTAssertLessThan(try backgroundAlpha(of: smartDraw, in: host), 0.3, "Smart draw keeps its flat tint: \(state)")
                XCTAssertLessThan(try backgroundAlpha(of: halo, in: host), 0.3, "The chip keeps its flat tint: \(state)")
                XCTAssertLessThan(try backgroundAlpha(of: pen, in: host), 0.3, "The chosen tool keeps its flat tint: \(state)")
            }
        }
    }

    /// Glass changes only the look: names, values, identifiers and their order stay the same, as on Windows (WIN-UI-09).
    func testGlassKeepsTheAccessibilityTree() throws {
        NativeAccessibilityNode.enable()
        func tree(glass: Bool) throws -> [String] {
            let (model, cleanup) = try makeModel("tree")
            defer { cleanup() }
            model.setGlass(glass)
            let (panel, host) = try showOnStates(model)
            defer { panel.contentView = nil; panel.close() }
            return NativeAccessibilityNode.nodes(in: host).compactMap { node in
                guard let role = node.accessibilityRole() else { return nil }
                return [role.rawValue, node.accessibilityLabel() ?? "", node.accessibilityIdentifier() ?? "",
                        node.accessibilityValue().map { String(describing: $0) } ?? ""].joined(separator: " | ")
            }
        }
        let solid = try tree(glass: false)
        XCTAssertTrue(solid.contains { $0.contains("stop-active-cursorHalo") }, "The toolbar shows its on states")
        XCTAssertEqual(try tree(glass: true), solid)
    }

    // MARK: Pictures and animations

    /// How opaque `image`, a picture of a view of `size`, is at `point` (y up from the bottom-left).
    private static func alpha(of image: CGImage, at point: CGPoint, in size: CGSize) throws -> CGFloat {
        let bitmap = NSBitmapImageRep(cgImage: image)
        let x = Int((point.x / size.width * CGFloat(bitmap.pixelsWide)).rounded(.down))
        let y = Int(((size.height - point.y) / size.height * CGFloat(bitmap.pixelsHigh)).rounded(.down))
        return try XCTUnwrap(bitmap.colorAt(x: x, y: y)).alphaComponent
    }

    private func showToolbar(_ app: AppDelegate, _ model: WorkspaceModel) async throws -> (NSPanel, ToolbarContentView) {
        app.activateTools()
        for _ in 0..<100 where model.visibilityAnimating { try await Task.sleep(for: .milliseconds(20)) }
        try await Task.sleep(for: .milliseconds(200))
        let toolbar = try XCTUnwrap(app.toolbarWindow)
        let content = try XCTUnwrap(toolbar.contentView as? ToolbarContentView)
        content.chrome.reducesTransparency = { false }
        return (toolbar, content)
    }

    /// A picture (`cacheDisplay`) can't hold live glass: it came out as a dark plate. Pictures for animations leave the
    /// glass out, and the shared region's picture is solid. The toolbar on screen keeps its glass either way.
    func testPicturesLeaveTheGlassOutOrDrawItSolid() async throws {
        try XCTSkipUnless(ToolbarGlass.movesWithPictures, "Before macOS 26, animation pictures are solid")
        let (model, cleanup) = try makeModel("pictures")
        model.setVisibilityAnimation(false)
        model.setPropertiesShown(false)
        model.setGlass(true)
        let app = AppDelegate(model: model)
        app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
        let (toolbar, content) = try await showToolbar(app, model)
        XCTAssertTrue(content.chrome.showsGlass)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        // On the background, between the left edge and the first tool.
        let background = CGPoint(x: 6, y: content.bounds.midY)
        let clear = try animator.snapshotToolbar(toolbar, pixelBudget: 4_000_000)
        XCTAssertLessThan(try Self.alpha(of: clear, at: background, in: content.bounds.size), 0.05,
                          "The picture leaves the glass out for the live glass under it")
        let solid = try animator.snapshotToolbar(toolbar, pixelBudget: 4_000_000, glass: .solid)
        XCTAssertGreaterThan(try Self.alpha(of: solid, at: background, in: content.bounds.size), 0.95,
                             "The shared region's picture is solid")
        XCTAssertTrue(content.chrome.showsGlass)
        XCTAssertEqual(content.chrome.subviews.first?.isHidden, false, "The glass on screen is back at once")
    }

    /// Control-2 hides and shows the toolbar by moving a picture of it in a window of its own. With glass, that window
    /// draws live glass of the toolbar's shape under the picture, in the active look, running the picture's own motion.
    func testShowAndHideMoveLiveGlassWithThePicture() async throws {
        try XCTSkipUnless(ToolbarGlass.movesWithPictures, "Before macOS 26, animation pictures are solid")
        for style in [VisibilityAnimationStyle.slide, .magic, .focus, .dissolve] {
            let (model, cleanup) = try makeModel("show-hide-\(style.rawValue)")
            model.setAnimationStyle(style)
            model.setPropertiesShown(false)
            model.setGlass(true)
            let app = AppDelegate(model: model, visibility: WorkspaceVisibilityAnimator(reduceMotion: { false }))
            app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
            let (toolbar, content) = try await showToolbar(app, model)
            XCTAssertTrue(content.chrome.showsGlass)
            let open = toolbar.frame
            app.activateTools()
            let panels = NSApp.windows.compactMap { $0 as? VisibilityTransitionPanel }.filter { $0.isVisible && $0.showsGlass }
            XCTAssertEqual(panels.count, 1, "\(style): only the toolbar's picture carries glass")
            let panel = try XCTUnwrap(panels.first)
            let view = try XCTUnwrap(panel.contentView as? VisibilityImageView)
            let plates = view.glassPicture?.subviews.compactMap { $0 as? ToolbarChromeView } ?? []
            XCTAssertEqual(plates.count, 1)
            let plate = try XCTUnwrap(plates.first)
            XCTAssertTrue(plate.showsGlass)
            XCTAssertEqual(plate.effectiveCornerRadius, content.chrome.effectiveCornerRadius)
            let onScreen = panel.convertToScreen(plate.convert(plate.bounds, to: nil))
            XCTAssertEqual(onScreen.minX, open.minX, accuracy: 0.5, "\(style): the glass sits where the toolbar was")
            XCTAssertEqual(onScreen.minY, open.minY, accuracy: 0.5)
            XCTAssertEqual(onScreen.size.width, open.width, accuracy: 0.5)
            XCTAssertEqual(onScreen.size.height, open.height, accuracy: 0.5)
            // The glass turns around the picture's center, where its layers are anchored.
            let center = CGPoint(x: view.imageLayer.frame.midX, y: view.imageLayer.frame.midY)
            XCTAssertEqual(view.glassMotion?.layer?.anchorPoint, .zero)
            XCTAssertEqual(view.glassMotion?.layer?.position, center)
            XCTAssertEqual(view.motionContainer.zPosition, 1, "The picture stays above its glass")
            if style == .dissolve {
                XCTAssertNotNil(view.glassPicture?.layer?.animation(forKey: VisibilityImageView.glassFadeKey),
                                "The glass fades with the picture as it breaks up")
            } else {
                XCTAssertFalse((view.imageLayer.animationKeys() ?? []).isEmpty)
                XCTAssertEqual(Set(view.glassPicture?.layer?.animationKeys() ?? []), Set(view.imageLayer.animationKeys() ?? []),
                               "\(style): the glass runs the picture's motion")
                XCTAssertEqual(Set(view.glassMotion?.layer?.animationKeys() ?? []),
                               Set(view.motionContainer.animationKeys() ?? []))
            }
            XCTAssertEqual(view.glassMotion?.layer?.filters?.count ?? 0, style == .focus ? 1 : 0, "Soft focus blurs the glass too")
            XCTAssertEqual(toolbar.alphaValue, 0, "The picture and its glass stand in for the toolbar")
            XCTAssertEqual(panel.animationBehavior, .none,
                           "\(style): the picture leaves at once, so its glass never fades over the toolbar it hands over to")
            if #available(macOS 26, *) {
                XCTAssertTrue(panel._hasActiveAppearanceIgnoringKeyFocus(), "The glass keeps the toolbar's active look")
            }
            for _ in 0..<100 where model.visibilityAnimating { try await Task.sleep(for: .milliseconds(20)) }
            XCTAssertFalse(panel.isVisible)
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
    }

    /// A side toolbar shrinking to one button fades a picture of its tools; its rail and properties keep live glass under
    /// that picture instead of turning dark.
    func testSideToolbarShrinksWithLiveGlassUnderItsPicture() async throws {
        try XCTSkipUnless(ToolbarGlass.movesWithPictures, "Before macOS 26, animation pictures are solid")
        let (model, cleanup) = try makeModel("side-shrink")
        model.setVisibilityAnimation(false)
        model.setToolbarOrientation(.vertical)
        model.setPropertiesShown(true)
        model.setToolbarCollapseDelay(3)
        model.setGlass(true)
        let app = AppDelegate(model: model)
        app.pointerLocation = { CGPoint(x: -100_000, y: -100_000) }
        app.collapse.wait = { _ in try await Task.sleep(for: .seconds(30)) }
        defer {
            app.hideTools()
            app.applicationWillTerminate(Notification(name: NSApplication.willTerminateNotification))
            cleanup()
        }
        let (_, content) = try await showToolbar(app, model)
        let surfaces = ToolbarGlass.surfaces(in: content.host)
        XCTAssertGreaterThanOrEqual(surfaces.count, 2, "The rail and the properties are glass")
        let picture = try XCTUnwrap(content.snapshotHost())
        let rail = try XCTUnwrap(surfaces.min { $0.frame.minX < $1.frame.minX })
        XCTAssertLessThan(try Self.alpha(of: picture, at: CGPoint(x: rail.frame.minX + 3, y: rail.frame.midY),
                                         in: content.host.bounds.size), 0.05, "The picture leaves the glass out")
        app.collapse.collapse()
        XCTAssertTrue(app.toolbarIsAnimating)
        let face = try XCTUnwrap(content.subviews.last)
        XCTAssertFalse(face === content.host)
        let plates = face.subviews.compactMap { $0 as? ToolbarChromeView }
        XCTAssertEqual(plates.map(\.frame), surfaces.map(\.frame), "Live glass sits where the rail and properties were")
        XCTAssertTrue(plates.allSatisfy(\.showsGlass))
        XCTAssertTrue(model.toolbarCollapsed)
    }

    func testSettingsPreviewsStaySolid() throws {
        let (model, cleanup) = try makeModel("preview")
        defer { cleanup() }
        model.setToolbarOrientation(.vertical)
        model.setPropertiesShown(true)
        model.setGlass(true)
        func surfaces(drawsChrome: Bool) -> [ToolbarChromeView] {
            let panel = NSPanel(contentRect: CGRect(x: -10_000, y: -10_000, width: 600, height: 900),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            let host = NSHostingView(rootView: ToolbarView(model: model, availableSize: CGSize(width: 600, height: 900),
                                                           drawsChrome: drawsChrome))
            panel.contentView = host
            defer { panel.contentView = nil; panel.close() }
            host.layoutSubtreeIfNeeded()
            host.displayIfNeeded()
            return Self.chromeViews(in: host)
        }
        XCTAssertFalse(surfaces(drawsChrome: false).isEmpty, "The toolbar window's surfaces use glass")
        XCTAssertTrue(surfaces(drawsChrome: true).isEmpty, "Previews in Settings keep the solid look")
    }
}
