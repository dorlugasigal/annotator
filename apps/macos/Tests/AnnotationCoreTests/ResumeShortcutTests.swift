import Foundation
import Testing
@testable import AnnotationCore

@Test func resumeShortcutRoundTripsAndRejectsPhysicalKeyConflicts() throws {
    var preferences = WorkspacePreferences()
    #expect((preferences.resumeShortcut ?? .resumeDrawing).label == "⌃⇧2")
    preferences.resumeShortcut = ActivationShortcut(keyCode: 15, character: "r", control: true, shift: true)
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
    for binding in [preferences.activationShortcut, preferences.hideShortcut ?? .hideTools,
                    preferences.zoomShortcut ?? .liveZoom] {
        var renamed = binding
        renamed.character = "X"
        preferences.resumeShortcut = renamed
        #expect(throws: CanvasError.self) { try preferences.validate() }
    }
}

@Test func oldProfilesKeepExistingShortcutsWhenResumeDefaultIsTaken() throws {
    let defaults = WorkspacePreferences()
    #expect(try WorkspacePreferences.read(defaults.data()).resumeShortcut == nil)
    var object = try #require(JSONSerialization.jsonObject(with: defaults.data()) as? [String: Any])
    let existing = [
        ActivationShortcut.resumeDrawing,
        ActivationShortcut(keyCode: 19, character: "x", control: true, option: true, shift: true),
        ActivationShortcut(keyCode: 19, character: "y", control: true, command: true, shift: true)
    ]
    for (field, shortcut) in zip(["activationShortcut", "hideShortcut", "zoomShortcut"], existing) {
        object[field] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(shortcut))
    }
    let migrated = try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object))
    #expect(migrated.activationShortcut == existing[0])
    #expect(migrated.hideShortcut == existing[1])
    #expect(migrated.zoomShortcut == existing[2])
    let resume = try #require(migrated.resumeShortcut)
    #expect(!existing.contains { $0.usesSameKeys(as: resume) })
    #expect(try WorkspacePreferences.read(migrated.data()) == migrated)
}
