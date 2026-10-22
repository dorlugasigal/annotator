import AppKit
import CoreImage
import AnnotationCore

@MainActor
public enum CaptureCompositor {
    private static var imageCache: [UUID: (Data, CIImage, Int)] = [:]
    public static func clearImageCache() { imageCache.removeAll() }
    public static func pixelRect(_ rect: Rect, logicalSize: CGSize, imageExtent: CGRect) throws -> CGRect {
        guard rect.isValid, logicalSize.width.isFinite, logicalSize.height.isFinite,
              logicalSize.width > 0, logicalSize.height > 0,
              imageExtent.width > 0, imageExtent.height > 0 else { throw RenderFailure.invalidSize }
        let sx = imageExtent.width / logicalSize.width, sy = imageExtent.height / logicalSize.height
        return CGRect(x: imageExtent.minX + rect.x * sx,
                      y: imageExtent.maxY - rect.maxY * sy,
                      width: rect.width * sx, height: rect.height * sy).integral.intersection(imageExtent)
    }

    public static func image(source: CIImage, logicalSize: CGSize, elements: [Element],
                             regionsOnly: Bool = false, includeImageLayers: Bool = false,
                             drawingLayer: CIImage? = nil, privacyRegion: Rect? = nil) throws -> CIImage {
        let extent = source.extent
        var scene = source
        if includeImageLayers {
            for element in elements where element.tool == .image {
                guard let data = element.imagePNG else { throw RenderFailure.imageUnavailable }
                let asset: CIImage
                if let cached = imageCache[element.id], cached.0 == data { asset = cached.1 }
                else {
                    let decoded = try RasterAsset.decode(data)
                    let cost = decoded.width * decoded.height * 4
                    guard cost <= 128 * 1_024 * 1_024 else { throw RenderFailure.invalidSize }
                    asset = CIImage(cgImage: decoded)
                    if imageCache.count >= 8 || imageCache.values.reduce(0, { $0 + $1.2 }) + cost > 128 * 1_024 * 1_024 {
                        imageCache.removeAll()
                    }
                    imageCache[element.id] = (data, asset, cost)
                }
                let sx = extent.width / logicalSize.width, sy = extent.height / logicalSize.height
                let target = CGRect(x: extent.minX + element.frame.x * sx, y: extent.maxY - element.frame.maxY * sy,
                                    width: element.frame.width * sx, height: element.frame.height * sy)
                let center = CGPoint(x: target.midX, y: target.midY)
                var transform = CGAffineTransform(translationX: center.x, y: center.y)
                transform = transform.rotated(by: -element.rotation)
                transform = transform.translatedBy(x: -center.x, y: -center.y)
                let placed = asset.transformed(by: CGAffineTransform(scaleX: target.width / asset.extent.width,
                                                                     y: target.height / asset.extent.height))
                    .transformed(by: CGAffineTransform(translationX: target.minX, y: target.minY))
                    .transformed(by: transform)
                    .applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: element.style.opacity)])
                scene = placed.composited(over: scene)
            }
        }
        if let drawingLayer { scene = drawingLayer.composited(over: scene) }
        if let privacyRegion {
            let full = Rect(x: 0, y: 0, width: logicalSize.width, height: logicalSize.height)
            guard full.isValid, privacyRegion.isValid, privacyRegion.width > 0, privacyRegion.height > 0,
                  full.contains(privacyRegion), logicalSize.width > 0, logicalSize.height > 0,
                  extent.minX.isFinite, extent.minY.isFinite, extent.width.isFinite, extent.height.isFinite,
                  extent.width > 0, extent.height > 0 else { throw RenderFailure.invalidSize }
            let sx = extent.width / logicalSize.width, sy = extent.height / logicalSize.height
            let minX = ceil(extent.minX + privacyRegion.x * sx)
            let minY = ceil(extent.maxY - privacyRegion.maxY * sy)
            let maxX = floor(extent.minX + privacyRegion.maxX * sx)
            let maxY = floor(extent.maxY - privacyRegion.y * sy)
            guard maxX > minX, maxY > minY else { throw RenderFailure.invalidSize }
            let crop = CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
            // Remove outside pixels before any filter can pull them into the shared region.
            scene = scene.cropped(to: crop).composited(over: CIImage(color: .black).cropped(to: extent))
        }
        // Filters can sample outside their output region. Remove covered pixels
        // before sampling so a distortion cannot pull them into another area.
        for element in elements where element.tool == .cover {
            let region = try pixelRect(element.frame, logicalSize: logicalSize, imageExtent: extent)
            if !region.isNull { scene = CIImage(color: .black).cropped(to: region).composited(over: scene) }
        }
        var output = regionsOnly ? CIImage(color: .clear).cropped(to: extent) : scene
        let ratio = extent.width / logicalSize.width
        for element in elements where element.tool.isPixelEffect {
            try element.validate()
            let region = try pixelRect(element.frame, logicalSize: logicalSize, imageExtent: extent)
            guard !region.isNull, region.width > 0, region.height > 0 else { continue }
            let amount = element.style.effectAmount ?? 18
            guard amount > 0 else { continue }
            let name: String
            let parameters: [String: Any]
            switch element.tool {
            case .blur:
                name = "CIGaussianBlur"
                parameters = [kCIInputRadiusKey: amount * ratio]
            case .pixelate:
                name = "CIPixellate"
                parameters = [kCIInputScaleKey: amount * ratio,
                              kCIInputCenterKey: CIVector(x: region.midX, y: region.midY)]
            case .distort:
                name = "CITwirlDistortion"
                parameters = [kCIInputCenterKey: CIVector(x: region.midX, y: region.midY),
                              kCIInputRadiusKey: max(region.width, region.height),
                              kCIInputAngleKey: amount / 100 * .pi * 2]
            default: continue
            }
            guard let filter = CIFilter(name: name) else { throw RenderFailure.imageUnavailable }
            filter.setValue(scene.clampedToExtent(), forKey: kCIInputImageKey)
            for (key, value) in parameters { filter.setValue(value, forKey: key) }
            guard let result = filter.outputImage else { throw RenderFailure.imageUnavailable }
            output = result.cropped(to: region).composited(over: output)
        }
        // Opaque covers win over every visual effect, independent of drawing order.
        for element in elements where element.tool == .cover {
            let region = try pixelRect(element.frame, logicalSize: logicalSize, imageExtent: extent)
            if !region.isNull { output = CIImage(color: .black).cropped(to: region).composited(over: output) }
        }
        return output.cropped(to: extent)
    }
}
