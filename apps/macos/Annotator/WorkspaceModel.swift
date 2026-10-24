import AppKit
import OSLog
import SwiftUI
import UniformTypeIdentifiers
import AnnotationCore
import PresentationRendering
import NativePlatform
import Combine
import ImageIO

struct DesktopDisplay {
    let id: CGDirectDisplayID
    let frame: CGRect
    let scale: Double

    @MainActor static func connected() throws -> [DesktopDisplay] {
        try NSScreen.screens.map { screen in
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
                throw CanvasError.invalidDocument("The display identity is unavailable.")
            }
            return DesktopDisplay(id: number.uint32Value, frame: screen.frame, scale: screen.backingScaleFactor)
        }
    }
}

private enum WorkspaceHistoryFailure: Error, LocalizedError {
    case changed, undoBudget
    var errorDescription: String? {
        switch self {
        case .changed: "Another display's drawing or history changed. No display was changed by this Undo or Redo."
        case .undoBudget: "This change would exceed the Undo history limit. Your drawings have not changed. Save your boards first."
        }
    }
}

@MainActor
final class WorkspaceModel: ObservableObject {
    enum Mode: String { case off = "Off", interact = "Interact", draw = "Draw" }
    enum Board: String, CaseIterable { case transparent = "Desktop", light = "Whiteboard", dark = "Blackboard" }

    @Published private(set) var mode: Mode = .off
    @Published var tool: Tool = .pen
    @Published var style = Style()
    private var initialBoard: Board = .transparent
    var board: Board {
        get { active?.board ?? initialBoard }
        set {
            objectWillChange.send()
            if let active { active.board = newValue }
            else { initialBoard = newValue }
            refreshCanvas()
        }
    }
    @Published var spotlight = false { didSet { spotlightFade.setVisible(spotlight); updatePointerEffects() } }
    private(set) lazy var spotlightFade = SpotlightFade { [weak self] in
        guard let self, !self.workspaceHidden else { return false }
        self.refreshPointerEffectsOnly()
        for session in self.sessions.values where session.presentation?.usesPointerOverlay != true {
            session.canvas.needsDisplay = true
        }
        return true
    }
    @Published var cursorHalo = false { didSet { updatePointerEffects() } }
    private var lastPointerMovement: Double?
    private var lastPointerClick: Double?
    private var pointerExpiry: Task<Void, Never>?
    let pointerClickAnimation = CursorClickAnimation()
    private(set) var cursorClickLocation: CGPoint?
    var cursorHighlightSettings: CursorHighlightSettings { preferences.cursorHighlight ?? CursorHighlightSettings() }
    var cursorHighlightVisible: Bool {
        cursorHalo && cursorHighlightSettings.isVisible(now: ProcessInfo.processInfo.systemUptime,
            lastMovement: lastPointerMovement, lastClick: lastPointerClick)
    }

    func setCursorHighlight(_ settings: CursorHighlightSettings) {
        do {
            try settings.validate()
            if let owner = globalSettingsOwner {
                owner.setCursorHighlight(settings)
                preferences = owner.preferences
            } else {
                preferences.cursorHighlight = settings
                persistPreferences()
            }
            refreshPointerEffectsOnly()
        } catch { report(error) }
    }
    @Published var spotlightRadius: Double = 130 {
        didSet {
            spotlightFade.setRadius(spotlightRadius, from: oldValue)
            if applyingSharedPreferences { return }
            preferences.spotlightRadius = spotlightRadius
            if let owner = globalSettingsOwner, owner.spotlightRadius != spotlightRadius { owner.spotlightRadius = spotlightRadius }
            persistPreferences()
            refreshCanvas()
        }
    }
    @Published var showHelp = false
    @Published var settingsTab: CustomizationTab = .general
    /// True while a tutorial card shows, so the support reminder waits. The name predates the tutorial.
    @Published var introductionVisible = false
    /// Opens the tutorial's welcome card from Settings.
    var onShowIntroduction: (() -> Void)?
    var openExternalURL: (URL) -> Bool = { NSWorkspace.shared.open($0) }
    @Published var status = "Ready. Control-2 opens drawing."
    @Published var errorMessage: String?
    @Published var settingsVisible = false
    @Published private(set) var captureVerified = false
    @Published private(set) var captureFailureMessage: String?
    @Published private(set) var zoomStartState = ZoomStartState.idle
    @Published private(set) var zoomGesturesAvailable = false
    @Published private(set) var zoomGestureMessage: String?
    private let zoomInputPermissions: ZoomInputPermissions
    private var requestedZoomScrollPermission = false
    private var pendingScreenZoomScale: Double?
    private var pendingScreenZoomScrollDelta: Double?
    var zoomScrollPermissionGranted: Bool { zoomInputPermissions.isGranted() }
    var zoomScrollStatus: String {
        if zoomGesturesAvailable { return "Option-scroll adjusts zoom in Drawing and Using apps." }
        if !zoomScrollPermissionGranted { return "Option-scroll over other apps needs Accessibility approval." }
        return zoomGestureMessage ?? "Option-scroll is not connected. Check Settings > Permissions."
    }
    private lazy var zoomStart = ZoomStartController(
        screenGranted: { DisplayCapture.hasPermission },
        gesturesGranted: { [weak self] in self?.zoomScrollPermissionGranted ?? false },
        requestScreen: { [weak self] in self?.requestCapturePermission() },
        requestGestures: { [weak self] in self?.requestZoomGesturePermission() },
        requiresGesturesForStart: false,
        start: { [weak self] in self?.beginScreenZoom() },
        changed: { [weak self] state in
            guard let self else { return }
            self.zoomStartState = state
            switch state {
            case .screenPermission: self.status = "Zoom is waiting for Screen Recording permission."
            case .gesturePermission: self.status = "Zoom is waiting for Accessibility permission."
            case .starting: self.status = "Starting live zoom…"
            case .active:
                self.status = "Live zoom is on. \(self.zoomScrollStatus)"
            case .failed(let message):
                if self.active?.document.presentation.mode == .screen {
                    self.active?.document.stopPresentationWithoutHistory()
                    self.active?.presentation?.suspend()
                    self.synchronize()
                }
                self.status = message
            case .idle: break
            }
            if state != .idle { self.onShowToolbar?() }
        })
    private var failedPresentationMode = PresentationSettings.Mode.off
    @Published private(set) var selectionCount = 0
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false
    /// Counts Undo operations that changed something, not an Undo that only cancelled an edit in progress.
    /// The tutorial reads it to see that Undo ran.
    @Published private(set) var undoSerial: UInt64 = 0
    @Published var smartDraw = false {
        didSet {
            if applyingSharedPreferences { return }
            preferences.smartDraw = smartDraw
            if let owner = globalSettingsOwner, owner.smartDraw != smartDraw { owner.smartDraw = smartDraw }
            persistPreferences()
        }
    }
    @Published private(set) var preferences = WorkspacePreferences()
    @Published var hotkeyRegistered = false
    @Published var zoomHotkeyRegistered = false
    @Published var showCustomization = false
    @Published private(set) var workspaceHidden = false
    @Published var visibilityAnimating = false
    @Published private(set) var magnifierEnabled = false
    @Published private(set) var screenZoomEnabled = false
    /// Screen zoom start and stop ramp the displayed factor; document state changes at once.
    var animatesScreenZoom: () -> Bool = { !Motion.systemReducesMotion }
    private var screenZoomEntrance: Double?
    private var defaultMagnification = 2.0
    var magnification: Double {
        get { active?.document.presentation.zoom ?? defaultMagnification }
        set {
            guard newValue.isFinite, (1...16).contains(newValue) else { report(CanvasError.invalidGeometry); return }
            defaultMagnification = newValue
            guard let active else { return }
            do {
                var settings = active.document.presentation
                settings.zoom = newValue
                try active.document.setPresentation(settings)
                synchronize()
            } catch { report(error) }
        }
    }
    var onShowToolbar: (() -> Void)?
    var onEndDrawing: (() -> Void)?
    var onHideToolbar: (() -> Void)?
    var onDismissToolbar: (() -> Void)?
    var onSuspendPresentation: (() -> Void)?
    var onToolbarSizeChanged: ((CGSize) -> Void)?
    var onToolbarBackgroundDrag: ((CGSize, Bool) -> Void)?
    var onActivationShortcutChanged: ((ActivationShortcut) throws -> Void)?
    var onActivationRequested: (() -> Void)?
    var onResumeDrawingRequested: (() -> Void)?
    var onFocusToolbar: (() -> Void)?
    @Published var canCheckForUpdates = false
    @Published var updatesConfigured = false
    @Published var automaticallyCheckForUpdates = false
    @Published var updateStatus = "Updates aren't set up in this build. Find new versions in Settings > About."
    var onCheckForUpdates: (() -> Void)?
    var onAutomaticUpdateChecks: ((Bool) -> Void)?
    @Published var loginItemController: LoginItemController?
    var effectiveLoginItemController: LoginItemController? {
        globalSettingsOwner?.loginItemController ?? loginItemController
    }
    var onOpenToolLibrary: ((ToolLibrarySection?) -> Void)?
    var onDismissMenuBar: (() -> Bool)?
    var toolLibraryShortcut: ActivationShortcut? {
        let key = ActivationShortcut(keyCode: 37, character: "l", command: true)
        let global = [preferences.activationShortcut, preferences.hideShortcut ?? .hideTools,
                      preferences.zoomShortcut ?? .liveZoom, preferences.resumeShortcut ?? .resumeDrawing,
                      preferences.breakShortcut ?? .breakTimer]
        return global.contains { $0.usesSameKeys(as: key) } ? nil : key
    }
    func openToolLibrary(_ section: ToolLibrarySection? = nil) {
        onFocusToolbar?()
        onOpenToolLibrary?(section)
    }
    var onHideShortcutChanged: ((ActivationShortcut) throws -> Void)?
    var onZoomShortcutChanged: ((ActivationShortcut) throws -> Void)?
    var onResumeShortcutChanged: ((ActivationShortcut) throws -> Void)?
    var onBreakShortcutChanged: ((ActivationShortcut) throws -> Void)?
    var onMenuBarVisibilityChanged: ((Bool) -> Void)?
    var onRegionSharingStateChanged: ((RegionSharingState) -> Void)?
    var onRegionSharingToolbar: (() -> NSWindow?)?
    var onThemeChanged: ((AppTheme) -> Void)?
    var onToolbarLayoutChanged: (() -> Void)?
    var onDisplaysChanged: (() -> Void)?
    var onOpenPreferences: (() -> Void)?
    var onInspectorVisibilityChanged: (() -> Void)?
    var onScreenZoomChanged: ((Bool) -> Void)?
    private var sessions: [CGDirectDisplayID: OverlaySession] = [:]
    private var regionSharingController: RegionSharingController?
    private var breakTimerController: BreakTimerController?
    /// What the break timer reads from the system. Tests replace it before the first break.
    var breakTimerEnvironment = BreakTimerEnvironment()
    private let availableDesktopDisplays: @MainActor () throws -> [DesktopDisplay]
    private struct WorkspaceHistoryEntry {
        let documents: Set<UUID>
        let globalSelection: Bool
    }
    private var workspaceHistory: [UUID: WorkspaceHistoryEntry] = [:]
    private var dragPreviews: [(session: OverlaySession, document: CanvasDocument)] = []
    @Published private(set) var selectionIsWorkspaceWide = false
    private(set) var active: OverlaySession?
    private var displayObserver: NSObjectProtocol?
    private var pointerMonitor: Any?
    private var localPointerMonitor: Any?
    private let defaults: UserDefaults
    let supportReminder: SupportReminder
    let usageMetrics: UsageMetrics
    let canvasInteraction = PassthroughSubject<Void, Never>()
    let buildLabel: String
    var versionLabel: String {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Version unavailable"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "unknown"
        let prerelease = Bundle.main.object(forInfoDictionaryKey: "AnnotatorPrerelease") as? String
        return Self.formatVersion(version: version, build: build, prerelease: prerelease)
    }

    static func formatVersion(version: String, build: String, prerelease: String?) -> String {
        let suffix = prerelease.flatMap { $0.isEmpty ? nil : "-\($0)" } ?? ""
        return "\(version)\(suffix) (\(build))"
    }
    private let imagePasteboard: NSPasteboard
    static let annotationPasteboardType = NSPasteboard.PasteboardType("app.annotator.annotations")
    private struct PasteCascade { let changeCount: Int; let document: UUID; let origin: Point; let pointer: Point? }
    private var pasteCascade: PasteCascade?
    var screenPointer: @MainActor () -> CGPoint = { NSEvent.mouseLocation }
    private var continuousStyleSessions: [OverlaySession] = []
    private(set) weak var globalSettingsOwner: WorkspaceModel?
    private var sharedSettingsChanges: AnyCancellable?
    private var applyingSharedPreferences = false
    private var zoomGesture: Task<Void, Never>?
    private var zoomGestureSession: OverlaySession?
    var preferenceDefaults: UserDefaults { defaults }

    func connectSettingsOwner(_ owner: WorkspaceModel?) {
        globalSettingsOwner = owner
        sharedSettingsChanges = owner?.$preferences.sink { [weak self] settings in
            guard let self else { return }
            self.applyingSharedPreferences = true
            self.preferences = settings
            self.smartDraw = settings.smartDraw
            self.spotlightRadius = settings.spotlightRadius
            if self.active?.document.selection.isEmpty != false && self.active?.document.isEditing != true {
                self.style = settings.toolStyles[self.tool.rawValue] ?? .defaults(for: self.tool)
            }
            self.applyingSharedPreferences = false
            self.refreshCanvas()
            self.active?.canvas.refreshToolCursor()
        }
    }

    init(defaults: UserDefaults = .standard, imagePasteboard: NSPasteboard = .general,
         zoomInputPermissions: ZoomInputPermissions = .system,
         desktopDisplays: @escaping @MainActor () throws -> [DesktopDisplay] = DesktopDisplay.connected) {
        self.defaults = defaults
        supportReminder = SupportReminder(defaults: defaults)
        usageMetrics = UsageMetrics(defaults: defaults,
                                    sendsAutomatically: defaults === UserDefaults.standard &&
                                        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil)
        availableDesktopDisplays = desktopDisplays
        self.zoomInputPermissions = zoomInputPermissions
        let stamp = Bundle.main.object(forInfoDictionaryKey: "AnnotatorBuildStamp") as? String
        buildLabel = stamp.flatMap { $0.isEmpty || $0.contains("$(") ? nil : $0 } ?? "Unstamped development build"
        self.imagePasteboard = imagePasteboard
        if let data = defaults.data(forKey: "workspace-preferences-v1") {
            do {
                preferences = try WorkspacePreferences.read(data)
                smartDraw = preferences.smartDraw
                spotlightRadius = preferences.spotlightRadius
                style = preferences.toolStyles[tool.rawValue] ?? .defaults(for: tool)
            } catch {
                errorMessage = "Saved settings were not loaded: \(error.localizedDescription)"
                status = errorMessage ?? "Saved settings could not be loaded."
            }
        }
        displayObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.displaysChanged() } }
    }

    var hasProtection: Bool { sessions.values.contains { $0.document.hasProtection } }
    var desktopSessions: [OverlaySession] { sessions.values.filter { $0.sourceImage == nil } }
    var hasReferenceImage: Bool { active?.sourceImage != nil }
    var regionSharingState: RegionSharingState { regionSharingController?.state ?? .idle }
    var regionSharingToolbar: NSWindow? { onRegionSharingToolbar?() }
    var regionSharing: RegionSharingController {
        if let regionSharingController { return regionSharingController }
        let controller = RegionSharingController(model: self)
        regionSharingController = controller
        return controller
    }

    func openRegionSharing() { regionSharing.showControls() }

    // MARK: Break timer (specification 014)

    var breakTimer: BreakTimerController {
        if let breakTimerController { return breakTimerController }
        let controller = BreakTimerController(model: self, environment: breakTimerEnvironment)
        breakTimerController = controller
        return controller
    }
    var breakTimerRunning: Bool { breakTimerController?.isRunning ?? false }
    var breakTimerSettings: BreakTimerSettings { preferences.breakTimer ?? BreakTimerSettings() }

    /// Starts a break with the saved settings, or with `minutes` for a quick start.
    @discardableResult func startBreak(minutes: Int? = nil) -> Bool {
        do {
            try breakTimer.start(minutes: minutes, settings: breakTimerSettings)
            status = "The break timer is on screen."
            return true
        } catch {
            report(error)
            return false
        }
    }

    func toggleBreakFromShortcut() {
        if breakTimerRunning { breakTimer.shortcutPressed() } else { startBreak() }
    }

    /// The break screen covers everything, so stop work that would run unseen under it.
    func prepareForBreak() {
        stopZoom()
        if regionSharingState.isCapturing {
            regionSharingController?.stopForSafety("Paused for a break. Start sharing again from the Annotator menu.")
        }
        for session in sessions.values { session.canvas.commitTextIfNeeded() }
        if mode == .draw { interact() }
    }

    func setBreakTimerSettings(_ settings: BreakTimerSettings) {
        if let owner = globalSettingsOwner {
            owner.setBreakTimerSettings(settings)
            preferences = owner.preferences
            return
        }
        do {
            try settings.validate()
            preferences.breakTimer = settings
            persistPreferences()
        } catch { report(error) }
    }

    func setBreakShortcut(_ value: ActivationShortcut) {
        if let owner = globalSettingsOwner {
            owner.setBreakShortcut(value)
            preferences.breakShortcut = owner.preferences.breakShortcut
            errorMessage = owner.errorMessage
            return
        }
        do {
            var updated = preferences
            updated.breakShortcut = value
            try updated.validate()
            try onBreakShortcutChanged?(value)
            preferences = updated
            persistPreferences()
        } catch { report(error) }
    }

    func displaysForRegionSharing() throws -> [DesktopDisplay] {
        let displays = try availableDesktopDisplays()
        guard Set(displays.map(\.id)).count == displays.count,
              displays.allSatisfy({
                  Rect($0.frame).isValid && $0.frame.width >= 64 && $0.frame.height >= 64 &&
                      $0.scale.isFinite && $0.scale > 0 &&
                      $0.frame.width * $0.scale <= 16_384 && $0.frame.height * $0.scale <= 16_384 &&
                      $0.frame.width * $0.frame.height * $0.scale * $0.scale <= 40_000_000
              }) else { throw CaptureFailure.displayUnavailable }
        return displays
    }

    func prepareRegionSharingSession(on id: CGDirectDisplayID) throws -> OverlaySession {
        guard !hasReferenceImage,
              let display = try displaysForRegionSharing().first(where: { $0.id == id }) else {
            throw CaptureFailure.displayUnavailable
        }
        let session: OverlaySession
        if let existing = sessions[id] { session = existing }
        else {
            session = OverlaySession(desktopFrame: display.frame, scale: display.scale, model: self)
            sessions[id] = session
        }
        if active == nil { active = session; session.board = initialBoard }
        if mode == .off { mode = .interact }
        if !workspaceHidden { session.setDrawing(mode == .draw, focus: false) }
        synchronize()
        return session
    }

    var censorFallbackActive: Bool {
        guard let active, active.sourceImage == nil else { return false }
        return active.document.elements.contains(where: { $0.tool.isPixelEffect }) && !active.effectsReady
    }
    /// Tools and buttons in toolbar order. Older profiles only have `toolbarTools`, where effects share one button.
    var toolbarItems: [ShortcutCommand] {
        let legacy = Self.normalizedTools(preferences.toolbarTools?.compactMap(Tool.init(rawValue:)) ?? WorkspacePreferences.defaultToolbar)
        var seen = Set<ShortcutCommand>()
        let items = (preferences.toolbarItems ?? []).compactMap(ShortcutCommand.init(id:)).filter { seen.insert($0).inserted }
        let tools = items.compactMap { if case .tool(let tool) = $0 { tool } else { nil } }
        // An older version may have changed the tools since; its list wins.
        guard items.contains(.tool(.select)), Self.normalizedTools(tools) == legacy else { return legacy.map(ShortcutCommand.tool) }
        return items
    }
    var toolbarTools: [Tool] {
        toolbarItems.compactMap { if case .tool(let tool) = $0 { tool } else { nil } }
    }
    static let toolbarCandidates: [ShortcutCommand] =
        Tool.allCases.filter { !$0.isProtection || $0 == .cover }.map(ShortcutCommand.tool) +
        [Tool.blur, .pixelate, .distort].map(ShortcutCommand.tool) + ShortcutAction.toolbarChoices.map(ShortcutCommand.action)

    private static func normalizedTools(_ tools: [Tool]) -> [Tool] {
        var seen = Set<Tool>()
        return tools.map { $0.isProtection ? .cover : $0 }.filter { seen.insert($0).inserted }
    }
    var isConstructingArrow: Bool { active?.canvas.constructingArrow ?? false }
    var selectionIsLocked: Bool {
        !selectedElements.isEmpty && selectedElements.allSatisfy(\.locked)
    }

    private var selectionSessions: [OverlaySession] {
        guard let active else { return [] }
        guard selectionIsWorkspaceWide, active.sourceImage == nil else { return [active] }
        return [active] + desktopSessions.filter { $0 !== active }.sorted { $0.panel.frame.minX < $1.panel.frame.minX }
    }

    var selectionScopeIDs: Set<UUID> { Set(selectionSessions.map { $0.document.identity }) }
    var selectedElementIDs: Set<UUID> { Set(selectedElements.map(\.id)) }
    var selectionSpansDisplays: Bool {
        selectionSessions.filter { !$0.document.selection.isEmpty }.count > 1
    }
    private var selectedElements: [Element] {
        selectionSessions.flatMap { session in
            session.document.elements.filter { session.document.selection.contains($0.id) }
        }
    }

    func selectAll() {
        finishStyleEdit()
        guard let active else { return }
        let targets = active.sourceImage == nil ? desktopSessions : [active]
        for session in targets {
            session.canvas.commitTextIfNeeded()
            session.canvas.cancelGesture()
            session.document.select(Set(session.document.elements.map(\.id)))
        }
        selectionIsWorkspaceWide = targets.count > 1
        tool = .select
        synchronize()
    }

    func useLocalSelection(in session: OverlaySession) {
        guard selectionIsWorkspaceWide || !continuousStyleSessions.isEmpty ||
            sessions.values.contains(where: { $0 !== session && !$0.document.selection.isEmpty }) else { return }
        finishStyleEdit()
        for other in sessions.values where other !== session { other.document.select([]) }
        selectionIsWorkspaceWide = false
        synchronize()
    }

    func pointerWindowNumber(at point: CGPoint) -> Int {
        var number = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0)
        var visited = Set<Int>()
        while number > 0 && visited.insert(number).inserted {
            guard let window = NSApp.windows.first(where: { $0.windowNumber == number }), window.ignoresMouseEvents else {
                return number
            }
            number = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: number)
        }
        return 0
    }

    func cursorForPointer(at point: CGPoint, windowNumber: Int) -> NSCursor {
        if let cursor = regionSharingController?.selectionCursor(at: point, windowNumber: windowNumber) {
            return cursor
        }
        guard mode == .draw, !workspaceHidden, !visibilityAnimating,
              let target = sessions.values.first(where: {
                  $0.panel.windowNumber == windowNumber && $0.panel.isVisible && !$0.panel.ignoresMouseEvents
              }), target.panel.frame.contains(point) else { return .arrow }
        let local = target.canvas.convert(target.panel.convertPoint(fromScreen: point), from: nil)
        return target.canvas.hoverCursor(at: Point(local))
    }

    func refreshPointerCursor() {
        let point = NSEvent.mouseLocation
        cursorForPointer(at: point, windowNumber: pointerWindowNumber(at: point)).set()
    }

    func beginDrawing(recordsUsage: Bool = true) {
        supportReminder.recordUse()
        if recordsUsage { usageMetrics.record(.drawing) }
        finishZoomGesture()
        if workspaceHidden, let onResumeDrawingRequested { onResumeDrawingRequested(); return }
        if workspaceHidden { revealWorkspaceSurfaces() }
        finishStyleEdit()
        if let active, active.sourceImage != nil {
            mode = .draw
            active.setDrawing(true)
            synchronize()
            return
        }
        guard let session = prepareDesktopDrawingSession() else { return }
        mode = .draw
        for canvas in desktopSessions { canvas.setDrawing(true, focus: canvas === session) }
        status = "Draw on any display. Escape returns to Interact."
        synchronize()
    }

    @discardableResult
    func prepareDrawingForReveal() -> Bool {
        supportReminder.recordUse()
        if active?.sourceImage == nil, prepareDesktopDrawingSession() == nil { return false }
        mode = .draw
        synchronize()
        return true
    }

    private func prepareDesktopDrawingSession() -> OverlaySession? {
        do {
            let displays = try availableDesktopDisplays()
            guard let display = displays.first(where: { sessions[$0.id] === active }) ??
                displays.first(where: { $0.frame.contains(NSEvent.mouseLocation) }) ?? displays.first else {
                throw CanvasError.invalidDocument("No attached display is available.")
            }
            guard Set(displays.map(\.id)).count == displays.count else {
                throw CanvasError.invalidDocument("The display list contains duplicate identities.")
            }
            for display in displays where sessions[display.id] == nil {
                sessions[display.id] = OverlaySession(desktopFrame: display.frame, scale: display.scale, model: self)
            }
            guard let session = sessions[display.id] else { throw CanvasError.invalidDocument("The drawing display is unavailable.") }
            switchActiveSession(to: session)
            return session
        } catch { report(error); return nil }
    }

    private func switchActiveSession(to session: OverlaySession) {
        if active == nil { session.board = initialBoard }
        if let active, active !== session {
            finishZoomGesture()
            finishStyleEdit()
            active.canvas.commitTextIfNeeded()
            active.canvas.cancelGesture()
            active.document.stopPresentationWithoutHistory()
            active.presentation?.presentationMode = .off
            active.presentation?.refresh()
        }
        if active !== session && tool != .select { session.document.select([]) }
        objectWillChange.send()
        active = session
    }

    @discardableResult
    func activateDrawingSession(_ session: OverlaySession) -> Bool {
        guard mode == .draw, !workspaceHidden, !visibilityAnimating,
              sessions.values.contains(where: { $0 === session }) else { return false }
        if active !== session {
            switchActiveSession(to: session)
            session.panel.makeKey()
            session.panel.makeFirstResponder(session.canvas)
            synchronize()
        }
        return true
    }

    func activateCanvasForEditing(_ session: OverlaySession) throws {
        guard active === session, !workspaceHidden, !visibilityAnimating else {
            throw CanvasError.invalidDocument("Show the drawing tools before inserting a saved shape.")
        }
        mode = .draw
        if session.sourceImage != nil { session.setDrawing(true) }
        else {
            for target in desktopSessions { target.setDrawing(true, focus: target === session) }
        }
    }

    func clearDisplayDragPreviews() {
        for preview in dragPreviews {
            preview.session.document = preview.document
            preview.session.canvas.renderer.reset()
            preview.session.canvas.needsDisplay = true
            preview.session.presentation?.refresh()
        }
        dragPreviews.removeAll()
    }

    private func movedElements(from source: OverlaySession, to destination: OverlaySession,
                               ids: Set<UUID>) -> [Element] {
        let offset = Point(source.panel.frame.minX - destination.panel.frame.minX,
                           destination.panel.frame.maxY - source.panel.frame.maxY)
        return source.document.elements.filter { ids.contains($0.id) && !$0.locked }.map {
            var element = $0
            element.translate(by: offset)
            if let anchor = element.startAnchor, !ids.contains(anchor.elementID) { element.startAnchor = nil }
            if let anchor = element.endAnchor, !ids.contains(anchor.elementID) { element.endAnchor = nil }
            return element
        }
    }

    func previewDisplayDrag(from source: OverlaySession, ids: Set<UUID>) throws {
        clearDisplayDragPreviews()
        guard source.sourceImage == nil else { return }
        for destination in desktopSessions where destination !== source {
            let elements = movedElements(from: source, to: destination, ids: ids)
            guard elements.contains(where: {
                destination.canvas.renderer.drawingBounds(for: $0).intersects(destination.canvas.bounds)
            }) else { continue }
            guard !destination.document.isEditing else {
                throw CanvasError.invalidDocument("Finish the edit on the other display before moving these objects.")
            }
            var preview = destination.document
            preview.begin()
            for element in elements { try preview.add(element) }
            preview.select(Set(elements.map(\.id)))
            dragPreviews.append((destination, destination.document))
            destination.document = preview
            destination.canvas.renderer.reset()
            destination.canvas.needsDisplay = true
            destination.presentation?.refresh()
        }
    }

    func finishDisplayDrag(from source: OverlaySession, at screenPoint: CGPoint, ids: Set<UUID>) throws -> Bool {
        clearDisplayDragPreviews()
        guard source.sourceImage == nil else { return false }
        guard let destination = desktopSessions.first(where: { $0.panel.frame.contains(screenPoint) }) else {
            source.document.cancel()
            return true
        }
        guard destination !== source else { return false }
        let elements = movedElements(from: source, to: destination, ids: ids)
        guard !elements.isEmpty else { return false }
        source.document.cancel()
        guard !destination.document.isEditing else {
            throw CanvasError.invalidDocument("Finish the edit on the other display before moving these objects.")
        }
        var departure = source.document
        var arrival = destination.document
        departure.begin()
        departure.select(Set(elements.map(\.id)))
        try departure.deleteSelection(includingProtection: true)
        arrival.begin()
        for element in elements { try arrival.add(element) }
        arrival.select(Set(elements.map(\.id)))
        try commitWorkspaceDocuments([departure, arrival], to: [source, destination])
        switchActiveSession(to: destination)
        destination.panel.makeKey()
        destination.panel.makeFirstResponder(destination.canvas)
        return true
    }

    func interact() {
        finishZoomGesture()
        finishStyleEdit()
        active?.canvas.commitTextIfNeeded()
        for session in sessions.values where !workspaceHidden { session.setDrawing(false) }
        mode = sessions.isEmpty ? .off : .interact
        status = "Interact: your apps receive clicks; drawings remain visible."
        synchronize()
        onEndDrawing?()
    }

    func closeAll() {
        breakTimerController?.close()
        regionSharingController?.close()
        regionSharingController = nil
        zoomStart.cancel()
        finishZoomGesture()
        finishStyleEdit()
        for session in sessions.values { session.close() }
        sessions.removeAll(); active = nil
        workspaceHistory.removeAll()
        selectionIsWorkspaceWide = false
        stopPointerMonitors()
        if let displayObserver { NotificationCenter.default.removeObserver(displayObserver) }
        displayObserver = nil
        mode = .off
        synchronize()
    }

    func concealWorkspaceSurfaces() {
        finishStyleEdit()
        workspaceHidden = true
        for session in desktopSessions {
            session.panel.ignoresMouseEvents = true
            session.presentation?.hideWorkspace()
            session.panel.orderOut(nil)
        }
        stopPointerMonitors()
        NSCursor.arrow.set()
    }

    func revealWorkspaceSurfaces(drawing: Bool = false) {
        workspaceHidden = false
        for session in desktopSessions {
            session.setDrawing(false)
            session.presentation?.retry()
        }
        if drawing, let active {
            mode = .draw
            for session in desktopSessions { session.setDrawing(true, focus: session === active) }
            status = "Draw on any display. Escape returns to Interact."
        } else { mode = sessions.isEmpty ? .off : .interact }
        synchronize()
        updatePointerEffects()
    }

    func setQuickPreferencesExpanded(_ expanded: Bool) {
        if let owner = globalSettingsOwner { owner.setQuickPreferencesExpanded(expanded); return }
        preferences.quickPreferencesExpanded = expanded
        persistPreferences()
        onInspectorVisibilityChanged?()
    }

    func setBoard(_ value: Board) {
        if active == nil { beginDrawing() }
        guard active != nil else { return }
        board = value
    }

    func toggleProperties() {
        if let owner = globalSettingsOwner { owner.toggleProperties(); return }
        let expanded = preferences.inspectorPinned == true && preferences.quickPreferencesExpanded != false
        setInspectorPinned(!expanded)
    }

    func setInspectorPinned(_ pinned: Bool) {
        if let owner = globalSettingsOwner { owner.setInspectorPinned(pinned); return }
        preferences.inspectorPinned = pinned
        if pinned { preferences.quickPreferencesExpanded = true }
        persistPreferences()
        onInspectorVisibilityChanged?()
    }

    func setVisibilityAnimation(_ enabled: Bool) {
        if let owner = globalSettingsOwner { owner.setVisibilityAnimation(enabled); return }
        preferences.animateVisibility = enabled
        if enabled && preferences.visibilityAnimationStyle == VisibilityAnimationStyle.none { preferences.visibilityAnimationStyle = .slide }
        persistPreferences()
    }

    func setAnimationStyle(_ style: VisibilityAnimationStyle) {
        if let owner = globalSettingsOwner { owner.setAnimationStyle(style); return }
        preferences.visibilityAnimationStyle = style == .fluid ? .slide : style
        preferences.animateVisibility = style != .none
        persistPreferences()
    }

    func setTheme(_ theme: AppTheme) {
        if let owner = globalSettingsOwner { owner.setTheme(theme); return }
        preferences.theme = theme
        persistPreferences()
        onThemeChanged?(theme)
    }

    func openPreferences(tab: CustomizationTab = .general) {
        if let owner = globalSettingsOwner { owner.openPreferences(tab: tab); return }
        settingsTab = tab
        interact()
        onOpenPreferences?()
    }

    func checkForUpdates() {
        guard canCheckForUpdates, let onCheckForUpdates else {
            status = updateStatus
            return
        }
        onCheckForUpdates()
    }

    var preferredEffectTool: Tool {
        guard let mode = preferences.lastCoverMode, mode.isPixelEffect else { return .blur }
        return mode
    }

    func chooseCover() { choose(preferredEffectTool) }

    func setCoverMode(_ value: Tool) {
        guard value.isPixelEffect else {
            report(CanvasError.invalidDocument("Choose Blur, Pixelate or Distort. Use Rectangle for a solid block."))
            return
        }
        if !hasReferenceImage && !ensureCapturePermission() { return }
        let selected = selectedElements
        if !selected.isEmpty && selected.allSatisfy(\.tool.isProtection) {
            guard !selectionIsLocked else { report(CanvasError.invalidDocument("Unlock this cover before changing its mode.")); return }
            perform { document in try document.editSelection { $0.tool = value } }
            preferences.lastCoverMode = value
            persistPreferences()
            synchronize()
        } else { choose(value) }
    }

    func choose(_ value: Tool) {
        if value == .cover { chooseCover(); return }
        if value == .image { pasteImage(); return }
        finishStyleEdit()
        active?.canvas.commitTextIfNeeded()
        active?.canvas.finishLinearPath()
        active?.canvas.cancelGesture()
        if value.isPixelEffect && !hasReferenceImage && !ensureCapturePermission() { return }
        tool = value
        if value == .select { usageMetrics.record(.editing) }
        else if value.isProtection { usageMetrics.record(.coverOrEffect) }
        else if value == .text || (!value.isPixelEffect && value != .pen && value != .highlighter && value != .eraser) {
            usageMetrics.record(.textOrShape)
        } else { usageMetrics.record(.drawing) }
        if value.isProtection { preferences.lastCoverMode = value; persistPreferences() }
        if value != .select {
            if let active { useLocalSelection(in: active) }
            active?.document.select([])
        }
        style = preferences.toolStyles[value.rawValue] ?? .defaults(for: value)
        if mode != .draw { beginDrawing(recordsUsage: false) }
        if value.isPixelEffect { active?.presentation?.retry() }
        synchronize()
        if mode == .draw && !workspaceHidden && !visibilityAnimating {
            active?.panel.makeKey()
        }
        active?.canvas.window?.makeFirstResponder(active?.canvas)
        active?.canvas.refreshToolCursor()
    }

    func perform(_ action: (inout CanvasDocument) throws -> Void) {
        if selectionIsWorkspaceWide { performAcrossDisplays(action); return }
        finishZoomGesture()
        finishStyleEdit()
        guard let active else { status = "Open a drawing session first."; return }
        active.canvas.commitTextIfNeeded()
        active.canvas.cancelGesture()
        do {
            try action(&active.document)
            synchronize()
        } catch { report(error) }
    }

    func applyStyle(where supports: (Element) -> Bool = { _ in true }, _ mutation: (inout Style) -> Void) {
        var changed = style
        let editsMixedSelection = effectiveTool == .select
        mutation(&changed)
        let changesPaint = changed.color != style.color || changed.strokeGradient != style.strokeGradient
        var ownsBatch = false
        do {
            try changed.validate()
            if selectionIsWorkspaceWide && continuousStyleSessions.isEmpty {
                beginStyleEdit()
                ownsBatch = !continuousStyleSessions.isEmpty
            }
            let previous = style
            style = changed
            if effectiveTool != .select {
                preferences.toolStyles[effectiveTool.rawValue] = changed
                if sharesToolStyles && effectiveTool.sharesStyle { shareStyleChange(from: previous, to: changed) }
            }
            persistPreferences()
            if let active, let id = active.canvas.editingShapeLabelID,
               let label = active.document.elements.first(where: { $0.id == id })?.label {
                try active.document.update(id) {
                    $0.label = ShapeLabel(text: label.text, style: changed,
                                          followsStroke: changesPaint ? false : label.followsStroke)
                }
                active.canvas.applyTextStyle(changed)
            } else {
                active?.canvas.applyTextStyle(changed)
                for session in selectionSessions where !session.document.selection.isEmpty {
                    try session.document.editSelection { element in
                        guard supports(element), !(editsMixedSelection && element.tool.isProtection) else { return }
                        let previous = element.style
                        mutation(&element.style)
                        if element.tool == .text,
                           previous.fontSize != element.style.fontSize || previous.fontName != element.style.fontName || previous.bold != element.style.bold {
                            let size = NativeTextFont.contentSize(element.text, font: NativeTextFont.resolve(element.style))
                            element.frame = Rect(x: element.frame.x, y: element.frame.y, width: size.width, height: size.height)
                        }
                    }
                }
            }
            if ownsBatch { finishStyleEdit() } else { synchronize() }
        } catch {
            for session in continuousStyleSessions { session.document.cancel() }
            continuousStyleSessions = []
            report(error)
            synchronize()
        }
    }

    /// With "Share style between tools" on, copy what just changed to every other drawing tool.
    private func shareStyleChange(from previous: Style, to changed: Style) {
        for other in Tool.allCases where other != effectiveTool && other.sharesStyle {
            let current = preferences.toolStyles[other.rawValue] ?? .defaults(for: other)
            var shared = current
            shared.adoptSharedChanges(from: previous, to: changed, supportsFill: other.supportsFill)
            if shared != current { preferences.toolStyles[other.rawValue] = shared }
        }
    }

    var editingLabelFollowsStroke: Bool {
        guard let id = active?.canvas.editingShapeLabelID else { return false }
        return active?.document.elements.first(where: { $0.id == id })?.label?.followsStroke == true
    }

    func matchLabelToStroke() {
        guard let active, let id = active.canvas.editingShapeLabelID,
              let owner = active.document.elements.first(where: { $0.id == id }), let label = owner.label else { return }
        do {
            let matched = ShapeLabel(text: label.text, style: label.style, followsStroke: true)
            try active.document.update(id) { $0.label = matched }
            active.canvas.applyTextStyle(matched.resolvedStyle(for: owner.style))
            synchronize()
        } catch { report(error) }
    }

    func synchronize() {
        let operationIDs = sessions.values.reduce(into: Set<UUID>()) { $0.formUnion($1.document.historyOperationIDs) }
        workspaceHistory = workspaceHistory.filter { operationIDs.contains($0.key) }
        for session in sessions.values {
            let settings = session.document.presentation
            let entrance = session === active && settings.mode == .screen ? screenZoomEntrance : nil
            if entrance != nil { screenZoomEntrance = nil }
            let exits = settings.mode == .off && session.presentation?.presentationMode == .screen && animatesScreenZoom()
            let changed = session.presentation?.apply(settings, enteringFrom: entrance, animatesExit: exits) ?? false
            if !workspaceHidden && changed && settings.mode != .off { session.presentation?.retry() }
        }
        magnifierEnabled = active?.document.presentation.mode == .lens
        let screenMode = active?.document.presentation.mode == .screen
        if !screenMode && zoomStartState == .active { zoomStart.cancel() }
        if screenZoomEnabled != screenMode {
            screenZoomEnabled = screenMode
            // A zoom-out ramp keeps window levels until the display returns to 1x.
            if screenMode || active?.presentation?.isExitingScreenZoom != true { onScreenZoomChanged?(screenMode) }
        }
        selectionCount = selectedElements.count
        canUndo = (active?.document.canUndo ?? false) || (active?.document.isEditing ?? false)
        canRedo = (active?.document.canRedo ?? false) && active?.document.isEditing != true
        if let labelStyle = active?.canvas.editingShapeLabelStyle {
            style = labelStyle
        } else if let selected = editableStyleSelection.first ?? selectedElements.first {
            style = selected.style
        } else if selectionCount == 0 {
           style = preferences.toolStyles[tool.rawValue] ?? .defaults(for: tool)
        }
        refreshCanvas()
        active?.canvas.refreshToolCursor()
    }

    var effectiveTool: Tool {
        if active?.canvas.editingShapeLabelID != nil { return .text }
        if !selectedElements.isEmpty {
            let selected = editableStyleSelection.isEmpty ? selectedElements : editableStyleSelection
            guard let first = selected.first else { return tool }
            return selected.allSatisfy { $0.tool == first.tool } ? first.tool : .select
        }
        return tool
    }

    var editableStyleSelection: [Element] {
        selectedElements.filter { !$0.locked }
    }

    var selectionStyleIsLocked: Bool { selectionCount > 0 && editableStyleSelection.isEmpty }
    var selectionSupportsFill: Bool {
        selectionCount == 0 ? effectiveTool.supportsFill : editableStyleSelection.contains { $0.tool.supportsFill }
    }

    func beginStyleEdit() {
        let targets = selectionSessions
        guard continuousStyleSessions.isEmpty, !selectedElements.isEmpty,
              targets.allSatisfy({ !$0.document.isEditing }) else { return }
        for session in targets { session.document.begin() }
        continuousStyleSessions = targets
    }

    func finishStyleEdit() {
        let targets = continuousStyleSessions
        guard !targets.isEmpty else { return }
        continuousStyleSessions = []
        do {
            if !targets.contains(where: { $0.document.hasPendingChanges }) {
                for session in targets where session.document.isEditing { try session.document.commit() }
            } else {
                try commitWorkspaceDocuments(targets.map(\.document), to: targets)
            }
        } catch {
            for session in targets { session.document.cancel() }
            report(error)
        }
        synchronize()
    }

    func setActivationShortcut(_ value: ActivationShortcut) {
        if let owner = globalSettingsOwner {
            owner.setActivationShortcut(value)
            preferences.activationShortcut = owner.preferences.activationShortcut
            errorMessage = owner.errorMessage
            return
        }
        do {
            var updated = preferences; updated.activationShortcut = value
            try updated.validate()
            try onActivationShortcutChanged?(value)
            preferences = updated
            persistPreferences()
            status = "\(value.label) opens drawing or hides the tools. \((preferences.hideShortcut ?? .hideTools).label) always hides them."
        } catch { report(error) }
    }

    func assignToolShortcut(_ value: String, tool: Tool) {
        if let owner = globalSettingsOwner {
            owner.assignToolShortcut(value, tool: tool)
            preferences.toolShortcuts = owner.preferences.toolShortcuts
            errorMessage = owner.errorMessage
            return
        }
        do { try preferences.assignShortcut(value, to: tool); persistPreferences() }
        catch { report(error) }
    }

    enum ShortcutBindingResult: Equatable {
        case assigned(displaced: ShortcutCommand?)
        case cleared
        case failed(String)
    }

    func shortcut(for command: ShortcutCommand) -> LocalShortcut? { preferences.shortcut(for: command) }
    func shortcutLabel(for tool: Tool) -> String { preferences.shortcut(for: .tool(tool))?.label ?? "" }

    /// Gives a key to a tool or toolbar button. Errors stay with the caller so Settings can show them in place.
    @discardableResult
    func bindShortcut(_ shortcut: LocalShortcut?, to command: ShortcutCommand) -> ShortcutBindingResult {
        if let owner = globalSettingsOwner {
            let result = owner.bindShortcut(shortcut, to: command)
            preferences = owner.preferences
            return result
        }
        do {
            var updated = preferences
            let displaced = try updated.bind(shortcut, to: command)
            preferences = updated
            persistPreferences()
            if let shortcut {
                status = displaced.map { "\(shortcut.label) is now the key for \(command.label). \($0.label) has no key now." } ??
                    "\(shortcut.label) is now the key for \(command.label)."
                return .assigned(displaced: displaced)
            }
            status = "\(command.label) has no key."
            return .cleared
        } catch let CanvasError.invalidDocument(message) {
            return .failed(message)
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    func resetLocalShortcuts() {
        if let owner = globalSettingsOwner {
            owner.resetLocalShortcuts()
            preferences = owner.preferences
            return
        }
        preferences.resetLocalShortcuts()
        persistPreferences()
        status = "Tool keys are back to their defaults."
    }

    /// Puts back tool and button keys from an earlier snapshot, for Undo in Settings.
    func restoreLocalShortcuts(from snapshot: WorkspacePreferences) {
        if let owner = globalSettingsOwner {
            owner.restoreLocalShortcuts(from: snapshot)
            preferences = owner.preferences
            return
        }
        var updated = preferences
        updated.toolShortcuts = snapshot.toolShortcuts
        updated.commandShortcuts = snapshot.commandShortcuts
        do {
            try updated.validate()
            preferences = updated
            persistPreferences()
            status = "Keys restored."
        } catch { report(error) }
    }

    func performShortcutAction(_ action: ShortcutAction) {
        switch action {
        case .properties:
            let next = ToolbarPropertiesMode(model: self).next
            next.apply(to: self)
            status = "Properties: \(next.rawValue.lowercased())."
        case .board:
            setBoard(board == .transparent ? .light : board == .light ? .dark : .transparent)
            status = "\(board.rawValue) background."
        case .spotlight:
            spotlight.toggle()
            status = spotlight ? "Spotlight is on." : "Spotlight is off."
        case .cursorHalo:
            cursorHalo.toggle()
            status = cursorHalo ? "Cursor halo is on." : "Cursor halo is off."
        case .pointerLens:
            toggleMagnifier()
        case .smartDraw:
            smartDraw.toggle()
            status = smartDraw ? "Smart draw is on." : "Smart draw is off."
        case .shareRegion:
            openRegionSharing()
        case .clearAll:
            clearCanvas()
        }
    }

    var toolbarSize: ToolbarSize { preferences.toolbarSize ?? .expanded }

    func setToolbarSize(_ size: ToolbarSize) {
        if let owner = globalSettingsOwner {
            owner.setToolbarSize(size)
            preferences.toolbarSize = owner.preferences.toolbarSize
            return
        }
        preferences.toolbarSize = size == .expanded ? nil : size
        persistPreferences()
        onToolbarLayoutChanged?()
    }

    var sharesToolStyles: Bool { preferences.shareToolStyles == true }

    func setShareToolStyles(_ shared: Bool) {
        if let owner = globalSettingsOwner {
            owner.setShareToolStyles(shared)
            preferences.shareToolStyles = owner.preferences.shareToolStyles
            return
        }
        preferences.shareToolStyles = shared ? true : nil
        persistPreferences()
        status = shared ? "Your next style change applies to every drawing tool." : "Each tool keeps its own style."
    }

    func deleteSelected() {
        guard !selectionIsLocked else { status = "The selection is locked. Unlock it from Selection actions before deleting."; return }
        perform { try $0.deleteSelection(includingProtection: true) }
    }

    func undo() {
        finishZoomGesture()
        finishStyleEdit()
        guard let active else { return }
        if active.canvas.hasActiveEdit {
            active.canvas.cancelGesture()
            active.canvas.renderer.reset()
            synchronize()
        } else {
            if applyWorkspaceHistory(redo: false) { return }
            active.canvas.commitTextIfNeeded()
            let generation = active.document.generation
            active.document.undo()
            if active.document.generation != generation { undoSerial &+= 1 }
            active.canvas.renderer.reset()
            synchronize()
        }
    }

    func redo() {
        guard active?.canvas.hasActiveEdit != true else {
            status = "Finish or cancel the current edit before redoing."
            return
        }
        finishStyleEdit()
        if applyWorkspaceHistory(redo: true) { return }
        active?.document.redo()
        active?.canvas.renderer.reset()
        synchronize()
    }

    var canDeleteCurrent: Bool {
        guard let active else { return false }
        if active.canvas.hasActiveEdit { return true }
        if selectionCount > 0 {
            return !editableStyleSelection.isEmpty
        }
        return canClearCanvas
    }

    var deleteActionLabel: String {
        guard let active else { return "Delete" }
        if active.canvas.hasActiveEdit { return "Cancel current drawing" }
        if selectionCount > 0 {
            if selectionIsLocked { return "Unlock selection to delete" }
            return "Delete selection"
        }
        if active.document.presentation.mode != .off { return "Clear canvas and stop zoom" }
        if !active.document.elements.isEmpty { return "Clear canvas" }
        return "Delete"
    }

    var deleteActionSymbol: String {
        guard let active else { return "trash" }
        if active.canvas.hasActiveEdit { return "xmark.circle" }
        return "trash"
    }

    var canClearCanvas: Bool {
        guard let active else { return false }
        let targets = active.sourceImage == nil ? desktopSessions : [active]
        return targets.contains { !$0.document.elements.isEmpty || $0.canvas.hasActiveEdit || $0.document.presentation.mode != .off }
    }

    func clearCanvas() {
        performAcrossDisplays { try $0.clearAll() }
    }

    func clearDrawingsKeepingProtection() {
        performAcrossDisplays { try $0.clearAnnotations() }
    }

    private func performAcrossDisplays(_ mutation: (inout CanvasDocument) throws -> Void) {
        finishZoomGesture()
        finishStyleEdit()
        guard let active else { return }
        let targets = active.sourceImage == nil ? desktopSessions : [active]
        for session in targets {
            session.canvas.commitTextIfNeeded()
            session.canvas.cancelGesture()
        }
        do {
            let documents = try targets.map { session in
                var document = session.document
                document.begin()
                try mutation(&document)
                return document
            }
            guard zip(targets, documents).contains(where: {
                $0.0.document.elements != $0.1.elements || $0.0.document.presentation != $0.1.presentation
            }) else { synchronize(); return }
            try commitWorkspaceDocuments(documents, to: targets)
            synchronize()
        } catch { report(error) }
    }

    private func commitWorkspaceDocuments(_ pending: [CanvasDocument], to targets: [OverlaySession]) throws {
        var documents = pending
        let operationID = targets.count > 1 ? UUID() : nil
        for index in documents.indices {
            try documents[index].commit(operationID: operationID)
            guard documents[index].canUndo, operationID == nil || documents[index].undoOperationID == operationID else {
                throw WorkspaceHistoryFailure.undoBudget
            }
        }
        if let operationID {
            workspaceHistory[operationID] = WorkspaceHistoryEntry(documents: Set(documents.map(\.identity)),
                                                                 globalSelection: selectionIsWorkspaceWide)
        }
        for (session, document) in zip(targets, documents) {
            session.document = document
            session.canvas.renderer.reset()
        }
    }

    private func applyWorkspaceHistory(redo: Bool) -> Bool {
        guard let active,
              let operationID = redo ? active.document.redoOperationID : active.document.undoOperationID,
              let entry = workspaceHistory[operationID] else { return false }
        let targets = sessions.values.filter { entry.documents.contains($0.document.identity) }
        guard targets.count == entry.documents.count, targets.allSatisfy({
            !$0.canvas.hasActiveEdit && !$0.document.isEditing &&
                (redo ? $0.document.redoOperationID : $0.document.undoOperationID) == operationID
        }) else { report(WorkspaceHistoryFailure.changed); return true }
        for session in targets {
            if redo { session.document.redo() } else { session.document.undo() }
            session.canvas.renderer.reset()
        }
        if !redo { undoSerial &+= 1 }
        selectionIsWorkspaceWide = entry.globalSelection
        if entry.globalSelection { tool = .select }
        synchronize()
        return true
    }

    func deleteCurrent() {
        guard let active else { return }
        if active.canvas.hasActiveEdit {
            active.canvas.cancelGesture()
            synchronize()
            return
        }
        if selectionCount > 0 { deleteSelected() }
        else { clearCanvas() }
    }

    /// Copies the selected objects. Returns false when nothing is selected, so the key can go elsewhere.
    @discardableResult
    func copySelection(cut: Bool = false) -> Bool {
        guard mode == .draw, !workspaceHidden, let active else { return false }
        finishStyleEdit()
        if active.canvas.hasActiveEdit { active.canvas.commitTextIfNeeded() }
        // Each display stores local points; move other displays' objects into the active canvas's space.
        let copied = selectionSessions.flatMap { session in
            let shift = Point(Double(session.panel.frame.minX - active.panel.frame.minX),
                              Double(active.panel.frame.maxY - session.panel.frame.maxY))
            return session.document.elements.filter { session.document.selection.contains($0.id) }.map {
                var element = $0
                if session !== active { element.translate(by: shift) }
                return element
            }
        }
        guard !copied.isEmpty else { return false }
        if cut && copied.contains(where: \.locked) {
            status = "Some selected objects are locked. Unlock them before cutting, or use Copy."
            return true
        }
        do {
            let data = try DrawingFile(document: CanvasDocument(elements: copied)).data()
            imagePasteboard.clearContents()
            guard imagePasteboard.setData(data, forType: Self.annotationPasteboardType) else {
                throw CanvasError.invalidDocument("The objects could not be copied. Try Copy again.")
            }
            pasteCascade = nil
            if cut { deleteSelected() }
            let noun = copied.count == 1 ? "1 object" : "\(copied.count) objects"
            status = cut ? "Cut \(noun). Press Command-V to paste." : "Copied \(noun). Press Command-V to paste."
        } catch { report(error) }
        return true
    }

    /// Pastes copied annotation objects, or an image when the clipboard holds no objects.
    func paste() {
        guard let data = imagePasteboard.data(forType: Self.annotationPasteboardType) else {
            pasteImage()
            return
        }
        do {
            let copied = try DrawingFile.read(data).elements
            guard !copied.isEmpty else { return }
            if let active { useLocalSelection(in: active) }
            if workspaceHidden { revealWorkspaceSurfaces(); onShowToolbar?() }
            if active == nil || mode != .draw { beginDrawing(recordsUsage: false) }
            guard let active else { throw CanvasError.invalidDocument("No canvas is available.") }
            active.canvas.commitTextIfNeeded()
            active.canvas.cancelGesture()
            let origin = pasteOrigin(for: Rect.enclosing(copied.flatMap { [$0.frame.origin, Point($0.frame.maxX, $0.frame.maxY)] }),
                                     in: active)
            try active.document.insertCopies(of: copied, offset: origin.offset)
            pasteCascade = PasteCascade(changeCount: imagePasteboard.changeCount, document: active.document.identity,
                                        origin: origin.target, pointer: origin.pointer)
            tool = .select
            synchronize()
            status = copied.count == 1 ? "Pasted 1 object." : "Pasted \(copied.count) objects."
        } catch { report(error) }
    }

    /// Pastes at the pointer when it is away from the copied objects; otherwise steps 18 points down and right.
    private func pasteOrigin(for bounds: Rect, in session: OverlaySession) -> (offset: Point, target: Point, pointer: Point?) {
        let canvas = session.canvas
        let local = Point(canvas.convert(session.panel.convertPoint(fromScreen: screenPointer()), from: nil))
        let pointer = Rect(canvas.bounds).contains(local) ? local : nil
        let previous = pasteCascade.flatMap {
            $0.changeCount == imagePasteboard.changeCount && $0.document == session.document.identity ? $0 : nil
        }
        var target: Point
        if let pointer, !bounds.contains(pointer, padding: 8), previous?.pointer != pointer {
            target = pointer - Point(bounds.width / 2, bounds.height / 2)
        } else {
            target = (previous?.origin ?? bounds.origin) + Point(18, 18)
        }
        let size = canvas.bounds.size
        target = Point(max(0, min(target.x, Double(size.width) - bounds.width)),
                       max(0, min(target.y, Double(size.height) - bounds.height)))
        return (target - bounds.origin, target, pointer)
    }

    func pasteImage() {
        do {
            try insertImage(ImageClipboard.read(imagePasteboard))
        } catch { report(error) }
    }

    func insertImage(_ bytes: Data) throws {
            if let active { useLocalSelection(in: active) }
            let image = try RasterAsset.decode(bytes)
            let png = try PNGExporter.encode(image)
            if workspaceHidden { revealWorkspaceSurfaces(); onShowToolbar?() }
            if active == nil || mode != .draw { beginDrawing(recordsUsage: false) }
            guard let active else { throw CanvasError.invalidDocument("No canvas is available.") }
            let local = active.canvas.convert(active.panel.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil)
            let scale = min(1, 600 / Double(image.width), 450 / Double(image.height))
            let x = max(0, min(local.x, active.canvas.bounds.width - Double(image.width) * scale))
            let y = max(0, min(local.y, active.canvas.bounds.height - Double(image.height) * scale))
            var element = Element(tool: .image,
                                  frame: Rect(x: x, y: y, width: Double(image.width) * scale, height: Double(image.height) * scale),
                                  style: preferences.toolStyles[Tool.image.rawValue] ?? Style())
            element.imagePNG = png
            try active.document.add(element)
            active.document.select([element.id]); tool = .select; synchronize()
            status = "Image pasted as an editable layer. Resize, rotate, group or delete it like other objects."
            usageMetrics.record(.imageInsertion)
    }

    func setHideShortcut(_ value: ActivationShortcut) {
        if let owner = globalSettingsOwner {
            owner.setHideShortcut(value)
            preferences.hideShortcut = owner.preferences.hideShortcut
            errorMessage = owner.errorMessage
            return
        }
        do {
            var updated = preferences; updated.hideShortcut = value
            try updated.validate()
            try onHideShortcutChanged?(value)
            preferences = updated; persistPreferences()
        } catch { report(error) }
    }

    func setZoomShortcut(_ value: ActivationShortcut) {
        if let owner = globalSettingsOwner { owner.setZoomShortcut(value); return }
        do {
            var updated = preferences
            updated.zoomShortcut = value
            try updated.validate()
            try onZoomShortcutChanged?(value)
            preferences = updated
            persistPreferences()
        } catch { report(error) }
    }

    func setResumeShortcut(_ value: ActivationShortcut) {
        if let owner = globalSettingsOwner {
            owner.setResumeShortcut(value)
            errorMessage = owner.errorMessage
            return
        }
        do {
            var updated = preferences
            updated.resumeShortcut = value
            try updated.validate()
            try onResumeShortcutChanged?(value)
            preferences = updated
            persistPreferences()
        } catch { report(error) }
    }

    func setMenuBarVisible(_ visible: Bool) {
        if let owner = globalSettingsOwner {
            owner.setMenuBarVisible(visible)
            preferences.showMenuBarIcon = owner.preferences.showMenuBarIcon
            errorMessage = owner.errorMessage
            return
        }
        guard visible || hotkeyRegistered else {
            report(CanvasError.invalidDocument("Register a working activation shortcut before hiding the menu-bar icon."))
            return
        }
        preferences.showMenuBarIcon = visible
        persistPreferences()
        onMenuBarVisibilityChanged?(visible)
    }

    func setToolbarDockEdge(_ edge: ToolbarDockEdge) {
        if let owner = globalSettingsOwner { owner.setToolbarDockEdge(edge); return }
        preferences.toolbarDockEdge = edge
        persistPreferences()
        onToolbarLayoutChanged?()
    }

    func dockToolbar(to edge: ToolbarDockEdge) {
        guard edge != .automatic else { return }
        if let owner = globalSettingsOwner { owner.dockToolbar(to: edge); return }
        preferences.toolbarOrientation = edge == .left || edge == .right ? .vertical : .horizontal
        preferences.toolbarDockEdge = edge
        persistPreferences()
        onToolbarLayoutChanged?()
    }

    func setToolbarOrientation(_ orientation: ToolbarOrientation) {
        if let owner = globalSettingsOwner {
            owner.setToolbarOrientation(orientation)
            preferences.toolbarOrientation = owner.preferences.toolbarOrientation
            return
        }
        preferences.toolbarOrientation = orientation
        if orientation == .vertical, preferences.toolbarDockEdge == .top || preferences.toolbarDockEdge == .bottom {
            preferences.toolbarDockEdge = .automatic
        } else if orientation == .horizontal, preferences.toolbarDockEdge == .left || preferences.toolbarDockEdge == .right {
            preferences.toolbarDockEdge = .automatic
        }
        persistPreferences()
        onToolbarLayoutChanged?()
    }

    func setCursorStyle(_ style: ToolCursorStyle) {
        if let owner = globalSettingsOwner {
            owner.setCursorStyle(style)
            preferences.cursorStyle = owner.preferences.cursorStyle
            active?.canvas.refreshToolCursor()
            return
        }

        preferences.cursorStyle = style
        persistPreferences()
        active?.canvas.refreshToolCursor()
    }

    func setShortcutHints(_ visible: Bool) {
        if let owner = globalSettingsOwner { owner.setShortcutHints(visible); return }
        preferences.showShortcutHints = visible
        persistPreferences()
        refreshCanvas()
    }

    /// Tool-only toolbars, as older profiles store them: any effect means the shared Effects button.
    func configureToolbar(_ tools: [Tool]) {
        configureToolbar(items: Self.normalizedTools(tools).map(ShortcutCommand.tool))
    }

    func configureToolbar(items: [ShortcutCommand]) {
        if let owner = globalSettingsOwner {
            owner.configureToolbar(items: items)
            preferences.toolbarTools = owner.preferences.toolbarTools
            preferences.toolbarItems = owner.preferences.toolbarItems
            errorMessage = owner.errorMessage
            return
        }
        do {
            var updated = preferences
            let tools = items.compactMap { if case .tool(let tool) = $0 { tool } else { nil } }
            updated.toolbarTools = tools.map(\.rawValue)
            // Keep the older format alone when it can describe this toolbar.
            updated.toolbarItems = tools == Self.normalizedTools(tools) && tools.count == items.count ? nil : items.map(\.id)
            try updated.validate(); preferences = updated; persistPreferences()
            onToolbarLayoutChanged?()
        } catch { report(error) }
    }

    func isOn(_ action: ShortcutAction) -> Bool {
        switch action {
        case .spotlight: spotlight
        case .cursorHalo: cursorHalo
        case .pointerLens: magnifierEnabled
        case .smartDraw: smartDraw
        case .shareRegion: regionSharingState.isCapturing
        case .properties, .board, .clearAll: false
        }
    }

    func exportPreferences() {
        let dialog = NSSavePanel()
        dialog.allowedContentTypes = [.json]
        dialog.nameFieldStringValue = "Annotator profile.json"
        guard dialog.runModal() == .OK, let url = dialog.url else { return }
        do { try preferences.data().write(to: url, options: .atomic) }
        catch { report(error) }
    }

    func importPreferences() {
        if let owner = globalSettingsOwner {
            owner.importPreferences()
            preferences = owner.preferences
            style = preferences.toolStyles[tool.rawValue] ?? .defaults(for: tool)
            smartDraw = preferences.smartDraw
            spotlightRadius = preferences.spotlightRadius
            errorMessage = owner.errorMessage
            synchronize()
            return
        }
        let dialog = NSOpenPanel()
        dialog.allowedContentTypes = [.json]
        guard dialog.runModal() == .OK, let url = dialog.url else { return }
        do {
            let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize
            guard let size, size <= 256 * 1_024 else { throw CanvasError.invalidDocument("The profile is too large.") }
            let incoming = try WorkspacePreferences.read(Data(contentsOf: url))
            guard incoming.showMenuBarIcon != false || hotkeyRegistered || onMenuBarVisibilityChanged == nil else {
                throw CanvasError.invalidDocument("A working activation shortcut is required before the profile can hide the menu-bar icon.")
            }
            try registerProfileShortcuts(incoming)
            preferences = incoming
            style = incoming.toolStyles[tool.rawValue] ?? .defaults(for: tool)
            smartDraw = incoming.smartDraw
            spotlightRadius = incoming.spotlightRadius
            persistPreferences()
            onMenuBarVisibilityChanged?(incoming.showMenuBarIcon ?? true)
            onThemeChanged?(incoming.theme ?? .system)
            onToolbarLayoutChanged?()
            synchronize()
        } catch { report(error) }
    }

    func registerProfileShortcuts(_ incoming: WorkspacePreferences) throws {
        try incoming.validate()
        try onActivationShortcutChanged?(incoming.activationShortcut)
        do {
            try onHideShortcutChanged?(incoming.hideShortcut ?? .hideTools)
            try onZoomShortcutChanged?(incoming.zoomShortcut ?? .liveZoom)
            try onResumeShortcutChanged?(incoming.resumeShortcut ?? .resumeDrawing)
            try onBreakShortcutChanged?(incoming.breakShortcut ?? .breakTimer)
        } catch {
            let registrationError = error
            do { try onBreakShortcutChanged?(preferences.breakShortcut ?? .breakTimer) }
            catch { report(error) }
            do { try onResumeShortcutChanged?(preferences.resumeShortcut ?? .resumeDrawing) }
            catch { report(error) }
            do { try onZoomShortcutChanged?(preferences.zoomShortcut ?? .liveZoom) }
            catch { report(error) }
            do { try onHideShortcutChanged?(preferences.hideShortcut ?? .hideTools) }
            catch { report(error) }
            do { try onActivationShortcutChanged?(preferences.activationShortcut) }
            catch { report(error) }
            throw registrationError
        }
    }

    private func persistPreferences() {
        guard !applyingSharedPreferences else { return }
        if let owner = globalSettingsOwner {
            let changed = preferences.toolStyles.filter { owner.preferences.toolStyles[$0.key] != $0.value }
            if !changed.isEmpty {
                owner.preferences.toolStyles.merge(changed) { $1 }
                owner.persistPreferences()
            }
            return
        }
        do { defaults.set(try preferences.data(), forKey: "workspace-preferences-v1") }
        catch { report(error) }
    }

    func refreshCanvas() {
        regionSharingController?.workspaceSceneChanged()
        guard !workspaceHidden else { return }
        sessions.values.forEach {
            $0.presentation?.refresh()
            $0.canvas.invalidateContentIfNeeded()
            $0.canvas.refreshAccessibilitySummary()
            $0.canvas.refreshToolCursor()
        }
    }

    func focusDrawingTools() -> Bool {
        guard let action = onFocusToolbar ?? globalSettingsOwner?.onFocusToolbar else { return false }
        action()
        return true
    }

    func toggleMagnifier() {
        if magnifierEnabled { stopZoom(); return }
        guard !hasReferenceImage else { status = "Live magnification is unavailable in a test reference canvas."; return }
        guard ensureCapturePermission() else { return }
        setZoomMode(.lens)
        status = "Live magnifier: desktop clicks pass through unchanged. This is a lens, not full-screen transformed zoom."
    }

    func toggleScreenZoom() {
        requestScreenZoom()
    }

    func toggleScreenZoomFromShortcut() {
        requestScreenZoom(startingAt: 1.5)
    }

    func startScreenZoomFromOptionScroll(_ delta: Double) {
        guard delta.isFinite, delta > 0 else { return }
        requestScreenZoom(startingAt: 1, scrollDelta: delta)
    }

    private func requestScreenZoom(startingAt scale: Double? = nil, scrollDelta: Double? = nil) {
        if screenZoomEnabled { stopZoom(); return }
        guard !hasReferenceImage else { status = "Live zoom is unavailable in a test reference canvas."; return }
        pendingScreenZoomScale = scale
        pendingScreenZoomScrollDelta = scrollDelta
        zoomStart.request()
    }

    private func beginScreenZoom() {
        if workspaceHidden { revealWorkspaceSurfaces(); onShowToolbar?() }
        let initialScroll = pendingScreenZoomScrollDelta
        var scale = pendingScreenZoomScale ?? magnification
        pendingScreenZoomScale = nil
        pendingScreenZoomScrollDelta = nil
        if initialScroll == nil && scale <= 1 { scale = 2 }
        if initialScroll == nil && animatesScreenZoom() { screenZoomEntrance = active?.presentation?.displayedScreenZoomFactor ?? 1 }
        let started = setZoomMode(.screen, scale: scale)
        screenZoomEntrance = nil
        guard started else {
            zoomStart.fail(errorMessage ?? "A drawing display could not be prepared. Show the toolbar and retry live zoom.")
            return
        }
        interact()
        prepareZoomScrollAccess()
        if let initialScroll { scrollScreenZoom(initialScroll) }
    }

    func prepareZoomScrollAccess() {
        guard !zoomScrollPermissionGranted else { return }
        noteZoomGestures(available: false, error: ZoomInputFailure.permissionRequired)
        guard !requestedZoomScrollPermission else { return }
        requestedZoomScrollPermission = true
        _ = zoomInputPermissions.request()
        status = zoomScrollStatus
    }

    func refreshZoomPermissions() {
        zoomStart.refresh()
        if screenZoomEnabled && !zoomGesturesAvailable && zoomScrollPermissionGranted {
            active?.presentation?.retryZoomGestures()
        }
    }
    func performLocalActivationShortcut() -> Bool {
        if let owner = globalSettingsOwner { return owner.performLocalActivationShortcut() }
        guard let action = onActivationRequested else { return false }
        action()
        return true
    }
    func resumeDrawing() {
        if let action = onResumeDrawingRequested { action() }
        else { beginDrawing() }
    }
    func noteZoomPresented() { zoomStart.presented() }
    func screenZoomExitFinished() { if !screenZoomEnabled { onScreenZoomChanged?(false) } }
    func noteZoomGestures(available: Bool, error: Error? = nil) {
        if zoomGesturesAvailable != available { zoomGesturesAvailable = available }
        if zoomGestureMessage != error?.localizedDescription { zoomGestureMessage = error?.localizedDescription }
    }

    func cancelPendingZoom() {
        stopZoom()
        status = "Zoom request cancelled."
    }

    func requestZoomGesturePermission() {
        guard !zoomScrollPermissionGranted else {
            active?.presentation?.retryZoomGestures()
            status = zoomGesturesAvailable ? "Option-scroll zoom gestures are enabled." :
                "Accessibility is enabled. Close Settings and restart screen zoom with Control-1."
            return
        }
        requestedZoomScrollPermission = true
        _ = zoomInputPermissions.request()
        guard openZoomGestureSettings() else { return }
        status = "Accessibility is used only for Option-scroll and Escape while screen zoom is active. Enable Annotator, then retry."
    }

    @discardableResult
    func openZoomGestureSettings() -> Bool {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"),
              NSWorkspace.shared.open(url) else { report(ZoomInputFailure.permissionRequired); return false }
        return true
    }

    @discardableResult
    private func setZoomMode(_ mode: PresentationSettings.Mode, scale: Double? = nil) -> Bool {
        let scale = scale ?? magnification
        guard scale.isFinite, (1...16).contains(scale) else {
            report(CanvasError.invalidGeometry)
            return false
        }
        defaultMagnification = scale
        finishZoomGesture()
        if active == nil { beginDrawing(recordsUsage: false); interact() }
        guard let active else { return false }
        active.canvas.commitTextIfNeeded(); active.canvas.cancelGesture()
        active.document.select([])
        do {
            try active.document.setPresentation(PresentationSettings(mode: mode, zoom: scale))
            usageMetrics.record(.zoom)
            synchronize()
            return true
        } catch { report(error); return false }
    }

    func stopZoom() {
        zoomStart.cancel()
        requestedZoomScrollPermission = false
        pendingScreenZoomScale = nil
        pendingScreenZoomScrollDelta = nil
        finishZoomGesture()
        guard let active else { return }
        active.canvas.commitTextIfNeeded(); active.canvas.cancelGesture()
        do {
            var settings = active.document.presentation
            settings.mode = .off
            try active.document.setPresentation(settings)
            synchronize()
        } catch { report(error) }
    }

    func handleZoomEscape() {
        if onDismissMenuBar?() == true { return }
        if active?.canvas.hasTextEditor == true {
            active?.canvas.commitTextIfNeeded()
        } else if active?.canvas.hasActiveEdit == true {
            active?.canvas.cancelGesture()
            synchronize()
        } else { stopZoom(); interact() }
    }

    func handleLocalZoomScroll(_ event: NSEvent) -> Bool {
        guard !workspaceHidden,
              active?.document.presentation.mode == .screen,
              let event = event.cgEvent, let delta = ZoomGestureMonitor.zoomScrollDelta(event) else { return false }
        scrollScreenZoom(delta)
        return true
    }

    func scrollScreenZoom(_ delta: Double) {
        if !workspaceHidden, delta.isFinite, delta > 0, active?.document.presentation.mode != .screen,
           active?.presentation?.isExitingScreenZoom == true, let current = active?.presentation?.magnification {
            requestScreenZoom(startingAt: current, scrollDelta: delta)
            return
        }
        guard !workspaceHidden, delta.isFinite, delta != 0, let active, active.document.presentation.mode == .screen else { return }
        if zoomGestureSession == nil {
            finishStyleEdit()
            active.canvas.commitTextIfNeeded(); active.canvas.cancelGesture()
            active.document.begin()
            zoomGestureSession = active
        }
        do {
            var settings = active.document.presentation
            // Scroll adjusts the requested zoom; a running entrance keeps easing toward the new value.
            settings.zoom = min(16, max(1, settings.zoom * exp(min(80, max(-80, delta)) * 0.012)))
            try active.document.setPresentation(settings)
            // Scroll changes only the viewport. Full synchronization rebuilds
            // unrelated UI and accessibility state on every input event.
            if active.presentation?.retargetZoomRamp(to: settings.zoom) != true {
                active.presentation?.magnification = settings.zoom
            }
            active.presentation?.refresh()
            active.canvas.invalidateContentIfNeeded()
            if !canUndo { canUndo = true }
            if canRedo { canRedo = false }
            objectWillChange.send()
        } catch { report(error) }
        zoomGesture?.cancel()
        zoomGesture = Task { [weak self] in
            do { try await Task.sleep(for: .milliseconds(180)) } catch { return }
            self?.finishZoomGesture()
        }
    }

    func finishZoomGesture() {
        zoomGesture?.cancel(); zoomGesture = nil
        guard let session = zoomGestureSession else { return }
        zoomGestureSession = nil
        do { if session.document.isEditing { try session.document.commit() } }
        catch { report(error) }
        synchronize()
    }

    func retryLiveEffects() {
        if failedPresentationMode != .off {
            if active?.document.presentation.mode == failedPresentationMode { active?.presentation?.retry(); return }
            if failedPresentationMode == .lens { toggleMagnifier() }
            else { toggleScreenZoom() }
            return
        }
        guard ensureCapturePermission() else { return }
        active?.presentation?.retry()
    }

    func suspendPresentation(cancelPendingRequests: Bool = true) {
        regionSharingController?.stopForSafety("The desktop changed or the Mac paused. Check the region, then start sharing.")
        onSuspendPresentation?()
        if cancelPendingRequests { zoomStart.cancel() }
        finishZoomGesture()
        for session in sessions.values { session.canvas.commitTextIfNeeded() }
        for session in sessions.values { session.document.stopPresentationWithoutHistory() }
        for session in sessions.values { session.presentation?.suspend() }
        interact()
        if !zoomStart.isWaiting {
            status = "Capture paused after a system transition. Existing effect regions are covered opaquely; select an effect tool to resume."
        }
    }

    func requestCapturePermission() {
        guard !DisplayCapture.hasPermission else {
            status = "Screen Recording is on. If screen access still fails, stop screen sharing before quitting: quitting removes covers. Save your board, then quit and reopen this copy of Annotator."
            return
        }
        if DisplayCapture.requestPermission() { status = "Screen Recording is on. Try screen zoom or the live effect again." }
        else {
            status = "Allow this copy of Annotator to see the screen. It does not record video. If macOS asks you to reopen it, stop screen sharing and save your board first: quitting removes covers."
            openCaptureSettings()
        }
    }

    func noteCaptureFrame() {
        if !captureVerified { captureVerified = true }
        if captureFailureMessage != nil { captureFailureMessage = nil }
        failedPresentationMode = .off
    }

    func reportCaptureFailure(_ error: Error, presentationMode: PresentationSettings.Mode = .off) {
        if presentationMode == .screen { zoomStart.fail(error.localizedDescription) }
        captureVerified = false
        failedPresentationMode = presentationMode
        captureFailureMessage = error.localizedDescription
        if let failure = error as? CaptureFailure, case .desktopLocked = failure {
            status = failure.localizedDescription
            return
        }
        let message = DisplayCapture.hasPermission
            ? "Screen Recording is on, but screen access failed. If you just allowed access or updated the app, stop screen sharing and save your board before reopening this copy: quitting removes covers."
            : "Screen Recording is off for this copy of Annotator. Allow it in System Settings. If macOS asks you to reopen the app, stop screen sharing and save your board first: quitting removes covers."
        status = message
        if !workspaceHidden { onShowToolbar?() }
    }

    func openCaptureSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"),
              NSWorkspace.shared.open(url) else {
            report(CaptureFailure.streamStopped("System Settings could not be opened. Open Privacy & Security > Screen Recording manually."))
            return
        }
    }

    @discardableResult
    private func ensureCapturePermission() -> Bool {
        if DisplayCapture.hasPermission { return true }
        requestCapturePermission()
        return DisplayCapture.hasPermission
    }

    func importScreenshot() {
        stopZoom()
        let dialog = NSOpenPanel()
        dialog.allowedContentTypes = [.png, .jpeg, .heic, .tiff]
        dialog.title = "Insert image into desktop drawing"
        dialog.prompt = "Insert"
        guard dialog.runModal() == .OK, let url = dialog.url else { return }
        do {
            let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize
            guard let size, size <= 128 * 1_024 * 1_024 else { throw RenderFailure.invalidSize }
            let data = try Data(contentsOf: url, options: .mappedIfSafe)
            try insertImage(data)
        } catch { report(error) }
    }

    #if DEBUG
    func adoptReferenceSession(_ session: OverlaySession) {
        precondition(ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil)
        active = session; sessions[UInt32.max] = session
        mode = .draw
        synchronize()
    }
    #endif

    func clearProtection() {
        performAcrossDisplays { try $0.clearProtection() }
    }

    func saveBoard() {
        active?.canvas.commitTextIfNeeded()
        guard let active else { status = "Open a drawing session first."; return }
        let dialog = NSSavePanel()
        if active.document.hasProtection && active.document.elements.contains(where: { $0.tool == .image }) {
            dialog.message = "Editable boards retain original images. Share a flattened PNG for censored content."
        }
        dialog.nameFieldStringValue = "Explanation.annotator"
        dialog.allowedContentTypes = [UTType(filenameExtension: "annotator") ?? .json]
        guard dialog.runModal() == .OK, let url = dialog.url else { return }
        do {
            try DrawingFile(document: active.document).data().write(to: url, options: .atomic)
            usageMetrics.record(.boardSave)
            status = "Drawing layers and pasted images saved locally. No ambient desktop capture was added."
        } catch { report(error) }
    }

    func openBoard() {
        let dialog = NSOpenPanel()
        dialog.allowedContentTypes = [UTType(filenameExtension: "annotator") ?? .json, .json]
        dialog.allowsMultipleSelection = false
        guard dialog.runModal() == .OK, let url = dialog.url else { return }
        openBoard(from: url)
    }

    func openBoard(from url: URL) {
        do {
            let values = try url.resourceValues(forKeys: [.fileSizeKey])
            guard let size = values.fileSize, size <= 128 * 1_024 * 1_024 else {
                throw CanvasError.invalidDocument("The board exceeds the safe import budget.")
            }
            let document = try DrawingFile.read(Data(contentsOf: url))
            for element in document.elements where element.tool == .image {
                guard let bytes = element.imagePNG else { throw RenderFailure.imageUnavailable }
                _ = try RasterAsset.decode(bytes)
            }
            beginDrawing(recordsUsage: false)
            guard let active else { return }
            if active.document.hasProtection {
                throw CanvasError.invalidDocument("Remove existing protective covers explicitly before replacing this board.")
            }
            active.document = document
            usageMetrics.record(.boardOpen)
            active.canvas.renderer.reset()
            synchronize()
            status = "Board opened. Check cover positions before sharing."
        } catch {
            report(CanvasError.invalidDocument(
                "Choose another Annotator board. Your current drawing has not changed. \(error.localizedDescription)"))
        }
    }

    func exportPNG() {
        active?.canvas.commitTextIfNeeded()
        guard let active else { status = "Open a drawing session first."; return }
        let dialog = NSSavePanel()
        dialog.allowedContentTypes = [.png]
        dialog.nameFieldStringValue = "Explanation.png"
        guard dialog.runModal() == .OK, let url = dialog.url else { return }
        do {
            let background: RGBA? = active.board == .light ? .white : active.board == .dark ? .ink : nil
            let data = try PNGExporter.data(elements: active.document.elements, size: active.canvas.bounds.size,
                                            scale: active.panel.backingScaleFactor, background: background)
            try data.write(to: url, options: .atomic)
            usageMetrics.record(.pngExport)
            status = "Annotations exported. This PNG does not contain desktop pixels."
        } catch { report(error) }
    }

    func report(_ error: Error) {
        Logger(subsystem: "com.dorlugasigal.annotator", category: "app")
            .error("Operation failed: \(error.localizedDescription, privacy: .private)")
        errorMessage = error.localizedDescription
        status = error.localizedDescription
    }

    private func updatePointerEffects() {
        stopPointerMonitors()
        guard !workspaceHidden else { return }
        if spotlight || cursorHalo {
            if active == nil { beginDrawing(recordsUsage: false); interact() }
            let events: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .leftMouseDown, .rightMouseDown]
            pointerMonitor = NSEvent.addGlobalMonitorForEvents(matching: events) {
                [weak self] event in MainActor.assumeIsolated { self?.notePointerEvent(event.type) }
            }
            localPointerMonitor = NSEvent.addLocalMonitorForEvents(matching: events) {
                [weak self] event in MainActor.assumeIsolated { self?.notePointerEvent(event.type) }; return event
            }
        }
        refreshCanvas()
    }

    func notePointerEvent(_ type: NSEvent.EventType) {
        let clicked = type == .leftMouseDown || type == .rightMouseDown
        if clicked {
            lastPointerClick = ProcessInfo.processInfo.systemUptime
            cursorClickLocation = NSEvent.mouseLocation
        }
        else { lastPointerMovement = ProcessInfo.processInfo.systemUptime }
        refreshPointerEffectsOnly()
        guard cursorHalo else { return }
        if clicked {
            pointerClickAnimation.start(animated: cursorHighlightSettings.resolvedClickEffect != .off &&
                !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion) { [weak self] in
                self?.refreshPointerEffectsOnly()
            }
            return
        }
        guard cursorHighlightSettings.visibility == .moving else { return }
        pointerExpiry?.cancel()
        pointerExpiry = Task { [weak self] in
            do { try await Task.sleep(for: .milliseconds(750)) }
            catch { return }
            self?.refreshPointerEffectsOnly()
        }
    }

    func refreshPointerEffectsOnly() {
        guard !workspaceHidden else { return }
        regionSharingController?.refreshPointer()
        for session in sessions.values {
            if session.sourceImage == nil { session.presentation?.refreshSelectionOverlay() }
            else { session.canvas.needsDisplay = true }
        }
    }

    private func stopPointerMonitors() {
        pointerExpiry?.cancel(); pointerExpiry = nil
        pointerClickAnimation.stop()
        cursorClickLocation = nil
        lastPointerMovement = nil; lastPointerClick = nil
        if let pointerMonitor { NSEvent.removeMonitor(pointerMonitor) }
        if let localPointerMonitor { NSEvent.removeMonitor(localPointerMonitor) }
        pointerMonitor = nil; localPointerMonitor = nil
    }

    func displaysChanged() {
        regionSharingController?.stopForSafety("The display setup changed. Check the region, then start sharing.")
        interact()
        let screens: [CGDirectDisplayID: DesktopDisplay]
        do { screens = Dictionary(uniqueKeysWithValues: try availableDesktopDisplays().map { ($0.id, $0) }) }
        catch { report(error); return }
        for (id, session) in sessions {
            if session.sourceImage != nil { continue }
            guard let screen = screens[id] else {
                if session.document.hasProtection { status = "A protected display disconnected. Stop sharing and review all covers." }
                session.close()
                sessions.removeValue(forKey: id)
                if active === session { active = nil }
                continue
            }
            if session.panel.frame != screen.frame || session.scale != screen.scale {
                session.presentation?.stop()
                session.panel.setFrame(screen.frame, display: true)
                session.scale = screen.scale
                session.canvas.renderer.reset()
                if session.document.hasProtection {
                    do {
                        try session.document.add(Element(tool: .cover, frame: Rect(x: 0, y: 0, width: screen.frame.width, height: screen.frame.height)))
                        status = "Display geometry changed: a full-display opaque cover was added. Review before removing."
                    } catch { report(error) }
                }
            }
        }
        regionSharingController?.refreshDisplays()
        onDisplaysChanged?()
        synchronize()
    }
}

@MainActor
final class OverlaySession {
    let panel: CanvasPanel
    let canvas: DrawingCanvas
    var document = CanvasDocument.empty()
    var board = WorkspaceModel.Board.transparent
    var scale: Double
    let sourceImage: CGImage?
    var presentation: LivePresentation?
    var effectsReady = false

    convenience init(screen: NSScreen, model: WorkspaceModel) {
        self.init(desktopFrame: screen.frame, scale: screen.backingScaleFactor, model: model)
    }

    init(desktopFrame: CGRect, scale: Double, model: WorkspaceModel) {
        sourceImage = nil
        self.scale = scale
        panel = CanvasPanel(contentRect: desktopFrame, styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.title = "Annotator drawing canvas"
        panel.imagePasteModel = model
        panel.preservesDesktopFrame = true
        panel.setFrame(desktopFrame, display: false)
        canvas = DrawingCanvas(frame: NSRect(origin: .zero, size: desktopFrame.size))
        canvas.autoresizingMask = [.width, .height]
        canvas.model = model
        canvas.session = self
        panel.contentView = canvas
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue - 1)
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.acceptsMouseMovedEvents = true
        panel.isReleasedWhenClosed = false
        presentation = LivePresentation(session: self, model: model)
    }

    #if DEBUG
    init(referenceImage image: CGImage, model: WorkspaceModel, frame: CGRect) {
        precondition(ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil)
        sourceImage = image; scale = 1
        panel = CanvasPanel(contentRect: frame, styleMask: [.titled, .closable, .resizable, .miniaturizable],
                            backing: .buffered, defer: false)
        panel.imagePasteModel = model
        panel.title = "Annotation test canvas"
        panel.minSize = NSSize(width: 720, height: 520)
        panel.isReleasedWhenClosed = false
        canvas = DrawingCanvas(frame: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        canvas.model = model; canvas.session = self
        panel.acceptsMouseMovedEvents = true
    }
    #endif

    func setDrawing(_ drawing: Bool, focus: Bool = true) {
        if !drawing { canvas.cancelGesture() }
        panel.ignoresMouseEvents = sourceImage == nil && !drawing
        canvas.setDrawingHUDVisible(drawing && sourceImage == nil)
        panel.orderFrontRegardless()
        if drawing {
            if focus { panel.makeKey(); panel.makeFirstResponder(canvas) }
        } else if sourceImage == nil { panel.resignKey() }
        canvas.needsDisplay = true
    }

    func close() {
        presentation?.stop()
        canvas.cancelGesture()
        panel.orderOut(nil)
        panel.contentView = nil
    }
}

final class CanvasPanel: AnnotationPanel {
    var preservesDesktopFrame = false
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        preservesDesktopFrame ? frameRect : super.constrainFrameRect(frameRect, to: screen)
    }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}
