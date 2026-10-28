import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class ToolbarDesignTests: XCTestCase {
    private var output: URL {
        Bundle(for: Self.self).bundleURL
            .deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("UIReview")
    }

    @discardableResult
    private func render<V: View>(_ content: V, name: String, dark: Bool) throws -> CGSize {
        let controller = NSHostingController(rootView: content.environment(\.colorScheme, dark ? .dark : .light))
        let view = controller.view
        view.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 880, height: 600),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.appearance = view.appearance
        panel.contentViewController = controller
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentViewController = nil; panel.close() }
        for _ in 0..<4 {
            view.setFrameSize(view.fittingSize)
            view.layoutSubtreeIfNeeded()
            RunLoop.current.run(until: Date().addingTimeInterval(0.02))
        }
        let size = view.fittingSize
        view.setFrameSize(size)
        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            .write(to: output.appendingPathComponent("\(name)-\(dark ? "dark" : "light").png"))
        print("TOOLBAR DESIGN \(name): \(size)")
        return size
    }

    func testCompactContextualLayoutsInBothAppearances() throws {
        let suite = "native-design-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.setInspectorPinned(false)
        model.beginDrawing()
        for dark in [false, true] {
            for tool in [Tool.select, .rectangle, .ellipse, .arrow, .line, .pen, .highlighter, .text, .eraser,
                         .database, .queue, .person, .grid, .axes, .blur, .cover] {
                model.tool = tool
                model.applyStyle { $0 = .defaults(for: tool) }
                let size = try render(ToolbarView(model: model), name: tool.rawValue, dark: dark)
                XCTAssertEqual(size.width, ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools,
                    available: 1000, propertiesVisible: true), accuracy: 1)
                XCTAssertLessThanOrEqual(size.height, 126, "\(tool.rawValue) is not compact")
                XCTAssertGreaterThan(size.height, 80)
            }
        }
    }

    func testAdaptiveLayoutsAndModes() throws {
        let suite = "native-design-adaptive-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let cases: [(String, CGSize, Bool, Bool, Bool)] = [
            ("collapsed", CGSize(width: 880, height: 900), false, false, false),
            ("narrow", CGSize(width: 400, height: 600), true, false, false),
            ("vertical", CGSize(width: 880, height: 620), true, false, true),
            ("pinned", CGSize(width: 880, height: 600), true, true, false),
            ("use-apps", CGSize(width: 880, height: 600), true, false, false)
        ]
        for dark in [false, true] {
            for (name, available, expanded, pinned, vertical) in cases {
                model.setToolbarOrientation(vertical ? .vertical : .horizontal)
                model.setInspectorPinned(pinned)
                model.setQuickPreferencesExpanded(expanded)
                model.tool = .pen
                model.beginDrawing()
                if name == "use-apps" { model.interact() }
                let size = try render(ToolbarView(model: model, availableSize: available), name: name, dark: dark)
                XCTAssertEqual(size.width, vertical ? (expanded ? 376 : 68) :
                    ToolbarToolLayout.horizontalWidth(tools: model.toolbarTools, available: available.width,
                                                      propertiesVisible: expanded), accuracy: 1)
                XCTAssertLessThanOrEqual(size.height, available.height)
                if !expanded { XCTAssertLessThanOrEqual(size.height, 72) }
                if !pinned && !vertical && expanded { XCTAssertLessThanOrEqual(size.height, 126) }
            }
        }
    }

    func testShortPanelKeepsChromeAndPaletteIsCompact() throws {
        let suite = "native-design-small-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.setInspectorPinned(false)
        model.tool = .arrow
        let size = try render(ToolbarView(model: model, availableSize: CGSize(width: 648, height: 230)),
                              name: "short", dark: false)
        XCTAssertEqual(size.width, 648, accuracy: 1)
        XCTAssertLessThanOrEqual(size.height, 230)
        let palette = try render(InspectorColorPalette(model: model, fill: true, done: {}), name: "palette", dark: true)
        XCTAssertEqual(palette.width, 284, accuracy: 1)
        XCTAssertLessThan(palette.height, 520)
        model.applyStyle { $0.setGradient(GradientPreset.spectrum.gradient, fill: true) }
        let gradients = try render(InspectorColorPalette(model: model, fill: true, done: {}), name: "gradients", dark: true)
        XCTAssertEqual(gradients.width, 284, accuracy: 1)
    }

    func testEverySettingsTabAndToolProperties() throws {
        let suite = "settings-properties-review-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        for dark in [false, true] {
            for tab in CustomizationTab.allCases {
                let size = try render(CustomizationView(model: model, showDone: false, initialTab: tab),
                                      name: "settings-\(tab.rawValue)", dark: dark)
                XCTAssertGreaterThanOrEqual(size.width, SettingsSurfaceView.minimumContentSize.width)
                XCTAssertLessThanOrEqual(size.width, SettingsSurfaceView.defaultContentSize.width + 20)
                XCTAssertGreaterThanOrEqual(size.height, 620)
                XCTAssertLessThanOrEqual(size.height, 760)
            }
            for tool in [Tool.rectangle, .diamond, .arrow, .pen, .highlighter, .text, .blur] {
                model.tool = tool
                model.applyStyle { $0 = .defaults(for: tool) }
                let size = try render(ContextualInspector(model: model).padding(16).frame(width: 648),
                                      name: "properties-\(tool.rawValue)", dark: dark)
                XCTAssertEqual(size.width, 648, accuracy: 1)
                XCTAssertLessThan(size.height, 400)
            }
        }
    }
}
