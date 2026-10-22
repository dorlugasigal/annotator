import Foundation
import Testing
@testable import AnnotationCore

@Test func recognitionSharesUndoAndRejectsLateEdits() throws {
    let points = (0...100).map { index in
        let angle = Double(index) / 100 * Double.pi * 2
        return Point(100 + 50 * cos(angle), 100 + 50 * sin(angle))
    }
    let stroke = Element(tool: .pen, points: points)
    var document = CanvasDocument.empty()
    try document.add(stroke)
    let generation = document.generation
    let recognized = try #require(ShapeRecognition.recognize(stroke))
    #expect(try document.applyRecognition(recognized, expectedGeneration: generation, expectedGeometryToken: stroke.geometryToken))
    #expect(document.elements[0].tool == .ellipse)
    document.undo()
    #expect(document.elements.isEmpty)
    document.redo()
    #expect(document.elements[0].tool == .ellipse)

    var edited = CanvasDocument.empty()
    try edited.add(stroke)
    let prior = edited.generation
    edited.select([stroke.id])
    try edited.setSelectionLocked(true)
    #expect(try !edited.applyRecognition(recognized, expectedGeneration: prior, expectedGeometryToken: stroke.geometryToken))
    #expect(edited.elements[0].tool == .pen)
    #expect(edited.elements[0].locked)
}
