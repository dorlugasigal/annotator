import AppKit
import CoreImage
import AnnotationCore

public struct ExportBackdrop: Sendable {
    public enum Kind: String, CaseIterable, Sendable { case none, solid, gradient }
    public var kind: Kind = .none
    public var padding: Double = 36
    public var radius: Double = 16
    public var color = RGBA(0.17, 0.2, 0.3)
    public var scale: Double = 1
    public init() {}
}

@MainActor
public enum SnapshotExporter {
    private static let effects = CIContext(options: [.cacheIntermediates: false])

    public static func effectOverlay(buffer: CVPixelBuffer, logicalSize: CGSize, elements: [Element]) throws -> CGImage {
        let image = try CaptureCompositor.image(source: CIImage(cvPixelBuffer: buffer), logicalSize: logicalSize,
                                                elements: elements, regionsOnly: true, includeImageLayers: true)
        guard let output = effects.createCGImage(image, from: image.extent) else { throw RenderFailure.imageUnavailable }
        return output
    }

    public static func flattenedSource(_ source: CGImage, elements: [Element]) throws -> CGImage {
        guard elements.contains(where: { $0.tool.isProtection }) else { return source }
        let composed = try CaptureCompositor.image(source: CIImage(cgImage: source),
                                                   logicalSize: CGSize(width: source.width, height: source.height), elements: elements)
        guard let image = effects.createCGImage(composed, from: composed.extent) else { throw RenderFailure.imageUnavailable }
        return image
    }

    public static func image(source: CGImage, elements: [Element], crop: Rect? = nil,
                             backdrop: ExportBackdrop = ExportBackdrop()) throws -> CGImage {
        try elements.forEach { try $0.validate() }
        let full = Rect(x: 0, y: 0, width: Double(source.width), height: Double(source.height))
        let crop = crop ?? full
        guard crop.isValid, full.contains(crop), crop.width >= 1, crop.height >= 1,
              backdrop.padding.isFinite, (0...256).contains(backdrop.padding),
              backdrop.radius.isFinite, (0...100).contains(backdrop.radius),
              backdrop.scale.isFinite, (0.25...2).contains(backdrop.scale), backdrop.color.isValid else {
            throw RenderFailure.invalidSize
        }
        let padding = backdrop.kind == .none ? 0 : backdrop.padding
        let size = CGSize(width: crop.width + padding * 2, height: crop.height + padding * 2)
        let w = ceil(size.width * backdrop.scale), h = ceil(size.height * backdrop.scale)
        guard w * h <= 40_000_000, w <= 16_384, h <= 16_384,
              let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: Int(w), height: Int(h), bitsPerComponent: 8, bytesPerRow: 0,
                                      space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw RenderFailure.invalidSize
        }
        context.scaleBy(x: backdrop.scale, y: backdrop.scale)
        if backdrop.kind != .none {
            context.setFillColor(backdrop.color.nsColor.cgColor)
            context.fill(CGRect(origin: .zero, size: size))
            if backdrop.kind == .gradient {
                let colors = [backdrop.color.nsColor.cgColor, NSColor(srgbRed: 0.6, green: 0.38, blue: 0.75, alpha: 1).cgColor] as CFArray
                guard let gradient = CGGradient(colorsSpace: colorSpace, colors: colors, locations: [0, 1]) else {
                    throw RenderFailure.contextUnavailable
                }
                context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: size.width, y: size.height), options: [])
            }
        }
        let content = try annotatedSource(source, elements: elements)
        let flattened = try flattenedSource(content, elements: elements)
        guard let cropped = flattened.cropping(to: crop.cgRect.integral) else { throw RenderFailure.imageUnavailable }
        let frame = CGRect(x: padding, y: padding, width: crop.width, height: crop.height)
        let radius = backdrop.kind == .none ? 0 : min(backdrop.radius, frame.width / 2, frame.height / 2)
        context.addPath(CGPath(roundedRect: frame, cornerWidth: radius, cornerHeight: radius, transform: nil))
        context.clip()
        context.draw(cropped, in: frame)
        guard let image = context.makeImage() else { throw RenderFailure.imageUnavailable }
        return image
    }

    private static func annotatedSource(_ source: CGImage, elements: [Element]) throws -> CGImage {
        let drawings = elements.filter { !$0.tool.isProtection }
        guard !drawings.isEmpty else { return source }
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: source.width, height: source.height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw RenderFailure.contextUnavailable
        }
        context.draw(source, in: CGRect(x: 0, y: 0, width: source.width, height: source.height))
        context.translateBy(x: 0, y: CGFloat(source.height))
        context.scaleBy(x: 1, y: -1)
        SceneRenderer().draw(drawings, in: context)
        guard let result = context.makeImage() else { throw RenderFailure.imageUnavailable }
        return result
    }
}
