import AppKit
import SwiftUI

enum FeedbackKind: String, CaseIterable, Identifiable {
    case issue = "Report a problem"
    case feedback = "Share feedback"
    var id: String { rawValue }
}

struct FeedbackDraft {
    var kind: FeedbackKind
    var title = ""
    var details = ""

    func report(version: String) throws -> String {
        guard !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !details.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw FeedbackFailure.empty
        }
        return "\(kind.rawValue)\nVersion: \(version)\n\n\(title)\n\n\(details)\n"
    }

    func githubURL(base: String?, version: String) throws -> URL {
        _ = try report(version: version)
        guard let baseURL = IssueReporting.url(base: base, version: version, feedback: kind == .feedback),
              var parts = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else {
            throw FeedbackFailure.unconfigured
        }
        var items = parts.queryItems ?? []
        items.removeAll { $0.name == "title" }
        items.append(URLQueryItem(name: "title", value: "[\(version)] \(title)"))
        items.append(URLQueryItem(name: kind == .issue ? "steps" : "experience", value: details))
        parts.queryItems = items
        guard let url = parts.url, url.absoluteString.utf8.count <= 8_000 else {
            throw FeedbackFailure.tooLongForLink
        }
        return url
    }

    @MainActor func copy(version: String, to pasteboard: NSPasteboard = .general) throws {
        let text = try report(version: version)
        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else { throw FeedbackFailure.clipboard }
    }
}

enum FeedbackFailure: Error, LocalizedError {
    case empty, unconfigured, tooLongForLink, clipboard, browser
    var errorDescription: String? {
        switch self {
        case .empty: "Add a short title and describe what happened."
        case .unconfigured: "Issue reporting is not set up in this copy. Choose Copy feedback to keep your report."
        case .tooLongForLink: "This report is too long for a browser draft. Choose Copy feedback, then Open GitHub form and paste the report. Review it before submitting."
        case .clipboard: "Feedback could not be copied. Try Copy feedback again."
        case .browser: "The feedback page could not open. Check your browser or copy the feedback instead."
        }
    }
}

struct FeedbackView: View {
    @ObservedObject var model: WorkspaceModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: FeedbackDraft
    @State private var error: String?
    @State private var copied = false
    @State private var showPlainForm = false

    init(model: WorkspaceModel, kind: FeedbackKind) {
        self.model = model
        _draft = State(initialValue: FeedbackDraft(kind: kind))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text(draft.kind.rawValue).font(.title2.bold())
                    Text("Version \(model.versionLabel)").font(.caption).foregroundStyle(.secondary)
                    Text("Title").font(.callout.bold())
                    TextField("Short title", text: $draft.title).textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Feedback title")
                    Text("Details").font(.callout.bold())
                    Text(draft.kind == .issue ? "What did you try, what did you expect, and what happened?" :
                            "What worked well? What would you change?").font(.callout)
                    TextEditor(text: $draft.details).frame(minHeight: 140, maxHeight: 220)
                        .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color.secondary.opacity(0.3)))
                        .accessibilityLabel("Feedback details")
                    Text("Remove private details before opening GitHub. Open GitHub draft sends your title, details and app version to GitHub in a browser link. Submitting it there makes it public. GitHub may ask you to sign in. No drawings, screenshots or logs are attached automatically.")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("Copy feedback copies the report without opening GitHub or submitting it.")
                        .font(.caption).foregroundStyle(.secondary)
                    if model.issueReportURL == nil {
                        Text("Issue reporting is not set up in this copy. You can still copy your report.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    if let error { Text(error).font(.caption).foregroundStyle(.red) }
                    if copied { Text("Feedback copied.").font(.caption).accessibilityIdentifier("feedback-copied") }
                    if showPlainForm {
                        Button("Open GitHub form") { model.reportIssue(feedback: draft.kind == .feedback) }
                            .accessibilityIdentifier("feedback-plain-form")
                        Text("This opens the form with your app version, without your title or details. Paste only what you want to share.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }.padding(20)
            }
            Divider()
            HStack {
                Button("Copy feedback") {
                    do { try draft.copy(version: model.versionLabel); copied = true; error = nil }
                    catch { self.error = error.localizedDescription }
                }
                Button("Open GitHub draft") {
                    showPlainForm = false
                    do {
                        let url = try draft.githubURL(
                            base: Bundle.main.object(forInfoDictionaryKey: "AnnotatorIssuesURL") as? String,
                            version: model.versionLabel)
                        guard model.openExternalURL(url) else { throw FeedbackFailure.browser }
                        error = nil
                    } catch FeedbackFailure.tooLongForLink {
                        error = FeedbackFailure.tooLongForLink.localizedDescription
                        showPlainForm = true
                    } catch { self.error = error.localizedDescription }
                }.disabled(model.issueReportURL == nil)
                Spacer()
                Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
            }.padding(20)
        }
        .frame(width: 460, height: min(680, max(300, (NSScreen.main?.visibleFrame.height ?? 780) - 100)))
        .onChange(of: draft.title) { _, _ in copied = false; showPlainForm = false }
        .onChange(of: draft.details) { _, _ in copied = false; showPlainForm = false }
    }
}
