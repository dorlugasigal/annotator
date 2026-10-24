import AppKit
import Combine
import SwiftUI
import AnnotationCore
import MotionCore

/// The tutorial's saved choices. Show on launch keeps the old tour's key, so a person who turned the tour off is not
/// asked again.
@MainActor
final class TutorialPreferences: ObservableObject {
    static let suppressedKey = "introduction-suppressed"
    static let completedKey = "tutorial-completed"
    private let defaults: UserDefaults

    init(defaults: UserDefaults) { self.defaults = defaults }

    /// Show on launch is off.
    var suppressed: Bool { defaults.bool(forKey: Self.suppressedKey) }
    /// The person reached the finish card once.
    var completed: Bool { defaults.bool(forKey: Self.completedKey) }

    func setSuppressed(_ value: Bool) {
        objectWillChange.send()
        defaults.set(value, forKey: Self.suppressedKey)
    }

    func setCompleted(_ value: Bool) {
        objectWillChange.send()
        defaults.set(value, forKey: Self.completedKey)
    }
}

/// Card measurements shared by the views and the placement.
enum TutorialLayout {
    static let width: CGFloat = 340
    static let padding: CGFloat = 16
    static let cornerRadius: CGFloat = 14
    /// Room around the card inside its window for the shadow and the entrance.
    static let margin: CGFloat = 24
}

/// Where the cards go, in screen points (AppKit coordinates, origin at the bottom left).
enum TutorialPlacement {
    static let gap: CGFloat = 12
    static let bottomInset: CGFloat = 24

    /// Centered on the display, its top at 28% of the work area.
    static func welcome(size: CGSize, visibleFrame: CGRect) -> CGRect {
        let top = visibleFrame.maxY - visibleFrame.height * 0.28
        return clamp(CGRect(x: visibleFrame.midX - size.width / 2, y: top - size.height,
                            width: size.width, height: size.height), to: visibleFrame)
    }

    /// Next to the toolbar: below a horizontal toolbar (above when there is no room) with right edges aligned, beside a
    /// vertical one (right when there is room, else left) with tops aligned. Without a toolbar, at the bottom center.
    static func coach(size: CGSize, toolbar: CGRect?, visibleFrame: CGRect) -> CGRect {
        guard let toolbar else {
            return clamp(CGRect(x: visibleFrame.midX - size.width / 2, y: visibleFrame.minY + bottomInset,
                                width: size.width, height: size.height), to: visibleFrame)
        }
        var frame = CGRect(origin: .zero, size: size)
        if toolbar.width >= toolbar.height {
            frame.origin.x = toolbar.maxX - size.width
            frame.origin.y = toolbar.minY - gap - size.height
            if frame.minY < visibleFrame.minY { frame.origin.y = toolbar.maxY + gap }
        } else {
            frame.origin.x = toolbar.maxX + gap
            if frame.maxX > visibleFrame.maxX { frame.origin.x = toolbar.minX - gap - size.width }
            frame.origin.y = toolbar.maxY - size.height
        }
        return clamp(frame, to: visibleFrame)
    }

    static func clamp(_ frame: CGRect, to bounds: CGRect) -> CGRect {
        var result = frame
        result.origin.x = max(bounds.minX, min(frame.minX, bounds.maxX - frame.width))
        result.origin.y = max(bounds.minY, min(frame.minY, bounds.maxY - frame.height))
        return result
    }
}

/// The tutorial's card window. It takes keyboard focus only for the welcome card opened from Settings; while coaching it
/// never does, so tool keys keep reaching the canvas.
final class TutorialPanel: NSPanel {
    var allowsKeyFocus = true
    override var canBecomeKey: Bool { allowsKeyFocus }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

/// Lets the first click on a card button work while another window is key.
final class TutorialHostingView: NSHostingView<TutorialPanelRoot> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// Holds the card's hosting view so the controller owns the window's frame. As a window's content view, the hosting
/// view resizes the window from inside layout and AppKit stops the loop with an exception.
final class TutorialContentView: NSView {
    let host: TutorialHostingView

    init(host: TutorialHostingView) {
        self.host = host
        super.init(frame: .zero)
        addSubview(host)
        host.autoresizingMask = [.width, .height]
    }

    required init?(coder: NSCoder) { nil }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func layout() {
        super.layout()
        host.frame = bounds
    }
    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        host.frame = bounds
    }
}

/// Finds toolbar controls by their accessibility names, the names VoiceOver reads.
@MainActor
enum TutorialAnchors {
    /// The toolbar control a goal points at, if any.
    static func name(for goal: TutorialGoal) -> String? {
        switch goal {
        case .drawing, .penTool: "Pen"
        case .rectangleTool: "Rectangle"
        case .colorChanged: "Stroke color"
        case .arrowTool: "Arrow"
        case .curvedStyle: "Curved arrow"
        case .selectTool: "Select"
        case .undone: "Undo"
        case .usingApps: "Use apps"
        case .hidden: "Hide drawings and tools"
        case .shapeDrawn, .curvedArrowDrawn, .penDrawn, .moved, .shown: nil
        }
    }

    /// The screen frame of the control named `name` in `window`, or nil when the window doesn't show it.
    /// Buttons win over text with the same name, such as a control's caption.
    static func frame(named name: String, in window: NSWindow) -> CGRect? {
        guard window.isVisible, let root = window.contentView else { return nil }
        let controlRoles: Set<String> = ["AXButton", "AXCheckBox", "AXRadioButton", "AXPopUpButton", "AXMenuButton"]
        var fallback: CGRect?
        var visited = Set<ObjectIdentifier>()
        var stack: [(object: NSObject, depth: Int)] = [(root, 0)]
        var budget = 4000
        while let entry = stack.popLast() {
            let object = entry.object
            guard entry.depth < 40, budget > 0, visited.insert(ObjectIdentifier(object)).inserted else { continue }
            budget -= 1
            if text(object, "accessibilityLabel") == name, let rect = accessibilityFrame(of: object), !rect.isEmpty,
               rect.intersects(window.frame) {
                if controlRoles.contains(text(object, "accessibilityRole") ?? "") { return rect }
                if fallback == nil { fallback = rect }
            }
            for child in children(of: object).reversed() { stack.append((child, entry.depth + 1)) }
        }
        return fallback
    }

    private static func value(_ object: NSObject, _ key: String) -> Any? {
        object.responds(to: NSSelectorFromString(key)) ? object.value(forKey: key) : nil
    }

    private static func text(_ object: NSObject, _ key: String) -> String? { value(object, key) as? String }

    private static func accessibilityFrame(of object: NSObject) -> CGRect? {
        (value(object, "accessibilityFrame") as? NSValue)?.rectValue
    }

    private static func children(of object: NSObject) -> [NSObject] {
        if let list = value(object, "accessibilityChildren") as? [NSObject], !list.isEmpty { return list }
        return (object as? NSView)?.subviews.filter { !$0.isHidden } ?? []
    }
}

/// The part of a display the ghost practices on: fractions of the work area, measured from its top-left corner.
struct TutorialPracticeArea {
    let workArea: CGRect

    func point(_ x: Double, _ y: Double) -> CGPoint {
        CGPoint(x: workArea.minX + workArea.width * CGFloat(x), y: workArea.maxY - workArea.height * CGFloat(y))
    }

    /// A point measured in points from the work area's top-left corner, y pointing down.
    func local(_ x: Double, _ y: Double) -> Point { Point(Double(workArea.width) * x, Double(workArea.height) * y) }

    func screenPoint(_ local: Point) -> CGPoint {
        CGPoint(x: workArea.minX + CGFloat(local.x), y: workArea.maxY - CGFloat(local.y))
    }
}

/// Runs the hands-on tutorial: the welcome card, one coach card per step, the finish card and the ghost that shows
/// what to press or click. Every step reads the workspace's state, so keys and clicks both count. The tutorial never
/// changes a drawing or a preference other than its own Show on launch and completed flags.
@MainActor
final class TutorialController: ObservableObject {
    enum Surface: String { case hidden, welcome, coach, finish }

    let preferences: TutorialPreferences
    let overlay = TutorialOverlay()
    @Published private(set) var surface = Surface.hidden
    /// The card is on its way out; the view sinks and fades it.
    @Published private(set) var leaving = false
    /// Changes each time a card enters, so the view plays its entrance again.
    @Published private(set) var presentationToken = 0
    /// The step the card shows. It trails the progress by the short pause that lets a check mark land.
    @Published private(set) var shownStep = TutorialStep.allCases[0]
    @Published private(set) var shownGoalsDone = 0
    /// The stroke color the toolbar showed when the shown step or goal appeared. The color step asks for green when it was
    /// blue already; the words stay put while the person changes the color.
    @Published private(set) var shownColor: RGBA?
    @Published private(set) var shownStepDone = false
    @Published private(set) var hintVisible = false
    /// Changes when the card's keycaps should press, one after another.
    @Published private(set) var keyPressToken = 0
    /// Changes when Show me should pulse once.
    @Published private(set) var showMePulseToken = 0
    /// One moment of a step change, drawn still for design review instead of animated.
    @Published private(set) var reviewStepChange: TutorialStepChangeReview?
    private(set) var progress: TutorialProgress?
    private(set) var window: TutorialPanel?
    /// Goals whose demonstration started, newest last.
    private(set) var demonstratedGoals: [TutorialGoal] = []
    /// The last step announced to VoiceOver.
    private(set) var lastAnnouncement: String?
    var reducesMotion: @MainActor () -> Bool = { Motion.systemReducesMotion }
    var hintDelay = TutorialMotion.hintDelay
    var demoDelay = TutorialMotion.ghostStartDelay
    private let model: WorkspaceModel
    private let presented: @MainActor () -> Bool
    private let toolbarWindow: @MainActor () -> NSWindow?
    private let startDrawing: @MainActor () -> Void
    private var host: TutorialHostingView?
    private var cardSize = CGSize(width: TutorialLayout.width, height: 220)
    private var welcomeArea: CGRect?
    private var fallbackArea: CGRect?
    private var placedToolbarFrame: CGRect?
    private var attemptedAutomaticPresentation = false
    private var subscriptions: Set<AnyCancellable> = []
    private var windowObservers: [NSObjectProtocol] = []
    private var observationPending = false
    private var editing = false
    private var beat: Task<Void, Never>?
    private var hintTask: Task<Void, Never>?
    private var demoTask: Task<Void, Never>?
    private var transitionTask: Task<Void, Never>?
    private var ringGoal: TutorialGoal?
    private var ringToolbarFrame: CGRect?
    private var ringFound = false
    private var lastRingLookup: TimeInterval = 0

    /// `presented` tells whether drawings and tools are shown or showing; `toolbarWindow` returns the drawing toolbar;
    /// `startDrawing` turns drawing on the way the drawing shortcut does.
    init(model: WorkspaceModel, presented: (@MainActor () -> Bool)? = nil,
         toolbarWindow: @escaping @MainActor () -> NSWindow? = { nil },
         startDrawing: (@MainActor () -> Void)? = nil) {
        self.model = model
        preferences = TutorialPreferences(defaults: model.preferenceDefaults)
        self.presented = presented ?? { [weak model] in model.map { !$0.workspaceHidden } ?? false }
        self.toolbarWindow = toolbarWindow
        self.startDrawing = startDrawing ?? { [weak model] in
            guard let model, model.mode != .draw else { return }
            model.beginDrawing()
        }
        model.objectWillChange.sink { [weak self] in self?.scheduleObservation() }.store(in: &subscriptions)
    }

    var isVisible: Bool { surface != .hidden }

    /// The same rule as the old tour: not while drawing, zooming, animating, editing or showing an error.
    var isEligible: Bool {
        model.mode != .draw && !model.screenZoomEnabled && !model.magnifierEnabled &&
            model.zoomStartState == .idle && !model.visibilityAnimating &&
            model.errorMessage == nil && model.captureFailureMessage == nil &&
            model.desktopSessions.allSatisfy { !$0.canvas.hasActiveEdit }
    }

    /// The goal the card asks for now, or nil while a finished step waits to move on.
    var shownGoal: TutorialGoal? {
        guard surface == .coach, !shownStepDone else { return nil }
        let goals = shownStep.goals
        return goals[min(shownGoalsDone, goals.count - 1)]
    }

    // MARK: Presentation

    /// Shows the welcome card once per launch, without taking focus, until the tutorial is finished or Show on launch
    /// is off. A login launch stays quiet.
    func showAutomatically(isLoginItem: Bool) {
        guard !attemptedAutomaticPresentation else { return }
        attemptedAutomaticPresentation = true
        guard !isLoginItem, !preferences.suppressed, !preferences.completed, isEligible else { return }
        present(takeFocus: false)
    }

    /// Settings > Help and General open the welcome card again.
    func showManually() {
        if surface != .hidden && !leaving {
            window?.orderFrontRegardless()
            return
        }
        guard isEligible else {
            model.status = "Finish drawing and stop zoom, then open the tutorial from Settings > Help."
            return
        }
        present(takeFocus: true)
    }

    /// Start tutorial: drawing turns on, so the first step's key reaches the canvas, the welcome card leaves and the
    /// first step's card enters next to the toolbar.
    func start() {
        guard surface == .welcome, !leaving else { return }
        window?.allowsKeyFocus = false
        startDrawing()
        let look = observation()
        progress = TutorialProgress(look)
        editing = look.editing
        swapCard { [weak self] in self?.enterCoach() }
    }

    func notNow() { dismiss() }

    /// The close button: the tutorial ends without being marked finished. Drawings stay.
    func end() { dismiss() }

    func done() { dismiss() }

    func openSettings() { model.openPreferences(tab: .toolbar) }

    /// Plays the current goal's demonstration again.
    func showMe() {
        guard surface == .coach, !leaving, let goal = shownGoal else { return }
        scheduleDemo(goal, after: 0)
    }

    /// Moves on to the next step. While the card is still catching up after a finished step, only the card moves on.
    func skipStep() {
        guard surface == .coach, !leaving, var progress else { return }
        if beat != nil || progress.isFinished || shownStep != progress.step {
            beat?.cancel()
            beat = nil
            advanceDisplay()
            return
        }
        progress.skip()
        self.progress = progress
        advanceDisplay()
    }

    /// Hides the card at once. The confetti, when it plays, finishes on its own.
    func close() {
        transitionTask?.cancel()
        transitionTask = nil
        beat?.cancel()
        beat = nil
        hintTask?.cancel()
        hintTask = nil
        cancelDemo()
        overlay.hideRing(animated: false)
        stopWatchingToolbar()
        window?.orderOut(nil)
        progress = nil
        leaving = false
        surface = .hidden
        hintVisible = false
        reviewStepChange = nil
        if model.introductionVisible { model.introductionVisible = false }
    }

    /// Closes everything, including the confetti, and lets go of the windows. Used when the app quits.
    func shutdown() {
        close()
        overlay.stop()
        window?.contentView = nil
        window?.close()
        window = nil
        host = nil
    }

    /// Shows a card state without a window, for design review and the UX reference.
    func showReview(_ surface: Surface, step: TutorialStep = .rectangle, goalsDone: Int = 0, stepDone: Bool = false,
                    hint: Bool = false) {
        self.surface = surface
        shownStep = step
        shownGoalsDone = stepDone ? step.goals.count : min(max(0, goalsDone), step.goals.count - 1)
        shownColor = model.style.color
        shownStepDone = stepDone
        hintVisible = hint
        reviewStepChange = nil
    }

    /// Shows the moment `time` seconds into the change from `from`'s finished card to the next step.
    func showStepChangeReview(from: TutorialStep, time: Double) {
        let steps = TutorialStep.allCases
        showReview(.coach, step: steps[min(from.index + 1, steps.count - 1)])
        reviewStepChange = TutorialStepChangeReview(from: from, time: time)
    }

    private func present(takeFocus: Bool) {
        close()
        let panel = window ?? makePanel()
        resetDisplay()
        surface = .welcome
        presentationToken &+= 1
        welcomeArea = Self.pointerScreen()?.visibleFrame
        placedToolbarFrame = nil
        panel.allowsKeyFocus = true
        panel.level = cardLevel
        measureCard()
        placeCard()
        startWatchingToolbar()
        if takeFocus { panel.makeKeyAndOrderFront(nil) } else { panel.orderFrontRegardless() }
        model.introductionVisible = true
    }

    private func makePanel() -> TutorialPanel {
        let panel = TutorialPanel(contentRect: NSRect(x: 0, y: 0, width: TutorialLayout.width + TutorialLayout.margin * 2,
                                                      height: 300),
                                  styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "Annotator tutorial"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let host = TutorialHostingView(rootView: TutorialPanelRoot(
            tutorial: self, model: model, preferences: preferences, metrics: model.usageMetrics,
            sized: { [weak self] size in self?.cardSizeChanged(size) }))
        host.sizingOptions = []
        panel.contentView = TutorialContentView(host: host)
        self.host = host
        window = panel
        return panel
    }

    /// The card leaves, `change` swaps what it shows, and the new card enters.
    private func swapCard(_ change: @escaping @MainActor @Sendable () -> Void) {
        transitionTask?.cancel()
        withAnimation(TutorialAnimation.cardExit(reduceMotion: reducesMotion())) { leaving = true }
        transitionTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(TutorialMotion.cardExitDuration))
            guard let self, !Task.isCancelled else { return }
            self.transitionTask = nil
            self.leaving = false
            self.presentationToken &+= 1
            change()
        }
    }

    private func dismiss() {
        guard surface != .hidden else { return }
        guard !leaving || transitionTask == nil else {
            transitionTask?.cancel()
            close()
            return
        }
        beat?.cancel()
        beat = nil
        stopCoaching()
        transitionTask?.cancel()
        withAnimation(TutorialAnimation.cardExit(reduceMotion: reducesMotion())) { leaving = true }
        transitionTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(TutorialMotion.cardExitDuration))
            guard let self, !Task.isCancelled else { return }
            self.transitionTask = nil
            self.close()
        }
    }

    private func enterCoach() {
        guard progress != nil else { return }
        resetDisplay()
        surface = .coach
        fallbackArea = Self.pointerScreen()?.visibleFrame
        placedToolbarFrame = nil
        refreshToolbarPlacement()
        placeCard()
        window?.level = cardLevel
        announceStep()
        goalBecameCurrent()
        settleDisplay()
    }

    private func enterFinish() {
        stopCoaching()
        withAnimation(TutorialAnimation.stepChange(reduceMotion: reducesMotion())) {
            surface = .finish
            hintVisible = false
        }
        preferences.setCompleted(true)
        // Only the finished tutorial celebrates: confetti pops from both bottom corners of the card's display.
        guard !reducesMotion(), let card = window?.frame, let display = Self.screen(for: card)?.frame else { return }
        overlay.celebrate(on: display, level: overlayLevel)
    }

    private func resetDisplay() {
        beat?.cancel()
        beat = nil
        shownStep = TutorialStep.allCases[0]
        shownGoalsDone = 0
        shownColor = model.style.color
        shownStepDone = false
        hintVisible = false
        reviewStepChange = nil
    }

    // MARK: Following the workspace

    /// What the workspace looks like now, over every display.
    func observation() -> TutorialObservation {
        let sessions = model.desktopSessions
        let mode: TutorialMode = switch model.mode {
        case .off: .off
        case .interact: .useApps
        case .draw: .draw
        }
        return TutorialObservation(
            mode: mode, presented: presented(), tool: model.tool, strokeColor: model.style.color,
            curved: model.style.curved,
            elements: sessions.flatMap { session in session.document.elements.map { TutorialElement($0) } },
            undoSerial: model.undoSerial,
            editing: sessions.contains { $0.canvas.hasActiveEdit || $0.document.isEditing })
    }

    /// Model changes arrive in bursts; look once on the next turn of the run loop.
    private func scheduleObservation() {
        guard surface == .coach, !observationPending else { return }
        observationPending = true
        Task { @MainActor [weak self] in
            guard let self else { return }
            self.observationPending = false
            self.observeWorkspace()
        }
    }

    private func observeWorkspace() {
        guard surface == .coach, !leaving, var progress else { return }
        let look = observation()
        if look.editing && !editing { cancelDemo() }
        editing = look.editing
        let change = progress.observe(look)
        self.progress = progress
        if change != TutorialChange.none { settleDisplay() }
        if window?.level != cardLevel { window?.level = cardLevel }
        refreshToolbarPlacement()
        updateRing(force: false)
    }

    /// Brings the card's words up to the progress: a finished step shows its check, waits a beat, then the next step
    /// rises in. Several finished steps play one after another with a shorter beat.
    private func settleDisplay() {
        guard surface == .coach, !leaving, beat == nil, let progress else { return }
        let steps = TutorialStep.allCases
        let behind = progress.isFinished || shownStep.index < progress.step.index
        guard behind else {
            if shownGoalsDone != progress.goalIndex {
                withAnimation(TutorialAnimation.check(reduceMotion: reducesMotion())) {
                    shownGoalsDone = progress.goalIndex
                    shownColor = model.style.color
                }
                goalBecameCurrent()
            }
            return
        }
        var wait = 0.0
        if progress.completed.contains(shownStep) {
            if !shownStepDone {
                withAnimation(TutorialAnimation.check(reduceMotion: reducesMotion())) {
                    shownGoalsDone = shownStep.goals.count
                    shownStepDone = true
                    hintVisible = false
                }
                stopCoaching()
            }
            let next = shownStep.index + 1
            let nextAlreadyDone = progress.isFinished ? next < steps.count : next < progress.step.index
            wait = nextAlreadyDone ? TutorialMotion.quickDoneBeat : TutorialMotion.doneBeat
        }
        beat = Task { @MainActor [weak self] in
            if wait > 0 { try? await Task.sleep(for: .seconds(wait)) }
            guard let self, !Task.isCancelled else { return }
            self.beat = nil
            self.advanceDisplay()
        }
    }

    private func advanceDisplay() {
        guard surface == .coach, !leaving, let progress else { return }
        let steps = TutorialStep.allCases
        let next = shownStep.index + 1
        guard next < steps.count else {
            enterFinish()
            return
        }
        let step = steps[next]
        let current = !progress.isFinished && step == progress.step
        withAnimation(TutorialAnimation.stepChange(reduceMotion: reducesMotion())) {
            shownStep = step
            shownColor = model.style.color
            shownStepDone = false
            shownGoalsDone = current ? progress.goalIndex : 0
            hintVisible = false
        }
        announceStep()
        goalBecameCurrent()
        settleDisplay()
    }

    /// VoiceOver reads the new step: "Step 3 of 10. Make it blue. Press B, or pick any color in the toolbar."
    private func announceStep() {
        guard surface == .coach else { return }
        let text = TutorialText.step(shownStep, preferences: model.preferences, color: shownColor)
        let words = TutorialText.plain(text.detail ?? text.goals.joined(separator: ". "))
        let announcement = "\(TutorialText.stepValue(shownStep)). \(text.title). \(words)"
        lastAnnouncement = announcement
        NSAccessibility.post(element: NSApplication.shared, notification: .announcementRequested,
                             userInfo: [.announcement: announcement,
                                        .priority: NSAccessibilityPriorityLevel.high.rawValue])
    }

    /// A new goal shows: the hint waits again, the ring moves to its control and the ghost plays once.
    private func goalBecameCurrent() {
        cancelDemo()
        hintTask?.cancel()
        hintTask = nil
        guard surface == .coach, let goal = shownGoal else {
            overlay.hideRing(animated: true)
            return
        }
        startHintTimer()
        updateRing(force: true)
        scheduleDemo(goal, after: demoDelay)
    }

    private func stopCoaching() {
        cancelDemo()
        hintTask?.cancel()
        hintTask = nil
        ringGoal = nil
        overlay.hideRing(animated: true)
    }

    private func startHintTimer() {
        let delay = hintDelay
        hintTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !Task.isCancelled, self.surface == .coach, self.shownGoal != nil else { return }
            self.hintTask = nil
            withAnimation(TutorialAnimation.fade(reduceMotion: self.reducesMotion())) { self.hintVisible = true }
            self.showMePulseToken &+= 1
        }
    }

    // MARK: Placement

    private var cardLevel: NSWindow.Level {
        NSWindow.Level(rawValue: PresentationWindowLevels.toolbar(screenZoom: model.screenZoomEnabled).rawValue + 1)
    }

    private var overlayLevel: NSWindow.Level {
        NSWindow.Level(rawValue: PresentationWindowLevels.toolbar(screenZoom: model.screenZoomEnabled).rawValue + 2)
    }

    private func measureCard() {
        guard let host else { return }
        host.layoutSubtreeIfNeeded()
        let fitting = host.fittingSize
        let margin = TutorialLayout.margin
        guard fitting.width > margin * 2 + 1, fitting.height > margin * 2 + 1 else { return }
        cardSize = CGSize(width: fitting.width - margin * 2, height: fitting.height - margin * 2)
    }

    private func cardSizeChanged(_ size: CGSize) {
        guard size.width > 0, size.height > 0,
              abs(size.width - cardSize.width) > 0.5 || abs(size.height - cardSize.height) > 0.5 else { return }
        cardSize = size
        placeCard()
    }

    private func placeCard() {
        guard let window, surface != .hidden else { return }
        let card: CGRect
        if surface == .welcome {
            card = TutorialPlacement.welcome(size: cardSize, visibleFrame: welcomeArea ?? Self.pointerScreen()?.visibleFrame ?? .zero)
        } else if let toolbar = placedToolbarFrame, let area = Self.screen(for: toolbar)?.visibleFrame {
            card = TutorialPlacement.coach(size: cardSize, toolbar: toolbar, visibleFrame: area)
        } else {
            card = TutorialPlacement.coach(size: cardSize, toolbar: nil,
                                           visibleFrame: fallbackArea ?? Self.pointerScreen()?.visibleFrame ?? .zero)
        }
        let frame = card.insetBy(dx: -TutorialLayout.margin, dy: -TutorialLayout.margin)
        if window.frame != frame { window.setFrame(frame, display: true) }
    }

    /// The card follows the toolbar only when the toolbar's frame changes while it shows, not while it hides.
    private func refreshToolbarPlacement() {
        guard surface == .coach || surface == .finish, !leaving,
              let toolbar = toolbarWindow(), toolbar.isVisible,
              !model.workspaceHidden, !model.visibilityAnimating,
              toolbar.frame != placedToolbarFrame else { return }
        placedToolbarFrame = toolbar.frame
        placeCard()
        updateRing(force: true)
    }

    private func startWatchingToolbar() {
        guard windowObservers.isEmpty else { return }
        for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification] {
            windowObservers.append(NotificationCenter.default.addObserver(
                forName: name, object: nil, queue: .main
            ) { [weak self] _ in MainActor.assumeIsolated { self?.refreshToolbarPlacement() } })
        }
    }

    private func stopWatchingToolbar() {
        windowObservers.forEach(NotificationCenter.default.removeObserver)
        windowObservers.removeAll()
    }

    static func pointerScreen() -> NSScreen? {
        let point = NSEvent.mouseLocation
        return NSScreen.screens.first { $0.frame.contains(point) } ?? NSScreen.main ?? NSScreen.screens.first
    }

    static func screen(for rect: CGRect) -> NSScreen? {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        return NSScreen.screens.first { $0.frame.contains(center) } ?? NSScreen.screens.first { $0.frame.intersects(rect) }
    }

    // MARK: Ring and ghost

    private func updateRing(force: Bool) {
        guard surface == .coach, !leaving, let goal = shownGoal, let name = TutorialAnchors.name(for: goal),
              presented(), !model.visibilityAnimating, let toolbar = toolbarWindow(), toolbar.isVisible else {
            ringGoal = nil
            overlay.hideRing(animated: true)
            return
        }
        let now = ProcessInfo.processInfo.systemUptime
        let stale = goal != ringGoal || toolbar.frame != ringToolbarFrame || (!ringFound && now - lastRingLookup > 0.25)
        guard force || stale else { return }
        ringGoal = goal
        ringToolbarFrame = toolbar.frame
        lastRingLookup = now
        guard let anchor = TutorialAnchors.frame(named: name, in: toolbar) else {
            ringFound = false
            overlay.hideRing(animated: true)
            return
        }
        ringFound = true
        overlay.showRing(around: anchor, level: overlayLevel, pulse: !reducesMotion())
    }

    private func cancelDemo() {
        demoTask?.cancel()
        demoTask = nil
        overlay.stopDemo()
    }

    private func scheduleDemo(_ goal: TutorialGoal, after delay: Double) {
        cancelDemo()
        demoTask = Task { @MainActor [weak self] in
            if delay > 0 { try? await Task.sleep(for: .seconds(delay)) }
            guard let self, !Task.isCancelled else { return }
            await self.playDemo(goal)
        }
    }

    /// The keys press first, then the ghost moves and clicks, so both ways show; the curved arrow has no key and ends with
    /// a double-click, which adds its head. With Reduce Motion only the keys play.
    private func playDemo(_ goal: TutorialGoal) async {
        guard surface == .coach, !leaving, shownGoal == goal, !editing else { return }
        demonstratedGoals.append(goal)
        if demonstratedGoals.count > 32 { demonstratedGoals.removeFirst(demonstratedGoals.count - 32) }
        let keys = goal == .curvedArrowDrawn ? []
            : TutorialText.keys(for: goal, preferences: model.preferences, color: shownColor)
        let ghost = reducesMotion() ? nil : demoRun(for: goal)
        if !keys.isEmpty {
            keyPressToken &+= 1
            guard ghost != nil else { return }
            try? await Task.sleep(for: .seconds(Self.keysDuration(keys.count) + TutorialMotion.ghostPause))
            guard !Task.isCancelled, surface == .coach, shownGoal == goal else { return }
        }
        guard let ghost else { return }
        overlay.play(ghost, level: overlayLevel)
    }

    static func keysDuration(_ count: Int) -> Double {
        Double(max(0, count - 1)) * TutorialMotion.keyStagger + TutorialMotion.keyDownDuration + TutorialMotion.keyUpDuration
    }

    /// Where the ghost goes for `goal`. A control it can't find is left out; the keys still play.
    private func demoRun(for goal: TutorialGoal) -> TutorialGhostRun? {
        let toolbar = toolbarWindow().flatMap { $0.isVisible ? $0 : nil }
        if let name = TutorialAnchors.name(for: goal) {
            guard let toolbar, let anchor = TutorialAnchors.frame(named: name, in: toolbar) else { return nil }
            let target = CGPoint(x: anchor.midX, y: anchor.midY)
            guard let display = Self.screen(for: anchor)?.frame else { return nil }
            return TutorialGhostRun(display: display, start: ghostStart(on: display), steps: [.click(target)])
        }
        let screen = toolbar.flatMap { Self.screen(for: $0.frame) } ?? window.flatMap { Self.screen(for: $0.frame) }
            ?? Self.pointerScreen()
        guard let screen else { return nil }
        let area = TutorialPracticeArea(workArea: screen.visibleFrame)
        let display = screen.frame
        let start = ghostStart(on: display)
        switch goal {
        case .shapeDrawn:
            return TutorialGhostRun(display: display, start: start,
                                    steps: [.drag([area.point(0.34, 0.42), area.point(0.48, 0.58)], ink: .rectangle)])
        case .curvedArrowDrawn:
            return TutorialGhostRun(display: display, start: start,
                                    steps: [.clicks([area.point(0.56, 0.62), area.point(0.64, 0.46), area.point(0.74, 0.54)],
                                                    double: true)])
        case .penDrawn:
            let loop = TutorialMotion.loop(center: area.local(0.30, 0.62), radius: 56).map { area.screenPoint($0) }
            return TutorialGhostRun(display: display, start: start, steps: [.drag(loop, ink: .line)])
        case .moved:
            return TutorialGhostRun(display: display, start: start, steps: [.drag(movePath(on: display, area: area), ink: nil)])
        default:
            return nil
        }
    }

    /// The move demo presses on the newest drawing's line on the toolbar's display, or on a practice spot.
    private func movePath(on display: CGRect, area: TutorialPracticeArea) -> [CGPoint] {
        let center = CGPoint(x: display.midX, y: display.midY)
        if let session = model.desktopSessions.first(where: { $0.panel.frame.contains(center) }),
           let element = session.document.elements.last(where: { !$0.locked }) {
            let grab = Self.grabPoint(for: element)
            let frame = session.panel.frame
            let start = CGPoint(x: frame.minX + CGFloat(grab.x), y: frame.maxY - CGFloat(grab.y))
            return [start, CGPoint(x: start.x + 120, y: start.y - 40)]
        }
        return [area.point(0.41, 0.50), area.point(0.49, 0.55)]
    }

    /// A point on a drawing's line in canvas points: the middle of its path, or the middle of its top edge.
    static func grabPoint(for element: Element) -> Point {
        let local = element.points.count >= 2 ? element.points[element.points.count / 2]
            : Point(element.frame.x + element.frame.width / 2, element.frame.y)
        return local.rotated(around: element.frame.center, by: element.rotation)
    }

    /// The ghost appears on the card when the card is on the same display, otherwise in the middle of the display.
    private func ghostStart(on display: CGRect) -> CGPoint {
        if let card = window?.frame, window?.isVisible == true, card.intersects(display) {
            return CGPoint(x: card.midX, y: card.midY)
        }
        return CGPoint(x: display.midX, y: display.midY)
    }
}
