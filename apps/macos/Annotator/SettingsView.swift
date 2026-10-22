import AppKit
import SwiftUI

struct SettingsView: View {
    @ObservedObject var model: WorkspaceModel
    @State private var feedback: FeedbackKind?
    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    HStack {
                        Button("Quick start", systemImage: "play.rectangle") { model.onShowIntroduction?() }
                            .buttonStyle(.borderedProminent)
                        Button("Online guide", systemImage: "book") { model.openProductPage(.help) }
                        Button("Shortcuts", systemImage: "keyboard") { model.openPreferences(tab: .toolbar) }
                    }
                    Divider()
                    DisclosureGroup("Screen-sharing limits") {
                        VStack(alignment: .leading, spacing: 10) {
                            Text("Sharing one app or window may leave out drawings and covers. Ask someone on the call to check. Hiding, quitting or a crash removes visible covers.")
                            Text("Covers don't follow windows or scrolling content. Blur can leave details readable. Remove private details before sharing; overlays aren't secure redaction.")
                            Button("Open Permissions") { model.openPreferences(tab: .permissions) }
                        }.padding(.top, 10)
                    }.accessibilityIdentifier("help-sharing")
                    DisclosureGroup("Save and share") {
                        Text("Boards keep original pasted images, even under covers. Don't share a board with private details. For a PNG, open the export and check it first; blur can still be readable.")
                            .padding(.top, 10)
                    }.accessibilityIdentifier("help-saving")
                    DisclosureGroup("Use an AI assistant") {
                        VStack(alignment: .leading, spacing: 12) {
                            Text("Your AI app needs computer-use tools and its own macOS permissions. The skill adds instructions, not those tools.")
                            Text("That app may send screenshots and text to its model provider. Check its privacy settings first.")
                            HStack {
                                Button("Save AI skill...") { model.exportComputerUseSkill() }
                                Button("Setup guide") { model.openProductPage(.ai) }
                            }
                        }.padding(.top, 10)
                    }.accessibilityIdentifier("help-ai")
                    Divider()
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Something we can improve?").font(.headline)
                        HStack {
                            Button("Report a problem...") { feedback = .issue }
                            Button("Share feedback...") { feedback = .feedback }
                        }
                        Text(model.issueReportURL == nil
                            ? "GitHub reporting isn't set up here. You can still copy a draft."
                            : "You'll write a draft first. Remove private details before opening GitHub; submitted issues are public.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Menu("Support info") {
                        Button("Copy support info") { model.copySupportInfo() }
                        Button("Save support info...") { model.saveSupportInfo() }
                    }.fixedSize()
                        .help("App and system details, not drawings or screenshots. Review before sharing.")
                }.font(.callout).padding(28)
            }
        }
        .sheet(item: $feedback) { kind in FeedbackView(model: model, kind: kind) }
    }
}
