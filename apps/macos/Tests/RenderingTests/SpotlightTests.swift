import CoreGraphics
import Testing
import PresentationRendering

@Test func spotlightEdgesFadeInsteadOfCuttingOff() throws {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 400, height: 400, bitsPerComponent: 8, bytesPerRow: 1600,
                                        space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    try SpotlightRenderer.draw(in: context, bounds: CGRect(x: 0, y: 0, width: 400, height: 400),
                               center: CGPoint(x: 200, y: 200), radius: 100)
    let bytes = try #require(context.data).assumingMemoryBound(to: UInt8.self)
    func alpha(_ x: Int) -> UInt8 { bytes[200 * 1600 + x * 4 + 3] }
    #expect(alpha(200) == 0)
    #expect(alpha(290) > 0)
    #expect(alpha(290) < alpha(300))
    #expect(alpha(300) < alpha(310))
    #expect(alpha(330) >= 155 && alpha(330) <= 160)
}
