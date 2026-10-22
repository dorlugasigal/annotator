import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class PaletteHexTests: XCTestCase {
    func testRGBDefaultsToOpaqueAndAcceptsOptionalPrefixAndWhitespace() throws {
        let color = try PaletteHex.parse(" \n#12abEF\t")
        XCTAssertEqual(color, RGBA(18.0 / 255, 171.0 / 255, 239.0 / 255))
        XCTAssertEqual(PaletteHex.format(color), "12ABEFFF")
    }

    func testRGBAKeepsColorAlphaIndependentOfObjectOpacity() throws {
        let color = try PaletteHex.parse("12345680")
        XCTAssertEqual(color.alpha, 128.0 / 255)
        XCTAssertEqual(PaletteHex.format(color), "12345680")
        XCTAssertEqual(try PaletteHex.parse("00000000").alpha, 0)
    }

    func testRejectsInvalidHexRatherThanTreatingItAsNoFill() {
        for value in ["", "None", "12345", "1234567", "123456789", "GG1122", "##112233", "112233#",
                      "+12345", "12 3456", "# 123456"] {
            XCTAssertThrowsError(try PaletteHex.parse(value), value)
        }
    }

    func testFormattingRoundsToNearestByte() {
        XCTAssertEqual(PaletteHex.format(RGBA(0.5, 0, 1, 0.5)), "8000FF80")
    }
}
