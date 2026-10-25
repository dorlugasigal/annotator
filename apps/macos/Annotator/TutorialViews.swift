import AppKit
import SwiftUI
import AnnotationCore
import MotionCore

/// The words of one tutorial step: its title, one line when the step has one goal, a line per goal when it has several,
/// and a hint. Keys are marked in braces, such as "Press {2}"; the card shows them as keycaps inside the words.
struct TutorialStepText: Equatable {
    let title: String
    let detail: String?
    let goals: [String]
    let hint: String
}

/// One word or keycap of a line, and whether a space comes before it.
struct TutorialLineToken: Equatable {
    let text: String
    let isKey: Bool
    let spaceBefore: Bool
    /// The keycap's place among the line's keycaps, for the press's stagger; 0 for words.
    let keyIndex: Int
}

/// The tutorial's words: short, casual and direct. Keys follow the person's shortcuts; a tool without a key uses the
/// words without one.
enum TutorialText {
    static let welcomeTitle = "Welcome to Annotator"
    static let welcomeBody = "Let's learn the basics. It takes two minutes, and keys or mouse both work."
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
    static let finishTitle = "You did it!"
    static let finishLead = "Keep these handy:"
    static let changeShortcuts = "Change any shortcut in Settings > Toolbar & Shortcuts."
    static let openSettings = "Open Settings"
    static let sharingTitle = "Before you share"
    static let sharingBody =
        "App or window sharing may leave out covers. Ask someone to check. Hiding, quitting or a crash removes them. Blur can leave details readable.\n\nDrawing needs no permissions. Zoom, effects and region sharing ask when needed."
    static let practiceAgain = "Replay this any time from Settings > Help."
    static let done = "Done"

    /// Keycaps of keys that are the same for everyone.
    static let escapeKey = "esc"
    static let returnKey = "return"
    static let undoShortcut = "⌘Z"
    static let inkBlueKey = "B"
    static let inkGreenKey = "G"

    /// "Step 3 of 10".
    static func stepValue(_ step: TutorialStep) -> String { "Step \(step.index + 1) of \(TutorialStep.allCases.count)" }

    /// Whether the color step asks for green: when the drawing (or the tool) is blue already, the blue ink key would change
    /// nothing. `color` is the stroke color the toolbar shows.
    static func asksForGreen(_ color: RGBA?) -> Bool {
        guard let color else { return false }
        let blue = RGBA.blue
        return abs(color.red - blue.red) < 0.002 && abs(color.green - blue.green) < 0.002 &&
            abs(color.blue - blue.blue) < 0.002
    }

    /// The ink key the color step shows: B, or G when the drawing is blue already.
    static func colorKey(_ color: RGBA?) -> String { asksForGreen(color) ? inkGreenKey : inkBlueKey }

    /// The words of `step` with the person's keys; `color` is the stroke color the toolbar shows.
    static func step(_ step: TutorialStep, preferences: WorkspacePreferences, color: RGBA? = nil) -> TutorialStepText {
        let resume = resumeShortcut(preferences).label
        let showHide = preferences.activationShortcut.label
        func pick(_ name: String, _ tool: Tool) -> String {
            toolKey(tool, preferences).map { "Grab the \(name): press {\($0)} or click it" }
                ?? "Grab the \(name): click it in the toolbar"
        }
        switch step {
        case .rectangle:
            return TutorialStepText(
                title: "Grab the Rectangle",
                detail: toolKey(.rectangle, preferences).map { "Press {\($0)}, or click it in the toolbar." }
                    ?? "Click the Rectangle in the toolbar.",
                goals: [], hint: "It's the square. Look for the blue ring.")
        case .shape:
            return TutorialStepText(title: "Draw a box", detail: "Drag anywhere on your screen.", goals: [],
                                    hint: "Press, drag, let go.")
        case .color:
            return TutorialStepText(title: asksForGreen(color) ? "Make it green" : "Make it blue",
                                    detail: "Press {\(colorKey(color))}, or pick any color in the toolbar.",
                                    goals: [], hint: "Any color works. New drawings use it too.")
        case .arrow:
            return TutorialStepText(
                title: "Draw a curved arrow", detail: nil,
                goals: [pick("Arrow", .arrow), "Turn on Curved in the properties",
                        "Click points, then double-click (or {\(returnKey)})"],
                hint: "Click, don't drag. Can't see Curved? Click the sliders button.")
        case .pen:
            return TutorialStepText(title: "Circle something", detail: nil,
                                    goals: [pick("Pen", .pen), "Draw a circle around anything"],
                                    hint: "Hold the mouse button down as you draw.")
        case .move:
            return TutorialStepText(
                title: "Move it", detail: nil,
                goals: [toolKey(.select, preferences).map { "Grab Select: press {\($0)} or click it" }
                            ?? "Grab Select: click it in the toolbar",
                        "Drag, turn or resize a drawing"],
                hint: "Grab the line, not the empty middle.")
        case .undo:
            return TutorialStepText(title: "Oops! Undo that", detail: "Press {\(undoShortcut)}, or click Undo.", goals: [],
                                    hint: "Changed your mind again? Redo is right next to it.")
        case .useApps:
            return TutorialStepText(title: "Back to your apps",
                                    detail: "Press {\(escapeKey)}, or click Use apps. Your drawings stay put.", goals: [],
                                    hint: "Typing or mid-drawing? Press {\(escapeKey)} twice.")
        case .drawAgain:
            return TutorialStepText(title: "Draw again", detail: "Press {\(resume)} from any app, or click a tool.",
                                    goals: [], hint: "That shortcut works everywhere, even in other apps.")
        case .hide:
            return TutorialStepText(
                title: "Now you see it…", detail: nil,
                goals: ["Hide it all: press {\(showHide)}", "Bring it back: press {\(showHide)} again"],
                hint: "No keys? Use × in the toolbar to hide, and the menu bar icon to bring it back.")
        }
    }

    /// The keycaps for `goal`, one per shortcut, such as "⌃⇧2"; empty when the goal has no key. `color` is the stroke
    /// color the toolbar shows.
    static func keys(for goal: TutorialGoal, preferences: WorkspacePreferences, color: RGBA? = nil) -> [String] {
        switch goal {
        case .drawing: [resumeShortcut(preferences).label]
        case .rectangleTool: toolKeys(.rectangle, preferences)
        case .colorChanged: [colorKey(color)]
        case .arrowTool: toolKeys(.arrow, preferences)
        case .curvedArrowDrawn: [returnKey]
        case .penTool: toolKeys(.pen, preferences)
        case .selectTool: toolKeys(.select, preferences)
        case .undone: [undoShortcut]
        case .usingApps: [escapeKey]
        case .hidden, .shown: [preferences.activationShortcut.label]
        case .shapeDrawn, .curvedStyle, .penDrawn, .moved: []
        }
    }

    /// Whether the card shows the keyboard for `goal`: a shortcut with a modifier, or esc.
    static func showsKeyboard(_ goal: TutorialGoal, preferences: WorkspacePreferences, color: RGBA? = nil) -> Bool {
        TutorialKeyboard.shows(keys(for: goal, preferences: preferences, color: color), mac: true)
    }

    /// The finish card's keys: Draw, Use apps, Show or hide and Undo.
    static func finishRows(preferences: WorkspacePreferences) -> [(label: String, keys: [String])] {
        [("Draw", [resumeShortcut(preferences).label]), ("Use apps", [escapeKey]),
         ("Show or hide", [preferences.activationShortcut.label]), ("Undo", [undoShortcut])]
    }

    /// A line split into its words and its keys: "Press {⌘Z}, or click Undo." gives ("Press ", no keys), ("", ⌘Z) and
    /// (", or click Undo.", no keys).
    static func segments(_ line: String) -> [(text: String, keys: [String])] {
        var result: [(text: String, keys: [String])] = []
        var rest = line[...]
        while !rest.isEmpty {
            guard let open = rest.firstIndex(of: "{"),
                  let close = rest[rest.index(after: open)...].firstIndex(of: "}") else {
                result.append((text: String(rest), keys: []))
                break
            }
            if open > rest.startIndex { result.append((text: String(rest[..<open]), keys: [])) }
            let key = String(rest[rest.index(after: open)..<close])
            result.append((text: "", keys: key.isEmpty ? [] : [key]))
            rest = rest[rest.index(after: close)...]
        }
        return result
    }

    /// The line as VoiceOver and tests read it: keys without their braces.
    static func plain(_ line: String) -> String {
        line.replacingOccurrences(of: "{", with: "").replacingOccurrences(of: "}", with: "")
    }

    /// The line's words and keycaps in order. Words and keycaps written together, such as "{2}," stay together.
    static func tokens(_ line: String) -> [TutorialLineToken] {
        var result: [TutorialLineToken] = []
        var space = false
        var keyCount = 0
        for segment in segments(line) {
            if segment.keys.isEmpty {
                for (index, word) in segment.text.split(separator: " ", omittingEmptySubsequences: false).enumerated() {
                    if index > 0 { space = true }
                    guard !word.isEmpty else { continue }
                    result.append(TutorialLineToken(text: String(word), isKey: false, spaceBefore: space && !result.isEmpty,
                                                    keyIndex: 0))
                    space = false
                }
            } else {
                for key in segment.keys {
                    result.append(TutorialLineToken(text: key, isKey: true, spaceBefore: space && !result.isEmpty,
                                                    keyIndex: keyCount))
                    keyCount += 1
                    space = false
                }
            }
        }
        return result
    }

    /// What VoiceOver reads for keycaps: "⌃⇧2" is "Control + Shift + 2".
    static func spoken(_ keys: [String]) -> String { keys.map { spoken($0) }.joined(separator: " + ") }

    static func spoken(_ key: String) -> String {
        if key == escapeKey { return "Escape" }
        if key == returnKey { return "Return" }
        return key.map { spokenSymbol($0) }.joined(separator: " + ")
    }

    private static func spokenSymbol(_ character: Character) -> String {
        switch character {
        case "⌃": return "Control"
        case "⌥": return "Option"
        case "⇧": return "Shift"
        case "⌘": return "Command"
        default: return String(character).uppercased()
        }
    }

    /// The Mac keycap words for the tutorial keyboard's keys.
    static func keyboardLabel(_ id: String) -> String {
        switch id {
        case "esc", "tab", "caps", "fn": return id
        case "shift": return "⇧"
        case "control": return "⌃"
        case "option": return "⌥"
        case "command": return "⌘"
        case "space": return ""
        default: return id.uppercased()
        }
    }

    private static func resumeShortcut(_ preferences: WorkspacePreferences) -> ActivationShortcut {
        preferences.resumeShortcut ?? .resumeDrawing
    }

    private static func toolKey(_ tool: Tool, _ preferences: WorkspacePreferences) -> String? {
        preferences.shortcut(for: .tool(tool))?.label
    }

    private static func toolKeys(_ tool: Tool, _ preferences: WorkspacePreferences) -> [String] {
        toolKey(tool, preferences).map { [$0] } ?? []
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
            Text(TutorialText.welcomeTitle).font(.system(size: 20, weight: .semibold))
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

    /// A step with one goal has a line; a step with several has a row per goal. Keys show inside the words, and a shortcut
    /// with a modifier or esc also lights up on the keyboard. The title's check has its room from the start, so a finished
    /// step never changes the card's size.
    private func words(_ step: TutorialStep, goalsDone: Int, stepDone: Bool, hint: Bool) -> some View {
        let preferences = model.preferences
        let color = tutorial.shownColor
        let text = TutorialText.step(step, preferences: preferences, color: color)
        let goals = step.goals
        let keyGoal = goals[min(goalsDone, goals.count - 1)]
        let current = stepDone ? -1 : min(goalsDone, goals.count - 1)
        let token = tutorial.keyPressToken
        let reduce = reduceMotion
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: 8) {
                Text(text.title)
                    .font(.system(size: 15, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)
                TutorialTitleCheck(done: stepDone, reduceMotion: reduce)
            }
            .frame(height: 22)
            if let detail = text.detail {
                TutorialKeyLine(line: detail, size: 13, pressToken: token, pressing: current >= 0, reduceMotion: reduce)
                    .foregroundStyle(.secondary)
                    .padding(.top, 6)
            }
            if !text.goals.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(Array(text.goals.enumerated()), id: \.offset) { index, goal in
                        HStack(alignment: .center, spacing: 8) {
                            TutorialGoalMark(done: index < goalsDone, reduceMotion: reduce)
                                .frame(width: 22, height: 22)
                            TutorialKeyLine(line: goal, size: 13, pressToken: token, pressing: index == current,
                                            reduceMotion: reduce)
                                .foregroundStyle(index == current ? Color.primary : Color.secondary)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .accessibilityElement(children: .combine)
                        .accessibilityValue(index < goalsDone ? "Done" : index == current ? "Current" : "Next")
                    }
                }
                .padding(.top, 10)
            }
            if TutorialText.showsKeyboard(keyGoal, preferences: preferences, color: color) {
                TutorialKeyboardView(keys: TutorialText.keys(for: keyGoal, preferences: preferences, color: color),
                                     pressToken: token,
                                     pressing: current >= 0, reduceMotion: reduce)
                    .padding(.top, 12)
            }
            if hint {
                TutorialKeyLine(line: text.hint, size: 11, pressToken: 0, pressing: false, reduceMotion: reduce)
                    .foregroundStyle(.secondary)
                    .padding(.top, 10)
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: Finish

    private var finish: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(TutorialText.finishTitle).font(.system(size: 20, weight: .semibold))
            Text(TutorialText.finishLead)
                .font(.system(size: 13)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 4)
            VStack(spacing: 6) {
                ForEach(Array(TutorialText.finishRows(preferences: model.preferences).enumerated()), id: \.offset) { _, row in
                    HStack {
                        Text(row.label).font(.system(size: 13, weight: .semibold))
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

/// Keycaps five points apart, read as one element named like "Control + Shift + 2" (the finish card's rows).
struct TutorialKeycaps: View {
    let keys: [String]

    var body: some View {
        HStack(spacing: 5) {
            ForEach(Array(keys.enumerated()), id: \.offset) { _, key in
                Keycap(text: key).help(TutorialText.spoken(key))
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(TutorialText.spoken(keys))
    }
}

/// Whether a word of a line has a space before it.
private struct TutorialSpaceBefore: LayoutValueKey {
    static let defaultValue = false
}

/// Lays out a line's words and keycaps like text: left to right, onto a new row when the next word doesn't fit, each
/// centered on its row. Words and keycaps written together stay on one row.
struct TutorialLineFlow: Layout {
    var space: CGFloat
    var lineSpacing: CGFloat

    private struct Row {
        var items: [(index: Int, x: CGFloat, size: CGSize)] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width, subviews: subviews)
        let width = rows.map(\.width).max() ?? 0
        let height = rows.reduce(CGFloat(0)) { $0 + $1.height } + lineSpacing * CGFloat(max(0, rows.count - 1))
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(width: bounds.width, subviews: subviews) {
            for item in row.items {
                subviews[item.index].place(at: CGPoint(x: bounds.minX + item.x, y: y + row.height / 2), anchor: .leading,
                                           proposal: ProposedViewSize(item.size))
            }
            y += row.height + lineSpacing
        }
    }

    private func arrange(width: CGFloat?, subviews: Subviews) -> [Row] {
        let limit = (width ?? .infinity) + 0.5
        var chunks: [[Int]] = []
        for index in subviews.indices {
            if chunks.isEmpty || subviews[index][TutorialSpaceBefore.self] {
                chunks.append([index])
            } else {
                chunks[chunks.count - 1].append(index)
            }
        }
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        var rows: [Row] = []
        var row = Row()
        for chunk in chunks {
            let chunkWidth = chunk.reduce(CGFloat(0)) { $0 + sizes[$1].width }
            if !row.items.isEmpty, row.width + space + chunkWidth > limit {
                rows.append(row)
                row = Row()
            }
            var x = row.items.isEmpty ? 0 : row.width + space
            for index in chunk {
                row.items.append((index: index, x: x, size: sizes[index]))
                x += sizes[index].width
                row.height = max(row.height, sizes[index].height)
            }
            row.width = x
        }
        if !row.items.isEmpty { rows.append(row) }
        return rows
    }
}

/// A line whose keys, marked in braces, show as small keycaps inside the words. VoiceOver reads the plain words.
/// When `pressToken` changes on the current line (`pressing`), its keycaps go down one after another.
struct TutorialKeyLine: View {
    let line: String
    let size: CGFloat
    var pressToken = 0
    var pressing = false
    var reduceMotion = false

    var body: some View {
        let tokens = TutorialText.tokens(line)
        TutorialLineFlow(space: size * 0.28, lineSpacing: 2) {
            ForEach(Array(tokens.enumerated()), id: \.offset) { _, token in
                piece(token)
                    .layoutValue(key: TutorialSpaceBefore.self, value: token.spaceBefore)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(TutorialText.plain(line))
    }

    @ViewBuilder private func piece(_ token: TutorialLineToken) -> some View {
        if token.isKey {
            TutorialInlineKey(text: token.text, index: token.keyIndex, pressToken: pressToken, pressing: pressing,
                              reduceMotion: reduceMotion)
        } else {
            Text(token.text).font(.system(size: size)).fixedSize()
        }
    }
}

/// A small keycap inside a line: monospaced semibold on a faint fill with a fine outline and 4-point corners.
struct TutorialInlineKey: View {
    let text: String
    var index = 0
    var pressToken = 0
    var pressing = false
    var reduceMotion = false

    var body: some View {
        let reduce = reduceMotion
        let pressed = pressing
        let delay = TutorialMotion.keyStagger * Double(index)
        Text(text)
            .font(.system(size: 11, weight: .semibold, design: .monospaced))
            .foregroundStyle(Color.primary)
            .lineLimit(1)
            .fixedSize()
            .padding(.horizontal, 5)
            .frame(minWidth: 18)
            .frame(height: 18)
            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 4, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 4, style: .continuous).strokeBorder(Color.primary.opacity(0.22), lineWidth: 1)
            }
            .keyframeAnimator(initialValue: TutorialPress(), trigger: pressToken) { content, value in
                let depth = pressed ? value.depth : 0
                content
                    .offset(y: CGFloat(reduce ? 0 : TutorialMotion.keyTravel * depth))
                    .overlay {
                        RoundedRectangle(cornerRadius: 4, style: .continuous).fill(Color.accentColor.opacity(0.3 * depth))
                    }
            } keyframes: { _ in
                KeyframeTrack(\.depth) {
                    LinearKeyframe(0, duration: delay)
                    CubicKeyframe(1, duration: TutorialMotion.keyDownDuration)
                    CubicKeyframe(0, duration: TutorialMotion.keyUpDuration)
                }
            }
            .padding(.horizontal, 1)
            .accessibilityHidden(true)
    }
}

/// The left part of a keyboard with the shortcut's keys lit in the accent: the esc row, the number row, three letter
/// rows and the bottom row, cut at its right edge, which fades into the card.
struct TutorialKeyboardView: View {
    let keys: [String]
    var pressToken = 0
    var pressing = false
    var reduceMotion = false

    var body: some View {
        let lit = TutorialKeyboard.ids(keys, mac: true) ?? []
        let width = CGFloat(TutorialKeyboard.width)
        let height = CGFloat(TutorialKeyboard.height)
        let fade = CGFloat(TutorialKeyboard.fadeWidth)
        let background = Color(nsColor: .windowBackgroundColor)
        let shown = TutorialKeyboard.keys(mac: true).filter { TutorialKeyboard.frame($0).x < TutorialKeyboard.width }
        ZStack(alignment: .topLeading) {
            ForEach(shown, id: \.id) { key in
                let frame = TutorialKeyboard.frame(key)
                TutorialKeyboardKey(key: key, litIndex: lit.firstIndex(of: key.id), pressToken: pressToken,
                                    pressing: pressing, reduceMotion: reduceMotion)
                    .frame(width: CGFloat(frame.width), height: CGFloat(frame.height))
                    .offset(x: CGFloat(frame.x), y: CGFloat(frame.y))
            }
            LinearGradient(colors: [background.opacity(0), background], startPoint: .leading, endPoint: .trailing)
                .frame(width: fade, height: height)
                .offset(x: width - fade)
        }
        .frame(width: width, height: height, alignment: .topLeading)
        .clipped()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Keyboard: \(TutorialText.spoken(keys))")
    }
}

/// One key of the tutorial keyboard. A lit key is accent with a white label and presses with the line's keycaps.
private struct TutorialKeyboardKey: View {
    let key: TutorialKey
    /// The key's place among the lit keys, or nil when it is not lit.
    let litIndex: Int?
    let pressToken: Int
    let pressing: Bool
    let reduceMotion: Bool

    var body: some View {
        let on = litIndex != nil
        let word = key.id.count > 1
        // Only keys wider than one unit, such as tab and shift, put their word at the left; esc and F1 stay centered.
        let wide = key.width > 1
        let reduce = reduceMotion
        let pressed = pressing && on
        let delay = TutorialMotion.keyStagger * Double(litIndex ?? 0)
        Text(TutorialText.keyboardLabel(key.id))
            .font(.system(size: word ? 8.5 : 10, weight: on ? .semibold : .regular))
            .foregroundStyle(on ? Color.white : Color.secondary)
            .lineLimit(1)
            .padding(.leading, wide ? 5 : 0)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: wide ? .leading : .center)
            .background(on ? Color.accentColor : Color.primary.opacity(0.05),
                        in: RoundedRectangle(cornerRadius: 4, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .strokeBorder(on ? Color.accentColor : Color.primary.opacity(0.14), lineWidth: 1)
            }
            .keyframeAnimator(initialValue: TutorialPress(), trigger: pressToken) { content, value in
                content.offset(y: CGFloat(reduce || !pressed ? 0 : TutorialMotion.keyTravel * value.depth))
            } keyframes: { _ in
                KeyframeTrack(\.depth) {
                    LinearKeyframe(0, duration: delay)
                    CubicKeyframe(1, duration: TutorialMotion.keyDownDuration)
                    CubicKeyframe(0, duration: TutorialMotion.keyUpDuration)
                }
            }
    }
}

/// The title's check. Its room is there from the start; when the step is done it pops in and draws itself.
struct TutorialTitleCheck: View {
    let done: Bool
    let reduceMotion: Bool

    var body: some View {
        ZStack {
            if done {
                TutorialCheckBadge(size: 16, reduceMotion: reduceMotion)
                    .transition(TutorialAnimation.checkTransition(reduceMotion: reduceMotion))
            }
        }
        .frame(width: 16, height: 16)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(done ? TutorialText.done : "")
        .accessibilityHidden(!done)
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

/// The steps as segments. Done segments and the current one are accent. Moving on, the leading edge travels
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
