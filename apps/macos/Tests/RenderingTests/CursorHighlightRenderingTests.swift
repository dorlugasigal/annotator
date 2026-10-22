import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@MainActor
private func haloImage(_ settings: CursorHighlightSettings, progress: Double? = nil, reduced: Bool = false) throws -> CGImage {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8, bytesPerRow: 1024,
        space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    CursorHighlightRenderer.draw(settings, center: CGPoint(x: 128, y: 128), clickProgress: progress,
                                 reduceMotion: reduced, in: context)
    return try #require(context.makeImage())
}

private func haloBytes(_ image: CGImage) throws -> [UInt8] {
    let data = try #require(image.dataProvider?.data)
    let bytes = try #require(CFDataGetBytePtr(data))
    return Array(UnsafeBufferPointer(start: bytes, count: CFDataGetLength(data)))
}

@Test @MainActor func yellowHaloAndGlowContainNoBlackFringe() throws {
    for shape in CursorHighlightSettings.Shape.allCases {
        for glow in [0.0, 12, 24] {
            var settings = CursorHighlightSettings()
            settings.shape = shape
            settings.color = RGBA(1, 0.82, 0, 0.65)
            settings.diameter = 100
            settings.borderWidth = 7
            settings.glowRadius = glow
            let bytes = try haloBytes(haloImage(settings))
            var painted = 0
            for offset in stride(from: 0, to: bytes.count, by: 4) where bytes[offset+3] > 0 {
                painted += 1
                // Premultiplied yellow retains red == alpha, even at its antialiased/glowing edge.
                #expect(abs(Int(bytes[offset]) - Int(bytes[offset+3])) <= 2)
                #expect(abs(Double(bytes[offset+1]) - Double(bytes[offset+3]) * 0.82) <= 2)
                #expect(bytes[offset+2] <= 1)
            }
            #expect(painted > 0)
        }
    }
}

@Test @MainActor func haloShapesAreDistinctAndIdleHasNoClickRing() throws {
    var images = Set<Data>()
    for shape in CursorHighlightSettings.Shape.allCases {
        var settings = CursorHighlightSettings()
        settings.shape = shape; settings.diameter = 74; settings.borderWidth = 7
        let image = try haloImage(settings)
        images.insert(try PNGExporter.encode(image))
        let bytes = try haloBytes(image)
        #expect(bytes[(128 * 256 + 128) * 4 + 3] == (shape == .disc ? 255 : 0))
        #expect(bytes[3] == 0)
    }
    #expect(images.count == CursorHighlightSettings.Shape.allCases.count)
}

@Test @MainActor func glowAddsSoftPixelsAndOpacityAppliesOnce() throws {
    var settings = CursorHighlightSettings()
    settings.diameter = 74; settings.shape = .roundedSquare
    let plain = try haloBytes(haloImage(settings))
    settings.glowRadius = 12
    let glow = try haloBytes(haloImage(settings))
    func painted(_ bytes: [UInt8]) -> Int { stride(from: 3, to: bytes.count, by: 4).filter { bytes[$0] > 0 }.count }
    #expect(painted(glow) > painted(plain))
    settings.color.alpha = 0.34
    let faint = try haloBytes(haloImage(settings))
    let error = stride(from: 3, to: faint.count, by: 4).map { abs(Double(faint[$0]) - Double(glow[$0]) * 0.34) }.max() ?? 0
    #expect(error <= 2)
}

@Test @MainActor func clickEffectsAnimateSelectedShapeAndRespectReducedMotion() throws {
    for effect in [CursorHighlightSettings.ClickEffect.pulse, .ripple, .turn] {
        var settings = CursorHighlightSettings()
        settings.shape = .roundedSquare; settings.diameter = 70; settings.clickEffect = effect
        let idle = try PNGExporter.encode(haloImage(settings))
        let early = try PNGExporter.encode(haloImage(settings, progress: 0.2))
        let late = try PNGExporter.encode(haloImage(settings, progress: 0.7))
        #expect(idle != early)
        #expect(early != late)
        let still = try PNGExporter.encode(haloImage(settings, progress: 0.2, reduced: true))
        #expect(try still == PNGExporter.encode(haloImage(settings, progress: 0.7, reduced: true)))
    }
    var off = CursorHighlightSettings()
    off.clickEffect = .off
    #expect(try PNGExporter.encode(haloImage(off)) == PNGExporter.encode(haloImage(off, progress: 0.5)))
}

@Test func pulseAttacksQuicklyPeaksAtAQuarterAndReleasesToRest() {
    let samples = stride(from: 0.0, through: 1.0, by: 0.01).map { CursorHighlightRenderer.pulseScale($0) }
    let peak = samples.indices.max { samples[$0] < samples[$1] } ?? 0
    #expect(abs(Double(peak) * 0.01 - 0.25) <= 0.011)
    #expect(abs(CursorHighlightRenderer.pulseScale(0.25) - 1.18) < 0.000001)
    #expect(CursorHighlightRenderer.pulseScale(0) == 1)
    #expect(abs(CursorHighlightRenderer.pulseScale(1) - 1) < 0.000001)
    #expect(zip(samples[..<25], samples[1...25]).allSatisfy { $0 < $1 })
    #expect(zip(samples[25..<100], samples[26...]).allSatisfy { $0 >= $1 })
    #expect(CursorHighlightRenderer.pulseScale(0.1) - 1 > 0.18 * 0.75, "The attack reaches most of the peak early.")
    #expect(CursorHighlightRenderer.pulseScale(0.5) - 1 > 0.18 * 0.5, "The release is slower than the attack.")
}
