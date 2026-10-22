import AppKit
import SwiftUI
import AnnotationCore
import PresentationRendering

struct PropertyBlock<Content: View>: View {
    let name: String
    let symbol: String
    @ViewBuilder var content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(name, systemImage: symbol)
                .font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            content
        }.frame(maxWidth: .infinity, alignment: .topLeading)
    }
}

enum InspectorSample: Equatable {
    case thickness(Double), pattern(StrokePattern), roughness(Double), edges(Bool)
    case fill(FillPattern), route(Bool), head(ArrowHead, Bool), headSize(Double), pressure(Bool)
}

enum InspectorPreviewStyle {
    static func roughness(_ value: Double) -> Double { min(1.2, max(0, value * 0.3)) }
}

struct InspectorSampleView: NSViewRepresentable {
    let sample: InspectorSample
    func makeNSView(context: Context) -> SampleView { SampleView(frame: .zero) }
    func updateNSView(_ view: SampleView, context: Context) { view.sample = sample; view.needsDisplay = true }

    final class SampleView: NSView {
        var sample = InspectorSample.thickness(3)
        override var isFlipped: Bool { true }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func draw(_ dirtyRect: NSRect) {
            guard let context = NSGraphicsContext.current?.cgContext,
                  let ink = NSColor.labelColor.usingColorSpace(.sRGB) else { return }
            context.saveGState()
            context.clip(to: bounds)
            defer { context.restoreGState() }
            var style = Style()
            style.color = RGBA(ink.redComponent, ink.greenComponent, ink.blueComponent)
            style.width = 1.5
            var element = Element(tool: .line, points: [Point(7, 18), Point(29, 18)], style: style)
            switch sample {
            case .thickness(let value): element.style.width = min(5, max(1, value / 1.5))
            case .pattern(let value): element.style.pattern = value
            case .roughness(let value):
                element = Element(tool: .rectangle, frame: Rect(x: 9, y: 8, width: 18, height: 20), style: style)
                element.style.roughness = InspectorPreviewStyle.roughness(value)
            case .edges(let rounded):
                element = Element(tool: .rectangle, frame: Rect(x: 9, y: 9, width: 20, height: 20), style: style)
                element.style.cornerRadius = rounded ? 7 : 0
            case .fill(let pattern):
                element = Element(tool: .rectangle, frame: Rect(x: 8, y: 8, width: 21, height: 21), style: style)
                element.style.fill = style.color
                element.style.fillPattern = pattern
                element.style.fill?.alpha = pattern == .solid ? 0.5 : 1
            case .route(let curved):
                context.setStrokeColor(ink.cgColor)
                context.setLineWidth(1.8)
                context.setLineCap(.round)
                context.setLineJoin(.round)
                context.move(to: CGPoint(x:7,y:28))
                if curved {
                    context.addCurve(to:CGPoint(x:28,y:8),control1:CGPoint(x:7,y:8),control2:CGPoint(x:15,y:8))
                    context.strokePath()
                    context.move(to:CGPoint(x:23,y:3));context.addLine(to:CGPoint(x:28,y:8));context.addLine(to:CGPoint(x:23,y:13))
                } else {
                    context.addLine(to:CGPoint(x:28,y:8));context.strokePath()
                    context.move(to:CGPoint(x:21,y:8));context.addLine(to:CGPoint(x:28,y:8));context.addLine(to:CGPoint(x:28,y:15))
                }
                context.strokePath()
                return
            case .head(let head, let start):
                element.tool = .arrow
                element.style.headScale = 0.35
                element.style.startHead = start ? head : .none
                element.style.endHead = start ? .none : head
            case .headSize(let size):
                element.tool = .arrow
                element.style.headScale = size * 0.35
            case .pressure(let variable):
                element = Element(tool: .pen,
                                  points: [Point(6, 22), Point(11, 17), Point(16, 15), Point(21, 18), Point(29, 17)],
                                  style: style)
                element.style.width = variable ? 5 : 3
                element.style.pressure = variable
                element.pressures = variable ? [0.15, 0.4, 1, 0.6, 0.2] : []
            }
            element.seed = 93
            SceneRenderer().draw([element], in: context)
        }
    }
}

struct SampleButton: View {
    let name: String
    let sample: InspectorSample
    var selected = false
    var size: CGFloat = 36
    let action: () -> Void
    var body: some View {
        InspectorControlButton(name: name, selected: selected, width: size, height: size, action: action) {
            InspectorSampleView(sample: sample).allowsHitTesting(false)
        }
    }
}

struct InspectorControlButton<Content: View>: View {
    let name: String
    var selected = false
    var width: CGFloat = 36
    var height: CGFloat = 36
    let action: () -> Void
    @ViewBuilder var content: Content
    @State private var hover = false
    var body: some View {
        Button(action: action) {
            content.frame(width: width, height: height)
                .background(Color.primary.opacity(selected ? 0.08 : hover ? 0.055 : 0.02),
                            in: RoundedRectangle(cornerRadius: 9))
                .overlay {
                    RoundedRectangle(cornerRadius: 9).strokeBorder(
                        selected ? Color.accentColor : Color.primary.opacity(0.16), lineWidth: selected ? 2 : 1)
                }
                .contentShape(RoundedRectangle(cornerRadius: 9))
        }
        .buttonStyle(.plain).help(name).accessibilityLabel(name)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .onHover { hover = $0 }
    }
}

struct SmartDrawButton: View {
    @ObservedObject var model: WorkspaceModel
    var compact = false

    var body: some View {
        InspectorControlButton(name: "Smart Draw", selected: model.smartDraw,
                               width: compact ? 32 : 36, height: compact ? 32 : 36,
                               action: { model.smartDraw.toggle() }) {
            Image(systemName: "wand.and.stars").font(.system(size: 18)).foregroundStyle(.primary)
        }
            .accessibilityValue(model.smartDraw ? "On" : "Off")
            .help(model.smartDraw ? "Smart Draw is on. Click to turn it off." :
                "Turn on Smart Draw to turn pen strokes into rectangles and ellipses.")
    }
}

struct SampleOptions<Value: Equatable>: View {
    let values: [(Value, String, InspectorSample)]
    let selection: Value
    let update: (Value) -> Void
    var body: some View {
        HStack(spacing: 5) {
            ForEach(Array(values.enumerated()), id: \.offset) { _, value in
                SampleButton(name: value.1, sample: value.2, selected: selection == value.0) { update(value.0) }
            }
        }
    }
}

struct ArrowEndControl: View {
    @ObservedObject var model: WorkspaceModel
    let start: Bool
    var body: some View {
        NativeArrowHeadPicker(model: model, start: start)
    }

}
