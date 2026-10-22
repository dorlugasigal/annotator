import XCTest
@testable import AnnotatorApp

@MainActor
final class UpdateConfigurationTests: XCTestCase {
    func testBuiltAppUsesCurrentReleaseVersion() throws {
        let root = RepositoryRoot.url
        let source = try String(contentsOf: root.appendingPathComponent("apps/macos/project.yml"), encoding: .utf8)
        for (setting, key) in [
            ("MARKETING_VERSION", "CFBundleShortVersionString"),
            ("CURRENT_PROJECT_VERSION", "CFBundleVersion"),
            ("ANNOTATOR_PRERELEASE", "AnnotatorPrerelease")
        ] {
            let prefix = "    \(setting):"
            let matches = source.components(separatedBy: .newlines).filter { $0.hasPrefix(prefix) }
            XCTAssertEqual(matches.count, 1)
            let line = try XCTUnwrap(matches.first)
            let expected = String(line.dropFirst(prefix.count))
                .trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
            XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: key) as? String, expected)
        }
        XCTAssertEqual(WorkspaceModel.formatVersion(version: "0.1.0", build: "9", prerelease: "alpha.1"), "0.1.0-alpha.1 (9)")
    }

    func testReleaseLabelsKeepBuildOrderingSeparateFromPrereleaseNames() {
        for phase in ["alpha.1", "alpha.2", "beta.1", "rc.1"] {
            XCTAssertEqual(WorkspaceModel.formatVersion(version: "0.1.0", build: "12", prerelease: phase),
                           "0.1.0-\(phase) (12)")
        }
        for phase: String? in [nil, ""] {
            XCTAssertEqual(WorkspaceModel.formatVersion(version: "0.1.0", build: "13", prerelease: phase), "0.1.0 (13)")
        }
    }

    func testUnconfiguredFeedIsExplicitlyDisabled() throws {
        XCTAssertFalse(try AppUpdateController.validate(feed:nil,publicKey:nil))
        XCTAssertFalse(try AppUpdateController.validate(feed:"$(ANNOTATOR_UPDATE_FEED_URL)",publicKey:"$(ANNOTATOR_UPDATE_PUBLIC_KEY)"))
    }
    func testUnconfiguredUpdateCopyNamesTheExistingRecoveryRoute() throws {
        var state: (Bool, Bool, String)?
        let controller = AppUpdateController { state = ($0, $1, $2) }
        try controller.configure(bundle: Bundle(for: Self.self))
        let result = try XCTUnwrap(state)
        XCTAssertFalse(result.0)
        XCTAssertFalse(result.1)
        XCTAssertTrue(result.2.contains("Updates aren't set up"))
        XCTAssertTrue(result.2.contains("Settings > About"))
        XCTAssertFalse(result.2.contains("will be available"))
    }
    func testOnlyHTTPSFeedsWithSigningKeysCanBeEnabled() throws {
        let key = Data(repeating:1,count:32).base64EncodedString()
        XCTAssertTrue(try AppUpdateController.validate(feed:"https://updates.example.org/appcast.xml",publicKey:key))
        for feed in ["http://updates.example.org/appcast.xml","https://name:secret@updates.example.org/appcast.xml","/appcast.xml",""] {
            XCTAssertThrowsError(try AppUpdateController.validate(feed:feed,publicKey:key))
        }
        XCTAssertThrowsError(try AppUpdateController.validate(feed:"https://updates.example.org/appcast.xml",publicKey:""))
    }
}
