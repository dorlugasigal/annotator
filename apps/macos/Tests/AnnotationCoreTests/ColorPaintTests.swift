import Foundation
import Testing
import AnnotationCore

@Test func colorChannelsKeepIndependentOpacityAndNoFill() throws {
    var style = Style()
    #expect(style.color.alpha == 1)
    #expect(style.fill == nil)
    style.setPaintColor(.blue, fill: true)
    #expect(style.fill?.alpha == 0.4)
    style.setPaintOpacity(0.2, fill: true)
    style.setPaintColor(.coral, fill: true)
    #expect(style.fill?.alpha == 0.2)
    #expect(style.color.alpha == 1)
    style.setPaintOpacity(0.7, fill: false)
    style.setPaintColor(.blue, fill: false)
    #expect(style.color.alpha == 0.7)
    style.setGradient(GradientPreset.spectrum.gradient, fill: true)
    #expect(style.fill?.alpha == 0.2)
    style.setPaintColor(nil, fill: true)
    #expect(style.fill == nil)
    #expect(style.fillGradient == nil)
    #expect(style.color.alpha == 0.7)
    try style.validate()
}

@Test func enablingFillCopiesEachStrokePaintAtFortyPercentWithoutLinkingIt() throws {
    for gradient in [nil, GradientPreset.spectrum.gradient] {
        var style = Style()
        style.color = RGBA(0.2, 0.4, 0.8, 0.8)
        if let gradient { style.setGradient(gradient, fill: false) }
        style.setFillEnabled(true)
        #expect(style.fillGradient == style.strokeGradient)
        #expect(style.fill?.red == style.color.red)
        #expect(style.fill?.alpha == 0.4)
        style.setPaintOpacity(0.25, fill: true)
        style.setPaintColor(.white, fill: true)
        let customFill = style.fill
        style.setFillEnabled(true)
        #expect(style.fill == customFill)
        style.matchFillToStroke()
        #expect(style.fill?.alpha == 0.25)
        #expect(style.fillGradient == style.strokeGradient)
        style.setFillEnabled(false)
        #expect(style.fill == nil && style.fillGradient == nil)
        #expect(style.color.alpha == 0.8)
        try style.validate()
    }
}

@Test func gradientsValidateAndRequireTheNewBoardFormat() throws {
    #expect(GradientPreset.allCases.count == 8)
    for preset in GradientPreset.allCases {
        #expect(preset.gradient.angle == 25)
        #expect(preset.gradient.colors.count == (preset == .spectrum ? 6 : 3))
    }
    for preset in GradientPreset.allCases { try preset.gradient.validate() }
    for gradient in [
        ColorGradient(colors: []), ColorGradient(colors: [.blue]),
        ColorGradient(colors: Array(repeating: .blue, count: 9)),
        ColorGradient(colors: [.blue, RGBA(1, 0, 0, 0.4)]),
        ColorGradient(colors: [.blue, .coral], angle: .nan)
    ] { #expect(throws: CanvasError.self) { try gradient.validate() } }
    var element = Element(tool: .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 60))
    element.style.setGradient(GradientPreset.sunset.gradient, fill: false)
    element.label = ShapeLabel(text: "Follow me", followsStroke: true)
    let document = try CanvasDocument(elements: [element])
    let file = DrawingFile(document: document)
    #expect(file.schemaVersion == 3)
    let data = try file.data()
    #expect(try DrawingFile.read(data).elements == [element])
    let downgraded = Data(String(decoding: data, as: UTF8.self)
        .replacingOccurrences(of: "\"schemaVersion\":3", with: "\"schemaVersion\":2").utf8)
    #expect(throws: CanvasError.self) { try DrawingFile.read(downgraded) }
    var selected = document
    selected.select([element.id])
    let saved = try SavedShape(name: "Gradient label", document: selected)
    #expect(try SavedShape.read(saved.data()).elements == [element])
}

@Test func legacyLabelsStayIndependentAndNewLabelsFollowStrokePaint() throws {
    var owner = Style()
    owner.setGradient(GradientPreset.sunset.gradient, fill: false)
    owner.setPaintOpacity(0.8, fill: false)
    let legacy = ShapeLabel(text: "Legacy")
    #expect(legacy.resolvedStyle(for: owner).color == .ink)
    let linked = ShapeLabel(text: "Linked", followsStroke: true)
    #expect(linked.resolvedStyle(for: owner).color.alpha == 0.8)
    #expect(linked.resolvedStyle(for: owner).strokeGradient == owner.strokeGradient)
    var preferences = WorkspacePreferences()
    preferences.toolStyles["rectangle"] = owner
    let restored = try WorkspacePreferences.read(preferences.data())
    #expect(restored.schemaVersion == 2)
    #expect(restored.toolStyles["rectangle"] == owner)
    var rectangle = Element(tool: .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 60))
    rectangle.label = linked
    rectangle.resize(to: Rect(x: 0, y: 0, width: 200, height: 120))
    #expect(rectangle.label?.followsStroke == true)
}
