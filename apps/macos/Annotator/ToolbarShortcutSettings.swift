import AppKit
import SwiftUI
import AnnotationCore

/// Keys that work in any app. They need Control, Option or Command.
enum GlobalShortcutKind: String, CaseIterable, Identifiable {
    case draw, showHide, hideOnly, liveZoom, breakTimer

    var id: String { rawValue }
    var title: String {
        switch self {
        case .draw: "Draw"
        case .showHide: "Show / hide"
        case .hideOnly: "Hide only"
        case .liveZoom: "Live zoom"
        case .breakTimer: "Break timer"
        }
    }
    var detail: String {
        switch self {
        case .draw: "Start drawing, or go back to drawing from your apps."
        case .showHide: "Show or hide your drawings and the toolbar."
        case .hideOnly: "Hide drawings and the toolbar. Covers hide too."
        case .liveZoom: "Start or stop full-screen zoom."
        case .breakTimer: "Start or end a break countdown."
        }
    }
    var symbol: String {
        switch self {
        case .draw: "pencil.tip"
        case .showHide: "pencil.tip.crop.circle"
        case .hideOnly: "eye.slash"
        case .liveZoom: "plus.magnifyingglass"
        case .breakTimer: "timer"
        }
    }

    @MainActor func value(in model: WorkspaceModel) -> ActivationShortcut {
        switch self {
        case .draw: model.preferences.resumeShortcut ?? .resumeDrawing
        case .showHide: model.preferences.activationShortcut
        case .hideOnly: model.preferences.hideShortcut ?? .hideTools
        case .liveZoom: model.preferences.zoomShortcut ?? .liveZoom
        case .breakTimer: model.preferences.breakShortcut ?? .breakTimer
        }
    }

    @MainActor func set(_ value: ActivationShortcut, in model: WorkspaceModel) {
        switch self {
        case .draw: model.setResumeShortcut(value)
        case .showHide: model.setActivationShortcut(value)
        case .hideOnly: model.setHideShortcut(value)
        case .liveZoom: model.setZoomShortcut(value)
        case .breakTimer: model.setBreakShortcut(value)
        }
    }
}

enum ShortcutRecordingTarget: Equatable {
    case local(ShortcutCommand)
    case global(GlobalShortcutKind)

    var title: String {
        switch self {
        case .local(let command): command.label
        case .global(let kind): kind.title
        }
    }
}

/// Shared recording state handed to every key chip on the tab.
struct ShortcutEditorContext {
    var recording: ShortcutRecordingTarget?
    var liveModifiers = ""
    var shakeTarget: ShortcutRecordingTarget?
    var shakes = 0
    var toggle: (ShortcutRecordingTarget) -> Void
    var say: (String) -> Void
}

struct SettingsToast: Equatable, Identifiable {
    let id = UUID()
    var message: String
    var symbol = "checkmark.circle.fill"
    var tint = Color.green
    var undo: WorkspacePreferences?
    /// Says exactly what Undo puts back, such as "Give 2 back to Rectangle".
    var undoTitle = "Undo"
    var persistent = false
}

struct ToolbarShortcutSettings: View {
    @ObservedObject var model: WorkspaceModel
    @State private var recording: ShortcutRecordingTarget?
    @State private var liveModifiers = ""
    @State private var shakeTarget: ShortcutRecordingTarget?
    @State private var shakes = 0
    @State private var toast: SettingsToast?
    @State private var toastTask: Task<Void, Never>?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var editor: ShortcutEditorContext {
        ShortcutEditorContext(recording: recording, liveModifiers: liveModifiers,
                              shakeTarget: shakeTarget, shakes: shakes,
                              toggle: { target in toggleRecording(target) },
                              say: { message in show(SettingsToast(message: message, symbol: "info.circle.fill", tint: .accentColor)) })
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                ToolbarArranger(model: model, editor: editor)
                PropertyChoicesEditor(model: model)
                layoutSection
                buttonKeysSection
                globalKeysSection
                fixedKeysSection
            }
            .padding(.horizontal, 22).padding(.top, 18).padding(.bottom, 64)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
            .onTapGesture { if recording != nil { stopRecording() } }
        }
        .background(KeyCapture(active: recording != nil, capturesCommandKeys: isRecordingGlobal,
                               onKey: handleKey, onFlags: { liveModifiers = $0 }, onResign: {
            if recording != nil { stopRecording() }
        }))
        .overlay(alignment: .bottom) {
            if let toast {
                toastView(toast)
                    .transition(reduceMotion ? .opacity : .move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(Motion.resolve(Motion.snappy, reduceMotion: reduceMotion), value: toast)
        .onDisappear {
            recording = nil
            toastTask?.cancel()
            toast = nil
        }
    }

    // MARK: Sections

    private var layoutSection: some View {
        VStack(alignment: .leading, spacing: 14) {
            sectionTitle("Size and position", detail: "Choose how big the toolbar is and where it starts.")
            ToolbarSizePicker(selection: model.toolbarSize) { size in
                withAnimation(Motion.resolve(Motion.smooth, reduceMotion: reduceMotion)) { model.setToolbarSize(size) }
            }
            Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 16, verticalSpacing: 12) {
                GridRow {
                    Text("Direction").gridColumnAlignment(.trailing)
                    Picker("Direction", selection: Binding(
                        get: { model.preferences.toolbarOrientation ?? .horizontal },
                        set: { model.setToolbarOrientation($0) })) {
                        Label("Horizontal", systemImage: "rectangle.split.3x1").tag(ToolbarOrientation.horizontal)
                        Label("Vertical", systemImage: "rectangle.split.1x3").tag(ToolbarOrientation.vertical)
                    }.pickerStyle(.segmented).labelsHidden().frame(width: 260)
                }
                GridRow {
                    Text("Start at")
                    Picker("Start at", selection: Binding(
                        get: { model.preferences.toolbarDockEdge ?? .automatic }, set: { model.setToolbarDockEdge($0) })) {
                        Text("Automatic").tag(ToolbarDockEdge.automatic)
                        if model.preferences.toolbarOrientation == .vertical {
                            Text("Left edge").tag(ToolbarDockEdge.left)
                            Text("Right edge").tag(ToolbarDockEdge.right)
                        } else {
                            Text("Top edge").tag(ToolbarDockEdge.top)
                            Text("Bottom edge").tag(ToolbarDockEdge.bottom)
                        }
                    }.labelsHidden().fixedSize()
                }
                GridRow {
                    Text("Properties")
                    Picker("Properties", selection: Binding(
                        get: { ToolbarPropertiesMode(model: model) }, set: { $0.apply(to: model) })) {
                        ForEach(ToolbarPropertiesMode.allCases, id: \.self) { mode in Text(mode.rawValue).tag(mode) }
                    }.labelsHidden().fixedSize()
                }
                GridRow {
                    Text("Shrink")
                    Picker("Shrink", selection: Binding(
                        get: { model.toolbarCollapseDelay ?? 0 }, set: { model.setToolbarCollapseDelay($0 > 0 ? $0 : nil) })) {
                        Text("Never").tag(0.0)
                        ForEach(collapseDelays, id: \.self) { delay in
                            Text("After \(delay.formatted()) seconds").tag(delay)
                        }
                    }
                    .labelsHidden().fixedSize()
                    .accessibilityIdentifier("toolbar-collapse-delay")
                }
            }
            Label("Press and hold anywhere on the toolbar, then drag it where you like. At a screen edge it docks: top and bottom make it horizontal, left and right make it vertical.",
                  systemImage: "hand.draw")
                .font(.caption).foregroundStyle(.secondary)
            HStack(alignment: .center, spacing: 16) {
                ToolbarShrinkDemo()
                VStack(alignment: .leading, spacing: 4) {
                    Text("Shrink").font(.headline)
                    Text(shrinkCaption).font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: 280, alignment: .leading)
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.03)))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.07)))
        }
    }

    private var shrinkCaption: String {
        guard let delay = model.toolbarCollapseDelay else {
            return "Pick a time in Shrink and the toolbar tucks into one button when you leave it. Point at the button and your tools are back."
        }
        return "Leave the toolbar alone for \(delay.formatted()) seconds and it shrinks to one button that shows Draw or Apps. Point at it and your tools are back."
    }

    /// The delay choices, plus a delay from an imported profile that isn't one of them.
    private var collapseDelays: [Double] {
        let choices = WorkspacePreferences.toolbarCollapseDelays
        guard let current = model.toolbarCollapseDelay, !choices.contains(current) else { return choices }
        return (choices + [current]).sorted()
    }

    private var buttonKeysSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            sectionTitle("Keys for toolbar buttons",
                         detail: "These keys work while you draw, even when you take the button off your toolbar.")
            let commands = ShortcutAction.allCases.filter { !$0.canBeInToolbar }.map(ShortcutCommand.action)
            Grid(horizontalSpacing: 10, verticalSpacing: 10) {
                ForEach(Array(stride(from: 0, to: commands.count, by: 2)), id: \.self) { index in
                    GridRow {
                        commandRow(commands[index])
                        if index + 1 < commands.count { commandRow(commands[index + 1]) } else { Color.clear }
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("toolbar-button-keys")
        }
    }

    private var globalKeysSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            sectionTitle("Keys that work in any app",
                         detail: "Hold Control, Option or Command and press a letter or number.")
            VStack(spacing: 8) {
                ForEach(GlobalShortcutKind.allCases) { kind in
                    HStack(spacing: 12) {
                        Image(systemName: kind.symbol).font(.system(size: 15)).foregroundStyle(.tint)
                            .frame(width: 24).accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(kind.title).font(.body.weight(.medium))
                            Text(kind.detail).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 12)
                        GlobalKeyButton(kind: kind, shortcut: kind.value(in: model), editor: editor)
                    }
                    .padding(.horizontal, 12).padding(.vertical, 9)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.035)))
                }
            }
        }
    }

    private var fixedKeysSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Keys that stay the same").font(.headline)
            FlowLayout(spacing: 8, lineSpacing: 8) {
                fixedKey("R G B Y O P C W K", "ink colors")
                fixedKey("⌃W", "whiteboard")
                fixedKey("⌃B", "blackboard")
                fixedKey("⌘Z", "undo")
                fixedKey("⇧⌘Z", "redo")
                if let library = model.toolLibraryShortcut { fixedKey(library.label, "more tools") }
                fixedKey("⌥ + scroll", "zoom")
                fixedKey("Esc", "use apps")
                fixedKey("⌫", "delete selection")
                fixedKey("← ↑ → ↓", "nudge; add ⇧ for 10 px")
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Keys that stay the same")
        }
    }

    // MARK: Pieces

    private func sectionTitle(_ title: String, detail: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.title3.weight(.semibold))
            Text(detail).font(.callout).foregroundStyle(.secondary)
        }
    }

    private func commandRow(_ command: ShortcutCommand) -> some View {
        let title: String, detail: String
        switch command {
        case .action(let action): title = action.label; detail = action.detail
        case .tool(let tool): title = tool.label; detail = tool.label
        }
        return HStack(alignment: .center, spacing: 10) {
            Group {
                switch command {
                case .action(let action): Image(systemName: action.symbol).font(.system(size: 15))
                case .tool(let tool): ToolGlyph(tool: tool, size: 16)
                }
            }
            .frame(width: 24).foregroundStyle(.secondary).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.medium)).lineLimit(1)
                Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 6)
            KeyChip(target: .local(command), shortcut: model.shortcut(for: command), name: title, editor: editor)
        }
        .padding(.horizontal, 12).padding(.vertical, 9)
        .frame(maxWidth: .infinity, minHeight: 58, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.035)))
    }

    private func fixedKey(_ keys: String, _ meaning: String) -> some View {
        HStack(spacing: 6) {
            Text(keys).font(.system(size: 11, weight: .semibold, design: .rounded))
                .padding(.horizontal, 6).padding(.vertical, 3)
                .background(RoundedRectangle(cornerRadius: 5).fill(Color(nsColor: .controlBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(Color.primary.opacity(0.14)))
            Text(meaning).font(.caption).foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }

    private func toastView(_ toast: SettingsToast) -> some View {
        HStack(spacing: 10) {
            Image(systemName: toast.symbol).foregroundStyle(toast.tint).font(.system(size: 14, weight: .semibold))
            Text(toast.message).font(.callout).fixedSize(horizontal: false, vertical: true)
                .contentTransition(.opacity)
            if let snapshot = toast.undo {
                Button(toast.undoTitle) {
                    model.restoreLocalShortcuts(from: snapshot)
                    show(SettingsToast(message: "Done. The keys are back as they were."))
                }
                .buttonStyle(.link).accessibilityIdentifier("undo-key-change")
            }
            if toast.persistent {
                Button("Cancel") { stopRecording() }.buttonStyle(.link)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 10)
        .frame(maxWidth: 600)
        .background(.regularMaterial, in: Capsule())
        .overlay(Capsule().strokeBorder(Color.primary.opacity(0.08)))
        .shadow(color: .black.opacity(0.14), radius: 12, y: 4)
        .padding(.bottom, 14).padding(.horizontal, 20)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("settings-toast")
    }

    // MARK: Recording

    private var isRecordingGlobal: Bool {
        if case .global = recording { return true }
        return false
    }

    private func toggleRecording(_ target: ShortcutRecordingTarget) {
        if recording == target { stopRecording(); return }
        liveModifiers = ""
        recording = target
        switch target {
        case .local(let command):
            show(SettingsToast(message: "Press a key for \(command.label). Shift with a letter works too. Delete removes the key.",
                               symbol: "keyboard", tint: .accentColor, persistent: true))
        case .global(let kind):
            show(SettingsToast(message: "Press Control, Option or Command with a letter or number for \(kind.title).",
                               symbol: "keyboard", tint: .accentColor, persistent: true))
        }
    }

    private func stopRecording() {
        recording = nil
        liveModifiers = ""
        if toast?.persistent == true { toast = nil }
    }

    private func handleKey(_ event: NSEvent) {
        guard let target = recording else { return }
        if event.keyCode == 53 { stopRecording(); return }
        switch target {
        case .local(let command): recordLocal(event, for: command, target: target)
        case .global(let kind): recordGlobal(event, for: kind, target: target)
        }
    }

    private func recordLocal(_ event: NSEvent, for command: ShortcutCommand, target: ShortcutRecordingTarget) {
        let snapshot = model.preferences
        if event.keyCode == 51 || event.keyCode == 117 {
            stopRecording()
            if case .failed(let message) = model.bindShortcut(nil, to: command) { fail(message, target: target); return }
            let previous = snapshot.shortcut(for: command)
            show(SettingsToast(message: "\(command.label) has no key now.", symbol: "minus.circle.fill",
                               tint: .secondary, undo: previous == nil ? nil : snapshot,
                               undoTitle: previous.map { "Give \($0.label) back to \(command.label)" } ?? "Undo"))
            return
        }
        let flags = event.modifierFlags.intersection([.command, .control, .option, .shift])
        guard flags.isDisjoint(with: [.command, .control, .option]) else {
            fail("Keys for tools and buttons work without Command, Control or Option. Press a letter or number.", target: target)
            return
        }
        let character = event.charactersIgnoringModifiers?.lowercased() ?? ""
        // Some layouts (AZERTY, Czech) need Shift to type a digit; the layout decides, like canvas routing does.
        let shift = flags.contains(.shift)
        guard let shortcut = LocalShortcut(key: character, shift: shift) ?? (shift ? LocalShortcut(key: character) : nil) else {
            fail("Press a letter or a number. Shift works with letters only.", target: target)
            return
        }
        guard !shortcut.isInkColor else {
            fail("\(shortcut.key.uppercased()) changes the ink color. Choose another key.", target: target)
            return
        }
        stopRecording()
        switch model.bindShortcut(shortcut, to: command) {
        case .assigned(let displaced):
            let message = displaced.map { "\(shortcut.label) is now the key for \(command.label). \($0.label) has no key now." }
                ?? "\(shortcut.label) is now the key for \(command.label)."
            show(SettingsToast(message: message, undo: displaced == nil ? nil : snapshot,
                               undoTitle: displaced.map { "Give \(shortcut.label) back to \($0.label)" } ?? "Undo"))
        case .cleared:
            break
        case .failed(let message):
            fail(message, target: target)
        }
    }

    private func recordGlobal(_ event: NSEvent, for kind: GlobalShortcutKind, target: ShortcutRecordingTarget) {
        let flags = event.modifierFlags
        let character = Self.digits[event.keyCode] ?? event.charactersIgnoringModifiers?.lowercased() ?? ""
        let shortcut = ActivationShortcut(keyCode: UInt32(event.keyCode), character: character,
                                          control: flags.contains(.control), option: flags.contains(.option),
                                          command: flags.contains(.command), shift: flags.contains(.shift))
        do { try shortcut.validate() } catch {
            fail("Hold Control, Option or Command and press a letter or number.", target: target)
            return
        }
        if let clash = GlobalShortcutKind.allCases.first(where: { $0 != kind && $0.value(in: model).usesSameKeys(as: shortcut) }) {
            fail("\(shortcut.label) already does \(clash.title). Choose another combination.", target: target)
            return
        }
        let before = kind.value(in: model)
        stopRecording()
        kind.set(shortcut, in: model)
        if kind.value(in: model).usesSameKeys(as: shortcut) {
            show(SettingsToast(message: "\(shortcut.label) now does \(kind.title) in any app."))
        } else {
            fail("\(shortcut.label) didn't work. Another app may use it. \(kind.title) still uses \(before.label).", target: target)
        }
    }

    private static let digits: [UInt16: String] = [
        29: "0", 18: "1", 19: "2", 20: "3", 21: "4", 23: "5", 22: "6", 26: "7", 28: "8", 25: "9"
    ]

    private func fail(_ message: String, target: ShortcutRecordingTarget) {
        shakeTarget = target
        NSHapticFeedbackManager.defaultPerformer.perform(.generic, performanceTime: .now)
        withAnimation(reduceMotion ? nil : .linear(duration: 0.42)) { shakes += 1 }
        show(SettingsToast(message: message, symbol: "exclamationmark.triangle.fill", tint: .orange,
                           persistent: recording != nil))
    }

    private func show(_ next: SettingsToast) {
        toastTask?.cancel()
        toast = next
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested,
                             userInfo: [.announcement: next.message, .priority: NSAccessibilityPriorityLevel.high.rawValue])
        guard !next.persistent else { return }
        let id = next.id
        toastTask = Task { @MainActor in
            try? await Task.sleep(for: .seconds(next.undo == nil ? 3.2 : 6))
            guard !Task.isCancelled, toast?.id == id else { return }
            toast = nil
        }
    }
}

// MARK: - Key chips

/// A clickable keycap. Click it, then press a key.
struct KeyChip: View {
    let target: ShortcutRecordingTarget
    let shortcut: LocalShortcut?
    let name: String
    let editor: ShortcutEditorContext
    var compact = false
    @State private var bounce = false
    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var recording: Bool { editor.recording == target }

    var body: some View {
        Button { editor.toggle(target) } label: {
            Text(recording ? (compact ? "Press" : "Press a key") : shortcut?.label ?? "Set")
                .font(.system(size: compact ? 10 : 11.5, weight: .semibold, design: .rounded))
                .foregroundStyle(recording ? Color.accentColor : shortcut == nil ? Color.secondary : Color.primary)
                .padding(.horizontal, compact ? 4 : 6)
                .frame(minWidth: compact ? 22 : 28, minHeight: compact ? 17 : 22)
                .background {
                    RoundedRectangle(cornerRadius: 5)
                        .fill(recording ? Color.accentColor.opacity(0.14) :
                              shortcut == nil ? Color.primary.opacity(hovering ? 0.06 : 0) :
                              Color(nsColor: .controlBackgroundColor))
                }
                .overlay {
                    RoundedRectangle(cornerRadius: 5)
                        .strokeBorder(recording ? Color.accentColor : Color.primary.opacity(shortcut == nil ? 0.25 : 0.16),
                                      style: StrokeStyle(lineWidth: recording ? 1.5 : 1,
                                                         dash: shortcut == nil && !recording ? [3, 2] : []))
                }
                .shadow(color: .black.opacity(shortcut == nil || recording ? 0 : 0.1), radius: 0, y: 1)
                .overlay { if recording && !reduceMotion { PulseRing(cornerRadius: 5) } }
                .scaleEffect(bounce ? 1.22 : hovering && !recording ? 1.06 : 1)
                .modifier(ShakeEffect(travel: editor.shakeTarget == target ? 5 : 0, animatableData: CGFloat(editor.shakes)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { value in withAnimation(reduceMotion ? nil : .easeOut(duration: 0.12)) { hovering = value } }
        .help(shortcut.map { "\(name): \($0.label). Click, then press a new key." } ?? "Click, then press a key for \(name).")
        .accessibilityLabel("\(name) key")
        .accessibilityValue(recording ? "Waiting for a key" : shortcut?.spokenLabel ?? "None")
        .accessibilityHint("Press, then type a letter or number.")
        .onChange(of: shortcut) { _, value in
            guard value != nil, !reduceMotion else { return }
            withAnimation(.spring(response: 0.16, dampingFraction: 0.5)) { bounce = true }
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(150))
                withAnimation(.spring(response: 0.34, dampingFraction: 0.6)) { bounce = false }
            }
        }
    }
}

private struct GlobalKeyButton: View {
    let kind: GlobalShortcutKind
    let shortcut: ActivationShortcut
    let editor: ShortcutEditorContext
    @State private var bounce = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private var target: ShortcutRecordingTarget { .global(kind) }
    private var recording: Bool { editor.recording == target }
    private var spoken: String {
        ([shortcut.control ? "Control" : nil, shortcut.option ? "Option" : nil, shortcut.shift ? "Shift" : nil,
          shortcut.command ? "Command" : nil].compactMap { $0 } + [shortcut.character.uppercased()]).joined(separator: " + ")
    }

    var body: some View {
        Button { editor.toggle(target) } label: {
            Group {
                if recording {
                    Text(editor.liveModifiers.isEmpty ? "Press keys" : editor.liveModifiers + " + key")
                        .font(.system(size: 12, weight: .semibold, design: .rounded))
                        .foregroundStyle(Color.accentColor)
                        .padding(.horizontal, 12).frame(minWidth: 120, minHeight: 30)
                        .background(RoundedRectangle(cornerRadius: 7).fill(Color.accentColor.opacity(0.14)))
                        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(Color.accentColor, lineWidth: 1.5))
                        .overlay { if !reduceMotion { PulseRing(cornerRadius: 7) } }
                } else {
                    ShortcutKeycaps(shortcut: shortcut).frame(minWidth: 120, alignment: .trailing)
                }
            }
            .scaleEffect(bounce ? 1.08 : 1)
            .modifier(ShakeEffect(travel: editor.shakeTarget == target ? 6 : 0, animatableData: CGFloat(editor.shakes)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Click, then press a new combination for \(kind.title).")
        .accessibilityLabel("\(kind.title) shortcut")
        .accessibilityValue(recording ? "Waiting for keys" : spoken)
        .accessibilityHint("Press, then hold Control, Option or Command and type a letter or number.")
        .accessibilityIdentifier("global-shortcut-\(kind.rawValue)")
        .onChange(of: shortcut) { _, _ in
            guard !reduceMotion else { return }
            withAnimation(.spring(response: 0.18, dampingFraction: 0.5)) { bounce = true }
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(160))
                withAnimation(.spring(response: 0.34, dampingFraction: 0.65)) { bounce = false }
            }
        }
    }
}

// MARK: - Toolbar arranger

/// A tile in the arranger: a tool or added button you can put anywhere, or one of the toolbar's own buttons, which keep
/// their places and can be taken off and put back.
enum ArrangerItem: Hashable {
    case command(ShortcutCommand)
    case control(ToolbarControl)

    var id: String {
        switch self {
        case .command(let command): command.id
        case .control(let control): "control.\(control.rawValue)"
        }
    }

    var label: String {
        switch self {
        case .command(let command): command.label
        case .control(let control): control.label
        }
    }
}

/// Drag tools and buttons between "Your toolbar" and "More tools", like customizing a Mac app toolbar.
struct ToolbarArranger: View {
    @ObservedObject var model: WorkspaceModel
    let editor: ShortcutEditorContext
    @Namespace private var namespace
    @State private var drag: ArrangerDrag?
    @State private var ignoringGesture = false
    @State private var frames: [String: CGRect] = [:]
    @State private var settle: Task<Void, Never>?
    @FocusState private var focused: ArrangerItem?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let space = "toolbar-arranger"
    private static let select = ArrangerItem.command(.tool(.select))

    private struct ArrangerDrag: Equatable {
        var item: ArrangerItem
        var fromToolbar: Bool
        var location: CGPoint
        var grabOffset: CGSize
        var order: [ShortcutCommand]
        var controls: [ToolbarControl]
        var overStrip: Bool
        var settling = false
    }

    private enum TrayGroup: String, CaseIterable {
        case drawing = "Drawing", effects = "Effects", presenting = "Presenting", shapes = "Shapes", buttons = "Toolbar buttons"

        func contains(_ item: ArrangerItem) -> Bool {
            switch item {
            case .control: self == .buttons
            case .command(.action): self == .presenting
            case .command(.tool(let tool)):
                tool.isProtection ? self == .effects : tool.isShape ? self == .shapes : self == .drawing
            }
        }
    }

    private var metrics: ToolbarMetrics { ToolbarMetrics(model.toolbarSize) }
    private var order: [ShortcutCommand] { drag?.order ?? model.toolbarItems }
    private var controls: [ToolbarControl] { drag?.controls ?? ToolbarControl.allCases.filter(model.showsToolbarControl) }
    private var stripItems: [ArrangerItem] { order.map(ArrangerItem.command) + controls.map(ArrangerItem.control) }
    private var tray: [ArrangerItem] {
        WorkspaceModel.toolbarCandidates.filter { !order.contains($0) }.map(ArrangerItem.command) +
            ToolbarControl.allCases.filter { !controls.contains($0) }.map(ArrangerItem.control)
    }
    private var reorder: Animation? {
        Motion.resolve(Motion.snappy, reduceMotion: reduceMotion)
    }
    private var removing: Bool { drag.map { !$0.overStrip && $0.fromToolbar && $0.item != Self.select } ?? false }
    private var adding: Bool { drag.map { $0.overStrip && !$0.fromToolbar } ?? false }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Your toolbar").font(.title3.weight(.semibold))
                    Text("Drag tools to arrange them. Drag any tool or button down to take it off. Click the key under one, then press a new key.")
                        .font(.callout).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                Menu("Reset") {
                    Button("Default tools and order") {
                        withAnimation(reorder) { model.configureToolbar(WorkspacePreferences.defaultToolbar) }
                        editor.say("Your toolbar has the default tools again.")
                    }
                    Button("Show every toolbar button") {
                        withAnimation(reorder) { model.showAllToolbarControls() }
                        editor.say("Every toolbar button is back.")
                    }
                    Button("Default keys for tools and buttons") {
                        model.resetLocalShortcuts()
                        editor.say("Tool and button keys are back to their defaults.")
                    }
                }
                .menuStyle(.borderlessButton).fixedSize()
                .accessibilityIdentifier("reset-toolbar-menu")
            }
            strip
            trayHeader
            trayGrid
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 16).fill(Color.primary.opacity(0.03)))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.primary.opacity(0.06)))
        .coordinateSpace(name: Self.space)
        .overlay(alignment: .topLeading) { floatingItem }
        .simultaneousGesture(dragGesture)
        .onPreferenceChange(ArrangerFrameKey.self) { value in
            Task { @MainActor in frames = value }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("tool-shortcuts-list")
        .onDisappear {
            settle?.cancel()
            if let drag, !drag.settling { NSCursor.pop() }
            drag = nil
            ignoringGesture = false
        }
    }

    /// Your toolbar on one line, as the toolbar itself is. Settings opens wide enough for 15 tools and every button; a
    /// narrower window scrolls the line sideways instead of wrapping it.
    private var strip: some View {
        let shown = controls
        let rightSide = shown.filter { $0 != .library }
        return ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 3) {
                ForEach(Array(order.enumerated()), id: \.element) { index, command in
                    let familyBreak = index > 0 && ToolbarToolLayout.family(order[index - 1]) != ToolbarToolLayout.family(command)
                    stripItem(.command(command))
                        .padding(.leading, familyBreak ? 9 : 0)
                        .overlay(alignment: .leading) { if familyBreak { separator } }
                        .reportFrame("strip.\(ArrangerItem.command(command).id)")
                        .matchedGeometryEffect(id: ArrangerItem.command(command), in: namespace)
                }
                if shown.contains(.library) {
                    controlStripItem(.library)
                        .reportFrame("strip.\(ArrangerItem.control(.library).id)")
                        .matchedGeometryEffect(id: ArrangerItem.control(.library), in: namespace)
                }
                // The buttons on the toolbar's right stay small and together, as on the toolbar.
                if !rightSide.isEmpty { cluster(rightSide) }
            }
            .fixedSize()
            .padding(metrics.padding)
            .background {
                RoundedRectangle(cornerRadius: metrics.cornerRadius)
                    .fill(Color(nsColor: .windowBackgroundColor))
                    .shadow(color: .black.opacity(0.13), radius: 9, y: 3)
            }
            .overlay {
                RoundedRectangle(cornerRadius: metrics.cornerRadius)
                    .strokeBorder(adding ? Color.accentColor : Color.primary.opacity(0.08), lineWidth: adding ? 2 : 1)
            }
            .reportFrame("strip")
            // Room for the shadow and the remove badges, which the scroll view would otherwise cut.
            .padding(.vertical, 10)
            .padding(.horizontal, 2)
        }
        .scrollBounceBehavior(.basedOnSize, axes: .horizontal)
        .padding(.vertical, -10)
        .padding(.horizontal, -2)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Your toolbar")
        .accessibilityIdentifier("toolbar-arrangement")
    }

    private func cluster(_ members: [ToolbarControl]) -> some View {
        HStack(spacing: metrics.spacing) {
            ForEach(members, id: \.self) { control in
                controlStripItem(control)
                    .reportFrame("strip.\(ArrangerItem.control(control).id)")
                    .matchedGeometryEffect(id: ArrangerItem.control(control), in: namespace)
            }
        }
        .frame(height: metrics.minimal ? 50 : 60)
        .padding(.leading, 9)
        .overlay(alignment: .leading) { separator }
    }

    private var separator: some View {
        Rectangle().fill(Color.primary.opacity(0.12)).frame(width: 1, height: 26).offset(x: 3)
            .accessibilityHidden(true)
    }

    private var trayHeader: some View {
        HStack(spacing: 8) {
            Text("More tools").font(.headline)
            Group {
                if removing {
                    Label("Let go to take it off your toolbar", systemImage: "minus.circle.fill")
                        .foregroundStyle(.red)
                } else if drag?.item == Self.select && drag?.overStrip == false {
                    Label("Select always stays in the toolbar", systemImage: "lock.fill")
                        .foregroundStyle(.orange)
                } else {
                    Text(tray.isEmpty ? "Everything is in your toolbar." : "Drag one up to add it, or click its plus button.")
                        .foregroundStyle(.secondary)
                }
            }
            .font(.caption)
            .transition(.opacity)
            .id(removing ? "remove" : drag?.item == Self.select ? "select" : "idle")
        }
        .animation(Motion.fadeIn, value: removing)
    }

    private var trayGrid: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(TrayGroup.allCases, id: \.self) { group in
                let members = tray.filter(group.contains)
                if !members.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(group.rawValue).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                            .accessibilityAddTraits(.isHeader)
                        FlowLayout(spacing: 6, lineSpacing: 6) {
                            ForEach(members, id: \.self) { item in
                                trayTile(item)
                                    .reportFrame("tray.\(item.id)")
                                    .matchedGeometryEffect(id: item, in: namespace)
                            }
                        }
                    }
                }
            }
        }
        .padding(8)
        .frame(maxWidth: .infinity, minHeight: 60, alignment: .topLeading)
        .background {
            RoundedRectangle(cornerRadius: 12)
                .fill(removing ? Color.red.opacity(0.06) : Color.clear)
        }
        .overlay {
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(removing ? Color.red.opacity(0.5) : Color.primary.opacity(0.07),
                              style: StrokeStyle(lineWidth: 1, dash: removing ? [5, 4] : []))
        }
        .animation(Motion.fadeIn, value: removing)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("More tools")
    }

    private func title(_ item: ArrangerItem) -> String { item.label }

    private static func isMenu(_ command: ShortcutCommand) -> Bool {
        if case .action(let action) = command { action.isMenu } else { false }
    }

    private func identifier(_ item: ArrangerItem, inToolbar: Bool) -> String {
        (inToolbar ? "toolbar-" : "more-") + item.id.replacingOccurrences(of: ".", with: "-")
    }

    private func detail(_ item: ArrangerItem) -> String {
        switch item {
        case .command(.action(let action)): action.detail
        case .command(.tool(.cover)): "Uses the last effect you chose: Blur, Pixelate or Distort."
        case .command(.tool(let tool)): tool.label
        case .control(let control): control.detail
        }
    }

    /// Tools and added buttons show their key under them. The toolbar's own buttons don't; Properties, Board and Clear all
    /// keep theirs in "Keys for toolbar buttons". The Spotlight and Zoom menus open with a click and have no key.
    private func keyChip(_ item: ArrangerItem) -> KeyChip? {
        guard case .command(let command) = item, !Self.isMenu(command) else { return nil }
        return KeyChip(target: .local(command), shortcut: model.shortcut(for: command), name: title(item), editor: editor,
                       compact: true)
    }

    @ViewBuilder private func stripItem(_ item: ArrangerItem) -> some View {
        switch item {
        case .command(let command): commandStripItem(command)
        case .control(let control): controlStripItem(control)
        }
    }

    private func commandStripItem(_ command: ShortcutCommand) -> some View {
        let item = ArrangerItem.command(command)
        let placeholder = drag?.item == item
        let index = order.firstIndex(of: command) ?? 0
        return ArrangerTile(item: item, title: title(item), style: .toolbar(metrics), placeholder: placeholder,
                            focused: focused == item, badge: item == Self.select ? nil : .remove,
                            key: keyChip(item), badgeAction: { remove(item) })
            // Only keyboard navigation focuses tiles; clicks and drags don't leave a focus ring behind.
            .focusable(interactions: .activate)
            .focusEffectDisabled()
            .focused($focused, equals: item)
            .onKeyPress(.leftArrow) { move(command, by: -1); return .handled }
            .onKeyPress(.rightArrow) { move(command, by: 1); return .handled }
            .onKeyPress(.delete) { remove(item); return .handled }
            .contextMenu {
                Button("Move Left") { move(command, by: -1) }.disabled(index == 0)
                Button("Move Right") { move(command, by: 1) }.disabled(index == order.count - 1)
                Divider()
                if !Self.isMenu(command) { Button("Change Key…") { editor.toggle(.local(command)) } }
                if item != Self.select { Button("Remove from Toolbar") { remove(item) } }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(title(item))
            .accessibilityValue("Position \(index + 1) of \(order.count)")
            .accessibilityHint("In your toolbar, position \(index + 1) of \(order.count). Use the actions to move or remove it.")
            .accessibilityIdentifier(identifier(item, inToolbar: true))
            .accessibilityAction(named: "Move left") { move(command, by: -1) }
            .accessibilityAction(named: "Move right") { move(command, by: 1) }
            .accessibilityAction(named: "Remove from toolbar") { remove(item) }
            .help(item == Self.select ? "Select always stays in the toolbar. Drag it to change its place." :
                    "Drag to move \(title(item)). Drag it down to remove it.")
    }

    /// The toolbar's own buttons look as they do on the toolbar and keep their places; they can only come off and go back.
    private func controlStripItem(_ control: ToolbarControl) -> some View {
        let item = ArrangerItem.control(control)
        return ArrangerControlTile(control: control, metrics: metrics, libraryKey: model.toolLibraryShortcut?.label,
                                   placeholder: drag?.item == item, focused: focused == item, removing: false,
                                   badgeAction: { remove(item) })
            .focusable(interactions: .activate)
            .focusEffectDisabled()
            .focused($focused, equals: item)
            .onKeyPress(.delete) { remove(item); return .handled }
            .contextMenu { Button("Remove from Toolbar") { remove(item) } }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(title(item))
            .accessibilityValue("In your toolbar")
            .accessibilityHint("It keeps its place in your toolbar. Use the Remove from toolbar action to take it off.")
            .accessibilityIdentifier(identifier(item, inToolbar: true))
            .accessibilityAction(named: "Remove from toolbar") { remove(item) }
            .help("\(control.label): \(control.detail) Drag it down to take it off your toolbar.")
    }

    private func trayTile(_ item: ArrangerItem) -> some View {
        ArrangerTile(item: item, title: title(item), style: .tray, placeholder: drag?.item == item,
                     focused: focused == item, badge: .add, key: keyChip(item), badgeAction: { add(item) })
            .focusable(interactions: .activate)
            .focusEffectDisabled()
            .focused($focused, equals: item)
            .onKeyPress(.return) { add(item); return .handled }
            .contextMenu {
                Button("Add to Toolbar") { add(item) }
                if case .command(let command) = item, !Self.isMenu(command) {
                    Button("Change Key…") { editor.toggle(.local(command)) }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(title(item))
            .accessibilityValue("Not in the toolbar")
            .accessibilityHint("Not in your toolbar. Use the Add to toolbar action to add it.")
            .accessibilityIdentifier(identifier(item, inToolbar: false))
            .accessibilityAction(named: "Add to toolbar") { add(item) }
            .help("\(detail(item)) Drag it into your toolbar, or click the plus button.")
    }

    @ViewBuilder private var floatingItem: some View {
        if let drag {
            let removable = drag.item != Self.select
            let removing = !drag.overStrip && drag.fromToolbar && removable && !drag.settling
            Group {
                if case .control(let control) = drag.item, drag.overStrip {
                    ArrangerControlTile(control: control, metrics: metrics, libraryKey: model.toolLibraryShortcut?.label,
                                        placeholder: false, focused: false, removing: false, badgeAction: {})
                } else {
                    ArrangerTile(item: drag.item, title: title(drag.item),
                                 style: drag.overStrip || !removable ? .toolbar(metrics) : .tray,
                                 placeholder: false, focused: false, badge: removing ? .removing : nil,
                                 key: keyChip(drag.item), badgeAction: {})
                }
            }
                .background {
                    RoundedRectangle(cornerRadius: 10)
                        .fill(Color(nsColor: .windowBackgroundColor))
                        .shadow(color: .black.opacity(drag.settling ? 0 : 0.25), radius: drag.settling ? 0 : 14,
                                y: drag.settling ? 0 : 8)
                }
                .scaleEffect(drag.settling || reduceMotion ? 1 : 1.1)
                .rotationEffect(.degrees(drag.settling || reduceMotion ? 0 : (drag.overStrip ? -2 : 2)))
                .position(x: drag.location.x - drag.grabOffset.width, y: drag.location.y - drag.grabOffset.height)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
                .transition(.identity)
        }
    }

    // MARK: Drag

    private var dragGesture: some Gesture {
        DragGesture(minimumDistance: 4, coordinateSpace: .named(Self.space))
            .onChanged(dragChanged)
            .onEnded(dragEnded)
    }

    private func hit(_ point: CGPoint) -> (ArrangerItem, Bool, CGRect)? {
        for item in stripItems {
            if let frame = frames["strip.\(item.id)"], frame.contains(point) { return (item, true, frame) }
        }
        for item in tray {
            if let frame = frames["tray.\(item.id)"], frame.contains(point) { return (item, false, frame) }
        }
        return nil
    }

    private func insertionIndex(for location: CGPoint, among others: [ShortcutCommand]) -> Int {
        for (index, item) in others.enumerated() {
            guard let frame = frames["strip.\(item.id)"] else { continue }
            if location.y < frame.minY - 3 { return index }
            if location.y <= frame.maxY + 3 && location.x < frame.midX { return index }
        }
        return others.count
    }

    private func dragChanged(_ value: DragGesture.Value) {
        if ignoringGesture { return }
        if drag == nil {
            guard let found = hit(value.startLocation) else { ignoringGesture = true; return }
            let (item, fromToolbar, frame) = found
            settle?.cancel()
            if let recording = editor.recording { editor.toggle(recording) }
            focused = nil
            let start = ArrangerDrag(item: item, fromToolbar: fromToolbar, location: value.location,
                                     grabOffset: CGSize(width: value.startLocation.x - frame.midX,
                                                        height: value.startLocation.y - frame.midY),
                                     order: model.toolbarItems,
                                     controls: ToolbarControl.allCases.filter(model.showsToolbarControl),
                                     overStrip: fromToolbar)
            withAnimation(reduceMotion ? nil : Motion.snappy) { drag = start }
            NSCursor.closedHand.push()
        }
        guard var current = drag, !current.settling else { return }
        current.location = value.location
        current.overStrip = (frames["strip"] ?? .zero).insetBy(dx: -18, dy: -18).contains(value.location)
        var next = current.order
        var nextControls = current.controls
        switch current.item {
        case .command(let command):
            if current.overStrip {
                var others = next.filter { $0 != command }
                others.insert(command, at: insertionIndex(for: value.location, among: others))
                next = others
            } else if current.item != Self.select {
                next.removeAll { $0 == command }
            }
        case .control(let control):
            // The toolbar's own buttons go back to their places, whatever the drop point.
            let shown = current.overStrip
            nextControls = ToolbarControl.allCases.filter { $0 == control ? shown : current.controls.contains($0) }
        }
        var still = Transaction()
        still.disablesAnimations = true
        withTransaction(still) { drag = current }
        if next != current.order || nextControls != current.controls {
            withAnimation(reorder) {
                drag?.order = next
                drag?.controls = nextControls
            }
        }
    }

    private func dragEnded(_ value: DragGesture.Value) {
        defer { ignoringGesture = false }
        guard var current = drag, !current.settling else { return }
        NSCursor.pop()
        switch current.item {
        case .command(let command):
            let result = current.order
            let before = model.toolbarItems
            if result != before, result.contains(.tool(.select)) {
                withAnimation(reorder) { model.configureToolbar(items: result) }
            }
            let added = !before.contains(command) && result.contains(command)
            let removed = before.contains(command) && !result.contains(command)
            if current.item == Self.select && !current.overStrip { editor.say("Select always stays in the toolbar.") }
            else if added { editor.say("\(title(current.item)) is in your toolbar now.") }
            else if removed { editor.say("\(title(current.item)) moved to More tools.") }
            current.overStrip = result.contains(command)
        case .control(let control):
            let shown = current.controls.contains(control)
            if shown != model.showsToolbarControl(control) {
                withAnimation(reorder) { model.setToolbarControl(control, shown: shown) }
                editor.say(shown ? "\(control.label) is back in your toolbar." :
                            "\(control.label) moved to More tools. Its key still works.")
            }
            current.overStrip = shown
        }
        let key = current.overStrip ? "strip.\(current.item.id)" : "tray.\(current.item.id)"
        current.settling = true
        if let target = frames[key] {
            current.location = CGPoint(x: target.midX + current.grabOffset.width, y: target.midY + current.grabOffset.height)
        }
        if reduceMotion { drag = nil; return }
        withAnimation(Motion.snappy) { drag = current }
        settle = Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(260))
            guard !Task.isCancelled else { return }
            drag = nil
        }
    }

    // MARK: Commands

    private func add(_ item: ArrangerItem) {
        switch item {
        case .command(let command):
            guard !model.toolbarItems.contains(command) else { return }
            withAnimation(reorder) { model.configureToolbar(items: model.toolbarItems + [command]) }
            editor.say("\(title(item)) is in your toolbar now.")
        case .control(let control):
            guard !model.showsToolbarControl(control) else { return }
            withAnimation(reorder) { model.setToolbarControl(control, shown: true) }
            editor.say("\(control.label) is back in your toolbar.")
        }
    }

    private func remove(_ item: ArrangerItem) {
        switch item {
        case .command(let command):
            guard item != Self.select else { editor.say("Select always stays in the toolbar."); return }
            withAnimation(reorder) { model.configureToolbar(items: model.toolbarItems.filter { $0 != command }) }
            editor.say("\(title(item)) moved to More tools.")
        case .control(let control):
            guard model.showsToolbarControl(control) else { return }
            withAnimation(reorder) { model.setToolbarControl(control, shown: false) }
            editor.say("\(control.label) moved to More tools. Its key still works.")
        }
    }

    private func move(_ item: ShortcutCommand, by delta: Int) {
        var items = model.toolbarItems
        guard let index = items.firstIndex(of: item), items.indices.contains(index + delta) else { return }
        items.swapAt(index, index + delta)
        withAnimation(reorder) { model.configureToolbar(items: items) }
    }
}

/// One tool or button in the arranger: a toolbar button or a "More tools" tile.
private struct ArrangerTile: View {
    enum Style: Equatable { case toolbar(ToolbarMetrics), tray }
    enum Badge { case add, remove, removing }
    let item: ArrangerItem
    let title: String
    let style: Style
    let placeholder: Bool
    let focused: Bool
    let badge: Badge?
    let key: KeyChip?
    let badgeAction: () -> Void
    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var size: CGSize {
        switch style {
        case .toolbar(let metrics): CGSize(width: metrics.buttonWidth + 4, height: metrics.minimal ? 50 : 60)
        case .tray: CGSize(width: 78, height: 84)
        }
    }

    var body: some View {
        VStack(spacing: 4) {
            switch style {
            case .toolbar(let metrics):
                glyph(size: metrics.glyphSize).frame(height: metrics.minimal ? 24 : 30)
                if key == nil, metrics.showsKeyLabels, case .command(.action(let action)) = item, action.isMenu {
                    Image(systemName: "chevron.down").font(.system(size: 7, weight: .semibold)).foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                }
            case .tray:
                glyph(size: 20).frame(height: 24)
                Text(title).font(.system(size: 10, weight: .medium)).lineLimit(2)
                    .multilineTextAlignment(.center).frame(height: 26)
            }
            if let key { key }
        }
        .frame(width: size.width, height: size.height)
        .background {
            RoundedRectangle(cornerRadius: 10)
                .fill(style == .tray ? Color.primary.opacity(hovering ? 0.08 : 0.045) :
                        Color.primary.opacity(hovering ? 0.06 : 0))
        }
        .overlay {
            RoundedRectangle(cornerRadius: 10).strokeBorder(Color.accentColor, lineWidth: 2)
                .opacity(focused ? 1 : 0)
                .transaction { $0.animation = nil }
        }
        .opacity(placeholder ? 0 : 1)
        .overlay {
            if placeholder {
                RoundedRectangle(cornerRadius: 10)
                    .fill(Color.accentColor.opacity(0.08))
                    .overlay(RoundedRectangle(cornerRadius: 10)
                        .strokeBorder(Color.accentColor.opacity(0.6), style: StrokeStyle(lineWidth: 1.5, dash: [4, 3])))
            }
        }
        .overlay(alignment: .topTrailing) {
            if let badge, !placeholder, hovering || badge == .removing {
                badgeView(badge)
                    .offset(x: 6, y: -6)
                    .transition(reduceMotion ? .opacity : .scale(scale: 0.3).combined(with: .opacity))
            }
        }
        .contentShape(RoundedRectangle(cornerRadius: 10))
        .onHover { value in
            withAnimation(reduceMotion ? nil : Motion.snappy) { hovering = value }
        }
    }

    @ViewBuilder private func glyph(size: CGFloat) -> some View {
        switch item {
        case .command(let command): CommandGlyph(command: command, size: size)
        case .control(let control):
            Image(systemName: control.symbol).font(.system(size: size * 0.82))
                .frame(width: size, height: size).accessibilityHidden(true)
        }
    }

    @ViewBuilder private func badgeView(_ badge: Badge) -> some View {
        let symbol = badge == .add ? "plus.circle.fill" : "minus.circle.fill"
        let tint: Color = badge == .add ? .green : .red
        let image = Image(systemName: symbol)
            .font(.system(size: 16, weight: .semibold))
            .symbolRenderingMode(.palette)
            .foregroundStyle(.white, tint)
            .background(Circle().fill(.white).padding(2))
            .shadow(color: .black.opacity(0.2), radius: 2, y: 1)
        if badge == .removing {
            image
        } else {
            Button(action: badgeAction) { image }
                .buttonStyle(.plain)
                .help(badge == .add ? "Add \(title) to your toolbar" : "Remove \(title) from your toolbar")
                .accessibilityLabel(badge == .add ? "Add \(title) to toolbar" : "Remove \(title) from toolbar")
        }
    }
}

/// One of the toolbar's own buttons in the arranger, drawn small as on the toolbar: Undo, Redo, Clear all, Properties,
/// Settings and Hide as icons, More tools with its key label, and Board and Draw as their buttons.
private struct ArrangerControlTile: View {
    let control: ToolbarControl
    let metrics: ToolbarMetrics
    let libraryKey: String?
    let placeholder: Bool
    let focused: Bool
    let removing: Bool
    let badgeAction: () -> Void
    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var size: CGSize {
        switch control {
        case .library: CGSize(width: metrics.buttonWidth, height: metrics.buttonHeight)
        case .board: CGSize(width: metrics.boardWidth, height: metrics.buttonHeight)
        case .mode: CGSize(width: metrics.modeWidth, height: metrics.buttonHeight)
        default: metrics.actionSize
        }
    }

    var body: some View {
        face
            .frame(width: size.width, height: size.height)
            .background {
                RoundedRectangle(cornerRadius: 7).fill(Color.primary.opacity(hovering ? 0.06 : 0))
            }
            .overlay {
                RoundedRectangle(cornerRadius: 7).strokeBorder(Color.accentColor, lineWidth: 2)
                    .opacity(focused ? 1 : 0)
                    .transaction { $0.animation = nil }
            }
            .opacity(placeholder ? 0 : 1)
            .overlay {
                if placeholder {
                    RoundedRectangle(cornerRadius: 7)
                        .fill(Color.accentColor.opacity(0.08))
                        .overlay(RoundedRectangle(cornerRadius: 7)
                            .strokeBorder(Color.accentColor.opacity(0.6), style: StrokeStyle(lineWidth: 1.5, dash: [4, 3])))
                }
            }
            .overlay(alignment: .topTrailing) {
                if !placeholder, hovering || removing {
                    Button(action: badgeAction) {
                        Image(systemName: "minus.circle.fill")
                            .font(.system(size: 13, weight: .semibold))
                            .symbolRenderingMode(.palette)
                            .foregroundStyle(.white, .red)
                            .background(Circle().fill(.white).padding(2))
                            .shadow(color: .black.opacity(0.2), radius: 2, y: 1)
                    }
                    .buttonStyle(.plain)
                    .offset(x: 6, y: -6)
                    .help("Remove \(control.label) from your toolbar")
                    .accessibilityLabel("Remove \(control.label) from toolbar")
                    .transition(reduceMotion ? .opacity : .scale(scale: 0.3).combined(with: .opacity))
                }
            }
            .contentShape(RoundedRectangle(cornerRadius: 7))
            .onHover { value in
                withAnimation(reduceMotion ? nil : Motion.snappy) { hovering = value }
            }
    }

    @ViewBuilder private var face: some View {
        switch control {
        case .library:
            VStack(spacing: 2) {
                Image(systemName: control.symbol).font(.system(size: metrics.minimal ? 16 : 19))
                    .frame(height: metrics.minimal ? 20 : 24)
                if metrics.showsKeyLabels {
                    Text(libraryKey ?? " ").font(.system(size: 10)).foregroundStyle(.secondary)
                }
            }
        case .board:
            VStack(spacing: 3) {
                Image(systemName: control.symbol).font(.system(size: metrics.minimal ? 15 : 17))
                if !metrics.minimal { Text("Desk").font(.system(size: 9, weight: .medium)) }
            }
        case .mode:
            VStack(spacing: metrics.minimal ? 1 : 3) {
                Image(systemName: "pencil.tip").font(.system(size: metrics.minimal ? 14 : 17))
                HStack(spacing: metrics.minimal ? 3 : 4) {
                    Circle().fill(Color.green).frame(width: 5, height: 5)
                    Text("Draw").font(.system(size: metrics.minimal ? 8 : 9, weight: .semibold))
                }
            }
            .frame(width: size.width, height: size.height)
            .background(Color.green.opacity(0.15), in: RoundedRectangle(cornerRadius: 7))
        default:
            Image(systemName: control.symbol)
        }
    }
}

// MARK: - Size picker

struct ToolbarSizePicker: View {
    let selection: ToolbarSize
    let choose: (ToolbarSize) -> Void
    @Namespace private var ring
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 12) {
            ForEach(ToolbarSize.allCases, id: \.self) { size in
                card(size)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Toolbar size")
    }

    private func card(_ size: ToolbarSize) -> some View {
        let selected = selection == size
        return Button { choose(size) } label: {
            VStack(alignment: .leading, spacing: 8) {
                MiniToolbar(size: size, highlighted: selected)
                    .frame(maxWidth: .infinity, minHeight: 46, alignment: .center)
                HStack {
                    Text(size == .expanded ? "Expanded" : "Minimal").font(.headline)
                    Spacer()
                    if selected {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(.tint)
                            .transition(reduceMotion ? .opacity : .scale.combined(with: .opacity))
                    }
                }
                Text(size == .expanded ? "Larger buttons, with each key shown under its tool." :
                        "Smaller buttons and no key labels. Hold the pointer over a tool to see its key.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(12)
            .frame(width: 250, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(selected ? 0.05 : 0.03)))
            .overlay {
                if selected {
                    RoundedRectangle(cornerRadius: 12).strokeBorder(Color.accentColor, lineWidth: 2)
                        .matchedGeometryEffect(id: "ring", in: ring)
                } else {
                    RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.08))
                }
            }
            .contentShape(RoundedRectangle(cornerRadius: 12))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(size == .expanded ? "Expanded toolbar" : "Minimal toolbar")
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("toolbar-size-\(size.rawValue)")
    }
}

private struct MiniToolbar: View {
    let size: ToolbarSize
    let highlighted: Bool
    private let tools: [(Tool, String)] = [(.select, "1"), (.rectangle, "2"), (.arrow, "5"), (.pen, "7"), (.text, "8")]

    var body: some View {
        let minimal = size == .minimal
        HStack(spacing: minimal ? 3 : 5) {
            ForEach(Array(tools.enumerated()), id: \.offset) { index, item in
                VStack(spacing: 1) {
                    ToolGlyph(tool: item.0, size: minimal ? 10 : 12)
                    if !minimal { Text(item.1).font(.system(size: 6.5)).foregroundStyle(.secondary) }
                }
                .frame(width: minimal ? 15 : 20, height: minimal ? 16 : 24)
                .background(RoundedRectangle(cornerRadius: 4)
                    .fill(index == 3 && highlighted ? Color.accentColor.opacity(0.2) : .clear))
            }
            Rectangle().fill(Color.primary.opacity(0.15)).frame(width: 1, height: minimal ? 12 : 18)
            ForEach(0..<3, id: \.self) { _ in
                RoundedRectangle(cornerRadius: 2).fill(Color.primary.opacity(0.25))
                    .frame(width: minimal ? 7 : 9, height: minimal ? 7 : 9)
            }
        }
        .padding(.horizontal, minimal ? 6 : 9).padding(.vertical, minimal ? 4 : 6)
        .background(RoundedRectangle(cornerRadius: minimal ? 6 : 8).fill(Color(nsColor: .windowBackgroundColor))
            .shadow(color: .black.opacity(0.12), radius: 3, y: 1))
        .overlay(RoundedRectangle(cornerRadius: minimal ? 6 : 8).strokeBorder(Color.primary.opacity(0.08)))
        .accessibilityHidden(true)
    }
}

// MARK: - Motion helpers

struct ShakeEffect: GeometryEffect {
    var travel: CGFloat = 5
    var animatableData: CGFloat

    func effectValue(size: CGSize) -> ProjectionTransform {
        let progress = animatableData - animatableData.rounded(.down)
        let damping = 1 - progress
        return ProjectionTransform(CGAffineTransform(
            translationX: travel * damping * sin(progress * .pi * 6), y: 0))
    }
}

/// An expanding ring that shows a key chip is listening. It only exists while recording.
struct PulseRing: View {
    let cornerRadius: CGFloat
    @State private var expanded = false

    var body: some View {
        RoundedRectangle(cornerRadius: cornerRadius)
            .stroke(Color.accentColor, lineWidth: 1.5)
            .scaleEffect(expanded ? 1.45 : 1)
            .opacity(expanded ? 0 : 0.7)
            .allowsHitTesting(false)
            .onAppear {
                withAnimation(.easeOut(duration: 1.1).repeatForever(autoreverses: false)) { expanded = true }
            }
    }
}

// MARK: - Layout and measurement

struct FlowLayout: Layout {
    var spacing: CGFloat = 4
    var lineSpacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width ?? .infinity, subviews: subviews)
        let width = rows.map(\.width).max() ?? 0
        let height = rows.reduce(0) { $0 + $1.height } + CGFloat(max(0, rows.count - 1)) * lineSpacing
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(width: bounds.width, subviews: subviews) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y + (row.height - size.height) / 2),
                                      proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + lineSpacing
        }
    }

    private struct Row { var indices: [Int] = []; var width: CGFloat = 0; var height: CGFloat = 0 }

    private func arrange(width: CGFloat, subviews: Subviews) -> [Row] {
        var rows: [Row] = []
        var row = Row()
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            let next = row.indices.isEmpty ? size.width : row.width + spacing + size.width
            if !row.indices.isEmpty && next > width {
                rows.append(row)
                row = Row()
            }
            row.width = row.indices.isEmpty ? size.width : row.width + spacing + size.width
            row.height = max(row.height, size.height)
            row.indices.append(index)
        }
        if !row.indices.isEmpty { rows.append(row) }
        return rows
    }
}

private struct ArrangerFrameKey: PreferenceKey {
    static let defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { $1 }
    }
}

private extension View {
    func reportFrame(_ key: String) -> some View {
        background {
            GeometryReader { proxy in
                Color.clear.preference(key: ArrangerFrameKey.self,
                                       value: [key: proxy.frame(in: .named(ToolbarArranger.space))])
            }
        }
    }
}

// MARK: - Key capture

/// Takes keyboard focus while a key chip is recording, then gives it back.
struct KeyCapture: NSViewRepresentable {
    var active: Bool
    var capturesCommandKeys: Bool
    var onKey: (NSEvent) -> Void
    var onFlags: (String) -> Void
    var onResign: () -> Void

    func makeNSView(context: Context) -> CaptureView { CaptureView() }

    func updateNSView(_ view: CaptureView, context: Context) {
        view.onKey = onKey
        view.onFlags = onFlags
        view.onResign = onResign
        view.capturesCommandKeys = capturesCommandKeys
        view.active = active
    }

    final class CaptureView: NSView {
        var onKey: (NSEvent) -> Void = { _ in }
        var onFlags: (String) -> Void = { _ in }
        var onResign: () -> Void = {}
        var capturesCommandKeys = false
        private var resigning = false
        var active = false {
            didSet {
                guard active != oldValue else { return }
                if active { window?.makeFirstResponder(self) }
                else if window?.firstResponder === self {
                    resigning = true
                    window?.makeFirstResponder(nil)
                    resigning = false
                }
            }
        }

        override var acceptsFirstResponder: Bool { active }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if active { window?.makeFirstResponder(self) }
        }

        override func keyDown(with event: NSEvent) {
            guard active else { super.keyDown(with: event); return }
            onKey(event)
        }

        override func performKeyEquivalent(with event: NSEvent) -> Bool {
            guard active, capturesCommandKeys, event.type == .keyDown else { return super.performKeyEquivalent(with: event) }
            onKey(event)
            return true
        }

        override func flagsChanged(with event: NSEvent) {
            guard active else { super.flagsChanged(with: event); return }
            let flags = event.modifierFlags
            onFlags((flags.contains(.control) ? "⌃" : "") + (flags.contains(.option) ? "⌥" : "") +
                    (flags.contains(.shift) ? "⇧" : "") + (flags.contains(.command) ? "⌘" : ""))
        }

        override func resignFirstResponder() -> Bool {
            let resigned = super.resignFirstResponder()
            if resigned && active && !resigning {
                let callback = onResign
                DispatchQueue.main.async { callback() }
            }
            return resigned
        }
    }
}
