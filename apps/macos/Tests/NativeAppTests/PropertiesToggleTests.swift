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
            owner.setInspectorPinned(true)
            owner.setQuickPreferencesExpanded(false)
            host.toggleProperties()
            XCTAssertEqual(owner.preferences.inspectorPinned, true)
            XCTAssertEqual(owner.preferences.quickPreferencesExpanded, true)
            host.toggleProperties()
            XCTAssertEqual(owner.preferences.inspectorPinned, false)
        }
    }

    func testFullPropertiesAreTheDefaultAndTheToggleSwitchesToQuickControls() throws {
        let suite = "properties-default-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        XCTAssertNil(model.preferences.inspectorPinned)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .full, "Full properties are the default, so Curved shows at once.")
        model.toggleProperties()
        XCTAssertEqual(model.preferences.inspectorPinned, false)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .quick)
        model.toggleProperties()
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .full)
        model.setQuickPreferencesExpanded(false)
        XCTAssertEqual(ToolbarPropertiesMode(model: model), .toolsOnly)
    }
}
