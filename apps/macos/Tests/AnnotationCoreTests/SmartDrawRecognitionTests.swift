import Foundation
import Testing
@testable import AnnotationCore

private func traced(_ vertices: [Point], noise: Double = 0, gap: Point = .zero) -> [Point] {
    var output: [Point] = []
    for index in 1..<vertices.count {
        let steps = index.isMultiple(of: 2) ? 53 : 13
        for step in 0..<steps {
            let t = Double(step) / Double(steps)
            let point = vertices[index - 1] + (vertices[index] - vertices[index - 1]) * t
            let wave = Double(output.count)
            output.append(point + Point(sin(wave * 0.53), cos(wave * 0.47)) * noise)
        }
    }
    if let last = vertices.last { output.append(last + gap) }
    return output
}

@Test(arguments: [0.0, 5.0, 10.0])
func smartDrawRecognizesUnevenWobblyRectanglesAndDiamonds(_ noise: Double) throws {
    let cases: [(Tool, [Point])] = [
        (.rectangle, [Point(40, 50), Point(260, 55), Point(255, 210), Point(35, 205), Point(40, 50)]),
        (.diamond, [Point(160, 40), Point(275, 130), Point(150, 220), Point(40, 135), Point(160, 40)])
    ]
    for (tool, vertices) in cases {
        let stroke = Element(tool: .pen, points: traced(vertices, noise: noise, gap: Point(14, -9)))
        let result = try #require(ShapeRecognition.recognize(stroke))
        #expect(result.tool == tool)
        #expect(result.id == stroke.id)
        try result.validate()
    }
}

@Test func smartDrawRecognizesLooseIncompleteEllipseAndTiltedRectangle() throws {
    let ellipse = (0...110).map { index in
        let t = Double(index) / 120 * 2 * Double.pi
        return Point(150 + 95 * cos(t) + 7 * sin(3 * t), 150 + 65 * sin(t) + 5 * cos(4 * t))
    }
    #expect(ShapeRecognition.recognize(Element(tool: .pen, points: ellipse))?.tool == .ellipse)
    let box = [Point(50, 50), Point(280, 50), Point(280, 190), Point(50, 190), Point(50, 50)]
    let tilted = traced(box, noise: 4).map { $0.rotated(around: Point(165, 120), by: 0.22) }
    let result = try #require(ShapeRecognition.recognize(Element(tool: .pen, points: tilted)))
    #expect(result.tool == .rectangle)
    #expect(abs(result.rotation - 0.22) < 0.12)
}

@Test(arguments: [0.0, Double.pi / 2, Double.pi, -Double.pi / 4])
func smartDrawRecognizesArrowsInBothStrokeDirections(_ angle: Double) throws {
    let vertices = [Point(0, 0), Point(200, 0), Point(160, -32), Point(200, 0), Point(163, 28)]
    let points = traced(vertices, noise: 2).map { $0.rotated(around: .zero, by: angle) + Point(300, 300) }
    for input in [points, Array(points.reversed())] {
        var stroke = Element(tool: .pen, points: input)
        stroke.groupID = UUID()
        stroke.locked = true
        stroke.style.color = .blue
        stroke.style.width = 5
        let arrow = try #require(ShapeRecognition.recognize(stroke))
        #expect(arrow.tool == .arrow)
        #expect(arrow.points.count == 2)
        #expect(arrow.points[0].distance(to: Point(300, 300)) < 6)
        #expect(arrow.points[1].distance(to: Point(200, 0).rotated(around: .zero, by: angle) + Point(300, 300)) < 6)
        #expect(arrow.style.endHead == .open)
        #expect(arrow.style.color == stroke.style.color)
        #expect(arrow.style.width == stroke.style.width)
        #expect(arrow.id == stroke.id && arrow.groupID == stroke.groupID && arrow.seed == stroke.seed && arrow.locked)
        try arrow.validate()
    }
}

@Test func smartDrawRejectsOpenMarksScribblesAndRepeatedLoops() {
    let marks = [
        traced([Point(0, 0), Point(200, 0)], noise: 2),
        traced([Point(0, 0), Point(200, 0), Point(200, 120)]),
        traced([Point(0, 0), Point(200, 0), Point(200, -40), Point(200, 40)]),
        traced([Point(0, 0), Point(50, 80), Point(200, -40)]),
        traced([Point(100, 20), Point(200, 200), Point(0, 200), Point(100, 20)]),
        (0...160).map { i in Point(150 + 80 * sin(Double(i) / 160 * 2 * .pi), 150 + 60 * sin(Double(i) / 160 * 4 * .pi)) },
        (0...160).map { i in Point(150 + 80 * cos(Double(i) / 160 * 4 * .pi), 150 + 60 * sin(Double(i) / 160 * 4 * .pi)) }
    ]
    for points in marks {
        #expect(ShapeRecognition.recognize(Element(tool: .pen, points: points)) == nil)
    }
}

@Test(arguments: [12, 24, 53, 97])
func smartDrawRecognizesEvenlySampledRetracedArrow(_ steps: Int) throws {
    let vertices = [Point(80, 170), Point(330, 170), Point(280, 130), Point(330, 170), Point(280, 210)]
    var points = [vertices[0]]
    for index in 1..<vertices.count {
        for step in 1...steps {
            points.append(vertices[index - 1] + (vertices[index] - vertices[index - 1]) * (Double(step) / Double(steps)))
        }
    }
    for input in [points, Array(points.reversed())] {
        let arrow = try #require(ShapeRecognition.recognize(Element(tool: .pen, points: input)))
        #expect(arrow.tool == .arrow)
        #expect(arrow.points[0].distance(to: vertices[0]) < 6)
        #expect(arrow.points[1].distance(to: vertices[1]) < 6)
    }
}

@Test func recognizedDiamondAndArrowShareOneUndoStepAndPreviewClassification() throws {
    let examples = [
        traced([Point(100, 0), Point(200, 100), Point(100, 200), Point(0, 100), Point(100, 0)], noise: 3),
        traced([Point(0, 0), Point(200, 0), Point(160, -32), Point(200, 0), Point(160, 32)], noise: 2)
    ]
    for points in examples {
        let stroke = Element(tool: .pen, points: points)
        let recognized = try #require(ShapeRecognition.recognize(stroke))
        #expect(ShapeRecognition.recognize(ShapeRecognition.previewInput(stroke))?.tool == recognized.tool)
        var document = CanvasDocument.empty()
        try document.add(stroke)
        let generation = document.generation
        #expect(try document.applyRecognition(recognized, expectedGeneration: generation, expectedGeometryToken: stroke.geometryToken))
        document.undo()
        #expect(document.elements.isEmpty)
        document.redo()
        #expect(document.elements.count == 1)
        #expect(document.elements[0].tool == recognized.tool)
    }
}
