import Foundation

/// Simulated pen pressure for a mouse or trackpad, so Ink pen strokes look drawn with ink: faster movement
/// draws a thinner line, slower movement a thicker one, and both ends taper. Feed every sample of a stroke
/// to `next(_:time:)` in order; when the stroke ends, `taperEnd(points:pressures:width:)` thins its last part.
/// Each value depends only on earlier samples, so a stroke drawn so far never changes while it grows.
/// The Windows port (Annotator.Core InkPressure) must match it number for number.
public struct InkPressure: Sendable {
    /// Pressure at full speed, before tapering.
    public static let thinnest = 0.4
    /// Speed in points per second at which the line is thinnest.
    public static let fastSpeed = 1_500.0
    /// Shortest time in seconds over which speed is measured; samples closer together are added up first.
    public static let window = 0.008
    /// About how long in seconds the width takes to follow a change of speed.
    public static let settle = 0.06
    /// Longest pause in seconds counted between two samples.
    public static let longestStep = 0.1
    /// Pressure at the very tip of a tapered end.
    public static let tip = 0.35

    /// Path length in points over which each end tapers.
    public let taperLength: Double
    private var last: Point?
    private var lastTime = 0.0
    private var pendingDistance = 0.0
    private var pendingTime = 0.0
    private var level = 1.0
    private var travelled = 0.0

    /// `width` is the stroke width; wider strokes taper over a longer distance.
    public init(width: Double) {
        taperLength = max(12, (width.isFinite ? width : 0) * 4)
    }

    /// Pressure for the next sample of the stroke. `time` is in seconds and only differences matter.
    public mutating func next(_ point: Point, time: Double) -> Double {
        guard let previous = last else {
            last = point
            lastTime = time
            return Self.clamped(level * taper(0))
        }
        let distance = previous.distance(to: point)
        let step = time - lastTime
        travelled += distance
        pendingDistance += distance
        pendingTime += step.isFinite ? min(max(step, 0), Self.longestStep) : 0
        last = point
        lastTime = time
        if pendingTime >= Self.window {
            let speed = pendingDistance / pendingTime
            let target = 1 - (1 - Self.thinnest) * min(1, speed / Self.fastSpeed)
            level += (target - level) * min(1, pendingTime / Self.settle)
            pendingDistance = 0
            pendingTime = 0
        }
        return Self.clamped(level * taper(travelled))
    }

    /// The pressures with the last `taperLength` points of path thinned towards the end, as a pen lifts off.
    /// Returns `pressures` unchanged when it does not have one value per point or the stroke has one point.
    public static func taperEnd(points: [Point], pressures: [Double], width: Double) -> [Double] {
        guard points.count > 1, pressures.count == points.count else { return pressures }
        let shape = InkPressure(width: width)
        var result = pressures
        var remaining = 0.0
        var index = points.count - 1
        while index > 0 && remaining < shape.taperLength {
            result[index] = clamped(result[index] * shape.taper(remaining))
            remaining += points[index].distance(to: points[index - 1])
            index -= 1
        }
        return result
    }

    private func taper(_ distance: Double) -> Double {
        let progress = min(1, distance / taperLength)
        return Self.tip + (1 - Self.tip) * progress * (2 - progress)
    }

    private static func clamped(_ pressure: Double) -> Double {
        pressure.isFinite ? min(1, max(0, pressure)) : 1
    }
}
