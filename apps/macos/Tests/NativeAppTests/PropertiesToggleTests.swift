import AppKit
import XCTest
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class PropertiesToggleTests: XCTestCase {
    func testBothHostsReopenPropertiesAfterSettingsCollapsesTheArea() throws {
        let suite = "properties-toggle-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let owner = WorkspaceModel(defaults: defaults)
        let image = try PNGExporter.image(elements: [], size: CGSize(width: 100, height: 100), background: .white)
        let editor = NativeCanvasFixture(image: image, settingsOwner: owner)
        defer { editor.close(); owner.closeAll(); defaults.removePersistentDomain(forName: suite) }
        for host in [owner, editor.model] {
            owner.setPropertiesShown(true)
            owner.setPropertiesShown(false)
            host.toggleProperties()
            XCTAssertEqual(owner.preferences.quickPreferencesExpanded, true)
            XCTAssertEqual(ToolbarPropertiesMode(model: owner), .shown)
            host.toggleProperties()
            XCTAssertEqual(owner.preferences.quickPreferencesExpanded, false)
            XCTAssertEqual(ToolbarPropertiesMode(model: editor.model), .hidden)
        }
    }

    func testPropertiesShowByDefaultAndTheToggleHidesThem() throws {
        let suite = "properties-default-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        XCTAssertNil(model.preferences.inspectorPinned)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .shown, "Properties show by default, so Curved shows at once.")
        model.toggleProperties()
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .hidden)
        model.performShortcutAction(.properties)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .shown)
        XCTAssertEqual(model.preferences.inspectorPinned, true)
        XCTAssertEqual(ToolbarPropertiesMode.allCases, [.shown, .hidden], "There are only two choices")
        // A profile from before, with quick controls, shows the properties.
        var quick = model.preferences
        quick.quickPreferencesExpanded = true
        quick.inspectorPinned = false
        defaults.set(try quick.data(), forKey: "workspace-preferences-v1")
        let restored = WorkspaceModel(defaults: defaults)
        defer { restored.closeAll() }
        XCTAssertEqual(restored.preferences.inspectorPinned, false)
        XCTAssertEqual(ToolbarPropertiesMode(model: restored), .shown)
    }
}
