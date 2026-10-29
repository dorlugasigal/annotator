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

@Test func toolbarControlsCanBeTurnedOffAndRoundTrip() throws {
    var preferences = WorkspacePreferences()
    #expect(ToolbarControl.allCases.allSatisfy(preferences.showsToolbarControl))
    preferences.setToolbarControl(.undo, shown: false)
    preferences.setToolbarControl(.library, shown: false)
    preferences.setToolbarControl(.undo, shown: false)
    #expect(preferences.hiddenToolbarControls == ["library", "undo"], "The list keeps the toolbar's order, each button once.")
    #expect(!preferences.showsToolbarControl(.undo))
    #expect(preferences.showsToolbarControl(.redo))
    preferences.toolbarCollapseDelay = 5
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
    preferences.setToolbarControl(.undo, shown: true)
    preferences.setToolbarControl(.library, shown: true)
    #expect(preferences.hiddenToolbarControls == nil)
    #expect(Set(ToolbarControl.allCases.map(\.label)).count == ToolbarControl.allCases.count)
}

@Test func toolbarControlsFromANewerVersionAreDroppedWhenReading() throws {
    var object = try #require(JSONSerialization.jsonObject(with: WorkspacePreferences().data()) as? [String: Any])
    object["hiddenToolbarControls"] = ["undo", "future", "undo"]
    #expect(try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object)).hiddenToolbarControls == ["undo"])
    object["hiddenToolbarControls"] = ["future"]
    #expect(try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object)).hiddenToolbarControls == nil)
}

@Test func invalidToolbarControlsAndCollapseDelaysAreRejected() {
    var preferences = WorkspacePreferences()
    preferences.hiddenToolbarControls = ["undo", "undo"]
    #expect(throws: CanvasError.self) { try preferences.data() }
    preferences.hiddenToolbarControls = ["dance"]
    #expect(throws: CanvasError.self) { try preferences.data() }
    preferences.hiddenToolbarControls = nil
    for delay in [0.5, 601, .infinity, .nan] {
        preferences.toolbarCollapseDelay = delay
        #expect(throws: CanvasError.self) { try preferences.data() }
    }
    for delay in WorkspacePreferences.toolbarCollapseDelays {
        preferences.toolbarCollapseDelay = delay
        #expect(throws: Never.self) { try preferences.data() }
    }
}

@Test func propertiesCanShowAllTheirChoicesAndRoundTrip() throws {
    var preferences = WorkspacePreferences()
    #expect(PropertyChoice.allCases.allSatisfy { !preferences.showsAllChoices($0) }, "One button is the default.")
    preferences.setShowsAllChoices(.font, true)
    preferences.setShowsAllChoices(.pattern, true)
    preferences.setShowsAllChoices(.font, true)
    #expect(preferences.expandedPropertyChoices == ["pattern", "font"])
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
    preferences.setShowsAllChoices(.pattern, false)
    preferences.setShowsAllChoices(.font, false)
    #expect(preferences.expandedPropertyChoices == nil)
    #expect(PropertyChoice.allCases.map(\.choiceCount) == [3, 3, 3, 2, 2, 2, 2, 3])
    var object = try #require(JSONSerialization.jsonObject(with: WorkspacePreferences().data()) as? [String: Any])
    object["expandedPropertyChoices"] = ["roughness", "sparkle", "roughness"]
    #expect(try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object)).expandedPropertyChoices == ["roughness"])
    preferences.expandedPropertyChoices = ["sparkle"]
    #expect(throws: CanvasError.self) { try preferences.data() }
}
