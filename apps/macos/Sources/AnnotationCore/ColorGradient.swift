import Foundation

public struct ColorGradient: Codable, Hashable, Sendable {
    public var colors: [RGBA]
    public var angle: Double

    public init(colors: [RGBA], angle: Double = 0) {
        self.colors = colors
        self.angle = angle
    }

    public func validate() throws {
        guard (2...8).contains(colors.count), colors.allSatisfy({ $0.isValid && $0.alpha == 1 }),
              angle.isFinite, (0...360).contains(angle) else { throw CanvasError.invalidStyle }
    }
}

public enum GradientPreset: String, CaseIterable, Sendable {
    case sunset, lagoon, ember, spectrum, dusk, citrus, ocean, orchid

    public var label: String { rawValue.capitalized }
    public var gradient: ColorGradient {
        let colors: [RGBA] = switch self {
        case .sunset: [RGBA(0.96, 0.12, 0.67), RGBA(1, 0.23, 0.39), RGBA(1, 0.48, 0.2)]
        case .lagoon: [RGBA(0.13, 0.48, 0.96), RGBA(0.05, 0.73, 0.89), RGBA(0.05, 0.82, 0.66)]
        case .ember: [RGBA(0.91, 0.19, 0.35), RGBA(1, 0.53, 0.16), RGBA(1, 0.85, 0.3)]
        case .spectrum: [
            RGBA(0.93, 0.19, 0.49), RGBA(0.63, 0.27, 0.9), RGBA(0.16, 0.48, 0.95),
            RGBA(0.03, 0.72, 0.64), RGBA(0.98, 0.74, 0.2), RGBA(0.97, 0.31, 0.29)
        ]
        case .dusk: [RGBA(0.37, 0.28, 0.89), RGBA(0.67, 0.29, 0.8), RGBA(0.88, 0.32, 0.66)]
        case .citrus: [RGBA(1, 0.55, 0.12), RGBA(0.97, 0.83, 0.2), RGBA(0.42, 0.76, 0.3)]
        case .ocean: [RGBA(0.08, 0.79, 0.74), RGBA(0.18, 0.51, 0.94), RGBA(0.47, 0.3, 0.85)]
        case .orchid: [RGBA(0.98, 0.43, 0.69), RGBA(0.7, 0.44, 0.91), RGBA(0.38, 0.63, 0.97)]
        }
        return ColorGradient(colors: colors, angle: 25)
    }
}

extension Style {
    public static let defaultFillOpacity = 0.4
    public var usesGradient: Bool { strokeGradient != nil || fillGradient != nil }
    public var paintEstimatedBytes: Int {
        ((strokeGradient?.colors.count ?? 0) + (fillGradient?.colors.count ?? 0)) * MemoryLayout<RGBA>.stride
    }
    public func paintOpacity(fill isFill: Bool) -> Double {
        isFill ? fill?.alpha ?? Self.defaultFillOpacity : color.alpha
    }

    public mutating func setPaintOpacity(_ value: Double, fill isFill: Bool) {
        if isFill { fill?.alpha = value } else { color.alpha = value }
    }

    public mutating func matchFillToStroke() {
        let alpha = paintOpacity(fill: true)
        fill = RGBA(color.red, color.green, color.blue, alpha)
        fillGradient = strokeGradient
    }

    public mutating func setFillEnabled(_ enabled: Bool) {
        if enabled {
            if fill == nil { matchFillToStroke() }
        } else {
            fill = nil
            fillGradient = nil
        }
    }

    public mutating func setPaintColor(_ value: RGBA?, fill isFill: Bool, useColorAlpha: Bool = false) {
        guard var value else {
            if isFill { fill = nil; fillGradient = nil }
            return
        }
        if !useColorAlpha { value.alpha = paintOpacity(fill: isFill) }
        if isFill { fill = value; fillGradient = nil }
        else { color = value; strokeGradient = nil }
    }

    public mutating func setGradient(_ gradient: ColorGradient, fill isFill: Bool) {
        let alpha = paintOpacity(fill: isFill)
        var fallback = gradient.colors.first ?? color
        fallback.alpha = alpha
        if isFill { fill = fallback; fillGradient = gradient }
        else { color = fallback; strokeGradient = gradient }
    }
}
