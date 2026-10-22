import AppKit
import Testing
import AnnotationCore
@testable import PresentationRendering

@Test @MainActor func systemShapesHaveDistinctRenderableGeometry() throws {
    var pictures = Set<Data>()
    for tool in Tool.systemDesignShapes + [.database] {
        var shape = Element(tool: tool, frame: Rect(x: 20, y: 20, width: 160, height: 160))
        shape.style.color = .ink
        let path = SystemDiagramGeometry.path(for: shape)
        if tool != .database {
            #expect(!path.isEmpty, "\(tool)")
            let ratio = try #require(tool.systemShapeAspectRatio)
            let bounds = path.boundingBoxOfPath
            #expect(abs(bounds.width/bounds.height-ratio) < 0.001)
            #expect(shape.anchorPoint(Point(0, 0.5)).x == shape.symbolFrame.x)
        }
        let image = try PNGExporter.image(elements: [shape], size: CGSize(width: 200, height: 200))
        let data = try #require(image.dataProvider?.data)
        let bytes = data as Data
        #expect(bytes.contains { $0 != 0 }, "\(tool)")
        pictures.insert(bytes)
        shape.style.roughness = 2
        shape.style.fill = RGBA(0.4, 0.6, 0.8, 0.5)
        _ = try PNGExporter.image(elements: [shape], size: CGSize(width: 200, height: 200))
        #expect(try DrawingFile.read(DrawingFile(document: CanvasDocument(elements: [shape])).data()).elements == [shape])
    }
    #expect(pictures.count == Tool.systemDesignShapes.count + 1)
}

@Test @MainActor func labelGeometryRemainsCenteredAndFollowsOwnerOpacity() throws {
    for tool in [Tool.rectangle, .ellipse, .database, .databaseStack, .server] {
        var shape = Element(tool: tool, frame: Rect(x: 50, y: 70, width: 200, height: 140))
        shape.style.opacity = 0.5
        shape.label = ShapeLabel(text: "Orders")
        shape.rotation = 0.6
        let label = try #require(ShapeLabelLayout.textElement(for: shape))
        #expect(label.frame.center == shape.frame.center)
        #expect(label.rotation == shape.rotation)
        #expect(label.style.opacity == 0.5)
        let labeled = try PNGExporter.data(elements: [shape], size: CGSize(width: 360, height: 300))
        shape.label = nil
        let plain = try PNGExporter.data(elements: [shape], size: CGSize(width: 360, height: 300))
        #expect(labeled != plain)
    }
}
