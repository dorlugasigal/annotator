import Testing
@testable import AnnotationCore

@Test func curvedArrowheadsStaySymmetricAndSelectableAtEveryAngle() throws {
    let center = Point(300, 300)
    let original = [Point(80, 60), Point(250, 200), Point(270, 370)]
    for step in 0..<8 {
        let angle = Double(step) * .pi / 4
        for start in [false, true] {
            var element = Element(tool: .arrow, points: original.map { $0.rotated(around: center, by: angle) })
            element.style.curved = true
            element.style.width = 6
            element.style.startHead = start ? .open : .none
            element.style.endHead = start ? .none : .open
            let head = try #require(ArrowHeadGeometry.make(for: element, atStart: start))
            #expect(abs(head.tip.distance(to: head.left) - head.tip.distance(to: head.right)) < 0.0001)
            let midpoint = (head.left + head.right) * 0.5
            #expect(midpoint.distance(to: head.base) < 0.0001)
            #expect(HitTesting.contains(head.left, element: element, tolerance: 0))
            #expect(HitTesting.contains(head.right, element: element, tolerance: 0))
            let chord = start ? element.points[1] : element.points[element.points.count - 2]
            #expect(head.length <= head.tip.distance(to: chord) * 0.7 + 0.0001)
        }
    }
}

@Test func shortArrowheadsDoNotFoldPastTheirShaftAndSquareCapsRotate() throws {
    var arrow = Element(tool: .arrow, points: [Point(20, 20), Point(40, 40)])
    arrow.style.width = 10
    arrow.style.endHead = .square
    let head = try #require(ArrowHeadGeometry.make(for: arrow, atStart: false))
    #expect(head.length <= Point(20, 20).distance(to: Point(40, 40)) * 0.7)
    #expect(abs(head.square[0].x - head.tip.x) < 0.001)
    #expect(head.square[0].y > head.tip.y)
    #expect(head.contains(head.square[0], tolerance: 0.01))
}

@Test func aTightCurveNeverTurnsItsHeadBackwards() throws {
    var arrow = Element(tool: .arrow, points: [Point(0, 100), Point(200, 100), Point(210, 100)])
    arrow.style.curved = true
    let head = try #require(ArrowHeadGeometry.make(for: arrow, atStart: false))
    #expect(head.direction.x > 0.99)
    #expect(abs(head.direction.y) < 0.01)
}
