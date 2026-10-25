import Foundation
import Testing
@testable import AnnotationCore

private func pressures(width: Double = 3, step: Double, interval: Double, count: Int) -> [Double] {
    var ink = InkPressure(width: width)
    return (0..<count).map { ink.next(Point(Double($0) * step, 0), time: Double($0) * interval) }
}

@Test func inkDrawsFastMovementThinnerThanSlowMovement() {
    let slow = pressures(step: 1, interval: 0.016, count: 60)
    let fast = pressures(step: 25, interval: 0.008, count: 60)
    #expect(slow[40] > 0.95)
    #expect(fast[40] < 0.5)
    #expect(fast.allSatisfy { $0 >= InkPressure.thinnest * InkPressure.tip && $0 <= 1 })
}

@Test func inkTapersTheStartAndTheEnd() {
    let drawn = pressures(step: 1, interval: 0.016, count: 60)
    #expect(drawn[0] == InkPressure.tip)
    #expect(drawn[1] < drawn[5] && drawn[5] < drawn[20])
    let points = (0..<60).map { Point(Double($0), 0) }
    let tapered = InkPressure.taperEnd(points: points, pressures: drawn, width: 3)
    #expect(tapered[59] == drawn[59] * InkPressure.tip)
    #expect(tapered[58] < tapered[52] && tapered[52] < tapered[40])
    #expect(Array(tapered.prefix(48)) == Array(drawn.prefix(48)))
}

@Test func inkKeepsEarlierPressuresWhileTheStrokeGrows() {
    let short = pressures(step: 7, interval: 0.009, count: 20)
    let long = pressures(step: 7, interval: 0.009, count: 40)
    #expect(Array(long.prefix(20)) == short)
}

@Test func inkCopesWithSharedAndBackwardTimes() {
    var ink = InkPressure(width: 3)
    let values = [(Point(0, 0), 1.0), (Point(5, 0), 1.0), (Point(10, 0), 0.5), (Point(15, 0), .nan), (Point(20, 0), 2)]
        .map { ink.next($0.0, time: $0.1) }
    #expect(values.allSatisfy { $0.isFinite && (0...1).contains($0) })
}

@Test func inkEndTaperLeavesShortOrMismatchedStrokesAlone() {
    #expect(InkPressure.taperEnd(points: [Point(1, 1)], pressures: [0.8], width: 3) == [0.8])
    #expect(InkPressure.taperEnd(points: [Point(1, 1), Point(2, 2)], pressures: [], width: 3) == [])
    #expect(InkPressure(width: 2).taperLength == 12)
    #expect(InkPressure(width: 10).taperLength == 40)
}
