import AppKit
import CoreImage
import QuartzCore

final class VisibilityTransitionPanel: NSPanel {
    /// The toolbar's picture carries live glass (`VisibilityImageView.addGlass`). Such a panel leaves at once: at the
    /// toolbar's level, AppKit fades a panel out as it's ordered out, and fading glass would lie over the toolbar it hands
    /// over to, washing it out for a moment.
    var showsGlass = false { didSet { animationBehavior = showsGlass ? .none : .default } }

    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        // Particle padding may cross a screen edge; shifting it would move
        // the image away from the real toolbar or drawing.
        frameRect
    }

    /// Keeps the glass under the toolbar's picture in the same full, active look as the toolbar's own (`ToolbarGlass`).
    @objc func _hasActiveAppearanceIgnoringKeyFocus() -> Bool {
        ToolbarGlass.activeAppearance(of: self, showsGlass: showsGlass, inheritedFrom: NSPanel.self)
    }
}

final class VisibilityImageView: NSView {
    static let blurFilterName = "visibilityBlur"
    static let pulseKey = "visibility-absorb"
    static let glassFadeKey = "visibility-glass-fade"
    /// Fills the view in the same coordinates, anchored on the image center, so it can pulse
    /// or blur the image (and any trails) without changing `imageLayer.frame`.
    let motionContainer = CALayer()
    let imageLayer = CALayer()
    let imageFrame: CGRect?
    private(set) var blurEnabled = false
    /// Live glass under the picture, for Glass toolbar (WIN-UI-09): a picture can't hold glass. Both views sit at the
    /// picture's center, which is their layers' anchor, so they turn the picture's motion around the same point:
    /// `glassMotion` follows `motionContainer` and `glassPicture`, inside it, follows `imageLayer`.
    private(set) var glassMotion: NSView?
    private(set) var glassPicture: NSView?

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

    /// Puts live glass of the toolbar's `surfaces` under the picture. Their frames are measured from the picture's
    /// bottom-left corner.
    func addGlass(_ surfaces: [ToolbarGlass.Surface]) {
        guard glassMotion == nil, !surfaces.isEmpty else { return }
        let picture = imageFrame ?? bounds
        let motion = GlassMirrorView(frame: CGRect(x: picture.midX, y: picture.midY, width: 0, height: 0))
        let mirror = GlassMirrorView(frame: .zero)
        motion.addSubview(mirror)
        for surface in surfaces {
            let plate = ToolbarChromeView(cornerRadius: surface.cornerRadius, glass: true)
            plate.frame = surface.frame.offsetBy(dx: -picture.width / 2, dy: -picture.height / 2)
            mirror.addSubview(plate)
        }
        addSubview(motion, positioned: .below, relativeTo: subviews.first)
        // The picture stays above its glass.
        motionContainer.zPosition = 1
        glassMotion = motion
        glassPicture = mirror
    }

    /// Gives the glass the picture's motion, blur and pulses. Call once they are all in place.
    func moveGlassWithPicture() {
        guard let motion = glassMotion, let motionLayer = motion.layer, let pictureLayer = glassPicture?.layer else { return }
        if blurEnabled, let filter = CIFilter(name: "CIGaussianBlur") {
            motion.layerUsesCoreImageFilters = true
            filter.name = Self.blurFilterName
            filter.setValue(0, forKey: kCIInputRadiusKey)
            motionLayer.filters = [filter]
        }
        Self.copyAnimations(from: motionContainer, to: motionLayer)
        Self.copyAnimations(from: imageLayer, to: pictureLayer)
    }

    /// Fades the glass through `opacities`, spread evenly from `begin` over `duration`, for a motion that doesn't move
    /// the picture itself (Dissolve's particles).
    func fadeGlass(_ opacities: [Float], from begin: CFTimeInterval, duration: Double) {
        guard let layer = glassPicture?.layer, opacities.count > 1, duration > 0 else { return }
        let fade = CAKeyframeAnimation(keyPath: "opacity")
        fade.values = opacities.map { NSNumber(value: $0) }
        fade.keyTimes = opacities.indices.map { NSNumber(value: Double($0) / Double(opacities.count - 1)) }
        fade.duration = duration
        fade.beginTime = layer.convertTime(begin, from: nil)
        fade.fillMode = .both
        fade.isRemovedOnCompletion = false
        layer.add(fade, forKey: Self.glassFadeKey)
    }

    private static func copyAnimations(from source: CALayer, to target: CALayer) {
        for key in source.animationKeys() ?? [] {
            if let animation = source.animation(forKey: key) { target.add(animation, forKey: key) }
        }
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

/// A view of no size whose subviews draw around it. AppKit anchors its layer at its origin, so a transform on the layer
/// turns around that point.
private final class GlassMirrorView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        clipsToBounds = false
    }

    required init?(coder: NSCoder) { nil }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func isAccessibilityElement() -> Bool { false }
}
