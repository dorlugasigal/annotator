import AppKit
import AnnotationCore
import ImageIO
import UniformTypeIdentifiers

public enum RenderFailure: Error, LocalizedError {
    case invalidSize, contextUnavailable, imageUnavailable, encodingFailed
    public var errorDescription: String? {
        switch self {
        case .invalidSize: "Export dimensions exceed the supported raster budget."
        case .contextUnavailable: "A drawing context could not be created."
        case .imageUnavailable: "The drawing could not be rendered."
        case .encodingFailed: "The PNG could not be encoded."
        }
    }
}

@MainActor
public enum PNGExporter {
    public static func image(elements: [Element], size: CGSize, scale: Double = 1,
                             background: RGBA? = nil) throws -> CGImage {
        try elements.forEach { try $0.validate() }
        let w = size.width * scale, h = size.height * scale
        guard w.isFinite, h.isFinite, w >= 1, h >= 1, w <= 16_384, h <= 16_384,
              w * h <= 40_000_000 else { throw RenderFailure.invalidSize }
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: Int(w.rounded(.up)), height: Int(h.rounded(.up)),
                                      bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw RenderFailure.contextUnavailable
        }
        if let background {
            context.setFillColor(background.nsColor.cgColor)
            context.fill(CGRect(x: 0, y: 0, width: w, height: h))
        }
        context.translateBy(x: 0, y: h)
        context.scaleBy(x: scale, y: -scale)
        SceneRenderer().draw(elements, in: context)
        guard let image = context.makeImage() else { throw RenderFailure.imageUnavailable }
        return image
    }

    public static func data(elements: [Element], size: CGSize, scale: Double = 1,
                            background: RGBA? = nil) throws -> Data {
        let image = try image(elements: elements, size: size, scale: scale, background: background)
        return try encode(image)
    }

    public static func encode(_ image: CGImage) throws -> Data {
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil) else {
            throw RenderFailure.encodingFailed
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw RenderFailure.encodingFailed }
        return output as Data
    }
}
