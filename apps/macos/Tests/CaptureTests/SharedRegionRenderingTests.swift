import AppKit
import CoreImage
import Testing
import AnnotationCore
import PresentationRendering

private func sharedRegionSource() -> CIImage {
    let full = CGRect(x: 0, y: 0, width: 128, height: 64)
    return CIImage(color: CIColor(red: 1, green: 0, blue: 0)).cropped(to: CGRect(x: 64, y: 0, width: 64, height: 64))
        .composited(over: CIImage(color: CIColor(red: 0, green: 1, blue: 0)).cropped(to: full))
}

private func sharedRegionBitmap(_ image: CIImage) throws -> NSBitmapImageRep {
    let output = try #require(CIContext(options: [.cacheIntermediates: false]).createCGImage(image, from: image.extent))
    return NSBitmapImageRep(cgImage: output)
}

@Test @MainActor func sharedRegionRemovesOutsideSourcePixelsBeforeEveryEffect() throws {
    let source = sharedRegionSource()
    let region = Rect(x: 0, y: 0, width: 64, height: 64)
    var untreatedLeaks = false
    for tool in [Tool.blur, .pixelate, .distort] {
        var effect = Element(tool: tool, frame: Rect(x: 36, y: 0, width: 64, height: 64))
        effect.style.effectAmount = tool == .distort ? 100 : 32
        let protected = try sharedRegionBitmap(CaptureCompositor.image(
            source: source, logicalSize: CGSize(width: 128, height: 64), elements: [effect], privacyRegion: region))
        let untreated = try sharedRegionBitmap(CaptureCompositor.image(
            source: source, logicalSize: CGSize(width: 128, height: 64), elements: [effect]))
        for y in 0..<64 {
            for x in 0..<64 {
                let color = try #require(protected.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
                #expect(color.redComponent < 0.01, "A filter must not sample the red desktop outside the shared region.")
                let other = try #require(untreated.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
                if other.redComponent > 0.1 { untreatedLeaks = true }
            }
        }
    }
    #expect(untreatedLeaks, "The source fixture must demonstrate the outside-sampling regression.")
}

@Test @MainActor func fractionalSharedCropDropsPartialEdgePixels() throws {
    let source = CIImage(color: CIColor(red: 1, green: 0, blue: 0))
        .cropped(to: CGRect(x: 0, y: 0, width: 64, height: 64))
    let result = try sharedRegionBitmap(CaptureCompositor.image(source: source,
        logicalSize: CGSize(width: 64, height: 64), elements: [],
        privacyRegion: Rect(x: 0.25, y: 0.25, width: 32, height: 32)))
    for point in [(0, 1), (32, 1), (1, 0), (1, 32), (63, 63)] {
        #expect(try #require(result.colorAt(x: point.0, y: point.1)?.usingColorSpace(.sRGB)).redComponent == 0)
    }
    #expect(try #require(result.colorAt(x: 1, y: 1)?.usingColorSpace(.sRGB)).redComponent > 0.99)
}

@Test @MainActor func sharedDrawingLayerIsUprightScaledCachedAndProtected() throws {
    let renderer = SharedRegionDrawingLayer()
    let logical = CGSize(width: 128, height: 64)
    let pixels = CGSize(width: 256, height: 128)
    let region = Rect(x: 0, y: 0, width: 64, height: 64)
    var drawing = Element(tool: .rectangle, frame: Rect(x: 8, y: 8, width: 32, height: 16))
    drawing.style.color = .blue; drawing.style.fill = .blue
    var cover = Element(tool: .cover, frame: Rect(x: 16, y: 12, width: 8, height: 8))
    cover.style.opacity = 0
    let blur = Element(tool: .blur, frame: cover.frame)
    let layer = try renderer.image(elements: [drawing, cover, blur], region: region,
        logicalSize: logical, pixelSize: pixels, background: .white)
    _ = try renderer.image(elements: [drawing], region: region, logicalSize: logical,
        pixelSize: pixels, background: .white)
    #expect(renderer.renderCount == 1, "Protection changes do not rebuild the unchanged drawing layer.")
    let source = CIImage(color: CIColor(red: 1, green: 0, blue: 0))
        .cropped(to: CGRect(origin: .zero, size: pixels))
    let result = try sharedRegionBitmap(CaptureCompositor.image(source: source, logicalSize: logical,
        elements: [blur, cover], drawingLayer: layer, privacyRegion: region))
    let blue = try #require(result.colorAt(x: 20, y: 20)?.usingColorSpace(.sRGB))
    #expect(blue.blueComponent - blue.redComponent > 0.5)
    let protected = try #require(result.colorAt(x: 40, y: 30)?.usingColorSpace(.sRGB))
    #expect(protected.redComponent == 0 && protected.greenComponent == 0 && protected.blueComponent == 0)
    #expect(protected.alphaComponent == 1)
    let bottom = try #require(result.colorAt(x: 20, y: 100)?.usingColorSpace(.sRGB))
    #expect(bottom.redComponent > 0.99 && bottom.blueComponent > 0.99)
    let outside = try #require(result.colorAt(x: 160, y: 40)?.usingColorSpace(.sRGB))
    #expect(outside.redComponent == 0 && outside.alphaComponent == 1)
    drawing.style.fill = .coral
    _ = try renderer.image(elements: [drawing], region: region, logicalSize: logical, pixelSize: pixels, background: .white)
    #expect(renderer.renderCount == 2)
    renderer.reset()
    _ = try renderer.image(elements: [drawing], region: region, logicalSize: logical, pixelSize: pixels, background: .white)
    #expect(renderer.renderCount == 3)
}

@Test @MainActor func sharedRenderingRefusesOutsideAndUnboundedRequests() throws {
    let source = sharedRegionSource()
    for region in [
        Rect(x: -1, y: 0, width: 64, height: 64),
        Rect(x: 0, y: 0, width: 129, height: 64),
        Rect(x: 0, y: 0, width: .infinity, height: 64),
        .zero
    ] {
        #expect(throws: RenderFailure.invalidSize) {
            try CaptureCompositor.image(source: source, logicalSize: CGSize(width: 128, height: 64),
                elements: [], privacyRegion: region)
        }
    }
    #expect(throws: RenderFailure.self) {
        try SharedRegionDrawingLayer().image(elements: [], region: Rect(x: 0, y: 0, width: 128, height: 64),
            logicalSize: CGSize(width: 128, height: 64), pixelSize: CGSize(width: 100_000, height: 100_000))
    }
}
