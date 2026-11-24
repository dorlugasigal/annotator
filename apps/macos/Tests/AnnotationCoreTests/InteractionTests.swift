import Foundation
import Testing
@testable import AnnotationCore

@Test func selectionMatchesVisibleProtectionStacking() throws {
    let frame = Rect(x: 20, y: 20, width: 100, height: 80)
    let solid = Element(tool: .cover, frame: frame)
    let blur = Element(tool: .blur, frame: frame)
    let shape = Element(tool: .rectangle, frame: frame)
    var document = try CanvasDocument(elements: [solid, blur, shape])
    document.select(at: frame.center)
    #expect(document.selection == [solid.id])
    try document.deleteSelection()
    document.select(at: frame.center)
    #expect(document.selection == [blur.id])
    try document.duplicateSelection()
    #expect(document.elements.filter { $0.tool == .blur }.count == 2)
    document.undo()
    #expect(document.elements.filter { $0.tool == .blur }.count == 1)
}

@Test func curvedConnectorsAndHighlighterEdgesAreSelectable() throws {
    var arrow = Element(tool: .arrow, points: [Point(10, 90), Point(90, 10), Point(170, 110)])
    arrow.style.curved = true
    let curve = try #require(ConnectorGeometry.curves(through: arrow.points).first)
    let point = curve.point(at: 0.5)
    #expect(HitTesting.contains(point, element: arrow, tolerance: 0.5))
    var marker = Element(tool: .highlighter, points: [Point(10, 50), Point(150, 50)])
    marker.style.width = 6
    #expect(HitTesting.contains(Point(80, 63), element: marker, tolerance: 0))
    #expect(!HitTesting.contains(Point(80, 80), element: marker, tolerance: 0))
}

@Test func presentationAndDrawingShareUndoAndRedo() throws {
    var document = CanvasDocument.empty()
    let shape = Element(tool: .rectangle, frame: Rect(x: 10, y: 10, width: 100, height: 60))
    try document.add(shape)
    try document.setPresentation(PresentationSettings(mode: .lens, zoom: 3))
    document.undo()
    #expect(document.presentation.mode == .off)
    #expect(document.elements.count == 1)
    document.undo()
    #expect(document.elements.isEmpty)
    document.redo(); document.redo()
    #expect(document.presentation == PresentationSettings(mode: .lens, zoom: 3))
    #expect(document.elements.count == 1)
    let reopened = try DrawingFile.read(DrawingFile(document: document).data())
    #expect(reopened.presentation.mode == .off)
}

@Test func cursorAnchoredZoomKeepsNativeInputAligned() throws {
    for screen in [Rect(x: 0, y: 0, width: 1920, height: 1080), Rect(x: -1440, y: -900, width: 1440, height: 900)] {
        for zoom in [1.0, 1.5, 2, 4, 16] {
            for pointer in [screen.origin, screen.center, Point(screen.maxX, screen.maxY), Point(screen.x + 53, screen.y + 91)] {
                let viewport = try Viewport.cursorAnchored(display: screen, factor: zoom, cursor: pointer)
                #expect(viewport.sourcePoint(for: pointer).distance(to: pointer) < 0.000_001)
                #expect(viewport.outputPoint(for: pointer).distance(to: pointer) < 0.000_001)
                #expect(screen.contains(viewport.source))
            }
        }
    }
}

@Test func zoomWhileDrawingHoldsStillUntilThePointerNearsAnEdge() throws {
    let screen = Rect(x: 0, y: 0, width: 1512, height: 982)
    let start = try ZoomFollow.viewport(previous: nil, display: screen, factor: 2, pointer: screen.center)
    #expect(start == (try Viewport.cursorAnchored(display: screen, factor: 2, cursor: screen.center)))
    for pointer in [Point(300, 200), Point(1200, 800), Point(120, 120)] {
        #expect(try ZoomFollow.viewport(previous: start.source, display: screen, factor: 2, pointer: pointer) == start)
        #expect(!ZoomFollow.slides(start, pointer: pointer))
    }
    let right = try ZoomFollow.slide(start, pointer: Point(1510, 491), elapsed: 0.05)
    #expect(right.source.x > start.source.x && right.source.y == start.source.y)
    let top = try ZoomFollow.slide(start, pointer: Point(756, 10), elapsed: 0.05)
    #expect(top.source.y < start.source.y && top.source.x == start.source.x)
    var edge = start
    for _ in 0..<200 { edge = try ZoomFollow.slide(edge, pointer: Point(0, 0), elapsed: 0.1) }
    #expect(edge.source.origin == screen.origin && !ZoomFollow.slides(edge, pointer: Point(0, 0)))
    #expect(ZoomFollow.velocity(display: screen, factor: 2, pointer: Point(-4, 491)) == Point(0, 0))
}

@Test func zoomWhileDrawingKeepsThePointUnderThePointerWhenTheFactorChanges() throws {
    let screen = Rect(x: -1440, y: -900, width: 1440, height: 900)
    let pointer = Point(-1000, -300)
    let before = try ZoomFollow.viewport(previous: Rect(x: -1300, y: -800, width: 720, height: 450), display: screen, factor: 2, pointer: pointer)
    let after = try ZoomFollow.viewport(previous: before.source, display: screen, factor: 3.5, pointer: pointer)
    #expect(after.sourcePoint(for: pointer).distance(to: before.sourcePoint(for: pointer)) < 0.000_001)
    #expect(screen.contains(after.source))
    let out = try ZoomFollow.viewport(previous: after.source, display: screen, factor: 1, pointer: pointer)
    #expect(out.source == screen)
}

@Test func zoomMapSitsInTheChosenCorner() throws {
    let screen = Rect(x: 0, y: 0, width: 1512, height: 982)
    let frames = ZoomMapPosition.allCases.map { ZoomMap.frame(display: screen, position: $0) }
    #expect(frames[4] == nil)
    let map = try #require(frames[0])
    #expect(map.maxX == screen.maxX - ZoomMap.inset && map.maxY == screen.maxY - ZoomMap.inset)
    #expect(frames[3]?.origin == Point(ZoomMap.inset, ZoomMap.inset))
    let zoomed = try Viewport.cursorAnchored(display: screen, factor: 2, cursor: screen.origin)
    let area = ZoomMap.zoomedArea(zoomed, in: map)
    #expect(area.origin == map.origin)
    #expect(abs(area.width - map.width / 2) < 0.000_001)
    #expect(ZoomMap.frame(display: Rect(x: 0, y: 0, width: 160, height: 90), position: .topLeft) == nil)
}

@Test func zoomPushesAtThePointersSpeedOnlyInsideAnEdgeZone() throws {
    let screen = Rect(x: 0, y: 0, width: 1512, height: 982)
    let start = try ZoomFollow.viewport(previous: nil, display: screen, factor: 2, pointer: screen.center)
    let zone = ZoomFollow.edgeZone(screen.width)
    #expect(try ZoomFollow.push(start, pointer: Point(800, 491), delta: Point(40, 0)) == start)
    let entering = try ZoomFollow.push(start, pointer: Point(screen.maxX - 10, 491), delta: Point(200, 0))
    #expect(abs(entering.source.x - (start.source.x + (zone - 10) / 2)) < 0.000_001)
    let atEdge = try ZoomFollow.push(start, pointer: Point(screen.maxX, 491), delta: Point(30, 0))
    #expect(abs(atEdge.source.x - (start.source.x + 15)) < 0.000_001)
    #expect(try ZoomFollow.push(start, pointer: Point(screen.maxX, 491), delta: Point(-30, 0)) == start)
    let up = try ZoomFollow.push(start, pointer: Point(756, 0), delta: Point(0, -20))
    #expect(abs(up.source.y - (start.source.y - 10)) < 0.000_001)
}
