import Foundation
import Testing
@testable import AnnotationCore

@Test func shiftSnapsLinesToTheNearest45DegreesAndKeepsTheirLength() {
    let start = Point(10, 10)
    let end = DragConstraint.snappedLineEnd(from: start, to: Point(110, 20))
    #expect(abs(end.x - (10 + hypot(100, 10))) < 1e-9)
    #expect(abs(end.y - 10) < 1e-9)
    let diagonal = DragConstraint.snappedLineEnd(from: start, to: Point(-80, -70))
    #expect(abs(diagonal.x - diagonal.y) < 1e-9)
    #expect(abs(start.distance(to: diagonal) - hypot(90, 80)) < 1e-9)
}

@Test func shiftMakesSquaresAndFixedRatiosKeepTheDragDirection() {
    let start = Point(50, 50)
    #expect(DragConstraint.squareEnd(from: start, to: Point(20, 90)) == Point(10, 90))
    #expect(DragConstraint.squareEnd(from: start, to: Point(51, 50)) == Point(51, 51))
    #expect(DragConstraint.aspectEnd(from: start, to: Point(150, 100), ratio: 1.25) == Point(112.5, 100))
    #expect(DragConstraint.aspectEnd(from: start, to: Point(0, -50), ratio: 0.5) == Point(0, -50))
}
