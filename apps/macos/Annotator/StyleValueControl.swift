import SwiftUI
import AnnotationCore

enum StyleParameter: Hashable {
    case width, textSize, opacity, effect, gridRows, gridColumns, axisTickCount

    func title(for tool: Tool) -> String {
        switch self {
        case .width: tool == .eraser ? "Size" : "Width"
        case .textSize: "Text size"
        case .opacity: "Opacity"
        case .gridRows: "Rows"
        case .gridColumns: "Columns"
        case .axisTickCount: "Ticks per direction"
        case .effect:
            switch tool {
            case .pixelate: "Pixel size"
            case .distort: "Strength"
            default: "Blur"
            }
        }
    }

    var range: ClosedRange<Double> {
        switch self {
        case .width: 1...40
        case .textSize: 8...120
        case .opacity: 0...100
        case .effect: 0...100
        case .gridRows, .gridColumns: 1...24
        case .axisTickCount: 0...24
        }
    }

    var unit: String {
        switch self {
        case .width, .textSize: "pt"
        case .opacity: "%"
        case .effect, .gridRows, .gridColumns, .axisTickCount: ""
        }
    }

    func sliderRange(for tool: Tool) -> ClosedRange<Double> {
        guard self == .effect else { return range }
        switch tool {
        case .blur: return 0...20
        case .pixelate: return 0...40
        default: return 2...100
        }
    }

    func value(in style: Style) -> Double {
        switch self {
        case .width: style.width
        case .textSize: style.fontSize
        case .opacity: style.opacity * 100
        case .effect: style.effectAmount ?? 18
        case .gridRows: Double(style.gridRows ?? 4)
        case .gridColumns: Double(style.gridColumns ?? 4)
        case .axisTickCount: Double(style.axisTickCount ?? 9)
        }
    }

    func apply(_ value: Double, to style: inout Style) {
        switch self {
        case .width: style.width = value
        case .textSize: style.fontSize = value
        case .opacity: style.opacity = value / 100
        case .effect: style.effectAmount = value
        case .gridRows: style.gridRows = Int(value)
        case .gridColumns: style.gridColumns = Int(value)
        case .axisTickCount: style.axisTickCount = Int(value)
        }
    }

    func parse(_ draft: String, locale: Locale = .current) -> Double? {
        let normalized = draft.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: locale.decimalSeparator ?? ".", with: ".")
        guard let value = Double(normalized), value.isFinite, range.contains(value) else { return nil }
        if requiresInteger && value.rounded() != value { return nil }
        return value
    }

    var requiresInteger: Bool { self == .gridRows || self == .gridColumns || self == .axisTickCount }
}

struct StyleValueControl: View {
    @ObservedObject var model: WorkspaceModel
    let parameter: StyleParameter
    var sliderWidth: CGFloat = 90
    var showLabel = true
    /// Without the number field, the slider shows its value beside it.
    var showsField = true
    @State private var draft = ""
    @State private var originalDraft = ""
    @State private var validation: String?
    @State private var editingTarget: Target?
    @State private var sliding = false
    @FocusState private var editing: Bool

    private struct Target: Equatable {
        let session: ObjectIdentifier?
        let selection: Set<UUID>
        let scope: Set<UUID>
        let tool: Tool
        let parameter: StyleParameter
    }

    private var target: Target {
        Target(session: model.active.map(ObjectIdentifier.init),
               selection: model.selectedElementIDs, scope: model.selectionScopeIDs,
               tool: model.effectiveTool, parameter: parameter)
    }
    private var current: Double { parameter.value(in: model.style) }
    private var formatted: String { current.formatted(.number.precision(.fractionLength(0...1))) }
    private var title: String { parameter.title(for: model.effectiveTool) }
    private var sliderRange: ClosedRange<Double> { parameter.sliderRange(for: model.effectiveTool) }

    var body: some View {
        HStack(spacing: 6) {
            if showLabel { Text(title).font(.caption).foregroundStyle(.secondary) }
            if sliderWidth > 0 {
                Slider(value: Binding(get: { min(sliderRange.upperBound, max(sliderRange.lowerBound, current)) }, set: { value in
                    model.applyStyle { parameter.apply(value, to: &$0) }
                    draft = formatted
                    originalDraft = draft
                    validation = nil
                }), in: sliderRange, onEditingChanged: { active in
                    if active {
                        if editingTarget == target { commit() }
                        editingTarget = nil
                        editing = false
                        model.finishStyleEdit()
                        sliding = true
                        model.beginStyleEdit()
                    } else {
                        sliding = false
                        model.finishStyleEdit()
                    }
                }).frame(width: sliderWidth).keepsToolbarPress().accessibilityLabel(title)
                    .help(parameter == .effect
                        ? "Slider: \(Int(sliderRange.lowerBound))–\(Int(sliderRange.upperBound)). Enter up to 100 in the number field for a stronger effect."
                        : "\(title): \(Int(sliderRange.lowerBound))–\(Int(sliderRange.upperBound)).")
            }
            if !showsField {
                Text(parameter.unit.isEmpty ? formatted : "\(formatted) \(parameter.unit)")
                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    .frame(minWidth: 34, alignment: .leading)
                    .contentTransition(.numericText())
                    .accessibilityHidden(true)
            } else {
            TextField(title, text: $draft, onEditingChanged: { active in
                if active {
                    editingTarget = target
                    originalDraft = draft
                    model.beginStyleEdit()
                } else {
                    let wasEditing = editingTarget != nil
                    if editingTarget == target { commit() }
                    editingTarget = nil
                    if wasEditing { model.finishStyleEdit() }
                }
            })
                .textFieldStyle(.roundedBorder).font(.caption.monospacedDigit())
                .frame(width: 48).keepsToolbarPress().focused($editing)
                .accessibilityLabel("Precise \(title.lowercased())")
                .help("\(title): \(Int(parameter.range.lowerBound))–\(Int(parameter.range.upperBound)) \(parameter.unit)")
                .onSubmit {
                    commit()
                    if validation == nil {
                        editingTarget = nil
                        editing = false
                        model.finishStyleEdit()
                    }
                }
                .onExitCommand {
                    editingTarget = nil
                    draft = formatted
                    validation = nil
                    editing = false
                    model.finishStyleEdit()
                }
            if !parameter.unit.isEmpty {
                Text(parameter.unit).font(.caption).foregroundStyle(.secondary).fixedSize()
            }
            if let validation {
                Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.red)
                    .help(validation).accessibilityLabel(validation)
            }
            }
        }
        .fixedSize(horizontal: true, vertical: false)
        .onAppear { draft = formatted }
        .onChange(of: current) { _, _ in
            if !editing { draft = formatted; validation = nil }
        }
        .onChange(of: target) { _, _ in
            let wasEditing = editingTarget != nil || sliding
            editingTarget = nil
            sliding = false
            editing = false
            draft = formatted
            validation = nil
            if wasEditing { model.finishStyleEdit() }
        }
        .onDisappear {
            let wasEditing = editingTarget != nil || sliding
            if editingTarget == target { commit() }
            if wasEditing { model.finishStyleEdit() }
        }
    }

    private func commit() {
        guard editingTarget == target else { return }
        guard draft != originalDraft else { return }
        guard let value = parameter.parse(draft) else {
            validation = "Enter \(parameter.requiresInteger ? "a whole number from " : "")\(Int(parameter.range.lowerBound)) to \(Int(parameter.range.upperBound))."
            return
        }
        if value != current { model.applyStyle { parameter.apply(value, to: &$0) } }
        validation = nil
        draft = formatted
        originalDraft = draft
    }
}
