import Foundation
import AppKit
import SwiftUI
import XCTest
@testable import AnnotatorApp

@MainActor
final class ReleaseNotesTests: XCTestCase {
    func testManualDisclosureOpensBundledNotesWithoutShowingUnreleasedChanges() throws {
        NativeAccessibilityNode.enable()
        let host = NSHostingView(rootView: ReleaseNotesView().padding(16).frame(width: 480))
        let panel = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 500, height: 600),
            styleMask: [.titled], backing: .buffered, defer: false)
        panel.isReleasedWhenClosed = false
        panel.contentView = host
        panel.orderFrontRegardless()
        defer { panel.orderOut(nil); panel.contentView = nil; panel.close() }
        host.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        let entry = try ReleaseNotesCatalog.load()
        let before = NativeAccessibilityNode.nodes(in: host)
        XCTAssertFalse(before.contains { $0.accessibilityValue() as? String == entry.version })
        let disclosure = try XCTUnwrap(before.first {
            $0.accessibilityRole() == .disclosureTriangle || $0.accessibilityRole() == .button
        })
        XCTAssertTrue(disclosure.accessibilityPerformPress())
        RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        host.layoutSubtreeIfNeeded()
        let strings = NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }
        XCTAssertTrue(strings.contains(entry.version), "The notes start with the plain version.")
        XCTAssertFalse(strings.contains { $0.contains("(\(entry.build))") }, "The build number is not shown.")
        if !entry.knownIssues.isEmpty { XCTAssertTrue(strings.joined(separator: "\n").contains("Known limits")) }
    }

    func testBundledNotesMatchRunningVersionAndExcludeUnreleased() throws {
        let entry = try ReleaseNotesCatalog.load()
        let version = try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
        let build = try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String)
        XCTAssertEqual(entry.version, version)
        XCTAssertEqual(entry.build, Int(build))
    }

    func testMissingOrInvalidNotesAreExplicitErrors() throws {
        let data = Data(#"""
        {"schemaVersion":1,"unreleased":{"Added":["Do not show this"]},"releases":[
          {"version":"0.2.1","build":201,"date":"2026-11-02","changes":{"Fixed":["A verified fix."]},"knownIssues":[]},
          {"version":"0.1.0-beta.17","build":35,"date":"2026-10-29","changes":{"Added":["An older change."]},"knownIssues":[]}
        ]}
        """#.utf8)
        let entry = try ReleaseNotesCatalog.current(data: data, version: "0.2.1", build: 201)
        XCTAssertEqual(entry.changes, ["Fixed": ["A verified fix."]])
        let legacy = try ReleaseNotesCatalog.current(data: data, version: "0.1.0-beta.17", build: 35)
        XCTAssertEqual(legacy.changes, ["Added": ["An older change."]], "Notes from before plain versions stay readable.")
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: data, version: "0.2.2", build: 202))
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: data, version: "0.2.1", build: 202))
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: data, version: "0.1.0", build: 35),
                             "A plain version never matches an older pre-release entry.")
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: Data("broken".utf8), version: "0.2.1", build: 201))
        let unsupported = Data(String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\"schemaVersion\":1", with: "\"schemaVersion\":2").utf8)
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: unsupported, version: "0.2.1", build: 201))
    }
}
