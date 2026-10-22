import AppKit
import XCTest
@testable import AnnotatorApp

@MainActor
private final class AskCounter {
    var value = 0
}

@MainActor
private final class RecordingFinder: FinderPresenting {
    var calls: [String] = []
    func open(_ url: URL) { calls.append("open \(url.path)") }
    func reveal(_ url: URL) { calls.append("reveal \(url.path)") }
}

@MainActor
final class ApplicationsMoveOfferTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!
    private var finder: RecordingFinder!
    private let release = ApplicationsMoveOffer.releaseBundleIdentifier

    override func setUp() async throws {
        suite = "applications-offer-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        finder = RecordingFinder()
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suite)
        defaults = nil
        finder = nil
    }

    private func offer() -> ApplicationsMoveOffer {
        ApplicationsMoveOffer(defaults: defaults, finder: finder,
                              applicationsFolder: URL(fileURLWithPath: "/Applications"),
                              downloadsFolder: URL(fileURLWithPath: "/Users/tester/Downloads"))
    }

    private func probe(original: URL? = nil, readOnly: URL? = nil) -> ApplicationsPlacementProbe {
        ApplicationsPlacementProbe(
            applicationFolders: [URL(fileURLWithPath: "/Applications"), URL(fileURLWithPath: "/Users/tester/Applications")],
            originalPath: { _ in original }, readOnlyVolume: { _ in readOnly })
    }

    func testPlacementRecognisesApplicationsFoldersAndOtherLocations() {
        let probe = probe()
        XCTAssertEqual(probe.placement(of: URL(fileURLWithPath: "/Applications/Annotator.app")), .applications)
        XCTAssertEqual(probe.placement(of: URL(fileURLWithPath: "/Users/tester/Applications/Annotator.app")), .applications)
        XCTAssertEqual(probe.placement(of: URL(fileURLWithPath: "/Volumes/Data/Applications/Annotator.app")), .applications)
        let downloads = URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app")
        XCTAssertEqual(probe.placement(of: downloads), .elsewhere(downloads))
        // A folder that only starts with the same letters is not an Applications folder.
        let lookalike = URL(fileURLWithPath: "/Applications Old/Annotator.app")
        XCTAssertEqual(probe.placement(of: lookalike), .elsewhere(lookalike))
    }

    func testPlacementDetectsTranslocationAndReadOnlyVolumes() {
        let original = URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app")
        let translocated = URL(fileURLWithPath: "/private/var/folders/ab/cd/T/AppTranslocation/1234/d/Annotator.app")
        XCTAssertEqual(probe(original: original).placement(of: translocated), .translocated(original: original))
        XCTAssertEqual(probe().placement(of: translocated), .translocated(original: nil))
        let volume = URL(fileURLWithPath: "/Volumes/Annotator")
        XCTAssertEqual(probe(readOnly: volume).placement(of: volume.appendingPathComponent("Annotator.app")),
                       .readOnlyVolume(volume))
    }

    func testTranslocationToolOutputGivesTheOriginalOnlyWhenItDiffers() {
        let translocated = URL(fileURLWithPath: "/private/var/folders/ab/cd/T/AppTranslocation/1234/d/Annotator.app")
        let text = "Original Path: (note if this is what you passed in then that path is not translocated)\n\t/Users/tester/Downloads/Annotator.app\n"
        XCTAssertEqual(TranslocationLookup.parse(text, for: translocated), URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app"))
        XCTAssertNil(TranslocationLookup.parse("Original Path: (note ...)\n\t\(translocated.path)\n", for: translocated))
        XCTAssertNil(TranslocationLookup.parse("", for: translocated))
        // The real tool reports an ordinary path as its own original.
        XCTAssertNil(TranslocationLookup.originalPath(of: Bundle.main.bundleURL))
    }

    func testOnlyTheReleaseAppAsksAndNeverAtLoginOrAfterDontAskAgain() {
        let offer = offer()
        XCTAssertTrue(offer.mayAsk(bundleIdentifier: release, isLoginItem: false))
        XCTAssertFalse(offer.mayAsk(bundleIdentifier: "com.dorlugasigal.annotator.dev", isLoginItem: false))
        XCTAssertFalse(offer.mayAsk(bundleIdentifier: nil, isLoginItem: false))
        XCTAssertFalse(offer.mayAsk(bundleIdentifier: release, isLoginItem: true))
        defaults.set(true, forKey: ApplicationsMoveOffer.suppressedKey)
        XCTAssertFalse(offer.mayAsk(bundleIdentifier: release, isLoginItem: false))
    }

    func testTheQuestionNamesTheActionAndTheNextStep() {
        let offer = offer()
        let downloads = offer.alert(for: .elsewhere(URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app")))
        XCTAssertEqual(downloads.messageText, "Move Annotator to Applications")
        XCTAssertTrue(downloads.informativeText.contains("drag Annotator into Applications"))
        XCTAssertTrue(downloads.informativeText.contains("Annotator quits while you move it."))
        XCTAssertEqual(downloads.buttons.map(\.title), ["Show in Finder", "Not Now"])
        XCTAssertEqual(downloads.buttons[1].keyEquivalent, "\u{1b}")
        XCTAssertTrue(downloads.showsSuppressionButton)
        XCTAssertEqual(downloads.suppressionButton?.title, "Don't ask again")
        let image = offer.alert(for: .readOnlyVolume(URL(fileURLWithPath: "/Volumes/Annotator")))
        XCTAssertTrue(image.informativeText.contains("disk image window"))
        XCTAssertEqual(image.buttons.map(\.title), ["Show Disk Image", "Not Now"])
    }

    func testShowInFinderOpensApplicationsRevealsTheCopyAndQuits() {
        let offer = offer()
        let copy = URL(fileURLWithPath: "/Users/tester/Desktop/Annotator.app")
        XCTAssertTrue(offer.handle(.showInFinder, dontAskAgain: false, for: .elsewhere(copy)))
        XCTAssertEqual(finder.calls, ["open /Applications", "reveal /Users/tester/Desktop/Annotator.app"])
        XCTAssertFalse(offer.suppressed)
    }

    func testTranslocatedCopyRevealsItsOriginalOrFallsBackToDownloads() {
        let offer = offer()
        let original = URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app")
        XCTAssertTrue(offer.handle(.showInFinder, dontAskAgain: false, for: .translocated(original: original)))
        XCTAssertEqual(finder.calls, ["open /Applications", "reveal /Users/tester/Downloads/Annotator.app"])
        finder.calls = []
        XCTAssertTrue(offer.handle(.showInFinder, dontAskAgain: false, for: .translocated(original: nil)))
        XCTAssertEqual(finder.calls, ["open /Applications", "open /Users/tester/Downloads"])
    }

    func testDiskImageShowsItsOwnWindowOnly() {
        XCTAssertTrue(offer().handle(.showInFinder, dontAskAgain: false, for: .readOnlyVolume(URL(fileURLWithPath: "/Volumes/Annotator"))))
        XCTAssertEqual(finder.calls, ["open /Volumes/Annotator"])
    }

    func testNotNowKeepsRunningAndDontAskAgainPersists() {
        let offer = offer()
        let copy = URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app")
        XCTAssertFalse(offer.handle(.notNow, dontAskAgain: false, for: .elsewhere(copy)))
        XCTAssertFalse(offer.suppressed)
        XCTAssertFalse(offer.handle(.notNow, dontAskAgain: true, for: .elsewhere(copy)))
        XCTAssertTrue(ApplicationsMoveOffer(defaults: defaults).suppressed)
        XCTAssertTrue(finder.calls.isEmpty)
    }

    func testLaunchCheckAsksOnceOutsideApplicationsAndNotInside() {
        let offer = offer()
        let asked = AskCounter()
        let present: @MainActor (NSAlert) -> NSApplication.ModalResponse = { alert in
            asked.value += 1
            alert.suppressionButton?.state = .on
            return .alertSecondButtonReturn
        }
        XCTAssertFalse(offer.offerAtLaunch(bundleURL: URL(fileURLWithPath: "/Applications/Annotator.app"),
                                           bundleIdentifier: release, isLoginItem: false, probe: probe(), present: present))
        XCTAssertEqual(asked.value, 0)
        XCTAssertFalse(offer.offerAtLaunch(bundleURL: URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app"),
                                           bundleIdentifier: release, isLoginItem: false, probe: probe(), present: present))
        XCTAssertEqual(asked.value, 1)
        XCTAssertTrue(offer.suppressed)
        XCTAssertFalse(offer.offerAtLaunch(bundleURL: URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app"),
                                           bundleIdentifier: release, isLoginItem: false, probe: probe(), present: present))
        XCTAssertEqual(asked.value, 1)
    }

    func testChoosingShowInFinderAtLaunchQuits() {
        let quits = offer().offerAtLaunch(bundleURL: URL(fileURLWithPath: "/Users/tester/Downloads/Annotator.app"),
                                          bundleIdentifier: release, isLoginItem: false, probe: probe(),
                                          present: { _ in .alertFirstButtonReturn })
        XCTAssertTrue(quits)
        XCTAssertEqual(finder.calls, ["open /Applications", "reveal /Users/tester/Downloads/Annotator.app"])
    }
}
