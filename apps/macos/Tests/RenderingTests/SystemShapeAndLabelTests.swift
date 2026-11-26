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

@Test @MainActor func handwritingTextDrawsInTheBundledFont() throws {
    var style = Style()
    style.fontName = "handwriting"
    style.fontSize = 100
    let regular = NativeTextFont.resolve(style)
    #expect(regular.familyName == "Caveat")
    #expect(regular.fontName == "Caveat-Regular")
    style.bold = true
    let bold = NativeTextFont.resolve(style, scale: 2)
    let weight = (CTFontCopyVariation(bold) as? [NSNumber: NSNumber])?[NSNumber(value: 0x7767_6874)]
    #expect(bold.familyName == "Caveat" && bold.pointSize == 200 && weight == 600)

    var text = Element(tool: .text, frame: Rect(x: 20, y: 20, width: 300, height: 130), text: "why the dip?")
    var pictures: Set<Data> = []
    for name in ["system", "monospace", "handwriting"] {
        text.style.fontName = name
        pictures.insert(try PNGExporter.data(elements: [text], size: CGSize(width: 360, height: 170)))
        #expect(try DrawingFile.read(DrawingFile(document: CanvasDocument(elements: [text])).data()).elements == [text])
    }
    #expect(pictures.count == 3)
}

@Test @MainActor func handwritingInkPastItsFrameIsPaintedAndRedrawn() throws {
    // Caveat leans: the "?" ends about 0.16 em to the right of the box the text is measured in.
    var style = Style()
    style.fontName = "handwriting"
    style.fontSize = 100
    let size = NativeTextFont.contentSize("why the dip?", font: NativeTextFont.resolve(style))
    var text = Element(tool: .text, frame: Rect(x: 20, y: 20, width: size.width, height: size.height), text: "why the dip?")
    text.style = style
    let width = Int(text.frame.maxX) + 80, height = Int(text.frame.maxY) + 40
    func rightmostInk(_ element: Element) throws -> Int {
        let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try #require(CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        SceneRenderer().draw([element], in: context)
        let data = try #require(context.data).assumingMemoryBound(to: UInt8.self)
        return try #require((0..<width).last { x in (0..<height).contains { data[($0 * width + x) * 4 + 3] > 0 } })
    }
    let solid = try rightmostInk(text)
    #expect(Double(solid) > text.frame.maxX + 8, "The ink passes the frame by more than the 2 points other fonts get.")
    var painted = text
    painted.style.setGradient(ColorGradient(colors: [RGBA(1, 0, 0), RGBA(0, 0, 1)]), fill: false)
    #expect(try rightmostInk(painted) == solid, "A gradient paints the same letters, to their last pixel.")
    #expect(SceneRenderer().drawingBounds(for: text).maxX >= CGFloat(solid + 1))

    // The system fonts stay in their box, so their paint and redraw areas are the frame's, as before.
    text.style.fontName = "system"
    #expect(NativeTextFont.inkFrame(text) == text.frame.cgRect)
    var label = Element(tool: .rectangle, frame: Rect(x: 0, y: 0, width: 40, height: 40))
    var leaningStyle = ShapeLabel().style
    leaningStyle.fontName = "handwriting"
    label.label = ShapeLabel(text: "why the dip?", style: leaningStyle)
    let labelText = try #require(ShapeLabelLayout.textElement(for: label))
    let labelInk = NativeTextFont.inkFrame(labelText)
    #expect(abs(labelInk.width - (labelText.frame.width + 0.7 * leaningStyle.fontSize)) < 0.001)
    #expect(SceneRenderer().drawingBounds(for: label).contains(labelInk), "A label's redraw area follows its font too.")
}
