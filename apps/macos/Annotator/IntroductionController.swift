import AppKit
import Combine
import SwiftUI
import AnnotationCore
import PresentationRendering
import NativePlatform

@MainActor
final class IntroductionPreferences: ObservableObject {
    static let suppressedKey = "introduction-suppressed"
    private let defaults: UserDefaults

    init(defaults: UserDefaults) { self.defaults = defaults }

    var suppressed: Bool { defaults.bool(forKey: Self.suppressedKey) }

    func setSuppressed(_ value: Bool) {
        objectWillChange.send()
        defaults.set(value, forKey: Self.suppressedKey)
    }
}

/// Moves between tour pages with the Left and Right arrow keys while the panel is key.
final class IntroductionPanel: NSPanel {
    var onNavigate: ((IntroductionDirection) -> Void)?

    override func sendEvent(_ event: NSEvent) {
        if event.type == .keyDown, let direction = Self.navigationDirection(for: event),
           !(firstResponder is NSText), let onNavigate {
            onNavigate(direction)
            return
        }
        super.sendEvent(event)
    }

    static func navigationDirection(for event: NSEvent) -> IntroductionDirection? {
        guard event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty else { return nil }
        switch event.keyCode {
        case 123: return .backward
        case 124: return .forward
        default: return nil
        }
    }
}

@MainActor
final class IntroductionController: NSObject, NSWindowDelegate {
    let preferences: IntroductionPreferences
    let navigator = IntroductionNavigator()
    private let model: WorkspaceModel
    private var observation: AnyCancellable?
    private var attemptedAutomaticPresentation = false
    private(set) var window: NSPanel?

    init(model: WorkspaceModel) {
        self.model = model
        preferences = IntroductionPreferences(defaults: model.preferenceDefaults)
        super.init()
        observation = model.objectWillChange.sink { [weak self] in
            guard self?.window?.isVisible == true else { return }
            Task { @MainActor [weak self] in
                guard let self, !self.isEligible else { return }
                self.close()
            }
        }
    }

    var isEligible: Bool {
        model.mode != .draw && !model.screenZoomEnabled && !model.magnifierEnabled &&
        model.zoomStartState == .idle && !model.visibilityAnimating &&
        model.errorMessage == nil && model.captureFailureMessage == nil &&
        model.desktopSessions.allSatisfy { !$0.canvas.hasActiveEdit }
    }

    func showAutomatically(isLoginItem: Bool) {
        guard !attemptedAutomaticPresentation else { return }
        attemptedAutomaticPresentation = true
        guard !isLoginItem, !preferences.suppressed, isEligible else { return }
        show(takeFocus: false)
    }

    func showManually() {
        guard isEligible else {
            model.status = "Finish drawing and stop zoom, then open the introduction from Settings > Help."
            return
        }
        show(takeFocus: true)
    }

    func close() {
        window?.orderOut(nil)
        navigator.setPresented(false)
        if model.introductionVisible { model.introductionVisible = false }
    }

    func windowWillClose(_ notification: Notification) { close() }

    private func show(takeFocus: Bool) {
        navigator.setPresented(true)
        if window == nil {
            let panel = IntroductionPanel(contentRect: NSRect(x: 0, y: 0, width: 520, height: 530),
                                          styleMask: [.titled, .closable, .nonactivatingPanel],
                                          backing: .buffered, defer: false)
            panel.title = "Welcome to Annotator"
            panel.delegate = self
            panel.isReleasedWhenClosed = false
            panel.hidesOnDeactivate = false
            panel.becomesKeyOnlyIfNeeded = true
            panel.onNavigate = { [weak self] direction in self?.navigator.move(direction) }
            let host = NSHostingView(rootView: IntroductionView(
                model: model, preferences: preferences, metrics: model.usageMetrics, navigator: navigator,
                close: { [weak self] in self?.close() }))
            panel.contentView = host
            panel.setContentSize(host.fittingSize)
            panel.center()
            window = panel
        }
        if takeFocus { window?.makeKeyAndOrderFront(nil) }
        else { window?.orderFrontRegardless() }
        model.introductionVisible = true
    }
}

struct IntroductionView: View {
    @ObservedObject var model: WorkspaceModel
    @ObservedObject var preferences: IntroductionPreferences
    @ObservedObject var metrics: UsageMetrics
    @ObservedObject var navigator: IntroductionNavigator
    let close: () -> Void
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @State private var screenAccess = false
    @State private var gestureAccess = false
    @State private var sharingExpanded = false
    @State private var illustrationHovered = false
    private let rowHeight: CGFloat = 40
    private let rowSpacing: CGFloat = 2

    private var reduceMotion: Bool { systemReduceMotion || navigator.reducesMotion() }

    var body: some View {
        VStack(spacing: 0) {
            IntroductionStepIndicator(navigator: navigator, reduceMotion: reduceMotion)
                .padding(.horizontal, 24).padding(.top, 10)
            ZStack(alignment: .top) {
                ScrollView {
                    page(navigator.page)
                        .padding(.horizontal, 24).padding(.top, 12).padding(.bottom, 18)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .id(navigator.page)
                .transition(IntroductionMotion.pageTransition(navigator: navigator, reduceMotion: reduceMotion))
            }
            .frame(maxHeight: .infinity)
            .clipped()
            Divider()
            footer
        }
        .frame(width: 520, height: min(530, max(340, (NSScreen.main?.visibleFrame.height ?? 780) - 90)))
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear { refreshPermissions() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshPermissions()
        }
    }

    private func page(_ page: IntroductionPage) -> some View {
        IntroductionStoryPlayer(page: page, navigator: navigator, reduceMotion: reduceMotion) { progress in
            switch page {
            case .draw: introduction(progress)
            case .color: colorIntroduction(progress)
            case .focus: focusIntroduction(progress)
            case .setup: permissionIntroduction(progress)
            }
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Toggle("Share usage counts", isOn: Binding(
                    get: { metrics.enabled }, set: { metrics.setEnabled($0) }))
                    .help("Optional daily counts: version, macOS, chip and feature categories. No drawings, screenshots, text or file names. Change this in General.")
                Text("Optional. Never your drawings or text.").font(.caption).foregroundStyle(.secondary)
            }
            HStack(spacing: 8) {
                Toggle("Show on launch", isOn: Binding(
                    get: { !preferences.suppressed }, set: { preferences.setSuppressed(!$0) }))
                Spacer()
                if navigator.canGoBack {
                    Button("Back") { navigator.move(.backward) }.transition(.opacity)
                }
                if navigator.canGoForward {
                    Button("Next") { navigator.move(.forward) }.transition(.opacity)
                }
                Button("Got it", action: close).buttonStyle(.borderedProminent)
            }
            Text("Find this again in Settings > Help.").font(.caption).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 20).padding(.vertical, 14)
    }

    private func illustration(_ page: IntroductionPage, progress: Double) -> some View {
        IntroductionIllustration(page: page, progress: progress)
            .frame(height: 120)
            .overlay(alignment: .bottomTrailing) {
                if !reduceMotion && progress >= 1 {
                    Image(systemName: "arrow.counterclockwise")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .frame(width: 22, height: 22)
                        .background(.regularMaterial, in: Circle())
                        .padding(8)
                        .opacity(illustrationHovered ? 1 : 0)
                        .accessibilityHidden(true)
                }
            }
            .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .onHover { inside in withAnimation(.easeOut(duration: 0.15)) { illustrationHovered = inside } }
            .onTapGesture { if !reduceMotion { navigator.replay() } }
            .help(reduceMotion ? "" : "Click to play the animation again.")
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(page.illustrationDescription ?? page.title)
            .accessibilityAddTraits(.isImage)
            .accessibilityIdentifier("introduction-illustration")
            .accessibilityActions {
                if !reduceMotion { Button("Replay animation") { navigator.replay() } }
            }
    }

    private func introduction(_ progress: Double) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            illustration(.draw, progress: progress)
            Text("Let's make a point.").font(.title.bold()).padding(.top, 14)
            Text("Pick a tool and draw over your screen. Try these three shortcuts.")
                .foregroundStyle(.secondary).padding(.top, 4)
            shortcutRows(progress).padding(.top, 8)
            Text("Esc finishes text or cancels a drawing first. Press again to use apps. It also exits zoom.")
                .font(.caption).foregroundStyle(.secondary).padding(.top, 6)
            DisclosureGroup("Before you share", isExpanded: $sharingExpanded) {
                Text("App or window sharing may leave out covers. Ask someone to check. Hiding, quitting or a crash removes them. Blur can leave details readable.\n\nDrawing needs no permissions. Zoom, effects and region sharing ask when needed.")
                    .font(.callout).foregroundStyle(.secondary).padding(.top, 8)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .accessibilityIdentifier("introduction-sharing")
            .padding(.top, 8)
        }
    }

    private func shortcutRows(_ progress: Double) -> some View {
        let focus = IntroductionStory.shortcutFocus(progress)
        return VStack(spacing: rowSpacing) {
            shortcutRow(0, "Draw", detail: "Start or resume drawing.", symbol: "pencil.tip", progress: progress) {
                ShortcutKeycaps(shortcut: model.preferences.resumeShortcut ?? .resumeDrawing)
            }
            shortcutRow(1, "Use apps", detail: "Your drawings stay visible.", symbol: "cursorarrow", progress: progress) {
                Keycap(text: "esc").accessibilityLabel("Escape")
            }
            shortcutRow(2, "Show / hide", detail: "Covers hide too.", symbol: "eye", progress: progress) {
                ShortcutKeycaps(shortcut: model.preferences.activationShortcut)
            }
        }
        .background(alignment: .top) {
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(Color.accentColor.opacity(0.1))
                .frame(height: rowHeight)
                .offset(y: CGFloat(focus.position) * (rowHeight + rowSpacing))
                .opacity(focus.opacity)
                .accessibilityHidden(true)
        }
        .padding(.horizontal, -10)
    }

    private func shortcutRow<Keys: View>(_ row: Int, _ title: String, detail: String, symbol: String,
                                         progress: Double, @ViewBuilder keys: () -> Keys) -> some View {
        let press = IntroductionStory.shortcutPress(row: row, progress: progress)
        return HStack(spacing: 12) {
            Image(systemName: symbol).font(.title3).foregroundStyle(.tint).frame(width: 28)
                .scaleEffect(1 + 0.12 * press)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            keys()
                .scaleEffect(1 - 0.06 * press)
                .shadow(color: Color.accentColor.opacity(0.5 * press), radius: 6)
        }
        .padding(.horizontal, 10)
        .frame(height: rowHeight)
    }

    private func colorIntroduction(_ progress: Double) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            illustration(.color, progress: progress)
            Text("A little color goes a long way.").font(.title.bold()).padding(.top, 14)
            Text("Keep the outline bold and the fill light. Their opacity controls are separate.")
                .padding(.top, 6)
            Text("Pick a swatch, try a gradient, or open the color wheel. New shape labels follow the outline's color.")
                .foregroundStyle(.secondary).padding(.top, 8)
            Label("Select a mark to change it. Undo is always close by.", systemImage: "arrow.uturn.backward")
                .font(.callout).padding(.top, 12)
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func focusIntroduction(_ progress: Double) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            illustration(.focus, progress: progress)
            Text("Right here. See it?").font(.title.bold()).padding(.top, 14)
            Text("Give your pointer a ring. Light up a detail with Spotlight. Zoom in when the small print matters.")
                .padding(.top, 6)
            Text("Find these in Tools > Focus. In Settings > Pointer, choose the highlight's shape, color and size—or show it only when you move or click.")
                .foregroundStyle(.secondary).padding(.top, 8)
            Text("Your drawings stay editable. Save a board to return to it, or keep a useful diagram in My shapes.")
                .font(.callout).padding(.top, 12)
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func permissionIntroduction(_ progress: Double) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("You're ready to draw.").font(.title.bold())
            Text("No permissions needed for that. These two are optional; you can set them up later.")
                .foregroundStyle(.secondary).padding(.top, 4)
            permissionCard(index: 0, progress: progress, title: "Screen Recording", symbol: "display",
                           detail: "For zoom, live effects and region sharing. We don't record video.",
                           allowed: screenAccess, allowedText: "Screen access allowed",
                           allowedNote: "Allowed. Try zoom to check that it works.",
                           actionTitle: "Allow screen access") {
                model.requestCapturePermission()
                refreshPermissions()
            }
            .padding(.top, 14)
            permissionCard(index: 1, progress: progress, title: "Accessibility", symbol: "hand.point.up.left",
                           detail: "For Option-scroll over other apps. The zoom buttons work without it.",
                           allowed: gestureAccess, allowedText: "Scroll gestures allowed", allowedNote: nil,
                           actionTitle: "Allow scroll gestures") {
                model.requestZoomGesturePermission()
                refreshPermissions()
            }
            .padding(.top, 8)
            Text("Sharing your screen? Check the other person's view. Window sharing can miss overlays, and blur isn't secure redaction. Stop sharing before quitting or restarting: covers disappear too.")
                .font(.caption).foregroundStyle(.secondary).padding(.top, 14)
                .opacity(0.35 + 0.65 * IntroductionEase.smooth(IntroductionEase.segment(progress, 0.45, 0.9)))
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func permissionCard(index: Int, progress: Double, title: String, symbol: String, detail: String,
                                allowed: Bool, allowedText: String, allowedNote: String?, actionTitle: String,
                                action: @escaping () -> Void) -> some View {
        let arrival = IntroductionStory.cardArrival(index: index, progress: progress)
        let tint: Color = allowed ? .green : .accentColor
        return HStack(alignment: .center, spacing: 12) {
            Image(systemName: symbol).font(.system(size: 15, weight: .medium)).foregroundStyle(tint)
                .frame(width: 32, height: 32)
                .background(tint.opacity(0.14), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if allowed, let allowedNote {
                    Text(allowedNote).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            if allowed {
                IntroductionAllowedBadge(
                    accessibilityText: allowedText,
                    delay: progress < 1 ? IntroductionStory.checkDelay(index: index, timeScale: navigator.storyTimeScale) : 0.1,
                    reduceMotion: reduceMotion,
                    fixedDraw: navigator.reviewProgress.map { _ in IntroductionStory.checkDraw(index: index, progress: progress) })
                    .transition(.opacity.combined(with: .scale(scale: 0.9)))
            } else {
                Button(actionTitle, action: action)
            }
        }
        .padding(12)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.primary.opacity(0.08)))
        .opacity(arrival.opacity)
        .scaleEffect(arrival.scale)
        .offset(y: arrival.offset)
    }

    private func refreshPermissions() {
        let screen = navigator.reviewPermissions?.screen ?? DisplayCapture.hasPermission
        let gestures = navigator.reviewPermissions?.gestures ?? ZoomGestureMonitor.hasPermission
        guard screen != screenAccess || gestures != gestureAccess else { return }
        withAnimation(reduceMotion ? nil : Motion.smooth) {
            screenAccess = screen
            gestureAccess = gestures
        }
    }
}
