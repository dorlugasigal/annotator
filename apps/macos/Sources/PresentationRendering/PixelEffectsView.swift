import AppKit
import CoreImage
import MetalKit
import AnnotationCore

public struct PixelFrameMetrics: Sendable {
    public let generation: UInt64
    public let encodeMilliseconds: Double
    public let submissionToCompletionMilliseconds: Double
    public let gpuMilliseconds: Double?
    public let textureWidth: Int
    public let textureHeight: Int
}

@MainActor
public final class PixelEffectsView: MTKView, MTKViewDelegate {
    private let imageContext: CIContext
    private let commands: MTLCommandQueue
    private let colorSpace: CGColorSpace
    private var latestBuffer: CVPixelBuffer?
    private var viewport: Viewport?
    private var effects: [Element] = []
    private var regionsOnly = false
    private var generation: UInt64 = 0
    private var sourceSize: CGSize = .zero
    private var includeImageLayers = true
    private var drawingLayer: CIImage?
    private var privacyRegion: Rect?
    private var screenOverlay: PixelScreenOverlay?
    private var requestedAt: UInt64 = 0
    public var onFailure: ((Error) -> Void)?
    public var onPresented: ((UInt64) -> Void)?
    public var onFrameMetrics: ((PixelFrameMetrics) -> Void)?
    /// Scales the picture down smoothly when the view is smaller than its source (ViewportProjection.imageForMetal).
    public var smoothDownscaling = false
    public override var isOpaque: Bool { false }

    public init(renderingFrame frame: CGRect) throws {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue(),
              let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else {
            throw CaptureRenderFailure.unavailable
        }
        commands = queue
        self.colorSpace = colorSpace
        imageContext = CIContext(mtlDevice: device, options: [.cacheIntermediates: false])
        super.init(frame: frame, device: device)
        // MTKView must own the draw cycle to retire presented drawables.
        delegate = self
        framebufferOnly = false
        colorPixelFormat = .bgra8Unorm
        isPaused = true
        enableSetNeedsDisplay = true
        autoResizeDrawable = true
        clearColor = MTLClearColorMake(0, 0, 0, 0)
        layer?.isOpaque = false
        (layer as? CAMetalLayer)?.colorspace = colorSpace
    }

    required init(coder: NSCoder) { fatalError("Programmatic view only") }

    public func present(_ buffer: CVPixelBuffer, viewport: Viewport, sourceSize: CGSize,
                        effects: [Element] = [], regionsOnly: Bool = false, generation: UInt64 = 0,
                        includeImageLayers: Bool = true, drawingLayer: CIImage? = nil,
                        privacyRegion: Rect? = nil, screenOverlay: PixelScreenOverlay? = nil,
                        drawImmediately: Bool = false) {
        latestBuffer = buffer; self.viewport = viewport; self.sourceSize = sourceSize
        self.effects = effects; self.regionsOnly = regionsOnly; self.generation = generation
        self.includeImageLayers = includeImageLayers
        self.drawingLayer = drawingLayer; self.privacyRegion = privacyRegion
        self.screenOverlay = screenOverlay
        if onFrameMetrics != nil { requestedAt = DispatchTime.now().uptimeNanoseconds }
        if drawImmediately { draw() } else { needsDisplay = true }
    }

    public func clearFrame() {
        latestBuffer = nil; viewport = nil
        drawingLayer = nil; privacyRegion = nil; effects = []
        screenOverlay = nil
        requestedAt = 0
        imageContext.clearCaches()
        CaptureCompositor.clearImageCache()
    }

    public func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}

    public func draw(in view: MTKView) {
        guard let buffer = latestBuffer, let viewport, sourceSize.width > 0, sourceSize.height > 0,
              let drawable = currentDrawable, let command = commands.makeCommandBuffer() else { return }
        let metricsEnabled = onFrameMetrics != nil && requestedAt > 0
        let encodeStart = metricsEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        let submitted = requestedAt
        let image: CIImage
        do {
            image = try CaptureCompositor.image(source: CIImage(cvPixelBuffer: buffer), logicalSize: sourceSize,
                                                elements: effects, regionsOnly: regionsOnly, includeImageLayers: includeImageLayers,
                                                drawingLayer: drawingLayer, privacyRegion: privacyRegion)
        } catch { onFailure?(error); return }
        let textureSize = CGSize(width: drawable.texture.width, height: drawable.texture.height)
        let target = CGRect(origin: .zero, size: textureSize)
        var transformed: CIImage
        do {
            transformed = try ViewportProjection.imageForMetal(image, sourceSize: sourceSize, viewport: viewport,
                                                               targetSize: textureSize, smoothDownscaling: smoothDownscaling)
            if let screenOverlay {
                transformed = try ViewportProjection.overlayForMetal(screenOverlay, targetSize: textureSize)
                    .composited(over: transformed).cropped(to: target)
            }
        } catch { onFailure?(error); return }
        imageContext.render(transformed, to: drawable.texture, commandBuffer: command,
                            bounds: target, colorSpace: colorSpace)
        command.present(drawable)
        let version = generation
        let textureWidth = drawable.texture.width
        let textureHeight = drawable.texture.height
        let encoded = metricsEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        command.addCompletedHandler { [weak self] completed in
            let succeeded = completed.status == .completed
            let finished = metricsEnabled ? DispatchTime.now().uptimeNanoseconds : 0
            let gpuDuration: Double? = metricsEnabled && completed.gpuEndTime >= completed.gpuStartTime && completed.gpuStartTime > 0
                ? (completed.gpuEndTime - completed.gpuStartTime) * 1_000 : nil
            Task { @MainActor in
                if succeeded {
                    if metricsEnabled {
                        self?.onFrameMetrics?(PixelFrameMetrics(generation: version,
                            encodeMilliseconds: Double(encoded - encodeStart) / 1_000_000,
                            submissionToCompletionMilliseconds: Double(finished - submitted) / 1_000_000,
                            gpuMilliseconds: gpuDuration, textureWidth: textureWidth, textureHeight: textureHeight))
                    }
                    self?.onPresented?(version)
                }
                else { self?.onFailure?(RenderFailure.imageUnavailable) }
            }
        }
        command.commit()
    }
}

public enum CaptureRenderFailure: Error, LocalizedError {
    case unavailable
    public var errorDescription: String? { "A Metal rendering device is unavailable. Live screen views cannot start." }
}
