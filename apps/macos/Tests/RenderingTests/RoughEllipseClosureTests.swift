import Foundation
import AppKit
import CoreGraphics
import Testing
import AnnotationCore
@testable import PresentationRendering

private struct EllipseOutlineSamples {
    var points: [Point] = []
    var moves = 0
    var cubics = 0

    init(_ path: CGPath) {
        var current = Point.zero
        var first = Point.zero
        var sampledPoints: [Point] = []
        var moveCount = 0
        var cubicCount = 0
        path.applyWithBlock { item in
            let operation = item.pointee
            switch operation.type {
            case .moveToPoint:
                current = Point(operation.points[0])
                first = current
                sampledPoints.append(current)
                moveCount += 1
            case .addCurveToPoint:
                let start = current
                let one = Point(operation.points[0])
                let two = Point(operation.points[1])
                let end = Point(operation.points[2])
                for index in 1...64 {
                    let t = Double(index) / 64, u = 1 - t
                    sampledPoints.append(start * (u * u * u) + one * (3 * u * u * t)
                                         + two * (3 * u * t * t) + end * (t * t * t))
                }
                current = end
                cubicCount += 1
            case .addLineToPoint, .closeSubpath:
                let end = operation.type == .closeSubpath ? first : Point(operation.points[0])
                for index in 1...64 {
                    sampledPoints.append(current + (end - current) * (Double(index) / 64))
                }
                current = end
            default:
                Issue.record("Unexpected ellipse path operation")
            }
        }
        points = sampledPoints
        moves = moveCount
        cubics = cubicCount
    }

    func staysNearPerimeter(of frame: Rect) -> Bool {
        !points.isEmpty && points.allSatisfy { point in
            let radius = hypot((point.x - frame.center.x) / (frame.width / 2),
                               (point.y - frame.center.y) / (frame.height / 2))
            return radius.isFinite && (0.85...1.15).contains(radius)
        }
    }
}

@Test @MainActor func roughEllipseClosureStaysOnPerimeterAcrossSeedsAndSizes() {
    let renderer = SceneRenderer()
    for size in [Point(600, 550), Point(300, 180), Point(1_200, 1_000)] {
        for roughness in [0.0, 2, 6] {
            for seed in UInt64(1)...256 {
                var element = Element(tool: .ellipse,
                                      frame: Rect(x: 30, y: 40, width: size.x, height: size.y))
                element.seed = seed
                element.style.roughness = roughness
                let samples = EllipseOutlineSamples(renderer.path(for: element))
                #expect(samples.staysNearPerimeter(of: element.frame),
                        "Ellipse \(size), roughness \(roughness), seed \(seed) crosses its interior or leaves its perimeter")
                #expect(samples.moves == (roughness == 0 ? 1 : 2))
                #expect(samples.cubics >= 4)
            }
        }
    }
}

@Test @MainActor func resizedAndUndoneRoughEllipsesKeepLocalClosingCurves() throws {
    let renderer = SceneRenderer()
    for roughness in [0.0, 2, 6] {
        for seed in [UInt64(1), 2, 42, 93, 116, 255] {
            var element = Element(tool: .ellipse, frame: Rect(x: 30, y: 40, width: 300, height: 180))
            element.seed = seed
            element.style.roughness = roughness
            var document = try CanvasDocument(elements: [element])
            let original = EllipseOutlineSamples(renderer.path(for: element))
            try document.update(element.id) {
                $0.resize(to: Rect(x: 60, y: 80, width: 600, height: 550))
            }
            let resized = try #require(document.elements.first)
            let resizedSamples = EllipseOutlineSamples(renderer.path(for: resized))
            #expect(resizedSamples.staysNearPerimeter(of: resized.frame))
            let builds = renderer.fullPathBuilds
            _ = renderer.path(for: resized)
            #expect(renderer.fullPathBuilds == builds)
            document.undo()
            let restored = try #require(document.elements.first)
            let restoredSamples = EllipseOutlineSamples(renderer.path(for: restored))
            #expect(restoredSamples.points == original.points)
            #expect(restoredSamples.staysNearPerimeter(of: restored.frame))
            document.redo()
            let redone = try #require(document.elements.first)
            #expect(EllipseOutlineSamples(renderer.path(for: redone)).points == resizedSamples.points)
        }
    }
}

@Test @MainActor func largeRoughEllipseExportsWithoutInteriorInk() throws {
    let size = CGSize(width: 660, height: 610)
    for roughness in [0.0, 2, 6] {
        var element = Element(tool: .ellipse, frame: Rect(x: 30, y: 30, width: 600, height: 550))
        element.seed = 116
        element.style.roughness = roughness
        element.style.width = 3
        let png = try PNGExporter.data(elements: [element], size: size)
        let image = try RasterAsset.decode(png)
        let bitmap = NSBitmapImageRep(cgImage: image)
        var interiorInk = false
        for y in stride(from: 200, to: 400, by: 4) {
            for x in stride(from: 210, to: 450, by: 4) {
                let color = try #require(bitmap.colorAt(x: x, y: y))
                if color.alphaComponent > 0.01 { interiorInk = true }
            }
        }
        #expect(!interiorInk, "Unfilled ellipse roughness \(roughness) paints inside its central region")
    }
}
