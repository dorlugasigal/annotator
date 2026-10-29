import XCTest
@testable import AnnotatorApp

@MainActor
final class UpdateConfigurationTests: XCTestCase {
    func testBuiltAppUsesCurrentReleaseVersion() throws {
        let root = RepositoryRoot.url
        let source = try String(contentsOf: root.appendingPathComponent("apps/macos/project.yml"), encoding: .utf8)
        for (setting, key) in [
            ("MARKETING_VERSION", "CFBundleShortVersionString"),
            ("CURRENT_PROJECT_VERSION", "CFBundleVersion")
        ] {
            let prefix = "    \(setting):"
            let matches = source.components(separatedBy: .newlines).filter { $0.hasPrefix(prefix) }
            XCTAssertEqual(matches.count, 1)
            let line = try XCTUnwrap(matches.first)
            let expected = String(line.dropFirst(prefix.count))
                .trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
            XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: key) as? String, expected)
        }
        XCTAssertFalse(source.contains("ANNOTATOR_PRERELEASE"), "Releases have no pre-release setting.")
        XCTAssertNil(Bundle.main.object(forInfoDictionaryKey: "AnnotatorPrerelease"), "Releases have no pre-release label.")
    }

    func testVersionIsPlainAndTheBuildIsDerivedFromIt() throws {
        let version = try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
        XCTAssertNotNil(version.range(of: #"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]?)\.(0|[1-9][0-9]?)$"#, options: .regularExpression),
                        "\(version) is not a plain X.Y.Z with minor and patch at 99 or lower.")
        let parts = version.split(separator: ".").compactMap { Int($0) }
        guard parts.count == 3 else { return XCTFail("\(version) is not X.Y.Z.") }
        let build = try XCTUnwrap(Int(try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String)))
        XCTAssertEqual(build, parts[0] * 10_000 + parts[1] * 100 + parts[2], "The build is major * 10000 + minor * 100 + patch.")
        XCTAssertGreaterThan(build, 35, "Installed betas (0.1.0-beta.17 is build 35) must see this build as newer.")
        let suite = "version-label-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        defer { model.closeAll(); defaults.removePersistentDomain(forName: suite) }
        XCTAssertEqual(model.versionLabel, version, "The app shows only X.Y.Z, without the build or a release stage.")
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
    func testMenuChecksQuietlyOnlyWhenTheLastCheckIsSixHoursOld() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertTrue(AppUpdateController.menuCheckIsDue(lastCheck: nil, now: now))
        XCTAssertFalse(AppUpdateController.menuCheckIsDue(lastCheck: now.addingTimeInterval(-5 * 60 * 60), now: now))
        XCTAssertTrue(AppUpdateController.menuCheckIsDue(lastCheck: now.addingTimeInterval(-6 * 60 * 60), now: now))
        XCTAssertTrue(AppUpdateController.menuCheckIsDue(lastCheck: now.addingTimeInterval(60), now: now))
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
