import Foundation

public enum ShareRegionPreset: String, CaseIterable, Sendable {
    case leftHalf, rightHalf, topHalf, bottomHalf
    case leftThird, centerThird, rightThird
    case topLeft, topRight, bottomLeft, bottomRight

    public var title: String {
        switch self {
        case .leftHalf: "Left half"
        case .rightHalf: "Right half"
        case .topHalf: "Top half"
        case .bottomHalf: "Bottom half"
        case .leftThird: "Left third"
        case .centerThird: "Center third"
        case .rightThird: "Right third"
        case .topLeft: "Top left"
        case .topRight: "Top right"
        case .bottomLeft: "Bottom left"
        case .bottomRight: "Bottom right"
        }
    }

    public var normalizedBounds: Rect {
        switch self {
        case .leftHalf: Rect(x: 0, y: 0, width: 0.5, height: 1)
        case .rightHalf: Rect(x: 0.5, y: 0, width: 0.5, height: 1)
        case .topHalf: Rect(x: 0, y: 0, width: 1, height: 0.5)
        case .bottomHalf: Rect(x: 0, y: 0.5, width: 1, height: 0.5)
        case .leftThird: Rect(x: 0, y: 0, width: 1.0 / 3, height: 1)
        case .centerThird: Rect(x: 1.0 / 3, y: 0, width: 1.0 / 3, height: 1)
        case .rightThird: Rect(x: 2.0 / 3, y: 0, width: 1.0 / 3, height: 1)
        case .topLeft: Rect(x: 0, y: 0, width: 0.5, height: 0.5)
        case .topRight: Rect(x: 0.5, y: 0, width: 0.5, height: 0.5)
        case .bottomLeft: Rect(x: 0, y: 0.5, width: 0.5, height: 0.5)
        case .bottomRight: Rect(x: 0.5, y: 0.5, width: 0.5, height: 0.5)
        }
    }
}

public struct SharingRegion: Equatable, Sendable {
    public static let minimumDimension: Double = 32
    public let bounds: Rect
    public let displayBounds: Rect

    public init(bounds: Rect, displayBounds: Rect) throws {
        guard displayBounds.isValid, bounds.isValid, displayBounds.contains(bounds),
              bounds.width >= Self.minimumDimension, bounds.height >= Self.minimumDimension else {
            throw CanvasError.invalidGeometry
        }
        self.bounds = bounds
        self.displayBounds = displayBounds
    }

    public init(preset: ShareRegionPreset, displayBounds: Rect) throws {
        let unit = preset.normalizedBounds
        try self.init(bounds: Rect(
            x: displayBounds.x + unit.x * displayBounds.width,
            y: displayBounds.y + unit.y * displayBounds.height,
            width: unit.width * displayBounds.width, height: unit.height * displayBounds.height),
            displayBounds: displayBounds)
    }

    public func sourceBounds(through viewport: Viewport?) -> Rect {
        guard let viewport else { return bounds }
        return Rect(from: viewport.sourcePoint(for: bounds.origin),
                    to: viewport.sourcePoint(for: Point(bounds.maxX, bounds.maxY)))
    }

    public func aspectFit(in output: Rect) throws -> Rect {
        guard output.isValid, output.width > 0, output.height > 0 else { throw CanvasError.invalidGeometry }
        let scale = min(output.width / bounds.width, output.height / bounds.height)
        let width = bounds.width * scale, height = bounds.height * scale
        return Rect(x: output.x + (output.width - width) / 2,
                    y: output.y + (output.height - height) / 2, width: width, height: height)
    }
}
