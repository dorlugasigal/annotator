import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

private enum StrokeRestore: CaseIterable {
    case cancel, undo, redo
}

@Test(arguments: [Tool.pen, .highlighter], StrokeRestore.allCases)
@MainActor private func strokeHistoryCacheRegression(
    tool: Tool, restoration: StrokeRestore
) throws {
    let renderer = SceneRenderer()
    let stroke = Element(tool: tool, points: [Point(20, 20), Point(40, 20)])
    var document = try CanvasDocument(elements: [stroke])

    if restoration == .redo {
        document.begin()
        try document.appendSample(to: stroke.id, point: Point(60, 20))
        try document.appendSample(to: stroke.id, point: Point(80, 20))
        try document.commit()
        document.undo()
    }
    document.begin()
    try document.appendSample(to: stroke.id, point: Point(40, 100))
    _ = try strokePixels(document.elements, renderer: renderer)
    if restoration == .undo {
        try document.commit()
        document.undo()
    } else {
        document.cancel()
    }
    #expect(document.elements == [stroke])
    if restoration == .redo {
        document.redo()
    } else {
        document.begin()
        try document.appendSample(to: stroke.id, point: Point(60, 20))
        try document.appendSample(to: stroke.id, point: Point(80, 20))
        try document.commit()
    }

    let actual = try strokePixels(document.elements, renderer: renderer)
    let expected = try strokePixels(document.elements, renderer: SceneRenderer())
    #expect(actual == expected, "Restoring without an intervening draw must not retain abandoned ink")
    var vertices: [Point] = []
    renderer.path(for: document.elements[0]).applyWithBlock { item in
        vertices.append(Point(item.pointee.points[0]))
    }
    #expect(vertices == document.elements[0].points)
    #expect(renderer.fullPathBuilds == 2)

    try document.appendSample(to: stroke.id, point: Point(100, 20))
    let continued = try strokePixels(document.elements, renderer: renderer)
    #expect(continued == (try strokePixels(document.elements, renderer: SceneRenderer())))
    let builds = renderer.fullPathBuilds
    let appends = renderer.incrementalAppends
    try document.appendSample(to: stroke.id, point: Point(120, 20))
    let next = try strokePixels(document.elements, renderer: renderer)
    #expect(next == (try strokePixels(document.elements, renderer: SceneRenderer())))
    #expect(renderer.fullPathBuilds == builds)
    #expect(renderer.incrementalAppends == appends + 1)
}

@MainActor private func strokePixels(_ elements: [Element], renderer: SceneRenderer) throws -> Data {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 128, height: 128, bitsPerComponent: 8,
                                       bytesPerRow: 0, space: space,
                                       bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    renderer.draw(elements, in: context)
    let bytes = try #require(context.data)
    return Data(bytes: bytes, count: context.bytesPerRow * context.height)
}
