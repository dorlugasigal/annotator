import AppKit
import SwiftUI
import AnnotationCore
import MotionCore

/// The words of one tutorial step: its title, one line of detail, a line per goal when it has several, and a hint.
struct TutorialStepText: Equatable {
    let title: String
    let detail: String
    let goals: [String]
    let hint: String
}

/// One keycap: what it shows and what VoiceOver reads.
struct TutorialKey: Hashable {
    let symbol: String
    let name: String
}

/// The tutorial's words. Keys follow the person's shortcuts; a tool without a key uses the words without one.
enum TutorialText {
    static let welcomeTitle = "Welcome to Annotator"
    static let welcomeBody = "Learn the basics in about two minutes. Use the keys or the mouse, whichever you like."
    static let start = "Start tutorial"
    static let notNow = "Not now"
    static let showOnLaunch = "Show on launch"
    static let usageCounts = "Share usage counts"
    static let usageCountsNote = "Optional. Never your drawings or text."
    static let usageCountsHelp =
        "Optional daily counts: version, macOS, chip and feature categories. No drawings, screenshots, text or file names. Change this in General."
    static let findAgain = "Find this again in Settings > Help."
    static let endTutorial = "End tutorial"
    static let showMe = "Show me"
    static let skipStep = "Skip step"
    static let finishTitle = "You're ready."
    static let finishLead = "That's the basics. Keep these keys handy."
    static let changeShortcuts = "Change any shortcut in Settings > Toolbar & Shortcuts."
    static let openSettings = "Open Settings"
    static let sharingTitle = "Before you share"
    static let sharingBody =
        "App or window sharing may leave out covers. Ask someone to check. Hiding, quitting or a crash removes them. Blur can leave details readable.\n\nDrawing needs no permissions. Zoom, effects and region sharing ask when needed."
    static let practiceAgain = "Practice again from Settings > Help."
    static let done = "Done"
    static let menuIcon = "menu bar icon"

    static let escape = [TutorialKey(symbol: "esc", name: "Escape")]
    static let returnKey = [TutorialKey(symbol: "return", name: "Return")]
    static let undo = [TutorialKey(symbol: "⌘", name: "Command"), TutorialKey(symbol: "Z", name: "Z")]
    static let blue = [TutorialKey(symbol: "B", name: "B")]

    /// "Step 3 of 11".
    static func stepValue(_ step: TutorialStep) -> String { "Step \(step.index + 1) of \(TutorialStep.allCases.count)" }

    static func step(_ step: TutorialStep, preferences: WorkspacePreferences) -> TutorialStepText {
        let resume = resumeShortcut(preferences).label
        let showHide = preferences.activationShortcut.label
        switch step {
        case .draw:
            return TutorialStepText(title: "Start drawing", detail: "Press \(resume), or click any tool in the toolbar.",
                                    goals: [], hint: "No toolbar? Click Annotator's \(menuIcon) and choose Show drawings and tools.")
        case .rectangle:
            return TutorialStepText(
                title: "Pick the Rectangle",
                detail: toolKey(.rectangle, preferences).map { "Press \($0), or click Rectangle in the toolbar." }
                    ?? "Click Rectangle in the toolbar.",
                goals: [], hint: "Look for the square in the toolbar. The ring shows where.")
        case .shape:
            return TutorialStepText(title: "Draw a rectangle", detail: "Drag anywhere on the screen.", goals: [],
                                    hint: "Press, move the pointer, then let go.")
        case .color:
            return TutorialStepText(title: "Change the color", detail: "Press B for blue, or pick a color in the toolbar.",
                                    goals: [], hint: "Any color works. New drawings use it too.")
        case .arrow:
            return TutorialStepText(
                title: "Draw a curved arrow", detail: "Arrows can bend around things.",
                goals: [toolKey(.arrow, preferences).map { "Pick the Arrow: press \($0) or click it." }
                            ?? "Click the Arrow in the toolbar.",
                        "Choose Curved in the properties.",
                        "Click a few points, then press Return."],
                hint: "Click, don't drag: each click adds a point. Return finishes the arrow.")
        case .pen:
            return TutorialStepText(
                title: "Circle something with the Pen", detail: "Draw freely, like on paper.",
                goals: [toolKey(.pen, preferences).map { "Pick the Pen: press \($0) or click it." }
                            ?? "Click the Pen in the toolbar.",
                        "Draw a circle around anything."],
                hint: "Hold the mouse button down while you draw.")
        case .move:
            return TutorialStepText(
                title: "Move a drawing", detail: "Select lets you change what you drew.",
                goals: [toolKey(.select, preferences).map { "Pick Select: press \($0) or click it." }
                            ?? "Click Select in the toolbar.",
                        "Drag one of your drawings to a new place."],
                hint: "Press on the drawing's line, not the empty space inside it.")
        case .undo:
            return TutorialStepText(title: "Undo", detail: "Press ⌘Z, or click Undo in the toolbar.", goals: [],
                                    hint: "Undo takes back your last change. Redo brings it back.")
        case .useApps:
            return TutorialStepText(
                title: "Use your apps",
                detail: "Press esc, or click Use apps in the toolbar. Your drawings stay on the screen.", goals: [],
                hint: "Typing text or drawing? The first esc finishes that. Press it again.")
        case .drawAgain:
            return TutorialStepText(title: "Draw again", detail: "Press \(resume), or click any tool in the toolbar.",
                                    goals: [], hint: "\(resume) works from any app.")
        case .hide:
            return TutorialStepText(
                title: "Hide and show",
                detail: "Clear your screen in a moment. Everything comes back as you left it.",
                goals: ["Press \(showHide), or click Hide drawings and tools in the toolbar.",
                        "Press \(showHide) again to bring them back."],
                hint: "You can also use Annotator's \(menuIcon).")
        }
    }

    /// The keycaps the card shows for `goal`; empty when the goal has no key.
    static func keys(for goal: TutorialGoal, preferences: WorkspacePreferences) -> [TutorialKey] {
        switch goal {
        case .drawing: keys(resumeShortcut(preferences))
        case .rectangleTool: toolKeys(.rectangle, preferences)
        case .colorChanged: blue
        case .arrowTool: toolKeys(.arrow, preferences)
        case .curvedArrowDrawn: returnKey
        case .penTool: toolKeys(.pen, preferences)
        case .selectTool: toolKeys(.select, preferences)
        case .undone: undo
        case .usingApps: escape
        case .hidden, .shown: keys(preferences.activationShortcut)
        case .shapeDrawn, .curvedStyle, .penDrawn, .moved: []
        }
    }

    /// The finish card's keys: Draw, Use apps, Show / hide and Undo.
    static func finishRows(preferences: WorkspacePreferences) -> [(label: String, keys: [TutorialKey])] {
        [("Draw", keys(resumeShortcut(preferences))), ("Use apps", escape),
         ("Show / hide", keys(preferences.activationShortcut)), ("Undo", undo)]
    }

    /// A global shortcut as keycaps, modifiers in the order macOS shows them.
    static func keys(_ shortcut: ActivationShortcut) -> [TutorialKey] {
        var keys: [TutorialKey] = []
        if shortcut.control { keys.append(TutorialKey(symbol: "⌃", name: "Control")) }
        if shortcut.option { keys.append(TutorialKey(symbol: "⌥", name: "Option")) }
        if shortcut.shift { keys.append(TutorialKey(symbol: "⇧", name: "Shift")) }
        if shortcut.command { keys.append(TutorialKey(symbol: "⌘", name: "Command")) }
        let character = shortcut.character.uppercased()
        keys.append(TutorialKey(symbol: character, name: character))
        return keys
    }

    private static func resumeShortcut(_ preferences: WorkspacePreferences) -> ActivationShortcut {
        preferences.resumeShortcut ?? .resumeDrawing
    }

    private static func toolKey(_ tool: Tool, _ preferences: WorkspacePreferences) -> String? {
        preferences.shortcut(for: .tool(tool))?.label
    }

    private static func toolKeys(_ tool: Tool, _ preferences: WorkspacePreferences) -> [TutorialKey] {
        guard let shortcut = preferences.shortcut(for: .tool(tool)) else { return [] }
        let key = shortcut.key.uppercased()
        return (shortcut.shift ? [TutorialKey(symbol: "⇧", name: "Shift")] : []) + [TutorialKey(symbol: key, name: key)]
    }
}

/// SwiftUI forms of the tutorial's motion.
@MainActor
enum TutorialAnimation {
    static func curve(_ curve: CubicBezier, duration: Double) -> Animation {
        .timingCurve(curve.x1, curve.y1, curve.x2, curve.y2, duration: duration)
    }

    /// Reduce Motion replaces movement with a short cross-fade.
    static let crossFade = Animation.easeInOut(duration: 0.2)

    static func check(reduceMotion: Bool) -> Animation {
        reduceMotion ? crossFade : curve(TutorialMotion.pop, duration: TutorialMotion.checkDuration)
    }

    static func stepChange(reduceMotion: Bool) -> Animation {
        reduceMotion ? crossFade : curve(MotionCurves.enter, duration: TutorialMotion.stepInDuration)
    }

    static func cardExit(reduceMotion: Bool) -> Animation {
        reduceMotion ? crossFade : curve(MotionCurves.exit, duration: TutorialMotion.cardExitDuration)
    }

    static func fade(reduceMotion: Bool) -> Animation {
        reduceMotion ? crossFade : curve(MotionCurves.fade, duration: 0.24)
    }

    /// The words leave upward, then the next step's words rise in.
    static func words(reduceMotion: Bool) -> AnyTransition {
        if reduceMotion { return .opacity.animation(crossFade) }
        return .asymmetric(
            insertion: AnyTransition.offset(y: TutorialMotion.stepInRise)
                .animation(curve(MotionCurves.enter, duration: TutorialMotion.stepInDuration).delay(TutorialMotion.stepInDelay))
                .combined(with: AnyTransition.opacity
                    .animation(curve(MotionCurves.fade, duration: TutorialMotion.stepInFade).delay(TutorialMotion.stepInDelay))),
            removal: AnyTransition(TutorialWordsLeaving())
                .animation(curve(MotionCurves.exit, duration: TutorialMotion.stepOutDuration)))
    }

    static func checkTransition(reduceMotion: Bool) -> AnyTransition {
        reduceMotion ? .opacity : .scale(scale: TutorialMotion.checkStartScale).combined(with: .opacity)
    }
}

/// Leaving words rise a little and fade.
struct TutorialWordsLeaving: Transition {
    func body(content: Content, phase: TransitionPhase) -> some View {
        content
            .opacity(phase.isIdentity ? 1 : 0)
            .offset(y: CGFloat(phase.isIdentity ? 0 : -TutorialMotion.stepOutRise))
    }
}

/// A step change drawn still at one moment, for design review.
struct TutorialStepChangeReview: Equatable {
    var from: TutorialStep
    var time: Double
}

/// The step change's motion as plain math, so review frames show exactly what the animation does.
enum TutorialStepChange {
    /// The finished step's words leave upward.
    static func outgoing(at time: Double) -> (opacity: Double, offset: Double) {
        let progress = MotionCurves.exit.progress(at: time / TutorialMotion.stepOutDuration)
        return (1 - progress, -TutorialMotion.stepOutRise * progress)
    }

    /// The next step's words rise in after a short delay.
    static func incoming(at time: Double) -> (opacity: Double, offset: Double) {
        let local = time - TutorialMotion.stepInDelay
        let rise = MotionCurves.enter.progress(at: local / TutorialMotion.stepInDuration)
        let fade = MotionCurves.fade.progress(at: local / TutorialMotion.stepInFade)
        return (fade, TutorialMotion.stepInRise * (1 - rise))
    }

    /// The progress track's edges: the leading edge moves first, the trailing edge follows.
    static func trackEdges(from: Int, to: Int, at time: Double) -> (lower: Double, upper: Double) {
        let lead = MotionCurves.standard.progress(at: time / TutorialMotion.trackLeadDuration)
        let trail = MotionCurves.standard.progress(at: (time - TutorialMotion.trackTrailDelay) / TutorialMotion.trackTrailDuration)
        let start = Double(from), end = Double(to)
        let leading = start + (end - start) * lead
        let trailing = start + (end - start) * trail
        return end >= start ? (trailing, leading) : (leading, trailing)
    }
}

private struct TutorialCardSize: PreferenceKey {
    static let defaultValue = CGSize.zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        let next = nextValue()
        if next.width > 0, next.height > 0 { value = next }
    }
}

/// The card window's content: the card with room for its shadow, its entrance and its exit.
struct TutorialPanelRoot: View {
    @ObservedObject var tutorial: TutorialController
    @ObservedObject var model: WorkspaceModel
    @ObservedObject var preferences: TutorialPreferences
    @ObservedObject var metrics: UsageMetrics
    let sized: @MainActor (CGSize) -> Void
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion

    var body: some View {
        TutorialCardChrome(leaving: tutorial.leaving, reduceMotion: systemReduceMotion || tutorial.reducesMotion()) {
            TutorialCardView(tutorial: tutorial, model: model, preferences: preferences, metrics: metrics)
        }
        .id(tutorial.presentationToken)
        .background {
            GeometryReader { proxy in Color.clear.preference(key: TutorialCardSize.self, value: proxy.size) }
        }
        .padding(TutorialLayout.margin)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .onPreferenceChange(TutorialCardSize.self) { size in
            guard size.width > 0, size.height > 0 else { return }
            Task { @MainActor in sized(size) }
        }
    }
}

/// The card enters rising from below, growing from 96% and fading in; it leaves sinking a little and fading out.
struct TutorialCardChrome<Content: View>: View {
    let leaving: Bool
    let reduceMotion: Bool
    @ViewBuilder let content: () -> Content
    @State private var risen = false
    @State private var shown = false

    var body: some View {
        let entered = risen || reduceMotion
        let scale = CGFloat(leaving ? (reduceMotion ? 1 : TutorialMotion.cardExitScale)
            : (entered ? 1 : TutorialMotion.cardEnterScale))
        let offset = CGFloat(leaving ? (reduceMotion ? 0 : TutorialMotion.cardExitSink)
            : (entered ? 0 : TutorialMotion.cardEnterRise))
        content()
            .opacity(leaving || !shown ? 0 : 1)
            .scaleEffect(scale)
            .offset(y: offset)
            .onAppear {
                if reduceMotion {
                    withAnimation(TutorialAnimation.crossFade) { shown = true; risen = true }
                    return
                }
                withAnimation(TutorialAnimation.curve(MotionCurves.enter, duration: TutorialMotion.cardEnterDuration)) {
                    risen = true
                }
                withAnimation(TutorialAnimation.curve(MotionCurves.fade, duration: TutorialMotion.cardEnterFade)) {
                    shown = true
                }
            }
    }
}

/// The welcome, coach and finish cards. The card itself, without its window, is what the UX reference records.
struct TutorialCardView: View {
    @ObservedObject var tutorial: TutorialController
    @ObservedObject var model: WorkspaceModel
    @ObservedObject var preferences: TutorialPreferences
    @ObservedObject var metrics: UsageMetrics
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @State private var sharingExpanded = false

    private var reduceMotion: Bool { systemReduceMotion || tutorial.reducesMotion() }

    var body: some View {
        ZStack(alignment: .topLeading) {
            card
                .id(tutorial.surface)
                .transition(TutorialAnimation.words(reduceMotion: reduceMotion))
        }
        .frame(width: TutorialLayout.width, alignment: .topLeading)
        .fixedSize(horizontal: false, vertical: true)
        .background(Color(nsColor: .windowBackgroundColor),
                    in: RoundedRectangle(cornerRadius: TutorialLayout.cornerRadius, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: TutorialLayout.cornerRadius, style: .continuous)
                .strokeBorder(Color.primary.opacity(0.1))
        }
        .clipShape(RoundedRectangle(cornerRadius: TutorialLayout.cornerRadius, style: .continuous))
        .shadow(color: .black.opacity(0.18), radius: 16, y: 6)
    }

    @ViewBuilder private var card: some View {
        switch tutorial.surface {
        case .welcome: welcome
        case .coach: coach
        case .finish: finish
        case .hidden: Color.clear.frame(height: 1)
        }
    }

    // MARK: Welcome

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(TutorialText.welcomeTitle).font(.system(size: 17, weight: .semibold))
            Text(TutorialText.welcomeBody)
                .font(.system(size: 13)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 6)
            HStack(spacing: 8) {
                Button(TutorialText.start) { tutorial.start() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("tutorial-start")
                Button(TutorialText.notNow) { tutorial.notNow() }
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("tutorial-not-now")
            }
            .padding(.top, 14)
            Divider().padding(.vertical, 12)
            Toggle(TutorialText.showOnLaunch, isOn: Binding(
                get: { !preferences.suppressed }, set: { preferences.setSuppressed(!$0) }))
                .accessibilityIdentifier("tutorial-show-on-launch")
            VStack(alignment: .leading, spacing: 2) {
                Toggle(TutorialText.usageCounts, isOn: Binding(
                    get: { metrics.enabled }, set: { metrics.setEnabled($0) }))
                    .help(TutorialText.usageCountsHelp)
                Text(TutorialText.usageCountsNote)
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(.leading, 20)
            }
            .padding(.top, 6)
            Text(TutorialText.findAgain)
                .font(.caption).foregroundStyle(.secondary)
                .padding(.top, 12)
        }
        .padding(TutorialLayout.padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("tutorial-welcome")
    }

    // MARK: Coach

    private var coach: some View {
        let reduce = reduceMotion
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                TutorialProgressTrack(step: tutorial.shownStep.index, count: TutorialStep.allCases.count,
                                      reduceMotion: reduce, edges: reviewEdges)
                Text(TutorialText.stepValue(tutorial.shownStep))
                    .font(.caption).monospacedDigit().foregroundStyle(.secondary)
                    .fixedSize()
                Button { tutorial.end() } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 11, weight: .semibold))
                        .frame(width: 20, height: 20)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help(TutorialText.endTutorial)
                .accessibilityLabel(TutorialText.endTutorial)
                .accessibilityIdentifier("tutorial-end")
            }
            stepWords.padding(.top, 12)
            HStack(spacing: 8) {
                Button(TutorialText.showMe) { tutorial.showMe() }
                    .disabled(tutorial.shownGoal == nil)
                    .keyframeAnimator(initialValue: TutorialPress(), trigger: tutorial.showMePulseToken) { content, value in
                        content.scaleEffect(CGFloat(reduce ? 1 : 1 + 0.06 * value.depth))
                    } keyframes: { _ in
                        KeyframeTrack(\.depth) {
                            CubicKeyframe(1, duration: 0.18)
                            CubicKeyframe(0, duration: 0.32)
                        }
                    }
                    .accessibilityIdentifier("tutorial-show-me")
                Spacer()
                Button(TutorialText.skipStep) { tutorial.skipStep() }
                    .accessibilityIdentifier("tutorial-skip")
            }
            .padding(.top, 14)
        }
        .padding(TutorialLayout.padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("tutorial-coach")
    }

    private var reviewEdges: (lower: Double, upper: Double)? {
        guard let review = tutorial.reviewStepChange else { return nil }
        return TutorialStepChange.trackEdges(from: review.from.index, to: tutorial.shownStep.index, at: review.time)
    }

    @ViewBuilder private var stepWords: some View {
        if let review = tutorial.reviewStepChange {
            let leaving = TutorialStepChange.outgoing(at: review.time)
            let arriving = TutorialStepChange.incoming(at: review.time)
            ZStack(alignment: .topLeading) {
                if review.time < TutorialMotion.stepOutDuration {
                    words(review.from, goalsDone: review.from.goals.count, stepDone: true, hint: false)
                        .opacity(leaving.opacity)
                        .offset(y: leaving.offset)
                }
                words(tutorial.shownStep, goalsDone: 0, stepDone: false, hint: false)
                    .opacity(arriving.opacity)
                    .offset(y: arriving.offset)
            }
        } else {
            ZStack(alignment: .topLeading) {
                words(tutorial.shownStep, goalsDone: tutorial.shownGoalsDone, stepDone: tutorial.shownStepDone,
                      hint: tutorial.hintVisible)
                    .id(tutorial.shownStep)
                    .transition(TutorialAnimation.words(reduceMotion: reduceMotion))
            }
        }
    }

    private func words(_ step: TutorialStep, goalsDone: Int, stepDone: Bool, hint: Bool) -> some View {
        let text = TutorialText.step(step, preferences: model.preferences)
        let goals = step.goals
        let current = stepDone ? nil : goals[min(goalsDone, goals.count - 1)]
        let keys = current.map { TutorialText.keys(for: $0, preferences: model.preferences) } ?? []
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: 6) {
                Text(text.title).font(.system(size: 15, weight: .semibold))
                if stepDone {
                    TutorialCheckBadge(size: 16, reduceMotion: reduceMotion)
                        .transition(TutorialAnimation.checkTransition(reduceMotion: reduceMotion))
                        .accessibilityLabel("Done")
                }
            }
            Text(text.detail)
                .font(.system(size: 13)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 4)
            if text.goals.count >= 2 {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(text.goals.enumerated()), id: \.offset) { index, goal in
                        HStack(alignment: .top, spacing: 8) {
                            TutorialGoalMark(done: index < goalsDone, reduceMotion: reduceMotion)
                                .padding(.top, 1)
                            Text(goal)
                                .font(.system(size: 13))
                                .foregroundStyle(index == goalsDone && !stepDone ? .primary : .secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        .accessibilityElement(children: .combine)
                        .accessibilityValue(index < goalsDone ? "Done" : index == goalsDone ? "Current" : "Next")
                    }
                }
                .padding(.top, 10)
            }
            if !keys.isEmpty {
                TutorialKeycaps(keys: keys, pressToken: tutorial.keyPressToken, reduceMotion: reduceMotion)
                    .padding(.top, 10)
            }
            if hint {
                Text(text.hint)
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 8)
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: Finish

    private var finish: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(TutorialText.finishTitle).font(.system(size: 17, weight: .semibold))
            Text(TutorialText.finishLead)
                .font(.system(size: 13)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 4)
            VStack(spacing: 6) {
                ForEach(Array(TutorialText.finishRows(preferences: model.preferences).enumerated()), id: \.offset) { _, row in
                    HStack {
                        Text(row.label).font(.system(size: 13))
                        Spacer()
                        TutorialKeycaps(keys: row.keys)
                    }
                    .accessibilityElement(children: .combine)
                }
            }
            .padding(.top, 12)
            Text(TutorialText.changeShortcuts)
                .font(.callout).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 12)
            Button(TutorialText.openSettings) { tutorial.openSettings() }
                .accessibilityIdentifier("tutorial-open-settings")
                .padding(.top, 6)
            DisclosureGroup(TutorialText.sharingTitle, isExpanded: $sharingExpanded) {
                Text(TutorialText.sharingBody)
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 8)
            }
            .accessibilityIdentifier("tutorial-sharing")
            .padding(.top, 12)
            HStack(alignment: .center) {
                Text(TutorialText.practiceAgain).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(TutorialText.done) { tutorial.done() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("tutorial-done")
            }
            .padding(.top, 14)
        }
        .padding(TutorialLayout.padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("tutorial-finish")
    }
}

/// How far a keycap or button is pressed, from 0 to 1.
struct TutorialPress {
    var depth = 0.0
}

/// The keys to press. When `pressToken` changes they go down one after another, as if pressed.
struct TutorialKeycaps: View {
    let keys: [TutorialKey]
    var pressToken = 0
    var reduceMotion = false

    var body: some View {
        let reduce = reduceMotion
        HStack(spacing: 5) {
            ForEach(Array(keys.enumerated()), id: \.offset) { index, key in
                Keycap(text: key.symbol)
                    .keyframeAnimator(initialValue: TutorialPress(), trigger: pressToken) { content, value in
                        content
                            .offset(y: CGFloat(reduce ? 0 : TutorialMotion.keyTravel * value.depth))
                            .overlay {
                                RoundedRectangle(cornerRadius: 6).fill(Color.accentColor.opacity(0.3 * value.depth))
                            }
                    } keyframes: { _ in
                        KeyframeTrack(\.depth) {
                            LinearKeyframe(0, duration: TutorialMotion.keyStagger * Double(index))
                            CubicKeyframe(1, duration: TutorialMotion.keyDownDuration)
                            CubicKeyframe(0, duration: TutorialMotion.keyUpDuration)
                        }
                    }
                    .help(key.name)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(keys.map(\.name).joined(separator: " + "))
    }
}

/// An accent circle whose check mark draws itself.
struct TutorialCheckBadge: View {
    let size: CGFloat
    let reduceMotion: Bool
    @State private var drawn: CGFloat = 0

    var body: some View {
        ZStack {
            Circle().fill(Color.accentColor)
            TutorialCheckShape()
                .trim(from: 0, to: reduceMotion ? 1 : drawn)
                .stroke(Color.white, style: StrokeStyle(lineWidth: max(1.5, size * 0.13), lineCap: .round, lineJoin: .round))
                .padding(size * 0.26)
        }
        .frame(width: size, height: size)
        .onAppear {
            guard !reduceMotion else { return }
            withAnimation(TutorialAnimation.curve(MotionCurves.enter, duration: TutorialMotion.checkDuration)) { drawn = 1 }
        }
    }
}

struct TutorialCheckShape: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY + rect.height * 0.55))
        path.addLine(to: CGPoint(x: rect.minX + rect.width * 0.38, y: rect.maxY - rect.height * 0.08))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY + rect.height * 0.1))
        return path
    }
}

/// A goal's mark: an empty circle, then a check that pops in.
struct TutorialGoalMark: View {
    let done: Bool
    let reduceMotion: Bool

    var body: some View {
        ZStack {
            Circle()
                .strokeBorder(Color.secondary.opacity(0.5), lineWidth: 1.5)
                .opacity(done ? 0 : 1)
            if done {
                TutorialCheckBadge(size: 14, reduceMotion: reduceMotion)
                    .transition(TutorialAnimation.checkTransition(reduceMotion: reduceMotion))
            }
        }
        .frame(width: 14, height: 14)
        .accessibilityHidden(true)
    }
}

/// The eleven steps as segments. Done segments and the current one are accent. Moving on, the leading edge travels
/// first and the trailing edge follows, like the old tour's step capsule.
struct TutorialProgressTrack: View {
    let step: Int
    let count: Int
    let reduceMotion: Bool
    /// Fixed edges for design review; nil animates.
    let edges: (lower: Double, upper: Double)?
    @State private var lower: Double
    @State private var upper: Double
    private let spacing: CGFloat = 3

    init(step: Int, count: Int, reduceMotion: Bool, edges: (lower: Double, upper: Double)? = nil) {
        self.step = step
        self.count = count
        self.reduceMotion = reduceMotion
        self.edges = edges
        _lower = State(initialValue: Double(step))
        _upper = State(initialValue: Double(step))
    }

    var body: some View {
        let done = edges.map { Int($0.lower.rounded(.down)) } ?? step
        HStack(spacing: spacing) {
            ForEach(0..<count, id: \.self) { index in
                Capsule()
                    .fill(index < done ? Color.accentColor : Color.secondary.opacity(0.2))
                    .frame(height: 4)
                    .frame(maxWidth: .infinity)
            }
        }
        .overlay {
            TutorialTrackCapsule(lower: edges?.lower ?? lower, upper: edges?.upper ?? upper, count: count, spacing: spacing)
                .fill(Color.accentColor)
                .frame(height: 4)
        }
        .frame(height: 8)
        .onChange(of: step) { old, new in
            let target = Double(new)
            if reduceMotion {
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) { lower = target; upper = target }
                return
            }
            let forward = new > old
            withAnimation(TutorialAnimation.curve(MotionCurves.standard, duration: TutorialMotion.trackLeadDuration)) {
                if forward { upper = target } else { lower = target }
            }
            withAnimation(TutorialAnimation.curve(MotionCurves.standard, duration: TutorialMotion.trackTrailDuration)
                .delay(TutorialMotion.trackTrailDelay)) {
                if forward { lower = target } else { upper = target }
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Tutorial progress")
        .accessibilityValue("Step \(step + 1) of \(count)")
    }
}

struct TutorialTrackCapsule: Shape {
    var lower: Double
    var upper: Double
    let count: Int
    let spacing: CGFloat

    var animatableData: AnimatablePair<Double, Double> {
        get { AnimatablePair(lower, upper) }
        set { lower = newValue.first; upper = newValue.second }
    }

    func path(in rect: CGRect) -> Path {
        let segment = (rect.width - spacing * CGFloat(count - 1)) / CGFloat(count)
        let start = CGFloat(min(lower, upper)) * (segment + spacing)
        let end = CGFloat(max(lower, upper)) * (segment + spacing) + segment
        return Capsule().path(in: CGRect(x: rect.minX + start, y: rect.minY, width: end - start, height: rect.height))
    }
}
