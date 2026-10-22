import AppKit
import QuartzCore
import AnnotationCore

@MainActor
final class MagicToolbarEffect {
    struct Pose {
        let scale: CGFloat
        let imageOpacity: Float
        let glowOpacity: Float
        let radius: CGFloat
        let sparkOpacity: Float
    }

    static let animationKey = "magic-toolbar-poof"
    static let sparkCount = 12
    private let imageLayer: CALayer
    private var effects: [CALayer] = []
    private let started: CFTimeInterval
    private let duration: Double
    private let from: Double
    private let to: Double
    var layerCount: Int { effects.count }
    var endTime: CFTimeInterval { started + duration }
    var currentProgress: Double { progress(at: CACurrentMediaTime()) }
    var imageState: WorkspaceLayerMotion.State {
        let pose = Self.pose(at: currentProgress)
        return WorkspaceLayerMotion.State(transform: CATransform3DMakeScale(pose.scale, pose.scale, 1),
                                          opacity: pose.imageOpacity)
    }

    private static func smooth(_ lower: Double, _ upper: Double, _ value: Double) -> Double {
        let t = min(1, max(0, (value - lower) / (upper - lower)))
        return t * t * (3 - 2 * t)
    }

    static func pose(at progress: Double) -> Pose {
        let shrink = smooth(0, 0.62, progress)
        let burst = smooth(0.62, 1, progress)
        return Pose(scale: 1 - 0.985 * shrink,
                    imageOpacity: Float(1 - smooth(0.44, 0.64, progress)),
                    glowOpacity: Float(smooth(0.30, 0.60, progress) * (1 - smooth(0.70, 0.95, progress))),
                    radius: 46 * burst,
                    sparkOpacity: Float(smooth(0.62, 0.72, progress) * (1 - smooth(0.78, 1, progress))))
    }

    func progress(at time: CFTimeInterval) -> Double {
        let t = Self.smooth(0, 1, (time - started) / duration)
        return from + (to - from) * t
    }

    init(imageLayer: CALayer, appearing: Bool, started: CFTimeInterval, duration: Double,
         progress: Double? = nil) throws {
        guard let parent = imageLayer.superlayer, duration > 0 else { throw CanvasError.invalidGeometry }
        self.imageLayer = imageLayer
        self.started = started
        from = progress ?? (appearing ? 1 : 0)
        to = appearing ? 0 : 1
        // A reversal that is nearly home finishes proportionally sooner.
        self.duration = progress == nil ? duration :
            duration * max(WorkspaceLayerMotion.minimumReversalShare, abs(to - from))
        let poses = (0...60).map { step in
            Self.pose(at: from + (to - from) * Self.smooth(0, 1, Double(step) / 60))
        }
        install(on: imageLayer, animations: [
            keys("transform", poses.map { NSValue(caTransform3D: CATransform3DMakeScale($0.scale, $0.scale, 1)) }),
            keys("opacity", poses.map(\.imageOpacity))
        ])

        let blue = NSColor(srgbRed: 0.25, green: 0.7, blue: 1, alpha: 1)
        let glow = CAGradientLayer()
        glow.type = .radial
        glow.bounds = CGRect(x: 0, y: 0, width: 36, height: 36)
        glow.position = imageLayer.position
        glow.startPoint = CGPoint(x: 0.5, y: 0.5)
        glow.endPoint = CGPoint(x: 1, y: 1)
        glow.colors = [NSColor.white.cgColor, blue.withAlphaComponent(0.8).cgColor, blue.withAlphaComponent(0).cgColor]
        glow.locations = [0, 0.22, 1]
        glow.contentsScale = imageLayer.contentsScale
        glow.opacity = 0
        parent.addSublayer(glow)
        effects.append(glow)
        install(on: glow, animations: [keys("opacity", poses.map(\.glowOpacity))])

        for index in 0..<Self.sparkCount {
            let spark = CALayer()
            spark.bounds = CGRect(x: 0, y: 0, width: 2.5, height: 2.5)
            spark.cornerRadius = 1.25
            spark.contentsScale = imageLayer.contentsScale
            spark.backgroundColor = index.isMultiple(of: 3) ? NSColor.white.cgColor : blue.cgColor
            spark.opacity = 0
            parent.addSublayer(spark)
            effects.append(spark)
            let angle = CGFloat(index) * 2 * .pi / CGFloat(Self.sparkCount)
            let distance = 0.7 + CGFloat(index % 4) * 0.1
            let positions = poses.map { pose in
                NSValue(point: CGPoint(x: imageLayer.position.x + cos(angle) * pose.radius * distance,
                                       y: imageLayer.position.y + sin(angle) * pose.radius * distance))
            }
            install(on: spark, animations: [keys("position", positions), keys("opacity", poses.map(\.sparkOpacity))])
        }
    }

    func cancel() {
        imageLayer.removeAnimation(forKey: Self.animationKey)
        effects.forEach { $0.removeAllAnimations(); $0.removeFromSuperlayer() }
        effects.removeAll()
    }

    private func keys(_ property: String, _ values: [Any]) -> CAKeyframeAnimation {
        let animation = CAKeyframeAnimation(keyPath: property)
        animation.values = values
        animation.keyTimes = values.indices.map { NSNumber(value: Double($0) / Double(values.count - 1)) }
        return animation
    }

    private func install(on layer: CALayer, animations: [CAAnimation]) {
        let group = CAAnimationGroup()
        animations.forEach { $0.duration = duration }
        group.animations = animations
        group.duration = duration
        group.beginTime = layer.convertTime(started, from: nil)
        group.timingFunction = CAMediaTimingFunction(name: .linear)
        group.fillMode = .both
        group.isRemovedOnCompletion = false
        layer.add(group, forKey: Self.animationKey)
    }
}
