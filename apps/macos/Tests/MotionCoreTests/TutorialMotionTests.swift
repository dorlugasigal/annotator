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
}

@Test func confettiPopsFromBothBottomCornersAndFallsAway() throws {
    let width = 1440.0, height = 900.0
    let pieces = ConfettiBurst.pieces(width: width, height: height)
    #expect(pieces.count == ConfettiBurst.piecesPerCorner * 2)
    #expect(pieces == ConfettiBurst.pieces(width: width, height: height))
    #expect(pieces.prefix(ConfettiBurst.piecesPerCorner).allSatisfy { $0.start.x < 30 && $0.velocity.x > 0 })
    #expect(pieces.dropFirst(ConfettiBurst.piecesPerCorner).allSatisfy { $0.start.x > width - 30 && $0.velocity.x < 0 })
    #expect(pieces.allSatisfy { (0...ConfettiBurst.duration).contains($0.delay + $0.lifetime) })
    var highest = Double.greatestFiniteMagnitude
    for piece in pieces {
        #expect(ConfettiBurst.state(piece, at: piece.delay - 0.01, height: height) == nil)
        #expect(ConfettiBurst.state(piece, at: piece.delay + piece.lifetime + 0.01, height: height) == nil)
        var time = piece.delay
        while time <= piece.delay + piece.lifetime {
            let state = try #require(ConfettiBurst.state(piece, at: time, height: height))
            highest = min(highest, state.position.y)
            time += 0.05
        }
        let end = try #require(ConfettiBurst.state(piece, at: piece.delay + piece.lifetime, height: height))
        #expect(abs(end.opacity) < 1e-9)
    }
    #expect(highest >= height * 0.1 && highest <= height * 0.6)
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
