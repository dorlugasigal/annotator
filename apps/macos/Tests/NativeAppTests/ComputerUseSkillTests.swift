import AppKit
import XCTest
@testable import AnnotatorApp

final class ComputerUseSkillTests: XCTestCase {
    func testBundledSkillExportsWithoutSourceCheckoutOrOverwrite() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("skill-export-\(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let exported = try ComputerUseSkill.export(to: root)
        let file = exported.appendingPathComponent("SKILL.md")
        let content = try String(contentsOf: file, encoding: .utf8)
        let words = content.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        XCTAssertTrue(content.contains("name: annotator"))
        XCTAssertTrue(content.contains("Option-scroll"))
        XCTAssertTrue(content.contains("computer-use"))
        XCTAssertTrue(content.contains("Start arrowhead"))
        XCTAssertTrue(content.contains("Database"))
        XCTAssertTrue(words.contains("only two drag endpoints"))
        XCTAssertTrue(words.contains("repeatable endpoints, not random target positions"))
        XCTAssertTrue(words.contains("unauthorized client, stop native control"))
        XCTAssertTrue(words.contains("Do not promise human-like pen movement"))
        XCTAssertTrue(words.contains("not evidence of a new computer-use session"))
        XCTAssertTrue(words.contains("Start the break timer only when the user asks."))
        XCTAssertTrue(words.contains("and your input counts"))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: exported.path), ["SKILL.md"])
        try Data("User's custom skill".utf8).write(to: file)
        XCTAssertThrowsError(try ComputerUseSkill.export(to: root))
        XCTAssertEqual(try String(contentsOf: file, encoding: .utf8), "User's custom skill")
    }
}
