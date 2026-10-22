import AppKit
import QuartzCore
import AnnotationCore
import PresentationRendering

@MainActor
final class SharedWorkspaceTransition: NSView {
    static let maximumImagePixels = 20_000_000
    let id = UUID()
    let screenRegion: CGRect
    private(set) var isPrepared = false
    private(set) var isFinishing = false
    private var imagePixels = 0
    private var motions: [WorkspaceLayerMotion] = []
    private var particles: [ParticleBurstView] = []
    private var trails: [MagicTrail] = []
    private var poofs: [MagicToolbarEffect] = []
    private var framePump: Task<Void, Never>?
    private var protection: [CGRect] = []
    private let protectionMask = CAShapeLayer()
    var onFailure: ((Error) -> Void)?
    var animatedImageCount: Int { subviews.count }
    var hasFramePump: Bool { framePump != nil }
    override var isFlipped: Bool { false }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    init(screenRegion: CGRect) {
        self.screenRegion = screenRegion
        super.init(frame: CGRect(origin: .zero, size: screenRegion.size))
        wantsLayer = true
        layer?.masksToBounds = true
        bounds = screenRegion
        protectionMask.fillColor = NSColor.white.cgColor
        layer?.mask = protectionMask
        updateProtection([])
    }

    required init?(coder: NSCoder) { nil }

    static func unprotectedPath(in region: CGRect, excluding protection: [CGRect]) -> CGPath {
        var path = CGPath(rect: region, transform: nil)
        for rect in protection {
            let clipped = rect.intersection(region)
            if !clipped.isNull && !clipped.isEmpty {
                path = path.subtracting(CGPath(rect: clipped, transform: nil), using: .winding)
            }
        }
        return path
    }

    static func clippedImage(_ image: CGImage, screenFrame: CGRect, region: CGRect,
                             protection: [CGRect]) throws -> CGImage? {
        guard screenFrame.intersects(region) else { return nil }
        let visiblePath = unprotectedPath(in: screenFrame.intersection(region), excluding: protection)
        guard !visiblePath.isEmpty else { return nil }
        guard screenFrame.width > 0, screenFrame.height > 0,
              image.width * image.height <= maximumImagePixels,
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: image.width, height: image.height,
                  bitsPerComponent: 8, bytesPerRow: 0, space: space,
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw RenderFailure.invalidSize
        }
        context.scaleBy(x: CGFloat(image.width) / screenFrame.width, y: CGFloat(image.height) / screenFrame.height)
        context.translateBy(x: -screenFrame.minX, y: -screenFrame.minY)
        context.addPath(visiblePath)
        context.clip()
        context.draw(image, in: screenFrame)
        guard WorkspaceVisibilityAnimator.visibleBounds(in: context) != nil else { return nil }
        guard let clipped = context.makeImage() else { throw RenderFailure.imageUnavailable }
        return clipped
    }

    func updateProtection(_ rects: [CGRect]) {
        guard protection != rects || protectionMask.path == nil else { return }
        protection = rects
        let local = rects.map { $0.offsetBy(dx: -screenRegion.minX, dy: -screenRegion.minY) }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        protectionMask.frame = bounds
        protectionMask.path = Self.unprotectedPath(in: CGRect(origin: .zero, size: screenRegion.size), excluding: local)
        CATransaction.commit()
    }

    func add(image: CGImage, frame: CGRect, imageFrame: CGRect, style: VisibilityAnimationStyle,
             appearing: Bool, duration: Double, started: CFTimeInterval,
             initial: WorkspaceLayerMotion.State?, hiddenTransform: CATransform3D?,
             annotation: Bool, edge: ToolbarDockEdge = .bottom, magicSeed: UInt64? = nil,
             curveSign: CGFloat = 1, particleLimit: Int = 32_000, particleProgress: Double? = nil,
             toolbarPoofProgress: Double? = nil, particlesEnabled: Bool = true,
             clipToFrame: Bool = false, poseSeed: UInt64? = nil, sweepDirection: CGVector? = nil,
             absorbArrivals: [CFTimeInterval] = []) throws {
        let count = image.width * image.height
        guard count <= Self.maximumImagePixels - imagePixels else { throw RenderFailure.invalidSize }
        imagePixels += count
        let view = VisibilityImageView(image: image, frame: frame,
            imageFrame: imageFrame.offsetBy(dx: -frame.minX, dy: -frame.minY))
        view.layer?.masksToBounds = clipToFrame
        addSubview(view)
        if style == .focus && poseSeed != nil { view.enableBlur() }
        if !absorbArrivals.isEmpty { view.absorbPulses(at: absorbArrivals) }
        if style == .magic && !annotation {
            poofs.append(try MagicToolbarEffect(imageLayer: view.imageLayer, appearing: appearing,
                started: started, duration: duration, progress: toolbarPoofProgress))
        } else {
            let motion = WorkspaceLayerMotion(layer: view.imageLayer, style: style, appearing: appearing,
                flipped: false, duration: duration, initial: initial, started: started, edge: edge,
                hiddenTransform: hiddenTransform, annotation: annotation, magicSeed: magicSeed, curveSign: curveSign,
                poseSeed: poseSeed)
            motions.append(motion)
            if let magicSeed {
                trails.append(try MagicTrail(imageLayer: view.imageLayer, transforms: motion.flightTransforms,
                    seed: magicSeed, started: started, duration: motion.effectiveDuration))
            }
        }
        if style == .dissolve && particlesEnabled {
            let burst = try ParticleBurstView(image: image, imageFrame: view.imageLayer.frame,
                frame: view.bounds, particleLimit: particleLimit)
            burst.autoresizingMask = [.width, .height]
            if let sweepDirection { burst.sweepDirection = sweepDirection }
            burst.onFailure = { [weak self] error in self?.onFailure?(error) }
            view.addSubview(burst)
            view.imageLayer.isHidden = true
            burst.start(appearing: appearing, duration: duration, at: started, progress: particleProgress)
            burst.isPaused = true
            burst.enableSetNeedsDisplay = true
            particles.append(burst)
        }
    }

    func prepare() {
        isPrepared = true
        guard !particles.isEmpty else { return }
        framePump = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                particles.forEach { $0.draw() }
                do { try await Task.sleep(for: .milliseconds(33)) } catch { return }
            }
        }
    }

    func finish() { isFinishing = true }

    func cancel() {
        framePump?.cancel()
        framePump = nil
        motions.forEach { $0.cancel() }
        particles.forEach { $0.stop() }
        trails.forEach { $0.cancel() }
        poofs.forEach { $0.cancel() }
        motions.removeAll()
        particles.removeAll()
        trails.removeAll()
        poofs.removeAll()
        subviews.forEach { $0.removeFromSuperview() }
        removeFromSuperview()
    }
}
