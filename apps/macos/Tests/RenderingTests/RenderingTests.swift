import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@Test @MainActor func multipleSelectionDrawsSharedHandlesInBothRenderPaths() throws {
    let first = Element(tool: .rectangle, frame: Rect(x: 30, y: 70, width: 50, height: 40))
    let second = Element(tool: .ellipse, frame: Rect(x: 170, y: 110, width: 50, height: 40))
    let ids: Set<UUID> = [first.id, second.id]
    for overlayOnly in [false, true] {
        let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try #require(CGContext(data: nil, width: 260, height: 200, bitsPerComponent: 8,
            bytesPerRow: 1040, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.translateBy(x: 0, y: 200)
        context.scaleBy(x: 1, y: -1)
        let renderer = SceneRenderer()
        if overlayOnly { renderer.selection([first, second], ids: ids, in: context) }
        else { renderer.draw([first, second], selection: ids, in: context) }
        let data = try #require(context.data).assumingMemoryBound(to: UInt8.self)
        // These handles are on the shared bounds, away from either object's handles.
        for point in [Point(220, 70), Point(30, 150), Point(125, 46)] {
            #expect(data[(Int(point.y) * 260 + Int(point.x)) * 4 + 3] > 0)
        }
    }
}

@Test @MainActor func exportsRealPNGAndFlattensOpaqueCover() throws {
    var style = Style()
    style.opacity = 0
    style.color = RGBA(1, 0, 0, 0)
    let cover = Element(tool: .cover, frame: Rect(x: 0, y: 0, width: 10, height: 10), style: style)
    let image = try PNGExporter.image(elements: [cover], size: CGSize(width: 10, height: 10))
    let provider = try #require(image.dataProvider?.data)
    let bytes = try #require(CFDataGetBytePtr(provider))
    #expect(bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 255)
    let png = try PNGExporter.data(elements: [cover], size: CGSize(width: 10, height: 10))
    #expect(Array(png.prefix(8)) == [137, 80, 78, 71, 13, 10, 26, 10])
}

@Test @MainActor func unchangedPathsAreCachedAndAppended() throws {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 200, height: 200, bitsPerComponent: 8,
                                       bytesPerRow: 0, space: space,
                                       bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    let renderer = SceneRenderer()
    var document = CanvasDocument.empty()
    let pen = Element(tool: .pen)
    document.begin()
    try document.add(pen)
    try document.appendSample(to: pen.id, point: Point(10, 10))
    try document.appendSample(to: pen.id, point: Point(20, 20))
    renderer.draw(document.elements, in: context)
    let builds = renderer.fullPathBuilds
    renderer.draw(document.elements, in: context)
    #expect(renderer.fullPathBuilds == builds)
    try document.appendSample(to: pen.id, point: Point(30, 30))
    renderer.draw(document.elements, in: context)
    #expect(renderer.incrementalAppends == 1)
}

@Test @MainActor func seededShapesAreStable() throws {
    var element = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 100, height: 80))
    element.style.roughness = 4
    let a = try PNGExporter.data(elements: [element], size: CGSize(width: 160, height: 140))
    let b = try PNGExporter.data(elements: [element], size: CGSize(width: 160, height: 140))
    #expect(a == b)
    #expect(throws: RenderFailure.self) {
        try PNGExporter.data(elements: [], size: CGSize(width: 100_000, height: 100_000))
    }

}

@Test @MainActor func undoBranchDoesNotReuseStaleGeometry() throws {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 200, height: 200, bitsPerComponent: 8,
                                       bytesPerRow: 0, space: space,
                                       bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    let renderer = SceneRenderer()
    let shape = Element(tool: .rectangle, frame: Rect(x: 10, y: 10, width: 40, height: 40))
    var document = try CanvasDocument(elements: [shape])
    try document.update(shape.id) { $0.frame.width = 60 }
    renderer.draw(document.elements, in: context)
    document.undo()
    try document.update(shape.id) { $0.frame.height = 90 }
    let count = renderer.fullPathBuilds
    renderer.draw(document.elements, in: context)
    #expect(renderer.fullPathBuilds == count + 1)
}

@Test @MainActor func selectedArrowShowsPointHandlesWithoutABoundingBox() throws {
    let arrow = Element(tool: .arrow, points: [Point(30, 40), Point(130, 150), Point(230, 60)])
    var locked = arrow
    locked.locked = true
    for (element, expectsBox) in [(arrow, false), (locked, true)] {
        let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try #require(CGContext(data: nil, width: 260, height: 200, bitsPerComponent: 8,
            bytesPerRow: 1040, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.translateBy(x: 0, y: 200)
        context.scaleBy(x: 1, y: -1)
        SceneRenderer().selection([element], ids: [element.id], in: context)
        let data = try #require(context.data).assumingMemoryBound(to: UInt8.self)
        func alpha(_ x: Int, _ y: Int) -> UInt8 { data[(y * 260 + x) * 4 + 3] }
        // The box would run along y = 37 and x = 233; the arrow itself is not drawn by selection().
        #expect((alpha(100, 37) > 0) == expectsBox)
        #expect((alpha(233, 120) > 0) == expectsBox)
        #expect((alpha(130, 150) > 0) == !element.locked, "Unlocked arrows keep their point handles.")
    }
}
