import AppKit
import SwiftUI
import CoreImage
import AnnotationCore
import PresentationRendering
import NativePlatform

enum RegionSharingState: Equatable {
    case idle, starting, live, stopped(String), failed(String)
    var isCapturing: Bool { self == .starting || self == .live }
    var title: String {
        switch self {
        case .idle, .stopped, .failed: "Region sharing is off"
        case .starting: "Starting region sharing"
        case .live: "Region sharing is on"
        }
    }
    var message: String? {
        switch self {
        case .stopped(let message), .failed(let message): message
        case .idle, .starting, .live: nil
        }
    }
}

struct RegionSharingDisplay: Identifiable {
    let id: CGDirectDisplayID
    let name: String
    let frame: CGRect
    let scale: Double
    var localBounds: Rect { Rect(x: 0, y: 0, width: frame.width, height: frame.height) }
}

private enum RegionSharingFailure: Error, LocalizedError {
    case display, region, unavailable, transition
    var errorDescription: String? {
        switch self {
        case .display: "The selected display is unavailable. Choose a connected display."
        case .region: "Choose a region inside the display, at least 32 points wide and high."
        case .unavailable: "The live screen view is unavailable. Stop sharing and reopen Annotator."
        case .transition: "Wait for the workspace animation to finish, then start sharing."
        }
    }
}

@MainActor
final class RegionSharingController: NSObject, ObservableObject, NSWindowDelegate {
    private struct AnimationDocument: Equatable {
        let id: UUID
        let generation: UInt64
        let elements: [Element]
        let board: WorkspaceModel.Board

        @MainActor init(_ session: OverlaySession) {
            id = session.document.identity
            generation = session.document.generation
            elements = session.canvas.elementsForSharing
            board = session.board
        }
    }

    private weak var model: WorkspaceModel?
    @Published private(set) var displays: [RegionSharingDisplay] = []
    @Published private(set) var selectedDisplayID: CGDirectDisplayID?
    @Published private(set) var selectedPreset: ShareRegionPreset? = .leftHalf
    @Published private(set) var region: SharingRegion?
    @Published private(set) var validationMessage: String?
    @Published private(set) var state = RegionSharingState.idle {
        didSet {
            model?.objectWillChange.send()
            model?.onRegionSharingStateChanged?(state)
        }
    }
    private(set) var controls: NSPanel?
    private(set) var output: SharedRegionWindow?
    private let guide = RegionSharingGuide()
    private weak var sourceSession: OverlaySession?
    private var sourceFrame = CGRect.zero
    private var generation: UInt64 = 0
    private var protection: [Element] = []
    private var drawingsVisible = true
    private var toolbar: SharedToolbarCapture?
    private var workspaceTransition: SharedWorkspaceTransition?
    private var animationDocument: AnimationDocument?
    private var readiness: Task<Void, Never>?
    private var permissionCheck: Task<Void, Never>?
    var isSelectingRegion: Bool { guide.isSelecting }
    var selectionWindows: [CGDirectDisplayID: NSPanel] { guide.selectionWindows }
    var outlineFrame: CGRect? { guide.outlineFrame }
    var outlineFillOpacity: Double { guide.outlineFillOpacity }
    var hasOutput: Bool { output != nil }
    var isCapturingToolbar: Bool { toolbar?.isCapturing == true }
    var selectedDisplay: RegionSharingDisplay? { displays.first { $0.id == selectedDisplayID } }
    var workspaceAnimationRegion: CGRect? {
        guard state.isCapturing, let display = selectedDisplay, let region else { return nil }
        return RegionSharingGuide.screenFrame(for: region.bounds, on: display.frame)
    }

    func workspaceProtectionFrames() -> [CGRect] {
        guard let source = sourceSession else { return [] }
        var projection = CGAffineTransform.identity
        if let viewport = source.presentation?.screenViewport {
            let sx = viewport.output.width / viewport.source.width
            let sy = viewport.output.height / viewport.source.height
            projection = CGAffineTransform(a: sx, b: 0, c: 0, d: sy,
                tx: viewport.output.x - viewport.source.x * sx, ty: viewport.output.y - viewport.source.y * sy)
        }
        let renderer = SceneRenderer()
        return source.document.elements.filter(\.tool.isProtection).map {
            let bounds = renderer.drawingBounds(for: $0).applying(projection)
            return RegionSharingGuide.screenFrame(for: Rect(bounds), on: sourceFrame)
        }
    }

    func beginWorkspaceAnimation() -> SharedWorkspaceTransition? {
        guard let screenRegion = workspaceAnimationRegion, let source = sourceSession, let output else { return nil }
        generation &+= 1
        state = .starting
        output.content.cover("Updating the shared view...", generation: generation)
        let transition = SharedWorkspaceTransition(screenRegion: screenRegion)
        transition.onFailure = { [weak self, weak transition] error in
            guard let self, let transition, workspaceTransition === transition else { return }
            fail(error)
        }
        transition.updateProtection(workspaceProtectionFrames())
        workspaceTransition = transition
        animationDocument = AnimationDocument(source)
        output.content.installWorkspaceTransition(transition)
        watchReadiness()
        return transition
    }

    func prepareWorkspaceAnimation(_ id: UUID) {
        guard state.isCapturing, let transition = workspaceTransition, transition.id == id else { return }
        transition.prepare()
        sourceSession?.presentation?.refreshSharedRegion()
    }

    func finishWorkspaceAnimation(_ id: UUID) {
        guard state.isCapturing, let transition = workspaceTransition, transition.id == id else { return }
        transition.finish()
        generation &+= 1
        output?.content.holdWorkspaceTransition(generation: generation)
        state = .starting
        watchReadiness()
        sourceSession?.presentation?.refreshSharedRegion()
    }

    func cancelWorkspaceAnimation(_ id: UUID) {
        guard state.isCapturing, workspaceTransition?.id == id else { return }
        generation &+= 1
        output?.content.cover("Updating the shared view...", generation: generation)
        workspaceTransition = nil
        animationDocument = nil
        state = .starting
        watchReadiness()
        sourceSession?.presentation?.refreshSharedRegion()
    }

    func workspaceAnimationFailed(_ error: Error) {
        guard state.isCapturing else { return }
        fail(error)
    }

    @discardableResult
    func workspaceSceneChanged() -> Bool {
        guard let transition = workspaceTransition, let version = animationDocument, let source = sourceSession,
              version != AnimationDocument(source) else { return false }
        model?.status = "The shared animation stopped because the drawing changed."
        cancelWorkspaceAnimation(transition.id)
        return true
    }

    init(model: WorkspaceModel) { self.model = model }

    func selectionCursor(at point: CGPoint, windowNumber: Int) -> NSCursor? {
        guide.cursor(at: point, windowNumber: windowNumber)
    }

    func showControls() {
        refreshDisplays()
        chooseInitialDisplay()
        if controls == nil {
            let available = NSScreen.screens.first { $0.frame == selectedDisplay?.frame }?.visibleFrame
                ?? NSScreen.main?.visibleFrame ?? CGRect(x: 0, y: 0, width: 960, height: 720)
            let panel = AnnotationPanel(contentRect: CGRect(x: 0, y: 0,
                width: min(960, available.width - 32), height: min(660, available.height - 64)),
                styleMask: [.titled, .closable, .resizable, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.title = "Share a region"
            panel.isReleasedWhenClosed = false
            panel.hidesOnDeactivate = false
            panel.isRestorable = false
            panel.contentMinSize = CGSize(width: min(840, available.width - 32), height: min(600, available.height - 64))
            panel.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 4)
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel.delegate = self
            let host = NSHostingController(rootView: RegionSharingControls(sharing: self))
            host.sizingOptions = []
            panel.contentViewController = host
            panel.center()
            controls = panel
        }
        controls?.makeKeyAndOrderFront(nil)
        updateOutline()
    }

    private func chooseInitialDisplay() {
        if selectedDisplayID == nil, let model {
            let chosen = displays.first { $0.frame == model.active?.panel.frame }
                ?? displays.first { $0.frame.contains(model.screenPointer()) } ?? displays.first
            if let chosen { selectDisplay(chosen.id) }
        }
    }

    func refreshDisplays() {
        guard let model else { return }
        do {
            let candidates = try model.displaysForRegionSharing()
            displays = candidates.enumerated().map { index, display in
                let screen = NSScreen.screens.first {
                    ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == display.id
                }
                return RegionSharingDisplay(id: display.id, name: screen?.localizedName ?? "Display \(index + 1)",
                                            frame: display.frame, scale: display.scale)
            }
            if selectedDisplayID != nil && selectedDisplay == nil {
                stopForSafety("The selected display disconnected. Choose a connected display.")
                region = nil
                validationMessage = RegionSharingFailure.display.localizedDescription
            } else if let region, let selectedDisplay, region.displayBounds != selectedDisplay.localBounds {
                stopForSafety("The display size changed. Choose the region again.")
                self.region = nil
                validationMessage = RegionSharingFailure.region.localizedDescription
            }
            updateOutline()
        } catch {
            displays = []
            region = nil
            stopForSafety("The display list could not be read. Check connected displays.")
            validationMessage = error.localizedDescription
            model.report(error)
        }
    }

    func selectDisplay(_ id: CGDirectDisplayID) {
        guard let display = displays.first(where: { $0.id == id }) else {
            validationMessage = RegionSharingFailure.display.localizedDescription
            return
        }
        if selectedDisplayID == id && region != nil { return }
        stopForRegionChange()
        selectedDisplayID = id
        do {
            if let selectedPreset {
                region = try SharingRegion(preset: selectedPreset, displayBounds: display.localBounds)
                validationMessage = nil
            } else {
                region = nil
                validationMessage = "Draw or enter a custom region for this display."
            }
        } catch { region = nil; validationMessage = error.localizedDescription }
        updateOutline()
    }

    func choosePreset(_ preset: ShareRegionPreset) {
        guard let display = selectedDisplay else {
            validationMessage = RegionSharingFailure.display.localizedDescription
            return
        }
        do {
            let selected = try SharingRegion(preset: preset, displayBounds: display.localBounds)
            if selected != region { stopForRegionChange() }
            selectedPreset = preset
            region = selected
            validationMessage = nil
            output?.content.configure(region: selected)
            updateOutline()
        } catch { validationMessage = error.localizedDescription }
    }

    func applyCustom(_ bounds: Rect) {
        guard let display = selectedDisplay else {
            validationMessage = RegionSharingFailure.display.localizedDescription
            return
        }
        applyCustom(bounds, on: display)
    }

    private func applyCustom(_ bounds: Rect, on display: RegionSharingDisplay) {
        do {
            let selected = try SharingRegion(bounds: bounds, displayBounds: display.localBounds)
            if selected != region || selectedDisplayID != display.id { stopForRegionChange() }
            selectedDisplayID = display.id
            region = selected
            selectedPreset = nil
            validationMessage = nil
            output?.content.configure(region: selected)
            updateOutline()
        } catch { validationMessage = RegionSharingFailure.region.localizedDescription }
    }

    func invalidCustomNumbers() { validationMessage = "Enter numbers for the region's position, width and height." }

    func drawCustomRegion() {
        guard let display = selectedDisplay else {
            validationMessage = RegionSharingFailure.display.localizedDescription
            return
        }
        stopForRegionChange()
        controls?.orderOut(nil)
        guide.hideOutline()
        guide.choose(on: displays, preferredDisplayID: display.id) { [weak self] result in
            guard let self else { return }
            if case .selected(let id, let bounds) = result {
                if let target = displays.first(where: { $0.id == id }) {
                    applyCustom(bounds, on: target)
                } else {
                    validationMessage = RegionSharingFailure.display.localizedDescription
                }
            }
            if result == .focusLost { controls?.orderFront(nil) }
            else { controls?.makeKeyAndOrderFront(nil) }
            updateOutline()
            model?.refreshPointerCursor()
        }
    }

    func start() {
        guard !state.isCapturing, let model else { return }
        refreshDisplays()
        chooseInitialDisplay()
        do {
            guard !model.visibilityAnimating else { throw RegionSharingFailure.transition }
            model.finishStyleEdit()
            model.active?.canvas.commitTextIfNeeded()
            guard let display = selectedDisplay else { throw RegionSharingFailure.display }
            guard validationMessage == nil, let region, region.displayBounds == display.localBounds else {
                throw RegionSharingFailure.region
            }
            if !DisplayCapture.hasPermission { model.requestCapturePermission() }
            guard DisplayCapture.hasPermission else { throw CaptureFailure.permissionRequired }
            guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
            let source = try model.prepareRegionSharingSession(on: display.id)
            guard let presentation = source.presentation else { throw RegionSharingFailure.unavailable }
            model.interact()
            let output = try self.output ?? SharedRegionWindow(region: region, displayFrame: display.frame)
            self.output = output
            output.onClosed = { [weak self] in self?.stop(closeOutput: true) }
            output.content.onFailure = { [weak self] in self?.fail($0) }
            output.content.onPresented = { [weak self] in self?.presented($0) }
            output.content.configure(region: region)
            generation &+= 1
            output.content.cover("Starting the region...", generation: generation)
            sourceSession = source
            sourceFrame = source.panel.frame
            protection = []
            drawingsVisible = !model.workspaceHidden
            let toolbar = SharedToolbarCapture()
            toolbar.onChanged = { [weak self] in self?.sourceSession?.presentation?.refreshSharedRegion() }
            toolbar.onFailure = { [weak self] in self?.fail($0) }
            self.toolbar = toolbar
            state = .starting
            validationMessage = nil
            presentation.regionSharing = self
            output.show(inBackground: true)
            presentation.retry()
            updateOutline()
            watchCapture()
        } catch { fail(error) }
    }

    func usesCapture(from session: OverlaySession) -> Bool { state.isCapturing && sourceSession === session }

    func receive(_ frame: CaptureFrame, from session: OverlaySession) {
        guard usesCapture(from: session), let model, let region, let output else { return }
        do {
            guard DisplayCapture.hasPermission else { throw CaptureFailure.permissionRequired }
            guard !DisplayCapture.desktopIsLocked else { throw CaptureFailure.desktopLocked }
            guard session.panel.frame == sourceFrame,
                  Rect(session.canvas.bounds) == region.displayBounds else { throw RegionSharingFailure.region }
            if workspaceSceneChanged() { return }
            if let transition = workspaceTransition, !transition.isPrepared { return }
            let animating = workspaceTransition?.isFinishing == false
            let visible = !model.workspaceHidden
            let elements = animating ? session.canvas.elementsForSharing.filter(\.tool.isProtection) :
                visible ? session.canvas.elementsForSharing : []
            let currentProtection = elements.filter(\.tool.isProtection)
            if protection != currentProtection || drawingsVisible != visible {
                let addedProtection = currentProtection.contains { !protection.contains($0) }
                protection = currentProtection
                drawingsVisible = visible
                generation &+= 1
                state = .starting
                if workspaceTransition == nil || addedProtection {
                    output.content.cover("Updating the shared view...", generation: generation,
                                         preservingWorkspaceTransition: workspaceTransition != nil)
                }
                watchReadiness()
            }
            workspaceTransition?.updateProtection(workspaceProtectionFrames())
            let source = region.sourceBounds(through: session.presentation?.screenViewport)
            let background: RGBA? = animating || !visible || session.board == .transparent ? nil :
                session.board == .light ? .white : RGBA(0.08, 0.08, 0.08)
            let toolbarWindow = model.regionSharingToolbar
            if let toolbarWindow, toolbarWindow === output.window || toolbarWindow === controls {
                throw CaptureFailure.streamStopped("Only the toolbar can be included in the shared view. Stop sharing and reopen Annotator.")
            }
            toolbar?.update(window: toolbarWindow, displayFrame: sourceFrame,
                region: RegionSharingGuide.screenFrame(for: region.bounds, on: sourceFrame), enabled: visible && !animating)
            guard toolbar?.isWaitingForFrame != true else { return }
            let overlay = toolbar?.frame.map { frame in
                PixelScreenOverlay(image: CIImage(cvPixelBuffer: frame.pixels),
                    bounds: Rect(x: frame.screenBounds.minX - sourceFrame.minX,
                        y: sourceFrame.maxY - frame.screenBounds.maxY,
                        width: frame.screenBounds.width, height: frame.screenBounds.height),
                    region: region.bounds, opacity: frame.opacity)
            }
            output.content.configurePointer(model: model, sourceFrame: sourceFrame)
            try output.content.present(frame.pixelBuffer, sourceBounds: source, sourceSize: session.canvas.bounds.size,
                elements: elements, background: background, screenOverlay: overlay, generation: generation,
                completingWorkspaceTransition: workspaceTransition?.isFinishing == true)
        } catch { fail(error) }
    }

    func refreshPointer() {
        guard state.isCapturing, let model, let output else { return }
        output.content.configurePointer(model: model, sourceFrame: sourceFrame)
        output.content.refreshPointer()
    }

    private func presented(_ completed: UInt64) {
        guard completed == generation, state.isCapturing else { return }
        readiness?.cancel(); readiness = nil
        if workspaceTransition?.isFinishing == true {
            workspaceTransition = nil
            animationDocument = nil
        }
        if state != .live { state = .live }
        output?.content.setAccessibilityValue("Live region. Choose this window in your meeting app.")
    }

    func toggle() {
        if state.isCapturing { stop() } else { start() }
    }

    func share(preset: ShareRegionPreset) {
        refreshDisplays()
        chooseInitialDisplay()
        choosePreset(preset)
        if validationMessage == nil { start() }
    }

    private func stopForRegionChange() {
        guide.cancelSelection()
        if output != nil { stopForSafety("The region changed. Check its outline, then start sharing.") }
    }

    func stopForSafety(_ message: String, refreshSource: Bool = true) {
        guide.cancelSelection()
        guard output != nil || sourceSession != nil else { return }
        generation &+= 1
        state = .stopped(message)
        output?.content.cover(message, generation: generation)
        stopTasks()
        releaseCapture(refresh: refreshSource)
        updateOutline()
    }

    func captureStopped(_ message: String) { stopForSafety(message, refreshSource: false) }

    private func fail(_ error: Error) {
        generation &+= 1
        state = .failed(error.localizedDescription)
        output?.content.cover(error.localizedDescription, generation: generation)
        stopTasks()
        releaseCapture(refresh: true)
        updateOutline()
        model?.report(error)
        if error is CaptureFailure { model?.reportCaptureFailure(error) }
    }

    func stop(closeOutput: Bool = false) {
        generation &+= 1
        state = .idle
        output?.content.cover("Region sharing is off", generation: generation)
        stopTasks()
        releaseCapture(refresh: true)
        if closeOutput {
            output?.close()
            output = nil
        }
        guide.cancelSelection()
        updateOutline()
    }

    func close() {
        stop(closeOutput: true)
        controls?.delegate = nil
        controls?.contentViewController = nil
        controls?.close()
        controls = nil
        guide.close()
    }

    func windowWillClose(_ notification: Notification) {
        guide.cancelSelection()
        if !state.isCapturing { guide.hideOutline() }
    }

    private func releaseCapture(refresh: Bool) {
        workspaceTransition?.cancel()
        workspaceTransition = nil
        animationDocument = nil
        toolbar?.stop()
        toolbar = nil
        let presentation = sourceSession?.presentation
        sourceSession = nil
        if presentation?.regionSharing === self {
            presentation?.regionSharing = nil
            if refresh { presentation?.refresh() }
        }
    }

    private func updateOutline() {
        guard let display = selectedDisplay, let region,
              controls?.isVisible == true || state.isCapturing else { guide.hideOutline(); return }
        guide.showOutline(region: region, displayFrame: display.frame, selectionFill: !state.isCapturing)
    }

    private func stopTasks() {
        readiness?.cancel(); readiness = nil
        permissionCheck?.cancel(); permissionCheck = nil
    }

    private func watchCapture() {
        stopTasks()
        watchReadiness()
        permissionCheck = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(1)) } catch { return }
                guard let self, state.isCapturing else { return }
                if !DisplayCapture.hasPermission { fail(CaptureFailure.permissionRequired); return }
                if DisplayCapture.desktopIsLocked { stopForSafety("The Mac is locked. Start sharing again after unlocking."); return }
            }
        }
    }

    private func watchReadiness() {
        readiness?.cancel()
        let requested = generation
        readiness = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(8)) } catch { return }
            guard let self, generation == requested, state == .starting else { return }
            fail(CaptureFailure.invalidFrame)
        }
    }
}
