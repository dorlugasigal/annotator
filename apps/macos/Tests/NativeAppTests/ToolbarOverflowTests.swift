import AppKit
import SwiftUI
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class ToolbarOverflowTests: XCTestCase {
    func testOverflowAlwaysFitsWholeButtonsAndIncludesTheActiveTool() {
        for tools in [WorkspacePreferences.defaultToolbar, Tool.allCases, Array(Tool.allCases.reversed())] {
            for selected in tools {
                for width in stride(from: CGFloat(0), through: 900, by: 3) {
                    let visible = ToolbarToolLayout.visible(tools, selected: selected, width: width)
                    XCTAssertLessThanOrEqual(ToolbarToolLayout.width(of: visible), width)
                    if width >= ToolbarToolLayout.buttonWidth { XCTAssertTrue(visible.contains(selected)) }
                    XCTAssertEqual(Set(visible).count, visible.count)
                }
            }
        }
    }

    /// A narrow toolbar keeps the properties on one row that scrolls; the number field at its start isn't cut off.
    func testNarrowPropertiesFieldIsNotClipped() throws {
        let suite = "narrow-precision-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        model.beginDrawing()
        for tool in [Tool.blur, .pixelate] {
            model.tool = tool
            let host = NSHostingController(rootView: ToolbarView(model: model, availableSize: CGSize(width: 400, height: 500)))
            let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 400, height: 130),
                                styleMask: [.borderless], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.contentViewController = host
            for _ in 0..<4 {
                host.view.setFrameSize(host.view.fittingSize)
                host.view.layoutSubtreeIfNeeded()
                RunLoop.current.run(until: Date().addingTimeInterval(0.02))
            }

            let fields = editableFields(in: host.view)
            XCTAssertFalse(fields.isEmpty, "\(tool.label) must expose its numeric field at narrow width.")
            for field in fields {
                let frame = field.convert(field.bounds, to: host.view)
                XCTAssertTrue(host.view.bounds.contains(frame), "\(tool.label) clips its numeric field: \(frame)")
            }
            panel.contentViewController = nil
            panel.close()
        }
    }

    func testCoverModesStayVisibleAndCoverCanBeReenabled() throws {
        let suite = "cover-toolbar-regression-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        for mode in [Tool.cover, .blur, .pixelate, .distort] {
            let identity = ToolbarToolLayout.identity(for: mode, in: model.toolbarTools)
            XCTAssertEqual(identity, .cover)
            XCTAssertEqual(ToolbarToolLayout.visible(model.toolbarTools, selected: identity, width: 44), [.cover])
        }
        model.configureToolbar([.select, .pen])
        XCTAssertTrue(WorkspaceModel.toolbarCandidates.contains(.tool(.cover)))
        XCTAssertTrue(WorkspaceModel.toolbarCandidates.contains(.tool(.blur)))
        model.configureToolbar([.select, .pen, .cover])
        XCTAssertTrue(model.toolbarTools.contains(.cover))
        XCTAssertNil(model.preferences.toolbarItems, "Tool-only toolbars keep the older format.")
    }

    func testEffectsAndPresentingButtonsCanHaveTheirOwnPlaces() throws {
        let suite = "toolbar-items-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let items: [ShortcutCommand] = [.tool(.select), .tool(.blur), .action(.spotlight), .tool(.cover), .tool(.pen)]
        model.configureToolbar(items: items)
        XCTAssertEqual(model.toolbarItems, items)
        XCTAssertEqual(model.toolbarTools, [.select, .blur, .cover, .pen])
        XCTAssertEqual(model.preferences.toolbarTools, ["select", "blur", "cover", "pen"], "Older versions still read the tools.")
        XCTAssertEqual(ToolbarToolLayout.identity(for: .blur, in: model.toolbarItems), .blur)
        XCTAssertEqual(ToolbarToolLayout.identity(for: .pixelate, in: model.toolbarItems), .cover)
        let reloaded = WorkspaceModel(defaults: defaults)
        defer { reloaded.closeAll() }
        XCTAssertEqual(reloaded.toolbarItems, items)

        // If an older version rewrites the tools, its list wins and the extra buttons are dropped.
        var older = model.preferences
        older.toolbarTools = ["select", "pen"]
        defaults.set(try older.data(), forKey: "workspace-preferences-v1")
        let downgraded = WorkspaceModel(defaults: defaults)
        defer { downgraded.closeAll() }
        XCTAssertEqual(downgraded.toolbarItems, [.tool(.select), .tool(.pen)])
    }

    private func editableFields(in view: NSView) -> [NSTextField] {
        if let field = view as? NSTextField, field.isEditable { return [field] }
        return view.subviews.flatMap { editableFields(in: $0) }
    }
}
