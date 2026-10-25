import Foundation
import Testing
import AnnotationCore

@Test func newProfilesStartDrawingToolsWithTheSunsetGradient() throws {
    let preferences = WorkspacePreferences.firstUse
    for tool in Tool.allCases {
        let style = preferences.toolStyles[tool.rawValue]
        if tool.startsWithGradient {
            #expect(style?.strokeGradient == GradientPreset.sunset.gradient, "\(tool)")
            #expect(style?.fillGradient == nil, "\(tool)")
            #expect(style?.color.alpha == 1, "\(tool)")
            #expect(style?.width == Style.defaults(for: tool).width, "\(tool)")
        } else {
            #expect(style == nil, "\(tool)")
        }
    }
    #expect(Tool.pen.startsWithGradient && Tool.rectangle.startsWithGradient && Tool.arrow.startsWithGradient)
    #expect(Tool.line.startsWithGradient && Tool.text.startsWithGradient && Tool.server.startsWithGradient)
    #expect(!Tool.highlighter.startsWithGradient && !Tool.eraser.startsWithGradient && !Tool.blur.startsWithGradient)
    #expect(!Tool.cover.startsWithGradient && !Tool.image.startsWithGradient && !Tool.counter.startsWithGradient)
    #expect(Style.firstUse(for: .arrow).headScale == 0.75)

    let data = try preferences.data()
    let saved = try WorkspacePreferences.read(data)
    #expect(saved.schemaVersion == 2)
    #expect(saved.toolStyles == preferences.toolStyles)
}

@Test func savedProfilesWithoutStylesKeepSolidDefaults() throws {
    let saved = try WorkspacePreferences.read(try WorkspacePreferences().data())
    #expect(saved.toolStyles.isEmpty)
    #expect(Style.defaults(for: .pen).strokeGradient == nil)
    #expect(Style.defaults(for: .pen).color == .coral)
}

@Test func gradientPresetsKeepTheirOrderAndAddSevenMore() throws {
    #expect(GradientPreset.allCases.map(\.rawValue) == [
        "sunset", "lagoon", "ember", "spectrum", "dusk", "citrus", "ocean", "orchid", "aurora", "cherry",
        "forest", "copper", "neon", "tropic", "steel"
    ])
    for preset in GradientPreset.allCases { try preset.gradient.validate() }
}
