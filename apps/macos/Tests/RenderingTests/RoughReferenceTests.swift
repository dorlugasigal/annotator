import Foundation
import CoreGraphics
import Testing
import AnnotationCore
@testable import PresentationRendering

private struct RoughFixture: Decodable {
    struct Operation: Decodable { let op: String; let data: [Double] }
    let shape: String
    let roughness: Double
    let operations: [Operation]
}

@Test @MainActor func roughShapesMatchUpstreamSeededCoordinates() throws {
    let url = try #require(Bundle.module.url(forResource: "roughjs-4.6.6", withExtension: "json"))
    let fixtures = try JSONDecoder().decode([RoughFixture].self, from: Data(contentsOf: url))
    for fixture in fixtures {
        var style = Style(); style.roughness = fixture.roughness; style.width = 3
        var element = Element(tool: fixture.shape == "rectangle" ? .rectangle : .ellipse,
                              frame: Rect(x: 20, y: 30, width: 300, height: 180), style: style)
        element.seed = 42
        let path = SceneRenderer().path(for: element)
        var operations: [(String, [Double])] = []
        path.applyWithBlock { item in
            switch item.pointee.type {
            case .moveToPoint:
                operations.append(("move", [item.pointee.points[0].x, item.pointee.points[0].y]))
            case .addCurveToPoint:
                operations.append(("bcurveTo", (0..<3).flatMap { [item.pointee.points[$0].x, item.pointee.points[$0].y] }))
            default: Issue.record("Unexpected rough geometry operation")
            }
        }
        #expect(operations.count == fixture.operations.count)
        for (actual, expected) in zip(operations, fixture.operations) {
            #expect(actual.0 == expected.op)
            #expect(actual.1.count == expected.data.count)
            for (a, b) in zip(actual.1, expected.data) { #expect(abs(a - b) < 0.000_001) }
        }
    }
}
