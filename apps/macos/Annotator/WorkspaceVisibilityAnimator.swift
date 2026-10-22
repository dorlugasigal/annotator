import AppKit
import QuartzCore
import OSLog
import AnnotationCore
import PresentationRendering

@MainActor
final class WorkspaceVisibilityAnimator {
    private struct Snapshot {
        let id: String
        let elementID: UUID?
        let image: CGImage
        let sharedImage: CGImage?
        let frame: CGRect
        let displayID: UInt32
        let visibleFrame: CGRect
        let center: CGPoint
    }
    private struct DocumentVersion: Equatable {
        let id: UUID
        let generation: UInt64
        let frame: CGRect
        let board: WorkspaceModel.Board
    }
    private struct SceneVersion: Equatable {
        let documents: [DocumentVersion]
        let displays: [CGRect]
        let scales: [CGFloat]
        let sharedRegion: CGRect?
    }

    private var panels: [NSPanel] = []
    private var token = UUID()
    private weak var animatedToolbar: NSPanel?
    private weak var animatedModel: WorkspaceModel?
    private var layerMotions: [WorkspaceLayerMotion] = []
    private var burstViews: [ParticleBurstView] = []
    private var burstKeys: [String] = []
    private var toolbarImage: CGImage?
    private var sharedToolbarImage: CGImage?
    private weak var sharedController: RegionSharingController?
    private weak var sharedTransition: SharedWorkspaceTransition?
    private var toolbarImageMotion: WorkspaceLayerMotion?
    private var magicTrails: [MagicTrail] = []
    private var toolbarPoof: MagicToolbarEffect?
    private var cornerRoutes = CornerRoutes()
    private var sceneVersion: SceneVersion?
    private var completionDeadline: Task<Void, Never>?
    private var transitionImages: [Snapshot] = []
    private var objectOrder: [String: Int] = [:]
    private let shuffled: (([String]) -> [String])?
    private(set) var transitionObjectDelays: [UUID: Double] = [:]
    private(set) var transitionCornerTargets: [UUID: CGPoint] = [:]
    private var toolbarInputBeforeAnimation = false
    private var revealDrawingRequested = false
    private(set) var targetVisible = true
    var transientPanelCount: Int { panels.count }
    var transitionImageScreenFrames: [CGRect] {
        panels.compactMap { panel in
            guard let view = panel.contentView as? VisibilityImageView else { return nil }
            return panel.convertToScreen(view.convert(view.imageLayer.frame, to: nil))
        }
    }
    var particleCount: Int { burstViews.reduce(0) { $0 + $1.particleCount } }
    var magicEffectLayerCount: Int { magicTrails.reduce(0) { $0 + $1.layerCount } + (toolbarPoof?.layerCount ?? 0) }
    var hasActiveTimeline: Bool { completionDeadline != nil }
    private let logger = Logger(subsystem: "com.dorlugasigal.annotator", category: "visibility")
    private let reduceMotion: @MainActor () -> Bool

    init(reduceMotion: @escaping @MainActor () -> Bool = { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion },
         shuffled: (([String]) -> [String])? = nil) {
        self.reduceMotion = reduceMotion
        self.shuffled = shuffled
    }

    func cancel(finishingSharedAnimation: Bool = false) {
        if let sharedTransition {
            if finishingSharedAnimation { sharedController?.finishWorkspaceAnimation(sharedTransition.id) }
            else { sharedController?.cancelWorkspaceAnimation(sharedTransition.id) }
        }
        sharedTransition = nil
        sharedController = nil
        sharedToolbarImage = nil
        revealDrawingRequested = false
        token = UUID()
        completionDeadline?.cancel()
        completionDeadline = nil
        if let toolbar = animatedToolbar {
            toolbar.ignoresMouseEvents = toolbarInputBeforeAnimation
        }
        // Order snapshots out before removing their held end-state animations.
        // Otherwise a hidden snapshot can briefly return at full size.
        for panel in panels { panel.orderOut(nil) }
        layerMotions.forEach { $0.cancel() }
        layerMotions.removeAll()
        toolbarImageMotion?.cancel()
        toolbarImageMotion = nil
        toolbarPoof?.cancel()
        toolbarPoof = nil
        magicTrails.forEach { $0.cancel() }
        magicTrails.removeAll()
        burstViews.forEach { $0.stop(); $0.removeFromSuperview() }
        burstViews.removeAll()
        burstKeys.removeAll()
        toolbarImage = nil
        animatedToolbar?.alphaValue = 1
        animatedToolbar = nil
        animatedModel?.visibilityAnimating = false
        animatedModel = nil
        for panel in panels { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        panels.removeAll()
        transitionImages.removeAll()
        transitionObjectDelays.removeAll()
        transitionCornerTargets.removeAll()
    }

    func cancelDrawingRequest() {
        revealDrawingRequested = false
    }

    func hide(toolbar: NSPanel, model: WorkspaceModel, completion: @escaping @MainActor @Sendable () -> Void) {
        model.finishZoomGesture()
        model.interact()
        transition(toolbar: toolbar, model: model, showing: false, completion: completion)
    }

    func show(toolbar: NSPanel, model: WorkspaceModel, drawing: Bool = false,
              completion: @escaping @MainActor @Sendable () -> Void) {
        transition(toolbar: toolbar, model: model, showing: true, drawing: drawing, completion: completion)
    }

    private func transition(toolbar: NSPanel, model: WorkspaceModel, showing: Bool, drawing: Bool = false,
                            completion: @escaping @MainActor @Sendable () -> Void) {
        let reversing = animatedToolbar === toolbar && animatedModel === model && hasActiveTimeline &&
            sceneVersion == version(for: model)
        let toolbarState = reversing ? toolbarPoof?.imageState ?? toolbarImageMotion?.currentState ?? layerMotions.last?.currentState : nil
        let poofProgress = reversing ? toolbarPoof?.currentProgress : nil
        let imageStates = reversing ? layerMotions.dropLast().map(\.currentState) : []
        let particleStates = reversing ? Dictionary(uniqueKeysWithValues: zip(burstKeys, burstViews.map(\.currentProgress))) : [:]
        let retainedToolbarImage = reversing ? toolbarImage : nil
        let retainedSharedToolbarImage = reversing ? sharedToolbarImage : nil
        let retainedImages = reversing ? transitionImages : nil
        cancel()
        targetVisible = showing
        let revealDrawing = showing && drawing && model.prepareDrawingForReveal()
        revealDrawingRequested = revealDrawing
        let animate = shouldAnimate(model)
        let images = animate ? (retainedImages ?? snapshots(model)) : []
        sceneVersion = version(for: model)
        let sharing = model.regionSharingState.isCapturing ? model.regionSharing : nil
        let sharedProtection = sharing?.workspaceProtectionFrames() ?? []
        let style = animationStyle(model)
        if animate && ([.dissolve, .slide, .magic, .corners].contains(style) || VisibilityPose.usesPose(style)) {
            do {
                if style == .dissolve { try ParticleBurstView.prepare() }
                let pixelBudget = 40_000_000 - images.reduce(0) { $0 + $1.image.width * $1.image.height }
                toolbarImage = try retainedToolbarImage ?? snapshotToolbar(toolbar, pixelBudget: pixelBudget)
            } catch {
                model.status = "The toolbar animation could not start. Using a fade instead."
                logger.error("Toolbar particle preparation failed: \(error.localizedDescription, privacy: .private)")
            }
        }
        if animate, let sharing {
            do {
                sharedToolbarImage = try retainedSharedToolbarImage ?? toolbarImage ??
                    snapshotToolbar(toolbar, pixelBudget: 4_000_000)
                sharedTransition = sharing.beginWorkspaceAnimation()
                sharedController = sharing
            } catch { sharing.workspaceAnimationFailed(error) }
        }
        if !showing { model.concealWorkspaceSurfaces() }
        guard animate else {
            toolbar.alphaValue = 1
            if showing {
                toolbar.orderFrontRegardless()
                model.revealWorkspaceSurfaces(drawing: revealDrawing)
            } else { toolbar.orderOut(nil) }
            model.visibilityAnimating = false
            completion()
            return
        }
        model.visibilityAnimating = true
        animatedToolbar = toolbar
        animatedModel = model
        toolbarInputBeforeAnimation = toolbar.ignoresMouseEvents
        toolbar.ignoresMouseEvents = true
        if toolbar.isKeyWindow { toolbar.resignKey() }
        let keys = images.filter { $0.elementID != nil }.map(\.id)
        if style == .corners {
            let items = images.filter { $0.elementID != nil }.map {
                CornerRoutes.Item(id: $0.id, displayID: $0.displayID, visibleFrame: $0.visibleFrame, center: $0.center)
            }
            cornerRoutes.update(items)
        }
        if !reversing && (!showing || Set(keys) != Set(objectOrder.keys)) {
            let order = shuffled?(keys) ?? ToolbarSlideMotion.distanceOrder(images.filter { $0.elementID != nil }.map { image in
                let center = CGPoint(x: image.frame.midX, y: image.frame.midY)
                return (image.id, center, style == .corners ? cornerRoutes.destinations[image.id]?.point ?? center :
                    CGPoint(x: toolbar.frame.midX, y: toolbar.frame.midY))
            })
            objectOrder = Dictionary(uniqueKeysWithValues: order.enumerated().map { ($0.element, $0.offset) })
        }
        let timing = WorkspaceMotionTiming(style: style, appearing: showing,
            hasDrawings: !images.isEmpty, objectCount: keys.count)
        let duration = timing.duration
        let toolbarDelay = reversing ? 0 : timing.toolbarDelay
        let run = token
        let started = CACurrentMediaTime()
        transitionImages = images
        var burstDelays: [String: Double] = [:]
        var gatherArrivals: [CFTimeInterval] = []
        for (index, image) in images.enumerated() {
            let delay = reversing ? 0 : (image.elementID == nil ? (showing ? 0 : timing.drawingsEndDelay) :
                timing.drawingDelay(rank: objectOrder[image.id] ?? 0))
            if let id = image.elementID { transitionObjectDelays[id] = delay }
            let imageStyle: VisibilityAnimationStyle = (style == .magic || style == .corners || VisibilityPose.usesPose(style)) &&
                image.elementID == nil ? .fade : style
            let poseSeed: UInt64? = VisibilityPose.usesPose(imageStyle) ? MagicFlight.seed(for: image.id) : nil
            let magicSeed: UInt64? = imageStyle == .magic ?
                MagicFlight.seed(for: image.id) ^ (showing ? 0x9e3779b97f4a7c15 : 0) : nil
            let screen = NSScreen.screens.first { $0.frame.contains(CGPoint(x: image.frame.midX, y: image.frame.midY)) }?.frame ?? image.frame
            var target = ToolbarSlideMotion.annotationTarget(source: image.frame, toolbar: toolbar.frame, screen: screen)
            var curveSign: CGFloat = 1
            if imageStyle == .corners {
                guard let route = cornerRoutes.destinations[image.id] else {
                    model.status = "A shape could not animate because its corner is unavailable."
                    logger.error("Missing corner assignment for a visible shape.")
                    continue
                }
                target = route.point
                curveSign = showing ? -route.bendSign : route.bendSign
                if let id = image.elementID { transitionCornerTargets[id] = target }
            }
            var travelFrame = [.slide, .quick, .magic, .corners].contains(imageStyle) ?
                image.frame.union(CGRect(x: target.x - 12, y: target.y - 12, width: 24, height: 24)) :
                VisibilityPose.travel(imageStyle, frame: image.frame)
            if (imageStyle == .magic || imageStyle == .corners), index < imageStates.count {
                let current = imageStates[index].transform
                travelFrame = travelFrame.union(image.frame.offsetBy(dx: current.m41, dy: current.m42))
            }
            let padding = imageStyle == .magic ? MagicFlight.maximumBend + max(image.frame.width, image.frame.height) * 0.18 + 24 :
                (style == .dissolve ? 170 : VisibilityPose.padding(imageStyle, size: image.frame.size))
            let envelope = travelFrame.insetBy(dx: -padding, dy: -padding)
            let panel = transitionPanel(image: image.image, frame: envelope, imageFrame: image.frame, level: toolbar.level)
            if let view = panel.contentView as? VisibilityImageView {
                if imageStyle == .focus { view.enableBlur() }
                let hidden: CATransform3D? = [.slide, .quick, .dissolve, .magic, .corners].contains(imageStyle) ?
                    ToolbarSlideMotion.annotationTransform(source: image.frame, target: target) : nil
                let motion = WorkspaceLayerMotion(layer: view.imageLayer, style: imageStyle,
                    appearing: showing, flipped: false, duration: duration,
                    initial: index < imageStates.count ? imageStates[index] : nil,
                    started: started + delay, hiddenTransform: hidden, annotation: true, magicSeed: magicSeed,
                    curveSign: curveSign, poseSeed: poseSeed)
                layerMotions.append(motion)
                if imageStyle == .slide && image.elementID != nil { gatherArrivals.append(motion.endTime) }
                if let magicSeed {
                    do {
                        magicTrails.append(try MagicTrail(imageLayer: view.imageLayer, transforms: motion.flightTransforms,
                            seed: magicSeed, started: started + delay, duration: motion.effectiveDuration))
                    } catch {
                        model.status = "Light trails could not start. Shapes will still move."
                        logger.error("Magic trail setup failed: \(error.localizedDescription, privacy: .private)")
                    }
                }
                if style == .dissolve {
                    do {
                        let particles = try ParticleBurstView(image: image.image, imageFrame: view.imageLayer.frame,
                            frame: view.bounds, particleLimit: max(1, 96_000 / max(1, images.count)))
                        particles.autoresizingMask = [.width, .height]
                        let toward = CGVector(dx: toolbar.frame.midX - image.frame.midX, dy: toolbar.frame.midY - image.frame.midY)
                        let length = hypot(toward.dx, toward.dy)
                        if length > 1 { particles.sweepDirection = CGVector(dx: toward.dx / length, dy: toward.dy / length) }
                        particles.onFailure = { [weak model, weak view] error in
                            view?.imageLayer.isHidden = false
                            model?.report(error)
                        }
                        view.addSubview(particles)
                        burstViews.append(particles)
                        burstKeys.append(image.id)
                        burstDelays[image.id] = delay
                        view.imageLayer.isHidden = true
                    } catch {
                        model.status = "Dissolve could not start. Using the slide animation instead."
                        logger.error("Dissolve failed: \(error.localizedDescription, privacy: .private)")
                    }
                }
                if let sharedImage = image.sharedImage, let sharedTransition {
                    do {
                        try sharedTransition.add(image: sharedImage, frame: envelope, imageFrame: image.frame,
                            style: imageStyle, appearing: showing, duration: duration, started: started + delay,
                            initial: index < imageStates.count ? imageStates[index] : nil,
                            hiddenTransform: hidden, annotation: true, magicSeed: magicSeed, curveSign: curveSign,
                            particleLimit: max(1, 96_000 / max(1, images.count)),
                            particleProgress: particleStates[image.id],
                            particlesEnabled: style != .dissolve || burstKeys.contains(image.id),
                            poseSeed: poseSeed, sweepDirection: burstViews.last.flatMap {
                                burstKeys.last == image.id ? $0.sweepDirection : nil })
                    } catch { sharing?.workspaceAnimationFailed(error) }
                }
            }
            panels.append(panel)
            panel.orderFrontRegardless()
        }
        let edge = ToolbarSlideMotion.resolvedEdge(frame: toolbar.frame,
            screen: toolbar.screen?.frame ?? NSScreen.main?.frame ?? toolbar.frame,
            orientation: model.preferences.toolbarOrientation ?? .horizontal,
            edge: model.preferences.toolbarDockEdge ?? .automatic)
        applyLayerMotion(to: toolbar, style: style, appearing: showing, duration: timing.toolbarDuration,
                         initial: toolbarState, started: started + toolbarDelay, edge: edge)
        toolbar.alphaValue = 1
        toolbar.orderFrontRegardless()
        if let toolbarImage {
            do {
                let hidden = ToolbarSlideMotion.annotationTransform(source: toolbar.frame,
                    target: CGPoint(x: toolbar.frame.midX, y: toolbar.frame.midY))
                let envelope = toolbar.frame.insetBy(dx: -170, dy: -170)
                let panel = transitionPanel(image: toolbarImage, frame: envelope,
                                            imageFrame: toolbar.frame, level: toolbar.level)
                panel.level = toolbar.level
                guard let view = panel.contentView as? VisibilityImageView else { throw RenderFailure.imageUnavailable }
                if style == .dissolve {
                    let particles = try ParticleBurstView(image: toolbarImage, imageFrame: view.imageLayer.frame,
                                                      frame: view.bounds, particleLimit: 32_000)
                    particles.autoresizingMask = [.width, .height]
                    particles.onFailure = { [weak model] error in model?.report(error) }
                    view.addSubview(particles)
                    view.imageLayer.isHidden = true
                    burstViews.append(particles)
                    burstKeys.append("toolbar")
                    burstDelays["toolbar"] = toolbarDelay
                } else if style == .magic {
                    toolbarPoof = try MagicToolbarEffect(imageLayer: view.imageLayer, appearing: showing,
                        started: started + toolbarDelay, duration: timing.toolbarDuration, progress: poofProgress)
                } else if VisibilityPose.usesPose(style) {
                    if style == .focus { view.enableBlur() }
                    toolbarImageMotion = WorkspaceLayerMotion(layer: view.imageLayer, style: style,
                        appearing: showing, flipped: false, duration: timing.toolbarDuration, initial: toolbarState,
                        started: started + toolbarDelay, poseSeed: 0)
                } else {
                    let motion = WorkspaceLayerMotion(layer: view.imageLayer, style: .slide,
                        appearing: showing, flipped: false, duration: timing.toolbarDuration, initial: toolbarState,
                        started: started + toolbarDelay, hiddenTransform: hidden)
                    toolbarImageMotion = motion
                    if style == .slide && !showing && !reversing {
                        // The toolbar gives a small pulse as gathered shapes land in it.
                        view.absorbPulses(at: gatherArrivals)
                    }
                }
                panels.append(panel)
                panel.orderFrontRegardless()
                toolbar.alphaValue = 0
            } catch {
                model.status = "The toolbar particle animation could not start. Using a fade instead."
                logger.error("Toolbar particle setup failed: \(error.localizedDescription, privacy: .private)")
            }
        }
        if let sharedTransition, let sharedToolbarImage {
            do {
                if let image = try SharedWorkspaceTransition.clippedImage(sharedToolbarImage,
                    screenFrame: toolbar.frame, region: sharedTransition.screenRegion, protection: sharedProtection) {
                    let usesSnapshot = toolbarImage != nil
                    let posed = usesSnapshot && VisibilityPose.usesPose(style)
                    let mirrorStyle: VisibilityAnimationStyle = usesSnapshot && style != .magic && style != .dissolve && !posed ? .slide : style
                    let hidden = usesSnapshot && !posed ? ToolbarSlideMotion.annotationTransform(source: toolbar.frame,
                        target: CGPoint(x: toolbar.frame.midX, y: toolbar.frame.midY)) : nil
                    var initial = toolbarState
                    if !usesSnapshot, toolbar.contentView?.isFlipped == true, let state = initial {
                        let flip = CATransform3DMakeScale(1, -1, 1)
                        initial?.transform = CATransform3DConcat(flip, CATransform3DConcat(state.transform, flip))
                    }
                    try sharedTransition.add(image: image,
                        frame: usesSnapshot ? toolbar.frame.insetBy(dx: -170, dy: -170) : toolbar.frame,
                        imageFrame: toolbar.frame, style: mirrorStyle, appearing: showing,
                        duration: timing.toolbarDuration, started: started + toolbarDelay, initial: initial,
                        hiddenTransform: hidden, annotation: false, edge: edge,
                        particleProgress: particleStates["toolbar"], toolbarPoofProgress: poofProgress,
                        particlesEnabled: burstKeys.contains("toolbar"), clipToFrame: !usesSnapshot,
                        poseSeed: posed ? 0 : nil,
                        absorbArrivals: style == .slide && usesSnapshot && !showing && !reversing ? gatherArrivals : [])
                }
            } catch { sharing?.workspaceAnimationFailed(error) }
        }
        let burstStart = started
        for (key, view) in zip(burstKeys, burstViews) {
            let delay = burstDelays[key] ?? 0
            view.start(appearing: showing, duration: duration, at: burstStart + delay,
                       progress: particleStates[key])
        }
        let ends = layerMotions.map(\.endTime) + [toolbarImageMotion?.endTime, toolbarPoof?.endTime].compactMap { $0 } +
            burstViews.compactMap(\.endTime)
        let reversedDuration = max(0, (ends.max() ?? started + max(images.isEmpty ? 0 : duration, timing.toolbarDuration)) - started)
        if let sharedTransition { sharing?.prepareWorkspaceAnimation(sharedTransition.id) }
        finishByDeadline(run: run, duration: reversing ? reversedDuration : timing.totalDuration,
                         toolbar: toolbar, model: model, showing: showing, drawing: revealDrawing, completion: completion)
    }

    private func version(for model: WorkspaceModel) -> SceneVersion {
        let documents = model.desktopSessions.map {
            DocumentVersion(id: $0.document.identity, generation: $0.document.generation, frame: $0.panel.frame, board: $0.board)
        }.sorted {
            if $0.id != $1.id { return $0.id.uuidString < $1.id.uuidString }
            if $0.frame.minX != $1.frame.minX { return $0.frame.minX < $1.frame.minX }
            return $0.frame.minY < $1.frame.minY
        }
        return SceneVersion(documents: documents, displays: NSScreen.screens.map(\.visibleFrame),
                            scales: NSScreen.screens.map(\.backingScaleFactor),
                            sharedRegion: model.regionSharingState.isCapturing ? model.regionSharing.workspaceAnimationRegion : nil)
    }

    func snapshotToolbar(_ toolbar: NSPanel, pixelBudget: Int) throws -> CGImage {
        guard let view = toolbar.contentView else { throw RenderFailure.imageUnavailable }
        view.needsLayout = true
        view.layoutSubtreeIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds),
              bitmap.pixelsWide * bitmap.pixelsHigh <= min(4_000_000, pixelBudget) else { throw RenderFailure.invalidSize }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let image = bitmap.cgImage else { throw RenderFailure.imageUnavailable }
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                bytesPerRow: 0, space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw RenderFailure.imageUnavailable
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let normalized = context.makeImage() else { throw RenderFailure.imageUnavailable }
        return normalized
    }

    private func applyLayerMotion(to panel: NSPanel, style: VisibilityAnimationStyle, appearing: Bool,
                                  duration: Double, initial: WorkspaceLayerMotion.State?,
                                  started: CFTimeInterval, edge: ToolbarDockEdge = .bottom) {
        guard let view = panel.contentView else { return }
        view.wantsLayer = true
        view.layoutSubtreeIfNeeded()
        guard let layer = view.layer else { return }
        layerMotions.append(WorkspaceLayerMotion(layer: layer, style: style, appearing: appearing,
            flipped: view.isFlipped, duration: duration, initial: initial, started: started, edge: edge))
    }

    private func finishByDeadline(run: UUID, duration: Double, toolbar: NSPanel, model: WorkspaceModel,
                                  showing: Bool, drawing: Bool, completion: @escaping @MainActor @Sendable () -> Void) {
        completionDeadline = Task { [weak self, weak toolbar, weak model] in
            do { try await Task.sleep(for: .seconds(duration + 0.02)) } catch { return }
            guard let self, self.token == run else { return }
            guard let toolbar, let model else { self.cancel(); return }
            let sessions = showing ? model.desktopSessions : []
            let opacities = sessions.map { $0.panel.alphaValue }
            if showing {
                sessions.forEach { $0.panel.alphaValue = 0 }
                model.revealWorkspaceSurfaces(drawing: drawing && self.revealDrawingRequested)
                for session in sessions {
                    session.canvas.displayIfNeeded()
                    session.panel.displayIfNeeded()
                }
            }
            guard self.token == run else {
                for (session, opacity) in zip(sessions, opacities) { session.panel.alphaValue = opacity }
                return
            }
            self.burstViews.forEach { $0.stop() }
            // Only swap visibility here. Rendering or acquiring Metal drawables
            // while screen updates are held can stall the compositor.
            NSDisableScreenUpdates()
            for panel in self.panels { panel.orderOut(nil) }
            for (session, opacity) in zip(sessions, opacities) { session.panel.alphaValue = opacity }
            if !showing { toolbar.orderOut(nil) }
            toolbar.alphaValue = 1
            NSEnableScreenUpdates()
            self.cancel(finishingSharedAnimation: true)
            model.visibilityAnimating = false
            completion()
        }
    }

    private func shouldAnimate(_ model: WorkspaceModel) -> Bool {
        guard model.preferences.animateVisibility != false,
              model.preferences.visibilityAnimationStyle != VisibilityAnimationStyle.none else { return false }
        let count = model.desktopSessions.reduce(0) { $0 + $1.document.elements.count }
        guard count <= 64 else {
            model.status = "This board has more than 64 shapes. It will show and hide without animation."
            logger.notice("Visibility animation skipped for \(count) objects.")
            return false
        }
        return true
    }

    private func animationStyle(_ model: WorkspaceModel) -> VisibilityAnimationStyle {
        // Reduce Motion keeps a short crossfade in place instead of movement.
        if reduceMotion() { return .fade }
        let style = model.preferences.visibilityAnimationStyle ?? .slide
        return style.resolved
    }

    private func transitionPanel(image: CGImage, frame: CGRect, imageFrame: CGRect, level: NSWindow.Level) -> NSPanel {
        let panel = VisibilityTransitionPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false; panel.backgroundColor = .clear
        panel.hasShadow = false; panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false; panel.isReleasedWhenClosed = false
        panel.level = NSWindow.Level(rawValue: level.rawValue - 1)
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let localImageFrame = imageFrame.offsetBy(dx: -frame.minX, dy: -frame.minY)
        let view = VisibilityImageView(image: image, frame: CGRect(origin: .zero, size: frame.size), imageFrame: localImageFrame)
        view.autoresizingMask = [.width, .height]
        panel.contentView = view
        return panel
    }

    private func snapshots(_ model: WorkspaceModel) -> [Snapshot] {
        var result: [Snapshot] = []
        var remainingPixels = 40_000_000
        var remainingSharedPixels = 16_000_000
        let sharing = model.regionSharingState.isCapturing ? model.regionSharing : nil
        let sharedRegion = sharing?.workspaceAnimationRegion
        let sharedProtection = sharing?.workspaceProtectionFrames() ?? []
        for session in model.desktopSessions {
            guard !session.document.elements.isEmpty || session.board != .transparent else { continue }
            do {
                let size = session.canvas.bounds.size
                guard let screen = session.panel.screen ?? NSScreen.screens.first(where: { $0.frame == session.panel.frame }),
                      let display = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
                    throw CanvasError.invalidDocument("The drawing display is unavailable.")
                }
                let scale = session.panel.backingScaleFactor
                let canvasBounds = CGRect(origin: .zero, size: size)
                guard size.width > 0, size.height > 0,
                      let space = CGColorSpace(name: CGColorSpace.sRGB) else { throw RenderFailure.invalidSize }
                var projection = CGAffineTransform.identity
                if let viewport = session.presentation?.screenViewport {
                    let sx = viewport.output.width / viewport.source.width
                    let sy = viewport.output.height / viewport.source.height
                    projection = CGAffineTransform(a: sx, b: 0, c: 0, d: sy,
                        tx: viewport.output.x - viewport.source.x * sx, ty: viewport.output.y - viewport.source.y * sy)
                }
                let renderer = SceneRenderer()
                func append(element: Element?, bounds: CGRect) throws {
                    let clipped = bounds.intersection(canvasBounds)
                    guard !clipped.isNull, !clipped.isEmpty else { return }
                    let pixels = clipped.applying(CGAffineTransform(scaleX: scale, y: scale)).integral
                    guard pixels.width.isFinite, pixels.height.isFinite,
                          pixels.width * pixels.height <= CGFloat(remainingPixels),
                          let context = CGContext(data: nil, width: Int(pixels.width), height: Int(pixels.height),
                            bitsPerComponent: 8, bytesPerRow: 0, space: space,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw RenderFailure.invalidSize }
                    let crop = pixels.applying(CGAffineTransform(scaleX: 1 / scale, y: 1 / scale))
                    let frame = CGRect(x: session.panel.frame.minX + crop.minX,
                                       y: session.panel.frame.maxY - crop.maxY, width: crop.width, height: crop.height)
                    context.scaleBy(x: scale, y: scale)
                    context.translateBy(x: 0, y: crop.height)
                    context.scaleBy(x: 1, y: -1)
                    context.translateBy(x: -crop.minX, y: -crop.minY)
                    if let element {
                        context.concatenate(projection)
                        let overlay = element.tool.isPixelEffect ?
                            try session.presentation?.transitionOverlay(elements: [element]) : nil
                        renderer.draw([element], in: context, pixelsAvailableForBlur: overlay != nil)
                        if element.tool.isPixelEffect, let overlay {
                            context.saveGState()
                            let center = element.frame.center
                            context.translateBy(x: center.x, y: center.y)
                            context.rotate(by: element.rotation)
                            context.translateBy(x: -center.x, y: -center.y)
                            context.clip(to: element.frame.cgRect)
                            context.translateBy(x: center.x, y: center.y)
                            context.rotate(by: -element.rotation)
                            context.translateBy(x: -center.x, y: -center.y)
                            context.translateBy(x: 0, y: size.height)
                            context.scaleBy(x: 1, y: -1)
                            context.draw(overlay, in: canvasBounds)
                            context.restoreGState()
                        }
                    } else {
                        context.setFillColor((session.board == .light ? NSColor.white : NSColor(calibratedWhite: 0.08, alpha: 1)).cgColor)
                        context.fill(canvasBounds)
                    }
                    var sharedImage: CGImage?
                    if element?.tool.isProtection != true, let sharedRegion,
                       sharing?.selectedDisplayID == display.uint32Value, frame.intersects(sharedRegion),
                       !SharedWorkspaceTransition.unprotectedPath(in: frame.intersection(sharedRegion),
                                                                 excluding: sharedProtection).isEmpty {
                        guard Int(pixels.width * pixels.height) <= remainingSharedPixels,
                              let clean = context.makeImage() else { throw RenderFailure.invalidSize }
                        sharedImage = try SharedWorkspaceTransition.clippedImage(clean, screenFrame: frame,
                            region: sharedRegion, protection: sharedProtection)
                        if sharedImage != nil { remainingSharedPixels -= Int(pixels.width * pixels.height) }
                    }
                    if let element, model.mode == .draw {
                        renderer.selection([element], ids: session.document.selection, in: context)
                    }
                    guard let image = context.makeImage() else { throw RenderFailure.imageUnavailable }
                    remainingPixels -= Int(pixels.width * pixels.height)
                    let logicalCenter = element?.frame.center.cgPoint.applying(projection) ?? CGPoint(x: crop.midX, y: crop.midY)
                    let center = CGPoint(x: session.panel.frame.minX + logicalCenter.x,
                                         y: session.panel.frame.maxY - logicalCenter.y)
                    result.append(Snapshot(id: "\(session.document.identity)/\(element?.id.uuidString ?? "board")",
                                           elementID: element?.id, image: image, sharedImage: sharedImage, frame: frame,
                                           displayID: display.uint32Value, visibleFrame: screen.visibleFrame, center: center))
                }
                if session.board != .transparent { try append(element: nil, bounds: canvasBounds) }
                for element in ElementStacking.ordered(session.document.elements) {
                    let padding = model.mode == .draw && session.document.selection.contains(element.id) ? 32.0 : 0
                    let bounds = renderer.drawingBounds(for: element).insetBy(dx: -padding, dy: -padding)
                    try append(element: element, bounds: bounds.applying(projection))
                }
            } catch {
                model.status = "Visibility animation unavailable: \(error.localizedDescription)"
                logger.error("Visibility snapshot failed: \(error.localizedDescription, privacy: .private)")
                sharing?.workspaceAnimationFailed(error)
            }
        }
        return result
    }

    static func visibleBounds(in context: CGContext) -> CGRect? {
        guard let data = context.data else { return nil }
        let bytes = data.assumingMemoryBound(to: UInt8.self)
        var minX = context.width, minY = context.height, maxX = -1, maxY = -1
        for y in 0..<context.height {
            let row = bytes.advanced(by: y * context.bytesPerRow)
            var x = 0
            while x < context.width {
                let count = min(8, context.width - x)
                if count == 8 {
                    let pixels = UnsafeRawPointer(row.advanced(by: x * 4)).loadUnaligned(as: SIMD4<UInt64>.self)
                    let alpha = pixels & SIMD4<UInt64>(repeating: 0xff000000ff000000)
                    if alpha == .zero { x += 8; continue }
                }
                for column in x..<(x + count) where row[column * 4 + 3] != 0 {
                    minX = min(minX, column); minY = min(minY, y)
                    maxX = max(maxX, column); maxY = max(maxY, y)
                }
                x += count
            }
        }
        guard maxX >= minX, maxY >= minY else { return nil }
        return CGRect(x: minX, y: minY, width: maxX-minX+1, height: maxY-minY+1)
    }
}
