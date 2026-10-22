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
        let versionLabel = "\(entry.version) (\(entry.build))"
        let before = NativeAccessibilityNode.nodes(in: host)
        XCTAssertFalse(before.contains { $0.accessibilityValue() as? String == versionLabel })
        let disclosure = try XCTUnwrap(before.first {
            $0.accessibilityRole() == .disclosureTriangle || $0.accessibilityRole() == .button
        })
        XCTAssertTrue(disclosure.accessibilityPerformPress())
        RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        host.layoutSubtreeIfNeeded()
        let text = NativeAccessibilityNode.nodes(in: host).flatMap {
            [$0.accessibilityLabel(), $0.accessibilityValue() as? String].compactMap { $0 }
        }.joined(separator: "\n")
        XCTAssertTrue(text.contains(versionLabel))
        if !entry.knownIssues.isEmpty { XCTAssertTrue(text.contains("Known limits")) }
    }

    func testBundledNotesMatchRunningVersionAndExcludeUnreleased() throws {
        let entry = try ReleaseNotesCatalog.load()
        let version = try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
        let suffix = Bundle.main.object(forInfoDictionaryKey: "AnnotatorPrerelease") as? String ?? ""
        let build = try XCTUnwrap(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String)
        XCTAssertEqual(entry.version, version + (suffix.isEmpty ? "" : "-\(suffix)"))
        XCTAssertEqual(entry.build, Int(build))
    }

    func testMissingOrInvalidNotesAreExplicitErrors() throws {
        let data = Data(#"""
        {"schemaVersion":1,"unreleased":{"Added":["Do not show this"]},"releases":[
          {"version":"0.1.0-alpha.1","build":9,"date":"2026-09-23","changes":{"Fixed":["A verified fix."]},"knownIssues":[]}
        ]}
        """#.utf8)
        let entry = try ReleaseNotesCatalog.current(data: data, version: "0.1.0-alpha.1", build: 9)
        XCTAssertEqual(entry.changes, ["Fixed": ["A verified fix."]])
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: data, version: "0.1.0-alpha.2", build: 10))
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: data, version: "0.1.0-alpha.1", build: 10))
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: Data("broken".utf8), version: "0.1.0", build: 9))
        let unsupported = Data(String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\"schemaVersion\":1", with: "\"schemaVersion\":2").utf8)
        XCTAssertThrowsError(try ReleaseNotesCatalog.current(data: unsupported, version: "0.1.0-alpha.1", build: 9))
    }
}
