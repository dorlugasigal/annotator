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
