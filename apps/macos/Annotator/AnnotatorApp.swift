import AppKit
import SwiftUI
import AnnotationCore
import NativePlatform

@main
struct AnnotatorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Settings {
            CustomizationView(model: delegate.model, showDone: false)
                .background(SettingsWindowSurface())
                .onAppear {
                    delegate.model.interact()
                    delegate.model.stopZoom()
                }
        }
        .defaultSize(width: 840, height: 700)
        .windowResizability(.contentMinSize)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model: WorkspaceModel
    private var item: NSStatusItem?
    private var toolbar: NSPanel?
    private var dockedFrame: CGRect?
    private var toolbarDockDisplayID: CGDirectDisplayID?
    private var edgeDocked = false
    private var dockPreview: NSPanel?
    private var previewEdge: ToolbarDockEdge?
    private var previewScreen: CGRect?
    private(set) var dockPreviewFrame: CGRect?
    private var pendingToolbarSize: CGSize?
    private var resumeAfterShowing = false
    private let visibility: WorkspaceVisibilityAnimator
    private var hotkey: GlobalHotkey?
    private var hideHotkey: GlobalHotkey?
    private var zoomHotkey: GlobalHotkey?
    private var resumeHotkey: GlobalHotkey?
    private var breakHotkey: GlobalHotkey?
    private var zoomActivationMonitor: ZoomGestureMonitor?
    private var observers: [NSObjectProtocol] = []
    private var lockObserver: NSObjectProtocol?
    private var zoomPermissionObserver: NSObjectProtocol?
    private var updates: AppUpdateController?
    private var library: ToolLibraryWindowController?
    private var menuPopover: MenuBarPopoverController?
    private var errorController: WorkspaceErrorController?
    private(set) var introduction: IntroductionController?

    override init() {
        model = WorkspaceModel()
        visibility = WorkspaceVisibilityAnimator()
        super.init()
        wireWorkspaceCallbacks()
    }
    init(model: WorkspaceModel, visibility: WorkspaceVisibilityAnimator = WorkspaceVisibilityAnimator()) {
        self.model = model
        self.visibility = visibility
        super.init()
        wireWorkspaceCallbacks()
    }
    var toolbarIsVisible: Bool { toolbar?.isVisible ?? false }
    var drawingToolsArePresented: Bool {
        model.visibilityAnimating ? visibility.targetVisible : toolbarIsVisible && !model.workspaceHidden
    }
    var toolbarFrame: CGRect? { toolbar?.frame }
    var toolbarWindow: NSPanel? { toolbar }
    var toolLibraryWindow: NSPanel? { library?.window }
    var errorWindow: NSPanel? { errorController?.window }

    func activateTools() {
        menuPopover?.close()
        if drawingToolsArePresented { hideTools() } else { openDrawing() }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil { return }
        let isLoginItem = AppLaunchContext.isLoginItem(NSAppleEventManager.shared().currentAppleEvent)
        // A copy outside Applications cannot update itself. Ask before anything else starts, and quit if
        // the person chooses to move it in Finder.
        if ApplicationsMoveOffer(defaults: model.preferenceDefaults).offerAtLaunch(isLoginItem: isLoginItem) {
            NSApp.terminate(nil)
            return
        }
        model.supportReminder.recordLaunch()
        model.usageMetrics.recordLaunch()
        model.onThemeChanged = { theme in Self.applyTheme(theme) }
        Self.applyTheme(model.preferences.theme ?? .system)
        model.loginItemController = LoginItemController { [weak model] error in model?.report(error) }
        let updates = AppUpdateController { [weak model] available, automatic, message in
            model?.canCheckForUpdates = available
            model?.automaticallyCheckForUpdates = automatic
            model?.updateStatus = message
        }
        self.updates = updates
        do { try updates.configure(); model.updatesConfigured = updates.isConfigured }
        catch { model.updateStatus = error.localizedDescription; model.report(error) }
        model.onCheckForUpdates = { [weak updates] in updates?.check() }
        model.onAutomaticUpdateChecks = { [weak updates] enabled in updates?.setAutomaticChecks(enabled) }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.autosaveName = "AnnotatorMenuBar"
        if let button = item.button { Self.configureMenuBarButton(button) }
        item.button?.target = self
        item.button?.action = #selector(toggleMenuPopover)
        self.item = item
        do {
            try registerShortcut(model.preferences.activationShortcut)
            item.isVisible = model.preferences.showMenuBarIcon ?? true
        } catch { model.setMenuBarVisible(true); model.report(error) }
        do { try registerHideShortcut(model.preferences.hideShortcut ?? .hideTools) }
        catch { model.report(error) }
        do { try registerZoomShortcut(model.preferences.zoomShortcut ?? .liveZoom) }
        catch { model.report(error) }
        do { try registerResumeShortcut(model.preferences.resumeShortcut ?? .resumeDrawing) }
        catch { model.report(error) }
        do { try registerBreakShortcut(model.preferences.breakShortcut ?? .breakTimer) }
        catch { model.report(error) }
        model.onActivationShortcutChanged = { [weak self] shortcut in
            try self?.replaceShortcut(shortcut)
        }
        model.onHideShortcutChanged = { [weak self] shortcut in try self?.replaceHideShortcut(shortcut) }
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.willSleepNotification, object: nil, queue: .main
        ) { [weak model] _ in MainActor.assumeIsolated { model?.suspendPresentation() } })
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.activeSpaceDidChangeNotification, object: nil, queue: .main
        ) { [weak model] _ in MainActor.assumeIsolated { model?.suspendPresentation(cancelPendingRequests: false) } })
        lockObserver = DistributedNotificationCenter.default().addObserver(
            forName: NSNotification.Name("com.apple.screenIsLocked"), object: nil, queue: .main
        ) { [weak model] _ in MainActor.assumeIsolated { model?.suspendPresentation() } }
        zoomPermissionObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated {
            self?.model.refreshZoomPermissions()
            self?.refreshZoomActivationMonitor()
        } }
        finishLaunchPresentation(isLoginItem: isLoginItem)
    }

    func finishLaunchPresentation(isLoginItem: Bool) {
        if !isLoginItem { showToolbar() }
        introduction?.showAutomatically(isLoginItem: isLoginItem)
    }

    static func configureMenuBarButton(_ button: NSButton, sharingActive: Bool = false) {
        button.image = MenuBarIcon.makeImage()
        button.title = ""
        button.toolTip = sharingActive ? "Annotator. Region sharing is on." : "Annotator"
        button.setAccessibilityLabel("Annotator menu")
        button.setAccessibilityValue(sharingActive ? "Region sharing on" : "Region sharing off")
        let dot = button.subviews.compactMap { $0 as? SharingStatusDot }.first ?? SharingStatusDot(
            frame: CGRect(x: button.bounds.maxX - 7, y: button.bounds.maxY - 7, width: 6, height: 6))
        dot.autoresizingMask = [.minXMargin, .minYMargin]
        dot.setAccessibilityElement(false)
        dot.isHidden = !sharingActive
        if dot.superview == nil { button.addSubview(dot) }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showToolbar()
        return true
    }

    static func applyTheme(_ theme: AppTheme) {
        switch theme {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }

    private func wireWorkspaceCallbacks() {
        errorController = WorkspaceErrorController(model: model)
        introduction = IntroductionController(model: model)
        model.onShowIntroduction = { [weak self] in self?.introduction?.showManually() }
        model.onDismissMenuBar = { [weak self] in
            guard let self else { return false }
            if self.errorController?.dismissIfShown() == true { return true }
            return self.menuPopover?.dismissIfShown() ?? false
        }
        model.onOpenToolLibrary = { [weak self] section in
            guard let self else { return }
            if self.library == nil { self.library = ToolLibraryWindowController(model: self.model) }
            self.library?.show(section: section, near: self.toolbar)
        }
        model.onSuspendPresentation = { [weak self] in
            guard let self else { return }
            (self.toolbar as? InspectorPanel)?.cancelToolbarDrag()
            self.menuPopover?.close()
            self.resumeAfterShowing = false
            self.stopZoomActivationMonitor()
            self.library?.hide()
            self.visibility.cancel()
            self.model.visibilityAnimating = false
            self.applyPendingToolbarSize()
            if self.model.workspaceHidden { self.toolbar?.orderOut(nil) }
        }
        model.onFocusToolbar = { [weak self] in
            guard let self else { return }
            self.showToolbar()
            self.toolbar?.makeKeyAndOrderFront(nil)
        }
        model.onActivationRequested = { [weak self] in self?.activateTools() }
        model.onResumeDrawingRequested = { [weak self] in self?.resumeDrawing() }
        model.onShowToolbar = { [weak self] in self?.showToolbar() }
        model.onEndDrawing = { [weak self] in
            guard let self, !self.model.workspaceHidden, !self.model.visibilityAnimating else { return }
            self.toolbar?.orderFrontRegardless()
        }
        model.onHideToolbar = { [weak self] in self?.hideTools() }
        model.onDismissToolbar = { [weak self] in
            guard let self else { return }
            self.resumeAfterShowing = false
            self.stopZoomActivationMonitor()
            self.library?.hide()
            self.visibility.cancel()
            self.model.visibilityAnimating = false
            self.applyPendingToolbarSize()
            self.toolbar?.orderOut(nil)
            self.toolbar?.alphaValue = 1
        }
        model.onToolbarSizeChanged = { [weak self] size in self?.resizeToolbar(to: size) }
        model.onToolbarBackgroundDrag = { [weak self] translation, ended in
            guard let self, let panel = self.toolbar as? InspectorPanel,
                  !self.model.visibilityAnimating, let point = panel.pointerEventLocation else { return }
            if !panel.isTrackingToolbarDrag {
                guard !ended else { return }
                panel.beginToolbarDrag(at: CGPoint(x: point.x - translation.width, y: point.y + translation.height),
                                       gestureOwned: true)
            }
            if ended { panel.endToolbarDrag(at: point) }
            else { panel.updateToolbarDrag(to: point) }
        }
        model.onMenuBarVisibilityChanged = { [weak self] visible in
            if !visible { self?.menuPopover?.close() }
            self?.item?.isVisible = visible
        }
        model.onRegionSharingToolbar = { [weak self] in self?.toolbar }
        model.onRegionSharingStateChanged = { [weak self] state in
            if let button = self?.item?.button {
                Self.configureMenuBarButton(button, sharingActive: state.isCapturing)
            }
        }
        model.onToolbarLayoutChanged = { [weak self] in self?.updateToolbarLayout() }
        model.onDisplaysChanged = { [weak self] in
            guard let self else { return }
            (self.toolbar as? InspectorPanel)?.cancelToolbarDrag()
            self.hideDockPreview()
            self.visibility.cancel()
            self.resumeAfterShowing = false
            self.recoverToolbarPlacement()
            if self.model.workspaceHidden { self.toolbar?.orderOut(nil) }
            else { self.toolbar?.orderFrontRegardless() }
        }
        model.onScreenZoomChanged = { [weak self] active in
            let level = PresentationWindowLevels.toolbar(screenZoom: active)
            self?.toolbar?.level = level
            self?.library?.window?.level = level
            self?.refreshZoomActivationMonitor()
        }
        model.onResumeShortcutChanged = { [weak self] value in try self?.replaceResumeShortcut(value) }
        model.onBreakShortcutChanged = { [weak self] value in try self?.replaceBreakShortcut(value) }
        model.onZoomShortcutChanged = { [weak self] value in
            guard let self else { return }
            let previous = self.model.preferences.zoomShortcut ?? .liveZoom
            self.zoomHotkey?.unregister()
            self.zoomHotkey = nil
            self.model.zoomHotkeyRegistered = false
            do { try self.registerZoomShortcut(value) }
            catch {
                let original = error
                do { try self.registerZoomShortcut(previous) } catch { self.model.report(error) }
                throw original
            }
        }
    }

    private func registerResumeShortcut(_ shortcut: ActivationShortcut) throws {
        resumeHotkey = try GlobalHotkey(shortcut: shortcut) { [weak self] in
            guard self?.model.breakTimerRunning == false else { return }
            self?.resumeDrawing()
        }
    }

    private func replaceResumeShortcut(_ shortcut: ActivationShortcut) throws {
        let previous = model.preferences.resumeShortcut ?? .resumeDrawing
        resumeHotkey?.unregister()
        resumeHotkey = nil
        do { try registerResumeShortcut(shortcut) }
        catch {
            let original = error
            do { try registerResumeShortcut(previous) } catch { model.report(error) }
            throw original
        }
    }

    private func registerBreakShortcut(_ shortcut: ActivationShortcut) throws {
        breakHotkey = try GlobalHotkey(shortcut: shortcut) { [weak self] in
            self?.menuPopover?.close()
            self?.model.toggleBreakFromShortcut()
        }
    }

    private func replaceBreakShortcut(_ shortcut: ActivationShortcut) throws {
        let previous = model.preferences.breakShortcut ?? .breakTimer
        breakHotkey?.unregister()
        breakHotkey = nil
        do { try registerBreakShortcut(shortcut) }
        catch {
            let original = error
            do { try registerBreakShortcut(previous) } catch { model.report(error) }
            throw original
        }
    }

    private func registerZoomShortcut(_ shortcut: ActivationShortcut) throws {
        zoomHotkey = try GlobalHotkey(shortcut: shortcut) { [weak self] in
            guard self?.model.breakTimerRunning == false else { return }
            self?.menuPopover?.close()
            self?.model.toggleScreenZoomFromShortcut()
        }
        model.zoomHotkeyRegistered = true
    }

    func applicationWillTerminate(_ notification: Notification) {
        introduction?.close()
        introduction = nil
        model.onShowIntroduction = nil
        errorController?.close()
        hideDockPreview()
        errorController = nil
        menuPopover?.close()
        menuPopover = nil
        model.onDismissMenuBar = nil
        visibility.cancel()
        library?.close()
        library = nil
        toolbar?.orderOut(nil)
        toolbar?.contentView = nil
        toolbar?.close()
        toolbar = nil
        stopZoomActivationMonitor()
        model.closeAll()
        hotkey?.unregister()
        hideHotkey?.unregister()
        zoomHotkey?.unregister()
        model.zoomHotkeyRegistered = false
        resumeHotkey?.unregister()
        breakHotkey?.unregister()
        observers.forEach(NSWorkspace.shared.notificationCenter.removeObserver)
        if let lockObserver { DistributedNotificationCenter.default().removeObserver(lockObserver) }
        if let zoomPermissionObserver { NotificationCenter.default.removeObserver(zoomPermissionObserver) }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        .terminateNow
    }

    @objc private func toggleMenuPopover() {
        guard let button = item?.button else { return }
        if menuPopover == nil {
            menuPopover = MenuBarPopoverController(model: model,
                toolsVisible: { [weak self] in self?.drawingToolsArePresented ?? false }) { [weak self] action in
                self?.performMenuAction(action)
            }
        }
        menuPopover?.toggle(relativeTo: button)
    }

    func performMenuAction(_ action: MenuBarAction) {
        switch action {
        case .showTools: openDrawing()
        case .hideTools: hideTools()
        case .draw: resumeDrawing()
        case .useApps:
            resumeAfterShowing = false
            visibility.cancelDrawingRequest()
            model.interact()
        case .stopZoom: model.stopZoom()
        case .toggleRegionSharing: model.regionSharing.toggle()
        case .shareLeftHalf: model.regionSharing.share(preset: .leftHalf)
        case .shareRightHalf: model.regionSharing.share(preset: .rightHalf)
        case .shareRegion: model.openRegionSharing()
        case .startBreak, .breakFive, .breakTen, .breakFifteen, .breakThirty:
            menuPopover?.close()
            model.startBreak(minutes: action.breakMinutes)
        case .openBoard: model.openBoard()
        case .saveBoard: model.saveBoard()
        case .exportPNG: model.exportPNG()
        case .settings: model.openPreferences()
        case .help: model.openPreferences(tab: .help)
        case .about: model.openPreferences(tab: .about)
        case .checkForUpdates: model.checkForUpdates()
        case .support: model.openOptionalSupport()
        case .quit: NSApp.terminate(nil)
        }
    }

    @objc private func openDrawing() {
        if model.workspaceHidden || model.visibilityAnimating { resumeDrawing() }
        else { model.beginDrawing(); showToolbar() }
    }
    func resumeDrawing() {
        menuPopover?.close()
        if model.workspaceHidden || model.visibilityAnimating {
            resumeAfterShowing = true
            if !model.visibilityAnimating || !visibility.targetVisible { showToolbar() }
            return
        }
        if model.mode != .draw { model.beginDrawing() }
        showToolbar()
    }
    private func registerShortcut(_ shortcut: ActivationShortcut) throws {
        hotkey = try GlobalHotkey(shortcut: shortcut) { [weak self] in
            guard self?.model.breakTimerRunning == false else { return }
            self?.activateTools()
        }
        model.hotkeyRegistered = true
        model.status = "Ready. \(shortcut.label) opens drawing or hides the tools."
    }

    private func replaceShortcut(_ shortcut: ActivationShortcut) throws {
        let previous = model.preferences.activationShortcut
        hotkey?.unregister()
        hotkey = nil; model.hotkeyRegistered = false
        do { try registerShortcut(shortcut) }
        catch {
            let registrationError = error
            do { try registerShortcut(previous) }
            catch { model.setMenuBarVisible(true); model.report(error) }
            throw registrationError
        }

    }

    func hideTools() {
        (toolbar as? InspectorPanel)?.cancelToolbarDrag()
        menuPopover?.close()
        resumeAfterShowing = false
        stopZoomActivationMonitor()
        library?.hide()
        guard let toolbar else { model.interact(); model.concealWorkspaceSurfaces(); return }
        visibility.hide(toolbar: toolbar, model: model) { [weak self] in self?.applyPendingToolbarSize() }
    }

    private func registerHideShortcut(_ shortcut: ActivationShortcut) throws {
        hideHotkey = try GlobalHotkey(shortcut: shortcut) { [weak self] in
            guard self?.model.breakTimerRunning == false else { return }
            self?.hideTools()
        }
    }

    private func replaceHideShortcut(_ shortcut: ActivationShortcut) throws {
        let prior = model.preferences.hideShortcut ?? .hideTools
        hideHotkey?.unregister(); hideHotkey = nil
        do { try registerHideShortcut(shortcut) }
        catch {
            let original = error
            do { try registerHideShortcut(prior) } catch { model.report(error) }
            throw original
        }
    }

    private func updateToolbarLayout() {
        guard let toolbar else { return }
        toolbar.contentView?.invalidateIntrinsicContentSize()
        toolbar.contentView?.needsLayout = true
        dockToolbar(toolbar, size: toolbar.frame.size)
    }

    private func dockToolbar(_ toolbar: NSPanel, size: CGSize) {
        guard let screen = toolbarDockScreen(for: toolbar) else { return }
        let frame = ToolbarPlacement.frame(size: size, visibleFrame: screen.visibleFrame,
                                            orientation: model.preferences.toolbarOrientation ?? .horizontal,
                                            edge: model.preferences.toolbarDockEdge ?? .automatic,
                                            inset: edgeDocked ? 0 : 12)
        toolbar.setFrame(frame, display: true)
        dockedFrame = frame
    }

    private func toolbarDockScreen(for panel: NSPanel) -> NSScreen? {
        if let toolbarDockDisplayID, let target = NSScreen.screens.first(where: {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == toolbarDockDisplayID
        }) { return target }
        return NSScreen.screens.first(where: { $0.frame.intersects(panel.frame) }) ?? NSScreen.screens.first
    }

    func recoverToolbarPlacement(visibleFrames: [CGRect]? = nil) {
        guard let toolbar else { return }
        let frames = visibleFrames ?? NSScreen.screens.map(\.visibleFrame)
        guard !frames.contains(where: { $0.contains(toolbar.frame) }), let target =
            frames.first(where: { $0.intersects(toolbar.frame) }) ?? frames.first else { return }
        let frame = ToolbarPlacement.constrainedDragFrame(
            CGRect(origin: toolbar.frame.origin,
                   size: CGSize(width: min(toolbar.frame.width, target.width),
                                height: min(toolbar.frame.height, target.height))),
            visibleFrame: target)
        toolbarDockDisplayID = nil
        dockedFrame = nil
        edgeDocked = false
        pendingToolbarSize = nil
        toolbar.contentView = ToolbarContentView(model: model,
            availableSize: CGSize(width: target.width - 32, height: target.height - 64))
        toolbar.setFrame(frame, display: true)
    }

    func finishToolbarDrag(at point: CGPoint) {
        updateDockPreview(at: point)
        let targetFrame = dockPreviewFrame
        let targetEdge = previewEdge
        let targetScreen = previewScreen
        hideDockPreview()
        guard let toolbar, !model.visibilityAnimating, !model.workspaceHidden,
              let screen = NSScreen.screens.first(where: { $0.visibleFrame == targetScreen }) ??
                NSScreen.screens.first(where: { $0.frame.contains(point) }) ?? toolbar.screen else { return }
        let available = CGSize(width: screen.visibleFrame.width - 32, height: screen.visibleFrame.height - 64)
        if let edge = targetEdge, let targetFrame {
            toolbarDockDisplayID = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
            edgeDocked = true
            pendingToolbarSize = nil
            model.dockToolbar(to: edge)
            toolbar.contentView = ToolbarContentView(model: model, availableSize: available)
            toolbar.setFrame(targetFrame, display: true)
            dockedFrame = targetFrame
            toolbar.contentView?.layoutSubtreeIfNeeded()
        } else {
            edgeDocked = false
            toolbarDockDisplayID = nil
            dockedFrame = nil
            if let content = toolbar.contentView as? ToolbarContentView,
               content.host.rootView.availableSize != available {
                content.host.rootView = ToolbarView(model: model, availableSize: available)
            }
        }
        applyPendingToolbarSize()
    }

    private func dockingSize(on screen: NSScreen, edge: ToolbarDockEdge) -> CGSize {
        let host = NSHostingView(rootView: ToolbarView(model: model,
            availableSize: CGSize(width: screen.visibleFrame.width - 32, height: screen.visibleFrame.height - 64),
            orientationOverride: edge == .left || edge == .right ? .vertical : .horizontal,
            dockEdgeOverride: edge))
        return host.fittingSize
    }

    func updateDockPreview(at point: CGPoint) {
        guard let toolbar, !model.visibilityAnimating, !model.workspaceHidden,
              let screen = NSScreen.screens.first(where: { $0.frame.insetBy(dx: -1, dy: -1).contains(point) }),
              let edge = ToolbarPlacement.dockEdge(at: point, visibleFrame: screen.frame) else {
            hideDockPreview()
            return
        }
        let size = dockPreview != nil && previewEdge == edge && previewScreen == screen.visibleFrame
            ? dockPreviewFrame?.size ?? dockingSize(on: screen, edge: edge) : dockingSize(on: screen, edge: edge)
        let frame = ToolbarPlacement.frame(size: size,
            visibleFrame: screen.visibleFrame, orientation: edge == .left || edge == .right ? .vertical : .horizontal,
            edge: edge, inset: 0)
        if dockPreviewFrame == frame { return }
        if dockPreview == nil {
            let panel = DockPreviewPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            let view = NSView()
            view.wantsLayer = true
            view.layer?.backgroundColor = NSColor.controlAccentColor.withAlphaComponent(0.18).cgColor
            view.layer?.borderColor = NSColor.controlAccentColor.withAlphaComponent(0.8).cgColor
            view.layer?.borderWidth = 2
            view.layer?.cornerRadius = 12
            panel.contentView = view
            dockPreview = panel
        }
        dockPreview?.level = NSWindow.Level(rawValue: toolbar.level.rawValue + 1)
        dockPreview?.setFrame(frame, display: true)
        dockPreview?.orderFrontRegardless()
        dockPreviewFrame = frame
        previewEdge = edge
        previewScreen = screen.visibleFrame
    }

    private func hideDockPreview() {
        dockPreview?.orderOut(nil)
        dockPreview?.close()
        dockPreview = nil
        dockPreviewFrame = nil
        previewEdge = nil
        previewScreen = nil
    }

    private func showToolbar() {
        if model.visibilityAnimating && visibility.targetVisible { return }
        if let toolbar {
            recoverToolbarPlacement()
            if model.workspaceHidden || model.visibilityAnimating {
                visibility.show(toolbar: toolbar, model: model, drawing: resumeAfterShowing) { [weak self] in
                    self?.finishShowingToolbar()
                }
            } else {
                toolbar.orderFrontRegardless()
                refreshZoomActivationMonitor()
            }
            return
        }
        let screen = NSScreen.screens.first(where: { $0.frame.contains(NSEvent.mouseLocation) }) ?? NSScreen.main
        let available = screen.map { CGSize(width: $0.visibleFrame.width - 32, height: $0.visibleFrame.height - 64) }
            ?? CGSize(width: 1000, height: 800)
        let panel = InspectorPanel(contentRect: NSRect(x: 0, y: 0, width: min(1000, available.width), height: 130),
                                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Annotator Drawing Tools"
        panel.onUserDragEnded = { [weak self] point in self?.finishToolbarDrag(at: point) }
        panel.onUserDragMoved = { [weak self] point in self?.updateDockPreview(at: point) }
        panel.onUserDragCancelled = { [weak self] in self?.hideDockPreview() }
        panel.onUserDragStarted = { [weak self] in
            self?.dockedFrame = nil
            self?.toolbarDockDisplayID = nil
            self?.edgeDocked = false
        }
        panel.imagePasteModel = model
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.acceptsMouseMovedEvents = true
        panel.isOpaque = false
        panel.isMovable = false
        panel.isMovableByWindowBackground = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = PresentationWindowLevels.toolbar(screenZoom: model.screenZoomEnabled)
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = ToolbarContentView(model: model, availableSize: available)
        panel.isReleasedWhenClosed = false
        if let screen {
            toolbarDockDisplayID = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
            let frame = ToolbarPlacement.frame(size: panel.frame.size, visibleFrame: screen.visibleFrame,
                                                orientation: model.preferences.toolbarOrientation ?? .horizontal,
                                                edge: model.preferences.toolbarDockEdge ?? .automatic)
            panel.setFrame(frame, display: true)
            dockedFrame = frame
        }
        toolbar = panel
        if model.workspaceHidden {
            visibility.show(toolbar: panel, model: model, drawing: resumeAfterShowing) { [weak self] in
                self?.finishShowingToolbar()
            }
        } else {
            panel.orderFrontRegardless()
            refreshZoomActivationMonitor()
        }
    }

    private func finishShowingToolbar() {
        let resume = resumeAfterShowing
        resumeAfterShowing = false
        if resume && model.mode != .draw { model.beginDrawing() }
        applyPendingToolbarSize()
        refreshZoomActivationMonitor()
    }

    private func refreshZoomActivationMonitor() {
        let shouldMonitor = ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: toolbarIsVisible,
            workspaceHidden: model.workspaceHidden,
            visibilityAnimating: model.visibilityAnimating,
            screenZoomEnabled: model.screenZoomEnabled,
            permissionGranted: model.zoomScrollPermissionGranted
        )
        guard shouldMonitor else {
            stopZoomActivationMonitor()
            return
        }
        guard zoomActivationMonitor == nil else { return }
        let monitor = ZoomGestureMonitor(
            scroll: { [weak self] delta in
                Task { @MainActor [weak self] in self?.activateScreenZoomFromOptionScroll(delta) }
            },
            exit: {},
            failure: { [weak self] error in
                self?.zoomActivationMonitor = nil
                self?.model.noteZoomGestures(available: false, error: error)
            },
            capturesEscape: false,
            shouldHandleScroll: ToolbarZoomActivation.shouldHandleScroll
        )
        do {
            try monitor.start()
            zoomActivationMonitor = monitor
        } catch ZoomInputFailure.testInputDisabled {
            return
        } catch {
            model.noteZoomGestures(available: false, error: error)
        }
    }

    private func activateScreenZoomFromOptionScroll(_ delta: Double) {
        guard ToolbarZoomActivation.shouldMonitor(
            toolbarVisible: toolbarIsVisible,
            workspaceHidden: model.workspaceHidden,
            visibilityAnimating: model.visibilityAnimating,
            screenZoomEnabled: model.screenZoomEnabled,
            permissionGranted: model.zoomScrollPermissionGranted
        ) else { return }
        model.startScreenZoomFromOptionScroll(delta)
    }

    private func stopZoomActivationMonitor() {
        zoomActivationMonitor?.stop()
        zoomActivationMonitor = nil
    }

    private func resizeToolbar(to size: CGSize) {
        guard let toolbar, size.width.isFinite, size.height.isFinite, size.width > 0, size.height > 0 else { return }
        if model.visibilityAnimating || (toolbar as? InspectorPanel)?.isTrackingToolbarDrag == true {
            pendingToolbarSize = size
            return
        }
        let screen = toolbarDockScreen(for: toolbar)
        let old = toolbar.frame
        if edgeDocked { dockToolbar(toolbar, size: size); return }
        // Hosting can resize the window around its top-left before this callback.
        // That is still a docked window, even when its new size already matches.
        if let dockedFrame, abs(old.minX - dockedFrame.minX) < 1,
           abs(old.minY - dockedFrame.minY) < 1 || abs(old.maxY - dockedFrame.maxY) < 1 {
            dockToolbar(toolbar, size: size)
            return
        }
        var frame = CGRect(x: old.minX, y: old.maxY - size.height, width: size.width, height: size.height)
        if let bounds = screen?.visibleFrame {
            frame.origin.x = max(bounds.minX, min(frame.minX, bounds.maxX - frame.width))
            frame.origin.y = max(bounds.minY, min(frame.minY, bounds.maxY - frame.height))
        }
        if toolbar.frame != frame { toolbar.setFrame(frame, display: true, animate: false) }
    }

    private func applyPendingToolbarSize() {
        guard let size = pendingToolbarSize else { return }
        pendingToolbarSize = nil
        resizeToolbar(to: size)
    }
}

@MainActor
enum ToolbarZoomActivation {
    static func shouldMonitor(toolbarVisible: Bool, workspaceHidden: Bool, visibilityAnimating: Bool,
                              screenZoomEnabled: Bool, permissionGranted: Bool) -> Bool {
        toolbarVisible && !workspaceHidden && !visibilityAnimating && !screenZoomEnabled && permissionGranted
    }

    static func shouldHandleScroll(_ event: CGEvent) -> Bool {
        guard let delta = ZoomGestureMonitor.zoomScrollDelta(event) else { return false }
        return delta > 0
    }
}

private final class DockPreviewPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

final class InspectorPanel: AnnotationPanel {
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
    var onUserDragMoved: ((CGPoint) -> Void)?
    var onUserDragCancelled: (() -> Void)?
    var onUserDragStarted: (() -> Void)?
    var onUserDragEnded: ((CGPoint) -> Void)?
    private var dragOrigin: CGPoint?
    private var dragFrame: CGRect?
    private var gestureOwnsDrag = false
    private(set) var pointerEventLocation: CGPoint?
    var isTrackingToolbarDrag: Bool { dragOrigin != nil }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func performDrag(with event: NSEvent) {
        beginToolbarDrag(at: convertPoint(toScreen: event.locationInWindow))
    }

    func beginToolbarDrag(at point: CGPoint, gestureOwned: Bool = false) {
        guard !isTrackingToolbarDrag, onUserDragEnded != nil else { return }
        dragOrigin = point
        dragFrame = frame
        gestureOwnsDrag = gestureOwned
        onUserDragStarted?()
    }

    func updateToolbarDrag(to point: CGPoint) {
        guard let dragOrigin, let dragFrame else { return }
        var proposed = dragFrame.offsetBy(dx: point.x - dragOrigin.x, dy: point.y - dragOrigin.y)
        if let destination = NSScreen.screens.first(where: { $0.frame.contains(point) }) ?? screen {
            proposed = ToolbarPlacement.constrainedDragFrame(proposed, visibleFrame: destination.visibleFrame)
        }
        setFrameOrigin(proposed.origin)
        onUserDragMoved?(point)
    }

    func endToolbarDrag(at point: CGPoint) {
        guard let original = dragFrame else { return }
        updateToolbarDrag(to: point)
        dragOrigin = nil
        dragFrame = nil
        gestureOwnsDrag = false
        if frame != original { onUserDragEnded?(point) }
        else { onUserDragCancelled?() }
    }

    override func sendEvent(_ event: NSEvent) {
        if [.leftMouseDown, .leftMouseDragged, .leftMouseUp].contains(event.type) {
            pointerEventLocation = convertPoint(toScreen: event.locationInWindow)
        }
        if isTrackingToolbarDrag && !gestureOwnsDrag {
            switch event.type {
            case .leftMouseDragged:
                updateToolbarDrag(to: convertPoint(toScreen: event.locationInWindow))
                return
            case .leftMouseUp:
                endToolbarDrag(at: convertPoint(toScreen: event.locationInWindow))
                return
            case .keyDown where event.keyCode == 53:
                cancelToolbarDrag()
                return
            default: break
            }
        }
        super.sendEvent(event)
    }

    override func orderOut(_ sender: Any?) {
        cancelToolbarDrag()
        super.orderOut(sender)
    }

    func cancelToolbarDrag() {
        dragOrigin = nil
        dragFrame = nil
        gestureOwnsDrag = false
        onUserDragCancelled?()
    }

    override func close() {
        cancelToolbarDrag()
        onUserDragEnded = nil
        super.close()
    }
}

final class MovableToolbarHost: NSHostingView<ToolbarView> {
    private var cursorTracking: NSTrackingArea?
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }

    override func resetCursorRects() {
        super.resetCursorRects()
        addCursorRect(bounds, cursor: .arrow)
    }

    override func updateTrackingAreas() {
        if let cursorTracking { removeTrackingArea(cursorTracking) }
        let tracking = NSTrackingArea(rect: .zero,
            options: [.inVisibleRect, .activeAlways, .cursorUpdate, .mouseEnteredAndExited], owner: self)
        addTrackingArea(tracking)
        cursorTracking = tracking
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) { NSCursor.arrow.set() }
    override func cursorUpdate(with event: NSEvent) { NSCursor.arrow.set() }
}

// Window geometry belongs to the controller, not the hosting view's sizing constraints.
final class ToolbarContentView: NSView {
    let host: MovableToolbarHost

    init(model: WorkspaceModel, availableSize: CGSize) {
        host = MovableToolbarHost(rootView: ToolbarView(model: model, availableSize: availableSize))
        host.sizingOptions = []
        super.init(frame: .zero)
        addSubview(host)
        host.autoresizingMask = [.width, .height]
    }

    required init?(coder: NSCoder) { nil }
    override var mouseDownCanMoveWindow: Bool { false }
    override func layout() {
        super.layout()
        host.frame = bounds
        host.layoutSubtreeIfNeeded()
    }
    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        host.frame = bounds
        needsLayout = true
    }
}

struct SettingsWindowSurface: NSViewRepresentable {
    func makeNSView(context: Context) -> SettingsSurfaceView { SettingsSurfaceView() }
    func updateNSView(_ view: SettingsSurfaceView, context: Context) {}
}

final class SettingsSurfaceView: NSView {
    static let minimumContentSize = NSSize(width: 720, height: 620)

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        window?.level = PresentationWindowLevels.toolbar(screenZoom: false)
        allowResizing()
    }

    override func layout() {
        super.layout()
        allowResizing()
    }

    // SwiftUI's Settings scene opens a fixed-size window and ignores windowResizability, so add the resize edges here.
    private func allowResizing() {
        guard let window, !window.styleMask.contains(.resizable) else { return }
        window.styleMask.insert(.resizable)
        window.contentMinSize = Self.minimumContentSize
    }
}
