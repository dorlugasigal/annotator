import AppKit
import SwiftUI
import AnnotationCore
import NativePlatform

enum CustomizationTab: String, CaseIterable {
    case general, toolbar, pointer, breakTimer, permissions, help, about
}

struct SettingsToolLabel: View {
    let tool: Tool
    var title: String { tool == .cover ? "Effects" : tool.label }
    var symbol: String { tool.toolbarSymbol }

    var body: some View {
        Label {
            Text(title)
        } icon: {
            ToolGlyph(tool: tool, size: 16).frame(width: 20)
        }
        .labelStyle(.titleAndIcon)
    }
}

struct CustomizationView: View {
    @ObservedObject var model: WorkspaceModel
    var showDone = true
    @State private var screenAccess = false
    @State private var zoomAccess = false
    private let initialTab: CustomizationTab?

    init(model: WorkspaceModel, showDone: Bool = true, initialTab: CustomizationTab? = nil) {
        self.model = model
        self.showDone = showDone
        self.initialTab = initialTab
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            TabView(selection: $model.settingsTab) {
                general.tabItem { Label("General", systemImage: "gearshape") }.tag(CustomizationTab.general)
                ToolbarShortcutSettings(model: model)
                    .tabItem { Label("Toolbar & Shortcuts", systemImage: "keyboard") }.tag(CustomizationTab.toolbar)
                CursorHighlightControls(model: model).tabItem { Label("Pointer", systemImage: "cursorarrow.rays") }.tag(CustomizationTab.pointer)
                BreakTimerSettingsView(model: model)
                    .tabItem { Label("Break timer", systemImage: "timer") }.tag(CustomizationTab.breakTimer)
                permissions.tabItem { Label("Permissions", systemImage: "lock.shield") }.tag(CustomizationTab.permissions)
                SettingsView(model: model).tabItem { Label("Help", systemImage: "questionmark.circle") }.tag(CustomizationTab.help)
                about.tabItem { Label("About", systemImage: "info.circle") }.tag(CustomizationTab.about)
            }
            .modifier(InlineSettingsTabs())
            .frame(minHeight: 440, maxHeight: .infinity)
            HStack {
                Menu("Profiles") {
                    Button("Export profile...") { model.exportPreferences() }
                    Button("Import profile...") { model.importPreferences() }
                }
                .fixedSize()
                .help("Profiles save settings and shortcuts. Import replaces these, not boards.")
                Spacer()
                if showDone { Button("Done") { model.showCustomization = false } }
            }
        }.padding(20)
            .frame(minWidth: 720, idealWidth: 840, maxWidth: .infinity,
                   minHeight: 620, idealHeight: 700, maxHeight: .infinity)
            .onAppear {
                if let initialTab { model.settingsTab = initialTab }
                model.settingsVisible = true
                refreshPermissions()
            }
            .onDisappear { model.settingsVisible = false }
            .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in refreshPermissions() }
            .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { _ in refreshPermissions() }
    }

    private var general: some View {
        VStack(spacing: 0) {
        Form {
            Section("Startup") {
                if let controller = model.effectiveLoginItemController {
                    LoginItemSettings(controller: controller)
                }
                Toggle("Show menu-bar icon", isOn: Binding(get: { model.preferences.showMenuBarIcon ?? true }, set: { model.setMenuBarVisible($0) }))
                    .help("If the menu bar is full, macOS may hide the icon. You can still show the tools with your Show / hide shortcut.")
                Button("Show tutorial") { model.onShowIntroduction?() }
            }
            Section("Drawing") {
                Toggle("Share style between tools", isOn: Binding(
                    get: { model.sharesToolStyles }, set: { model.setShareToolStyles($0) }))
                    .accessibilityIdentifier("share-tool-styles")
                Text("When this is on, changing the color, fill, opacity, width or line style of one tool changes it for all drawing tools. Effects, the eraser and images keep their own settings. Text size and arrowheads stay separate.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Appearance") {
                Picker("Theme", selection: Binding(
                    get: { model.preferences.theme ?? .system }, set: { model.setTheme($0) })) {
                    Text("System").tag(AppTheme.system)
                    Text("Light").tag(AppTheme.light)
                    Text("Dark").tag(AppTheme.dark)
                }
                .help("Changes app controls and windows, not your drawings.")
                Toggle("Show tips on screen", isOn: Binding(get: { model.preferences.showShortcutHints ?? true }, set: { model.setShortcutHints($0) }))
                    .help("Tips in the top-left corner show your keys and what to do next while you draw.")
                Picker("Workspace animation", selection: Binding(
                    get: { model.preferences.animateVisibility == false ? VisibilityAnimationStyle.none :
                        (model.preferences.visibilityAnimationStyle ?? .slide).resolved },
                    set: { model.setAnimationStyle($0) })) {
                    Text("Dissolve").tag(VisibilityAnimationStyle.dissolve)
                    Text("Gather").tag(VisibilityAnimationStyle.slide)
                    Text("Magic").tag(VisibilityAnimationStyle.magic)
                    Text("Corners").tag(VisibilityAnimationStyle.corners)
                    Text("Pop").tag(VisibilityAnimationStyle.pop)
                    Text("Drop").tag(VisibilityAnimationStyle.drop)
                    Text("Soft focus").tag(VisibilityAnimationStyle.focus)
                    Text("Fade").tag(VisibilityAnimationStyle.fade)
                    Text("Off").tag(VisibilityAnimationStyle.none)
                }
                .help("Choose how drawings and tools hide and return. Off turns animation off. When macOS Reduce Motion is on, a short fade replaces any movement.")
                Label("Hiding drawings and tools also hides covers.", systemImage: "eye.slash")
                    .font(.caption)
            }
            Section("Updates") {
                Button("Check for updates...") { model.checkForUpdates() }
                    .disabled(!model.canCheckForUpdates)
                Toggle("Automatically check for updates", isOn: Binding(
                    get: { model.automaticallyCheckForUpdates },
                    set: { model.onAutomaticUpdateChecks?($0) }))
                    .disabled(!model.updatesConfigured)
                Text(model.updateStatus).font(.caption).foregroundStyle(.secondary)
                DisclosureGroup("What an update check sends") {
                    Text("The release server gets connection details, including your IP address. Your drawings and screenshots are not sent.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section("Privacy") {
                    UsageMetricsSettings(metrics: model.usageMetrics)
                    Button("Privacy details") { model.openProductPage(.privacy) }.buttonStyle(.link)
                }
            }
        }.formStyle(.grouped)
            CoffeeSupportView(model: model).padding(16)
        }
    }

    private var about: some View {
        ScrollView {
            VStack(spacing: 16) {
                Image(nsImage: NSImage(named: "AppIcon") ?? NSApplication.shared.applicationIconImage)
                    .resizable().scaledToFit().frame(width: 96, height: 96)
                    .accessibilityLabel("Annotator app icon").accessibilityIdentifier("about-app-icon")
                VStack(spacing: 6) {
                    Text("Annotator").font(.largeTitle.bold())
                    Text("Version \(model.versionLabel)").foregroundStyle(.secondary)
                    if Bundle.main.bundleIdentifier?.hasSuffix(".dev") == true {
                        Text("Local build").font(.caption.weight(.medium))
                            .padding(.horizontal, 9).padding(.vertical, 4)
                            .background(Color.orange.opacity(0.14), in: Capsule())
                    }
                }
                Text("Draw on your desktop. Keep your app open.").font(.callout)
                HStack {
                    Button("Check for updates...") { model.checkForUpdates() }
                        .disabled(!model.canCheckForUpdates)
                    Button("Copy support info") { model.copySupportInfo() }
                }
                Text(model.updateStatus).font(.caption).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                if !model.updatesConfigured {
                    Button("Open Downloads page...") { model.openProductPage(.downloads) }
                        .buttonStyle(.link)
                }
                Divider()
                ReleaseNotesView().frame(maxWidth: .infinity, alignment: .leading)
                DisclosureGroup("Build & support details") {
                    VStack(alignment: .leading, spacing: 10) {
                        Text(model.buildLabel).font(.caption.monospaced()).textSelection(.enabled)
                        Text("Support info has app and system details, not drawings or screenshots. Review it before sharing.")
                            .font(.caption).foregroundStyle(.secondary)
                        Button("Save support info...") { model.saveSupportInfo() }
                        Text("© 2026 Dor Lugasi-Gal. Open source under the Apache License 2.0.")
                            .font(.caption).foregroundStyle(.secondary)
                    }.padding(.top, 8)
                }
            }.frame(maxWidth: 480).padding(28).frame(maxWidth: .infinity)
        }
    }

    private var permissions: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Label("Drawing needs no permissions. You're good to go.", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green).font(.headline)
                permissionCard("Screen Recording", symbol: "display", tint: .blue,
                               detail: "For zoom, live effects and region sharing. No video is recorded.",
                               status: !screenAccess ? "Not allowed" :
                                model.captureFailureMessage != nil ? "Access failed" :
                                model.captureVerified ? "Working" : "Allowed. Start zoom to check.") {
                    Button(screenAccess ? "Open Screen Recording settings" : "Allow screen access") {
                        if screenAccess { model.openCaptureSettings() }
                        else { model.requestCapturePermission() }
                        refreshPermissions()
                    }
                }
                if let failure = model.captureFailureMessage {
                    Label(failure, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red)
                        .font(.callout).textSelection(.enabled)
                }
                permissionCard("Accessibility", symbol: "cursorarrow.rays", tint: .orange,
                               detail: "For Option-scroll over other apps. Zoom buttons don't need this.",
                               status: zoomAccess ? "Allowed" : "Optional") {
                    Button(zoomAccess ? "Open Accessibility settings" : "Allow scroll gestures") {
                        if zoomAccess { model.openZoomGestureSettings() }
                        else { model.requestZoomGesturePermission() }
                        refreshPermissions()
                    }
                }
                Button("Refresh status", systemImage: "arrow.clockwise") { refreshPermissions() }
                    .buttonStyle(.borderless)
                DisclosureGroup("If access isn't working") {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Check the permission is for this copy of Annotator. Replace only an older Annotator entry; leave other apps alone.")
                        Text("Stop sharing before you quit: quitting removes covers. Save your board, reopen Annotator, then try zoom again.")
                        HStack { Text("Live zoom"); ShortcutKeycaps(shortcut: model.preferences.zoomShortcut ?? .liveZoom) }
                    }.font(.callout).padding(.top, 10)
                }.accessibilityIdentifier("permission-recovery")
                Label("Blur isn't secure redaction. App or window sharing may leave out covers.",
                      systemImage: "eye.trianglebadge.exclamationmark")
                    .font(.caption).foregroundStyle(.secondary)
            }.padding(22)
        }
    }

    private func permissionCard<Action: View>(_ title: String, symbol: String, tint: Color,
                                               detail: String, status: String,
                                               @ViewBuilder action: () -> Action) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Image(systemName: symbol).font(.title2).foregroundStyle(tint)
                    .frame(width: 44, height: 44).background(tint.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                VStack(alignment: .leading, spacing: 4) {
                    Text(title).font(.headline)
                    Text(detail).font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                action().tint(tint)
            }
            Label(status, systemImage: status == "Working" || status == "Allowed" ? "checkmark.circle.fill" : "info.circle")
                .font(.caption.weight(.medium)).foregroundStyle(tint)
        }.padding(.vertical, 12).frame(maxWidth: .infinity, alignment: .leading)
    }

    private func refreshPermissions() {
        screenAccess = DisplayCapture.hasPermission
        zoomAccess = ZoomGestureMonitor.hasPermission
        model.effectiveLoginItemController?.refresh()
        model.refreshZoomPermissions()
    }
}

private struct UsageMetricsSettings: View {
    @ObservedObject var metrics: UsageMetrics
    var body: some View {
        Toggle("Share usage counts", isOn: Binding(
            get: { metrics.enabled }, set: { metrics.setEnabled($0) }))
        Text(metrics.status).font(.caption).foregroundStyle(.secondary)
        DisclosureGroup("What's shared?") {
            Text("One daily summary: version, macOS major, chip, a random ID and broad feature counts. No drawings, screenshots, typed text or file names. Turning this off deletes the local ID and unsent counts.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// macOS 26.7 moves TabView tabs into the window's title bar, where six labeled tabs don't fit at the
/// default width and collapse behind "»". Keep them in the window as a row of tabs instead.
private struct InlineSettingsTabs: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) { content.tabViewStyle(.grouped) } else { content }
    }
}
