import AnnotationCore
import CoreGraphics
import CryptoKit
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// A JSON value for corpus records. Every file is written by `JSONEncoder` with sorted keys.
indirect enum J: Encodable, ExpressibleByStringLiteral, ExpressibleByIntegerLiteral, ExpressibleByFloatLiteral,
    ExpressibleByBooleanLiteral, ExpressibleByArrayLiteral, ExpressibleByDictionaryLiteral, ExpressibleByNilLiteral {
    case null
    case bool(Bool)
    case int(Int)
    case uint(UInt64)
    case double(Double)
    case string(String)
    case array([J])
    case object([String: J])
    /// Any `Encodable` model value, written exactly as `JSONEncoder` writes it.
    case value(any Encodable)

    init(stringLiteral value: String) { self = .string(value) }
    init(integerLiteral value: Int) { self = .int(value) }
    init(floatLiteral value: Double) { self = .double(value) }
    init(booleanLiteral value: Bool) { self = .bool(value) }
    init(arrayLiteral elements: J...) { self = .array(elements) }
    init(dictionaryLiteral elements: (String, J)...) {
        self = .object(Dictionary(elements) { _, _ in preconditionFailure("Duplicate key in a corpus record.") })
    }
    init(nilLiteral: ()) { self = .null }

    private struct CodingName: CodingKey {
        let stringValue: String
        init(_ value: String) { stringValue = value }
        init?(stringValue: String) { self.stringValue = stringValue }
        var intValue: Int? { nil }
        init?(intValue: Int) { nil }
    }

    func encode(to encoder: Encoder) throws {
        switch self {
        case .array(let items):
            var container = encoder.unkeyedContainer()
            for item in items { try container.encode(item) }
        case .object(let fields):
            var container = encoder.container(keyedBy: CodingName.self)
            for (key, value) in fields { try container.encode(value, forKey: CodingName(key)) }
        case .null:
            var container = encoder.singleValueContainer()
            try container.encodeNil()
        case .bool(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .int(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .uint(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .double(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .string(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        case .value(let value):
            var container = encoder.singleValueContainer()
            try container.encode(value)
        }
    }

    /// Adds or replaces fields of an object.
    func merging(_ other: [String: J]) -> J {
        guard case .object(let fields) = self else { preconditionFailure("Only objects can be merged.") }
        return .object(fields.merging(other) { $1 })
    }
}

protocol JSONRepresentable { var json: J { get } }
extension Double: JSONRepresentable { var json: J { .double(self) } }
extension Int: JSONRepresentable { var json: J { .int(self) } }
extension UInt64: JSONRepresentable { var json: J { .uint(self) } }
extension Bool: JSONRepresentable { var json: J { .bool(self) } }
extension String: JSONRepresentable { var json: J { .string(self) } }
extension UUID: JSONRepresentable { var json: J { .string(uuidString) } }
extension Point: JSONRepresentable { var json: J { [.double(x), .double(y)] } }
extension Rect: JSONRepresentable {
    var json: J { ["x": .double(x), "y": .double(y), "width": .double(width), "height": .double(height)] }
}
extension CGPoint: JSONRepresentable { var json: J { [.double(Double(x)), .double(Double(y))] } }
extension CGSize: JSONRepresentable { var json: J { ["width": .double(Double(width)), "height": .double(Double(height))] } }
extension CGRect: JSONRepresentable {
    var json: J {
        ["x": .double(Double(origin.x)), "y": .double(Double(origin.y)),
         "width": .double(Double(size.width)), "height": .double(Double(size.height))]
    }
}
extension Array: JSONRepresentable where Element: JSONRepresentable { var json: J { .array(map(\.json)) } }
extension Optional: JSONRepresentable where Wrapped: JSONRepresentable { var json: J { self?.json ?? .null } }

/// Collects corpus files in memory, then writes them with a manifest or compares them with the files on disk.
@MainActor
final class Corpus {
    private(set) var files: [String: Data] = [:]
    private(set) var sections: [String: String] = [:]

    nonisolated static func encoder(pretty: Bool) -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = pretty ? [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
            : [.sortedKeys, .withoutEscapingSlashes]
        encoder.nonConformingFloatEncodingStrategy = .convertToString(
            positiveInfinity: "+Infinity", negativeInfinity: "-Infinity", nan: "NaN")
        return encoder
    }

    func describe(_ section: String, _ description: String) { sections[section] = description }

    func data(_ path: String, _ data: Data) {
        precondition(files[path] == nil, "Two corpus files use \(path).")
        precondition(!path.hasPrefix("/") && !path.contains(".."), "Corpus paths are relative: \(path)")
        files[path] = data
    }

    /// One readable JSON document.
    func json(_ path: String, _ value: J) throws {
        var bytes = try Self.encoder(pretty: true).encode(value)
        bytes.append(0x0A)
        data(path, bytes)
    }

    /// JSON Lines: one compact record per line, in the given order.
    func lines(_ path: String, _ records: [J]) throws {
        precondition(!records.isEmpty, "\(path) has no records.")
        let encoder = Self.encoder(pretty: false)
        var bytes = Data()
        for record in records {
            bytes.append(try encoder.encode(record))
            bytes.append(0x0A)
        }
        data(path, bytes)
    }

    nonisolated static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    func manifest(source: J, environment: J) throws -> Data {
        let entries: [J] = files.keys.sorted().map { path in
            ["path": .string(path), "sha256": .string(Self.sha256(files[path]!)), "bytes": .int(files[path]!.count)]
        }
        let value: J = [
            "format": 1,
            "generator": "swift run --package-path apps/macos ConformanceExport (apps/macos/Tools/Conformance)",
            "source": source,
            "environment": environment,
            "sections": .object(sections.mapValues { .string($0) }),
            "fileCount": .int(files.count),
            "files": .array(entries)
        ]
        var bytes = try Self.encoder(pretty: true).encode(value)
        bytes.append(0x0A)
        return bytes
    }
}

/// Fixed identities so every run writes the same files.
enum Fixture {
    static func id(_ number: Int) -> UUID {
        let digits = String(number)
        precondition(digits.count <= 12)
        return UUID(uuidString: "00000000-0000-4000-8000-" + String(repeating: "0", count: 12 - digits.count) + digits)!
    }

    /// An element with a fixed identity, geometry token and append epoch.
    static func element(_ number: Int, _ tool: Tool, frame: Rect = .zero, points: [Point] = [], text: String = "",
                        style: Style? = nil, _ configure: (inout Element) -> Void = { _ in }) -> Element {
        var element = Element(id: id(number), tool: tool, frame: frame, points: points, text: text,
                              style: style ?? .defaults(for: tool))
        element.geometryToken = id(100_000_000 + number)
        element.appendEpoch = id(200_000_000 + number)
        configure(&element)
        return element
    }

    static func style(_ tool: Tool, _ configure: (inout Style) -> Void) -> Style {
        var style = Style.defaults(for: tool)
        configure(&style)
        return style
    }

    /// A small PNG drawn and encoded here: 4 x 3 pixels in four colors, one of them half transparent.
    static func smallPNG() throws -> Data {
        let colors: [(UInt8, UInt8, UInt8, UInt8)] = [
            (230, 60, 50, 255), (40, 160, 90, 255), (40, 110, 220, 255), (250, 250, 250, 128)
        ]
        var pixels = [UInt8]()
        for row in 0..<3 {
            for column in 0..<4 {
                let color = colors[(row + column) % colors.count]
                let alpha = Double(color.3) / 255
                pixels += [UInt8((Double(color.0) * alpha).rounded()), UInt8((Double(color.1) * alpha).rounded()),
                           UInt8((Double(color.2) * alpha).rounded()), color.3]
            }
        }
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let provider = CGDataProvider(data: Data(pixels) as CFData),
              let image = CGImage(width: 4, height: 3, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: 16,
                                  space: space, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
        else { throw ExportFailure("The fixture image could not be created.") }
        return try encodePNG(image)
    }

    static func encodePNG(_ image: CGImage) throws -> Data {
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil) else {
            throw ExportFailure("The PNG encoder is unavailable.")
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw ExportFailure("A PNG could not be encoded.") }
        return output as Data
    }
}

struct ExportFailure: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

/// SplitMix64, so fixture strokes and points are the same on every run and easy to reproduce elsewhere.
struct SeededRandom {
    private var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var value = state
        value = (value ^ (value >> 30)) &* 0xBF58_476D_1CE4_E5B9
        value = (value ^ (value >> 27)) &* 0x94D0_49BB_1331_11EB
        return value ^ (value >> 31)
    }
    /// A double in [0, 1) from the top 53 bits.
    mutating func unit() -> Double { Double(next() >> 11) / 9_007_199_254_740_992 }
    mutating func range(_ low: Double, _ high: Double) -> Double { low + (high - low) * unit() }
}

/// The elements of a Core Graphics path, as `applyWithBlock` reports them.
enum PathRecord {
    static func elements(_ path: CGPath) -> J {
        var items: [J] = []
        path.applyWithBlock { pointer in
            let element = pointer.pointee
            func points(_ count: Int) -> J { .array((0..<count).map { element.points[$0].json }) }
            switch element.type {
            case .moveToPoint: items.append(["move", points(1)])
            case .addLineToPoint: items.append(["line", points(1)])
            case .addQuadCurveToPoint: items.append(["quad", points(2)])
            case .addCurveToPoint: items.append(["cubic", points(3)])
            case .closeSubpath: items.append(["close"])
            @unknown default: items.append(["unknown"])
            }
        }
        return .array(items)
    }

    static func summary(_ path: CGPath) -> J {
        [
            "elements": elements(path),
            "boundingBox": path.boundingBox.json,
            "boundingBoxOfPath": path.boundingBoxOfPath.json,
            "currentPoint": path.isEmpty ? .null : path.currentPoint.json,
            "isEmpty": .bool(path.isEmpty)
        ]
    }
}

/// Error details that a port can compare: the message people see and, for decoding errors, Foundation's detail.
enum ErrorRecord {
    static func describe(_ error: Error) -> J {
        var fields: [String: J] = ["message": .string(error.localizedDescription)]
        if let canvas = error as? CanvasError {
            fields["type"] = "CanvasError"
            fields["case"] = .string(caseName(canvas))
        } else if let library = error as? ShapeLibraryError {
            fields["type"] = "ShapeLibraryError"
            fields["case"] = .string(caseName(library))
        } else if let decoding = error as? DecodingError {
            fields["type"] = "DecodingError"
            let context: DecodingError.Context
            switch decoding {
            case .typeMismatch(let type, let value):
                fields["case"] = "typeMismatch"; fields["expectedType"] = .string(String(describing: type)); context = value
            case .valueNotFound(let type, let value):
                fields["case"] = "valueNotFound"; fields["expectedType"] = .string(String(describing: type)); context = value
            case .keyNotFound(let key, let value):
                fields["case"] = "keyNotFound"; fields["key"] = .string(key.stringValue); context = value
            case .dataCorrupted(let value):
                fields["case"] = "dataCorrupted"; context = value
            @unknown default:
                fields["case"] = "unknown"
                return .object(fields)
            }
            fields["codingPath"] = .array(context.codingPath.map { key in
                key.intValue.map { .int($0) } ?? .string(key.stringValue)
            })
            fields["debugDescription"] = .string(context.debugDescription)
            if let underlying = context.underlyingError as NSError? {
                var detail: [String: J] = ["domain": .string(underlying.domain), "code": .int(underlying.code)]
                if let text = underlying.userInfo[NSDebugDescriptionErrorKey] as? String { detail["debugDescription"] = .string(text) }
                fields["underlying"] = .object(detail)
            }
        } else {
            let typeName = String(describing: type(of: error))
            let error = error as NSError
            fields["type"] = .string(typeName)
            fields["domain"] = .string(error.domain)
            fields["code"] = .int(error.code)
        }
        return .object(fields)
    }

    private static func caseName(_ value: Any) -> String {
        let text = String(describing: value)
        return String(text.prefix { $0 != "(" })
    }
}

/// Input bytes for a record: the text when the bytes are UTF-8 that converts back exactly, otherwise Base64.
func inputRecord(_ data: Data) -> J {
    if let text = String(data: data, encoding: .utf8), Data(text.utf8) == data { return ["utf8": .string(text)] }
    return ["base64": .string(data.base64EncodedString())]
}

/// Runs `body` and records either its result or the error it throws.
func outcome(_ body: () throws -> J) -> J {
    do { return ["ok": try body()] } catch { return ["error": ErrorRecord.describe(error)] }
}
