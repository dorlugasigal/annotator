import Foundation
import Testing
import AnnotationCore
@testable import MotionCore

// Windows ports these tests in apps/windows/tests/Annotator.Core.Tests/Tutorial/TutorialTests.cs.

@Test func theGhostArcsAndStaysWithinBounds() {
    let from = Point(100, 100)
    let to = Point(500, 100)
    #expect(TutorialMotion.travelPoint(from: from, to: to, at: 0) == from)
    #expect(TutorialMotion.travelPoint(from: from, to: to, at: 1) == to)
    let middle = TutorialMotion.travelPoint(from: from, to: to, at: 0.5)
    #expect(middle.y > 100, "The arc bends to the right of travel.")
    #expect(TutorialMotion.travelDuration(10) == 0.45)
    #expect(TutorialMotion.travelDuration(5000) == 1.1)
    #expect(TutorialMotion.dragDuration(100) == 0.5)
    #expect(TutorialMotion.dragDuration(480) == 1.0)
    #expect(TutorialMotion.dragDuration(5000) == 1.6)
    #expect(TutorialMotion.travelPoint(from: from, to: from, at: 0.3) == from)
    let smooth = TutorialMotion.smooth([Point(0, 0), Point(50, -40), Point(100, 0)], segments: 10)
    #expect(smooth.count == 21)
    #expect(smooth.last == Point(100, 0))
    #expect(TutorialMotion.loop(center: Point(0, 0), radius: 40).count == 49)
    let head = TutorialMotion.arrowhead(from: Point(0, 0), tip: Point(100, 0))
    #expect(head.left.x < 100 && head.right.x < 100 && head.left.y * head.right.y < 0)
    let barb = ((100 - head.left.x) * (100 - head.left.x) + head.left.y * head.left.y).squareRoot()
    #expect(abs(barb - TutorialMotion.arrowheadLength) < 1e-6)
}

@Test func confettiPopsFromBothBottomCornersAndFallsAway() throws {
    let width = 1440.0, height = 900.0
    let pieces = ConfettiBurst.pieces(width: width, height: height)
    let perCorner = ConfettiBurst.piecesPerCorner
    #expect(pieces.count == perCorner * 2 * ConfettiBurst.waves)
    #expect(pieces == ConfettiBurst.pieces(width: width, height: height))
    #expect(pieces.prefix(perCorner).allSatisfy { $0.start.x < 30 && $0.velocity.x > 0 })
    #expect(pieces.dropFirst(perCorner).prefix(perCorner).allSatisfy { $0.start.x > width - 30 && $0.velocity.x < 0 })
    // The second wave leaves after the first.
    #expect(pieces.dropFirst(perCorner * 2).allSatisfy { $0.delay >= ConfettiBurst.waveGap })
    #expect(pieces.allSatisfy { (0...ConfettiBurst.duration).contains($0.delay + $0.lifetime) })
    var highest = Double.greatestFiniteMagnitude
    var lowestPeak = -Double.greatestFiniteMagnitude
    var farthest = 0.0
    for piece in pieces {
        #expect(ConfettiBurst.state(piece, at: piece.delay - 0.01, height: height) == nil)
        #expect(ConfettiBurst.state(piece, at: piece.delay + piece.lifetime + 0.01, height: height) == nil)
        var peak = Double.greatestFiniteMagnitude
        var time = piece.delay
        while time <= piece.delay + piece.lifetime {
            let state = try #require(ConfettiBurst.state(piece, at: time, height: height))
            peak = min(peak, state.position.y)
            let fromCorner = piece.velocity.x > 0 ? state.position.x : width - state.position.x
            farthest = max(farthest, fromCorner)
            time += 0.05
        }
        highest = min(highest, peak)
        lowestPeak = max(lowestPeak, peak)
        let end = try #require(ConfettiBurst.state(piece, at: piece.delay + piece.lifetime, height: height))
        #expect(abs(end.opacity) < 1e-9)
    }
    // Pieces rise from about a third of the height to about nine tenths, and the farthest reach the middle.
    #expect(highest >= 0 && highest <= height * 0.15)
    #expect(lowestPeak >= height * 0.55 && lowestPeak <= height * 0.7)
    #expect(farthest >= width * 0.45 && farthest <= width * 0.55)
    let first = try #require(ConfettiBurst.state(pieces[0], at: pieces[0].delay, height: height))
    #expect(first.position == pieces[0].start)
}

@Test func splitMixMatchesItsReferenceValues() {
    var random = SplitMix64(seed: 1234567)
    let first = random.next()
    let second = random.next()
    #expect(first == 6457827717110365317)
    #expect(second == 3203168211198807973)
    var zero = SplitMix64(seed: 0)
    let value = zero.nextDouble()
    #expect(value >= 0 && value < 1)
}

@Test func tutorialMotionKeepsItsSharedTimings() {
    #expect(TutorialMotion.pop == CubicBezier(0.34, 1.4, 0.64, 1))
    #expect(TutorialMotion.cardEnterDuration == 0.42 && TutorialMotion.cardExitDuration == 0.2)
    #expect(TutorialMotion.doneBeat == 0.85 && TutorialMotion.quickDoneBeat == 0.45)
    #expect(TutorialMotion.ghostStartDelay == 0.55 && TutorialMotion.hintDelay == 15)
    #expect(ConfettiBurst.seed == 0x5EED_A110_7A7E_C0DE)
    #expect(ConfettiBurst.palette.count == 7 && ConfettiBurst.shapeSizes.count == 3)
}
