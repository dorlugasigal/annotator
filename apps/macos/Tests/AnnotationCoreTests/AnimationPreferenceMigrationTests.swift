import Foundation
import Testing
@testable import AnnotationCore

@Test func rejectedFluidPreferenceMigratesToAnchoredSlideWithoutChangingOtherSettings() throws {
    var preferences = WorkspacePreferences()
    preferences.visibilityAnimationStyle = .fluid
    preferences.animateVisibility = true
    preferences.toolbarOrientation = .vertical
    let restored = try WorkspacePreferences.read(JSONEncoder().encode(preferences))
    #expect(restored.visibilityAnimationStyle == .slide)
    #expect(restored.toolbarOrientation == .vertical)
    #expect(restored.animateVisibility == true)
    preferences.visibilityAnimationStyle = VisibilityAnimationStyle.none
    preferences.animateVisibility = false
    let disabled = try WorkspacePreferences.read(JSONEncoder().encode(preferences))
    #expect(disabled.animateVisibility == false)
    #expect(disabled.visibilityAnimationStyle == VisibilityAnimationStyle.none)
}

@Test(arguments: AppTheme.allCases)
func themePreferenceRoundTripsWithoutChangingInk(_ theme: AppTheme) throws {
    var preferences = WorkspacePreferences()
    preferences.theme = theme
    preferences.toolStyles["pen"] = .defaults(for: .pen)
    let restored = try WorkspacePreferences.read(preferences.data())
    #expect(restored == preferences)
    #expect(restored.theme == theme)
}

@Test func animationPickerHasDistinctEffectsAndOffWithoutBreakingOldProfiles() {
    #expect(VisibilityAnimationStyle.available == [.dissolve, .slide, .magic, .corners, .pop, .drop, .focus, .fade, .none])
    for added in [VisibilityAnimationStyle.pop, .drop, .focus] { #expect(added.resolved == added) }
    #expect(VisibilityAnimationStyle.magic.resolved == .magic)
    #expect(VisibilityAnimationStyle.quick.resolved == .slide)
    for old in [VisibilityAnimationStyle.reveal, .curtain, .fold, .rise] {
        #expect(old.resolved == .fade)
    }
    #expect(VisibilityAnimationStyle.slide.rawValue == "slide")
}

@Test func oldProfilesUseSystemThemeAndStillMigrateFluid() throws {
    var preferences = WorkspacePreferences()
    preferences.visibilityAnimationStyle = .fluid
    var object = try #require(JSONSerialization.jsonObject(with: preferences.data()) as? [String: Any])
    object.removeValue(forKey: "theme")
    let migrated = try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object))
    #expect(migrated.theme == nil)
    #expect((migrated.theme ?? .system) == .system)
    #expect(migrated.visibilityAnimationStyle == .slide)
    object["theme"] = "unsupported"
    #expect(throws: DecodingError.self) {
        try WorkspacePreferences.read(JSONSerialization.data(withJSONObject: object))
    }
}

@Test(arguments: [VisibilityAnimationStyle.reveal, .curtain, .fold, .rise, .magic, .corners, .pop, .drop, .focus])
func containedAnimationPreferencesRoundTrip(_ style: VisibilityAnimationStyle) throws {
    var preferences = WorkspacePreferences()
    preferences.visibilityAnimationStyle = style
    preferences.animateVisibility = true
    #expect(try WorkspacePreferences.read(preferences.data()) == preferences)
}
