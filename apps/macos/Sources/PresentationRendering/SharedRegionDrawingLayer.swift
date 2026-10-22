import AppKit
import CoreImage
import AnnotationCore

@MainActor
public final class SharedRegionDrawingLayer {
    private struct Key: Equatable {
        let elements: [Element]
        let region: Rect
        let logicalSize: CGSize
        let pixelSize: CGSize
        let background: RGBA?
    }
    private let renderer = SceneRenderer()
    private var lastKey: Key?
    private var lastImage: CIImage?
    public private(set) var renderCount = 0

    public init() {}

    public func image(elements: [Element], region: Rect, logicalSize: CGSize,
                      pixelSize: CGSize, background: RGBA? = nil) throws -> CIImage {
        let drawings = elements.filter { !$0.tool.isProtection }
        let key = Key(elements: drawings, region: region, logicalSize: logicalSize,
                      pixelSize: pixelSize, background: background)
        if key == lastKey, let lastImage { return lastImage }
        let display = Rect(x: 0, y: 0, width: logicalSize.width, height: logicalSize.height)
        guard display.isValid, display.width > 0, display.height > 0,
              region.isValid, region.width > 0, region.height > 0, display.contains(region),
              pixelSize.width.isFinite, pixelSize.height.isFinite,
              pixelSize.width > 0, pixelSize.height > 0,
              background?.isValid != false else { throw RenderFailure.invalidSize }
        try drawings.forEach { try $0.validate() }
        let sx = pixelSize.width / logicalSize.width, sy = pixelSize.height / logicalSize.height
        let width = ceil(region.width * sx), height = ceil(region.height * sy)
        guard width.isFinite, height.isFinite, width > 0, height > 0,
              width <= 16_384, height <= 16_384, width * height <= 40_000_000,
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: Int(width), height: Int(height), bitsPerComponent: 8,
                  bytesPerRow: 0, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw RenderFailure.contextUnavailable
        }
        context.translateBy(x: 0, y: height)
        context.scaleBy(x: width / region.width, y: -height / region.height)
        context.translateBy(x: -region.x, y: -region.y)
        if let background {
            context.setFillColor(background.nsColor.cgColor)
            context.fill(region.cgRect)
        }
        renderer.draw(drawings, in: context, pixelsAvailableForBlur: true)
        guard let image = context.makeImage() else { throw RenderFailure.imageUnavailable }
        let result = CIImage(cgImage: image)
            .transformed(by: CGAffineTransform(scaleX: region.width * sx / width, y: region.height * sy / height))
            .transformed(by: CGAffineTransform(translationX: region.x * sx, y: (logicalSize.height - region.maxY) * sy))
        lastKey = key; lastImage = result
        renderCount += 1
        return result
    }

    public func reset() {
        lastKey = nil; lastImage = nil
        renderer.reset()
    }
}
