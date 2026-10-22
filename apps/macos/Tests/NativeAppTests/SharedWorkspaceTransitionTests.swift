import AppKit
import CoreVideo
import QuartzCore
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class SharedWorkspaceTransitionTests: XCTestCase {
    private func image(width: Int = 100, height: Int = 60) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: height,
            bitsPerComponent: 8, bytesPerRow: width * 4,
            space: try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(NSColor.red.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return try XCTUnwrap(context.makeImage())
    }

    private func buffer() throws -> CVPixelBuffer {
        var result: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(kCFAllocatorDefault, 64, 64, kCVPixelFormatType_32BGRA,
            [kCVPixelBufferMetalCompatibilityKey: true, kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary,
            &result), kCVReturnSuccess)
        let buffer = try XCTUnwrap(result)
        CVPixelBufferLockBaseAddress(buffer, [])
        let base = try XCTUnwrap(CVPixelBufferGetBaseAddress(buffer))
        memset(base, 0, CVPixelBufferGetBytesPerRow(buffer) * 64)
        CVPixelBufferUnlockBaseAddress(buffer, [])
        return buffer
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<400 {
            if condition() { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("The shared transition did not present its expected frame.")
    }

    func testSnapshotClipsOriginalRegionAndUnionOfOverlappingProtection() throws {
        let source = try image()
        let frame = CGRect(x: -300, y: 40, width: 100, height: 60)
        let region = CGRect(x: -260, y: 40, width: 60, height: 60)
        let protection = [
            CGRect(x: -245, y: 40, width: 20, height: 60),
            CGRect(x: -235, y: 40, width: 20, height: 60)
        ]
        let clipped = try XCTUnwrap(SharedWorkspaceTransition.clippedImage(
            source, screenFrame: frame, region: region, protection: protection))
        let bitmap = NSBitmapImageRep(cgImage: clipped)
        for x in [0, 30, 60, 70, 80] {
            XCTAssertEqual(try XCTUnwrap(bitmap.colorAt(x: x, y: 30)).alphaComponent, 0, accuracy: 0.01)
        }
        for x in [45, 90] {
            XCTAssertEqual(try XCTUnwrap(bitmap.colorAt(x: x, y: 30)).alphaComponent, 1, accuracy: 0.01)
        }
        XCTAssertNil(try SharedWorkspaceTransition.clippedImage(source, screenFrame: frame,
            region: CGRect(x: 100, y: 100, width: 50, height: 50), protection: []))
        XCTAssertNil(try SharedWorkspaceTransition.clippedImage(source, screenFrame: frame,
            region: frame, protection: [frame]), "A fully protected object must not produce a motion trail.")
    }

    func testOutputMaskKeepsMovingSpritesBehindProtection() throws {
        let transition = SharedWorkspaceTransition(screenRegion: CGRect(x: -300, y: 40, width: 100, height: 60))
        defer { transition.cancel() }
        transition.updateProtection([
            CGRect(x: -280, y: 40, width: 30, height: 60),
            CGRect(x: -260, y: 40, width: 30, height: 60)
        ])
        let mask = try XCTUnwrap(transition.layer?.mask as? CAShapeLayer)
        let path = try XCTUnwrap(mask.path)
        XCTAssertTrue(path.contains(CGPoint(x: 10, y: 30)))
        XCTAssertFalse(path.contains(CGPoint(x: 25, y: 30)))
        XCTAssertFalse(path.contains(CGPoint(x: 45, y: 30)), "Overlapping protection must not reopen a hole.")
        XCTAssertFalse(path.contains(CGPoint(x: 60, y: 30)))
        XCTAssertTrue(path.contains(CGPoint(x: 90, y: 30)))
    }

    func testAllMotionStylesPrepareAndReleaseTheirResources() async throws {
        let bitmap = try image()
        for style: VisibilityAnimationStyle in [.quick, .fade, .slide, .fluid, .reveal, .curtain, .fold, .rise, .dissolve, .magic, .corners] {
            let transition = SharedWorkspaceTransition(screenRegion: CGRect(x: -400, y: 20, width: 400, height: 300))
            let frame = CGRect(x: -350, y: 70, width: 100, height: 60)
            let hidden = ToolbarSlideMotion.annotationTransform(source: frame, target: CGPoint(x: -390, y: 290))
            try transition.add(image: bitmap, frame: frame.insetBy(dx: -170, dy: -170),
                imageFrame: frame, style: style, appearing: false,
                duration: WorkspaceLayerMotion.duration(style, appearing: false),
                started: CACurrentMediaTime(), initial: nil, hiddenTransform: hidden,
                annotation: true, magicSeed: style == .magic ? 42 : nil)
            transition.prepare()
            XCTAssertTrue(transition.isPrepared)
            XCTAssertEqual(transition.animatedImageCount, 1)
            XCTAssertEqual(transition.hasFramePump, style == .dissolve)
            try await Task.sleep(for: .milliseconds(10))
            transition.finish()
            XCTAssertTrue(transition.isFinishing)
            transition.cancel()
            XCTAssertEqual(transition.animatedImageCount, 0)
            XCTAssertFalse(transition.hasFramePump)
        }
    }

    func testLiveFrameHandoffRejectsEarlierGPUCompletion() async throws {
        let region = try SharingRegion(preset: .leftHalf, displayBounds: Rect(x: 0, y: 0, width: 64, height: 64))
        let output = try SharedRegionWindow(region: region, displayFrame: try XCTUnwrap(NSScreen.main).visibleFrame)
        defer { output.close() }
        output.show(inBackground: true)
        var presented: UInt64?
        var failure: Error?
        output.content.onPresented = { presented = $0 }
        output.content.onFailure = { failure = $0 }
        let pixels = try buffer()
        try output.content.present(pixels, sourceBounds: region.bounds, sourceSize: CGSize(width: 64, height: 64),
            elements: [], background: nil, generation: 1)
        try await waitUntil { presented == 1 || failure != nil }
        if let failure { throw failure }
        let transition = SharedWorkspaceTransition(screenRegion: CGRect(x: -200, y: 30, width: 32, height: 64))
        output.content.installWorkspaceTransition(transition)
        transition.prepare()
        XCTAssertEqual(transition.frame, output.content.contentFrame)
        XCTAssertEqual(transition.bounds.minX, transition.screenRegion.minX, accuracy: 0.00000001)
        XCTAssertEqual(transition.bounds.minY, transition.screenRegion.minY, accuracy: 0.00000001)
        XCTAssertEqual(transition.bounds.width, transition.screenRegion.width, accuracy: 0.00000001)
        XCTAssertEqual(transition.bounds.height, transition.screenRegion.height, accuracy: 0.00000001)
        try output.content.present(pixels, sourceBounds: region.bounds, sourceSize: CGSize(width: 64, height: 64),
            elements: [], background: nil, generation: 2)
        transition.finish()
        output.content.holdWorkspaceTransition(generation: 3)
        try await Task.sleep(for: .milliseconds(80))
        XCTAssertNotEqual(presented, 2)
        XCTAssertTrue(output.content.workspaceTransition === transition)
        XCTAssertFalse(output.content.isCovered)
        try output.content.present(pixels, sourceBounds: region.bounds, sourceSize: CGSize(width: 64, height: 64),
            elements: [], background: nil, generation: 3, completingWorkspaceTransition: true)
        try await waitUntil { presented == 3 || failure != nil }
        if let failure { throw failure }
        XCTAssertNil(output.content.workspaceTransition)
        XCTAssertNil(transition.superview)
    }

    func testOpaqueStopCancelsAnimationAndParticlePumpImmediately() throws {
        let region = try SharingRegion(preset: .leftHalf, displayBounds: Rect(x: 0, y: 0, width: 200, height: 100))
        let output = try SharedRegionView(region: region)
        let transition = SharedWorkspaceTransition(screenRegion: region.bounds.cgRect)
        output.installWorkspaceTransition(transition)
        try transition.add(image: image(width: 50, height: 50), frame: CGRect(x: 0, y: 0, width: 100, height: 100),
            imageFrame: CGRect(x: 0, y: 0, width: 50, height: 50), style: .dissolve, appearing: false,
            duration: 1, started: CACurrentMediaTime(), initial: nil, hiddenTransform: nil, annotation: true)
        transition.prepare()
        XCTAssertTrue(transition.hasFramePump)
        output.cover("Applying protection", generation: 8, preservingWorkspaceTransition: true)
        XCTAssertTrue(output.isCovered)
        XCTAssertTrue(output.workspaceTransition === transition)
        output.cover("Region stopped", generation: 9)
        XCTAssertTrue(output.isCovered)
        XCTAssertNil(output.workspaceTransition)
        XCTAssertFalse(transition.hasFramePump)
        XCTAssertEqual(transition.animatedImageCount, 0)
    }
}
