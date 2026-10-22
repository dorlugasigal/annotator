import SwiftUI
import AnnotationCore

struct NativeArrowHeadPicker: View {
    @ObservedObject var model: WorkspaceModel
    let start: Bool
    @State private var open = false
    private var selection: ArrowHead { start ? model.style.startHead : model.style.endHead }
    private var side: String { start ? "Start" : "End" }

    var body: some View {
        InspectorControlButton(name: "\(side) arrowhead", selected: open, width: 68, action: { open.toggle() }) {
            HStack(spacing: 5) {
                InspectorSampleView(sample: .head(selection, start)).frame(width: 36, height: 36)
                    .accessibilityHidden(true)
                Image(systemName: "chevron.down").font(.system(size: 9)).accessibilityHidden(true)
            }
        }
        .accessibilityValue(selection.label)
        .popover(isPresented: $open) {
            ArrowHeadPalette(model: model, start: start, dismiss: { open = false })
        }
    }
}

struct ArrowHeadPalette: View {
    @ObservedObject var model: WorkspaceModel
    let start: Bool
    let dismiss: () -> Void
    @FocusState private var focused: ArrowHead?
    private var selected: ArrowHead { start ? model.style.startHead : model.style.endHead }
    private var choices: [ArrowHead] { ArrowHead.shapeChoices + ArrowHead.relationshipChoices }
    private let columns = Array(repeating: GridItem(.fixed(36), spacing: 7), count: 4)

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(start ? "Start arrowhead" : "End arrowhead").font(.headline)
            grid(ArrowHead.shapeChoices)
            Text("Relationships").font(.caption).foregroundStyle(.secondary)
            grid(ArrowHead.relationshipChoices)
            Text(selected.label).font(.caption).foregroundStyle(.secondary)
        }
        .padding(14)
        .onAppear { focused = selected }
        .onExitCommand(perform: dismiss)
        .onMoveCommand { direction in
            guard let current = choices.firstIndex(of: focused ?? selected) else { return }
            let delta: Int
            switch direction {
            case .left: delta = -1
            case .right: delta = 1
            case .up: delta = -4
            case .down: delta = 4
            @unknown default: return
            }
            focused = choices[(current + delta + choices.count) % choices.count]
        }
    }
    private func grid(_ values: [ArrowHead]) -> some View {
        LazyVGrid(columns: columns, alignment: .leading, spacing: 7) {
            ForEach(values, id: \.self) { head in
                SampleButton(name: "\(start ? "Start" : "End"): \(head.label)",
                             sample: .head(head, start), selected: selected == head) {
                    model.applyStyle { if start { $0.startHead = head } else { $0.endHead = head } }
                    dismiss()
                }
                .focused($focused, equals: head)
            }
        }
    }
}
