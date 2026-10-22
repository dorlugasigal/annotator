import Foundation
import Testing
@testable import AnnotationCore

@Test func newPolygonShapesHitTheirPaintedOutlineAndExposeRealBoundaryAnchors() throws {
    for tool in [Tool.triangle, .hexagon, .star] {
        var element = Element(tool: tool, frame: Rect(x:100,y:100,width:180,height:120))
        for angle in [0.0, .pi/3] {
            element.rotation = angle
            let center = element.frame.center
            #expect(HitTesting.contains(center,element:element,tolerance:0))
            let outside = Point(102,102).rotated(around:center,by:angle)
            #expect(!HitTesting.contains(outside,element:element,tolerance:0))
            let kind = try #require(tool.polygonKind)
            #expect(Rect.enclosing(PolygonShape.vertices(kind,in:element.frame)) == element.frame)
            let tip = PolygonShape.vertices(kind,in:element.frame)[0].rotated(around:center,by:angle)
            #expect(HitTesting.contains(tip,element:element,tolerance:0))
            let document = try CanvasDocument(elements:[element])
            let anchor = try #require(document.nearestAnchor(to:tip,excluding:UUID(),threshold:0.1))
            #expect(element.anchorPoint(anchor.relative).distance(to:tip)<0.001)
        }
        #expect(try DrawingFile.read(DrawingFile(document:CanvasDocument(elements:[element])).data()).elements[0].tool==tool)
    }
}

@Test func renamingSavedShapesPreservesIdentityGeometryAndOriginalCanvas() async throws {
    var document = try CanvasDocument(elements:[Element(tool:.rectangle,frame:Rect(x:10,y:10,width:70,height:50))])
    document.select(Set(document.elements.map(\.id)))
    let original = try SavedShape(name:"Original",document:document)
    let store = ShapeLibraryStore()
    _ = try await store.save(original)
    let renamed = try await store.rename(original.id,to:"New name")
    #expect(renamed[0].id==original.id)
    #expect(renamed[0].name=="New name")
    #expect(renamed[0].elements==original.elements)
    await #expect(throws: ShapeLibraryError.self) { try await store.rename(original.id,to:" ") }
    #expect(try await store.load()[0].name=="New name")
    #expect(document.elements==original.elements)
}

@Test func multiObjectSavedShapeInsertsAsOneGroupAndCanBeUngrouped() throws {
    var source = try CanvasDocument(elements:[
        Element(tool:.rectangle,frame:Rect(x:10,y:10,width:40,height:30)),
        Element(tool:.ellipse,frame:Rect(x:80,y:10,width:40,height:30))
    ])
    source.select(Set(source.elements.map(\.id)))
    let saved = try SavedShape(name:"Pair",document:source)
    var result = try saved.adding(to:.empty(),centeredAt:Point(200,200))
    #expect(result.elements[0].groupID != nil)
    #expect(result.elements[0].groupID == result.elements[1].groupID)
    #expect(source.elements.allSatisfy{$0.groupID==nil})
    try result.ungroupSelection()
    #expect(result.elements.allSatisfy{$0.groupID==nil})
    result.undo()
    #expect(result.elements[0].groupID==result.elements[1].groupID)
    result.undo()
    #expect(result.elements.isEmpty)
}
