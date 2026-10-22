import AppKit
import CoreImage
import ImageIO
import Testing
import AnnotationCore
import PresentationRendering

@Test @MainActor func importedImageOrientationIsApplied() throws {
    let source = try PNGExporter.image(elements: [], size: CGSize(width: 40, height: 20), background: .coral)
    let bytes = NSMutableData()
    let destination = try #require(CGImageDestinationCreateWithData(bytes, "public.jpeg" as CFString, 1, nil))
    CGImageDestinationAddImage(destination, source, [kCGImagePropertyOrientation: 6] as CFDictionary)
    #expect(CGImageDestinationFinalize(destination))
    let oriented = try RasterAsset.decode(bytes as Data)
    #expect(oriented.width == 20)
    #expect(oriented.height == 40)
}

@Test @MainActor func censorsApplyAfterPastedImageLayers() throws {
    let size = CGSize(width: 64, height: 64)
    let background = try PNGExporter.image(elements: [], size: size, background: .white)
    let art = try PNGExporter.image(elements: [], size: CGSize(width: 32, height: 32), background: .coral)
    var image = Element(tool: .image, frame: Rect(x: 16, y: 16, width: 32, height: 32))
    image.imagePNG = try PNGExporter.encode(art)
    var cover = Element(tool: .cover, frame: image.frame)
    cover.style.opacity = 0
    let rendered = try SnapshotExporter.image(source: background, elements: [cover, image])
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 64, height: 64, bitsPerComponent: 8, bytesPerRow: 256,
                                       space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    context.draw(rendered, in: CGRect(origin: .zero, size: size))
    let bytes = try #require(context.data).assumingMemoryBound(to: UInt8.self)
    let center = (32 * 64 + 32) * 4
    #expect(bytes[center] == 0 && bytes[center + 1] == 0 && bytes[center + 2] == 0 && bytes[center + 3] == 255)

    let live = try CaptureCompositor.image(source: CIImage(cgImage: background), logicalSize: size,
                                           elements: [cover, image], regionsOnly: true, includeImageLayers: true)
    let output = try #require(CIContext().createCGImage(live, from: live.extent))
    context.clear(CGRect(origin: .zero, size: size))
    context.draw(output, in: CGRect(origin: .zero, size: size))
    #expect(bytes[center] == 0 && bytes[center + 1] == 0 && bytes[center + 2] == 0 && bytes[center + 3] == 255)
    #expect(bytes[3] == 0)
}
