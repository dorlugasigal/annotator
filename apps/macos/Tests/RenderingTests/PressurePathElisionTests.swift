import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@MainActor private func pressurePixels(_ elements: [Element], renderer: SceneRenderer,
                                      faded: Set<UUID> = [], selected: Set<UUID> = [],
                                      effectsReady: Bool = false, scale: Double = 1) throws -> Data {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8,
                                       bytesPerRow: 1_024, space: space,
                                       bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    context.translateBy(x: 0.25, y: 255.5)
    context.scaleBy(x: scale, y: -scale)
    context.clip(to: CGRect(x: 8, y: 8, width: 220, height: 220))
    renderer.draw(elements, selection: selected, in: context, pixelsAvailableForBlur: effectsReady,
                  fadedIDs: faded)
    return Data(bytes: try #require(context.data), count: context.bytesPerRow * context.height)
}

@Test @MainActor func pressureRenderingDoesNotBuildAnUnusedWholeStrokePath() throws {
    var stroke = Element(tool: .pen, points: (0..<2_000).map {
        Point(20 + Double($0) * 0.1, 100 + sin(Double($0) * 0.01) * 50)
    })
    stroke.style.pressure = true
    stroke.style.roughness = 6
    stroke.pressures = stroke.points.indices.map { Double($0 % 101) / 100 }
    let renderer = SceneRenderer()
    let original = try pressurePixels([stroke], renderer: renderer)
    #expect(original.contains { $0 != 0 })
    #expect(renderer.fullPathBuilds == 0)
    #expect(renderer.incrementalAppends == 0)
    stroke.points.append(Point(220, 220))
    stroke.pressures.append(0.8)
    stroke.geometryToken = UUID()
    let appended = try pressurePixels([stroke], renderer: renderer)
    #expect(appended != original)
    #expect(appended == (try pressurePixels([stroke], renderer: SceneRenderer())))
    #expect(renderer.fullPathBuilds == 0)
    #expect(stroke.points.count == 2_001 && stroke.pressures.count == 2_001)
}

@Test @MainActor func pressureModeTransitionsDoNotReuseAbandonedGeometry() throws {
    var stroke = Element(tool: .pen, points: [Point(20, 30), Point(100, 180), Point(180, 40)])
    stroke.pressures = [0.2, 0.8, 0.4]
    stroke.style.opacity = 0.5
    stroke.rotation = 0.2
    let renderer = SceneRenderer()
    _ = try pressurePixels([stroke], renderer: renderer)
    stroke.style.pressure = true
    stroke.points.append(Point(210, 160))
    stroke.pressures.append(1)
    stroke.geometryToken = UUID()
    for pressure in [true, false, true] {
        stroke.style.pressure = pressure
        for scale in [1.0, 1.25, 2.0] {
            for faded in [Set<UUID>(), [stroke.id]] {
                for effectsReady in [false, true] {
                    let effect = Element(tool: .blur, frame: Rect(x: 100, y: 100, width: 40, height: 40))
                    let cover = Element(tool: .cover, frame: Rect(x: 60, y: 60, width: 30, height: 30))
                    let elements = [cover, stroke, effect]
                    let actual = try pressurePixels(elements, renderer: renderer, faded: faded,
                                                    selected: [stroke.id], effectsReady: effectsReady, scale: scale)
                    let expected = try pressurePixels(elements, renderer: SceneRenderer(), faded: faded,
                                                      selected: [stroke.id], effectsReady: effectsReady, scale: scale)
                    #expect(actual == expected)
                }
            }
        }
    }
    renderer.reset()
    let blank = try pressurePixels([], renderer: renderer)
    #expect(blank.allSatisfy { $0 == 0 })
}

@Test @MainActor func singlePointInkDoesNotBuildAPathBeforeItCanBeStroked() throws {
    for tool in [Tool.pen, .highlighter] {
        var stroke = Element(tool: tool, points: [Point(60, 60)])
        let renderer = SceneRenderer()
        _ = try pressurePixels([stroke], renderer: renderer)
        #expect(renderer.fullPathBuilds == 0)
        stroke.points.append(Point(100, 100))
        stroke.geometryToken = UUID()
        #expect(try pressurePixels([stroke], renderer: renderer)
                == pressurePixels([stroke], renderer: SceneRenderer()))
        #expect(renderer.fullPathBuilds == 1)
    }
}
