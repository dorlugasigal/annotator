import Foundation
import Testing
import AnnotationCore

@Test func pointerVisibilityExpiresWithoutChangingDrawingState() throws {
    var settings = CursorHighlightSettings()
    #expect(settings.isVisible(now: 10, lastMovement: nil, lastClick: nil))
    settings.visibility = .moving
    #expect(settings.isVisible(now: 10.5, lastMovement: 10, lastClick: nil))
    #expect(!settings.isVisible(now: 10.8, lastMovement: 10, lastClick: nil))
    settings.visibility = .clicks
    #expect(settings.isVisible(now: 10.2, lastMovement: nil, lastClick: 10))
    #expect(!settings.isVisible(now: 10.5, lastMovement: nil, lastClick: 10))
    for size in [Double.nan, .infinity, 0, 161] {
        settings.diameter = size
        #expect(throws: CanvasError.self) { try settings.validate() }
    }
}

@Test func haloSettingsPreserveLegacyDefaultsAndValidateGlow() throws {
        let old = Data("""
        {"shape":"roundedSquare","visibility":"always","color":{"red":1,"green":0.82,"blue":0,"alpha":0.34},
        "diameter":74,"borderWidth":7,"showClicks":true}
        """.utf8)
        var settings = try JSONDecoder().decode(CursorHighlightSettings.self, from: old)
        try settings.validate()
        #expect(settings.shape == .roundedSquare)
        #expect(settings.resolvedGlow == 0)
        #expect(settings.resolvedClickEffect == .pulse)
        #expect(settings.resolvedBorder == .solid)
        settings.showClicks = false
        #expect(settings.resolvedClickEffect == .off)
        for value in [Double.nan, .infinity, -1, 25] {
            settings.glowRadius = value
            #expect(throws: CanvasError.self) { try settings.validate() }
        }
        settings.glowRadius = 12
        settings.clickEffect = .ripple
        settings.shape = .diamond
        settings.borderStyle = .dotted
        try settings.validate()
        #expect(try JSONDecoder().decode(CursorHighlightSettings.self, from: JSONEncoder().encode(settings)) == settings)
    }

@Test func clickProgressIsBoundedAndClicksRevealMovingHighlights() {
        #expect(CursorHighlightSettings.clickProgress(now: 10, startedAt: nil) == nil)
        #expect(CursorHighlightSettings.clickProgress(now: 9, startedAt: 10) == nil)
        #expect(CursorHighlightSettings.clickProgress(now: .infinity, startedAt: 10) == nil)
        #expect(CursorHighlightSettings.clickProgress(now: 10, startedAt: 10) == 0)
        #expect(abs((CursorHighlightSettings.clickProgress(now: 10.225, startedAt: 10) ?? 0) - 0.5) < 0.001)
        #expect(CursorHighlightSettings.clickProgress(now: 10.5, startedAt: 10) == nil)
        var settings = CursorHighlightSettings()
        settings.visibility = .moving
        #expect(settings.isVisible(now: 10.2, lastMovement: nil, lastClick: 10))
}
