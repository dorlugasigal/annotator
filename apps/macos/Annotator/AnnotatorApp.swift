import AppKit
import Combine
import SwiftUI
import AnnotationCore
import MotionCore
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
        .defaultSize(SettingsSurfaceView.defaultContentSize)
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
    private let frameAnimator = ToolbarFrameAnimator()
    /// A collapse or expansion waiting for the new face's measured size.
    private var pendingMorph: PendingToolbarMorph?
    private var morphing = false
    private var openMenus = 0
    private var tutorialSurface: AnyCancellable?
    private(set) lazy var collapse = ToolbarCollapseController(model: model)
    /// Where the pointer is, for deciding whether the toolbar may shrink. Tests move it away.
    var pointerLocation: @MainActor () -> CGPoint = { NSEvent.mouseLocation }
    var toolbarIsAnimating: Bool { frameAnimator.isRunning || pendingMorph != nil || morphing }
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
    private(set) var tutorial: TutorialController?
    private(set) var whatsChanged: WhatsChangedController?

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
        // Before Sparkle starts, which marks every launch: is this the first launch after an update?
        whatsChanged?.prepare(build: (Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String).flatMap { Int($0) })
        model.usageMetrics.recordLaunch()
        model.onThemeChanged = { theme in Self.applyTheme(theme) }
        Self.applyTheme(model.preferences.theme ?? .system)
        model.loginItemController = LoginItemController { [weak model] error in model?.report(error) }
        let updates = AppUpdateController(changed: { [weak model] available, automatic, message in
            model?.canCheckForUpdates = available
            model?.automaticallyCheckForUpdates = automatic
            model?.updateStatus = message
        }, reminder: { [weak model] version in
            model?.updateReminderVersion = version
        }, canInterrupt: { [weak model] in
            model?.canShowAnnouncement ?? false
        })
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
        tutorial?.showAutomatically(isLoginItem: isLoginItem)
        whatsChanged?.start(isLoginItem: isLoginItem)
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
        tutorial = TutorialController(model: model,
                                      presented: { [weak self] in self?.drawingToolsArePresented ?? false },
                                      toolbarWindow: { [weak self] in self?.toolbarWindow },
                                      startDrawing: { [weak self] in self?.resumeDrawing() })
        whatsChanged = WhatsChangedController(model: model)
        model.onShowIntroduction = { [weak self] in self?.tutorial?.showManually() }
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
            self.finishToolbarAnimations()
            self.collapse.cancel()
            self.visibility.cancel()
            self.model.visibilityAnimating = false
            self.applyPendingToolbarSize()
            if self.model.workspaceHidden { self.toolbar?.orderOut(nil); self.model.setToolbarCollapsed(false) }
        }
        model.onFocusToolbar = { [weak self] in
            guard let self else { return }
            self.showToolbar()
            self.expandToolbar()
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
            self.finishToolbarAnimations()
            self.collapse.cancel()
            self.visibility.cancel()
            self.model.visibilityAnimating = false
            self.applyPendingToolbarSize()
            self.toolbar?.orderOut(nil)
            self.toolbar?.alphaValue = 1
            self.model.setToolbarCollapsed(false)
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
        model.onExpandToolbarRequested = { [weak self] in self?.expandToolbar() }
        model.onToolbarCollapseSettingChanged = { [weak self] in self?.collapse.restart() }
        collapse.isHeldOpen = { [weak self] in self?.toolbarMustStayOpen ?? true }
        collapse.collapse = { [weak self] in self?.collapseToolbar() }
        collapse.expand = { [weak self] in self?.expandToolbar() }
        for (name, change) in [(NSMenu.didBeginTrackingNotification, 1), (NSMenu.didEndTrackingNotification, -1)] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.openMenus = max(0, self.openMenus + change)
                    if self.openMenus == 0 { self.collapse.restart() }
                }
            })
        }
        tutorialSurface = tutorial?.$surface.sink { [weak self] surface in
            guard surface != .hidden else { return }
            Task { @MainActor [weak self] in self?.expandToolbar() }
        }
        model.onDisplaysChanged = { [weak self] in
            guard let self else { return }
            (self.toolbar as? InspectorPanel)?.cancelToolbarDrag()
            self.hideDockPreview()
            self.finishToolbarAnimations()
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
        tutorial?.shutdown()
        tutorial = nil
        whatsChanged?.close()
        whatsChanged = nil
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
        whatsChanged?.personOpenedAnnotator()
        if menuPopover == nil {
            menuPopover = MenuBarPopoverController(model: model,
                toolsVisible: { [weak self] in self?.drawingToolsArePresented ?? false }) { [weak self] action in
                self?.performMenuAction(action)
            }
        }
        menuPopover?.toggle(relativeTo: button)
        if menuPopover?.popover.isShown == true { updates?.checkWhenMenuOpens() }
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
        finishToolbarAnimations()
        collapse.cancel()
        guard let toolbar else { model.interact(); model.concealWorkspaceSurfaces(); return }
        visibility.hide(toolbar: toolbar, model: model) { [weak self] in
            guard let self else { return }
            // The tools come back open next time; the one button returns after the delay.
            if self.model.workspaceHidden { self.model.setToolbarCollapsed(false) }
            self.applyPendingToolbarSize()
        }
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
        finishToolbarAnimations()
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
        finishToolbarAnimations()
        toolbar.contentView = makeToolbarContent(availableSize: CGSize(width: target.width - 32, height: target.height - 64))
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
            toolbar.contentView = makeToolbarContent(availableSize: available)
            toolbar.setFrame(targetFrame, display: true)
            dockedFrame = targetFrame
            toolbar.contentView?.layoutSubtreeIfNeeded()
        } else {
            edgeDocked = false
            toolbarDockDisplayID = nil
            dockedFrame = nil
            if let content = toolbar.contentView as? ToolbarContentView,
               content.host.rootView.availableSize != available {
                content.host.rootView = ToolbarContentView.toolbar(model: model, availableSize: available)
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
        whatsChanged?.personOpenedAnnotator()
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
                collapse.restart()
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
        panel.onUserDragWillStart = { [weak self] in self?.finishToolbarAnimations() }
        panel.onUserDragStarted = { [weak self] in
            self?.dockedFrame = nil
            self?.toolbarDockDisplayID = nil
            self?.edgeDocked = false
        }
        panel.allowsHoldToMove = { [weak self] in
            guard let self else { return false }
            return !self.model.visibilityAnimating && !self.model.workspaceHidden
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
        panel.contentView = makeToolbarContent(availableSize: available)
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
            collapse.restart()
        }
    }

    private func makeToolbarContent(availableSize: CGSize) -> ToolbarContentView {
        let content = ToolbarContentView(model: model, availableSize: availableSize)
        content.onPointerInside = { [weak self] inside in self?.collapse.pointerChanged(inside: inside) }
        return content
    }

    private func finishShowingToolbar() {
        let resume = resumeAfterShowing
        resumeAfterShowing = false
        if resume && model.mode != .draw { model.beginDrawing() }
        applyPendingToolbarSize()
        refreshZoomActivationMonitor()
        collapse.restart()
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
        if let morph = pendingMorph {
            pendingMorph = nil
            morph.deadline.cancel()
            runToolbarMorph(morph, size: size)
            return
        }
        if morphing {
            pendingToolbarSize = size
            return
        }
        // While the window animates, it is already on its way to the animation's frame.
        let old = frameAnimator.target ?? toolbar.frame
        let frame = toolbarFrame(size: size, from: old, pin: .topLeading)
        let content = toolbar.contentView as? ToolbarContentView
        // A horizontal toolbar grows or shrinks sideways in a short animation when a tool's properties need more or less room.
        let animated = !Motion.systemReducesMotion && toolbar.isVisible && !model.workspaceHidden && !model.toolbarCollapsed &&
            model.preferences.toolbarOrientation != .vertical && abs(frame.height - old.height) < 1 && abs(frame.width - old.width) >= 1
        if animated {
            // The toolbar fills the window on every frame, so its buttons stay at the window's edges.
            content?.contentSize = nil
            frameAnimator.animate(toolbar, to: frame, duration: MotionTiming.toolbarResizeDuration)
        } else {
            frameAnimator.finishNow(nil)
            content?.contentSize = nil
            if toolbar.frame != frame { toolbar.setFrame(frame, display: true, animate: false) }
        }
    }

    /// The frame for a toolbar of `size`: docked frames stay docked; a floating toolbar keeps the `pin` point of `old` and
    /// stays on its screen.
    private func toolbarFrame(size: CGSize, from old: CGRect, pin: ToolbarPin) -> CGRect {
        guard let toolbar else { return CGRect(origin: old.origin, size: size) }
        // Hosting can resize the window around its top-left before this callback.
        // That is still a docked window, even when its new size already matches.
        let docked = edgeDocked || dockedFrame.map { dockedFrame in
            abs(old.minX - dockedFrame.minX) < 1 && (abs(old.minY - dockedFrame.minY) < 1 || abs(old.maxY - dockedFrame.maxY) < 1)
        } == true
        if docked, let screen = toolbarDockScreen(for: toolbar) {
            let frame = ToolbarPlacement.frame(size: size, visibleFrame: screen.visibleFrame,
                                                orientation: model.preferences.toolbarOrientation ?? .horizontal,
                                                edge: model.preferences.toolbarDockEdge ?? .automatic,
                                                inset: edgeDocked ? 0 : 12)
            dockedFrame = frame
            return frame
        }
        var frame = pin.frame(size: size, keeping: old)
        if let bounds = toolbarDockScreen(for: toolbar)?.visibleFrame {
            frame.origin.x = max(bounds.minX, min(frame.minX, bounds.maxX - frame.width))
            frame.origin.y = max(bounds.minY, min(frame.minY, bounds.maxY - frame.height))
        }
        return frame
    }

    /// Where the toolbar's content stays while the window changes: the docked edge's middle, or a floating toolbar's
    /// top-left corner.
    private var toolbarPin: ToolbarPin {
        guard let toolbar else { return .topLeading }
        let frame = frameAnimator.target ?? toolbar.frame
        let docked = edgeDocked || dockedFrame.map { abs(frame.minX - $0.minX) < 1 && abs(frame.minY - $0.minY) < 1 } == true
        guard docked else { return .topLeading }
        return .docked(orientation: model.preferences.toolbarOrientation ?? .horizontal,
                       edge: model.preferences.toolbarDockEdge ?? .automatic)
    }

    /// Ends any resize or morph at once, for example before a drag, a hide or a new layout.
    private func finishToolbarAnimations() {
        let content = toolbar?.contentView as? ToolbarContentView
        if let morph = pendingMorph {
            pendingMorph = nil
            morph.deadline.cancel()
            content?.endMorph()
            content?.contentSize = nil
        }
        frameAnimator.finishNow(toolbar)
    }

    /// Reasons the toolbar stays open: the pointer, a menu or popover, a drag, typing, the tutorial or a pending step.
    private var toolbarMustStayOpen: Bool {
        guard let toolbar, toolbar.isVisible, !model.workspaceHidden, !model.visibilityAnimating else { return true }
        if (toolbar as? InspectorPanel)?.isTrackingToolbarDrag == true || openMenus > 0 || morphing || pendingMorph != nil {
            return true
        }
        let pointer = pointerLocation()
        if toolbar.frame.contains(pointer) { return true }
        if let surface = tutorial?.surface, surface != .hidden { return true }
        if model.isConstructingArrow || PendingZoomPresentation(state: model.zoomStartState) != nil { return true }
        if toolbar.isKeyWindow, toolbar.firstResponder is NSText { return true }
        if let window = library?.window, window.isVisible, window.frame.contains(pointer) { return true }
        return NSApp.windows.contains { $0.isVisible && $0 !== toolbar && NSStringFromClass(type(of: $0)).contains("Popover") }
    }

    private func collapseToolbar() {
        guard model.toolbarCollapseDelay != nil, !model.toolbarCollapsed, !toolbarMustStayOpen else { return }
        beginToolbarMorph(collapsing: true)
    }

    private func expandToolbar() {
        guard model.toolbarCollapsed else { return }
        guard let toolbar, toolbar.isVisible, !model.workspaceHidden, !model.visibilityAnimating else {
            model.setToolbarCollapsed(false)
            return
        }
        beginToolbarMorph(collapsing: false)
    }

    /// Covers the toolbar with a picture of its face, then switches to the other face; the morph starts when the new face
    /// reports its size (`runToolbarMorph`).
    private func beginToolbarMorph(collapsing: Bool) {
        guard let toolbar, let content = toolbar.contentView as? ToolbarContentView else {
            model.setToolbarCollapsed(collapsing)
            return
        }
        finishToolbarAnimations()
        let vertical = model.preferences.toolbarOrientation == .vertical
        let metrics = ToolbarMetrics(model.toolbarSize)
        let pill = CollapsedToolbarButton.size(vertical: vertical, toolbarSize: model.toolbarSize)
        let pillRadius = min(pill.width, pill.height) / 2
        let pin = toolbarPin
        content.beginMorph(outgoing: content.snapshotHost(), outgoingSize: content.host.frame.size, pin: pin,
                           radii: collapsing ? (metrics.cornerRadius, pillRadius) : (pillRadius, metrics.cornerRadius),
                           chromeFade: vertical ? (collapsing ? (0, 1, 0, 0.35) : (1, 0, 0.65, 1)) : nil)
        let deadline = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled, let self, self.pendingMorph != nil else { return }
            // The new face kept the same size, so there is nothing to move.
            self.pendingMorph = nil
            content.endMorph()
            if !collapsing { self.collapse.restart() }
        }
        pendingMorph = PendingToolbarMorph(start: toolbar.frame, pin: pin, collapsing: collapsing, deadline: deadline)
        model.setToolbarCollapsed(collapsing)
    }

    private func runToolbarMorph(_ morph: PendingToolbarMorph, size: CGSize) {
        guard let toolbar, let content = toolbar.contentView as? ToolbarContentView else { return }
        let target = toolbarFrame(size: size, from: morph.start, pin: morph.pin)
        let reduced = Motion.systemReducesMotion
        // Reduce Motion: the window changes at once and the faces only cross-fade.
        if reduced { toolbar.setFrame(target, display: true) }
        morphing = true
        frameAnimator.animate(toolbar, to: target,
                              duration: reduced ? MotionTiming.reduced.duration :
                                morph.collapsing ? MotionTiming.toolbarCollapseDuration : MotionTiming.toolbarExpandDuration,
                              step: { [weak content] time in content?.updateMorph(time) },
                              completion: { [weak self, weak content] in
            content?.endMorph()
            content?.contentSize = nil
            content?.pin = .topLeading
            guard let self else { return }
            self.morphing = false
            self.applyPendingToolbarSize()
            if !morph.collapsing { self.collapse.restart() }
        })
        content.pin = morph.pin
        content.contentSize = size
    }

    private func applyPendingToolbarSize() {
        guard let size = pendingToolbarSize else { return }
        pendingToolbarSize = nil
        resizeToolbar(to: size)
    }
}

/// A collapse or expansion that has its starting picture and waits for the new face's size.
private struct PendingToolbarMorph {
    let start: CGRect
    let pin: ToolbarPin
    let collapsing: Bool
    let deadline: Task<Void, Never>
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
    /// Runs before the drag records the frame, so an animation can finish first.
    var onUserDragWillStart: (() -> Void)?
    var onUserDragEnded: ((CGPoint) -> Void)?
    /// Whether a press and hold can pick the toolbar up now (`ToolbarHold`). `nil` turns press and hold off.
    var allowsHoldToMove: (() -> Bool)?
    /// Whether the left mouse button is still down when the hold ends; tests replace it.
    var leftButtonIsDown: () -> Bool = { NSEvent.pressedMouseButtons & 1 != 0 }
    private var dragOrigin: CGPoint?
    private var dragFrame: CGRect?
    private var gestureOwnsDrag = false
    /// A press held back while it might become a press and hold that moves the toolbar.
    private var heldPress: NSEvent?
    private var holdTimer: Timer?
    private(set) var pointerEventLocation: CGPoint?
    var isTrackingToolbarDrag: Bool { dragOrigin != nil }
    var isHoldingPress: Bool { heldPress != nil }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func performDrag(with event: NSEvent) {
        beginToolbarDrag(at: convertPoint(toScreen: event.locationInWindow))
    }

    func beginToolbarDrag(at point: CGPoint, gestureOwned: Bool = false) {
        guard !isTrackingToolbarDrag, onUserDragEnded != nil else { return }
        onUserDragWillStart?()
        dragOrigin = point
        dragFrame = frame
        gestureOwnsDrag = gestureOwned
        onUserDragStarted?()
        NSCursor.closedHand.set()
    }

    func updateToolbarDrag(to point: CGPoint) {
        guard let dragOrigin, let dragFrame else { return }
        var proposed = dragFrame.offsetBy(dx: point.x - dragOrigin.x, dy: point.y - dragOrigin.y)
        if let destination = NSScreen.screens.first(where: { $0.frame.contains(point) }) ?? screen {
            proposed = ToolbarPlacement.constrainedDragFrame(proposed, visibleFrame: destination.visibleFrame)
        }
        setFrameOrigin(proposed.origin)
        NSCursor.closedHand.set()
        onUserDragMoved?(point)
    }

    func endToolbarDrag(at point: CGPoint) {
        guard let original = dragFrame else { return }
        updateToolbarDrag(to: point)
        dragOrigin = nil
        dragFrame = nil
        gestureOwnsDrag = false
        NSCursor.arrow.set()
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
        if holdPress(event) { return }
        super.sendEvent(event)
    }

    /// Press and hold (`ToolbarHold`): a left press waits until it lets go, moves or stays still long enough. The first
    /// two give the press to the control under the pointer; staying still picks the toolbar up instead.
    private func holdPress(_ event: NSEvent) -> Bool {
        switch event.type {
        case .leftMouseDown:
            // A press whose release went elsewhere is dropped, so its timer can't pick the toolbar up later.
            clearHold()
            guard !isTrackingToolbarDrag, onUserDragEnded != nil, allowsHoldToMove?() == true,
                  !keepsPressAtOnce(event) else { return false }
            heldPress = event
            let timer = Timer(timeInterval: ToolbarHold.delay, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated { self?.finishHold() }
            }
            RunLoop.main.add(timer, forMode: .common)
            holdTimer = timer
            return true
        case .leftMouseDragged:
            guard let press = heldPress else { return false }
            if ToolbarHold.isStill(dx: event.locationInWindow.x - press.locationInWindow.x,
                                   dy: event.locationInWindow.y - press.locationInWindow.y) { return true }
            releaseHeldPress(followedBy: event)
            return true
        case .leftMouseUp:
            guard heldPress != nil else { return false }
            releaseHeldPress(followedBy: event)
            return true
        default:
            return false
        }
    }

    /// Sliders and number fields get a press at once: holding one is how people use it.
    private func keepsPressAtOnce(_ event: NSEvent) -> Bool {
        guard let content = contentView, let container = content.superview else { return false }
        var view = content.hitTest(container.convert(event.locationInWindow, from: nil))
        while let current = view, current !== content {
            if current is NSSlider || current is NSTextView || (current as? NSTextField)?.isEditable == true { return true }
            view = current.superview
        }
        return false
    }

    /// The hold lasted: the toolbar follows the pointer from where the press started. If the button is already up, the
    /// press was a slow click, so the control gets it and the queued release after it.
    private func finishHold() {
        guard let press = heldPress else { return }
        guard isVisible, allowsHoldToMove?() == true, leftButtonIsDown() else {
            releaseHeldPress(followedBy: nil)
            return
        }
        clearHold()
        beginToolbarDrag(at: convertPoint(toScreen: press.locationInWindow))
    }

    /// Gives the held press to the control under the pointer, then `event`. A control can track the pointer in its own
    /// loop that waits for the next event, so `event` waits at the front of the queue, where that loop takes it. When no
    /// loop took it, it is sent here, so it never trails a newer press.
    private func releaseHeldPress(followedBy event: NSEvent?) {
        guard let press = heldPress else { return }
        clearHold()
        if let event { NSApp.postEvent(event, atStart: true) }
        super.sendEvent(press)
        guard let event else { return }
        let mask = NSEvent.EventTypeMask(type: event.type)
        if let next = NSApp.nextEvent(matching: mask, until: .distantPast, inMode: .default, dequeue: false),
           next.timestamp == event.timestamp, next.locationInWindow == event.locationInWindow,
           let queued = NSApp.nextEvent(matching: mask, until: .distantPast, inMode: .default, dequeue: true) {
            sendEvent(queued)
        }
    }

    private func clearHold() {
        holdTimer?.invalidate()
        holdTimer = nil
        heldPress = nil
    }

    override func orderOut(_ sender: Any?) {
        clearHold()
        cancelToolbarDrag()
        super.orderOut(sender)
    }

    func cancelToolbarDrag() {
        if isTrackingToolbarDrag { NSCursor.arrow.set() }
        dragOrigin = nil
        dragFrame = nil
        gestureOwnsDrag = false
        onUserDragCancelled?()
    }

    override func close() {
        clearHold()
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
/// The toolbar window's content: the SwiftUI toolbar over a background AppKit draws, so the window can grow, shrink or
/// morph smoothly. A resize lays the toolbar out at the window's size on every frame; a morph keeps the new face at its
/// final size, pinned where the old and new frames meet.
final class ToolbarContentView: NSView {
    let host: MovableToolbarHost
    let chrome = ToolbarChromeView()
    /// The new face's size while a morph animates the window toward it; `nil` fills the window.
    var contentSize: CGSize? { didSet { if contentSize != oldValue { placeSubviews() } } }
    var pin = ToolbarPin.topLeading { didSet { if pin != oldValue { placeSubviews() } } }
    var onPointerInside: ((Bool) -> Void)?
    private var pointerTracking: NSTrackingArea?
    private var outgoing: MorphFaceView?
    private var outgoingSize = CGSize.zero
    private var morphRadii: (from: CGFloat, to: CGFloat)?
    /// A vertical toolbar has no background of its own, so the morph fades one in or out between `start` and `end`.
    private var chromeFade: (from: CGFloat, to: CGFloat, start: Double, end: Double)?
    private var state: (vertical: Bool, minimal: Bool, collapsed: Bool) = (false, false, false)
    private var changes: AnyCancellable?
    var isMorphing: Bool { morphRadii != nil }

    init(model: WorkspaceModel, availableSize: CGSize) {
        host = MovableToolbarHost(rootView: Self.toolbar(model: model, availableSize: availableSize))
        host.sizingOptions = []
        super.init(frame: .zero)
        wantsLayer = true
        addSubview(chrome)
        addSubview(host)
        changes = model.$preferences.combineLatest(model.$toolbarCollapsed).sink { [weak self] preferences, collapsed in
            self?.state = (preferences.toolbarOrientation == .vertical, preferences.toolbarSize == .minimal, collapsed)
            self?.refreshChrome()
        }
    }

    static func toolbar(model: WorkspaceModel, availableSize: CGSize) -> ToolbarView {
        ToolbarView(model: model, availableSize: availableSize, drawsChrome: false, fillsWindow: true)
    }

    required init?(coder: NSCoder) { nil }
    override var mouseDownCanMoveWindow: Bool { false }
    override func layout() {
        super.layout()
        placeSubviews()
        host.layoutSubtreeIfNeeded()
    }
    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        placeSubviews()
        needsLayout = true
    }

    private func placeSubviews() {
        chrome.frame = bounds
        let frame = pin.rect(size: contentSize ?? bounds.size, in: bounds)
        if host.frame != frame { host.frame = frame }
        outgoing?.frame = pin.rect(size: outgoingSize, in: bounds)
    }

    /// A vertical toolbar draws its own surfaces; the horizontal toolbar and the one button use this background.
    private func refreshChrome() {
        guard !isMorphing else { return }
        chrome.isHidden = state.vertical && !state.collapsed
        chrome.alphaValue = 1
        chrome.cornerRadius = state.collapsed ? .greatestFiniteMagnitude : state.minimal ? 13 : 15
    }

    override func updateTrackingAreas() {
        if let pointerTracking { removeTrackingArea(pointerTracking) }
        let tracking = NSTrackingArea(rect: .zero, options: [.inVisibleRect, .activeAlways, .mouseEnteredAndExited], owner: self)
        addTrackingArea(tracking)
        pointerTracking = tracking
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) { onPointerInside?(true) }
    override func mouseExited(with event: NSEvent) { onPointerInside?(false) }

    /// A picture of the toolbar's controls, without the background, for the morph.
    func snapshotHost() -> CGImage? {
        host.layoutSubtreeIfNeeded()
        guard host.bounds.width >= 1, host.bounds.height >= 1,
              let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return nil }
        host.cacheDisplay(in: host.bounds, to: bitmap)
        return bitmap.cgImage
    }

    /// Shows `image` (the face that leaves) over the background and hides the new face until `updateMorph` fades it in.
    func beginMorph(outgoing image: CGImage?, outgoingSize: CGSize, pin: ToolbarPin,
                    radii: (from: CGFloat, to: CGFloat),
                    chromeFade: (from: CGFloat, to: CGFloat, start: Double, end: Double)?) {
        endMorph()
        self.pin = pin
        self.outgoingSize = outgoingSize
        morphRadii = radii
        self.chromeFade = chromeFade
        if let image {
            // A view, not a sublayer, so AppKit keeps it above the new face and the background.
            let face = MorphFaceView(image: image)
            addSubview(face, positioned: .above, relativeTo: host)
            outgoing = face
        }
        chrome.isHidden = false
        updateMorph(0)
        placeSubviews()
    }

    func updateMorph(_ time: Double) {
        outgoing?.alphaValue = ToolbarMorph.outgoingOpacity(time)
        host.alphaValue = ToolbarMorph.incomingOpacity(time)
        if let morphRadii {
            chrome.cornerRadius = morphRadii.from + (morphRadii.to - morphRadii.from) * ToolbarMorph.shape(time)
        }
        if let chromeFade {
            let share = min(1, max(0, (time - chromeFade.start) / (chromeFade.end - chromeFade.start)))
            chrome.alphaValue = chromeFade.from + (chromeFade.to - chromeFade.from) * share
        }
    }

    func endMorph() {
        outgoing?.removeFromSuperview()
        outgoing = nil
        morphRadii = nil
        chromeFade = nil
        host.alphaValue = 1
        refreshChrome()
    }
}

/// The face that leaves during a morph: a picture of the toolbar's controls that fades out while the window changes.
private final class MorphFaceView: NSView {
    private let image: CGImage

    init(image: CGImage) {
        self.image = image
        super.init(frame: .zero)
        wantsLayer = true
    }

    required init?(coder: NSCoder) { nil }
    override var wantsUpdateLayer: Bool { true }
    override func updateLayer() {
        layer?.contents = image
        layer?.contentsGravity = .resize
    }
    // Clicks go to the toolbar under it.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

struct SettingsWindowSurface: NSViewRepresentable {
    func makeNSView(context: Context) -> SettingsSurfaceView { SettingsSurfaceView() }
    func updateNSView(_ view: SettingsSurfaceView, context: Context) {}
}

final class SettingsSurfaceView: NSView {
    static let minimumContentSize = NSSize(width: 720, height: 620)
    /// Wide enough for "Your toolbar" to show 15 tools and every toolbar button on one line.
    static let defaultContentSize = CGSize(width: 1420, height: 700)

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
