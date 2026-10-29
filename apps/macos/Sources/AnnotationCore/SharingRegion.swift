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

    /// The output window's distance from the edges of the display's usable area.
    public static let outputMargin: Double = 24
    public static let outputMinimumWidth: Double = 320
    public static let outputMinimumHeight: Double = 200

    /// Where the output window's content goes when sharing starts, in the coordinates of `bounds`. Meeting apps send
    /// the window at its own size, so the content shows the region at its own size, or as large as fits in `available`
    /// (the display's usable area) with `outputMargin` around the window's frame. The window takes the corner of
    /// `available` that covers the least of the region, top right first, so it stays out of the shared area when it can.
    public func outputContentFrame(available: Rect, frame insets: FrameInsets) throws -> Rect {
        guard available.isValid, available.width > 0, available.height > 0, insets.isValid else {
            throw CanvasError.invalidGeometry
        }
        let margin = Self.outputMargin
        let chromeWidth = insets.left + insets.right, chromeHeight = insets.top + insets.bottom
        let scale = min(1, (available.width - 2 * margin - chromeWidth) / bounds.width,
                        (available.height - 2 * margin - chromeHeight) / bounds.height)
        let width = max(1, min(max(bounds.width * scale, Self.outputMinimumWidth), available.width - chromeWidth))
        let height = max(1, min(max(bounds.height * scale, Self.outputMinimumHeight), available.height - chromeHeight))
        let frameWidth = width + chromeWidth, frameHeight = height + chromeHeight
        let right = max(available.x, available.maxX - frameWidth - margin)
        let left = max(available.x, min(available.maxX - frameWidth, available.x + margin))
        let top = max(available.y, min(available.maxY - frameHeight, available.y + margin))
        let bottom = max(available.y, available.maxY - frameHeight - margin)
        var best = Rect.zero, covered = Double.infinity
        for (x, y) in [(right, top), (left, top), (right, bottom), (left, bottom)] {
            let frame = Rect(x: x, y: y, width: frameWidth, height: frameHeight)
            let overlap = max(0, min(frame.maxX, bounds.maxX) - max(frame.x, bounds.x)) *
                max(0, min(frame.maxY, bounds.maxY) - max(frame.y, bounds.y))
            if overlap < covered { best = frame; covered = overlap }
        }
        return Rect(x: best.x + insets.left, y: best.y + insets.top, width: width, height: height)
    }
}

/// The space a window's frame adds around its content, in points.
public struct FrameInsets: Equatable, Sendable {
    public var top: Double
    public var left: Double
    public var bottom: Double
    public var right: Double

    public init(top: Double, left: Double = 0, bottom: Double = 0, right: Double = 0) {
        self.top = top; self.left = left; self.bottom = bottom; self.right = right
    }

    public var isValid: Bool {
        [top, left, bottom, right].allSatisfy { $0.isFinite && $0 >= 0 && $0 <= 10_000 }
    }
}
