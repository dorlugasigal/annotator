import AppKit
import Combine
import SwiftUI

/// Decides when What's changed follows an update: once for each newer build with notes bundled in the app. A first
/// install, an older or equal build and a build without notes (a development build) never show it.
@MainActor
struct WhatsChangedTracker {
    static let lastBuildKey = "whats-changed-last-build"
    /// Keys that only an earlier launch of Annotator writes. Finding one without `lastBuildKey` means this launch
    /// follows an update from a version before What's changed existed. Sparkle writes SUHasLaunchedBefore after its
    /// first start, so read these before Sparkle starts.
    static let earlierUseKeys = ["SUHasLaunchedBefore", "NSStatusItem Preferred Position AnnotatorMenuBar",
                                 TutorialPreferences.suppressedKey, SupportReminder.lastUseKey,
                                 "workspace-preferences-v1"]
    let defaults: UserDefaults

    /// The notes to show for `build`, or nil. A first install and a build without notes are recorded at once, so
    /// they never show later; a due build is recorded only once the window shows (`record`).
    func due(build: Int, notes: () throws -> ReleaseNotesCatalog.Entry) -> ReleaseNotesCatalog.Entry? {
        let saved = defaults.object(forKey: Self.lastBuildKey) as? Int
        if let saved, build <= saved { return nil }
        guard saved != nil || Self.earlierUseKeys.contains(where: { defaults.object(forKey: $0) != nil }) else {
            record(build)
            return nil
        }
        guard let entry = try? notes(), entry.build == build else {
            record(build)
            return nil
        }
        return entry
    }

    func record(_ build: Int) { defaults.set(build, forKey: Self.lastBuildKey) }
}

/// What's changed after an update: a small window, centered on screen, with the new version's notes and "Enjoying
/// Annotator?" with an optional Buy me a coffee button. It appears at most once per version, only when it covers nothing
/// the person is presenting, and after a quiet sign-in launch only once the person opens Annotator.
@MainActor
final class WhatsChangedController: NSObject, NSWindowDelegate {
    static let title = "What's changed"
    private let model: WorkspaceModel
    private let tracker: WhatsChangedTracker
    private var pending: ReleaseNotesCatalog.Entry?
    private var waitsForPerson = false
    private var observation: AnyCancellable?
    private(set) var window: NSPanel?

    init(model: WorkspaceModel) {
        self.model = model
        tracker = WhatsChangedTracker(defaults: model.preferenceDefaults)
        super.init()
    }

    var isPending: Bool { pending != nil }

    /// At launch, before Sparkle starts: finds out whether this launch follows an update.
    func prepare(build: Int?, notes: () throws -> ReleaseNotesCatalog.Entry = { try ReleaseNotesCatalog.load() }) {
        guard let build else { return }
        pending = tracker.due(build: build, notes: notes)
    }

    /// After the launch presentation: shows the window now when it may, otherwise as soon as it may.
    func start(isLoginItem: Bool) {
        guard pending != nil else { return }
        waitsForPerson = isLoginItem
        observation = model.objectWillChange.sink { [weak self] in
            Task { @MainActor [weak self] in self?.showIfReady() }
        }
        showIfReady()
    }

    /// The person opened Annotator after a quiet sign-in launch.
    func personOpenedAnnotator() {
        guard waitsForPerson else { return }
        waitsForPerson = false
        showIfReady()
    }

    func showIfReady() {
        guard let entry = pending, !waitsForPerson, window?.isVisible != true, model.canShowAnnouncement else { return }
        pending = nil
        observation = nil
        tracker.record(entry.build)
        show(entry)
    }

    func close() {
        window?.orderOut(nil)
        if model.whatsChangedVisible { model.whatsChangedVisible = false }
    }

    func windowWillClose(_ notification: Notification) { close() }

    private func show(_ entry: ReleaseNotesCatalog.Entry) {
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: WhatsChangedView.width, height: WhatsChangedView.height),
                            styleMask: [.titled, .closable, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = Self.title
        panel.delegate = self
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.contentView = NSHostingView(rootView: WhatsChangedView(entry: entry, model: model,
                                                                      close: { [weak self] in self?.close() }))
        panel.center()
        window = panel
        model.whatsChangedVisible = true
        panel.orderFrontRegardless()
    }
}

struct WhatsChangedView: View {
    static let width: CGFloat = 480
    static let height: CGFloat = 560
    let entry: ReleaseNotesCatalog.Entry
    @ObservedObject var model: WorkspaceModel
    let close: () -> Void
    /// The UX reference captures the settled window, without the entrance.
    var animatesEntrance = true
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var appeared = false
    @State private var limitsExpanded = false

    private var settled: Bool { appeared || !animatesEntrance }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 14) {
                        Image(nsImage: NSImage(named: "AppIcon") ?? NSApplication.shared.applicationIconImage)
                            .resizable().scaledToFit().frame(width: 56, height: 56)
                            .accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Annotator is updated").font(.title2.bold())
                            Text("You now have version \(entry.version).")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(.bottom, 6)
                    .offset(y: settled || reduceMotion ? 0 : 10)
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(ReleaseNotesCatalog.categories, id: \.self) { category in
                            if let changes = entry.changes[category] {
                                Text(category).font(.subheadline.bold())
                                ForEach(Array(changes.enumerated()), id: \.offset) { _, item in
                                    Text(item).fixedSize(horizontal: false, vertical: true)
                                }
                            }
                        }
                        if !entry.knownIssues.isEmpty {
                            DisclosureGroup("Known limits", isExpanded: $limitsExpanded) {
                                VStack(alignment: .leading, spacing: 8) {
                                    ForEach(Array(entry.knownIssues.enumerated()), id: \.offset) { _, item in
                                        Text(item).fixedSize(horizontal: false, vertical: true)
                                    }
                                }
                                .padding(.top, 6)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .accessibilityIdentifier("whats-changed-known-limits")
                        }
                    }
                    .font(.callout)
                    .textSelection(.enabled)
                    .offset(y: settled || reduceMotion ? 0 : 18)
                }
                .padding(24)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Divider()
            CoffeeSupportView(model: model, showsDetail: false)
                .padding(.horizontal, 16).padding(.top, 14)
                .scaleEffect(settled || reduceMotion ? 1 : 0.96)
            HStack {
                Spacer()
                Button("Close", action: close)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("whats-changed-close")
            }
            .padding(16)
        }
        .frame(width: Self.width, height: Self.height)
        .background(Color(nsColor: .windowBackgroundColor))
        .opacity(settled ? 1 : 0)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("whats-changed")
        .onAppear {
            guard animatesEntrance else { return }
            withAnimation(reduceMotion ? .easeOut(duration: 0.15) : .spring(response: 0.5, dampingFraction: 0.82)) {
                appeared = true
            }
        }
    }
}
