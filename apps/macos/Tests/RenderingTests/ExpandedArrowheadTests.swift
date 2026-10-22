import AppKit
import Testing
import AnnotationCore
import PresentationRendering

@Test @MainActor func allExpandedHeadsRenderAtBothEndsAndRoundTrip() throws {
    #expect(ArrowHead.shapeChoices.count + ArrowHead.relationshipChoices.count == 16)
    for kind in ArrowHead.allCases where kind != .none {
        for start in [true, false] {
            var style = Style()
            style.width = 3
            style.startHead = start ? kind : .none
            style.endHead = start ? .none : kind
            let element = Element(tool: .arrow, points: [Point(70,80),Point(250,80)], style: style)
            let head = try #require(ArrowHeadGeometry.make(for: element, atStart: start))
            let image = try PNGExporter.image(elements: [element], size: CGSize(width: 330, height: 160))
            let bitmap = NSBitmapImageRep(cgImage: image)
            let tipX = start ? 70 : 250
            var count = 0
            for x in (tipX-38)...(tipX+38) {
                for y in 50...110 where abs(y-80)>3 {
                    if (bitmap.colorAt(x:x,y:y)?.alphaComponent ?? 0)>0.1 { count += 1 }
                }
            }
            #expect(count > 4, "\(kind) needs a visible head, not only a shaft.")
            let point: Point
            if kind == .circle || kind == .circleOutline { point = head.tip + head.side * (head.length/3) }
            else if let segment = head.relationshipSegments.first { point = segment.0 }
            else if kind == .square { point = head.square[0] }
            else if kind == .diamond || kind == .diamondOutline { point = head.polygon[1] }
            else { point = head.left }
            #expect(head.contains(point, tolerance: 0.2))
            let data = try DrawingFile(document: CanvasDocument(elements:[element])).data()
            #expect(try DrawingFile.read(data).elements.first?.style == style)
        }
    }
}

@Test @MainActor func outlinedHeadsDoNotFillOrLetTheShaftShowThroughTheirInterior() throws {
    for kind in [ArrowHead.triangleOutline, .circleOutline, .diamondOutline] {
        var style = Style()
        style.width = 2
        style.endHead = kind
        let element = Element(tool:.arrow,points:[Point(50,60),Point(210,60)],style:style)
        let head = try #require(ArrowHeadGeometry.make(for:element,atStart:false))
        let interior = kind == .circleOutline ? head.tip : head.tip - head.direction * (head.length*0.5)
        let image = try PNGExporter.image(elements:[element],size:CGSize(width:280,height:120))
        #expect((NSBitmapImageRep(cgImage:image).colorAt(x:Int(interior.x),y:Int(interior.y))?.alphaComponent ?? 1)<0.05)
    }
}

@Test func newArrowDefaultsAreSmallerButLegacyGeometryIsUnchanged() {
    #expect(Style.defaults(for:.arrow).headScale == 0.75)
    #expect(Style().headScale == nil)
}
