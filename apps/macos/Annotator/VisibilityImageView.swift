import AppKit
import CoreImage
import QuartzCore

final class VisibilityTransitionPanel: NSPanel {
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        // Particle padding may cross a screen edge; shifting it would move
        // the image away from the real toolbar or drawing.
        frameRect
    }
}

final class VisibilityImageView: NSView {
    static let blurFilterName = "visibilityBlur"
    static let pulseKey = "visibility-absorb"
    /// Fills the view in the same coordinates, anchored on the image center, so it can pulse
    /// or blur the image (and any trails) without changing `imageLayer.frame`.
    let motionContainer = CALayer()
    let imageLayer = CALayer()
    let imageFrame: CGRect?
    private(set) var blurEnabled = false

    init(image: CGImage, frame: CGRect, imageFrame: CGRect? = nil) {
        self.imageFrame = imageFrame
        super.init(frame: frame)
        wantsLayer = true
        imageLayer.contents = image
        imageLayer.contentsGravity = .resize
        imageLayer.frame = imageFrame ?? bounds
        imageLayer.contentsScale = CGFloat(image.width) / max(1, imageLayer.bounds.width)
        motionContainer.addSublayer(imageLayer)
        layer?.addSublayer(motionContainer)
        layoutContainer()
    }

    required init?(coder: NSCoder) { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layoutContainer()
        imageLayer.frame = imageFrame ?? bounds
        CATransaction.commit()
    }

    private func layoutContainer() {
        let center = CGPoint(x: (imageFrame ?? bounds).midX, y: (imageFrame ?? bounds).midY)
        motionContainer.bounds = bounds
        motionContainer.anchorPoint = CGPoint(x: bounds.width > 0 ? (center.x - bounds.minX) / bounds.width : 0.5,
                                              y: bounds.height > 0 ? (center.y - bounds.minY) / bounds.height : 0.5)
        motionContainer.position = center
    }

    /// Adds a Gaussian blur that `WorkspaceLayerMotion` animates through `filters.visibilityBlur.inputRadius`.
    func enableBlur() {
        guard !blurEnabled, let filter = CIFilter(name: "CIGaussianBlur") else { return }
        layerUsesCoreImageFilters = true
        filter.name = Self.blurFilterName
        filter.setValue(0, forKey: kCIInputRadiusKey)
        motionContainer.filters = [filter]
        blurEnabled = true
    }

    /// Small scale bumps around the image center, one per arrival time, for the toolbar absorbing shapes.
    func absorbPulses(at arrivals: [CFTimeInterval], amplitude: CGFloat = 0.035, width: Double = 0.13) {
        let times = ToolbarSlideMotion.pulseTimes(arrivals)
        guard let first = times.first, let last = times.last else { return }
        let span = last + width - first
        let steps = max(2, Int(span * 120))
        let values = (0...steps).map { step -> NSNumber in
            let time = first + span * Double(step) / Double(steps)
            return NSNumber(value: Double(1 + amplitude * ToolbarSlideMotion.pulse(at: time, arrivals: times, width: width)))
        }
        let pulse = CAKeyframeAnimation(keyPath: "transform.scale")
        pulse.values = values
        pulse.keyTimes = (0...steps).map { NSNumber(value: Double($0) / Double(steps)) }
        pulse.duration = span
        pulse.beginTime = motionContainer.convertTime(first, from: nil)
        pulse.fillMode = .both
        pulse.isRemovedOnCompletion = false
        motionContainer.add(pulse, forKey: Self.pulseKey)
    }
}
