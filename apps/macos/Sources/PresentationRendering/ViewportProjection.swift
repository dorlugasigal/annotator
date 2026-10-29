import CoreImage
import AnnotationCore

public struct PixelScreenOverlay {
    public let image: CIImage
    public let bounds: Rect
    public let region: Rect
    public let opacity: Double

    public init(image: CIImage, bounds: Rect, region: Rect, opacity: Double = 1) {
        self.image = image; self.bounds = bounds; self.region = region; self.opacity = opacity
    }
}

public enum ViewportProjection {
    /// The viewport's source fills `targetSize`, sampled nearest-neighbor. With `smoothDownscaling`, a target smaller
    /// than the source is scaled down smoothly instead: nearest-neighbor would drop whole rows and columns of pixels
    /// and break up text.
    public static func imageForMetal(_ image: CIImage, sourceSize: CGSize, viewport: Viewport,
                                     targetSize: CGSize, smoothDownscaling: Bool = false) throws -> CIImage {
        guard sourceSize.width > 0, sourceSize.height > 0, sourceSize.width.isFinite, sourceSize.height.isFinite,
              targetSize.width > 0, targetSize.height > 0, targetSize.width.isFinite, targetSize.height.isFinite else {
            throw RenderFailure.invalidSize
        }
        let extent = image.extent
        let sx = extent.width / sourceSize.width, sy = extent.height / sourceSize.height
        let crop = CGRect(x: extent.minX + viewport.source.x * sx,
                          y: extent.maxY - viewport.source.maxY * sy,
                          width: viewport.source.width * sx, height: viewport.source.height * sy)
        let scale = CGAffineTransform(scaleX: targetSize.width / crop.width, y: targetSize.height / crop.height)
        if smoothDownscaling && (targetSize.width < crop.width || targetSize.height < crop.height) {
            // Clamping after the crop repeats the crop's edge pixels, so nothing outside it is sampled.
            return image.cropped(to: crop).clampedToExtent()
                .transformed(by: CGAffineTransform(translationX: -crop.minX, y: -crop.minY))
                .transformed(by: scale, highQualityDownsample: true)
                .cropped(to: CGRect(origin: .zero, size: targetSize))
        }
        return image.samplingNearest().cropped(to: crop)
            .transformed(by: CGAffineTransform(translationX: -crop.minX, y: -crop.minY))
            .transformed(by: scale)
    }

    public static func overlayForMetal(_ overlay: PixelScreenOverlay, targetSize: CGSize) throws -> CIImage {
        let frame = overlay.bounds, region = overlay.region, extent = overlay.image.extent
        guard frame.isValid, region.isValid, frame.width > 0, frame.height > 0,
              region.width > 0, region.height > 0,
              targetSize.width.isFinite, targetSize.height.isFinite, targetSize.width > 0, targetSize.height > 0,
              !extent.isInfinite, !extent.isNull,
              extent.minX.isFinite, extent.minY.isFinite, extent.width.isFinite, extent.height.isFinite,
              extent.width > 0, extent.height > 0, extent.width <= 16_384, extent.height <= 16_384,
              extent.width * extent.height <= 8_000_000,
              overlay.opacity.isFinite, (0...1).contains(overlay.opacity) else {
            throw RenderFailure.invalidSize
        }
        let sx = targetSize.width / region.width, sy = targetSize.height / region.height
        let target = CGRect(origin: .zero, size: targetSize)
        return overlay.image.cropped(to: extent)
            .transformed(by: CGAffineTransform(translationX: -extent.minX, y: -extent.minY))
            .transformed(by: CGAffineTransform(scaleX: frame.width * sx / extent.width, y: frame.height * sy / extent.height))
            .transformed(by: CGAffineTransform(translationX: (frame.x - region.x) * sx, y: (region.maxY - frame.maxY) * sy))
            .applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: overlay.opacity)])
            .cropped(to: target)
    }
}
