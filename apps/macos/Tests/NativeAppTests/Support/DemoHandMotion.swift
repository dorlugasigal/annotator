import Foundation
import AnnotationCore

struct DemoHandMotion {
    private(set) var seed: UInt64

    mutating func next() -> Double {
        seed = seed &* 6364136223846793005 &+ 1442695040888963407
        return Double(seed >> 11) / Double(UInt64.max >> 11)
    }

    mutating func duration(_ base: Double) -> Double { base * (0.82 + next() * 0.36) }

    mutating func path(from start: Point, to end: Point, samples: Int, bend: Double) -> [Point] {
        precondition(samples >= 2)
        let phase = next() * .pi * 2
        let offset = (next() * 2 - 1) * bend
        let delta = end - start
        let length = max(1, hypot(delta.x, delta.y))
        let normal = Point(-delta.y / length, delta.x / length)
        return (0...samples).map { index in
            if index == 0 { return start }
            if index == samples { return end }
            let t = Double(index) / Double(samples)
            let eased = t * t * t * (10 + t * (-15 + 6 * t))
            let progress = eased * 0.85 + t * 0.15
            let drift = sin(.pi * t) * (offset + sin(t * .pi * 3 + phase) * bend * 0.15)
            return start + delta * progress + normal * drift
        }
    }
}
