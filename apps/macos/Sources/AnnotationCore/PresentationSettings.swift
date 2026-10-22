import Foundation

public struct PresentationSettings: Equatable, Sendable {
    public enum Mode: String, Sendable { case off, lens, screen }
    public var mode: Mode
    public var zoom: Double

    public init(mode: Mode = .off, zoom: Double = 2) {
        self.mode = mode; self.zoom = zoom
    }
    public func validate() throws {
        guard zoom.isFinite, (1...16).contains(zoom) else { throw CanvasError.invalidGeometry }
    }
}
