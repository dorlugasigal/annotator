import Foundation
import Testing
@testable import AnnotationCore

private func trail(_ points: [(Double, Double, Double)], release: Double? = nil, width: Double = 6) -> LaserTrail {
    var trail = LaserTrail()
    guard let first = points.first else { return trail }
    trail.press(at: Point(first.0, first.1), time: first.2, color: LaserTrail.defaultColor, width: width)
    for point in points.dropFirst() { trail.move(to: Point(point.0, point.1), time: point.2) }
    if let release { trail.release(at: nil, time: release) }
    return trail
}

@Test func laserTrailKeepsTheDotUnderAHeldPointer() {
    let held = trail([(10, 10, 0)])
    for now in [0.0, 0.5, 5] {
        let shapes = held.shapes(at: now)
        #expect(shapes.count == 1)
        #expect(shapes.first?.dots == [LaserTrail.Dot(center: Point(10, 10), radius: 3)])
        #expect(shapes.first?.opacity == 1)
    }
    #expect(held.isResting)
}

@Test func laserTrailNarrowsTowardsItsTailAndFadesBehindThePointer() {
    let moving = trail((0...40).map { (Double($0) * 5, 0, Double($0) * 0.01) })
    let dots = moving.shapes(at: 0.4).first?.dots ?? []
    #expect(dots.first?.radius == 0)
    #expect(dots.last == LaserTrail.Dot(center: Point(200, 0), radius: 3))
    #expect(zip(dots, dots.dropFirst()).allSatisfy { $0.radius <= $1.radius + 1e-9 })
    // 0.8 seconds after the pointer passed a point, that part is gone: the tail is where the pointer was at 0.2 s.
    let later = moving.shapes(at: 1.0).first?.dots ?? []
    #expect(abs((later.first?.center.x ?? 0) - 100) < 1e-9)
    #expect(later.last?.center == Point(200, 0))
}

@Test func laserTrailFadesOutAfterTheButtonGoesUp() {
    var released = trail([(0, 0, 0), (40, 0, 0.1)], release: 0.1)
    #expect(!released.isDrawing)
    #expect(released.shapes(at: 0.5).first.map { abs($0.opacity - 0.5) < 1e-9 } == true)
    released.prune(at: 0.95)
    #expect(released.isEmpty)
    #expect(released.shapes(at: 0.95).isEmpty)
    #expect(released.bounds(at: 0.95) == nil)
}

@Test func laserTrailRemembersARestSoAnOldSegmentStaysFaded() {
    var rested = trail([(0, 0, 0), (50, 0, 0.1)])
    rested.prune(at: 2)
    #expect(rested.strokes.first?.samples.count == 1)
    rested.move(to: Point(60, 0), time: 2)
    let dots = rested.shapes(at: 2.05).first?.dots ?? []
    #expect(dots.allSatisfy { $0.center.x >= 50 })
    #expect(dots.last == LaserTrail.Dot(center: Point(60, 0), radius: 3))
}

@Test func laserTrailStartsANewStrokeForEachPress() {
    var trail = trail([(0, 0, 0), (30, 0, 0.05)], release: 0.05)
    trail.press(at: Point(100, 100), time: 0.2, color: RGBA(0, 0, 1), width: 10)
    trail.move(to: Point(130, 100), time: 0.25)
    let shapes = trail.shapes(at: 0.3)
    #expect(shapes.count == 2)
    #expect(shapes[0].color == LaserTrail.defaultColor && shapes[1].color == RGBA(0, 0, 1))
    #expect(shapes[1].dots.last?.radius == 5)
    #expect(trail.isDrawing)
}

@Test func laserTrailMovesTheNewestPointForTinySteps() {
    let wobble = trail([(0, 0, 0), (10, 0, 0.01), (10.3, 0.2, 0.02), (10.6, 0.1, 0.03)])
    let samples = wobble.strokes.first?.samples ?? []
    #expect(samples.map(\.point) == [Point(0, 0), Point(10, 0), Point(10.6, 0.1)])
}

@Test func laserTrailBoundsIncludeTheGlow() throws {
    let held = trail([(100, 100, 0)])
    let bounds = try #require(held.bounds(at: 0))
    let reach = 3 * 2.8 + 3
    #expect(bounds == Rect(x: 100 - reach, y: 100 - reach, width: reach * 2, height: reach * 2))
}

@Test func laserBandsWindLikeTheirCircles() throws {
    let pass = LaserTrail.passes[4]
    for (start, end) in [(Point(0, 0), Point(10, 0)), (Point(10, 0), Point(0, 0)), (Point(0, 0), Point(0, -10))] {
        let band = try #require(LaserTrail.band(from: LaserTrail.Dot(center: start, radius: 2),
                                                to: LaserTrail.Dot(center: end, radius: 3), pass: pass))
        #expect(LaserTrail.signedArea(band) > 0)
    }
    #expect(LaserTrail.band(from: LaserTrail.Dot(center: .zero, radius: 2), to: LaserTrail.Dot(center: .zero, radius: 2),
                            pass: pass) == nil)
}

@Test func laserPointerNeverBecomesPartOfADrawing() {
    #expect(Tool.laser.label == "Laser pointer")
    #expect(!Tool.laser.sharesStyle && !Tool.laser.startsWithGradient && !Tool.laser.isShape)
    #expect(throws: CanvasError.self) { try Element(tool: .laser, points: [Point(0, 0), Point(5, 5)]).validate() }
    #expect(Style.defaults(for: .laser).color == LaserTrail.defaultColor)
    #expect(Style.defaults(for: .laser).width == LaserTrail.defaultWidth)
    #expect(WorkspacePreferences.firstUse.toolStyles[Tool.laser.rawValue] == nil)
}

@Test func laserPointerIsOnTheDefaultToolbarWithTheLKey() throws {
    #expect(WorkspacePreferences.defaultToolbar.contains(.laser))
    let preferences = WorkspacePreferences()
    #expect(preferences.command(forPressed: try #require(LocalShortcut(key: "l"))) == .tool(.laser))
    #expect(preferences.shortcut(for: .tool(.laser))?.label == "L")
    try preferences.validate()
}
