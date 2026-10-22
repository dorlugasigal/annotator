import AppKit
import MetalKit

@MainActor
public final class ParticleBurstView: MTKView, MTKViewDelegate {
    private struct Uniforms {
        var viewportAndOrigin: SIMD4<Float>
        var sourceAndGrid: SIMD4<Float>
        var progressAndScale: SIMD4<Float>
    }

    @MainActor private final class Resources {
        let device: MTLDevice
        let queue: MTLCommandQueue
        let image: MTLRenderPipelineState
        let particles: MTLRenderPipelineState

        init() throws {
            guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else {
                throw CaptureRenderFailure.unavailable
            }
            self.device = device
            self.queue = queue
            let library = try device.makeLibrary(source: ParticleBurstView.shader, options: nil)
            func pipeline(vertex: String, fragment: String) throws -> MTLRenderPipelineState {
                let descriptor = MTLRenderPipelineDescriptor()
                descriptor.vertexFunction = library.makeFunction(name: vertex)
                descriptor.fragmentFunction = library.makeFunction(name: fragment)
                descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
                descriptor.colorAttachments[0].isBlendingEnabled = true
                descriptor.colorAttachments[0].sourceRGBBlendFactor = .one
                descriptor.colorAttachments[0].destinationRGBBlendFactor = .oneMinusSourceAlpha
                descriptor.colorAttachments[0].sourceAlphaBlendFactor = .one
                descriptor.colorAttachments[0].destinationAlphaBlendFactor = .oneMinusSourceAlpha
                return try device.makeRenderPipelineState(descriptor: descriptor)
            }
            image = try pipeline(vertex: "imageVertex", fragment: "imageFragment")
            particles = try pipeline(vertex: "particleVertex", fragment: "particleFragment")
        }
    }

    private static var cachedResources: Resources?
    public static func prepare() throws {
        if cachedResources == nil { cachedResources = try Resources() }
    }

    public static func grid(size: CGSize, limit: Int) -> SIMD2<UInt32> {
        precondition(size.width > 0 && size.height > 0 && limit > 0)
        let spacing = max(1.5, sqrt(size.width * size.height / Double(limit)))
        let columns = max(1, min(limit, Int(size.width / spacing)))
        let rows = max(1, min(limit / columns, Int(size.height / spacing)))
        return SIMD2(UInt32(columns), UInt32(rows))
    }

    private let resources: Resources
    private let texture: MTLTexture
    private let imageFrame: CGRect
    private let gridSize: SIMD2<UInt32>
    private var started: CFTimeInterval?
    private var duration = 0.6
    private var fromProgress = 0.0
    private var toProgress = 1.0
    private var failed = false
    private var lastEncodedProgress: Double?
    public var onFailure: ((Error) -> Void)?
    public var particleCount: Int { Int(gridSize.x) * Int(gridSize.y) }
    public var currentProgress: Double {
        guard let started else { return fromProgress }
        let time = min(1, max(0, (CACurrentMediaTime() - started) / duration))
        let eased = time * time * (3 - 2 * time)
        return fromProgress + (toProgress - fromProgress) * eased
    }
    public override var isOpaque: Bool { false }

    public init(image: CGImage, imageFrame: CGRect, frame: CGRect, particleLimit: Int) throws {
        try Self.prepare()
        guard let resources = Self.cachedResources else { throw CaptureRenderFailure.unavailable }
        self.resources = resources
        self.imageFrame = imageFrame
        gridSize = Self.grid(size: imageFrame.size, limit: particleLimit)
        texture = try Self.makeTexture(image: image, device: resources.device)
        super.init(frame: frame, device: resources.device)
        delegate = self
        colorPixelFormat = .bgra8Unorm
        isPaused = true
        enableSetNeedsDisplay = false
        autoResizeDrawable = true
        clearColor = MTLClearColorMake(0, 0, 0, 0)
        layer?.isOpaque = false
        (layer as? CAMetalLayer)?.colorspace = CGColorSpace(name: CGColorSpace.sRGB)
    }

    required init(coder: NSCoder) { fatalError("Programmatic view only") }

    private static func makeTexture(image: CGImage, device: MTLDevice) throws -> MTLTexture {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: image.width, height: image.height,
                bitsPerComponent: 8, bytesPerRow: image.width * 4, space: space,
                bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue),
              let bytes = context.data else { throw RenderFailure.imageUnavailable }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm,
            width: image.width, height: image.height, mipmapped: false)
        descriptor.storageMode = device.hasUnifiedMemory ? .shared : .managed
        descriptor.usage = .shaderRead
        guard let texture = device.makeTexture(descriptor: descriptor) else { throw RenderFailure.imageUnavailable }
        // Avoid MTKTextureLoader's synchronous wait on the shared worker pool.
        texture.replace(region: MTLRegionMake2D(0, 0, image.width, image.height), mipmapLevel: 0,
                        withBytes: bytes, bytesPerRow: context.bytesPerRow)
        return texture
    }

    public var endTime: CFTimeInterval? { started.map { $0 + duration } }
    /// Unit vector in screen coordinates (y up) toward where the burst should begin; the facing edge breaks first.
    public var sweepDirection = CGVector(dx: 0, dy: -1)

    public func start(appearing: Bool, duration: Double, at time: CFTimeInterval, progress: Double? = nil) {
        fromProgress = progress ?? (appearing ? 1 : 0)
        toProgress = appearing ? 0 : 1
        self.duration = progress == nil ? duration : duration * max(0.35, abs(toProgress - fromProgress))
        started = time
        lastEncodedProgress = nil
        preferredFramesPerSecond = max(60, window?.screen?.maximumFramesPerSecond ?? 60)
        isPaused = false
    }

    public func stop() {
        isPaused = true
        started = nil
        lastEncodedProgress = nil
    }

    public func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}

    public func draw(in view: MTKView) {
        guard !failed, started != nil, bounds.width > 0, bounds.height > 0 else { return }
        let progress = currentProgress
        guard progress != lastEncodedProgress, let drawable = currentDrawable else { return }
        let command: MTLCommandBuffer
        do { command = try encodeFrame(progress: progress, into: drawable.texture, logicalSize: bounds.size) }
        catch {
            failed = true
            stop()
            onFailure?(error)
            return
        }
        command.present(drawable)
        command.addCompletedHandler { [weak self] result in
            guard result.status != .completed else { return }
            Task { @MainActor in
                guard let self, !self.failed else { return }
                self.failed = true
                self.stop()
                self.onFailure?(RenderFailure.imageUnavailable)
            }
        }
        command.commit()
        lastEncodedProgress = progress
        if progress == toProgress { isPaused = true }
    }

    func encodeFrame(progress: Double, into target: MTLTexture, logicalSize: CGSize) throws -> MTLCommandBuffer {
        guard progress.isFinite, (0...1).contains(progress), logicalSize.width > 0, logicalSize.height > 0 else {
            throw RenderFailure.imageUnavailable
        }
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = target
        pass.colorAttachments[0].clearColor = clearColor
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        guard let command = resources.queue.makeCommandBuffer(),
              let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { throw RenderFailure.imageUnavailable }
        var uniforms = Uniforms(
            viewportAndOrigin: SIMD4(Float(logicalSize.width), Float(logicalSize.height),
                                     Float(imageFrame.minX), Float(logicalSize.height - imageFrame.maxY)),
            sourceAndGrid: SIMD4(Float(imageFrame.width), Float(imageFrame.height), Float(gridSize.x), Float(gridSize.y)),
            progressAndScale: SIMD4(Float(progress), Float(target.width) / Float(logicalSize.width), 140,
                                    Float(atan2(sweepDirection.dy, sweepDirection.dx))))
        encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 0)
        encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 0)
        encoder.setVertexTexture(texture, index: 0)
        encoder.setFragmentTexture(texture, index: 0)
        encoder.setRenderPipelineState(resources.image)
        encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
        encoder.setRenderPipelineState(resources.particles)
        encoder.drawPrimitives(type: .point, vertexStart: 0, vertexCount: particleCount)
        encoder.endEncoding()
        return command
    }

    private static let shader = """
    #include <metal_stdlib>
    using namespace metal;
    struct Uniforms { float4 viewportAndOrigin; float4 sourceAndGrid; float4 progressAndScale; };
    struct ImageVertex { float4 position [[position]]; float2 uv; };
    struct ParticleVertex { float4 position [[position]]; float size [[point_size]]; float4 color; };
    constexpr sampler pixels(coord::normalized, address::clamp_to_edge, filter::linear);
    float randomValue(uint value) {
        value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15;
        value *= 0x846ca68b; value ^= value >> 16;
        return float(value & 0xffffff) / 16777216.0;
    }
    float4 project(float2 point, constant Uniforms &u) {
        return float4(point.x / u.viewportAndOrigin.x * 2 - 1,
                      1 - point.y / u.viewportAndOrigin.y * 2, 0, 1);
    }
    vertex ImageVertex imageVertex(uint id [[vertex_id]], constant Uniforms &u [[buffer(0)]]) {
        const float2 corners[6] = {float2(0,0), float2(1,0), float2(0,1),
                                  float2(0,1), float2(1,0), float2(1,1)};
        ImageVertex out;
        out.uv = corners[id];
        out.position = project(u.viewportAndOrigin.zw + out.uv * u.sourceAndGrid.xy, u);
        return out;
    }
    // 0 on the edge facing the sweep direction, 1 on the far edge. uv.y grows downward.
    float sweepAlong(float2 uv, constant Uniforms &u) {
        float angle = u.progressAndScale.w;
        return saturate(0.5 - dot(uv - 0.5, float2(cos(angle), -sin(angle))));
    }
    float localProgress(float p, float along) { return saturate((p - 0.25 * along) / 0.75); }
    fragment float4 imageFragment(ImageVertex in [[stage_in]], texture2d<float> image [[texture(0)]],
                                   constant Uniforms &u [[buffer(0)]]) {
        float p = localProgress(u.progressAndScale.x, sweepAlong(in.uv, u));
        return image.sample(pixels, in.uv) * (1 - smoothstep(0.0, 0.18, p));
    }
    vertex ParticleVertex particleVertex(uint id [[vertex_id]], constant Uniforms &u [[buffer(0)]],
                                         texture2d<float> image [[texture(0)]]) {
        float2 cell = float2(id % uint(u.sourceAndGrid.z), id / uint(u.sourceAndGrid.z));
        float2 uv = (cell + 0.5) / u.sourceAndGrid.zw;
        float p = localProgress(u.progressAndScale.x, sweepAlong(uv, u));
        float angle = randomValue(id + 1) * 6.2831853;
        float2 direction = float2(cos(angle), sin(angle));
        float speed = 0.25 + 0.75 * randomValue(id + 9209);
        // Drag: a quick burst that slows down, like a puff.
        float drag = (1 - exp(-3.5 * p)) / (1 - exp(-3.5));
        float2 displacement = direction * u.progressAndScale.z * speed * drag;
        displacement += float2(-direction.y, direction.x) * sin(p * 3.1415927) * 10;
        displacement.y += p * p * 12;
        ParticleVertex out;
        out.position = project(u.viewportAndOrigin.zw + uv * u.sourceAndGrid.xy + displacement, u);
        out.size = (1.5 + 2.0 * randomValue(id + 7919)) * (1 - 0.65 * p) * u.progressAndScale.y;
        float opacity = smoothstep(0.0, 0.18, p) * (1 - smoothstep(0.35, 1.0, p));
        out.color = image.sample(pixels, uv) * opacity;
        return out;
    }
    fragment float4 particleFragment(ParticleVertex in [[stage_in]], float2 point [[point_coord]]) {
        float radius = length(point * 2 - 1);
        return in.color * (1 - smoothstep(0.6, 1.0, radius));
    }
    """
}
