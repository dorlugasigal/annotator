import AppKit
import SwiftUI
import AnnotationCore

enum MenuBarAction: String, Hashable {
    case showTools, hideTools, draw, useApps, stopZoom, toggleRegionSharing, shareLeftHalf, shareRightHalf, shareRegion, startBreak, breakFive, breakTen, breakFifteen, breakThirty, openBoard, saveBoard, exportPNG, settings, help, about, checkForUpdates, support, quit

    static let quickBreaks: [MenuBarAction] = [.breakFive, .breakTen, .breakFifteen, .breakThirty]

    /// The length of a quick break start; `nil` uses the saved length.
    var breakMinutes: Int? {
        switch self {
        case .breakFive: 5
        case .breakTen: 10
        case .breakFifteen: 15
        case .breakThirty: 30
        default: nil
        }
    }
}

struct MenuBarPopoverView: View {
    @ObservedObject var model: WorkspaceModel
    let developerPreview: Bool
    let perform: (MenuBarAction) -> Void
    var resized: (CGSize) -> Void = { _ in }
    var maximumHeight: CGFloat = 640
    var toolsVisible: () -> Bool = { false }
    @Environment(\.colorScheme) private var scheme
    @Environment(\.openSettings) private var openSettings
    @State private var boardExpanded = false
    @State private var contentHeight: CGFloat = 380
    @FocusState private var focus: Focus?

    private enum Focus: Hashable {
        case action(MenuBarAction), board
    }
    private var accent: Color {
        scheme == .dark ? Color(red: 0.30, green: 0.45, blue: 0.36) : Color(red: 0.17, green: 0.27, blue: 0.21)
    }
    private var primaryAction: MenuBarAction { toolsVisible() ? .hideTools : .showTools }
    private var primaryTitle: String { toolsVisible() ? "Hide drawings and tools" : "Show drawings and tools" }
    private var modeAction: MenuBarAction? {
        toolsVisible() ? (model.mode == .draw ? .useApps : .draw) : nil
    }
    private var focusOrder: [Focus] {
        var result: [Focus] = [.action(primaryAction)]
        if let modeAction { result.append(.action(modeAction)) }
        if model.screenZoomEnabled || model.magnifierEnabled { result.append(.action(.stopZoom)) }
        result += [.action(.toggleRegionSharing), .action(.shareLeftHalf), .action(.shareRightHalf), .action(.shareRegion)]
        result.append(.board)
        if boardExpanded {
            result.append(.action(.openBoard))
            if model.active != nil { result += [.action(.saveBoard), .action(.exportPNG)] }
        }
        result += [.action(.startBreak)] + MenuBarAction.quickBreaks.map(Focus.action)
        result += [.action(.settings), .action(.help), .action(.about)]
        if model.canCheckForUpdates { result.append(.action(.checkForUpdates)) }
        if model.optionalSupportURL != nil { result.append(.action(.support)) }
        return result + [.action(.quit)]
    }

    var body: some View {
        ScrollView(.vertical) { content }
            .scrollIndicators(.hidden)
            .frame(width: 320, height: min(contentHeight, maximumHeight))
            .onAppear {
                let action = openSettings
                model.onOpenPreferences = { action() }
            }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                Image(nsImage: MenuBarIcon.makeImage())
                    .renderingMode(.template).resizable().scaledToFit()
                    .frame(width: 28, height: 28)
                    .foregroundStyle(Color(red: 0.95, green: 0.94, blue: 0.91))
                    .frame(width: 46, height: 46)
                    .background(Color(red: 0.145, green: 0.17, blue: 0.155), in: RoundedRectangle(cornerRadius: 12))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text("Annotator").font(.custom("AvenirNext-DemiBold", size: 20, relativeTo: .headline))
                    Text("Version \(model.versionLabel)")
                        .font(.custom("AvenirNext-Regular", size: 11, relativeTo: .caption))
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            if developerPreview {
                HStack(spacing: 5) {
                    Circle().fill(Color(red: 0.91, green: 0.35, blue: 0.25)).frame(width: 5, height: 5)
                    Text("Local build").font(.custom("AvenirNext-Medium", size: 10, relativeTo: .caption2))
                }
                .foregroundStyle(.secondary)
                .padding(.top, 10)
            }
            Button { perform(primaryAction) } label: {
                HStack(spacing: 9) {
                    Image(systemName: toolsVisible() ? "eye.slash" : "pencil.tip").font(.system(size: 16, weight: .medium))
                    Text(primaryTitle).font(.custom("AvenirNext-DemiBold", size: 13, relativeTo: .body))
                    Spacer(minLength: 4)
                    shortcut(model.preferences.activationShortcut.label)
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 12).frame(height: 46)
                .background(accent, in: RoundedRectangle(cornerRadius: 10))
                .contentShape(RoundedRectangle(cornerRadius: 10))
            }
            .buttonStyle(.plain)
            .focusable()
            .focused($focus, equals: .action(primaryAction))
            .overlay {
                RoundedRectangle(cornerRadius: 8).stroke(.white.opacity(focus == .action(primaryAction) ? 0.7 : 0), lineWidth: 1)
                    .padding(3).allowsHitTesting(false)
            }
            .accessibilityLabel(primaryTitle)
            .accessibilityValue(model.preferences.activationShortcut.label)
            .accessibilityIdentifier(toolsVisible() ? "menu-hide-tools" : "menu-show-tools")
            .padding(.top, 16).padding(.bottom, 8)

            if modeAction == .useApps {
                row("Use apps", symbol: "cursorarrow", action: .useApps)
            } else if modeAction == .draw {
                row("Draw", symbol: "pencil.tip", action: .draw,
                    shortcut: (model.preferences.resumeShortcut ?? .resumeDrawing).label)
            }
            if model.screenZoomEnabled || model.magnifierEnabled {
                row("Stop zoom", symbol: "minus.magnifyingglass", action: .stopZoom,
                    shortcut: (model.preferences.zoomShortcut ?? .liveZoom).label)
            }
            row(model.regionSharingState.isCapturing ? "Stop region sharing" : "Start region sharing",
                symbol: model.regionSharingState.isCapturing ? "stop.circle" : "rectangle.on.rectangle",
                action: .toggleRegionSharing, tint: model.regionSharingState.isCapturing ? .red : nil)
                .accessibilityValue(model.regionSharingState.isCapturing ? "On" : "Off")
                .background(model.regionSharingState.isCapturing ? Color.red.opacity(scheme == .dark ? 0.16 : 0.08) : .clear,
                            in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8)
                    .stroke(model.regionSharingState.isCapturing ? Color.red.opacity(0.4) : .clear, lineWidth: 1))
            if model.regionSharingState.isCapturing {
                HStack(spacing: 6) {
                    Circle().fill(.red).frame(width: 5, height: 5)
                    Text("Sharing is on. Hide drawings without stopping it.")
                }.font(.caption).foregroundStyle(.secondary).padding(.horizontal, 10).padding(.vertical, 5)
            }
            HStack(spacing: 8) {
                quickRegion("Left half", action: .shareLeftHalf, preset: .leftHalf)
                quickRegion("Right half", action: .shareRightHalf, preset: .rightHalf)
            }.padding(.vertical, 5)
            row("Configure region...", symbol: "slider.horizontal.3", action: .shareRegion)
            MenuPopoverRow(title: "Board", symbol: "square.stack", shortcut: nil,
                           trailingSymbol: boardExpanded ? "chevron.up" : "chevron.down") {
                boardExpanded.toggle()
            }
            .focused($focus, equals: .board)
            .overlay {
                RoundedRectangle(cornerRadius: 7).stroke(Color.accentColor.opacity(focus == .board ? 1 : 0), lineWidth: 1.5)
                    .allowsHitTesting(false)
            }
            .accessibilityValue(boardExpanded ? "Expanded" : "Collapsed")
            .accessibilityHint("Open, save or export a board.")
            .accessibilityIdentifier("menu-board")
            if boardExpanded {
                VStack(spacing: 1) {
                    row("Open board…", symbol: "folder", action: .openBoard, shortcut: "⌘O")
                    row("Save board…", symbol: "square.and.arrow.down", action: .saveBoard, shortcut: "⌘S",
                        enabled: model.active != nil)
                    row("Export PNG…", symbol: "square.and.arrow.up", action: .exportPNG,
                        enabled: model.active != nil)
                }
                .padding(.leading, 12)
                .overlay(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 1).fill(Color.primary.opacity(0.12))
                        .frame(width: 2).padding(.leading, 9).padding(.vertical, 5)
                }
            }
            Divider().padding(.vertical, 9)
            row("Start break timer", symbol: "timer", action: .startBreak,
                shortcut: (model.preferences.breakShortcut ?? .breakTimer).label)
                .accessibilityValue("\(model.breakTimerSettings.minutes) minutes")
            HStack(spacing: 6) {
                ForEach(MenuBarAction.quickBreaks, id: \.self) { action in
                    quickBreak(action)
                }
            }.padding(.vertical, 5)
            Text(model.breakTimerSettings.lockWhenTouched ?
                 "Your Mac locks if someone touches it." : "Press Esc to end the break.")
                .font(.caption).foregroundStyle(.secondary).padding(.horizontal, 10).padding(.bottom, 6)
                .accessibilityIdentifier("menu-break-caption")
            Divider().padding(.vertical, 9)
            row("Settings…", symbol: "slider.horizontal.3", action: .settings, shortcut: "⌘,")
            row("Help", symbol: "questionmark.circle", action: .help)
            row("About Annotator", symbol: "info.circle", action: .about)
            row(model.updateReminderVersion == nil ? "Check for updates…" : "Update available…",
                symbol: model.updateReminderVersion == nil ? "arrow.triangle.2.circlepath" : "square.and.arrow.down",
                action: .checkForUpdates, enabled: model.canCheckForUpdates)
            if !model.canCheckForUpdates || model.updateReminderVersion != nil {
                Text(model.updateStatusLine).font(.caption).foregroundStyle(.secondary).padding(.horizontal, 10)
            }
            row("Enjoying it? Buy me a coffee…", symbol: "cup.and.saucer", action: .support,
                enabled: model.optionalSupportURL != nil)
                .background(Color.orange.opacity(scheme == .dark ? 0.20 : 0.14), in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.orange.opacity(0.3), lineWidth: 1))
                .padding(.top, 8)
            Divider().padding(.vertical, 9)
            row("Quit Annotator", symbol: "power", action: .quit, shortcut: "⌘Q")
        }
        .padding(16)
        .frame(width: 320)
        .fixedSize(horizontal: false, vertical: true)
        .background {
            Rectangle().fill(.ultraThinMaterial)
                .overlay((scheme == .dark ? Color(red: 0.10, green: 0.12, blue: 0.11) :
                    Color(red: 0.97, green: 0.96, blue: 0.94)).opacity(0.82))
        }
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .background {
            GeometryReader { proxy in Color.clear.preference(key: MenuPopoverSize.self, value: proxy.size) }
        }
        .onPreferenceChange(MenuPopoverSize.self) { size in
            guard size.width > 0, size.height > 0 else { return }
            Task { @MainActor in
                contentHeight = size.height
                resized(CGSize(width: size.width, height: min(size.height, maximumHeight)))
            }
        }
        .onAppear { focus = .action(primaryAction) }
        .onChange(of: primaryAction) { old, new in
            if focus == .action(old) { focus = .action(new) }
        }
        .onChange(of: modeAction) { old, new in
            if let old, focus == .action(old) { focus = new.map(Focus.action) ?? .action(primaryAction) }
        }
        .onMoveCommand { direction in
            if direction == .right && focus == .board { boardExpanded = true; return }
            if direction == .left && boardExpanded { boardExpanded = false; focus = .board; return }
            guard direction == .up || direction == .down else { return }
            let order = focusOrder
            let index = focus.flatMap { order.firstIndex(of: $0) } ?? 0
            focus = order[(index + (direction == .down ? 1 : order.count - 1)) % order.count]
        }
        .onKeyPress(.return) {
            if focus == .board { boardExpanded.toggle() }
            else if case .action(let action)? = focus { perform(action) }
            return .handled
        }
    }

    private func row(_ title: String, symbol: String, action: MenuBarAction, shortcut: String? = nil,
                     enabled: Bool = true, tint: Color? = nil) -> some View {
        MenuPopoverRow(title: title, symbol: symbol, shortcut: shortcut, tint: tint) { perform(action) }
            .disabled(!enabled)
            .focused($focus, equals: .action(action))
            .overlay {
                RoundedRectangle(cornerRadius: 7)
                    .stroke(Color.accentColor.opacity(focus == .action(action) && enabled ? 1 : 0), lineWidth: 1.5)
                    .allowsHitTesting(false)
            }
            .accessibilityIdentifier("menu-\(action.rawValue)")
    }

    private func quickRegion(_ title: String, action: MenuBarAction, preset: ShareRegionPreset) -> some View {
        Button { perform(action) } label: {
            Text(title).font(.custom("AvenirNext-Medium", size: 12, relativeTo: .body))
                .frame(maxWidth: .infinity, minHeight: 34)
                .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .focusable()
        .focused($focus, equals: .action(action))
        .overlay(RoundedRectangle(cornerRadius: 7)
            .stroke(focus == .action(action) ? Color.accentColor : .clear, lineWidth: 1.5))
        .accessibilityLabel("Share \(title.lowercased())")
        .accessibilityIdentifier("menu-\(action.rawValue)")
        .accessibilityAddTraits(model.regionSharingState.isCapturing && model.regionSharing.selectedPreset == preset ? .isSelected : [])
    }

    private func quickBreak(_ action: MenuBarAction) -> some View {
        let minutes = action.breakMinutes ?? 0
        return Button { perform(action) } label: {
            Text("\(minutes) min").font(.custom("AvenirNext-Medium", size: 12, relativeTo: .body))
                .monospacedDigit()
                .frame(maxWidth: .infinity, minHeight: 30)
                .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .focusable()
        .focused($focus, equals: .action(action))
        .overlay(RoundedRectangle(cornerRadius: 7)
            .stroke(focus == .action(action) ? Color.accentColor : .clear, lineWidth: 1.5))
        .accessibilityLabel("Start a \(minutes)-minute break")
        .accessibilityIdentifier("menu-\(action.rawValue)")
    }

    private func shortcut(_ value: String) -> some View {
        Text(value).font(.system(size: 10, weight: .medium, design: .monospaced))
            .padding(.horizontal, 6).padding(.vertical, 4)
            .background(.white.opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
            .accessibilityHidden(true)
    }
}

private struct MenuPopoverRow: View {
    let title: String
    let symbol: String
    let shortcut: String?
    var trailingSymbol: String? = nil
    var tint: Color? = nil
    let action: () -> Void
    @Environment(\.isEnabled) private var enabled
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol).font(.system(size: 14)).frame(width: 20)
                    .foregroundStyle(tint ?? .secondary)
                Text(title).font(.custom("AvenirNext-Medium", size: 13, relativeTo: .body))
                Spacer(minLength: 8)
                if let shortcut {
                    Text(shortcut).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                }
                if let trailingSymbol {
                    Image(systemName: trailingSymbol).font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                }
            }
            .padding(.horizontal, 10).frame(height: 36)
            .foregroundStyle(enabled ? tint ?? Color.primary : Color.secondary)
            .background(Color.primary.opacity(hovered && enabled ? 0.065 : 0), in: RoundedRectangle(cornerRadius: 7))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .focusable()
        .onHover { hovered = $0 }
        .accessibilityLabel(title)
    }
}

private struct MenuPopoverSize: PreferenceKey {
    static let defaultValue = CGSize.zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        let next = nextValue()
        if next.width > 0, next.height > 0 { value = next }
    }
}
