import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

private func alphaBytes(_ image: CGImage) throws -> [UInt8] {
    try #require(image.bitsPerPixel == 32)
    let data = try #require(image.dataProvider?.data)
    let bytes = try #require(CFDataGetBytePtr(data))
    return (0..<image.height).flatMap { y in
        (0..<image.width).map { x in bytes[y * image.bytesPerRow + x * 4 + 3] }
    }
}

@Test @MainActor func diamondCornerSettingChangesCurvesFillAndBothExports() throws {
    let frame = Rect(x: 30, y: 30, width: 120, height: 120)
    var sharp = Element(tool: .diamond, frame: frame)
    sharp.style.width = 1
    sharp.style.fill = .blue
    var rounded = sharp
    rounded.style.cornerRadius = 24
    rounded.invalidateGeometry()
    let renderer = SceneRenderer()
    let sharpPath = renderer.path(for: sharp)
    let roundedPath = renderer.path(for: rounded)
    var curves = 0
    roundedPath.applyWithBlock { item in
        if item.pointee.type == .addQuadCurveToPoint { curves += 1 }
    }
    #expect(curves == 4)
    #expect(sharpPath.contains(CGPoint(x: 90, y: 32)))
    #expect(!roundedPath.contains(CGPoint(x: 90, y: 32)))
    #expect(roundedPath.contains(frame.center.cgPoint))
    #expect(frame.cgRect.contains(roundedPath.boundingBoxOfPath))

    let size = CGSize(width: 180, height: 180)
    let sharpImage = try PNGExporter.image(elements: [sharp], size: size)
    let roundedImage = try PNGExporter.image(elements: [rounded], size: size)
    let sharpAlpha = try alphaBytes(sharpImage)
    let roundedAlpha = try alphaBytes(roundedImage)
    #expect(sharpAlpha != roundedAlpha)
    #expect(sharpAlpha.filter { $0 > 0 }.count > roundedAlpha.filter { $0 > 0 }.count)
    #expect(try PNGExporter.encode(sharpImage) != PNGExporter.encode(roundedImage))
    let source = try PNGExporter.image(elements: [], size: size, background: .white)
    let sharpSnapshot = try SnapshotExporter.image(source: source, elements: [sharp])
    let roundedSnapshot = try SnapshotExporter.image(source: source, elements: [rounded])
    #expect(try PNGExporter.encode(sharpSnapshot) != PNGExporter.encode(roundedSnapshot))
}

@Test @MainActor func diamondRoundingIsBoundedForThinAndDegenerateFrames() {
    for frame in [
        Rect(x: 20, y: 20, width: 200, height: 2),
        Rect(x: 20, y: 20, width: 2, height: 200),
        Rect(x: 20, y: 20, width: 0, height: 100),
        Rect(x: 20, y: 20, width: 100, height: 0),
        Rect(x: 20, y: 20, width: 0, height: 0)
    ] {
        let path = DiamondGeometry.path(frame: frame, cornerRadius: 1_000)
        path.applyWithBlock { item in
            let count: Int
            switch item.pointee.type {
            case .moveToPoint, .addLineToPoint: count = 1
            case .addQuadCurveToPoint: count = 2
            case .addCurveToPoint: count = 3
            case .closeSubpath: count = 0
            @unknown default: count = 0
            }
            for index in 0..<count {
                let point = item.pointee.points[index]
                #expect(point.x.isFinite && point.y.isFinite)
                #expect(point.x >= frame.x && point.x <= frame.maxX)
                #expect(point.y >= frame.y && point.y <= frame.maxY)
            }
        }
    }
}

@Test @MainActor func textRenderingMultipliesColorAlphaByObjectOpacity() throws {
    var text = Element(tool: .text, frame: Rect(x: 10, y: 10, width: 180, height: 70), text: "MMMM")
    text.style.fontSize = 40
    text.style.color = .blue
    let size = CGSize(width: 200, height: 100)
    let opaque = try alphaBytes(PNGExporter.image(elements: [text], size: size))
    let opaqueSum = opaque.reduce(0.0) { $0 + Double($1) }
    #expect(opaqueSum > 0)
    for (colorAlpha, opacity) in [(0.0, 1.0), (0.5, 1.0), (0.5, 0.5), (1.0, 0.5)] {
        text.style.color.alpha = colorAlpha
        text.style.opacity = opacity
        let result = try alphaBytes(PNGExporter.image(elements: [text], size: size))
        let ratio = result.reduce(0.0) { $0 + Double($1) } / opaqueSum
        #expect(abs(ratio - colorAlpha * opacity) < 0.02)
        if colorAlpha == 0 { #expect(result.allSatisfy { $0 == 0 }) }
    }
}

@Test @MainActor func measurementOpacityCompositesEveryPaintedPartExactlyOnce() throws {
    let size = CGSize(width: 220, height: 160)
    let counter = Element(tool: .counter, frame: Rect(x: 60, y: 40, width: 80, height: 80), text: "8")
    let ruler = Element(tool: .ruler, points: [Point(30, 100), Point(190, 100)], text: "160 px")
    for original in [counter, ruler] {
        let opaque = try alphaBytes(PNGExporter.image(elements: [original], size: size))
        #expect(opaque.contains(255))
        for opacity in [0.0, 0.5] {
            var translucent = original
            translucent.style.opacity = opacity
            let result = try alphaBytes(PNGExporter.image(elements: [translucent], size: size))
            let largestError = try #require(zip(opaque, result).map { abs(Double($0.1) - Double($0.0) * opacity) }.max())
            #expect(largestError <= 2)
            if opacity == 0 { #expect(result.allSatisfy { $0 == 0 }) }
        }
    }
}

@Test @MainActor func rulerWidthChangesBothShaftAndEndpointMarks() throws {
    let size = CGSize(width: 220, height: 160)
    var ruler = Element(tool: .ruler, points: [Point(30, 100), Point(190, 100)], text: "160 px")
    ruler.style.width = 1
    let thin = try alphaBytes(PNGExporter.image(elements: [ruler], size: size))
    ruler.style.width = 9
    let thick = try alphaBytes(PNGExporter.image(elements: [ruler], size: size))
    func painted(_ alpha: [UInt8], x: Range<Int>) -> Int {
        (0..<160).reduce(0) { sum, row in sum + x.reduce(0) { $0 + Int(alpha[row * 220 + $1]) } }
    }
    #expect(painted(thick, x: 80..<140) > painted(thin, x: 80..<140))
    #expect(painted(thick, x: 22..<38) > painted(thin, x: 22..<38))
}
