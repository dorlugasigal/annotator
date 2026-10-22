import AnnotationCore
import CoreGraphics
import Foundation
@testable import PresentationRendering

/// Swift, Foundation and Core Graphics behavior that the Windows port reproduces by hand.
@MainActor
enum PlatformExport {
    static func run(_ corpus: Corpus) throws {
        corpus.describe("platform", "Swift memory layout, Foundation JSON and string behavior, Core Graphics paths, rectangle math, hatch positions and libm samples.")
        try layout(corpus)
        try jsonDecoding(corpus)
        try jsonEncoding(corpus)
        try strings(corpus)
        try paths(corpus)
        try rects(corpus)
        try hatch(corpus)
        try math(corpus)
    }

    // MARK: Memory layout and history budget

    private static func layout(_ corpus: Corpus) throws {
        func entry<T>(_ type: T.Type) -> J {
            ["size": .int(MemoryLayout<T>.size), "stride": .int(MemoryLayout<T>.stride),
             "alignment": .int(MemoryLayout<T>.alignment)]
        }
        var gradientStyle = Style()
        gradientStyle.strokeGradient = GradientPreset.spectrum.gradient
        gradientStyle.fill = RGBA(0.2, 0.4, 0.6, 0.5)
        gradientStyle.fillGradient = GradientPreset.sunset.gradient
        let label = ShapeLabel(text: "Label ü", style: gradientStyle, followsStroke: true)
        let samples: [(String, Element)] = [
            ("empty rectangle", Fixture.element(1, .rectangle, frame: Rect(x: 0, y: 0, width: 10, height: 10))),
            ("pen with 100 points and pressures", Fixture.element(2, .pen,
                points: (0..<100).map { Point(Double($0), Double($0 % 7)) }) { $0.pressures = Array(repeating: 0.5, count: 100) }),
            ("text with multibyte characters", Fixture.element(3, .text, frame: Rect(x: 0, y: 0, width: 80, height: 30),
                text: "héllo wörld ✓")),
            ("image", Fixture.element(4, .image, frame: Rect(x: 0, y: 0, width: 4, height: 3)) { $0.imagePNG = try? Fixture.smallPNG() }),
            ("rectangle with gradient label", Fixture.element(5, .rectangle, frame: Rect(x: 0, y: 0, width: 50, height: 40),
                style: gradientStyle) { $0.label = label })
        ]
        var history = try CanvasDocument()
        var historyBytes: [J] = [.int(history.historyBytes)]
        for (_, element) in samples {
            try history.add(element)
            historyBytes.append(.int(history.historyBytes))
        }
        try corpus.json("platform/layout.json", [
            "note": "Element.estimatedBytes uses max(512, MemoryLayout<Element>.stride). History snapshots cost 64 bytes plus the estimate of every element; historyBytes lists the total after each add, starting from an empty document.",
            "memoryLayout": [
                "Element": entry(Element.self), "Style": entry(Style.self), "Point": entry(Point.self),
                "Rect": entry(Rect.self), "RGBA": entry(RGBA.self), "ColorGradient": entry(ColorGradient.self),
                "Anchor": entry(Anchor.self), "UUID": entry(UUID.self), "ShapeLabel": entry(ShapeLabel.self)
            ],
            "estimatedBytes": .array(samples.map { (name, element) -> J in
                ["name": .string(name), "estimatedBytes": .int(element.estimatedBytes),
                 "points": .int(element.points.count), "pressures": .int(element.pressures.count),
                 "textUTF8": .int(element.text.utf8.count), "imageBytes": .int(element.imagePNG?.count ?? 0),
                 "labelEstimatedBytes": element.label.map { .int($0.estimatedBytes) } ?? .null,
                 "paintEstimatedBytes": .int(element.style.paintEstimatedBytes)]
            }),
            "historyBytesAfterEachAdd": .array(historyBytes)
        ])
    }

    // MARK: JSON decoding

    private struct Numbers<T: Codable>: Codable { let values: [T] }

    /// A board with one rectangle, written by the app's encoder, used as the base for decoding cases.
    static func baseBoard() throws -> String {
        var document = try CanvasDocument()
        try document.add(Fixture.element(1, .rectangle, frame: Rect(x: 10, y: 20, width: 30, height: 40)))
        let data = try DrawingFile(document: document).data()
        return String(decoding: data, as: UTF8.self)
    }

    private static func replacing(_ text: String, _ target: String, _ replacement: String) throws -> String {
        guard text.contains(target) else { throw ExportFailure("The base board has no \(target).") }
        return text.replacingOccurrences(of: target, with: replacement)
    }

    private static func boardResults(_ data: Data) -> [String: J] {
        let decoded = outcome {
            let file = try JSONDecoder().decode(DrawingFile.self, from: data)
            let canonical = try Corpus.encoder(pretty: false).encode(file)
            return ["schemaVersion": .int(file.schemaVersion), "elementCount": .int(file.elements.count),
                    "canonical": .string(String(decoding: canonical, as: UTF8.self))]
        }
        let read = outcome {
            let document = try DrawingFile.read(data)
            return .string(String(decoding: try DrawingFile(document: document).data(), as: UTF8.self))
        }
        return ["decode": decoded, "read": read]
    }

    private static func jsonDecoding(_ corpus: Corpus) throws {
        let base = try baseBoard()
        var records: [J] = []
        func add(_ name: String, _ data: Data, note: String? = nil) {
            var record: [String: J] = ["name": .string(name), "target": "DrawingFile", "input": inputRecord(data)]
            if let note { record["note"] = .string(note) }
            record.merge(boardResults(data)) { $1 }
            records.append(.object(record))
        }
        func board(_ name: String, _ text: String, note: String? = nil) {
            add(name, Data(text.utf8), note: note)
        }

        board("base board", base, note: "The base for the other cases: one rectangle, written by DrawingFile.data().")
        board("empty data", "")
        board("whitespace only", " \n\t ")
        board("truncated object", String(base.dropLast(5)))
        board("array instead of object", "[]")
        board("string instead of object", "\"board\"")
        board("null document", "null")
        board("empty object", "{}")
        board("elements missing", #"{"schemaVersion":1}"#)
        board("schema as string", #"{"elements":[],"schemaVersion":"1"}"#)
        board("schema null", #"{"elements":[],"schemaVersion":null}"#)
        board("schema with fraction", #"{"elements":[],"schemaVersion":1.5}"#)
        board("schema written 1.0", #"{"elements":[],"schemaVersion":1.0}"#)
        board("schema written 1e0", #"{"elements":[],"schemaVersion":1e0}"#)
        board("schema written 10e-1", #"{"elements":[],"schemaVersion":10e-1}"#)
        board("schema too large for Int", #"{"elements":[],"schemaVersion":99999999999999999999}"#)
        board("schema negative zero", #"{"elements":[],"schemaVersion":-0}"#)
        board("unsupported schema 0", #"{"elements":[],"schemaVersion":0}"#)
        board("unsupported schema 4", #"{"elements":[],"schemaVersion":4}"#)
        board("elements as object", #"{"elements":{},"schemaVersion":1}"#)
        board("element null", #"{"elements":[null],"schemaVersion":1}"#)
        board("unknown keys ignored", try replacing(base, #""schemaVersion":1"#, #""future":{"a":[1,2,{"b":null}]},"schemaVersion":1"#))
        board("unknown tool", try replacing(base, #""tool":"rectangle""#, #""tool":"hammer""#))
        board("tool null", try replacing(base, #""tool":"rectangle""#, #""tool":null"#))
        board("tool missing", try replacing(base, #","tool":"rectangle""#, ""))
        board("invalid UUID", try replacing(base, Fixture.id(1).uuidString, "not-a-uuid"))
        board("lowercase UUID", try replacing(base, Fixture.id(1).uuidString, Fixture.id(1).uuidString.lowercased()))
        board("UUID without hyphens", try replacing(base, Fixture.id(1).uuidString, Fixture.id(1).uuidString.replacingOccurrences(of: "-", with: "")))
        board("UUID in braces", try replacing(base, Fixture.id(1).uuidString, "{" + Fixture.id(1).uuidString + "}"))
        board("rotation as string", try replacing(base, #""rotation":0"#, #""rotation":"0""#))
        board("locked as number", try replacing(base, #""locked":false"#, #""locked":0"#))
        board("revision negative", try replacing(base, #""revision":0"#, #""revision":-1"#))
        board("revision with fraction", try replacing(base, #""revision":0"#, #""revision":0.5"#))
        board("revision UInt64 max", try replacing(base, #""revision":0"#, #""revision":18446744073709551615"#))
        board("revision above UInt64 max", try replacing(base, #""revision":0"#, #""revision":18446744073709551616"#))
        board("coordinate overflow", try replacing(base, #""x":10"#, #""x":1e400"#))
        board("coordinate underflow", try replacing(base, #""x":10"#, #""x":1e-400"#))
        board("coordinate NaN literal", try replacing(base, #""x":10"#, #""x":NaN"#))
        board("coordinate leading zero", try replacing(base, #""x":10"#, #""x":010"#))
        board("coordinate leading plus", try replacing(base, #""x":10"#, #""x":+10"#))
        board("coordinate trailing dot", try replacing(base, #""x":10"#, #""x":10."#))
        board("coordinate hexadecimal", try replacing(base, #""x":10"#, #""x":0x10"#))
        board("coordinate exponent forms", try replacing(base, #""x":10"#, #""x":1E1"#))
        board("trailing comma in object", try replacing(base, #""schemaVersion":1}"#, #""schemaVersion":1,}"#))
        board("trailing comma in array", try replacing(base, "}],", "},],"))
        board("comment", "// board\n" + base)
        board("single quotes", try replacing(base, #""schemaVersion""#, "'schemaVersion'"))
        board("trailing content", base + " x")
        board("two documents", base + base)
        board("trailing whitespace", base + " \r\n\t")
        board("invalid escape", try replacing(base, #""text":"""#, #""text":"\x""#))
        board("raw control character in string", try replacing(base, #""text":"""#, "\"text\":\"a\u{01}b\""))
        board("raw tab in string", try replacing(base, #""text":"""#, "\"text\":\"a\tb\""))
        board("escaped slash and unicode", try replacing(base, #""text":"""#, #""text":"a\/b \u00e9 \ud83d\ude00""#))
        board("lone high surrogate escape", try replacing(base, #""text":"""#, #""text":"\ud800""#))
        board("lone low surrogate escape", try replacing(base, #""text":"""#, #""text":"\udc00""#))
        board("uppercase escape", try replacing(base, #""text":"""#, #""text":"\u00C9""#))
        board("invalid Base64 image", try replacing(base, #""geometryToken""#, #""imagePNG":"@@@","geometryToken""#))
        board("image on a rectangle", try replacing(base, #""geometryToken""#, #""imagePNG":"iVBORw0KGgo=","geometryToken""#))

        // Duplicate keys: which value does Foundation keep?
        board("duplicate key at top level", try replacing(base, #""schemaVersion":1"#, #""schemaVersion":4,"schemaVersion":1"#))
        board("duplicate key at top level, other order", try replacing(base, #""schemaVersion":1"#, #""schemaVersion":1,"schemaVersion":4"#))
        board("duplicate key in element", try replacing(base, #""text":"""#, #""text":"first","text":"second""#))
        board("duplicate key with different types", try replacing(base, #""text":"""#, #""text":5,"text":"second""#))
        board("duplicate elements key", try replacing(base, #"{"elements":"#, #"{"elements":[],"elements":"#))

        // Nesting depth, inside an ignored key.
        for depth in [255, 256, 257, 510, 511, 512, 513, 1_000] {
            let nested = String(repeating: "[", count: depth) + String(repeating: "]", count: depth)
            board("ignored value nested \(depth) arrays deep", try replacing(base, #""schemaVersion":1"#, #""extra":\#(nested),"schemaVersion":1"#),
                  note: "The document object adds one more level.")
        }

        // Text encodings and byte order marks.
        let utf8 = Data(base.utf8)
        add("UTF-8 with byte order mark", Data([0xEF, 0xBB, 0xBF]) + utf8)
        add("UTF-16 little endian with byte order mark", Data([0xFF, 0xFE]) + base.data(using: .utf16LittleEndian)!)
        add("UTF-16 big endian with byte order mark", Data([0xFE, 0xFF]) + base.data(using: .utf16BigEndian)!)
        add("UTF-16 little endian without byte order mark", base.data(using: .utf16LittleEndian)!)
        add("UTF-16 big endian without byte order mark", base.data(using: .utf16BigEndian)!)
        add("UTF-32 little endian with byte order mark", Data([0xFF, 0xFE, 0x00, 0x00]) + base.data(using: .utf32LittleEndian)!)
        add("UTF-32 big endian without byte order mark", base.data(using: .utf32BigEndian)!)
        add("invalid UTF-8 byte in string", Data(try replacing(base, #""text":"""#, #""text":"aXb""#).utf8).replacingByte(UInt8(ascii: "X"), with: 0xFF))
        add("overlong UTF-8 slash", Data(try replacing(base, #""text":"""#, #""text":"aXYb""#).utf8)
            .replacingByte(UInt8(ascii: "X"), with: 0xC0).replacingByte(UInt8(ascii: "Y"), with: 0xAF))
        add("UTF-8 surrogate code point", Data(try replacing(base, #""text":"""#, #""text":"aXYZb""#).utf8)
            .replacingByte(UInt8(ascii: "X"), with: 0xED).replacingByte(UInt8(ascii: "Y"), with: 0xA0).replacingByte(UInt8(ascii: "Z"), with: 0x80))
        add("NUL byte inside string", Data(try replacing(base, #""text":"""#, #""text":"aXb""#).utf8).replacingByte(UInt8(ascii: "X"), with: 0x00))
        try corpus.lines("platform/json-decoding.jsonl", records)

        // Generic containers: duplicate dictionary keys and numbers by type.
        var generic: [J] = []
        func decode<T: Decodable & Encodable>(_ name: String, _ type: T.Type, _ text: String) {
            generic.append([
                "name": .string(name), "target": .string(String(describing: T.self)), "input": inputRecord(Data(text.utf8)),
                "decode": outcome {
                    let value = try JSONDecoder().decode(T.self, from: Data(text.utf8))
                    return .string(String(decoding: try Corpus.encoder(pretty: false).encode(value), as: UTF8.self))
                }
            ])
        }
        decode("duplicate dictionary key", [String: Int].self, #"{"a":1,"a":2}"#)
        decode("duplicate dictionary key, three times", [String: Int].self, #"{"a":1,"b":5,"a":2,"a":3}"#)
        decode("dictionary keys that differ only by normalization", [String: Int].self, "{\"\u{E9}\":1,\"e\u{301}\":2}")
        decode("dictionary key escaped and raw", [String: Int].self, #"{"\u0061":1,"a":2}"#)
        for text in ["0", "-0", "1", "-1", "9223372036854775807", "9223372036854775808", "-9223372036854775808",
                     "-9223372036854775809", "1.0", "1.5", "1e2", "1E2", "1e+2", "1e-2", "100e-2", "2.5e1", "1e19", "0.0"] {
            decode("Int from \(text)", [Int].self, "[\(text)]")
        }
        for text in ["0", "-0", "-1", "18446744073709551615", "18446744073709551616", "1.0", "1e3", "1.8446744073709552e19"] {
            decode("UInt64 from \(text)", [UInt64].self, "[\(text)]")
        }
        for text in ["0", "-0", "-0.0", "0.1", "1e400", "-1e400", "1e-400", "4.9e-324", "2.4e-324", "1.7976931348623157e308",
                     "1.7976931348623159e308", "123456789012345678901234567890", "0.30000000000000004", "9007199254740993",
                     "1.00000000000000011102230246251565404236316680908203125", "1e", "1e+", ".5", "-", "--1", "Infinity", "-Infinity",
                     "true", "\"1\""] {
            decode("Double from \(text)", [Double].self, "[\(text)]")
        }
        for text in [#""a\/b""#, #""\u00e9""#, #""\ud83d\ude00""#, #""\ud83d""#, #""\ude00\ud83d""#, #""\u0000""#, #""\b\f\n\r\t""#,
                     "\"\u{2028}\u{2029}\"", "\"\u{7F}\"", "\"\u{1F}\""] {
            decode("String from \(text)", [String].self, "[\(text)]")
        }
        decode("Bool from 1", [Bool].self, "[1]")
        decode("Bool from \"true\"", [Bool].self, #"["true"]"#)
        decode("null for optional", [Int?].self, "[null,1]")
        try corpus.lines("platform/json-decoding-values.jsonl", generic)
    }

    // MARK: JSON encoding

    private static func jsonEncoding(_ corpus: Corpus) throws {
        let appEncoder = JSONEncoder()
        appEncoder.outputFormatting = [.sortedKeys]
        func written<T: Encodable>(_ value: T) -> J {
            outcome { .string(String(decoding: try appEncoder.encode(value), as: UTF8.self)) }
        }
        var records: [J] = []
        let doubles: [Double] = [
            0, -0.0, 1, -1, 2, 0.1, 0.2, 0.5, 1.5, -1.5, 1.0 / 3, 2.0 / 3, 0.1 + 0.2, 100, 1e-7, 1e-6, 1e-5, 0.000123,
            123456.789, 1e15, 1e16, 1e17, 123456789012345680, 1e21, 1e22, 1e100, 5e-324, 2.2250738585072014e-308,
            1.7976931348623157e308, Double(Float(0.1)), Double(Float(1) / 3), 3.0000000000000004, 0.75, 12.34, 1234.5,
            9007199254740992, 9007199254740994, 4.35, 2.675, 1e-300, 6.02214076e23, Double.pi, -Double.pi / 2
        ]
        for value in doubles {
            records.append(["kind": "Double", "bits": .string(String(value.bitPattern, radix: 16)),
                            "written": written([value])])
        }
        for value in [Double.nan, .infinity, -.infinity] {
            records.append(["kind": "Double", "bits": .string(String(value.bitPattern, radix: 16)), "written": written([value])])
        }
        for value in [0, -1, Int.max, Int.min] { records.append(["kind": "Int", "value": .int(value), "written": written([value])]) }
        for value in [0, UInt64.max, 5381] { records.append(["kind": "UInt64", "value": .uint(value), "written": written([value])]) }
        for value in [true, false] { records.append(["kind": "Bool", "written": written([value])]) }
        let strings = ["", "plain", "a/b", "http://example.com/a?b=c&d=e", "quote \" here", "back\\slash", "line\nbreak",
                       "tab\there", "\r\n", "\u{0}", "\u{1}\u{1F}", "\u{7F}", "é", "e\u{301}", "😀", "👩🏽‍💻", "\u{2028}\u{2029}",
                       "<script>&'", "\u{FEFF}", "\u{200E}", "ß", "\u{10FFFF}", "\u{FFFD}"]
        for value in strings {
            records.append(["kind": "String", "value": .string(value),
                            "utf8Hex": .string(value.utf8.map { String(format: "%02x", $0) }.joined()), "written": written([value])])
        }
        for uuid in [Fixture.id(1), UUID(uuidString: "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")!] {
            records.append(["kind": "UUID", "value": .string(uuid.uuidString), "written": written([uuid])])
        }
        for count in 0...5 {
            let bytes = Data((0..<count).map { UInt8(250 - $0 * 40) })
            records.append(["kind": "Data", "hex": .string(bytes.map { String(format: "%02x", $0) }.joined()), "written": written([bytes])])
        }
        let keys = ["b", "a", "B", "A", "_", "a10", "a9", "a1", "é", "e", "z", "Z", "1", "10", "9", "ß", "ss", "aa", "a-b", "a_b", "Ä", "🙂", "~"]
        records.append(["kind": "sorted keys", "keys": .array(keys.map { .string($0) }),
                        "written": written(Dictionary(uniqueKeysWithValues: keys.enumerated().map { ($1, $0) }))])
        var optional = Style()
        optional.headScale = nil
        records.append(["kind": "nil optionals are omitted", "written": written(optional)])
        try corpus.lines("platform/json-encoding.jsonl", records)
    }

    // MARK: Strings

    private static func strings(_ corpus: Corpus) throws {
        let samples = ["a", "A", "z", "Z", "0", "9", "ß", "ẞ", "ﬁ", "İ", "ı", "I", "i", "Σ", "ΑΣ", "ς", "ǅ", "Ⅻ", "²", "٣", "Ａ",
                       "é", "e\u{301}", "1️⃣", "😀", "👍🏽", "\u{0}", "\t", "\n", "\r\n", "\u{85}", "\u{2028}", " ", "\u{A0}",
                       "\u{3000}", "\u{200B}", "\u{200E}", "\u{202E}", "\u{2066}", "\u{61C}", " padded ", "\u{3000}wide\u{3000}",
                       "Shape 10", "shape 9", "Ä", "ä", "\u{C5}", "\u{212B}", "A\u{30A}", "日本", "ǈ", "ǉ", "\u{3A9}", "\u{2126}",
                       "K", "\u{212A}", "ŉ"]
        func flags(_ string: String, _ set: CharacterSet) -> J {
            .string(string.unicodeScalars.map { set.contains($0) ? "1" : "0" }.joined())
        }
        let records: [J] = samples.map { (value) -> J in
            [
                "value": .string(value),
                "scalars": .array(value.unicodeScalars.map { .string(String(format: "U+%04X", $0.value)) }),
                "lowercased": .string(value.lowercased()), "uppercased": .string(value.uppercased()),
                "characterCount": .int(value.count), "scalarCount": .int(value.unicodeScalars.count),
                "utf8Count": .int(value.utf8.count), "utf16Count": .int(value.utf16.count),
                "trimmedWhitespaceAndNewlines": .string(value.trimmingCharacters(in: .whitespacesAndNewlines)),
                "alphanumerics": flags(value, .alphanumerics), "letters": flags(value, .letters),
                "controlCharacters": flags(value, .controlCharacters), "newlines": flags(value, .newlines),
                "whitespacesAndNewlines": flags(value, .whitespacesAndNewlines),
                "equalsLowercased": .bool(value == value.lowercased()), "equalsUppercased": .bool(value == value.uppercased())
            ]
        }
        try corpus.lines("platform/strings.jsonl", records)
        let pairs: [(String, String)] = [
            ("é", "e\u{301}"), ("\u{C5}", "\u{212B}"), ("\u{C5}", "A\u{30A}"), ("\u{3A9}", "\u{2126}"), ("K", "\u{212A}"),
            ("a", "A"), ("Z", "a"), ("ä", "z"),
            ("Shape 10", "Shape 9"), ("shape", "Shape"), ("ß", "ss"), ("ﬁ", "fi"), ("1️⃣", "1"), ("", "a"), ("a", "a "),
            ("e\u{301}x", "éa"), ("😀", "\u{FFFF}"), ("\u{10000}", "\u{FFFD}"), ("ǅ", "Dž")
        ]
        try corpus.lines("platform/string-comparison.jsonl", pairs.map { (left, right) -> J in
            ["left": .string(left), "right": .string(right), "equal": .bool(left == right), "less": .bool(left < right),
             "greater": .bool(left > right), "leftUTF8Hex": .string(left.utf8.map { String(format: "%02x", $0) }.joined()),
             "rightUTF8Hex": .string(right.utf8.map { String(format: "%02x", $0) }.joined())]
        })
    }

    // MARK: Core Graphics paths

    private enum Op {
        case move(CGPoint), line(CGPoint), quad(CGPoint, CGPoint), cubic(CGPoint, CGPoint, CGPoint), close
        case rect(CGRect), ellipse(CGRect), roundedRect(CGRect, CGFloat, CGFloat), lines([CGPoint])
        case arc(CGPoint, CGFloat, CGFloat, CGFloat, Bool), relativeArc(CGPoint, CGFloat, CGFloat, CGFloat)
        case tangentArc(CGPoint, CGPoint, CGFloat)
        case addPath([Op], CGAffineTransform)

        var json: J {
            switch self {
            case .move(let p): ["move", p.json]
            case .line(let p): ["line", p.json]
            case .quad(let c, let p): ["quad", c.json, p.json]
            case .cubic(let a, let b, let p): ["cubic", a.json, b.json, p.json]
            case .close: ["close"]
            case .rect(let r): ["addRect", r.json]
            case .ellipse(let r): ["addEllipse", r.json]
            case .roundedRect(let r, let w, let h): ["addRoundedRect", r.json, .double(w), .double(h)]
            case .lines(let points): ["addLines", .array(points.map(\.json))]
            case .arc(let c, let radius, let start, let end, let clockwise):
                ["addArc", c.json, .double(radius), .double(start), .double(end), .bool(clockwise)]
            case .relativeArc(let c, let radius, let start, let delta):
                ["addRelativeArc", c.json, .double(radius), .double(start), .double(delta)]
            case .tangentArc(let a, let b, let radius): ["addArcTangent", a.json, b.json, .double(radius)]
            case .addPath(let ops, let t): ["addPath", .array(ops.map(\.json)), transform(t)]
            }
        }

        func apply(to path: CGMutablePath) {
            switch self {
            case .move(let p): path.move(to: p)
            case .line(let p): path.addLine(to: p)
            case .quad(let c, let p): path.addQuadCurve(to: p, control: c)
            case .cubic(let a, let b, let p): path.addCurve(to: p, control1: a, control2: b)
            case .close: path.closeSubpath()
            case .rect(let r): path.addRect(r)
            case .ellipse(let r): path.addEllipse(in: r)
            case .roundedRect(let r, let w, let h): path.addRoundedRect(in: r, cornerWidth: w, cornerHeight: h)
            case .lines(let points): path.addLines(between: points)
            case .arc(let c, let radius, let start, let end, let clockwise):
                path.addArc(center: c, radius: radius, startAngle: start, endAngle: end, clockwise: clockwise)
            case .relativeArc(let c, let radius, let start, let delta):
                path.addRelativeArc(center: c, radius: radius, startAngle: start, delta: delta)
            case .tangentArc(let a, let b, let radius): path.addArc(tangent1End: a, tangent2End: b, radius: radius)
            case .addPath(let ops, let t):
                let inner = CGMutablePath()
                ops.forEach { $0.apply(to: inner) }
                path.addPath(inner, transform: t)
            }
        }
    }

    nonisolated private static func transform(_ t: CGAffineTransform) -> J {
        [.double(t.a), .double(t.b), .double(t.c), .double(t.d), .double(t.tx), .double(t.ty)]
    }

    private static func paths(_ corpus: Corpus) throws {
        let p = { (x: CGFloat, y: CGFloat) in CGPoint(x: x, y: y) }
        let r = { (x: CGFloat, y: CGFloat, w: CGFloat, h: CGFloat) in CGRect(x: x, y: y, width: w, height: h) }
        let rotation = CGAffineTransform(translationX: 50, y: 40).rotated(by: .pi / 6).translatedBy(x: -50, y: -40)
        let cases: [(String, [Op])] = [
            ("rect", [.rect(r(10, 20, 30, 40))]),
            ("rect with negative size", [.rect(r(40, 60, -30, -40))]),
            ("rect with zero size", [.rect(r(5, 5, 0, 0))]),
            ("rect after an open subpath", [.move(p(0, 0)), .line(p(5, 5)), .rect(r(10, 20, 30, 40))]),
            ("ellipse", [.ellipse(r(10, 20, 30, 40))]),
            ("ellipse with negative size", [.ellipse(r(40, 60, -30, -40))]),
            ("ellipse with zero width", [.ellipse(r(10, 20, 0, 40))]),
            ("ellipse with fractional frame", [.ellipse(r(0.25, 0.5, 33.3, 12.7))]),
            ("rounded rect", [.roundedRect(r(10, 20, 100, 60), 12, 12)]),
            ("rounded rect with zero radius", [.roundedRect(r(10, 20, 100, 60), 0, 0)]),
            ("rounded rect with unequal radii", [.roundedRect(r(10, 20, 100, 60), 20, 8)]),
            ("rounded rect with full radius", [.roundedRect(r(10, 20, 100, 60), 50, 30)]),
            ("rounded rect with tiny radius", [.roundedRect(r(10, 20, 100, 60), 0.001, 0.001)]),
            ("rounded rect after an open subpath", [.move(p(0, 0)), .line(p(5, 5)), .roundedRect(r(10, 20, 100, 60), 12, 12)]),
            ("rounded rect after a point at its start", [.move(p(110, 50)), .roundedRect(r(10, 20, 100, 60), 12, 12)]),
            ("lines", [.lines([p(0, 0), p(10, 0), p(10, 10)])]),
            ("lines with one point", [.lines([p(3, 4)])]),
            ("lines with no points", [.lines([])]),
            ("lines after an open subpath", [.move(p(-5, -5)), .line(p(-1, -1)), .lines([p(0, 0), p(10, 0)])]),
            ("quad curve", [.move(p(0, 0)), .quad(p(50, 100), p(100, 0))]),
            ("cubic curve", [.move(p(0, 0)), .cubic(p(10, 50), p(90, 50), p(100, 0))]),
            ("close on empty path", [.close]),
            ("close twice", [.move(p(0, 0)), .line(p(10, 0)), .line(p(10, 10)), .close, .close]),
            ("line after close", [.move(p(0, 0)), .line(p(10, 0)), .close, .line(p(20, 20))]),
            ("move after move", [.move(p(0, 0)), .move(p(10, 10)), .line(p(20, 10))]),
            ("trailing move", [.move(p(0, 0)), .line(p(10, 10)), .move(p(100, 100))]),
            ("move only", [.move(p(7, 8))]),
            ("line without move", [.line(p(10, 10))]),
            ("arc quarter counterclockwise", [.arc(p(50, 50), 20, 0, .pi / 2, false)]),
            ("arc quarter clockwise", [.arc(p(50, 50), 20, 0, .pi / 2, true)]),
            ("arc 100 degrees", [.arc(p(50, 50), 20, 0, 100 * .pi / 180, false)]),
            ("arc half", [.arc(p(50, 50), 20, .pi, 0, false)]),
            ("arc full circle", [.arc(p(50, 50), 20, 0, 2 * .pi, false)]),
            ("arc more than a full circle", [.arc(p(50, 50), 20, 0, 3 * .pi, false)]),
            ("arc with equal angles", [.arc(p(50, 50), 20, 1, 1, false)]),
            ("arc with equal angles clockwise", [.arc(p(50, 50), 20, 1, 1, true)]),
            ("arc negative angles", [.arc(p(50, 50), 20, -.pi / 3, -.pi / 6, false)]),
            ("arc after an open subpath", [.move(p(0, 0)), .arc(p(50, 50), 20, 0, .pi / 2, false)]),
            ("arc with zero radius", [.move(p(0, 0)), .arc(p(50, 50), 0, 0, .pi / 2, false)]),
            ("relative arc 100 degrees", [.relativeArc(p(50, 50), 20, 0, 100 * .pi / 180)]),
            ("relative arc minus 45 degrees", [.relativeArc(p(50, 50), 20, .pi / 4, -.pi / 4)]),
            ("relative arc zero", [.relativeArc(p(50, 50), 20, 0, 0)]),
            ("tangent arc", [.move(p(0, 0)), .tangentArc(p(100, 0), p(100, 100), 20)]),
            ("tangent arc collinear", [.move(p(0, 0)), .tangentArc(p(50, 0), p(100, 0), 20)]),
            ("add path with rotation", [.addPath([.rect(r(20, 20, 60, 40)), .ellipse(r(30, 30, 10, 10))], rotation)]),
            ("add path after an open subpath", [.move(p(0, 0)), .line(p(5, 5)), .addPath([.move(p(10, 10)), .line(p(20, 20))], .identity)])
        ]
        var records: [J] = cases.map { (name, ops) -> J in
            let path = CGMutablePath()
            ops.forEach { $0.apply(to: path) }
            return ["name": .string(name), "ops": .array(ops.map(\.json)), "path": PathRecord.summary(path)]
        }
        var t = CGAffineTransform(translationX: 50, y: 50).rotated(by: .pi / 4).translatedBy(x: -50, y: -50)
        let rotatedRounded = CGPath(roundedRect: r(25, 25, 50, 50), cornerWidth: 6, cornerHeight: 6, transform: &t)
        let constructors: [(String, J, CGPath)] = [
            ("CGPath(rect:)", ["CGPath.rect", r(10, 20, 30, 40).json, .null], CGPath(rect: r(10, 20, 30, 40), transform: nil)),
            ("CGPath(ellipseIn:)", ["CGPath.ellipse", r(10, 20, 30, 40).json, .null], CGPath(ellipseIn: r(10, 20, 30, 40), transform: nil)),
            ("CGPath(roundedRect:)", ["CGPath.roundedRect", r(10, 20, 100, 60).json, 12.0, 12.0, .null],
             CGPath(roundedRect: r(10, 20, 100, 60), cornerWidth: 12, cornerHeight: 12, transform: nil)),
            ("CGPath(roundedRect:) zero radius", ["CGPath.roundedRect", r(10, 20, 100, 60).json, 0.0, 0.0, .null],
             CGPath(roundedRect: r(10, 20, 100, 60), cornerWidth: 0, cornerHeight: 0, transform: nil)),
            ("CGPath(roundedRect:) rotated, as the diamond pointer halo", ["CGPath.roundedRect", r(25, 25, 50, 50).json, 6.0, 6.0, transform(t)],
             rotatedRounded)
        ]
        records += constructors.map { (name, op, path) -> J in ["name": .string(name), "ops": [op], "path": PathRecord.summary(path)] }
        try corpus.lines("platform/cgpath.jsonl", records)
    }

    // MARK: Rectangle math

    private static func rects(_ corpus: Corpus) throws {
        let samples: [CGRect] = [
            CGRect(x: 0.4, y: 0.6, width: 10.2, height: 10.3), CGRect(x: -0.5, y: -0.5, width: 1, height: 1),
            CGRect(x: 1.5, y: 2.5, width: 0, height: 0), CGRect(x: 10, y: 10, width: -5.5, height: -2.25),
            CGRect(x: -3.7, y: 4.2, width: 0.1, height: 0.1), CGRect(x: 2, y: 3, width: 4, height: 5),
            CGRect(x: 1e7 + 0.5, y: -1e7 - 0.5, width: 0.5, height: 0.5), CGRect(x: 0.999999, y: 1.000001, width: 2, height: 2),
            CGRect(x: 4503599627370495.5, y: 0, width: 1, height: 1), .null, .zero, .infinite
        ]
        var records: [J] = samples.map { (rect) -> J in
            ["rect": rect.json, "integral": rect.integral.json, "standardized": rect.standardized.json,
             "isNull": .bool(rect.isNull), "isEmpty": .bool(rect.isEmpty), "isInfinite": .bool(rect.isInfinite),
             "insetBy(-2.5,-2.5)": rect.insetBy(dx: -2.5, dy: -2.5).json, "insetBy(6,6)": rect.insetBy(dx: 6, dy: 6).json,
             "minX": .double(rect.minX), "maxX": .double(rect.maxX), "midX": .double(rect.midX)]
        }
        let pairs: [(CGRect, CGRect)] = [
            (CGRect(x: 0, y: 0, width: 10, height: 10), CGRect(x: 5, y: 5, width: 10, height: 10)),
            (CGRect(x: 0, y: 0, width: 10, height: 10), CGRect(x: 20, y: 20, width: 5, height: 5)),
            (CGRect(x: 0, y: 0, width: 10, height: 10), CGRect(x: 10, y: 0, width: 5, height: 5)),
            (CGRect(x: 0, y: 0, width: 10, height: 10), .null), (.null, .null),
            (CGRect(x: 0, y: 0, width: -10, height: 10), CGRect(x: -5, y: 0, width: 2, height: 2)),
            (CGRect(x: 1, y: 1, width: 0, height: 0), CGRect(x: 3, y: 3, width: 0, height: 0))
        ]
        records += pairs.map { (a, b) -> J in
            ["a": a.json, "b": b.json, "union": a.union(b).json, "intersection": a.intersection(b).json,
             "intersects": .bool(a.intersects(b)), "containsRect": .bool(a.contains(b))]
        }
        let rotation = CGAffineTransform(translationX: 50, y: 30).rotated(by: 0.3).translatedBy(x: -50, y: -30)
        records += [CGRect(x: 10, y: 10, width: 80, height: 40), CGRect(x: 0, y: 0, width: 0, height: 0), .null].map { (rect) -> J in
            ["rect": rect.json, "transform": transform(rotation), "applying": rect.applying(rotation).json]
        }
        try corpus.lines("platform/rect.jsonl", records)
    }

    // MARK: Hatch positions

    private static func hatch(_ corpus: Corpus) throws {
        let frames: [(Rect, Double)] = [
            (Rect(x: 10, y: 20, width: 120, height: 80), 3), (Rect(x: 0.1, y: 0.2, width: 33.3, height: 22.2), 2.5),
            (Rect(x: -50.5, y: 10, width: 200.75, height: 0), 1), (Rect(x: 0, y: 0, width: 5, height: 5), 0.25),
            (Rect(x: 1_000.1, y: 0, width: 300_000.3, height: 200_000.7), 3),
            (Rect(x: -9_999_000.7, y: 3, width: 1_000_000, height: 1_000_000), 12),
            (Rect(x: 0.7, y: 0, width: 999_999.9, height: 3), 256), (Rect(x: 1e-3, y: 0, width: 0.3, height: 0.1), 2.1)
        ]
        var agreesWithFusedMultiplyAdd = true
        let records: [J] = frames.map { (frame, width) -> J in
            let positions = Array(SceneRenderer.hatchPositions(frame: frame, width: width))
            let spacing = max(6, width * 3, (frame.width + frame.height * 2) / 4_096)
            let start = frame.x - frame.height
            let fused = positions.indices.map { start.addingProduct(Double($0), spacing) }
            let separate = positions.indices.map { start + Double($0) * spacing }
            var repeated = [start]
            while repeated.count < positions.count { repeated.append(repeated.last! + spacing) }
            if fused != positions { agreesWithFusedMultiplyAdd = false }
            return [
                "frame": frame.json, "width": .double(width), "spacing": .double(spacing), "start": .double(start),
                "end": .double(frame.maxX + frame.height), "count": .int(positions.count),
                "matchesFusedMultiplyAdd": .bool(fused == positions),
                "matchesMultiplyThenAdd": .bool(separate == positions), "matchesRepeatedAddition": .bool(repeated == positions),
                "first": positions.prefix(64).map(\.json).asArray, "last": positions.suffix(64).map(\.json).asArray
            ]
        }
        guard agreesWithFusedMultiplyAdd else { throw ExportFailure("Hatch positions no longer follow start.addingProduct(index, spacing).") }
        try corpus.lines("platform/hatch.jsonl", records)
    }

    // MARK: libm samples

    private static func math(_ corpus: Corpus) throws {
        var random = SeededRandom(seed: 0x6D61_7468)
        var inputs: [Double] = [0, -0.0, 1e-9, 0.5, 1, -1, .pi / 8, .pi / 6, .pi / 4, .pi / 3, .pi / 2, .pi, 3 * .pi / 4, 2 * .pi,
                                -.pi / 4, 100, 1e6, 0.1, 2.5]
        for _ in 0..<80 { inputs.append(random.range(-10, 10)) }
        var records: [J] = inputs.map { (x) -> J in
            ["x": .double(x), "sin": .double(sin(x)), "cos": .double(cos(x)), "tan": .double(tan(x)), "exp": .double(exp(x)),
             "log": x > 0 ? .double(log(x)) : .null, "sqrt": x >= 0 ? .double(x.squareRoot()) : .null,
             "pow(x,2.4)": x >= 0 ? .double(pow(x, 2.4)) : .null, "rounded": .double(x.rounded()),
             "roundedToEven": .double(x.rounded(.toNearestOrEven))]
        }
        var pairs: [(Double, Double)] = [(0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, -1), (3, 4), (1e-300, 1e-300),
                                         (-0.0, -1), (0, -0.0), (-0.0, -0.0), (1e300, 1e300)]
        for _ in 0..<60 { pairs.append((random.range(-500, 500), random.range(-500, 500))) }
        records += pairs.map { (y, x) -> J in
            ["y": .double(y), "x": .double(x), "atan2": .double(atan2(y, x)), "hypot": .double(hypot(x, y))]
        }
        for value in [0.5, 1.5, 2.5, -0.5, -1.5, -2.5, 0.49999999999999994] {
            records.append(["x": .double(value), "rounded": .double(value.rounded()), "roundedToEven": .double(value.rounded(.toNearestOrEven))])
        }
        try corpus.lines("platform/math.jsonl", records)
    }
}

extension Array where Element == J {
    var asArray: J { .array(self) }
}

extension Data {
    /// Replaces every `byte` with `replacement`. The fixtures use ASCII placeholders that occur once.
    func replacingByte(_ byte: UInt8, with replacement: UInt8) -> Data {
        Data(map { $0 == byte ? replacement : $0 })
    }
}
