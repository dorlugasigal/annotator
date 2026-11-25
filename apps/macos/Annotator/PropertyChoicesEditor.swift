import SwiftUI
import AnnotationCore
import MotionCore

/// "Your properties": each property with a few choices shows as one button that steps through them, one button that
/// opens a menu of them, or as all its choices side by side. An on and off property is one button or both choices.
/// Each way is drawn with the toolbar's own controls; click one to use it.
struct PropertyChoicesEditor: View {
    @ObservedObject var model: WorkspaceModel
    @Namespace private var ring
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Your properties").font(.title3.weight(.semibold))
                    Text(Self.intro)
                        .font(.callout).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                Button("Use one button everywhere") {
                    withAnimation(animation) { model.resetPropertyChoices() }
                }
                .buttonStyle(.link)
                .disabled(model.preferences.expandedPropertyChoices == nil && model.preferences.menuPropertyChoices == nil)
                .accessibilityIdentifier("reset-property-choices")
            }
            FlowLayout(spacing: 10, lineSpacing: 10) {
                ForEach(PropertyChoice.allCases, id: \.self) { choice in card(choice) }
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 16).fill(Color.primary.opacity(0.03)))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.primary.opacity(0.06)))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("property-choices")
    }

    private var animation: Animation? { Motion.resolve(Motion.snappy, reduceMotion: reduceMotion) }

    private func card(_ choice: PropertyChoice) -> some View {
        let current = model.propertyChoiceStyle(choice)
        return VStack(alignment: .leading, spacing: 8) {
            Label(choice.label, systemImage: Self.symbol(choice))
                .font(.system(size: 12, weight: .semibold))
            HStack(alignment: .top, spacing: 8) {
                ForEach(choice.styles, id: \.self) { style in option(choice, style: style, selected: style == current) }
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.03)))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.07)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(choice.label)
        .accessibilityIdentifier("property-choice-\(choice.rawValue)")
    }

    private func option(_ choice: PropertyChoice, style: PropertyChoiceStyle, selected: Bool) -> some View {
        Button {
            withAnimation(animation) { model.setPropertyChoiceStyle(choice, style) }
        } label: {
            VStack(spacing: 6) {
                preview(choice, style: style)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
                    .frame(minWidth: 52, minHeight: 44)
                    .padding(.horizontal, 8).padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .windowBackgroundColor)))
                    .overlay {
                        if selected {
                            RoundedRectangle(cornerRadius: 10).strokeBorder(Color.accentColor, lineWidth: 2)
                                .matchedGeometryEffect(id: choice, in: ring)
                        } else {
                            RoundedRectangle(cornerRadius: 10).strokeBorder(Color.primary.opacity(0.1))
                        }
                    }
                HStack(spacing: 4) {
                    Text(Self.title(style)).font(.caption.weight(selected ? .semibold : .regular))
                    if selected {
                        Image(systemName: "checkmark.circle.fill").font(.caption).foregroundStyle(.tint)
                            .transition(reduceMotion ? .opacity : .scale.combined(with: .opacity))
                    }
                }
                .foregroundStyle(selected ? .primary : .secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(Self.help(choice, style))
        .accessibilityLabel("\(choice.label): \(Self.title(style).lowercased())")
        .accessibilityValue(selected ? "Selected" : "")
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier("property-choice-\(choice.rawValue)-\(Self.identifier(style))")
    }

    /// The control as the properties row draws it. One button shows the first choice.
    @ViewBuilder private func preview(_ choice: PropertyChoice, style: PropertyChoiceStyle) -> some View {
        let all = style == .all
        switch choice {
        case .pattern:
            samples(style, StrokePattern.allCases.map { ($0, $0.rawValue.capitalized, InspectorSample.pattern($0)) },
                    name: choice.label, first: .solid)
        case .roughness:
            samples(style, [(0.0, "Clean", InspectorSample.roughness(0)), (2, "Sketchy", .roughness(2)), (6, "Wild", .roughness(6))],
                    name: choice.label, first: 0)
        case .fillStyle:
            samples(style, FillPattern.allCases.map { ($0, $0.rawValue.capitalized, InspectorSample.fill($0)) },
                    name: choice.label, first: .solid)
        case .edges:
            pair(all, name: "Rounded corners", off: .edges(false), on: .edges(true), labels: choice.choices)
        case .arrowType:
            pair(all, name: "Curved arrow", off: .route(false), on: .route(true), labels: choice.choices)
        case .pressure:
            pair(all, name: "Ink pressure", off: .pressure(false), on: .pressure(true), labels: choice.choices)
        case .font:
            if all {
                HStack(spacing: 5) {
                    ForEach(TextFontChoice.allCases, id: \.self) { font in
                        InspectorControlButton(name: "\(font.title) font", selected: font == .system, action: {}) {
                            Text("Aa").font(font.sample(size: 17))
                        }
                    }
                }
            } else {
                ChoiceButton(style: style, name: "Font",
                             choices: TextFontChoice.allCases.map { ($0, $0.title, .text("Aa", $0.sample(size: 16))) },
                             selection: TextFontChoice.system) { _ in }
            }
        case .alignment:
            if all {
                HStack(spacing: 5) {
                    ForEach(AnnotationCore.TextAlignment.allCases, id: \.self) { alignment in
                        InspectorControlButton(name: "Align \(alignment.rawValue)", selected: alignment == .left, action: {}) {
                            Image(systemName: ContextualInspector.alignmentSymbol(alignment))
                        }
                    }
                }
            } else {
                ChoiceButton(style: style, name: "Alignment", choices: AnnotationCore.TextAlignment.allCases.map {
                    ($0, $0.rawValue.capitalized, .symbol(ContextualInspector.alignmentSymbol($0)))
                }, selection: .left) { _ in }
            }
        }
    }

    @ViewBuilder private func samples<Value: Equatable>(_ style: PropertyChoiceStyle, _ values: [(Value, String, InspectorSample)],
                                                        name: String, first: Value) -> some View {
        if style == .all {
            SampleOptions(values: values, selection: first) { _ in }
        } else {
            ChoiceButton(style: style, name: name, values: values, selection: first) { _ in }
        }
    }

    @ViewBuilder private func pair(_ all: Bool, name: String, off: InspectorSample, on: InspectorSample,
                                   labels: [String]) -> some View {
        if all {
            SampleOptions(values: [(false, labels[0], off), (true, labels[1], on)], selection: false) { _ in }
        } else {
            InspectorToggle(name: name, sample: on, isOn: false, onHelp: "", offHelp: "") {}
        }
    }

    static func symbol(_ choice: PropertyChoice) -> String {
        switch choice {
        case .pattern: "line.3.horizontal"
        case .roughness: "scribble"
        case .fillStyle: "square.lefthalf.filled"
        case .edges: "rectangle"
        case .arrowType: "arrow.turn.up.right"
        case .pressure: "pencil.tip"
        case .font: "textformat"
        case .alignment: "text.alignleft"
        }
    }

    static let intro = "One button saves room: click it to go to the next choice. Menu shows every choice when you click it. All choices puts them side by side, so each one is a click away."

    /// "One button", "Menu" or "All choices".
    static func title(_ style: PropertyChoiceStyle) -> String {
        switch style {
        case .oneButton: "One button"
        case .menu: "Menu"
        case .all: "All choices"
        }
    }

    /// The end of an option's identifier: "one", "menu" or "all".
    static func identifier(_ style: PropertyChoiceStyle) -> String {
        switch style {
        case .oneButton: "one"
        case .menu: "menu"
        case .all: "all"
        }
    }

    /// "One button. Click it to go to the next choice: Solid, Dashed and Dotted."
    static func help(_ choice: PropertyChoice, _ style: PropertyChoiceStyle) -> String {
        let names = list(choice.choices)
        switch style {
        case .oneButton: return "One button. Click it to go to the next choice: \(names)."
        case .menu: return "A menu. Click it to choose from \(names)."
        case .all: return "\(names), side by side."
        }
    }

    /// "Solid, Dashed and Dotted".
    private static func list(_ names: [String]) -> String {
        guard names.count > 1 else { return names.first ?? "" }
        return names.dropLast().joined(separator: ", ") + " and " + (names.last ?? "")
    }
}
