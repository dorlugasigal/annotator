import AppKit
import CoreImage
import Testing
import AnnotationCore
import PresentationRendering

@Test @MainActor func zeroStrengthBypassesFiltersWithoutErasingOtherProtection() throws {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let bitmap = try #require(CGContext(data: nil, width: 64, height: 64, bitsPerComponent: 8, bytesPerRow: 256,
        space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    bitmap.setFillColor(NSColor.white.cgColor)
    bitmap.fill(CGRect(x: 0, y: 0, width: 64, height: 64))
    bitmap.setFillColor(NSColor.black.cgColor)
    bitmap.fill(CGRect(x: 0, y: 0, width: 32, height: 64))
    let source = CIImage(cgImage: try #require(bitmap.makeImage()))
    let context = CIContext()
    func rendered(_ elements: [Element], regionsOnly: Bool = false) throws -> Data {
        let result = try CaptureCompositor.image(source: source, logicalSize: CGSize(width: 64, height: 64),
            elements: elements, regionsOnly: regionsOnly)
        return try PNGExporter.encode(#require(context.createCGImage(result, from: source.extent)))
    }
    var blur = Element(tool: .blur, frame: Rect(x: 0, y: 0, width: 64, height: 64))
    blur.style.effectAmount = 12
    let cover = Element(tool: .cover, frame: Rect(x: 16, y: 16, width: 32, height: 32))
    for tool in [Tool.blur, .pixelate] {
        var zero = Element(tool: tool, frame: Rect(x: 0, y: 0, width: 64, height: 64))
        zero.style.effectAmount = 0
        try zero.validate()
        #expect(try rendered([zero]) == rendered([]))
        #expect(try rendered([zero], regionsOnly: true) == rendered([], regionsOnly: true))
        #expect(try rendered([blur, zero]) == rendered([blur]))
        #expect(try rendered([blur, zero], regionsOnly: true) == rendered([blur], regionsOnly: true))
        #expect(try rendered([cover, zero]) == rendered([cover]))
        let restored = try DrawingFile.read(DrawingFile(document: CanvasDocument(elements: [zero])).data())
        #expect(restored.elements[0].style.effectAmount == 0)
        zero.style.effectAmount = -0.1
        #expect(throws: CanvasError.self) { try zero.validate() }
    }
}

@Test @MainActor func changingBlurStrengthChangesTheRenderedRegion() throws {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 128, height: 32, bitsPerComponent: 8, bytesPerRow: 512,
                                       space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    context.setFillColor(NSColor.black.cgColor)
    context.fill(CGRect(x: 0, y: 0, width: 128, height: 32))
    context.setFillColor(NSColor.white.cgColor)
    context.fill(CGRect(x: 64, y: 0, width: 64, height: 32))
    let source = try #require(context.makeImage())
    var level = Element(tool: .blur, frame: Rect(x: 0, y: 0, width: 128, height: 32))
    var samples: [UInt8] = []
    for strength in [4.0, 40] {
        level.style.effectAmount = strength
        let output = try SnapshotExporter.flattenedSource(source, elements: [level])
        context.clear(CGRect(x: 0, y: 0, width: 128, height: 32))
        context.draw(output, in: CGRect(x: 0, y: 0, width: 128, height: 32))
        let bytes = try #require(context.data).assumingMemoryBound(to: UInt8.self)
        samples.append(bytes[(16 * 128 + 48) * 4])
    }
    #expect(Int(samples[1]) > Int(samples[0]) + 20)
}
