import AnnotationCore
import CoreImage
import Foundation
@testable import PresentationRendering

/// Fixed input images through CaptureCompositor: Blur, Pixelate, Distort, covers, image layers and privacy crops.
@MainActor
enum EffectsExport {
    private struct Run {
        let name: String
        let input: String
        let pixelsPerPoint: Double
        let elements: [Element]
        var regionsOnly = false
        var includeImageLayers = false
        var privacyRegion: Rect?
    }

    static func run(_ corpus: Corpus) throws {
        corpus.describe("effects", "Input PNGs and CaptureCompositor.image output, rendered into sRGB RGBA8 by a Core Image context configured like the app's (Metal, default working space: extended linear sRGB in half float). See context.json.")
        let inputs = try ["pattern": pattern(), "noise": noise()]
        for (name, image) in inputs { corpus.data("effects/inputs/\(name).png", try PNGExporter.encode(image)) }
        // The same options as SnapshotExporter. On Apple silicon even the software-renderer option runs on Metal.
        let context = CIContext(options: [.cacheIntermediates: false])
        guard let sRGB = CGColorSpace(name: CGColorSpace.sRGB) else { throw ExportFailure("sRGB is unavailable.") }
        try corpus.json("effects/context.json", [
            "options": "CIContext(options: [.cacheIntermediates: false]), as SnapshotExporter uses",
            "workingColorSpace": .string((context.workingColorSpace?.name as String?) ?? "unknown"),
            "workingFormat": .string(context.workingFormat == .RGBAh ? "RGBAh (16-bit float per channel)" : "\(context.workingFormat.rawValue)"),
            "outputFormat": "RGBA8 premultiplied, sRGB",
            "input": "CIImage(cgImage:) from the sRGB input PNG; Core Image converts it to the working space before any filter",
            "filters": [
                "blur": "CIGaussianBlur, inputRadius = amount × pixelsPerPoint, on the scene clamped to its extent, cropped to the region",
                "pixelate": "CIPixellate, inputScale = amount × pixelsPerPoint, inputCenter = region center in pixels",
                "distort": "CITwirlDistortion, inputCenter = region center, inputRadius = max(region width, region height), inputAngle = amount / 100 × 2π",
                "defaultAmount": 18.0,
                "zeroAmount": "An amount of 0 leaves the pixels unchanged."
            ],
            "order": "Image layers, then the drawing layer, then the privacy crop (outside turns black), then covers blacken their pixels, then each pixel effect samples the scene, then covers are drawn again on top. Regions are pixelRect: integral rectangles clipped to the image, with y flipped."
        ])
        var records: [J] = []
        for run in runs() {
            let source = inputs[run.input]!
            let pixelSize = CGSize(width: source.width, height: source.height)
            let logical = CGSize(width: pixelSize.width / run.pixelsPerPoint, height: pixelSize.height / run.pixelsPerPoint)
            let output = try CaptureCompositor.image(source: CIImage(cgImage: source), logicalSize: logical, elements: run.elements,
                                                     regionsOnly: run.regionsOnly, includeImageLayers: run.includeImageLayers,
                                                     privacyRegion: run.privacyRegion)
            guard let rendered = context.createCGImage(output, from: output.extent, format: .RGBA8, colorSpace: sRGB) else {
                throw ExportFailure("Core Image could not render \(run.name).")
            }
            let file = "\(run.name).png"
            corpus.data("effects/\(file)", try PNGExporter.encode(rendered))
            records.append([
                "name": .string(run.name), "input": .string("inputs/\(run.input).png"), "output": .string(file),
                "pixelsPerPoint": .double(run.pixelsPerPoint), "logicalSize": logical.json, "elements": .value(run.elements),
                "regionsOnly": .bool(run.regionsOnly), "includeImageLayers": .bool(run.includeImageLayers),
                "privacyRegion": run.privacyRegion?.json ?? .null,
                "pixelRegions": .array(try run.elements.filter { $0.tool.isProtection }.map { element in
                    ["id": .string(element.id.uuidString), "tool": .string(element.tool.rawValue),
                     "pixels": try CaptureCompositor.pixelRect(element.frame, logicalSize: logical,
                                                               imageExtent: CGRect(origin: .zero, size: pixelSize)).json]
                })
            ])
        }
        try corpus.lines("effects/index.jsonl", records)
        try pixelRects(corpus)
    }

    /// 192 x 128 opaque test pattern: a horizontal gradient, a checkerboard, three discs and thin lines.
    private static func pattern() throws -> CGImage {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: 192, height: 128, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let gradient = CGGradient(colorSpace: space, colorComponents: [0.1, 0.2, 0.6, 1, 0.9, 0.5, 0.1, 1], locations: [0, 1], count: 2)
        else { throw ExportFailure("The pattern could not be drawn.") }
        context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: 192, y: 0), options: [])
        for row in 0..<8 {
            for column in 0..<8 where (row + column) % 2 == 0 {
                context.setFillColor(CGColor(gray: 1, alpha: 1))
                context.fill(CGRect(x: 8 + column * 6, y: 72 + row * 6, width: 6, height: 6))
            }
        }
        for (index, color) in [(0.94, 0.27, 0.22), (0.28, 0.69, 0.4), (0.96, 0.89, 0.22)].enumerated() {
            context.setFillColor(CGColor(srgbRed: color.0, green: color.1, blue: color.2, alpha: 1))
            context.fillEllipse(in: CGRect(x: 70 + index * 36, y: 30 + index * 20, width: 40, height: 40))
        }
        context.setStrokeColor(CGColor(gray: 1, alpha: 1))
        context.setLineWidth(1)
        for offset in stride(from: 0, through: 60, by: 6) {
            context.move(to: CGPoint(x: 120 + offset, y: 0)); context.addLine(to: CGPoint(x: 192, y: 72 - offset))
        }
        context.strokePath()
        guard let image = context.makeImage() else { throw ExportFailure("The pattern could not be drawn.") }
        return image
    }

    /// 96 x 64 opaque pixels from SplitMix64 with seed 0x6E6F697365.
    private static func noise() throws -> CGImage {
        var random = SeededRandom(seed: 0x6E6F_6973_65)
        var bytes = [UInt8]()
        for _ in 0..<(96 * 64) {
            let value = random.next()
            bytes += [UInt8(value & 0xFF), UInt8((value >> 8) & 0xFF), UInt8((value >> 16) & 0xFF), 255]
        }
        guard let space = CGColorSpace(name: CGColorSpace.sRGB), let provider = CGDataProvider(data: Data(bytes) as CFData),
              let image = CGImage(width: 96, height: 64, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: 96 * 4, space: space,
                                  bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue), provider: provider,
                                  decode: nil, shouldInterpolate: false, intent: .defaultIntent)
        else { throw ExportFailure("The noise image could not be made.") }
        return image
    }

    private static func effect(_ number: Int, _ tool: Tool, _ frame: Rect, _ amount: Double?) -> Element {
        Fixture.element(number, tool, frame: frame, style: Fixture.style(tool) { $0.effectAmount = amount })
    }

    private static func runs() -> [Run] {
        let region = Rect(x: 24, y: 20, width: 96, height: 64)
        var runs: [Run] = []
        for amount in [0.0, 5, 50, 100] { runs.append(Run(name: "blur-\(Int(amount))", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_001, .blur, region, amount)])) }
        runs.append(Run(name: "blur-default-18", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_002, .blur, region, nil)]))
        runs.append(Run(name: "blur-18-at-2x", input: "pattern", pixelsPerPoint: 2, elements: [effect(10_003, .blur, Rect(x: 12, y: 10, width: 48, height: 32), 18)]))
        runs.append(Run(name: "blur-at-the-edge", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_004, .blur, Rect(x: 0, y: 0, width: 70, height: 50), 18)]))
        for amount in [4.0, 18, 50] { runs.append(Run(name: "pixelate-\(Int(amount))", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_010, .pixelate, region, amount)])) }
        runs.append(Run(name: "pixelate-18-at-2x", input: "pattern", pixelsPerPoint: 2, elements: [effect(10_011, .pixelate, Rect(x: 12, y: 10, width: 48, height: 32), 18)]))
        runs.append(Run(name: "pixelate-fractional-region", input: "pattern", pixelsPerPoint: 1,
                        elements: [effect(10_012, .pixelate, Rect(x: 10.3, y: 7.6, width: 50.5, height: 33.3), 9)]))
        for amount in [2.0, 50, 100] { runs.append(Run(name: "distort-\(Int(amount))", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_020, .distort, region, amount)])) }
        runs.append(Run(name: "distort-50-at-2x", input: "pattern", pixelsPerPoint: 2, elements: [effect(10_021, .distort, Rect(x: 12, y: 10, width: 48, height: 32), 50)]))
        runs.append(Run(name: "cover-fractional", input: "pattern", pixelsPerPoint: 1,
                        elements: [Fixture.element(10_030, .cover, frame: Rect(x: 30.4, y: 20.6, width: 50.3, height: 30.2))]))
        runs.append(Run(name: "cover-over-blur", input: "pattern", pixelsPerPoint: 1, elements: [
            Fixture.element(10_031, .cover, frame: Rect(x: 60, y: 40, width: 60, height: 40)), effect(10_032, .blur, Rect(x: 20, y: 20, width: 100, height: 60), 18)]))
        runs.append(Run(name: "distort-beside-cover", input: "pattern", pixelsPerPoint: 1, elements: [
            effect(10_033, .distort, Rect(x: 60, y: 20, width: 80, height: 60), 60), Fixture.element(10_034, .cover, frame: Rect(x: 100, y: 30, width: 40, height: 40))]))
        runs.append(Run(name: "blur-regions-only", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_040, .blur, region, 18)], regionsOnly: true))
        runs.append(Run(name: "privacy-region", input: "pattern", pixelsPerPoint: 1, elements: [effect(10_041, .blur, region, 18)],
                        privacyRegion: Rect(x: 16.5, y: 16.5, width: 140, height: 90)))
        var image = Fixture.element(10_050, .image, frame: Rect(x: 40, y: 30, width: 48, height: 36), style: Fixture.style(.image) { $0.opacity = 0.5 })
        image.imagePNG = try? Fixture.smallPNG()
        image.rotation = 0.3
        runs.append(Run(name: "image-layer", input: "pattern", pixelsPerPoint: 1, elements: [image, effect(10_051, .blur, Rect(x: 100, y: 60, width: 60, height: 40), 6)],
                        includeImageLayers: true))
        runs.append(Run(name: "noise-pixelate-8", input: "noise", pixelsPerPoint: 1, elements: [effect(10_060, .pixelate, Rect(x: 8, y: 8, width: 64, height: 40), 8)]))
        runs.append(Run(name: "noise-blur-3", input: "noise", pixelsPerPoint: 1, elements: [effect(10_061, .blur, Rect(x: 8, y: 8, width: 64, height: 40), 3)]))
        return runs
    }

    private static func pixelRects(_ corpus: Corpus) throws {
        var records: [J] = []
        let rects = [Rect(x: 10, y: 10, width: 50, height: 30), Rect(x: 10.3, y: 7.6, width: 50.5, height: 33.3), Rect(x: -5, y: -5, width: 20, height: 20),
                     Rect(x: 180, y: 120, width: 40, height: 40), Rect(x: 0.5, y: 0.5, width: 0, height: 0), Rect(x: 300, y: 300, width: 10, height: 10)]
        for rect in rects {
            for (logical, extent) in [(CGSize(width: 192, height: 128), CGRect(x: 0, y: 0, width: 192, height: 128)),
                                      (CGSize(width: 96, height: 64), CGRect(x: 0, y: 0, width: 192, height: 128)),
                                      (CGSize(width: 1512, height: 982), CGRect(x: 0, y: 0, width: 3024, height: 1964)),
                                      (CGSize(width: 100, height: 100), CGRect(x: 0, y: 0, width: 125, height: 125))] {
                records.append(["rect": rect.json, "logicalSize": logical.json, "imageExtent": extent.json,
                                "pixels": outcome { try CaptureCompositor.pixelRect(rect, logicalSize: logical, imageExtent: extent).json }])
            }
        }
        try corpus.lines("effects/pixel-rects.jsonl", records)
    }
}
