import Testing
@testable import AnnotationCore

@Test func previewInputIsBoundedAndPreservesEndpoints() {
    let stroke = Element(tool: .pen, points: (0..<200_000).map { Point(Double($0), Double($0 % 100)) })
    let preview = ShapeRecognition.previewInput(stroke)
    #expect(preview.points.count == 256)
    #expect(preview.points.first == stroke.points.first)
    #expect(preview.points.last == stroke.points.last)
    #expect(preview.id == stroke.id)
    #expect(preview.seed == stroke.seed)
    #expect(stroke.points.count == 200_000)
}
