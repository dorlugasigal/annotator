import Foundation
import Testing
@testable import AnnotationCore

@Test func preferencesRoundTripAndRejectConflicts() throws {
    var preferences = WorkspacePreferences()
    var style = Style()
    style.width = 11; style.color = .blue
    preferences.toolStyles["pen"] = style
    try preferences.assignShortcut("v", to: .pen)
    #expect(preferences.toolShortcuts["7"] == nil)
    #expect(preferences.toolShortcuts["v"] == "pen")
    let beforeConflict = preferences
    #expect(throws: CanvasError.self) { try preferences.assignShortcut("1", to: .pen) }
    #expect(preferences == beforeConflict)
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
}

@Test(arguments: ToolCursorStyle.allCases)
func legacyDrawingCursorPreferencesStillRoundTrip(_ style: ToolCursorStyle) throws {
    var preferences = WorkspacePreferences()
    preferences.cursorStyle = style
    #expect(try WorkspacePreferences.read(preferences.data()).cursorStyle == style)
}

@Test func numericDefaultsReserveColorsAndMigrateLegacyProfiles() throws {
    let defaults = WorkspacePreferences()
    for (index, tool) in [Tool.select, .rectangle, .diamond, .ellipse, .arrow, .line, .pen, .text, .highlighter, .eraser].enumerated() {
        #expect(defaults.toolShortcuts[String((index + 1) % 10)] == tool.rawValue)
    }
    var preferences = defaults
    for key in InkShortcut.choices.keys {
        #expect(throws: CanvasError.self) { try preferences.assignShortcut(key, to: .pen) }
        #expect(preferences == defaults)
    }
    var object = try #require(JSONSerialization.jsonObject(with: defaults.data()) as? [String: Any])
    object.removeValue(forKey: "shortcutVersion")
    object["toolShortcuts"] = WorkspacePreferences.legacyToolShortcuts
    let migrated = try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object))
    #expect(migrated.toolShortcuts == defaults.toolShortcuts)
    var custom = WorkspacePreferences.legacyToolShortcuts
    custom.removeValue(forKey: "p")
    custom["j"] = "pen"
    object["toolShortcuts"] = custom
    let customMigration = try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object))
    #expect(customMigration.toolShortcuts["j"] == "pen")
    #expect(customMigration.toolShortcuts["7"] == nil)
    #expect(try WorkspacePreferences.read(customMigration.data()) == customMigration)
}

@Test func globalShortcutMustBeDeliberateAndPreferencesValidated() throws {
    #expect(throws: CanvasError.self) { try ActivationShortcut(keyCode: 0, character: "a", shift: true).validate() }
    var preferences = WorkspacePreferences()
    preferences.spotlightRadius = .nan
    #expect(throws: CanvasError.self) { try preferences.data() }
    preferences = WorkspacePreferences()
    preferences.toolStyles["unrecognized"] = Style()
    #expect(throws: CanvasError.self) { try preferences.data() }
    #expect(ActivationShortcut.standard.label == "⌃2")
}

@Test func toolbarAndHideShortcutValidationPreserveRecoveryPaths() throws {
    var preferences = WorkspacePreferences()
    preferences.hideShortcut = preferences.activationShortcut
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences = WorkspacePreferences()
    preferences.toolbarTools = ["pen", "blur"]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.toolbarTools = ["select", "pen", "pen"]
    #expect(throws: CanvasError.self) { try preferences.validate() }
    preferences.toolbarTools = ["select", "blur", "image"]
    preferences.toolbarOrientation = .vertical
    preferences.cursorStyle = .toolPreview
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
}
