import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@Test @MainActor func identicalImagesAcrossObjectsShareOneDecodeAndReleaseOnRemoval() throws {
    let data = try PNGExporter.data(elements: [], size: CGSize(width: 64, height: 64), background: .blue)
    let otherData = Data(Array(data))
    let first = UUID(), second = UUID()
    let cache = DecodedImageCache()
    cache.beginFrame(validIDs: [first, second])
    let one = try cache.image(for: data, elementID: first)
    let two = try cache.image(for: otherData, elementID: second)
    cache.endFrame()
    #expect(one === two)
    #expect(cache.decodeCount == 1)
    #expect(cache.bytes == one.bytesPerRow * one.height)
    cache.beginFrame(validIDs: [])
    cache.endFrame()
    #expect(cache.bytes == 0)
}

@Test @MainActor func changedImageContentInvalidatesOnlyItsAliasAndCacheStaysBounded() throws {
    let blue = try PNGExporter.data(elements: [], size: CGSize(width: 64, height: 64), background: .blue)
    let white = try PNGExporter.data(elements: [], size: CGSize(width: 64, height: 64), background: .white)
    let id = UUID()
    let cache = DecodedImageCache(byteLimit: 20_000)
    cache.beginFrame(validIDs: [id])
    _ = try cache.image(for: blue, elementID: id)
    cache.endFrame()
    cache.beginFrame(validIDs: [id])
    _ = try cache.image(for: white, elementID: id)
    cache.endFrame()
    #expect(cache.decodeCount == 2)
    #expect(cache.bytes <= 20_000)
    let tiny = DecodedImageCache(byteLimit: 1)
    tiny.beginFrame(validIDs: [id])
    #expect(try tiny.image(for: blue, elementID: id).width == 64)
    tiny.endFrame()
    #expect(tiny.bytes == 0)
}
