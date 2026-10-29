import CoreImage
import CoreVideo
import Metal
import Testing
import AnnotationCore
import PresentationRendering

@Test @MainActor func metalProjectionUsesDrawableCoordinatesAndMagnifiesTheRequestedRegion() throws {
    guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else {
        Issue.record("A Metal device is required to validate display projection.")
        return
    }
    var buffer: CVPixelBuffer?
    #expect(CVPixelBufferCreate(kCFAllocatorDefault, 8, 8, kCVPixelFormatType_32BGRA,
                               [kCVPixelBufferMetalCompatibilityKey: true] as CFDictionary, &buffer) == kCVReturnSuccess)
    let pixels = try #require(buffer)
    CVPixelBufferLockBaseAddress(pixels, [])
    let base = try #require(CVPixelBufferGetBaseAddress(pixels)).assumingMemoryBound(to: UInt8.self)
    let stride = CVPixelBufferGetBytesPerRow(pixels)
    for y in 0..<8 {
        for x in 0..<8 {
            let color: [UInt8] = y < 4 ? (x < 4 ? [0, 0, 255, 255] : [0, 255, 0, 255])
                : (x < 4 ? [255, 0, 0, 255] : [255, 255, 255, 255])
            for channel in 0..<4 { base[y * stride + x * 4 + channel] = color[channel] }
        }
    }

    CVPixelBufferUnlockBaseAddress(pixels, [])
    let image = CIImage(cvPixelBuffer: pixels)
    let context = CIContext(mtlDevice: device)
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let full = Rect(x: 0, y: 0, width: 8, height: 8)
    for crop in [full, Rect(x: 0, y: 0, width: 4, height: 4)] {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: 8, height: 8, mipmapped: false)
        descriptor.storageMode = .shared
        descriptor.usage = [.shaderRead, .shaderWrite, .renderTarget]
        let texture = try #require(device.makeTexture(descriptor: descriptor))
        let command = try #require(queue.makeCommandBuffer())
        let projected = try ViewportProjection.imageForMetal(image, sourceSize: CGSize(width: 8, height: 8),
                                                             viewport: Viewport(output: full, source: crop),
                                                             targetSize: CGSize(width: 8, height: 8))
        context.render(projected, to: texture, commandBuffer: command, bounds: CGRect(x: 0, y: 0, width: 8, height: 8), colorSpace: space)
        command.commit(); command.waitUntilCompleted()
        #expect(command.status == .completed)
        var result = [UInt8](repeating: 0, count: 8 * 8 * 4)
        result.withUnsafeMutableBytes { raw in
            if let address = raw.baseAddress {
                texture.getBytes(address, bytesPerRow: 32, from: MTLRegionMake2D(0, 0, 8, 8), mipmapLevel: 0)
            }
        }
        if crop == full {
            // Core Animation presents this drawable with its vertical orientation.
            #expect(Array(result.prefix(4)) == [255, 0, 0, 255])
            #expect(Array(result.suffix(4)) == [0, 255, 0, 255])
        } else {
            #expect(Array(result.prefix(4)) == [0, 0, 255, 255])
            #expect(Array(result.suffix(4)) == [0, 0, 255, 255])
        }
    }
}

private func projectedPixel(_ image: CIImage, x: Int, y: Int) throws -> [UInt8] {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    var result = [UInt8](repeating: 0, count: 4)
    try result.withUnsafeMutableBytes { bytes in
        let address = try #require(bytes.baseAddress)
        CIContext(options: [.cacheIntermediates: false]).render(image, toBitmap: address, rowBytes: 4,
            bounds: CGRect(x: x, y: y, width: 1, height: 1), format: .RGBA8, colorSpace: space)
    }
    return result
}

@Test @MainActor func toolbarOverlayUsesDisplayCoordinatesAndRemainsUprightAtEveryScale() throws {
    let bottom = CIImage(color: CIColor(red: 1, green: 0, blue: 0))
        .cropped(to: CGRect(x: 13, y: 17, width: 40, height: 10))
    let image = CIImage(color: CIColor(red: 0, green: 0, blue: 1))
        .cropped(to: CGRect(x: 13, y: 27, width: 40, height: 10)).composited(over: bottom)
    let overlay = PixelScreenOverlay(image: image, bounds: Rect(x: 75, y: 50, width: 100, height: 60),
                                     region: Rect(x: 50, y: 20, width: 200, height: 100))
    for scale in [1, 2, 3] {
        let size = CGSize(width: 200 * scale, height: 100 * scale)
        let projected = try ViewportProjection.overlayForMetal(overlay, targetSize: size)
        let output = projected.composited(over: CIImage(color: CIColor(red: 0, green: 1, blue: 0))
            .cropped(to: CGRect(origin: .zero, size: size)))
        #expect(try projectedPixel(output, x: 50 * scale, y: 15 * scale) == [255, 0, 0, 255])
        #expect(try projectedPixel(output, x: 50 * scale, y: 65 * scale) == [0, 0, 255, 255])
        #expect(try projectedPixel(output, x: 10 * scale, y: 50 * scale) == [0, 255, 0, 255])
        #expect(try projectedPixel(output, x: 50 * scale, y: 85 * scale) == [0, 255, 0, 255])
    }
}

@Test @MainActor func toolbarOverlayClipsToTheSharedRegionAndPreservesItsOpacity() throws {
    let image = CIImage(color: CIColor(red: 0, green: 0, blue: 1))
        .cropped(to: CGRect(x: 0, y: 0, width: 80, height: 80))
    let overlay = PixelScreenOverlay(image: image, bounds: Rect(x: 20, y: 20, width: 80, height: 80),
                                     region: Rect(x: 50, y: 50, width: 100, height: 100), opacity: 0.25)
    let projected = try ViewportProjection.overlayForMetal(overlay, targetSize: CGSize(width: 200, height: 200))
    #expect(projected.extent.minX >= 0 && projected.extent.maxX <= 200)
    #expect(projected.extent.minY >= 0 && projected.extent.maxY <= 200)
    let inside = try projectedPixel(projected, x: 50, y: 150)
    #expect((63...65).contains(inside[3]))
    #expect(try projectedPixel(projected, x: 150, y: 150) == [0, 0, 0, 0])
    #expect(try projectedPixel(projected, x: 50, y: 50) == [0, 0, 0, 0])
}

@Test @MainActor func toolbarOverlayRejectsInvalidGeometryOpacityAndUnboundedImages() throws {
    let image = CIImage(color: CIColor(red: 1, green: 0, blue: 0))
        .cropped(to: CGRect(x: 0, y: 0, width: 32, height: 32))
    let valid = Rect(x: 0, y: 0, width: 100, height: 100)
    for overlay in [
        PixelScreenOverlay(image: image, bounds: .zero, region: valid),
        PixelScreenOverlay(image: image, bounds: valid, region: .zero),
        PixelScreenOverlay(image: image, bounds: Rect(x: .nan, y: 0, width: 20, height: 20), region: valid),
        PixelScreenOverlay(image: image, bounds: valid, region: valid, opacity: .nan),
        PixelScreenOverlay(image: image, bounds: valid, region: valid, opacity: 1.1),
        PixelScreenOverlay(image: CIImage(color: .white), bounds: valid, region: valid)
    ] {
        #expect(throws: RenderFailure.invalidSize) {
            try ViewportProjection.overlayForMetal(overlay, targetSize: CGSize(width: 100, height: 100))
        }
    }
}

private func projectedPixels(_ image: CIImage, size: CGSize) throws -> [[UInt8]] {
    let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
    let width = Int(size.width), height = Int(size.height)
    var bytes = [UInt8](repeating: 0, count: width * height * 4)
    try bytes.withUnsafeMutableBytes { raw in
        let address = try #require(raw.baseAddress)
        CIContext(options: [.cacheIntermediates: false]).render(image, toBitmap: address, rowBytes: width * 4,
            bounds: CGRect(origin: .zero, size: size), format: .RGBA8, colorSpace: space)
    }
    return stride(from: 0, to: bytes.count, by: 4).map { Array(bytes[$0..<($0 + 4)]) }
}

@Test @MainActor func sharedViewScalesDownSmoothlyWithoutDroppingPixelsOrReadingOutsideTheRegion() throws {
    // One-pixel black and white columns, with red outside the shared part.
    let width = 80, height = 24
    var bytes: [UInt8] = []
    for y in 0..<height {
        for x in 0..<width {
            let inside = (8..<72).contains(x) && (4..<20).contains(y)
            bytes += inside ? (x % 2 == 0 ? [0, 0, 0, 255] : [255, 255, 255, 255]) : [255, 0, 0, 255]
        }
    }
    let image = CIImage(bitmapData: Data(bytes), bytesPerRow: width * 4, size: CGSize(width: width, height: height),
                        format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
    let source = Rect(x: 8, y: 4, width: 64, height: 16)
    func project(_ size: CGSize, smooth: Bool) throws -> [[UInt8]] {
        try projectedPixels(ViewportProjection.imageForMetal(image, sourceSize: CGSize(width: width, height: height),
            viewport: Viewport(output: Rect(x: 0, y: 0, width: size.width, height: size.height), source: source),
            targetSize: size, smoothDownscaling: smooth), size: size)
    }
    let half = CGSize(width: 32, height: 8)
    #expect(try project(half, smooth: false).allSatisfy { $0[0] == $0[1] && ($0[0] == 0 || $0[0] == 255) },
            "Nearest-neighbor keeps one column in two: the fixture shows the dropped pixels.")
    for pixel in try project(half, smooth: true) {
        #expect((96...160).contains(Int(pixel[0])), "Both columns must count: \(pixel)")
        #expect(abs(Int(pixel[0]) - Int(pixel[1])) <= 2, "No red from outside the region: \(pixel)")
        #expect(pixel[3] == 255)
    }
    for size in [CGSize(width: 64, height: 16), CGSize(width: 128, height: 32)] {
        #expect(try project(size, smooth: true).allSatisfy { $0[0] == $0[1] && ($0[0] == 0 || $0[0] == 255) },
                "At its own size and larger, the picture keeps its exact pixels.")
    }
}
