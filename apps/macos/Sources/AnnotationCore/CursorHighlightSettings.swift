import Foundation

public struct CursorHighlightSettings: Codable, Equatable, Sendable {
    public enum Shape: String, Codable, CaseIterable, Sendable {
        case ring, disc, roundedSquare, square, diamond, hexagon, crosshair

        public var label: String {
            switch self {
            case .ring: "Ring"
            case .disc: "Disc"
            case .roundedSquare: "Squircle"
            case .square: "Square"
            case .diamond: "Diamond"
            case .hexagon: "Hexagon"
            case .crosshair: "Crosshair"
            }
        }
    }
    public enum Visibility: String, Codable, CaseIterable, Sendable { case always, moving, clicks }
    public enum ClickEffect: String, Codable, CaseIterable, Sendable {
        case off, pulse, ripple, turn
        public var label: String { rawValue.capitalized }
    }
    public var shape: Shape = .ring
    public var visibility: Visibility = .always
    public var color = RGBA(1, 0.82, 0)
    public var diameter: Double = 36
    public var borderWidth: Double = 3
    public var showClicks = true
    public var glowRadius: Double?
    public var borderStyle: StrokePattern?
    public var clickEffect: ClickEffect?
    public static let clickDuration = 0.45
    public var resolvedGlow: Double { glowRadius ?? 0 }
    public var resolvedBorder: StrokePattern { borderStyle ?? .solid }
    public var resolvedClickEffect: ClickEffect { showClicks ? clickEffect ?? .pulse : .off }

    public init() {}

    public func validate() throws {
        if let glowRadius, !glowRadius.isFinite || !(0...24).contains(glowRadius) { throw CanvasError.invalidStyle }
        guard color.isValid, diameter.isFinite, (16...160).contains(diameter),
              borderWidth.isFinite, (1...12).contains(borderWidth) else { throw CanvasError.invalidStyle }
    }

    public func isVisible(now: Double, lastMovement: Double?, lastClick: Double?) -> Bool {
        switch visibility {
        case .always: true
        case .moving:
            (lastMovement.map { now >= $0 && now - $0 < 0.75 } ?? false) ||
                (lastClick.map { now >= $0 && now - $0 < Self.clickDuration } ?? false)
        case .clicks: lastClick.map { now >= $0 && now - $0 < Self.clickDuration } ?? false
        }
    }

    public static func clickProgress(now: Double, startedAt: Double?) -> Double? {
        guard let startedAt, now.isFinite, startedAt.isFinite,
              now >= startedAt, now - startedAt < clickDuration else { return nil }
        return (now - startedAt) / clickDuration
    }
}
