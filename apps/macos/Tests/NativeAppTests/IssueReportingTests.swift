import XCTest
@testable import AnnotatorApp

final class IssueReportingTests: XCTestCase {
    func testOnlyConfiguredGitHubNewIssuePagesAreAllowed() throws {
        let version = "0.2.0"
        let url = try XCTUnwrap(IssueReporting.url(base: "https://github.com/example/annotator-releases/issues/new", version: version))
        let parts = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false))
        XCTAssertEqual(parts.queryItems?.first { $0.name == "version" }?.value, version)
        XCTAssertEqual(parts.queryItems?.first { $0.name == "template" }?.value, "bug-report.yml")
        XCTAssertNil(parts.queryItems?.first { $0.name == "body" }, "Never silently attach diagnostics or drawing data.")
        for input in [nil, "", "$(ANNOTATOR_ISSUES_URL)", "http://github.com/example/repo/issues/new",
                      "https://evil.example/example/repo/issues/new",
                      "https://github.com/example/repo/issues/new?body=private",
                      "https://github.com/example/repo/issues", "https://user@github.com/example/repo/issues/new"] {
            XCTAssertNil(IssueReporting.url(base: input, version: version))
        }

    }

    func testFeedbackAndSupportAreExplicitOptInLinks() throws {
            let url = try XCTUnwrap(IssueReporting.url(base: "https://github.com/example/releases/issues/new",
                                                     version: "0.2.0", feedback: true))
            XCTAssertTrue(url.absoluteString.contains("feedback.yml"))
            XCTAssertFalse(url.absoluteString.contains("body="))
            XCTAssertNil(IssueReporting.supportURL(nil))
            XCTAssertNil(IssueReporting.supportURL("$(ANNOTATOR_SUPPORT_URL)"))
            XCTAssertNil(IssueReporting.supportURL("http://example.com/support"))
            XCTAssertNil(IssueReporting.supportURL("https://user:password@example.com/support"))
            XCTAssertEqual(IssueReporting.supportURL("https://example.com/support")?.host, "example.com")
    }
}
