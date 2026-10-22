import AppKit
import QuartzCore
import Metal
import XCTest
import AnnotationCore
@testable import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class ToolbarSlideMotionTests: XCTestCase {
    func testGatherWaitsForArrivalBeforeToolbarClosesAndOpensBeforeReturningShapes() {
        let hide = WorkspaceMotionTiming(style: .slide, appearing: false, hasDrawings: true, objectCount: 4)
        let lastArrival = (0..<4).map { hide.drawingDelay(rank: $0) + hide.duration }.max() ?? 0
        XCTAssertGreaterThanOrEqual(hide.toolbarDelay, lastArrival)
        let show = WorkspaceMotionTiming(style: .slide, appearing: true, hasDrawings: true, objectCount: 4)
        XCTAssertGreaterThanOrEqual(show.drawingsDelay, show.toolbarDuration)
        XCTAssertEqual(hide.totalDuration, hide.toolbarDelay + hide.toolbarDuration)
        XCTAssertEqual(show.totalDuration, show.drawingsEndDelay + show.duration)
    }

    func testGatherToolbarShrinksUniformlyAtItsCenterWithoutSquashing() throws {
        let layer = CALayer()
        layer.bounds = CGRect(x: 0, y: 0, width: 600, height: 120)
        let source = CGRect(x: 100, y: 200, width: 600, height: 120)
        let hidden = ToolbarSlideMotion.annotationTransform(source: source,
            target: CGPoint(x: source.midX, y: source.midY))
        for appearing in [false, true] {
            let motion = WorkspaceLayerMotion(layer: layer, style: .slide, appearing: appearing,
                flipped: false, duration: 0.3, hiddenTransform: hidden)
            let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
            let movement = try XCTUnwrap(group.animations?.first as? CAKeyframeAnimation)
            let values = try XCTUnwrap(movement.values as? [NSValue]).map(\.caTransform3DValue)
            XCTAssertEqual(values.count, 25)
            XCTAssertTrue(values.allSatisfy { $0.m11 == $0.m22 })
            XCTAssertTrue(values.allSatisfy { $0.m41 == 0 && $0.m42 == 0 })
            if appearing {
                XCTAssertTrue(CATransform3DIsIdentity(try XCTUnwrap(values.last)))
            } else {
                XCTAssertLessThan(try XCTUnwrap(values.last).m11, 0.1)
            }
            motion.cancel()
        }
        let state = WorkspaceLayerMotion.State(transform: CATransform3DMakeScale(0.4, 0.4, 1), opacity: 1)
        let reverse = WorkspaceLayerMotion(layer: layer, style: .slide, appearing: true,
            flipped: false, duration: 0.3, initial: state, hiddenTransform: hidden)
        let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
        let movement = try XCTUnwrap(group.animations?.first as? CAKeyframeAnimation)
        let values = try XCTUnwrap(movement.values as? [NSValue]).map(\.caTransform3DValue)
        XCTAssertEqual(values.first?.m11, 0.4)
        XCTAssertTrue(values.allSatisfy { $0.m11 >= 0.4 })
        reverse.cancel()
    }

    func testDissolveToolbarSnapshotStaysAtItsRealScreenPosition() async throws {
        let suite = "dissolve-screen-position-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        let screen = try XCTUnwrap(NSScreen.main)
        let visible = screen.visibleFrame
        let rest = CGRect(x: visible.midX - 200, y: visible.maxY - 110, width: 400, height: 100)
        let toolbar = NSPanel(contentRect: rest, styleMask: [.borderless], backing: .buffered, defer: false)
        toolbar.contentView = NSView(frame: CGRect(origin: .zero, size: rest.size))
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        model.setAnimationStyle(.dissolve)
        for frame in [
            rest,
            CGRect(x: visible.minX + 8, y: visible.midY, width: 400, height: 100),
            CGRect(x: visible.maxX - 408, y: visible.midY, width: 400, height: 100),
            CGRect(x: visible.midX - 200, y: visible.minY + 8, width: 400, height: 100)
        ] {
            toolbar.setFrame(frame, display: false)
            toolbar.orderFrontRegardless()
            animator.hide(toolbar: toolbar, model: model) {}
            XCTAssertEqual(try XCTUnwrap(animator.transitionImageScreenFrames.last), frame,
                           "Particle padding must not let AppKit move the toolbar snapshot away from its source.")
            animator.show(toolbar: toolbar, model: model) {}
            XCTAssertEqual(try XCTUnwrap(animator.transitionImageScreenFrames.last), frame)
            try await Task.sleep(for: .seconds(0.8))
            XCTAssertEqual(toolbar.frame, frame)
        }
    }

    func testDirectionsRespectDockEdgesWithoutScalingOrRotation() {
        for edge in [ToolbarDockEdge.top, .bottom, .left, .right] {
            let offset = ToolbarSlideMotion.offset(edge: edge, flipped: false)
            XCTAssertEqual(hypot(offset.x, offset.y), 12)
            switch edge {
            case .top: XCTAssertGreaterThan(offset.y, 0)
            case .bottom: XCTAssertLessThan(offset.y, 0)
            case .left: XCTAssertLessThan(offset.x, 0)
            case .right: XCTAssertGreaterThan(offset.x, 0)
            default: XCTFail("Unexpected edge")
            }
        }
    }

    func testChoreographyShowsToolbarFirstAndHidesItLast() {
        for style in VisibilityAnimationStyle.available {
            let show = WorkspaceMotionTiming(style: style, appearing: true, hasDrawings: true)
            let hide = WorkspaceMotionTiming(style: style, appearing: false, hasDrawings: true)
            if style == .fade || style == .none {
                XCTAssertEqual(show.drawingsDelay, 0)
                XCTAssertEqual(hide.toolbarDelay, 0)
            } else {
                XCTAssertGreaterThan(show.drawingsDelay, show.toolbarDelay)
                XCTAssertGreaterThan(hide.toolbarDelay, hide.drawingsDelay)
                if style == .dissolve { XCTAssertEqual(show.drawingsDelay, hide.toolbarDelay) }
            }
            XCTAssertEqual(show.totalDuration, show.duration + show.drawingsDelay)
            XCTAssertEqual(hide.totalDuration, hide.toolbarDuration + hide.toolbarDelay)
            let empty = WorkspaceMotionTiming(style: style, appearing: true, hasDrawings: false)
            XCTAssertEqual(empty.totalDuration, empty.toolbarDuration)
        }
    }

    func testSlideUsesCompositorWithoutMovingOrResizingTheWindow() async throws {
        let suite = "slide-motion-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        let screen = try XCTUnwrap(NSScreen.main)
        let rest = CGRect(x: screen.frame.midX-300, y: screen.frame.minY+20, width: 600, height: 120)
        let panel = NSPanel(contentRect: rest,
                            styleMask: [.borderless], backing: .buffered, defer: false)
        panel.contentView = NSView(frame: CGRect(x: 0, y: 0, width: 600, height: 120))
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer { animator.cancel(); panel.orderOut(nil) }
        model.setToolbarDockEdge(.bottom)
        model.setAnimationStyle(.slide)
        panel.orderFrontRegardless()
        animator.hide(toolbar: panel, model: model) {}
        try await Task.sleep(for: .milliseconds(90))
        XCTAssertEqual(panel.frame, rest)
        let layer = try XCTUnwrap(panel.contentView?.layer)
        XCTAssertNotNil(layer.animation(forKey: WorkspaceLayerMotion.animationKey))
        animator.show(toolbar: panel, model: model) {}
        try await Task.sleep(for: .seconds(ToolbarSlideMotion.showDuration + 0.2))
        XCTAssertEqual(panel.frame, rest)
        XCTAssertFalse(animator.hasActiveTimeline)
        XCTAssertFalse(panel.ignoresMouseEvents)
    }

    func testSnapshotCropFindsVisibleInkRatherThanTheWholeDisplay() throws {
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: 200, height: 100, bitsPerComponent: 8,
            bytesPerRow: 800, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        XCTAssertNil(WorkspaceVisibilityAnimator.visibleBounds(in: context))
        context.translateBy(x: 0, y: 100)
        context.scaleBy(x: 1, y: -1)
        context.setFillColor(NSColor.black.cgColor)
        context.fill(CGRect(x: 40, y: 20, width: 30, height: 10))
        XCTAssertEqual(WorkspaceVisibilityAnimator.visibleBounds(in: context), CGRect(x: 40, y: 20, width: 30, height: 10))
    }

    func testSnapshotBoundsHandlesPartialBlocksAndIgnoresTransparentRGB() throws {
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        for width in [1, 7, 8, 9, 17, 31] {
            let context = try XCTUnwrap(CGContext(data: nil, width: width, height: 3, bitsPerComponent: 8,
                bytesPerRow: 0, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            let bytes = try XCTUnwrap(context.data).assumingMemoryBound(to: UInt8.self)
            for y in 0..<3 {
                for x in 0..<width { bytes[y * context.bytesPerRow + x * 4] = 255 }
            }
            XCTAssertNil(WorkspaceVisibilityAnimator.visibleBounds(in: context))
            bytes[2 * context.bytesPerRow + (width - 1) * 4 + 3] = 1
            XCTAssertEqual(WorkspaceVisibilityAnimator.visibleBounds(in: context),
                           CGRect(x: width - 1, y: 2, width: 1, height: 1))
            bytes[3] = 255
            XCTAssertEqual(WorkspaceVisibilityAnimator.visibleBounds(in: context),
                           CGRect(x: 0, y: 0, width: width, height: 3))
        }
    }

    func testEveryStyleHasBoundedGeometryAndFullSizeEndpoints() {
        for bounds in [CGRect(x: 0, y: 0, width: 880, height: 220),
                       CGRect(x: 12, y: -8, width: 160, height: 720)] {
            for style in VisibilityAnimationStyle.allCases where style != .none {
                for flipped in [true, false] {
                    for step in 0...100 {
                        let progress = CGFloat(step) / 100
                        let transform = WorkspaceLayerMotion.contentTransform(style: style, bounds: bounds,
                            progress: progress, flipped: flipped, anchor: CGPoint(x: 0.5, y: 0.5))
                        XCTAssertGreaterThanOrEqual(transform.m11, 0.98)
                        XCTAssertGreaterThanOrEqual(transform.m22, 0.98)
                        XCTAssertLessThanOrEqual(abs(transform.m41), 12)
                        XCTAssertLessThanOrEqual(abs(transform.m42), 12)
                        if step == 100 { XCTAssertTrue(CATransform3DIsIdentity(transform)) }
                    }
                }
                for appearing in [true, false] {
                    XCTAssertLessThanOrEqual(WorkspaceLayerMotion.duration(style, appearing: appearing),
                                            style == .magic ? 1 : (style == .dissolve ? 0.7 : 0.4))
                    XCTAssertGreaterThan(WorkspaceLayerMotion.duration(style, appearing: appearing), 0)
                }
            }
        }
    }

    func testRiseSlidesWithoutStretchingAndFoldUsesASubtleHinge() {
        let bounds = CGRect(x: 0, y: 0, width: 880, height: 220)
        for flipped in [false, true] {
            for anchor in [CGPoint.zero, CGPoint(x: 0.5, y: 0.5)] {
                for step in 0...100 {
                    let progress = CGFloat(step) / 100
                    let rise = WorkspaceLayerMotion.contentTransform(style: .rise, bounds: bounds,
                        progress: progress, flipped: flipped, anchor: anchor)
                    XCTAssertEqual(rise.m11, 1)
                    XCTAssertEqual(rise.m22, 1)
                    XCTAssertEqual(rise.m41, 0)
                    XCTAssertLessThanOrEqual(abs(rise.m42), 10)
                    if step == 100 { XCTAssertTrue(CATransform3DIsIdentity(rise)) }
                }
                let fold = WorkspaceLayerMotion.contentTransform(style: .fold, bounds: bounds,
                    progress: 0, flipped: flipped, anchor: anchor)
                XCTAssertNotEqual(fold.m23, 0, "Fold should rotate in depth rather than squash the whole panel.")
                XCTAssertNotEqual(fold.m34, 0)
                XCTAssertLessThanOrEqual(abs(fold.m23), sin(.pi / 36) + 0.001)
            }
        }
    }

    func testLayerTimelinesReverseTheirEndpointsAndRestoreOriginalMask() throws {
        let layer = CALayer()
        layer.bounds = CGRect(x: 0, y: 0, width: 600, height: 140)
        let originalMask = CALayer()
        layer.mask = originalMask
        for style in VisibilityAnimationStyle.allCases where style != .none {
            var showing: [CAPropertyAnimation] = []
            for appearing in [true, false] {
                let motion = WorkspaceLayerMotion(layer: layer, style: style, appearing: appearing,
                    flipped: true, duration: WorkspaceLayerMotion.duration(style, appearing: appearing))
                let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
                let animations = try XCTUnwrap(group.animations as? [CAPropertyAnimation])
                XCTAssertFalse(group.autoreverses)
                XCTAssertEqual(group.repeatCount, 0)
                XCTAssertLessThanOrEqual(group.duration, style == .magic ? 1 : (style == .dissolve ? 0.7 : 0.4))
                XCTAssertFalse(group.isRemovedOnCompletion)
                XCTAssertFalse(animations.isEmpty)
                if appearing { showing = animations }
                else {
                    XCTAssertEqual(animations.count, showing.count)
                    for (show, hide) in zip(showing, animations) {
                        XCTAssertEqual(show.keyPath, hide.keyPath)
                        if let show = show as? CABasicAnimation, let hide = hide as? CABasicAnimation {
                            XCTAssertEqual(show.fromValue as? NSObject, hide.toValue as? NSObject)
                            XCTAssertEqual(show.toValue as? NSObject, hide.fromValue as? NSObject)
                        } else {
                            let show = try XCTUnwrap(show as? CAKeyframeAnimation)
                            let hide = try XCTUnwrap(hide as? CAKeyframeAnimation)
                            XCTAssertEqual(show.values as? [NSValue], Array(try XCTUnwrap(hide.values as? [NSValue]).reversed()))
                            XCTAssertEqual(show.values?.count, 17)
                        }
                    }

                }
                motion.cancel()
                XCTAssertTrue(layer.mask === originalMask)
                XCTAssertNil(layer.animationKeys())
                XCTAssertTrue(CATransform3DIsIdentity(layer.transform))
                XCTAssertEqual(layer.opacity, 1)
            }
        }
    }

    func testReversalStartsAtTheVisibleTransformAndOpacityForEveryStyle() throws {
        for style in VisibilityAnimationStyle.allCases where style != .none {
            let layer = CALayer()
            layer.bounds = CGRect(x: 0, y: 0, width: 600, height: 140)
            let state = WorkspaceLayerMotion.State(transform: CATransform3DMakeTranslation(0, 4, 0), opacity: 0.4)
            let motion = WorkspaceLayerMotion(layer: layer, style: style, appearing: true,
                flipped: false, duration: 0.26, initial: state)
            let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
            let animations = try XCTUnwrap(group.animations as? [CABasicAnimation])
            XCTAssertEqual(animations.first?.fromValue as? NSValue, NSValue(caTransform3D: state.transform))
            XCTAssertEqual(animations.last?.fromValue as? Float, state.opacity)
            XCTAssertEqual(animations.last?.toValue as? Float, 1)
            XCTAssertEqual(layer.mask != nil, style == .reveal || style == .curtain)
            motion.cancel()
        }
    }

    func testAnnotationTransportKeepsAspectRatioAndEndsAtLocalDestination() {
        let source = CGRect(x: -900, y: 200, width: 600, height: 180)
        let screen = CGRect(x: -1200, y: 0, width: 1200, height: 900)
        let toolbar = CGRect(x: -1000, y: 30, width: 600, height: 120)
        let target = ToolbarSlideMotion.annotationTarget(source: source, toolbar: toolbar, screen: screen)
        let transform = ToolbarSlideMotion.annotationTransform(source: source, target: target)
        XCTAssertEqual(transform.m11, transform.m22)
        XCTAssertEqual(source.midX + transform.m41, toolbar.midX)
        XCTAssertEqual(source.midY + transform.m42, toolbar.midY)
        XCTAssertEqual(source.width * transform.m11, 24, accuracy: 0.001)
        XCTAssertEqual(ToolbarSlideMotion.annotationTarget(source: source,
            toolbar: CGRect(x: 100, y: 50, width: 600, height: 120), screen: screen),
            CGPoint(x: -1176, y: 876))
    }

    func testParticleBurstUsesDenseBoundedGPUGridAndStopsDrawing() throws {
        let colorSpace = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: 203, height: 101, bitsPerComponent: 8,
            bytesPerRow: 0, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(NSColor.red.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 203, height: 101))
        let image = try XCTUnwrap(context.makeImage())
        let frame = CGRect(x: 30, y: 40, width: 101.5, height: 50.5)
        for limit in [1, 12_000, 32_000] {
            let view = try ParticleBurstView(image: image, imageFrame: frame,
                                             frame: frame.insetBy(dx: -170, dy: -170), particleLimit: limit)
            XCTAssertLessThanOrEqual(view.particleCount, limit)
            XCTAssertGreaterThan(view.particleCount, 0)
            XCTAssertTrue(view.isPaused)
            view.start(appearing: true, duration: 0.65, at: CACurrentMediaTime() + 1, progress: 0.4)
            XCTAssertEqual(view.currentProgress, 0.4, accuracy: 0.0001)
            view.stop()
            XCTAssertTrue(view.isPaused)
        }
        let grid = ParticleBurstView.grid(size: CGSize(width: 600, height: 200), limit: 32_000)
        XCTAssertGreaterThan(Int(grid.x) * Int(grid.y), 20_000)
        XCTAssertLessThanOrEqual(Int(grid.x) * Int(grid.y), 32_000)
    }

    func testMenuBarSymbolIsSizedAndAccessible() {
        let button = NSButton()
        AppDelegate.configureMenuBarButton(button)
        XCTAssertEqual(button.image?.size, NSSize(width: 18, height: 18))
        XCTAssertEqual(button.image?.isTemplate, true)
        XCTAssertEqual(button.accessibilityLabel(), "Annotator menu")
        XCTAssertEqual(button.toolTip, "Annotator")
    }

    func testParticlePixelsDisperseOutwardAndFinishFullyTransparent() throws {
        let width = 60, height = 40
        var source = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                source[(y * width + x) * 4 + (y < height / 2 ? 0 : 2)] = 255
                source[(y * width + x) * 4 + 3] = 255
            }
        }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(source) as CFData))
        let image = try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 32, bytesPerRow: width * 4, space: try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
        let size = CGSize(width: 300, height: 250)
        let imageFrame = CGRect(x: 100, y: 100, width: width, height: height)
        let view = try ParticleBurstView(image: image, imageFrame: imageFrame,
                                        frame: CGRect(origin: .zero, size: size), particleLimit: 32_000)
        let device = try XCTUnwrap(view.device)
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,
            width: Int(size.width), height: Int(size.height), mipmapped: false)
        descriptor.storageMode = .shared
        descriptor.usage = [.renderTarget]
        let texture = try XCTUnwrap(device.makeTexture(descriptor: descriptor))
        func pixels(_ progress: Double) throws -> [UInt8] {
            let command = try view.encodeFrame(progress: progress, into: texture, logicalSize: size)
            command.commit(); command.waitUntilCompleted()
            XCTAssertEqual(command.status, .completed)
            var bytes = [UInt8](repeating: 0, count: texture.width * texture.height * 4)
            bytes.withUnsafeMutableBytes {
                texture.getBytes($0.baseAddress!, bytesPerRow: texture.width * 4,
                    from: MTLRegionMake2D(0, 0, texture.width, texture.height), mipmapLevel: 0)
            }
            return bytes
        }
        let visible = try pixels(0)
        XCTAssertEqual(visible[(115 * texture.width + 105) * 4 + 2], 255, "Top of the source stays red.")
        XCTAssertEqual(visible[(145 * texture.width + 105) * 4], 255, "Bottom stays blue.")
        let burst = try pixels(0.5)
        var dispersed = 0
        for y in 0..<texture.height {
            for x in 0..<texture.width where !(100..<160).contains(x) || !(110..<150).contains(y) {
                if burst[(y * texture.width + x) * 4 + 3] > 4 { dispersed += 1 }
            }
        }
        XCTAssertGreaterThan(dispersed, 100, "Fine particles must leave the original element, not shrink into it.")
        XCTAssertTrue(try pixels(1).allSatisfy { $0 == 0 })
        XCTAssertEqual(try pixels(0.5), burst, "Reversing must retrace the same seeded particle field.")
    }

    func testAnnotationStylesHaveDifferentGeometryOrMasks() throws {
        let bounds = CGRect(x: 0, y: 0, width: 400, height: 200)
        let rise = WorkspaceLayerMotion.contentTransform(style: .rise, bounds: bounds,
            progress: 0, flipped: false, anchor: CGPoint(x: 0.5, y: 0.5), annotation: true)
        XCTAssertEqual(rise.m42, -40)
        XCTAssertEqual(rise.m11, 1)
        let fold = WorkspaceLayerMotion.contentTransform(style: .fold, bounds: bounds,
            progress: 0, flipped: false, anchor: CGPoint(x: 0.5, y: 0.5), annotation: true)
        XCTAssertGreaterThan(abs(fold.m23), 0.8)
        let fade = WorkspaceLayerMotion.contentTransform(style: .fade, bounds: bounds,
            progress: 0, flipped: false, anchor: .zero, annotation: true)
        XCTAssertTrue(CATransform3DIsIdentity(fade))
        for style in [VisibilityAnimationStyle.reveal, .curtain] {
            let layer = CALayer()
            layer.bounds = bounds
            let motion = WorkspaceLayerMotion(layer: layer, style: style, appearing: true,
                flipped: false, duration: 0.26, annotation: true)
            let mask = try XCTUnwrap(layer.mask as? CAGradientLayer)
            let wipe = try XCTUnwrap(mask.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAKeyframeAnimation)
            XCTAssertEqual((wipe.values?.first as? [NSNumber]), WorkspaceLayerMotion.maskLocations(style, visible: false))
            XCTAssertEqual(mask.colors?.count, style == .reveal ? 4 : 6)
            motion.cancel()
            XCTAssertNil(layer.mask)
        }
    }

    func testObjectSnapshotBoundsIncludeRotatedInkAndArrowHeads() throws {
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let renderer = SceneRenderer()
        for tool in [Tool.rectangle, .ellipse, .arrow, .highlighter, .ruler, .text] {
            var element = Element(tool: tool, frame: Rect(x: 160, y: 160, width: 180, height: 90))
            element.points = [Point(160, 160), Point(340, 250)]
            element.rotation = 0.4
            element.style.width = 12
            element.style.headScale = 3
            element.text = "Snapshot"
            let context = try XCTUnwrap(CGContext(data: nil, width: 600, height: 500,
                bitsPerComponent: 8, bytesPerRow: 0, space: space,
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            context.translateBy(x: 0, y: 500)
            context.scaleBy(x: 1, y: -1)
            let predicted = renderer.drawingBounds(for: element)
            renderer.draw([element], in: context)
            let actual = try XCTUnwrap(WorkspaceVisibilityAnimator.visibleBounds(in: context), "\(tool)")
            XCTAssertTrue(predicted.contains(actual), "\(tool): \(predicted) must contain \(actual)")
        }
    }

    func testPoseStylesEndExactlyAtRestOrHiddenAndStayBounded() throws {
        for style in [VisibilityAnimationStyle.pop, .drop, .focus] {
            for annotation in [true, false] {
                for appearing in [true, false] {
                    let layer = CALayer()
                    layer.bounds = CGRect(x: 0, y: 0, width: 300, height: 120)
                    let motion = WorkspaceLayerMotion(layer: layer, style: style, appearing: appearing, flipped: false,
                        duration: WorkspaceLayerMotion.duration(style, appearing: appearing), annotation: annotation, poseSeed: 7)
                    let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
                    XCTAssertLessThanOrEqual(group.duration, 0.4)
                    let movement = try XCTUnwrap(group.animations?.first as? CAKeyframeAnimation)
                    let opacity = try XCTUnwrap(group.animations?.last as? CAKeyframeAnimation)
                    let transforms = try XCTUnwrap(movement.values as? [NSValue]).map(\.caTransform3DValue)
                    let opacities = try XCTUnwrap(opacity.values as? [NSNumber]).map(\.floatValue)
                    XCTAssertEqual(transforms.count, VisibilityPose.stepCount + 1)
                    if appearing {
                        XCTAssertTrue(CATransform3DIsIdentity(try XCTUnwrap(transforms.last)), "\(style)")
                        XCTAssertEqual(opacities.last, 1)
                        XCTAssertEqual(opacities.first ?? 1, 0, accuracy: 0.001)
                    } else {
                        XCTAssertTrue(CATransform3DIsIdentity(try XCTUnwrap(transforms.first)), "\(style)")
                        XCTAssertEqual(opacities.first, 1)
                        XCTAssertEqual(opacities.last ?? 1, 0, accuracy: 0.001)
                    }
                    for transform in transforms {
                        XCTAssertLessThanOrEqual(hypot(transform.m11, transform.m12), 1.07)
                        XCTAssertLessThanOrEqual(abs(transform.m42), 111)
                    }
                    motion.cancel()
                    XCTAssertNil(layer.animationKeys())
                }
            }
        }
    }

    func testDropLandsWithOneSmallBounceAndFallsWithGravity() throws {
        let layer = CALayer()
        layer.bounds = CGRect(x: 0, y: 0, width: 200, height: 100)
        let show = WorkspaceLayerMotion(layer: layer, style: .drop, appearing: true, flipped: false,
                                        duration: 0.4, annotation: true, poseSeed: 3)
        var values = try XCTUnwrap((layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)?
            .animations?.first as? CAKeyframeAnimation).values as? [NSValue]
        let lifts = try XCTUnwrap(values).map { $0.caTransform3DValue.m42 }
        XCTAssertGreaterThan(lifts.first ?? 0, 40, "Drop arrives from above.")
        XCTAssertLessThan(lifts.min() ?? 0, -2, "It dips slightly below its place once.")
        XCTAssertGreaterThan(lifts.min() ?? 0, -10)
        show.cancel()
        let hide = WorkspaceLayerMotion(layer: layer, style: .drop, appearing: false, flipped: false,
                                        duration: 0.32, annotation: true, poseSeed: 3)
        values = try XCTUnwrap((layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)?
            .animations?.first as? CAKeyframeAnimation).values as? [NSValue]
        let falls = try XCTUnwrap(values).map { -$0.caTransform3DValue.m42 }
        let half = falls[VisibilityPose.stepCount / 2]
        XCTAssertLessThan(half, (falls.last ?? 0) * 0.3, "Falling accelerates instead of moving evenly.")
        hide.cancel()
    }

    func testSoftFocusAnimatesABlurOnTheSnapshotContainerAndCleansIt() throws {
        let space = try XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try XCTUnwrap(CGContext(data: nil, width: 40, height: 20, bitsPerComponent: 8, bytesPerRow: 0,
            space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let image = try XCTUnwrap(context.makeImage())
        let view = VisibilityImageView(image: image, frame: CGRect(x: 0, y: 0, width: 200, height: 160),
                                       imageFrame: CGRect(x: 80, y: 70, width: 40, height: 20))
        view.enableBlur()
        XCTAssertTrue(view.layerUsesCoreImageFilters)
        XCTAssertEqual(view.imageLayer.frame, CGRect(x: 80, y: 70, width: 40, height: 20))
        let motion = WorkspaceLayerMotion(layer: view.imageLayer, style: .focus, appearing: false, flipped: false,
                                          duration: 0.28, annotation: true, poseSeed: 1)
        let blur = try XCTUnwrap(view.motionContainer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAKeyframeAnimation)
        XCTAssertEqual(blur.keyPath, "filters.\(VisibilityImageView.blurFilterName).inputRadius")
        XCTAssertEqual((blur.values?.first as? NSNumber)?.doubleValue, 0)
        XCTAssertEqual((blur.values?.last as? NSNumber)?.doubleValue ?? 0, 12, accuracy: 0.001)
        motion.cancel()
        XCTAssertNil(view.motionContainer.animationKeys())
    }

    func testReversalsFinishSoonerWhenLittleDistanceIsLeft() {
        let layer = CALayer()
        layer.bounds = CGRect(x: 0, y: 0, width: 200, height: 100)
        var dock = CATransform3DMakeScale(0.1, 0.1, 1)
        dock.m41 = 400
        var nearlyHome = CATransform3DMakeScale(0.95, 0.95, 1)
        nearlyHome.m41 = 20
        let near = WorkspaceLayerMotion(layer: layer, style: .slide, appearing: true, flipped: false, duration: 0.36,
            initial: .init(transform: nearlyHome, opacity: 1), hiddenTransform: dock)
        XCTAssertEqual(near.effectiveDuration, 0.36 * WorkspaceLayerMotion.minimumReversalShare, accuracy: 0.001)
        near.cancel()
        var halfway = CATransform3DMakeScale(0.5, 0.5, 1)
        halfway.m41 = 200
        let middle = WorkspaceLayerMotion(layer: layer, style: .slide, appearing: true, flipped: false, duration: 0.36,
            initial: .init(transform: halfway, opacity: 1), hiddenTransform: dock)
        XCTAssertEqual(middle.effectiveDuration, 0.36 * 0.5 / 0.9, accuracy: 0.001)
        middle.cancel()
        let pose = WorkspaceLayerMotion(layer: layer, style: .pop, appearing: true, flipped: false, duration: 0.34,
            initial: .init(transform: CATransform3DIdentity, opacity: 1, progress: 0.2), poseSeed: 1)
        XCTAssertEqual(pose.effectiveDuration, 0.34 * WorkspaceLayerMotion.minimumReversalShare, accuracy: 0.001)
        pose.cancel()
        let fresh = WorkspaceLayerMotion(layer: layer, style: .slide, appearing: true, flipped: false, duration: 0.36,
                                         hiddenTransform: dock)
        XCTAssertEqual(fresh.effectiveDuration, 0.36)
        fresh.cancel()
    }

    func testGatherKeepsSizeEarlyInAHideAndGrowsEarlyInAShow() throws {
        let layer = CALayer()
        layer.bounds = CGRect(x: 0, y: 0, width: 200, height: 100)
        var dock = CATransform3DMakeScale(0.1, 0.1, 1)
        dock.m41 = 400
        for appearing in [false, true] {
            let motion = WorkspaceLayerMotion(layer: layer, style: .slide, appearing: appearing, flipped: false,
                                              duration: 0.3, hiddenTransform: dock)
            let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
            let path = try XCTUnwrap(group.animations?.first as? CAKeyframeAnimation)
            let scales = try XCTUnwrap(path.values as? [NSValue]).map { $0.caTransform3DValue.m11 }
            if appearing {
                XCTAssertEqual(scales[20], 1, accuracy: 0.0001, "Shapes reach full size before they arrive.")
                XCTAssertTrue(curveMatches(path.timingFunction, Motion.enter))
            } else {
                XCTAssertEqual(scales[4], 1, accuracy: 0.0001, "Shapes keep their size as they start to travel.")
                XCTAssertTrue(curveMatches(path.timingFunction, Motion.exit))
            }
            XCTAssertTrue(zip(scales, scales.dropFirst()).allSatisfy { appearing ? $0 <= $1 + 0.0001 : $0 + 0.0001 >= $1 })
            XCTAssertTrue(curveMatches(group.timingFunction, CAMediaTimingFunction(name: .linear)))
            motion.cancel()
        }
    }

    func testDistanceOrderStartsNearestAndPulsesGroupCloseArrivals() {
        let target = CGPoint(x: 0, y: 0)
        let order = ToolbarSlideMotion.distanceOrder([
            ("far", CGPoint(x: 500, y: 0), target), ("near", CGPoint(x: 50, y: 0), target),
            ("middle", CGPoint(x: 0, y: 200), target)
        ])
        XCTAssertEqual(order, ["near", "middle", "far"])
        let pulses = ToolbarSlideMotion.pulseTimes([1.0, 1.02, 1.2, 1.3, 1.4, 1.5, 1.6])
        XCTAssertEqual(pulses, [1.0, 1.2, 1.3, 1.4])
        XCTAssertEqual(ToolbarSlideMotion.pulse(at: 1.065, arrivals: [1.0], width: 0.13), 1, accuracy: 0.001)
        XCTAssertEqual(ToolbarSlideMotion.pulse(at: 0.99, arrivals: [1.0], width: 0.13), 0)
        XCTAssertLessThanOrEqual(ToolbarSlideMotion.pulse(at: 1.08, arrivals: [1.0, 1.03], width: 0.13), 1)
    }

    func testGatherToolbarPulsesAsShapesLand() async throws {
        let suite = "gather-absorb-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        for index in 0..<3 {
            try session.document.add(Element(tool: .rectangle,
                frame: Rect(x: 100 + Double(index) * 160, y: 200, width: 90, height: 60)))
        }
        let toolbar = NSPanel(contentRect: CGRect(x: 120, y: 80, width: 600, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        toolbar.contentView = NSView(frame: CGRect(x: 0, y: 0, width: 600, height: 120))
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        model.setAnimationStyle(.slide)
        toolbar.orderFrontRegardless()
        animator.hide(toolbar: toolbar, model: model) {}
        let delays = animator.transitionObjectDelays
        XCTAssertEqual(Set(delays.values).count, 3, "Each shape leaves at its own time.")
        let pulse = NSApp.windows.compactMap { ($0.contentView as? VisibilityImageView)?.motionContainer }
            .compactMap { $0.animation(forKey: VisibilityImageView.pulseKey) as? CAKeyframeAnimation }
        XCTAssertEqual(pulse.count, 1, "Only the toolbar snapshot pulses.")
        let peak = (pulse.first?.values as? [NSNumber])?.map(\.doubleValue).max() ?? 0
        XCTAssertEqual(peak, 1.035, accuracy: 0.002)
    }

    private func curveMatches(_ lhs: CAMediaTimingFunction?, _ rhs: CAMediaTimingFunction) -> Bool {
        guard let lhs else { return false }
        return [0.1, 0.3, 0.5, 0.7, 0.9].allSatisfy { abs(Motion.progress(lhs, at: $0) - Motion.progress(rhs, at: $0)) < 0.0001 }
    }
}
