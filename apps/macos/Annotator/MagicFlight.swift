import AppKit
import QuartzCore
import AnnotationCore

enum MagicFlight {
    static let steps = 96
    static let maximumBend: CGFloat = 110
    static let maximumBank: CGFloat = 0.15
    /// Peak of `(1-t)^1.6 · sin(πt)`, so the envelope tops out at one.
    private static let envelopePeak: CGFloat = 0.4572

    static func seed(for identifier: String) -> UInt64 {
        identifier.utf8.reduce(14_695_981_039_346_656_037) { ($0 ^ UInt64($1)) &* 1_099_511_628_211 }
    }

    /// One confident swoop (one to one and a half turns) that is strongest early and glides straight into place.
    static func transforms(from: CATransform3D, to: CATransform3D, seed: UInt64) -> [CATransform3D] {
        let dx = to.m41 - from.m41, dy = to.m42 - from.m42
        let distance = hypot(dx, dy)
        let direction = distance > 0.001 ? CGPoint(x: dx / distance, y: dy / distance) : CGPoint(x: 1, y: 0)
        let normal = CGPoint(x: -direction.y, y: direction.x)
        let amplitude = min(maximumBend, max(24, distance * 0.18))
        let cycles = 1 + CGFloat(seed % 2) * 0.5
        let phase = CGFloat((seed >> 8) % 360) * .pi / 180
        let startScale = hypot(from.m11, from.m12), endScale = hypot(to.m11, to.m12)
        let startAngle = atan2(from.m12, from.m11), endAngle = atan2(to.m12, to.m11)

        func point(_ t: CGFloat) -> CGPoint {
            let envelope = pow(1 - t, 1.6) * sin(.pi * t) / envelopePeak
            let side = amplitude * envelope * sin(2 * .pi * cycles * t + phase)
            let forward = amplitude * 0.12 * envelope * sin(2 * .pi * t + phase)
            return CGPoint(x: from.m41 + dx * t + direction.x * forward + normal.x * side,
                           y: from.m42 + dy * t + direction.y * forward + normal.y * side)
        }

        return (0...steps).map { step in
            if step == 0 { return from }
            if step == steps { return to }
            let t = CGFloat(step) / CGFloat(steps)
            let location = point(t)
            let before = point(max(0, t - 0.003)), after = point(min(1, t + 0.003))
            let vx = after.x - before.x, vy = after.y - before.y
            let turn = atan2(vx * normal.x + vy * normal.y, vx * direction.x + vy * direction.y)
            let bank = min(maximumBank, max(-maximumBank, startAngle * (1 - t) + endAngle * t + turn * 0.18 * sin(.pi * t)))
            let scale = startScale + (endScale - startScale) * t
            var transform = CATransform3DMakeRotation(bank, 0, 0, 1)
            transform = CATransform3DScale(transform, scale, scale, 1)
            transform.m41 = location.x
            transform.m42 = location.y
            return transform
        }
    }

    static var timingFunction: CAMediaTimingFunction {
        CAMediaTimingFunction(controlPoints: 0.22, 0.06, 0.24, 1)
    }
}

@MainActor
final class MagicTrail {
    private var layers: [CALayer] = []
    var layerCount: Int { layers.count }

    init(imageLayer: CALayer, transforms: [CATransform3D], seed: UInt64,
         started: CFTimeInterval, duration: Double) throws {
        guard let parent = imageLayer.superlayer, transforms.count > 1 else { throw CanvasError.invalidGeometry }
        let points = transforms.map {
            CGPoint(x: imageLayer.position.x + $0.m41, y: imageLayer.position.y + $0.m42)
        }
        let path = CGMutablePath()
        path.addLines(between: points)
        var lengths = [CGFloat(0)]
        for index in 1..<points.count {
            lengths.append(lengths[index - 1] + hypot(points[index].x - points[index - 1].x,
                                                     points[index].y - points[index - 1].y))
        }
        let total = max(1, lengths.last ?? 1)
        let fractions = lengths.map { Double($0 / total) }
        let tail = min(0.28, 110 / Double(total))
        let times = points.indices.map { NSNumber(value: Double($0) / Double(points.count - 1)) }
        let blue = NSColor(srgbRed: 0.3, green: 0.75, blue: 1, alpha: 1).cgColor
        let white = NSColor(srgbRed: 0.85, green: 0.96, blue: 1, alpha: 1).cgColor
        let gold = NSColor(srgbRed: 1, green: 0.76, blue: 0.3, alpha: 1).cgColor

        for (width, opacity) in [(9.0, 0.10), (4.0, 0.30), (1.25, 0.85)] {
            let trail = CAShapeLayer()
            trail.frame = parent.bounds
            trail.contentsScale = imageLayer.contentsScale
            trail.path = path
            trail.fillColor = nil
            trail.strokeColor = width < 2 ? white : blue
            trail.lineWidth = width
            trail.lineCap = .round
            trail.lineJoin = .round
            trail.opacity = 0
            parent.insertSublayer(trail, below: imageLayer)
            let end = keyframes("strokeEnd", values: fractions, times: times)
            let start = keyframes("strokeStart", values: fractions.map { max(0, $0 - tail) }, times: times)
            install(on: trail, animations: [start, end, fade(peak: opacity)], started: started, duration: duration)
        }
        for index in 0..<6 {
            let spark = CALayer()
            spark.contentsScale = imageLayer.contentsScale
            let diameter = index.isMultiple(of: 2) ? 2.0 : 3.0
            spark.bounds = CGRect(x: 0, y: 0, width: diameter, height: diameter)
            spark.cornerRadius = diameter / 2
            spark.backgroundColor = index.isMultiple(of: 3) ? gold : white
            spark.opacity = 0
            let phase = CGFloat((seed &+ UInt64(index) &* 31) % 360) * .pi / 180
            let positions = points.indices.map { sample -> NSValue in
                let t = CGFloat(sample) / CGFloat(points.count - 1)
                let point = points[max(0, sample - 2 - index * 2)]
                let spread = sin(.pi * t) * CGFloat(3 + index)
                return NSValue(point: CGPoint(x: point.x + sin(t * 12 + phase) * spread,
                                              y: point.y + cos(t * 9 + phase) * spread))
            }
            parent.insertSublayer(spark, below: imageLayer)
            let position = keyframes("position", values: positions, times: times)
            install(on: spark, animations: [position, fade(peak: 0.9)], started: started, duration: duration)
        }
    }

    func cancel() {
        layers.forEach { $0.removeAllAnimations(); $0.removeFromSuperlayer() }
        layers.removeAll()
    }

    private func keyframes(_ key: String, values: [Any], times: [NSNumber]) -> CAKeyframeAnimation {
        let animation = CAKeyframeAnimation(keyPath: key)
        animation.values = values
        animation.keyTimes = times
        return animation
    }

    private func fade(peak: Double) -> CAKeyframeAnimation {
        keyframes("opacity", values: [0.0, peak, peak * 0.7, 0.0], times: [0, 0.12, 0.75, 1])
    }

    private func install(on layer: CALayer, animations: [CAAnimation], started: CFTimeInterval, duration: Double) {
        let group = CAAnimationGroup()
        animations.forEach { $0.duration = duration }
        group.animations = animations
        group.duration = duration
        group.beginTime = layer.convertTime(started, from: nil)
        group.timingFunction = MagicFlight.timingFunction
        group.fillMode = .both
        group.isRemovedOnCompletion = false
        layer.add(group, forKey: WorkspaceLayerMotion.animationKey)
        layers.append(layer)
    }
}
