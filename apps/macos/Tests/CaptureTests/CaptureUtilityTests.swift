import AppKit
import CoreImage
import Testing
import AnnotationCore
import PresentationRendering
import NativePlatform

private func checker() throws -> CGImage {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 64, height: 64, bitsPerComponent: 8, bytesPerRow: 256,
                                       space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    for y in 0..<32 {
        for x in 0..<32 {
            let white = (x + y) % 2 == 0
            context.setFillColor(CGColor(gray: white ? 1 : 0, alpha: 1))
            context.fill(CGRect(x: x * 2, y: y * 2, width: 2, height: 2))
        }
    }
    return try #require(context.makeImage())
}

private func pixels(_ image: CGImage) throws -> [UInt8] {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                       bytesPerRow: image.width * 4, space: space,
                                       bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    let data = try #require(context.data).assumingMemoryBound(to: UInt8.self)
    return Array(UnsafeBufferPointer(start: data, count: image.width * image.height * 4))
}

@Test @MainActor func visualEffectsChangeOnlySelectedPixels() throws {
    let source = try checker()
    let original = try pixels(source)
    for tool in [Tool.blur, .pixelate, .distort] {
        var effect = Element(tool: tool, frame: Rect(x: 16, y: 16, width: 32, height: 32))
        effect.style.effectAmount = 25
        let result = try pixels(SnapshotExporter.flattenedSource(source, elements: [effect]))
        #expect(result != original)
        #expect(Array(result.prefix(4)) == Array(original.prefix(4)))
        #expect(Array(result.suffix(4)) == Array(original.suffix(4)))
    }
}

@Test @MainActor func opaqueCoverWinsAndOverlayOutsideIsTransparent() throws {
    let source = try checker()
    var cover = Element(tool: .cover, frame: Rect(x: 16, y: 16, width: 32, height: 32))
    cover.style.opacity = 0
    let blur = Element(tool: .blur, frame: cover.frame)
    let image = try SnapshotExporter.flattenedSource(source, elements: [cover, blur])
    let bytes = try pixels(image)
    let middle = (32 * 64 + 32) * 4
    #expect(Array(bytes[middle..<(middle + 4)]) == [0, 0, 0, 255])
    let overlay = try CaptureCompositor.image(source: CIImage(cgImage: source),
                                              logicalSize: CGSize(width: 64, height: 64), elements: [blur], regionsOnly: true)
    let rendered = try #require(CIContext().createCGImage(overlay, from: overlay.extent))
    #expect(try pixels(rendered)[3] == 0)
}

@Test @MainActor func cropAndBackdropProduceExactExportDimensions() throws {
    let source = try checker()
    let crop = Rect(x: 8, y: 12, width: 24, height: 16)
    var options = ExportBackdrop()
    options.scale = 2
    let scaled = try SnapshotExporter.image(source: source, elements: [], crop: crop, backdrop: options)
    #expect(scaled.width == 48 && scaled.height == 32)
    options.scale = 1; options.kind = .solid; options.padding = 12
    let framed = try SnapshotExporter.image(source: source, elements: [], crop: crop, backdrop: options)
    #expect(framed.width == 48 && framed.height == 40)
    #expect(throws: RenderFailure.self) {
        try SnapshotExporter.image(source: source, elements: [], crop: Rect(x: 60, y: 60, width: 20, height: 20))
    }
}
