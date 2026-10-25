import AppKit
import SwiftUI
import AnnotationCore

private struct ToolbarMeasuredSize: PreferenceKey {
    static let defaultValue = CGSize.zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        let next = nextValue()
        if next.width > 0, next.height > 0 { value = next }
    }
}

enum ToolbarPropertiesMode: String, CaseIterable {
    case toolsOnly = "Tools only", quick = "Quick controls", full = "Full properties"

    /// Full properties are the default, so Curved and the other choices show without a click.
    @MainActor init(model: WorkspaceModel) {
        self = model.preferences.quickPreferencesExpanded == false ? .toolsOnly :
            model.preferences.inspectorPinned == false ? .quick : .full
    }

    var next: Self {
        switch self {
        case .toolsOnly: .quick
        case .quick: .full
        case .full: .toolsOnly
        }
    }

    @MainActor func apply(to model: WorkspaceModel) {
        switch self {
        case .toolsOnly: model.setQuickPreferencesExpanded(false)
        case .quick:
            model.setInspectorPinned(false)
            model.setQuickPreferencesExpanded(true)
        case .full: model.setInspectorPinned(true)
        }
    }
}

enum ActiveToolbarMode: String, CaseIterable {
    case screenZoom, magnifier, spotlight, cursorHalo, whiteboard, blackboard

    var title: String {
        switch self {
        case .screenZoom: "Screen zoom"
        case .magnifier: "Pointer lens"
        case .spotlight: "Spotlight"
        case .cursorHalo: "Cursor halo"
        case .whiteboard: "Whiteboard"
        case .blackboard: "Blackboard"
        }
    }

    var shortTitle: String {
        switch self {
        case .screenZoom: "Zoom"
        case .magnifier: "Lens"
        case .spotlight: "Spot"
        case .cursorHalo: "Halo"
        case .whiteboard: "White"
        case .blackboard: "Black"
        }
    }

    var symbol: String {
        switch self {
        case .screenZoom: "arrow.up.left.and.arrow.down.right"
        case .magnifier: "plus.magnifyingglass"
        case .spotlight: "flashlight.on.fill"
        case .cursorHalo: "circle.dotted"
        case .whiteboard: "rectangle.fill"
        case .blackboard: "rectangle"
        }
    }

    @MainActor func isActive(in model: WorkspaceModel) -> Bool {
        switch self {
        case .screenZoom: model.screenZoomEnabled && PendingZoomPresentation(state: model.zoomStartState) == nil
        case .magnifier: model.magnifierEnabled
        case .spotlight: model.spotlight
        case .cursorHalo: model.cursorHalo
        case .whiteboard: model.board == .light
        case .blackboard: model.board == .dark
        }
    }

    @MainActor func stop(in model: WorkspaceModel) {
        switch self {
        case .screenZoom, .magnifier: model.stopZoom()
        case .spotlight: model.spotlight = false
        case .cursorHalo: model.cursorHalo = false
        case .whiteboard, .blackboard: model.board = .transparent
        }
    }
}

struct PendingZoomPresentation {
    let phase: String
    let detail: String
    var title: String { "Zoom \(phase.lowercased())" }

    init?(state: ZoomStartState) {
        switch state {
        case .screenPermission:
            phase = "Waiting"
            detail = "Waiting for Screen Recording permission"
        case .gesturePermission:
            phase = "Waiting"
            detail = "Waiting for Accessibility permission"
        case .starting:
            phase = "Starting"
            detail = "Starting live screen zoom"
        case .idle, .active, .failed:
            return nil
        }
    }
}

struct PendingZoomControl: View {
    let presentation: PendingZoomPresentation
    var compact: Bool
    let cancel: () -> Void

    var body: some View {
        Button(action: cancel) {
            VStack(spacing: 3) {
                if compact {
                    Text("Zoom").font(.system(size: 9, weight: .medium))
                    Text(presentation.phase).font(.system(size: 8))
                    Label("Cancel", systemImage: "xmark").font(.system(size: 8, weight: .medium))
                } else {
                    Label(presentation.title, systemImage: "hourglass")
                        .font(.system(size: 11, weight: .medium))
                    Text("Cancel").font(.system(size: 10))
                }
            }
            .foregroundStyle(Color.orange)
            .frame(width: compact ? 44 : 130, height: 44)
            .background(Color.orange.opacity(0.13), in: RoundedRectangle(cornerRadius: 8))
            .contentShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .help("\(presentation.detail). Click to cancel the zoom request.")
        .accessibilityLabel("Cancel pending zoom")
        .accessibilityValue(presentation.detail)
        .accessibilityIdentifier("cancel-pending-zoom")
    }
}

struct ToolbarView: View {
    @ObservedObject var model: WorkspaceModel
    var availableSize = CGSize(width: 1000, height: 900)
    var showInspector = true
    var orientationOverride: ToolbarOrientation?
    var dockEdgeOverride: ToolbarDockEdge?
    private var tools: [Tool] { model.toolbarTools }
    private var items: [ShortcutCommand] { model.toolbarItems }
    @State private var libraryOpen = false
    @State private var librarySection = ToolLibrarySection.shapes
    @State private var drawPulse = 0
    /// The properties layout the person last saw. A different current layout means they just changed it.
    @State private var settledPropertiesMode: ToolbarPropertiesMode?
    @Namespace private var selectionSpace
    @Environment(\.openSettings) private var openSettings
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Hidden or transitioning toolbars change state at once so their snapshots match what lands.
    private var selectionAnimation: Animation? {
        model.workspaceHidden || model.visibilityAnimating ? nil : Motion.resolve(Motion.snappy, reduceMotion: reduceMotion)
    }
    private var vertical: Bool { showInspector && (orientationOverride ?? model.preferences.toolbarOrientation) == .vertical }
    private var metrics: ToolbarMetrics { ToolbarMetrics(model.toolbarSize) }
    private var activeModes: [ActiveToolbarMode] { ActiveToolbarMode.allCases.filter { $0.isActive(in: model) } }
    private var pendingZoom: PendingZoomPresentation? { PendingZoomPresentation(state: model.zoomStartState) }
    private var showsModeStrip: Bool { pendingZoom != nil || !activeModes.isEmpty }
    private var activeColumnWidth: CGFloat { showsModeStrip ? 76 : 0 }
    private var verticalPropertyWidth: CGFloat { min(300, max(240, availableSize.width - 76 - activeColumnWidth)) }
    private var width: CGFloat {
        vertical ? metrics.verticalRailWidth + activeColumnWidth + (expanded ? verticalPropertyWidth + 8 : 0) :
            ToolbarToolLayout.horizontalWidth(items: items, available: availableSize.width, propertiesVisible: expanded,
                                              size: model.toolbarSize)
    }
    private var compactRail: Bool { vertical || width < metrics.compactThreshold }
    private var toolRailWidth: CGFloat {
        let budget = max(metrics.buttonWidth, width - (compactRail ? metrics.compactRailReserve : metrics.railReserve))
        let visible = ToolbarToolLayout.visible(items, selected: .tool(activeRailTool), width: budget, size: model.toolbarSize)
        return ToolbarToolLayout.width(of: visible, size: model.toolbarSize)
    }
    private var propertiesMode: ToolbarPropertiesMode { ToolbarPropertiesMode(model: model) }
    private var expanded: Bool { propertiesMode != .toolsOnly }
    private var pinned: Bool { propertiesMode == .full }
    private var revealingProperties: Bool {
        !model.workspaceHidden && !model.visibilityAnimating && (settledPropertiesMode.map { $0 != propertiesMode } ?? false)
    }
    private var activeRailTool: Tool {
        ToolbarToolLayout.identity(for: model.tool, in: items)
    }
    private var zoomFailure: String? {
        if case .failed(let message) = model.zoomStartState { return message }
        return nil
    }
    private var needsCaptureRecovery: Bool { model.censorFallbackActive || model.captureFailureMessage != nil || zoomFailure != nil }

    var body: some View {
        VStack(spacing: 8) {
            if vertical {
                HStack(alignment: .top, spacing: 8) {
                    verticalRail.frame(width: metrics.buttonWidth).padding(metrics.padding)
                        .environment(\.layoutDirection, .leftToRight)
                        .modifier(ToolbarSurface())
                        .accessibilityElement(children: .contain)
                        .accessibilityLabel("Drawing tool rail")
                    if showsModeStrip {
                        activeModeStrip.padding(12).modifier(ToolbarSurface())
                            .environment(\.layoutDirection, .leftToRight)
                    }
                    if expanded {
                        VStack(alignment: .leading, spacing: 8) {
                            toolTitle
                            if pinned {
                                CompactInspectorViewport(model: model, width: verticalPropertyWidth - 24,
                                                         limit: max(64, availableSize.height - 50), compact: true)
                            } else {
                                quickPreferences.frame(width: verticalPropertyWidth - 24)
                            }
                        }
                        .padding(12)
                        .frame(width: verticalPropertyWidth)
                        .fixedSize(horizontal: false, vertical: true)
                        .modifier(ToolbarSurface())
                        .accessibilityElement(children: .contain)
                        .accessibilityLabel("Attached tool properties")
                        .accessibilityIdentifier("attached-tool-properties")
                        .environment(\.layoutDirection, .leftToRight)
                        .modifier(PropertiesReveal(animated: revealingProperties))
                    }
                }
                .environment(\.layoutDirection,
                    (dockEdgeOverride ?? model.preferences.toolbarDockEdge) == .right ? .rightToLeft : .leftToRight)
            } else {
            HStack(spacing: metrics.spacing) {
                GeometryReader { proxy in
                        HStack(spacing: 2) {
                            let visible = ToolbarToolLayout.visible(items, selected: .tool(activeRailTool),
                                                                    width: proxy.size.width, size: model.toolbarSize)
                            ForEach(Array(visible.enumerated()), id: \.element) { index, tool in
                                if index > 0 && ToolbarToolLayout.family(tool) != ToolbarToolLayout.family(visible[index - 1]) {
                                    Divider().frame(height: metrics.minimal ? 20 : 24).padding(.horizontal, metrics.dividerPadding)
                                }
                                itemButton(tool)
                            }
                        }
                }.frame(width: toolRailWidth, height: metrics.buttonHeight)
                .animation(selectionAnimation, value: model.tool)
                .animation(selectionAnimation, value: model.mode)
                moreMenu
                Divider().frame(height: metrics.minimal ? 22 : 28)
                boardMenu
                modeButton
                if !compactRail {
                    historyActions
                    propertiesButton
                    action("Settings and shortcuts", icon: "gearshape") { model.openPreferences() }
                    action("Hide drawings and tools", icon: "xmark") { model.onHideToolbar?() }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if compactRail {
                if expanded && !pinned { Divider() }
                HStack(spacing: metrics.spacing) {
                    if expanded && !pinned { compactQuickPreferences }
                    Spacer(minLength: 0)
                    historyActions
                    propertiesButton
                    action("Settings and shortcuts", icon: "gearshape") { model.openPreferences() }
                    action("Hide drawings and tools", icon: "xmark") { model.onHideToolbar?() }
                }
            }
            if showsModeStrip { activeModeStrip }
            if expanded && !pinned && !compactRail {
                Divider()
                HStack(spacing: 10) {
                    quickPreferences
                    if !compactRail {
                        if model.selectionCount > 0 { selectionMenu }
                        if !model.hasReferenceImage { backgroundMenu; zoomMenu }
                    }
                }
                .modifier(PropertiesReveal(animated: revealingProperties))
            }
            if pinned {
                Divider()
                CompactInspectorViewport(model: model, width: width - 24,
                    limit: max(64, availableSize.height - 100 - (compactRail ? 48 : 24) - (showsModeStrip ? 52 : 0)),
                    compact: width < 620, scale: width < 620 ? 1 : metrics.propertiesScale)
                    .modifier(PropertiesReveal(animated: revealingProperties))
                if !compactRail { fullPropertiesMenus }
            }
            if model.isConstructingArrow {
                HStack(spacing: 8) {
                    Button("Done") { model.active?.canvas.finishLinearPath() }
                    Button("Cancel") { model.active?.canvas.cancelGesture(); model.synchronize() }
                    Text("Return finishes · Escape cancels").font(.system(size: 10)).foregroundStyle(.secondary)
                    Spacer()
                }.controlSize(.small)
            }
            if needsCaptureRecovery {
                HStack(spacing: 8) {
                    Label(zoomFailure ?? (model.captureFailureMessage == nil ? "Pixel effects are covered solidly until capture is ready." :
                            "Live pixels are unavailable. Details are in Settings."), systemImage: "exclamationmark.shield")
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                    Spacer()
                }
            }
            }
        }
        .id(vertical)
        .padding(vertical ? 0 : metrics.padding)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        .background {
            ToolbarDragBackground(model: model)
        }
        .background {
            if !vertical { RoundedRectangle(cornerRadius: metrics.cornerRadius).fill(Color(nsColor: .windowBackgroundColor)) }
        }
        .overlay {
            if !vertical { RoundedRectangle(cornerRadius: metrics.cornerRadius).strokeBorder(Color.primary.opacity(0.07)) }
        }
        .allowsHitTesting(!model.visibilityAnimating)
        .accessibilityHidden(model.visibilityAnimating)
        .background {
            GeometryReader { proxy in Color.clear.preference(key: ToolbarMeasuredSize.self, value: proxy.size) }
        }
        .onPreferenceChange(ToolbarMeasuredSize.self) { size in
            let orientation = vertical
            Task { @MainActor in
                guard orientationOverride == nil,
                      orientation == (showInspector && model.preferences.toolbarOrientation == .vertical) else { return }
                model.onToolbarSizeChanged?(size)
            }
        }
        .modifier(SupportReminderPresentation(model: model, reminder: model.supportReminder))
        .onChange(of: model.workspaceHidden) { _, hidden in
            if hidden { libraryOpen = false }
        }
        .onChange(of: propertiesMode) { _, mode in settledPropertiesMode = mode }
        .onAppear {
            settledPropertiesMode = propertiesMode
            let action = openSettings
            if !model.hasReferenceImage { model.onOpenPreferences = { action() } }
        }
    }

    private var activeModeStrip: some View {
        let layout = vertical ? AnyLayout(VStackLayout(spacing: 6)) : AnyLayout(HStackLayout(spacing: 6))
        return layout {
            if let pendingZoom {
                PendingZoomControl(presentation: pendingZoom, compact: compactRail) { model.cancelPendingZoom() }
            }
            ForEach(activeModes, id: \.self) { mode in
                Button { mode.stop(in: model) } label: {
                    Group {
                        if compactRail {
                            VStack(spacing: 3) {
                                HStack(spacing: 4) {
                                    Image(systemName: mode.symbol)
                                    Image(systemName: "xmark").font(.system(size: 8, weight: .bold))
                                }
                                Text("\(mode.shortTitle) on").font(.system(size: 9, weight: .medium))
                            }
                        } else {
                            HStack(spacing: 6) {
                                Image(systemName: mode.symbol)
                                Text(mode.title).font(.system(size: 11, weight: .medium))
                                Image(systemName: "xmark").font(.system(size: 9, weight: .bold))
                            }
                        }
                    }
                    .foregroundStyle(Color.accentColor)
                    .frame(width: compactRail ? 44 : 130, height: 44)
                    .background(Color.accentColor.opacity(0.13), in: RoundedRectangle(cornerRadius: 8))
                    .contentShape(RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain)
                .help("\(mode.title) is active. Click to stop.")
                .accessibilityLabel("Stop \(mode.title.lowercased())")
                .accessibilityValue("Active")
                .accessibilityHint(mode == .whiteboard || mode == .blackboard ? "Return to the desktop background" : "Turn off \(mode.title.lowercased())")
                .accessibilityIdentifier("stop-active-\(mode.rawValue)")
            }
            if model.screenZoomEnabled && pendingZoom == nil {
                LiveZoomControls(model: model, compact: compactRail)
            }
        }
        .frame(maxWidth: vertical ? 44 : .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Active and pending modes")
    }

    /// The quick row's menus, kept under full properties so Focus and cleanup and Zoom stay one click away now that full
    /// properties are the default.
    private var fullPropertiesMenus: some View {
        HStack(spacing: 10) {
            if model.selectionCount > 0 { selectionMenu }
            if !model.hasReferenceImage { backgroundMenu; zoomMenu }
        }
        .frame(height: 16)
        .frame(maxWidth: .infinity, alignment: .trailing)
    }

    private var propertiesButton: some View {
        Button {
            propertiesMode.next.apply(to: model)
        } label: {
            Image(systemName: "slider.horizontal.3")
                .frame(width: metrics.actionSize.width, height: metrics.actionSize.height)
                .foregroundStyle(expanded ? Color.accentColor : .primary)
                // The whole cell takes the click, not only the symbol's lines.
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("\(propertiesMode.rawValue). Show \(propertiesMode.next.rawValue.lowercased()). Cycles quick controls, full properties and tools only." +
              (model.shortcut(for: .action(.properties)).map { " Key: \($0.label)." } ?? ""))
        .accessibilityLabel("Properties")
        .accessibilityValue(propertiesMode.rawValue)
        .accessibilityHint("Show \(propertiesMode.next.rawValue.lowercased())")
        .accessibilityIdentifier("toolbar-properties")
    }

    private var quickPreferences: some View {
        Group {
            if model.effectiveTool.isProtection {
                CoverPropertiesView(model: model, compact: compactRail)
            } else if model.effectiveTool == .counter {
                HStack(spacing: 8) {
                    InspectorColorPicker(model: model, compact: true)
                    StyleValueControl(model: model, parameter: .opacity, sliderWidth: compactRail ? 0 : 90)
                }
            } else if compactRail {
                HStack(spacing: 12) {
                    toolTitle
                    Spacer(minLength: 0)
                    if model.effectiveTool == .cover {
                        Text("100% opaque").font(.caption).foregroundStyle(.secondary)
                    } else if model.effectiveTool == .select && model.selectionCount == 0 {
                        Text("Select to edit").font(.caption).foregroundStyle(.secondary)
                    } else {
                        if model.selectionSupportsFill {
                            InspectorColorPicker(model: model, fill: true, compact: true)
                        }
                        StyleValueControl(model: model, parameter: quickParameter, sliderWidth: 0)
                    }
                }
            } else {
            HStack(spacing: 16) {
                toolTitle.frame(minWidth: 54, alignment: .leading)
                if model.effectiveTool.isPixelEffect {
                    StyleValueControl(model: model, parameter: .effect)
                } else if model.effectiveTool == .image {
                    StyleValueControl(model: model, parameter: .opacity)
                } else if model.effectiveTool == .cover {
                    Text("100% opaque").font(.caption).foregroundStyle(.secondary)
                        .help("Select this region to move, resize or delete it.")
                } else if model.effectiveTool == .select && model.selectionCount == 0 {
                    Text("Select any object to move, edit or delete it.").font(.caption).foregroundStyle(.secondary)
                } else {
                    if model.effectiveTool != .eraser {
                        HStack(spacing: 10) {
                            InspectorColorPicker(model: model, compact: true)
                            if model.selectionSupportsFill { InspectorColorPicker(model: model, fill: true, compact: true) }
                        }.frame(width: 138, alignment: .leading)
                    }
                    if model.effectiveTool == .text {
                        StyleValueControl(model: model, parameter: .textSize)
                    } else {
                        StyleValueControl(model: model, parameter: .width)
                    }
                    if model.effectiveTool == .pen {
                        SmartDrawButton(model: model, compact: true)
                    }
                }
            }
            }
        }.frame(height: 34).disabled(model.selectionStyleIsLocked)
    }

    private var toolTitle: some View {
        HStack(spacing: 4) {
            Text(model.effectiveTool.label).font(.system(size: 11, weight: .semibold)).lineLimit(1)
            if model.sharesToolStyles && model.effectiveTool.sharesStyle {
                Image(systemName: "link").font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
                    .help("All drawing tools share this style. You can turn this off in Settings > General.")
                    .accessibilityLabel("Style shared by all drawing tools")
            }
        }
    }

    private var clearHelp: String {
        "Clear all drawings — undoable" + (model.shortcut(for: .action(.clearAll)).map { " (\($0.label))" } ?? "")
    }

    private var compactQuickPreferences: some View {
        Group {
            if model.effectiveTool.isProtection {
                CoverPropertiesView(model: model, compact: true, showsSlider: width >= 500)
            } else if model.effectiveTool == .select && model.selectionCount == 0 {
                Text("Select to edit").font(.caption).foregroundStyle(.secondary)
            } else {
                HStack(spacing: 6) {
                    if model.effectiveTool == .counter { InspectorColorPicker(model: model, compact: true, showLabel: false) }
                    if model.selectionSupportsFill { InspectorColorPicker(model: model, fill: true, compact: true, showLabel: false) }
                    StyleValueControl(model: model, parameter: quickParameter, sliderWidth: 0, showLabel: false)
                }
            }
        }.disabled(model.selectionStyleIsLocked)
    }

    private var quickParameter: StyleParameter {
        if model.effectiveTool.isPixelEffect { return .effect }
        if model.effectiveTool == .image || model.effectiveTool == .counter { return .opacity }
        if model.effectiveTool == .text { return .textSize }
        return .width
    }

    private var verticalRail: some View {
        VStack(spacing: availableSize.height < 500 || metrics.minimal ? 3 : 5) {
            VStack(spacing: 3) {
                ForEach(verticalItems, id: \.self) { itemButton($0) }
            }.frame(width: metrics.buttonWidth)
            .animation(selectionAnimation, value: model.tool)
            .animation(selectionAnimation, value: model.mode)
            Divider()
            propertiesButton
            boardMenu
            modeButton
            zoomMenu
            let history = metrics.minimal ? AnyLayout(VStackLayout(spacing: 3)) : AnyLayout(HStackLayout(spacing: 0))
            history {
                action("Undo", icon: "arrow.uturn.backward", enabled: model.canUndo) { model.undo() }
                action("Redo", icon: "arrow.uturn.forward", enabled: model.canRedo) { model.redo() }
            }
            moreMenu
            if needsCaptureRecovery {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 12)).frame(width: 44, height: 28)
                    .foregroundStyle(.orange)
                    .help(zoomFailure ?? "Live pixels are unavailable. Details are in Settings.")
                    .accessibilityLabel("Live pixels unavailable")
            }
            action("Clear all drawings — undoable", help: clearHelp, icon: "trash", enabled: model.canClearCanvas) { model.clearCanvas() }
            action("Settings and shortcuts", icon: "gearshape") { model.openPreferences() }
            action("Hide drawings and tools", icon: "xmark") { model.onHideToolbar?() }
        }
    }

    private var moreMenu: some View {
        Button {
            if model.onOpenToolLibrary != nil { model.openToolLibrary() }
            else { libraryOpen.toggle() }
        } label: {
            VStack(spacing: 2) {
                Image(systemName: "square.grid.2x2").font(.system(size: metrics.minimal ? 16 : 19))
                    .frame(height: metrics.minimal ? 20 : 24)
                if metrics.showsKeyLabels {
                    Text(model.toolLibraryShortcut?.label ?? " ").font(.system(size: 10)).foregroundStyle(.secondary)
                }
            }
            .frame(width: metrics.buttonWidth, height: metrics.buttonHeight)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain).accessibilityLabel("Tool library")
        .help(model.toolLibraryShortcut.map { "More shapes and tools (\($0.label))" } ??
              "More shapes and tools. Command-L is assigned to a global shortcut.")
        .popover(isPresented: $libraryOpen) {
            ToolLibraryPanel(model: model, section: $librarySection, dismiss: { libraryOpen = false },
                             maximumHeight: max(240,min(420,availableSize.height-32)))
        }
    }

    private var zoomMenu: some View {
        Menu {
            Button(model.magnifierEnabled ? "Stop lens" : "Pointer lens") { model.toggleMagnifier() }
            Button(model.screenZoomEnabled ? "Stop screen zoom" : "Full-screen zoom") { model.toggleScreenZoom() }
            ForEach([2.0, 3, 4], id: \.self) { level in
                Button("\(Int(level))×") { model.magnification = level }
            }
            if model.magnifierEnabled || model.screenZoomEnabled {
                Divider()
                Button("Stop zoom") { model.stopZoom() }
            }
        } label: {
            Self.menuSymbol("plus.magnifyingglass", named: "Zoom tools")
                .foregroundStyle(model.magnifierEnabled || model.screenZoomEnabled ? Color.accentColor : .primary)
                .frame(width: 25, height: 26)
        }
        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        .help("Lens and full-screen zoom").accessibilityLabel("Zoom tools")
    }

    @ViewBuilder private func itemButton(_ item: ShortcutCommand) -> some View {
        switch item {
        case .tool(let tool): toolButton(tool)
        case .action(let action): actionButton(action)
        }
    }

    /// Effects light up for any effect, unless that effect has its own button.
    private func isCurrent(_ tool: Tool, represented: Tool) -> Bool {
        model.tool == represented || (tool == .cover && model.tool.isProtection && !tools.contains(model.tool))
    }

    private func actionButton(_ action: ShortcutAction) -> some View {
        let on = model.isOn(action)
        let key = model.shortcut(for: .action(action))?.label ?? ""
        let radius: CGFloat = metrics.minimal ? 8 : 10
        return Button { model.performShortcutAction(action) } label: {
            VStack(spacing: 2) {
                Image(systemName: action.symbol).font(.system(size: metrics.minimal ? 15 : 17))
                    .foregroundStyle(on ? Color.accentColor : .primary)
                    .frame(height: metrics.minimal ? 22 : 24)
                if metrics.showsKeyLabels {
                    Text(key.isEmpty ? " " : key).font(.system(size: 10)).foregroundStyle(.secondary)
                }
            }
            .frame(width: metrics.buttonWidth, height: metrics.buttonHeight)
            .background(on ? Color.accentColor.opacity(0.17) : .clear, in: RoundedRectangle(cornerRadius: radius))
            .contentShape(RoundedRectangle(cornerRadius: radius))
            .animation(selectionAnimation, value: on)
        }
        .buttonStyle(.plain)
        .help(key.isEmpty ? "\(action.label). \(action.detail)" : "\(action.label) (\(key)). \(action.detail)")
        .accessibilityLabel(action.label)
        .accessibilityValue(on ? "On" : "Off")
        .accessibilityHint(key.isEmpty ? action.detail : "\(action.detail) Key \(key).")
        .accessibilityAddTraits(on ? .isSelected : [])
        .accessibilityIdentifier("toolbar-action-\(action.rawValue)")
    }

    /// One shared highlight that slides between tools instead of jumping.
    private func selectionMarker(radius: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: radius)
            .fill(Color.accentColor.opacity(model.mode == .draw ? 0.17 : 0))
            .overlay {
                RoundedRectangle(cornerRadius: radius)
                    .strokeBorder(Color.secondary.opacity(model.mode == .draw ? 0 : 0.3))
            }
            .matchedGeometryEffect(id: "tool-selection", in: selectionSpace)
    }

    private func toolButton(_ tool: Tool) -> some View {
        let diagram = model.tool.isShape && ![Tool.rectangle, .ellipse, .diamond].contains(model.tool)
        let represented = tool == .rectangle && diagram && !tools.contains(model.tool) ? model.tool : tool
        let shortcut = model.shortcutLabel(for: represented)
        let radius: CGFloat = metrics.minimal ? 8 : 10
        return Button {
            if tool == .cover { model.chooseCover() } else { model.choose(represented) }
        } label: {
            VStack(spacing: 2) {
                ToolGlyph(tool: represented, size: metrics.glyphSize).frame(height: metrics.minimal ? 22 : 24)
                if metrics.showsKeyLabels {
                    Text(shortcut.isEmpty ? " " : shortcut).font(.system(size: 10)).foregroundStyle(.secondary)
                }
            }
            .frame(width: metrics.buttonWidth, height: metrics.buttonHeight)
            .background {
                if isCurrent(tool, represented: represented) { selectionMarker(radius: radius) }
            }
            .contentShape(RoundedRectangle(cornerRadius: radius))
        }
        .buttonStyle(.plain)
        .help(tool == .cover ? "Effects (\(shortcut)) — Blur, Pixelate or Distort" :
                shortcut.isEmpty ? represented.label : "\(represented.label) (\(shortcut))")
        .accessibilityLabel(tool == .cover ? "Effects" : represented.label)
        .accessibilityHint(shortcut.isEmpty ? "" : "Shortcut \(shortcut)")
        .accessibilityAddTraits(isCurrent(tool, represented: represented) ? .isSelected : [])
    }

    private var selectionActions: some View {
        SelectionCommands(model: model)
    }

    /// A menu's symbol with its own accessibility description. AppKit names a menu button after its image,
    /// so a plain `Image(systemName:)` would read as the symbol ("zoom in") whatever label the menu has.
    private static func menuSymbol(_ name: String, named label: String) -> Image {
        Image(nsImage: NSImage(systemSymbolName: name, accessibilityDescription: label) ?? NSImage())
            .renderingMode(.template)
    }

    private var selectionMenu: some View {
        Menu { selectionActions } label: {
            Self.menuSymbol("square.on.square", named: "Selection actions").frame(width: 25, height: 26)
        }.menuStyle(.borderlessButton).fixedSize()
            .disabled(model.selectionCount == 0).accessibilityLabel("Selection actions")
    }

    private var backgroundMenu: some View {
            Menu {
                Toggle("Spotlight", isOn: $model.spotlight)
                Toggle("Cursor halo", isOn: $model.cursorHalo)
                Divider()
                Button("Clear drawings (keep effects)") { model.clearDrawingsKeepingProtection() }
                Button("Remove all censors") { model.clearProtection() }
            } label: {
                Self.menuSymbol("light.beacon.max", named: "Focus and cleanup").frame(width: 25, height: 26)
            }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize().accessibilityLabel("Focus and cleanup")
    }

    private var boardMenu: some View {
        Button {
            model.setBoard(model.board == .transparent ? .light : model.board == .light ? .dark : .transparent)
        } label: {
            VStack(spacing: 3) {
                Image(systemName: model.board == .transparent ? "desktopcomputer" : model.board == .light ? "rectangle" : "rectangle.fill")
                    .font(.system(size: metrics.minimal ? 15 : 17))
                if !metrics.minimal {
                    Text(model.board == .transparent ? "Desk" : model.board == .light ? "White" : "Black")
                        .font(.system(size: 9, weight: .medium))
                }
            }
            .frame(width: metrics.boardWidth, height: vertical ? (metrics.minimal ? 30 : 36) : metrics.buttonHeight)
            .background(model.board == .transparent ? Color.clear : Color.accentColor.opacity(0.15),
                        in: RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain).fixedSize()
        .accessibilityLabel("Board background")
        .accessibilityValue(model.board.rawValue)
        .help("Cycle Desktop, Whiteboard and Blackboard on this drawing display. Control-W: whiteboard. Control-B: blackboard." +
              (model.shortcut(for: .action(.board)).map { " Key: \($0.label)." } ?? ""))
    }

    private var modeButton: some View {
        Button {
            if model.mode == .draw { model.interact() } else { model.resumeDrawing() }
        } label: {
            VStack(spacing: metrics.minimal ? 1 : 3) {
                Image(systemName: model.mode == .draw ? "pencil.tip" : "cursorarrow")
                    .font(.system(size: metrics.minimal ? 14 : 17))
                    .contentTransition(selectionAnimation == nil || reduceMotion ? .identity : .symbolEffect(.replace))
                HStack(spacing: metrics.minimal ? 3 : 4) {
                    Circle().fill(model.mode == .draw ? Color.green : Color.secondary).frame(width: 5, height: 5)
                        .phaseAnimator([1.0, 1.6, 1.0], trigger: drawPulse) { dot, scale in
                            dot.scaleEffect(reduceMotion ? 1 : scale)
                        } animation: { scale in scale > 1 ? .easeOut(duration: 0.12) : Motion.playful }
                    Text(model.mode == .draw ? "Draw" : "Apps").font(.system(size: metrics.minimal ? 8 : 9, weight: .semibold))
                }
            }
                .frame(width: vertical ? metrics.buttonWidth : metrics.modeWidth, height: vertical ? 36 : metrics.buttonHeight)
                .background(model.mode == .draw ? Color.green.opacity(0.15) : Color.secondary.opacity(0.1),
                            in: RoundedRectangle(cornerRadius: 7))
                .animation(selectionAnimation, value: model.mode)
        }.buttonStyle(.plain)
            .onChange(of: model.mode) { _, mode in
                if mode == .draw && !model.workspaceHidden && !model.visibilityAnimating { drawPulse &+= 1 }
            }
            .help(model.mode == .draw ?
                "Drawing is active. Click Use apps to pass clicks through. Escape finishes text or cancels an unfinished drawing first. \((model.preferences.resumeShortcut ?? .resumeDrawing).label) returns to drawing." :
                "Your apps receive input. Click Draw or press \((model.preferences.resumeShortcut ?? .resumeDrawing).label).")
            .accessibilityLabel(model.mode == .draw ? "Use apps" : "Resume drawing")
            .accessibilityValue(model.mode == .draw ? "Drawing is active" : "Apps receive input")
    }

    private var verticalItems: [ShortcutCommand] {
        let recoveryHeight: CGFloat = needsCaptureRecovery ? 64 : 0
        let count = max(1, min(items.count, Int(max(48, availableSize.height - metrics.verticalReserve - recoveryHeight) /
                                                 metrics.verticalToolPitch)))
        let activeIndex = items.firstIndex(of: .tool(activeRailTool)) ?? 0
        let start = max(0, min(activeIndex - count + 1, items.count - count))
        return Array(items.dropFirst(start).prefix(count))
    }

    private var historyActions: some View {
        Group {
            action("Undo", icon: "arrow.uturn.backward", enabled: model.canUndo) { model.undo() }
            action("Redo", icon: "arrow.uturn.forward", enabled: model.canRedo) { model.redo() }
            action("Clear all drawings — undoable", help: clearHelp, icon: "trash", enabled: model.canClearCanvas) { model.clearCanvas() }
        }
    }

    private func action(_ name: String, help: String? = nil, icon: String, enabled: Bool = true,
                        perform: @escaping () -> Void) -> some View {
        Button(action: perform) { Image(systemName: icon).frame(width: metrics.actionSize.width, height: metrics.actionSize.height) }
            .buttonStyle(.plain).disabled(!enabled).help(help ?? name).accessibilityLabel(name)
    }
    private func symbol(_ tool: Tool) -> String {
        tool.toolbarSymbol
    }
}

private struct ToolbarSurface: ViewModifier {
    func body(content: Content) -> some View {
        content
            .background(Color(nsColor: .windowBackgroundColor), in: RoundedRectangle(cornerRadius: 15))
            .overlay { RoundedRectangle(cornerRadius: 15).strokeBorder(Color.primary.opacity(0.07)) }
    }
}

private struct ToolbarDragBackground: View {
    let model: WorkspaceModel
    var body: some View {
        Color.clear.contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 3, coordinateSpace: .global)
                .onChanged { value in model.onToolbarBackgroundDrag?(value.translation, false) }
                .onEnded { value in model.onToolbarBackgroundDrag?(value.translation, true) })
    }

}

private struct SupportReminderPresentation: ViewModifier {
    @ObservedObject var model: WorkspaceModel
    @ObservedObject var reminder: SupportReminder

    func body(content: Content) -> some View {
        content.popover(isPresented: Binding(
            get: { reminder.available && model.canShowSupportReminder },
            set: { if !$0 && reminder.available { reminder.snooze() } })) {
                SupportReminderView(model: model, reminder: reminder)
            }
    }
}

/// Takes `scale` times its content's room; the content, drawn with the same scaleEffect, fills it from the top leading corner.
private struct ScaledPropertiesLayout: Layout {
    let scale: CGFloat

    private func contentSize(_ view: LayoutSubview, width: CGFloat?, height: CGFloat?) -> CGSize {
        view.sizeThatFits(ProposedViewSize(width: width.map { $0 / scale }, height: height.map { $0 / scale }))
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let view = subviews.first else { return .zero }
        let size = contentSize(view, width: proposal.width, height: proposal.height)
        return CGSize(width: size.width * scale, height: size.height * scale)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let view = subviews.first else { return }
        let size = contentSize(view, width: bounds.width, height: bounds.height)
        view.place(at: bounds.origin, anchor: .topLeading, proposal: ProposedViewSize(size))
    }
}

private struct CompactInspectorViewport: NSViewRepresentable {
    @ObservedObject var model: WorkspaceModel
    let width: CGFloat
    let limit: CGFloat
    var compact = false
    /// Below 1, the panel is laid out `1 / scale` times wider and drawn `scale` times smaller (minimal toolbar).
    var scale: CGFloat = 1
    struct Target: Equatable {
        let session: ObjectIdentifier?
        let selection: Set<UUID>
        let tool: Tool
        let width: CGFloat
        let compact: Bool
        let scale: CGFloat
    }

    private var target: Target {
        Target(session: model.active.map(ObjectIdentifier.init),
               selection: model.active?.document.selection ?? [], tool: model.effectiveTool,
               width: width, compact: compact, scale: scale)
    }

    private var contentWidth: CGFloat {
        max(1, width - NSScroller.scrollerWidth(for: .small, scrollerStyle: .legacy))
    }

    private var content: AnyView {
        let inspector = ContextualInspector(model: model, compact: compact)
            .frame(width: contentWidth / scale, alignment: .topLeading)
            .fixedSize(horizontal: false, vertical: true)
            .background { ToolbarDragBackground(model: model) }
        guard scale < 1 else { return AnyView(inspector) }
        return AnyView(ScaledPropertiesLayout(scale: scale) { inspector.scaleEffect(scale, anchor: .topLeading) })
    }

    func makeNSView(context: Context) -> InspectorScrollView {
        InspectorScrollView(content: content)
    }
    func updateNSView(_ view: InspectorScrollView, context: Context) {
        view.update(content: content, target: target)
    }
    func sizeThatFits(_ proposal: ProposedViewSize, nsView: InspectorScrollView, context: Context) -> CGSize? {
        CGSize(width: width, height: min(limit, max(1, nsView.host.fittingSize.height)))
    }

    final class InspectorScrollView: NSScrollView {
        let document: InspectorDocumentView
        var host: NSHostingView<AnyView> { document.host }
        private var target: Target?
        private var resetToTop = true

        init(content: AnyView) {
            document = InspectorDocumentView(content: content)
            super.init(frame: .zero)
            identifier = NSUserInterfaceItemIdentifier("contextual-inspector-viewport")
            drawsBackground = false
            borderType = .noBorder
            hasVerticalScroller = true
            autohidesScrollers = false
            scrollerStyle = .legacy
            verticalScroller?.controlSize = .small
            documentView = document
        }
        required init?(coder: NSCoder) { nil }

        func update(content: AnyView, target: Target) {
            if self.target != target {
                resetToTop = true
                // Reused SwiftUI hosts can report the previous tool's intrinsic size for one update.
                document.replaceHost(content: content)
            } else {
                host.rootView = content
            }
            self.target = target
            host.invalidateIntrinsicContentSize()
            document.invalidateIntrinsicContentSize()
            invalidateIntrinsicContentSize()
            needsLayout = true
        }

        override func layout() {
            super.layout()
            let size = CGSize(width: contentSize.width, height: max(1, host.fittingSize.height))
            if document.frame.size != size { document.setFrameSize(size) }
            if host.frame != CGRect(origin: .zero, size: size) { host.frame = CGRect(origin: .zero, size: size) }
            host.layoutSubtreeIfNeeded()
            let maximumOffset = max(0, size.height - contentView.bounds.height)
            let y = resetToTop ? 0 : min(maximumOffset, max(0, contentView.bounds.minY))
            contentView.scroll(to: CGPoint(x: 0, y: y))
            reflectScrolledClipView(contentView)
            resetToTop = false
        }
    }

    final class InspectorDocumentView: NSView {
        private(set) var host: NSHostingView<AnyView>
        override var isFlipped: Bool { true }
        override var intrinsicContentSize: NSSize { host.fittingSize }

        init(content: AnyView) {
            host = InspectorHostingView(rootView: content)
            super.init(frame: .zero)
            addSubview(host)
        }
        required init?(coder: NSCoder) { nil }

        func replaceHost(content: AnyView) {
            host.removeFromSuperview()
            host = InspectorHostingView(rootView: content)
            addSubview(host)
        }
    }

    final class InspectorHostingView: NSHostingView<AnyView> {
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    }
}

/// Fades newly opened properties in just after the panel grows, so the change reads as one movement.
/// Only a change the person makes animates; the first appearance and snapshots show content at once.
private struct PropertiesReveal: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var shown: Bool

    init(animated: Bool) { _shown = State(initialValue: !animated) }

    func body(content: Content) -> some View {
        content
            .opacity(shown ? 1 : 0)
            .offset(y: shown || reduceMotion ? 0 : -4)
            .onAppear {
                guard !shown else { return }
                withAnimation(reduceMotion ? Motion.reduced : Motion.fadeIn.delay(0.04)) { shown = true }
            }
    }
}
