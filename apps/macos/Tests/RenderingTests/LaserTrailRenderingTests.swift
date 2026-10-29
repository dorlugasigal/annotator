import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

private func laserBytes(_ trail: LaserTrail, at now: Double) throws -> [UInt8] {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 200, height: 200, bitsPerComponent: 8, bytesPerRow: 800,
        space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    LaserTrailRenderer.draw(trail, at: now, in: context)
    let data = try #require(context.makeImage()?.dataProvider?.data)
    let bytes = try #require(CFDataGetBytePtr(data))
    return Array(UnsafeBufferPointer(start: bytes, count: CFDataGetLength(data)))
}

/// A pixel in the bitmap's own rows, which run from the top of the image.
private func pixel(_ bytes: [UInt8], _ x: Int, _ y: Int) -> [UInt8] {
    let offset = ((199 - y) * 200 + x) * 4
    return Array(bytes[offset..<offset + 4])
}

@Test func laserTrailThatCrossesItselfIsNoDarkerAndHasNoHoles() throws {
    var trail = LaserTrail()
    trail.press(at: Point(20, 100), time: 0, color: RGBA(1, 0, 0), width: 12)
    // Right, then up through the first part and back down across it.
    let path = [(180.0, 100.0), (100, 180), (100, 20)]
    for (index, point) in path.enumerated() { trail.move(to: Point(point.0, point.1), time: 0.01 * Double(index + 1)) }
    let bytes = try laserBytes(trail, at: 0.04)
    let crossing = pixel(bytes, 100, 100)
    let single = pixel(bytes, 140, 100)
    #expect(crossing[3] > 200)
    #expect(crossing == single, "One fill per layer: a crossing looks like the rest of the line")
    // Every pass winds the same way, so overlapping circles and bands never cancel into a hole.
    for x in stride(from: 60, through: 170, by: 5) { #expect(pixel(bytes, x, 100)[3] > 200, "x = \(x)") }
    #expect(pixel(bytes, 5, 5)[3] == 0)
}

@Test func laserTrailHasAWhiteCoreAndFadesWithItsOpacity() throws {
    var trail = LaserTrail()
    trail.press(at: Point(40, 100), time: 0, color: RGBA(1, 0, 0), width: 16)
    trail.move(to: Point(160, 100), time: 0.01)
    let held = try laserBytes(trail, at: 0.01)
    let core = pixel(held, 159, 100)
    #expect(core[1] > 150 && core[2] > 150, "The center of the line is white-hot")
    let edge = pixel(held, 159, 106)
    #expect(edge[0] > 200 && edge[1] < 60, "The line around the core keeps its color")
    trail.release(at: nil, time: 0.01)
    let fading = try laserBytes(trail, at: 0.41)
    #expect(pixel(fading, 159, 100)[3] < core[3])
    #expect(try laserBytes(trail, at: 0.9).allSatisfy { $0 == 0 })
}
