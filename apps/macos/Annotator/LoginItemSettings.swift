import SwiftUI

struct LoginItemSettings: View {
    @ObservedObject var controller: LoginItemController

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Toggle("Launch at login", isOn: Binding(
                get: { controller.state.isRequested },
                set: { enabled in Task { await controller.setEnabled(enabled) } }))
                .disabled(controller.isChanging || !controller.state.canChange)
                .accessibilityIdentifier("launch-at-login")
                .help("Start Annotator for this Mac account without opening the drawing toolbar.")
            if controller.isChanging {
                Text("Updating macOS Login Items...").font(.caption).foregroundStyle(.secondary)
            } else {
                Text(controller.state.message).font(.caption).foregroundStyle(.secondary)
            }
            if let error = controller.errorMessage {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            Button("Open Login Items...") { controller.openSystemSettings() }
                .disabled(controller.isChanging)
            Text("Managed by macOS for this account. Not included in exported profiles.")
                .font(.caption2).foregroundStyle(.secondary)
        }
        .onAppear { controller.refresh() }
    }
}
