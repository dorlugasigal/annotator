import Testing
import Foundation
@testable import AnnotationCore

@Test func recognitionIsBoundedAndPreservesIdentity() throws {
    let points = (0...1_000).map { index in
        let angle = Double(index) / 1_000 * 2 * Double.pi
        return Point(100 + 80 * cos(angle), 100 + 60 * sin(angle))
    }
    let stroke = Element(tool: .pen, points: points)
    let recognized = try #require(ShapeRecognition.recognize(stroke))
    #expect(recognized.tool == .ellipse)
    #expect(recognized.id == stroke.id)
    #expect(recognized.seed == stroke.seed)
    #expect(ShapeRecognition.recognize(Element(tool: .pen, points: [Point(0, 0), Point(100, 100)])) == nil)
}

@Test func appendingImportedFreehandKeepsPressureAligned() throws {
    let element = Element(tool: .pen, points: [Point(0, 0), Point(10, 10)])
    var document = try CanvasDocument(elements: [element])
    try document.appendSample(to: element.id, point: Point(20, 20), pressure: 0.5)
    try document.elements[0].validate()
    #expect(document.elements[0].pressures == [1, 1, 0.5])
}
