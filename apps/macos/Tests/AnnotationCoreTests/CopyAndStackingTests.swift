import Foundation
import Testing
@testable import AnnotationCore

@Test func textInsideANewerOutlineShapeStaysReachable() throws {
    let text = Element(tool: .text, frame: Rect(x: 60, y: 60, width: 80, height: 24), text: "Orders")
    let box = Element(tool: .rectangle, frame: Rect(x: 20, y: 20, width: 200, height: 120))
    var document = try CanvasDocument(elements: [text, box])
    #expect(ElementStacking.frontmost(at: Point(80, 70), in: document.elements)?.id == text.id)
    #expect(ElementStacking.frontmost(at: Point(180, 120), in: document.elements)?.id == box.id)
    document.select(at: Point(80, 70))
    #expect(document.selection == [text.id])
}

@Test func solidFilledShapeStillHidesTextBehindIt() throws {
    let text = Element(tool: .text, frame: Rect(x: 60, y: 60, width: 80, height: 24), text: "Hidden")
    var box = Element(tool: .ellipse, frame: Rect(x: 20, y: 20, width: 200, height: 120))
    box.style.fill = .blue
    #expect(ElementStacking.frontmost(at: Point(100, 70), in: [text, box])?.id == box.id)
    box.style.fillPattern = .hatch
    #expect(ElementStacking.frontmost(at: Point(100, 70), in: [text, box])?.id == text.id)
    let pen = Element(tool: .pen, points: [Point(60, 70), Point(140, 70)])
    #expect(ElementStacking.frontmost(at: Point(100, 70), in: [text, pen])?.id == pen.id)
}

@Test func insertCopiesKeepsInternalLinksAndDropsOutsideOnes() throws {
    let target = Element(tool: .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 100))
    let outside = Element(tool: .ellipse, frame: Rect(x: 300, y: 0, width: 100, height: 100))
    var arrow = Element(tool: .arrow, points: [Point(100, 50), Point(300, 50)])
    arrow.startAnchor = Anchor(elementID: target.id, relative: Point(1, 0.5))
    arrow.endAnchor = Anchor(elementID: outside.id, relative: Point(0, 0.5))
    var grouped = Element(tool: .text, frame: Rect(x: 10, y: 10, width: 40, height: 20), text: "A")
    grouped.groupID = UUID()
    grouped.locked = true
    var document = try CanvasDocument(elements: [target, outside, arrow])
    try document.insertCopies(of: [target, arrow, grouped], offset: Point(10, 20))
    #expect(document.elements.count == 6)
    let copies = document.elements.filter { document.selection.contains($0.id) }
    #expect(copies.count == 3)
    #expect(Set(copies.map(\.id)).isDisjoint(with: [target.id, arrow.id, grouped.id]))
    let copiedTarget = try #require(copies.first { $0.tool == .rectangle })
    let copiedArrow = try #require(copies.first { $0.tool == .arrow })
    let copiedText = try #require(copies.first { $0.tool == .text })
    #expect(copiedTarget.frame.origin == Point(10, 20))
    #expect(copiedArrow.startAnchor?.elementID == copiedTarget.id)
    #expect(copiedArrow.endAnchor == nil)
    #expect(copiedText.groupID != nil && copiedText.groupID != grouped.groupID)
    #expect(!copiedText.locked)
    document.undo()
    #expect(document.elements.count == 3)
}

@Test func anchorLocationsMatchNearestAnchor() throws {
    let box = Element(tool: .rectangle, frame: Rect(x: 0, y: 0, width: 100, height: 60))
    #expect(box.anchorLocations.count == 4)
    let document = try CanvasDocument(elements: [box])
    #expect(document.nearestAnchor(to: Point(104, 30), excluding: UUID()) == Anchor(elementID: box.id, relative: Point(1, 0.5)))
    #expect(document.nearestAnchor(to: Point(150, 30), excluding: UUID()) == nil)
}
