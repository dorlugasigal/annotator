import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

private func pixel(_ image: CGImage, _ x: Int, _ y: Int) throws -> [UInt8] {
    let data = try #require(image.dataProvider?.data)
    let bytes = try #require(CFDataGetBytePtr(data))
    let offset = y * image.bytesPerRow + x * 4
    return Array(UnsafeBufferPointer(start: bytes + offset, count: 4))
}

@Test @MainActor func gradientStrokeAndFillHaveIndependentAlphaAndSpatialColors() throws {
    var shape = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 160, height: 80))
    shape.style.width = 8
    shape.style.setGradient(ColorGradient(colors: [RGBA(1, 0, 0), RGBA(0, 0, 1)]), fill: false)
    shape.style.setGradient(GradientPreset.lagoon.gradient, fill: true)
    let image = try PNGExporter.image(elements: [shape], size: CGSize(width: 200, height: 120))
    let left = try pixel(image, 40, 20), right = try pixel(image, 160, 20)
    #expect(left[0] > left[2])
    #expect(right[2] > right[0])
    #expect(left[3] == 255)
    #expect(abs(Int(try pixel(image, 100, 60)[3]) - 102) <= 2)
    #expect(try pixel(image, 5, 5)[3] == 0)
    shape.style.opacity = 0.5
    let faded = try PNGExporter.image(elements: [shape], size: CGSize(width: 200, height: 120))
    #expect(abs(Int(try pixel(faded, 40, 17)[3]) - 128) <= 2)
    #expect(abs(Int(try pixel(faded, 100, 60)[3]) - 51) <= 2)
}

@Test @MainActor func gradientLabelsAndExportsUseTheOwnerPaintWithoutChangingCovers() throws {
    var shape = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 440, height: 100))
    shape.style.setGradient(GradientPreset.sunset.gradient, fill: false)
    shape.style.setGradient(GradientPreset.sunset.gradient, fill: true)
    shape.style.width = 6
    shape.style.cornerRadius = 10
    shape.label = ShapeLabel(text: "Make your point", followsStroke: true)
    let label = try #require(ShapeLabelLayout.textElement(for: shape))
    #expect(label.style.strokeGradient == shape.style.strokeGradient)
    let image = try PNGExporter.image(elements: [shape], size: CGSize(width: 480, height: 140), background: RGBA(0.1, 0.1, 0.1))
    if let directory = ProcessInfo.processInfo.environment["ANNOTATOR_COLOR_REVIEW_DIR"] {
        let folder = URL(fileURLWithPath: directory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try PNGExporter.encode(image).write(to: folder.appendingPathComponent("gradient-label.png"))
    }

    var cover = Element(tool: .cover, frame: Rect(x: 20, y: 20, width: 100, height: 60))
    cover.style = shape.style
    cover.style.opacity = 0.1
    let protected = try PNGExporter.image(elements: [cover], size: CGSize(width: 160, height: 100))
    #expect(try pixel(protected, 60, 40) == [0, 0, 0, 255])
}

@Test @MainActor func gradientsPreserveTheAlphaMaskOfEveryDrawingPath() throws {
    for tool in [Tool.rectangle, .pen, .highlighter, .arrow, .text, .counter, .ruler] {
        var element = Element(tool: tool, frame: Rect(x: 20, y: 20, width: 150, height: 80), text: "8")
        if tool.isFreehand || tool.isConnector || tool == .ruler {
            element = Element(tool: tool, points: [Point(30, 30), Point(80, 60), Point(140, 30)], text: "8")
        }
        element.style.opacity = 0.6
        element.style.color.alpha = 0.5
        element.style.width = 8
        if tool.supportsFill { element.style.setPaintColor(.blue, fill: true) }
        if tool == .pen { element.style.pressure = true; element.pressures = [0.5, 0.8, 1] }
        let solid = try PNGExporter.image(elements: [element], size: CGSize(width: 200, height: 140))
        element.style.setGradient(GradientPreset.spectrum.gradient, fill: false)
        if tool.supportsFill { element.style.setGradient(GradientPreset.sunset.gradient, fill: true) }
        let gradient = try PNGExporter.image(elements: [element], size: CGSize(width: 200, height: 140))
        let solidData = try #require(solid.dataProvider?.data)
        let gradientData = try #require(gradient.dataProvider?.data)
        let before = try #require(CFDataGetBytePtr(solidData))
        let after = try #require(CFDataGetBytePtr(gradientData))
        var largestError = 0
        for y in 0..<140 {
            for x in 0..<200 {
                let solidAlpha = Int(before[y * solid.bytesPerRow + x * 4 + 3])
                let gradientAlpha = Int(after[y * gradient.bytesPerRow + x * 4 + 3])
                largestError = max(largestError, abs(solidAlpha - gradientAlpha))
            }
        }
        #expect(largestError <= 2, "\(tool.rawValue) changed opacity with its gradient")
    }
}
