import XCTest
import AnnotationCore
@testable import AnnotatorApp

final class DemoHandMotionTests: XCTestCase {
    func testSeededGestureKeepsEndpointsAndVariesSpeed() {
        var first = DemoHandMotion(seed: 83)
        var replay = DemoHandMotion(seed: 83)
        let start = Point(10, 20), end = Point(310, 20)
        let points = first.path(from: start, to: end, samples: 30, bend: 8)
        XCTAssertEqual(points, replay.path(from: start, to: end, samples: 30, bend: 8))
        XCTAssertEqual(points.first, start)
        XCTAssertEqual(points.last, end)
        XCTAssertTrue(points.allSatisfy { abs($0.y - start.y) <= 10 })
        let steps = zip(points, points.dropFirst()).map { $1.x - $0.x }
        XCTAssertTrue(steps.allSatisfy { $0 > 0 })
        XCTAssertGreaterThan(steps.max()!, steps.min()! * 3)
        var different = DemoHandMotion(seed: 84)
        XCTAssertNotEqual(points, different.path(from: start, to: end, samples: 30, bend: 8))
    }

    func testGestureDurationStaysBounded() {
        var hand = DemoHandMotion(seed: 83)
        let times = (0..<40).map { _ in hand.duration(0.8) }
        XCTAssertTrue(times.allSatisfy { (0.65...0.95).contains($0) })
        XCTAssertGreaterThan(Set(times).count, 30)
    }
}
