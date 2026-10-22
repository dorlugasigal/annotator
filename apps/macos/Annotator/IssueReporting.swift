import AppKit

enum ProductPage: String, CaseIterable {
    case help = "docs/", downloads = "downloads/", ai = "ai/", privacy = "privacy/"
}

enum IssueReporting {
    static func url(base: String?, version: String, feedback: Bool = false) -> URL? {
        guard let base, !base.isEmpty, !base.contains("$("),
              var parts = URLComponents(string: base), parts.scheme == "https",
              parts.host == "github.com", parts.user == nil, parts.password == nil,
              parts.port == nil, parts.query == nil, parts.fragment == nil else { return nil }
        let path = parts.path.split(separator: "/")
        guard path.count == 4, path[2] == "issues", path[3] == "new",
              path.prefix(2).allSatisfy({ $0 != "." && $0 != ".." &&
                  $0.range(of: "^[A-Za-z0-9_.-]+$", options: .regularExpression) != nil }) else { return nil }
        parts.queryItems = [
            URLQueryItem(name: "template", value: feedback ? "feedback.yml" : "bug-report.yml"),
            URLQueryItem(name: "title", value: "[\(version)] "),
            URLQueryItem(name: "version", value: version)
        ]
        return parts.url
    }

    static func supportURL(_ value: String?) -> URL? {
        guard let value, !value.isEmpty, !value.contains("$("),
              let parts = URLComponents(string: value),
              parts.scheme == "https", parts.host?.isEmpty == false,
              parts.user == nil, parts.password == nil, parts.port == nil,
              parts.fragment == nil else { return nil }
        return parts.url
    }
}

extension WorkspaceModel {
    func openProductPage(_ page: ProductPage) {
        let configured = Bundle.main.object(forInfoDictionaryKey: "AnnotatorWebsiteURL") as? String ?? ""
        let base = configured.isEmpty || configured.contains("$(")
            ? "https://dorlugasigal.github.io/annotator" : configured
        guard let root = IssueReporting.supportURL(base),
              let url = IssueReporting.supportURL(root.appending(path: page.rawValue).absoluteString),
              openExternalURL(url) else {
            report(IssueReportingFailure.browserUnavailable)
            return
        }
    }

    var canShowSupportReminder: Bool {
        optionalSupportURL != nil && mode == .interact && !workspaceHidden &&
            !visibilityAnimating && !screenZoomEnabled && !magnifierEnabled &&
            zoomStartState == .idle && captureFailureMessage == nil &&
            !showHelp && !settingsVisible && !introductionVisible && errorMessage == nil &&
            desktopSessions.allSatisfy { !$0.canvas.hasActiveEdit }
    }
    var issueReportURL: URL? {
        IssueReporting.url(base: Bundle.main.object(forInfoDictionaryKey: "AnnotatorIssuesURL") as? String,
                           version: versionLabel)
    }

    var optionalSupportURL: URL? {
        IssueReporting.supportURL(Bundle.main.object(forInfoDictionaryKey: "AnnotatorSupportURL") as? String)
    }

    func reportIssue(feedback: Bool = false) {
        guard let url = IssueReporting.url(
            base: Bundle.main.object(forInfoDictionaryKey: "AnnotatorIssuesURL") as? String,
            version: versionLabel, feedback: feedback) else {
            report(IssueReportingFailure.notConfigured)
            return
        }
        guard openExternalURL(url) else {
            report(IssueReportingFailure.browserUnavailable)
            return
        }
    }

    @discardableResult func openOptionalSupport() -> Bool {
        guard let url = optionalSupportURL else {
            report(IssueReportingFailure.supportNotConfigured)
            return false
        }
        guard openExternalURL(url) else {
            report(IssueReportingFailure.browserUnavailable)
            return false
        }
        supportReminder.snooze()
        return true
    }
}

private enum IssueReportingFailure: Error, LocalizedError {
    case notConfigured, browserUnavailable, supportNotConfigured
    var errorDescription: String? {
        switch self {
        case .notConfigured: "Issue reporting is not configured for this build. Send your feedback to the person who shared it."
        case .browserUnavailable: "The page could not open. Check your default browser and try again."
        case .supportNotConfigured: "The optional support link is not configured yet."
        }
    }
}
