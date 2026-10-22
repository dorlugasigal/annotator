import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@Test @MainActor func fadingPaintsOnlyTheObjectNotItsBoundingBox() throws {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 120, height: 120, bitsPerComponent: 8, bytesPerRow: 480,
                                       space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    var rectangle = Element(tool: .rectangle, frame: Rect(x: 30, y: 30, width: 60, height: 60))
    rectangle.style.width = 4
    SceneRenderer().draw([rectangle], in: context, fadedIDs: [rectangle.id])
    let pixels = try #require(context.data).assumingMemoryBound(to: UInt8.self)
    let stroke = pixels[(60 * 120 + 30) * 4 + 3]
    #expect((40...70).contains(Int(stroke)))
    #expect(pixels[(60 * 120 + 60) * 4 + 3] == 0)
    context.clear(CGRect(x: 0, y: 0, width: 120, height: 120))
    let censor = Element(tool: .cover, frame: Rect(x: 30, y: 30, width: 60, height: 60))
    SceneRenderer().draw([censor], in: context, fadedIDs: [censor.id])
    #expect(pixels[(60 * 120 + 60) * 4 + 3] == 255)
}
