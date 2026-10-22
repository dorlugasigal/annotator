import AppKit
import Testing
import AnnotationCore
import PresentationRendering

@Test @MainActor func filtersCannotSampleSecretsOutOfASolidCensor() throws {
    func source(_ color: NSColor) throws -> CGImage {
        let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try #require(CGContext(data: nil, width: 64, height: 64, bitsPerComponent: 8,
                                           bytesPerRow: 256, space: space,
                                           bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(NSColor.white.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 64, height: 64))
        context.setFillColor(color.cgColor)
        context.fill(CGRect(x: 24, y: 24, width: 16, height: 16))
        return try #require(context.makeImage())
    }
    let first = try source(.red), second = try source(.blue)
    let cover = Element(tool: .cover, frame: Rect(x: 24, y: 24, width: 16, height: 16))
    for tool in [Tool.blur, .pixelate, .distort] {
        var effect = Element(tool: tool, frame: Rect(x: 8, y: 8, width: 48, height: 48))
        effect.style.effectAmount = 40
        let a = try PNGExporter.encode(SnapshotExporter.image(source: first, elements: [cover, effect]))
        let b = try PNGExporter.encode(SnapshotExporter.image(source: second, elements: [cover, effect]))
        #expect(a == b)
    }
}
