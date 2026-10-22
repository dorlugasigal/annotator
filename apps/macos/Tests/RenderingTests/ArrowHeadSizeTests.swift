import AppKit
import Testing
import AnnotationCore
import PresentationRendering

@Test @MainActor func everyArrowheadUsesTheDoubledFootprintAtBothEnds() throws {
    for start in [false, true] {
        for head in [ArrowHead.open, .filled, .circle, .square, .bar] {
            var style = Style()
            style.width = 3
            style.startHead = start ? head : .none
            style.endHead = start ? .none : head
            let arrow = Element(tool: .arrow, points: [Point(60, 60), Point(220, 60)], style: style)
            let image = try PNGExporter.image(elements: [arrow], size: CGSize(width: 300, height: 120))
            let bitmap = NSBitmapImageRep(cgImage: image)
            let tip = start ? 60 : 220
            let x: Int
            let y: Int
            switch head {
            case .open: x = start ? 82 : 198; y = 73
            case .filled: x = start ? 82 : 198; y = 68
            case .circle, .square: x = tip; y = 67
            case .bar: x = tip; y = 69
            default: continue
            }
            #expect((bitmap.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.1,
                    "The expanded \(head.rawValue) head must paint beyond its previous footprint.")
        }
    }
}

@Test @MainActor func renderCurvedArrowReadabilityGallery() throws {
    var style = Style()
    style.width = 6
    style.headScale = 1.5
    style.curved = true
    let paths: [[Point]] = [
        [Point(310, 40), Point(110, 130), Point(65, 320)],
        [Point(380, 310), Point(540, 240), Point(580, 60)],
        [Point(665, 75), Point(850, 170), Point(755, 320)]
    ]
    let arrows = paths.map { Element(tool: .arrow, points: $0, style: style) }
    let data = try PNGExporter.data(elements: arrows, size: CGSize(width: 920, height: 370), background: .white)
    let directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("build/ArrowReview")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    try data.write(to: directory.appendingPathComponent("curved-heads.png"), options: .atomic)
}
