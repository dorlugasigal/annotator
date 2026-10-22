import AppKit
import SwiftUI

enum IntroductionPage: Int, CaseIterable, Identifiable, Sendable {
    case draw, color, focus, setup

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .draw: "Make a point"
        case .color: "Make it yours"
        case .focus: "Lead the way"
        case .setup: "A little setup"
        }
    }

    /// Seconds for the page's illustration to play once. Nothing loops and nothing advances the page.
    var storyDuration: Double {
        switch self {
        case .draw: 4.8
        case .color: 3.6
        case .focus: 4.2
        case .setup: 1.1
        }
    }

    var illustrationDescription: String? {
        switch self {
        case .draw:
            "A pen circles a bar on a chart. The pointer clicks a button, and the circle stays. Then the drawing hides and comes back."
        case .color:
            "The palette switches to a gradient. A rounded outline draws itself, a light fill fades in and the label \"Make your point\" appears."
        case .focus:
            "The pointer moves to a small price. A ring appears around it, the rest of the screen dims and a zoom lens makes the price bigger."
        case .setup: nil
        }
    }
}

enum IntroductionDirection: Equatable, Sendable {
    case forward, backward

    var sign: CGFloat { self == .forward ? 1 : -1 }
}

struct IntroductionPlayback: Equatable, Sendable {
    let page: IntroductionPage
    let animated: Bool
}

/// Owns tour navigation and playback state. Pages change only through explicit navigation.
@MainActor
final class IntroductionNavigator: ObservableObject {
    @Published private(set) var page: IntroductionPage = .draw
    @Published private(set) var playbackToken = 0
    @Published private(set) var isPresented = false
    /// Read by the page transition when a page is inserted or removed, so Back always moves right.
    private(set) var direction: IntroductionDirection = .forward
    private(set) var playbacks: [IntroductionPlayback] = []
    private(set) var finishedPlaybacks: [IntroductionPage] = []
    var reducesMotion: () -> Bool = { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }
    /// Freezes every illustration at one moment. Used for design review frames and tests.
    var reviewProgress: Double?
    /// Shows chosen permission states in review frames instead of reading the system.
    var reviewPermissions: (screen: Bool, gestures: Bool)?
    var storyTimeScale = 1.0

    var canGoBack: Bool { page.rawValue > 0 }
    var canGoForward: Bool { page.rawValue < IntroductionPage.allCases.count - 1 }

    func select(_ target: IntroductionPage) {
        guard target != page else { return }
        direction = target.rawValue > page.rawValue ? .forward : .backward
        withAnimation(IntroductionMotion.pageAnimation(reduceMotion: reducesMotion())) { page = target }
    }

    @discardableResult
    func move(_ direction: IntroductionDirection) -> Bool {
        guard let target = IntroductionPage(rawValue: page.rawValue + (direction == .forward ? 1 : -1)) else {
            return false
        }
        select(target)
        return true
    }

    func replay() {
        guard isPresented else { return }
        playbackToken &+= 1
    }

    /// Hiding the window restarts the story in its final state, which also ends any running animation.
    func setPresented(_ visible: Bool) {
        guard visible != isPresented else { return }
        isPresented = visible
        playbackToken &+= 1
    }

    func notePlayback(_ playback: IntroductionPlayback) {
        playbacks.append(playback)
        if playbacks.count > 24 { playbacks.removeFirst(playbacks.count - 24) }
    }

    func noteFinished(_ page: IntroductionPage) {
        finishedPlaybacks.append(page)
        if finishedPlaybacks.count > 24 { finishedPlaybacks.removeFirst(finishedPlaybacks.count - 24) }
    }
}

enum IntroductionMotion {
    static let storyDelay = 0.3
    static let pageOffset: CGFloat = 28

    static func pageAnimation(reduceMotion: Bool) -> Animation {
        reduceMotion ? .easeInOut(duration: 0.2) : .spring(duration: 0.5, bounce: 0)
    }

    static func pageTransition(navigator: IntroductionNavigator, reduceMotion: Bool) -> AnyTransition {
        if reduceMotion { return .opacity.animation(.easeInOut(duration: 0.2)) }
        // Fade through: the old page clears quickly, then the new one glides in, so text never stacks up.
        return .asymmetric(
            insertion: AnyTransition(IntroductionPageTransition(navigator: navigator, entering: true))
                .animation(.spring(duration: 0.5, bounce: 0).delay(0.1)),
            removal: AnyTransition(IntroductionPageTransition(navigator: navigator, entering: false))
                .animation(.easeOut(duration: 0.14)))
    }
}

/// Offsets pages by the live navigation direction: forward enters from the right, Back from the left.
struct IntroductionPageTransition: Transition {
    let navigator: IntroductionNavigator
    let entering: Bool

    func body(content: Content, phase: TransitionPhase) -> some View {
        let travel = entering ? IntroductionMotion.pageOffset : IntroductionMotion.pageOffset * 0.5
        let side: CGFloat = phase == .willAppear ? 1 : -1
        content
            .opacity(phase.isIdentity ? 1 : 0)
            .offset(x: phase.isIdentity ? 0 : side * travel * navigator.direction.sign)
            .blur(radius: phase.isIdentity ? 0 : 2)
    }
}

enum IntroductionEase {
    static func clamp(_ value: Double) -> Double { min(1, max(0, value)) }

    static func segment(_ progress: Double, _ start: Double, _ end: Double) -> Double {
        clamp((progress - start) / (end - start))
    }

    static func smooth(_ t: Double) -> Double { t * t * (3 - 2 * t) }
    static func easeOut(_ t: Double) -> Double { 1 - pow(1 - t, 3) }

    static func easeInOut(_ t: Double) -> Double {
        t < 0.5 ? 4 * t * t * t : 1 - pow(-2 * t + 2, 3) / 2
    }

    /// A damped settle that overshoots by under 3 percent.
    static func settle(_ t: Double) -> Double {
        t >= 1 ? 1 : 1 - exp(-7.5 * t) * cos(6.5 * t)
    }

    /// One soft bump from 0 to 1 and back. Never repeats.
    static func pulse(_ progress: Double, at start: Double, width: Double) -> Double {
        let t = segment(progress, start, start + width)
        return t <= 0 || t >= 1 ? 0 : sin(.pi * t)
    }

    static func mix(_ a: CGFloat, _ b: CGFloat, _ t: Double) -> CGFloat { a + (b - a) * CGFloat(t) }
}

/// Timings shared by illustrations and the text rows that play in sync with them.
enum IntroductionStory {
    static let shortcutPresses: [[Double]] = [[0.02], [0.43], [0.735, 0.835]]

    static func initialProgress(reduceMotion: Bool, presented: Bool, review: Double?) -> Double {
        if let review { return IntroductionEase.clamp(review) }
        return reduceMotion || !presented ? 1 : 0
    }

    /// The highlighted shortcut row: a continuous row position and its opacity.
    static func shortcutFocus(_ progress: Double) -> (position: Double, opacity: Double) {
        let toUseApps = IntroductionEase.easeInOut(IntroductionEase.segment(progress, 0.40, 0.45))
        let toShowHide = IntroductionEase.easeInOut(IntroductionEase.segment(progress, 0.70, 0.75))
        let opacity = IntroductionEase.smooth(IntroductionEase.segment(progress, 0, 0.05)) *
            (1 - IntroductionEase.smooth(IntroductionEase.segment(progress, 0.95, 1)))
        return (toUseApps + toShowHide, opacity)
    }

    static func shortcutPress(row: Int, progress: Double) -> Double {
        shortcutPresses[row].map { IntroductionEase.pulse(progress, at: $0, width: 0.05) }.max() ?? 0
    }

    /// The check for an allowed permission draws once its card has settled.
    static func checkDraw(index: Int, progress: Double) -> Double {
        let start = 0.45 + Double(index) * 0.2
        return IntroductionEase.easeOut(IntroductionEase.segment(progress, start, start + 0.3))
    }

    static func checkDelay(index: Int, timeScale: Double) -> Double {
        (IntroductionMotion.storyDelay + IntroductionPage.setup.storyDuration * (0.45 + Double(index) * 0.2)) * timeScale
    }

    /// Setup cards settle in one after another.
    static func cardArrival(index: Int, progress: Double) -> (opacity: Double, offset: CGFloat, scale: CGFloat) {
        let start = 0.05 + Double(index) * 0.2
        let t = IntroductionEase.segment(progress, start, start + 0.55)
        let settle = IntroductionEase.settle(t)
        return (0.35 + 0.65 * IntroductionEase.smooth(min(1, t * 1.6)), CGFloat(1 - settle) * 14,
                0.985 + 0.015 * CGFloat(min(1, settle)))
    }
}

/// A moving capsule over the step tracks. Its front edge leads and its back edge follows,
/// so it stretches toward the next step and then settles.
struct IntroductionStepIndicator: View {
    @ObservedObject var navigator: IntroductionNavigator
    let reduceMotion: Bool
    @State private var lower: Double
    @State private var upper: Double
    private let spacing: CGFloat = 8

    init(navigator: IntroductionNavigator, reduceMotion: Bool) {
        self.navigator = navigator
        self.reduceMotion = reduceMotion
        _lower = State(initialValue: Double(navigator.page.rawValue))
        _upper = State(initialValue: Double(navigator.page.rawValue))
    }

    var body: some View {
        HStack(spacing: spacing) {
            ForEach(IntroductionPage.allCases) { step in
                Button { navigator.select(step) } label: {
                    Capsule().fill(Color.secondary.opacity(0.2)).frame(height: 4)
                        .frame(maxWidth: .infinity).padding(.vertical, 6).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(step.title)
                .accessibilityLabel(step.title)
                .accessibilityValue("Step \(step.rawValue + 1) of \(IntroductionPage.allCases.count)")
                .accessibilityAddTraits(step == navigator.page ? .isSelected : [])
            }
        }
        .overlay {
            IntroductionStepCapsule(lower: lower, upper: upper, count: IntroductionPage.allCases.count, spacing: spacing)
                .fill(Color.accentColor)
                .frame(height: 4)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
        .onChange(of: navigator.page) { old, new in
            let target = Double(new.rawValue)
            if reduceMotion {
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) { lower = target; upper = target }
                return
            }
            let forward = new.rawValue > old.rawValue
            withAnimation(.spring(duration: 0.32, bounce: 0)) {
                if forward { upper = target } else { lower = target }
            }
            withAnimation(.spring(duration: 0.5, bounce: 0).delay(0.07)) {
                if forward { lower = target } else { upper = target }
            }
        }
    }
}

struct IntroductionStepCapsule: Shape {
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

/// Plays a page's story once when it appears. Re-created for each replay or presentation change,
/// so hiding the window always ends the animation instead of letting it run unseen.
struct IntroductionStoryPlayer<Content: View>: View {
    let page: IntroductionPage
    @ObservedObject var navigator: IntroductionNavigator
    let reduceMotion: Bool
    @ViewBuilder let content: (Double) -> Content

    var body: some View {
        IntroductionStoryRun(page: page, navigator: navigator, reduceMotion: reduceMotion, content: content)
            .id(navigator.playbackToken)
    }
}

private struct IntroductionStoryRun<Content: View>: View {
    let page: IntroductionPage
    let navigator: IntroductionNavigator
    let reduceMotion: Bool
    let content: (Double) -> Content
    @State private var progress: Double

    init(page: IntroductionPage, navigator: IntroductionNavigator, reduceMotion: Bool,
         content: @escaping (Double) -> Content) {
        self.page = page
        self.navigator = navigator
        self.reduceMotion = reduceMotion
        self.content = content
        _progress = State(initialValue: IntroductionStory.initialProgress(
            reduceMotion: reduceMotion, presented: navigator.isPresented, review: navigator.reviewProgress))
    }

    var body: some View {
        IntroductionProgressView(progress: progress, content: content)
            .onAppear(perform: play)
            .onChange(of: reduceMotion) { _, reduce in
                if reduce { navigator.replay() }
            }
    }

    private func play() {
        guard navigator.reviewProgress == nil, navigator.isPresented else { return }
        let animated = !reduceMotion && progress < 1
        navigator.notePlayback(IntroductionPlayback(page: page, animated: animated))
        guard animated else { return }
        let scale = max(0.01, navigator.storyTimeScale)
        let navigator = navigator, page = page
        withAnimation(.linear(duration: page.storyDuration * scale).delay(IntroductionMotion.storyDelay * scale)) {
            progress = 1
        } completion: {
            navigator.noteFinished(page)
        }
    }
}

private struct IntroductionProgressView<Content: View>: View, Animatable {
    var progress: Double
    let content: (Double) -> Content

    nonisolated var animatableData: Double {
        get { progress }
        set { progress = newValue }
    }

    var body: some View { content(progress) }
}
