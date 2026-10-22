import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@MainActor
private func pressureClipPixels(_ stroke: Element, scale: Double, clip: CGRect, reference: Bool) throws -> Data {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let context = try #require(CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8,
                                       bytesPerRow: 1024, space: space,
                                       bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
    context.translateBy(x: 0.25, y: 255.5)
    context.scaleBy(x: scale, y: -scale)
    context.clip(to: clip)
    if reference {
        context.translateBy(x: stroke.frame.center.x, y: stroke.frame.center.y)
        context.rotate(by: stroke.rotation)
        context.translateBy(x: -stroke.frame.center.x, y: -stroke.frame.center.y)
        context.setAlpha(stroke.style.opacity)
        context.setStrokeColor(stroke.style.color.nsColor.cgColor)
        context.setLineCap(.round)
        context.setLineJoin(.round)
        switch stroke.style.pattern {
        case .solid: context.setLineDash(phase: 0, lengths: [])
        case .dashed: context.setLineDash(phase: 0, lengths: [stroke.style.width * 5, stroke.style.width * 3])
        case .dotted: context.setLineDash(phase: 0, lengths: [0.01, stroke.style.width * 3])
        }
        for index in stroke.points.indices.dropFirst() {
            context.setLineWidth(stroke.style.width * max(0.15, stroke.pressures[index]))
            context.move(to: stroke.points[index - 1].cgPoint)
            context.addLine(to: stroke.points[index].cgPoint)
            context.strokePath()
        }
    } else {
        let renderer = SceneRenderer()
        renderer.draw([stroke], in: context)
        #expect(renderer.fullPathBuilds == 0)
    }
    context.flush()
    return Data(bytes: try #require(context.data), count: context.bytesPerRow * context.height)
}

@Test @MainActor func clippedPressurePixelsMatchUnculledReference() throws {
    var stroke = Element(tool: .pen, points: (0..<2_000).map {
        Point(Double($0) * 0.4 - 100, 120 + sin(Double($0) * 0.037) * 110)
    })
    stroke.style.pressure = true
    stroke.style.width = 12
    stroke.style.opacity = 0.45
    stroke.style.color = RGBA(0.2, 0.5, 0.8, 0.6)
    stroke.pressures = stroke.points.indices.map { Double($0 % 101) / 100 }
    for scale in [0.02, 0.75, 1.0, 2.0] {
        for rotation in [0.0, 0.37, -0.9] {
            stroke.rotation = rotation
            for pattern in StrokePattern.allCases {
                stroke.style.pattern = pattern
                for clip in [CGRect(x: 40, y: 40, width: 170, height: 170),
                             CGRect(x: 95.25, y: 100.75, width: 3.5, height: 4.5)] {
                    let expected = try pressureClipPixels(stroke, scale: scale, clip: clip, reference: true)
                    let actual = try pressureClipPixels(stroke, scale: scale, clip: clip, reference: false)
                    #expect(actual == expected)
                }
            }
        }
    }
}
