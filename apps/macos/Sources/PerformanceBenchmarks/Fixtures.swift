import AnnotationCore
import AppKit
import ImageIO
import UniformTypeIdentifiers

enum BenchmarkFailure: Error, CustomStringConvertible {
    case invalid(String)
    var description: String {
        switch self { case .invalid(let message): message }
    }
}

func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    guard condition() else { throw BenchmarkFailure.invalid(message) }
}

enum Fixtures {
    static let seed: UInt64 = 0xA660_2026
    static let width = 1_920
    static let height = 1_080

    static func id(_ index: Int) -> UUID {
        let n = UInt64(index)
        return UUID(uuid: (0xA6, 0x60, 0x20, 0x26, 0, 0, 0x40, 0, 0x80, 0,
                           UInt8(truncatingIfNeeded: n >> 40), UInt8(truncatingIfNeeded: n >> 32),
                           UInt8(truncatingIfNeeded: n >> 24), UInt8(truncatingIfNeeded: n >> 16),
                           UInt8(truncatingIfNeeded: n >> 8), UInt8(truncatingIfNeeded: n)))
    }

    static func elements(_ count: Int, ellipsesOnly: Bool = false) -> [Element] {
        let tools: [Tool] = [.rectangle, .ellipse, .diamond, .arrow, .pen, .highlighter,
                             .text, .counter, .grid, .axes]
        return (0..<count).map { index in
            let tool: Tool = ellipsesOnly ? .ellipse : tools[index % tools.count]
            let x = Double((index * 73) % 1_800 + 20)
            let y = Double((index * 97) % 950 + 20)
            var style = Style()
            style.roughness = ellipsesOnly ? Double([2, 6, 12][index % 3]) : Double(index % 3) * 2
            style.width = 2
            style.fontSize = 14
            if tool.supportsFill && index % 4 == 0 { style.fill = RGBA(0.2, 0.4, 0.8, 0.2) }
            let points = tool.isFreehand || tool.isConnector
                ? (0..<24).map { Point(x + Double($0) * 3, y + sin(Double($0) * 0.2) * 25) } : []
            var element = Element(id: id(index), tool: tool,
                                  frame: Rect(x: x, y: y, width: 75, height: 55),
                                  points: points, text: tool == .counter ? "\(index % 99)" : "Owned \(index)",
                                  style: style)
            element.seed = seed &+ UInt64(index)
            element.geometryToken = id(index + 1_000_000)
            element.appendEpoch = id(index + 2_000_000)
            return element
        }
    }

    static func stroke(_ count: Int) -> Element {
        let points = (0..<count).map { index in
            let t = Double(index) / Double(max(1, count - 1))
            return Point(10 + t * 1_900, 540 + sin(t * .pi * 24) * 400)
        }
        var stroke = Element(id: id(900_000), tool: .pen, points: points)
        stroke.seed = seed
        stroke.geometryToken = id(3_000_000)
        stroke.appendEpoch = id(4_000_000)
        return stroke
    }

    static func context(width: Int = width, height: Int = height) throws -> CGContext {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: width * 4, space: space,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw BenchmarkFailure.invalid("Cannot create owned bitmap context.")
        }
        return context
    }

    static func source(width: Int = width, height: Int = height) throws -> CGImage {
        let context = try context(width: width, height: height)
        for y in stride(from: 0, to: height, by: 32) {
            for x in stride(from: 0, to: width, by: 32) {
                context.setFillColor(CGColor(red: CGFloat(x % 255) / 255,
                                            green: CGFloat(y % 255) / 255, blue: 0.4, alpha: 1))
                context.fill(CGRect(x: x, y: y, width: 32, height: 32))
            }
        }
        guard let image = context.makeImage() else { throw BenchmarkFailure.invalid("Owned image unavailable.") }
        return image
    }

    static func png(_ image: CGImage) throws -> Data {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else {
            throw BenchmarkFailure.invalid("PNG destination unavailable.")
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw BenchmarkFailure.invalid("PNG encoding failed.") }
        return data as Data
    }

    static func images(_ count: Int, data: Data) -> [Element] {
        (0..<count).map { index in
            var image = Element(id: id(index + 800_000), tool: .image,
                                frame: Rect(x: Double(index * 83 % 1_600), y: Double(index * 61 % 800),
                                            width: 256, height: 256))
            image.imagePNG = data
            return image
        }
    }

    static func checksum(_ context: CGContext) throws -> UInt64 {
        guard let data = context.data else { throw BenchmarkFailure.invalid("Bitmap bytes unavailable.") }
        let bytes = data.bindMemory(to: UInt8.self, capacity: context.bytesPerRow * context.height)
        var result: UInt64 = 14695981039346656037
        for index in 0..<(context.bytesPerRow * context.height) {
            result = (result ^ UInt64(bytes[index])) &* 1099511628211
        }
        return result
    }
}
